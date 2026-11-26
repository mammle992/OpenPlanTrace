//! Place the common inscribed body of a painted wall with two skew end caps.
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, effective_wall, roundoff};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{HashMap, HashSet};

const MAX_WORK: usize = 8_000_000;

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    width: f64,
    epsilon: f64,
}

impl Frame {
    fn of(wall: &Wall) -> Option<Self> {
        let width = wall.thickness;
        let length = wall.start.distance(wall.end);
        (wall.body == BodyEvidence::FilledBody
            && wall.curve.is_none()
            && width.is_finite()
            && width > 0.
            && length.is_finite()
            && length >= 8. * width)
            .then(|| Self {
                origin: wall.start,
                axis: (wall.end - wall.start).unit(),
                length,
                width,
                epsilon: roundoff(wall).max(width * 1e-7),
            })
    }

    fn local(&self, point: Point) -> Point {
        let delta = point - self.origin;
        Point::new(delta.dot(self.axis), self.axis.cross(delta))
    }
}

struct Face {
    points: [Point; 2],
    bounds: [Point; 2],
}

impl Face {
    fn new(frame: &Frame, a: Point, b: Point) -> Self {
        let [u, v] = [a, b].map(|p| frame.local(p));
        if u.x <= v.x {
            Self {
                points: [a, b],
                bounds: [u, v],
            }
        } else {
            Self {
                points: [b, a],
                bounds: [v, u],
            }
        }
    }

    fn aligned(&self, frame: &Frame) -> bool {
        let [a, b] = self.bounds;
        b.x - a.x >= 0.85 * frame.length
            && (a.y - b.y).abs() <= 0.001 * frame.width
            && a.y * b.y > 0.
            && [a, b]
                .into_iter()
                .all(|p| (0.495 * frame.width..=0.505 * frame.width).contains(&p.y.abs()))
    }
}

#[derive(Clone, Copy)]
struct Footprint {
    center: Point,
    direction: Point,
    radius: f64,
    half_width: f64,
}

impl Footprint {
    fn new(start: Point, end: Point, width: f64) -> Option<Self> {
        let delta = end - start;
        let length = delta.norm();
        (length > 0. && length.is_finite() && width >= 0. && width.is_finite()).then(|| Self {
            center: start + delta * 0.5,
            direction: delta.unit(),
            radius: length * 0.5,
            half_width: width * 0.5,
        })
    }

    fn axes(self) -> [Point; 2] {
        let u = self.direction;
        [u, Point::new(-u.y, u.x)]
    }

    fn reach(self, axis: Point) -> f64 {
        self.radius * self.direction.dot(axis).abs()
            + self.half_width * self.direction.cross(axis).abs()
    }

    fn overlaps(self, other: Self, epsilon: f64) -> bool {
        let delta = other.center - self.center;
        self.axes()
            .into_iter()
            .chain(other.axes())
            .all(|axis| delta.dot(axis).abs() < self.reach(axis) + other.reach(axis) - epsilon)
    }

    fn near_arc(self, center: Point, radius: f64, epsilon: f64) -> bool {
        let delta = center - self.center;
        self.axes()
            .into_iter()
            .all(|axis| delta.dot(axis).abs() <= self.reach(axis) + radius + epsilon)
    }
}

struct Core {
    faces: [Face; 2],
    start: Point,
    end: Point,
    width: f64,
    footprint: Footprint,
}

fn contains(ring: &[Point; 4], point: Point, epsilon: f64) -> bool {
    let edges = ring
        .iter()
        .copied()
        .zip(ring.iter().copied().cycle().skip(1));
    let area: f64 = edges
        .clone()
        .map(|(a, b)| (a - ring[0]).cross(b - ring[0]))
        .sum();
    let sign = area.signum();
    area.abs() > epsilon * epsilon
        && edges.into_iter().all(|(a, b)| {
            let edge = b - a;
            sign * edge.cross(point - a) >= -epsilon * edge.norm()
        })
}

impl Core {
    fn from_polygon(frame: &Frame, points: &[Point; 4]) -> Option<Self> {
        let faces: Vec<_> = points
            .iter()
            .copied()
            .zip(points.iter().copied().cycle().skip(1))
            .map(|(a, b)| Face::new(frame, a, b))
            .filter(|face| face.aligned(frame))
            .collect();
        let [a, b]: [Face; 2] = faces.try_into().ok()?;
        let [first, last] = [0, 1].map(|i| a.bounds[i].x - b.bounds[i].x);
        if a.bounds[0].y * b.bounds[0].y >= 0.
            || first * last <= 0.
            || [first, last]
                .into_iter()
                .any(|shift| !(0.05 * frame.width..=0.65 * frame.width).contains(&shift.abs()))
            || (first - last).abs() > 0.05 * frame.width
        {
            return None;
        }
        // Each long face is affine in source coordinates. Taking their common
        // longitudinal interval and minimum transverse distance lies inside
        // both slanted end planes and both faces, including slight face drift.
        let lo = a.bounds[0].x.max(b.bounds[0].x).max(0.) + frame.epsilon;
        let hi = a.bounds[1].x.min(b.bounds[1].x).min(frame.length) - frame.epsilon;
        let half = a
            .bounds
            .into_iter()
            .chain(b.bounds)
            .map(|p| p.y.abs())
            .fold(0.5 * frame.width, f64::min)
            - frame.epsilon;
        if hi - lo < 0.9 * frame.length || half < 0.495 * frame.width {
            return None;
        }
        let corners = [lo, hi]
            .into_iter()
            .flat_map(|x| [-half, half].into_iter().map(move |y| Point::new(x, y)));
        let ring = points.map(|p| frame.local(p));
        if corners
            .into_iter()
            .any(|p| !contains(&ring, p, frame.epsilon * 0.25))
        {
            return None;
        }
        let start = frame.origin + frame.axis * lo;
        let end = frame.origin + frame.axis * hi;
        let width = 2. * half;
        Some(Self {
            faces: [a, b],
            start,
            end,
            width,
            footprint: Footprint::new(start, end, width)?,
        })
    }

    fn casing(&self, frame: &Frame, inner_side: f64, a: Point, b: Point) -> bool {
        let [a, b] = Face::new(frame, a, b).bounds;
        let [lo, hi] = [
            self.faces[0].bounds[0].x.min(self.faces[1].bounds[0].x),
            self.faces[0].bounds[1].x.max(self.faces[1].bounds[1].x),
        ];
        if a.x > lo + frame.epsilon || b.x < hi - frame.epsilon || a.x >= b.x {
            return false;
        }
        let slope = (b.y - a.y) / (b.x - a.x);
        slope.abs() * frame.length <= 0.001 * frame.width
            && [lo, hi].into_iter().all(|x| {
                let offset = a.y + slope * (x - a.x);
                offset * inner_side < 0.
                    && (0.52 * frame.width..=1.1 * frame.width).contains(&offset.abs())
            })
    }
}

fn structural_layer(source: &Primitive) -> Option<&str> {
    let layer = source.layer.as_deref()?;
    if source.layer_category_override.is_some() {
        (source.layer_kind() == LayerKind::Wall).then_some(layer)
    } else {
        crate::layers::is_named_wall(layer).then_some(layer)
    }
}

fn vertices(p: &Primitive) -> Option<[Point; 4]> {
    let Shape::Polyline {
        points,
        filled: true,
        closed: true,
    } = &p.shape
    else {
        return None;
    };
    match points.as_slice() {
        [a, b, c, d] => Some([*a, *b, *c, *d]),
        [a, b, c, d, end] if a == end => Some([*a, *b, *c, *d]),
        _ => None,
    }
}

struct Rail<'a> {
    id: &'a str,
    ends: [Point; 2],
    visible_pen: bool,
}

fn drawing_path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(path, _)| path)
}

fn witness<'a>(
    frame: &Frame,
    core: &Core,
    paint: &str,
    rails: &[Rail<'a>],
) -> Option<[&'a str; 2]> {
    for face in &core.faces {
        let Some(inner) = rails.iter().find(|r| {
            let [a, b] = r.ends;
            let [c, d] = face.points;
            drawing_path(r.id) != drawing_path(paint)
                && (a.distance(c) <= frame.epsilon && b.distance(d) <= frame.epsilon
                    || a.distance(d) <= frame.epsilon && b.distance(c) <= frame.epsilon)
        }) else {
            continue;
        };
        if let Some(outer) = rails.iter().find(|r| {
            r.visible_pen
                && drawing_path(r.id) != drawing_path(paint)
                && drawing_path(r.id) != drawing_path(inner.id)
                && core.casing(frame, face.bounds[0].y, r.ends[0], r.ends[1])
        }) {
            return Some([inner.id, outer.id]);
        }
    }
    None
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let authorized: HashSet<_> = page
        .placement_polygon_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    if authorized.is_empty() {
        return;
    }
    let mut paint = HashMap::new();
    let mut layers = HashMap::<&str, Vec<Rail<'_>>>::new();
    for p in &page.primitives {
        if authorized.contains(p.id.as_str())
            && let Some(layer) = structural_layer(p)
            && let Some(points) = vertices(p)
        {
            paint.insert(p.id.as_str(), (layer, points));
            layers.entry(layer).or_default();
        }
    }
    if paint.is_empty() {
        return;
    }
    for p in &page.primitives {
        if let Some(rails) = structural_layer(p).and_then(|layer| layers.get_mut(layer))
            && let Shape::Line { start, end } = p.shape
        {
            rails.push(Rail {
                id: &p.id,
                ends: [start, end],
                visible_pen: p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.),
            });
        }
    }
    let mut ready = Vec::new();
    let mut curves = Vec::new();
    let widths: HashMap<_, _> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .map(|(w, d)| {
            let measured = effective_wall(w, Some(d));
            if d.status == WallStatus::Ready {
                if let Some(arc) = w.curve {
                    curves.push((arc.center, arc.radius + 0.5 * measured.thickness));
                } else if let Some(body) =
                    Footprint::new(measured.start, measured.end, measured.thickness)
                {
                    ready.push(body);
                }
            }
            (w.id.as_str(), measured.thickness)
        })
        .collect();
    let mut hosts = HashSet::new();
    let apertures: Vec<_> = page
        .openings
        .iter()
        .filter_map(|o| {
            hosts.extend(o.host_wall_ids.iter().map(String::as_str));
            let width = o
                .host_wall_ids
                .iter()
                .filter_map(|id| widths.get(id.as_str()))
                .copied()
                .fold(0_f64, f64::max);
            Footprint::new(o.start, o.end, width)
        })
        .collect();
    let mut work = MAX_WORK;
    let mut proposals = vec![];
    for (i, (wall, decision)) in page.walls.iter().zip(decisions.iter()).enumerate() {
        if decision.status != WallStatus::Review
            || decision.placement_extent.is_some()
            || !decision.opening_ids.is_empty()
            || hosts.contains(wall.id.as_str())
        {
            continue;
        }
        let [id] = wall.source_ids.as_slice() else {
            continue;
        };
        let Some((layer, points)) = paint.get(id.as_str()) else {
            continue;
        };
        let Some(frame) = Frame::of(wall) else {
            continue;
        };
        let Some(core) = Core::from_polygon(&frame, points) else {
            continue;
        };
        let rails = &layers[layer];
        let cost = ready
            .len()
            .saturating_add(curves.len())
            .saturating_add(apertures.len())
            .saturating_add(rails.len().saturating_mul(4));
        let Some(remaining) = work.checked_sub(cost) else {
            return;
        };
        work = remaining;
        if ready
            .iter()
            .chain(&apertures)
            .any(|&b| core.footprint.overlaps(b, frame.epsilon))
            || curves
                .iter()
                .any(|&(center, radius)| core.footprint.near_arc(center, radius, frame.epsilon))
        {
            continue;
        }
        let Some(rails) = witness(&frame, &core, id, rails) else {
            continue;
        };
        let mut sources = [id.as_str(), rails[0], rails[1]]
            .map(str::to_owned)
            .to_vec();
        sources.sort_unstable();
        proposals.push((i, core, frame.epsilon, sources));
    }
    if proposals.len().saturating_mul(proposals.len()) > work {
        return;
    }
    for (i, core, epsilon, sources) in &proposals {
        if proposals.iter().any(|(j, other, other_epsilon, _)| {
            i != j
                && core
                    .footprint
                    .overlaps(other.footprint, epsilon.max(*other_epsilon))
        }) {
            continue;
        }
        let d = &mut decisions[*i];
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedSkewPaintedCore;
        d.support_source_ids = sources.clone();
        d.placement_extent = Some(PlacementExtent {
            start: core.start,
            end: core.end,
            thickness: Some(core.width),
            source_ids: sources.clone(),
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn measured_skew_caps_do_not_export_beyond_their_common_span() {
        let start = Point::new(30., 40.);
        let axis = Point::new(0.8, 0.6);
        let offset = Point::new(-0.6, 0.8);
        let world = |x, y| start + axis * x + offset * y;
        let frame = Frame {
            origin: start,
            axis,
            length: 40.,
            width: 4.,
            epsilon: 1e-9,
        };
        let points = [
            world(0., 2.),
            world(-1.5, -2.),
            world(40., -2.),
            world(41.5, 1.999),
        ];
        let core = Core::from_polygon(&frame, &points).unwrap();
        assert!(frame.local(core.start).x > 0.);
        assert!(frame.local(core.end).x < 40.);
        assert!((core.width - 3.998).abs() < 1e-7);
        assert!(core.casing(&frame, 2., world(-4., -3.), world(44., -3.)));
        assert!(!core.casing(&frame, 2., world(2., -3.), world(44., -3.)));
        assert!(!core.casing(&frame, 2., world(-4., 3.), world(44., 3.)));

        let square = [
            world(0., 2.),
            world(0., -2.),
            world(40., -2.),
            world(40., 2.),
        ];
        assert!(Core::from_polygon(&frame, &square).is_none());
        let through = Footprint::new(world(10., -5.), world(10., 5.), 1.).unwrap();
        let clear = Footprint::new(world(41., -5.), world(41., 5.), 1.).unwrap();
        assert!(core.footprint.overlaps(through, frame.epsilon));
        assert!(!core.footprint.overlaps(clear, frame.epsilon));
    }
}
