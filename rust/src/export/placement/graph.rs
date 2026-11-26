use crate::model::{PageScan, Point};
use crate::placement::{WallDecision, WallReason, point_key};
use serde_json::{Value, json};
use std::collections::{BTreeSet, HashMap, HashSet};

struct Vertex<'a> {
    id: String,
    position: Point,
    walls: BTreeSet<&'a str>,
    degree: usize,
}

fn edge<'a>(
    vertices: &mut [Vertex<'a>],
    edges: &mut Vec<Value>,
    a: usize,
    b: usize,
    walls: &[&'a str],
    openings: &[&str],
) {
    for k in [a, b] {
        vertices[k].walls.extend(walls.iter().copied());
        vertices[k].degree += 1;
    }
    edges.push(json!({"startNodeId":vertices[a].id,"endNodeId":vertices[b].id,"wallIds":walls,"openingIds":openings}));
}

pub(super) fn selected(
    page: &PageScan,
    ready: &HashSet<&str>,
    decisions: &HashMap<&str, &WallDecision>,
    joints: &[crate::placement::stroked::CornerJoint],
) -> Value {
    let mut exact: HashSet<_> = decisions
        .values()
        .filter(|d| {
            ready.contains(d.wall_id.as_str())
                && matches!(
                    d.reason,
                    WallReason::ConnectedRasterMaterialFaces
                        | WallReason::SourceBoundedRasterPartition
                        | WallReason::SourceBoundedRasterCompanion
                        | WallReason::SourceBoundedRasterOffsetPair
                        | WallReason::SourceBoundedExteriorCore
                        | WallReason::PaintedWallLayersBetweenFaces
                        | WallReason::PaintedCoreBetweenWallFaces
                        | WallReason::InsulatedDoorJamb
                        | WallReason::ContrastingCappedWallFaces
                        | WallReason::ContrastingBentWallFaces
                        | WallReason::IndependentlyOutlinedPaintedBody
                        | WallReason::SourceBoundedDoorJambFlanks
                        | WallReason::SourceBoundedPaintedJunctionSpan
                        | WallReason::SourceBoundedVisibleInsulationAssembly
                )
        })
        .map(|d| d.wall_id.as_str())
        .collect();
    exact.extend(
        joints
            .iter()
            .flat_map(|j| j.wall_ids.iter().map(String::as_str)),
    );
    let openings: HashMap<_, _> = page.openings.iter().map(|o| (o.id.as_str(), o)).collect();
    let mut vertices: Vec<_> = page
        .nodes
        .iter()
        .map(|n| Vertex {
            id: n.id.clone(),
            position: n.position,
            walls: BTreeSet::new(),
            degree: 0,
        })
        .collect();
    let mut edges = Vec::new();
    for e in &page.edges {
        let kept: Vec<_> = e
            .wall_ids
            .iter()
            .map(String::as_str)
            .filter(|id| ready.contains(id) && !exact.contains(id))
            .collect();
        if kept.is_empty() || e.start_node >= vertices.len() || e.end_node >= vertices.len() {
            continue;
        }
        let doors: Vec<_> = e
            .opening_ids
            .iter()
            .map(String::as_str)
            .filter(|id| {
                openings
                    .get(id)
                    .is_some_and(|o| o.host_wall_ids.iter().any(|w| kept.contains(&w.as_str())))
            })
            .collect();
        edge(
            &mut vertices,
            &mut edges,
            e.start_node,
            e.end_node,
            &kept,
            &doors,
        );
    }
    if !exact.is_empty() {
        // The observed room graph may snap a partition to a neighbouring wall or extend its
        // axis into a junction. Source-measured solids end at the painted end plane.
        let mut at: HashMap<_, _> = vertices
            .iter()
            .enumerate()
            .map(|(i, n)| (point_key(n.position), i))
            .collect();
        let mut taken: HashSet<_> = vertices.iter().map(|n| n.id.clone()).collect();
        let mut serial = 0;
        let mut incident: HashMap<&str, Vec<usize>> = HashMap::new();
        for (i, n) in page.nodes.iter().enumerate() {
            for id in &n.wall_ids {
                if exact.contains(id.as_str()) {
                    incident.entry(id).or_default().push(i);
                }
            }
        }
        let mut ends_by_wall = HashMap::new();
        let mut hosted: HashMap<&str, Vec<_>> = HashMap::new();
        let frozen_apertures = !joints.is_empty()
            || decisions
                .values()
                .any(|d| d.reason == WallReason::SourceBoundedDoorJambFlanks);
        if frozen_apertures
            || decisions.values().any(|d| {
                matches!(
                    d.reason,
                    WallReason::SourceBoundedPaintedJunctionSpan
                        | WallReason::SourceBoundedVisibleInsulationAssembly
                )
            })
        {
            for o in &page.openings {
                for wall_id in &o.host_wall_ids {
                    if exact.contains(wall_id.as_str())
                        && (frozen_apertures
                            || decisions.get(wall_id.as_str()).is_some_and(|d| {
                                matches!(
                                    d.reason,
                                    WallReason::SourceBoundedPaintedJunctionSpan
                                        | WallReason::SourceBoundedVisibleInsulationAssembly
                                )
                            }))
                    {
                        hosted.entry(wall_id).or_default().push(o);
                    }
                }
            }
        }
        // Index measured contacts once. Scanning every joint for every source
        // wall makes dense, otherwise bounded input quadratic again here.
        let current_raster = page
            .pdf_input
            .as_ref()
            .is_some_and(|p| p.analysis_revision == 1);
        let mut contacts_by_wall: HashMap<&str, Vec<&crate::placement::stroked::CornerJoint>> =
            HashMap::new();
        if current_raster {
            for joint in joints.iter().filter(|j| !j.adds_material) {
                for id in &joint.wall_ids {
                    contacts_by_wall.entry(id.as_str()).or_default().push(joint);
                }
            }
        }
        for w in page.walls.iter().filter(|w| exact.contains(w.id.as_str())) {
            let length = w.start.distance(w.end);
            let axis = (w.end - w.start).unit();
            let eps = crate::placement::roundoff(w);
            let mut points: Vec<_> = incident
                .get(w.id.as_str())
                .into_iter()
                .flatten()
                .filter_map(|&i| {
                    let v = vertices[i].position - w.start;
                    let dist = v.dot(axis);
                    (dist > eps && dist < length - eps && v.cross(axis).abs() <= eps)
                        .then_some((dist, i))
                })
                .collect();
            let mut vertex_at = |p| {
                *at.entry(point_key(p)).or_insert_with(|| {
                    let id = loop {
                        serial += 1;
                        let id = format!("p{}:placement-node:{}", page.page_number, serial);
                        if taken.insert(id.clone()) {
                            break id;
                        }
                    };
                    let i = vertices.len();
                    vertices.push(Vertex {
                        id,
                        position: p,
                        walls: BTreeSet::new(),
                        degree: 0,
                    });
                    i
                })
            };
            let endpoints = [vertex_at(w.start), vertex_at(w.end)];
            ends_by_wall.insert(w.id.as_str(), endpoints);
            points.extend([(0., endpoints[0]), (length, endpoints[1])]);
            if current_raster {
                for j in contacts_by_wall.get(w.id.as_str()).into_iter().flatten() {
                    let center = (j.polygon[0] + j.polygon[2]) * 0.5;
                    let side = usize::from(j.wall_ids[1] == w.id);
                    let cut = j.contact_points.map_or(center, |p| p[side]);
                    let t = (cut - w.start).dot(axis);
                    if t > eps && t < length - eps && (cut - w.start).cross(axis).abs() <= eps {
                        points.push((t, vertex_at(cut)));
                    }
                }
            }

            let apertures: Vec<_> = hosted
                .get(w.id.as_str())
                .into_iter()
                .flatten()
                .filter_map(|o| {
                    o.interval_on(w)
                        .map(|(lo, hi)| (lo * length, hi * length, o.id.as_str()))
                })
                .collect();
            for dist in apertures
                .iter()
                .flat_map(|&(lo, hi, _)| [lo, hi])
                .filter(|&t| t > eps && t < length - eps)
            {
                let i = vertex_at(w.start + axis * dist);
                points.push((dist, i));
            }
            points.sort_by(|a, b| a.0.total_cmp(&b.0));
            points.dedup_by(|a, b| (a.0 - b.0).abs() <= eps);
            for pair in points.windows(2) {
                let mid = (pair[0].0 + pair[1].0) * 0.5;
                let open: Vec<_> = apertures
                    .iter()
                    .filter_map(|&(lo, hi, id)| (lo < mid && mid < hi).then_some(id))
                    .collect();
                edge(
                    &mut vertices,
                    &mut edges,
                    pair[0].1,
                    pair[1].1,
                    &[w.id.as_str()],
                    &open,
                );
            }
        }
        for (serial, j) in joints.iter().enumerate() {
            let id = format!("p{}:corner:{}", page.page_number, serial + 1);
            let center = (j.polygon[0] + j.polygon[2]) * 0.5;
            let c = *at.entry(point_key(center)).or_insert_with(|| {
                let i = vertices.len();
                let mut center_id = format!("{id}:center");
                while !taken.insert(center_id.clone()) {
                    center_id.push(':');
                }
                vertices.push(Vertex {
                    id: center_id,
                    position: center,
                    walls: BTreeSet::new(),
                    degree: 0,
                });
                i
            });
            for (wall_id, side) in j.incident_ends() {
                if let Some(points) = j.contact_points {
                    let point = points[usize::from(j.wall_ids[1] == wall_id)];
                    let contact = *at
                        .get(&point_key(point))
                        .expect("measured contact was cut on its source axis");
                    vertices[c].walls.insert(wall_id);
                    if contact != c {
                        edge(&mut vertices, &mut edges, contact, c, &[], &[]);
                        let row = edges.last_mut().unwrap();
                        row["junctionId"] = json!(id);
                        row["materialWallIds"] = json!([wall_id]);
                    }
                    continue;
                }
                // Every junction participant was projected above, including
                // room-supported walls whose structural graph uses a snapped end.
                if !j.adds_material
                    && page
                        .pdf_input
                        .as_ref()
                        .is_some_and(|p| p.analysis_revision == 1)
                    && let Some(w) = page.walls.iter().find(|w| w.id == wall_id)
                {
                    let axis = (w.end - w.start).unit();
                    let t = (center - w.start).dot(axis);
                    let eps = crate::placement::roundoff(w);
                    if t >= -eps
                        && t <= w.start.distance(w.end) + eps
                        && (center - w.start).cross(axis).abs() <= eps
                    {
                        vertices[c].walls.insert(wall_id);
                        continue;
                    }
                }
                let end = ends_by_wall[wall_id][side];
                vertices[c].walls.insert(wall_id);
                if end == c {
                    continue;
                }
                edge(&mut vertices, &mut edges, end, c, &[], &[]);
                let row = edges.last_mut().unwrap();
                row["junctionId"] = json!(id);
            }
        }
    }
    let nodes: Vec<_> = vertices
        .into_iter()
        .filter(|n| n.degree > 0)
        .map(|n| {
            json!({"id":n.id,"position":n.position,
        "degree":n.degree,"wallIds":n.walls})
        })
        .collect();
    json!({"nodes":nodes,"edges":edges})
}
