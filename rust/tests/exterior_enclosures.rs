use openplantrace_rs::{export, model::*, pipeline};

fn outlined_house(leaf: bool, angle: f64) -> Scan {
    let axis = Point::new(angle.cos(), angle.sin());
    let at = |x, y| Point::new(200., 200.) + axis * x + Point::new(-axis.y, axis.x) * y;
    let mut primitives = Vec::new();
    let mut line = |name: &str, x, y, xx, yy, width| {
        primitives.push(Primitive {
            id: name.into(),
            layer: None,
            layer_category_override: None,
            stroke_width: Some(width),
            shape: Shape::Line {
                start: at(x, y),
                end: at(xx, yy),
            },
        })
    };
    for (id, x, y, xx, yy) in [
        ("north-out", 20., 20., 140., 20.),
        ("north-in", 30., 30., 130., 30.),
        ("west-out", 20., 20., 20., 100.),
        ("west-in", 30., 30., 30., 90.),
        ("south-out", 20., 100., 140., 100.),
        ("south-in", 30., 90., 130., 90.),
        ("east-top-out", 140., 20., 140., 50.),
        ("east-top-in", 130., 30., 130., 50.),
        ("east-bottom-out", 140., 70., 140., 100.),
        ("east-bottom-in", 130., 70., 130., 90.),
        ("jamb-top", 130., 50., 140., 50.),
        ("jamb-bottom", 130., 70., 140., 70.),
    ] {
        line(id, x, y, xx, yy, 1.);
    }
    for i in 0..16 {
        let y = 130. + i as f64 * 3.;
        line(&format!("ordinary-{i}"), 20., y, 23., y, 0.5);
    }
    if leaf {
        primitives.push(Primitive {
            id: "leaf".into(),
            layer: None,
            layer_category_override: None,
            stroke_width: Some(0.5),
            shape: Shape::Polyline {
                points: vec![at(135., 50.), at(153., 50.), at(153., 51.), at(135., 51.)],
                closed: true,
                filled: false,
            },
        });
    }
    pipeline::scan_document(
        Document {
            pages: vec![Page {
                finish_history: None,
                pdf_input: None,
                number: 1,
                width: 600.,
                height: 600.,
                units_to_mm: None,
                primitives,
                material_tiles: vec![],
                placement_paint_ids: None,
                placement_polygon_paint_ids: None,
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
            }],
            warnings: vec![],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap()
}

#[test]
fn native_scan_owns_and_replays_its_exterior_findings() {
    let scan = outlined_house(true, 0.6);
    let encoded = openplantrace_rs::compact::encode(&scan).unwrap();
    let decoded = openplantrace_rs::compact::decode(&encoded).unwrap();
    assert_eq!(
        decoded["pages"][0]["exteriorEnclosures"],
        export::placement(&scan)["pages"][0]["exteriorEnclosures"]
    );
    let restored: Scan = serde_json::from_value(decoded).unwrap();
    export::validate(&restored).unwrap();

    let mut altered = restored.clone();
    let records = altered.pages[0].exterior_enclosures.as_mut().unwrap();
    assert_eq!(records.len(), 1);
    records[0].area_drawing_units_squared += 1.;
    assert!(
        export::validate(&altered)
            .unwrap_err()
            .contains("exterior enclosure")
    );
    // Export projects recorded data. Only validation replays the measurement.
    assert_eq!(
        export::placement(&altered)["pages"][0]["exteriorEnclosures"][0]["areaDrawingUnitsSquared"],
        altered.pages[0].exterior_enclosures.as_ref().unwrap()[0].area_drawing_units_squared
    );
    altered.pages[0].exterior_enclosures = Some(vec![]);
    assert!(export::validate(&altered).is_err());
    altered.pages[0].exterior_enclosures = None;
    assert!(
        export::validate(&altered)
            .unwrap_err()
            .contains("missing exterior enclosure inventory")
    );
}

#[test]
fn source_door_threshold_closes_exterior_without_a_barrier() {
    for angle in [0., 0.6] {
        let scan = outlined_house(true, angle);
        export::validate(&scan).unwrap();
        let packet = export::placement(&scan);
        let enclosures = packet["pages"][0]["exteriorEnclosures"].as_array().unwrap();
        assert_eq!(enclosures.len(), 1, "{packet}");
        let e = &enclosures[0];
        assert_eq!(e["closed"], true);
        assert_eq!(e["requiresReview"], true);
        assert!((e["areaDrawingUnitsSquared"].as_f64().unwrap() - 9600.).abs() < 1e-5);
        assert!(
            e["boundaryEdges"]
                .as_array()
                .unwrap()
                .iter()
                .any(|v| v["evidence"] == "capped_gap_with_open_leaf" && v["kind"] == "opening")
        );
        assert!(!packet["pages"][0]["barriers"].to_string().contains("leaf"));
    }
}

#[test]
fn an_unproved_gap_stays_open_and_wall_material_is_not_a_house_area() {
    let scan = outlined_house(false, 0.);
    export::validate(&scan).unwrap();
    let packet = export::placement(&scan);
    assert!(
        packet["pages"][0]["exteriorEnclosures"]
            .as_array()
            .unwrap()
            .is_empty()
    );
}
