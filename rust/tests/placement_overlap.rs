use openplantrace_rs::{export, model::*, pipeline};
use serde_json::{Value, json};

fn stroke(id: &str, layer: &str, start: (f64, f64), end: (f64, f64)) -> Primitive {
    Primitive {
        id: id.into(),
        layer: Some(layer.into()),
        layer_category_override: None,
        stroke_width: None,
        shape: Shape::Line {
            start: Point::new(start.0, start.1),
            end: Point::new(end.0, end.1),
        },
    }
}

// An outlined partition stops before the door. Two longer lines also continue
// across the aperture as drawn. The scanner assigns the swing to that wider host.
fn drawing(inner_layer: &str) -> Vec<Primitive> {
    let wall = "Partition";
    let mut ink = Vec::new();
    for (name, y) in [("bottom", 96.), ("top", 104.)] {
        ink.push(stroke(
            &format!("inner-{name}"),
            inner_layer,
            (100., y),
            (20., y),
        ));
    }
    for (name, y) in [("bottom", 95.), ("top", 105.)] {
        for (part, from, to) in [
            ("solid", 20., 100.),
            ("frame", 100., 155.),
            ("far", 155., 255.),
        ] {
            ink.push(stroke(
                &format!("outer-{name}-{part}"),
                wall,
                (from, y),
                (to, y),
            ));
        }
        ink.push(stroke(
            &format!("area-{name}"),
            "Area outline",
            (20., y),
            (255., y),
        ));
    }
    for (id, x, y1, y2) in [
        ("stop", 100., 96., 104.),
        ("hinge-jamb", 103., 95., 105.),
        ("latch-jamb", 152., 95., 105.),
    ] {
        ink.push(stroke(id, "Door", (x, y1), (x, y2)));
    }
    ink.push(stroke("open-leaf", "Door", (103., 100.), (103., 149.)));
    ink.push(Primitive {
        id: "swing".into(),
        layer: Some("Door".into()),
        layer_category_override: None,
        stroke_width: None,
        shape: Shape::Arc {
            center: Point::new(103., 100.),
            radius: 49.,
            start_angle: 0.,
            sweep: std::f64::consts::FRAC_PI_2,
        },
    });
    ink
}

fn packet(primitives: Vec<Primitive>) -> Value {
    let doc = Document {
        warnings: Vec::new(),
        pages: vec![Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 300.,
            height: 250.,
            units_to_mm: None,
            primitives,
        }],
    };
    let scan = pipeline::scan_document(doc, &Options::default(), None, false).unwrap();
    export::validate(&scan).unwrap();
    export::placement(&scan)["pages"][0].take()
}

#[test]
fn selected_short_partition_is_the_only_barrier_and_the_door_keeps_its_measured_host() {
    let page = packet(drawing("Partition"));
    let walls = page["walls"].as_array().unwrap();
    assert_eq!(walls.len(), 2);
    let flank = walls.iter().find(|w| w["thickness"] == 8.).unwrap();
    let host = walls.iter().find(|w| w["thickness"] == 10.).unwrap();
    let (flank_id, host_id) = (&flank["wallId"], &host["wallId"]);
    assert_eq!(page["wallSets"]["readyWallIds"], json!([flank_id]));
    assert_eq!(page["wallSets"]["reviewWallIds"], json!([host_id]));

    let barriers = page["barriers"].as_array().unwrap();
    assert_eq!(barriers.len(), 1);
    assert_eq!(&barriers[0]["wallId"], flank_id);
    let spans = barriers[0]["spans"].as_array().unwrap();
    assert_eq!(spans.len(), 1);
    assert_eq!(spans[0]["start"], json!({"x":20.,"y":100.}));
    assert_eq!(spans[0]["end"], json!({"x":100.,"y":100.}));
    assert_eq!(page["wallGraph"]["edges"].as_array().unwrap().len(), 1);
    assert_eq!(page["wallGraph"]["edges"][0]["wallIds"], json!([flank_id]));
    assert_eq!(page["wallGraph"]["edges"][0]["openingIds"], json!([]));

    let openings = page["openings"].as_array().unwrap();
    assert_eq!(openings.len(), 1);
    let door = &openings[0];
    assert_eq!(door["hostWallIds"], json!([host_id]));
    assert_eq!(door["placementHostWallIds"], json!([]));
    assert_eq!(door["start"], json!({"x":103.,"y":100.}));
    assert_eq!(door["end"], json!({"x":152.,"y":100.}));
    let swing = &door["swingObservations"][0];
    assert_eq!(swing["arcSourceId"], "swing");
    assert_eq!(swing["hinge"], door["start"]);
    assert_eq!(swing["closedTip"], door["end"]);
    assert_eq!(swing["openTip"], json!({"x":103.,"y":149.}));
    assert_eq!(host["openingIntervals"].as_array().unwrap().len(), 1);
}

#[test]
fn furniture_and_glazing_outlines_cannot_borrow_the_nearby_signed_opening() {
    for layer in ["Cabinet", "Partition glass"] {
        let page = packet(drawing(layer));
        assert_eq!(page["walls"].as_array().unwrap().len(), 2);
        assert_eq!(page["openings"].as_array().unwrap().len(), 1);
        assert_eq!(page["barriers"], json!([]), "{layer}");
        assert_eq!(page["wallGraph"]["edges"], json!([]), "{layer}");
    }
}

#[test]
fn unequal_face_lengths_at_a_door_need_matching_material_on_both_sides() {
    for (depth, upper_skin, outer_start, ready) in [
        (1., true, 21., true),
        (1., false, 21., false),
        (0.5, true, 21., false),
        (1., true, 24., false),
    ] {
        let mut ink = drawing("Partition");
        for p in &mut ink {
            let x = match p.id.as_str() {
                "outer-top-solid" => outer_start,
                "outer-bottom-solid" => 19.,
                _ => continue,
            };
            let Shape::Line { start, .. } = &mut p.shape else {
                unreachable!()
            };
            start.x = x;
        }
        for (side, y, sign) in [("lower", 96., -1.), ("upper", 104., 1.)] {
            if sign > 0. && !upper_skin {
                continue;
            }
            for x in (22..100).step_by(4) {
                let at = f64::from(x);
                ink.push(stroke(
                    &format!("skin-{side}-{x}"),
                    "Partition",
                    (at, y),
                    (at + sign * depth, y + sign * depth),
                ));
            }
        }
        let page = packet(ink);
        let walls = page["walls"].as_array().unwrap();
        let flank = walls.iter().find(|w| w["thickness"] == 8.).unwrap();
        let host = walls.iter().find(|w| w["thickness"] == 10.).unwrap();
        let door = &page["openings"][0];
        assert_eq!(door["hostWallIds"], json!([host["wallId"]]));
        assert_eq!(door["placementHostWallIds"], json!([]));
        assert_eq!(host["decision"]["status"], "Review");
        let selected = &page["wallSets"]["readyWallIds"];
        if ready {
            assert_eq!(*selected, json!([flank["wallId"]]));
            let sources = flank["decision"]["supportSourceIds"].as_array().unwrap();
            for witness in [
                "skin-lower-22",
                "skin-upper-98",
                "outer-top-solid",
                "outer-bottom-solid",
                "stop",
                "swing",
            ] {
                assert!(sources.contains(&json!(witness)), "{witness}");
            }
        } else {
            assert_eq!(
                *selected,
                json!([]),
                "depth={depth}, upper_skin={upper_skin}, outer_start={outer_start}"
            );
        }
    }
}
