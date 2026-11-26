//! Recover one signed swing authored between two already placed structural jambs.
use super::{swing::LeafStrokes, *};
use crate::placement::{WallStatus, effective_wall};
use std::collections::{BTreeMap, HashMap, HashSet};

struct Ink<'a> {
    sides: Vec<(&'a Primitive, Point, Point)>,
    arcs: Vec<&'a Primitive>,
}

pub(crate) fn proposals(page: &PageScan) -> Result<Vec<Opening>, String> {
    if page.material_tiles.is_empty() {
        return Ok(vec![]);
    }
    let tiles: HashMap<_, _> = page
        .material_tiles
        .iter()
        .filter_map(|t| Some((t.id.as_str(), t.layer.as_deref()?)))
        .collect();
    let hosts: Vec<_> = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter_map(|(w, d)| {
            if d.status != WallStatus::Ready || w.curve.is_some() {
                return None;
            }
            let mut layers = d
                .support_source_ids
                .iter()
                .filter_map(|s| tiles.get(s.as_str()).copied());
            let layer = layers.next()?;
            layers
                .all(|l| l == layer)
                .then(|| (effective_wall(w, Some(d)), layer))
        })
        .collect();
    if hosts
        .len()
        .saturating_pow(2)
        .saturating_mul(page.primitives.len())
        > 20_000_000
    {
        return Ok(vec![]);
    }
    let mut ink = HashMap::<&str, Ink>::new();
    let known: HashSet<_> = page
        .openings
        .iter()
        .filter(|o| o.evidence != OpeningEvidence::AuthoredInteriorGap)
        .flat_map(|o| &o.swing_observations)
        .map(|s| s.arc_source_id.as_str())
        .collect();
    for p in &page.primitives {
        let Some(layer) = p.layer.as_deref().filter(|name| {
            p.layer_kind() == LayerKind::Wall
                || (p.layer_category_override.is_none() && crate::layers::is_named_wall(name))
        }) else {
            continue;
        };
        let list = ink.entry(layer).or_insert_with(|| Ink {
            sides: vec![],
            arcs: vec![],
        });
        match p.shape {
            Shape::Line { start, end } => list.sides.push((p, start, end)),
            Shape::Arc { sweep, .. }
                if !known.contains(p.id.as_str())
                    && (60.0..=120.0).contains(&sweep.abs().to_degrees()) =>
            {
                list.arcs.push(p)
            }
            _ => {}
        }
    }
    let mut leaves =
        LeafStrokes::from_primitives(page.primitives.iter(), page.width.max(page.height));
    let mut matches = BTreeMap::<&str, Vec<Opening>>::new();
    for (i, (first, layer)) in hosts.iter().enumerate() {
        let Some(ink) = ink.get(layer).filter(|l| !l.arcs.is_empty()) else {
            continue;
        };
        for (second, l) in &hosts[i + 1..] {
            if layer != l {
                continue;
            }
            let half = first.thickness * 0.5;
            let tolerance = half * 6e-5;
            let axis = (first.end - first.start).unit();
            if (first.thickness - second.thickness).abs() > tolerance
                || (second.end - second.start).unit().cross(axis).abs() > 1e-6
                || (second.start - first.start).cross(axis).abs() > tolerance
            {
                continue;
            }
            let ((x, y), (a, b)) = (ordered(first, axis), ordered(second, axis));
            let (lo, hi, wall_ids) = if (a - y).dot(axis) > 0. {
                (y, a, [&first.id, &second.id])
            } else if (x - b).dot(axis) > 0. {
                (b, x, [&second.id, &first.id])
            } else {
                continue;
            };
            let length = (hi - lo).dot(axis);
            if !(6. * half..=48. * half).contains(&length) {
                continue;
            }
            if page.openings.iter().any(|o| {
                o.evidence != OpeningEvidence::AuthoredInteriorGap
                    && (o.end - o.start).unit().cross(axis).abs() < 0.05
                    && (o.start - lo).cross(axis).abs() <= 2. * half
                    && (o.start - lo).dot(axis).min((o.end - lo).dot(axis)) < length - tolerance
                    && (o.start - lo).dot(axis).max((o.end - lo).dot(axis)) > tolerance
            }) {
                continue;
            }
            let Some(rails) = rails(lo, hi, axis, half, &ink.sides, tolerance) else {
                continue;
            };
            for &arc in &ink.arcs {
                let Shape::Arc {
                    center,
                    radius,
                    start_angle,
                    sweep,
                } = arc.shape
                else {
                    continue;
                };
                if !(length - 3.4 * half..length).contains(&radius) {
                    continue;
                }
                let tips = [start_angle, start_angle + sweep]
                    .map(|angle| center + Point::new(angle.cos(), angle.sin()) * radius);
                for side in 0..2 {
                    let ray = (tips[side] - center).unit();
                    let (start, end, hosts) = if ray.dot(axis) > 0. {
                        (lo, hi, wall_ids)
                    } else {
                        (hi, lo, [wall_ids[1], wall_ids[0]])
                    };
                    let direction = (end - start).unit();
                    if ray.dot(direction) < 0.9999
                        || ![center, tips[side]]
                            .iter()
                            .all(|p| (*p - start).cross(axis).abs() <= 1.24 * half)
                        || !(0.0..=1.7 * half).contains(&(center - start).dot(direction))
                        || !(0.0..=1.7 * half).contains(&(end - tips[side]).dot(direction))
                    {
                        continue;
                    }
                    let leaf_tolerance = radius * 0.0125;
                    let open = leaves.fit(center, tips[1 - side], leaf_tolerance)?.ids;
                    let closed = leaves.fit(center, tips[side], leaf_tolerance)?.ids;
                    if open.is_empty()
                        || closed.is_empty()
                        || open.iter().any(|id| closed.contains(id))
                    {
                        continue;
                    }
                    let mut source_ids: Vec<_> = open.iter().chain(&closed).cloned().collect();
                    source_ids.extend(rails.iter().map(|&id| id.to_owned()));
                    source_ids.push(arc.id.clone());
                    source_ids.sort();
                    source_ids.dedup();
                    matches.entry(arc.id.as_str()).or_default().push(Opening {
                        id: String::new(),
                        page: page.page_number,
                        kind: OpeningKind::Door,
                        operation: "Hinged".into(),
                        host_wall_id: hosts[0].clone(),
                        host_wall_ids: hosts.into_iter().cloned().collect(),
                        start,
                        end,
                        hinge: Some(center),
                        source_ids,
                        evidence: OpeningEvidence::AuthoredInteriorGap,
                        requires_review: true,
                        physical_jambs: None,
                        swing_observations: vec![SwingObservation {
                            arc_source_id: arc.id.clone(),
                            segment_source_ids: vec![],
                            hinge: center,
                            closed_tip: tips[side],
                            open_tip: tips[1 - side],
                            leaf_tolerance,
                            open_leaf_source_ids: open,
                            closed_leaf_source_ids: closed,
                        }],
                    });
                }
            }
        }
    }
    let count = page
        .openings
        .iter()
        .filter(|o| o.evidence != OpeningEvidence::AuthoredInteriorGap)
        .count();
    let mut out: Vec<_> = matches
        .into_values()
        .filter_map(|mut group| (group.len() == 1).then(|| group.remove(0)))
        .collect();
    // Two unused swings on the same portal are ambiguous here. Leave those
    // to the separate multi-leaf recognizer.
    let mut ambiguous = vec![false; out.len()];
    for i in 0..out.len() {
        for j in i + 1..out.len() {
            if out[i]
                .host_wall_ids
                .iter()
                .all(|host| out[j].host_wall_ids.contains(host))
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    out = out
        .into_iter()
        .zip(ambiguous)
        .filter_map(|(o, ambiguous)| (!ambiguous).then_some(o))
        .collect();
    for (i, o) in out.iter_mut().enumerate() {
        o.id = format!("p{}:opening:{}", page.page_number, count + i + 1);
    }
    Ok(out)
}

fn ordered(w: &Wall, axis: Point) -> (Point, Point) {
    if (w.end - w.start).dot(axis) > 0. {
        (w.start, w.end)
    } else {
        (w.end, w.start)
    }
}

fn rails<'a>(
    lo: Point,
    hi: Point,
    axis: Point,
    half: f64,
    lines: &[(&'a Primitive, Point, Point)],
    eps: f64,
) -> Option<Vec<&'a str>> {
    let length = (hi - lo).dot(axis);
    let mut sides = [Vec::new(), Vec::new()];
    let mut caps = [Vec::new(), Vec::new()];
    for &(p, a, b) in lines {
        let (a, b) = (a - lo, b - lo);
        let (u, v) = (a.dot(axis), b.dot(axis));
        let (x, y) = (axis.cross(a), axis.cross(b));
        if u.min(v).abs() <= eps
            && (u.max(v) - length).abs() <= eps
            && (x - y).abs() <= eps
            && (half - eps..=half * 1.24).contains(&x.abs())
        {
            sides[usize::from(x > 0.)].push((p.id.as_str(), x, p.stroke_width.is_some()));
        }
        if p.stroke_width.is_some() && (u - v).abs() <= eps {
            for (i, at) in [0., length].into_iter().enumerate() {
                if (u - at).abs() <= eps && x.max(y) > -half && x.min(y) < half {
                    caps[i].push((x.min(y).max(-half), x.max(y).min(half), p.id.as_str()));
                }
            }
        }
    }
    let mut proof = sides[0].iter().find_map(|&(a, x, stroked)| {
        sides[1]
            .iter()
            .find(|&&(_, y, other)| (x + y).abs() <= eps && (stroked || other))
            .map(|&(b, _, _)| vec![a, b])
    })?;
    for mut intervals in caps {
        intervals.sort_by(|a, b| {
            a.0.total_cmp(&b.0)
                .then(b.1.total_cmp(&a.1))
                .then(a.2.cmp(b.2))
        });
        let mut at = -half;
        for (a, b, id) in intervals {
            if b <= at + eps {
                continue;
            }
            if a > at + eps {
                break;
            }
            proof.push(id);
            at = b;
        }
        if at < half - eps {
            return None;
        }
    }
    Some(proof)
}
