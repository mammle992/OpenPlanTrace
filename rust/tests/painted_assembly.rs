use openplantrace_rs::{export, model::*, pipeline};

// Independent drawing strokes may be reversed or rotated. Two thin facing
// strips and a core share finite ends on the two outer faces.
fn drawing(angle: f64, upper_start: f64, upper_end: f64, layer: &str, duplicate: bool) -> Scan {
    let axis = Point::new(angle.cos(), angle.sin());
    let point = |u, v| Point::new(30., 30.) + axis * u + Point::new(-axis.y, axis.x) * v;
    let ink = |name: &str, layer: &str, shape| Primitive {
        id: name.into(),
        layer: Some(layer.into()),
        shape,
        layer_category_override: None,
        stroke_width: None,
    };
    let line = |name, a, b| ink(name, layer, Shape::Line { start: a, end: b });
    let fill = |name, min, max, end| {
        ink(
            name,
            layer,
            Shape::Polyline {
                points: vec![
                    point(0., min),
                    point(end, min),
                    point(end, max),
                    point(0., max),
                ],
                closed: true,
                filled: true,
            },
        )
    };
    let mut primitives = vec![
        line("face-a", point(0., 0.), point(80., 0.)),
        line("face-b", point(80., 8.), point(0., 8.)),
        fill("lower", 0., 1., 80.),
        fill("core", 1., 7., 80.),
        fill("upper", upper_start, 8., upper_end),
    ];
    if duplicate {
        primitives.push(fill("duplicate", 1., 7., 80.));
    }
    let doc = Document {
        warnings: vec![],
        pages: vec![Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            width: 200.,
            height: 200.,
            units_to_mm: None,
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            material_tiles: vec![],
            primitives,
        }],
    };
    let scan = pipeline::scan_document(doc, &Options::default(), None, true).unwrap();
    export::validate(&scan).unwrap();
    scan
}

fn new_walls(page: &serde_json::Value) -> Vec<&serde_json::Value> {
    page["walls"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|w| {
            matches!(
                w["decision"]["reason"].as_str(),
                Some("painted_wall_layers_between_faces" | "painted_core_between_wall_faces")
            )
        })
        .collect()
}

#[test]
fn registered_layers_produce_one_finite_barrier_and_exact_graph() {
    for angle in [0., 0.73] {
        let scan = drawing(angle, 7., 80., "A-WALL", false);
        let packet = export::placement(&scan);
        let page = &packet["pages"][0];
        let added = new_walls(page);
        assert_eq!(added.len(), 1, "{}", page["walls"]);
        let w = added[0];
        assert_eq!(w["decision"]["status"], "Ready");
        let support = w["decision"]["supportSourceIds"].as_array().unwrap();
        for id in ["face-a", "face-b", "upper", "lower", "core"] {
            assert!(support.iter().any(|x| x == id));
        }
        let g = &page["wallGraph"];
        for end in ["start", "end"] {
            assert!(
                g["nodes"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|n| n["position"] == w["centerLine"][end]
                        && n["wallIds"].as_array().unwrap().contains(&w["wallId"]))
            );
        }
        let mut changed = scan.clone();
        let d = changed.pages[0]
            .placement_decisions
            .as_mut()
            .unwrap()
            .iter_mut()
            .find(|d| w["wallId"] == d.wall_id)
            .unwrap();
        d.support_source_ids.retain(|id| id != "core");
        assert!(export::validate(&changed).is_err());
    }
}

#[test]
fn gaps_short_fills_duplicate_paint_and_furniture_cannot_witness_a_partition() {
    for (start, end, layer, dup) in [
        (7.2, 80., "A-WALL", false),
        (7., 70., "A-WALL", false),
        (7., 80., "A-FURN", false),
        (7., 80., "A-WALL", true),
    ] {
        let packet = export::placement(&drawing(0., start, end, layer, dup));
        assert!(
            new_walls(&packet["pages"][0]).is_empty(),
            "{start} {end} {layer} {dup}"
        );
    }
}

#[test]
fn a_measured_core_stays_within_its_own_paint_when_both_facings_are_drawn() {
    // Model the case where face pairing saw only the central fill. A desk edge
    // has already claimed the other observed face, so no outer wall is available.
    let mut scan = drawing(0., 7., 80., "A-WALL", false);
    let p = &mut scan.pages[0];
    let mut core = p.walls[0].clone();
    core.body = BodyEvidence::FilledBody;
    core.start = Point::new(30., 34.);
    core.end = Point::new(110., 34.);
    core.thickness = 6.;
    core.source_ids = vec!["core".to_owned()];
    p.walls = vec![core.clone()];
    p.edges.clear();
    p.nodes.clear();
    p.rooms.clear();
    p.openings.clear();
    let mut expected = p.placement_decisions.as_ref().unwrap()[0].clone();
    expected.wall_id = core.id.clone();
    expected.component_id = "p1:placement-component:1".into();
    expected.component_wall_count = 1;
    expected.opening_ids.clear();
    expected.room_ids.clear();
    expected.junction_node_ids.clear();
    expected.support_wall_ids.clear();
    expected.status = openplantrace_rs::placement::WallStatus::Ready;
    expected.reason = openplantrace_rs::placement::WallReason::PaintedCoreBetweenWallFaces;
    expected.support_source_ids = ["core", "face-a", "face-b", "lower", "upper"]
        .map(String::from)
        .to_vec();
    p.placement_decisions = Some(vec![expected]);
    openplantrace_rs::placement::validate(p, true).unwrap();
    let packet = export::placement(&scan);
    assert_eq!(packet["pages"][0]["barriers"][0]["thickness"], 6.);
    scan.pages[0].primitives.retain(|p| p.id != "face-b");
    assert!(openplantrace_rs::placement::validate(&scan.pages[0], true).is_err());
}
