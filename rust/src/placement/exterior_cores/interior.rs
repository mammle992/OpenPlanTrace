//! Resolve concentric interior wall hypotheses using the finite authored batt core.
use super::*;

const PREFIX: &str = "interior-core:";

pub(super) fn interior_layer(name: &str) -> bool {
    material::wall_name(name).1
        && name
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
                        | "skillevegg"
                        | "skillevegger"
                )
            })
}

fn coincident_extent(frame: &Frame, wall: &Wall) -> bool {
    let [a, b] = [wall.start, wall.end].map(|p| frame.local(p));
    let width = frame.half * 2.;
    let end_margin = width * 0.15;
    wall.body == BodyEvidence::ParallelFaces
        && wall.curve.is_none()
        && a.1.abs().max(b.1.abs()) <= width * 3e-5
        && a.0.min(b.0).abs() <= end_margin
        && (a.0.max(b.0) - frame.length).abs() <= end_margin
}

fn face_pair<'a>(
    wall: &'a Wall,
    frame: &Frame,
    layer: &str,
    sources: &material::Sources<'_>,
    core: bool,
) -> Option<[&'a str; 2]> {
    let eps = crate::placement::roundoff(wall);
    let end_margin = if core { 0.3 * frame.half } else { eps };
    let mut faces: [Vec<(&str, f64, f64)>; 2] = [vec![], vec![]];
    for id in &wall.source_ids {
        let Some(source) = sources
            .get(id.as_str())
            .filter(|s| s.exclusive_wall && s.primitive.layer.as_deref() == Some(layer))
        else {
            continue;
        };
        let Shape::Line { start, end } = source.primitive.shape else {
            continue;
        };
        let [a, b] = [start, end].map(|p| frame.local(p));
        let side = a.1.signum();
        if (a.1 - side * frame.half)
            .abs()
            .max((b.1 - side * frame.half).abs())
            > eps
        {
            continue;
        }
        let lo = a.0.min(b.0);
        let hi = a.0.max(b.0);
        if lo <= end_margin && hi >= frame.length - end_margin {
            faces[usize::from(side > 0.)].push((id.as_str(), lo, hi));
        }
    }
    for &(left, left_lo, left_hi) in &faces[0] {
        for &(right, right_lo, right_hi) in &faces[1] {
            if draw_path(left) != draw_path(right)
                && left_lo.min(right_lo) <= eps
                && left_hi.max(right_hi) >= frame.length - eps
            {
                return Some([left, right]);
            }
        }
    }
    None
}

/// Select an exact painted core only where an observed wall and a wider
/// same-axis finish would otherwise leave competing placement hypotheses.
pub(super) fn append(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    if page.material_tiles.is_empty()
        || page.material_tiles.len().saturating_mul(page.walls.len()) > MAX_WORK
    {
        return;
    }
    let mut reviewed = vec![];
    let mut ready = vec![];
    for (wall, decision) in page.walls.iter().zip(decisions.iter()) {
        match decision.status {
            WallStatus::Ready => ready.push(effective_wall(wall, Some(decision))),
            WallStatus::Review if decision.reason == WallReason::OverlappingWallAxes => {
                reviewed.push(wall);
            }
            _ => (),
        }
    }
    if reviewed.len() < 2 {
        return;
    }
    let sources = material::sources(page);
    let linings: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.reason == WallReason::SourceBoundedWallLining)
        .map(|(wall, _)| wall)
        .collect();
    let mut proposals = vec![];
    for (index, tile) in page.material_tiles.iter().enumerate() {
        let Some(layer) = tile.layer.as_deref().filter(|name| interior_layer(name)) else {
            continue;
        };
        let Some(mut wall) = body(tile, page.page_number, index) else {
            continue;
        };
        let frame = Frame::new(&wall);
        if frame.length < 5. * wall.thickness {
            continue;
        }
        let mut cores = vec![];
        let mut finishes = vec![];
        for other in reviewed
            .iter()
            .copied()
            .filter(|other| coincident_extent(&frame, other))
        {
            let gauge = other.thickness / wall.thickness;
            if (gauge - 1.).abs() <= 3e-5 {
                if let Some(pair) = face_pair(other, &frame, layer, &sources, true) {
                    cores.push((other, pair));
                }
            } else if (1.08..=1.4).contains(&gauge)
                && let Some(pair) = face_pair(other, &Frame::new(other), layer, &sources, false)
            {
                finishes.push((other, pair));
            }
        }
        let ([(core, rails)], [(finish, trims)]) = (cores.as_slice(), finishes.as_slice()) else {
            continue;
        };
        if ready
            .iter()
            .any(|w| overlap::parallel_bodies_overlap(&wall, w))
            || linings
                .iter()
                .any(|w| overlap::parallel_bodies_overlap(&wall, w))
            || page
                .openings
                .iter()
                .any(|opening| overlap::aperture_crosses(&wall, opening))
        {
            continue;
        }
        wall.id = format!("{PREFIX}{}:{}", wall.page, index + 1);
        wall.source_ids = rails.iter().map(|id| (*id).to_owned()).collect();
        wall.source_ids.sort();
        let mut support_source_ids = wall.source_ids.clone();
        support_source_ids.extend(trims.iter().map(|id| (*id).to_owned()));
        support_source_ids.push(tile.id.clone());
        support_source_ids.sort();
        let mut support_wall_ids = vec![core.id.clone(), finish.id.clone()];
        support_wall_ids.sort();
        let decision = WallDecision {
            wall_id: wall.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::SourceBoundedInteriorCore,
            component_id: format!("p{}:{}", wall.page, wall.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids,
            support_source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        proposals.push((wall, decision));
    }
    let mut disputed = vec![false; proposals.len()];
    for (i, (wall, _)) in proposals.iter().enumerate() {
        for (j, (other, _)) in proposals.iter().enumerate().skip(i + 1) {
            if overlap::parallel_bodies_overlap(wall, other) {
                disputed[i] = true;
                disputed[j] = true;
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
