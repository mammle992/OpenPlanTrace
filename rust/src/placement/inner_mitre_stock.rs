//! Place an inscribed interior core with two authored tapered finishes.
use super::exterior_piers::{aperture_guard, beside_ready_wall, corners, overlaps};
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, effective_wall, roundoff};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 14_000_000;

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    vertices: [Point; 4],
}

struct Stroke<'a> {
    id: &'a str,
    span: [Point; 2],
}

#[derive(Default)]
struct Layer<'a> {
    paint: Vec<Paint<'a>>,
    ink: Vec<Stroke<'a>>,
}

struct Exterior<'a> {
    layer: &'a str,
    paint: Paint<'a>,
    axis: Point,
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(root, _)| root)
}

fn interior_layer(p: &Primitive) -> Option<&str> {
    let layer = p.layer.as_deref()?;
    if p.layer_category_override.is_some() && p.layer_kind() != LayerKind::Wall {
        return None;
    }
    (crate::layers::is_named_wall(layer)
        && !crate::layers::is_named_exterior_wall(layer)
        && layer
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .any(|word| {
                matches!(
                    word,
                    "interior"
                        | "internal"
                        | "innervegg"
                        | "innervegger"
                        | "partition"
                        | "partitions"
                )
            }))
    .then_some(layer)
}

fn quadrilateral(p: &Primitive) -> Option<Paint<'_>> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    let points = if points.first() == points.last() {
        points.get(..points.len().checked_sub(1)?)?
    } else {
        points
    };
    let vertices: [Point; 4] = points.try_into().ok()?;
    let [a, b, c, d] = vertices;
    let edges = [b - a, c - b, d - c, a - d];
    let turns = [0, 1, 2, 3].map(|i| edges[i].cross(edges[(i + 1) % 4]));
    (vertices.iter().all(|p| p.finite())
        && (turns.iter().all(|&t| t > 0.) || turns.iter().all(|&t| t < 0.)))
    .then_some(Paint {
        id: &p.id,
        vertices,
    })
}

fn close(p: f64, q: f64, eps: f64) -> bool {
    (p - q).abs() <= eps
}

fn segment_eq([a, b]: [Point; 2], [c, d]: [Point; 2], eps: f64) -> bool {
    (a.distance(c) <= eps && b.distance(d) <= eps) || (a.distance(d) <= eps && b.distance(c) <= eps)
}

fn edges(points: [Point; 4]) -> [[Point; 2]; 4] {
    [0, 1, 2, 3].map(|i| [points[i], points[(i + 1) % 4]])
}

fn range(points: [Point; 4], by: fn(Point) -> f64) -> [f64; 2] {
    points
        .map(by)
        .into_iter()
        .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], p| {
            [lo.min(p), hi.max(p)]
        })
}

struct Core {
    start: Point,
    end: Point,
    axis: Point,
    width: f64,
    eps: f64,
    faces: [[Point; 2]; 2],
}

impl Core {
    fn local(&self, p: Point) -> Point {
        let d = p - self.start;
        Point::new(d.dot(self.axis), self.axis.cross(d))
    }

    fn fit(wall: &Wall, paint: Paint<'_>) -> Option<Self> {
        let length = wall.start.distance(wall.end);
        let width = wall.thickness;
        if !length.is_finite() || !width.is_finite() || width <= 0. || length < width * 8. {
            return None;
        }
        let eps = roundoff(wall).max(width * 1e-7);
        let direction = (wall.end - wall.start).unit();
        let along = |p: Point| (p - wall.start).dot(direction);
        let across = |p: Point| direction.cross(p - wall.start);
        let mut candidates = edges(paint.vertices).into_iter().filter_map(|[a, b]| {
            (close(across(a), across(b), eps)
                && close(across(a).abs(), width * 0.5, eps)
                && (along(a) - along(b)).abs() >= length * 0.9)
                .then_some(if along(a) < along(b) { [a, b] } else { [b, a] })
        });
        let mut faces = [candidates.next()?, candidates.next()?];
        if candidates.next().is_some() || across(faces[0][0]) * across(faces[1][0]) >= 0. {
            return None;
        }
        let caps = [0, 1].map(|i| (along(faces[0][i]) - along(faces[1][i])).abs());
        let (start, axis) = match caps.map(|d| d <= eps) {
            [true, false] => (wall.start, direction),
            [false, true] => {
                for face in &mut faces {
                    face.swap(0, 1);
                }
                (wall.end, direction * -1.)
            }
            _ => return None,
        };
        if !(width * 0.12..=width * 0.75).contains(&caps[0].max(caps[1])) {
            return None;
        }
        let local_x = |p: Point| (p - start).dot(axis);
        if faces.iter().any(|face| !close(local_x(face[0]), 0., eps)) {
            return None;
        }
        let last = local_x(faces[0][1]).min(local_x(faces[1][1])).min(length);
        if last < length * 0.9 {
            return None;
        }
        let end = start + axis * last;
        let width = faces
            .iter()
            .flatten()
            .map(|&p| 2. * axis.cross(p - start).abs())
            .fold(width, f64::min);
        let square = corners(start, end, width);
        let outline = edges(paint.vertices);
        let twice_area = outline
            .into_iter()
            .map(|[a, b]| (a - start).cross(b - start))
            .sum::<f64>();
        if square.iter().any(|&p| {
            outline
                .iter()
                .any(|&[a, b]| twice_area.signum() * (b - a).cross(p - a) < -eps * a.distance(b))
        }) {
            return None;
        }
        Some(Self {
            start,
            end,
            axis,
            width,
            eps,
            faces,
        })
    }

    fn finish(&self, face: [Point; 2], p: Paint<'_>) -> Option<([Point; 2], f64)> {
        let e = self.eps;
        let sides = edges(p.vertices);
        let index = sides.iter().position(|&line| segment_eq(line, face, e))?;
        let mut outer = sides[(index + 2) % 4];
        if self.local(outer[0]).x > self.local(outer[1]).x {
            outer.swap(0, 1);
        }
        let [a, b] = outer.map(|p| self.local(p));
        let [f, g] = face.map(|p| self.local(p));
        let side = f.y.signum();
        let depth = (a.y - f.y) * side;
        if !(self.width * 0.04..=self.width * 0.27).contains(&depth)
            || !close(a.y, b.y, e)
            || !close(a.x, f.x, e)
            || !close(f.y, g.y, e)
            || !(self.width * 0.01..=self.width * 0.3).contains(&(b.x - g.x).abs())
        {
            return None;
        }
        let slope = self.mitre_slope();
        if !close(b.x - g.x, slope * (b.y - g.y), depth * 0.02 + e) {
            return None;
        }
        Some((outer, depth))
    }

    fn mitre_slope(&self) -> f64 {
        let [a, b] = self.faces.map(|face| self.local(face[1]));
        (b.x - a.x) / (b.y - a.y)
    }
}

fn wing<'a>(
    core: &Core,
    end: Point,
    exterior: &[Exterior<'a>],
    work: &mut usize,
) -> Option<Option<(&'a str, &'a str)>> {
    *work = work.checked_sub(exterior.len())?;
    let side = core.local(end).y.signum();
    let trace = core.local(end).y * side;
    let found = exterior.iter().find(|w| {
        w.axis.cross(core.axis).abs() >= 0.2
            && w.paint
                .vertices
                .iter()
                .any(|&p| p.distance(end) <= core.eps)
            && w.paint
                .vertices
                .iter()
                .all(|&p| core.local(p).y * side >= trace - core.eps)
            && w.paint
                .vertices
                .iter()
                .any(|&p| core.local(p).y * side > trace + core.width)
    });
    Some(found.map(|w| (w.paint.id, w.layer)))
}

struct Section<'a> {
    id: &'a str,
    x: [f64; 2],
    y: [f64; 2],
}

fn tee<'a>(
    core: &Core,
    layer: &Layer<'a>,
    depth: f64,
    work: &mut usize,
) -> Option<Option<[&'a str; 5]>> {
    let e = core.eps;
    let half = core.width * 0.5;
    let same = |a, b| close(a, b, e);
    let near_depth = |a: f64| close(a, depth, depth * 0.02 + e);
    *work = work.checked_sub(layer.paint.len())?;
    let near = layer
        .paint
        .iter()
        .filter_map(|p| {
            let points = p.vertices.map(|q| core.local(q));
            let x = range(points, |q| q.x);
            let y = range(points, |q| q.y);
            (x[1] <= e
                && x[0] >= -2.5 * core.width
                && y[0] <= half + e
                && y[1] >= -half - e
                && x.into_iter().all(|x| {
                    y.into_iter()
                        .all(|y| points.iter().any(|&p| p.distance(Point::new(x, y)) <= e))
                }))
            .then_some(Section { id: p.id, x, y })
        })
        .collect::<Vec<_>>();
    *work = work.checked_sub(near.len().saturating_pow(3))?;
    let full = |p: &Section<'_>| same(p.y[0], -half) && same(p.y[1], half);
    for cap in near
        .iter()
        .filter(|p| full(p) && same(p.x[1], 0.) && near_depth(p.x[1] - p.x[0]))
    {
        for body in near.iter().filter(|p| {
            full(p)
                && same(p.x[1], cap.x[0])
                && path(p.id) != path(cap.id)
                && (core.width * 0.6..=core.width * 1.4).contains(&(p.x[1] - p.x[0]))
        }) {
            let arm = |i: usize| {
                near.iter().find(|p| {
                    same(p.x[0], body.x[0])
                        && same(p.x[1], body.x[1])
                        && p.y[1] - p.y[0] >= core.width * 0.6
                        && path(p.id) != path(cap.id)
                        && path(p.id) != path(body.id)
                        && if i == 0 {
                            same(p.y[1], -half)
                        } else {
                            same(p.y[0], half)
                        }
                })
            };
            let [Some(a), Some(b)] = [arm(0), arm(1)] else {
                continue;
            };
            if path(a.id) == path(b.id) {
                continue;
            }
            *work = work.checked_sub(layer.ink.len())?;
            if let Some(line) = layer.ink.iter().find(|line| {
                let [p, q] = line.span.map(|p| core.local(p));
                same(p.x, q.x)
                    && near_depth(body.x[0] - p.x)
                    && p.y.min(q.y) <= a.y[0] + e
                    && p.y.max(q.y) >= b.y[1] - e
                    && [cap, body, a, b]
                        .iter()
                        .all(|p| path(p.id) != path(line.id))
            }) {
                return Some(Some([cap.id, body.id, a.id, b.id, line.id]));
            }
        }
    }
    Some(None)
}

type Trim<'a> = (&'a str, &'a str, &'a str, &'a str, f64);

fn trim<'a>(
    core: &Core,
    face: [Point; 2],
    paint: &'a str,
    layer: &Layer<'a>,
    exterior: &[Exterior<'a>],
    work: &mut usize,
) -> Option<Option<Trim<'a>>> {
    *work = work.checked_sub(layer.paint.len())?;
    for &p in &layer.paint {
        if path(p.id) == path(paint) {
            continue;
        }
        let Some((outer, depth)) = core.finish(face, p) else {
            continue;
        };
        *work = work.checked_sub(layer.ink.len())?;
        let Some(rail) = layer.ink.iter().find(|rail| {
            path(rail.id) != path(p.id)
                && path(rail.id) != path(paint)
                && segment_eq(rail.span, outer, core.eps)
        }) else {
            continue;
        };
        let Some((corner, exterior_layer)) = wing(core, outer[1], exterior, work)? else {
            continue;
        };
        return Some(Some((p.id, rail.id, corner, exterior_layer, depth)));
    }
    Some(None)
}

struct Proposal {
    index: usize,
    wall: Wall,
    bounds: [Point; 4],
    sources: Vec<String>,
}

fn measure(page: &PageScan, decisions: &[WallDecision], work: &mut usize) -> Option<Vec<Proposal>> {
    let certified = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let polygon = page
        .placement_polygon_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    if polygon.is_empty() {
        return Some(Vec::new());
    }
    *work = work.checked_sub(page.primitives.len())?;
    let mut layers = HashMap::<&str, Layer<'_>>::new();
    let mut interior = HashMap::new();
    let mut exterior_paint = HashMap::new();
    for p in &page.primitives {
        if let Some(layer) = interior_layer(p) {
            if certified.contains(p.id.as_str())
                && let Some(q) = quadrilateral(p)
            {
                layers.entry(layer).or_default().paint.push(q);
                if polygon.contains(p.id.as_str()) {
                    interior.insert(p.id.as_str(), (layer, q));
                }
            }
            if let Shape::Line { start, end } = p.shape
                && p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.)
            {
                layers.entry(layer).or_default().ink.push(Stroke {
                    id: &p.id,
                    span: [start, end],
                });
            }
        } else if polygon.contains(p.id.as_str())
            && let Some(layer) = crate::layers::named_exterior_wall(p)
            && let Some(b) = quadrilateral(p)
        {
            exterior_paint.insert(p.id.as_str(), (layer, b));
        }
    }
    if interior.is_empty() || exterior_paint.len() < 2 {
        return Some(Vec::new());
    }
    let mut exterior = Vec::new();
    let mut ready = Vec::new();
    for (wall, decision) in page
        .walls
        .iter()
        .zip(decisions)
        .filter(|(_, d)| d.status == WallStatus::Ready)
    {
        let measured = effective_wall(wall, Some(decision));
        if let Some(arc) = measured.curve {
            let radius = arc.radius + 0.5 * measured.thickness;
            let span = Point::new(radius, 0.);
            ready.push(corners(arc.center - span, arc.center + span, 2. * radius));
            continue;
        }
        ready.push(corners(measured.start, measured.end, measured.thickness));
        for id in &wall.source_ids {
            if let Some(&(layer, paint)) = exterior_paint.get(id.as_str()) {
                exterior.push(Exterior {
                    layer,
                    paint,
                    axis: (measured.end - measured.start).unit(),
                });
            }
        }
    }
    let apertures = aperture_guard(page);
    let hosts: HashSet<_> = page
        .opening_evidence()
        .flat_map(|o| o.host_wall_ids.iter().map(String::as_str))
        .collect();
    let mut proposals = Vec::new();
    for (index, (wall, decision)) in page.walls.iter().zip(decisions).enumerate() {
        if wall.body != BodyEvidence::FilledBody
            || wall.curve.is_some()
            || decision.status != WallStatus::Review
            || decision.placement_extent.is_some()
            || !decision.opening_ids.is_empty()
            || hosts.contains(wall.id.as_str())
        {
            continue;
        }
        let [id] = wall.source_ids.as_slice() else {
            continue;
        };
        let Some(&(layer, paint)) = interior.get(id.as_str()) else {
            continue;
        };
        let Some(core) = Core::fit(wall, paint) else {
            continue;
        };
        let layer = &layers[layer];
        let Some((p0, l0, c0, z0, d0)) = trim(&core, core.faces[0], id, layer, &exterior, work)?
        else {
            continue;
        };
        let Some((p1, l1, c1, z1, d1)) = trim(&core, core.faces[1], id, layer, &exterior, work)?
        else {
            continue;
        };
        if z0 != z1
            || path(c0) == path(c1)
            || path(p0) == path(p1)
            || path(l0) == path(l1)
            || !close(d0, d1, d0.min(d1) * 0.02 + core.eps)
        {
            continue;
        }
        let Some(terminal) = tee(&core, layer, d0.min(d1), work)? else {
            continue;
        };
        let mut measured = wall.clone();
        measured.start = core.start;
        measured.end = core.end;
        measured.thickness = core.width;
        let bounds = corners(measured.start, measured.end, measured.thickness);
        *work = work.checked_sub(ready.len().saturating_add(apertures.len()))?;
        if ready
            .iter()
            .any(|&b| overlaps(bounds, b, core.eps) || beside_ready_wall(&measured, b))
            || apertures.iter().any(|&b| overlaps(bounds, b, 1e-7))
        {
            continue;
        }
        let sources = [id.as_str(), p0, p1, l0, l1, c0, c1]
            .into_iter()
            .chain(terminal)
            .collect::<BTreeSet<_>>()
            .into_iter()
            .map(str::to_owned)
            .collect();
        proposals.push(Proposal {
            index,
            wall: measured,
            bounds,
            sources,
        });
    }
    Some(proposals)
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let mut work = MAX_WORK;
    let Some(proposals) = measure(page, decisions, &mut work) else {
        return;
    };
    if proposals.len().saturating_pow(2) > work {
        return;
    }
    for p in &proposals {
        if proposals.iter().any(|q| {
            p.index != q.index
                && (p.wall.source_ids == q.wall.source_ids
                    || overlaps(p.bounds, q.bounds, 1e-7)
                    || beside_ready_wall(&p.wall, q.bounds))
        }) {
            continue;
        }
        let d = &mut decisions[p.index];
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedInteriorMitreStock;
        d.support_source_ids = p.sources.clone();
        d.placement_extent = Some(PlacementExtent {
            start: p.wall.start,
            end: p.wall.end,
            thickness: Some(p.wall.thickness),
            source_ids: p.sources.clone(),
        });
    }
}
