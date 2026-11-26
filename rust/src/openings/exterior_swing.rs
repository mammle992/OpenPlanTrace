//! Recover an exterior doorway from a full casing and its separately drawn leaf.
use crate::{
    layers::named_exterior_wall,
    model::{
        BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape,
        SwingObservation,
    },
    placement::{WallStatus, effective_wall},
    semantics::{RoutingItem, RoutingKind, Span},
};
use std::collections::{BTreeMap, BTreeSet, HashSet};

const MAX_WORK: usize = 2_000_000;

struct Casing<'a> {
    start: Point,
    end: Point,
    width: f64,
    layer: &'a str,
    rails: [&'a str; 2],
}

impl Casing<'_> {
    fn local(&self, p: Point) -> Point {
        let u = (self.end - self.start).unit();
        let q = p - self.start;
        Point::new(q.dot(u), u.cross(q))
    }
    fn overlaps(&self, a: Point, b: Point, half_width: f64, eps: f64) -> bool {
        let [p, q] = [a, b].map(|x| self.local(x));
        p.x.min(q.x) < self.start.distance(self.end) - eps
            && p.x.max(q.x) > eps
            && p.y.min(q.y) - half_width < self.width * 0.5 - eps
            && p.y.max(q.y) + half_width > -self.width * 0.5 + eps
    }
}

fn line(p: &Primitive) -> Option<[Point; 2]> {
    match p.shape {
        Shape::Line { start, end } => Some([start, end]),
        _ => None,
    }
}

fn casing<'a>(
    a: &'a Primitive,
    b: &'a Primitive,
    hinge: Point,
    tip: Point,
    layer: &'a str,
) -> Option<Casing<'a>> {
    let [mut lo, mut hi] = line(a)?;
    let [mut left, mut right] = line(b)?;
    let radial = (tip - hinge).unit();
    if (hi - lo).dot(radial) < 0. {
        std::mem::swap(&mut lo, &mut hi);
    }
    let axis = (hi - lo).unit();
    if axis.dot(radial) < 0.9999 {
        return None;
    }
    if (right - left).dot(axis) < 0. {
        std::mem::swap(&mut left, &mut right);
    }
    let width = axis.cross(left - lo).abs();
    let eps = width * 0.004;
    let length = lo.distance(hi);
    let radius = hinge.distance(tip);
    if !(length * 0.12..=length * 0.4).contains(&width)
        || (a.stroke_width.is_none() && b.stroke_width.is_none())
        || axis.dot(left - lo).abs().max(axis.dot(right - hi).abs()) > eps
        || (right - left).unit().cross(axis).abs() > 1e-5
        || !(0.02 * width..=0.23 * width).contains(&axis.dot(hinge - lo))
        || !(0.02 * width..=0.23 * width).contains(&axis.dot(hi - tip))
        || !(0.12 * width..=0.43 * width).contains(&(length - radius))
        || axis
            .cross(hinge - lo)
            .abs()
            .min(axis.cross(hinge - left).abs())
            > width * 0.07
    {
        return None;
    }
    let result = Casing {
        start: (lo + left) * 0.5,
        end: (hi + right) * 0.5,
        width,
        layer,
        rails: [&a.id, &b.id],
    };
    (result.local(hinge).y * result.local(tip).y > 0.).then_some(result)
}

fn cap<'a>(frame: &Casing<'_>, at: f64, hinge: Point, ink: &[&'a Primitive]) -> Option<&'a str> {
    let across = frame.local(hinge).y;
    ink.iter().find_map(|p| {
        let [a, b] = line(p)?.map(|v| frame.local(v));
        let eps = frame.width * 0.004;
        let [lo, hi] = [a.y.min(b.y), a.y.max(b.y)];
        (p.stroke_width.is_some()
            && (a.x - at).abs().max((b.x - at).abs()) <= eps
            && (frame.width * 0.18..=frame.width * 1.05).contains(&(hi - lo))
            && across >= lo - frame.width * 0.07
            && across <= hi + frame.width * 0.07)
            .then_some(p.id.as_str())
    })
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let mut lines = BTreeMap::<&str, Vec<&Primitive>>::new();
    let mut arcs = vec![];
    for p in &page.primitives {
        let Some(layer) = named_exterior_wall(p) else {
            continue;
        };
        match p.shape {
            Shape::Line { .. } => lines.entry(layer).or_default().push(p),
            Shape::Arc { sweep, .. }
                if (sweep.abs() - std::f64::consts::FRAC_PI_2).abs() < 0.035 =>
            {
                arcs.push((layer, p))
            }
            _ => {}
        }
    }
    if arcs.is_empty() {
        return Ok(());
    }
    let used: HashSet<_> = page
        .openings
        .iter()
        .flat_map(|o| {
            o.swing_observations
                .iter()
                .map(|s| s.arc_source_id.as_str())
        })
        .collect();
    let mut proposals = vec![];
    let mut work = MAX_WORK;
    for (layer, p) in arcs {
        if used.contains(p.id.as_str()) {
            continue;
        }
        let Some(ink) = lines.get(layer) else {
            continue;
        };
        let Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } = p.shape
        else {
            unreachable!()
        };
        let point = |angle: f64| center + Point::new(angle.cos(), angle.sin()) * radius;
        let [a, b] = [point(start_angle), point(start_angle + sweep)];
        let mut choices = vec![];
        for [closed, open] in [[a, b], [b, a]] {
            if work < ink.len() {
                return Ok(());
            }
            work -= ink.len();
            let near = ink
                .iter()
                .copied()
                .filter(|q| {
                    line(q).is_some_and(|[start, end]| {
                        (radius * 1.02..=radius * 1.16).contains(&start.distance(end))
                            && (end - start).unit().dot((closed - center).unit()).abs() > 0.9999
                            && (start + end).distance(center + closed) < radius
                    })
                })
                .collect::<Vec<_>>();
            for (i, first) in near.iter().enumerate() {
                for second in &near[i + 1..] {
                    if work == 0 {
                        return Ok(());
                    }
                    work -= 1;
                    let Some(c) = casing(first, second, center, closed, layer) else {
                        continue;
                    };
                    let h = c.local(center).y;
                    let leaf = c.local(open).y;
                    if h * leaf <= 0. || leaf.abs() <= c.width {
                        continue;
                    }
                    let length = c.start.distance(c.end);
                    let cost = ink
                        .len()
                        .saturating_mul(2)
                        .saturating_add(page.walls.len())
                        .saturating_add(page.openings.len());
                    if work < cost {
                        return Ok(());
                    }
                    work -= cost;
                    let (Some(left), Some(right)) =
                        (cap(&c, 0., center, ink), cap(&c, length, center, ink))
                    else {
                        continue;
                    };
                    let epsilon = c.width * 0.005;
                    if page.openings.iter().any(|o| {
                        (o.kind == OpeningKind::Door
                            || matches!(
                                o.evidence,
                                OpeningEvidence::AuthoredWindowFrame
                                    | OpeningEvidence::CompositeWindowFrame
                            ))
                            && c.overlaps(o.start, o.end, 0., epsilon)
                    }) {
                        continue;
                    }
                    let mut hosts = vec![];
                    let mut conflict = false;
                    for (wall, decision) in page
                        .walls
                        .iter()
                        .zip(page.placement_decisions.iter().flatten())
                    {
                        let measured = effective_wall(wall, Some(decision));
                        if decision.status == WallStatus::Ready {
                            conflict |= measured.curve.is_none()
                                && c.overlaps(
                                    measured.start,
                                    measured.end,
                                    measured.thickness * 0.5,
                                    epsilon,
                                );
                            let [m, n] = [measured.start, measured.end].map(|v| c.local(v));
                            if (m.y - n.y).abs() <= epsilon
                                && m.y.abs() <= c.width * 0.35
                                && (m.x.max(n.x).abs() <= epsilon
                                    || (m.x.min(n.x) - length).abs() <= epsilon)
                            {
                                hosts.push(wall.id.clone());
                            }
                        } else if wall.body == BodyEvidence::ParallelFaces
                            && wall
                                .source_ids
                                .iter()
                                .any(|id| c.rails.contains(&id.as_str()))
                        {
                            let [m, n] = [wall.start, wall.end].map(|v| c.local(v));
                            if (m.y - n.y).abs() <= epsilon
                                && m.y.abs() < c.width * 0.5
                                && m.x.min(n.x) <= epsilon
                                && m.x.max(n.x) >= length - epsilon
                            {
                                hosts.push(wall.id.clone());
                            }
                        }
                    }
                    if conflict
                        || hosts.is_empty()
                        || choices.iter().any(|(o, other): &(Opening, Casing<'_>)| {
                            o.start.distance(c.start) <= epsilon
                                && o.end.distance(c.end) <= epsilon
                                && other.layer == c.layer
                        })
                    {
                        continue;
                    }
                    let proof =
                        BTreeSet::from([p.id.as_str(), c.rails[0], c.rails[1], left, right]);
                    let source_ids = proof.into_iter().map(str::to_owned).collect();
                    choices.push((
                        Opening {
                            id: String::new(),
                            page: page.page_number,
                            kind: OpeningKind::Door,
                            operation: "Hinged".into(),
                            host_wall_id: hosts[0].clone(),
                            host_wall_ids: hosts,
                            start: c.start,
                            end: c.end,
                            hinge: Some(center),
                            swing_observations: vec![SwingObservation {
                                arc_source_id: p.id.clone(),
                                segment_source_ids: vec![],
                                hinge: center,
                                closed_tip: closed,
                                open_tip: open,
                                leaf_tolerance: radius * 0.02,
                                open_leaf_source_ids: vec![],
                                closed_leaf_source_ids: vec![],
                            }],
                            source_ids,
                            evidence: OpeningEvidence::AuthoredExteriorSwingCasing,
                            requires_review: true,
                            physical_jambs: None,
                        },
                        c,
                    ));
                }
            }
        }
        if choices.len() == 1 {
            proposals.push(choices.pop().unwrap());
        }
    }
    // Multiple swings claiming the same throat need review.
    let mut valid = vec![true; proposals.len()];
    for (i, (a, frame)) in proposals.iter().enumerate() {
        for (j, (b, _)) in proposals.iter().enumerate().skip(i + 1) {
            if frame.overlaps(b.start, b.end, 0., frame.width * 0.01)
                && (a.end - a.start).unit().dot((b.end - b.start).unit()).abs() > 0.99
            {
                valid[i] = false;
                valid[j] = false;
            }
        }
    }
    for (i, ((mut o, _), valid)) in proposals
        .into_iter()
        .zip(valid)
        .filter(|(_, valid)| *valid)
        .enumerate()
    {
        debug_assert!(valid);
        o.id = format!("p{}:authored-exterior-door:{}", page.page_number, i + 1);
        page.semantics.routing.push(RoutingItem {
            kind: RoutingKind::DoorPassageCandidate,
            entity_id: o.id.clone(),
            source_ids: o.source_ids.clone(),
            span: Some(Span {
                start: o.start,
                end: o.end,
            }),
            curve: None,
            room_ids: vec![],
            usable_for_routing: false,
            requires_review: true,
        });
        page.openings.push(o);
    }
    Ok(())
}
