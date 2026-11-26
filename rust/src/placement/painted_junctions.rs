//! Finite, independently faced portions of visible structural paint.
//! Internal face interruptions need complete independently faced return material;
//! a contained plain face pair additionally needs both painted terminal returns.
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, material, overlap, roundoff};
use crate::model::{BodyEvidence, LayerKind, OpeningKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 32_000_000;
const MAX_INK: usize = 100_000;
const MAX_PAINT: usize = 32_768;
const MAX_PROPOSALS: usize = 4096;

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("painted junction evidence budget exceeded")?;
    Ok(())
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::model::{FurnitureFootprint, Object, Opening, OpeningEvidence, Room};

    pub(crate) fn fixture(angle: f64, scale: f64, reverse: bool) -> (PageScan, Vec<WallDecision>) {
        let axis = Point::new(angle.cos(), angle.sin());
        let n = Point::new(-axis.y, axis.x);
        let at = |x: f64, y: f64| Point::new(1000., 1000.) + (axis * x + n * y) * scale;
        let line = |id: &str, a: Point, b: Point| Primitive {
            id: id.into(),
            layer: Some("A-WALL".into()),
            layer_category_override: None,
            stroke_width: None,
            shape: Shape::Line { start: a, end: b },
        };
        let fill = |id: &str, x0, y0, x1, y1| Primitive {
            id: id.into(),
            layer: Some("A-WALL".into()),
            layer_category_override: None,
            stroke_width: None,
            shape: Shape::Polyline {
                points: vec![at(x0, y0), at(x1, y0), at(x1, y1), at(x0, y1)],
                closed: true,
                filled: true,
            },
        };
        let primitives = vec![
            fill("main paint", 0., -5., 200., 5.),
            line("left prefix", at(0., -5.), at(10., -5.)),
            line("left continuous", at(30., -5.), at(200., -5.)),
            line("right a", at(0., 5.), at(80., 5.)),
            line("right b", at(88., 5.), at(130., 5.)),
            line("right c", at(136., 5.), at(200., 5.)),
            line("upper near", at(80., 5.), at(80., 35.)),
            line("upper far", at(88., 5.), at(88., 35.)),
            line("lower near", at(130., 5.), at(130., 35.)),
            line("lower far", at(136., 5.), at(136., 35.)),
            fill("upper paint a", 80., 5., 84., 35.),
            fill("upper paint b", 84., 5., 88., 35.),
            fill("lower paint a", 130., 5., 133., 35.),
            fill("lower paint b", 133., 5., 136., 35.),
            Primitive {
                id: "room name".into(),
                layer: Some("A-ROOM".into()),
                layer_category_override: None,
                stroke_width: None,
                shape: Shape::Text {
                    at: at(100., -30.),
                    height: 5. * scale,
                    value: "OFFICE".into(),
                },
            },
            Primitive {
                id: "incidental cabinet".into(),
                layer: Some("A-FURN".into()),
                layer_category_override: None,
                stroke_width: None,
                shape: Shape::Line {
                    start: at(30., -5.),
                    end: at(200., -5.),
                },
            },
        ];
        let mut wall = Wall {
            id: "painted body".into(),
            page: 1,
            start: at(0., 0.),
            end: at(200., 0.),
            thickness: 10. * scale,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids: vec!["main paint".into()],
            requires_review: true,
        };
        if reverse {
            std::mem::swap(&mut wall.start, &mut wall.end);
        }
        let decision = WallDecision {
            wall_id: wall.id.clone(),
            status: WallStatus::Review,
            reason: WallReason::OverlappingWallAxes,
            component_id: "observed component".into(),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: vec![],
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        let mut paints = vec![
            "main paint".into(),
            "upper paint a".into(),
            "upper paint b".into(),
            "lower paint a".into(),
            "lower paint b".into(),
        ];
        paints.sort();
        let page = PageScan {
            finish_visibility: None,
            pdf_input: None,
            page_number: 1,
            exterior_enclosures: None,
            exterior_connections: None,
            placement_paint_ids: Some(paints),
            placement_polygon_paint_ids: Some(vec![]),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 10000.,
            height: 10000.,
            units_to_millimeters: None,
            scale_source: None,
            calibration_source_ids: vec![],
            primitives,
            walls: vec![wall],
            placement_decisions: None,
            withheld_walls: Some(vec![]),
            openings: vec![],
            window_panes: vec![],
            nodes: vec![],
            edges: vec![],
            rooms: vec![Room {
                id: "room".into(),
                boundary_evidence: None,
                polygon: vec![at(0., -5.), at(0., -60.), at(200., -60.), at(200., -5.)],
                area: 11000. * scale * scale,
                wall_ids: vec![],
                label: Some("OFFICE".into()),
                label_source_id: Some("room name".into()),
                requires_review: true,
            }],
            objects: vec![],
            object_groups: vec![],
            room_connections: vec![],
            drawing_features: vec![],
            semantics: Default::default(),
            room_annotations: vec![],
            warnings: vec![],
        };
        (page, vec![decision])
    }

    #[test]
    fn return_material_closes_only_internal_face_gaps_and_selects_source_bounded_span() {
        for angle in [0., 0.71, -1.12] {
            for scale in [0.001, 1., 1000.] {
                for reverse in [false, true] {
                    let (page, mut d) = fixture(angle, scale, reverse);
                    let observed = serde_json::to_value(&page).unwrap();
                    select(&page, &mut d).unwrap();
                    assert_eq!(d[0].status, WallStatus::Ready, "{angle}/{scale}/{reverse}");
                    assert_eq!(d[0].reason, WallReason::SourceBoundedPaintedJunctionSpan);
                    let extent = d[0].placement_extent.as_ref().unwrap();
                    assert!(
                        (extent.start.distance(extent.end) - 170. * scale).abs()
                            < 1e-7 * scale.max(1.)
                    );
                    assert_eq!(extent.source_ids, d[0].support_source_ids);
                    for id in [
                        "main paint",
                        "left continuous",
                        "right a",
                        "right b",
                        "right c",
                        "upper near",
                        "upper far",
                        "lower near",
                        "lower far",
                        "upper paint a",
                        "upper paint b",
                        "lower paint a",
                        "lower paint b",
                        "room name",
                    ] {
                        assert!(d[0].support_source_ids.iter().any(|p| p == id), "{id}");
                    }
                    assert!(
                        !d[0]
                            .support_source_ids
                            .iter()
                            .any(|p| p == "incidental cabinet" || p == "left prefix")
                    );
                    assert_eq!(serde_json::to_value(&page).unwrap(), observed);
                }
            }
        }
    }

    #[test]
    fn each_gap_needs_its_own_two_faces_and_complete_visible_material() {
        for missing in [
            "upper near",
            "upper far",
            "lower near",
            "lower far",
            "upper paint a",
            "upper paint b",
            "lower paint a",
            "lower paint b",
            "left continuous",
            "main paint",
        ] {
            let (mut page, mut d) = fixture(0.3, 1., false);
            page.primitives.retain(|p| p.id != missing);
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{missing}");
        }
        for missing in ["main paint", "upper paint a", "lower paint b"] {
            let (mut page, mut d) = fixture(0., 1., false);
            page.placement_paint_ids
                .as_mut()
                .unwrap()
                .retain(|p| p != missing);
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "uncertified{missing}");
        }
    }

    #[test]
    fn repeated_partial_return_paint_cannot_fill_a_real_sliver() {
        let (mut page, mut d) = fixture(0., 1., false);
        let p = page
            .primitives
            .iter_mut()
            .find(|p| p.id == "upper paint b")
            .unwrap();
        if let Shape::Polyline { points, .. } = &mut p.shape {
            for q in points {
                if q.x < 1088. {
                    q.x += 0.01;
                }
            }
        }
        let mut copy = p.clone();
        copy.id = "duplicate incomplete paint".into();
        page.primitives.push(copy);
        page.placement_paint_ids
            .as_mut()
            .unwrap()
            .push("duplicate incomplete paint".into());
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn borrowed_furniture_or_single_outline_cannot_replace_structural_faces() {
        for source in ["left continuous", "right b", "upper near"] {
            let (mut page, mut d) = fixture(0., 1., false);
            page.primitives
                .iter_mut()
                .find(|p| p.id == source)
                .unwrap()
                .layer = Some("A-FURN".into());
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{source}");
        }
        let (mut page, mut d) = fixture(0., 1., false);
        page.rooms.clear();
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn two_faces_from_one_authored_outline_are_not_independent_witnesses() {
        let (mut page, mut d) = fixture(0., 1., false);
        page.walls[0].body = BodyEvidence::ParallelFaces;
        page.walls[0].start = Point::new(1088., 1000.);
        page.walls[0].end = Point::new(1130., 1000.);
        page.walls[0].source_ids = vec!["single outline".into()];
        page.primitives
            .retain(|p| p.id != "left continuous" && p.id != "right b");
        page.primitives.push(Primitive {
            id: "single outline".into(),
            layer: Some("A-WALL".into()),
            layer_category_override: None,
            stroke_width: None,
            shape: Shape::Polyline {
                points: vec![
                    Point::new(1030., 995.),
                    Point::new(1200., 995.),
                    Point::new(1130., 1005.),
                    Point::new(1088., 1005.),
                ],
                closed: false,
                filled: false,
            },
        });
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    fn object(id: &str, polygon: [Point; 4], source_ids: Vec<String>) -> Object {
        Object {
            geometry: None,
            profile_signature: None,
            footprint: Some(FurnitureFootprint {
                selection_id: id.into(),
                polygon,
            }),
            id: id.into(),
            at: polygon[0],
            category: "FurnishedSurface".into(),
            name: "Table".into(),
            room_id: None,
            source_ids,
            requires_review: true,
        }
    }
    #[test]
    fn claimed_furniture_material_and_spatial_interiors_veto_new_authority() {
        for patch in [false, true] {
            let (mut page, mut d) = fixture(0., 1., false);
            let polygon = if patch {
                [
                    Point::new(1081., 1010.),
                    Point::new(1087., 1010.),
                    Point::new(1087., 1020.),
                    Point::new(1081., 1020.),
                ]
            } else {
                [
                    Point::new(1100., 996.),
                    Point::new(1110., 996.),
                    Point::new(1110., 1004.),
                    Point::new(1100., 1004.),
                ]
            };
            page.objects.push(object("object", polygon, vec![]));
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{patch}");
        }
        let (mut page, mut d) = fixture(0., 1., false);
        page.objects.push(object(
            "claim",
            [
                Point::new(1500., 1500.),
                Point::new(1510., 1500.),
                Point::new(1510., 1510.),
                Point::new(1500., 1510.),
            ],
            vec!["main paint".into()],
        ));
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn any_opening_in_new_material_or_proving_return_stays_open() {
        for (start, end) in [
            (Point::new(1100., 996.), Point::new(1100., 1004.)),
            (Point::new(1082., 1010.), Point::new(1086., 1010.)),
        ] {
            let (mut page, mut d) = fixture(0., 1., false);
            page.openings.push(Opening {
                physical_jambs: None,
                id: "unrelated window".into(),
                page: 1,
                kind: OpeningKind::Window,
                operation: "Fixed".into(),
                host_wall_id: "another wall".into(),
                host_wall_ids: vec!["another wall".into()],
                start,
                end,
                hinge: None,
                swing_observations: vec![],
                source_ids: vec![],
                evidence: OpeningEvidence::FiniteWindowAssembly,
                requires_review: true,
            });
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review);
        }
    }

    #[test]
    fn largest_proven_body_wins_over_contained_parallel_observations_and_old_ready_wins_all() {
        let (mut page, mut d) = fixture(0., 1., false);
        let mut small = page.walls[0].clone();
        small.id = "finite contained".into();
        small.start = Point::new(1088., 1000.);
        small.end = Point::new(1130., 1000.);
        small.body = BodyEvidence::ParallelFaces;
        small.source_ids = vec!["left continuous".into(), "right b".into()];
        page.walls.push(small);
        let mut child = d[0].clone();
        child.wall_id = "finite contained".into();
        d.push(child);
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Ready);
        assert_eq!(d[1].status, WallStatus::Review);
        let (mut page, mut d) = fixture(0., 1., false);
        let mut old = page.walls[0].clone();
        old.id = "old correct".into();
        old.start = Point::new(1030., 1000.);
        old.end = Point::new(1200., 1000.);
        page.walls.push(old);
        let mut prior = d[0].clone();
        prior.wall_id = "old correct".into();
        prior.status = WallStatus::Ready;
        d.push(prior);
        let before = d[1].clone();
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        assert_eq!(d[1], before);
    }

    #[test]
    fn alignment_source_identity_and_work_errors_never_publish_partial_decisions() {
        let (page, d) = fixture(0., 1., false);
        for budget in [0, 20, 100] {
            let mut decisions = d.clone();
            assert!(select_with_budget(&page, &mut decisions, budget).is_err());
            assert_eq!(decisions, d);
        }
        let mut decisions = d.clone();
        decisions[0].wall_id = "wrong".into();
        let before = decisions.clone();
        assert!(select(&page, &mut decisions).is_err());
        assert_eq!(decisions, before);
        let mut duplicate = page.clone();
        duplicate.primitives.push(duplicate.primitives[0].clone());
        let mut decisions = d.clone();
        assert!(select(&duplicate, &mut decisions).is_err());
        assert_eq!(decisions, d);
    }

    fn native_fixture() -> crate::model::Scan {
        use crate::model::{Edge, Node, Page, Scan};
        let (mut page, _) = fixture(0.37, 1., false);
        let source = Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            placement_paint_ids: page.placement_paint_ids.clone(),
            placement_polygon_paint_ids: page.placement_polygon_paint_ids.clone(),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: page.width,
            height: page.height,
            units_to_mm: None,
            primitives: page.primitives.clone(),
        };
        let w = &page.walls[0];
        let axis = (w.end - w.start).unit();
        let n = Point::new(-axis.y, axis.x);
        page.nodes = vec![
            Node {
                id: "observed a".into(),
                position: w.start - axis * 20. + n * 4.,
                wall_ids: vec![w.id.clone()],
                requires_review: true,
            },
            Node {
                id: "observed b".into(),
                position: w.end + axis * 30. - n * 3.,
                wall_ids: vec![w.id.clone()],
                requires_review: true,
            },
        ];
        page.edges = vec![Edge {
            start_node: 0,
            end_node: 1,
            wall_ids: vec![w.id.clone()],
            opening_ids: vec![],
        }];
        page.semantics = crate::semantics::interpret(
            &source,
            &page.walls,
            &page.rooms,
            &page.openings,
            &page.objects,
            &page.room_connections,
            None,
        );
        page.placement_decisions = Some(crate::placement::select(&page).unwrap());
        page.exterior_enclosures = Some(crate::placement::enclosures::build(&page));
        page.exterior_connections = Some(crate::placement::connections::build(&page).unwrap());
        Scan {
            visual_classification: None,
            schema_version: "openplantrace.rust.scan.v15".into(),
            engine_version: env!("CARGO_PKG_VERSION").into(),
            layer_profile: None,
            object_label_profile: None,
            pages: vec![page],
            warnings: vec![],
        }
    }

    #[test]
    fn native_exports_replay_only_finite_supported_axis_despite_observed_graph_drift() {
        use crate::export;
        let scan = native_fixture();
        export::validate(&scan).unwrap();
        let restored = serde_json::from_slice(&serde_json::to_vec(&scan).unwrap()).unwrap();
        export::validate(&restored).unwrap();
        let page = &scan.pages[0];
        let w = &page.walls[0];
        let extent = page.placement_decisions.as_ref().unwrap()[0]
            .placement_extent
            .as_ref()
            .unwrap();
        let axis = (extent.end - extent.start).unit();
        let length = extent.start.distance(extent.end);
        let placement = export::placement(&scan);
        let p = &placement["pages"][0];
        assert_eq!(
            serde_json::from_value::<Point>(p["walls"][0]["centerLine"]["start"].clone()).unwrap(),
            extent.start
        );
        assert_eq!(
            serde_json::from_value::<Point>(p["walls"][0]["observedCenterLine"]["start"].clone())
                .unwrap(),
            w.start
        );
        let structure = export::structure(&scan);
        assert_eq!(
            serde_json::from_value::<Point>(
                structure["pages"][0]["wallRuns"][0]["centerLine"]["start"].clone()
            )
            .unwrap(),
            w.start
        );
        assert_eq!(p["barriers"][0]["spans"], p["walls"][0]["spans"]);
        let nodes: HashMap<String, Point> = p["wallGraph"]["nodes"]
            .as_array()
            .unwrap()
            .iter()
            .map(|n| {
                (
                    n["id"].as_str().unwrap().to_owned(),
                    serde_json::from_value(n["position"].clone()).unwrap(),
                )
            })
            .collect();
        let mut covered = 0.;
        for edge in p["wallGraph"]["edges"].as_array().unwrap() {
            if !edge["wallIds"]
                .as_array()
                .unwrap()
                .iter()
                .any(|id| id == &w.id)
            {
                continue;
            }
            let a = nodes[edge["startNodeId"].as_str().unwrap()];
            let b = nodes[edge["endNodeId"].as_str().unwrap()];
            for q in [a, b] {
                let v = q - extent.start;
                assert!(v.cross(axis).abs() < 1e-8);
                assert!(v.dot(axis) >= -1e-8 && v.dot(axis) <= length + 1e-8);
            }
            covered += a.distance(b);
        }
        assert!((covered - length).abs() < 1e-8);
        let connections = page.exterior_connections.as_ref().unwrap();
        for node in &connections.nodes {
            let v = node.position - extent.start;
            assert!(v.cross(axis).abs() < 1e-8);
            assert!(v.dot(axis) >= -1e-8 && v.dot(axis) <= length + 1e-8);
        }
        assert!(connections.edges.iter().all(|e| !e.adds_wall_material));
    }

    #[test]
    fn native_replay_rejects_changed_extent_support_material_and_archive_downgrade() {
        let scan = native_fixture();
        crate::export::validate(&scan).unwrap();
        for change in 0..6 {
            let mut bad = scan.clone();
            let page = &mut bad.pages[0];
            match change {
                0 => page.placement_decisions.as_mut().unwrap()[0]
                    .support_source_ids
                    .pop()
                    .map(|_| ())
                    .unwrap(),
                1 => {
                    page.placement_decisions.as_mut().unwrap()[0]
                        .placement_extent
                        .as_mut()
                        .unwrap()
                        .start = page.walls[0].start
                }
                2 => page
                    .placement_paint_ids
                    .as_mut()
                    .unwrap()
                    .retain(|id| id != "upper paint a"),
                3 => page.primitives.retain(|p| p.id != "lower far"),
                4 => page.placement_decisions.as_mut().unwrap()[0].placement_extent = None,
                _ => bad.schema_version = "openplantrace.rust.scan.v14".into(),
            }
            assert!(crate::export::validate(&bad).is_err(), "change{change}");
        }
    }
}
fn structural(p: &Primitive) -> bool {
    match p.layer_category_override {
        Some(_) => p.layer_kind() == LayerKind::Wall,
        None => material::wall_name(p.layer_name()).1,
    }
}
struct Ink<'a> {
    id: &'a str,
    layer: &'a str,
    a: Point,
    b: Point,
}
struct Paint<'a> {
    id: &'a str,
    layer: &'a str,
    corners: [Point; 4],
}
struct Frame {
    start: Point,
    axis: Point,
    length: f64,
    half: f64,
    eps: f64,
}
impl Frame {
    fn local(&self, p: Point) -> Point {
        let v = p - self.start;
        Point::new(v.dot(self.axis), self.axis.cross(v))
    }
    fn bounds(&self, p: &Paint<'_>) -> Option<[f64; 4]> {
        let points = p.corners.map(|q| self.local(q));
        let lo = Point::new(
            points.iter().map(|p| p.x).fold(f64::INFINITY, f64::min),
            points.iter().map(|p| p.y).fold(f64::INFINITY, f64::min),
        );
        let hi = Point::new(
            points.iter().map(|p| p.x).fold(f64::NEG_INFINITY, f64::max),
            points.iter().map(|p| p.y).fold(f64::NEG_INFINITY, f64::max),
        );
        if (hi.x - lo.x).min(hi.y - lo.y) <= self.eps {
            return None;
        }
        for x in [lo.x, hi.x] {
            for y in [lo.y, hi.y] {
                if !points
                    .iter()
                    .any(|p| p.distance(Point::new(x, y)) <= self.eps)
                {
                    return None;
                }
            }
        }
        Some([lo.x, lo.y, hi.x, hi.y])
    }
    fn body(&self) -> [Point; 4] {
        let n = Point::new(-self.axis.y, self.axis.x) * self.half;
        [
            self.start + n,
            self.start + self.axis * self.length + n,
            self.start + self.axis * self.length - n,
            self.start - n,
        ]
    }
}

fn covers<'a>(
    parts: &mut [(f64, f64, &'a str)],
    lo: f64,
    hi: f64,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    charge(
        work,
        parts
            .len()
            .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
    )?;
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut at = lo;
    let mut ids = BTreeSet::new();
    for &(a, b, id) in parts.iter() {
        if b <= at + eps {
            continue;
        }
        if a > at + eps {
            return Ok(None);
        }
        at = b;
        ids.insert(id.to_owned());
        if at >= hi - eps {
            return Ok(Some(ids.into_iter().collect()));
        }
    }
    Ok(None)
}

fn polygon_hits(
    body: &[Point; 4],
    polygon: &[Point],
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    if polygon.len() < 3 {
        return Ok(false);
    }
    charge(
        work,
        polygon
            .len()
            .saturating_mul(polygon.len().saturating_add(6)),
    )?;
    if polygon.iter().any(|p| !p.finite()) {
        return Ok(true);
    }
    let axes = body
        .iter()
        .zip(body.iter().cycle().skip(1))
        .map(|(&a, &b)| b - a)
        .chain(
            polygon
                .iter()
                .zip(polygon.iter().cycle().skip(1))
                .map(|(&a, &b)| b - a),
        );
    for edge in axes {
        if edge.norm() <= eps {
            continue;
        }
        let axis = Point::new(-edge.y, edge.x).unit();
        let range = |pts: &[Point]| {
            pts.iter()
                .map(|p| (*p - body[0]).dot(axis))
                .fold((f64::INFINITY, f64::NEG_INFINITY), |(a, b), x| {
                    (a.min(x), b.max(x))
                })
        };
        let (a, b) = range(body);
        let (c, d) = range(polygon);
        if b.min(d) - a.max(c) <= eps {
            return Ok(false);
        }
    }
    Ok(true)
}
fn furniture_hits(
    page: &PageScan,
    body: &[Point; 4],
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    for item in page.withheld_walls.iter().flatten() {
        charge(work, 1)?;
        if let Some(poly) = &item.furniture_outline {
            if polygon_hits(body, poly, eps, work)? {
                return Ok(true);
            }
        }
    }
    for item in &page.objects {
        charge(work, 1)?;
        if let Some(poly) = item
            .footprint
            .as_ref()
            .map(|p| p.polygon.as_slice())
            .or_else(|| item.geometry.as_ref().map(|p| p.polygon.as_slice()))
        {
            if polygon_hits(body, poly, eps, work)? {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

pub(super) fn context(
    page: &PageScan,
    w: &Wall,
    by_id: &HashMap<&str, &Primitive>,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let eps = roundoff(w);
    for room in &page.rooms {
        charge(work, room.polygon.len().saturating_mul(3).saturating_add(1))?;
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
                        crate::model::on_segment(p, a, b).0.distance(p) <= 2. * w.thickness + eps
                    })
                })
        {
            return Ok(Some(vec![id.clone()]));
        }
    }
    for door in page.openings.iter().filter(|o| o.kind == OpeningKind::Door) {
        charge(work, door.source_ids.len().saturating_add(1))?;
        for swing in &door.swing_observations {
            charge(
                work,
                swing
                    .open_leaf_source_ids
                    .len()
                    .saturating_mul(door.source_ids.len().saturating_add(1))
                    .saturating_add(1),
            )?;
            let radius = swing.hinge.distance(swing.closed_tip);
            if swing.hinge.finite()
                && swing.closed_tip.finite()
                && radius > eps
                && door.source_ids.contains(&swing.arc_source_id)
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
                    <= 4. * radius
            {
                let mut ids = vec![swing.arc_source_id.clone()];
                ids.extend(swing.open_leaf_source_ids.iter().cloned());
                return Ok(Some(ids));
            }
        }
    }
    Ok(None)
}

/// A real return has two independent transverse faces. Certified paint must
/// fill its entire depth for a finite wall-like patch outside the main body.
/// The patch is evidence only, never new placement material.
fn return_band(
    frame: &Frame,
    layer: &str,
    side: f64,
    lo: f64,
    hi: f64,
    ink: &[Ink<'_>],
    paint: &[Paint<'_>],
    page: &PageScan,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let eps = frame.eps;
    let base = frame.half;
    let depth = hi - lo;
    if depth <= eps {
        return Ok(None);
    }
    let run = (2. * frame.half).max(2. * depth);
    let mut near = vec![];
    let mut far = vec![];
    for s in ink.iter().filter(|s| s.layer == layer) {
        charge(work, 1)?;
        let a = frame.local(s.a);
        let b = frame.local(s.b);
        if (a.x - b.x).abs() > eps {
            continue;
        }
        let (u, v) = (a.y * side, b.y * side);
        let (nlo, nhi) = (u.min(v), u.max(v));
        if (nlo - base).abs() > eps || nhi - base < run - eps {
            continue;
        }
        if (a.x - lo).abs() <= eps {
            near.push(s.id);
        }
        if (a.x - hi).abs() <= eps {
            far.push(s.id);
        }
    }
    for a in near {
        for &b in &far {
            charge(
                work,
                paint
                    .len()
                    .saturating_add(page.openings.len())
                    .saturating_add(1),
            )?;
            if a == b {
                continue;
            }
            let mut parts = vec![];
            for p in paint.iter().filter(|p| p.layer == layer) {
                let Some([x0, y0, x1, y1]) = frame.bounds(p) else {
                    continue;
                };
                let (n0, n1) = if side > 0. { (y0, y1) } else { (-y1, -y0) };
                if n0 <= base + eps && n1 >= base + run - eps && x1 > lo + eps && x0 < hi - eps {
                    parts.push((x0, x1, p.id));
                }
            }
            let Some(mut ids) = covers(&mut parts, lo, hi, eps, work)? else {
                continue;
            };
            let normal = Point::new(-frame.axis.y, frame.axis.x) * side;
            let start = frame.start + frame.axis * ((lo + hi) * 0.5) + normal * base;
            let return_wall = Wall {
                id: String::new(),
                page: page.page_number,
                start,
                end: start + normal * run,
                thickness: depth,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: vec![],
                requires_review: true,
            };
            // Move to the centre of the proved return band along the main axis.
            if page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(&return_wall, o))
            {
                continue;
            }
            let axis = (return_wall.end - return_wall.start).unit();
            let n = Point::new(-axis.y, axis.x) * (depth * 0.5);
            let body = [
                return_wall.start + n,
                return_wall.end + n,
                return_wall.end - n,
                return_wall.start - n,
            ];
            if furniture_hits(page, &body, eps, work)? {
                continue;
            }
            ids.extend([a.to_owned(), b.to_owned()]);
            return Ok(Some(ids));
        }
    }
    Ok(None)
}

fn terminal_return(
    frame: &Frame,
    layer: &str,
    side: f64,
    terminal: f64,
    before: bool,
    ink: &[Ink<'_>],
    paint: &[Paint<'_>],
    page: &PageScan,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    for s in ink.iter().filter(|s| s.layer == layer) {
        charge(work, 1)?;
        let a = frame.local(s.a);
        let b = frame.local(s.b);
        if (a.x - b.x).abs() > frame.eps {
            continue;
        }
        let depth = (a.x - terminal) * if before { -1. } else { 1. };
        if depth <= frame.eps || depth > 2. * frame.half + frame.eps {
            continue;
        }
        if let Some(proof) = return_band(
            frame,
            layer,
            side,
            terminal.min(a.x),
            terminal.max(a.x),
            ink,
            paint,
            page,
            work,
        )? {
            return Ok(Some(proof));
        }
    }
    Ok(None)
}

fn faced_union(
    frame: &Frame,
    layer: &str,
    side: f64,
    parts: &mut [(f64, f64, &str)],
    ink: &[Ink<'_>],
    paint: &[Paint<'_>],
    page: &PageScan,
    work: &mut usize,
) -> Result<Option<(Vec<String>, bool)>, String> {
    charge(
        work,
        parts
            .len()
            .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
    )?;
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut at = 0.;
    let mut ids = BTreeSet::new();
    let mut has_junction = false;
    for &(lo, hi, id) in parts.iter() {
        if hi <= at + frame.eps {
            continue;
        }
        if lo > at + frame.eps {
            // Only internal intervals between independently observed face arms
            // can be source junctions. Missing terminal ink is not a junction.
            if at <= frame.eps || lo >= frame.length - frame.eps {
                return Ok(None);
            }
            let Some(proof) = return_band(frame, layer, side, at, lo, ink, paint, page, work)?
            else {
                return Ok(None);
            };
            ids.extend(proof);
            has_junction = true;
        }
        at = hi;
        ids.insert(id.to_owned());
        if at >= frame.length - frame.eps {
            return Ok(Some((ids.into_iter().collect(), has_junction)));
        }
    }
    Ok(None)
}

fn body_proof(
    w: &Wall,
    p: &Paint<'_>,
    ink: &[Ink<'_>],
    paints: &[Paint<'_>],
    page: &PageScan,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let frame = Frame {
        start: w.start,
        axis: (w.end - w.start).unit(),
        length: w.start.distance(w.end),
        half: w.thickness * 0.5,
        eps: roundoff(w),
    };
    let eps = frame.eps;
    let Some([x0, y0, x1, y1]) = frame.bounds(p) else {
        return Ok(None);
    };
    if x0 > eps
        || x1 < frame.length - eps
        || (y0 + frame.half).abs() > eps
        || (y1 - frame.half).abs() > eps
    {
        return Ok(None);
    }
    let raw: HashSet<_> = w.source_ids.iter().map(String::as_str).collect();
    let mut faces = [vec![], vec![]];
    let mut exact = [false, false];
    for s in ink.iter().filter(|s| {
        s.layer == p.layer && (w.body == BodyEvidence::FilledBody || raw.contains(s.id))
    }) {
        charge(work, 1)?;
        let a = frame.local(s.a);
        let b = frame.local(s.b);
        if (a.y - b.y).abs() > eps || (a.y.abs() - frame.half).abs() > eps {
            continue;
        }
        let (lo, hi) = (a.x.min(b.x), a.x.max(b.x));
        let side = usize::from(a.y > 0.);
        if hi <= eps || lo >= frame.length - eps {
            continue;
        }
        faces[side].push((lo, hi, s.id));
        exact[side] |= lo.abs() <= eps && (hi - frame.length).abs() <= eps;
    }
    if !exact.iter().any(|v| *v) {
        return Ok(None);
    }
    let Some((a, junction_a)) =
        faced_union(&frame, p.layer, -1., &mut faces[0], ink, paints, page, work)?
    else {
        return Ok(None);
    };
    let Some((b, junction_b)) =
        faced_union(&frame, p.layer, 1., &mut faces[1], ink, paints, page, work)?
    else {
        return Ok(None);
    };
    let real_a: HashSet<_> = faces[0].iter().map(|p| p.2).collect();
    if faces[1].iter().any(|p| real_a.contains(p.2)) {
        return Ok(None);
    }
    if w.body == BodyEvidence::FilledBody && (junction_a || junction_b) {
        let mut ids = BTreeSet::from([p.id.to_owned()]);
        ids.extend(a);
        ids.extend(b);
        return Ok(Some(ids.into_iter().collect()));
    }
    for (side, is_exact) in [(-1., exact[0]), (1., exact[1])] {
        if !is_exact {
            continue;
        }
        let Some(c) = terminal_return(&frame, p.layer, side, 0., true, ink, paints, page, work)?
        else {
            continue;
        };
        let Some(d) = terminal_return(
            &frame,
            p.layer,
            side,
            frame.length,
            false,
            ink,
            paints,
            page,
            work,
        )?
        else {
            continue;
        };
        let mut ids = BTreeSet::from([p.id.to_owned()]);
        ids.extend(a.iter().chain(&b).cloned());
        ids.extend(c);
        ids.extend(d);
        return Ok(Some(ids.into_iter().collect()));
    }
    Ok(None)
}

fn candidates(
    w: &Wall,
    p: &Paint<'_>,
    ink: &[Ink<'_>],
    work: &mut usize,
) -> Result<Vec<Wall>, String> {
    let frame = Frame {
        start: w.start,
        axis: (w.end - w.start).unit(),
        length: w.start.distance(w.end),
        half: w.thickness * 0.5,
        eps: roundoff(w),
    };
    let Some([a, b, c, d]) = frame.bounds(p) else {
        return Ok(vec![]);
    };
    if w.body != BodyEvidence::FilledBody {
        return Ok(
            if a <= frame.eps
                && c >= frame.length - frame.eps
                && (b + frame.half).abs() <= frame.eps
                && (d - frame.half).abs() <= frame.eps
            {
                vec![w.clone()]
            } else {
                vec![]
            },
        );
    }
    if !w.source_ids.iter().any(|id| id == p.id) {
        return Ok(vec![]);
    }
    if a.abs() > frame.eps
        || (c - frame.length).abs() > frame.eps
        || (b + frame.half).abs() > frame.eps
        || (d - frame.half).abs() > frame.eps
    {
        return Ok(vec![]);
    }
    let mut spans = vec![(0., frame.length)];
    for side in [-1., 1.] {
        let mut parts = vec![];
        for s in ink.iter().filter(|s| s.layer == p.layer) {
            charge(work, 1)?;
            let a = frame.local(s.a);
            let b = frame.local(s.b);
            if (a.y - b.y).abs() > frame.eps || (a.y - side * frame.half).abs() > frame.eps {
                continue;
            }
            let (lo, hi) = (a.x.min(b.x).max(0.), a.x.max(b.x).min(frame.length));
            if hi - lo > frame.eps {
                parts.push((lo, hi));
            }
        }
        charge(
            work,
            parts
                .len()
                .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
        )?;
        parts.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
        let mut merged: Vec<(f64, f64)> = vec![];
        for (lo, hi) in parts {
            if let Some(last) = merged.last_mut()
                && lo <= last.1 + frame.eps
            {
                last.1 = last.1.max(hi);
            } else {
                merged.push((lo, hi));
            }
        }
        spans.extend(
            merged
                .into_iter()
                .filter(|&(lo, hi)| lo <= frame.eps || hi >= frame.length - frame.eps),
        );
    }
    spans.sort_by(|a, b| {
        (b.1 - b.0)
            .total_cmp(&(a.1 - a.0))
            .then(a.0.total_cmp(&b.0))
            .then(a.1.total_cmp(&b.1))
    });
    spans.dedup_by(|a, b| (a.0 - b.0).abs() <= frame.eps && (a.1 - b.1).abs() <= frame.eps);
    Ok(spans
        .into_iter()
        .filter(|&(lo, hi)| hi - lo > w.thickness)
        .map(|(lo, hi)| {
            let mut body = w.clone();
            body.start = w.start + frame.axis * lo;
            body.end = w.start + frame.axis * hi;
            body
        })
        .collect())
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) -> Result<(), String> {
    select_with_budget(page, decisions, MAX_WORK)
}
fn select_with_budget(
    page: &PageScan,
    decisions: &mut [WallDecision],
    mut work: usize,
) -> Result<(), String> {
    if decisions.len() != page.walls.len()
        || decisions
            .iter()
            .zip(&page.walls)
            .any(|(d, w)| d.wall_id != w.id)
    {
        return Err("painted junction decisions are not aligned with observations".into());
    }
    charge(
        &mut work,
        page.primitives.len().saturating_add(page.walls.len()),
    )?;
    let by_id: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    if by_id.len() != page.primitives.len() {
        return Err("painted junction evidence requires unique source IDs".into());
    }
    let mut claimed = HashSet::new();
    for item in page
        .withheld_walls
        .iter()
        .flatten()
        .filter(|w| w.furniture_outline.is_some())
    {
        charge(&mut work, item.wall.source_ids.len())?;
        claimed.extend(item.wall.source_ids.iter().map(String::as_str));
    }
    for item in page
        .objects
        .iter()
        .filter(|o| o.footprint.is_some() || o.geometry.is_some())
    {
        charge(&mut work, item.source_ids.len())?;
        claimed.extend(item.source_ids.iter().map(String::as_str));
    }
    let mut paints = vec![];
    for id in page.placement_paint_ids.iter().flatten() {
        charge(&mut work, 1)?;
        let Some(p) = by_id
            .get(id.as_str())
            .copied()
            .filter(|p| structural(p) && !claimed.contains(p.id.as_str()))
        else {
            continue;
        };
        let Some(corners) = super::painted_rectangle(&p.shape) else {
            continue;
        };
        paints.push(Paint {
            id: p.id.as_str(),
            layer: p.layer_name(),
            corners,
        });
        if paints.len() > MAX_PAINT {
            return Err("painted junction paint limit exceeded".into());
        }
    }
    if paints.is_empty() {
        return Ok(());
    }
    let mut ink = vec![];
    for p in page
        .primitives
        .iter()
        .filter(|p| structural(p) && !claimed.contains(p.id.as_str()))
    {
        let mut add = |a, b| -> Result<(), String> {
            charge(&mut work, 1)?;
            ink.push(Ink {
                id: p.id.as_str(),
                layer: p.layer_name(),
                a,
                b,
            });
            if ink.len() > MAX_INK {
                return Err("painted junction source stroke limit exceeded".into());
            }
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
        }
    }
    let mut proposals = vec![];
    for (i, w) in page.walls.iter().enumerate() {
        charge(&mut work, 1)?;
        if decisions[i].status != WallStatus::Review
            || decisions[i].placement_extent.is_some()
            || !matches!(
                w.body,
                BodyEvidence::ParallelFaces | BodyEvidence::FilledBody
            )
            || w.curve.is_some()
            || !w.start.finite()
            || !w.end.finite()
            || !w.thickness.is_finite()
            || w.thickness <= 0.
            || w.start.distance(w.end) <= w.thickness
        {
            continue;
        }
        let mut found = None;
        for p in &paints {
            charge(&mut work, 1)?;
            for body in candidates(w, p, &ink, &mut work)? {
                let frame = Frame {
                    start: body.start,
                    axis: (body.end - body.start).unit(),
                    length: body.start.distance(body.end),
                    half: body.thickness * 0.5,
                    eps: roundoff(&body),
                };
                charge(&mut work, page.openings.len())?;
                if page
                    .openings
                    .iter()
                    .any(|o| overlap::aperture_crosses(&body, o))
                    || furniture_hits(page, &frame.body(), frame.eps, &mut work)?
                {
                    continue;
                }
                if let Some(ids) = body_proof(&body, p, &ink, &paints, page, &mut work)? {
                    found = Some((body, ids));
                    break;
                }
            }
            if found.is_some() {
                break;
            }
        }
        let Some((body, mut ids)) = found else {
            continue;
        };
        let Some(plan) = context(page, &body, &by_id, &mut work)? else {
            continue;
        };
        ids.extend(plan);
        ids.sort();
        ids.dedup();
        proposals.push((i, body, ids));
        if proposals.len() > MAX_PROPOSALS {
            return Err("painted junction proposal limit exceeded".into());
        }
    }
    proposals.sort_by(|a, b| {
        let area = |p: &(usize, Wall, Vec<String>)| p.1.start.distance(p.1.end) * p.1.thickness;
        area(b).total_cmp(&area(a)).then(a.0.cmp(&b.0))
    });
    let mut ready: Vec<Wall> = decisions
        .iter()
        .enumerate()
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(i, d)| super::effective_wall(&page.walls[i], Some(d)).into_owned())
        .collect();
    let mut accepted = vec![];
    for (i, body, ids) in proposals {
        charge(&mut work, ready.len())?;
        if ready
            .iter()
            .any(|other| overlap::parallel_bodies_overlap(&body, other))
        {
            continue;
        }
        ready.push(body.clone());
        accepted.push((i, body, ids));
    }
    for (i, body, ids) in accepted {
        if body.start != page.walls[i].start || body.end != page.walls[i].end {
            decisions[i].placement_extent = Some(PlacementExtent {
                thickness: None,
                start: body.start,
                end: body.end,
                source_ids: ids.clone(),
            });
        }
        decisions[i].status = WallStatus::Ready;
        decisions[i].reason = WallReason::SourceBoundedPaintedJunctionSpan;
        decisions[i].support_source_ids = ids;
    }
    Ok(())
}
