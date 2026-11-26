//! Resolve competing wall axes where the drawing supplies both complete frame caps.
use super::{DoorMatch, Tick, projected};
use crate::model::{LayerKind, OpeningEvidence, Point, Primitive, Wall};
use std::collections::{BTreeSet, HashMap};

struct Cap<'a> {
    at: Point,
    sources: Vec<&'a str>,
}

fn cap<'a>(
    ticks: &[Tick<'a>],
    primitives: &HashMap<&str, &Primitive>,
    layer: &str,
    host: &Wall,
    near: Point,
    away: Point,
    reach: f64,
) -> Option<Cap<'a>> {
    let direction = (away - near).unit();
    let axis_at = projected(host, near);
    let local = |p: Point| {
        let delta = p - axis_at;
        (delta.dot(direction), direction.cross(delta))
    };
    let roundoff = (host.thickness * 2e-5).max(1e-7);
    let face_roundoff = host.thickness * 0.005;
    let half = host.thickness * 0.5;
    let closed_leaf_side = local(near).1;
    let mut slices = Vec::new();
    for t in ticks {
        let Some(p) = primitives.get(t.id) else {
            continue;
        };
        if p.layer.as_deref() != Some(layer)
            || (p.layer_category_override.is_some() && p.layer_kind() != LayerKind::Wall)
            || p.stroke_width.is_none_or(|w| w <= 0.)
        {
            continue;
        }
        let (ta, xa) = local(t.a);
        let (tb, xb) = local(t.b);
        if (ta - tb).abs() > roundoff || ta < -reach || ta > roundoff {
            continue;
        }
        let lo = xa.min(xb);
        let hi = xa.max(xb);
        if hi >= -half - face_roundoff && lo <= half + face_roundoff && hi - lo > roundoff {
            slices.push((ta, lo, hi, t.id));
        }
    }
    slices.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.3.cmp(b.3))
    });
    let mut results = vec![];
    let mut remaining = slices.as_slice();
    while let Some(&(plane, _, _, _)) = remaining.first() {
        let count = remaining.partition_point(|s| s.0 - plane <= roundoff);
        let mut section = remaining[..count].to_vec();
        remaining = &remaining[count..];
        section.sort_by(|a, b| {
            a.1.total_cmp(&b.1)
                .then(b.2.total_cmp(&a.2))
                .then(a.3.cmp(b.3))
        });
        let mut components = Vec::<(f64, f64, Vec<&str>)>::new();
        for (_, lo, hi, id) in section {
            if let Some((_, end, ids)) = components.last_mut()
                && lo <= *end + roundoff
            {
                if hi > *end + roundoff {
                    *end = hi;
                    ids.push(id);
                }
            } else {
                components.push((lo, hi, vec![id]));
            }
        }
        for (lo, hi, sources) in components {
            let independent: BTreeSet<_> = sources
                .iter()
                .map(|s| s.split_once(":subpath:").map_or(*s, |(path, _)| path))
                .collect();
            if independent.len() >= 2
                && (lo + half).abs() <= face_roundoff
                && (hi - half).abs() <= face_roundoff
                && (lo - face_roundoff..=hi + face_roundoff).contains(&closed_leaf_side)
            {
                results.push(Cap {
                    at: axis_at + direction * plane,
                    sources,
                });
            }
        }
    }
    let [one]: [Cap<'a>; 1] = results.try_into().ok()?;
    Some(one)
}

fn cap_nearby<'s, 'a>(
    ticks: &'s [Tick<'a>],
    host: &Wall,
    tip: Point,
    inward: Point,
    reach: f64,
    max_tick: f64,
) -> &'s [Tick<'a>] {
    let lane = projected(host, tip);
    let face = lane - inward * reach;
    let half_x = inward.y.abs() * host.thickness * 0.5 + host.thickness * 0.005;
    let lo = lane.x.min(face.x) - half_x - max_tick;
    let hi = lane.x.max(face.x) + half_x;
    let first = ticks.partition_point(|t| t.a.x.min(t.b.x) < lo);
    let last = ticks.partition_point(|t| t.a.x.min(t.b.x) <= hi);
    &ticks[first..last]
}

pub(super) fn resolve<'a>(
    matches: &mut Vec<DoorMatch<'a>>,
    tick_index: &[Tick<'a>],
    primitives: &HashMap<&str, &Primitive>,
    arc: &Primitive,
    tolerance: f64,
    max_tick: f64,
    visits: &mut usize,
) -> Result<(), String> {
    if !(2..=16).contains(&matches.len()) {
        return Ok(());
    }
    let Some(layer) = arc
        .layer
        .as_deref()
        .filter(|l| crate::layers::is_named_wall(l))
    else {
        return Ok(());
    };
    let mut supported = Vec::new();
    for (i, m) in matches.iter().enumerate() {
        if m.evidence != OpeningEvidence::FiniteJambSwing
            || m.swing.open_leaf_source_ids.is_empty()
            || !m.primary.source_ids.iter().any(|id| {
                primitives
                    .get(id.as_str())
                    .is_some_and(|p| p.layer.as_deref() == Some(layer))
            })
        {
            continue;
        }
        let [a, b] = [m.swing.hinge, m.swing.closed_tip];
        let axis = (m.end - m.start).unit();
        let source_axis = (b - a).unit();
        if source_axis.dot(axis) < 0.9999 {
            continue;
        }
        let reach = tolerance.min(m.primary.thickness * 0.45);
        let near = cap_nearby(tick_index, m.primary, a, axis, reach, max_tick);
        let far = cap_nearby(tick_index, m.primary, b, axis * -1., reach, max_tick);
        *visits = visits.saturating_add(near.len() + far.len());
        if *visits > 60_000_000 {
            return Err("opening jamb contact budget exceeded".into());
        }
        let (Some(mut start), Some(end)) = (
            cap(near, primitives, layer, m.primary, a, a + axis, reach),
            cap(far, primitives, layer, m.primary, b, b - axis, reach),
        ) else {
            continue;
        };
        let exact = |p: Point| p.distance(projected(m.primary, p)) <= m.primary.thickness * 2e-5;
        if !exact(start.at) || !exact(end.at) || start.at.distance(end.at) <= a.distance(b) {
            continue;
        }
        start.sources.extend(end.sources);
        supported.push((i, start.at, end.at, start.sources));
    }
    if let Ok([(i, start, end, sources)]) = <[_; 1]>::try_from(supported) {
        let mut winner = matches.swap_remove(i);
        winner.start = start;
        winner.end = end;
        winner.confirmed_caps = true;
        winner
            .sources
            .extend(sources.into_iter().map(str::to_owned));
        matches.clear();
        matches.push(winner);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{BodyEvidence, Shape};

    #[test]
    fn joined_cap_certifies_only_the_full_measured_lane() {
        let p = Point::new;
        let make = |id: &str, y0, y1| Primitive {
            id: id.to_owned(),
            layer: Some("Walls".into()),
            stroke_width: Some(0.1),
            layer_category_override: None,
            shape: Shape::Line {
                start: p(-1., y0),
                end: p(-1., y1),
            },
        };
        let strokes = [make("face-one", 3., 4.5), make("face-two", 4.5, 7.)];
        let ids: HashMap<_, _> = strokes.iter().map(|p| (p.id.as_str(), p)).collect();
        let ticks = strokes
            .iter()
            .map(|p| match p.shape {
                Shape::Line { start, end } => Tick {
                    a: start,
                    b: end,
                    id: &p.id,
                },
                _ => unreachable!(),
            })
            .collect::<Vec<_>>();
        let mut host = Wall {
            id: "bounded wall".into(),
            page: 1,
            start: p(-10., 5.),
            end: p(20., 5.),
            thickness: 4.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![],
            requires_review: true,
        };
        let check = |host: &Wall| cap(&ticks, &ids, "Walls", host, p(0., 3.2), p(1., 3.2), 2.);
        assert_eq!(check(&host).unwrap().at, p(-1., 5.));
        host.start.y = 4.;
        host.end.y = 4.;
        host.thickness = 2.;
        assert!(check(&host).is_none());
    }
}
