//! Promote existing exact raster stock once both original shoulders are verified.
use super::{WallDecision, WallReason, WallStatus, exterior_piers, raster_narrow_course::occupied};
use crate::{
    input::raster::{self, VerifiedRasterJambReturn},
    model::{PageScan, Point, Wall},
};
use std::borrow::Cow;

const REASON: WallReason = WallReason::SourceBoundedRasterJambReturn;

fn verified(page: &PageScan) -> Result<Vec<VerifiedRasterJambReturn>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    raster::verified_jamb_returns(info, &page.primitives, page.width, page.height)
}

fn matching_wall(
    page: &PageScan,
    geometry: (Point, Point, f64),
    faces: Option<&[String; 2]>,
    status: WallStatus,
    reason: WallReason,
) -> Option<usize> {
    let mut found = page
        .walls
        .iter()
        .zip(page.placement_decisions.as_deref()?)
        .enumerate()
        .filter_map(|(i, (w, d))| {
            (d.status == status
                && d.reason == reason
                && d.placement_extent.is_none()
                && w.curve.is_none()
                && (w.start, w.end, w.thickness) == geometry
                && faces.is_none_or(|pair| {
                    w.source_ids.len() == 2 && pair.iter().all(|id| w.source_ids.contains(id))
                }))
            .then_some(i)
        });
    let index = found.next()?;
    found.next().is_none().then_some(index)
}

fn hosts(page: &PageScan, p: &VerifiedRasterJambReturn) -> Option<[usize; 2]> {
    Some([
        matching_wall(
            page,
            (p.incoming_start, p.incoming_end, p.incoming_thickness),
            Some(&p.incoming_face_ids),
            WallStatus::Ready,
            WallReason::SourceBoundedRasterPartition,
        )?,
        matching_wall(
            page,
            (p.jamb_start, p.jamb_end, p.jamb_thickness),
            None,
            WallStatus::Ready,
            WallReason::SourceBoundedRasterJambStock,
        )?,
    ])
}

fn body(
    page: &PageScan,
    p: &VerifiedRasterJambReturn,
    status: WallStatus,
    reason: WallReason,
) -> Option<usize> {
    matching_wall(
        page,
        (p.start, p.end, p.thickness),
        Some(&p.raw_face_ids),
        status,
        reason,
    )
}

fn outline(w: &Wall) -> [Point; 4] {
    exterior_piers::corners(w.start, w.end, w.thickness)
}

fn unobstructed(
    candidate: &[Point; 4],
    old: &[[Point; 4]],
    hosts: &[[Point; 4]; 2],
    proof: &VerifiedRasterJambReturn,
) -> bool {
    let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
    let paths = [&proof.incoming_path, &proof.jamb_path];
    // Only the two verified hosts may intersect the source-contact path. All
    // retained apertures, furniture footprints and unrelated walls still veto.
    old.iter().all(|&other| {
        !exterior_piers::overlaps(*candidate, other, eps)
            && (hosts.contains(&other)
                || paths.iter().all(|p| {
                    p.windows(2).all(|s| {
                        !exterior_piers::overlaps(
                            exterior_piers::corners(s[0], s[1], eps * 8.),
                            other,
                            eps,
                        )
                    })
                }))
    })
}

pub(crate) fn apply(page: &mut PageScan) -> Result<bool, String> {
    let proofs = verified(page)?;
    if proofs.is_empty() {
        return Ok(false);
    }
    let mut used = occupied(page);
    let mut changed = false;
    for proof in proofs {
        let Some(hosts) = hosts(page, &proof) else {
            continue;
        };
        let Some(index) = body(
            page,
            &proof,
            WallStatus::Review,
            WallReason::RasterFacesNeedReview,
        ) else {
            continue;
        };
        let candidate = outline(&page.walls[index]);
        if !unobstructed(
            &candidate,
            &used,
            &hosts.map(|i| outline(&page.walls[i])),
            &proof,
        ) {
            continue;
        }
        let mut host_ids = hosts.map(|i| page.walls[i].id.clone()).to_vec();
        host_ids.sort_unstable();
        let decision = page
            .placement_decisions
            .as_mut()
            .and_then(|d| d.get_mut(index))
            .ok_or("missing raster jamb return decision")?;
        if !eligible(decision) {
            continue;
        }
        decision.status = WallStatus::Ready;
        decision.reason = REASON;
        decision.support_wall_ids = host_ids;
        decision.support_source_ids = proof.source_ids;
        used.push(candidate);
        changed = true;
    }
    Ok(changed)
}

fn eligible(decision: &WallDecision) -> bool {
    decision.status == WallStatus::Review
        && decision.reason == WallReason::RasterFacesNeedReview
        && decision.support_wall_ids.is_empty()
        && decision.support_source_ids.is_empty()
        && decision.opening_ids.is_empty()
        && decision.placement_extent.is_none()
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let observed = page
        .placement_decisions
        .iter()
        .flatten()
        .any(|d| d.reason == REASON);
    let evidence = page
        .pdf_input
        .as_ref()
        .is_some_and(|i| !i.raster_jamb_returns.is_empty());
    if !observed && !evidence {
        return Ok(Cow::Borrowed(page));
    }
    if !supported {
        return Err("raster jamb returns require scan v43 or newer".into());
    }
    let mut previous = page.clone();
    for d in previous
        .placement_decisions
        .iter_mut()
        .flatten()
        .filter(|d| d.reason == REASON)
    {
        d.status = WallStatus::Review;
        d.reason = WallReason::RasterFacesNeedReview;
        d.support_wall_ids.clear();
        d.support_source_ids.clear();
    }
    let mut replay = previous.clone();
    let reapplied = apply(&mut replay)?;
    if reapplied != observed || replay.placement_decisions != page.placement_decisions {
        return Err("raster jamb return decisions disagree with the measured shoulders".into());
    }
    if observed {
        Ok(Cow::Owned(previous))
    } else {
        Ok(Cow::Borrowed(page))
    }
}

pub(crate) struct ReturnContact {
    pub wall_id: String,
    pub host_wall_id: String,
    pub points: Vec<Point>,
    pub source_ids: Vec<String>,
}

pub(crate) fn contacts(page: &PageScan) -> Result<Vec<ReturnContact>, String> {
    let mut result = vec![];
    for p in verified(page)? {
        let Some(hosts) = hosts(page, &p) else {
            continue;
        };
        let Some(index) = body(page, &p, WallStatus::Ready, REASON) else {
            continue;
        };
        let wall_id = &page.walls[index].id;
        for (host, points) in hosts.into_iter().zip([p.incoming_path, p.jamb_path]) {
            result.push(ReturnContact {
                wall_id: wall_id.clone(),
                host_wall_id: page.walls[host].id.clone(),
                points,
                source_ids: p.source_ids.clone(),
            });
        }
    }
    Ok(result)
}
