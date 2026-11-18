use openplantrace_rs::{export, model::*, pipeline};

fn elbow(angle: f64, scale: f64, broken_turn: bool) -> Scan {
    let axis = Point::new(angle.cos(), angle.sin());
    let at = |x, y| Point::new(100., 100.) + (axis * x + Point::new(-axis.y, axis.x) * y) * scale;
    let line = |id: String, a, b, width| Primitive {
        id,
        layer: None,
        layer_category_override: None,
        stroke_width: Some(width * scale),
        shape: Shape::Line { start: a, end: b },
    };
    let mut primitives = vec![
        line("outer-horizontal".into(), at(0., 0.), at(90., 0.), 1.),
        line("inner-horizontal".into(), at(0., 10.), at(80., 10.), 1.),
        line(
            "outer-vertical".into(),
            at(90., if broken_turn { 10. } else { 0. }),
            at(90., 70.),
            1.,
        ),
        line("inner-vertical".into(), at(80., 10.), at(80., 70.), 1.),
        line("west-end".into(), at(0., 0.), at(0., 10.), 1.),
        line("south-end".into(), at(80., 70.), at(90., 70.), 1.),
    ];
    for i in 0..14 {
        primitives.push(line(
            format!("fine-detail-{i}"),
            at(-20., i as f64 * 4.),
            at(-16., i as f64 * 4.),
            0.5,
        ));
    }
    pipeline::scan_document(
        Document {
            warnings: vec![],
            pages: vec![Page {
                finish_history: None,
                pdf_input: None,
                number: 1,
                width: 1000.,
                height: 1000.,
                units_to_mm: None,
                placement_paint_ids: None,
                placement_polygon_paint_ids: None,
                material_tiles: vec![],
                primitives,
            }],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap()
}

#[test]
fn source_bends_add_material_and_connect_the_measured_spans() {
    for (angle, scale) in [(0., 1.), (0.7, 1.), (0., 1.5)] {
        let scan = elbow(angle, scale, false);
        export::validate(&scan).unwrap();
        let packet = export::placement(&scan);
        assert_eq!(packet["schemaVersion"], "openplantrace.rust.placement.v3");
        let page = &packet["pages"][0];
        assert_eq!(
            page["wallSets"]["readyWallIds"].as_array().unwrap().len(),
            2,
            "{page}"
        );
        let joints = page["cornerJunctions"].as_array().unwrap();
        assert_eq!(joints.len(), 1);
        assert_eq!(joints[0]["wallIds"].as_array().unwrap().len(), 2);
        assert_eq!(joints[0]["polygon"].as_array().unwrap().len(), 4);
        assert_eq!(joints[0]["requiresReview"], false);
        let joins = page["wallGraph"]["edges"]
            .as_array()
            .unwrap()
            .iter()
            .filter(|e| e.get("junctionId").is_some())
            .count();
        assert_eq!(joins, 2);
        assert!(
            page["walls"]
                .as_array()
                .unwrap()
                .iter()
                .all(|w| w["decision"]["reason"] == "contrasting_bent_wall_faces")
        );
    }
}

#[test]
fn one_missing_ink_turn_cannot_be_replaced_by_a_nearby_graph_node() {
    let scan = elbow(0., 1., true);
    let packet = export::placement(&scan);
    assert!(
        packet["pages"][0]["cornerJunctions"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    assert!(
        packet["pages"][0]["wallSets"]["readyWallIds"]
            .as_array()
            .unwrap()
            .is_empty()
    );
}

fn window_at(start: Point, end: Point, hosts: Vec<String>) -> Opening {
    Opening {
        physical_jambs: None,
        id: "window".into(),
        page: 1,
        kind: OpeningKind::Window,
        operation: "Fixed".into(),
        host_wall_id: hosts.first().cloned().unwrap_or_default(),
        host_wall_ids: hosts,
        start,
        end,
        hinge: None,
        swing_observations: vec![],
        source_ids: vec![],
        evidence: OpeningEvidence::ExplicitWindowFrame,
        requires_review: false,
    }
}

#[test]
fn an_aperture_in_the_corner_square_retires_the_joint_but_boundary_contact_is_clear() {
    for angle in [0_f64, 0.7] {
        let axis = Point::new(angle.cos(), angle.sin());
        let at = |x, y| Point::new(100., 100.) + axis * x + Point::new(-axis.y, axis.x) * y;
        for (start, end, clear) in [
            (at(82., 5.), at(88., 5.), false),
            (at(82., 0.), at(88., 0.), true),
            (at(85., -6.), at(85., 0.), true),
        ] {
            let mut scan = elbow(angle, 1., false);
            // An independent aperture crosses neither observed finite wall body.
            scan.pages[0].openings.push(window_at(start, end, vec![]));
            let packet = export::placement(&scan);
            assert_eq!(
                packet["pages"][0]["cornerJunctions"]
                    .as_array()
                    .unwrap()
                    .len(),
                usize::from(clear)
            );
            assert_eq!(
                openplantrace_rs::placement::validate(&scan.pages[0], true).is_ok(),
                clear
            );
        }
    }
}

#[test]
fn mixed_ready_reasons_connect_measured_ends_and_keep_other_apertures_open() {
    use openplantrace_rs::placement::WallReason;
    let mut scan = elbow(0., 1., false);
    let page = &mut scan.pages[0];
    let wall = page.walls.iter().find(|w| w.start.y == w.end.y).unwrap();
    let wall_id = wall.id.clone();
    let y = wall.start.y;
    let decision = page
        .placement_decisions
        .as_mut()
        .unwrap()
        .iter_mut()
        .find(|d| d.wall_id == wall_id)
        .unwrap();
    // Exercise projection of an already-Ready wall classified by room evidence.
    decision.reason = WallReason::SupportedRoomBoundary;
    for n in &mut page.nodes {
        n.position = n.position + Point::new(0.2, 0.3);
    }
    page.openings.push(window_at(
        Point::new(120., y),
        Point::new(132., y),
        vec![wall_id.clone()],
    ));
    let packet = export::placement(&scan);
    let p = &packet["pages"][0];
    let joint = &p["cornerJunctions"][0];
    let graph = &p["wallGraph"];
    let nodes = graph["nodes"].as_array().unwrap();
    let edges = graph["edges"].as_array().unwrap();
    let connections: Vec<_> = edges
        .iter()
        .filter(|e| e["junctionId"] == joint["id"])
        .collect();
    assert_eq!(connections.len(), 2);
    let center = &connections[0]["endNodeId"];
    assert_eq!(&connections[1]["endNodeId"], center);
    assert_eq!(
        nodes.iter().find(|n| &n["id"] == center).unwrap()["wallIds"]
            .as_array()
            .unwrap()
            .len(),
        2
    );
    for (connection, id) in connections.iter().zip(joint["wallIds"].as_array().unwrap()) {
        let endpoint = nodes
            .iter()
            .find(|n| n["id"] == connection["startNodeId"])
            .unwrap();
        assert!(endpoint["wallIds"].as_array().unwrap().contains(id));
        let w = scan.pages[0]
            .walls
            .iter()
            .find(|w| id.as_str() == Some(w.id.as_str()))
            .unwrap();
        let pos: Point = serde_json::from_value(endpoint["position"].clone()).unwrap();
        assert!(pos == w.start || pos == w.end);
    }
    let windows: Vec<_> = edges
        .iter()
        .filter(|e| {
            e["openingIds"]
                .as_array()
                .unwrap()
                .iter()
                .any(|id| id == "window")
        })
        .collect();
    assert_eq!(windows.len(), 1);
    let position = |id: &serde_json::Value| {
        nodes.iter().find(|n| n["id"] == *id).unwrap()["position"]["x"]
            .as_f64()
            .unwrap()
    };
    let start = position(&windows[0]["startNodeId"]);
    let end = position(&windows[0]["endNodeId"]);
    assert_eq!((start.min(end), start.max(end)), (120., 132.));
}

fn rescan(scan: Scan, primitives: Vec<Primitive>) -> Scan {
    pipeline::scan_document(
        Document {
            warnings: vec![],
            pages: vec![Page {
                finish_history: None,
                pdf_input: None,
                number: 1,
                width: scan.pages[0].width,
                height: scan.pages[0].height,
                units_to_mm: None,
                placement_paint_ids: None,
                placement_polygon_paint_ids: None,
                material_tiles: vec![],
                primitives,
            }],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap()
}

#[test]
fn measured_face_ends_at_glazing_keep_the_empty_bay_open() {
    let base = elbow(0., 1., false);
    let primitives = base.pages[0]
        .primitives
        .iter()
        .filter(|p| p.id != "west-end")
        .cloned()
        .collect();
    let scan = rescan(base, primitives);
    let packet = export::placement(&scan);
    assert_eq!(
        packet["pages"][0]["wallSets"]["readyWallIds"]
            .as_array()
            .unwrap()
            .len(),
        2
    );
    export::validate(&scan).unwrap();
}

#[test]
fn a_short_pier_requires_its_source_cap_and_real_corner() {
    let base = elbow(0., 1., false);
    let mut primitives = base.pages[0].primitives.clone();
    for p in &mut primitives {
        if let Shape::Line { start, end } = &mut p.shape {
            if p.id == "outer-vertical" || p.id == "inner-vertical" {
                end.y = 115.;
            }
            if p.id == "south-end" {
                start.y = 115.;
                end.y = 115.;
            }
        }
    }
    let scan = rescan(base, primitives);
    export::validate(&scan).unwrap();
    let packet = export::placement(&scan);
    assert_eq!(
        packet["pages"][0]["wallSets"]["readyWallIds"]
            .as_array()
            .unwrap()
            .len(),
        2,
        "{packet}"
    );
    assert_eq!(
        packet["pages"][0]["cornerJunctions"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
}
