use openplantrace_rs::{
    input::{self, DrawingFormat},
    model::*,
    pipeline,
    placement::{self, WallReason, WallStatus},
};

const SMOKE: &str = include_str!("../../samples/golden/semantic-smoke.dxf");
fn line(x1: i32, y1: i32, x2: i32, y2: i32) -> String {
    format!("0\nLINE\n8\nA-WALL\n10\n{x1}\n20\n{y1}\n11\n{x2}\n21\n{y2}\n")
}
const SWING: &str = "0\nARC\n8\nA-DOOR\n10\n170\n20\n540\n40\n30\n50\n0\n51\n90\n";
fn scan(dxf: &str, scale: Option<f64>) -> PageScan {
    let doc = input::load_bytes(DrawingFormat::Dxf, dxf.as_bytes()).unwrap();
    pipeline::scan_document(doc, &Options::default(), scale, false)
        .unwrap()
        .pages
        .remove(0)
}
fn smoke() -> String {
    SMOKE.replace("\r\n", "\n")
}

#[test]
fn short_isolated_opening_flanks_use_drawing_units_and_keep_original_door_proof() {
    let src = smoke();
    let left = line(120, 540, 170, 540);
    assert!(src.contains(&left));
    for (length, expected) in [
        (50, WallStatus::EvidenceOnly),
        (140, WallStatus::EvidenceOnly),
        (141, WallStatus::Review),
        (180, WallStatus::Review),
    ] {
        let drawing = src.replace(&left, &line(170 - length, 540, 170, 540));
        let p = scan(&drawing, Some(25.4));
        let d = p.placement_decisions.as_ref().unwrap();
        let o = &p.openings[0];
        assert_eq!(o.host_wall_ids.len(), 2);
        assert!(!o.swing_observations.is_empty());
        assert_eq!(
            d.iter().filter(|x| x.status == WallStatus::Ready).count(),
            4
        );
        let mut tested = 0;
        for w in p.walls.iter().filter(|w| o.host_wall_ids.contains(&w.id)) {
            let row = d.iter().find(|x| x.wall_id == w.id).unwrap();
            assert_eq!(row.opening_ids, [o.id.clone()]);
            assert_eq!(row.component_wall_count, 1);
            if (w.start.distance(w.end) - f64::from(length)).abs() < 1e-6 {
                assert_eq!(row.status, expected);
                tested += 1;
            }
        }
        assert_eq!(tested, 1);
    }
    let without = scan(&src.replace(SWING, ""), None);
    assert!(without.openings.is_empty());
    assert!(
        without
            .placement_decisions
            .unwrap()
            .iter()
            .all(|r| r.status != WallStatus::EvidenceOnly)
    );
}

#[test]
fn attached_short_flank_is_ready_and_unlabeled_detached_box_stays_under_review() {
    let src = smoke();
    let attachment = line(120, 540, 120, 470);
    let box_sides = [
        line(400, 540, 450, 540),
        line(450, 540, 450, 570),
        line(450, 570, 400, 570),
        line(400, 570, 400, 540),
    ]
    .concat();
    let p = scan(
        &src.replace(SWING, &format!("{SWING}{attachment}{box_sides}")),
        None,
    );
    let d = p.placement_decisions.as_ref().unwrap();
    let hosts = &p.openings[0].host_wall_ids;
    let ready_host = d
        .iter()
        .find(|r| hosts.contains(&r.wall_id) && r.status == WallStatus::Ready)
        .unwrap();
    assert_eq!(ready_host.reason, WallReason::AuthoredWallInRoomComponent);
    assert_eq!(
        d.iter().filter(|r| r.status == WallStatus::Ready).count(),
        6
    );
    assert_eq!(
        d.iter().filter(|r| r.status == WallStatus::Review).count(),
        4
    );
    assert_eq!(
        d.iter()
            .filter(|r| r.status == WallStatus::EvidenceOnly)
            .count(),
        1
    );
}

#[test]
fn changed_decision_or_missing_middle_of_a_ready_run_fails_replay() {
    let src = smoke();
    let branches = [line(180, 470, 180, 410), line(220, 470, 220, 405)].concat();
    let mut p = scan(&src.replace(SWING, &format!("{SWING}{branches}")), None);
    let mut altered = p.clone();
    altered.placement_decisions.as_mut().unwrap()[0].component_wall_count += 1;
    assert!(placement::validate(&altered, true).is_err());
    let n1 = p
        .nodes
        .iter()
        .position(|n| n.wall_ids.len() >= 2 && n.position == Point::new(128., 120.))
        .unwrap();
    let n2 = p
        .nodes
        .iter()
        .position(|n| n.wall_ids.len() >= 2 && n.position == Point::new(168., 120.))
        .unwrap();
    let before = p.edges.len();
    p.edges.retain(|e| {
        !(e.start_node.min(e.end_node) == n1.min(n2) && e.start_node.max(e.end_node) == n1.max(n2))
    });
    assert_eq!(p.edges.len() + 1, before);
    assert!(placement::validate(&p, true).is_err());
}

fn primitive(id: &str, layer: &str, shape: Shape) -> Primitive {
    Primitive {
        id: id.into(),
        layer: Some(layer.into()),
        layer_category_override: None,
        stroke_width: None,
        shape,
    }
}
fn painted(id: &str, layer: &str, lo: Point, hi: Point) -> Primitive {
    primitive(
        id,
        layer,
        Shape::Polyline {
            points: vec![lo, Point::new(hi.x, lo.y), hi, Point::new(lo.x, hi.y)],
            filled: true,
            closed: true,
        },
    )
}
fn roomless_sources(primitives: Vec<Primitive>) -> PageScan {
    let doc = Document {
        warnings: vec![],
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
            recessed_stock: vec![],
            masked_junction_stock: vec![],
            pocket_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 400.,
            height: 400.,
            units_to_mm: None,
            primitives,
        }],
    };
    pipeline::scan_document(doc, &Options::default(), None, true)
        .unwrap()
        .pages
        .remove(0)
}

#[test]
fn both_wall_faces_need_continuous_independent_hatching() {
    let line = |id: &str, x, y, end_x, end_y| {
        primitive(
            id,
            "A-WALL",
            Shape::Line {
                start: Point::new(x, y),
                end: Point::new(end_x, end_y),
            },
        )
    };
    let mut ink = vec![
        line("face-a", 30., 40., 130., 40.),
        line("face-b", 130., 48., 30., 48.),
    ];
    for n in 0..20 {
        let x = 32.5 + 5. * f64::from(n);
        ink.push(line(&format!("lower-{n}"), x, 40., x - 1., 39.));
        ink.push(line(&format!("upper-{n}"), x + 1., 49., x, 48.));
    }
    let page = roomless_sources(ink.clone());
    let receipt = page
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .find(|d| d.reason == WallReason::HatchedWallFaces)
        .expect("independent material layers");
    assert_eq!(receipt.support_source_ids.len(), ink.len());
    assert_eq!(receipt.status, WallStatus::Ready);
    assert!(placement::validate_preview4(&page).is_err());

    let mut old_page = page.clone();
    let old = old_page
        .placement_decisions
        .as_mut()
        .unwrap()
        .iter_mut()
        .find(|d| d.wall_id == receipt.wall_id)
        .unwrap();
    old.status = WallStatus::Review;
    old.reason = WallReason::IsolatedWall;
    old.support_source_ids.clear();
    placement::validate_preview4(&old_page).unwrap();
    assert!(placement::validate(&old_page, true).is_err());

    let mut clipped = ink.clone();
    for mark in clipped.iter_mut().filter(|p| p.id.starts_with("lower-")) {
        if let Shape::Line { start, end } = &mut mark.shape {
            start.x -= 2.;
            end.x -= 2.;
            if mark.id == "lower-0" {
                *end = Point::new(30., 39.5);
            }
        }
    }
    assert!(
        roomless_sources(clipped.clone())
            .placement_decisions
            .unwrap()
            .iter()
            .any(|d| d.reason == WallReason::HatchedWallFaces)
    );
    let rejects = |ink: Vec<Primitive>| {
        let p = roomless_sources(ink);
        assert!(
            p.placement_decisions
                .unwrap()
                .iter()
                .all(|d| d.status != WallStatus::Ready)
        );
    };
    let middle = clipped.iter_mut().find(|p| p.id == "lower-9").unwrap();
    if let Shape::Line { start, end } = &mut middle.shape {
        *end = (*start + *end) * 0.5;
    }
    rejects(clipped);
    rejects(ink.iter().filter(|p| p.id != "lower-8").cloned().collect());
    rejects(
        ink.iter()
            .filter(|p| !p.id.starts_with("upper-"))
            .cloned()
            .collect(),
    );
    let mut foreign = ink.clone();
    for mark in &mut foreign[2..] {
        mark.layer = Some("A-FURN".into());
    }
    rejects(foreign);
    // Closely spaced decorative ticks on just the central part leave both ends unproved.
    for mark in &mut ink[2..] {
        if let Shape::Line { start, end } = &mut mark.shape {
            start.x = 55. + 0.5 * start.x;
            end.x = 55. + 0.5 * end.x;
        }
    }
    rejects(ink);
}

#[test]
fn painted_partitions_bind_to_real_material_on_the_declared_wall_layer() {
    let wall_layer = "Bærende yttervegger";
    let backing = painted(
        "backing",
        wall_layer,
        Point::new(20., 97.),
        Point::new(220., 103.),
    );
    let free = painted(
        "partition",
        wall_layer,
        Point::new(97., 97.),
        Point::new(103., 180.),
    );
    let mut p = roomless_sources(vec![backing.clone(), free.clone()]);
    let w = p
        .walls
        .iter()
        .find(|w| w.source_ids.contains(&"partition".into()))
        .unwrap();
    let proof = p
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .find(|r| r.wall_id == w.id)
        .unwrap();
    assert_eq!(
        (proof.status, proof.reason),
        (WallStatus::Ready, WallReason::AuthoredFilledPartition)
    );
    assert_eq!(proof.support_source_ids, ["partition"]);
    assert_eq!(proof.support_wall_ids.len(), 1);
    let donor = &proof.support_wall_ids[0];
    assert!(
        p.walls
            .iter()
            .any(|w| w.id == *donor && w.source_ids.contains(&"backing".into()))
    );
    // A shifted fill and a coincident wall-layer line do not witness the same strip.
    let Shape::Polyline { points, .. } = &mut p
        .primitives
        .iter_mut()
        .find(|p| p.id == "partition")
        .unwrap()
        .shape
    else {
        panic!()
    };
    points.iter_mut().for_each(|p| p.x += 1.);
    assert!(placement::validate(&p, true).is_err());
    let mut unrelated = free;
    unrelated.layer = Some("A-FURN".into());
    let p = roomless_sources(vec![backing, unrelated]);
    assert!(
        p.placement_decisions
            .unwrap()
            .iter()
            .all(|r| r.status != WallStatus::Ready)
    );
}

#[test]
fn independent_ink_must_cover_both_faces_of_an_isolated_painted_wall() {
    let layer = "A-WALL";
    let p = |x, y| Point::new(x, y);
    let fill = painted("paint", layer, p(40., 50.), p(200., 56.));
    let trace = |id: &str, a, b| primitive(id, layer, Shape::Line { start: a, end: b });
    let lower = trace("south", p(40., 50.), p(200., 50.));
    let upper = trace("north", p(40., 56.), p(200., 56.));
    let scan = roomless_sources(vec![fill.clone(), lower.clone(), upper]);
    let d = &scan.placement_decisions.as_ref().unwrap()[0];
    assert_eq!(d.reason, WallReason::AuthoredPaintedAndOutlinedWall);
    assert_eq!(d.support_source_ids, ["north", "paint", "south"]);
    for upper in [
        trace("short", p(40., 56.), p(190., 56.)),
        primitive(
            "furniture",
            "A-FURN",
            Shape::Line {
                start: p(40., 56.),
                end: p(200., 56.),
            },
        ),
    ] {
        let scan = roomless_sources(vec![fill.clone(), lower.clone(), upper]);
        assert!(
            scan.placement_decisions
                .unwrap()
                .iter()
                .all(|row| row.status != WallStatus::Ready)
        );
    }
}

#[test]
fn roomless_physical_partition_needs_two_independent_through_walls() {
    let p = Point::new;
    let top = painted("top", "MODEL", p(20., 97.), p(220., 103.));
    let bottom = painted("bottom", "MODEL", p(20., 177.), p(220., 183.));
    let partition = painted("partition", "MODEL", p(97., 100.), p(103., 180.));
    let scan = roomless_sources(vec![top.clone(), bottom, partition.clone()]);
    let d = scan
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .find(|d| d.status == WallStatus::Ready)
        .unwrap();
    assert_eq!(d.reason, WallReason::SupportedPhysicalEndpoints);
    assert_eq!(d.support_wall_ids.len(), 2);
    assert_ne!(d.support_wall_ids[0], d.support_wall_ids[1]);
    let only_one = roomless_sources(vec![top, partition]);
    assert!(
        only_one
            .placement_decisions
            .unwrap()
            .iter()
            .all(|d| d.status == WallStatus::Review)
    );
}
