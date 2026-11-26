//! Short joints bounded on both sides by accepted wall material and authored ink.
use super::*;
use std::collections::BTreeSet;

const PREFIX: &str = "interior-seam:";

pub(super) fn part<'a>(
    f: &Frame,
    bars: &'a [Rail<'a>],
    face: f64,
    eps: f64,
) -> Option<(&'a str, bool)> {
    bars.iter()
        .filter_map(|s| {
            let [a, b] = s.ends.map(|p| f.local(p));
            ((a.1 - face).abs().max((b.1 - face).abs()) <= eps
                && a.0.min(b.0) <= eps
                && a.0.max(b.0) >= f.length - eps)
                .then_some((s.id, s.stroked))
        })
        .min_by_key(|(id, stroked)| (!stroked, *id))
}

pub(super) fn pattern<'a>(
    f: &Frame,
    bars: &'a [Rail<'a>],
    t: &insulation::MaterialTile,
) -> Option<Vec<&'a str>> {
    let repeat = t.repeat.dot(f.axis);
    let across = f.axis.cross(t.across);
    let phase = f.local(t.origin).0;
    let eps = f.half * 0.006;
    if (across.abs() - 2. * f.half).abs() > eps || repeat.abs() <= eps {
        return None;
    }
    let edge = -across.signum() * f.half;
    let mut slopes = [false, false];
    let mut lo = [f64::INFINITY; 2];
    let mut hi = [f64::NEG_INFINITY; 2];
    let mut seen = BTreeSet::new();
    let mut witnesses = vec![];
    for s in bars {
        let [a, b] = s.ends.map(|p| f.local(p));
        if a.0.min(b.0) < -eps
            || a.0.max(b.0) > f.length + eps
            || a.1.abs().max(b.1.abs()) > f.half + eps
            || (a.0 - b.0).abs().min((a.1 - b.1).abs()) <= eps
        {
            continue;
        }
        let cell = ((a.0.midpoint(b.0) - phase) / repeat).round();
        let u = Point::new(a.0, a.1);
        let v = Point::new(b.0, b.1);
        let matched = t.strokes.iter().any(|s| {
            [-1., 0., 1.].into_iter().any(|shift| {
                let [p, q] = s.map(|p| {
                    Point::new(phase + (cell + shift + p.x) * repeat, edge + p.y * across)
                });
                crate::model::on_segment(u, p, q).0.distance(u) <= eps
                    && crate::model::on_segment(v, p, q).0.distance(v) <= eps
            })
        });
        if matched && seen.insert(draw_path(s.id)) {
            slopes[usize::from((a.0 - b.0) * (a.1 - b.1) > 0.)] = true;
            lo = [lo[0].min(a.0).min(b.0), lo[1].min(a.1).min(b.1)];
            hi = [hi[0].max(a.0).max(b.0), hi[1].max(a.1).max(b.1)];
            witnesses.push(s.id);
        }
    }
    (witnesses.len() >= 3
        && slopes == [true, true]
        && hi[0] - lo[0] >= 0.62 * f.length
        && hi[1] - lo[1] >= f.half)
        .then_some(witnesses)
}

pub(super) fn bounds(f: &Frame, wall: &Wall) -> [(f64, f64); 4] {
    let other = Frame::new(wall);
    let normal = Point::new(-other.axis.y, other.axis.x) * other.half;
    [
        wall.start + normal,
        wall.start - normal,
        wall.end + normal,
        wall.end - normal,
    ]
    .map(|p| f.local(p))
}

pub(super) fn overlap_rect(a: &Wall, b: &Wall) -> bool {
    let eps = super::super::roundoff(a).max(super::super::roundoff(b));
    [(a, b), (b, a)].into_iter().all(|(w, v)| {
        let f = Frame::new(w);
        let p = bounds(&f, v);
        let (x0, x1, y0, y1) = minmax(&p);
        x1.min(f.length) - x0.max(0.) > eps && y1.min(f.half) - y0.max(-f.half) > eps
    })
}

pub(super) fn minmax(p: &[(f64, f64)]) -> (f64, f64, f64, f64) {
    p.iter().fold(
        (
            f64::INFINITY,
            f64::NEG_INFINITY,
            f64::INFINITY,
            f64::NEG_INFINITY,
        ),
        |(x0, x1, y0, y1), &(x, y)| (x0.min(x), x1.max(x), y0.min(y), y1.max(y)),
    )
}

fn placed_face(f: &Frame, wall: &Wall, side: f64, eps: f64) -> bool {
    let (lo, hi, below, above) = minmax(&bounds(f, wall));
    let axis = (wall.end - wall.start).unit();
    f.axis.dot(axis).abs() <= 1e-5
        && lo <= eps
        && hi >= f.length - eps
        && if side < 0. {
            (above + f.half).abs() <= eps
        } else {
            (below - f.half).abs() <= eps
        }
}

fn gap(f: &Frame, branch: &Wall, mate: &Wall) -> Option<(f64, f64)> {
    let eps = super::super::roundoff(branch).max(super::super::roundoff(mate));
    let v = (mate.end - mate.start).unit();
    let aligned = f.axis.cross(v).abs() <= 1e-5;
    if !aligned && f.axis.dot(v).abs() > 1e-5 {
        return None;
    }
    let (lo, hi, left, right) = minmax(&bounds(f, mate));
    let width = branch.thickness;
    if left > -f.half + eps || right < f.half - eps {
        return None;
    }
    if aligned && (left + right).abs() > 3e-5 * width {
        return None;
    }
    // A longer gap needs a continuation of the same lane. On a T joint
    // the new material should be only the tiny, measured finish seam.
    let limit = width * if aligned { 1.25 } else { 0.19 };
    if (eps..=limit).contains(&(-hi)) {
        Some((hi, 0.))
    } else if (eps..=limit).contains(&(lo - f.length)) {
        Some((f.length, lo))
    } else {
        None
    }
}

pub(super) fn append(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    if page.material_tiles.is_empty()
        || page
            .walls
            .len()
            .saturating_mul(page.material_tiles.len())
            .saturating_mul(page.walls.len())
            > MAX_WORK
    {
        return;
    }
    let bodies: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| {
            d.status == WallStatus::Ready || d.reason == WallReason::SourceBoundedWallLining
        })
        .filter(|(w, _)| w.curve.is_none() && w.thickness > 0. && w.start.distance(w.end) > 0.)
        .map(|(w, d)| (effective_wall(w, Some(d)), d))
        .collect();
    let mut ink: HashMap<&str, Vec<Rail<'_>>> = HashMap::new();
    let mut exterior_ink: HashMap<&str, Vec<Rail<'_>>> = HashMap::new();
    for p in &page.primitives {
        let Some(layer) = p
            .layer
            .as_deref()
            .filter(|name| super::interior::interior_layer(name) || exterior_layer(name))
        else {
            continue;
        };
        if p.layer_category_override.is_some() && p.layer_kind() != crate::model::LayerKind::Wall {
            continue;
        }
        if let Shape::Line { start, end } = p.shape {
            let layer_ink = if exterior_layer(layer) {
                &mut exterior_ink
            } else {
                &mut ink
            };
            layer_ink.entry(layer).or_default().push(Rail {
                id: &p.id,
                ends: [start, end],
                stroked: p.stroke_width.is_some(),
            });
        }
    }
    let tiles: Vec<_> = page
        .material_tiles
        .iter()
        .enumerate()
        .filter_map(|(i, t)| {
            let bars = ink.get(t.layer.as_deref()?)?;
            Some((t, batt_body(t, page.page_number, i, 1.)?, bars))
        })
        .collect();
    let mut proposals = vec![];
    for (branch, decision) in &bodies {
        if decision.status != WallStatus::Ready {
            continue;
        }
        let f = Frame::new(branch);
        let eps = super::super::roundoff(branch);
        for (mate, mate_decision) in &bodies {
            if branch.id == mate.id {
                continue;
            }
            let Some((lo, hi)) = gap(&f, branch, mate) else {
                continue;
            };
            let mut seam = branch.as_ref().clone();
            seam.start = f.origin + f.axis * lo;
            seam.end = f.origin + f.axis * hi;
            if page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(&seam, o))
                || bodies.iter().any(|(w, _)| overlap_rect(&seam, w))
            {
                continue;
            }
            let s = Frame::new(&seam);
            let Some((t, mut sources, extra_host)) = tiles.iter().find_map(|(t, w, ink)| {
                let [a, b] = [w.start, w.end].map(|p| f.local(p));
                if a.1.abs().max(b.1.abs()) > eps
                    || (w.thickness - branch.thickness).abs() > eps
                    || a.0.max(b.0).min(f.length) - a.0.min(b.0).max(0.) <= eps
                {
                    return None;
                }
                let mut proof = pattern(&s, ink, t)?;
                let extra = match (part(&s, ink, -s.half, eps), part(&s, ink, s.half, eps)) {
                    (Some((a, sa)), Some((b, sb)))
                        if draw_path(a) != draw_path(b) && (sa || sb) =>
                    {
                        proof.extend([a, b]);
                        None
                    }
                    (Some((a, true)), None) | (None, Some((a, true))) => {
                        let side = if part(&s, ink, -s.half, eps).is_some() {
                            1.
                        } else {
                            -1.
                        };
                        let (host, _) = bodies.iter().find(|(body, decision)| {
                            decision.status == WallStatus::Ready && placed_face(&s, body, side, eps)
                        })?;
                        proof.push(a);
                        Some(host.id.clone())
                    }
                    (None, None)
                        if s.length <= 0.19 * seam.thickness
                            && mate_decision.status == WallStatus::Ready =>
                    {
                        // At a shared layer boundary the opposing placed wall
                        // can own both exact rails. Its proven material layer
                        // supplies the borders; the interior lattice remains
                        // the only authority for what fills the seam.
                        let layers: BTreeSet<_> = page
                            .material_tiles
                            .iter()
                            .filter(|t| mate_decision.support_source_ids.contains(&t.id))
                            .filter_map(|t| t.layer.as_deref().filter(|name| exterior_layer(name)))
                            .collect();
                        if layers.len() != 1 {
                            return None;
                        }
                        let bars = exterior_ink.get(layers.first()?)?;
                        let (a, sa) = part(&s, bars, -s.half, eps)?;
                        let (b, sb) = part(&s, bars, s.half, eps)?;
                        if draw_path(a) == draw_path(b) || !(sa || sb) {
                            return None;
                        }
                        proof.extend([a, b]);
                        None
                    }
                    _ => return None,
                };
                Some((*t, proof, extra))
            }) else {
                continue;
            };
            sources.sort();
            sources.dedup();
            seam.source_ids = sources.into_iter().map(str::to_owned).collect();
            let mut wall_ids = vec![branch.id.clone(), mate.id.clone()];
            wall_ids.extend(extra_host);
            wall_ids.sort();
            wall_ids.dedup();
            proposals.push((seam, wall_ids, t.id.clone()));
        }
    }
    drop(bodies);
    let mut chosen: Vec<(Wall, Vec<String>, String)> = vec![];
    for (mut seam, walls, tile) in proposals {
        if (seam.start.x, seam.start.y) > (seam.end.x, seam.end.y) {
            std::mem::swap(&mut seam.start, &mut seam.end);
        }
        let eps = super::super::roundoff(&seam);
        if chosen.iter().any(|(w, _, _)| {
            w.start.distance(seam.start) <= eps
                && w.end.distance(seam.end) <= eps
                && (w.thickness - seam.thickness).abs() <= eps
        }) {
            continue;
        }
        chosen.push((seam, walls, tile));
    }
    let mut withheld = vec![false; chosen.len()];
    for (i, (w, _, _)) in chosen.iter().enumerate() {
        for (j, (v, _, _)) in chosen.iter().enumerate().skip(i + 1) {
            if overlap_rect(w, v) {
                withheld[i] = true;
                withheld[j] = true;
            }
        }
    }
    for (i, ((mut seam, walls, tile), reject)) in chosen.into_iter().zip(withheld).enumerate() {
        if reject {
            continue;
        }
        seam.id = format!("{PREFIX}{}:{}", page.page_number, i + 1);
        let mut support_source_ids = seam.source_ids.clone();
        support_source_ids.push(tile);
        support_source_ids.sort();
        let decision = WallDecision {
            wall_id: seam.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::SourceBoundedInteriorCore,
            component_id: format!("p{}:{}", seam.page, seam.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: walls,
            support_source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        page.semantics
            .routing
            .push(crate::semantics::wall_route(&seam));
        page.walls.push(seam);
        decisions.push(decision);
    }
}
