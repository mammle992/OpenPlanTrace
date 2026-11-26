//! Append measured raster observations after physical placement and graph construction.
use super::{WallDecision, WallReason, WallStatus, exterior_piers};
use crate::input::raster;
use crate::model::{BodyEvidence, PageScan, Wall};
use std::borrow::Cow;

const PREFIX: &str = "raster-face-continuation:";

fn observations(page: &PageScan) -> Result<(Vec<Wall>, Vec<WallDecision>), String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok((vec![], vec![]));
    };
    let verified =
        raster::verified_face_continuations(info, &page.primitives, page.width, page.height)?;
    if verified.is_empty() {
        return Ok((vec![], vec![]));
    }
    let apertures = exterior_piers::aperture_guard(page);
    Ok(verified
        .into_iter()
        .filter(|proof| {
            let outline = exterior_piers::corners(proof.start, proof.end, proof.thickness);
            let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
            !apertures
                .iter()
                .any(|&aperture| exterior_piers::overlaps(outline, aperture, eps))
        })
        .enumerate()
        .map(|(i, proof)| {
            let ordinal = i + 1;
            let number = page.page_number;
            let id = format!("{PREFIX}{number}:{ordinal}");
            let wall = Wall {
                id: id.clone(),
                page: number,
                start: proof.start,
                end: proof.end,
                thickness: proof.thickness,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: proof.source_ids.clone(),
                requires_review: true,
            };
            let decision = WallDecision {
                wall_id: id,
                status: WallStatus::Review,
                reason: WallReason::SourceBoundedRasterFaceContinuation,
                component_id: format!("p{number}:raster-continuation:{ordinal}"),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![],
                support_source_ids: proof.source_ids,
                opening_ids: vec![],
                room_ids: vec![],
                placement_extent: None,
            };
            (wall, decision)
        })
        .unzip())
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let (walls, decisions) = observations(page)?;
    if walls.is_empty() {
        return Ok(());
    }
    if page.walls.iter().any(|wall| wall.id.starts_with(PREFIX)) {
        return Err("duplicate raster face continuation projection".into());
    }
    page.placement_decisions
        .as_mut()
        .ok_or("missing wall placement decisions")?
        .extend(decisions);
    page.walls.extend(walls);
    Ok(())
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let first = page
        .walls
        .iter()
        .position(|wall| wall.id.starts_with(PREFIX))
        .unwrap_or(page.walls.len());
    let receipts = page
        .pdf_input
        .as_ref()
        .is_some_and(|input| !input.raster_face_continuations.is_empty());
    if !receipts && first == page.walls.len() {
        return Ok(Cow::Borrowed(page));
    }
    if !supported {
        return Err("raster face continuations require scan v38 or newer".into());
    }
    let (walls, decisions) = observations(page)?;
    let real = &page.walls[first..];
    let saved_decisions = page
        .placement_decisions
        .as_deref()
        .and_then(|all| all.get(first..))
        .ok_or("missing raster face continuation decisions")?;
    if real.len() != walls.len()
        || saved_decisions != decisions
        || real.iter().zip(&walls).any(|(actual, expected)| {
            actual.id != expected.id
                || actual.page != expected.page
                || actual.start != expected.start
                || actual.end != expected.end
                || actual.thickness != expected.thickness
                || actual.body != expected.body
                || actual.curve.is_some()
                || actual.source_ids != expected.source_ids
                || !actual.requires_review
        })
    {
        return Err(
            "raster face continuations disagree with original measured observations".into(),
        );
    }
    if walls.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    base.walls.truncate(first);
    if let Some(decisions) = base.placement_decisions.as_mut() {
        decisions.truncate(first);
    }
    Ok(Cow::Owned(base))
}
