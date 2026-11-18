use openplantrace_rs::{export, model::*, pipeline};

fn room(id: &str, x: f64, y: f64, w: f64, h: f64, text: &str) -> Vec<Primitive> {
    let p = |dx, dy| Point::new(x + dx, y + dy);
    vec![
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: format!("{id}-fill"),
            layer: None,
            shape: Shape::Polyline {
                points: vec![p(0., 0.), p(w, 0.), p(w, h), p(0., h)],
                closed: true,
                filled: true,
            },
        },
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: format!("{id}-area"),
            layer: None,
            shape: Shape::Text {
                at: p(w / 2., h / 2.),
                value: text.into(),
                height: 1.,
            },
        },
    ]
}
fn run(primitives: Vec<Primitive>, scale: Option<f64>) -> Scan {
    let page = Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        placement_paint_ids: None,
        placement_polygon_paint_ids: None,
        material_tiles: vec![],
        width: 400.,
        height: 200.,
        units_to_mm: scale,
        primitives,
    };
    let scan = pipeline::scan_document(
        Document {
            pages: vec![page],
            warnings: vec![],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap();
    export::validate(&scan).unwrap();
    scan
}

#[test]
fn a_corridor_fill_requires_independent_area_and_material_evidence() {
    let corridor = room("corridor", 10., 10., 60., 6., "3,6 m²");
    let space_a = room("a", 10., 30., 40., 30., "12 m²");
    let space_b = room("b", 60., 30., 50., 40., "20 m²");
    let mut shapes = corridor.clone();
    shapes.extend(space_a.clone());
    shapes.extend(space_b.clone());
    let scan = run(shapes.clone(), None);
    let p = &scan.pages[0];
    assert!(p.units_to_millimeters.is_none());
    let record = p
        .withheld_walls
        .as_ref()
        .unwrap()
        .iter()
        .find(|w| w.reason == WallSelectionReason::PrintedRoomArea)
        .unwrap();
    assert_eq!(record.wall.source_ids, ["corridor-fill"]);
    assert_eq!(
        record.witness_source_ids,
        [
            "a-area",
            "a-fill",
            "b-area",
            "b-fill",
            "corridor-area",
            "corridor-fill"
        ]
    );
    assert!(
        p.walls
            .iter()
            .all(|w| w.source_ids.iter().all(|id| id != "corridor-fill"))
    );
    let mut corrupted = scan.clone();
    let record = corrupted.pages[0]
        .withheld_walls
        .as_mut()
        .unwrap()
        .iter_mut()
        .find(|w| w.reason == WallSelectionReason::PrintedRoomArea)
        .unwrap();
    record.witness_source_ids.retain(|id| id != "b-fill");
    assert!(export::validate(&corrupted).is_err());

    let retains = |scan: Scan| {
        scan.pages[0]
            .walls
            .iter()
            .any(|w| w.source_ids.iter().any(|id| id == "corridor-fill"))
    };
    let mut only_two = corridor.clone();
    only_two.extend(space_a.clone());
    assert!(retains(run(only_two, None)));
    assert!(retains(run(shapes.clone(), Some(10.))));
    // Stacked duplicated fills with one area label cannot act as two separate spaces.
    let mut overprint = shapes.clone();
    let mut duplicate = space_b[0].clone();
    duplicate.id = "duplicate-b".into();
    overprint.push(duplicate);
    assert!(retains(run(overprint, None)));
    let mut different = shapes.clone();
    if let Shape::Text { value, .. } = &mut different[5].shape {
        *value = "2 m²".into();
    }
    assert!(retains(run(different, None)));
    shapes[0].layer = Some("A-WALL".into());
    assert!(retains(run(shapes, None)));
}
