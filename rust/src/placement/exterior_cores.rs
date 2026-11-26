//! Reconcile a reviewed exterior wall with independently bounded batt paint.
//! The generated body is the exact visible core, including its actual end planes.
use super::{WallDecision, WallReason, WallStatus, effective_wall, insulation, material, overlap};
use crate::model::{
    BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Shape, Wall,
};
use std::collections::HashMap;

mod corner_patch;
mod interior;
mod interior_corners;
mod interior_jambs;
mod interior_seams;
mod returns;

pub(super) fn append_interiors(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    interior::append(page, decisions);
    interior_jambs::append(page, decisions);
    interior_seams::append(page, decisions);
}

const PREFIX: &str = "exterior-core:";
const STEP: &str = "exterior-step:";
const MAX_WORK: usize = 6_000_000;

pub(super) fn is_derived(wall: &Wall) -> bool {
    wall.id.starts_with(PREFIX)
}

pub(super) fn is_step(wall: &Wall) -> bool {
    wall.id.starts_with(STEP)
}

pub(super) fn exterior_layer(name: &str) -> bool {
    material::wall_name(name).1
        && name
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .any(|word| matches!(word, "exterior" | "external" | "yttervegg" | "yttervegger"))
}

fn body(tile: &insulation::MaterialTile, page: u32, index: usize) -> Option<Wall> {
    batt_body(tile, page, index, 2.)
}

fn batt_body(tile: &insulation::MaterialTile, page: u32, index: usize, cells: f64) -> Option<Wall> {
    let [a, b, c, d] = tile.outline;
    let u = (b - a).unit();
    let v = (d - a).unit();
    let axis = tile.repeat.unit();
    let (mut start, mut end, thickness) = if axis.cross(u).abs() <= 1e-5 {
        ((a + d) * 0.5, (b + c) * 0.5, a.distance(d))
    } else if axis.cross(v).abs() <= 1e-5 {
        ((a + b) * 0.5, (d + c) * 0.5, a.distance(b))
    } else {
        return None;
    };
    let direction = end - start;
    if direction.x.abs() >= direction.y.abs() && direction.x < 0.
        || direction.y.abs() > direction.x.abs() && direction.y < 0.
    {
        std::mem::swap(&mut start, &mut end);
    }
    let wall = Wall {
        id: format!("{PREFIX}{page}:{}", index + 1),
        page,
        start,
        end,
        thickness,
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: vec![],
        requires_review: true,
    };
    // Independent PDF clip coordinates and lattice transforms round at
    // different stages. This tolerance compares material width to the cell;
    // the published solid still uses the original finite clip exactly.
    (thickness > 0. && insulation::aligned_row_with_minimum_cells(&wall, tile, 3e-5, cells))
        .then_some(wall)
}

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    half: f64,
    tolerance: f64,
}

impl Frame {
    fn new(w: &Wall) -> Self {
        Self {
            origin: w.start,
            axis: (w.end - w.start).unit(),
            length: w.start.distance(w.end),
            half: w.thickness * 0.5,
            // Limited to comparing authored support strokes. Physical extents
            // and graph connections never use this authoring tolerance.
            tolerance: 0.025 * w.thickness,
        }
    }

    fn local(&self, point: Point) -> (f64, f64) {
        let p = point - self.origin;
        (p.dot(self.axis), self.axis.cross(p))
    }

    fn observed_extent(&self, w: &Wall) -> bool {
        if w.curve.is_some()
            || w.body != BodyEvidence::ParallelFaces
            || !(1.9 * self.half..=2.8 * self.half).contains(&w.thickness)
        {
            return false;
        }
        let [(a, ay), (b, by)] = [self.local(w.start), self.local(w.end)];
        (ay - by).abs() <= self.tolerance
            && ay.abs() + self.half <= w.thickness * 0.5 + self.tolerance
            && a.min(b).abs() <= self.tolerance
            && (a.max(b) - self.length).abs() <= self.tolerance
    }

    fn lining(&self, other: &Wall) -> bool {
        if other.thickness >= self.half * 1.5 || other.thickness <= self.half * 0.35 {
            return false;
        }
        let [(a, ay), (b, by)] = [self.local(other.start), self.local(other.end)];
        let gap = ay.abs() - other.thickness * 0.5 - self.half;
        (ay - by).abs() <= self.tolerance
            && (-self.tolerance..=self.tolerance).contains(&gap)
            && a.max(b).min(self.length) - a.min(b).max(0.) >= 0.8 * self.length
    }
}

struct Rail<'a> {
    id: &'a str,
    ends: [Point; 2],
    stroked: bool,
}

fn draw_path(id: &str) -> &str {
    id.rsplit_once(":subpath:").map_or(id, |(path, _)| path)
}

fn rails<'a>(shape: &Frame, ink: &'a [Rail<'a>], tile: &str) -> Option<[&'a str; 2]> {
    let mut faces: [Vec<&Rail<'_>>; 2] = [vec![], vec![]];
    for line in ink
        .iter()
        .filter(|line| draw_path(line.id) != draw_path(tile))
    {
        let [(a, ay), (b, by)] = line.ends.map(|p| shape.local(p));
        if ay.signum() != by.signum()
            || (ay.abs() - shape.half).abs() > shape.tolerance
            || (by.abs() - shape.half).abs() > shape.tolerance
            || a.min(b) > shape.tolerance
            || a.max(b) < shape.length - shape.tolerance
        {
            continue;
        }
        // Two distinct authored paths of each stroke kind are sufficient
        // to find a pair that does not reuse the same path on both sides.
        let side = &mut faces[usize::from(ay > 0.)];
        if side
            .iter()
            .filter(|old| old.stroked == line.stroked)
            .count()
            < 2
            && side
                .iter()
                .all(|old| old.stroked != line.stroked || draw_path(old.id) != draw_path(line.id))
        {
            side.push(line);
        }
    }
    for a in &faces[0] {
        for b in &faces[1] {
            if draw_path(a.id) != draw_path(b.id) && (a.stroked || b.stroked) {
                return Some([a.id, b.id]);
            }
        }
    }
    None
}

pub(super) fn append(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    if page.material_tiles.is_empty()
        || page.material_tiles.len().saturating_mul(
            page.primitives
                .len()
                .saturating_add(page.material_tiles.len())
                .saturating_add(page.walls.len()),
        ) > MAX_WORK
    {
        return;
    }
    let mut ink: HashMap<&str, Vec<Rail<'_>>> = HashMap::new();
    for p in &page.primitives {
        let Some(layer) = p.layer.as_deref().filter(|&l| exterior_layer(l)) else {
            continue;
        };
        if p.layer_category_override.is_some() && p.layer_kind() != crate::model::LayerKind::Wall {
            continue;
        }
        if let Shape::Line { start, end } = p.shape {
            ink.entry(layer).or_default().push(Rail {
                id: &p.id,
                ends: [start, end],
                stroked: p.stroke_width.is_some(),
            });
        }
    }
    let tiles: Vec<_> = page
        .material_tiles
        .iter()
        .enumerate()
        .filter_map(|(i, tile)| {
            let layer = tile.layer.as_deref()?;
            ink.get(layer)?;
            Some((tile, body(tile, page.page_number, i)?))
        })
        .collect();
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| effective_wall(w, Some(d)))
        .collect();
    let mut proposals = vec![];
    for (tile, w) in &tiles {
        let shape = Frame::new(w);
        if shape.length < 5. * w.thickness
            || ready
                .iter()
                .any(|other| overlap::parallel_bodies_overlap(w, other))
            || page
                .openings
                .iter()
                .any(|opening| overlap::aperture_crosses(w, opening))
            || !tiles
                .iter()
                .any(|(inner, lining)| inner.layer == tile.layer && shape.lining(lining))
        {
            continue;
        }
        let Some([left, right]) = rails(
            &shape,
            &ink[tile.layer.as_deref().expect("checked layer")],
            &tile.id,
        ) else {
            continue;
        };
        let Some((observed, _)) = page
            .walls
            .iter()
            .zip(decisions.iter())
            .find(|(observed, d)| {
                shape.observed_extent(observed)
                    && (d.status == WallStatus::Review
                        || d.reason == WallReason::ShortOpeningLinkedIsolatedFragment
                            && page.openings.iter().any(|o| {
                                o.kind == crate::model::OpeningKind::Door
                                    && o.swing_observations.len() == 2
                                    && o.host_wall_ids.contains(&observed.id)
                            }))
            })
        else {
            continue;
        };
        let mut wall = w.clone();
        wall.source_ids = vec![left.to_owned(), right.to_owned()];
        wall.source_ids.sort();
        let mut support_source_ids = wall.source_ids.clone();
        support_source_ids.push(tile.id.clone());
        support_source_ids.sort();
        let decision = WallDecision {
            wall_id: wall.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::SourceBoundedExteriorCore,
            component_id: format!("p{}:{}", page.page_number, wall.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![observed.id.clone()],
            support_source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        proposals.push((wall, decision));
    }
    let mut disputed = vec![false; proposals.len()];
    for (i, (a, _)) in proposals.iter().enumerate() {
        for (offset, (b, _)) in proposals[i + 1..].iter().enumerate() {
            if overlap::parallel_bodies_overlap(a, b) {
                disputed[i] = true;
                disputed[i + 1 + offset] = true;
            }
        }
    }
    drop(ready);
    for ((wall, decision), disputed) in proposals.into_iter().zip(disputed) {
        if !disputed {
            page.semantics
                .routing
                .push(crate::semantics::wall_route(&wall));
            page.walls.push(wall);
            decisions.push(decision);
        }
    }
}

pub(super) fn same_body(a: &Wall, b: &Wall) -> bool {
    a.id == b.id
        && a.page == b.page
        && a.start == b.start
        && a.end == b.end
        && a.thickness == b.thickness
        && a.body == b.body
        && a.curve.is_none()
        && a.source_ids == b.source_ids
        && a.requires_review
}

fn corner_touch(a: &Wall, b: &Wall) -> bool {
    let axis = |w: &Wall| (w.end - w.start).unit();
    if axis(a).dot(axis(b)).abs() > 1e-5 {
        return false;
    }
    let corners = |w: &Wall| {
        let u = axis(w);
        let n = Point::new(-u.y, u.x) * (w.thickness * 0.5);
        [w.start + n, w.start - n, w.end + n, w.end - n]
    };
    let second = corners(b);
    corners(a).iter().any(|p| {
        second
            .iter()
            .any(|q| p.distance(*q) <= super::roundoff(a).max(super::roundoff(b)))
    })
}

fn tile_tail(tile_wall: &Wall, ready: &Wall, at_start: bool) -> Option<Wall> {
    let f = Frame::new(tile_wall);
    let (a, ay) = f.local(ready.start);
    let (b, by) = f.local(ready.end);
    let eps = super::roundoff(tile_wall).max(super::roundoff(ready));
    if ready.curve.is_some()
        || (ay - by).abs() > eps
        || ay.abs() + f.half > ready.thickness * 0.5 + eps
    {
        return None;
    }
    let depth = if at_start {
        a.min(b)
    } else {
        f.length - a.max(b)
    };
    if !(eps..=tile_wall.thickness * 0.25).contains(&depth) {
        return None;
    }
    let mut tail = tile_wall.clone();
    if at_start {
        tail.end = tail.start + f.axis * depth;
    } else {
        tail.start = tail.end - f.axis * depth;
    }
    Some(tail)
}

/// A short scan extent can stop just before an authored insulation return.
/// Accept only material inside the original visible tile and a branch with
/// its own paired faces and signed exterior opening jamb.
pub(super) fn append_steps(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    if page
        .material_tiles
        .len()
        .saturating_mul(page.primitives.len().max(1))
        > MAX_WORK
        || page.material_tiles.is_empty()
    {
        return;
    }
    let mut ink: HashMap<&str, Vec<Rail<'_>>> = HashMap::new();
    for p in &page.primitives {
        let Some(layer) = p.layer.as_deref().filter(|&l| exterior_layer(l)) else {
            continue;
        };
        if p.layer_category_override.is_some() && p.layer_kind() != crate::model::LayerKind::Wall {
            continue;
        }
        if let Shape::Line { start, end } = p.shape {
            ink.entry(layer).or_default().push(Rail {
                id: &p.id,
                ends: [start, end],
                stroked: p.stroke_width.is_some(),
            });
        }
    }
    let tiles: Vec<_> = page
        .material_tiles
        .iter()
        .enumerate()
        .filter_map(|(i, tile)| {
            let layer = tile.layer.as_deref()?;
            let bars = ink.get(layer)?;
            Some((tile, batt_body(tile, page.page_number, i, 1.)?, bars))
        })
        .collect();
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| (effective_wall(w, Some(d)), d))
        .collect();
    let absolute_conflict = |w: &Wall| {
        ready
            .iter()
            .any(|(v, _)| overlap::parallel_bodies_overlap(w, v))
            || page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(w, o))
    };
    let doors: Vec<&Opening> = page
        .openings
        .iter()
        .filter(|o| {
            o.kind == OpeningKind::Door
                && o.evidence == OpeningEvidence::FiniteJambSwing
                && matches!(o.swing_observations.as_slice(), [swing]
                    if !swing.open_leaf_source_ids.is_empty() && !swing.closed_leaf_source_ids.is_empty())
        })
        .collect();
    let mut additions: Vec<Vec<(Wall, WallDecision)>> = vec![];
    for (tile, branch, bars) in &tiles {
        let shape = Frame::new(branch);
        if shape.length < 5. * branch.thickness || absolute_conflict(branch) {
            continue;
        }
        let Some(pair) = rails(&shape, bars, &tile.id) else {
            continue;
        };
        for &door in &doors {
            let Some((door_at_start, cap)) = measured_doorward_end(branch, door, bars) else {
                continue;
            };
            for (parent, parent_decision) in &ready {
                for (prior_tile, full, prior_bars) in &tiles {
                    if tile.layer != prior_tile.layer
                        || !parent_decision.support_source_ids.contains(&prior_tile.id)
                        || !corner_touch(branch, full)
                    {
                        continue;
                    }
                    for at_start in [false, true] {
                        let Some(tail) = tile_tail(full, parent, at_start) else {
                            continue;
                        };
                        if !corner_touch(branch, &tail) || absolute_conflict(&tail) {
                            continue;
                        }
                        let free_end = if door_at_start {
                            branch.end
                        } else {
                            branch.start
                        };
                        let other_end = if door_at_start {
                            branch.start
                        } else {
                            branch.end
                        };
                        let tip = if at_start { tail.start } else { tail.end };
                        if tip.distance(free_end) >= tip.distance(other_end) {
                            continue;
                        }
                        let Some(tail_rails) =
                            rails(&Frame::new(&tail), prior_bars, &prior_tile.id)
                        else {
                            continue;
                        };
                        let corner =
                            corner_patch::prove(branch, tile, &tail, prior_tile, bars, &parent.id)
                                .filter(|(wall, _)| !absolute_conflict(wall));
                        let (branch, mut decision) = step_decision(
                            branch.clone(),
                            tile,
                            &[pair[0], pair[1], cap],
                            &parent.id,
                            "branch",
                        );
                        decision.opening_ids.push(door.id.clone());
                        let mut returns = returns::prove(page, &ready, &tiles, &branch, tile, door);
                        let mut set = vec![
                            (branch, decision),
                            step_decision(tail, prior_tile, &tail_rails, &parent.id, "tail"),
                        ];
                        if returns.len() == 1 {
                            set.extend(returns.pop().unwrap());
                        }
                        set.extend(corner);
                        additions.push(set);
                    }
                }
            }
        }
    }
    drop(ready);
    append_unambiguous_steps(page, decisions, additions);
}

fn measured_doorward_end<'a>(
    w: &Wall,
    o: &Opening,
    ink: &'a [Rail<'a>],
) -> Option<(bool, &'a str)> {
    let f = Frame::new(w);
    let [a, b] = [f.local(o.start), f.local(o.end)];
    if (a.1 - b.1).abs() > f.tolerance || a.1.abs().max(b.1.abs()) > f.half + f.tolerance {
        return None;
    }
    let (at_start, offset, at) = if a.0.max(b.0) <= 0. {
        (true, -a.0.max(b.0), 0.)
    } else if a.0.min(b.0) >= f.length {
        (false, a.0.min(b.0) - f.length, f.length)
    } else {
        return None;
    };
    if offset > w.thickness * 0.85 {
        return None;
    }
    let eps = super::roundoff(w);
    let cap = ink.iter().find(|s| {
        let [a, b] = s.ends.map(|p| f.local(p));
        s.stroked
            && (a.0 - at).abs() <= eps
            && (b.0 - at).abs() <= eps
            && a.1.min(b.1) <= -f.half + eps
            && a.1.max(b.1) >= f.half - eps
            && a.1.max(b.1) - a.1.min(b.1) <= w.thickness * 1.1
    })?;
    Some((at_start, cap.id))
}

fn step_decision(
    mut wall: Wall,
    tile: &insulation::MaterialTile,
    sources: &[&str],
    parent: &str,
    role: &str,
) -> (Wall, WallDecision) {
    let index = wall.id.strip_prefix(PREFIX).expect("derived tile");
    wall.id = format!("{STEP}{index}:{role}");
    wall.source_ids = sources.iter().map(|&id| id.to_owned()).collect();
    wall.source_ids.sort();
    wall.source_ids.dedup();
    let mut support = wall.source_ids.clone();
    support.push(tile.id.clone());
    support.sort();
    let d = WallDecision {
        wall_id: wall.id.clone(),
        status: WallStatus::Ready,
        reason: WallReason::SourceBoundedExteriorCore,
        component_id: format!("p{}:{}", wall.page, wall.id),
        component_wall_count: 1,
        junction_node_ids: vec![],
        support_wall_ids: vec![parent.to_owned()],
        support_source_ids: support,
        opening_ids: vec![],
        room_ids: vec![],
        placement_extent: None,
    };
    (wall, d)
}

fn append_unambiguous_steps(
    page: &mut PageScan,
    decisions: &mut Vec<WallDecision>,
    mut additions: Vec<Vec<(Wall, WallDecision)>>,
) {
    additions.sort_by(|a, b| {
        a[0].0
            .id
            .cmp(&b[0].0.id)
            .then_with(|| a[1].0.id.cmp(&b[1].0.id))
    });
    additions.dedup_by(|a, b| {
        a.len() == b.len()
            && a.iter()
                .zip(b.iter())
                .all(|((wall, decision), (other, other_decision))| {
                    same_body(wall, other) && decision == other_decision
                })
    });
    let mut disputed = vec![false; additions.len()];
    for (i, a) in additions.iter().enumerate() {
        for (offset, b) in additions[i + 1..].iter().enumerate() {
            if a.iter().any(|(a, _)| {
                b.iter()
                    .any(|(b, _)| a.id == b.id || overlap::parallel_bodies_overlap(a, b))
            }) {
                disputed[i] = true;
                disputed[i + 1 + offset] = true;
            }
        }
    }
    for (set, disputed) in additions.into_iter().zip(disputed) {
        if !disputed {
            for (w, d) in set {
                page.semantics
                    .routing
                    .push(crate::semantics::wall_route(&w));
                page.walls.push(w);
                decisions.push(d);
            }
        }
    }
}
