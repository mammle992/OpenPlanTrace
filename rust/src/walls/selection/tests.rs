use super::*;
use crate::model::Primitive;

fn p(x: f64, y: f64) -> Point {
    Point::new(x, y)
}
fn wall(a: Point, b: Point, w: f64, ids: &[&str]) -> Wall {
    Wall {
        id: "w".into(),
        page: 1,
        start: a,
        end: b,
        thickness: w,
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: ids.iter().map(|v| v.to_string()).collect(),
        requires_review: true,
    }
}
fn stroke(name: &str, a: Point, b: Point) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: name.into(),
        layer: None,
        shape: Shape::Line { start: a, end: b },
    }
}
fn decide(ps: Vec<Primitive>, w: Wall) -> (Vec<Wall>, Vec<WithheldWall>) {
    select(
        &Page {
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
            width: 500.,
            height: 500.,
            units_to_mm: None,
            primitives: ps,
        },
        vec![w],
        &Options::default(),
    )
    .unwrap()
}

#[test]
fn splayed_free_strokes_do_not_form_a_wall_and_keep_their_witnesses() {
    let bands: Vec<_> = (0..5)
        .map(|i| {
            stroke(
                &format!("b{i}"),
                p(20., 20. + 6. * i as f64),
                p(65., 16. + 8. * i as f64),
            )
        })
        .collect();
    let candidate = wall(p(20., 35.), p(65., 36.), 7., &["b2", "b3"]);
    let (chosen, rejected) = decide(bands.clone(), candidate.clone());
    assert!(chosen.is_empty());
    assert_eq!(
        rejected[0].reason,
        WallSelectionReason::RepeatedSymbolStrokes
    );
    assert_eq!(rejected[0].witness_source_ids.len(), 5);
    assert_eq!(rejected[0].wall.source_ids, candidate.source_ids);
    // Rows joined by real end bars can be a framed assembly instead.
    let mut framed = bands;
    framed.push(stroke("left", p(20., 15.), p(20., 52.)));
    framed.push(stroke("right", p(65., 14.), p(65., 53.)));
    assert_eq!(decide(framed, candidate).0.len(), 1);
}
#[test]
fn detached_detail_crosses_a_claimed_face_but_contained_hatch_does_not() {
    let boundary = vec![
        stroke("a", p(50., 10.), p(50., 95.)),
        stroke("b", p(65., 10.), p(65., 95.)),
    ];
    let candidate = wall(p(57.5, 10.), p(57.5, 95.), 15., &["a", "b"]);
    let marks = |far: f64| {
        (0..5).map(move |i| {
            stroke(
                &format!("mark{i}"),
                p(54., 25. + 8. * i as f64),
                p(far, 21. + 9. * i as f64),
            )
        })
    };
    let mut furniture = boundary.clone();
    furniture.extend(marks(80.));
    // Detached strokes on a longer uninterrupted partition do not erase it.
    assert_eq!(decide(furniture.clone(), candidate.clone()).0.len(), 1);
    furniture.extend([
        stroke("cap1", p(50., 10.), p(85., 10.)),
        stroke("cap2", p(50., 95.), p(85., 95.)),
    ]);
    let (_, rejected) = decide(furniture.clone(), candidate.clone());
    assert_eq!(
        rejected[0].reason,
        WallSelectionReason::InteriorDetailCrossesFace
    );
    // Explicit CAD wall survives overlaid loose furnishing strokes.
    furniture[0].layer = Some("A-WALL".into());
    assert_eq!(decide(furniture, candidate.clone()).0.len(), 1);
    let mut filled = boundary;
    filled.extend(marks(64.));
    assert_eq!(decide(filled, candidate).0.len(), 1);
}
#[test]
fn freestanding_parallel_wall_pairs_keep_their_intervening_corridor() {
    let strokes: Vec<_> = [0., 3., 14., 17.]
        .into_iter()
        .enumerate()
        .map(|(i, y)| stroke(&format!("s{i}"), p(10., y), p(85., y)))
        .collect();
    for (at, ids) in [(1.5, ["s0", "s1"]), (15.5, ["s2", "s3"])] {
        assert_eq!(
            decide(strokes.clone(), wall(p(10., at), p(85., at), 3., &ids))
                .0
                .len(),
            1
        );
    }
}

#[test]
fn cabinet_swing_row_explains_the_bay_but_preserves_its_backing_wall() {
    // Oblique bays exercise the same registration-independent geometry used on vector PDFs.
    let rot = |x: f64, y: f64| p(100. + 0.8 * x - 0.6 * y, 110. + 0.6 * x + 0.8 * y);
    let angle = 0.6_f64.atan2(0.8);
    let swing = 15_f64.to_radians();
    let mut ps = vec![
        stroke("front", rot(0., 10.), rot(60., 10.)),
        stroke("back", rot(0., 0.), rot(60., 0.)),
        stroke("left", rot(0., 0.), rot(0., 10.)),
        stroke("right", rot(60., 0.), rot(60., 10.)),
        stroke("divider", rot(20., 0.), rot(20., 10.)),
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: "material".into(),
            layer: None,
            shape: Shape::Polyline {
                points: vec![rot(0., -2.), rot(60., -2.), rot(60., 0.), rot(0., 0.)],
                closed: true,
                filled: true,
            },
        },
    ];
    for i in 0..3 {
        let x = 20. * i as f64;
        ps.push(stroke(
            &format!("leaf-{i}"),
            rot(x, 10.),
            rot(x + 20. * swing.cos(), 10. + 20. * swing.sin()),
        ));
        ps.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: format!("arc-{i}"),
            layer: None,
            shape: Shape::Arc {
                center: rot(x, 10.),
                radius: 20.,
                start_angle: angle,
                sweep: swing,
            },
        });
    }
    let mut material = wall(rot(0., -1.), rot(60., -1.), 2., &["material"]);
    material.id = "structural".into();
    material.body = BodyEvidence::FilledBody;
    let observation = wall(rot(0., 5.), rot(60., 5.), 10., &["front", "back"]);
    let evaluate = |primitives: Vec<Primitive>, has_material: bool| {
        let mut walls = vec![observation.clone()];
        if has_material {
            walls.push(material.clone());
        }
        select(
            &Page {
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
                width: 300.,
                height: 300.,
                units_to_mm: None,
                primitives,
            },
            walls,
            &Options::default(),
        )
        .unwrap()
    };
    let (kept, rejected) = evaluate(ps.clone(), true);
    assert_eq!(kept.len(), 1);
    assert_eq!(kept[0].id, "structural");
    assert_eq!(rejected.len(), 1);
    assert_eq!(rejected[0].reason, WallSelectionReason::CabinetFront);
    for witness in ["material", "divider", "leaf-0", "arc-2"] {
        assert!(
            rejected[0]
                .witness_source_ids
                .iter()
                .any(|id| id == witness)
        );
    }
    for omitted in ["divider", "arc-1", "right"] {
        let remaining = ps.iter().filter(|p| p.id != omitted).cloned().collect();
        assert!(evaluate(remaining, true).1.is_empty());
    }
    assert!(evaluate(ps.clone(), false).1.is_empty());
    // A front drawn along part of a longer wall cannot explain the full observed extent.
    let mut longer = observation;
    longer.end = rot(80., 5.);
    let mut longer_back = material;
    longer_back.end = rot(80., -1.);
    ps.push(stroke("later-end", rot(80., 0.), rot(80., 10.)));
    let page = Page {
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
        width: 300.,
        height: 300.,
        units_to_mm: None,
        primitives: ps,
    };
    assert!(
        select(&page, vec![longer, longer_back], &Options::default())
            .unwrap()
            .1
            .is_empty()
    );
}

#[test]
fn independent_rims_and_two_rows_can_establish_a_labeled_cavity() {
    let paint = |name: &str, l: f64, t: f64, r: f64, b: f64| Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: name.into(),
        layer: None,
        shape: Shape::Polyline {
            points: vec![p(l, t), p(r, t), p(r, b), p(l, b)],
            filled: true,
            closed: true,
        },
    };
    let label = |name: &str, y: f64| Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: name.into(),
        layer: None,
        shape: Shape::Text {
            at: p(10., y),
            height: 3.,
            value: name.into(),
        },
    };
    let candidate = wall(p(0., 10.), p(32., 10.), 20., &["top", "bottom"]);
    let base = vec![
        stroke("top", p(0., 0.), p(32., 0.)),
        stroke("bottom", p(0., 20.), p(32., 20.)),
        paint("sheet", -50., -55., 120., 105.),
        label("service", 8.),
        label("platform", 12.),
    ];
    let mut rimmed = base.clone();
    rimmed.push(paint("top-rim", 0., 0., 32., 1.2));
    assert_eq!(decide(rimmed.clone(), candidate.clone()).0.len(), 1);
    rimmed.push(paint("bottom-rim", 0., 18.8, 32., 20.));
    let (_, rejected) = decide(rimmed.clone(), candidate.clone());
    assert_eq!(rejected[0].reason, WallSelectionReason::LabeledEnclosure);
    assert_eq!(
        rejected[0].witness_source_ids,
        ["bottom-rim", "platform", "service", "top-rim"]
    );
    let mut single = rimmed.clone();
    single.retain(|s| s.id != "platform");
    assert_eq!(decide(single, candidate.clone()).0.len(), 1);
    rimmed.push(paint("material", 0., 0., 32., 20.));
    assert_eq!(decide(rimmed, candidate).0.len(), 1);
}
#[test]
fn closed_member_of_two_adjacent_bays_cannot_be_paired_to_outside_wall_face() {
    // Rotate the case so box recognition cannot rely on drawing orientation.
    let rot = |x: f64, y: f64| p(100. + 0.8 * x - 0.6 * y, 80. + 0.6 * x + 0.8 * y);
    let s = |id: &str, ax: f64, ay: f64, bx: f64, by: f64| stroke(id, rot(ax, ay), rot(bx, by));
    let mut ps = vec![
        s("host", -6., -35., -6., 50.),
        s("edge", 0., 0., 0., 40.),
        s("a", 0., 0., 50., 0.),
        s("b", 0., 40., 50., 40.),
        s("c", 50., 0., 50., 40.),
    ];
    let candidate = wall(rot(-3., 0.), rot(-3., 40.), 6., &["host", "edge"]);
    assert_eq!(decide(ps.clone(), candidate.clone()).0.len(), 1);
    ps.extend([
        s("d", 50., 0., 100., 0.),
        s("e", 50., 40., 100., 40.),
        s("f", 100., 0., 100., 40.),
    ]);
    let (chosen, rejected) = decide(ps, candidate);
    assert!(chosen.is_empty());
    assert_eq!(rejected[0].reason, WallSelectionReason::OutlineEdgePair);
    assert_eq!(rejected[0].witness_source_ids.len(), 7);
}
#[test]
fn sketch_outline_stepping_against_long_reference_rejects_only_the_broad_bridge() {
    let ps = vec![
        stroke("ref", p(-30., 40.), p(95., 40.)),
        stroke("step", p(0., 20.), p(32., 20.)),
        stroke("up", p(0., 20.), p(0., 0.)),
        stroke("down", p(32., 20.), p(32., 38.)),
    ];
    let mut candidate = wall(p(0., 30.), p(32., 30.), 20., &["ref", "step"]);
    assert_eq!(
        decide(ps.clone(), candidate.clone()).1[0].reason,
        WallSelectionReason::OutlineEdgePair
    );
    candidate.body = BodyEvidence::FilledBody;
    assert_eq!(decide(ps, candidate).0.len(), 1);
}

#[test]
fn opposed_cabinet_fronts_need_both_compartments_and_independent_interior_detail() {
    let rot = |x: f64, y: f64| p(70. + 0.8 * x - 0.6 * y, 30. + 0.6 * x + 0.8 * y);
    let s = |id, x, y, a, b| stroke(id, rot(x, y), rot(a, b));
    let mut ps = vec![
        s("left", 0., 0., 44., 0.),
        s("center", 0., 16., 44., 16.),
        s("right", 0., 32., 44., 32.),
        s("back", 0., 0., 0., 32.),
        s("front", 44., 0., 44., 32.),
        s("shelf", 21., 0., 21., 16.),
        s("mark-1", 6., 4., 31., 7.),
        s("mark-2", 5., 10., 30., 13.),
    ];
    let opening = 0.4_f64;
    let rotation = 0.6_f64.atan2(0.8);
    for (y, dy, start, sign, leaf, arc) in [
        (0., 1., PI / 2., -1., "leaf-a", "arc-a"),
        (32., -1., -PI / 2., 1., "leaf-b", "arc-b"),
    ] {
        ps.push(s(
            leaf,
            44.,
            y,
            44. + 16. * opening.sin(),
            y + dy * 16. * opening.cos(),
        ));
        ps.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: arc.into(),
            layer: None,
            shape: Shape::Arc {
                center: rot(44., y),
                radius: 16.,
                start_angle: rotation + start,
                sweep: sign * opening,
            },
        });
    }
    let candidate = wall(rot(0., 8.), rot(44., 8.), 16., &["left", "center"]);
    let (_, rejected) = decide(ps.clone(), candidate.clone());
    assert_eq!(rejected[0].reason, WallSelectionReason::CabinetFront);
    assert_eq!(rejected[0].witness_source_ids.len(), 12);
    let footprint = rejected[0].furniture_outline.unwrap();
    for end in [rot(0., 0.), rot(44., 0.), rot(0., 32.), rot(44., 32.)] {
        assert!(footprint.iter().any(|p| p.distance(end) < 1e-7));
    }
    for absent in ["arc-b", "leaf-a", "right", "shelf", "mark-2"] {
        let incomplete = ps.iter().filter(|s| s.id != absent).cloned().collect();
        assert!(decide(incomplete, candidate.clone()).1.is_empty());
    }
    ps.push(Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: "material".into(),
        layer: None,
        shape: Shape::Polyline {
            points: vec![rot(0., 0.), rot(44., 0.), rot(44., 16.), rot(0., 16.)],
            closed: true,
            filled: true,
        },
    });
    assert!(decide(ps, candidate).1.is_empty());
}
