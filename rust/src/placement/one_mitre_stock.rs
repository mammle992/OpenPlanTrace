//! Recover the inscribed structural stock of a visible single-mitre body.
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, effective_wall, roundoff};
use crate::layers::named_exterior_wall;
use crate::model::{BodyEvidence, OpeningEvidence, OpeningKind, PageScan, Point, Shape, Wall};
use std::collections::{HashMap, HashSet};

const MAX_WORK: usize = 8_000_000;

struct Rail<'a> {
    id: &'a str,
    ends: [Point; 2],
    ink: bool,
}

struct Pad<'a> {
    id: &'a str,
    layer: &'a str,
    corners: [Point; 4],
}

struct Section<'a> {
    pad: &'a Pad<'a>,
    x: [f64; 2],
    y: [f64; 2],
}

fn interior_layer(source: &crate::model::Primitive) -> Option<&str> {
    let layer = source.layer.as_deref()?;
    if source.layer_category_override.is_some()
        && source.layer_kind() != crate::model::LayerKind::Wall
    {
        return None;
    }
    (crate::layers::is_named_wall(layer)
        && layer
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .any(|word| matches!(word, "interior" | "internal" | "innervegg" | "innervegger")))
    .then_some(layer)
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(path, _)| path)
}

fn ring(shape: &Shape) -> Option<[Point; 4]> {
    let Shape::Polyline {
        points,
        filled: true,
        closed: true,
    } = shape
    else {
        return None;
    };
    let vertices = match points.as_slice() {
        [a, b, c, d] => [*a, *b, *c, *d],
        [a, b, c, d, close] if a == close => [*a, *b, *c, *d],
        _ => return None,
    };
    vertices.iter().all(|p| p.finite()).then_some(vertices)
}

#[derive(Clone, Copy)]
struct Body {
    center: Point,
    axis: Point,
    half_length: f64,
    half_width: f64,
}

impl Body {
    fn new(a: Point, b: Point, width: f64) -> Option<Self> {
        let length = a.distance(b);
        (length.is_finite() && length > 0. && width.is_finite() && width >= 0.).then(|| Self {
            center: a + (b - a) * 0.5,
            axis: (b - a).unit(),
            half_length: length * 0.5,
            half_width: width * 0.5,
        })
    }

    fn axes(self) -> [Point; 2] {
        [self.axis, Point::new(-self.axis.y, self.axis.x)]
    }

    fn radius(self, axis: Point) -> f64 {
        self.half_length * self.axis.dot(axis).abs() + self.half_width * self.axis.cross(axis).abs()
    }

    fn overlaps(self, other: Self, eps: f64) -> bool {
        let d = other.center - self.center;
        self.axes()
            .into_iter()
            .chain(other.axes())
            .all(|axis| d.dot(axis).abs() < self.radius(axis) + other.radius(axis) - eps)
    }
}

struct Stock {
    start: Point,
    axis: Point,
    eps: f64,
    width: f64,
    faces: [[Point; 2]; 2],
    flat: usize,
    span: [Point; 2],
    body: Body,
}

impl Stock {
    fn local(&self, p: Point) -> Point {
        let d = p - self.start;
        Point::new(d.dot(self.axis), self.axis.cross(d))
    }

    fn fit(wall: &Wall, vertices: [Point; 4]) -> Option<Self> {
        let width = wall.thickness;
        let length = wall.start.distance(wall.end);
        if wall.body != BodyEvidence::FilledBody
            || wall.curve.is_some()
            || !width.is_finite()
            || width <= 0.
            || !length.is_finite()
            || length < 8. * width
        {
            return None;
        }
        let origin = wall.start;
        let axis = (wall.end - origin).unit();
        let eps = roundoff(wall).max(width * 1e-7);
        let p = vertices.map(|p| {
            let d = p - origin;
            Point::new(d.dot(axis), axis.cross(d))
        });
        let mut sides =
            p.into_iter()
                .zip(p.into_iter().cycle().skip(1))
                .filter_map(|(mut a, mut b)| {
                    if a.x > b.x {
                        std::mem::swap(&mut a, &mut b);
                    }
                    (b.x - a.x >= length * 0.9
                        && (a.y - b.y).abs() <= eps
                        && a.y * b.y > 0.
                        && (a.y.abs() - width * 0.5).abs() <= eps
                        && (b.y.abs() - width * 0.5).abs() <= eps)
                        .then_some([a, b])
                });
        let faces = [sides.next()?, sides.next()?];
        if sides.next().is_some() || faces[0][0].y * faces[1][0].y >= 0. {
            return None;
        }
        let shift = [0, 1].map(|i| (faces[0][i].x - faces[1][i].x).abs());
        let flat = match shift.map(|v| v <= eps) {
            [true, false] => 0,
            [false, true] => 1,
            _ => return None,
        };
        if !(width * 0.1..=width).contains(&shift[1 - flat]) {
            return None;
        }
        let lo = faces[0][0].x.max(faces[1][0].x).max(0.);
        let hi = faces[0][1].x.min(faces[1][1].x).min(length);
        let half = faces
            .iter()
            .flatten()
            .map(|p| p.y.abs())
            .fold(width * 0.5, f64::min);
        if hi - lo < length * 0.9 {
            return None;
        }

        let edges = p.into_iter().zip(p.into_iter().cycle().skip(1));
        let area = edges.clone().map(|(a, b)| a.cross(b)).sum::<f64>();
        if area.abs() <= eps * eps {
            return None;
        }
        let corners = [lo, hi]
            .into_iter()
            .flat_map(|x| [-half, half].map(|y| Point::new(x, y)));
        if corners.into_iter().any(|point| {
            edges.clone().any(|(a, b)| {
                let edge = b - a;
                area.signum() * edge.cross(point - a) < -eps * edge.norm()
            })
        }) {
            return None;
        }
        let start = origin + axis * lo;
        let end = origin + axis * hi;
        Some(Self {
            start: origin,
            axis,
            eps,
            width,
            faces,
            flat,
            span: [start, end],
            body: Body::new(start, end, 2. * half)?,
        })
    }

    fn face_and_casing<'a>(&self, paint: &str, rails: &[Rail<'a>]) -> Option<[&'a str; 2]> {
        for face in self.faces {
            let exact = rails.iter().find(|r| {
                if path(r.id) == path(paint) {
                    return false;
                }
                let [a, b] = r.ends.map(|p| self.local(p));
                (a.distance(face[0]) <= self.eps && b.distance(face[1]) <= self.eps)
                    || (b.distance(face[0]) <= self.eps && a.distance(face[1]) <= self.eps)
            });
            let Some(exact) = exact else { continue };
            let [lo, hi] = [
                self.faces[0][0].x.min(self.faces[1][0].x),
                self.faces[0][1].x.max(self.faces[1][1].x),
            ];
            let outer = rails.iter().find(|r| {
                if !r.ink || path(r.id) == path(paint) || path(r.id) == path(exact.id) {
                    return false;
                }
                let [a, b] = r.ends.map(|p| self.local(p));
                a.x.min(b.x) <= lo + self.eps
                    && a.x.max(b.x) >= hi - self.eps
                    && a.x.min(b.x) >= lo - self.width * 3.5
                    && a.x.max(b.x) <= hi + self.width * 3.5
                    && (a.y - b.y).abs() <= self.eps
                    && a.y * face[0].y < 0.
                    && (self.width * 0.52..=self.width * 1.1).contains(&a.y.abs())
            });
            if let Some(outer) = outer {
                return Some([exact.id, outer.id]);
            }
        }
        None
    }

    fn jamb<'a>(
        &self,
        page: &'a PageScan,
        paint: &str,
        rails: &[Rail<'a>],
        work: &mut usize,
    ) -> Option<Vec<&'a str>> {
        let terminal = self.faces[0][self.flat].x;
        let inward = if self.flat == 0 { 1. } else { -1. };
        for open in &page.openings {
            if open.kind != OpeningKind::Door
                || !matches!(
                    open.evidence,
                    OpeningEvidence::FiniteJambSwing
                        | OpeningEvidence::WallGapSwing
                        | OpeningEvidence::AuthoredExteriorSwingCasing
                )
                || open.swing_observations.is_empty()
                || !open.source_ids.iter().any(|s| s == paint)
            {
                continue;
            }
            let [a, b] = [open.start, open.end].map(|p| self.local(p));
            let closest = ((a.x - terminal) * inward).max((b.x - terminal) * inward);
            if (a.y - b.y).abs() > self.eps
                || a.y.abs() > self.width * 0.5 + self.eps
                || closest > self.eps
                || closest < -self.width * 0.4
            {
                continue;
            }
            *work = work.checked_sub(rails.len().saturating_mul(open.source_ids.len().max(1)))?;
            let half = self.body.half_width;
            let mut capped = rails
                .iter()
                .filter_map(|r| {
                    if !r.ink
                        || path(r.id) == path(paint)
                        || !open.source_ids.iter().any(|id| id == r.id)
                    {
                        return None;
                    }
                    let [a, b] = r.ends.map(|p| self.local(p));
                    ((a.x - terminal).abs().max((b.x - terminal).abs()) <= self.eps
                        && (a.y - b.y).abs() > self.eps)
                        .then_some((a.y.min(b.y), a.y.max(b.y), r.id))
                })
                .collect::<Vec<_>>();
            capped.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.2.cmp(b.2)));
            let mut to = -half;
            let mut used = Vec::new();
            for (lo, hi, id) in capped {
                if lo > to + self.eps {
                    break;
                }
                if hi > to + self.eps {
                    to = hi;
                    used.push(id);
                }
                if to >= half - self.eps {
                    return Some(used);
                }
            }
        }
        None
    }

    /// A transverse wall may continue through the square cap of the exterior
    /// stock. Require its independent solid, facing, continuation and authored
    /// face stroke. A thin facing on its own is not a wall terminal.
    fn tee<'a>(
        &self,
        pad: &'a [Pad<'a>],
        rails: &HashMap<&str, Vec<Rail<'a>>>,
        work: &mut usize,
    ) -> Option<Vec<&'a str>> {
        *work = work.checked_sub(pad.len())?;
        let terminal = self.faces[0][self.flat].x;
        let inward = if self.flat == 0 { 1. } else { -1. };
        let half = self.body.half_width;
        let local = |p| {
            let p = self.local(p);
            Point::new((p.x - terminal) * inward, p.y)
        };
        let close = |a: f64, b: f64| (a - b).abs() <= self.eps;
        let bounds = |a: [f64; 4]| {
            [
                a.into_iter().fold(f64::INFINITY, f64::min),
                a.into_iter().fold(f64::NEG_INFINITY, f64::max),
            ]
        };
        let sections = pad
            .iter()
            .filter_map(|pad| {
                let p = pad.corners.map(local);
                let x = bounds(p.map(|p| p.x));
                let y = bounds(p.map(|p| p.y));
                (x[1] <= self.eps
                    && x[0] >= -self.width * 2.
                    && y[0] < half + self.width * 2.
                    && y[1] > -half - self.width * 2.
                    && p.iter().all(|p| {
                        (close(p.x, x[0]) || close(p.x, x[1]))
                            && (close(p.y, y[0]) || close(p.y, y[1]))
                    }))
                .then_some(Section { pad, x, y })
            })
            .collect::<Vec<_>>();
        *work = work.checked_sub(sections.len().saturating_pow(3))?;
        let full = |s: &Section<'_>| close(s.y[0], -half) && close(s.y[1], half);
        for face in sections.iter().filter(|s| {
            full(s)
                && close(s.x[1], 0.)
                && (self.width * 0.02..=self.width * 0.25).contains(&(s.x[1] - s.x[0]))
        }) {
            for body in sections.iter().filter(|s| {
                full(s)
                    && close(s.x[1], face.x[0])
                    && s.pad.layer == face.pad.layer
                    && path(s.pad.id) != path(face.pad.id)
                    && (self.width * 0.45..=self.width * 1.6).contains(&(s.x[1] - s.x[0]))
            }) {
                for leg in sections.iter().filter(|s| {
                    s.pad.layer == face.pad.layer
                        && close(s.x[0], body.x[0])
                        && close(s.x[1], body.x[1])
                        && path(s.pad.id) != path(face.pad.id)
                        && path(s.pad.id) != path(body.pad.id)
                        && (close(s.y[1], -half) || close(s.y[0], half))
                        && s.y[1] - s.y[0] >= self.width * 0.6
                }) {
                    let Some(ink) = rails.get(face.pad.layer) else {
                        continue;
                    };
                    *work = work.checked_sub(ink.len())?;
                    if let Some(stroke) = ink.iter().find(|r| {
                        if !r.ink
                            || [face, body, leg]
                                .iter()
                                .any(|s| path(r.id) == path(s.pad.id))
                        {
                            return false;
                        }
                        let [a, b] = r.ends.map(local);
                        close(a.x, 0.)
                            && close(b.x, 0.)
                            && close(a.y.min(b.y), leg.y[0])
                            && close(a.y.max(b.y), leg.y[1])
                    }) {
                        return Some(vec![face.pad.id, body.pad.id, leg.pad.id, stroke.id]);
                    }
                }
            }
        }
        None
    }
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let visible = page
        .placement_polygon_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    if visible.is_empty() {
        return;
    }
    let rectangle_paint = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let mut paint = HashMap::new();
    let mut interior = Vec::new();
    let mut rails = HashMap::<&str, Vec<Rail>>::new();
    for p in &page.primitives {
        let exterior = named_exterior_wall(p);
        let Some(layer) = exterior.or_else(|| interior_layer(p)) else {
            continue;
        };
        if exterior.is_some()
            && visible.contains(p.id.as_str())
            && let Some(vertices) = ring(&p.shape)
        {
            paint.insert(p.id.as_str(), (layer, vertices));
        } else if exterior.is_none()
            && (visible.contains(p.id.as_str()) || rectangle_paint.contains(p.id.as_str()))
            && let Some(corners) = super::painted_rectangle(&p.shape)
        {
            interior.push(Pad {
                id: &p.id,
                layer,
                corners,
            });
        }
        if let Shape::Line { start, end } = p.shape {
            rails.entry(layer).or_default().push(Rail {
                id: &p.id,
                ends: [start, end],
                ink: p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.),
            });
        }
    }
    if paint.is_empty() {
        return;
    }
    let width: HashMap<_, _> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .map(|(w, d)| (w.id.as_str(), effective_wall(w, Some(d)).thickness))
        .collect();
    let apertures = page
        .openings
        .iter()
        .filter_map(|o| {
            let width = o
                .host_wall_ids
                .iter()
                .filter_map(|id| width.get(id.as_str()).copied())
                .fold(0.0, f64::max);
            Body::new(o.start, o.end, width)
        })
        .collect::<Vec<_>>();
    let mut ready = Vec::new();
    let mut corners = Vec::new();
    let mut curves = Vec::new();
    for (w, decision) in page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
    {
        let measured = effective_wall(w, Some(decision));
        if let Some(arc) = w.curve {
            curves.push((arc.center, arc.radius + measured.thickness * 0.5));
            continue;
        }
        let Some(body) = Body::new(measured.start, measured.end, measured.thickness) else {
            continue;
        };
        ready.push(body);
        for id in &w.source_ids {
            if let Some(&(layer, vertices)) = paint.get(id.as_str()) {
                corners.push((id.as_str(), layer, body, vertices));
            }
        }
    }

    let mut work = MAX_WORK;
    let mut proposals = Vec::new();
    for (i, (w, d)) in page.walls.iter().zip(decisions.iter()).enumerate() {
        if d.status != WallStatus::Review
            || d.placement_extent.is_some()
            || !d.opening_ids.is_empty()
        {
            continue;
        }
        let [id] = w.source_ids.as_slice() else {
            continue;
        };
        let Some(&(layer, vertices)) = paint.get(id.as_str()) else {
            continue;
        };
        let Some(stock) = Stock::fit(w, vertices) else {
            continue;
        };
        let Some(exterior_ink) = rails.get(layer) else {
            continue;
        };
        let cost = ready
            .len()
            .saturating_add(curves.len())
            .saturating_add(apertures.len())
            .saturating_add(corners.len())
            .saturating_add(page.openings.len())
            .saturating_add(exterior_ink.len().saturating_mul(4));
        let Some(remaining) = work.checked_sub(cost) else {
            return;
        };
        work = remaining;
        if ready
            .iter()
            .chain(&apertures)
            .any(|&body| stock.body.overlaps(body, stock.eps))
            || curves.iter().any(|&(center, radius)| {
                stock.body.axes().into_iter().all(|axis| {
                    (center - stock.body.center).dot(axis).abs() < stock.body.radius(axis) + radius
                })
            })
        {
            continue;
        }
        let Some(faces) = stock.face_and_casing(id, exterior_ink) else {
            continue;
        };
        let Some(terminal) = stock
            .jamb(page, id, exterior_ink, &mut work)
            .or_else(|| stock.tee(&interior, &rails, &mut work))
        else {
            continue;
        };
        // A separate visible Ready wall turns at a native mitre vertex.
        let corner = corners
            .iter()
            .find(|&&(source, other_layer, body, points)| {
                other_layer == layer
                    && path(source) != path(id)
                    && body.axis.cross(stock.axis).abs() >= 0.2
                    && stock
                        .faces
                        .iter()
                        .map(|face| face[1 - stock.flat])
                        .any(|p| {
                            points
                                .iter()
                                .any(|&q| p.distance(stock.local(q)) <= stock.eps)
                        })
            });
        let Some(&(corner, _, _, _)) = corner else {
            continue;
        };
        let mut sources = [id.as_str(), faces[0], faces[1], corner]
            .into_iter()
            .chain(terminal)
            .map(str::to_owned)
            .collect::<Vec<_>>();
        sources.sort_unstable();
        sources.dedup();
        proposals.push((i, stock, sources));
    }
    if proposals.len().saturating_pow(2) > work {
        return;
    }
    for (i, stock, sources) in &proposals {
        if proposals.iter().any(|(j, other, _)| {
            i != j && stock.body.overlaps(other.body, stock.eps.max(other.eps))
        }) {
            continue;
        }
        let d = &mut decisions[*i];
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedExteriorMitreStock;
        d.support_source_ids = sources.clone();
        d.placement_extent = Some(PlacementExtent {
            start: stock.span[0],
            end: stock.span[1],
            thickness: Some(stock.body.half_width * 2.),
            source_ids: sources.clone(),
        });
    }
}
