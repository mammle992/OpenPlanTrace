//! A stepped perimeter can contain a batt return whose source outline is
//! partitioned by corner clips. Require the rails, caps and matching texture.
use super::*;

fn cap<'a>(f: &Frame, lines: &'a [Rail<'a>], at: f64, eps: f64) -> Option<&'a str> {
    lines.iter().find_map(|s| {
        let [a, b] = s.ends.map(|p| f.local(p));
        ((a.0 - at).abs() <= eps
            && (b.0 - at).abs() <= eps
            && (a.1.min(b.1) + f.half).abs() <= eps
            && (a.1.max(b.1) - f.half).abs() <= eps)
            .then_some(s.id)
    })
}

fn face_chain<'a>(
    f: &Frame,
    lines: &'a [Rail<'a>],
    offset: f64,
    expected: [f64; 2],
    eps: f64,
) -> Option<Vec<&'a str>> {
    let mut intervals = Vec::new();
    for s in lines {
        let [a, b] = s.ends.map(|p| f.local(p));
        if (a.1 - offset).abs().max((b.1 - offset).abs()) > eps {
            continue;
        }
        let lo = a.0.min(b.0).max(expected[0]);
        let hi = a.0.max(b.0).min(expected[1]);
        if hi - lo > eps {
            intervals.push((lo, hi, s.id));
        }
    }
    intervals.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(b.1.total_cmp(&a.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = expected[0];
    let mut witnesses = vec![];
    for (lo, hi, id) in intervals {
        if hi <= end + eps {
            continue;
        }
        if lo > end + eps {
            return None;
        }
        end = hi;
        witnesses.push(id);
        if end >= expected[1] - eps {
            return Some(witnesses);
        }
    }
    None
}

fn matching_hatch<'a>(
    f: &Frame,
    lines: &'a [Rail<'a>],
    template: &insulation::MaterialTile,
) -> Option<Vec<&'a str>> {
    let repeat = template.repeat.dot(f.axis);
    let across = f.axis.cross(template.across);
    let phase = f.local(template.origin).0;
    let eps = f.half * 0.006;
    let width = 2. * f.half;
    if (across.abs() - width).abs() > eps || repeat.abs() <= eps || repeat.abs() > width * 1.3 {
        return None;
    }
    let edge = -across.signum() * f.half;
    let mut samples: [Vec<(&str, bool, f64)>; 2] = [vec![], vec![]];
    for line in lines {
        let [a, b] = line.ends.map(|p| f.local(p));
        let middle = (a.0 + b.0) * 0.5;
        let i = if middle <= 1.6 * width {
            0
        } else if f.length - middle <= 1.6 * width {
            1
        } else {
            continue;
        };
        let u = Point::new(a.0, a.1);
        let v = Point::new(b.0, b.1);
        if a.0.min(b.0) < -eps
            || a.0.max(b.0) > f.length + eps
            || a.1.abs().max(b.1.abs()) > f.half + eps
            || (a.0 - b.0).abs().min((a.1 - b.1).abs()) <= 4. * eps
        {
            continue;
        }
        let k = ((middle - phase) / repeat).round();
        let found = template.strokes.iter().any(|s| {
            [-1., 0., 1.].into_iter().any(|shift| {
                let [p, q] =
                    s.map(|p| Point::new(phase + (k + shift + p.x) * repeat, edge + p.y * across));
                crate::model::on_segment(u, p, q).0.distance(u) <= eps
                    && crate::model::on_segment(v, p, q).0.distance(v) <= eps
            })
        });
        if found {
            samples[i].push((line.id, (a.0 - b.0) * (a.1 - b.1) > 0., (a.1 + b.1) * 0.5));
        }
    }
    if !samples.iter().all(|s| {
        s.iter().filter(|x| x.1).count() >= 3
            && s.iter().filter(|x| !x.1).count() >= 3
            && s.iter().any(|x| x.2 < -f.half * 0.45)
            && s.iter().any(|x| x.2 > f.half * 0.45)
    }) {
        return None;
    }
    Some(samples.into_iter().flatten().map(|x| x.0).collect())
}

fn measured_return(short: &Wall, long: &Wall) -> Option<Wall> {
    let u = (short.end - short.start).unit();
    let v = (long.end - long.start).unit();
    let width = short.thickness;
    // Compare differently rounded authored widths. The published body keeps the exact step.
    let gauge_tolerance = width * 3e-5;
    if u.cross(v).abs() > 1e-5 || (short.thickness - long.thickness).abs() > gauge_tolerance {
        return None;
    }
    for [a, far_a] in [[short.start, short.end], [short.end, short.start]] {
        for [b, far_b] in [[long.start, long.end], [long.end, long.start]] {
            let step = (b - a).dot(u);
            let sideways = b - a - u * step;
            let depth = sideways.norm();
            if (step.abs() - width).abs() > gauge_tolerance
                || !(2.5 * width..=10. * width).contains(&depth)
                || (far_a - a).dot(u) * step >= 0.
                || (far_b - b).dot(u) * step <= 0.
            {
                continue;
            }
            let n = sideways.unit();
            let midpoint = a + u * (step * 0.5);
            let mut wall = short.clone();
            wall.start = midpoint - n * (width * 0.5);
            wall.end = midpoint + n * (depth + long.thickness * 0.5);
            wall.thickness = step.abs();
            wall.source_ids.clear();
            return Some(wall);
        }
    }
    None
}

fn near_template(candidate: &Wall, material: &Wall) -> bool {
    let f = Frame::new(candidate);
    let [(a, b), (c, d)] = [f.local(material.start), f.local(material.end)];
    let eps = super::super::roundoff(candidate).max(super::super::roundoff(material));
    if (material.thickness - candidate.thickness).abs() > 4. * eps
        || b.abs().max(d.abs()) > 4. * eps
    {
        return false;
    }
    let margin = candidate.thickness * 0.25;
    let (lo, hi) = (a.min(c), a.max(c));
    ((-margin..=eps).contains(&hi)) || ((f.length - eps..=f.length + margin).contains(&lo))
}

/// Derive the opposite door jamb and its return only when the wall can
/// reconnect to a mature exterior shoulder through independently cited ink.
pub(super) fn prove<'a>(
    page: &PageScan,
    ready: &[(std::borrow::Cow<'_, Wall>, &WallDecision)],
    tiles: &[(&'a insulation::MaterialTile, Wall, &'a Vec<Rail<'a>>)],
    branch: &Wall,
    branch_tile: &insulation::MaterialTile,
    door: &Opening,
) -> Vec<Vec<(Wall, WallDecision)>> {
    let mut proposals = vec![];
    let branch_is_left = branch
        .start
        .distance(door.start)
        .min(branch.end.distance(door.start))
        < branch
            .start
            .distance(door.end)
            .min(branch.end.distance(door.end));
    let direction = (door.end - door.start).unit() * (if branch_is_left { 1. } else { -1. });
    for &(short_tile, ref short, bars) in tiles {
        let f = Frame::new(short);
        if short_tile.layer != branch_tile.layer
            || f.length < short.thickness
            || f.length > short.thickness * 2.5
            || (branch.thickness - short.thickness).abs() > 2. * super::super::roundoff(short)
            || (short.start - branch.start).dot(direction) <= 0.
            || (short.end - short.start)
                .unit()
                .cross((branch.end - branch.start).unit())
                .abs()
                > 1e-5
            || page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(short, o))
        {
            continue;
        }
        let Some((at_start, jamb)) = measured_doorward_end(short, door, bars) else {
            continue;
        };
        let doorward = if at_start { short.start } else { short.end };
        if (doorward - door.start).dot(direction)
            < (door.end - door.start).dot(direction).max(0.) - f.tolerance
        {
            continue;
        }
        let eps = super::super::roundoff(short);
        let (Some(face_a), Some(face_b)) = (
            face_chain(&f, bars, -f.half, [0., f.length], eps),
            face_chain(&f, bars, f.half, [0., f.length], eps),
        ) else {
            continue;
        };
        let mut short_sources = face_a;
        short_sources.extend(face_b);
        short_sources.push(jamb);
        for (existing, decision) in ready {
            for &(long_tile, ref long, _) in tiles {
                if long_tile.layer != short_tile.layer
                    || !decision.support_source_ids.contains(&long_tile.id)
                {
                    continue;
                }
                let Some(ret) = measured_return(short, long) else {
                    continue;
                };
                if !corner_touch(&ret, existing) && !corner_touch(&ret, long) {
                    continue;
                }
                if ready.iter().any(|(w, _)| {
                    overlap::parallel_bodies_overlap(&ret, w)
                        || overlap::parallel_bodies_overlap(short, w)
                }) || page
                    .openings
                    .iter()
                    .any(|o| overlap::aperture_crosses(&ret, o))
                {
                    continue;
                }
                let r = Frame::new(&ret);
                let eps = super::super::roundoff(&ret);
                let (Some(a), Some(b)) = (cap(&r, bars, 0., eps), cap(&r, bars, r.length, eps))
                else {
                    continue;
                };
                // Each facing rail is present throughout the open middle.
                // A known perpendicular painted core supplies the abutting
                // face interval at the corresponding end, never a free gap.
                let side = (short.start - ret.start)
                    .dot(Point::new(-r.axis.y, r.axis.x))
                    .signum();
                let (Some(rail_a), Some(rail_b)) = (
                    face_chain(&r, bars, side * r.half, [short.thickness, r.length], eps),
                    face_chain(
                        &r,
                        bars,
                        -side * r.half,
                        [0., r.length - long.thickness],
                        eps,
                    ),
                ) else {
                    continue;
                };
                let Some((template, hatch)) =
                    page.material_tiles.iter().enumerate().find_map(|(i, t)| {
                        if !t
                            .layer
                            .as_deref()
                            .is_some_and(|layer| material::wall_name(layer).1)
                        {
                            return None;
                        }
                        let reference = body(t, page.page_number, i)?;
                        near_template(&ret, &reference).then_some(())?;
                        Some((t, matching_hatch(&r, bars, t)?))
                    })
                else {
                    continue;
                };
                let mut sources = rail_a;
                sources.extend(rail_b);
                sources.extend([a, b]);
                sources.extend(hatch);
                let (pier, mut pier_decision) = step_decision(
                    short.clone(),
                    short_tile,
                    &short_sources,
                    &branch.id,
                    "pier",
                );
                pier_decision.opening_ids = vec![door.id.clone()];
                let (bend, mut bend_decision) =
                    step_decision(ret, short_tile, &sources, &existing.id, "return");
                bend_decision
                    .support_source_ids
                    .extend([template.id.clone(), long_tile.id.clone()]);
                bend_decision.support_source_ids.sort();
                bend_decision.support_source_ids.dedup();
                proposals.push(vec![(pier, pier_decision), (bend, bend_decision)]);
            }
        }
    }
    proposals
}
