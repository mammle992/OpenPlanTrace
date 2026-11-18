mod graph;
use super::wall_coverage;
use crate::{
    model::{Opening, PageScan},
    placement::WallStatus,
};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};

pub(super) fn overlays(page: &PageScan) -> Value {
    let view = crate::placement::effective_page(page);
    overlays_with_joints(&view, &crate::placement::corner_joints(&view))
}

fn overlays_with_joints(
    page: &PageScan,
    measured: &[crate::placement::stroked::CornerJoint],
) -> Value {
    let joints: Vec<_> = measured
        .iter()
        .enumerate()
        .map(|(i, j)| {
            let mut record = json!({
                "id": format!("p{}:corner:{}", page.page_number, i + 1),
                "wallIds": j.wall_ids, "sourceIds": j.source_ids,
                "polygon": j.polygon, "requiresReview": false
            });
            if !j.adds_material {
                record["polygon"] = json!([]);
                record["position"] = json!((j.polygon[0] + j.polygon[2]) * 0.5);
                record["materialAdded"] = json!(false);
                record["evidence"] = json!("MeasuredBodyOverlap");
                if let Some(points) = j.contact_points {
                    record["axisPoints"] = json!(points);
                }
            }
            record
        })
        .collect();
    json!({"cornerJunctions": joints, "exteriorEnclosures": page.exterior_enclosures.as_deref().unwrap_or_default()})
}

pub(super) fn legacy_page(page: &PageScan) -> Value {
    let barriers: Vec<_> = page.walls.iter().map(|w| json!({
        "wallId": w.id, "thickness": w.thickness, "spans": super::coverage(w, &page.openings).1,
        "requiresReview": true
    })).collect();
    json!({
        "pageNumber": page.page_number, "unitsToMillimeters": page.units_to_millimeters,
        "scaleSource": page.scale_source, "barriers": barriers, "openings": page.openings,
        "roomConnections": page.room_connections, "rooms": page.rooms, "objects": page.objects,
        "objectGroups": page.object_groups, "readyForUnattendedPlacement": false
    })
}

pub(super) fn page(page: &PageScan, with_enclosures: bool) -> Value {
    let observed = page;
    let view = crate::placement::effective_page(page);
    let page = &*view;
    let joints = if with_enclosures {
        crate::placement::corner_joints(page)
    } else {
        vec![]
    };
    let decisions: HashMap<_, _> = page
        .placement_decisions
        .iter()
        .flatten()
        .map(|decision| (decision.wall_id.as_str(), decision))
        .collect();
    let mut hosted: HashMap<&str, Vec<&Opening>> = HashMap::new();
    for opening in &page.openings {
        for host in &opening.host_wall_ids {
            hosted.entry(host).or_default().push(opening);
        }
    }
    let mut ready_ids = Vec::new();
    let mut review_ids = Vec::new();
    let mut evidence_ids = Vec::new();
    let mut walls = Vec::with_capacity(page.walls.len());
    let mut barriers = Vec::new();
    for (wall, original) in page.walls.iter().zip(&observed.walls) {
        let id = wall.id.as_str();
        let decision = decisions.get(id).copied();
        let (intervals, spans) = wall_coverage(wall, hosted.get(id).into_iter().flatten().copied());
        match decision.map(|d| &d.status) {
            Some(WallStatus::Ready) => {
                ready_ids.push(id);
                barriers.push(json!({
                    "wallId": id, "thickness": wall.thickness, "thicknessKnown": wall.thickness > 0., "spans": spans,
                    "sourceIds": wall.source_ids, "requiresReview": false
                }));
            }
            Some(WallStatus::EvidenceOnly) => evidence_ids.push(id),
            Some(WallStatus::Review) | None => review_ids.push(id),
        }
        let mut row = json!({
            "wallId": id, "centerLine": {"start": wall.start, "end": wall.end},
            "curve": wall.curve, "body": wall.body, "thickness": wall.thickness, "thicknessKnown": wall.thickness > 0.,
            "sourceIds": wall.source_ids, "openingIntervals": intervals, "spans": spans, "decision": decision
        });
        if wall.start != original.start || wall.end != original.end {
            row["observedCenterLine"] = json!({"start":original.start,"end":original.end});
        }
        walls.push(row);
    }

    let ready: HashSet<_> = ready_ids.iter().copied().collect();
    let openings: Vec<_> = page
        .openings
        .iter()
        .map(|o| {
            let mut row = json!(o);
            row["placementHostWallIds"] = json!(
                o.host_wall_ids
                    .iter()
                    .filter(|id| ready.contains(id.as_str()))
                    .collect::<Vec<_>>()
            );
            row
        })
        .collect();
    let mut packet = json!({
        "pageNumber": page.page_number, "width": page.width, "height": page.height,
        "unitsToMillimeters": page.units_to_millimeters, "scaleSource": page.scale_source,
        "walls": walls, "wallSets": {"readyWallIds": ready_ids, "reviewWallIds": review_ids, "evidenceOnlyWallIds": evidence_ids},
        "barriers": barriers, "wallGraph": graph::selected(page, &ready, &decisions, &joints), "openings": openings,
        "roomConnections": page.room_connections, "rooms": page.rooms, "objects": page.objects,
        "objectGroups": page.object_groups, "readyForUnattendedPlacement": false
    });
    if with_enclosures {
        let overlay = overlays_with_joints(page, &joints);
        packet["cornerJunctions"] = overlay["cornerJunctions"].clone();
        packet["exteriorEnclosures"] = overlay["exteriorEnclosures"].clone();
    }
    if let Some(connections) = &page.exterior_connections {
        packet["exteriorConnections"] = json!(connections);
    }
    if !page.material_tiles.is_empty() {
        let used: HashSet<_> = decisions
            .values()
            .filter(|d| d.status == WallStatus::Ready)
            .flat_map(|d| d.support_source_ids.iter().map(String::as_str))
            .collect();
        let tiles: Vec<_> = page
            .material_tiles
            .iter()
            .filter(|t| used.contains(t.id.as_str()))
            .collect();
        if !tiles.is_empty() {
            packet["materialTiles"] = json!(tiles);
        }
    }
    packet
}
