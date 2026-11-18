//! Resolve source geometry that also admits a non-wall reading. Raw observations
//! keep their original IDs, extents and evidence in either outcome.
use super::*;
use crate::model::{WallSelectionReason, WithheldWall};
use std::collections::HashMap;
mod casework;
mod enclosure;
mod furnishings;

#[cfg(test)]
mod tests;

#[derive(Clone, Copy)]
struct Stroke {
    line: Segment,
    source: usize,
}
struct Furniture {
    sources: Vec<usize>,
    perimeter: [Point; 4],
}

struct Ink {
    strokes: Vec<Stroke>,
    index: Index,
    loose: Vec<Option<bool>>,
    work: usize,
}

impl Ink {
    fn new(page: &Page, options: &Options) -> Result<Self, String> {
        let limit = options.max_wall_seeds.saturating_mul(32).min(500_000);
        let mut strokes = Vec::new();
        for (source, p) in page.primitives.iter().enumerate() {
            if !matches!(
                p.layer_kind(),
                LayerKind::Wall | LayerKind::Unknown | LayerKind::Object
            ) {
                continue;
            }
            let mut edge = |a: &Point, b: &Point| -> Result<(), String> {
                if let Some(line) = Segment::new(V::from(a), V::from(b)) {
                    if strokes.len() >= limit {
                        return Err("wall selection stroke limit exceeded".into());
                    }
                    strokes.push(Stroke { line, source });
                }
                Ok(())
            };
            match &p.shape {
                Shape::Line { start, end } => edge(start, end)?,
                Shape::Polyline {
                    points,
                    filled: false,
                    closed,
                } => {
                    for e in points.windows(2) {
                        edge(&e[0], &e[1])?;
                    }
                    if *closed && points.len() > 2 {
                        edge(points.last().unwrap(), &points[0])?;
                    }
                }
                _ => {}
            }
        }
        let index = Index::build(&strokes.iter().map(|s| s.line.bbox()).collect::<Vec<_>>());
        Ok(Self {
            loose: vec![None; strokes.len()],
            strokes,
            index,
            work: options.max_wall_seeds.saturating_mul(2048).min(32_000_000),
        })
    }
    fn charge(&mut self, n: usize) -> Result<(), String> {
        self.work = self
            .work
            .checked_sub(n)
            .ok_or("wall selection neighborhood work limit exceeded")?;
        Ok(())
    }
    fn nearby(&mut self, b: Box2) -> Result<Vec<usize>, String> {
        let mut out = vec![];
        self.index.neighbors(b, &mut out);
        self.charge(out.len())?;
        out.retain(|&i| self.strokes[i].line.bbox().touches(b));
        Ok(out)
    }
    // A line ending on a jamb, return or another stroke is not freestanding
    // symbol detail. Duplicate paint of the same segment is not a connection.
    fn free_ends(&mut self, idx: usize, tol: f64) -> Result<bool, String> {
        if let Some(loose) = self.loose[idx] {
            return Ok(loose);
        }
        let line = self.strokes[idx].line;
        let mut loose = true;
        'ends: for p in [line.a, line.b] {
            for j in self.nearby(Box2::new(p, p).pad(tol))? {
                let other = self.strokes[j].line;
                if idx == j || same_ends(line, other, tol) {
                    continue;
                }
                let nearest = other.at(other.offset(p).clamp(0., other.len));
                if nearest.minus(p).norm() <= tol {
                    loose = false;
                    break 'ends;
                }
            }
        }
        self.loose[idx] = Some(loose);
        Ok(loose)
    }
    fn returns(&mut self, p: V, axis: Segment, tol: f64) -> Result<Vec<(V, usize)>, String> {
        let mut out = vec![];
        for i in self.nearby(Box2::new(p, p).pad(tol))? {
            let s = self.strokes[i];
            if s.line.dir.dot(axis.dir).abs() > ANGLE.sin() {
                continue;
            }
            for (end, far) in [(s.line.a, s.line.b), (s.line.b, s.line.a)] {
                if end.minus(p).norm() <= tol && far.minus(p).norm() > tol {
                    out.push((far, s.source));
                }
            }
        }
        Ok(out)
    }
    fn joins(&mut self, a: V, b: V, tol: f64) -> Result<Option<usize>, String> {
        Ok(self.joined_stroke(a, b, tol)?.map(|s| s.source))
    }
    fn joined_stroke(&mut self, a: V, b: V, tol: f64) -> Result<Option<Stroke>, String> {
        let Some(wanted) = Segment::new(a, b) else {
            return Ok(None);
        };
        Ok(self
            .nearby(Box2::new(a, a).pad(tol))?
            .into_iter()
            .find_map(|i| {
                let s = self.strokes[i];
                same_ends(wanted, s.line, tol).then_some(s)
            }))
    }
}

fn same_ends(a: Segment, b: Segment, tol: f64) -> bool {
    (a.a.minus(b.a).norm() <= tol && a.b.minus(b.b).norm() <= tol)
        || (a.a.minus(b.b).norm() <= tol && a.b.minus(b.a).norm() <= tol)
}
fn range(axis: Segment, b: Segment) -> (f64, f64) {
    let (a, b) = (axis.offset(b.a), axis.offset(b.b));
    (a.min(b), a.max(b))
}
fn height(axis: Segment, p: V) -> f64 {
    axis.dir.cross(p.minus(axis.a))
}

/// A row of detached strokes must share two end planes and have a sequence of
/// separated, non-crossing members. Slightly splayed bedding lines still form
/// a row. Collinear duplicate paint cannot supply additional members.
fn row(
    ink: &mut Ink,
    reference: Segment,
    indices: &[usize],
    needed: usize,
    opts: &Options,
    join: f64,
) -> Result<Vec<usize>, String> {
    let mut offsets = vec![];
    for &i in indices {
        let l = ink.strokes[i].line;
        let (lo, hi) = range(reference, l);
        // Two end planes define the longitudinal extent. Limit the direction
        // difference too so a shallow arc or converging rays cannot count.
        if lo.abs() > opts.snap_tolerance
            || (hi - reference.len).abs() > opts.snap_tolerance
            || l.dir.cross(reference.dir).abs() > (15f64).to_radians().sin()
            || !ink.free_ends(i, join)?
        {
            continue;
        }
        let (a, b) = if reference.offset(l.a) < reference.offset(l.b) {
            (l.a, l.b)
        } else {
            (l.b, l.a)
        };
        offsets.push((height(reference, a), height(reference, b), i));
    }
    offsets.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(&b.2))
    });
    let mut result = vec![];
    let mut gaps = vec![];
    let mut last = (f64::NEG_INFINITY, f64::NEG_INFINITY);
    for (a, b, i) in offsets {
        if a - last.0 >= opts.min_wall_pair_separation
            && b - last.1 >= opts.min_wall_pair_separation
        {
            if !result.is_empty() {
                gaps.push((a - last.0, b - last.1));
            }
            result.push(i);
            last = (a, b);
        }
    }
    let regular = |second: bool| {
        let (mut lo, mut hi) = (f64::INFINITY, 0.0_f64);
        for &(left, right) in &gaps {
            let d = if second { right } else { left };
            lo = lo.min(d);
            hi = hi.max(d);
        }
        hi - lo <= opts.snap_tolerance + 0.12 * lo
    };
    Ok(
        if result.len() >= needed && regular(false) && regular(true) {
            result
        } else {
            vec![]
        },
    )
}

fn details(
    ink: &mut Ink,
    axis: Segment,
    wall: &Wall,
    neighborhood: &[usize],
    sources: &BTreeSet<usize>,
    opts: &Options,
    join: f64,
) -> Result<Option<(WallSelectionReason, Vec<usize>)>, String> {
    let half = wall.thickness / 2.;
    let mut parallels = vec![];
    let mut crossing = [vec![], vec![]];
    for &i in neighborhood {
        let s = ink.strokes[i];
        let line = s.line;
        if line.len < opts.min_wall_fragment_length {
            continue;
        }
        let (a, b) = (height(axis, line.a), height(axis, line.b));
        if line.dir.cross(axis.dir).abs() <= (15f64).to_radians().sin()
            && a.abs().max(b.abs()) <= 4. * wall.thickness
        {
            parallels.push(i);
        }
        if line.dir.cross(axis.dir).abs() <= 0.5 {
            continue;
        }
        for (inside, outside) in [(line.a, line.b), (line.b, line.a)] {
            let inward = height(axis, inside);
            let beyond = height(axis, outside);
            if inward.abs() < half - join
                && beyond.abs() > half + join
                && axis.offset(inside) > join
                && axis.offset(inside) < axis.len - join
                && axis.offset(outside) > join
                && axis.offset(outside) < axis.len - join
            {
                crossing[usize::from(beyond > 0.)].push(i);
            }
        }
    }
    // Reject a paired observation only when both claimed faces are themselves
    // independent members of the detail band on opposite sides of the axis.
    let candidates = row(ink, axis, &parallels, 4, opts, join)?;
    let on_side = |i: &usize, sign: f64| {
        let s = ink.strokes[*i];
        sources.contains(&s.source)
            && (height(axis, s.line.at(s.line.len / 2.)) * sign - half).abs() <= opts.snap_tolerance
    };
    if candidates.iter().any(|i| on_side(i, -1.)) && candidates.iter().any(|i| on_side(i, 1.)) {
        return Ok(Some((
            WallSelectionReason::RepeatedSymbolStrokes,
            candidates
                .into_iter()
                .map(|i| ink.strokes[i].source)
                .collect(),
        )));
    }
    // Distinguish a transverse detail that *crosses* a claimed material face
    // from insulation hatches terminating within, or at, the two wall faces.
    for (side_number, side) in crossing.iter().enumerate().filter(|(_, v)| v.len() >= 3) {
        let sign = if side_number == 0 { -1. } else { 1. };
        // Both ends of the crossed face must terminate within transverse end
        // strokes of the same outline. Local furnishing strokes crossing a
        // much longer partition cannot invalidate its entire observed run.
        let mut ends = [None; 2];
        for &i in neighborhood {
            let s = ink.strokes[i];
            if s.line.dir.dot(axis.dir).abs() > ANGLE.sin() {
                continue;
            }
            let (a, b) = (sign * height(axis, s.line.a), sign * height(axis, s.line.b));
            if a.min(b) > -half + join || a.max(b) < half + join {
                continue;
            }
            for (e, target) in [0., axis.len].into_iter().enumerate() {
                let p = axis.at(target).plus(axis.dir.normal().times(sign * half));
                if s.line
                    .at(s.line.offset(p).clamp(0., s.line.len))
                    .minus(p)
                    .norm()
                    <= join
                {
                    ends[e] = Some(s.source);
                }
            }
        }
        let [Some(start), Some(end)] = ends else {
            continue;
        };
        for &i in side {
            if !ink.free_ends(i, join)? {
                continue;
            }
            ink.charge(side.len())?;
            let detail = row(ink, ink.strokes[i].line, side, 3, opts, join)?;
            if !detail.is_empty() {
                return Ok(Some((
                    WallSelectionReason::InteriorDetailCrossesFace,
                    detail
                        .into_iter()
                        .map(|j| ink.strokes[j].source)
                        .chain([start, end])
                        .collect(),
                )));
            }
        }
    }
    Ok(None)
}

fn outline(
    ink: &mut Ink,
    axis: Segment,
    wall: &Wall,
    neighborhood: &[usize],
    sources: &BTreeSet<usize>,
    opts: &Options,
    join: f64,
) -> Result<Option<Vec<usize>>, String> {
    let mut supports = vec![];
    let half = wall.thickness / 2.;
    for &i in neighborhood {
        let s = ink.strokes[i];
        if !sources.contains(&s.source) || s.line.dir.cross(axis.dir).abs() > ANGLE.sin() {
            continue;
        }
        let h = height(axis, s.line.at(s.line.len / 2.));
        let (lo, hi) = range(axis, s.line);
        if (h.abs() - half).abs() <= opts.snap_tolerance
            && lo <= opts.snap_tolerance
            && hi >= axis.len - opts.snap_tolerance
        {
            supports.push((s, h, lo, hi));
        }
    }
    // A source outline can be paired with an unrelated long wall face next to
    // it. Require an observed return or contiguous repeated bay and an
    // opposite face with different natural limits, never just parallelism.
    for &(s, h, lo, hi) in &supports {
        if lo.abs() > opts.snap_tolerance || (hi - axis.len).abs() > opts.snap_tolerance {
            continue;
        }
        let extends = supports.iter().any(|(_, other, a, b)| {
            other * h < 0. && (*a < -opts.snap_tolerance || *b > axis.len + opts.snap_tolerance)
        });
        if !extends {
            continue;
        }
        // Reliable flush ends on both observed faces corroborate the body.
        if supports.iter().any(|(_, other, a, b)| {
            other * h < 0. && a.abs() <= join && (b - axis.len).abs() <= join
        }) {
            continue;
        }
        let first = ink.returns(s.line.a, axis, join)?;
        let second = ink.returns(s.line.b, axis, join)?;
        let broad = axis.len < 3. * wall.thickness;
        for &(p, aid) in &first {
            for &(q, bid) in &second {
                ink.charge(1)?;
                let a = height(axis, p) - h;
                let b = height(axis, q) - h;
                if a.abs().min(b.abs()) < opts.min_wall_fragment_length {
                    continue;
                }
                // A transverse stepped outline goes to opposite sides at its
                // two ends. It cannot by itself cap a broad inferred strip.
                if broad && a * b < 0. && a.abs().min(b.abs()) > half {
                    return Ok(Some(vec![s.source, aid, bid]));
                }
                if (a - b).abs() > join || a * b <= 0. {
                    continue;
                }
                let Some(back) = ink.joins(p, q, join)? else {
                    continue;
                };
                if a * h < 0. && a.abs() < wall.thickness - join && broad {
                    return Ok(Some(vec![s.source, aid, bid, back]));
                }
                // A second full bay on the far side corroborates a cabinet or
                // workbench outline. One rectangle adjoining a wall is
                // ambiguous and remains available to structural consumers.
                if a * h <= 0. {
                    continue;
                }
                let dx = p.minus(s.line.a);
                let dy = q.minus(s.line.b);
                let far_a = p.plus(dx);
                let far_b = q.plus(dy);
                if let (Some(x), Some(y), Some(z)) = (
                    ink.joins(p, far_a, join)?,
                    ink.joins(q, far_b, join)?,
                    ink.joins(far_a, far_b, join)?,
                ) {
                    return Ok(Some(vec![s.source, aid, bid, back, x, y, z]));
                }
            }
        }
    }
    Ok(None)
}

pub fn select(
    page: &Page,
    observations: Vec<Wall>,
    opts: &Options,
) -> Result<(Vec<Wall>, Vec<WithheldWall>), String> {
    if !observations
        .iter()
        .any(|w| w.body == BodyEvidence::ParallelFaces)
    {
        return Ok((observations, vec![]));
    }
    let by_id: HashMap<_, _> = page
        .primitives
        .iter()
        .enumerate()
        .map(|(i, p)| (p.id.as_str(), i))
        .collect();
    let mut ink = Ink::new(page, opts)?;
    let enclosures = enclosure::Context::new(page, &mut ink)?;
    let casework = casework::Context::new(page, &observations, &by_id);
    let fittings = furnishings::Context::new(page, &observations, &by_id);
    let mut selected = vec![];
    let mut withheld = vec![];
    let join = (opts.snap_tolerance * 0.1).min(opts.min_wall_pair_separation * 0.1);
    for wall in observations {
        if wall.body != BodyEvidence::ParallelFaces || wall.curve.is_some() {
            selected.push(wall);
            continue;
        }
        let sources: BTreeSet<_> = wall
            .source_ids
            .iter()
            .filter_map(|id| by_id.get(id.as_str()).copied())
            .collect();
        if sources
            .iter()
            .any(|&i| page.primitives[i].layer_kind() == LayerKind::Wall)
        {
            selected.push(wall);
            continue;
        }
        let axis = Segment::new(V::from(&wall.start), V::from(&wall.end))
            .ok_or("invalid wall selection axis")?;
        let nearby = ink.nearby(axis.bbox().pad(wall.thickness * 4. + join))?;
        let mut furniture_outline = None;
        let mut furniture = |reason, observed: Furniture| {
            furniture_outline = Some(observed.perimeter);
            (reason, observed.sources)
        };
        let mut decision = match details(&mut ink, axis, &wall, &nearby, &sources, opts, join)? {
            Some(detail) => Some(detail),
            None => outline(&mut ink, axis, &wall, &nearby, &sources, opts, join)?
                .map(|w| (WallSelectionReason::OutlineEdgePair, w)),
        };
        if decision.is_none() {
            decision = enclosures
                .labeled(page, &mut ink, axis, wall.thickness, opts)?
                .map(|ids| (WallSelectionReason::LabeledEnclosure, ids));
        }
        if decision.is_none() {
            decision = casework
                .bay(page, &mut ink, axis, wall.thickness, &nearby, opts, join)?
                .map(|observed| furniture(WallSelectionReason::CabinetFront, observed));
        }
        if decision.is_none()
            && let Some(ids) =
                casework.two_leaf_bay(page, &mut ink, axis, wall.thickness, &nearby, join)?
            && !enclosures.spans_interior(page, &mut ink, axis, wall.thickness, join)?
        {
            decision = Some(furniture(WallSelectionReason::CabinetFront, ids));
        }
        if decision.is_none() {
            decision = fittings
                .surface(
                    &casework,
                    &enclosures,
                    page,
                    &mut ink,
                    axis,
                    wall.thickness,
                    &sources,
                    &nearby,
                    opts,
                    join,
                )?
                .map(|observed| furniture(WallSelectionReason::FurnishedSurface, observed));
        }
        if let Some((reason, ids)) = decision {
            let witness_source_ids = ids
                .into_iter()
                .map(|i| page.primitives[i].id.clone())
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect();
            withheld.push(WithheldWall {
                furniture_outline,
                wall,
                reason,
                witness_source_ids,
            });
        } else {
            selected.push(wall);
        }
    }
    Ok((selected, withheld))
}
