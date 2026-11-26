//! Select the one existing physical core independently proved by a native PDF receipt.
use super::exterior_piers::{aperture_guard, beside_ready_wall, corners, overlaps};
use super::{WallDecision, WallReason, WallStatus, effective_wall, roundoff};
use crate::input::white_nested::Receipt;
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{HashMap, HashSet};

const WORK_LIMIT: usize = 5_000_000;

fn rail_with_role(primitive: Option<&&Primitive>, expected: LayerKind) -> bool {
    primitive.is_some_and(|p| {
        matches!(p.shape, Shape::Line { .. })
            && if p.layer_category_override.is_some() {
                p.layer_kind() == expected
            } else if expected == LayerKind::Wall {
                p.layer.as_deref().is_some_and(crate::layers::is_named_wall)
            } else {
                // The native receipt validator recognizes localized dimension
                // layer names and independently verifies all four box borders.
                true
            }
    })
}

fn receipt_active(receipt: &Receipt, source: &HashMap<&str, &Primitive>) -> bool {
    receipt.rails.iter().all(|id| {
        let p = source.get(id.as_str());
        rail_with_role(p, LayerKind::Wall)
            && p.is_some_and(|p| p.layer.as_deref() == Some(receipt.core.layer.as_str()))
    }) && receipt
        .returns
        .iter()
        .flatten()
        .all(|id| rail_with_role(source.get(id.as_str()), LayerKind::Wall))
        && receipt.dimensions.iter().all(|mask| {
            mask.borders.iter().all(|id| {
                let p = source.get(id.as_str());
                rail_with_role(p, LayerKind::Dimension)
                    && p.is_some_and(|p| p.layer.as_deref() == Some(mask.mask.layer.as_str()))
            })
        })
}

fn eligible(wall: &Wall, decision: &WallDecision) -> bool {
    decision.wall_id == wall.id
        && decision.status == WallStatus::Review
        && decision.reason == WallReason::OverlappingWallAxes
        && decision.opening_ids.is_empty()
        && decision.placement_extent.is_none()
        && wall.body == BodyEvidence::ParallelFaces
        && wall.curve.is_none()
        && wall.start.finite()
        && wall.end.finite()
        && wall.thickness.is_finite()
        && wall.thickness > 0.
}

fn same_core(wall: &Wall, [a, b]: [Point; 2], width: f64, receipt: &Receipt, eps: f64) -> bool {
    (wall.thickness - width).abs() <= eps
        && ((wall.start.distance(a) <= eps && wall.end.distance(b) <= eps)
            || (wall.start.distance(b) <= eps && wall.end.distance(a) <= eps))
        && receipt.rails[1..3]
            .iter()
            .all(|id| wall.source_ids.contains(id))
}

fn ready_bounds(wall: &Wall) -> Option<[Point; 4]> {
    if let Some(arc) = wall.curve {
        let radius = arc.radius + wall.thickness * 0.5;
        let h = Point::new(radius, 0.);
        return (radius > 0. && radius.is_finite() && arc.center.finite())
            .then(|| corners(arc.center - h, arc.center + h, radius * 2.));
    }
    (wall.thickness.is_finite()
        && wall.thickness > 0.
        && wall.start.finite()
        && wall.end.finite()
        && wall.start.distance(wall.end) > 0.)
        .then(|| corners(wall.start, wall.end, wall.thickness))
}

/// An unresolved quarter swing may still be a real opening. One of its two
/// radial legs must physically follow this finite wall lane to veto readiness.
fn unresolved_swing_crosses(wall: &Wall, primitive: &Primitive, eps: f64) -> bool {
    let Shape::Arc {
        center,
        radius,
        start_angle,
        sweep,
    } = primitive.shape
    else {
        return false;
    };
    if !center.finite()
        || !radius.is_finite()
        || radius <= wall.thickness
        || !start_angle.is_finite()
        || !sweep.is_finite()
        || (sweep.abs() - std::f64::consts::FRAC_PI_2).abs() > 0.16
    {
        return false;
    }
    let axis = (wall.end - wall.start).unit();
    let length = wall.start.distance(wall.end);
    let hinge = center - wall.start;
    let normal_limit = wall.thickness * 0.7 + eps;
    if hinge.cross(axis).abs() > normal_limit {
        return false;
    }
    [start_angle, start_angle + sweep].into_iter().any(|angle| {
        let radial = Point::new(angle.cos(), angle.sin()) * radius;
        if radial.cross(axis).abs() > 0.02 * radius
            || (hinge + radial).cross(axis).abs() > normal_limit
        {
            return false;
        }
        let a = hinge.dot(axis);
        let b = (hinge + radial).dot(axis);
        a.max(b).min(length) - a.min(b).max(0.) > eps
    })
}

struct Proposal<'a> {
    index: usize,
    core: [Point; 4],
    material: [[Point; 4]; 3],
    receipt: &'a Receipt,
}

fn propose<'a>(
    page: &PageScan,
    decisions: &[WallDecision],
    receipts: &'a [Receipt],
) -> Option<Vec<Proposal<'a>>> {
    let mut work = WORK_LIMIT
        .checked_sub(page.primitives.len())?
        .checked_sub(page.walls.len())?;
    let source: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let good = receipts
        .iter()
        .flat_map(|r| r.rails[1..3].iter().map(String::as_str))
        .collect::<HashSet<_>>();
    let hosts: HashSet<_> = page
        .opening_evidence()
        .flat_map(|o| o.host_wall_ids.iter().map(String::as_str))
        .collect();
    let signed_arcs: HashSet<_> = page
        .opening_evidence()
        .flat_map(|o| {
            o.swing_observations
                .iter()
                .map(|s| s.arc_source_id.as_str())
        })
        .collect();
    let unresolved: Vec<_> = page
        .primitives
        .iter()
        .filter(|p| {
            matches!(p.shape, Shape::Arc { .. })
                && !signed_arcs.contains(p.id.as_str())
                && (p.layer_category_override.is_none()
                    || matches!(p.layer_kind(), LayerKind::Wall | LayerKind::Door))
        })
        .collect();
    let occupied: Vec<_> = page
        .walls
        .iter()
        .zip(decisions)
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .filter_map(|(w, d)| ready_bounds(&effective_wall(w, Some(d))))
        .collect();
    let apertures = aperture_guard(page);
    let furnishings: Vec<_> = page
        .withheld_walls
        .iter()
        .flatten()
        .filter_map(|w| w.furniture_outline)
        .collect();
    let mut by_rail = HashMap::<&str, Vec<usize>>::new();
    for (i, (wall, decision)) in page.walls.iter().zip(decisions).enumerate() {
        if !eligible(wall, decision) || hosts.contains(wall.id.as_str()) {
            continue;
        }
        work = work.checked_sub(wall.source_ids.len())?;
        for id in wall
            .source_ids
            .iter()
            .map(String::as_str)
            .filter(|id| good.contains(id))
        {
            by_rail.entry(id).or_default().push(i);
        }
    }
    let mut result = Vec::new();
    for receipt in receipts {
        work = work.checked_sub(receipt.source_ids().count())?;
        if !receipt_active(receipt, &source) {
            continue;
        }
        let Some((ends, width)) = receipt.extent() else {
            continue;
        };
        if !width.is_finite() || width <= 0. || !ends.iter().all(|p| p.finite()) {
            continue;
        }
        let Some(indices) = by_rail.get(receipt.rails[1].as_str()) else {
            continue;
        };
        work = work.checked_sub(indices.len())?;
        let mut matching = indices.iter().copied().filter(|&i| {
            let w = &page.walls[i];
            same_core(w, ends, width, receipt, roundoff(w).max(width * 1e-6))
        });
        let Some(index) = matching.next() else {
            continue;
        };
        if matching.next().is_some() {
            continue;
        }
        let w = &page.walls[index];
        let eps = roundoff(w).max(width * 1e-6);
        let core = corners(w.start, w.end, w.thickness);
        let material = [core, receipt.facings[0].outline, receipt.facings[1].outline];
        if !material.into_iter().flatten().all(|p| p.finite()) {
            continue;
        }
        work = work.checked_sub(
            occupied
                .len()
                .saturating_add(apertures.len())
                .saturating_add(furnishings.len())
                .saturating_mul(3)
                .saturating_add(unresolved.len()),
        )?;
        let blocked = material.iter().any(|&body| {
            occupied
                .iter()
                .chain(&apertures)
                .chain(&furnishings)
                .any(|&other| overlaps(body, other, eps))
        }) || occupied.iter().any(|&b| beside_ready_wall(w, b))
            || unresolved
                .iter()
                .any(|p| unresolved_swing_crosses(w, p, eps));
        if blocked {
            continue;
        }
        result.push(Proposal {
            index,
            core,
            material,
            receipt,
        });
    }
    (result.len().saturating_pow(2).saturating_mul(9) <= work).then_some(result)
}

/// Invoke only for alpha 38's receipt-bearing replay, immediately before the
/// connection pass. The receipt geometry must already have been validated.
pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision], receipts: &[Receipt]) {
    if receipts.is_empty() || decisions.len() != page.walls.len() {
        return;
    }
    let Some(proposals) = propose(page, decisions, receipts) else {
        return;
    };
    for (i, p) in proposals.iter().enumerate() {
        if proposals.iter().enumerate().any(|(j, q)| {
            i != j
                && (p.index == q.index
                    || p.material
                        .iter()
                        .any(|&a| q.material.iter().any(|&b| overlaps(a, b, 1e-7)))
                    || beside_ready_wall(&page.walls[p.index], q.core))
        }) {
            continue;
        }
        let d = &mut decisions[p.index];
        let mut support = p
            .receipt
            .source_ids()
            .map(str::to_owned)
            .collect::<Vec<_>>();
        support.sort_unstable();
        support.dedup();
        d.support_source_ids = support;
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedWhiteNestedWall;
    }
}
