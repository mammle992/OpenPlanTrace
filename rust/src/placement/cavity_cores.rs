//! Two independently painted structural plies form one wall axis.
use super::{
    PlacementExtent, WallDecision, WallReason, WallStatus, effective_wall, material, overlap,
    painted_rectangle, roundoff,
};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 16_000_000;

fn charge(work: &mut usize, amount: usize) -> Option<()> {
    *work = work.checked_sub(amount)?;
    Some(())
}

#[derive(Clone, Copy)]
struct Band<'a> {
    id: &'a str,
    layer: &'a str,
    corners: [Point; 4],
    mitred: bool,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Stock {
    Whole,
    Mitred,
    Remainder,
    DoubleInset,
}

fn clipped_leaf(wall: &Wall, band: Band<'_>) -> Option<Wall> {
    let axis = (wall.end - wall.start).unit();
    let length = wall.start.distance(wall.end);
    let epsilon = roundoff(wall).max(wall.thickness * 1e-5);
    let mut range = [length, 0_f64];
    for p in band.corners {
        let local = p - wall.start;
        let t = local.dot(axis);
        if (axis.cross(local).abs() - wall.thickness * 0.5).abs() > epsilon
            || t < -epsilon
            || t > length + epsilon
        {
            return None;
        }
        range = [range[0].min(t), range[1].max(t)];
    }
    let [lo, hi] = range;
    if hi - lo < length * 0.875 || (lo <= epsilon) == (length - hi <= epsilon) {
        return None;
    }
    let mut clipped = wall.clone();
    clipped.start = wall.start + axis * lo;
    clipped.end = wall.start + axis * hi;
    Some(clipped)
}

/// A square-ended trapezoid supplies a finite rectangle only up to the
/// shorter of its two parallel faces. The angled stock outside that common
/// interval remains source evidence.
fn inscribed_mitre(shape: &Shape) -> Option<[Point; 4]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = shape
    else {
        return None;
    };
    let ring = match points.as_slice() {
        [a, b, c, d] => [*a, *b, *c, *d],
        [a, b, c, d, end] if a == end => [*a, *b, *c, *d],
        _ => return None,
    };
    let edge = |index| ring[(index + 1) % 4] - ring[index];
    let face = (0..4).max_by(|&a, &b| edge(a).norm().total_cmp(&edge(b).norm()))?;
    let u = edge(face).unit();
    let origin = ring[face];
    let n = Point::new(-u.y, u.x);
    let local = |p: Point| Point::new((p - origin).dot(u), (p - origin).dot(n));
    let [a, b] = [ring[(face + 3) % 4], ring[(face + 2) % 4]].map(local);
    let length = edge(face).norm();
    let width = a.y.abs();
    let eps = 128. * f64::EPSILON * length.max(origin.norm()).max(1.);
    if width <= eps || (a.y - b.y).abs() > eps || a.x >= b.x || (b.x - a.x).min(length) < 6. * width
    {
        return None;
    }
    let [left, right] = [a.x.abs(), (length - b.x).abs()];
    let skew = left.max(right);
    if left.min(right) > eps || !(0.08 * width..=1.25 * width).contains(&skew) {
        return None;
    }
    let [lo, hi] = [a.x.max(0.), b.x.min(length)];
    let point = |x, y| origin + u * x + n * y;
    Some([point(lo, 0.), point(hi, 0.), point(hi, a.y), point(lo, a.y)])
}

fn structural(p: &Primitive) -> Option<&str> {
    let layer = p.layer.as_deref()?;
    let eligible = match p.layer_category_override {
        Some(_) => p.layer_kind() == LayerKind::Wall,
        None => material::wall_name(layer).1,
    };
    eligible.then_some(layer)
}

fn draw_path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(path, _)| path)
}

struct Leaf<'a> {
    index: usize,
    paint: Band<'a>,
    wall: &'a Wall,
    axis: Point,
    length: f64,
    half: f64,
    epsilon: f64,
}

#[derive(Clone, Copy)]
struct Interval {
    along: [f64; 2],
    across: [f64; 2],
}

impl<'a> Leaf<'a> {
    fn new(index: usize, wall: &'a Wall, painted: &HashMap<&str, Band<'a>>) -> Option<Self> {
        if wall.body != BodyEvidence::FilledBody
            || wall.curve.is_some()
            || !wall.thickness.is_finite()
            || wall.thickness <= 0.
            || !wall.start.finite()
            || !wall.end.finite()
        {
            return None;
        }
        let span = wall.end - wall.start;
        let length = span.norm();
        if !length.is_finite() || length < wall.thickness * 8. {
            return None;
        }
        let mut exact = wall
            .source_ids
            .iter()
            .filter_map(|id| painted.get(id.as_str()).copied());
        let paint = exact.next()?;
        if exact.next().is_some() {
            return None;
        }
        let leaf = Self {
            index,
            paint,
            wall,
            axis: span * (1. / length),
            length,
            half: wall.thickness * 0.5,
            epsilon: roundoff(wall).max(wall.thickness * 1e-5),
        };
        let measured = leaf.interval(paint)?;
        let eps = leaf.epsilon;
        (measured.along[0].abs() <= eps
            && (measured.along[1] - length).abs() <= eps
            && (measured.across[0] + leaf.half).abs() <= eps
            && (measured.across[1] - leaf.half).abs() <= eps)
            .then_some(leaf)
    }

    fn interval(&self, paint: Band<'_>) -> Option<Interval> {
        let points = paint.corners.map(|p| {
            let p = p - self.wall.start;
            Point::new(p.dot(self.axis), self.axis.cross(p))
        });
        let lo =
            |coord: fn(Point) -> f64| points.into_iter().map(coord).fold(f64::INFINITY, f64::min);
        let hi = |coord: fn(Point) -> f64| {
            points
                .into_iter()
                .map(coord)
                .fold(f64::NEG_INFINITY, f64::max)
        };
        let [a, b] = [lo(|p| p.x), hi(|p| p.x)];
        let [u, v] = [lo(|p| p.y), hi(|p| p.y)];
        (b - a > self.epsilon
            && v - u > self.epsilon
            && [a, b].into_iter().all(|x| {
                [u, v].into_iter().all(|y| {
                    points
                        .iter()
                        .any(|p| p.distance(Point::new(x, y)) <= self.epsilon)
                })
            }))
        .then_some(Interval {
            along: [a, b],
            across: [u, v],
        })
    }

    fn envelope(&self, b: Interval) -> Wall {
        let cross = [(-self.half).min(b.across[0]), self.half.max(b.across[1])];
        let normal = Point::new(-self.axis.y, self.axis.x) * cross[0].midpoint(cross[1]);
        let mut combined = self.wall.clone();
        combined.start = self.wall.start + self.axis * b.along[0].min(0.) + normal;
        combined.end = self.wall.start + self.axis * b.along[1].max(self.length) + normal;
        combined.thickness = cross[1] - cross[0];
        combined
    }
}

struct Proof {
    indices: [usize; 2],
    ends: [(Point, Point); 2],
    envelope: Wall,
    ids: Vec<String>,
}

fn coverage<'a>(
    parts: &mut [(f64, f64, &'a str)],
    interval: [f64; 2],
    eps: f64,
) -> (f64, Vec<&'a str>) {
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = interval[0];
    let mut length = 0.;
    let mut ids = vec![];
    for &(a, b, id) in parts.iter() {
        let lo = a.max(interval[0]).max(end);
        let hi = b.min(interval[1]);
        if hi > lo + eps {
            length += hi - lo;
            end = hi;
            ids.push(id);
        }
    }
    (length, ids)
}

#[derive(Clone, Copy)]
struct CapPaint<'a> {
    id: &'a str,
    vertices: [Point; 3],
}

fn cap_triangle(p: &Primitive) -> Option<[Point; 3]> {
    let Shape::Polyline {
        points,
        filled: true,
        closed: true,
    } = &p.shape
    else {
        return None;
    };
    let vertices = match points.as_slice() {
        [a, b, c, end] if a == end => [*a, *b, *c],
        [a, b, c] => [*a, *b, *c],
        _ => return None,
    };
    vertices.iter().all(|p| p.finite()).then_some(vertices)
}

fn named_interior(layer: &str) -> bool {
    crate::layers::is_named_wall(layer)
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
            })
}

#[derive(Clone, Copy)]
struct EndFrame {
    origin: Point,
    inward: Point,
    across: Point,
    gap: f64,
    inset: f64,
    short_width: f64,
    eps: f64,
}

impl EndFrame {
    fn local(self, point: Point) -> Point {
        let d = point - self.origin;
        Point::new(d.dot(self.inward), d.dot(self.across))
    }

    fn contains_triangle(self, triangle: &CapPaint<'_>, expected: [(f64, f64); 3]) -> bool {
        let points = triangle.vertices.map(|p| self.local(p));
        expected.into_iter().all(|(x, y)| {
            points
                .iter()
                .any(|p| p.distance(Point::new(x, y)) <= self.eps)
        })
    }

    fn end_witness<'a>(
        self,
        rectangles: impl Iterator<Item = (Interval, &'a str)>,
        triangles: &[CapPaint<'a>],
        work: &mut usize,
    ) -> Option<Option<Vec<&'a str>>> {
        let matches = |range: [f64; 2], target: [f64; 2]| {
            (range[0] - target[0])
                .abs()
                .max((range[1] - target[1]).abs())
                <= self.eps
        };
        let mut middle = Vec::new();
        let mut shorter = Vec::new();
        for (r, id) in rectangles {
            let [a, b] = r.along;
            if a >= self.inset - self.eps || b <= self.eps {
                continue;
            }
            if matches(r.across, [0., self.gap]) {
                middle.push((a, b, id));
            } else if matches(r.across, [self.gap, self.gap + self.short_width])
                && a >= -self.eps
                && b <= self.inset + self.eps
            {
                shorter.push((a, b, id));
            }
        }
        // A square return starts at the measured long cap and has an authored
        // subdivision in the other ply. The full gap lane must also close.
        if middle.iter().any(|(a, _, _)| a.abs() <= self.eps) {
            let (total, mut ids) = coverage(&mut middle, [0., self.inset], self.eps);
            let (returned, extra) = coverage(&mut shorter, [0., self.inset], self.eps);
            if total >= self.inset - self.eps
                && returned >= self.inset - self.eps
                && extra.len() >= 2
            {
                ids.extend(extra);
                return Some(Some(ids));
            }
        }
        charge(work, triangles.len())?;
        for middle_cap in triangles {
            let t = middle_cap
                .vertices
                .map(|p| self.local(p).x)
                .into_iter()
                .fold(f64::NEG_INFINITY, f64::max);
            if !(self.gap * 0.3..=self.gap * 1.8).contains(&t)
                || self.inset - t <= self.short_width * 0.3
                || !self.contains_triangle(middle_cap, [(0., 0.), (t, 0.), (t, self.gap)])
            {
                continue;
            }
            let (length, mut ids) = coverage(&mut middle, [t, self.inset], self.eps);
            if length < self.inset - t - self.eps {
                continue;
            }
            charge(work, triangles.len())?;
            if let Some(short_cap) = triangles.iter().find(|p| {
                draw_path(p.id) != draw_path(middle_cap.id)
                    && self.contains_triangle(
                        p,
                        [
                            (t, self.gap),
                            (self.inset, self.gap),
                            (self.inset, self.gap + self.short_width),
                        ],
                    )
            }) {
                ids.extend([middle_cap.id, short_cap.id]);
                return Some(Some(ids));
            }
        }
        Some(None)
    }
}

fn double_inset_ends<'a>(
    a: &Leaf<'a>,
    b: &Leaf<'a>,
    paints: &[Band<'a>],
    triangles: &[CapPaint<'a>],
    across: [f64; 2],
    along: [f64; 2],
    work: &mut usize,
) -> Option<Option<Vec<&'a str>>> {
    let [u, v] = along;
    let minimum = a.wall.thickness.min(b.wall.thickness) * 0.2;
    if u < minimum || a.length - v < minimum {
        return Some(None);
    }
    let sign = if across[0] > 0. { 1. } else { -1. };
    let gap = across[1] - across[0];
    let across = Point::new(-a.axis.y, a.axis.x) * sign;
    let mut witnesses = Vec::new();
    for (terminal, sign_inward, inset) in [(a.wall.start, 1., u), (a.wall.end, -1., a.length - v)] {
        charge(work, paints.len())?;
        let frame = EndFrame {
            origin: terminal + across * a.half,
            inward: a.axis * sign_inward,
            across,
            gap,
            inset,
            short_width: b.wall.thickness,
            eps: a.epsilon,
        };
        let station = (terminal - a.wall.start).dot(a.axis);
        let rects = paints.iter().filter_map(|paint| {
            if draw_path(paint.id) == draw_path(a.paint.id)
                || draw_path(paint.id) == draw_path(b.paint.id)
            {
                return None;
            }
            let interval = a.interval(*paint)?;
            let [lo, hi] = interval.along.map(|t| (t - station) * sign_inward);
            let [left, right] = interval.across.map(|x| x * sign - a.half);
            Some((
                Interval {
                    along: [lo.min(hi), lo.max(hi)],
                    across: [left.min(right), left.max(right)],
                },
                paint.id,
            ))
        });
        let Some(ends) = frame.end_witness(rects, triangles, work)? else {
            return Some(None);
        };
        if ends.iter().any(|id| witnesses.contains(id)) {
            // A common middle strip corroborates both ends, but they must
            // have different cap parts. A long line cannot certify both.
            let independent = ends.iter().filter(|&&id| !witnesses.contains(&id)).count();
            if independent < 2 {
                return Some(None);
            }
        }
        witnesses.extend(ends);
    }
    Some(Some(witnesses))
}

fn crossing_finish<'a>(
    a: &Leaf<'_>,
    foreign: &[Band<'a>],
    face: f64,
    side: f64,
    along: [f64; 2],
    width: f64,
    work: &mut usize,
) -> Option<Vec<(f64, f64, &'a str, Vec<&'a str>)>> {
    let eps = a.epsilon;
    let surface = |r: Interval| {
        let [p, q] = r.across.map(|v| (v - face) * side);
        [p.min(q), p.max(q)]
    };
    let mut patches = Vec::new();
    charge(work, foreign.len())?;
    for paint in foreign {
        if paint.layer == a.paint.layer {
            continue;
        }
        let Some(strip) = a.interval(*paint) else {
            continue;
        };
        let [u, v] = strip.along;
        let [lo, hi] = surface(strip);
        if lo.abs() > eps
            || hi > width * 0.5
            || u < along[0] - eps
            || v > along[1] + eps
            || !(width * 0.75..=width * 1.8).contains(&(v - u))
        {
            continue;
        }
        charge(work, foreign.len())?;
        let mut returns: Vec<_> = foreign
            .iter()
            .filter_map(|p| {
                if p.layer != paint.layer || draw_path(p.id) == draw_path(paint.id) {
                    return None;
                }
                let r = a.interval(*p)?;
                let [l, h] = surface(r);
                ((r.along[0] - u).abs().max((r.along[1] - v).abs()) <= eps && l >= hi - eps)
                    .then_some((l, h, p.id))
            })
            .collect();
        let depth = width * 0.75;
        let (filled, sources) = coverage(&mut returns, [hi, hi + depth], eps);
        if filled >= depth - eps && sources.len() >= 2 {
            patches.push((u, v, paint.id, sources));
        }
    }
    Some(patches)
}

fn prove(
    a: &Leaf<'_>,
    b: &Leaf<'_>,
    paints: &[Band<'_>],
    double_inset: Option<&[CapPaint<'_>]>,
    certified_interiors: &[Band<'_>],
    work: &mut usize,
) -> Option<Option<Proof>> {
    let width = a.wall.thickness.min(b.wall.thickness);
    if (a.wall.thickness - b.wall.thickness).abs() > width * 0.15
        || a.axis.cross(b.axis).abs() > 1e-5
        || draw_path(a.paint.id) == draw_path(b.paint.id)
    {
        return Some(None);
    }
    let Some(other) = a.interval(b.paint) else {
        return Some(None);
    };
    let eps = a.epsilon;
    let [l, r] = other.along;
    let interval = [l.max(0.), r.min(a.length)];
    let len = interval[1] - interval[0];
    if len < a.length.max(b.length) * 0.90
        || l.abs().max((r - a.length).abs()) > width * 1.8
        || (double_inset.is_none() && l.abs().min((r - a.length).abs()) > eps)
        || (double_inset.is_some() && (l < width * 0.2 || a.length - r < width * 0.2))
    {
        return Some(None);
    }
    let (gap, outward) = if other.across[0] > a.half + eps {
        ([a.half, other.across[0]], [-a.half, other.across[1]])
    } else if other.across[1] < -a.half - eps {
        ([other.across[1], -a.half], [other.across[0], a.half])
    } else {
        return Some(None);
    };
    if !(width * 0.15..=width * 0.6).contains(&(gap[1] - gap[0])) {
        return Some(None);
    }
    let mut interior = vec![];
    let mut finishes = [vec![], vec![]];
    charge(work, paints.len())?;
    for &p in paints {
        if draw_path(p.id) == draw_path(a.paint.id) || draw_path(p.id) == draw_path(b.paint.id) {
            continue;
        }
        let Some(paint) = a.interval(p) else {
            continue;
        };
        let [x, y] = paint.along;
        if x >= interval[1] - eps || y <= interval[0] + eps {
            continue;
        }
        let [lo, hi] = paint.across;
        if (lo - gap[0]).abs() <= eps && (hi - gap[1]).abs() <= eps {
            interior.push((x, y, p.id));
            continue;
        }
        if hi - lo > width * 0.5 {
            continue;
        }
        if (hi - outward[0]).abs() <= eps {
            finishes[0].push((x, y, p.id));
        }
        if (lo - outward[1]).abs() <= eps {
            finishes[1].push((x, y, p.id));
        }
    }
    let mut visible_returns = vec![];
    if let Some(triangles) = double_inset {
        charge(work, triangles.len())?;
        for p in triangles {
            let local = p.vertices.map(|v| {
                let v = v - a.wall.start;
                Point::new(v.dot(a.axis), a.axis.cross(v))
            });
            for (side, sign) in [-1., 1.].into_iter().enumerate() {
                let face = outward[side];
                let line = local
                    .iter()
                    .filter(|v| (v.y - face).abs() <= eps)
                    .collect::<Vec<_>>();
                if let [a, b] = line.as_slice()
                    && local.iter().all(|p| {
                        ((p.y - face) * sign) >= -eps && ((p.y - face) * sign) <= width * 0.5
                    })
                {
                    finishes[side].push((a.x.min(b.x), a.x.max(b.x), p.id));
                }
            }
        }
        for (side, sign) in [-1., 1.].into_iter().enumerate() {
            let (depth, _) = coverage(&mut finishes[side], interval, eps);
            if depth >= len - eps {
                continue;
            }
            // A perpendicular painted wall may supply the exact same finish
            // band at its own finite end. Its distinct outward return must
            // be source-proven in one compatible wall layer.
            let candidates = crossing_finish(
                a,
                certified_interiors,
                outward[side],
                sign,
                interval,
                width,
                work,
            )?;
            if let [(u, v, id, proof)] = candidates.as_slice() {
                finishes[side].push((*u, *v, *id));
                visible_returns.extend(proof.iter().copied());
            }
        }
    }
    let (separation, inside) = coverage(&mut interior, interval, eps);
    let [left, right] = finishes.each_mut().map(|f| coverage(f, interval, eps));
    let finish = if double_inset.is_some() {
        len - eps
    } else {
        len * 0.70
    };
    if separation < len - eps || left.0 < finish || right.0 < finish {
        return Some(None);
    }
    let terminal_sources = if let Some(triangles) = double_inset {
        let Some(witnesses) = double_inset_ends(a, b, paints, triangles, gap, [l, r], work)? else {
            return Some(None);
        };
        witnesses
    } else {
        vec![]
    };
    // The intervening strip and finishes corroborate grouping. Export only
    // the two exact observed wall plies as wall material.
    let ids = [a.paint.id, b.paint.id]
        .into_iter()
        .chain(inside)
        .chain(left.1)
        .chain(right.1)
        .chain(terminal_sources)
        .chain(visible_returns)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .map(str::to_owned)
        .collect();
    let ordered = if a.length >= b.length { [a, b] } else { [b, a] };
    Some(Some(Proof {
        indices: ordered.map(|leaf| leaf.index),
        ends: ordered.map(|leaf| (leaf.wall.start, leaf.wall.end)),
        envelope: a.envelope(other),
        ids,
    }))
}

fn proposals(
    page: &PageScan,
    decisions: &[WallDecision],
    work: &mut usize,
    stock: Stock,
) -> Option<Vec<Proof>> {
    let allowed: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    let mut positive_polygons: HashSet<_> = if matches!(stock, Stock::Mitred | Stock::DoubleInset) {
        page.placement_polygon_paint_ids
            .iter()
            .flatten()
            .map(String::as_str)
            .collect()
    } else {
        HashSet::new()
    };
    if stock == Stock::DoubleInset {
        charge(work, page.placement_contact_paint.len())?;
        positive_polygons.extend(
            page.placement_contact_paint
                .iter()
                .filter(|r| r.occluder_source_ids.is_empty())
                .map(|r| r.source_id.as_str()),
        );
    }
    let remainders: HashMap<_, _> = if stock == Stock::Remainder {
        page.placement_paint_remainders
            .iter()
            .map(|r| (r.source_id.as_str(), r))
            .collect()
    } else {
        HashMap::new()
    };
    if (allowed.is_empty() && remainders.is_empty())
        || (stock == Stock::Mitred && positive_polygons.is_empty())
        || (stock == Stock::Remainder && remainders.is_empty())
    {
        return Some(vec![]);
    }
    charge(work, page.primitives.len())?;
    let mut layers = BTreeMap::<&str, Vec<Band<'_>>>::new();
    let mut caps = BTreeMap::<&str, Vec<CapPaint<'_>>>::new();
    let mut painted = HashMap::new();
    let masked: HashSet<_> = if stock == Stock::DoubleInset {
        page.placement_contact_paint
            .iter()
            .filter(|r| !r.occluder_source_ids.is_empty())
            .map(|r| r.source_id.as_str())
            .collect()
    } else {
        HashSet::new()
    };
    for p in &page.primitives {
        let rectangular = allowed.contains(p.id.as_str());
        let angled = positive_polygons.contains(p.id.as_str());
        let remaining = remainders.get(p.id.as_str());
        if !rectangular && !angled && remaining.is_none() {
            continue;
        }
        if stock == Stock::DoubleInset {
            if masked.contains(p.id.as_str()) || !structural(p).is_some_and(named_interior) {
                continue;
            }
            if angled && let Some(vertices) = cap_triangle(p) {
                let layer = p.layer.as_deref().expect("qualified structural layer");
                caps.entry(layer).or_default().push(CapPaint {
                    id: &p.id,
                    vertices,
                });
            }
            if !rectangular {
                continue;
            }
        }
        let corners = if let Some(r) = remaining {
            Some(r.outline)
        } else if rectangular {
            painted_rectangle(&p.shape)
        } else {
            inscribed_mitre(&p.shape)
        };
        if let (Some(layer), Some(corners)) = (structural(p), corners) {
            let band = Band {
                id: &p.id,
                layer,
                corners,
                mitred: angled && !rectangular,
            };
            layers.entry(layer).or_default().push(band);
            painted.insert(p.id.as_str(), band);
        }
    }
    let mut leaves = BTreeMap::<&str, Vec<Leaf<'_>>>::new();
    let mut ready = vec![];
    charge(work, page.walls.len())?;
    let clipped: Vec<_> = if stock == Stock::Remainder {
        page.walls
            .iter()
            .map(|wall| {
                let mut matches = wall.source_ids.iter().filter_map(|id| {
                    remainders
                        .contains_key(id.as_str())
                        .then(|| painted.get(id.as_str()).copied())
                        .flatten()
                });
                let band = matches.next()?;
                if matches.next().is_some() {
                    return None;
                }
                clipped_leaf(wall, band)
            })
            .collect()
    } else {
        vec![]
    };
    for (i, (wall, d)) in page.walls.iter().zip(decisions).enumerate() {
        if d.status == WallStatus::Ready {
            ready.push(effective_wall(wall, Some(d)));
        }
        let eligible = if stock == Stock::Remainder {
            clipped[i].as_ref()
        } else {
            Some(wall)
        };
        if d.status == WallStatus::Review
            && d.placement_extent.is_none()
            && let Some(leaf) = eligible.and_then(|w| Leaf::new(i, w, &painted))
            && leaf.paint.mitred == (stock == Stock::Mitred)
        {
            leaves.entry(leaf.paint.layer).or_default().push(leaf);
        }
    }
    let aperture_guards = if stock == Stock::DoubleInset {
        charge(
            work,
            page.walls
                .len()
                .saturating_add(page.openings.len())
                .saturating_add(page.window_panes.len()),
        )?;
        super::exterior_piers::aperture_guard(page)
    } else {
        vec![]
    };
    let certified_interiors = if stock == Stock::DoubleInset {
        layers.values().flatten().copied().collect::<Vec<_>>()
    } else {
        vec![]
    };
    let mut result = vec![];
    for (name, items) in leaves {
        let bands = &layers[name];
        for (i, a) in items.iter().enumerate() {
            charge(work, items.len() - i - 1)?;
            for b in &items[i + 1..] {
                if stock == Stock::Remainder
                    && a.interval(b.paint).is_none_or(|band| {
                        band.along[0].abs() > a.epsilon
                            || (band.along[1] - a.length).abs() > a.epsilon
                    })
                {
                    continue;
                }
                let measure = if stock == Stock::DoubleInset {
                    let [long, short] = if a.length >= b.length { [a, b] } else { [b, a] };
                    prove(
                        long,
                        short,
                        bands,
                        Some(caps.get(name).map_or(&[], Vec::as_slice)),
                        &certified_interiors,
                        work,
                    )?
                } else {
                    prove(a, b, bands, None, &[], work)?
                };
                let Some(mut p) = measure else {
                    continue;
                };
                let extra_apertures = if matches!(stock, Stock::Remainder | Stock::DoubleInset) {
                    page.window_panes.len()
                } else {
                    0
                };
                charge(
                    work,
                    ready
                        .len()
                        .saturating_add(page.openings.len())
                        .saturating_add(extra_apertures),
                )?;
                let crosses = |opening: &crate::model::Opening| {
                    p.indices
                        .iter()
                        .any(|&idx| opening.host_wall_ids.contains(&page.walls[idx].id))
                        || overlap::aperture_crosses(&p.envelope, opening)
                };
                if ready
                    .iter()
                    .any(|w| overlap::parallel_bodies_overlap(&p.envelope, w))
                    || page.openings.iter().any(crosses)
                    || (matches!(stock, Stock::Remainder | Stock::DoubleInset)
                        && page
                            .window_panes
                            .iter()
                            .any(|pane| crosses(&pane.observation)))
                {
                    continue;
                }
                if stock == Stock::DoubleInset {
                    charge(work, aperture_guards.len())?;
                    let outline = super::exterior_piers::corners(
                        p.envelope.start,
                        p.envelope.end,
                        p.envelope.thickness,
                    );
                    if aperture_guards.iter().any(|&cut| {
                        super::exterior_piers::overlaps(outline, cut, roundoff(&p.envelope))
                    }) {
                        continue;
                    }
                }
                if stock == Stock::Remainder {
                    let cutters = p
                        .ids
                        .iter()
                        .filter_map(|id| remainders.get(id.as_str()))
                        .flat_map(|r| r.occluder_source_ids.iter().cloned())
                        .collect::<Vec<_>>();
                    p.ids.extend(cutters);
                    p.ids.sort_unstable();
                    p.ids.dedup();
                }
                result.push(p);
            }
        }
    }
    Some(result)
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    select_shape(page, decisions, Stock::Whole);
}

pub(super) fn select_mitred(page: &PageScan, decisions: &mut [WallDecision]) {
    select_shape(page, decisions, Stock::Mitred);
}

pub(super) fn select_remainders(page: &PageScan, decisions: &mut [WallDecision]) {
    if !page.placement_paint_remainders.is_empty() {
        select_shape(page, decisions, Stock::Remainder);
    }
}

pub(super) fn select_double_inset(page: &PageScan, decisions: &mut [WallDecision]) {
    select_shape(page, decisions, Stock::DoubleInset);
}

fn select_shape(page: &PageScan, decisions: &mut [WallDecision], stock: Stock) {
    let mut work = MAX_WORK;
    let Some(proposals) = proposals(page, decisions, &mut work, stock) else {
        return;
    };
    if charge(&mut work, proposals.len().saturating_pow(2)).is_none() {
        return;
    }
    let mut ambiguous = vec![false; proposals.len()];
    for (i, a) in proposals.iter().enumerate() {
        for (j, b) in proposals.iter().enumerate().skip(i + 1) {
            if a.indices.iter().any(|x| b.indices.contains(x))
                || overlap::parallel_bodies_overlap(&a.envelope, &b.envelope)
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    for (p, ambiguous) in proposals.into_iter().zip(ambiguous) {
        if ambiguous {
            continue;
        }
        let [owner, leaf] = p.indices;
        if stock == Stock::Remainder {
            for (i, (start, end)) in p.indices.into_iter().zip(p.ends) {
                decisions[i].placement_extent = Some(PlacementExtent {
                    start,
                    end,
                    thickness: None,
                    source_ids: p.ids.clone(),
                });
            }
        }
        decisions[owner].status = WallStatus::Ready;
        decisions[owner].reason = if stock == Stock::DoubleInset {
            WallReason::SourceBoundedDoubleInsetCavityWall
        } else {
            WallReason::SourceBoundedCavityWall
        };
        decisions[owner].support_wall_ids = vec![page.walls[leaf].id.clone()];
        decisions[owner].support_source_ids = p.ids.clone();
        decisions[leaf].status = WallStatus::EvidenceOnly;
        decisions[leaf].reason = WallReason::SourceBoundedCavityWallLeaf;
        decisions[leaf].support_wall_ids = vec![page.walls[owner].id.clone()];
        decisions[leaf].support_source_ids = p.ids;
    }
}
