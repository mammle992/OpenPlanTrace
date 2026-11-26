//! Project the measured storage-wall upright and its two seats at existing walls.
use super::{
    WallDecision, WallReason, WallStatus, exterior_piers,
    raster_narrow_course::{append_rows, occupied, occupied_except, ready_wall, replay_rows},
};
use crate::{
    input::raster::{self, VerifiedRasterCornerCourse},
    model::{BodyEvidence, PageScan, Point, Wall},
};
use std::borrow::Cow;

const PREFIX: &str = "raster-corner-course:";
const DESCRIPTION: &str = "raster corner course";
const REASON: WallReason = WallReason::SourceBoundedRasterCornerCourse;

fn verified(page: &PageScan) -> Result<Vec<VerifiedRasterCornerCourse>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    raster::verified_corner_courses(info, &page.primitives, page.width, page.height)
}

fn hosts<'a>(page: &'a PageScan, p: &VerifiedRasterCornerCourse) -> Option<[&'a Wall; 2]> {
    let incoming = ready_wall(
        page,
        (p.incoming_start, p.incoming_end, p.incoming_thickness),
        WallReason::SourceBoundedRasterCourse,
    )?;
    if incoming.source_ids != p.incoming_sources {
        return None;
    }
    let terminal = ready_wall(
        page,
        (p.terminal_start, p.terminal_end, p.terminal_thickness),
        WallReason::SourceBoundedRasterAlignedCourse,
    )?;
    Some([incoming, terminal])
}

fn path_clear(page: &PageScan, path: &[Point], host: &str, upright: &str) -> bool {
    if path.len() < 2 || path.iter().any(|p| !p.finite()) {
        return false;
    }
    let margin = 64. * f64::EPSILON * page.width.max(page.height).max(1.);
    let occupied = occupied_except(page, &[host, upright]);
    path.windows(2).all(|p| {
        if p[0].distance(p[1]) <= margin {
            return true;
        }
        // The connection is a zero-width route. A numerical-width band tests
        // its incidence without granting the sample any material authority.
        let footprint = exterior_piers::corners(p[0], p[1], 8. * margin);
        !occupied
            .iter()
            .any(|&other| exterior_piers::overlaps(footprint, other, margin))
    })
}

fn seats_clear(
    page: &PageScan,
    p: &VerifiedRasterCornerCourse,
    anchors: [&Wall; 2],
    upright: &str,
) -> bool {
    [&p.north_path, &p.corner_path]
        .into_iter()
        .zip(anchors)
        .all(|(path, host)| path_clear(page, path, &host.id, upright))
}

fn observations(page: &PageScan) -> Result<Vec<(Wall, WallDecision)>, String> {
    let proofs = verified(page)?;
    if proofs.is_empty() {
        return Ok(vec![]);
    }
    let mut existing = occupied(page);
    let mut rows = Vec::new();
    for proof in proofs {
        let Some(anchors) = hosts(page, &proof) else {
            continue;
        };
        let footprint = exterior_piers::corners(proof.start, proof.end, proof.thickness);
        let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
        if existing
            .iter()
            .any(|&q| exterior_piers::overlaps(footprint, q, eps))
            || !seats_clear(page, &proof, anchors, "")
        {
            continue;
        }
        let number = page.page_number;
        let ordinal = rows.len() + 1;
        let id = format!("{PREFIX}{number}:{ordinal}");
        let mut parents = anchors.map(|w| w.id.clone());
        parents.sort_unstable();
        let wall = Wall {
            id: id.clone(),
            page: number,
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
            component_id: format!("p{number}:raster-corner-course:{ordinal}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: parents.to_vec(),
            support_source_ids: proof.source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        existing.push(footprint);
        rows.push((wall, decision));
    }
    Ok(rows)
}

pub(crate) fn append(page: &mut PageScan) -> Result<bool, String> {
    let rows = observations(page)?;
    append_rows(page, rows, DESCRIPTION)
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let receipt = page
        .pdf_input
        .as_ref()
        .is_some_and(|i| !i.raster_corner_courses.is_empty());
    replay_rows(
        page,
        supported,
        receipt,
        PREFIX,
        DESCRIPTION,
        45,
        observations,
    )
}

pub(crate) struct Seat {
    pub wall_ids: [String; 2],
    pub path: Vec<Point>,
    pub source_ids: Vec<String>,
}

pub(crate) fn contacts(page: &PageScan) -> Result<Vec<Seat>, String> {
    let mut result = Vec::new();
    for p in verified(page)? {
        let Some(anchors) = hosts(page, &p) else {
            continue;
        };
        let Some(wall) = ready_wall(page, (p.start, p.end, p.thickness), REASON) else {
            continue;
        };
        if !seats_clear(page, &p, anchors, &wall.id) {
            continue;
        }
        for (host, path) in anchors.into_iter().zip([p.north_path, p.corner_path]) {
            result.push(Seat {
                wall_ids: [host.id.clone(), wall.id.clone()],
                path,
                source_ids: p.source_ids.clone(),
            });
        }
    }
    Ok(result)
}
