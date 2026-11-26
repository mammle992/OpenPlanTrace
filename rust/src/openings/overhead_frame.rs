//! Overhead doors from authored labels, matching frames, and finite exterior stock.
use crate::model::{
    BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape,
};
use crate::placement::{WallStatus, effective_wall, painted_rectangle, wall_layers};
use crate::semantics::{RoutingItem, RoutingKind, Span};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 12_000_000;

struct OverheadProof {
    start: Point,
    end: Point,
    throat: [Point; 4],
    host_wall_ids: [String; 2],
    source_ids: Vec<String>,
}

struct Stock<'a> {
    host: &'a str,
    layer: &'a str,
    paint: &'a str,
    a: Point,
    b: Point,
    width: f64,
}

impl Stock<'_> {
    fn ends(&self) -> [(Point, Point); 2] {
        let direction = (self.b - self.a).unit();
        [(self.a, direction * -1.), (self.b, direction)]
    }
}

struct Frame {
    a: Point,
    axis: Point,
    length: f64,
    width: f64,
    eps: f64,
}

impl Frame {
    fn new(a: Point, b: Point, width: f64) -> Self {
        let length = a.distance(b);
        Self {
            a,
            axis: (b - a).unit(),
            length,
            width,
            eps: 128. * f64::EPSILON * a.norm().max(b.norm()).max(length).max(1.),
        }
    }

    fn project(&self, p: Point) -> Point {
        let delta = p - self.a;
        Point::new(delta.dot(self.axis), self.axis.cross(delta))
    }

    fn equal(&self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }

    fn label_aligned(&self, p: Point) -> bool {
        (0.25 * self.length..=0.75 * self.length).contains(&p.x) && p.y.abs() <= 5. * self.width
    }
}

#[derive(Clone, Copy)]
struct Line<'a> {
    id: &'a str,
    at: f64,
    span: [f64; 2],
    ink: bool,
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

fn port_label(value: &str) -> bool {
    matches!(
        value.trim().to_lowercase().as_str(),
        "port" | "garasjeport" | "rulleport" | "garage door" | "overhead door" | "sectional door"
    )
}

fn size_label(value: &str) -> bool {
    let Some((w, h)) = value.split_once(['x', 'X', '×']) else {
        return false;
    };
    [w, h].into_iter().all(|part| {
        part.trim()
            .replace(',', ".")
            .parse::<f64>()
            .is_ok_and(|n| n.is_finite() && n > 0.)
    })
}

fn lines<'a>(frame: &Frame, primitives: &[&'a Primitive]) -> [Vec<Line<'a>>; 2] {
    let mut lines = [Vec::new(), Vec::new()];
    for p in primitives {
        let Shape::Line { start, end } = p.shape else {
            continue;
        };
        let [a, b] = [start, end].map(|p| frame.project(p));
        if a.x.min(b.x) < -frame.eps
            || a.x.max(b.x) > frame.length + frame.eps
            || a.y.abs().max(b.y.abs()) > frame.width * 1.75
        {
            continue;
        }
        let ink = p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.);
        let id = p.id.as_str();
        if frame.equal(a.y, b.y) && !frame.equal(a.x, b.x) {
            lines[0].push(Line {
                id,
                at: a.y,
                span: [a.x.min(b.x), a.x.max(b.x)],
                ink,
            });
        }
        if frame.equal(a.x, b.x) && !frame.equal(a.y, b.y) {
            lines[1].push(Line {
                id,
                at: a.x,
                span: [a.y.min(b.y), a.y.max(b.y)],
                ink,
            });
        }
    }
    lines
}

fn exact<'a>(
    frame: &Frame,
    lines: &[Line<'a>],
    at: f64,
    span: [f64; 2],
    ink: bool,
) -> Option<Line<'a>> {
    lines.iter().copied().find(|p| {
        (!ink || p.ink)
            && frame.equal(p.at, at)
            && frame.equal(p.span[0], span[0])
            && frame.equal(p.span[1], span[1])
    })
}

fn complete_jamb<'a>(
    frame: &Frame,
    depth: f64,
    at: f64,
    lines: &[Line<'a>],
) -> Option<Vec<&'a str>> {
    let half = depth * 0.5;
    let mut stamps = lines
        .iter()
        .filter(|p| p.ink && frame.equal(p.at, at))
        .collect::<Vec<_>>();
    stamps.sort_unstable_by(|a, b| {
        a.span[0]
            .total_cmp(&b.span[0])
            .then(b.span[1].total_cmp(&a.span[1]))
            .then(a.id.cmp(b.id))
    });
    let mut to = -half;
    let mut witness = Vec::new();
    for s in stamps {
        if s.span[0] > to + frame.eps {
            break;
        }
        if s.span[1] > to + frame.eps {
            to = s.span[1];
            witness.push(s.id);
        }
        if to >= half - frame.eps {
            return Some(witness);
        }
    }
    None
}

fn casing_return<'a>(
    frame: &Frame,
    lines: &[Vec<Line<'a>>; 2],
    at: f64,
    inset: f64,
    panel: [f64; 2],
) -> Option<([f64; 2], Vec<&'a str>)> {
    let [rails, jambs] = lines;
    let span = [at.min(inset), at.max(inset)];
    let mut matches = Vec::new();
    for outer in jambs.iter().filter(|p| {
        p.ink
            && frame.equal(p.at, at)
            && p.span[0] <= panel[0] + frame.eps
            && p.span[1] >= panel[1] - frame.eps
            && p.span[1] - p.span[0] <= frame.width * 1.4
    }) {
        let (Some(inner), Some(low), Some(high)) = (
            exact(frame, jambs, inset, outer.span, true),
            exact(frame, rails, outer.span[0], span, true),
            exact(frame, rails, outer.span[1], span, true),
        ) else {
            continue;
        };
        let ids = [outer.id, inner.id, low.id, high.id];
        if ids.into_iter().map(path).collect::<BTreeSet<_>>().len() == 4
            && !matches
                .iter()
                .any(|(edges, _): &([f64; 2], Vec<&str>)| edges == &outer.span)
        {
            matches.push((outer.span, ids.to_vec()));
        }
    }
    (matches.len() == 1).then(|| matches.pop().expect("one proved return"))
}

fn panel<'a>(
    frame: &Frame,
    lines: &[Vec<Line<'a>>; 2],
    work: &mut usize,
) -> Option<Option<Vec<&'a str>>> {
    let [rails, caps] = lines;
    let long = rails
        .iter()
        .copied()
        .filter(|p| {
            p.span[0] > frame.eps
                && p.span[1] < frame.length - frame.eps
                && p.span[1] - p.span[0] >= frame.length * 0.8
                && (p.span[0] - (frame.length - p.span[1])).abs() <= frame.width * 0.02
        })
        .collect::<Vec<_>>();
    *work = work.checked_sub(
        long.len()
            .saturating_pow(2)
            .saturating_mul((rails.len() + caps.len()).saturating_pow(2)),
    )?;
    let mut result = None;
    let mut seen = BTreeSet::new();
    for (i, a) in long.iter().enumerate() {
        for b in &long[i + 1..] {
            if path(a.id) == path(b.id)
                || !frame.equal(a.span[0], b.span[0])
                || !frame.equal(a.span[1], b.span[1])
                || a.at * b.at <= 0.
            {
                continue;
            }
            let cross = [a.at.min(b.at), a.at.max(b.at)];
            if !(frame.width * 0.1..=frame.width * 0.7).contains(&(cross[1] - cross[0])) {
                continue;
            }
            let Some(left_cap) = exact(frame, caps, a.span[0], cross, false) else {
                continue;
            };
            let Some(right_cap) = exact(frame, caps, a.span[1], cross, false) else {
                continue;
            };
            if [a.id, b.id, left_cap.id, right_cap.id]
                .into_iter()
                .map(path)
                .collect::<BTreeSet<_>>()
                .len()
                != 4
            {
                continue;
            }
            let Some((u, left)) = casing_return(frame, lines, 0., a.span[0], cross) else {
                continue;
            };
            let Some((v, right)) = casing_return(frame, lines, frame.length, a.span[1], cross)
            else {
                continue;
            };
            if !frame.equal(u[0], v[0]) || !frame.equal(u[1], v[1]) {
                continue;
            }
            let signature = [a.span[0], a.span[1], cross[0], cross[1]].map(f64::to_bits);
            if !seen.insert(signature) {
                continue;
            }
            if result.is_some() {
                return Some(None);
            }
            result = Some(
                [a.id, b.id, left_cap.id, right_cap.id]
                    .into_iter()
                    .chain(left)
                    .chain(right)
                    .collect(),
            );
        }
    }
    Some(result)
}

fn rect(a: Point, b: Point, width: f64) -> Option<[Point; 4]> {
    if !a.finite() || !b.finite() || a == b || width <= 0. || !width.is_finite() {
        return None;
    }
    let d = (b - a).unit();
    let n = Point::new(-d.y, d.x) * (0.5 * width);
    Some([a + n, b + n, b - n, a - n])
}

fn overlap(a: [Point; 4], b: [Point; 4], eps: f64) -> bool {
    [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]]
        .into_iter()
        .all(|d| {
            let dir = d.unit();
            let range = |p: [Point; 4]| {
                p.into_iter()
                    .map(|p| (p - a[0]).dot(dir))
                    .fold([f64::INFINITY, f64::NEG_INFINITY], |[u, v], x| {
                        [u.min(x), v.max(x)]
                    })
            };
            let [x, y] = range(a);
            let [u, v] = range(b);
            y.min(v) - x.max(u) > eps
        })
}

fn proofs(page: &PageScan, work: &mut usize) -> Option<Vec<OverheadProof>> {
    let mut layers = HashMap::<&str, Vec<&Primitive>>::new();
    let mut labels = Vec::new();
    *work = work.checked_sub(page.primitives.len())?;
    for p in &page.primitives {
        let Shape::Text { at, ref value, .. } = p.shape else {
            continue;
        };
        if p.id.starts_with("pdf:")
            && port_label(value)
            && let Some(layer) = crate::layers::named_exterior_wall(p)
        {
            labels.push((p, layer, at));
            layers.entry(layer).or_default();
        }
    }
    if labels.is_empty() {
        return Some(Vec::new());
    }
    *work = work.checked_sub(page.primitives.len())?;
    let mut native = HashMap::new();
    for p in &page.primitives {
        if p.id.starts_with("pdf:")
            && let Some(group) = p.layer.as_deref().and_then(|layer| layers.get_mut(layer))
            && crate::layers::named_exterior_wall(p).is_some()
        {
            group.push(p);
            native.insert(p.id.as_str(), p);
        }
    }
    let visible: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let mut occupied = Vec::new();
    let mut stock = Vec::new();
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if d.status != WallStatus::Ready {
            continue;
        }
        let f = effective_wall(w, Some(d));
        if let Some(c) = f.curve {
            let reach = c.radius + f.thickness * 0.5;
            let v = Point::new(reach, 0.);
            occupied.extend(rect(c.center - v, c.center + v, 2. * reach));
            continue;
        }
        let Some(outline) = rect(f.start, f.end, f.thickness) else {
            continue;
        };
        occupied.push(outline);
        let [id] = w.source_ids.as_slice() else {
            continue;
        };
        if f.body != BodyEvidence::FilledBody || !visible.contains(id.as_str()) {
            continue;
        }
        let Some(p) = native.get(id.as_str()) else {
            continue;
        };
        let Some(layer) = crate::layers::named_exterior_wall(p) else {
            continue;
        };
        let Some(paint) = painted_rectangle(&p.shape) else {
            continue;
        };
        if !outline
            .into_iter()
            .all(|v| paint.into_iter().any(|u| u.distance(v) <= 1e-7))
        {
            continue;
        }
        stock.push(Stock {
            host: &w.id,
            layer,
            paint: id,
            a: f.start,
            b: f.end,
            width: f.thickness,
        });
    }
    for layer in wall_layers::layers(page) {
        if let Some(owner) = walls.get(layer.owner_id) {
            let f = layer.footprint(owner);
            occupied.extend(rect(f.start, f.end, f.thickness));
        }
    }
    occupied.extend(wall_layers::contacts(page).into_iter().map(|j| j.outline));
    for o in page.opening_evidence() {
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|i| walls.get(i.as_str()))
            .map(|w| w.thickness)
            .fold(1e-5_f64, f64::max);
        occupied.extend(rect(o.start, o.end, width));
        occupied.extend(
            o.swing_observations
                .iter()
                .filter_map(|s| rect(s.hinge, s.closed_tip, 2. * s.leaf_tolerance)),
        );
    }
    let mut proposals = Vec::new();
    for (label, layer, label_at) in labels {
        let primitives = &layers[layer];
        let nearby = stock
            .iter()
            .filter(|s| s.layer == layer)
            .collect::<Vec<_>>();
        *work = work.checked_sub(nearby.len().saturating_pow(2).saturating_mul(4))?;
        let mut candidates = Vec::new();
        for (i, p) in nearby.iter().enumerate() {
            for q in &nearby[i + 1..] {
                if p.paint == q.paint || (p.width - q.width).abs() > p.width.min(q.width) * 0.10 {
                    continue;
                }
                for (a, out) in p.ends() {
                    for (b, into) in q.ends() {
                        let (a, b, out, into, p, q) = if (a.x, a.y) < (b.x, b.y) {
                            (a, b, out, into, *p, *q)
                        } else {
                            (b, a, into, out, *q, *p)
                        };
                        let frame = Frame::new(a, b, p.width.max(q.width));
                        if !frame.length.is_finite()
                            || frame.length < 5. * frame.width
                            || frame.axis.cross(out).abs() * frame.length > frame.eps
                            || frame.axis.cross(into).abs() * frame.length > frame.eps
                            || frame.axis.dot(out) <= 0.
                            || frame.axis.dot(into) >= 0.
                        {
                            continue;
                        }
                        let annotation = frame.project(label_at);
                        if !frame.label_aligned(annotation) {
                            continue;
                        }
                        *work = work.checked_sub(primitives.len().saturating_mul(2))?;
                        let mut sizes = primitives.iter().filter(|p| {
                            let Shape::Text { at, ref value, .. } = p.shape else {
                                return false;
                            };
                            let u = frame.project(at);
                            size_label(value)
                                && frame.label_aligned(u)
                                && u.y * annotation.y > 0.
                                && u.y.abs() < annotation.y.abs()
                                && (u.x - annotation.x).abs() <= frame.width * 2.
                        });
                        let Some(size) = sizes.next() else { continue };
                        if sizes.next().is_some() {
                            continue;
                        }
                        let local = lines(&frame, primitives);
                        let [_, jambs] = &local;
                        *work = work.checked_sub(
                            jambs
                                .len()
                                .saturating_pow(2)
                                .saturating_mul(2)
                                .saturating_add(occupied.len()),
                        )?;
                        let (Some(left), Some(right)) = (
                            complete_jamb(&frame, p.width, 0., jambs),
                            complete_jamb(&frame, q.width, frame.length, jambs),
                        ) else {
                            continue;
                        };
                        let Some(panel) = panel(&frame, &local, work)? else {
                            continue;
                        };
                        let throat = rect(a, b, frame.width)?;
                        if occupied.iter().any(|&s| overlap(throat, s, frame.eps)) {
                            continue;
                        }
                        let source_ids = [label.id.as_str(), size.id.as_str(), p.paint, q.paint]
                            .into_iter()
                            .chain(left)
                            .chain(right)
                            .chain(panel)
                            .collect::<BTreeSet<_>>()
                            .into_iter()
                            .map(str::to_owned)
                            .collect();
                        candidates.push(OverheadProof {
                            start: a,
                            end: b,
                            throat,
                            host_wall_ids: [p.host.to_owned(), q.host.to_owned()],
                            source_ids,
                        });
                    }
                }
            }
        }
        if candidates.len() == 1 {
            proposals.append(&mut candidates);
        }
    }
    *work = work.checked_sub(proposals.len().saturating_pow(2))?;
    let mut ambiguous = vec![false; proposals.len()];
    for (i, a) in proposals.iter().enumerate() {
        for (j, b) in proposals.iter().enumerate().skip(i + 1) {
            if a.host_wall_ids == b.host_wall_ids
                || a.host_wall_ids.iter().any(|w| b.host_wall_ids.contains(w))
                || overlap(a.throat, b.throat, 1e-7)
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    let mut out = proposals
        .into_iter()
        .zip(ambiguous)
        .filter_map(|(p, no)| (!no).then_some(p))
        .collect::<Vec<_>>();
    out.sort_unstable_by(|a, b| {
        a.start
            .x
            .total_cmp(&b.start.x)
            .then(a.start.y.total_cmp(&b.start.y))
    });
    Some(out)
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let mut work = MAX_WORK;
    let Some(proposals) = proofs(page, &mut work) else {
        return Ok(());
    };
    for (i, p) in proposals.into_iter().enumerate() {
        let opening = Opening {
            id: format!(
                "p{}:authored-exterior-overhead-door:{}",
                page.page_number,
                i + 1
            ),
            page: page.page_number,
            kind: OpeningKind::Door,
            operation: "Overhead".into(),
            host_wall_id: p.host_wall_ids[0].clone(),
            host_wall_ids: p.host_wall_ids.into(),
            start: p.start,
            end: p.end,
            hinge: None,
            swing_observations: Vec::new(),
            source_ids: p.source_ids,
            evidence: OpeningEvidence::AuthoredExteriorOverheadFrame,
            requires_review: true,
            physical_jambs: None,
        };
        page.semantics.routing.push(RoutingItem {
            kind: RoutingKind::DoorPassageCandidate,
            entity_id: opening.id.clone(),
            source_ids: opening.source_ids.clone(),
            span: Some(Span {
                start: opening.start,
                end: opening.end,
            }),
            curve: None,
            room_ids: Vec::new(),
            usable_for_routing: false,
            requires_review: true,
        });
        page.openings.push(opening);
    }
    Ok(())
}
