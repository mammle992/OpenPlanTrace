use openplantrace_rs::{export, model::*, openings, pipeline, topology};
use std::path::Path;
fn pt(x: f64, y: f64) -> Point {
    Point::new(x, y)
}
fn primitive(id: &str, layer: Option<&str>, shape: Shape) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: layer.map(str::to_owned),
        shape,
    }
}
fn page(primitives: Vec<Primitive>) -> Page {
    Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        placement_paint_ids: None,
        placement_polygon_paint_ids: None,
        material_tiles: vec![],
        width: 500.,
        height: 500.,
        units_to_mm: None,
        primitives,
    }
}
fn wall(id: &str, a: Point, b: Point) -> Wall {
    Wall {
        id: id.into(),
        page: 1,
        start: a,
        end: b,
        thickness: 4.,
        curve: None,
        body: BodyEvidence::ExplicitCenterline,
        source_ids: vec![format!("src-{id}")],
        requires_review: true,
    }
}
fn door_case(reverse: bool) -> (Page, Wall) {
    let start = pt(100., 100.);
    let end = pt(130., 100.);
    let ps = vec![
        primitive(
            "swing",
            None,
            Shape::Arc {
                center: start,
                radius: 30.,
                start_angle: std::f64::consts::FRAC_PI_2,
                sweep: -std::f64::consts::FRAC_PI_2,
            },
        ),
        primitive(
            "j1",
            None,
            Shape::Line {
                start: pt(start.x, 98.),
                end: pt(start.x, 102.),
            },
        ),
        primitive(
            "j2",
            None,
            Shape::Line {
                start: pt(end.x, 98.),
                end: pt(end.x, 102.),
            },
        ),
    ];
    let (a, b) = (pt(50., 100.), pt(180., 100.));
    (
        page(ps),
        if reverse {
            wall("host", b, a)
        } else {
            wall("host", a, b)
        },
    )
}
#[test]
fn finite_jamb_door_tracks_real_arc_endpoint_on_reversed_host() {
    for reverse in [true, false] {
        let (p, w) = door_case(reverse);
        let out = openings::detect(&p, std::slice::from_ref(&w), &Options::default()).unwrap();
        assert_eq!(out.len(), 1);
        let d = &out[0];
        assert_eq!(d.start, pt(100., 100.));
        assert_eq!(d.end, pt(130., 100.));
        assert_eq!(d.source_ids, ["j1", "j2", "swing"]);
        let (iv, solid) = export::coverage(&w, &out);
        assert_eq!(iv.len(), 1);
        assert_eq!(solid.len(), 2);
        assert!((solid.iter().map(|s| s.start.distance(s.end)).sum::<f64>() - 100.).abs() < 1e-8);
    }
}

#[test]
fn a_ready_room_wall_keeps_its_signed_door_and_its_two_solid_spans() {
    let (mut drawing, host) = door_case(false);
    let far = pt(host.end.x, 190.);
    let near = pt(host.start.x, 190.);
    for (id, start, end) in [
        ("room-host", host.start, host.end),
        ("return", host.end, far),
        ("back", far, near),
        ("left", near, host.start),
    ] {
        drawing
            .primitives
            .push(primitive(id, Some("A-WALL"), Shape::Line { start, end }));
    }
    drawing.primitives.push(primitive(
        "room-use",
        Some("A-ROOM-NAME"),
        Shape::Text {
            at: pt(110., 145.),
            value: "OFFICE".into(),
            height: 8.,
        },
    ));
    let document = Document {
        pages: vec![drawing],
        warnings: vec![],
    };
    let scan = pipeline::scan_document(document, &Options::default(), None, false).unwrap();
    export::validate(&scan).unwrap();
    let output = export::placement(&scan);
    let page = &output["pages"][0];
    let doors = page["openings"].as_array().unwrap();
    assert_eq!(doors.len(), 1);
    let door = &doors[0];
    assert_eq!(door["placementHostWallIds"], door["hostWallIds"]);
    assert_eq!(door["hinge"], serde_json::json!(pt(100., 100.)));
    let wall_id = &door["hostWallId"];
    let barrier = page["barriers"]
        .as_array()
        .unwrap()
        .iter()
        .find(|b| &b["wallId"] == wall_id)
        .unwrap();
    let spans = barrier["spans"].as_array().unwrap();
    assert_eq!(spans.len(), 2);
    let lengths: f64 = spans
        .iter()
        .map(|s| {
            let a: Point = serde_json::from_value(s["start"].clone()).unwrap();
            let b: Point = serde_json::from_value(s["end"].clone()).unwrap();
            a.distance(b)
        })
        .sum();
    assert!((lengths - 100.).abs() < 1e-8);
    let marked = page["wallGraph"]["edges"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|e| e["openingIds"].as_array().unwrap().contains(&door["id"]))
        .count();
    assert_eq!(marked, 1);
}
#[test]
fn one_jamb_and_a_nearby_extension_are_insufficient() {
    let (mut p, w) = door_case(false);
    p.primitives[2].shape = Shape::Line {
        start: pt(130., 108.),
        end: pt(130., 114.),
    };
    assert!(
        openings::detect(&p, &[w], &Options::default())
            .unwrap()
            .is_empty()
    )
}
#[test]
fn hosted_door_marks_only_graph_edges_between_its_jambs() {
    for reversed in [false, true] {
        let (p, host) = door_case(reversed);
        let doors = openings::detect(&p, std::slice::from_ref(&host), &Options::default()).unwrap();
        assert_eq!(doors.len(), 1);
        let adjacent = wall("parallel", pt(50., 110.), pt(180., 110.));
        let crossing = wall("crossing", pt(120., 80.), pt(120., 125.));
        let t =
            topology::build(&p, &[host, adjacent, crossing], &doors, &Options::default()).unwrap();
        let mut portions = Vec::new();
        for e in &t.edges {
            if !e.opening_ids.is_empty() {
                assert_eq!(e.opening_ids, [doors[0].id.as_str()]);
                assert_eq!(e.wall_ids, ["host"]);
                let a = t.nodes[e.start_node].position;
                let b = t.nodes[e.end_node].position;
                assert!((a.y - 100.).abs() < 1e-6 && (b.y - 100.).abs() < 1e-6);
                portions.push((a.x.min(b.x), a.x.max(b.x)));
            }
        }
        portions.sort_by(|a, b| a.0.total_cmp(&b.0));
        assert_eq!(portions.len(), 2);
        for (actual, expected) in portions.iter().zip([(100., 120.), (120., 130.)]) {
            assert!((actual.0 - expected.0).abs() < 1e-6 && (actual.1 - expected.1).abs() < 1e-6);
        }
    }
}
#[test]
fn ambiguous_parallel_hosts_do_not_cut_either_wall() {
    let (p, w) = door_case(false);
    assert!(
        openings::detect(
            &p,
            &[w, wall("other", pt(50., 101.), pt(180., 101.))],
            &Options::default()
        )
        .unwrap()
        .is_empty()
    );
}
#[test]
fn geometric_wall_gap_door_preserves_both_observed_endpoints() {
    let (p, _) = door_case(false);
    let a = wall("a", pt(50., 100.), pt(100., 100.));
    let b = wall("b", pt(130., 100.), pt(180., 100.));
    let o = openings::detect(&p, &[a.clone(), b.clone()], &Options::default()).unwrap();
    assert_eq!(o.len(), 1);
    assert_eq!(o[0].host_wall_ids, ["a", "b"]);
    for w in [a, b] {
        let (iv, sp) = export::coverage(&w, &o);
        assert!(iv.is_empty());
        assert_eq!(sp[0].start, w.start);
        assert_eq!(sp[0].end, w.end)
    }
}
#[test]
fn branching_plan_returns_two_faces_and_retains_outside_wall_tail() {
    let w = vec![
        wall("a", pt(-10., 0.), pt(100., 0.)),
        wall("b", pt(100., 0.), pt(100., 80.)),
        wall("c", pt(0., 80.), pt(100., 80.)),
        wall("d", pt(0., 0.), pt(0., 80.)),
        wall("e", pt(50., 0.), pt(50., 80.)),
    ];
    let t = topology::build(&page(vec![]), &w, &[], &Options::default()).unwrap();
    let areas: Vec<_> = t.rooms.iter().map(|r| r.area).collect();
    assert_eq!(areas, [4000., 4000.]);
    assert_eq!(w[0].start, pt(-10., 0.));
    assert!(t.nodes.iter().any(|n| n.position == pt(-10., 0.)));
    assert!(
        t.nodes
            .iter()
            .any(|n| n.position.distance(pt(50., 0.)) < 1e-9 && n.wall_ids == ["a", "e"])
    );
}
#[test]
fn open_chain_is_not_a_room() {
    let t = topology::build(
        &page(vec![]),
        &[
            wall("a", pt(0., 0.), pt(100., 0.)),
            wall("b", pt(100., 0.), pt(100., 80.)),
        ],
        &[],
        &Options::default(),
    )
    .unwrap();
    assert!(t.rooms.is_empty())
}
#[test]
fn public_dxf_legacy_semantic_smoke_targets() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let s = pipeline::scan_file(&path, &Options::default(), None).unwrap();
    export::validate(&s).unwrap();
    let p = &s.pages[0];
    assert_eq!(p.units_to_millimeters, Some(1.));
    assert_eq!(
        p.rooms
            .iter()
            .filter(|r| r.label.as_deref() == Some("OFFICE"))
            .count(),
        1
    );
    assert_eq!(
        p.openings
            .iter()
            .filter(|o| o.kind == OpeningKind::Door && o.operation == "Hinged")
            .count(),
        1
    );
    assert!(p.objects.iter().any(|o| o.category == "HVACEquipment"));
    assert!(
        p.object_groups
            .iter()
            .any(|g| g.member_ids.len() >= 2 && g.requires_review)
    );
}
#[test]
fn conflicting_scales_are_not_silently_resolved() {
    let text = |i: &str, s: &str| {
        primitive(
            i,
            None,
            Shape::Text {
                at: pt(0., 0.),
                value: s.into(),
                height: 10.,
            },
        )
    };
    let doc = Document {
        pages: vec![page(vec![
            text("a", "SCALE 1:100"),
            text("b", "SCALE 1:50"),
        ])],
        warnings: vec![],
    };
    let s = pipeline::scan_document(doc, &Options::default(), None, true).unwrap();
    assert_eq!(s.pages[0].units_to_millimeters, None);
}
#[test]
fn source_references_and_georeferencing_are_checked() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let mut s = pipeline::scan_file(&path, &Options::default(), None).unwrap();
    assert!(export::spatial(&s, Some([0., 0., 0., 0., 0., 0.])).is_err());
    assert!(export::spatial(&s, Some([1., 0., 400., 0., 1., 100.])).is_err());
    let geo = export::spatial(&s, Some([0.00001, 0., 10., 0., -0.00001, 55.])).unwrap();
    assert_eq!(geo["type"], "FeatureCollection");
    s.pages[0].walls[0].source_ids.push("missing".into());
    assert!(export::validate(&s).is_err());
}

#[test]
fn withheld_observations_are_auditable_and_absent_from_selected_outputs() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let mut scan = pipeline::scan_file(&path, &Options::default(), None).unwrap();
    let original = scan.pages[0].walls[0].clone();
    let mut rejected = original.clone();
    rejected.id = "withheld-fixture".into();
    scan.pages[0]
        .withheld_walls
        .as_mut()
        .unwrap()
        .push(WithheldWall {
            furniture_outline: None,
            witness_source_ids: rejected.source_ids.clone(),
            wall: rejected,
            reason: WallSelectionReason::OutlineEdgePair,
        });
    export::validate(&scan).unwrap();
    assert_eq!(
        export::structure(&scan)["selection"],
        "SelectedPhysicalWalls"
    );
    for selected in [
        export::structure(&scan),
        export::placement(&scan),
        export::spatial(&scan, None).unwrap(),
    ] {
        assert!(!selected.to_string().contains("withheld-fixture"));
    }
    assert!(!export::svg(&scan.pages[0], true).contains("withheld-fixture"));
    let entry = scan.pages[0]
        .withheld_walls
        .as_mut()
        .unwrap()
        .last_mut()
        .unwrap();
    entry.wall.id = original.id;
    assert!(export::validate(&scan).is_err());
    let entry = scan.pages[0]
        .withheld_walls
        .as_mut()
        .unwrap()
        .last_mut()
        .unwrap();
    entry.wall.id = "withheld-fixture".into();
    entry.witness_source_ids = vec!["missing-source".into()];
    assert!(export::validate(&scan).is_err());
}

#[test]
fn legacy_observations_and_new_selection_have_distinct_validated_schemas() {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let scan = pipeline::scan_file(&path, &Options::default(), None).unwrap();
    let mut json = serde_json::to_value(scan).unwrap();
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("withheldWalls");
    let missing: Scan = serde_json::from_value(json.clone()).unwrap();
    assert!(export::validate(&missing).is_err());
    json["schemaVersion"] = "openplantrace.rust.scan.v1".into();
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("exteriorEnclosures");
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("exteriorConnections");
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementPaintIds");
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementPolygonPaintIds");
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("finishVisibility");
    json["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementDecisions");
    let old: Scan = serde_json::from_value(json).unwrap();
    export::validate(&old).unwrap();
    assert_eq!(
        export::structure(&old)["selection"],
        "ObservedPhysicalWalls"
    );
}
