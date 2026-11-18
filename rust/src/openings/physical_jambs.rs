//! Resolve an inset leaf-sized cut against paired physical wall-face terminals.
//! The authored swing is immutable; only a uniquely supported frame can widen a cut.
use crate::model::{
    LayerKind, Opening, OpeningEvidence, OpeningKind, Point, Primitive, Shape, Wall, on_segment,
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashMap};

const MAX_WORK: usize = 32_000_000;
const MAX_STROKES: usize = 200_000;
const MAX_PROPOSALS: usize = 4096;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PhysicalJambEvidence {
    pub revision: u32,
    pub host_wall_id: String,
    pub original_start: Point,
    pub original_end: Point,
    pub source_layer: String,
    pub rail_source_ids: Vec<String>,
    pub cap_source_ids: Vec<String>,
}

#[derive(Clone, Debug, PartialEq)]
pub struct PhysicalJambCorrection {
    pub opening_id: String,
    pub start: Point,
    pub end: Point,
    pub evidence: PhysicalJambEvidence,
}

pub(crate) fn structural(p: &Primitive) -> bool {
    if p.layer_category_override.is_some() {
        return p.layer_kind() == LayerKind::Wall;
    }
    crate::layers::is_named_wall(p.layer_name())
        && p.layer_name()
            .split(|c: char| !c.is_alphanumeric())
            .all(|token| {
                matches!(
                    crate::model::layer_kind(Some(token)),
                    LayerKind::Wall | LayerKind::Unknown
                )
            })
}

fn charge(work: &mut usize, amount: usize) -> Result<(), String> {
    *work = work
        .checked_sub(amount)
        .ok_or("physical jamb evidence work budget exceeded")?;
    Ok(())
}

fn charge_swing_sources(
    opening: &Opening,
    sources: &HashMap<&str, &Primitive>,
    work: &mut usize,
) -> Result<(), String> {
    let n = opening.source_ids.len();
    charge(work, n.saturating_mul(n.max(1).ilog2() as usize + 1))?;
    for swing in &opening.swing_observations {
        for id in swing
            .open_leaf_source_ids
            .iter()
            .chain(&swing.closed_leaf_source_ids)
        {
            charge(work, n.max(1).ilog2() as usize + 1)?;
            let steps = match sources.get(id.as_str()).map(|p| &p.shape) {
                Some(Shape::Polyline { points, .. }) => points.len().saturating_add(1),
                _ => 1,
            };
            // Leaf validation walks every referenced finite source stroke.
            // A repeated large path must consume work on every replay, even
            // when its original layer kept it out of the wall-stroke index.
            charge(
                work,
                steps.saturating_mul((steps.max(1).ilog2() as usize + 3).saturating_mul(2)),
            )?;
        }
    }
    Ok(())
}

#[derive(Clone, Copy)]
struct Stroke<'a> {
    a: Point,
    b: Point,
    id: &'a str,
}

#[derive(Clone, Copy)]
struct Rail<'a> {
    lo: f64,
    hi: f64,
    side: usize,
    stroke: Stroke<'a>,
}

fn roundoff(w: &Wall) -> f64 {
    [w.start.x, w.start.y, w.end.x, w.end.y, w.thickness]
        .into_iter()
        .map(f64::abs)
        .fold(1., f64::max)
        * 128.
        * f64::EPSILON
}

fn cover(parts: &mut [(f64, f64, &str)], length: f64, eps: f64) -> Option<Vec<String>> {
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

fn cap(
    strokes: &[Stroke<'_>],
    w: &Wall,
    axis: Point,
    at: f64,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let mut pieces = vec![];
    let half = w.thickness * 0.5;
    for s in strokes {
        charge(work, 1)?;
        let a = s.a - w.start;
        let b = s.b - w.start;
        let (x, y) = (a.dot(axis), b.dot(axis));
        let (u, v) = (axis.cross(a), axis.cross(b));
        if (x - at).abs() <= eps
            && (y - at).abs() <= eps
            && u.min(v) >= -half - eps
            && u.max(v) <= half + eps
            && (u - v).abs() > eps
        {
            pieces.push((
                (u.min(v) + half).max(0.),
                (u.max(v) + half).min(w.thickness),
                s.id,
            ));
        }
    }
    charge(
        work,
        pieces
            .len()
            .saturating_mul(pieces.len().max(1).ilog2() as usize + 1),
    )?;
    Ok(cover(&mut pieces, w.thickness, eps))
}

fn intersects_gap(w: &Wall, axis: Point, lo: f64, hi: f64, a: Point, b: Point, eps: f64) -> bool {
    let a = a - w.start;
    let b = b - w.start;
    let mut enter = 0_f64;
    let mut exit = 1_f64;
    for (start, end, min, max) in [
        (a.dot(axis), b.dot(axis), lo + eps, hi - eps),
        (
            axis.cross(a),
            axis.cross(b),
            -w.thickness * 0.5 + eps,
            w.thickness * 0.5 - eps,
        ),
    ] {
        if (start < min && end < min) || (start > max && end > max) {
            return false;
        }
        if start != end {
            let p = (min - start) / (end - start);
            let q = (max - start) / (end - start);
            enter = enter.max(p.min(q));
            exit = exit.min(p.max(q));
        }
    }
    enter < exit
}

/// Derive proposals from original, leaf-sized opening observations. The caller
/// applies them only after frozen semantic/topology stages and saves the proof.
pub fn proposals(
    primitives: &[Primitive],
    walls: &[Wall],
    openings: &[Opening],
) -> Result<Vec<PhysicalJambCorrection>, String> {
    let mut work = MAX_WORK;
    charge(&mut work, primitives.len().saturating_add(walls.len()))?;
    let sources: HashMap<_, _> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let hosts: HashMap<_, _> = walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let mut layers = BTreeMap::<&str, Vec<Stroke<'_>>>::new();
    let mut by_source = HashMap::<&str, Vec<Stroke<'_>>>::new();
    let mut count = 0;
    for p in primitives {
        if !structural(p) {
            continue;
        }
        let Some(layer) = p.layer.as_deref() else {
            continue;
        };
        let mut add = |a, b| -> Result<(), String> {
            charge(&mut work, 1)?;
            count += 1;
            if count > MAX_STROKES {
                return Err("physical jamb source stroke limit exceeded".into());
            }
            let stroke = Stroke { a, b, id: &p.id };
            layers.entry(layer).or_default().push(stroke);
            by_source.entry(&p.id).or_default().push(stroke);
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
    let mut out = vec![];
    for o in openings {
        charge(&mut work, 1)?;
        if o.kind != OpeningKind::Door
            || o.evidence != OpeningEvidence::FiniteJambSwing
            || o.operation != "Hinged"
            || o.host_wall_ids.len() != 1
            || o.swing_observations.len() != 1
        {
            continue;
        }
        let Some(w) = hosts.get(o.host_wall_id.as_str()).copied() else {
            continue;
        };
        if w.curve.is_some()
            || !w.thickness.is_finite()
            || w.thickness <= 0.
            || !w.start.finite()
            || !w.end.finite()
        {
            continue;
        }
        let axis = (w.end - w.start).unit();
        let length = w.start.distance(w.end);
        let eps = roundoff(w);
        if length <= w.thickness * 2. {
            continue;
        }
        let swing = &o.swing_observations[0];
        charge_swing_sources(o, &sources, &mut work)?;
        // Bind the saved original cut to the original detector's finite projection.
        // Already extended/multi-hand cuts are deliberately outside this rule.
        if o.start.distance(on_segment(swing.hinge, w.start, w.end).0) > eps
            || o.end
                .distance(on_segment(swing.closed_tip, w.start, w.end).0)
                > eps
            || !super::validate_swing_sources(o, &sources)
        {
            continue;
        }
        let x = (o.start - w.start).dot(axis);
        let y = (o.end - w.start).dot(axis);
        let (old_lo, old_hi) = (x.min(y), x.max(y));
        let mut by_layer = BTreeMap::<&str, Vec<Rail<'_>>>::new();
        for id in &w.source_ids {
            charge(&mut work, 1)?;
            let Some(p) = sources.get(id.as_str()).copied().filter(|p| structural(p)) else {
                continue;
            };
            let Some(layer) = p.layer.as_deref() else {
                continue;
            };
            let Some(strokes) = by_source.get(id.as_str()) else {
                continue;
            };
            // Restrict terminal authority to source strokes already belonging to this body.
            for s in strokes {
                charge(&mut work, 1)?;
                let a = s.a - w.start;
                let b = s.b - w.start;
                let (u, v) = (axis.cross(a), axis.cross(b));
                if (u - v).abs() > eps || (u.abs() - w.thickness * 0.5).abs() > eps {
                    continue;
                }
                let (a, b) = (a.dot(axis), b.dot(axis));
                if (a - b).abs() < w.thickness - eps {
                    continue;
                }
                by_layer.entry(layer).or_default().push(Rail {
                    lo: a.min(b),
                    hi: a.max(b),
                    side: usize::from(u > 0.),
                    stroke: *s,
                });
            }
        }
        let mut candidates = vec![];
        for (layer, rails) in by_layer {
            let mut low = vec![];
            let mut high = vec![];
            for a in rails.iter().filter(|r| r.side == 0) {
                for b in rails.iter().filter(|r| r.side == 1) {
                    charge(&mut work, 1)?;
                    if a.stroke.id == b.stroke.id {
                        continue;
                    }
                    if (a.hi - b.hi).abs() <= eps
                        && a.hi <= old_lo + eps
                        && old_lo - a.hi <= w.thickness + eps
                    {
                        low.push((a.hi, [a.stroke.id, b.stroke.id]));
                    }
                    if (a.lo - b.lo).abs() <= eps
                        && a.lo >= old_hi - eps
                        && a.lo - old_hi <= w.thickness + eps
                    {
                        high.push((a.lo, [a.stroke.id, b.stroke.id]));
                    }
                }
            }
            for &(lo, ids_lo) in &low {
                for &(hi, ids_hi) in &high {
                    charge(
                        &mut work,
                        rails.len().saturating_add(openings.len()).saturating_add(1),
                    )?;
                    if lo < -eps
                        || hi > length + eps
                        || hi - lo <= eps
                        || (old_lo - lo <= eps && hi - old_hi <= eps)
                        || rails.iter().any(|r| r.lo < hi - eps && r.hi > lo + eps)
                        || openings.iter().any(|other| {
                            other.id != o.id
                                && intersects_gap(w, axis, lo, hi, other.start, other.end, eps)
                        })
                    {
                        continue;
                    }
                    let strokes = &layers[layer];
                    let Some(mut caps) = cap(strokes, w, axis, lo, eps, &mut work)? else {
                        continue;
                    };
                    let Some(other_caps) = cap(strokes, w, axis, hi, eps, &mut work)? else {
                        continue;
                    };
                    caps.extend(other_caps);
                    caps.sort();
                    caps.dedup();
                    let rails = ids_lo
                        .into_iter()
                        .chain(ids_hi)
                        .map(str::to_owned)
                        .collect::<BTreeSet<_>>()
                        .into_iter()
                        .collect::<Vec<_>>();
                    candidates.push((lo, hi, layer, rails, caps));
                }
            }
        }
        charge(
            &mut work,
            candidates
                .len()
                .saturating_mul(candidates.len().max(1).ilog2() as usize + 1),
        )?;
        candidates.sort_by(|a, b| {
            a.0.total_cmp(&b.0)
                .then(a.1.total_cmp(&b.1))
                .then(a.2.cmp(b.2))
        });
        // Repeated paint with the same physical bounds is evidence, never another vote.
        let mut unique: Vec<(f64, f64, &str, Vec<String>, Vec<String>)> = vec![];
        for candidate in candidates {
            if let Some((lo, hi, layer, rails, caps)) = unique.last_mut() {
                if (candidate.0 - *lo).abs() <= eps
                    && (candidate.1 - *hi).abs() <= eps
                    && candidate.2 == *layer
                {
                    rails.extend(candidate.3);
                    rails.sort();
                    rails.dedup();
                    caps.extend(candidate.4);
                    caps.sort();
                    caps.dedup();
                    continue;
                }
            }
            unique.push(candidate);
        }
        if unique.len() != 1 {
            continue;
        }
        let (lo, hi, layer, rail_source_ids, cap_source_ids) = unique.pop().unwrap();
        let (start, end) = if x < y {
            (w.start + axis * lo, w.start + axis * hi)
        } else {
            (w.start + axis * hi, w.start + axis * lo)
        };
        out.push(PhysicalJambCorrection {
            opening_id: o.id.clone(),
            start,
            end,
            evidence: PhysicalJambEvidence {
                revision: 1,
                host_wall_id: w.id.clone(),
                original_start: o.start,
                original_end: o.end,
                source_layer: layer.to_owned(),
                rail_source_ids,
                cap_source_ids,
            },
        });
        if out.len() > MAX_PROPOSALS {
            return Err("physical jamb correction count limit exceeded".into());
        }
    }
    Ok(out)
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use crate::model::{BodyEvidence, SwingObservation};
    use std::f64::consts::FRAC_PI_2;

    pub(crate) fn line(id: &str, a: (f64, f64), b: (f64, f64)) -> Primitive {
        Primitive {
            id: id.into(),
            layer: Some("A-WALL".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Line {
                start: Point::new(a.0, a.1),
                end: Point::new(b.0, b.1),
            },
        }
    }

    pub(crate) fn fixture() -> (Vec<Primitive>, Wall, Opening) {
        let mut source = vec![
            line("west north", (95., 10.), (95., 45.)),
            line("east north", (105., 5.), (105., 45.)),
            line("west south", (95., 70.), (95., 105.)),
            line("east south", (105., 70.), (105., 105.)),
            line("north cap west", (95., 45.), (100., 45.)),
            line("north cap east", (100., 45.), (105., 45.)),
            line("south cap west", (95., 70.), (100., 70.)),
            line("south cap east", (100., 70.), (105., 70.)),
            line("open leaf", (100., 68.), (120., 68.)),
            line("closed leaf", (100., 68.), (100., 48.)),
        ];
        source.push(Primitive {
            id: "swing".into(),
            layer: Some("A-DOOR".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Arc {
                center: Point::new(100., 68.),
                radius: 20.,
                start_angle: -FRAC_PI_2,
                sweep: FRAC_PI_2,
            },
        });
        let w = Wall {
            id: "observed wall".into(),
            page: 1,
            start: Point::new(100., 10.),
            end: Point::new(100., 110.),
            thickness: 10.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: source[..4].iter().map(|p| p.id.clone()).collect(),
            requires_review: true,
        };
        let opening = Opening {
            id: "signed door".into(),
            page: 1,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: w.id.clone(),
            host_wall_ids: vec![w.id.clone()],
            start: Point::new(100., 68.),
            end: Point::new(100., 48.),
            hinge: Some(Point::new(100., 68.)),
            swing_observations: vec![SwingObservation {
                arc_source_id: "swing".into(),
                hinge: Point::new(100., 68.),
                closed_tip: Point::new(100., 48.),
                open_tip: Point::new(120., 68.),
                leaf_tolerance: 0.5,
                open_leaf_source_ids: vec!["open leaf".into()],
                closed_leaf_source_ids: vec!["closed leaf".into()],
            }],
            source_ids: vec!["swing".into(), "open leaf".into(), "closed leaf".into()],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
            physical_jambs: None,
        };
        (source, w, opening)
    }

    #[test]
    fn physical_cut_uses_opposed_frame_terminals_without_mutating_swing() {
        let (source, w, opening) = fixture();
        let snapshot = opening.clone();
        let corrections = proposals(&source, &[w], std::slice::from_ref(&opening)).unwrap();
        assert_eq!(corrections.len(), 1);
        let c = &corrections[0];
        assert_eq!(c.start, Point::new(100., 70.));
        assert_eq!(c.end, Point::new(100., 45.));
        assert_eq!(c.evidence.original_start, opening.start);
        assert_eq!(c.evidence.original_end, opening.end);
        assert_eq!(c.evidence.rail_source_ids.len(), 4);
        assert_eq!(c.evidence.cap_source_ids.len(), 4);
        assert_eq!(opening, snapshot);
    }

    #[test]
    fn physical_cut_requires_full_caps_not_repeated_inner_ticks() {
        let (mut source, w, o) = fixture();
        source.retain(|p| p.id != "north cap east");
        for i in 0..20 {
            source.push(line(&format!("repeat {i}"), (95., 45.), (100., 45.)));
        }
        assert!(proposals(&source, &[w], &[o]).unwrap().is_empty());
    }

    #[test]
    fn physical_cut_requires_both_opposed_independent_wall_faces() {
        let (source, mut w, o) = fixture();
        w.source_ids.retain(|id| id != "east north");
        assert!(proposals(&source, &[w], &[o]).unwrap().is_empty());
    }

    #[test]
    fn physical_cut_does_not_override_continuous_structural_face() {
        let (mut source, mut w, o) = fixture();
        source.push(line("continuous wall face", (95., 10.), (95., 105.)));
        w.source_ids.push("continuous wall face".into());
        assert!(proposals(&source, &[w], &[o]).unwrap().is_empty());
    }

    #[test]
    fn physical_cut_ignores_room_area_and_furniture_face_coincidence() {
        for layer in ["A-ROOM", "A-FURNITURE", "A-WALL-FURNITURE"] {
            let (mut source, mut w, o) = fixture();
            let mut p = line("coincident nonmaterial", (95., 10.), (95., 110.));
            p.layer = Some(layer.into());
            source.push(p);
            w.source_ids.push("coincident nonmaterial".into());
            assert_eq!(proposals(&source, &[w], &[o]).unwrap().len(), 1, "{layer}");
        }
    }

    #[test]
    fn physical_cut_preserves_other_openings_and_authored_originals() {
        let (source, w, o) = fixture();
        let mut window = o.clone();
        window.id = "other window".into();
        window.kind = OpeningKind::Window;
        window.start = Point::new(94., 46.);
        window.end = Point::new(106., 46.);
        assert!(
            proposals(&source, std::slice::from_ref(&w), &[o.clone(), window])
                .unwrap()
                .is_empty()
        );
        let mut forged = o.clone();
        forged.start.y += 1.;
        assert!(
            proposals(&source, std::slice::from_ref(&w), &[forged])
                .unwrap()
                .is_empty()
        );
        let mut two_hands = o.clone();
        two_hands
            .swing_observations
            .push(o.swing_observations[0].clone());
        assert!(proposals(&source, &[w], &[two_hands]).unwrap().is_empty());
    }

    #[test]
    fn physical_cut_is_rotation_scale_and_host_direction_independent() {
        for angle in [0., 0.37, FRAC_PI_2, 2.8] {
            for scale in [0.1, 1., 100.] {
                for reverse in [false, true] {
                    let (mut source, mut w, mut o) = fixture();
                    let transform = |p: Point| {
                        Point::new(700., -300.)
                            + Point::new(
                                p.x * angle.cos() - p.y * angle.sin(),
                                p.x * angle.sin() + p.y * angle.cos(),
                            ) * scale
                    };
                    for p in &mut source {
                        match &mut p.shape {
                            Shape::Line { start, end } => {
                                *start = transform(*start);
                                *end = transform(*end);
                            }
                            Shape::Arc {
                                center,
                                radius,
                                start_angle,
                                ..
                            } => {
                                *center = transform(*center);
                                *radius *= scale;
                                *start_angle += angle;
                            }
                            _ => unreachable!(),
                        }
                    }
                    w.start = transform(w.start);
                    w.end = transform(w.end);
                    w.thickness *= scale;
                    if reverse {
                        std::mem::swap(&mut w.start, &mut w.end);
                    }
                    o.start = transform(o.start);
                    o.end = transform(o.end);
                    o.hinge = o.hinge.map(transform);
                    for s in &mut o.swing_observations {
                        s.hinge = transform(s.hinge);
                        s.closed_tip = transform(s.closed_tip);
                        s.open_tip = transform(s.open_tip);
                        s.leaf_tolerance *= scale;
                    }
                    let out = proposals(&source, &[w], &[o]).unwrap();
                    assert_eq!(out.len(), 1, "{angle}/{scale}/{reverse}");
                    assert!(out[0].start.distance(transform(Point::new(100., 70.))) < 1e-7);
                    assert!(out[0].end.distance(transform(Point::new(100., 45.))) < 1e-7);
                }
            }
        }
    }

    #[test]
    fn physical_cut_budget_failure_returns_no_partial_proposal() {
        let (mut source, w, o) = fixture();
        source.push(Primitive {
            id: "path overflow".into(),
            layer: Some("A-WALL".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Polyline {
                points: vec![Point::new(0., 0.); MAX_STROKES + 2],
                closed: false,
                filled: false,
            },
        });
        assert!(proposals(&source, &[w], &[o]).is_err());
    }

    #[test]
    fn physical_cut_charges_full_referenced_leaf_paths_before_validation() {
        let (mut source, _, o) = fixture();
        let leaf = source.iter_mut().find(|p| p.id == "open leaf").unwrap();
        leaf.layer = Some("A-DOOR".into());
        leaf.shape = Shape::Polyline {
            points: vec![Point::new(100., 68.); 200],
            closed: false,
            filled: false,
        };
        let sources = source.iter().map(|p| (p.id.as_str(), p)).collect();
        let mut work = 100;
        assert!(charge_swing_sources(&o, &sources, &mut work).is_err());
        let mut work = 10_000;
        charge_swing_sources(&o, &sources, &mut work).unwrap();
        assert!(work < 6_000);
    }
}
