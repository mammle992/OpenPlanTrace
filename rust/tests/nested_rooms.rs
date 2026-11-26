use openplantrace_rs::{model::*, topology};

fn page() -> Page {
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
        recessed_stock: vec![],
        masked_junction_stock: vec![],
        pocket_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 1000.,
        height: 1000.,
        units_to_mm: None,
        primitives: vec![],
    }
}

fn ring(walls: &mut Vec<Wall>, prefix: &str, points: &[(f64, f64)]) {
    for i in 0..points.len() {
        let (ax, ay) = points[i];
        let (bx, by) = points[(i + 1) % points.len()];
        let id = format!("{prefix}-{i}");
        walls.push(Wall {
            id: id.clone(),
            page: 1,
            start: Point::new(ax, ay),
            end: Point::new(bx, by),
            thickness: 0.,
            body: BodyEvidence::ExplicitCenterline,
            curve: None,
            source_ids: vec![id],
            requires_review: true,
        });
    }
}
fn rectangle(w: &mut Vec<Wall>, prefix: &str, x: f64, y: f64, width: f64, height: f64) {
    ring(
        w,
        prefix,
        &[
            (x, y),
            (x + width, y),
            (x + width, y + height),
            (x, y + height),
        ],
    );
}

#[test]
fn disconnected_enclosure_is_not_exported_as_a_filled_room_over_its_island() {
    let mut w = vec![];
    rectangle(&mut w, "outer", 0., 0., 100., 100.);
    rectangle(&mut w, "inner", 25., 25., 50., 50.);
    let g = topology::build(&page(), &w, &[], &Options::default()).unwrap();
    assert_eq!(
        g.edges.len(),
        8,
        "both wall rings must survive in the graph"
    );
    assert_eq!(g.rooms.len(), 1);
    assert_eq!(g.rooms[0].area, 2500.);
    assert!(
        g.rooms[0].requires_review
            && g.rooms[0]
                .wall_ids
                .iter()
                .all(|id| id.starts_with("inner-"))
    );
}
#[test]
fn an_inner_loop_below_the_minimum_room_area_still_prevents_a_false_full_outer_room() {
    let mut w = vec![];
    rectangle(&mut w, "outer", 0., 0., 100., 100.);
    rectangle(&mut w, "island", 30., 30., 10., 10.);
    let g = topology::build(&page(), &w, &[], &Options::default()).unwrap();
    assert_eq!(g.edges.len(), 8);
    assert!(g.rooms.is_empty());
}
#[test]
fn adjacent_rooms_and_a_separate_building_are_retained() {
    let mut w = vec![];
    rectangle(&mut w, "left", 10., 10., 40., 30.);
    rectangle(&mut w, "right", 50., 10., 40., 30.); // Same boundary, reverse direction
    rectangle(&mut w, "separate", 110., 10., 30., 30.);
    let g = topology::build(&page(), &w, &[], &Options::default()).unwrap();
    let mut areas: Vec<_> = g.rooms.iter().map(|r| r.area).collect();
    areas.sort_by(f64::total_cmp);
    assert_eq!(areas, vec![900., 1200., 1200.]);
}
#[test]
fn a_disconnected_face_in_a_concave_notch_is_not_a_hole() {
    let mut w = vec![];
    ring(
        &mut w,
        "ell",
        &[
            (0., 0.),
            (100., 0.),
            (100., 20.),
            (20., 20.),
            (20., 100.),
            (0., 100.),
        ],
    );
    rectangle(&mut w, "notch", 50., 50., 30., 30.);
    let g = topology::build(&page(), &w, &[], &Options::default()).unwrap();
    assert_eq!(g.rooms.len(), 2);
    assert_eq!(g.rooms.iter().map(|r| r.area).sum::<f64>(), 4500.);
}
