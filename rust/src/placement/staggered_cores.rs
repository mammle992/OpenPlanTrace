//! A visible painted core keeps its own end planes when adjoining finishes are staggered.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall, material, overlap, painted_rectangle,
    roundoff,
};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{HashMap, HashSet};

const MAX_WORK: usize = 20_000_000;

struct Frame {
    origin: Point,
    direction: Point,
    length: f64,
    half: f64,
    eps: f64,
}

impl Frame {
    fn of(wall: &Wall) -> Option<Self> {
        let length = wall.start.distance(wall.end);
        let half = wall.thickness * 0.5;
        (wall.curve.is_none()
            && wall.body == BodyEvidence::FilledBody
            && half.is_finite()
            && half > 0.
            && length.is_finite()
            && length >= 8. * half)
            .then(|| Self {
                origin: wall.start,
                direction: (wall.end - wall.start).unit(),
                length,
                half,
                eps: roundoff(wall).max(wall.thickness * 1e-5),
            })
    }

    fn coordinates(&self, p: Point) -> Point {
        let v = p - self.origin;
        Point::new(v.dot(self.direction), self.direction.cross(v))
    }

    fn rectangle(&self, band: &Band<'_>) -> Option<[f64; 4]> {
        let points = band.corners.map(|p| self.coordinates(p));
        let extent = |get: fn(Point) -> f64| {
            let vals = points.map(get);
            [
                vals.into_iter().fold(f64::INFINITY, f64::min),
                vals.into_iter().fold(f64::NEG_INFINITY, f64::max),
            ]
        };
        let [a, b] = extent(|p| p.x);
        let [lo, hi] = extent(|p| p.y);
        ([a, b].into_iter().all(|x| {
            [lo, hi].into_iter().all(|y| {
                points
                    .iter()
                    .any(|p| p.distance(Point::new(x, y)) <= self.eps)
            })
        }))
        .then_some([a, b, lo, hi])
    }

    fn clipped_interval(&self, a: f64, b: f64) -> Option<[f64; 2]> {
        let lo = a.min(b).max(0.);
        let hi = a.max(b).min(self.length);
        (hi - lo > self.eps).then_some([lo, hi])
    }
}

struct Band<'a> {
    id: &'a str,
    corners: [Point; 4],
}

struct Rail<'a> {
    id: &'a str,
    start: Point,
    end: Point,
}

#[derive(Default)]
struct Layer<'a> {
    bands: Vec<Band<'a>>,
    rails: Vec<Rail<'a>>,
}

fn draw_path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(path, _)| path)
}

fn structural_layer(p: &Primitive) -> Option<&str> {
    let name = p.layer.as_deref()?;
    match p.layer_category_override {
        Some(_) => (p.layer_kind() == LayerKind::Wall).then_some(name),
        None => material::wall_name(name).1.then_some(name),
    }
}

type Interval<'a> = ([f64; 2], &'a str);

fn coverage_on_face<'a>(
    rails: &[Rail<'a>],
    f: &Frame,
    offset: f64,
    core: &str,
) -> Option<Vec<Interval<'a>>> {
    let mut face = rails
        .iter()
        .filter_map(|rail| {
            let a = f.coordinates(rail.start);
            let b = f.coordinates(rail.end);
            if draw_path(rail.id) == draw_path(core)
                || (a.y - offset).abs() > f.eps
                || (b.y - offset).abs() > f.eps
            {
                return None;
            }
            Some((f.clipped_interval(a.x, b.x)?, rail.id))
        })
        .collect::<Vec<_>>();
    (covered_length(&mut face) >= f.length - f.eps).then_some(face)
}

fn covered_length(intervals: &mut [Interval<'_>]) -> f64 {
    intervals.sort_by(|a, b| a.0[0].total_cmp(&b.0[0]));
    let mut covered = 0.;
    let mut end: f64 = 0.;
    for &([lo, hi], _) in intervals.iter() {
        covered += (hi - lo.max(end)).max(0.);
        end = end.max(hi);
    }
    covered
}

struct Proof<'a> {
    core: &'a str,
    finishes: Vec<&'a str>,
    sources: Vec<&'a str>,
}

fn proof<'a>(wall: &Wall, f: &Frame, layer: &Layer<'a>) -> Option<Proof<'a>> {
    let mut core = None;
    let mut finishes = [Vec::new(), Vec::new()];
    let mut finish_widths = [Vec::new(), Vec::new()];
    for band in &layer.bands {
        let Some([lo, hi, inner, outer]) = f.rectangle(band) else {
            continue;
        };
        if lo.abs() <= f.eps
            && (hi - f.length).abs() <= f.eps
            && (inner + f.half).abs() <= f.eps
            && (outer - f.half).abs() <= f.eps
            && wall.source_ids.iter().any(|id| id == band.id)
        {
            if core.replace(band.id).is_some() {
                return None;
            }
            continue;
        }
        let depth = outer - inner;
        if depth <= f.eps || depth >= 2. * f.half {
            continue;
        }
        let side = if (outer + f.half).abs() <= f.eps {
            0
        } else if (inner - f.half).abs() <= f.eps {
            1
        } else {
            continue;
        };
        if let Some(interval) = f.clipped_interval(lo, hi) {
            finishes[side].push((interval, band.id));
            if interval[1] - interval[0] >= 0.75 * f.length {
                finish_widths[side].push((depth, band.id));
            }
        }
    }
    let core = core?;
    for side in &mut finishes {
        side.retain(|(_, id)| draw_path(id) != draw_path(core));
    }
    let has_finish = finishes
        .each_mut()
        .map(|side| covered_length(side) >= 0.75 * f.length);
    if !has_finish[0] && !has_finish[1] {
        return None;
    }
    let mut sources = vec![core];
    for (side, offset) in [-f.half, f.half].into_iter().enumerate() {
        if !has_finish[side] {
            // A directly drawn opposite face can corroborate a complete
            // one-sided stack, also at the facing depth measured on the
            // certified side. Neither line contributes new material.
            let face = coverage_on_face(&layer.rails, f, offset, core).or_else(|| {
                let mut widths = finish_widths[1 - side]
                    .iter()
                    .filter(|(_, id)| draw_path(id) != draw_path(core));
                let &(depth, _) = widths.next()?;
                widths
                    .all(|&(other, _)| (other - depth).abs() <= f.eps)
                    .then_some(())?;
                coverage_on_face(&layer.rails, f, offset + offset.signum() * depth, core)
            })?;
            sources.extend(face.into_iter().map(|(_, id)| id));
        }
    }
    let finishes: Vec<_> = finishes
        .into_iter()
        .zip(has_finish)
        .filter(|(_, qualifies)| *qualifies)
        .flat_map(|(side, _)| side.into_iter().map(|(_, id)| id))
        .collect();
    sources.extend(&finishes);
    sources.sort_unstable();
    sources.dedup();
    Some(Proof {
        core,
        finishes,
        sources,
    })
}

fn course_overlap(a: &Wall, b: &Wall) -> f64 {
    let axis = (a.end - a.start).unit();
    if axis.cross((b.end - b.start).unit()).abs() > 1e-5 {
        return 0.;
    }
    let length = a.start.distance(a.end);
    let [x, y] = [b.start, b.end].map(|p| (p - a.start).dot(axis));
    (x.max(y).min(length) - x.min(y).max(0.)).max(0.)
}

fn same_stack(a: &Proof<'_>, b: &Proof<'_>) -> bool {
    a.finishes.contains(&b.core)
        || b.finishes.contains(&a.core)
        || a.finishes.iter().any(|id| b.finishes.contains(id))
}

fn painted_lining<'a>(
    core: &Frame,
    lining: &Wall,
    layer: &Layer<'a>,
    proof: &Proof<'a>,
) -> Option<(&'a str, &'a str)> {
    let frame = Frame::of(lining)?;
    let eps = core.eps.max(frame.eps);
    if frame.half * 2. > core.half {
        return None;
    }
    let [a, b] = [lining.start, lining.end].map(|p| core.coordinates(p));
    let near = a.y.abs() - frame.half;
    let gap = near - core.half;
    if (a.y - b.y).abs() > eps || gap <= eps || gap > frame.half * 0.2 {
        return None;
    }
    let [lo, hi] = core.clipped_interval(a.x, b.x)?;
    let required = 0.9 * frame.length;
    if hi - lo < required {
        return None;
    }
    let (mut ply, mut seam) = (None, None);
    for band in &layer.bands {
        if lining.source_ids.iter().any(|id| id == band.id) {
            let Some([x, y, inner, outer]) = frame.rectangle(band) else {
                continue;
            };
            if x.abs() <= eps
                && (y - frame.length).abs() <= eps
                && (inner + frame.half).abs() <= eps
                && (outer - frame.half).abs() <= eps
                && ply.replace(band.id).is_some()
            {
                return None;
            }
        } else if proof.finishes.contains(&band.id) {
            let Some([x, y, inner, outer]) = core.rectangle(band) else {
                continue;
            };
            let [inner, outer] = if a.y > 0. {
                [inner, outer]
            } else {
                [-outer, -inner]
            };
            if (inner - core.half).abs() <= eps
                && (outer - near).abs() <= eps
                && hi.min(y) - lo.max(x) >= required
                && seam.replace(band.id).is_some()
            {
                return None;
            }
        }
    }
    let (ply, seam) = (ply?, seam?);
    (draw_path(ply) != draw_path(proof.core) && draw_path(ply) != draw_path(seam))
        .then_some((ply, seam))
}

pub(crate) fn certified_rectangle(shape: &Shape) -> Option<[Point; 4]> {
    if let Some(corners) = painted_rectangle(shape) {
        return Some(corners);
    }
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = shape
    else {
        return None;
    };
    if !(5..=129).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
        return None;
    }
    let mut vertices = if points.first() == points.last() {
        points[..points.len() - 1].to_vec()
    } else {
        points.clone()
    };
    while vertices.len() > 4 {
        let n = vertices.len();
        let redundant = (0..n).find(|&i| {
            let incoming = vertices[i] - vertices[(i + n - 1) % n];
            let outgoing = vertices[(i + 1) % n] - vertices[i];
            incoming.cross(outgoing) == 0. && incoming.dot(outgoing) > 0.
        })?;
        vertices.remove(redundant);
    }
    let corners: [Point; 4] = vertices.try_into().ok()?;
    super::insulation::rectangle(&corners).then_some(corners)
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    select_certified(page, decisions, false);
    // Segmented outlines can attest additional cores. Preserve the already
    // supported walls and treat shared finishes against them as ambiguous.
    select_certified(page, decisions, true);
}

fn select_certified(page: &PageScan, decisions: &mut [WallDecision], segmented: bool) {
    let authorized: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    let polygons: HashSet<_> = if segmented {
        page.placement_polygon_paint_ids
            .iter()
            .flatten()
            .map(String::as_str)
            .collect()
    } else {
        HashSet::new()
    };
    if (!segmented && authorized.is_empty()) || (segmented && polygons.is_empty()) {
        return;
    }
    let mut layers = HashMap::<&str, Layer<'_>>::new();
    let mut paint_to_layer = HashMap::new();
    for p in &page.primitives {
        let Some(name) = structural_layer(p) else {
            continue;
        };
        let rectangle = authorized.contains(p.id.as_str());
        let polygon = polygons.contains(p.id.as_str());
        let corners = if rectangle {
            painted_rectangle(&p.shape)
        } else if polygon {
            certified_rectangle(&p.shape)
        } else {
            None
        };
        if let Some(corners) = corners {
            layers
                .entry(name)
                .or_default()
                .bands
                .push(Band { id: &p.id, corners });
            if !segmented || !rectangle {
                paint_to_layer.insert(p.id.as_str(), name);
            }
        }
    }
    if layers.is_empty() {
        return;
    }
    for p in &page.primitives {
        if let Some(layer) = structural_layer(p).and_then(|name| layers.get_mut(name))
            && let Shape::Line { start, end } = p.shape
        {
            layer.rails.push(Rail {
                id: &p.id,
                start,
                end,
            });
        }
    }
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .enumerate()
        .filter(|(_, (_, d))| d.status == WallStatus::Ready)
        .map(|(index, (wall, d))| (index, effective_wall(wall, Some(d)), d))
        .collect();
    let mut work = MAX_WORK;
    let mut proposals = Vec::new();
    'candidate: for (i, (wall, d)) in page.walls.iter().zip(decisions.iter()).enumerate() {
        if d.status != WallStatus::Review || d.placement_extent.is_some() {
            continue;
        }
        let Some(frame) = Frame::of(wall) else {
            continue;
        };
        let mut sources = wall
            .source_ids
            .iter()
            .filter_map(|id| paint_to_layer.get(id.as_str()).copied());
        let Some(layer) = sources.next() else {
            continue;
        };
        if sources.next().is_some() {
            continue;
        }
        let layer = &layers[layer];
        let Some(remaining) = work
            .checked_sub(layer.bands.len() + layer.rails.len() + page.openings.len() + ready.len())
        else {
            return;
        };
        work = remaining;
        if page
            .openings
            .iter()
            .any(|o| o.host_wall_ids.contains(&wall.id) || overlap::aperture_crosses(wall, o))
            || ready
                .iter()
                .any(|(_, other, _)| overlap::parallel_bodies_overlap(wall, other))
        {
            continue;
        }
        let Some(proof) = proof(wall, &frame, layer) else {
            continue;
        };
        let mut lining = None;
        for (j, other, decision) in &ready {
            if let Some((ply, seam)) = painted_lining(&frame, other, layer, &proof) {
                if decision.placement_extent.is_some()
                    || !decision.opening_ids.is_empty()
                    || page.openings.iter().any(|o| {
                        o.host_wall_ids.contains(&other.id) || overlap::aperture_crosses(other, o)
                    })
                    || lining.replace((*j, ply, seam)).is_some()
                {
                    continue 'candidate;
                }
            } else if segmented
                && decision.reason == WallReason::SourceBoundedStaggeredPaintedCore
                && course_overlap(wall, other) > frame.eps
                && (decision
                    .support_source_ids
                    .iter()
                    .any(|id| id == proof.core || proof.finishes.contains(&id.as_str()))
                    || other
                        .source_ids
                        .iter()
                        .any(|id| proof.finishes.contains(&id.as_str())))
            {
                continue 'candidate;
            }
        }
        proposals.push((i, proof, lining));
    }
    if proposals.len().saturating_pow(2).saturating_mul(2) > work {
        return;
    }
    // A thin painted ply in an already proven stack is not a second wall.
    // Similar competing cores still need additional evidence.
    let is_lining: Vec<_> = proposals
        .iter()
        .map(|(i, proof, _)| {
            let wall = &page.walls[*i];
            proposals.iter().any(|(j, other, _)| {
                let body = &page.walls[*j];
                i != j
                    && body.thickness >= 2. * wall.thickness
                    && same_stack(proof, other)
                    && course_overlap(wall, body) >= 0.75 * wall.start.distance(wall.end)
            })
        })
        .collect();
    for ((i, proof, absorbed), lining) in proposals.iter().zip(&is_lining) {
        if *lining {
            continue;
        }
        let wall = &page.walls[*i];
        if proposals
            .iter()
            .zip(&is_lining)
            .any(|((j, witness, other_absorbed), other_lining)| {
                if i == j || *other_lining {
                    return false;
                }
                let other = &page.walls[*j];
                (absorbed.is_some()
                    && absorbed.map(|(idx, ..)| idx) == other_absorbed.map(|(idx, ..)| idx))
                    || overlap::parallel_bodies_overlap(wall, other)
                    || (course_overlap(wall, other) > roundoff(wall).max(roundoff(other))
                        && same_stack(proof, witness))
            })
        {
            continue;
        }
        let d = &mut decisions[*i];
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedStaggeredPaintedCore;
        d.support_source_ids = proof.sources.iter().map(|&id| id.to_owned()).collect();
        if let Some((j, ply, seam)) = absorbed {
            let d = &mut decisions[*j];
            d.status = WallStatus::EvidenceOnly;
            d.reason = WallReason::SourceBoundedWallLining;
            d.support_wall_ids = vec![wall.id.clone()];
            d.support_source_ids
                .extend([*ply, *seam, proof.core].map(str::to_owned));
            d.support_source_ids.sort_unstable();
            d.support_source_ids.dedup();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_certified_polygon_may_have_a_redundant_rectangle_vertex() {
        let outline = |mid| Shape::Polyline {
            points: vec![
                Point::new(0., 0.),
                Point::new(6., 0.),
                mid,
                Point::new(6., 2.),
                Point::new(0., 2.),
                Point::new(0., 0.),
            ],
            closed: true,
            filled: true,
        };
        assert!(certified_rectangle(&outline(Point::new(6., 1.))).is_some());
        assert!(certified_rectangle(&outline(Point::new(7., 1.))).is_none());
    }
}
