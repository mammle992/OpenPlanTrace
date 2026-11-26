use openplantrace_rs::{
    export,
    model::*,
    openings::physical_jambs,
    placement::{
        self, PlacementExtent, WallDecision, WallReason, WallStatus, connections, enclosures,
    },
    semantics, topology,
};
use serde_json::Value;
use std::{collections::HashMap, f64::consts::FRAC_PI_2};

fn line(id: &str, a: (f64, f64), b: (f64, f64)) -> Primitive {
    Primitive {
        id: id.into(),
        layer: Some("A-WALL".into()),
        stroke_width: None,
        layer_category_override: None,
        shape: Shape::Line {
            start: Point::new(a.0, a.1),
            end: Point::new(b.0, b.1),
        },
    }
}
fn fill(id: &str, points: &[(f64, f64)]) -> Primitive {
    Primitive {
        id: id.into(),
        layer: Some("A-WALL".into()),
        stroke_width: None,
        layer_category_override: None,
        shape: Shape::Polyline {
            points: points.iter().map(|&(x, y)| Point::new(x, y)).collect(),
            closed: true,
            filled: true,
        },
    }
}

/// Construct the source detector's retained records directly. This isolates
/// export/replay contracts from wall pairing and includes no measured corners.
fn fixture() -> Scan {
    let mut primitives = vec![
        line("west north", (95., 10.), (95., 45.)),
        line("east north", (105., 5.), (105., 45.)),
        line("west south", (95., 70.), (95., 105.)),
        line("east south", (105., 70.), (105., 105.)),
        line("north cap west", (95., 45.), (100., 45.)),
        line("north cap east", (100., 45.), (105., 45.)),
        line("south cap west", (95., 70.), (100., 70.)),
        line("south cap east", (100., 70.), (105., 70.)),
        line("open leaf", (100., 68.), (120., 68.)),
        line("closed leaf", (100., 68.), (100., 48.)),
        fill(
            "upper core",
            &[(95., 10.), (102., 6.5), (102., 45.), (95., 45.)],
        ),
        fill(
            "upper facing",
            &[(102., 6.5), (105., 5.), (105., 45.), (102., 45.)],
        ),
        fill(
            "lower core",
            &[(95., 70.), (102., 70.), (102., 105.), (95., 105.)],
        ),
        fill(
            "lower facing",
            &[(102., 70.), (105., 70.), (105., 105.), (102., 105.)],
        ),
    ];
    primitives.push(Primitive {
        id: "swing".into(),
        layer: Some("A-DOOR".into()),
        stroke_width: None,
        layer_category_override: None,
        shape: Shape::Arc {
            center: Point::new(100., 68.),
            radius: 20.,
            start_angle: -FRAC_PI_2,
            sweep: FRAC_PI_2,
        },
    });
    let wall = Wall {
        id: "observed wall".into(),
        page: 1,
        start: Point::new(100., 10.),
        end: Point::new(100., 110.),
        thickness: 10.,
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: primitives[..4].iter().map(|p| p.id.clone()).collect(),
        requires_review: true,
    };
    let mut door = Opening {
        id: "signed door".into(),
        page: 1,
        kind: OpeningKind::Door,
        operation: "Hinged".into(),
        host_wall_id: wall.id.clone(),
        host_wall_ids: vec![wall.id.clone()],
        start: Point::new(100., 68.),
        end: Point::new(100., 48.),
        hinge: Some(Point::new(100., 68.)),
        swing_observations: vec![SwingObservation {
            arc_source_id: "swing".into(),
            segment_source_ids: vec![],
            hinge: Point::new(100., 68.),
            closed_tip: Point::new(100., 48.),
            open_tip: Point::new(120., 68.),
            leaf_tolerance: 0.5,
            open_leaf_source_ids: vec!["open leaf".into()],
            closed_leaf_source_ids: vec!["closed leaf".into()],
        }],
        source_ids: vec!["swing".into(), "open leaf".into(), "closed leaf".into()],
        evidence: OpeningEvidence::FiniteJambSwing,
        requires_review: true,
        physical_jambs: None,
    };
    let source_page = Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        placement_paint_ids: Some(vec!["lower core".into(), "lower facing".into()]),
        placement_polygon_paint_ids: Some(vec!["upper core".into(), "upper facing".into()]),
        placement_paint_remainders: vec![],
        white_nested_walls: vec![],
        terminal_stock: vec![],
        local_stock: vec![],
        stepped_stock: vec![],
        capped_stock: vec![],
        recessed_stock: vec![],
        masked_junction_stock: vec![],
        pocket_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 200.,
        height: 200.,
        units_to_mm: None,
        primitives,
    };
    let graph = topology::build(
        &source_page,
        std::slice::from_ref(&wall),
        std::slice::from_ref(&door),
        &Options::default(),
    )
    .unwrap();
    let correction = physical_jambs::proposals(
        &source_page.primitives,
        std::slice::from_ref(&wall),
        std::slice::from_ref(&door),
    )
    .unwrap()
    .pop()
    .unwrap();
    door.start = correction.start;
    door.end = correction.end;
    door.physical_jambs = Some(correction.evidence);
    let source_ids: Vec<String> = [
        "west north",
        "east north",
        "west south",
        "east south",
        "north cap west",
        "north cap east",
        "south cap west",
        "south cap east",
        "upper core",
        "upper facing",
        "lower core",
        "lower facing",
    ]
    .into_iter()
    .map(str::to_owned)
    .collect::<std::collections::BTreeSet<_>>()
    .into_iter()
    .collect();
    let decision = WallDecision {
        wall_id: wall.id.clone(),
        status: WallStatus::Ready,
        reason: WallReason::SourceBoundedDoorJambFlanks,
        component_id: "p1:placement-component:1".into(),
        component_wall_count: 1,
        junction_node_ids: vec![],
        support_wall_ids: vec![],
        support_source_ids: source_ids.clone(),
        opening_ids: vec![door.id.clone()],
        room_ids: vec![],
        placement_extent: Some(PlacementExtent {
            thickness: None,
            start: wall.start,
            end: Point::new(100., 105.),
            source_ids,
        }),
    };
    let room_connections = vec![Connection {
        opening_id: door.id.clone(),
        room_ids: vec![],
    }];
    let semantics = semantics::interpret(
        &source_page,
        std::slice::from_ref(&wall),
        &[],
        std::slice::from_ref(&door),
        &[],
        &room_connections,
        None,
    );
    let mut page = PageScan {
        finish_visibility: None,
        pdf_input: None,
        page_number: 1,
        exterior_enclosures: None,
        exterior_connections: None,
        exterior_jamb_backings: vec![],
        placement_paint_ids: source_page.placement_paint_ids,
        placement_polygon_paint_ids: source_page.placement_polygon_paint_ids,
        placement_paint_remainders: vec![],
        white_nested_walls: vec![],
        terminal_stock: vec![],
        local_stock: vec![],
        stepped_stock: vec![],
        capped_stock: vec![],
        recessed_stock: vec![],
        masked_junction_stock: vec![],
        pocket_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 200.,
        height: 200.,
        units_to_millimeters: None,
        scale_source: None,
        calibration_source_ids: vec![],
        primitives: source_page.primitives,
        walls: vec![wall],
        placement_decisions: Some(vec![decision]),
        withheld_walls: Some(vec![]),
        openings: vec![door],
        window_panes: vec![],
        nodes: graph.nodes,
        edges: graph.edges,
        rooms: graph.rooms,
        objects: vec![],
        object_groups: vec![],
        room_connections,
        drawing_features: vec![],
        semantics,
        room_annotations: vec![],
        warnings: vec![],
    };
    page.exterior_enclosures = Some(enclosures::build(&page));
    page.exterior_connections = Some(connections::build(&page).unwrap());
    Scan {
        visual_classification: None,
        schema_version: "openplantrace.rust.scan.v14".into(),
        engine_version: env!("CARGO_PKG_VERSION").into(),
        layer_profile: None,
        object_label_profile: None,
        pages: vec![page],
        warnings: vec![],
    }
}

fn point(value: &Value) -> Point {
    serde_json::from_value(value.clone()).unwrap()
}
fn bounds(span: &Value) -> (f64, f64) {
    let a = point(&span["start"]).y;
    let b = point(&span["end"]).y;
    (a.min(b), a.max(b))
}
fn close(a: f64, b: f64) {
    assert!((a - b).abs() < 1e-8, "{a} != {b}");
}

#[test]
fn physical_jamb_placement_exports_exact_supported_extent_and_preserves_observation() {
    let scan = fixture();
    export::validate(&scan).unwrap();
    let packet = export::placement(&scan);
    let page = &packet["pages"][0];
    let wall = &page["walls"][0];
    assert_eq!(point(&wall["centerLine"]["start"]), Point::new(100., 10.));
    assert_eq!(point(&wall["centerLine"]["end"]), Point::new(100., 105.));
    assert_eq!(
        point(&wall["observedCenterLine"]["end"]),
        Point::new(100., 110.)
    );
    let mut spans: Vec<_> = wall["spans"]
        .as_array()
        .unwrap()
        .iter()
        .map(bounds)
        .collect();
    spans.sort_by(|a, b| a.0.total_cmp(&b.0));
    assert_eq!(spans.len(), 2);
    close(spans[0].0, 10.);
    close(spans[0].1, 45.);
    close(spans[1].0, 70.);
    close(spans[1].1, 105.);
    assert_eq!(page["barriers"][0]["spans"], wall["spans"]);
    assert!(page["cornerJunctions"].as_array().unwrap().is_empty());
    let structure = export::structure(&scan);
    assert_eq!(
        point(&structure["pages"][0]["wallRuns"][0]["centerLine"]["end"]),
        Point::new(100., 110.)
    );
    assert!(scan.pages[0].walls[0].requires_review);
}

#[test]
fn physical_jamb_equal_supported_extent_keeps_proof_without_redundant_observation() {
    let mut scan = fixture();
    let extent = scan.pages[0].placement_decisions.as_ref().unwrap()[0]
        .placement_extent
        .clone()
        .unwrap();
    scan.pages[0].walls[0].start = extent.start;
    scan.pages[0].walls[0].end = extent.end;
    let p = &mut scan.pages[0];
    let source = Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        placement_paint_ids: p.placement_paint_ids.clone(),
        placement_polygon_paint_ids: p.placement_polygon_paint_ids.clone(),
        placement_paint_remainders: vec![],
        white_nested_walls: vec![],
        terminal_stock: vec![],
        local_stock: vec![],
        stepped_stock: vec![],
        capped_stock: vec![],
        recessed_stock: vec![],
        masked_junction_stock: vec![],
        pocket_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: p.width,
        height: p.height,
        units_to_mm: None,
        primitives: p.primitives.clone(),
    };
    p.semantics = semantics::interpret(
        &source,
        &p.walls,
        &p.rooms,
        &p.openings,
        &p.objects,
        &p.room_connections,
        None,
    );
    export::validate(&scan).unwrap();
    let packet = export::placement(&scan);
    let row = &packet["pages"][0]["walls"][0];
    assert!(row.get("observedCenterLine").is_none());
    assert_eq!(
        row["decision"]["placementExtent"],
        serde_json::to_value(&extent).unwrap()
    );
    assert_eq!(point(&row["centerLine"]["end"]), extent.end);
    assert_eq!(row["spans"].as_array().unwrap().len(), 2);
    assert!(
        packet["pages"][0]["wallGraph"]["edges"]
            .as_array()
            .unwrap()
            .iter()
            .any(|e| e["openingIds"] == serde_json::json!(["signed door"]))
    );
}

#[test]
fn physical_jamb_selected_graph_splits_and_labels_portal_without_corner_joints() {
    let scan = fixture();
    let packet = export::placement(&scan);
    let page = &packet["pages"][0];
    assert!(page["cornerJunctions"].as_array().unwrap().is_empty());
    let graph = &page["wallGraph"];
    let nodes: HashMap<_, _> = graph["nodes"]
        .as_array()
        .unwrap()
        .iter()
        .map(|n| (n["id"].as_str().unwrap(), point(&n["position"])))
        .collect();
    let mut intervals = vec![];
    for edge in graph["edges"].as_array().unwrap() {
        let a = nodes[edge["startNodeId"].as_str().unwrap()];
        let b = nodes[edge["endNodeId"].as_str().unwrap()];
        close(a.x, 100.);
        close(b.x, 100.);
        let (lo, hi) = (a.y.min(b.y), a.y.max(b.y));
        assert!(lo >= 10. - 1e-8 && hi <= 105. + 1e-8 && hi > lo);
        let portal = (lo + hi) * 0.5 > 45. && (lo + hi) * 0.5 < 70.;
        assert_eq!(
            edge["openingIds"].as_array().unwrap().len(),
            usize::from(portal)
        );
        if portal {
            assert_eq!(edge["openingIds"][0], "signed door");
            assert!(lo >= 45. - 1e-8 && hi <= 70. + 1e-8);
        } else {
            assert!(hi <= 45. + 1e-8 || lo >= 70. - 1e-8);
        }
        intervals.push((lo, hi));
    }
    intervals.sort_by(|a, b| a.0.total_cmp(&b.0));
    let mut at = 10.;
    for (lo, hi) in intervals {
        close(lo, at);
        at = hi;
    }
    close(at, 105.);
}

#[test]
fn physical_jamb_connections_stop_at_supported_end_and_keep_threshold_nonmaterial() {
    let scan = fixture();
    let p = &scan.pages[0];
    let pass = p.exterior_connections.as_ref().unwrap();
    let nodes: HashMap<_, _> = pass
        .nodes
        .iter()
        .map(|n| (n.id.as_str(), n.position))
        .collect();
    let mut wall_spans = vec![];
    let mut doors = vec![];
    for edge in &pass.edges {
        let a = nodes[edge.start_node_id.as_str()];
        let b = nodes[edge.end_node_id.as_str()];
        assert!(a.y >= 10. - 1e-8 && a.y <= 105. + 1e-8 && b.y >= 10. - 1e-8 && b.y <= 105. + 1e-8);
        let span = (a.y.min(b.y), a.y.max(b.y));
        if edge.contributes_wall_material {
            wall_spans.push(span);
            assert!(span.1 <= 45. + 1e-8 || span.0 >= 70. - 1e-8);
        }
        if edge.kind == connections::ConnectionKind::DoorThreshold {
            assert!(!edge.contributes_wall_material && !edge.adds_wall_material);
            assert_eq!(edge.opening_ids, ["signed door"]);
            doors.push(span);
        }
    }
    assert_eq!(wall_spans.len(), 2);
    assert_eq!(doors, [(45., 70.)]);
    assert_eq!(*pass, connections::build(p).unwrap());
}

#[test]
fn physical_jamb_cut_proof_and_extent_tampering_fail_native_replay() {
    let scan = fixture();
    export::validate(&scan).unwrap();
    for mutation in 0..9 {
        let mut changed = scan.clone();
        let p = &mut changed.pages[0];
        match mutation {
            0 => p.openings[0].start.y -= 0.1,
            1 => {
                p.openings[0]
                    .physical_jambs
                    .as_mut()
                    .unwrap()
                    .original_start
                    .y += 0.1
            }
            2 => {
                p.openings[0]
                    .physical_jambs
                    .as_mut()
                    .unwrap()
                    .rail_source_ids
                    .pop();
            }
            3 => {
                p.openings[0]
                    .physical_jambs
                    .as_mut()
                    .unwrap()
                    .cap_source_ids
                    .pop();
            }
            4 => {
                p.placement_decisions.as_mut().unwrap()[0]
                    .placement_extent
                    .as_mut()
                    .unwrap()
                    .end
                    .y = 110.
            }
            5 => {
                p.placement_decisions.as_mut().unwrap()[0]
                    .placement_extent
                    .as_mut()
                    .unwrap()
                    .source_ids
                    .pop();
            }
            6 => {
                p.placement_polygon_paint_ids.as_mut().unwrap().pop();
            }
            7 => p.openings[0].swing_observations[0].open_tip.x += 1.,
            8 => changed.schema_version = "openplantrace.rust.scan.v13".into(),
            _ => unreachable!(),
        }
        assert!(
            export::validate(&changed).is_err(),
            "accepted mutation {mutation}"
        );
    }
    let saved: Scan = serde_json::from_slice(&serde_json::to_vec(&scan).unwrap()).unwrap();
    export::validate(&saved).unwrap();
    assert_eq!(export::placement(&scan), export::placement(&saved));
    placement::validate(&scan.pages[0], true).unwrap();
}
