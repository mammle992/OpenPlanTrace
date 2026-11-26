//! A pair of leaves can close at a shared meeting point without a center jamb.
use super::{Jamb, Tick, same_axis};
use crate::model::*;
use std::collections::{HashMap, HashSet};

const PROOF_BUDGET: usize = 8_000_000;

struct Flank<'a> {
    wall: &'a Wall,
    jamb: Point,
    sources: Vec<String>,
}

fn named_structure(p: &Primitive) -> bool {
    p.layer_kind() == LayerKind::Wall
        || (p.layer_category_override.is_none() && crate::layers::is_named_wall(p.layer_name()))
}

fn pair_axis(a: &SwingObservation, b: &SwingObservation) -> Option<Point> {
    let eps = a.leaf_tolerance.min(b.leaf_tolerance);
    let u = (a.closed_tip - a.hinge).unit();
    let v = (b.closed_tip - b.hinge).unit();
    let d = (b.hinge - a.hinge).unit();
    let open_a = (a.open_tip - a.hinge).unit();
    let open_b = (b.open_tip - b.hinge).unit();
    let independent = a.arc_source_id != b.arc_source_id
        && !a
            .open_leaf_source_ids
            .iter()
            .chain(&a.closed_leaf_source_ids)
            .any(|id| b.open_leaf_source_ids.contains(id) || b.closed_leaf_source_ids.contains(id));
    (independent
        && a.closed_tip.distance(b.closed_tip) <= eps
        && u.dot(d) >= 0.995
        && v.dot(d) <= -0.995
        && open_a.dot(open_b) >= 0.995
        && open_a.dot(d).abs() <= 0.02
        && open_b.dot(d).abs() <= 0.02)
        .then_some(d)
}

fn flanks<'a>(
    walls: impl IntoIterator<Item = &'a Wall>,
    ticks: &[Tick<'_>],
    structural: &HashSet<&str>,
    find_jambs: &mut impl FnMut(Point, Point) -> Result<Jamb, String>,
    hinge: Point,
    outward: Point,
    snap: f64,
) -> Result<Vec<Flank<'a>>, String> {
    let eps = snap * 0.25;
    let mut flanks = vec![];
    for w in walls {
        if w.thickness <= 0. || w.start.distance(w.end) <= w.thickness || !same_axis(w, outward) {
            continue;
        }
        let at_start = (w.start - hinge).dot(outward) < (w.end - hinge).dot(outward);
        let jamb = if at_start { w.start } else { w.end };
        let delta = jamb - hinge;
        let offset = delta.dot(outward);
        if !(-eps..=w.thickness + eps).contains(&offset)
            || delta.cross(outward).abs() > w.thickness * 0.5 + eps
        {
            continue;
        }
        let Some(src) = w
            .source_ids
            .iter()
            .find(|id| structural.contains(id.as_str()))
        else {
            continue;
        };
        // The rail may end at the outer jamb beyond the leaf hinge. Measure
        // its actual cap at that end plane, without extending the wall or aperture.
        let face = hinge + outward * offset;
        let caps = find_jambs(face, outward)?
            .contacts(ticks, face, jamb, outward, w.thickness, snap)
            .into_iter()
            .filter(|id| structural.contains(id.as_str()));
        let mut sources: Vec<_> = caps.collect();
        if !sources.is_empty() {
            sources.push(src.clone());
            flanks.push(Flank {
                wall: w,
                jamb,
                sources,
            });
        }
    }
    Ok(flanks)
}

fn competing(o: &Opening, width: f64, previous: &Opening, previous_width: f64) -> bool {
    let axis = (o.end - o.start).unit();
    let [a, b] = [previous.start - o.start, previous.end - o.start];
    let [x, y] = [a.dot(axis), b.dot(axis)];
    let lane = (width + previous_width) * 0.5;
    (b - a).unit().dot(axis).abs() >= 0.995
        && x.max(y).min(o.start.distance(o.end)) > x.min(y).max(0.) + 1e-7
        && a.cross(axis).abs().max(b.cross(axis).abs()) <= lane
}

fn coherent_pair(l: &Flank<'_>, r: &Flank<'_>, axis: Point, eps: f64) -> bool {
    let d = (l.wall.end - l.wall.start).unit();
    let cross = (r.jamb - l.jamb).cross(d).abs();
    l.wall.id != r.wall.id
        && l.jamb.distance(r.jamb) > 1e-7
        && (r.jamb - l.jamb).unit().dot(axis) >= 0.995
        && cross <= eps
        && (l.wall.thickness - r.wall.thickness).abs() <= eps
        && same_axis(r.wall, d)
        && l.sources.iter().all(|id| !r.sources.contains(id))
}

pub(super) fn append(
    page: &Page,
    walls: &[Wall],
    ticks: &[Tick<'_>],
    swings: &[SwingObservation],
    find_jambs: &mut impl FnMut(Point, Point) -> Result<Jamb, String>,
    openings: &mut Vec<Opening>,
) -> Result<(), String> {
    if swings.len() < 2 {
        return Ok(());
    }
    // Previously established apertures take priority, including geometrically
    // coincident windows with different inferred hosts.
    let occupied: HashSet<_> = openings
        .iter()
        .flat_map(|o| {
            o.swing_observations
                .iter()
                .map(|s| s.arc_source_id.as_str())
        })
        .collect();
    let mut candidates: Vec<_> = swings
        .iter()
        .filter(|s| !occupied.contains(s.arc_source_id.as_str()))
        .collect();
    candidates.sort_by(|a, b| {
        a.hinge
            .x
            .total_cmp(&b.hinge.x)
            .then(a.hinge.y.total_cmp(&b.hinge.y))
            .then(a.arc_source_id.cmp(&b.arc_source_id))
    });
    if candidates.len() < 2
        || candidates.len().saturating_mul(candidates.len() - 1) / 2 > PROOF_BUDGET
    {
        return Ok(());
    }

    let structural: HashSet<_> = page
        .primitives
        .iter()
        .filter(|p| named_structure(p))
        .map(|p| p.id.as_str())
        .collect();
    let width_by_id: HashMap<_, _> = walls.iter().map(|w| (w.id.as_str(), w.thickness)).collect();
    let width_of = |o: &Opening| {
        o.host_wall_ids
            .iter()
            .filter_map(|id| width_by_id.get(id.as_str()).copied())
            .fold(0., f64::max)
    };
    let mut work = candidates.len() * (candidates.len() - 1) / 2;
    let mut proposals = Vec::new();
    for (i, &a) in candidates.iter().enumerate() {
        for &b in &candidates[i + 1..] {
            let Some(axis) = pair_axis(a, b) else {
                continue;
            };
            work = work.saturating_add(walls.len() * 2);
            if work > PROOF_BUDGET {
                return Ok(());
            }
            let eps = a.leaf_tolerance.min(b.leaf_tolerance);
            let snap = eps * 4.;
            let left = flanks(
                walls,
                ticks,
                &structural,
                find_jambs,
                a.hinge,
                axis * -1.,
                snap,
            )?;
            let right = flanks(walls, ticks, &structural, find_jambs, b.hinge, axis, snap)?;
            work = work.saturating_add(left.len().saturating_mul(right.len()));
            if work > PROOF_BUDGET {
                return Ok(());
            }
            let mut proofs = left
                .iter()
                .flat_map(|l| right.iter().map(move |r| (l, r)))
                .filter(|(l, r)| coherent_pair(l, r, axis, eps));
            let Some((l, r)) = proofs.next() else {
                continue;
            };
            if proofs.next().is_some() {
                continue;
            }
            let mut sources = l.sources.clone();
            sources.extend(r.sources.iter().cloned());
            sources.extend(
                [a, b]
                    .into_iter()
                    .flat_map(|s| {
                        std::iter::once(&s.arc_source_id)
                            .chain(&s.open_leaf_source_ids)
                            .chain(&s.closed_leaf_source_ids)
                    })
                    .cloned(),
            );
            sources.sort_unstable();
            sources.dedup();
            let o = Opening {
                physical_jambs: None,
                id: String::new(),
                page: page.number,
                kind: OpeningKind::Door,
                operation: "Hinged".into(),
                host_wall_id: l.wall.id.clone(),
                host_wall_ids: vec![l.wall.id.clone(), r.wall.id.clone()],
                start: l.jamb,
                end: r.jamb,
                hinge: Some(a.hinge),
                swing_observations: vec![a.clone(), b.clone()],
                source_ids: sources,
                evidence: OpeningEvidence::WallGapSwing,
                requires_review: true,
            };
            work = work.saturating_add(openings.len());
            if work > PROOF_BUDGET {
                return Ok(());
            }
            let width = width_of(&o);
            if !openings
                .iter()
                .any(|previous| competing(&o, width, previous, width_of(previous)))
            {
                proposals.push(o);
            }
        }
    }
    if work.saturating_add(proposals.len().saturating_mul(proposals.len())) > PROOF_BUDGET {
        return Ok(());
    }
    let ambiguous: Vec<_> = proposals
        .iter()
        .enumerate()
        .map(|(i, a)| {
            let width = width_of(a);
            proposals.iter().enumerate().any(|(j, b)| {
                i != j
                    && (a.swing_observations.iter().any(|p| {
                        b.swing_observations
                            .iter()
                            .any(|q| p.arc_source_id == q.arc_source_id)
                    }) || competing(a, width, b, width_of(b)))
            })
        })
        .collect();
    for (mut o, blocked) in proposals.into_iter().zip(ambiguous) {
        if !blocked {
            o.id = format!("p{}:opening:{}", page.number, openings.len() + 1);
            openings.push(o);
        }
    }
    Ok(())
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    // Older two-hand observations each close at the opposite jamb. These
    // complementary leaves share a single interior meeting point.
    let mut pairs = page
        .openings
        .iter()
        .filter_map(|o| {
            if o.evidence != OpeningEvidence::WallGapSwing {
                return None;
            }
            let [a, b, ..] = o.swing_observations.as_slice() else {
                return None;
            };
            pair_axis(a, b).map(|axis| (o, a, b, axis))
        })
        .peekable();
    if pairs.peek().is_none() {
        return Ok(());
    }
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    for (o, a, b, axis) in pairs {
        let invalid = || {
            format!(
                "opposing gap leaves disagree with measured jambs for {}",
                o.id
            )
        };
        let [left, right] = o.host_wall_ids.as_slice() else {
            return Err(invalid());
        };
        if o.swing_observations.len() != 2
            || left != &o.host_wall_id
            || [a, b]
                .into_iter()
                .any(|s| s.open_leaf_source_ids.is_empty() || s.closed_leaf_source_ids.is_empty())
        {
            return Err(invalid());
        }
        let &left = walls.get(left.as_str()).ok_or_else(invalid)?;
        let &right = walls.get(right.as_str()).ok_or_else(invalid)?;
        let caps: Vec<_> = o
            .source_ids
            .iter()
            .filter_map(|id| {
                sources
                    .get(id.as_str())
                    .copied()
                    .filter(|p| named_structure(p))
            })
            .collect();
        let structural = caps.iter().map(|p| p.id.as_str()).collect();
        let ticks: Vec<_> = caps
            .into_iter()
            .flat_map(|p| {
                segments(&p.shape)
                    .into_iter()
                    .map(|(a, b)| Tick { a, b, id: &p.id })
            })
            .collect();
        let eps = a.leaf_tolerance.min(b.leaf_tolerance);
        let snap = eps * 4.;
        let mut jambs = |p: Point, direction: Point| {
            let mut j = Jamb::default();
            for (i, t) in ticks.iter().enumerate() {
                if (t.b - t.a).unit().dot(direction).abs() <= 0.1
                    && on_segment(p, t.a, t.b).0.distance(p) <= snap
                {
                    j.ticks.push(i);
                }
            }
            Ok(j)
        };
        let l = flanks(
            [left],
            &ticks,
            &structural,
            &mut jambs,
            a.hinge,
            axis * -1.,
            snap,
        )?;
        let r = flanks(
            [right],
            &ticks,
            &structural,
            &mut jambs,
            b.hinge,
            axis,
            snap,
        )?;
        if !matches!((l.as_slice(), r.as_slice()), ([l], [r]) if
            coherent_pair(l, r, axis, eps) && l.jamb == o.start && r.jamb == o.end)
        {
            return Err(invalid());
        }
        if page.openings.iter().filter(|p| p.id != o.id).any(|p| {
            let width = p
                .host_wall_ids
                .iter()
                .filter_map(|id| walls.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(0_f64, f64::max);
            competing(o, left.thickness.max(right.thickness), p, width)
        }) {
            return Err(invalid());
        }
    }
    Ok(())
}
