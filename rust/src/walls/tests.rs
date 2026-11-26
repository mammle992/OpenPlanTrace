use super::*;
use crate::model::Primitive;

fn p(x: f64, y: f64) -> Point {
    Point::new(x, y)
}
fn item(id: &str, layer: Option<&str>, shape: Shape) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: layer.map(str::to_owned),
        shape,
    }
}
fn line(id: &str, layer: Option<&str>, a: Point, b: Point) -> Primitive {
    item(id, layer, Shape::Line { start: a, end: b })
}
fn fill(id: &str, points: Vec<Point>) -> Primitive {
    item(
        id,
        None,
        Shape::Polyline {
            points,
            closed: true,
            filled: true,
        },
    )
}
fn page(primitives: Vec<Primitive>) -> Page {
    Page {
        finish_history: None,
        pdf_input: None,
        number: 3,
        placement_paint_ids: None,
        placement_polygon_paint_ids: None,
        placement_paint_remainders: vec![],
        white_nested_walls: vec![],
        terminal_stock: vec![],
        local_stock: vec![],
        stepped_stock: vec![],
        capped_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: Vec::new(),
        width: 700.,
        height: 600.,
        units_to_mm: None,
        primitives,
    }
}
fn scan(items: Vec<Primitive>) -> Vec<Wall> {
    detect(&page(items), &Options::default()).unwrap()
}
fn close(actual: f64, expected: f64) {
    assert!((actual - expected).abs() < 1e-5, "{actual} != {expected}");
}
fn arc(id: &str, c: Point, r: f64, start: f64, sweep: f64) -> Primitive {
    item(
        id,
        None,
        Shape::Arc {
            center: c,
            radius: r,
            start_angle: start,
            sweep,
        },
    )
}

// Corresponds to the explicit CAD centerline and double-line room cases in
// WallPairReconstructionTests: source-backed body versus unmeasured centerline.
#[test]
fn classifies_paired_and_unpaired_faces_without_invented_thickness() {
    let w = scan(vec![
        line("outer", Some("A-WALL"), p(100., 100.), p(500., 100.)),
        line("inner", Some("A-WALL"), p(500., 110.), p(100., 110.)),
        line("left", Some("A-WALL"), p(100., 105.), p(100., 400.)),
        line("door", Some("A-DOOR"), p(100., 102.), p(500., 102.)),
    ]);
    assert_eq!(w.len(), 2);
    let pair = w
        .iter()
        .find(|w| w.body == BodyEvidence::ParallelFaces)
        .unwrap();
    close(pair.thickness, 10.);
    close(pair.start.y, 105.);
    close(pair.end.y, 105.);
    assert_eq!(pair.source_ids, ["inner", "outer"]);
    assert!(pair.requires_review);
    let single = w
        .iter()
        .find(|w| w.body == BodyEvidence::ExplicitCenterline)
        .unwrap();
    close(single.thickness, 0.);
    assert_eq!(single.source_ids, ["left"]);
}

// Exact source coordinates from WallSolidRecoveryTests' oblique filled body.
#[test]
fn csharp_rotated_filled_body_retains_full_finite_axis_and_width() {
    let w = scan(vec![
        fill(
            "body",
            vec![
                p(53.5355, 46.4645),
                p(194.9569, 187.8859),
                p(187.8859, 194.9569),
                p(46.4645, 53.5355),
            ],
        ),
        line("contact", Some("FURN"), p(75., 50.), p(75., 200.)),
    ]);
    assert_eq!(w.len(), 1);
    let b = &w[0];
    assert_eq!(b.body, BodyEvidence::FilledBody);
    assert!((b.thickness - 10.).abs() < 0.001);
    assert!((b.start.distance(b.end) - 200.).abs() < 0.001);
    close(b.start.x, 50.);
    close(b.start.y, 50.);
    close(b.end.x, 191.4214);
    close(b.end.y, 191.4214);
    assert_eq!(b.source_ids, ["body"]);
}

#[test]
fn explicit_wall_crossing_furniture_does_not_trim_observed_body() {
    let w = scan(vec![
        fill(
            "partition",
            vec![p(100., 40.), p(110., 40.), p(110., 180.), p(100., 180.)],
        ),
        line("bed", Some("A-FURN"), p(60., 163.), p(140., 163.)),
    ]);
    assert_eq!(w.len(), 1);
    close(w[0].start.x, 105.);
    close(w[0].start.y, 40.);
    close(w[0].end.y, 180.);
    assert_eq!(w[0].source_ids, ["partition"]);
}

#[test]
fn one_long_face_retains_disjoint_opposite_spans_and_cannot_bridge_hole() {
    let w = scan(vec![
        line("continuous", None, p(0., 0.), p(160., 0.)),
        line("left-a", None, p(0., 6.), p(30., 6.)),
        line("left-b", None, p(30., 6.), p(60., 6.)),
        line("right", None, p(160., 6.), p(90., 6.)),
    ]);
    assert_eq!(w.len(), 2);
    close(w[0].start.x, 0.);
    close(w[0].end.x, 60.);
    close(w[1].start.x, 90.);
    close(w[1].end.x, 160.);
    assert_eq!(w[0].source_ids, ["continuous", "left-a", "left-b"]);
    assert_eq!(w[1].source_ids, ["continuous", "right"]);
}

#[test]
fn partial_overlap_has_no_union_extension_or_unrelated_provenance() {
    let w = scan(vec![
        line("a", None, p(0., 10.), p(50., 10.)),
        line("b", None, p(50., 10.), p(130., 10.)),
        line("c", None, p(170., 18.), p(70., 18.)),
    ]);
    assert_eq!(w.len(), 1);
    close(w[0].start.x, 70.);
    close(w[0].end.x, 130.);
    close(w[0].thickness, 8.);
    assert_eq!(w[0].source_ids, ["b", "c"]);
}

#[test]
fn pair_overlap_threshold_is_inclusive_but_not_rounded_up() {
    let run = |start| {
        scan(vec![
            line("a", None, p(0., 0.), p(100., 0.)),
            line("b", None, p(start, 8.), p(start + 100., 8.)),
        ])
    };
    assert_eq!(run(45.).len(), 1); // 55/100 observed overlap
    assert!(run(45.001).is_empty());
}

#[test]
fn four_regular_hatch_members_are_rejected_without_poisoning_nearby_wall() {
    let mut prim: Vec<_> = (0..5)
        .map(|i| {
            line(
                &format!("h{i}"),
                None,
                p(0., 5. * i as f64),
                p(100., 5. * i as f64),
            )
        })
        .collect();
    prim.push(line("real-a", None, p(180., 10.), p(360., 10.)));
    prim.push(line("real-b", None, p(180., 18.), p(360., 18.)));
    let w = scan(prim);
    assert_eq!(w.len(), 1);
    assert_eq!(w[0].source_ids, ["real-a", "real-b"]);
}

#[test]
fn hollow_unknown_single_stroke_and_solid_triangle_do_not_prove_a_wall() {
    assert!(
        scan(vec![
            line("edge", None, p(0., 0.), p(220., 0.)),
            fill(
                "wedge",
                vec![p(0., 60.), p(220., 60.), p(220., 67.), p(216., 67.)]
            ),
            line("cabinet1", Some("A-FURN"), p(0., 80.), p(220., 80.)),
            line("cabinet2", Some("A-FURN"), p(0., 84.), p(220., 84.))
        ])
        .is_empty()
    );
}

#[test]
fn reversed_duplicate_paint_is_provenance_not_an_extra_wall() {
    let mut f = vec![
        fill(
            "paint",
            vec![p(40., 100.), p(220., 100.), p(220., 108.), p(40., 108.)],
        ),
        line("a", None, p(220., 100.), p(40., 100.)),
        line("b", None, p(40., 108.), p(220., 108.)),
        line("overpaint", None, p(40., 100.), p(220., 100.)),
    ];
    let w = scan(f.clone());
    f.reverse();
    let v = scan(f);
    assert_eq!(w.len(), 1);
    assert_eq!(v.len(), 1);
    assert_eq!(w[0].id, v[0].id);
    assert_eq!(w[0].body, BodyEvidence::FilledBody);
    assert_eq!(w[0].source_ids, ["a", "b", "overpaint", "paint"]);
    assert_eq!(v[0].source_ids, w[0].source_ids);
    close(w[0].start.y, 104.);
}

// Corresponds to CurvedWallDetectionTests: native geometry survives; graph
// code must treat this as a finite arc, never its chord or a complete circle.
#[test]
fn concentric_arcs_preserve_finite_overlap_across_angle_zero_and_reversals() {
    let c = p(150., 140.);
    let d = PI / 180.;
    let w = scan(vec![
        arc("face-a", c, 70., 320. * d, 100. * d),
        arc("face-b", c, 80., 70. * d, -100. * d),
    ]);
    assert_eq!(w.len(), 1);
    assert_eq!(w[0].body, BodyEvidence::ConcentricArcs);
    let g = w[0].curve.unwrap();
    close(g.radius, 75.);
    close(g.start_angle, 330. * d);
    close(g.sweep, 90. * d);
    assert!(w[0].requires_review);
    assert_eq!(w[0].source_ids, ["face-a", "face-b"]);
    assert!(w[0].start.distance(g.at(0.)) < 1e-5 && w[0].end.distance(g.at(1.)) < 1e-5);
}

#[test]
fn small_closed_symbol_rings_and_radial_stairs_are_not_curved_walls() {
    let c = p(150., 140.);
    let mut stairs = vec![
        arc("inner", c, 70., 0., PI * 0.45),
        arc("outer", c, 80., 0., PI * 0.45),
        arc("ring-in", c, 7., 0., TAU),
        arc("ring-out", c, 9., 0., TAU),
    ];
    for i in 0..6 {
        let a = 0.12 + 0.22 * i as f64;
        let v = p(a.cos(), a.sin());
        stairs.push(line(&format!("r{i}"), None, c + v * 30., c + v * 115.));
    }
    assert!(scan(stairs).iter().all(|w| w.curve.is_none()));
}

#[test]
fn cubic_sampled_circular_faces_are_paired_but_non_circular_shapes_are_not_fitted() {
    let from_radius = |id: &str, r: f64| {
        item(
            id,
            None,
            Shape::Polyline {
                closed: false,
                filled: false,
                points: (0..=12)
                    .map(|i| {
                        let a = i as f64 * PI / 24.;
                        p(100. + r * a.cos(), 100. + r * a.sin())
                    })
                    .collect(),
            },
        )
    };
    let w = scan(vec![from_radius("a", 60.), from_radius("b", 66.)]);
    assert_eq!(w.len(), 1);
    close(w[0].curve.unwrap().radius, 63.);
    assert!(
        fit_arc(
            &[p(0., 0.), p(12., 0.), p(12., 10.), p(5., 10.), p(1., 13.)],
            0,
            &Options::default()
        )
        .is_none()
    );
}

#[test]
fn work_budget_errors_do_not_silently_emit_incomplete_pages() {
    let mut o = Options::default();
    o.max_wall_seeds = 1;
    assert!(
        detect(
            &page(vec![
                line("a", None, p(0., 0.), p(80., 0.)),
                line("b", None, p(0., 8.), p(80., 8.))
            ]),
            &o
        )
        .is_err()
    );
    let mut invalid = Options::default();
    invalid.min_wall_pair_separation = f64::NAN;
    assert!(detect(&page(vec![]), &invalid).is_err());
}
