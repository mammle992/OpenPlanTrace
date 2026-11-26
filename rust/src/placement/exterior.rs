//! Finite painted wall bodies with independent complete face and terminal ink.
//! Source authority stays independent of a coalesced room/area/furniture graph.
use super::{WallDecision, WallReason, WallStatus, material, overlap, roundoff};
use crate::model::{BodyEvidence, LayerKind, OpeningKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 32_000_000;
const MAX_INK: usize = 100_000;
const MAX_PROPOSALS: usize = 4096;

fn charge(work: &mut usize, amount: usize) -> Result<(), String> {
    *work = work
        .checked_sub(amount)
        .ok_or("independent wall-body evidence budget exceeded")?;
    Ok(())
}

fn structural(p: &Primitive) -> bool {
    match p.layer_category_override {
        Some(_) => p.layer_kind() == LayerKind::Wall,
        None => material::wall_name(p.layer_name()).1,
    }
}

fn plan_sources(
    page: &PageScan,
    w: &Wall,
    by_id: &HashMap<&str, &Primitive>,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    for room in &page.rooms {
        charge(work, room.polygon.len().saturating_mul(4).saturating_add(1))?;
        if let Some(id) = &room.label_source_id
            && room.label.is_some()
            && by_id
                .get(id.as_str())
                .is_some_and(|p| matches!(p.shape, Shape::Text { .. }))
            && room
                .polygon
                .iter()
                .zip(room.polygon.iter().cycle().skip(1))
                .any(|(&a, &b)| {
                    [w.start, (w.start + w.end) * 0.5, w.end].iter().any(|&p| {
                        crate::model::on_segment(p, a, b).0.distance(p)
                            <= w.thickness * 2. + roundoff(w)
                    })
                })
        {
            return Ok(Some(vec![id.clone()]));
        }
    }
    for door in page.openings.iter().filter(|o| o.kind == OpeningKind::Door) {
        charge(work, 1)?;
        for swing in &door.swing_observations {
            charge(
                work,
                door.source_ids
                    .len()
                    .saturating_mul(swing.open_leaf_source_ids.len().saturating_add(1))
                    .saturating_add(swing.open_leaf_source_ids.len())
                    .saturating_add(1),
            )?;
            if door.source_ids.contains(&swing.arc_source_id)
                && by_id
                    .get(swing.arc_source_id.as_str())
                    .is_some_and(|p| matches!(p.shape, Shape::Arc { .. }))
                && !swing.open_leaf_source_ids.is_empty()
                && swing
                    .open_leaf_source_ids
                    .iter()
                    .all(|id| door.source_ids.contains(id) && by_id.contains_key(id.as_str()))
                && crate::model::on_segment(swing.hinge, w.start, w.end)
                    .0
                    .distance(swing.hinge)
                    <= 4. * swing.hinge.distance(swing.closed_tip)
            {
                let mut ids = vec![swing.arc_source_id.clone()];
                ids.extend(swing.open_leaf_source_ids.iter().cloned());
                return Ok(Some(ids));
            }
        }
    }
    Ok(None)
}

fn same_body(w: &Wall, p: &Primitive) -> bool {
    let Some(corners) = super::painted_rectangle(&p.shape) else {
        return false;
    };
    let axis = (w.end - w.start).unit();
    let off = Point::new(-axis.y, axis.x) * (w.thickness * 0.5);
    let measured = [w.start + off, w.end + off, w.end - off, w.start - off];
    let eps = roundoff(w);
    corners
        .iter()
        .all(|p| measured.iter().any(|q| p.distance(*q) <= eps))
}

#[derive(Clone)]
struct Ink<'a> {
    id: &'a str,
    a: Point,
    b: Point,
}

fn covering(parts: &mut [(f64, f64, &str)], length: f64, eps: f64) -> Option<Vec<String>> {
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = 0.;
    let mut ids = BTreeSet::new();
    for &(lo, hi, id) in parts.iter() {
        if hi <= end + eps {
            continue;
        }
        if lo > end + eps {
            return None;
        }
        end = hi;
        ids.insert(id.to_owned());
        if end >= length - eps {
            return Some(ids.into_iter().collect());
        }
    }
    None
}

/// The paint fixes the original finite body. Both faces must be independently
/// stroked in full; one exact finite face and a shared terminal prevent a remote
/// collinear line from supplying all the wall's end evidence.
fn proof(
    w: &Wall,
    fill: &Primitive,
    ink: &[Ink<'_>],
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let axis = (w.end - w.start).unit();
    let length = w.start.distance(w.end);
    let half = w.thickness * 0.5;
    let eps = roundoff(w);
    let mut faces = [vec![], vec![]];
    let mut caps = [vec![], vec![]];
    let mut finite_face = false;
    let mut terminal_faces = [[false, false]; 2];
    for s in ink {
        charge(work, 1)?;
        let a = s.a - w.start;
        let b = s.b - w.start;
        let (x, y) = (a.dot(axis), b.dot(axis));
        let (u, v) = (axis.cross(a), axis.cross(b));
        if (u - v).abs() <= eps && (u.abs() - half).abs() <= eps {
            let lo = x.min(y);
            let hi = x.max(y);
            if hi <= eps || lo >= length - eps {
                continue;
            }
            // A face can continue into a measured return at one end, but it
            // cannot borrow a long unrelated reference passing both ends.
            if lo < -eps && hi > length + eps {
                continue;
            }
            let side = usize::from(u > 0.);
            faces[side].push((lo.max(0.), hi.min(length), s.id));
            finite_face |= lo.abs() <= eps && (hi - length).abs() <= eps;
            terminal_faces[side][0] |= lo.abs() <= eps;
            terminal_faces[side][1] |= (hi - length).abs() <= eps;
        }
        if (x - y).abs() <= eps && u.min(v) >= -half - eps && u.max(v) <= half + eps {
            for (end, at) in [0., length].into_iter().enumerate() {
                if (x - at).abs() <= eps && u.max(v) - u.min(v) > eps {
                    caps[end].push((u.min(v) + half, u.max(v) + half, s.id));
                }
            }
        }
    }
    if !finite_face {
        return Ok(None);
    }
    for parts in faces.iter().chain(&caps) {
        charge(
            work,
            parts
                .len()
                .saturating_mul(parts.len().max(1).ilog2() as usize + 2),
        )?;
    }
    let Some(left) = covering(&mut faces[0], length, eps) else {
        return Ok(None);
    };
    let Some(right) = covering(&mut faces[1], length, eps) else {
        return Ok(None);
    };
    if left.iter().any(|id| right.contains(id)) {
        return Ok(None);
    }
    let cap = (0..2)
        .filter(|&i| terminal_faces.iter().all(|side| side[i]))
        .find_map(|i| covering(&mut caps[i], w.thickness, eps));
    let Some(cap) = cap else {
        return Ok(None);
    };
    let ids = std::iter::once(fill.id.clone())
        .chain(left)
        .chain(right)
        .chain(cap)
        .collect::<BTreeSet<_>>();
    Ok(Some(ids.into_iter().collect()))
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) -> Result<(), String> {
    if decisions.len() != page.walls.len()
        || decisions
            .iter()
            .zip(&page.walls)
            .any(|(d, w)| d.wall_id != w.id)
    {
        return Err("independent wall-body decisions are not aligned to source walls".into());
    }
    let mut work = MAX_WORK;
    charge(&mut work, page.primitives.len())?;
    let by_id: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let eligible_paint: Option<HashSet<_>> = page
        .placement_paint_ids
        .as_ref()
        .map(|ids| ids.iter().map(String::as_str).collect());
    charge(&mut work, eligible_paint.as_ref().map_or(0, HashSet::len))?;
    let mut claimed = HashSet::new();
    for item in page.withheld_walls.iter().flatten() {
        charge(&mut work, item.wall.source_ids.len().saturating_add(1))?;
        claimed.extend(item.wall.source_ids.iter().map(String::as_str));
    }
    for item in &page.objects {
        charge(&mut work, item.source_ids.len().saturating_add(1))?;
        if item.footprint.is_some() || item.geometry.is_some() {
            claimed.extend(item.source_ids.iter().map(String::as_str));
        }
    }
    let mut bodies = vec![];
    for (i, (w, d)) in page.walls.iter().zip(decisions.iter()).enumerate() {
        charge(&mut work, w.source_ids.len() + 1)?;
        if d.status != WallStatus::Review
            || w.body != BodyEvidence::FilledBody
            || w.curve.is_some()
            || !w.start.finite()
            || !w.end.finite()
            || !w.thickness.is_finite()
            || w.thickness <= 0.
            || w.start.distance(w.end) < 4. * w.thickness
        {
            continue;
        }
        let fill = w
            .source_ids
            .iter()
            .filter_map(|id| by_id.get(id.as_str()).copied())
            .find(|p| {
                p.layer.is_some()
                    && structural(p)
                    && same_body(w, p)
                    && eligible_paint
                        .as_ref()
                        .is_none_or(|paint| paint.contains(p.id.as_str()))
                    && !claimed.contains(p.id.as_str())
            });
        if let Some(fill) = fill {
            bodies.push((i, fill));
        }
    }
    if bodies.len() > MAX_PROPOSALS {
        return Err("independent wall-body proposal limit exceeded".into());
    }
    if bodies.is_empty() {
        return Ok(());
    }
    let needed: HashSet<_> = bodies
        .iter()
        .filter_map(|(_, p)| p.layer.as_deref())
        .collect();
    let mut ink = HashMap::<&str, Vec<Ink<'_>>>::new();
    let mut count = 0;
    for p in &page.primitives {
        charge(&mut work, 1)?;
        let Some(layer) = p.layer.as_deref().filter(|l| needed.contains(l)) else {
            continue;
        };
        if !structural(p) || matches!(p.shape, Shape::Polyline { filled: true, .. }) {
            continue;
        }
        let mut add = |a, b| -> Result<(), String> {
            charge(&mut work, 1)?;
            count += 1;
            if count > MAX_INK {
                return Err("independent wall-body source ink limit exceeded".into());
            }
            ink.entry(layer).or_default().push(Ink { id: &p.id, a, b });
            Ok(())
        };
        match &p.shape {
            Shape::Line { start, end } => add(*start, *end)?,
            Shape::Polyline {
                points,
                closed,
                filled: false,
            } => {
                for pair in points.windows(2) {
                    add(pair[0], pair[1])?;
                }
                if *closed && points.len() > 2 && points.first() != points.last() {
                    add(*points.last().unwrap(), points[0])?;
                }
            }
            _ => {}
        };
    }
    let mut proposals = vec![];
    for (i, fill) in bodies {
        let w = &page.walls[i];
        charge(&mut work, page.openings.len())?;
        if page
            .openings
            .iter()
            .any(|o| overlap::aperture_crosses(w, o))
        {
            continue;
        }
        let Some(context) = plan_sources(page, w, &by_id, &mut work)? else {
            continue;
        };
        let Some(mut ids) = proof(
            w,
            fill,
            ink.get(fill.layer.as_deref().unwrap())
                .map(Vec::as_slice)
                .unwrap_or_default(),
            &mut work,
        )?
        else {
            continue;
        };
        ids.extend(context.iter().cloned());
        ids.sort();
        ids.dedup();
        proposals.push((i, ids));
    }
    // Prefer one complete painted body when different observations describe
    // overlapping material. Earlier Ready placements always retain precedence.
    charge(
        &mut work,
        proposals
            .len()
            .saturating_mul(proposals.len().max(1).ilog2() as usize + 1),
    )?;
    proposals.sort_by(|(a, _), (b, _)| {
        let area =
            |i: usize| page.walls[i].start.distance(page.walls[i].end) * page.walls[i].thickness;
        area(*b).total_cmp(&area(*a)).then(a.cmp(b))
    });
    let mut ready: Vec<_> = decisions
        .iter()
        .enumerate()
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(i, _)| i)
        .collect();
    let mut accepted = vec![];
    for (i, ids) in proposals {
        charge(&mut work, ready.len())?;
        if ready
            .iter()
            .any(|&j| overlap::parallel_bodies_overlap(&page.walls[i], &page.walls[j]))
        {
            continue;
        }
        ready.push(i);
        accepted.push((i, ids));
    }
    // No partial decision mutation if evidence work exhausts its budget.
    for (i, ids) in accepted {
        decisions[i].status = WallStatus::Ready;
        decisions[i].reason = WallReason::IndependentlyOutlinedPaintedBody;
        decisions[i].support_source_ids = ids;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Document, Opening, OpeningEvidence, Options, Page, Room};

    fn fixture(angle: f64, scale: f64) -> (PageScan, Vec<WallDecision>) {
        let axis = Point::new(angle.cos(), angle.sin());
        let at = |x: f64, y: f64| {
            Point::new(100., 100.) + (axis * x + Point::new(-axis.y, axis.x) * y) * scale
        };
        let primitive = |id: &str, layer: &str, shape| Primitive {
            id: id.into(),
            layer: Some(layer.into()),
            layer_category_override: None,
            stroke_width: None,
            shape,
        };
        let line = |id: &str, a, b| primitive(id, "A-WALL", Shape::Line { start: a, end: b });
        let mut page = crate::pipeline::scan_document(
            Document {
                warnings: vec![],
                pages: vec![Page {
                    finish_history: None,
                    number: 1,
                    width: 1000.,
                    height: 1000.,
                    units_to_mm: None,
                    primitives: vec![],
                    pdf_input: None,
                    material_tiles: vec![],
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
                }],
            },
            &Options::default(),
            None,
            true,
        )
        .unwrap()
        .pages
        .remove(0);
        page.primitives = vec![
            primitive(
                "paint",
                "A-WALL",
                Shape::Polyline {
                    points: vec![at(0., 0.), at(80., 0.), at(80., 10.), at(0., 10.)],
                    closed: true,
                    filled: true,
                },
            ),
            line("face-a", at(0., 0.), at(80., 0.)),
            line("face-b", at(-10., 10.), at(80., 10.)),
            line("cap-a", at(80., 0.), at(80., 4.)),
            line("cap-b", at(80., 4.), at(80., 10.)),
            primitive(
                "incidental",
                "A-FURN",
                Shape::Line {
                    start: at(0., 0.),
                    end: at(80., 0.),
                },
            ),
            primitive(
                "room-name",
                "A-ROOM",
                Shape::Text {
                    at: at(40., 40.),
                    value: "OFFICE".into(),
                    height: 5. * scale,
                },
            ),
        ];
        page.placement_paint_ids = Some(vec!["paint".into()]);
        page.walls = vec![Wall {
            id: "body".into(),
            page: 1,
            start: at(0., 5.),
            end: at(80., 5.),
            thickness: 10. * scale,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids: vec!["paint".into(), "incidental".into()],
            requires_review: true,
        }];
        page.rooms = vec![Room {
            id: "room".into(),
            polygon: vec![at(0., 10.), at(80., 10.), at(80., 70.), at(0., 70.)],
            area: 4800. * scale * scale,
            label: Some("OFFICE".into()),
            label_source_id: Some("room-name".into()),
            wall_ids: vec![],
            boundary_evidence: None,
            requires_review: true,
        }];
        let decisions = vec![WallDecision {
            placement_extent: None,
            wall_id: "body".into(),
            status: WallStatus::Review,
            reason: WallReason::OverlappingWallAxes,
            component_id: "component".into(),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: vec![],
            opening_ids: vec![],
            room_ids: vec![],
        }];
        (page, decisions)
    }

    #[test]
    fn complete_independent_faces_and_terminal_recover_only_the_painted_body_across_rotations_and_scales()
     {
        for (angle, scale) in [(0., 1.), (0.73, 1.), (-0.41, 0.001), (1.2, 1000.)] {
            let (p, mut d) = fixture(angle, scale);
            let geometry = serde_json::to_value(&p.walls).unwrap();
            select(&p, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Ready, "{angle}/{scale}");
            assert_eq!(d[0].reason, WallReason::IndependentlyOutlinedPaintedBody);
            for id in ["paint", "face-a", "face-b", "cap-a", "cap-b", "room-name"] {
                assert!(
                    d[0].support_source_ids.contains(&id.to_owned()),
                    "missing{id}"
                );
            }
            assert!(!d[0].support_source_ids.contains(&"incidental".to_owned()));
            assert_eq!(geometry, serde_json::to_value(&p.walls).unwrap());
        }
    }

    #[test]
    fn missing_faces_caps_or_ineligible_paint_never_borrow_the_filled_outline() {
        for remove in ["face-a", "face-b", "cap-a", "cap-b"] {
            let (mut p, mut d) = fixture(0.4, 1.);
            p.primitives.retain(|s| s.id != remove);
            select(&p, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{remove}");
        }
        let (mut p, mut d) = fixture(0., 1.);
        p.placement_paint_ids = Some(vec![]);
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn a_small_real_face_gap_is_not_rounded_into_full_coverage() {
        for fraction in [0.95, 0.99999] {
            let (mut p, mut d) = fixture(0., 1.);
            if let Shape::Line { end, .. } = &mut p
                .primitives
                .iter_mut()
                .find(|s| s.id == "face-a")
                .unwrap()
                .shape
            {
                end.x = 100. + 80. * fraction;
            }
            select(&p, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review);
        }
    }

    #[test]
    fn one_closed_outline_cannot_claim_two_independent_faces() {
        let (mut p, mut d) = fixture(0., 1.);
        p.primitives
            .retain(|s| !["face-a", "face-b", "cap-a", "cap-b"].contains(&s.id.as_str()));
        let mut outline = p.primitives[0].clone();
        outline.id = "single-outline".into();
        if let Shape::Polyline { filled, .. } = &mut outline.shape {
            *filled = false;
        }
        p.primitives.push(outline);
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn non_plan_or_remote_room_context_cannot_qualify_an_elevation_beam() {
        let (mut p, mut d) = fixture(0., 1.);
        p.rooms.clear();
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        let (mut p, mut d) = fixture(0., 1.);
        for pt in &mut p.rooms[0].polygon {
            *pt = *pt + Point::new(1000., 1000.);
        }
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn a_local_source_hinged_door_is_context_but_a_distant_or_unwitnessed_one_is_not() {
        for (offset, source_arc, expected) in [
            (0., true, WallStatus::Ready),
            (1000., true, WallStatus::Review),
            (0., false, WallStatus::Review),
        ] {
            let (mut p, mut d) = fixture(0., 1.);
            p.rooms.clear();
            let hinge = Point::new(200. + offset, 150.);
            let closed = hinge + Point::new(20., 0.);
            let open = hinge + Point::new(0., 20.);
            let primitive = |id: &str, shape| Primitive {
                id: id.into(),
                layer: Some("A-DOOR".into()),
                layer_category_override: None,
                stroke_width: None,
                shape,
            };
            p.primitives.push(primitive(
                "door-arc",
                if source_arc {
                    Shape::Arc {
                        center: hinge,
                        radius: 20.,
                        start_angle: 0.,
                        sweep: std::f64::consts::FRAC_PI_2,
                    }
                } else {
                    Shape::Line {
                        start: hinge,
                        end: open,
                    }
                },
            ));
            p.primitives.push(primitive(
                "door-leaf",
                Shape::Line {
                    start: hinge,
                    end: open,
                },
            ));
            p.openings.push(Opening {
                physical_jambs: None,
                id: "context-door".into(),
                page: 1,
                kind: OpeningKind::Door,
                operation: "hinged".into(),
                host_wall_id: "other".into(),
                host_wall_ids: vec!["other".into()],
                start: hinge,
                end: closed,
                hinge: Some(hinge),
                swing_observations: vec![crate::model::SwingObservation {
                    arc_source_id: "door-arc".into(),
                    segment_source_ids: vec![],
                    hinge,
                    closed_tip: closed,
                    open_tip: open,
                    leaf_tolerance: 0.1,
                    open_leaf_source_ids: vec!["door-leaf".into()],
                    closed_leaf_source_ids: vec![],
                }],
                source_ids: vec!["door-arc".into(), "door-leaf".into()],
                evidence: OpeningEvidence::WallGapSwing,
                requires_review: true,
            });
            select(&p, &mut d).unwrap();
            assert_eq!(d[0].status, expected);
        }
    }

    #[test]
    fn object_layers_and_positively_claimed_furnishing_fill_stay_under_review() {
        let (mut p, mut d) = fixture(0., 1.);
        for primitive in &mut p.primitives {
            if primitive.layer.as_deref() == Some("A-WALL") {
                primitive.layer = Some("A-FURN".into());
            }
        }
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        let (mut p, mut d) = fixture(0., 1.);
        p.withheld_walls = Some(vec![crate::model::WithheldWall {
            wall: p.walls[0].clone(),
            reason: crate::model::WallSelectionReason::FurnishedSurface,
            witness_source_ids: vec!["paint".into()],
            furniture_outline: None,
        }]);
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn every_observed_aperture_is_protected_even_when_assigned_to_another_host() {
        for kind in [OpeningKind::Door, OpeningKind::Window] {
            let (mut p, mut d) = fixture(0., 1.);
            p.openings.push(Opening {
                physical_jambs: None,
                id: "unrelated".into(),
                page: 1,
                kind,
                operation: "test".into(),
                host_wall_id: "other".into(),
                host_wall_ids: vec!["other".into()],
                start: Point::new(130., 95.),
                end: Point::new(130., 115.),
                hinge: None,
                swing_observations: vec![],
                source_ids: vec![],
                evidence: if kind == OpeningKind::Door {
                    OpeningEvidence::WallGapSwing
                } else {
                    OpeningEvidence::FiniteWindowAssembly
                },
                requires_review: true,
            });
            select(&p, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review);
        }
    }

    #[test]
    fn prior_ready_decisions_and_material_take_precedence() {
        let (mut p, mut d) = fixture(0., 1.);
        let mut other = p.walls[0].clone();
        other.id = "prior".into();
        p.walls.push(other);
        let mut prior = d[0].clone();
        prior.wall_id = "prior".into();
        prior.status = WallStatus::Ready;
        prior.support_source_ids = vec!["prior-evidence".into()];
        d.push(prior.clone());
        select(&p, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        assert_eq!(d[1], prior);
    }

    #[test]
    fn source_work_exhaustion_is_explicit() {
        let (p, _) = fixture(0., 1.);
        let w = &p.walls[0];
        let mut work = 0;
        let ink = [Ink {
            id: "face",
            a: Point::new(100., 100.),
            b: Point::new(180., 100.),
        }];
        assert!(
            proof(w, &p.primitives[0], &ink, &mut work)
                .unwrap_err()
                .contains("budget")
        );
    }
}
