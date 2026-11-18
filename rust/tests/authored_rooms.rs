use openplantrace_rs::{export, model::*, pipeline};

fn ring(id: &str, layer: Option<&str>, vertices: &[(f64, f64)]) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: layer.map(str::to_owned),
        shape: Shape::Polyline {
            points: vertices.iter().map(|&(x, y)| Point::new(x, y)).collect(),
            closed: true,
            filled: layer.is_none(),
        },
    }
}
fn text(id: &str, value: &str, x: f64, y: f64, layer: Option<&str>) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: layer.map(str::to_owned),
        shape: Shape::Text {
            at: Point::new(x, y),
            value: value.into(),
            height: 1.,
        },
    }
}
fn scan(primitives: Vec<Primitive>, scale: Option<f64>) -> Scan {
    let doc = Document {
        pages: vec![Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            material_tiles: vec![],
            width: 300.,
            height: 200.,
            primitives,
            units_to_mm: scale,
        }],
        warnings: vec![],
    };
    let scan = pipeline::scan_document(doc, &Options::default(), None, true).unwrap();
    export::validate(&scan).unwrap();
    scan
}
fn rect(id: &str, layer: Option<&str>, x: f64, y: f64, w: f64, h: f64) -> Primitive {
    ring(id, layer, &[(x, y), (x + w, y), (x + w, y + h), (x, y + h)])
}
#[test]
fn explicit_cad_perimeter_preserves_a_concave_room_and_its_source_label() {
    let outline = ring(
        "outline",
        Some("A-ROOM"),
        &[
            (0., 0.),
            (0., 40.),
            (20., 40.),
            (20., 20.),
            (40., 20.),
            (40., 0.),
            (0., 0.),
        ],
    );
    let name = text("name", "OFFICE", 8., 8., Some("A-ROOM-TEXT"));
    let scan = scan(vec![outline, name], None);
    let rooms = &scan.pages[0].rooms;
    assert_eq!(rooms.len(), 1);
    let r = &rooms[0];
    assert_eq!(r.area, 1200.);
    assert_eq!(r.polygon.len(), 6);
    assert_eq!(r.label.as_deref(), Some("OFFICE"));
    assert_eq!(
        r.boundary_evidence,
        Some(RoomBoundaryEvidence::RoomLayerPerimeter {
            source_id: "outline".into()
        })
    );
    // A forged polygon with the same area and a valid primitive reference fails replay.
    let mut tampered = scan.clone();
    for pt in &mut tampered.pages[0].rooms[0].polygon {
        pt.x += 2.;
    }
    assert!(export::validate(&tampered).is_err());
}

#[test]
fn broken_nested_overlapping_and_annotation_rings_do_not_create_authored_rooms() {
    let layer = Some("A-ROOM");
    let outer = rect("outer", layer, 0., 0., 80., 70.);
    let tiny_island = rect("island", layer, 10., 10., 5., 4.);
    let crossing = rect("cross", layer, 60., 20., 50., 20.);
    let duplicate = rect("duplicate", layer, 0., 0., 80., 70.);
    let closed_spur = ring(
        "spur",
        layer,
        &[
            (0., 0.),
            (80., 0.),
            (80., 60.),
            (40., 60.),
            (40., 50.),
            (40., 60.),
            (0., 60.),
        ],
    );
    let non_simple = ring(
        "self-cross",
        layer,
        &[(0., 0.), (30., 40.), (0., 60.), (60., 5.), (60., 60.)],
    );
    let mut open = outer.clone();
    if let Shape::Polyline { closed, .. } = &mut open.shape {
        *closed = false;
    }
    for primitives in [
        vec![outer.clone(), tiny_island],
        vec![outer.clone(), crossing],
        vec![outer, duplicate],
        vec![closed_spur],
        vec![non_simple],
        vec![open],
        vec![rect("tag", Some("A-ROOM-TAG"), 0., 0., 40., 40.)],
    ] {
        assert!(
            scan(primitives, None).pages[0]
                .rooms
                .iter()
                .all(|r| r.boundary_evidence.is_none())
        );
    }
    let adjacent = scan(
        vec![
            rect("a", layer, 0., 0., 30., 30.),
            rect("b", layer, 30., 0., 30., 30.),
        ],
        None,
    );
    assert_eq!(adjacent.pages[0].rooms.len(), 2);
}

#[test]
fn printed_areas_corroborate_source_room_fills_without_inventing_calibration() {
    let mut shapes = vec![];
    for (id, x, width, m2) in [
        ("one", 10., 30., "9 m²"),
        ("two", 70., 40., "12 m²"),
        ("three", 140., 50., "15 m²"),
    ] {
        shapes.push(rect(id, None, x, 10., width, 30.));
        shapes.push(text(&format!("{id}-area"), m2, x + 10., 20., None));
    }
    let accepted = scan(shapes.clone(), None);
    let page = &accepted.pages[0];
    assert!(
        page.rooms.is_empty(),
        "unwalled floor areas include terraces and open zones"
    );
    assert_eq!(page.units_to_millimeters, None);
    for a in &page.room_annotations {
        assert!(a.room_id.is_none());
        let foot = a.floor_footprint.as_ref().unwrap();
        assert_eq!(foot.witness_source_ids.len(), 6);
        assert_eq!(foot.polygon.len(), 4);
    }
    let mut forged = accepted;
    forged.pages[0].room_annotations[0]
        .floor_footprint
        .as_mut()
        .unwrap()
        .witness_source_ids
        .pop();
    assert!(export::validate(&forged).is_err());
    assert!(
        scan(shapes.clone(), Some(10.)).pages[0]
            .room_annotations
            .iter()
            .all(|a| a.floor_footprint.is_none())
    );
    shapes.truncate(4);
    assert!(
        scan(shapes, None).pages[0]
            .room_annotations
            .iter()
            .all(|a| a.floor_footprint.is_none())
    );
}
