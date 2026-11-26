//! Local material-contact proof from exact source polygons and later paint.
//! The caller supplies authenticated Ready stock and structural paint records.
//! A result proves a graph segment only and carries no added material.
use crate::model::Point;
use std::collections::{HashSet, VecDeque};

const MAX_LOCAL_CELLS: usize = 64;
const MAX_VISIBLE_SPANS: usize = 128;

#[derive(Clone, Copy)]
pub(crate) struct Obstruction<'a> {
    pub id: &'a str,
    pub order: u64,
    pub points: &'a [Point],
}

/// Each occluder is a later, resolved, opaque paint operation. If an
/// unresolved operation might cross this cell, the importer withholds it.
pub(crate) struct Cell<'a> {
    pub id: &'a str,
    pub layer: &'a str,
    pub order: u64,
    pub points: &'a [Point],
    pub obscurers: &'a [Obstruction<'a>],
}

#[derive(Clone, Copy)]
pub(crate) struct Arm<'a> {
    pub at: Point,
    pub outside: Point,
    pub width: f64,
    pub source_id: &'a str,
    pub layer: &'a str,
}

pub(crate) struct Contact<'a> {
    pub source_ids: Vec<&'a str>,
}

#[derive(Clone, Copy, Debug)]
struct Range {
    lo: f64,
    hi: f64,
}

impl Range {
    fn length(self) -> f64 {
        self.hi - self.lo
    }
}

#[derive(Clone, Copy)]
struct Ring<'a> {
    points: &'a [Point],
    sign: f64,
}

impl<'a> Ring<'a> {
    fn new(points: &'a [Point], eps: f64) -> Option<Self> {
        let points = if points.first() == points.last() {
            points.get(..points.len().checked_sub(1)?)?
        } else {
            points
        };
        if !(3..=8).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
            return None;
        }
        let n = points.len();
        let turn = |i: usize| {
            let a = points[i];
            let b = points[(i + 1) % n];
            let c = points[(i + 2) % n];
            let edge = b - a;
            (edge.norm() > eps).then(|| edge.unit().cross(c - b))
        };
        let mut sign = 0.;
        for i in 0..n {
            let bend = turn(i)?;
            if bend.abs() <= eps {
                continue;
            }
            if sign == 0. {
                sign = bend.signum();
            } else if sign != bend.signum() {
                return None;
            }
        }
        if sign == 0. {
            return None;
        }
        // A consistent local turn also occurs on a self-intersecting star.
        // Convex paint puts every point in every inward edge half-plane.
        let ring = Self { points, sign };
        ring.edges()
            .all(|(a, b)| {
                let edge = (b - a).unit();
                points.iter().all(|&p| sign * edge.cross(p - a) >= -eps)
            })
            .then_some(ring)
    }

    fn edges(self) -> impl Iterator<Item = (Point, Point)> + 'a {
        self.points
            .iter()
            .copied()
            .zip(self.points.iter().copied().cycle().skip(1))
    }

    fn slice(self, a: Point, b: Point, eps: f64) -> Option<Range> {
        let mut result = Range { lo: 0., hi: 1. };
        for (p, q) in self.edges() {
            let normal = (q - p).unit();
            let signed = |v: Point| {
                let d = self.sign * normal.cross(v - p);
                if d.abs() <= eps { 0. } else { d }
            };
            let [x, y] = [signed(a), signed(b)];
            if x < 0. && y < 0. {
                return None;
            }
            if x < 0. {
                result.lo = result.lo.max(x / (x - y));
            }
            if y < 0. {
                result.hi = result.hi.min(x / (x - y));
            }
            if result.lo > result.hi {
                return None;
            }
        }
        Some(result)
    }

    fn shares_side_at(
        self,
        other: Self,
        at: Point,
        width: f64,
        eps: f64,
        mut visible: impl FnMut(Point, Point, f64) -> Option<bool>,
    ) -> Option<bool> {
        for (a, b) in self.edges() {
            let length = a.distance(b);
            let axis = (b - a).unit();
            let p = at - a;
            if axis.cross(p).abs() > eps {
                continue;
            }
            let t = p.dot(axis);
            for (c, d) in other.edges() {
                let [u, v] = [c - a, d - a];
                if axis.cross(u).abs().max(axis.cross(v).abs()) > eps {
                    continue;
                }
                // A common positive-length edge is required. Diagonal tips
                // touching at a single point do not support a wall junction.
                let [x, y] = [u.dot(axis), v.dot(axis)];
                if self.sign * other.sign * (y - x) >= 0. {
                    continue;
                }
                let lo = x.min(y).max(0.);
                let hi = x.max(y).min(length);
                if hi - lo >= width
                    && t >= lo - eps
                    && t <= hi + eps
                    && visible(a + axis * lo, a + axis * hi, (t - lo) / (hi - lo))?
                {
                    return Some(true);
                }
            }
        }
        Some(false)
    }
}

fn charge(work: &mut usize, amount: usize) -> Option<()> {
    match work.checked_sub(amount) {
        Some(remaining) => {
            *work = remaining;
            Some(())
        }
        None => {
            *work = 0;
            None
        }
    }
}

pub(super) fn stock_contains_terminal(
    arm: Arm<'_>,
    points: &[Point],
    length: f64,
    eps: f64,
) -> bool {
    let inward = arm.at - arm.outside.unit() * (arm.width * 0.25).min(length * 0.5);
    Ring::new(points, eps)
        .and_then(|ring| ring.slice(inward, arm.at, eps))
        .is_some_and(|part| part.lo == 0. && part.hi == 1.)
}

pub(super) fn convex_span(points: &[Point], a: Point, b: Point, eps: f64) -> Option<[f64; 2]> {
    let span = Ring::new(points, eps)?.slice(a, b, eps)?;
    Some([span.lo, span.hi])
}

fn visible_ranges(
    cell: &Cell<'_>,
    ring: Ring<'_>,
    a: Point,
    b: Point,
    eps: f64,
    tol: f64,
    work: &mut usize,
) -> Option<Vec<Range>> {
    let Some(full) = ring.slice(a, b, eps) else {
        return Some(vec![]);
    };
    let mut removals = Vec::new();
    for obs in cell.obscurers {
        if obs.id == cell.id || obs.order <= cell.order {
            return None;
        }
        charge(work, obs.points.len().saturating_pow(2))?;
        let mask = Ring::new(obs.points, eps)?;
        if let Some(r) = mask.slice(a, b, eps)
            && r.hi > full.lo
            && r.lo < full.hi
        {
            removals.push(r);
        }
    }
    charge(work, removals.len().saturating_pow(2))?;
    removals.sort_unstable_by(|a, b| a.lo.total_cmp(&b.lo).then(a.hi.total_cmp(&b.hi)));
    let mut kept = Vec::new();
    let mut at = full.lo;
    for cut in removals {
        if cut.lo > at + tol {
            kept.push(Range {
                lo: at,
                hi: cut.lo.min(full.hi),
            });
        }
        at = at.max(cut.hi);
        if at >= full.hi {
            return Some(kept);
        }
    }
    if at < full.hi || (at == full.lo && full.length() <= tol) {
        kept.push(Range {
            lo: at,
            hi: full.hi,
        });
    }
    Some(kept)
}

#[derive(Clone, Copy)]
struct Span<'a> {
    id: &'a str,
    cell: usize,
    ring: Ring<'a>,
    extent: Range,
}

/// Returns a straight graph contact only when the finite segment is wholly
/// visible structural paint, physically chained through shared side edges.
pub(crate) fn prove_contact<'a>(
    [first, last]: [Arm<'a>; 2],
    cells: &[Cell<'a>],
    apertures: &[&[Point]],
    work: &mut usize,
) -> Option<Contact<'a>> {
    let [a, b] = [first.at, last.at];
    let width = first.width.min(last.width);
    let length = a.distance(b);
    let eps = 256. * f64::EPSILON * a.norm().max(b.norm()).max(length).max(1.);
    let axis = (b - a).unit();
    let [u, v] = [first.outside.unit(), last.outside.unit()];
    if !a.finite()
        || !b.finite()
        || !first.width.is_finite()
        || !last.width.is_finite()
        || width <= eps
        || length <= eps
        || length > 0.875 * (first.width + last.width)
        || first.layer != last.layer
        || first.source_id == last.source_id
        || axis.dot(u) <= 0.
        || axis.dot(v) >= 0.
    {
        return None;
    }
    let collinear = u.cross(v).abs() <= 1e-4;
    if collinear && axis.cross(u).abs() > 0.12 {
        return None;
    }
    let tol = eps / length;
    for points in apertures {
        charge(work, points.len().saturating_pow(2))?;
        if Ring::new(points, eps)?
            .slice(a, b, eps)
            .is_some_and(|r| r.length() > tol)
        {
            return None;
        }
    }

    let mut spans = Vec::new();
    let mut sources = HashSet::new();
    let mut count = 0usize;
    charge(work, cells.len())?;
    for (index, cell) in cells.iter().enumerate() {
        if !collinear && cell.layer != first.layer {
            continue;
        }
        charge(work, cell.points.len().saturating_pow(2))?;
        let Some(ring) = Ring::new(cell.points, eps) else {
            continue;
        };
        if ring.slice(a, b, eps).is_none() {
            continue;
        }
        count += 1;
        if count > MAX_LOCAL_CELLS || !sources.insert(cell.id) {
            return None;
        }
        let ranges = visible_ranges(cell, ring, a, b, eps, tol, work)?;
        for extent in ranges {
            if extent.length() > tol || [first.source_id, last.source_id].contains(&cell.id) {
                spans.push(Span {
                    id: cell.id,
                    cell: index,
                    ring,
                    extent,
                });
                if spans.len() > MAX_VISIBLE_SPANS {
                    return None;
                }
            }
        }
    }
    charge(work, spans.len().saturating_pow(2))?;
    spans.sort_unstable_by(|p, q| {
        p.extent
            .lo
            .total_cmp(&q.extent.lo)
            .then(q.extent.hi.total_cmp(&p.extent.hi))
            .then(p.id.cmp(q.id))
    });
    let mut seen = vec![false; spans.len()];
    let mut came_from = vec![None::<usize>; spans.len()];
    let mut next = VecDeque::new();
    for (i, span) in spans.iter().enumerate() {
        if span.id == first.source_id && span.extent.lo <= tol {
            next.push_back(i);
            seen[i] = true;
        }
    }
    while let Some(i) = next.pop_front() {
        let current = spans[i];
        if current.id == last.source_id && current.extent.hi >= 1. - tol {
            let mut path = vec![current.id];
            let mut previous = came_from[i];
            while let Some(j) = previous {
                path.push(spans[j].id);
                previous = came_from[j];
            }
            path.reverse();
            return Some(Contact { source_ids: path });
        }
        for (j, span) in spans.iter().enumerate() {
            let end_anchor = span.id == last.source_id && span.extent.hi >= 1. - tol;
            if seen[j]
                || current.id == span.id
                || span.extent.lo > current.extent.hi + tol
                || span.extent.lo + tol < current.extent.lo
                || (!end_anchor && span.extent.hi <= current.extent.hi + tol)
            {
                continue;
            }
            charge(
                work,
                current
                    .ring
                    .points
                    .len()
                    .saturating_mul(span.ring.points.len()),
            )?;
            let t = (current.extent.hi + span.extent.lo) * 0.5;
            let side_width = width * 0.25;
            if current.ring.shares_side_at(
                span.ring,
                a + (b - a) * t,
                side_width,
                eps * 4.,
                |left, right, handoff| {
                    let distance = left.distance(right);
                    let tolerance = eps / distance;
                    let first = visible_ranges(
                        &cells[current.cell],
                        current.ring,
                        left,
                        right,
                        eps,
                        tolerance,
                        work,
                    )?;
                    let second = visible_ranges(
                        &cells[span.cell],
                        span.ring,
                        left,
                        right,
                        eps,
                        tolerance,
                        work,
                    )?;
                    charge(work, first.len().saturating_mul(second.len()))?;
                    Some(first.iter().any(|a| {
                        second.iter().any(|b| {
                            let lo = a.lo.max(b.lo);
                            let hi = a.hi.min(b.hi);
                            (hi - lo) * distance >= side_width
                                && handoff >= lo - tolerance
                                && handoff <= hi + tolerance
                        })
                    }))
                },
            )? {
                seen[j] = true;
                came_from[j] = Some(i);
                next.push_back(j);
            }
        }
    }
    None
}
