use openplantrace_rs::{compact, export, model::*, pipeline, placement::connections};
fn scan() -> Scan {
    let line = |id: &str, a, b, width| Primitive {
        id: id.into(),
        layer: None,
        layer_category_override: None,
        stroke_width: Some(width),
        shape: Shape::Line { start: a, end: b },
    };
    let mut primitives = vec![
        line("upper", Point::new(100., 100.), Point::new(180., 100.), 1.),
        line("lower", Point::new(180., 110.), Point::new(100., 110.), 1.),
        line("left", Point::new(100., 110.), Point::new(100., 100.), 1.),
        line("right", Point::new(180., 100.), Point::new(180., 110.), 1.),
    ];
    for i in 0..12 {
        let y = 150. + i as f64 * 3.;
        primitives.push(line(
            &format!("detail-{i}"),
            Point::new(100., y),
            Point::new(103., y),
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
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap()
}

#[test]
fn connection_report_round_trips_without_changing_physical_placement() {
    let current = scan();
    assert_eq!(current.schema_version, "openplantrace.rust.scan.v45");
    export::validate(&current).unwrap();
    let report = current.pages[0].exterior_connections.as_ref().unwrap();
    assert!(!report.nodes.is_empty());
    assert!(!report.edges.is_empty());
    assert!(report.requires_review);
    assert!(report.edges.iter().all(|e| !e.adds_wall_material));
    assert!(report.uncertain_gaps.iter().all(|g| !g.connected));
    assert_eq!(*report, connections::build(&current.pages[0]).unwrap());
    let restored: Scan = serde_json::from_slice(&serde_json::to_vec(&current).unwrap()).unwrap();
    export::validate(&restored).unwrap();
    assert_eq!(
        export::connections(&current),
        export::connections(&restored)
    );
    let decoded: Scan =
        serde_json::from_value(compact::decode(&compact::encode(&current).unwrap()).unwrap())
            .unwrap();
    export::validate(&decoded).unwrap();
    assert_eq!(export::connections(&current), export::connections(&decoded));

    // v11 is the same physical interpretation, with no connection-pass field.
    let mut old = restored;
    old.schema_version = "openplantrace.rust.scan.v11".into();
    old.pages.iter_mut().for_each(|p| {
        p.exterior_connections = None;
        p.placement_polygon_paint_ids = None;
        p.finish_visibility = None;
    });
    export::validate(&old).unwrap();
    let mut placement = export::placement(&current);
    for page in placement["pages"].as_array_mut().unwrap() {
        page.as_object_mut().unwrap().remove("exteriorConnections");
    }
    assert_eq!(placement, export::placement(&old));
    let overlay = export::connections_svg(&current.pages[0]);
    assert!(overlay.contains("boundary-connections"));
    assert!(overlay.contains("data-adds-wall-material=\"false\""));
}

#[test]
fn native_validation_replays_nodes_edges_gaps_and_required_inventory() {
    let current = scan();
    let mut bad = current.clone();
    bad.pages[0].exterior_connections.as_mut().unwrap().nodes[0]
        .position
        .x += 0.01;
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("connection pass disagrees")
    );
    let mut bad = current.clone();
    bad.pages[0].exterior_connections.as_mut().unwrap().edges[0].evidence =
        "nearest point guess".into();
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("connection pass disagrees")
    );
    let mut bad = current.clone();
    bad.pages[0].exterior_connections.as_mut().unwrap().edges[0].adds_wall_material = true;
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("connection pass disagrees")
    );
    let mut bad = current.clone();
    bad.pages[0]
        .exterior_connections
        .as_mut()
        .unwrap()
        .uncertain_gaps
        .clear();
    assert!(
        !current.pages[0]
            .exterior_connections
            .as_ref()
            .unwrap()
            .uncertain_gaps
            .is_empty()
    );
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("connection pass disagrees")
    );
    let mut bad = current.clone();
    bad.pages[0]
        .exterior_connections
        .as_mut()
        .unwrap()
        .uncertain_gaps[0]
        .connected = true;
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("connection pass disagrees")
    );
    let mut bad = current.clone();
    bad.pages[0].exterior_connections = None;
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("missing exterior connection pass")
    );
    let mut bad = current;
    bad.schema_version = "openplantrace.rust.scan.v11".into();
    bad.pages[0].placement_polygon_paint_ids = None;
    bad.pages[0].finish_visibility = None;
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("requires scan v12")
    );
}
