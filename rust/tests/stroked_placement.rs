use openplantrace_rs::{export, model::*, pipeline};

fn drawing(angle: f64, scale: f64, body: f64, cap: bool, contrast: bool, details: bool) -> Scan {
    let axis = Point::new(angle.cos(), angle.sin());
    let at = |x, y| Point::new(100., 100.) + (axis * x + Point::new(-axis.y, axis.x) * y) * scale;
    let line = |id: String, a, b, width| Primitive {
        id,
        layer: None,
        shape: Shape::Line { start: a, end: b },
        layer_category_override: None,
        stroke_width: Some(width * scale),
    };
    let mut primitives = vec![
        line("west-face".into(), at(0., 0.), at(80., 0.), 1.),
        line("east-face".into(), at(80., body), at(0., body), 1.),
        line("start-jamb".into(), at(0., 0.), at(0., body), 1.),
    ];
    if cap {
        primitives.push(line("end-jamb".into(), at(80., body), at(80., 0.), 1.));
    }
    // Independent ordinary pen strokes set the page's source style distribution.
    for i in 0..12 {
        let y = 40. + i as f64 * 3.;
        primitives.push(line(
            format!("detail-{i}"),
            at(0., y),
            at(3., y),
            if contrast { 0.5 } else { 1. },
        ));
    }
    if details {
        for i in 0..5 {
            let x = 8. + i as f64 * 12.;
            primitives.push(line(
                format!("tread-{i}"),
                at(x, body),
                at(x, body + 50.),
                0.5,
            ));
        }
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
                placement_contact_paint: vec![],
            }],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap()
}

fn selected(scan: &Scan) -> Vec<&openplantrace_rs::placement::WallDecision> {
    scan.pages[0]
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .filter(|d| d.reason == openplantrace_rs::placement::WallReason::ContrastingCappedWallFaces)
        .collect()
}

#[test]
fn capped_faces_use_source_style_across_rotations_and_scales() {
    for (angle, scale) in [(0., 1.), (0.7, 1.), (0., 1.5)] {
        let scan = drawing(angle, scale, 10., true, true, false);
        export::validate(&scan).unwrap();
        let added = selected(&scan);
        assert_eq!(
            added.len(),
            1,
            "angle={angle} scale={scale} {:?}",
            scan.pages[0].placement_decisions
        );
        assert_eq!(
            added[0].status,
            openplantrace_rs::placement::WallStatus::Ready
        );
        assert_eq!(added[0].support_source_ids.len(), 4);
        let packet = export::placement(&scan);
        let page = &packet["pages"][0];
        let wall = page["walls"]
            .as_array()
            .unwrap()
            .iter()
            .find(|w| w["wallId"] == added[0].wall_id)
            .unwrap();
        for end in ["start", "end"] {
            assert!(
                page["wallGraph"]["nodes"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|n| n["position"] == wall["centerLine"][end])
            );
        }
    }
}

#[test]
fn thin_frames_uncontrasted_furniture_and_incomplete_ends_stay_under_review() {
    for scan in [
        drawing(0., 1., 4., true, true, false),
        drawing(0., 1., 10., true, false, false),
        drawing(0., 1., 10., false, true, false),
    ] {
        assert!(selected(&scan).is_empty());
        export::validate(&scan).unwrap();
    }
}

#[test]
fn saved_geometry_cannot_claim_a_cap_that_has_no_ink_proof() {
    let mut scan = drawing(0., 1., 10., true, true, false);
    assert_eq!(selected(&scan).len(), 1);
    scan.pages[0]
        .primitives
        .iter_mut()
        .find(|p| p.id == "end-jamb")
        .unwrap()
        .stroke_width = None;
    assert!(export::validate(&scan).is_err());
}

#[test]
fn archived_preview8_retains_its_capped_wall_authority() {
    let mut scan = drawing(0., 1., 10., true, true, false);
    scan.schema_version = "openplantrace.rust.scan.v7".into();
    scan.pages[0].exterior_enclosures = None;
    scan.pages[0].exterior_connections = None;
    scan.pages[0].placement_polygon_paint_ids = None;
    scan.pages[0].finish_visibility = None;
    let mut saved: Scan = serde_json::from_slice(&serde_json::to_vec(&scan).unwrap()).unwrap();
    assert_eq!(selected(&saved).len(), 1);
    export::validate(&saved).unwrap();
    let mut downgraded = saved.clone();
    downgraded.pages[0].walls[0].id = "ink-pair:altered observation".into();
    assert!(
        export::validate(&downgraded)
            .unwrap_err()
            .contains("require scan v8")
    );
    saved.pages[0]
        .primitives
        .iter_mut()
        .find(|p| p.id == "start-jamb")
        .unwrap()
        .stroke_width = None;
    assert!(export::validate(&saved).is_err());
}

#[test]
fn repeated_transverse_treads_withhold_a_stair_rail() {
    for angle in [0., 0.7] {
        let scan = drawing(angle, 1., 10., true, true, true);
        assert!(selected(&scan).is_empty());
        export::validate(&scan).unwrap();
    }
}

fn short_unanchored_rectangle() -> Scan {
    let base = drawing(0., 1., 10., true, true, false);
    let mut primitives = base.pages[0].primitives.clone();
    for p in &mut primitives {
        if ["west-face", "east-face", "end-jamb"].contains(&p.id.as_str()) {
            if let Shape::Line { start, end } = &mut p.shape {
                if start.x == 180. {
                    start.x = 115.;
                }
                if end.x == 180. {
                    end.x = 115.;
                }
            }
        }
    }
    let opts = Options {
        min_wall_length: 4.,
        ..Options::default()
    };
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
                placement_contact_paint: vec![],
            }],
        },
        &opts,
        None,
        true,
    )
    .unwrap()
}

#[test]
fn a_short_original_rectangle_requires_the_same_anchor_as_recovered_ink() {
    let scan = short_unanchored_rectangle();
    assert!(!scan.pages[0].walls.is_empty());
    assert!(
        scan.pages[0]
            .placement_decisions
            .as_ref()
            .unwrap()
            .iter()
            .all(|d| d.status != openplantrace_rs::placement::WallStatus::Ready),
        "{:?} {:?}",
        scan.pages[0].walls,
        scan.pages[0].placement_decisions
    );
}

#[test]
fn saved_recovery_cannot_invent_its_missing_source_anchor() {
    let mut scan = short_unanchored_rectangle();
    let mut forged = scan.pages[0].walls[0].clone();
    forged.id = "ink-pair:invented".into();
    let mut decision = scan.pages[0].placement_decisions.as_ref().unwrap()[0].clone();
    decision.wall_id = forged.id.clone();
    scan.pages[0].walls.push(forged);
    scan.pages[0]
        .placement_decisions
        .as_mut()
        .unwrap()
        .push(decision);
    let page = &mut scan.pages[0];
    let source = Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        width: page.width,
        height: page.height,
        units_to_mm: None,
        primitives: page.primitives.clone(),
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
    };
    page.semantics = openplantrace_rs::semantics::interpret(
        &source,
        &page.walls,
        &page.rooms,
        &page.openings,
        &page.objects,
        &page.room_connections,
        None,
    );
    let error = export::validate(&scan).unwrap_err();
    assert!(error.contains("recovered ink pair"), "{error}");
}

#[test]
fn window_frame_contacts_inside_jamb_strokes_anchor_a_short_pier() {
    let line = |id: String, start: Point, end: Point, width, layer| Primitive {
        id,
        layer,
        layer_category_override: None,
        stroke_width: Some(width),
        shape: Shape::Line { start, end },
    };
    let mut primitives = vec![];
    for (tag, lo, hi) in [("long", 100., 180.), ("short", 200., 215.)] {
        for (side, x) in [("left", 100.), ("right", 110.)] {
            primitives.push(line(
                format!("{tag}-{side}"),
                Point::new(x, lo),
                Point::new(x, hi),
                1.,
                None,
            ));
        }
        for (side, y) in [("top", lo), ("bottom", hi)] {
            primitives.push(line(
                format!("{tag}-{side}"),
                Point::new(100., y),
                Point::new(110., y),
                1.,
                None,
            ));
        }
    }
    for x in [103., 107.] {
        primitives.push(line(
            format!("window-{x}"),
            Point::new(x, 180.),
            Point::new(x, 200.),
            0.5,
            Some("A-WINDOW".into()),
        ));
    }
    for i in 0..12 {
        let x = 200. + i as f64 * 3.;
        primitives.push(line(
            format!("ordinary-{i}"),
            Point::new(x, 100.),
            Point::new(x, 106.),
            0.5,
            None,
        ));
    }
    let scan = pipeline::scan_document(
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
                placement_paint_remainders: vec![],
                white_nested_walls: vec![],
                terminal_stock: vec![],
                local_stock: vec![],
                stepped_stock: vec![],
                capped_stock: vec![],
                placement_contact_paint: vec![],
                material_tiles: vec![],
                primitives,
            }],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap();
    export::validate(&scan).unwrap();
    let page = &scan.pages[0];
    let short = page
        .walls
        .iter()
        .find(|w| w.start.y == 200. && w.end.y == 215. && w.thickness == 10.)
        .unwrap();
    assert_eq!(
        page.placement_decisions
            .as_ref()
            .unwrap()
            .iter()
            .find(|d| d.wall_id == short.id)
            .unwrap()
            .status,
        openplantrace_rs::placement::WallStatus::Ready
    );
    let packet = export::placement(&scan);
    assert!(
        packet["pages"][0]["barriers"]
            .as_array()
            .unwrap()
            .iter()
            .all(|b| b["spans"]
                .as_array()
                .unwrap()
                .iter()
                .all(|s| s["start"]["y"] != 180. || s["end"]["y"] != 200.))
    );
}
