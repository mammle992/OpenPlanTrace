//! Finite raster material outside the rough cuts of measured hinged openings.
use super::{WallDecision, WallReason, WallStatus, effective_wall, exterior_piers};
use crate::input::raster;
use crate::model::{BodyEvidence, OpeningEvidence, PageScan, Point, Wall};
use crate::semantics::wall_route;
use std::{borrow::Cow, collections::HashSet};

const PREFIX: &str = "raster-jamb-stock:";
const REASON: WallReason = WallReason::SourceBoundedRasterJambStock;

fn observations(page: &PageScan) -> Result<Vec<(Wall, WallDecision)>, String> {
    let Some(info) = page.pdf_input.as_ref() else {
        return Ok(vec![]);
    };
    if info.raster_jamb_stocks.is_empty() {
        return Ok(vec![]);
    }
    let verified = raster::verified_jamb_stocks(info, &page.primitives, page.width, page.height)?;
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
            let d = Point::new(r, 0.);
            occupied.push(exterior_piers::corners(
                curve.center - d,
                curve.center + d,
                2. * r,
            ));
        } else {
            occupied.push(exterior_piers::corners(
                wall.start,
                wall.end,
                wall.thickness,
            ));
        }
    }
    for points in page
        .objects
        .iter()
        .filter_map(|o| o.footprint.as_ref())
        .map(|f| f.polygon.as_slice())
        .chain(
            page.withheld_walls
                .iter()
                .flatten()
                .filter_map(|w| w.furniture_outline.as_ref())
                .map(<[Point; 4]>::as_slice),
        )
    {
        let mut x = [f64::INFINITY, f64::NEG_INFINITY];
        let mut y = x;
        for p in points {
            x = [x[0].min(p.x), x[1].max(p.x)];
            y = [y[0].min(p.y), y[1].max(p.y)];
        }
        if x[0] < x[1] && y[0] < y[1] {
            occupied.push([
                Point::new(x[0], y[0]),
                Point::new(x[1], y[0]),
                Point::new(x[1], y[1]),
                Point::new(x[0], y[1]),
            ]);
        }
    }
    let mut result = Vec::new();
    for proof in verified {
        let mut openings = page.openings.iter().filter(|o| {
            o.evidence == OpeningEvidence::RasterFiniteHingedDoor
                && [o.start, o.end] == proof.threshold
        });
        let Some(opening) = openings.next() else {
            continue;
        };
        if openings.next().is_some() {
            continue;
        }
        let outline = exterior_piers::corners(proof.start, proof.end, proof.thickness);
        let eps = 64. * f64::EPSILON * proof.start.norm().max(proof.end.norm()).max(1.);
        let [jamb, other] = if proof.at_high_end {
            [opening.end, opening.start]
        } else {
            [opening.start, opening.end]
        };
        let away = (jamb - other).unit();
        let stations = [proof.start, proof.end].map(|p| (p - jamb).dot(away));
        if stations[0].min(stations[1]).abs() > eps
            || (proof.start - jamb).cross(away).abs() > proof.thickness * 0.5 + eps
        {
            continue;
        }
        if occupied
            .iter()
            .any(|&other| exterior_piers::overlaps(outline, other, eps))
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
            component_id: format!("p{number}:raster-jamb-stock:{ordinal}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: proof.source_ids,
            opening_ids: vec![opening.id.clone()],
            room_ids: vec![],
            placement_extent: None,
        };
        occupied.push(outline);
        result.push((wall, decision));
    }
    Ok(result)
}

pub(crate) fn append(page: &mut PageScan) -> Result<bool, String> {
    let measured = observations(page)?;
    if measured.is_empty() {
        return Ok(false);
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("missing raster jamb decisions")?;
    for (wall, decision) in measured {
        page.semantics.routing.push(wall_route(&wall));
        page.walls.push(wall);
        decisions.push(decision);
    }
    Ok(true)
}

pub(crate) fn before_replay(page: &PageScan, supported: bool) -> Result<Cow<'_, PageScan>, String> {
    let first = page
        .walls
        .iter()
        .position(|w| w.id.starts_with(PREFIX))
        .unwrap_or(page.walls.len());
    let has_proof = page
        .pdf_input
        .as_ref()
        .is_some_and(|info| !info.raster_jamb_stocks.is_empty());
    let has_rows = first < page.walls.len();
    if !has_proof && !has_rows {
        return Ok(Cow::Borrowed(page));
    }
    if !supported {
        return Err("raster jamb stock requires scan v40 or newer".into());
    }
    if !has_rows {
        if !observations(page)?.is_empty() {
            return Err("missing measured raster jamb stock".into());
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
        .ok_or("missing raster jamb decisions")?
        .truncate(first);
    let measured = observations(&base)?;
    let decisions = page
        .placement_decisions
        .as_deref()
        .and_then(|all| all.get(first..));
    let saved_routes = page
        .semantics
        .routing
        .iter()
        .filter(|r| ids.contains(r.entity_id.as_str()));
    let measured_routes = measured.iter().map(|(w, _)| wall_route(w));
    if measured.len() != page.walls.len() - first
        || !decisions.is_some_and(|ds| ds.iter().eq(measured.iter().map(|(_, d)| d)))
        || !page.walls[first..]
            .iter()
            .eq(measured.iter().map(|(w, _)| w))
        || !saved_routes
            .map(|r| serde_json::json!(r))
            .eq(measured_routes.map(|r| serde_json::json!(r)))
    {
        return Err("raster jamb stock disagrees with original measured material".into());
    }
    Ok(Cow::Owned(base))
}
