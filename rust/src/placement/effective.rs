//! Separate immutable observations from replayed physical cut/placement views.
use super::WallDecision;
use crate::model::{PageScan, Wall};
use std::{
    borrow::Cow,
    collections::{HashMap, HashSet},
};

/// Use only with a source-validated decision. The original observation is retained.
pub fn effective_wall<'a>(wall: &'a Wall, decision: Option<&WallDecision>) -> Cow<'a, Wall> {
    if let Some(extent) = decision.and_then(|d| d.placement_extent.as_ref()) {
        let mut measured = wall.clone();
        measured.start = extent.start;
        measured.end = extent.end;
        Cow::Owned(measured)
    } else {
        Cow::Borrowed(wall)
    }
}

pub(crate) fn effective_page(page: &PageScan) -> Cow<'_, PageScan> {
    if !page
        .placement_decisions
        .iter()
        .flatten()
        .any(|d| d.placement_extent.is_some())
    {
        return Cow::Borrowed(page);
    }
    let decisions: HashMap<_, _> = page
        .placement_decisions
        .iter()
        .flatten()
        .map(|d| (d.wall_id.as_str(), d))
        .collect();
    let mut measured = page.clone();
    for wall in &mut measured.walls {
        if let Some(extent) = decisions
            .get(wall.id.as_str())
            .and_then(|d| d.placement_extent.as_ref())
        {
            wall.start = extent.start;
            wall.end = extent.end;
        }
    }
    Cow::Owned(measured)
}

/// Frozen stages retain the source detector's aperture while physical placement
/// consumes the independently measured jamb cut.
pub(crate) fn observation_page(page: &PageScan) -> Cow<'_, PageScan> {
    if !page.openings.iter().any(|o| o.physical_jambs.is_some()) {
        return Cow::Borrowed(page);
    }
    let mut observed = page.clone();
    for opening in &mut observed.openings {
        if let Some(proof) = opening.physical_jambs.take() {
            opening.start = proof.original_start;
            opening.end = proof.original_end;
            for route in &mut observed.semantics.routing {
                if route.entity_id == opening.id
                    && route.kind == crate::semantics::RoutingKind::DoorPassageCandidate
                {
                    if let Some(span) = route.span.as_mut() {
                        span.start = opening.start;
                        span.end = opening.end;
                    }
                }
            }
        }
    }
    Cow::Owned(observed)
}

/// A bounded host cannot authorize corner material outside its explicit extent.
pub(crate) fn corner_joints(page: &PageScan) -> Vec<super::stroked::CornerJoint> {
    let bounded: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.placement_extent.is_some())
        .map(|d| d.wall_id.as_str())
        .collect();
    super::stroked::corner_joints(page)
        .into_iter()
        .filter(|j| j.wall_ids.iter().all(|id| !bounded.contains(id.as_str())))
        .collect()
}
