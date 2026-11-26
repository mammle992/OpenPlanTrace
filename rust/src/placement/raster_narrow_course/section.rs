//! Keep a verified section overprint separate from either physical wall body.
use super::{
    BodyEvidence, Cow, PageScan, Point, Wall, WallDecision, WallReason, WallStatus, append_rows,
    exterior_piers, occupied, ready_wall, replay_rows,
};
use crate::input::raster::{self, VerifiedRasterSectionCourse};

const PREFIX: &str = "raster-section-course:";
const DESCRIPTION: &str = "raster section course";
const REASON: WallReason = WallReason::SourceBoundedRasterSectionCourse;

fn verified(page: &PageScan) -> Result<Vec<VerifiedRasterSectionCourse>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    raster::verified_section_courses(info, &page.primitives, page.width, page.height)
}

fn previous<'a>(page: &'a PageScan, proof: &VerifiedRasterSectionCourse) -> Option<&'a Wall> {
    ready_wall(
        page,
        (proof.previous_start, proof.previous_end, proof.thickness),
        WallReason::SourceBoundedRasterNarrowCourse,
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
        let Some(previous) = previous(page, &proof) else {
            continue;
        };
        let [pa, pb] = proof.junction;
        let shape = exterior_piers::corners(proof.start, proof.end, proof.thickness);
        let interval = exterior_piers::corners(pa, pb, proof.thickness);
        let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
        if used.iter().any(|&p| {
            exterior_piers::overlaps(shape, p, eps) || exterior_piers::overlaps(interval, p, eps)
        }) {
            continue;
        }
        let n = page.page_number;
        let ordinal = result.len() + 1;
        let id = format!("{PREFIX}{n}:{ordinal}");
        let wall = Wall {
            id: id.clone(),
            page: n,
            start: proof.start,
            end: proof.end,
            thickness: proof.thickness,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: proof.source_ids.clone(),
            requires_review: false,
        };
        let decision = WallDecision {
            wall_id: id,
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{n}:raster-section-course:{ordinal}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![previous.id.clone()],
            support_source_ids: proof.source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        used.push(shape);
        result.push((wall, decision));
    }
    Ok(result)
}

pub(crate) fn append_section_courses(page: &mut PageScan) -> Result<bool, String> {
    let measured = observations(page)?;
    append_rows(page, measured, DESCRIPTION)
}

pub(crate) fn before_section_replay(
    page: &PageScan,
    supported: bool,
) -> Result<Cow<'_, PageScan>, String> {
    let has_proof = page
        .pdf_input
        .as_ref()
        .is_some_and(|i| !i.raster_section_courses.is_empty());
    replay_rows(
        page,
        supported,
        has_proof,
        PREFIX,
        DESCRIPTION,
        42,
        observations,
    )
}

pub(crate) struct SectionContact {
    pub previous_wall_id: String,
    pub wall_id: String,
    pub previous_end: Point,
    pub new_end: Point,
    pub source_ids: Vec<String>,
}

pub(crate) fn section_contacts(page: &PageScan) -> Result<Vec<SectionContact>, String> {
    let mut contacts = vec![];
    for p in verified(page)? {
        let Some(previous) = previous(page, &p) else {
            continue;
        };
        let Some(new) = ready_wall(page, (p.start, p.end, p.thickness), REASON) else {
            continue;
        };
        let [previous_end, new_end] = p.junction;
        contacts.push(SectionContact {
            previous_wall_id: previous.id.clone(),
            wall_id: new.id.clone(),
            previous_end,
            new_end,
            source_ids: p.source_ids,
        });
    }
    Ok(contacts)
}
