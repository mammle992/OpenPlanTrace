use super::*;
use std::collections::HashMap;
use std::f64::consts::{FRAC_PI_2, PI};
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
fn arc(id: &str, a: Point, angle: f64, sweep: f64) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Arc {
            center: a,
            radius: 24.,
            start_angle: angle,
            sweep,
        },
    }
}
fn wall(id: &str, a: Point, b: Point, width: f64, ids: &[&str]) -> Wall {
    Wall {
        id: id.into(),
        page: 1,
        start: a,
        end: b,
        thickness: width,
        curve: None,
        body: BodyEvidence::ParallelFaces,
        source_ids: ids.iter().map(|&x| x.to_owned()).collect(),
        requires_review: true,
    }
}
fn page(primitives: Vec<Primitive>) -> Page {
    Page {
        finish_history: None,
        pdf_input: None,
        number: 1,
        placement_paint_ids: None,
        placement_polygon_paint_ids: None,
        material_tiles: vec![],
        width: 500.,
        height: 500.,
        units_to_mm: None,
        primitives,
    }
}
fn opposing() -> Page {
    page(vec![
        arc("first", pt(30., 20.), 0., FRAC_PI_2),
        arc("second", pt(54., 20.), PI, -FRAC_PI_2),
        line("left jamb", pt(30., 17.), pt(30., 23.)),
        line("right jamb", pt(54., 17.), pt(54., 23.)),
        line("first open", pt(30., 20.), pt(30., 44.)),
        line("second open", pt(54., 20.), pt(54., 44.)),
        line("shut", pt(30., 20.), pt(54., 20.)),
    ])
}
#[test]
fn one_physical_cut_retains_two_authored_hands_and_their_own_leaf_strokes() {
    let mut source = opposing();
    let host = wall("wall", pt(1., 20.), pt(90., 20.), 4., &[]);
    for _ in 0..2 {
        let openings = detect(&source, std::slice::from_ref(&host), &Options::default()).unwrap();
        assert_eq!(openings.len(), 1);
        let door = &openings[0];
        assert_eq!(door.swing_observations.len(), 2);
        assert_eq!(
            door.swing_observations[0].open_leaf_source_ids,
            ["first open"]
        );
        assert_eq!(
            door.swing_observations[1].open_leaf_source_ids,
            ["second open"]
        );
        assert_eq!(door.start, pt(30., 20.));
        assert_eq!(door.end, pt(54., 20.));
        assert_eq!(crate::export::coverage(&host, &openings).0.len(), 1);
        source.primitives.reverse();
    }
}
#[test]
fn differing_jambs_or_missing_radial_leaf_cannot_merge_overlapping_door_claims() {
    let host = wall("wall", pt(1., 20.), pt(90., 20.), 4., &[]);
    let mut source = opposing();
    source.primitives.retain(|p| p.id != "second open");
    assert!(
        detect(&source, std::slice::from_ref(&host), &Options::default())
            .unwrap()
            .is_empty()
    );
    let mut source = opposing();
    source.primitives[1] = arc("second", pt(51., 20.), PI, -FRAC_PI_2);
    source.primitives[5] = line("second open", pt(51., 20.), pt(51., 44.));
    assert!(
        detect(&source, &[host], &Options::default())
            .unwrap()
            .is_empty()
    );
}
#[test]
fn two_observations_of_each_physical_jamb_do_not_hide_a_real_gap() {
    let p = page(vec![arc("swing", pt(30., 20.), 0., FRAC_PI_2)]);
    let mut walls = vec![
        wall("a", pt(1., 20.), pt(29., 20.), 4., &["l"]),
        wall("a2", pt(29., 20.1), pt(5., 20.1), 4., &["l2"]),
        wall("b", pt(55., 20.), pt(89., 20.), 4., &["r"]),
        wall("b2", pt(55., 20.), pt(85., 20.), 4., &["r2"]),
    ];
    let options = Options::default();
    let result = detect(&p, &walls, &options).unwrap();
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].host_wall_ids.len(), 4);
    assert!(
        walls
            .iter()
            .all(|w| crate::export::coverage(w, &result).0.is_empty())
    );
    walls[1].start.y += 1.;
    walls[1].end.y += 1.;
    assert!(detect(&p, &walls, &options).unwrap().is_empty());
}
fn face_host() -> (Page, Vec<Wall>) {
    let p = page(vec![
        arc("swing", pt(30., 26.), 0., FRAC_PI_2),
        line("first jamb", pt(30., 26.), pt(30., 19.)),
        line("other jamb", pt(54., 26.), pt(54., 19.)),
        line("leaf section A", pt(30., 26.), pt(30., 39.)),
        line("leaf section B", pt(30., 39.), pt(30., 50.)),
        line("leaf end", pt(30., 50.), pt(32., 50.)),
        line("hinge edge", pt(30., 26.), pt(32., 26.)),
        line("unrelated paint", pt(75., 80.), pt(76., 80.)),
    ]);
    let walls = vec![
        wall(
            "real host",
            pt(0., 20.),
            pt(100., 20.),
            12.,
            &["wall faces"],
        ),
        wall("closed panel", pt(30.6, 23.), pt(53.6, 23.), 2., &["panel"]),
        wall(
            "open panel",
            pt(31., 26.),
            pt(31., 50.),
            2.,
            &["leaf section A", "leaf section B"],
        ),
    ];
    (p, walls)
}
#[test]
fn face_mounted_swing_cuts_through_host_and_enclosed_closed_panel_without_cutting_jambs() {
    let (p, walls) = face_host();
    let options = Options::default();
    let openings = detect(&p, &walls, &options).unwrap();
    assert_eq!(openings.len(), 1);
    assert_eq!(openings[0].host_wall_ids, ["real host", "closed panel"]);
    let (_, material) = crate::export::coverage(&walls[0], &openings);
    assert_eq!(material.len(), 2);
    assert_eq!(material[0].end, pt(30., 20.));
    assert_eq!(material[1].start, pt(54., 20.));
    assert!(crate::export::coverage(&walls[1], &openings).1.is_empty());
    let withheld = door_leaf_walls(&p, &walls, &openings, &options).unwrap();
    assert_eq!(withheld.len(), 1);
    assert_eq!(
        withheld["open panel"],
        ["leaf section A", "leaf section B", "swing"]
    );
    let extended = wall(
        "long wall",
        pt(31., 15.),
        pt(31., 60.),
        2.,
        &["leaf section A"],
    );
    assert!(
        door_leaf_walls(&p, &[extended], &openings, &options)
            .unwrap()
            .is_empty()
    );
}
#[test]
fn explicit_cad_wall_sources_protect_a_panel_even_when_it_coincides_with_a_leaf() {
    let (mut p, walls) = face_host();
    let openings = detect(&p, &walls, &Options::default()).unwrap();
    p.primitives
        .iter_mut()
        .find(|p| p.id == "leaf section B")
        .unwrap()
        .layer = Some("A-WALL".into());
    assert!(
        door_leaf_walls(&p, &walls, &openings, &Options::default())
            .unwrap()
            .is_empty()
    );
    assert!(detect(&p, &walls, &Options::default()).unwrap().is_empty());
}
#[test]
fn forged_radial_source_and_turning_a_door_perpendicular_fail_validation() {
    let (p, walls) = face_host();
    let map: HashMap<_, _> = p.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let original = detect(&p, &walls, &Options::default()).unwrap().remove(0);
    assert!(validate_swing_sources(&original, &map));
    let mut forged = original.clone();
    forged.source_ids.push("unrelated paint".into());
    forged.swing_observations[0].open_leaf_source_ids = vec!["unrelated paint".into()];
    assert!(!validate_swing_sources(&forged, &map));
    let mut incomplete = original.clone();
    incomplete.swing_observations[0].open_leaf_source_ids.pop();
    assert!(!validate_swing_sources(&incomplete, &map));
    let mut bad_angle = original;
    bad_angle.end = bad_angle.start + pt(0., 24.);
    assert!(!validate_swing_sources(&bad_angle, &map));
}

fn face_opening(cap_end: f64) -> Page {
    page(vec![
        arc("arc", pt(30., 0.), 0., -FRAC_PI_2),
        line("open leaf", pt(30., 0.), pt(30., -24.)),
        line("closed leaf", pt(30., 0.), pt(54., 0.)),
        line("hinge cap", pt(30., 0.), pt(30., cap_end)),
        line("latch cap", pt(54., 0.), pt(54., cap_end)),
    ])
}

#[test]
fn short_caps_must_cross_the_measured_host_and_have_both_leaf_positions() {
    let opts = Options::default();
    let host = wall("wall", pt(0., 0.8), pt(80., 0.8), 2., &["faces"]);
    let mut p = face_opening(1.4);
    let found = detect(&p, std::slice::from_ref(&host), &opts).unwrap();
    assert_eq!(found.len(), 1);
    for cap in ["hinge cap", "latch cap"] {
        assert!(found[0].source_ids.iter().any(|id| id == cap));
    }
    let incomplete = face_opening(0.15);
    assert!(
        detect(&incomplete, std::slice::from_ref(&host), &opts)
            .unwrap()
            .is_empty()
    );
    p.primitives.retain(|p| p.id != "closed leaf");
    assert!(detect(&p, &[host], &opts).unwrap().is_empty());
}

#[test]
fn caps_on_continuing_wall_resolve_a_superimposed_face_pair_bounded_by_both_jambs() {
    let p = face_opening(5.4);
    let opts = Options::default();
    let host = wall("wall", pt(0., 5.), pt(90., 5.), 10., &["traced face"]);
    let overlay = wall(
        "bounded pair",
        pt(30., 0.),
        pt(54., 0.),
        12.,
        &["frame", "second stroke"],
    );
    let found = detect(&p, &[host.clone(), overlay.clone()], &opts).unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].host_wall_ids, ["wall", "bounded pair"]);
    assert!(crate::export::coverage(&overlay, &found).1.is_empty());
    assert_eq!(crate::export::coverage(&host, &found).1.len(), 2);
    let mut continuing = overlay;
    continuing.start.x = 0.;
    continuing.end.x = 90.;
    assert!(detect(&p, &[host, continuing], &opts).unwrap().is_empty());
}

#[test]
fn a_cut_reaches_both_nearly_coincident_measured_bodies_without_reaching_another_wall() {
    let p = face_opening(5.4);
    let opts = Options::default();
    let ws = [
        wall("primary", pt(0., 5.), pt(90., 5.), 10., &["face"]),
        wall(
            "overprint",
            pt(80., 5.7),
            pt(4., 5.7),
            10.,
            &["second face"],
        ),
        wall("nearby", pt(0., 7.), pt(85., 7.), 10., &["separate face"]),
    ];
    let found = detect(&p, &ws, &opts).unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].host_wall_ids, ["primary", "overprint"]);
    assert_eq!(crate::export::coverage(&ws[1], &found).0.len(), 1);
    assert!(crate::export::coverage(&ws[2], &found).0.is_empty());
}

#[test]
fn matching_finite_flanks_preserve_their_ends_and_corroborate_one_cut_across_the_overdraw() {
    let p = face_opening(3.);
    let opts = Options::default();
    let ws = [
        wall("overdraw", pt(0., 2.), pt(100., 2.), 4., &["ink"]),
        wall("left flank", pt(3., 2.), pt(29., 2.), 4., &["left"]),
        wall("right flank", pt(55., 2.), pt(99., 2.), 4., &["right"]),
    ];
    let result = detect(&p, &ws, &opts).unwrap();
    assert_eq!(result.len(), 1);
    assert_eq!(result[0].host_wall_ids, ["overdraw"]);
    assert_eq!(result[0].evidence, OpeningEvidence::FiniteJambSwing);
    for id in ["left", "right"] {
        assert!(result[0].source_ids.iter().any(|s| s == id));
    }
    assert_eq!(crate::export::coverage(&ws[0], &result).0.len(), 1);
    for flank in &ws[1..] {
        assert!(crate::export::coverage(flank, &result).0.is_empty());
    }
    let mut mismatched = ws;
    mismatched[2].thickness = 8.;
    assert!(detect(&p, &mismatched, &opts).unwrap().is_empty());
}

fn jamb_block(id: &str, x: f64, out: f64) -> Primitive {
    Primitive {
        layer_category_override: None,
        stroke_width: None,
        id: id.into(),
        layer: None,
        shape: Shape::Polyline {
            points: vec![pt(x, 0.), pt(x + out, 0.), pt(x + out, 1.), pt(x, 1.)],
            closed: true,
            filled: true,
        },
    }
}
#[test]
fn a_finite_open_leaf_and_two_outward_jamb_blocks_prove_a_gap_without_a_closed_stroke() {
    let host = wall("host", pt(0., 1.), pt(90., 1.), 2., &["faces"]);
    let p = page(vec![
        arc("arc", pt(30., 0.), 0., -FRAC_PI_2),
        line("leaf", pt(30., 0.), pt(30., -24.)),
        jamb_block("hinge", 30., -0.8),
        jamb_block("latch", 54., 0.8),
    ]);
    let run = |p: &Page| detect(p, std::slice::from_ref(&host), &Options::default()).unwrap();
    let found = run(&p);
    assert_eq!(found.len(), 1);
    assert!(
        found[0].swing_observations[0]
            .closed_leaf_source_ids
            .is_empty()
    );
    assert_eq!(
        found[0].swing_observations[0].open_leaf_source_ids,
        ["leaf"]
    );
    assert_eq!(crate::export::coverage(&host, &found).0.len(), 1);
    let mut inward = p.clone();
    inward.primitives[3] = jamb_block("latch", 54., -0.8);
    assert!(run(&inward).is_empty());
    let mut missing_leaf = p;
    missing_leaf.primitives.remove(1);
    assert!(run(&missing_leaf).is_empty());
}
