use super::*;
use crate::model::Primitive;
fn p(x: f64, y: f64) -> Point {
    Point::new(x, y)
}
fn line(id: &str, a: (f64, f64), b: (f64, f64)) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Line {
            start: p(a.0, a.1),
            end: p(b.0, b.1),
        },
    }
}
fn rect(id: &str, l: f64, t: f64, r: f64, b: f64, fill: bool) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Polyline {
            closed: true,
            filled: fill,
            points: vec![p(l, t), p(r, t), p(r, b), p(l, b)],
        },
    }
}
fn circle(id: &str, x: f64, y: f64, r: f64) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Arc {
            center: p(x, y),
            radius: r,
            start_angle: 0.,
            sweep: TAU,
        },
    }
}
fn pair(id: &str, a: (f64, f64), b: (f64, f64), width: f64, sources: &[&str]) -> Wall {
    Wall {
        id: id.into(),
        page: 1,
        start: p(a.0, a.1),
        end: p(b.0, b.1),
        thickness: width,
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: sources.iter().map(|s| s.to_string()).collect(),
        requires_review: true,
    }
}
fn candidates() -> Vec<Wall> {
    vec![
        pair(
            "observed-surface",
            (0., 10.),
            (45., 10.),
            20.,
            &["front", "rear"],
        ),
        pair(
            "continuous-wall",
            (-10., 22.),
            (85., 22.),
            4.,
            &["outside-a", "outside-b"],
        ),
    ]
}
fn base() -> Vec<Primitive> {
    vec![
        line("front", (0., 0.), (45., 0.)),
        line("rear", (0., 20.), (45., 20.)),
        line("left", (0., 0.), (0., 20.)),
        line("right", (45., 0.), (45., 20.)),
        line("outside-a", (-10., 20.), (85., 20.)),
        line("outside-b", (-10., 24.), (85., 24.)),
    ]
}
fn result(items: Vec<Primitive>, walls: Vec<Wall>) -> (Vec<Wall>, Vec<WithheldWall>) {
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
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 240.,
            height: 240.,
            units_to_mm: None,
            primitives: items,
        },
        walls,
        &Options::default(),
    )
    .unwrap()
}
#[test]
fn separately_bounded_fittings_select_the_backing_wall_and_keep_all_contact_evidence() {
    let mut source = base();
    source.extend([
        rect("display", 11., 5., 29., 8., false),
        rect("screen", 12., 5.5, 28., 7.5, false),
        rect("keys", 12., 12., 27., 14., false),
    ]);
    let (kept, hidden) = result(source.clone(), candidates());
    assert_eq!(
        kept.iter().map(|w| w.id.as_str()).collect::<Vec<_>>(),
        ["continuous-wall"]
    );
    assert_eq!(hidden[0].reason, WallSelectionReason::FurnishedSurface);
    for id in [
        "display",
        "screen",
        "keys",
        "front",
        "rear",
        "left",
        "right",
        "outside-a",
        "outside-b",
    ] {
        assert!(hidden[0].witness_source_ids.contains(&id.to_string()));
    }
    // A solid footprint, missing independent backing, just one isolated
    // fitting, or an incomplete enclosure cannot justify this rejection.
    let mut column = source.clone();
    column.push(rect("poured", 0., 0., 45., 20., true));
    assert!(result(column, candidates()).1.is_empty());
    assert!(
        result(source.clone(), vec![candidates()[0].clone()])
            .1
            .is_empty()
    );
    for absent in ["keys", "left", "screen"] {
        let mut incomplete = source.clone();
        incomplete.retain(|p| p.id != absent);
        assert!(result(incomplete, candidates()).1.is_empty());
    }
}
#[test]
fn independent_ring_symbols_survive_duplicate_outer_strokes_and_hatch_is_not_equipment() {
    let mut rings = base();
    rings.extend([
        circle("r1", 12., 10., 5.),
        circle("r1-core", 12., 10., 3.),
        circle("r2", 30., 10., 5.),
        circle("r2-core", 30., 10., 3.),
        circle("r1-copy", 12., 10., 5.),
    ]);
    let (kept, hidden) = result(rings, candidates());
    assert_eq!(kept.len(), 1);
    assert_eq!(hidden[0].reason, WallSelectionReason::FurnishedSurface);
    let mut hatch = base();
    for i in 1..10 {
        hatch.push(line(
            &format!("hatch-{i}"),
            (i as f64 * 4., 0.),
            (i as f64 * 4. + 2., 20.),
        ));
    }
    assert!(result(hatch, candidates()).1.is_empty());
}

#[test]
fn occupied_tabletop_overhanging_a_short_frame_keeps_only_the_frame() {
    let mut source = vec![
        line("front", (-0.6, 0.), (81., 0.)),
        line("left", (-0.6, 0.), (-0.6, 19.4)),
        line("right", (81., 0.), (81., 19.4)),
        line("rear", (-0.6, 19.4), (81., 19.4)),
        line("frame-trim", (0., 20.), (80., 20.)),
        line("frame-a", (-0.7, 20.1), (81., 20.1)),
        line("frame-b", (-0.7, 23.7), (81., 23.7)),
        rect("display", 50., 3., 67., 8., false),
        rect("screen", 51., 4., 66., 7., false),
        rect("keys", 25., 5., 34., 12., false),
    ];
    let walls = vec![
        pair(
            "broad",
            (0., 10.),
            (80., 10.),
            20.,
            &["front", "frame-trim"],
        ),
        pair(
            "short-frame",
            (-0.7, 21.9),
            (81., 21.9),
            3.6,
            &["frame-a", "frame-b"],
        ),
    ];
    let (selected, rejected) = result(source.clone(), walls.clone());
    assert_eq!(selected.len(), 1);
    assert_eq!(selected[0].id, "short-frame");
    assert_eq!(rejected[0].reason, WallSelectionReason::FurnishedSurface);
    // Object placement follows the drawn tabletop even where its ends and rear
    // project beyond the broad wall observation that triggered the check.
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
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 240.,
        height: 240.,
        units_to_mm: None,
        primitives: source.clone(),
    };
    let (objects, _) = crate::objects::detect_selected(
        &page,
        &[],
        &selected,
        &[],
        &[rejected[0].clone(), rejected[0].clone()],
        &Options::default(),
    )
    .unwrap();
    assert_eq!(objects.len(), 1);
    let desk = &objects[0];
    assert_eq!(desk.category, "FurnishedSurface");
    assert_eq!(desk.source_ids, rejected[0].witness_source_ids);
    assert!(desk.room_id.is_none() && desk.requires_review);
    let poly = desk.footprint.as_ref().unwrap().polygon;
    for corner in [p(-0.6, 0.), p(81., 0.), p(-0.6, 19.4), p(81., 19.4)] {
        assert!(poly.contains(&corner));
    }
    let refs = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut wrong = poly;
    wrong[0].x += 0.1;
    assert!(crate::objects::checked_outline(&wrong, &rejected[0], &refs, &mut 100000).is_err());
    let mut ambiguous = rejected[0].clone();
    ambiguous.reason = WallSelectionReason::OutlineEdgePair;
    ambiguous.furniture_outline = None;
    assert!(
        crate::objects::detect_selected(
            &page,
            &[],
            &selected,
            &[],
            &[ambiguous],
            &Options::default()
        )
        .unwrap()
        .0
        .is_empty()
    );
    for id in [
        "frame-a", "frame-b", "left", "right", "rear", "display", "screen", "keys",
    ] {
        assert!(rejected[0].witness_source_ids.contains(&id.to_string()));
    }
    // A rectangle with no fittings, a missing end or a merely coincident
    // frame assembled from the tabletop itself must remain reviewable.
    for absent in ["keys", "rear"] {
        let incomplete = source.iter().filter(|s| s.id != absent).cloned().collect();
        assert!(result(incomplete, walls.clone()).1.is_empty());
    }
    let mut shared = walls.clone();
    shared[1].source_ids = vec!["rear".into(), "frame-b".into()];
    assert!(result(source.clone(), shared).1.is_empty());
    source.push(rect("painted-material", 0., 0., 80., 20., true));
    assert!(result(source, walls).1.is_empty());
}
