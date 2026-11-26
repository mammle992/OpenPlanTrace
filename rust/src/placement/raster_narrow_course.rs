//! Project a separately sampled narrow raster body at a verified jamb return.
use super::{WallDecision, WallReason, WallStatus, effective_wall, exterior_piers};
use crate::{
    input::raster::{self, VerifiedRasterNarrowCourse},
    model::{BodyEvidence, PageScan, Point, Wall},
    semantics::wall_route,
};
use std::{borrow::Cow, collections::HashSet};
mod section;
pub(crate) use section::{append_section_courses, before_section_replay, section_contacts};

const PREFIX: &str = "raster-narrow-course:";
const REASON: WallReason = WallReason::SourceBoundedRasterNarrowCourse;

fn ready_wall(page: &PageScan, geometry: (Point, Point, f64), reason: WallReason) -> Option<&Wall> {
    let mut found = page
        .walls
        .iter()
        .zip(page.placement_decisions.as_deref()?)
        .filter_map(|(w, d)| {
            (d.status == WallStatus::Ready
                && d.reason == reason
                && d.placement_extent.is_none()
                && w.curve.is_none()
                && (w.start, w.end, w.thickness) == geometry)
                .then_some(w)
        });
    let wall = found.next()?;
    found.next().is_none().then_some(wall)
}

fn jamb<'a>(page: &'a PageScan, proof: &VerifiedRasterNarrowCourse) -> Option<&'a Wall> {
    ready_wall(
        page,
        (proof.jamb_start, proof.jamb_end, proof.jamb_thickness),
        WallReason::SourceBoundedRasterJambStock,
    )
}

fn verified(page: &PageScan) -> Result<Vec<VerifiedRasterNarrowCourse>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    raster::verified_narrow_courses(info, &page.primitives, page.width, page.height)
}

pub(super) fn occupied(page: &PageScan) -> Vec<[Point; 4]> {
    let mut footprints = exterior_piers::aperture_guard(page);
    footprints.extend(
        page.walls
            .iter()
            .zip(page.placement_decisions.iter().flatten())
            .filter(|(_, d)| d.status == WallStatus::Ready)
            .map(|(wall, decision)| {
                let w = effective_wall(wall, Some(decision));
                if let Some(curve) = w.curve {
                    let radius = curve.radius + w.thickness * 0.5;
                    let offset = Point::new(radius, 0.);
                    exterior_piers::corners(
                        curve.center - offset,
                        curve.center + offset,
                        2. * radius,
                    )
                } else {
                    exterior_piers::corners(w.start, w.end, w.thickness)
                }
            }),
    );
    let objects = page
        .objects
        .iter()
        .filter_map(|o| o.footprint.as_ref())
        .map(|f| f.polygon.as_slice());
    let withheld = page
        .withheld_walls
        .iter()
        .flatten()
        .filter_map(|w| w.furniture_outline.as_ref())
        .map(<[Point; 4]>::as_slice);
    for poly in objects.chain(withheld) {
        let [mut x, mut y, mut u, mut v] = [
            f64::INFINITY,
            f64::INFINITY,
            f64::NEG_INFINITY,
            f64::NEG_INFINITY,
        ];
        for p in poly {
            x = x.min(p.x);
            y = y.min(p.y);
            u = u.max(p.x);
            v = v.max(p.y);
        }
        if x < u && y < v {
            footprints.push([
                Point::new(x, y),
                Point::new(u, y),
                Point::new(u, v),
                Point::new(x, v),
            ]);
        }
    }
    footprints
}

fn observations(page: &PageScan) -> Result<Vec<(Wall, WallDecision)>, String> {
    let proofs = verified(page)?;
    if proofs.is_empty() {
        return Ok(vec![]);
    }
    let mut used = occupied(page);
    let mut result = Vec::new();
    for proof in proofs {
        let Some(jamb) = jamb(page, &proof) else {
            continue;
        };
        let shape = exterior_piers::corners(proof.start, proof.end, proof.thickness);
        let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
        if used
            .iter()
            .any(|&other| exterior_piers::overlaps(shape, other, eps))
        {
            continue;
        }
        let number = page.page_number;
        let ordinal = result.len() + 1;
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
            requires_review: false,
        };
        let decision = WallDecision {
            wall_id: id,
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{number}:raster-narrow-course:{ordinal}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![jamb.id.clone()],
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

pub(crate) fn append(page: &mut PageScan) -> Result<bool, String> {
    let measured = observations(page)?;
    append_rows(page, measured, "raster narrow course")
}

fn append_rows(
    page: &mut PageScan,
    measured: Vec<(Wall, WallDecision)>,
    description: &str,
) -> Result<bool, String> {
    if measured.is_empty() {
        return Ok(false);
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or_else(|| format!("missing {description} decisions"))?;
    for (wall, decision) in measured {
        page.semantics.routing.push(wall_route(&wall));
        page.walls.push(wall);
        decisions.push(decision);
    }
    Ok(true)
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let has_proof = page
        .pdf_input
        .as_ref()
        .is_some_and(|info| !info.raster_narrow_courses.is_empty());
    replay_rows(
        page,
        supported,
        has_proof,
        PREFIX,
        "raster narrow course",
        41,
        observations,
    )
}

fn replay_rows<'a>(
    page: &'a PageScan,
    supported: bool,
    has_proof: bool,
    prefix: &str,
    description: &str,
    revision: u32,
    observe: impl Fn(&PageScan) -> Result<Vec<(Wall, WallDecision)>, String>,
) -> Result<Cow<'a, PageScan>, String> {
    let first = page
        .walls
        .iter()
        .position(|w| w.id.starts_with(prefix))
        .unwrap_or(page.walls.len());
    if !has_proof && first == page.walls.len() {
        return Ok(Cow::Borrowed(page));
    }
    if !supported {
        return Err(format!("{description}s require scan v{revision} or newer"));
    }
    if first == page.walls.len() {
        if !observe(page)?.is_empty() {
            return Err(format!("missing measured {description}"));
        }
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    let ids: HashSet<_> = page.walls[first..].iter().map(|w| w.id.as_str()).collect();
    base.semantics
        .routing
        .retain(|r| !ids.contains(r.entity_id.as_str()));
    base.walls.truncate(first);
    base.placement_decisions
        .as_mut()
        .ok_or_else(|| format!("missing {description} decisions"))?
        .truncate(first);
    let measured = observe(&base)?;
    let decisions = page
        .placement_decisions
        .as_deref()
        .and_then(|d| d.get(first..));
    let routes = page
        .semantics
        .routing
        .iter()
        .filter(|r| ids.contains(r.entity_id.as_str()));
    if !page.walls[first..]
        .iter()
        .eq(measured.iter().map(|(w, _)| w))
        || !decisions.is_some_and(|ds| ds.iter().eq(measured.iter().map(|(_, d)| d)))
        || !routes.map(|r| serde_json::json!(r)).eq(measured
            .iter()
            .map(|(w, _)| serde_json::json!(wall_route(w))))
    {
        return Err(format!(
            "{description} disagrees with original measured material"
        ));
    }
    Ok(Cow::Owned(base))
}

pub(crate) struct NarrowContact {
    pub wall_id: String,
    pub jamb_id: String,
    pub junction_end: Point,
    pub jamb_far_end: Point,
    pub contact: Point,
    pub source_ids: Vec<String>,
}

pub(crate) fn contacts(page: &PageScan) -> Result<Vec<NarrowContact>, String> {
    let mut result = Vec::new();
    for proof in verified(page)? {
        let Some(post) = jamb(page, &proof) else {
            continue;
        };
        let Some(wall) = ready_wall(page, (proof.start, proof.end, proof.thickness), REASON) else {
            continue;
        };
        result.push(NarrowContact {
            wall_id: wall.id.clone(),
            jamb_id: post.id.clone(),
            junction_end: proof.junction_end,
            jamb_far_end: proof.jamb_far_end,
            contact: proof.contact,
            source_ids: proof.source_ids,
        });
    }
    Ok(result)
}
