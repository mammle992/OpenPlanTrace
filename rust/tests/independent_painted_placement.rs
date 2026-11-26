use openplantrace_rs::{
    export,
    model::*,
    pipeline,
    placement::{WallReason, WallStatus, connections, enclosures},
};
use std::collections::HashMap;

fn primitive(id: &str, layer: Option<&str>, shape: Shape) -> Primitive {
    Primitive {
        id: id.into(),
        layer: layer.map(str::to_owned),
        layer_category_override: None,
        stroke_width: None,
        shape,
    }
}
fn line(id: &str, layer: Option<&str>, a: Point, b: Point) -> Primitive {
    primitive(id, layer, Shape::Line { start: a, end: b })
}

fn fixture(angle: f64) -> Scan {
    let axis = Point::new(angle.cos(), angle.sin());
    let at = |x, y| Point::new(300., 300.) + axis * x + Point::new(-axis.y, axis.x) * y;
    let primitives = vec![
        primitive(
            "paint",
            Some("A-WALL"),
            Shape::Polyline {
                points: vec![at(0., 0.), at(80., 0.), at(80., 10.), at(0., 10.)],
                closed: true,
                filled: true,
            },
        ),
        line("face-a", Some("A-WALL"), at(0., 0.), at(80., 0.)),
        line("face-b", Some("A-WALL"), at(-10., 10.), at(80., 10.)),
        line("cap", Some("A-WALL"), at(80., 0.), at(80., 10.)),
        // An independently observed, longer axis mimics polluted area topology.
        line("long-reference-a", None, at(-20., 0.), at(100., 0.)),
        line("long-reference-b", None, at(-20., 10.), at(100., 10.)),
        line("door-host", Some("A-WALL"), at(100., 40.), at(170., 40.)),
        primitive(
            "swing",
            Some("A-DOOR"),
            Shape::Arc {
                center: at(120., 40.),
                radius: 20.,
                start_angle: angle + std::f64::consts::FRAC_PI_2,
                sweep: -std::f64::consts::FRAC_PI_2,
            },
        ),
        line("open-leaf", Some("A-DOOR"), at(120., 40.), at(120., 60.)),
        line("jamb-a", Some("A-DOOR"), at(120., 38.), at(120., 42.)),
        line("jamb-b", Some("A-DOOR"), at(140., 38.), at(140., 42.)),
    ];
    let scan = pipeline::scan_document(
        Document {
            warnings: vec![],
            pages: vec![Page {
                finish_history: None,
                number: 1,
                width: 1000.,
                height: 1000.,
                units_to_mm: None,
                primitives,
                pdf_input: None,
                material_tiles: vec![],
                placement_paint_ids: Some(vec!["paint".into()]),
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
    .unwrap();
    export::validate(&scan).unwrap();
    assert!(
        scan.pages[0]
            .placement_decisions
            .as_ref()
            .unwrap()
            .iter()
            .any(|d| d.reason == WallReason::IndependentlyOutlinedPaintedBody),
        "{:?}",
        scan.pages[0].placement_decisions
    );
    scan
}

fn poison_topology(scan: &mut Scan) {
    let page = &mut scan.pages[0];
    let target = page
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .find(|d| d.reason == WallReason::IndependentlyOutlinedPaintedBody)
        .unwrap()
        .wall_id
        .clone();
    let wall = page.walls.iter().find(|w| w.id == target).unwrap().clone();
    let u = (wall.end - wall.start).unit();
    let n = Point::new(-u.y, u.x);
    for node in &mut page.nodes {
        if node.wall_ids.contains(&target) {
            let t = (node.position - wall.start).dot(u);
            node.position = if t <= wall.start.distance(wall.end) * 0.5 {
                wall.start - u * 3. + n * 1.25
            } else {
                wall.end + u * 4. - n * 1.
            };
        }
    }
    page.exterior_enclosures = Some(enclosures::build(page));
    page.exterior_connections = Some(connections::build(page).unwrap());
}

#[test]
fn new_ready_graph_uses_the_finite_source_axis_after_coalesced_graph_drift() {
    for angle in [0., 0.63] {
        let mut scan = fixture(angle);
        let before = export::placement(&scan)["pages"][0]["barriers"].clone();
        poison_topology(&mut scan);
        export::validate(&scan).unwrap();
        let page = &scan.pages[0];
        let packet = export::placement(&scan);
        let projected = &packet["pages"][0];
        assert_eq!(projected["barriers"], before);
        let graph = &projected["wallGraph"];
        let nodes: HashMap<_, Point> = graph["nodes"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| {
                (
                    v["id"].as_str().unwrap(),
                    serde_json::from_value(v["position"].clone()).unwrap(),
                )
            })
            .collect();
        for decision in page
            .placement_decisions
            .as_ref()
            .unwrap()
            .iter()
            .filter(|d| d.reason == WallReason::IndependentlyOutlinedPaintedBody)
        {
            let wall = page
                .walls
                .iter()
                .find(|w| w.id == decision.wall_id)
                .unwrap();
            let axis = (wall.end - wall.start).unit();
            let length = wall.start.distance(wall.end);
            let mut graph_length = 0.;
            let mut ends = vec![];
            for edge in graph["edges"].as_array().unwrap().iter().filter(|e| {
                e["wallIds"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .any(|id| id == &decision.wall_id)
            }) {
                let a = nodes[edge["startNodeId"].as_str().unwrap()];
                let b = nodes[edge["endNodeId"].as_str().unwrap()];
                for p in [a, b] {
                    let v = p - wall.start;
                    assert!(v.cross(axis).abs() < 1e-9);
                    assert!(v.dot(axis) >= -1e-9 && v.dot(axis) <= length + 1e-9);
                    ends.push(p);
                }
                graph_length += a.distance(b);
            }
            assert!((graph_length - length).abs() < 1e-9);
            assert!(ends.iter().any(|p| p.distance(wall.start) < 1e-9));
            assert!(ends.iter().any(|p| p.distance(wall.end) < 1e-9));
            assert_eq!(decision.status, WallStatus::Ready);
        }
    }
}

#[test]
fn source_replay_rejects_forged_witnesses_and_an_archived_version_cannot_claim_the_new_authority() {
    let scan = fixture(0.2);
    let mut bad = scan.clone();
    let source = bad.pages[0]
        .primitives
        .iter_mut()
        .find(|p| p.id == "cap")
        .unwrap();
    source.shape = Shape::Line {
        start: Point::new(1., 1.),
        end: Point::new(2., 2.),
    };
    assert!(export::validate(&bad).is_err());
    let mut bad = scan.clone();
    bad.pages[0]
        .placement_decisions
        .as_mut()
        .unwrap()
        .iter_mut()
        .find(|d| d.reason == WallReason::IndependentlyOutlinedPaintedBody)
        .unwrap()
        .support_source_ids
        .pop();
    assert!(
        export::validate(&bad)
            .unwrap_err()
            .contains("placement wall decisions disagree")
    );
    let mut archived = scan;
    archived.schema_version = "openplantrace.rust.scan.v12".into();
    archived.pages[0].placement_polygon_paint_ids = None;
    archived.pages[0].finish_visibility = None;
    assert!(
        export::validate(&archived)
            .unwrap_err()
            .contains("placement wall decisions disagree")
    );
}
