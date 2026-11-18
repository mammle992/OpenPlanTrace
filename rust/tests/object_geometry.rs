use openplantrace_rs::{export, model::*, object_labels, pipeline};
use serde_json::json;

fn basin() -> Scan {
    let primitive = |id: &str, shape| Primitive {
        id: id.into(),
        layer: None,
        stroke_width: Some(0.2),
        layer_category_override: None,
        shape,
    };
    let mut rim = vec![Point::new(20., 20.), Point::new(70., 20.)];
    rim.extend((0..=16).map(|i| {
        let a = std::f64::consts::PI * i as f64 / 16.;
        Point::new(45., 30.) + Point::new(a.cos(), a.sin()) * 25.
    }));
    let drain = (0..32)
        .map(|i| {
            let a = std::f64::consts::TAU * i as f64 / 32.;
            Point::new(45., 37.5) + Point::new(a.cos(), a.sin()) * 1.5
        })
        .collect();
    let mut filled = primitive(
        "drain",
        Shape::Polyline {
            points: drain,
            closed: true,
            filled: true,
        },
    );
    filled.stroke_width = None;
    let doc = Document {
        warnings: vec![],
        pages: vec![Page {
            finish_history: None,
            number: 1,
            width: 400.,
            height: 300.,
            units_to_mm: None,
            material_tiles: vec![],
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            pdf_input: None,
            primitives: vec![
                primitive(
                    "rim",
                    Shape::Polyline {
                        points: rim,
                        closed: true,
                        filled: false,
                    },
                ),
                primitive(
                    "bowl",
                    Shape::Arc {
                        center: Point::new(45., 37.5),
                        radius: 12.,
                        start_angle: 0.,
                        sweep: std::f64::consts::TAU,
                    },
                ),
                filled,
                primitive(
                    "tap",
                    Shape::Line {
                        start: Point::new(45., 22.),
                        end: Point::new(45., 25.),
                    },
                ),
            ],
        }],
    };
    pipeline::scan_document(doc, &Options::default(), None, true).unwrap()
}

#[test]
fn measured_object_survives_json_and_exports_its_real_outline() {
    let s = basin();
    assert_eq!(s.pages[0].objects.len(), 1);
    assert_eq!(s.pages[0].objects[0].name, "Sink");
    assert!(s.pages[0].objects[0].requires_review);
    let saved: Scan = serde_json::from_slice(&serde_json::to_vec(&s).unwrap()).unwrap();
    export::validate(&saved).unwrap();
    let local = export::spatial(&saved, None).unwrap();
    assert!(
        local["features"]
            .as_array()
            .unwrap()
            .iter()
            .any(|v| v["geometry"]["type"] == "Polygon")
    );
}

#[test]
fn source_replay_rejects_changed_and_disguised_geometry() {
    let s = basin();
    for label in [
        "outline",
        "witness",
        "missing",
        "disguised",
        "review",
        "schema",
    ] {
        let mut changed = s.clone();
        let object = &mut changed.pages[0].objects[0];
        match label {
            "outline" => object.geometry.as_mut().unwrap().polygon[0].x -= 1.,
            "witness" => object.source_ids.pop().map(|_| ()).unwrap(),
            "missing" => object.geometry = None,
            "disguised" => {
                object.geometry = None;
                object.id = "p1:object:1".into();
                object.name = "Unclassified vector symbol".into();
                object.category = "GenericSymbol".into();
            }
            "review" => object.requires_review = false,
            _ => changed.schema_version = "openplantrace.rust.scan.v10".into(),
        }
        assert!(export::validate(&changed).is_err(), "{label}");
    }
}

#[test]
fn explicit_label_profiles_keep_measured_candidates_under_review() {
    let mut s = basin();
    let profile=object_labels::Profile::parse(json!({"rules":[{"matchCategory":"PlumbingFixture","label":"Wash basin","requiresReview":false}]})).unwrap();
    object_labels::apply(&mut s, "PDF", &[profile]).unwrap();
    assert_eq!(s.pages[0].objects[0].name, "Wash basin");
    assert!(s.pages[0].objects[0].requires_review);
    export::validate(&s).unwrap();
}
