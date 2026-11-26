//! Project independently measured aligned stock and its optional unfilled seam.
use super::{
    WallDecision, WallReason, WallStatus, exterior_piers,
    raster_narrow_course::{append_rows, occupied, ready_wall, replay_rows},
};
use crate::{
    input::raster::{self, VerifiedRasterAlignedCourse},
    model::{BodyEvidence, PageScan, Point, Wall},
};
use std::borrow::Cow;

const PREFIX: &str = "raster-aligned-course:";
const DESCRIPTION: &str = "aligned raster course";
const REASON: WallReason = WallReason::SourceBoundedRasterAlignedCourse;

fn verified(page: &PageScan) -> Result<Vec<VerifiedRasterAlignedCourse>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    raster::verified_aligned_courses(info, &page.primitives, page.width, page.height)
}

fn previous<'a>(page: &'a PageScan, proof: &VerifiedRasterAlignedCourse) -> Option<&'a Wall> {
    ready_wall(
        page,
        (proof.previous_start, proof.previous_end, proof.thickness),
        WallReason::SourceBoundedRasterSectionCourse,
    )
}

fn observations(page: &PageScan) -> Result<Vec<(Wall, WallDecision)>, String> {
    let proofs = verified(page)?;
    if proofs.is_empty() {
        return Ok(vec![]);
    }
    let mut used = occupied(page);
    let mut result = Vec::new();
    for proof in proofs {
        let Some(parent) = previous(page, &proof) else {
            continue;
        };
        let mut predecessor = parent.id.clone();
        for p in std::iter::once(proof.first).chain(proof.second) {
            let outline = exterior_piers::corners(p.start, p.end, proof.thickness);
            let eps = 64. * f64::EPSILON * p.start.norm().max(p.end.norm()).max(1.);
            if used
                .iter()
                .any(|&q| exterior_piers::overlaps(outline, q, eps))
            {
                break;
            }
            let n = page.page_number;
            let ordinal = result.len() + 1;
            let id = format!("{PREFIX}{n}:{ordinal}");
            let wall = Wall {
                id: id.clone(),
                page: n,
                start: p.start,
                end: p.end,
                thickness: proof.thickness,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: p.source_ids.clone(),
                requires_review: false,
            };
            let decision = WallDecision {
                wall_id: id.clone(),
                status: WallStatus::Ready,
                reason: REASON,
                component_id: format!("p{n}:raster-aligned-course:{ordinal}"),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![predecessor],
                support_source_ids: p.source_ids,
                opening_ids: vec![],
                room_ids: vec![],
                placement_extent: None,
            };
            used.push(outline);
            result.push((wall, decision));
            predecessor = id;
        }
    }
    Ok(result)
}

pub(crate) fn append(page: &mut PageScan) -> Result<bool, String> {
    let rows = observations(page)?;
    append_rows(page, rows, DESCRIPTION)
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let receipt = page
        .pdf_input
        .as_ref()
        .is_some_and(|i| !i.raster_aligned_courses.is_empty());
    replay_rows(
        page,
        supported,
        receipt,
        PREFIX,
        DESCRIPTION,
        44,
        observations,
    )
}

pub(crate) struct AlignedContact {
    pub wall_ids: [String; 2],
    pub points: [Point; 2],
    pub source_ids: Vec<String>,
}

pub(crate) fn contacts(page: &PageScan) -> Result<Vec<AlignedContact>, String> {
    let proofs = verified(page)?;
    if proofs.is_empty() {
        return Ok(vec![]);
    }
    let used = occupied(page);
    let mut result = Vec::new();
    for p in proofs {
        if previous(page, &p).is_none() {
            continue;
        }
        let (Some(points), Some(second)) = (p.seam, p.second) else {
            continue;
        };
        let (Some(a), Some(b)) = (
            ready_wall(page, (p.first.start, p.first.end, p.thickness), REASON),
            ready_wall(page, (second.start, second.end, p.thickness), REASON),
        ) else {
            continue;
        };
        let outline = exterior_piers::corners(points[0], points[1], p.thickness);
        let eps = 64. * f64::EPSILON * points[0].norm().max(points[1].norm()).max(1.);
        if used
            .iter()
            .any(|&other| exterior_piers::overlaps(outline, other, eps))
        {
            continue;
        }
        let mut source_ids = p.first.source_ids;
        source_ids.extend(second.source_ids);
        source_ids.sort_unstable();
        source_ids.dedup();
        result.push(AlignedContact {
            wall_ids: [a.id.clone(), b.id.clone()],
            points,
            source_ids,
        });
    }
    Ok(result)
}
