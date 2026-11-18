//! Advisory source regions for existing objects and independently witnessed doors.
//! This module never changes the scan, opening hosts, learned scores or placement.
use super::source_review::Region;
use crate::model::{Opening, OpeningEvidence, OpeningKind, Point, Primitive, Scan, Shape};
use serde::Serialize;
use std::collections::{BTreeMap, BTreeSet, HashMap};

pub const MAX_PROPOSALS: usize = 200;
const MAX_WORK: usize = 8_000_000;

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProposalSet {
    pub proposals: Vec<Proposal>,
    pub counts: ProposalCounts,
    pub limit: usize,
    pub truncated: bool,
}
impl ProposalSet {
    pub fn regions(&self) -> Vec<Region> {
        self.proposals.iter().map(|p| p.region.clone()).collect()
    }
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProposalCounts {
    pub existing_object_regions: usize,
    /// Valid physical opening records, including records sharing exact arc evidence.
    pub validated_hinged_openings: usize,
    pub duplicate_opening_evidence: usize,
    pub rejected_door_openings: usize,
    pub total_before_limit: usize,
    pub emitted: usize,
    pub omitted: usize,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Proposal {
    pub region: Region,
    pub evidence: ProposalEvidence,
}

#[derive(Clone, Debug, Serialize)]
#[serde(tag = "kind", rename_all_fields = "camelCase")]
pub enum ProposalEvidence {
    ExistingObject {
        object_id: String,
    },
    HingedOpening {
        opening_ids: Vec<String>,
        arc_source_ids: Vec<String>,
        leaf_source_ids: Vec<String>,
        /// Host references are provenance, never crop-bounds witnesses.
        host_wall_ids: Vec<String>,
    },
}

fn charge(work: &mut usize, amount: usize) -> Result<(), String> {
    *work = work
        .checked_sub(amount)
        .ok_or("source proposal work limit exceeded")?;
    Ok(())
}

#[derive(Clone, Copy)]
struct Bounds {
    lo: Point,
    hi: Point,
}
impl Bounds {
    fn point(p: Point) -> Self {
        Self { lo: p, hi: p }
    }
    fn add(&mut self, p: Point) -> bool {
        if !p.finite() {
            return false;
        }
        self.lo.x = self.lo.x.min(p.x);
        self.lo.y = self.lo.y.min(p.y);
        self.hi.x = self.hi.x.max(p.x);
        self.hi.y = self.hi.y.max(p.y);
        true
    }
    fn normalized(self, width: f64, height: f64) -> Option<[f64; 4]> {
        if !self.lo.finite() || !self.hi.finite() {
            return None;
        }
        let b = [
            (self.lo.x / width).clamp(0., 1.),
            (self.lo.y / height).clamp(0., 1.),
            (self.hi.x / width).clamp(0., 1.),
            (self.hi.y / height).clamp(0., 1.),
        ];
        (b[0] < b[2] && b[1] < b[3]).then_some(b)
    }
}

fn shape_work(shape: &Shape) -> usize {
    match shape {
        Shape::Polyline { points, .. } => points.len().saturating_add(1),
        _ => 1,
    }
}

/// Add only segments supporting this finite radial. A source polyline may also
/// contain a much longer wall; those unrelated segments must not enlarge a crop.
fn add_leaf_segments(bounds: &mut Bounds, shape: &Shape, a: Point, b: Point, eps: f64) {
    let d = (b - a).unit();
    let length = a.distance(b);
    let mut add = |p: Point, q: Point| {
        if !p.finite() || !q.finite() || p.distance(q) <= 1e-6 {
            return;
        }
        let along = ((p - a).dot(d), (q - a).dot(d));
        if (q - p).unit().dot(d).abs() >= 0.995
            && (p - a).cross(d).abs().max((q - a).cross(d).abs()) <= eps
            && along.0.min(along.1) >= -eps
            && along.0.max(along.1) <= length + eps
        {
            bounds.add(p);
            bounds.add(q);
        }
    };
    match shape {
        Shape::Line { start, end } => add(*start, *end),
        Shape::Polyline {
            points,
            closed,
            filled: false,
        } => {
            for pair in points.windows(2) {
                add(pair[0], pair[1]);
            }
            if *closed && points.len() > 2 && points.first() != points.last() {
                add(points[points.len() - 1], points[0]);
            }
        }
        _ => {}
    }
}

fn door_bounds(
    opening: &Opening,
    sources: &HashMap<&str, &Primitive>,
    hosts: &BTreeSet<&str>,
    work: &mut usize,
) -> Result<Option<(Bounds, Vec<String>, Vec<String>)>, String> {
    if opening.id.trim().is_empty()
        || !matches!(
            opening.evidence,
            OpeningEvidence::FiniteJambSwing | OpeningEvidence::WallGapSwing
        )
        || opening.operation != "Hinged"
        || !opening.start.finite()
        || !opening.end.finite()
        || opening.host_wall_ids.is_empty()
        || !opening.host_wall_ids.contains(&opening.host_wall_id)
        || opening
            .host_wall_ids
            .iter()
            .any(|id| !hosts.contains(id.as_str()))
        || opening.swing_observations.is_empty()
    {
        return Ok(None);
    }
    charge(
        work,
        opening.source_ids.len() + opening.swing_observations.len(),
    )?;
    for swing in &opening.swing_observations {
        if !swing.hinge.finite()
            || !swing.closed_tip.finite()
            || !swing.open_tip.finite()
            || (swing.open_leaf_source_ids.is_empty() && swing.closed_leaf_source_ids.is_empty())
        {
            return Ok(None);
        }
        let Some(Primitive {
            shape:
                Shape::Arc {
                    center,
                    radius,
                    start_angle,
                    sweep,
                },
            ..
        }) = sources.get(swing.arc_source_id.as_str()).copied()
        else {
            return Ok(None);
        };
        if !center.finite()
            || !radius.is_finite()
            || *radius <= 0.
            || !start_angle.is_finite()
            || !sweep.is_finite()
            || !(45.0..=140.0).contains(&sweep.abs().to_degrees())
        {
            return Ok(None);
        }
        charge(work, 2)?;
        // The same compound path can witness several hands or both radials.
        // Charge each actual occurrence before the validator and ROI passes,
        // rather than just each distinct source ID in the opening.
        for id in swing
            .open_leaf_source_ids
            .iter()
            .chain(&swing.closed_leaf_source_ids)
        {
            let Some(p) = sources.get(id.as_str()).copied() else {
                return Ok(None);
            };
            charge(work, shape_work(&p.shape).saturating_mul(4))?;
        }
    }
    if !crate::openings::validate_swing_sources(opening, sources) {
        return Ok(None);
    }
    let mut bounds = Bounds::point(opening.swing_observations[0].hinge);
    let mut arcs = BTreeSet::new();
    let mut leaves = BTreeSet::new();
    for swing in &opening.swing_observations {
        let p = sources[swing.arc_source_id.as_str()];
        let Some(b) = crate::semantics::bounds(&p.shape) else {
            return Ok(None);
        };
        if !bounds.add(b.min)
            || !bounds.add(b.max)
            || !bounds.add(swing.hinge)
            || !bounds.add(swing.closed_tip)
            || !bounds.add(swing.open_tip)
        {
            return Ok(None);
        }
        arcs.insert(swing.arc_source_id.clone());
        for (tip, ids) in [
            (swing.open_tip, &swing.open_leaf_source_ids),
            (swing.closed_tip, &swing.closed_leaf_source_ids),
        ] {
            for id in ids {
                add_leaf_segments(
                    &mut bounds,
                    &sources[id.as_str()].shape,
                    swing.hinge,
                    tip,
                    swing.leaf_tolerance,
                );
                leaves.insert(id.clone());
            }
        }
    }
    Ok(Some((
        bounds,
        arcs.into_iter().collect(),
        leaves.into_iter().collect(),
    )))
}

fn merge_ids(destination: &mut Vec<String>, source: impl IntoIterator<Item = String>) {
    destination.extend(source);
    destination.sort();
    destination.dedup();
}

/// Preserve existing object order, then add finite witnessed hinged openings.
/// Count all bounded candidates but retain at most 200 emitted region records.
/// Input is expected to be a validated native scan from the pipeline. This bridge
/// rechecks finite swing/leaf witnesses and resolved hosts; it does not run a new
/// jamb detector or independently establish a physical opening.
pub fn propose(scan: &Scan, limit: usize) -> Result<ProposalSet, String> {
    let limit = limit.min(MAX_PROPOSALS);
    let mut result = ProposalSet {
        proposals: vec![],
        counts: ProposalCounts::default(),
        limit,
        truncated: false,
    };
    let mut work = MAX_WORK;
    for page in &scan.pages {
        if page.page_number == 0
            || !page.width.is_finite()
            || !page.height.is_finite()
            || page.width <= 0.
            || page.height <= 0.
        {
            return Err("source proposals require positive finite page dimensions".into());
        }
        charge(&mut work, page.primitives.len() + page.objects.len())?;
        for member in crate::object_labels::source_members(page)? {
            if member.category == "TextLabel" {
                continue;
            }
            let Some(target) = (Bounds {
                lo: member.bounds.min,
                hi: member.bounds.max,
            })
            .normalized(page.width, page.height) else {
                continue;
            };
            result.counts.existing_object_regions += 1;
            result.counts.total_before_limit += 1;
            if result.proposals.len() < limit {
                result.proposals.push(Proposal {
                    region: Region {
                        id: member.object_id.clone(),
                        page_number: page.page_number,
                        target,
                        context: None,
                        object_ids: vec![member.object_id.clone()],
                        source_ids: member.source_ids,
                    },
                    evidence: ProposalEvidence::ExistingObject {
                        object_id: member.object_id,
                    },
                });
            }
        }
    }
    for page in &scan.pages {
        charge(
            &mut work,
            page.primitives.len() + page.openings.len() + page.walls.len(),
        )?;
        if !page.openings.iter().any(|o| o.kind == OpeningKind::Door) {
            continue;
        }
        let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
        if sources.len() != page.primitives.len() {
            return Err("source proposals require unique primitive IDs".into());
        }
        let hosts: BTreeSet<_> = page.walls.iter().map(|w| w.id.as_str()).collect();
        let mut seen = BTreeMap::<Vec<String>, Option<usize>>::new();
        for opening in &page.openings {
            if opening.kind != OpeningKind::Door {
                continue;
            }
            if opening.page != page.page_number {
                result.counts.rejected_door_openings += 1;
                continue;
            }
            let Some((bounds, arcs, leaves)) = door_bounds(opening, &sources, &hosts, &mut work)?
            else {
                result.counts.rejected_door_openings += 1;
                continue;
            };
            let Some(target) = bounds.normalized(page.width, page.height) else {
                result.counts.rejected_door_openings += 1;
                continue;
            };
            result.counts.validated_hinged_openings += 1;
            let mut source_ids = arcs.clone();
            merge_ids(&mut source_ids, leaves.clone());
            if let Some(index) = seen.get(&arcs).copied() {
                result.counts.duplicate_opening_evidence += 1;
                if let Some(index) = index {
                    let old = &mut result.proposals[index];
                    old.region.target = [
                        old.region.target[0].min(target[0]),
                        old.region.target[1].min(target[1]),
                        old.region.target[2].max(target[2]),
                        old.region.target[3].max(target[3]),
                    ];
                    merge_ids(&mut old.region.source_ids, source_ids);
                    if let ProposalEvidence::HingedOpening {
                        opening_ids,
                        leaf_source_ids,
                        host_wall_ids,
                        ..
                    } = &mut old.evidence
                    {
                        merge_ids(opening_ids, [opening.id.clone()]);
                        merge_ids(leaf_source_ids, leaves);
                        merge_ids(host_wall_ids, opening.host_wall_ids.clone());
                    }
                }
                continue;
            }
            result.counts.total_before_limit += 1;
            let emitted = (result.proposals.len() < limit).then_some(result.proposals.len());
            seen.insert(arcs.clone(), emitted);
            if emitted.is_some() {
                let mut host_wall_ids = opening.host_wall_ids.clone();
                host_wall_ids.sort();
                host_wall_ids.dedup();
                result.proposals.push(Proposal {
                    region: Region {
                        id: format!("source-opening:{}", opening.id),
                        page_number: page.page_number,
                        target,
                        context: None,
                        object_ids: vec![],
                        source_ids,
                    },
                    evidence: ProposalEvidence::HingedOpening {
                        opening_ids: vec![opening.id.clone()],
                        arc_source_ids: arcs,
                        leaf_source_ids: leaves,
                        host_wall_ids,
                    },
                });
            }
        }
    }
    result.counts.emitted = result.proposals.len();
    result.counts.omitted = result.counts.total_before_limit - result.counts.emitted;
    result.truncated = result.counts.omitted > 0;
    super::source_review::validate_regions(&result.regions())?;
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::*;
    use std::f64::consts::{FRAC_PI_2, PI};

    fn primitive(id: &str, shape: Shape) -> Primitive {
        Primitive {
            id: id.into(),
            layer: None,
            stroke_width: Some(0.2),
            layer_category_override: None,
            shape,
        }
    }
    fn empty_scan() -> Scan {
        crate::pipeline::scan_document(
            Document {
                warnings: vec![],
                pages: vec![Page {
                    finish_history: None,
                    number: 1,
                    width: 1000.,
                    height: 1000.,
                    units_to_mm: None,
                    primitives: vec![],
                    material_tiles: vec![],
                    placement_paint_ids: None,
                    placement_polygon_paint_ids: None,
                    pdf_input: None,
                }],
            },
            &Options::default(),
            None,
            true,
        )
        .unwrap()
    }
    fn add_door(scan: &mut Scan, serial: usize, hinge: Point, angle: f64, sweep: f64) {
        let page = &mut scan.pages[0];
        let radius = 40.;
        let closed = hinge + Point::new(angle.cos(), angle.sin()) * radius;
        let open = hinge + Point::new((angle + sweep).cos(), (angle + sweep).sin()) * radius;
        let arc = format!("arc{serial}");
        let leaf = format!("leaf{serial}");
        let host = format!("wall{serial}");
        let host_source = format!("host{serial}");
        page.primitives.extend([
            primitive(
                &arc,
                Shape::Arc {
                    center: hinge,
                    radius,
                    start_angle: angle,
                    sweep,
                },
            ),
            primitive(
                &leaf,
                Shape::Line {
                    start: hinge,
                    end: open,
                },
            ),
            primitive(
                &host_source,
                Shape::Line {
                    start: hinge - (closed - hinge) * 5.,
                    end: hinge + (closed - hinge) * 10.,
                },
            ),
        ]);
        page.walls.push(Wall {
            id: host.clone(),
            page: 1,
            start: hinge - (closed - hinge) * 5.,
            end: hinge + (closed - hinge) * 10.,
            thickness: 10.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![host_source.clone()],
            requires_review: true,
        });
        page.openings.push(Opening {
            physical_jambs: None,
            id: format!("door{serial}"),
            page: 1,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: host.clone(),
            host_wall_ids: vec![host],
            start: hinge,
            end: closed,
            hinge: Some(hinge),
            swing_observations: vec![SwingObservation {
                arc_source_id: arc.clone(),
                hinge,
                closed_tip: closed,
                open_tip: open,
                leaf_tolerance: 0.5,
                open_leaf_source_ids: vec![leaf.clone()],
                closed_leaf_source_ids: vec![],
            }],
            source_ids: vec![arc, leaf, host_source],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
        });
    }
    #[test]
    fn swept_cardinal_extrema_survive_rotations_and_negative_sweeps() {
        for (angle, sweep) in [(PI / 4., FRAC_PI_2), (3. * PI / 4., -FRAC_PI_2)] {
            let mut scan = empty_scan();
            add_door(&mut scan, 1, Point::new(200., 200.), angle, sweep);
            let result = propose(&scan, 200).unwrap();
            assert_eq!(result.proposals.len(), 1);
            let target = result.proposals[0].region.target;
            assert!((target[3] - 0.24).abs() < 1e-12);
            assert!((target[0] - (200. - 40. / 2_f64.sqrt()) / 1000.).abs() < 1e-12);
            assert!((target[2] - (200. + 40. / 2_f64.sqrt()) / 1000.).abs() < 1e-12);
        }
    }
    #[test]
    fn one_physical_opening_unions_all_observed_hands_without_host_bounds() {
        let mut scan = empty_scan();
        add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
        add_door(&mut scan, 2, Point::new(240., 200.), PI, FRAC_PI_2);
        let other = scan.pages[0].openings.pop().unwrap();
        let opening = &mut scan.pages[0].openings[0];
        opening.swing_observations.extend(other.swing_observations);
        opening.source_ids.extend(other.source_ids);
        let result = propose(&scan, 200).unwrap();
        assert_eq!(result.proposals.len(), 1);
        assert_eq!(result.proposals[0].region.target, [0.2, 0.16, 0.24, 0.24]);
        assert_eq!(
            result.proposals[0].region.source_ids,
            ["arc1", "arc2", "leaf1", "leaf2"]
        );
        assert!(result.proposals[0].region.object_ids.is_empty());
    }
    #[test]
    fn host_length_and_unrelated_segments_of_a_leaf_source_do_not_enlarge_target() {
        let mut scan = empty_scan();
        add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
        let first = propose(&scan, 200).unwrap().proposals[0].region.target;
        let p = scan.pages[0]
            .primitives
            .iter_mut()
            .find(|p| p.id == "leaf1")
            .unwrap();
        p.shape = Shape::Polyline {
            points: vec![
                Point::new(200., 200.),
                Point::new(200., 240.),
                Point::new(900., 900.),
            ],
            closed: false,
            filled: false,
        };
        scan.pages[0].walls[0].end = Point::new(1e8, 200.);
        let second = propose(&scan, 200).unwrap();
        assert_eq!(second.proposals[0].region.target, first);
        assert!(
            !second.proposals[0]
                .region
                .source_ids
                .contains(&"host1".into())
        );
    }
    #[test]
    fn missing_forged_and_arc_only_leaf_evidence_is_rejected() {
        for failure in 0..6 {
            let mut scan = empty_scan();
            add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
            match failure {
                0 => scan.pages[0].primitives.retain(|p| p.id != "leaf1"),
                1 => {
                    scan.pages[0].openings[0].swing_observations[0].open_leaf_source_ids =
                        vec!["host1".into()]
                }
                2 => scan.pages[0].openings[0].swing_observations[0]
                    .open_leaf_source_ids
                    .clear(),
                3 => scan.pages[0].openings[0].swing_observations[0].open_tip.x += 2.,
                4 => scan.pages[0].openings[0].page = 2,
                _ => scan.pages[0].walls.clear(),
            }
            let result = propose(&scan, 200).unwrap();
            assert!(result.proposals.is_empty());
            assert_eq!(result.counts.rejected_door_openings, 1);
        }
    }
    #[test]
    fn exact_arc_duplicates_merge_refs_but_adjacent_doors_remain_distinct() {
        let mut scan = empty_scan();
        add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
        let mut duplicate = scan.pages[0].openings[0].clone();
        duplicate.id = "duplicate-observation".into();
        scan.pages[0].openings.push(duplicate);
        add_door(&mut scan, 2, Point::new(242., 200.), 0., FRAC_PI_2);
        let result = propose(&scan, 1).unwrap();
        assert_eq!(result.counts.validated_hinged_openings, 3);
        assert_eq!(result.counts.duplicate_opening_evidence, 1);
        assert_eq!(result.counts.total_before_limit, 2);
        assert_eq!(result.counts.emitted, 1);
        assert_eq!(result.counts.omitted, 1);
        assert!(result.truncated);
        let ProposalEvidence::HingedOpening { opening_ids, .. } = &result.proposals[0].evidence
        else {
            panic!()
        };
        assert_eq!(opening_ids, &["door1", "duplicate-observation"]);
    }
    #[test]
    fn bare_furniture_arcs_never_create_opening_proposals_and_scan_is_unchanged() {
        let mut scan = empty_scan();
        scan.pages[0].primitives.push(primitive(
            "bare-arc",
            Shape::Arc {
                center: Point::new(200., 200.),
                radius: 40.,
                start_angle: 0.,
                sweep: FRAC_PI_2,
            },
        ));
        let before = serde_json::to_vec(&scan).unwrap();
        assert!(propose(&scan, 200).unwrap().proposals.is_empty());
        assert_eq!(before, serde_json::to_vec(&scan).unwrap());
    }
    #[test]
    fn clipping_zero_limit_and_nonfinite_inputs_are_explicit() {
        let mut scan = empty_scan();
        add_door(&mut scan, 1, Point::new(20., 20.), PI, FRAC_PI_2);
        assert_eq!(
            propose(&scan, 200).unwrap().proposals[0].region.target[0],
            0.
        );
        let empty = propose(&scan, 0).unwrap();
        assert_eq!(empty.counts.total_before_limit, 1);
        assert!(empty.truncated && empty.proposals.is_empty());
        scan.pages[0].openings[0].swing_observations[0].open_tip.x = f64::NAN;
        assert_eq!(
            propose(&scan, 200).unwrap().counts.rejected_door_openings,
            1
        );
        scan.pages[0].width = f64::NAN;
        assert!(propose(&scan, 200).is_err());
    }

    #[test]
    fn existing_object_regions_retain_their_order_and_provenance() {
        let mut scan = empty_scan();
        let mut p = primitive(
            "cabinet-source",
            Shape::Polyline {
                points: vec![
                    Point::new(10., 10.),
                    Point::new(30., 10.),
                    Point::new(30., 25.),
                    Point::new(10., 25.),
                ],
                closed: true,
                filled: false,
            },
        );
        p.layer = Some("FURN".into());
        scan.pages[0].primitives.push(p);
        scan.pages[0].objects.push(Object {
            id: "existing-object".into(),
            at: Point::new(20., 17.5),
            name: "Cabinet".into(),
            category: "Furniture".into(),
            room_id: None,
            source_ids: vec!["cabinet-source".into()],
            requires_review: true,
            geometry: None,
            profile_signature: None,
            footprint: None,
        });
        add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
        let old = super::super::source_review::scan_regions(&scan, 200).unwrap();
        let before = serde_json::to_vec(&scan).unwrap();
        let result = propose(&scan, 200).unwrap();
        assert_eq!(result.counts.existing_object_regions, 1);
        assert_eq!(result.counts.total_before_limit, 2);
        assert_eq!(
            serde_json::to_value(&result.proposals[0].region).unwrap(),
            serde_json::to_value(&old[0]).unwrap()
        );
        assert!(matches!(
            result.proposals[0].evidence,
            ProposalEvidence::ExistingObject { .. }
        ));
        assert!(matches!(
            result.proposals[1].evidence,
            ProposalEvidence::HingedOpening { .. }
        ));
        assert_eq!(before, serde_json::to_vec(&scan).unwrap());
    }

    #[test]
    fn hard_cap_retains_two_hundred_records_with_exact_omitted_count() {
        let mut scan = empty_scan();
        for serial in 0..201 {
            add_door(
                &mut scan,
                serial,
                Point::new(
                    30. + (serial % 20) as f64 * 45.,
                    30. + (serial / 20) as f64 * 50.,
                ),
                0.,
                FRAC_PI_2,
            );
        }
        let result = propose(&scan, 500).unwrap();
        assert_eq!(result.limit, 200);
        assert_eq!(result.counts.total_before_limit, 201);
        assert_eq!(result.counts.emitted, 200);
        assert_eq!(result.counts.omitted, 1);
        assert!(result.truncated);
    }

    #[test]
    fn repeated_compound_witness_is_charged_for_every_hand() {
        let mut scan = empty_scan();
        add_door(&mut scan, 1, Point::new(200., 200.), 0., FRAC_PI_2);
        let page = &mut scan.pages[0];
        let leaf = page
            .primitives
            .iter_mut()
            .find(|p| p.id == "leaf1")
            .unwrap();
        let mut points = vec![Point::new(200., 200.), Point::new(200., 240.)];
        points.extend(std::iter::repeat_n(Point::new(900., 900.), 1000));
        leaf.shape = Shape::Polyline {
            points,
            closed: false,
            filled: false,
        };
        let arc = page
            .primitives
            .iter()
            .find(|p| p.id == "arc1")
            .unwrap()
            .clone();
        for i in 0..2 {
            let mut copy = arc.clone();
            copy.id = format!("arc-copy-{i}");
            let mut swing = page.openings[0].swing_observations[0].clone();
            swing.arc_source_id = copy.id.clone();
            page.openings[0].source_ids.push(copy.id.clone());
            page.openings[0].swing_observations.push(swing);
            page.primitives.push(copy);
        }
        let sources = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
        let hosts = page.walls.iter().map(|w| w.id.as_str()).collect();
        let error = door_bounds(&page.openings[0], &sources, &hosts, &mut 9000)
            .err()
            .unwrap();
        assert!(error.contains("work limit"));
    }
}
