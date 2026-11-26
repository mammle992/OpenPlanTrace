//! Export pixel-verified swings as review apertures until both hosts are ready.
use super::{WallStatus, effective_wall, exterior_piers};
use crate::{
    input::raster,
    model::{Opening, OpeningEvidence, OpeningKind, PageScan, Point, SwingObservation},
    semantics::{RoutingItem, RoutingKind, Span},
};
use std::{borrow::Cow, collections::HashSet};

const EVIDENCE: OpeningEvidence = OpeningEvidence::RasterFiniteHingedDoor;

fn openings(page: &PageScan) -> Result<Vec<Opening>, String> {
    let Some(input) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    let measured = raster::verified_hinged_doors(input, &page.primitives, page.width, page.height)?;
    if measured.is_empty() {
        return Ok(vec![]);
    }
    let mut occupied = exterior_piers::aperture_guard(page);
    for (wall, decision) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if decision.status != WallStatus::Ready {
            continue;
        }
        let wall = effective_wall(wall, Some(decision));
        if let Some(curve) = wall.curve {
            let r = curve.radius + wall.thickness * 0.5;
            let x = Point::new(r, 0.);
            occupied.push(exterior_piers::corners(
                curve.center - x,
                curve.center + x,
                2. * r,
            ));
        } else if wall.thickness > 0. && wall.start != wall.end {
            occupied.push(exterior_piers::corners(
                wall.start,
                wall.end,
                wall.thickness,
            ));
        }
    }
    let pixel =
        (page.width / input.render_width as f64).max(page.height / input.render_height as f64);
    let mut result = Vec::new();
    for proof in measured {
        let Some(source) = proof.source_ids.first() else {
            return Err("raster swing has no covering image source".into());
        };
        let body = exterior_piers::corners(proof.start, proof.end, proof.wall_thickness);
        let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
        if occupied
            .iter()
            .any(|&other| exterior_piers::overlaps(body, other, eps))
        {
            continue;
        }
        let axis = (proof.end - proof.start).unit();
        let toward_latch = if proof.hinge.distance(proof.start) <= proof.hinge.distance(proof.end) {
            axis
        } else {
            axis * -1.
        };
        let radius = proof.hinge.distance(proof.leaf_tip);
        let closed = proof.hinge + toward_latch * radius;
        let ordinal = result.len() + 1;
        result.push(Opening {
            id: format!("p{}:raster-hinged-door:{ordinal}", page.page_number),
            page: page.page_number,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: String::new(),
            host_wall_ids: vec![],
            start: proof.start,
            end: proof.end,
            hinge: Some(proof.hinge),
            swing_observations: vec![SwingObservation {
                arc_source_id: source.clone(),
                segment_source_ids: vec![],
                hinge: proof.hinge,
                closed_tip: closed,
                open_tip: proof.leaf_tip,
                leaf_tolerance: pixel,
                open_leaf_source_ids: vec![source.clone()],
                closed_leaf_source_ids: vec![],
            }],
            source_ids: proof.source_ids,
            evidence: EVIDENCE,
            requires_review: true,
            physical_jambs: None,
        });
    }
    Ok(result)
}

fn route(opening: &Opening) -> RoutingItem {
    RoutingItem {
        kind: RoutingKind::DoorPassageCandidate,
        entity_id: opening.id.clone(),
        source_ids: opening.source_ids.clone(),
        span: Some(Span {
            start: opening.start,
            end: opening.end,
        }),
        curve: None,
        room_ids: vec![],
        usable_for_routing: false,
        requires_review: true,
    }
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let measured = openings(page)?;
    page.semantics.routing.extend(measured.iter().map(route));
    page.openings.extend(measured);
    Ok(())
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let has_receipts = page
        .pdf_input
        .as_ref()
        .is_some_and(|i| !i.raster_hinged_doors.is_empty());
    let first = page
        .openings
        .iter()
        .position(|o| o.evidence == EVIDENCE)
        .unwrap_or(page.openings.len());
    let has_pane = page
        .window_panes
        .iter()
        .any(|p| p.observation.evidence == EVIDENCE);
    if !has_receipts && first == page.openings.len() && !has_pane {
        return Ok(Cow::Borrowed(page));
    }
    if !supported {
        return Err("raster hinged doors require scan v39 or newer".into());
    }
    if has_pane
        || page.openings[first..]
            .iter()
            .any(|o| o.evidence != EVIDENCE)
    {
        return Err("invalid raster hinged door inventory".into());
    }
    let ids: HashSet<_> = page.openings[first..]
        .iter()
        .map(|o| o.id.as_str())
        .collect();
    let saved_routes: Vec<_> = page
        .semantics
        .routing
        .iter()
        .filter(|r| ids.contains(r.entity_id.as_str()))
        .map(|r| serde_json::json!(r))
        .collect();
    let mut base = page.clone();
    base.openings.truncate(first);
    base.semantics
        .routing
        .retain(|r| !ids.contains(r.entity_id.as_str()));
    let measured = openings(&base)?;
    let routes: Vec<_> = measured
        .iter()
        .map(|o| serde_json::json!(route(o)))
        .collect();
    if measured != page.openings[first..] || routes != saved_routes {
        return Err("raster hinged doors disagree with source evidence".into());
    }
    if measured.is_empty() {
        Ok(Cow::Borrowed(page))
    } else {
        Ok(Cow::Owned(base))
    }
}
