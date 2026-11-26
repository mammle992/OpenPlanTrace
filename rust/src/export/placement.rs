mod graph;
use super::wall_coverage;
use crate::{
    input::white_nested::Receipt,
    model::{Opening, PageScan, Wall},
    placement::{
        WallDecision, WallReason, WallStatus, cavity_material,
        connections::{ConnectionKind, FRAMED_DOOR_THRESHOLD, STEPPED_APERTURE_THRESHOLD},
        finish_attachments, wall_layers,
    },
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

fn white_facings(
    wall: &Wall,
    decision: &WallDecision,
    frames: &HashMap<&str, Option<&Receipt>>,
) -> Option<[Value; 2]> {
    if decision.reason != WallReason::SourceBoundedWhiteNestedWall {
        return None;
    }
    let support: HashSet<_> = decision
        .support_source_ids
        .iter()
        .map(String::as_str)
        .collect();
    let mut witnesses = support
        .iter()
        .filter_map(|id| frames.get(id).copied().flatten());
    let receipt = witnesses.next()?;
    if witnesses.next().is_some()
        || receipt.source_ids().count() != support.len()
        || receipt.source_ids().any(|id| !support.contains(id))
        || !receipt.rails[1..3]
            .iter()
            .all(|id| wall.source_ids.contains(id))
    {
        return None;
    }
    let ([a, b], width) = receipt.extent()?;
    let eps = crate::placement::roundoff(wall).max(width * 1e-6);
    if !((wall.thickness - width).abs() <= eps
        && ((wall.start.distance(a) <= eps && wall.end.distance(b) <= eps)
            || (wall.start.distance(b) <= eps && wall.end.distance(a) <= eps)))
    {
        return None;
    }
    Some([0, 1].map(|i| {
        let facing = &receipt.facings[i];
        let mut ids = [
            receipt.core.source_id.as_str(),
            facing.source_id.as_str(),
            receipt.rails[i * 2].as_str(),
            receipt.rails[i * 2 + 1].as_str(),
        ];
        ids.sort_unstable();
        json!({
            "observedWallId": wall.id,
            "sourcePaintId": facing.source_id,
            "outline": facing.outline,
            "sourceIds": ids,
        })
    }))
}

pub(super) fn page(
    page: &PageScan,
    with_enclosures: bool,
    with_source_finishes: bool,
    with_cavity_material: bool,
) -> Value {
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
    for opening in page.opening_evidence() {
        for host in &opening.host_wall_ids {
            hosted.entry(host).or_default().push(opening);
        }
    }
    let mut ready_ids = Vec::new();
    let mut review_ids = Vec::new();
    let mut evidence_ids = Vec::new();
    let mut walls = Vec::with_capacity(page.walls.len());
    let mut barriers = Vec::new();
    let mut layers: HashMap<&str, Vec<_>> = HashMap::new();
    for layer in wall_layers::layers(page) {
        layers.entry(layer.owner_id).or_default().push(layer);
    }
    let mut junction_material: HashMap<&str, Vec<_>> = HashMap::new();
    let mut occupied = Vec::new();
    if with_source_finishes {
        occupied.extend(joints.iter().filter(|j| j.adds_material).map(|j| j.polygon));
    }
    for junction in wall_layers::contacts(page) {
        if with_source_finishes {
            occupied.push(junction.outline);
        }
        junction_material
            .entry(junction.owner_id)
            .or_default()
            .push(json!({
                "touchingWallId": junction.target_id,
                "outline": junction.outline,
                "sourceIds": junction.source_ids,
            }));
    }
    let coextensive = cavity_material::coextensive::assemblies(page);
    for a in &coextensive {
        for &(id, outline) in &a.cap_cells {
            occupied.push(outline);
            junction_material.entry(a.owner).or_default().push(json!({
                "touchingWallId": a.stem,
                "outline": outline,
                "sourceIds": [id],
            }));
        }
    }
    let mut source_finishes: HashMap<&str, Vec<Value>> = HashMap::new();
    let mut white_frames = HashMap::new();
    if with_source_finishes {
        for frame in &page.white_nested_walls {
            white_frames
                .entry(frame.core.source_id.as_str())
                .and_modify(|entry| *entry = None)
                .or_insert(Some(frame));
        }
        let mut finishes = finish_attachments::attachments(page, &occupied);
        if with_cavity_material {
            occupied.extend(finishes.iter().map(|finish| finish.outline));
            finishes.extend(cavity_material::attachments(page, &occupied));
        }
        for finish in finishes {
            source_finishes
                .entry(finish.owner_id)
                .or_default()
                .push(json!({
                    "observedWallId": finish.owner_id,
                    "sourcePaintId": finish.paint_id,
                    "outline": finish.outline,
                    "sourceIds": finish.source_ids,
                }));
        }
        for a in coextensive {
            let layer = |wall, source, outline| {
                json!({
                    "observedWallId": wall,
                    "sourcePaintId": source,
                    "outline": outline,
                    "sourceIds": [source],
                })
            };
            source_finishes.entry(a.owner).or_default().extend([
                layer(a.mate, a.leaf_source, a.leaf),
                layer(a.owner, a.center_source, a.center),
            ]);
        }
        for a in &page.exterior_jamb_backings {
            let layers = source_finishes.entry(a.owner_wall_id.as_str()).or_default();
            for (paint, outline) in [
                (&a.core_source_id, a.core),
                (&a.separator_source_id, a.separator),
            ] {
                layers.push(json!({
                    "observedWallId": a.owner_wall_id,
                    "sourcePaintId": paint,
                    "outline": outline,
                    "sourceIds": a.source_ids,
                }));
            }
        }
        for a in cavity_material::exterior_lining::linings(page) {
            for ((source, outline), id) in a.materials.into_iter().zip([a.leaf, a.owner]) {
                source_finishes.entry(a.owner).or_default().push(json!({
                    "observedWallId": id,
                    "sourcePaintId": source,
                    "outline": outline,
                    "sourceIds": [source],
                }));
            }
        }
    }
    for (wall, original) in page.walls.iter().zip(&observed.walls) {
        let id = wall.id.as_str();
        let decision = decisions.get(id).copied();
        let (intervals, spans) = wall_coverage(wall, hosted.get(id).into_iter().flatten().copied());
        match decision.map(|d| &d.status) {
            Some(WallStatus::Ready) => {
                ready_ids.push(id);
                let source_ids = decision
                    .filter(|d| {
                        matches!(
                            d.reason,
                            WallReason::SourceBoundedRasterCourse
                                | WallReason::SourceBoundedOverheadGateStock
                        )
                    })
                    .and_then(|d| d.placement_extent.as_ref())
                    .map_or(wall.source_ids.as_slice(), |extent| &extent.source_ids);
                let mut barrier = json!({
                    "wallId": id, "thickness": wall.thickness, "thicknessKnown": wall.thickness > 0., "spans": spans,
                    "sourceIds": source_ids, "requiresReview": false
                });
                let mut shapes: Vec<_> = layers
                    .get(id)
                    .into_iter()
                    .flatten()
                    .map(|layer| {
                        let footprint = layer.footprint(wall);
                        let axis = (footprint.end - footprint.start).unit();
                        let offset =
                            crate::model::Point::new(-axis.y, axis.x) * (footprint.thickness * 0.5);
                        json!({
                            "observedWallId": layer.wall.id,
                            "outline": [
                                footprint.start + offset, footprint.end + offset,
                                footprint.end - offset, footprint.start - offset,
                            ],
                            "sourceIds": layer.decision.support_source_ids,
                        })
                    })
                    .collect();
                shapes.extend(source_finishes.remove(id).into_iter().flatten());
                if !white_frames.is_empty()
                    && let Some(facings) =
                        decision.and_then(|d| white_facings(wall, d, &white_frames))
                {
                    shapes.extend(facings);
                }
                if !shapes.is_empty() {
                    barrier["materialLayers"] = json!(shapes);
                }
                if let Some(junctions) = junction_material.get(id) {
                    barrier["junctionMaterial"] = json!(junctions);
                }
                barriers.push(barrier);
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
    let mut derived_hosts: HashMap<&str, Vec<&str>> = HashMap::new();
    let mut stepped_openings = HashSet::new();
    let mut placement_thresholds = HashMap::new();
    if let Some(graph) = &page.exterior_connections {
        let nodes: HashMap<_, _> = graph
            .nodes
            .iter()
            .map(|n| (n.id.as_str(), n.position))
            .collect();
        for edge in &graph.edges {
            let stepped = edge.evidence == STEPPED_APERTURE_THRESHOLD;
            if edge.kind != ConnectionKind::DoorThreshold
                && !(stepped && edge.kind == ConnectionKind::WindowThreshold)
            {
                continue;
            }
            for opening in &edge.opening_ids {
                if stepped {
                    stepped_openings.insert(opening.as_str());
                }
                derived_hosts.entry(opening.as_str()).or_default().extend(
                    edge.wall_ids.iter().map(String::as_str).filter(|id| {
                        decisions.get(id).is_some_and(|d| {
                            d.reason == crate::placement::WallReason::SourceBoundedExteriorCore
                                || ((stepped || edge.evidence == FRAMED_DOOR_THRESHOLD)
                                    && d.status == WallStatus::Ready)
                        })
                    }),
                );
                if (stepped || edge.evidence == FRAMED_DOOR_THRESHOLD)
                    && let (Some(&start), Some(&end)) = (
                        nodes.get(edge.start_node_id.as_str()),
                        nodes.get(edge.end_node_id.as_str()),
                    )
                {
                    placement_thresholds.insert(opening.as_str(), (start, end));
                }
            }
        }
    }
    let openings: Vec<_> = page
        .openings
        .iter()
        .map(|o| {
            let mut row = json!(o);
            let mut hosts: Vec<_> = if stepped_openings.contains(o.id.as_str()) {
                Vec::new()
            } else {
                o.host_wall_ids
                    .iter()
                    .map(String::as_str)
                    .filter(|id| ready.contains(id))
                    .collect()
            };
            for &host in derived_hosts.get(o.id.as_str()).into_iter().flatten() {
                if !hosts.contains(&host) {
                    hosts.push(host);
                }
            }
            row["placementHostWallIds"] = json!(hosts);
            if let Some(&(start, end)) = placement_thresholds.get(o.id.as_str()) {
                row["placementThreshold"] = json!({ "start": start, "end": end });
            }
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
    if !page.window_panes.is_empty() {
        packet["windowPanes"] = json!(page.window_panes);
    }
    if !page.material_tiles.is_empty() {
        let used: HashSet<_> = decisions
            .values()
            .filter(|d| {
                d.status == WallStatus::Ready
                    || d.reason == crate::placement::WallReason::SourceBoundedWallLining
            })
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
