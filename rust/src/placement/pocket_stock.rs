//! Place only the solid distal body of a source-bounded pocket wall.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall,
    exterior_piers::{aperture_guard, corners, overlaps},
    wall_layers,
};
use crate::{
    input::pocket_stock,
    model::{BodyEvidence, PageScan, Point, Wall},
    semantics::wall_route,
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashMap},
};

const PREFIX: &str = "pocket-stock:";
const REASON: WallReason = WallReason::SourceBoundedPocketStock;

pub(crate) fn is_stock(w: &Wall) -> bool {
    w.id.starts_with(PREFIX)
}

fn center(outline: [Point; 4]) -> (Point, Point, f64) {
    let [a, b, c, d] = outline;
    ((a + d) * 0.5, (b + c) * 0.5, a.distance(d))
}

fn protected(page: &PageScan) -> Vec<[Point; 4]> {
    let mut zones = aperture_guard(page);
    zones.extend(
        page.objects
            .iter()
            .filter_map(|o| o.footprint.as_ref().map(|p| p.polygon)),
    );
    zones.extend(
        page.withheld_walls
            .iter()
            .flatten()
            .filter_map(|w| w.furniture_outline),
    );
    zones
}

fn occupied(page: &PageScan, protected: &[[Point; 4]]) -> Vec<[Point; 4]> {
    let mut solids: Vec<_> = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| {
            let w = effective_wall(w, Some(d));
            if let Some(c) = w.curve {
                let r = c.radius + w.thickness * 0.5;
                let offset = Point::new(r, 0.);
                corners(c.center - offset, c.center + offset, r * 2.)
            } else {
                corners(w.start, w.end, w.thickness)
            }
        })
        .collect();
    let hosts: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    solids.extend(wall_layers::layers(page).filter_map(|layer| {
        let w = hosts.get(layer.owner_id)?;
        let f = layer.footprint(w);
        Some(corners(f.start, f.end, f.thickness))
    }));
    solids.extend(
        wall_layers::contacts(page)
            .iter()
            .map(|contact| contact.outline),
    );
    solids.extend_from_slice(protected);
    solids
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    if page.pocket_stock.is_empty() {
        return Ok(());
    }
    let bodies = pocket_stock::verified(page).ok_or("invalid native pocket wall stock")?;
    let protected = protected(page);
    let occupied = occupied(page, &protected);
    let mut additions = Vec::new();
    let rows = page.pocket_stock.iter().zip(&bodies).enumerate();
    // Existing receipts retain their old IDs and compete on their own terms.
    let ordered = rows
        .clone()
        .filter(|(_, (r, _))| r.authored_faces.is_none())
        .chain(
            rows.clone()
                .filter(|(_, (r, _))| r.authored_faces.is_some()),
        );
    for (ordinal, (i, (receipt, &body))) in ordered.enumerate() {
        let authored = receipt.authored_faces.is_some();
        let rejects = |&other| overlaps(body, other, 1e-7);
        if occupied.iter().any(rejects)
            || rows
                .clone()
                .any(|(j, (r, b))| j != i && (authored || r.authored_faces.is_none()) && rejects(b))
        {
            continue;
        }
        if receipt
            .authored_faces
            .as_ref()
            .is_some_and(|proof| proof.continuation.is_some())
        {
            let tail = receipt
                .continued_extent(page)
                .ok_or("invalid pocket wall pen continuation")?;
            if protected.iter().any(|&zone| overlaps(tail, zone, 1e-7)) {
                continue;
            }
        }
        let source_ids: Vec<_> = std::iter::once(receipt.paint_source_id.clone())
            .chain(receipt.face_source_ids.iter().cloned())
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        let mut support_ids = source_ids.clone();
        support_ids.push(receipt.pocket_source_id.clone());
        support_ids.extend(receipt.authored_source_ids().map(str::to_owned));
        support_ids.sort_unstable();
        let (start, end, thickness) = center(body);
        let number = page.page_number;
        let id = format!("{PREFIX}{number}:{}", ordinal + 1);
        let wall = Wall {
            id: id.clone(),
            page: number,
            start,
            end,
            thickness,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids,
            requires_review: false,
        };
        let decision = WallDecision {
            wall_id: id.clone(),
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{number}:{id}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: support_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        additions.push((wall, decision));
    }
    if additions.is_empty() {
        return Ok(());
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("pocket wall stock requires placement decisions")?;
    for (w, d) in additions {
        page.semantics.routing.push(wall_route(&w));
        page.walls.push(w);
        decisions.push(d);
    }
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let own: BTreeSet<_> = page
        .walls
        .iter()
        .filter(|w| is_stock(w))
        .map(|w| w.id.as_str())
        .collect();
    if own.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    base.walls.retain(|w| !own.contains(w.id.as_str()));
    base.semantics
        .routing
        .retain(|r| !own.contains(r.entity_id.as_str()));
    base.placement_decisions
        .as_mut()
        .ok_or("missing pocket wall decisions")?
        .retain(|d| !own.contains(d.wall_id.as_str()));
    Ok(Cow::Owned(base))
}
