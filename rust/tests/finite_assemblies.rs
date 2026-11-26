use openplantrace_rs::{model::*, openings, room_labels, topology};
fn pt(x: f64, y: f64) -> Point {
    Point::new(x, y)
}
fn line(id: &str, a: Point, b: Point) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Line { start: a, end: b },
    }
}
fn p(ps: Vec<Primitive>) -> Page {
    Page {
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
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 200.,
        height: 200.,
        units_to_mm: None,
        primitives: ps,
    }
}
fn wall(id: &str, a: Point, b: Point, t: f64) -> Wall {
    Wall {
        id: id.into(),
        page: 1,
        start: a,
        end: b,
        thickness: t,
        body: BodyEvidence::ParallelFaces,
        source_ids: vec!["outside".into()],
        curve: None,
        requires_review: true,
    }
}
fn frames() -> Page {
    p(vec![
        line("j1", pt(30., -4.), pt(30., 4.)),
        line("j2", pt(70., -4.), pt(70., 4.)),
        line("r1", pt(30., -2.), pt(70., -2.)),
        line("r2", pt(70., 2.), pt(30., 2.)),
    ])
}
#[test]
fn finite_window_requires_two_distinct_rails_and_both_actual_jambs() {
    let mut f = frames();
    let w = wall("host", pt(0., 0.), pt(100., 0.), 4.);
    let o = openings::detect(&f, std::slice::from_ref(&w), &Options::default()).unwrap();
    assert_eq!(o.len(), 1);
    assert_eq!(o[0].kind, OpeningKind::Window);
    assert_eq!(o[0].evidence, OpeningEvidence::FiniteWindowAssembly);
    assert_eq!(o[0].source_ids.len(), 4);
    f.primitives[3].shape = Shape::Line {
        start: pt(30., 2.),
        end: pt(66., 2.),
    };
    assert!(
        openings::detect(&f, &[w], &Options::default())
            .unwrap()
            .is_empty()
    );
}
#[test]
fn one_frame_cannot_cut_two_neighboring_hosts() {
    let walls = [
        wall("a", pt(0., 0.), pt(100., 0.), 4.),
        wall("b", pt(0., 1.), pt(100., 1.), 4.),
    ];
    assert!(
        openings::detect(&frames(), &walls, &Options::default())
            .unwrap()
            .is_empty()
    );
}

#[test]
fn duplicate_rails_do_not_prove_a_frame() {
    let mut f = frames();
    f.primitives[3].shape = f.primitives[2].shape.clone();
    assert!(
        openings::detect(
            &f,
            &[wall("host", pt(0., 0.), pt(100., 0.), 4.)],
            &Options::default()
        )
        .unwrap()
        .is_empty()
    )
}
#[test]
fn observed_filled_pier_faces_do_not_become_window_rails() {
    let mut f = frames();
    f.primitives.push(Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: "pier".into(),
        layer: None,
        shape: Shape::Polyline {
            points: vec![pt(30., -2.), pt(70., -2.), pt(70., 2.), pt(30., 2.)],
            closed: true,
            filled: true,
        },
    });
    let mut w = wall("pier-wall", pt(30., 0.), pt(70., 0.), 4.);
    w.body = BodyEvidence::FilledBody;
    w.source_ids = vec!["pier".into()];
    assert!(
        openings::detect(
            &f,
            &[wall("host", pt(0., 0.), pt(100., 0.), 4.), w],
            &Options::default()
        )
        .unwrap()
        .is_empty()
    )
}

fn boxed_pane() -> Page {
    let mut parts = vec![
        line("glass", pt(10., 1.), pt(30., 1.)),
        line("rail-l", pt(10., 0.), pt(30., 0.)),
        line("rail-r", pt(30., 2.), pt(10., 2.)),
    ];
    for (i, (x, outside)) in [(10., 8.), (30., 32.)].into_iter().enumerate() {
        parts.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: format!("jamb-{i}"),
            layer: None,
            shape: Shape::Polyline {
                points: vec![pt(x, 0.), pt(x, 2.), pt(outside, 2.), pt(outside, 0.)],
                closed: true,
                filled: false,
            },
        });
    }
    p(parts)
}

#[test]
fn glazed_panel_with_two_boxed_jambs_ends_at_the_actual_corner() {
    let w = wall("host", pt(0., 2.), pt(32., 2.), 5.);
    let detect =
        |p: &Page, walls: &[Wall]| openings::detect(p, walls, &Options::default()).unwrap();
    let mut drawing = boxed_pane();
    let result = detect(&drawing, std::slice::from_ref(&w));
    assert_eq!(result.len(), 1);
    assert_eq!((result[0].start, result[0].end), (pt(8., 2.), pt(32., 2.)));
    assert_eq!(result[0].source_ids.len(), 5);
    // An actual material pier passing behind the pane rules out an inferred cut.
    let mut pier = wall("pier", pt(6., 1.), pt(36., 1.), 4.);
    pier.body = BodyEvidence::FilledBody;
    assert!(detect(&drawing, &[w.clone(), pier]).is_empty());
    // A second host encompassing the same pane leaves attribution ambiguous.
    assert!(
        detect(
            &drawing,
            &[w.clone(), wall("other", pt(0., 1.), pt(32., 1.), 5.)]
        )
        .is_empty()
    );
    if let Shape::Polyline { closed, .. } = &mut drawing.primitives[4].shape {
        *closed = false;
    }
    assert!(detect(&drawing, std::slice::from_ref(&w)).is_empty());
    // A rectangular outline that has no central glazing stroke cannot cut at a free end.
    let mut empty = boxed_pane();
    empty.primitives.remove(0);
    assert!(detect(&empty, std::slice::from_ref(&w)).is_empty());
    // Two complete outer ends against the same inner cap provide no unique rough opening.
    let mut ambiguous = boxed_pane();
    ambiguous.primitives.push(Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: "other outer end".into(),
        layer: None,
        shape: Shape::Polyline {
            points: vec![pt(10., 0.), pt(10., 2.), pt(7., 2.), pt(7., 0.)],
            closed: true,
            filled: false,
        },
    });
    assert!(detect(&ambiguous, &[w]).is_empty());
}
#[test]
fn butt_junction_uses_finite_body_width_without_inventing_coverage() {
    let walls = vec![
        wall("t", pt(5., 0.), pt(95., 0.), 10.),
        wall("b", pt(5., 80.), pt(95., 80.), 10.),
        wall("l", pt(0., 5.), pt(0., 75.), 10.),
        wall("r", pt(100., 5.), pt(100., 75.), 10.),
    ];
    let t = topology::build(&p(vec![]), &walls, &[], &Options::default()).unwrap();
    assert_eq!(t.rooms.len(), 1);
    assert!((t.rooms[0].area - 8000.).abs() < 1e-7);
    assert_eq!(walls[0].start, pt(5., 0.));
    let mut disconnected = walls;
    disconnected[0].start.x = 12.;
    assert!(
        topology::build(&p(vec![]), &disconnected, &[], &Options::default())
            .unwrap()
            .rooms
            .is_empty()
    )
}
#[test]
fn printed_area_and_label_do_not_invent_room_geometry() {
    let text = |id: &str, at: Point, s: &str| Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Text {
            at,
            value: s.into(),
            height: 6.,
        },
    };
    let page = p(vec![
        text("label", pt(21., 20.), "Library"),
        text("number", pt(20., 27.), "24,5"),
        text("unit", pt(34., 27.), "m²"),
    ]);
    let a = room_labels::associate(&page, &mut []).unwrap();
    assert_eq!(a.len(), 1);
    assert_eq!(a[0].area_square_meters, 24.5);
    assert_eq!(a[0].label.as_deref(), Some("Library"));
    assert!(a[0].room_id.is_none());
}
