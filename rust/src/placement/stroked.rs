//! Finite wall outlines on drawings without authored structural layers.
//! Ink contrast supplements four complete sides; it cannot certify a face by itself.
use super::{WallDecision, WallReason, WallStatus, overlap, roundoff};
use crate::model::{
    BodyEvidence, LayerKind, Opening, Options, Page, PageScan, Point, Primitive, Wall,
    WithheldWall, segments,
};
use std::collections::{HashMap, HashSet, VecDeque};

struct Frame {
    ids: Vec<String>,
    caps: [Option<String>; 2],
    ink: f64,
    termini: [bool; 2],
    exact_bounds: bool,
}

struct Stroke<'a> {
    primitive: &'a Primitive,
    start: Point,
    end: Point,
    width: f64,
}

#[derive(Clone)]
pub(crate) struct CornerJoint {
    pub wall_ids: [String; 2],
    pub source_ids: Vec<String>,
    pub polygon: [Point; 4],
    pub adds_material: bool,
    pub contact_points: Option<[Point; 2]>,
    pub(crate) ends: [usize; 2],
}

const RECOVERED: &str = "ink-pair:";

pub(crate) fn is_recovered(w: &Wall) -> bool {
    w.id.starts_with(RECOVERED)
}

fn same_span(a: &Wall, b: &Wall) -> bool {
    let eps = roundoff(a).max(roundoff(b)).max(a.thickness * 1e-5);
    (a.thickness - b.thickness).abs() <= eps
        && b.curve.is_none()
        && ((a.start.distance(b.start) <= eps && a.end.distance(b.end) <= eps)
            || (a.start.distance(b.end) <= eps && a.end.distance(b.start) <= eps))
}

/// Other pen styles can make the ordinary detector pair a structural face with
/// a nearby detail line. Re-observe only the independently visible heavy ink.
pub(crate) fn recover(
    page: &Page,
    walls: &mut Vec<Wall>,
    openings: &[Opening],
    withheld: &[WithheldWall],
    opts: &Options,
) -> Result<(), String> {
    let ink = strokes(&page.primitives);
    if ink.is_empty() {
        return Ok(());
    }
    let ids: HashSet<_> = ink.iter().map(|s| s.primitive.id.as_str()).collect();
    let mut structural = page.clone();
    structural
        .primitives
        .retain(|p| ids.contains(p.id.as_str()));
    let bounded = Options {
        min_wall_length: opts.min_wall_fragment_length,
        ..*opts
    };
    let candidates = crate::walls::detect(&structural, &bounded)?;
    let mut recovered = vec![];
    for (i, mut w) in candidates.into_iter().enumerate() {
        if w.body == BodyEvidence::ParallelFaces
            && w.curve.is_none()
            && faces(&w, &ink, true).is_some_and(|f| f.exact_bounds)
            && !blocked(&w, withheld)
            && !repeated_details(&w, &page.primitives)
            && !walls.iter().any(|other| same_span(&w, other))
        {
            w.id = format!("{RECOVERED}{}:{}", page.number, i + 1);
            recovered.push(w);
        }
    }
    let (anchored, corners) = anchored_pairs(&page.primitives, walls, &recovered, &ink, openings);
    walls.extend(recovered.into_iter().filter(|w| {
        anchored.contains(&w.id)
            && (w.start.distance(w.end) >= w.thickness || corners.contains(&w.id))
            && (!outline_rejected(w, withheld) || corners.contains(&w.id))
    }));
    Ok(())
}

fn blocked(w: &Wall, withheld: &[WithheldWall]) -> bool {
    let u = (w.end - w.start).unit();
    let n = Point::new(-u.y, u.x) * (w.thickness * 0.5);
    let body = [w.start + n, w.end + n, w.end - n, w.start - n];
    let eps = roundoff(w).max(w.thickness * 1e-5);
    withheld.iter().any(|rejected| {
        if rejected.reason == crate::model::WallSelectionReason::OutlineEdgePair
            && rejected.furniture_outline.is_none()
        {
            return false;
        }
        if overlap::parallel_bodies_overlap(w, &rejected.wall) {
            return true;
        }
        let Some(outline) = rejected.furniture_outline else {
            return false;
        };
        let normal = |a: Point, b: Point| {
            let v = b - a;
            Point::new(-v.y, v.x).unit()
        };
        let axes = [
            u,
            n.unit(),
            normal(outline[0], outline[1]),
            normal(outline[1], outline[2]),
            normal(outline[2], outline[3]),
            normal(outline[3], outline[0]),
        ];
        axes.into_iter().all(|axis| {
            let extent = |points: &[Point]| {
                points
                    .iter()
                    .map(|p| (*p - w.start).dot(axis))
                    .fold((f64::INFINITY, f64::NEG_INFINITY), |(lo, hi), x| {
                        (lo.min(x), hi.max(x))
                    })
            };
            let (a, b) = extent(&body);
            let (c, d) = extent(&outline);
            b.min(d) - a.max(c) > eps
        })
    })
}

fn outline_rejected(w: &Wall, withheld: &[WithheldWall]) -> bool {
    withheld.iter().any(|r| {
        r.reason == crate::model::WallSelectionReason::OutlineEdgePair
            && r.furniture_outline.is_none()
            && overlap::parallel_bodies_overlap(w, &r.wall)
    })
}

fn anchored_pairs(
    primitives: &[Primitive],
    walls: &[Wall],
    recovered: &[Wall],
    ink: &[Stroke<'_>],
    openings: &[Opening],
) -> (HashSet<String>, HashSet<String>) {
    let mut combined = walls.to_vec();
    combined.extend(recovered.iter().cloned());
    let frames: Vec<_> = combined
        .iter()
        .map(|w| credible_frame(w, ink, primitives))
        .collect();
    let mut adjacency: HashMap<_, Vec<_>> = HashMap::new();
    let mut corners = HashSet::new();
    for j in joints(&combined, ink, &frames, openings) {
        let [a, b] = j.wall_ids;
        corners.extend([a.clone(), b.clone()]);
        adjacency.entry(a.clone()).or_default().push(b.clone());
        adjacency.entry(b).or_default().push(a);
    }
    let anchored_sources = connected_ink(primitives, walls, ink);
    let mut anchored: HashSet<_> = walls.iter().map(|w| w.id.clone()).collect();
    anchored.extend(
        recovered
            .iter()
            .filter(|w| {
                faces(w, &ink, true)
                    .is_some_and(|f| f.ids.iter().all(|id| anchored_sources.contains(id)))
            })
            .map(|w| w.id.clone()),
    );
    let mut queue: VecDeque<_> = anchored.iter().cloned().collect();
    while let Some(id) = queue.pop_front() {
        for other in adjacency.get(&id).into_iter().flatten() {
            if anchored.insert(other.clone()) {
                queue.push_back(other.clone());
            }
        }
    }
    (anchored, corners)
}

pub(crate) fn validate_recovered(page: &PageScan) -> Result<(), String> {
    if !page.walls.iter().any(is_recovered) {
        return Ok(());
    }
    let ink = strokes(&page.primitives);
    let original: Vec<_> = page
        .walls
        .iter()
        .filter(|w| !is_recovered(w))
        .cloned()
        .collect();
    let recovered: Vec<_> = page
        .walls
        .iter()
        .filter(|w| is_recovered(w))
        .cloned()
        .collect();
    let (anchored, corners) = anchored_pairs(
        &page.primitives,
        &original,
        &recovered,
        &ink,
        &page.openings,
    );
    for w in &recovered {
        if w.body != BodyEvidence::ParallelFaces
            || w.curve.is_some()
            || !faces(w, &ink, true).is_some_and(|f| f.exact_bounds)
            || !anchored.contains(&w.id)
            || (w.start.distance(w.end) < w.thickness && !corners.contains(&w.id))
            || blocked(w, page.withheld_walls.as_deref().unwrap_or_default())
            || (outline_rejected(w, page.withheld_walls.as_deref().unwrap_or_default())
                && !corners.contains(&w.id))
            || repeated_details(w, &page.primitives)
        {
            return Err("recovered ink pair lacks its visible source faces".into());
        }
    }
    Ok(())
}

/// Window frames can connect two solid piers through their original ink paths.
/// Endpoint contact uses ink-scale roundoff and never jumps an empty doorway.
fn connected_ink(
    primitives: &[Primitive],
    walls: &[Wall],
    heavy: &[Stroke<'_>],
) -> HashSet<String> {
    let anchor_walls: Vec<_> = walls
        .iter()
        .filter_map(|w| proof(w, heavy).map(|ids| (w, ids)))
        .filter(|(w, _)| !repeated_details(w, primitives))
        .collect();
    let anchors: HashSet<_> = anchor_walls
        .iter()
        .flat_map(|(_, ids)| ids.iter().cloned())
        .collect();
    if anchors.is_empty() {
        return HashSet::new();
    }
    let eps = heavy.iter().map(|s| s.width).fold(f64::INFINITY, f64::min) * 0.02;
    let body = anchor_walls
        .iter()
        .map(|(w, _)| w.thickness)
        .fold(0., f64::max);
    let cell_size = body * 4.;
    let pad = body * 12.;
    let xs = anchor_walls.iter().flat_map(|(w, _)| [w.start.x, w.end.x]);
    let ys = anchor_walls.iter().flat_map(|(w, _)| [w.start.y, w.end.y]);
    let low = Point::new(
        xs.clone().fold(f64::INFINITY, f64::min) - pad,
        ys.clone().fold(f64::INFINITY, f64::min) - pad,
    );
    let high = Point::new(
        xs.fold(f64::NEG_INFINITY, f64::max) + pad,
        ys.fold(f64::NEG_INFINITY, f64::max) + pad,
    );
    let near = |p: Point| p.x >= low.x && p.y >= low.y && p.x <= high.x && p.y <= high.y;
    let cell = |p: Point| {
        (
            (p.x / cell_size).floor() as i64,
            (p.y / cell_size).floor() as i64,
        )
    };
    let mut lines = vec![];
    for (i, p) in primitives.iter().enumerate() {
        if p.stroke_width.is_none()
            || !matches!(
                p.layer_kind(),
                LayerKind::Unknown | LayerKind::Wall | LayerKind::Window | LayerKind::Door
            )
            || !matches!(
                p.shape,
                crate::model::Shape::Line { .. }
                    | crate::model::Shape::Polyline { filled: false, .. }
            )
        {
            continue;
        }
        for (a, b) in segments(&p.shape) {
            if near(a) && near(b) {
                if lines.len() == 12000 {
                    return HashSet::new();
                }
                lines.push((a, b, i));
            }
        }
    }
    let mut buckets: HashMap<(i64, i64), Vec<usize>> = HashMap::new();
    let mut work = 4_000_000usize;
    for (index, &(a, b, _)) in lines.iter().enumerate() {
        let lo = cell(Point::new(a.x.min(b.x) - eps, a.y.min(b.y) - eps));
        let hi = cell(Point::new(a.x.max(b.x) + eps, a.y.max(b.y) + eps));
        for x in lo.0..=hi.0 {
            for y in lo.1..=hi.1 {
                if work == 0 {
                    return HashSet::new();
                }
                work -= 1;
                buckets.entry((x, y)).or_default().push(index);
            }
        }
    }
    let mut adjacent: Vec<Vec<usize>> = vec![vec![]; primitives.len()];
    for &(a, b, i) in &lines {
        for at in [a, b] {
            for &index in buckets.get(&cell(at)).into_iter().flatten() {
                if work == 0 {
                    return HashSet::new();
                }
                work -= 1;
                let (c, d, j) = lines[index];
                if i != j && crate::model::on_segment(at, c, d).0.distance(at) <= eps {
                    adjacent[i].push(j);
                    adjacent[j].push(i);
                }
            }
        }
    }
    let mut reached: HashSet<_> = primitives
        .iter()
        .enumerate()
        .filter(|(_, p)| anchors.contains(&p.id))
        .map(|(i, _)| i)
        .collect();
    let mut queue: VecDeque<_> = reached.iter().copied().collect();
    while let Some(i) = queue.pop_front() {
        for &j in &adjacent[i] {
            if reached.insert(j) {
                queue.push_back(j);
            }
        }
    }
    reached
        .into_iter()
        .map(|i| primitives[i].id.clone())
        .collect()
}

/// Stair treads and shelving can attach a regular ladder of long thin strokes
/// to one face of a rail. Such an outline needs review even with contrasting ink.
fn repeated_details(w: &Wall, primitives: &[Primitive]) -> bool {
    let axis = (w.end - w.start).unit();
    let length = w.start.distance(w.end);
    let half = w.thickness * 0.5;
    let eps = roundoff(w).max(w.thickness * 1e-5);
    let mut contacts = [Vec::new(), Vec::new()];
    for p in primitives {
        if !matches!(
            p.shape,
            crate::model::Shape::Line { .. } | crate::model::Shape::Polyline { filled: false, .. }
        ) {
            continue;
        }
        for (a, b) in segments(&p.shape) {
            let a = a - w.start;
            let b = b - w.start;
            let (u, v) = (a.dot(axis), b.dot(axis));
            if (u - v).abs() > eps || u <= eps || u >= length - eps {
                continue;
            }
            let (x, y) = (axis.cross(a), axis.cross(b));
            for (at, far) in [(x, y), (y, x)] {
                if (at.abs() - half).abs() <= eps && far * at.signum() > half + 3. * w.thickness {
                    contacts[usize::from(at > 0.)].push(u);
                }
            }
        }
    }
    contacts.iter_mut().any(|points| {
        points.sort_by(f64::total_cmp);
        points.dedup_by(|a, b| (*a - *b).abs() <= eps);
        points.windows(4).any(|row| {
            let pitch = (row[3] - row[0]) / 3.;
            pitch > w.thickness
                && row
                    .windows(2)
                    .all(|p| ((p[1] - p[0]) - pitch).abs() <= pitch * 0.05)
        })
    })
}

fn credible_frame(w: &Wall, strokes: &[Stroke<'_>], primitives: &[Primitive]) -> Option<Frame> {
    let frame = faces(w, strokes, true)?;
    if repeated_details(w, primitives) {
        return None;
    }
    Some(frame)
}

fn faces(w: &Wall, strokes: &[Stroke<'_>], bends: bool) -> Option<Frame> {
    let axis = (w.end - w.start).unit();
    let length = w.start.distance(w.end);
    let half = w.thickness * 0.5;
    let eps = roundoff(w).max(w.thickness * 1e-5);
    let mut sides = [Vec::new(), Vec::new()];
    let mut caps = [Vec::new(), Vec::new()];
    let mut ink = None::<f64>;
    let layer = &strokes
        .iter()
        .find(|s| w.source_ids.contains(&s.primitive.id))?
        .primitive
        .layer;
    for s in strokes.iter().filter(|s| &s.primitive.layer == layer) {
        let a = s.start - w.start;
        let b = s.end - w.start;
        let (u, v) = (a.dot(axis), b.dot(axis));
        let (x, y) = (axis.cross(a), axis.cross(b));
        if (x - y).abs() <= eps && (x.abs() - half).abs() <= eps {
            let (lo, hi) = (u.min(v), u.max(v));
            if hi - lo <= eps
                || lo < -w.thickness * (if bends { 2. } else { 1. }) - eps
                || hi > length + w.thickness * (if bends { 2. } else { 1. }) + eps
            {
                continue;
            }
            if !w.source_ids.contains(&s.primitive.id) {
                continue;
            }
            ink = Some(ink.unwrap_or(s.width).max(s.width));
            sides[usize::from(x > 0.)].push((lo, hi, s));
        }
        if (u - v).abs() <= eps && (x.min(y) + half).abs() <= eps && (x.max(y) - half).abs() <= eps
        {
            for (end, at) in [0., length].into_iter().enumerate() {
                if (u - at).abs() <= eps {
                    caps[end].push(s);
                }
            }
        }
    }
    let ink = ink?;
    // Thin pen rectangles are also used for windows and elevation frames.
    if w.thickness < 8. * ink || length < if bends { 2. * ink } else { 2. * w.thickness } {
        return None;
    }
    let termini = [0., length].map(|at| {
        sides.iter().all(|spans| {
            spans.iter().any(|&(lo, hi, s)| {
                (s.width - ink).abs() <= ink * 1e-5
                    && ((lo - at).abs() <= eps || (hi - at).abs() <= eps)
            })
        })
    });
    let bounds = sides.each_ref().map(|spans| {
        let matching: Vec<_> = spans
            .iter()
            .filter(|(_, _, s)| (s.width - ink).abs() <= ink * 1e-5)
            .collect();
        (
            matching.iter().map(|s| s.0).fold(f64::INFINITY, f64::min),
            matching
                .iter()
                .map(|s| s.1)
                .fold(f64::NEG_INFINITY, f64::max),
        )
    });
    let exact_bounds = bounds[0].0.max(bounds[1].0).abs() <= eps
        && (bounds[0].1.min(bounds[1].1) - length).abs() <= eps;
    let mut ids = Vec::new();
    for spans in &mut sides {
        spans.sort_by(|a, b| a.0.total_cmp(&b.0));
        let mut end: f64 = 0.;
        for &(lo, hi, s) in spans.iter() {
            if (s.width - ink).abs() > ink * 1e-5 || hi <= end + eps {
                continue;
            }
            if lo > end + eps {
                return None;
            }
            end = hi;
            ids.push(s.primitive.id.clone());
        }
        if end < length - eps {
            return None;
        }
    }
    ids.sort();
    ids.dedup();
    Some(Frame {
        ids,
        caps: caps.map(|cap| {
            cap.iter()
                .find(|s| (s.width - ink).abs() <= ink * 1e-5)
                .map(|s| s.primitive.id.clone())
        }),
        ink,
        termini,
        exact_bounds,
    })
}

fn proof(w: &Wall, strokes: &[Stroke<'_>]) -> Option<Vec<String>> {
    let frame = faces(w, strokes, false)?;
    let mut ids = frame.ids;
    ids.extend([frame.caps[0].clone()?, frame.caps[1].clone()?]);
    ids.sort();
    ids.dedup();
    (ids.len() >= 3).then_some(ids)
}

fn strokes(primitives: &[Primitive]) -> Vec<Stroke<'_>> {
    let mut widths: Vec<_> = primitives.iter().filter_map(|p| p.stroke_width).collect();
    if widths.is_empty() {
        return vec![];
    }
    widths.sort_by(f64::total_cmp);
    let ordinary = widths[widths.len() / 2];
    let mut strokes = Vec::new();
    for p in primitives {
        let Some(width) = p.stroke_width.filter(|&v| v > ordinary * 1.25) else {
            continue;
        };
        if p.layer_kind() != LayerKind::Unknown {
            continue;
        }
        for (start, end) in segments(&p.shape) {
            if strokes.len() == 4096 {
                return vec![];
            }
            strokes.push(Stroke {
                primitive: p,
                start,
                end,
                width,
            });
        }
    }
    // Dense illustrations leave the existing selections intact.
    if strokes.len() > 4096 {
        return vec![];
    }
    strokes
}

/// Two offset ink corners must meet exactly at both sides of an architectural bend.
fn corner(
    a: &Wall,
    aa: &Frame,
    b: &Wall,
    bb: &Frame,
    strokes: &[Stroke<'_>],
) -> Option<CornerJoint> {
    let u = (a.end - a.start).unit();
    let v = (b.end - b.start).unit();
    let endpoint_roundoff = roundoff(a).max(roundoff(b));
    let eps = endpoint_roundoff.max(a.thickness.min(b.thickness) * 1e-5);
    if u.dot(v).abs() > 1e-5 || (aa.ink - bb.ink).abs() > aa.ink * 1e-5 {
        return None;
    }
    let t = (b.start - a.start).cross(v) / u.cross(v);
    let at = a.start + u * t;
    let ends = |w: &Wall, axis: Point, reach: f64| {
        let distances = [w.start.distance(at), w.end.distance(at)];
        let end = usize::from(distances[1] < distances[0]);
        // An ink turn joins the terminal planes. Two width interpretations
        // of one closed rectangle can share corners while their axes cross inside.
        let outward = if end == 0 {
            (w.start - at).dot(axis)
        } else {
            (at - w.end).dot(axis)
        };
        (outward >= -endpoint_roundoff && distances[end] <= reach + eps).then_some(end)
    };
    let ea = ends(a, u, b.thickness * 0.5)?;
    let eb = ends(b, v, a.thickness * 0.5)?;
    let na = Point::new(-u.y, u.x) * (a.thickness * 0.5);
    let nb = Point::new(-v.y, v.x) * (b.thickness * 0.5);
    let meet = |w: &Wall, f: &Frame, axis: Point, p: Point| {
        strokes
            .iter()
            .find(|s| {
                f.ids.contains(&s.primitive.id)
                    && w.source_ids.contains(&s.primitive.id)
                    && (s.width - f.ink).abs() <= f.ink * 1e-5
                    && (s.end - s.start).unit().cross(axis).abs() <= 1e-5
                    && (s.start.distance(p) <= eps || s.end.distance(p) <= eps)
            })
            .map(|s| s.primitive.id.clone())
    };
    let polygon = [at + na + nb, at - na + nb, at - na - nb, at + na - nb];
    let mut witnesses: Vec<_> = polygon
        .iter()
        .map(|p| {
            let x = meet(a, aa, u, *p)?;
            let y = meet(b, bb, v, *p)?;
            Some([x, y])
        })
        .collect();
    let mut ids = None;
    for pair in [[0, 2], [1, 3]] {
        if witnesses[pair[0]].is_some() && witnesses[pair[1]].is_some() {
            let mut proof = witnesses[pair[0]].take()?.to_vec();
            proof.extend(witnesses[pair[1]].take()?);
            proof.sort();
            proof.dedup();
            ids = Some(proof);
            break;
        }
    }
    Some(CornerJoint {
        wall_ids: [a.id.clone(), b.id.clone()],
        source_ids: ids?,
        polygon,
        adds_material: true,
        contact_points: None,
        ends: [ea, eb],
    })
}

impl CornerJoint {
    pub(crate) fn incident_ends(&self) -> impl Iterator<Item = (&str, usize)> {
        self.wall_ids.iter().map(String::as_str).zip(self.ends)
    }

    fn crosses(&self, opening: &crate::model::Opening) -> bool {
        let [origin, p, _, q] = self.polygon;
        let u = p - origin;
        let v = q - origin;
        let area = u.cross(v);
        let a = opening.start - origin;
        let b = opening.end - origin;
        let scale = self
            .polygon
            .iter()
            .map(|p| p.x.abs().max(p.y.abs()))
            .fold(1., f64::max);
        let eps = 64. * f64::EPSILON * scale;
        let (mut enter, mut exit) = (0_f64, 1_f64);
        // In the source parallelogram's basis clip to its open interior.
        // A doorway may end on the exterior ink or run along it.
        for (start, end, margin) in [
            (
                a.cross(v) / area,
                b.cross(v) / area,
                eps * v.norm() / area.abs(),
            ),
            (
                u.cross(a) / area,
                u.cross(b) / area,
                eps * u.norm() / area.abs(),
            ),
        ] {
            let (lo, hi) = (margin, 1. - margin);
            if (start < lo && end < lo) || (start > hi && end > hi) {
                return false;
            }
            if start != end {
                let t = (lo - start) / (end - start);
                let s = (hi - start) / (end - start);
                enter = enter.max(t.min(s));
                exit = exit.min(t.max(s));
            }
        }
        enter <= exit
    }
}

fn joints(
    walls: &[Wall],
    strokes: &[Stroke<'_>],
    frames: &[Option<Frame>],
    openings: &[crate::model::Opening],
) -> Vec<CornerJoint> {
    let eligible: Vec<_> = frames
        .iter()
        .enumerate()
        .filter_map(|(i, f)| f.as_ref().map(|f| (i, f)))
        .collect();
    if eligible.len() > 512 {
        return vec![];
    }
    let mut out = vec![];
    for (n, &(i, a)) in eligible.iter().enumerate() {
        for &(j, b) in &eligible[n + 1..] {
            if let Some(joint) = corner(&walls[i], a, &walls[j], b, strokes)
                && !openings.iter().any(|o| joint.crosses(o))
            {
                out.push(joint);
            }
        }
    }
    out
}

pub(crate) fn source_corner_joints(page: &PageScan) -> Vec<CornerJoint> {
    let ink = strokes(&page.primitives);
    let frames: Vec<_> = page
        .walls
        .iter()
        .map(|w| credible_frame(w, &ink, &page.primitives))
        .collect();
    let mut out = joints(&page.walls, &ink, &frames, &page.openings);
    out.extend(super::raster::corner_joints(page));
    out
}

pub(crate) fn corner_joints(page: &PageScan) -> Vec<CornerJoint> {
    let ready: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.wall_id.as_str())
        .collect();
    source_corner_joints(page)
        .into_iter()
        .filter(|j| j.wall_ids.iter().all(|id| ready.contains(id.as_str())))
        .collect()
}

/// Bounded replay for the connection report. The older physical-placement
/// helpers above retain their original inventory and ordering.
pub(crate) fn connection_corner_joints(
    page: &PageScan,
    mut budget: usize,
) -> Result<Vec<CornerJoint>, String> {
    fn spend(budget: &mut usize, count: usize) -> Result<(), String> {
        *budget = budget
            .checked_sub(count)
            .ok_or("measured corner evidence budget exceeded")?;
        Ok(())
    }
    let ready: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.wall_id.as_str())
        .collect();
    spend(&mut budget, page.walls.len() + page.primitives.len())?;
    let walls: Vec<_> = page
        .walls
        .iter()
        .filter(|w| ready.contains(w.id.as_str()))
        .cloned()
        .collect();
    // Count source segments once, before using the old detail/frame predicates.
    // This is a bounded QA-evidence replay, not another wall detector.
    let mut source_segments = 0usize;
    for p in &page.primitives {
        let count = match &p.shape {
            crate::model::Shape::Polyline { points, .. } => points.len(),
            crate::model::Shape::Line { .. } => 1,
            _ => 0,
        };
        spend(&mut budget, count)?;
        source_segments = source_segments
            .checked_add(count)
            .ok_or("corner source count overflow")?;
    }
    let sort_work = page
        .primitives
        .len()
        .checked_mul(page.primitives.len().max(1).ilog2() as usize + 1)
        .ok_or("corner source style work overflow")?;
    spend(&mut budget, sort_work)?;
    let ink = strokes(&page.primitives);
    let mut frames = Vec::with_capacity(walls.len());
    for w in &walls {
        let frame_cost = ink
            .len()
            .checked_mul(w.source_ids.len().saturating_mul(4).saturating_add(8))
            .ok_or("corner frame work overflow")?;
        spend(&mut budget, frame_cost)?;
        let frame = faces(w, &ink, true);
        let frame = if let Some(f) = frame {
            spend(
                &mut budget,
                page.primitives.len().saturating_add(source_segments),
            )?;
            (!repeated_details(w, &page.primitives)).then_some(f)
        } else {
            None
        };
        frames.push(frame);
    }
    let eligible: Vec<_> = frames
        .iter()
        .enumerate()
        .filter_map(|(i, f)| f.as_ref().map(|f| (i, f)))
        .collect();
    if eligible.len() > 512 {
        return Err("measured corner frame limit exceeded".into());
    }
    let mut out = vec![];
    for (pos, &(i, a)) in eligible.iter().enumerate() {
        for &(j, b) in &eligible[pos + 1..] {
            spend(&mut budget, 1)?;
            let wa = &walls[i];
            let wb = &walls[j];
            let u = (wa.end - wa.start).unit();
            let v = (wb.end - wb.start).unit();
            if u.dot(v).abs() > 1e-5 || (a.ink - b.ink).abs() > a.ink * 1e-5 {
                continue;
            }
            let at = wa.start + u * ((wb.start - wa.start).cross(v) / u.cross(v));
            let eps = roundoff(wa)
                .max(roundoff(wb))
                .max(wa.thickness.min(wb.thickness) * 1e-5);
            if wa.start.distance(at).min(wa.end.distance(at)) > wb.thickness * 0.5 + eps
                || wb.start.distance(at).min(wb.end.distance(at)) > wa.thickness * 0.5 + eps
            {
                continue;
            }
            let memberships = a
                .ids
                .len()
                .saturating_add(b.ids.len())
                .saturating_add(wa.source_ids.len())
                .saturating_add(wb.source_ids.len())
                .saturating_add(4);
            spend(
                &mut budget,
                ink.len()
                    .checked_mul(memberships)
                    .and_then(|n| n.checked_mul(8))
                    .ok_or("corner witness work overflow")?,
            )?;
            if let Some(joint) = corner(wa, a, wb, b, &ink) {
                spend(&mut budget, page.openings.len())?;
                if !page.openings.iter().any(|o| joint.crosses(o)) {
                    out.push(joint);
                }
            }
        }
    }
    // Raster replay already filters Ready walls; reserve its finite pair,
    // source-end membership and opening tests before entering the old helper.
    let raster: Vec<_> = walls
        .iter()
        .filter(|w| super::raster::is_raster(w))
        .collect();
    let max_ids = raster.iter().map(|w| w.source_ids.len()).max().unwrap_or(0);
    let per_pair = 128usize
        .saturating_add(max_ids.saturating_mul(16))
        .saturating_add(page.openings.len().saturating_mul(16));
    let pairs = raster
        .len()
        .checked_mul(raster.len().saturating_sub(1))
        .map(|n| n / 2)
        .ok_or("raster corner pair work overflow")?;
    spend(
        &mut budget,
        pairs
            .checked_mul(per_pair)
            .ok_or("raster corner work overflow")?,
    )?;
    out.extend(super::raster::corner_joints(page));
    Ok(out)
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision], allow_bends: bool) {
    if allow_bends {
        // Existing capped-wall decisions take precedence over additional bends.
        select(page, decisions, false);
    }
    let strokes = strokes(&page.primitives);
    let anchored_sources = if allow_bends {
        connected_ink(&page.primitives, &page.walls, &strokes)
    } else {
        HashSet::new()
    };
    let frames: Vec<_> = page
        .walls
        .iter()
        .map(|w| {
            if allow_bends {
                credible_frame(w, &strokes, &page.primitives)
            } else {
                faces(w, &strokes, false)
            }
        })
        .collect();
    let joints = if allow_bends {
        joints(&page.walls, &strokes, &frames, &page.openings)
    } else {
        vec![]
    };
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, _)| w)
        .collect();
    let proposals: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .enumerate()
        .filter(|(_, (w, d))| {
            d.status == WallStatus::Review
                && w.body == BodyEvidence::ParallelFaces
                && w.curve.is_none()
                && !page
                    .openings
                    .iter()
                    .any(|o| o.host_wall_ids.contains(&w.id) || overlap::aperture_crosses(w, o))
        })
        .filter_map(|(i, (w, _))| {
            let bent = || {
                let f = frames[i].as_ref()?;
                let mut ids = f.ids.clone();
                let mut ends = f.caps.each_ref().map(Option::is_some);
                let mut has_corner = false;
                ids.extend(f.caps.iter().flatten().cloned());
                for j in &joints {
                    if let Some(side) = j.wall_ids.iter().position(|id| id == &w.id) {
                        ends[j.ends[side]] = true;
                        has_corner = true;
                        ids.extend(j.source_ids.iter().cloned());
                    }
                }
                if has_corner {
                    for (end, terminal) in ends.iter_mut().zip(f.termini) {
                        *end |= terminal;
                    }
                }
                if w.start.distance(w.end) < w.thickness && !has_corner {
                    return None;
                }
                if !has_corner
                    && w.start.distance(w.end) < 2. * w.thickness
                    && !f.ids.iter().all(|id| anchored_sources.contains(id))
                {
                    return None;
                }
                if !ends.iter().all(|&e| e) {
                    return None;
                }
                ids.sort();
                ids.dedup();
                Some(ids)
            };
            proof(w, &strokes)
                .or_else(|| allow_bends.then(bent).flatten())
                .filter(|_| !repeated_details(w, &page.primitives))
                .filter(|_| {
                    !allow_bends || !blocked(w, page.withheld_walls.as_deref().unwrap_or_default())
                })
                .map(|ids| (i, ids))
        })
        .collect();
    let conflicts: HashSet<_> = proposals
        .iter()
        .filter(|(i, _)| {
            ready
                .iter()
                .any(|w| overlap::parallel_bodies_overlap(&page.walls[*i], w))
                || proposals.iter().any(|(j, _)| {
                    i != j && overlap::parallel_bodies_overlap(&page.walls[*i], &page.walls[*j])
                })
        })
        .map(|(i, _)| *i)
        .collect();
    for (i, ids) in proposals {
        if conflicts.contains(&i) {
            continue;
        }
        let d = &mut decisions[i];
        d.status = WallStatus::Ready;
        d.reason = if proof(&page.walls[i], &strokes).is_some()
            || frames[i]
                .as_ref()
                .is_some_and(|f| f.caps.iter().all(Option::is_some))
        {
            WallReason::ContrastingCappedWallFaces
        } else {
            WallReason::ContrastingBentWallFaces
        };
        d.support_source_ids = ids;
    }
}
