//! Layerless overhead gates from two painted stock termini and a signed raised leaf.
use crate::model::{
    BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Shape, Wall, WindowPane,
};
use crate::placement::{
    PlacementExtent, WallDecision, WallReason, WallStatus, effective_wall, wall_layers,
};
use crate::semantics::{RoutingItem, RoutingKind, Span};
use std::{
    borrow::Cow,
    collections::{BTreeMap, BTreeSet, HashMap, HashSet},
};

const EVIDENCE: OpeningEvidence = OpeningEvidence::LayerlessOverheadProjection;
const READY: WallReason = WallReason::SourceBoundedOverheadGateStock;
const MAX_WORK: usize = 4_000_000;
const TERMINAL_PREFIX: &str = "overhead-terminal-stock:";

pub(crate) fn is_terminal_stock(wall: &Wall) -> bool {
    wall.id.starts_with(TERMINAL_PREFIX)
}

fn terminal_gate(o: &Opening) -> bool {
    o.evidence == EVIDENCE
        && o.id
            .starts_with(&format!("p{}:terminal-overhead-door:", o.page))
}

#[derive(Clone, Copy)]
struct Line<'a> {
    a: Point,
    b: Point,
    pen: f64,
    id: &'a str,
}

impl Line<'_> {
    fn along(self) -> [f64; 2] {
        order(self.a.x, self.b.x)
    }

    fn cross(self) -> [f64; 2] {
        order(self.a.y, self.b.y)
    }
}

struct Paint<'a> {
    id: &'a str,
    points: &'a [Point],
}

struct Stock<'a> {
    index: Option<usize>,
    start: Point,
    end: Point,
    cap: Point,
    outward: Point,
    width: f64,
    cap_ink: Line<'a>,
    paint: &'a str,
    faces: [&'a str; 2],
    overlays: [Option<&'a str>; 2],
    terminal_remainder: bool,
}

impl<'a> Stock<'a> {
    fn sources(&self) -> impl Iterator<Item = &'a str> {
        [self.paint, self.cap_ink.id, self.faces[0], self.faces[1]]
            .into_iter()
            .chain(self.overlays.into_iter().flatten())
    }
}

struct Frame {
    start: Point,
    u: Point,
    length: f64,
    width: f64,
    eps: f64,
}

impl Frame {
    fn new(start: Point, end: Point, width: f64) -> Self {
        Self {
            start,
            u: (end - start).unit(),
            length: start.distance(end),
            width,
            // Native PDF paths are stored after separate floating point transforms.
            eps: (width * 0.0001).max(128. * f64::EPSILON * start.norm().max(end.norm())),
        }
    }

    fn local(&self, p: Point) -> Point {
        let d = p - self.start;
        Point::new(d.dot(self.u), self.u.cross(d))
    }

    fn world(&self, x: f64, y: f64) -> Point {
        self.start + self.u * x + Point::new(-self.u.y, self.u.x) * y
    }

    fn line<'a>(&self, l: Line<'a>) -> Line<'a> {
        Line {
            a: self.local(l.a),
            b: self.local(l.b),
            ..l
        }
    }

    fn eq(&self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }

    fn equal_span(&self, a: [f64; 2], b: [f64; 2]) -> bool {
        self.eq(a[0], b[0]) && self.eq(a[1], b[1])
    }

    fn horizontal(&self, l: Line<'_>) -> bool {
        self.eq(l.a.y, l.b.y) && l.along()[1] - l.along()[0] > self.eps
    }

    fn vertical(&self, l: Line<'_>) -> bool {
        self.eq(l.a.x, l.b.x) && l.cross()[1] - l.cross()[0] > self.eps
    }

    fn overlap(&self, a: [f64; 2], b: [f64; 2], points: impl IntoIterator<Item = Point>) -> bool {
        let [x, y] = bounds(points.into_iter().map(|p| self.local(p)));
        x[1].min(a[1]) - x[0].max(a[0]) > self.eps && y[1].min(b[1]) - y[0].max(b[0]) > self.eps
    }
}

fn order(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}

fn bounds(points: impl IntoIterator<Item = Point>) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], p| {
            [
                [x[0].min(p.x), x[1].max(p.x)],
                [y[0].min(p.y), y[1].max(p.y)],
            ]
        })
}

fn charge(work: &mut usize, cost: usize) -> Option<()> {
    *work = work.checked_sub(cost)?;
    Some(())
}

fn native(id: &str) -> bool {
    id.starts_with("pdf:") && id.contains(":path:")
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

fn paint_points(shape: &Shape) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = shape
    else {
        return None;
    };
    let points = if points.len() > 3 && points.first() == points.last() {
        &points[..points.len() - 1]
    } else {
        points.as_slice()
    };
    (points.len() >= 3).then_some(points)
}

fn contains_rectangle(f: &Frame, points: &[Point], along: [f64; 2], cross: [f64; 2]) -> bool {
    if !(4..=8).contains(&points.len()) {
        return false;
    }
    let local = points.iter().map(|&p| f.local(p)).collect::<Vec<_>>();
    let mut edges = local
        .iter()
        .copied()
        .zip(local.iter().copied().cycle().skip(1));
    let area = edges.clone().map(|(a, b)| a.cross(b)).sum::<f64>();
    if area.abs() <= f.eps {
        return false;
    }
    // Prove the source polygon convex before using its half planes as containment.
    edges.all(|(a, b)| {
        let side = |q: Point| (b - a).unit().cross(q - a) * area.signum() >= -f.eps;
        local.iter().copied().all(side)
            && along
                .into_iter()
                .all(|x| cross.into_iter().all(|y| side(Point::new(x, y))))
    })
}

fn stock<'a>(
    page: &PageScan,
    ink: &[Line<'a>],
    by_id: &HashMap<&str, Line<'a>>,
    paints: &[Paint<'a>],
    work: &mut usize,
) -> Option<Vec<Stock<'a>>> {
    let mut out = Vec::new();
    for (index, (w, d)) in page
        .walls
        .iter()
        .zip(page.placement_decisions.as_ref()?)
        .enumerate()
    {
        if d.status != WallStatus::Review
            || !d.opening_ids.is_empty()
            || w.curve.is_some()
            || !matches!(
                w.body,
                BodyEvidence::ParallelFaces | BodyEvidence::FilledBody
            )
            || !w.thickness.is_finite()
            || w.thickness <= 0.
        {
            continue;
        }
        let f = Frame::new(w.start, w.end, w.thickness);
        if !f.length.is_finite() || f.length < 6. * f.width {
            continue;
        }
        let half = 0.5 * f.width;
        let sides = [-half, half];
        let face = |side| {
            w.source_ids
                .iter()
                .filter_map(|id| by_id.get(id.as_str()))
                .copied()
                .find(|&p| {
                    let l = f.line(p);
                    let [a, b] = l.along();
                    f.horizontal(l)
                        && f.eq(l.a.y, side)
                        && a <= f.eps
                        && b >= f.length - f.eps
                        && a >= -f.width
                        && b <= f.length + f.width
                        && l.pen <= 0.22 * f.width
                })
        };
        let [Some(left), Some(right)] = sides.map(face) else {
            continue;
        };
        if path(left.id) == path(right.id) || !f.eq(left.pen, right.pen) {
            continue;
        }
        charge(work, paints.len())?;
        for paint in paints {
            if !(4..=8).contains(&paint.points.len()) {
                continue;
            }
            let [along, cross] = bounds(paint.points.iter().map(|&p| f.local(p)));
            if !f.equal_span(cross, sides)
                || along[0] < -f.width
                || along[1] > f.length + f.width
                || !contains_rectangle(&f, paint.points, [0., f.length], sides)
            {
                continue;
            }
            charge(work, ink.len().saturating_mul(2))?;
            for (x, cap, outwards) in [(0., w.start, -1.), (f.length, w.end, 1.)] {
                if !along.into_iter().any(|v| f.eq(v, x)) {
                    continue;
                }
                let cap_ink = ink.iter().copied().find(|&i| {
                    let line = f.line(i);
                    let [a, b] = line.cross();
                    f.vertical(line)
                        && f.eq(line.a.x, x)
                        && (0.23..=0.50).contains(&(line.pen / f.width))
                        && a <= sides[0] + f.eps
                        && b >= sides[1] - f.eps
                        && (b - a) <= 1.20 * f.width
                        && f.eq((a + b) * 0.5, 0.)
                        && path(line.id) != path(left.id)
                        && path(line.id) != path(right.id)
                });
                if let Some(cap_ink) = cap_ink {
                    out.push(Stock {
                        index: Some(index),
                        start: w.start,
                        end: w.end,
                        cap,
                        outward: f.u * outwards,
                        width: f.width,
                        cap_ink,
                        paint: paint.id,
                        faces: [left.id, right.id],
                        overlays: [None; 2],
                        terminal_remainder: false,
                    });
                }
            }
        }
    }
    Some(out)
}

/// Pair a newly visible paint remnant with the fully witnessed stock from the
/// same paint operation. Only the signed projection may promote these bodies.
fn terminal_stock<'a>(
    page: &'a PageScan,
    ink: &[Line<'a>],
    paints: &[Paint<'a>],
    work: &mut usize,
) -> Option<Vec<Stock<'a>>> {
    let by_primitive = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let mut stocks = Vec::new();
    let mut whole = BTreeSet::new();
    for receipt in &page.terminal_stock {
        let [a, b, c, d] = receipt.outline;
        let f = Frame::new((a + d) * 0.5, (b + c) * 0.5, a.distance(d));
        if !f.width.is_finite()
            || !f.length.is_finite()
            || f.width <= 0.
            || f.length < 2.5 * f.width
        {
            continue;
        }
        let paint = receipt.paint_source_id.as_str();
        let Some(original) = by_primitive.get(paint).and_then(|p| paint_points(&p.shape)) else {
            continue;
        };
        let [full, _] = bounds(original.iter().copied().map(|p| f.local(p)));
        let sides = [-0.5 * f.width, 0.5 * f.width];
        let cap = |x, work: &mut usize| {
            charge(work, ink.len())?;
            let mut matches = ink.iter().copied().filter(|&p| {
                let l = f.line(p);
                let [a, b] = l.cross();
                f.vertical(l)
                    && f.eq(l.a.x, x)
                    && (0.23..=0.50).contains(&(l.pen / f.width))
                    && a <= sides[0] + f.eps
                    && b >= sides[1] - f.eps
                    && b - a <= 1.20 * f.width
                    && f.eq((a + b) * 0.5, 0.)
            });
            let cap = matches.next();
            Some(cap.filter(|_| matches.next().is_none()))
        };
        let (terminal, sign) = match (f.eq(full[0], 0.), f.eq(full[1], f.length)) {
            (true, false) => (0., -1.),
            (false, true) => (f.length, 1.),
            _ => continue,
        };
        let Some(cap_ink) = cap(terminal, work)? else {
            continue;
        };
        stocks.push(Stock {
            index: None,
            start: f.start,
            end: f.world(f.length, 0.),
            cap: f.world(terminal, 0.),
            outward: f.u * sign,
            width: f.width,
            cap_ink,
            paint,
            faces: receipt.face_source_ids.each_ref().map(String::as_str),
            overlays: receipt
                .face_overlay_source_ids
                .each_ref()
                .map(|id| id.as_deref()),
            terminal_remainder: true,
        });
        charge(work, paints.len())?;
        for sibling in paints
            .iter()
            .filter(|p| p.id != paint && path(p.id) == path(paint))
        {
            if whole.contains(sibling.id) {
                continue;
            }
            let [along, cross] = bounds(sibling.points.iter().copied().map(|p| f.local(p)));
            if !f.equal_span(cross, sides) || along[1] - along[0] < 2.5 * f.width {
                continue;
            }
            let (end, toward) = if along[0] >= f.length - f.eps {
                (along[0], 1.)
            } else if along[1] <= f.eps {
                (along[1], -1.)
            } else {
                continue;
            };
            // Both fully visible pens must start at the signed cap. The
            // shorter rail bounds the usable rectangle at a tapered far end.
            charge(work, 2usize.saturating_mul(ink.len()))?;
            let face = |side| {
                let mut candidates = ink.iter().copied().filter_map(|p| {
                    let l = f.line(p);
                    let [a, b] = l.along();
                    let (near, far) = if toward > 0. { (a, b) } else { (b, a) };
                    (f.horizontal(l)
                        && f.eq(l.a.y, side)
                        && f.eq(near, end)
                        && l.pen <= 0.22 * f.width
                        && (far - end).abs() >= 2.5 * f.width
                        && far >= along[0] - f.eps
                        && far <= along[1] + f.eps)
                        .then_some((p, far))
                });
                let candidate = candidates.next();
                candidate.filter(|_| candidates.next().is_none())
            };
            let (Some((lo, x)), Some((hi, y))) = (face(sides[0]), face(sides[1])) else {
                continue;
            };
            let far = if toward > 0. { x.min(y) } else { x.max(y) };
            if path(lo.id) == path(hi.id)
                || !f.eq(lo.pen, hi.pen)
                || !contains_rectangle(&f, sibling.points, order(end, far), sides)
            {
                continue;
            }
            let Some(cap_ink) = cap(end, work)? else {
                continue;
            };
            whole.insert(sibling.id);
            stocks.push(Stock {
                index: None,
                start: f.world(end.min(far), 0.),
                end: f.world(end.max(far), 0.),
                cap: f.world(end, 0.),
                outward: f.u * -toward,
                width: f.width,
                cap_ink,
                paint: sibling.id,
                faces: [lo.id, hi.id],
                overlays: [None; 2],
                terminal_remainder: false,
            });
        }
    }
    Some(stocks)
}

struct Sill<'a> {
    along: [f64; 2],
    cross: [f64; 2],
    center: f64,
    ids: Vec<&'a str>,
}

fn sill<'a>(
    f: &Frame,
    ink: &[Line<'a>],
    paints: &[Paint<'a>],
    work: &mut usize,
) -> Option<Option<Sill<'a>>> {
    let rails = ink
        .iter()
        .copied()
        .filter(|&l| {
            let [a, b] = l.along();
            f.horizontal(l)
                && (0.12..=0.75).contains(&(-a / f.width))
                && f.eq(b - f.length, -a)
                && (0.15..=2.30).contains(&(l.a.y.abs() / f.width))
                && l.pen <= f.width * 0.22
        })
        .collect::<Vec<_>>();
    charge(work, rails.len().saturating_pow(3))?;
    let mut out: Option<Sill<'a>> = None;
    for &mid in &rails {
        if !(0.75..=1.8).contains(&(mid.a.y.abs() / f.width)) {
            continue;
        }
        for &lo in &rails {
            let offset = mid.a.y - lo.a.y;
            if !(0.15..=0.50).contains(&(offset / f.width))
                || !f.equal_span(lo.along(), mid.along())
                || !f.eq(lo.pen, mid.pen)
            {
                continue;
            }
            let Some(hi) = rails.iter().copied().find(|hi| {
                f.eq(hi.a.y, mid.a.y + offset)
                    && f.equal_span(hi.along(), mid.along())
                    && f.eq(hi.pen, mid.pen)
            }) else {
                continue;
            };
            if [lo, hi]
                .into_iter()
                .any(|l| l.a.y.signum() != mid.a.y.signum() || l.a.y.abs() <= 0.5 * f.width + f.eps)
                || [lo.id, mid.id, hi.id]
                    .into_iter()
                    .map(path)
                    .collect::<BTreeSet<_>>()
                    .len()
                    != 3
            {
                continue;
            }
            let along = mid.along();
            charge(work, paints.len().saturating_mul(2))?;
            let filled = |cross: [f64; 2]| {
                paints.iter().find(|p| {
                    let [a, b] = bounds(p.points.iter().map(|&p| f.local(p)));
                    f.equal_span(a, along)
                        && f.equal_span(b, cross)
                        && contains_rectangle(f, p.points, along, cross)
                })
            };
            let (Some(a), Some(b)) = (filled([lo.a.y, mid.a.y]), filled([mid.a.y, hi.a.y])) else {
                continue;
            };
            let proposed = Sill {
                along,
                cross: [lo.a.y, hi.a.y],
                center: mid.a.y,
                ids: vec![lo.id, mid.id, hi.id, a.id, b.id],
            };
            if let Some(prior) = &out {
                if !f.equal_span(prior.cross, proposed.cross) || !f.equal_span(prior.along, along) {
                    return Some(None);
                }
            } else {
                out = Some(proposed);
            }
        }
    }
    Some(out)
}

fn broken_chain<'a>(
    f: &Frame,
    segments: impl Iterator<Item = (Line<'a>, [f64; 2])>,
    span: [f64; 2],
) -> Option<Vec<&'a str>> {
    let width = span[1] - span[0];
    let mut runs = segments
        .filter(|&(_, [a, b])| {
            a >= span[0] - f.eps
                && b <= span[1] + f.eps
                && (0.017..=0.22).contains(&((b - a) / width))
        })
        .collect::<Vec<_>>();
    runs.sort_unstable_by(|(a, p), (b, q)| {
        p[0].total_cmp(&q[0])
            .then(p[1].total_cmp(&q[1]))
            .then(a.id.cmp(b.id))
    });
    runs.dedup_by(|a, b| f.equal_span(a.1, b.1));
    if runs.len() < 4 || !f.eq(runs.first()?.1[0], span[0]) || !f.eq(runs.last()?.1[1], span[1]) {
        return None;
    }
    let coverage = runs.iter().map(|(_, [a, b])| b - a).sum::<f64>() / width;
    if !(0.28..=0.72).contains(&coverage)
        || runs.windows(2).any(|p| {
            let gap = p[1].1[0] - p[0].1[1];
            gap <= f.eps || gap > 0.23 * width
        })
    {
        return None;
    }
    Some(runs.into_iter().map(|(l, _)| l.id).collect())
}

fn projection<'a>(
    f: &Frame,
    ink: &[Line<'a>],
    sill: &Sill<'_>,
    jamb_near: f64,
    work: &mut usize,
) -> Option<Option<Vec<&'a str>>> {
    let side = sill.center.signum();
    let vertical = ink
        .iter()
        .copied()
        .filter(|&l| f.vertical(l) && l.pen <= 0.22 * f.width)
        .collect::<Vec<_>>();
    let mut frames = Vec::new();
    charge(work, vertical.len().saturating_pow(2))?;
    for &left in &vertical {
        let [near, far] = order(left.a.y * side, left.b.y * side);
        if !(0.65..=1.8).contains(&(-left.a.x / f.width))
            || !f.eq(near, jamb_near * side)
            || !(0.70..=1.50).contains(&((far - near) / f.length))
        {
            continue;
        }
        let right = vertical.iter().copied().find(|r| {
            f.eq(r.a.x, f.length - left.a.x)
                && f.equal_span(left.cross(), r.cross())
                && f.eq(left.pen, r.pen)
                && path(left.id) != path(r.id)
        });
        if let Some(r) = right {
            if !frames.iter().any(|&(y, _, _)| f.eq(far * side, y)) {
                frames.push((far * side, left, r));
            }
        }
    }
    let [(far, left_frame, right_frame)] = frames.as_slice() else {
        return Some(None);
    };
    let thin = ink
        .iter()
        .copied()
        .filter(|l| l.pen <= 0.22 * f.width && f.eq(l.pen, left_frame.pen))
        .collect::<Vec<_>>();
    let end_lines = thin
        .iter()
        .copied()
        .filter(|&l| f.horizontal(l) && f.eq(l.a.y, *far))
        .collect::<Vec<_>>();
    let vertical_chain = |at| {
        broken_chain(
            f,
            vertical.iter().copied().filter_map(|l| {
                (f.eq(l.a.x, at) && f.eq(l.pen, left_frame.pen)).then_some((l, l.cross()))
            }),
            order(*far, jamb_near),
        )
    };
    let left_edges = end_lines
        .iter()
        .filter_map(|l| {
            let x = l.along()[0];
            (x < left_frame.a.x - f.eps && x >= -5. * f.width).then_some(x)
        })
        .collect::<Vec<_>>();
    let right_edges = end_lines
        .iter()
        .filter_map(|l| {
            let x = l.along()[1];
            (x > right_frame.a.x + f.eps && x <= f.length + 5. * f.width).then_some(x)
        })
        .collect::<Vec<_>>();
    charge(
        work,
        left_edges
            .len()
            .saturating_mul(right_edges.len())
            .saturating_mul(ink.len())
            .saturating_mul(6),
    )?;
    let mut out = None;
    let mut seen = Vec::new();
    for x in left_edges {
        for &y in &right_edges {
            if seen.iter().any(|&p| f.equal_span(p, [x, y])) {
                continue;
            }
            seen.push([x, y]);
            let side_span = [x, y];
            let far_chain = broken_chain(f, end_lines.iter().map(|&l| (l, l.along())), side_span);
            let (Some(first), Some(last), Some(end)) =
                (vertical_chain(x), vertical_chain(y), far_chain)
            else {
                continue;
            };
            let diagonal = |a: f64, b: f64| {
                let m = (b - a) / (y - x);
                broken_chain(
                    f,
                    thin.iter().copied().filter_map(|l| {
                        [l.a, l.b]
                            .into_iter()
                            .all(|p| f.eq(p.y, a + m * (p.x - x)))
                            .then_some((l, l.along()))
                    }),
                    side_span,
                )
            };
            let (Some(up), Some(down)) = (diagonal(jamb_near, *far), diagonal(*far, jamb_near))
            else {
                continue;
            };
            if out.is_some() {
                return Some(None);
            }
            out = Some(
                [left_frame.id, right_frame.id]
                    .into_iter()
                    .chain(first)
                    .chain(last)
                    .chain(end)
                    .chain(up)
                    .chain(down)
                    .collect(),
            );
        }
    }
    Some(out)
}

fn route(o: &Opening) -> RoutingItem {
    RoutingItem {
        kind: if o.kind == OpeningKind::Door {
            RoutingKind::DoorPassageCandidate
        } else {
            RoutingKind::WindowCandidate
        },
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
    }
}

fn review_route(o: &Opening, r: &RoutingItem) -> bool {
    r.kind == RoutingKind::WindowCandidate
        && r.entity_id == o.id
        && r.source_ids == o.source_ids
        && r.span
            .as_ref()
            .is_some_and(|s| s.start == o.start && s.end == o.end)
        && r.curve.is_none()
        && r.room_ids.is_empty()
        && !r.usable_for_routing
        && r.requires_review
}

fn pane(
    o: &Opening,
    f: &Frame,
    sill: &Sill<'_>,
    by_id: &HashMap<&str, Line<'_>>,
) -> Option<([f64; 2], f64)> {
    if o.kind != OpeningKind::Window
        || o.evidence != OpeningEvidence::FiniteWindowAssembly
        || o.operation != "Fixed"
        || !o.requires_review
        || o.hinge.is_some()
        || !o.swing_observations.is_empty()
        || o.physical_jambs.is_some()
        || o.host_wall_ids.as_slice() != [o.host_wall_id.as_str()]
        || o.source_ids.len() < 4
    {
        return None;
    }
    let [a, b] = [f.local(o.start), f.local(o.end)];
    let [x, y] = order(a.x, b.x);
    let pane_offset = (a.y - sill.center) * sill.center.signum();
    if x <= f.width
        || y >= f.length - f.width
        || !f.eq(a.y, b.y)
        || !(-0.15..=3.0).contains(&(pane_offset / f.width))
        || !(0.09..=0.40).contains(&((y - x) / f.length))
    {
        return None;
    }
    let margin = 0.04 * f.width;
    let mut spanning = false;
    for id in &o.source_ids {
        let l = f.line(*by_id.get(id.as_str())?);
        let [lo, hi] = l.cross();
        let [start, end] = l.along();
        if lo < sill.cross[0] - margin
            || hi > sill.cross[1] + margin
            || start < x - f.eps
            || end > y + f.eps
            || l.pen > 0.22 * f.width
        {
            return None;
        }
        spanning |= f.horizontal(l)
            && f.equal_span([start, end], [x, y])
            && sill.cross.into_iter().any(|edge| f.eq(l.a.y, edge));
    }
    spanning.then_some(([x, y], a.y))
}

fn touches(f: &Frame, extent: [f64; 2], cross: [f64; 2], a: Point, b: Point, inflate: f64) -> bool {
    let [x, y] = bounds([f.local(a), f.local(b)]);
    x[1] + inflate > extent[0] + f.eps
        && x[0] - inflate < extent[1] - f.eps
        && y[1] + inflate >= cross[0] - f.eps
        && y[0] - inflate <= cross[1] + f.eps
}

fn outline(a: Point, b: Point, w: f64) -> [Point; 4] {
    let u = (b - a).unit();
    let n = Point::new(-u.y, u.x) * (w * 0.5);
    [a + n, b + n, b - n, a - n]
}

fn occupied(page: &PageScan) -> Vec<Vec<Point>> {
    let mut out = Vec::new();
    let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if d.status != WallStatus::Ready {
            continue;
        }
        let w = effective_wall(w, Some(d));
        if let Some(c) = w.curve {
            let r = c.radius + w.thickness * 0.5;
            let v = Point::new(r, 0.);
            out.push(outline(c.center - v, c.center + v, 2. * r).to_vec());
        } else {
            out.push(outline(w.start, w.end, w.thickness).to_vec());
        }
    }
    for ply in wall_layers::layers(page) {
        if let Some(w) = walls.get(ply.owner_id) {
            let p = ply.footprint(w);
            out.push(outline(p.start, p.end, p.thickness).to_vec());
        }
    }
    out.extend(
        wall_layers::contacts(page)
            .into_iter()
            .map(|p| p.outline.to_vec()),
    );
    out.extend(
        page.objects
            .iter()
            .filter_map(|o| o.footprint.as_ref())
            .map(|p| p.polygon.to_vec()),
    );
    out.extend(
        page.withheld_walls
            .iter()
            .flatten()
            .filter_map(|w| w.furniture_outline)
            .map(|p| p.to_vec()),
    );
    out
}

struct Proposal<'a> {
    flanks: [&'a Stock<'a>; 2],
    opening: Opening,
    panes: Vec<usize>,
    zone: [Point; 4],
}

fn proof<'a>(
    page: &PageScan,
    pair: [&'a Stock<'a>; 2],
    ink: &[Line<'a>],
    by_id: &HashMap<&str, Line<'a>>,
    paints: &[Paint<'a>],
    occupied: &[Vec<Point>],
    referenced: &HashSet<&str>,
    routes: &HashMap<&str, &RoutingItem>,
    work: &mut usize,
) -> Option<Option<Proposal<'a>>> {
    let [left, right] = pair;
    let f = Frame::new(left.cap, right.cap, left.width);
    if (left.index.is_some() && left.index == right.index)
        || left.paint == right.paint
        || path(left.cap_ink.id) == path(right.cap_ink.id)
        || !(12.0..=100.0).contains(&(f.length / f.width))
        || !f.eq(left.width, right.width)
        || !f.eq(f.u.cross(left.outward) * f.length, 0.)
        || !f.eq(f.u.cross(right.outward) * f.length, 0.)
        || f.u.dot(left.outward) < 0.
        || f.u.dot(right.outward) > 0.
    {
        return Some(None);
    }
    charge(work, ink.len())?;
    let local = ink
        .iter()
        .copied()
        .map(|l| f.line(l))
        .filter(|&l| {
            let [a, b] = l.along();
            let [lo, hi] = l.cross();
            a >= -5. * f.width
                && b <= f.length + 5. * f.width
                && lo >= -1.6 * f.length
                && hi <= 1.6 * f.length
        })
        .collect::<Vec<_>>();
    let Some(sill) = sill(&f, &local, paints, work)? else {
        return Some(None);
    };
    let side = sill.center.signum();
    let jamb_edge = |s: &Stock<'_>| {
        let l = f.line(s.cap_ink);
        (l.a.y * side).max(l.b.y * side) * side
    };
    let near = jamb_edge(left);
    if !f.eq(near, jamb_edge(right)) {
        return Some(None);
    }
    let Some(motif) = projection(&f, &local, &sill, near, work)? else {
        return Some(None);
    };
    let half = 0.5 * f.width;
    let body = [-half, half];
    let span = [0., f.length];
    let surround = [sill.cross[0].min(-half), sill.cross[1].max(half)];
    let from = f.local(left.start).x.min(f.local(left.end).x);
    let to = f.local(right.start).x.max(f.local(right.end).x);
    charge(
        work,
        occupied
            .iter()
            .map(Vec::len)
            .sum::<usize>()
            .saturating_add(paints.len())
            .saturating_add(page.openings.len())
            .saturating_add(page.window_panes.len()),
    )?;
    if occupied.iter().any(|p| {
        f.overlap([from, to], body, p.iter().copied())
            || f.overlap(span, surround, p.iter().copied())
    }) || paints.iter().any(|p| {
        p.id != left.paint
            && p.id != right.paint
            && f.overlap([from, to], body, p.points.iter().copied())
    }) {
        return Some(None);
    }
    let relevant = |o: &Opening| {
        touches(&f, [from, to], body, o.start, o.end, 0.)
            || touches(
                &f,
                span,
                [sill.center - 3. * f.width, sill.center + 3. * f.width],
                o.start,
                o.end,
                0.,
            )
            || o.swing_observations.iter().any(|s| {
                touches(
                    &f,
                    [from, to],
                    surround,
                    s.hinge,
                    s.closed_tip,
                    s.leaf_tolerance,
                )
            })
    };
    if page.window_panes.iter().any(|p| relevant(&p.observation)) {
        return Some(None);
    }
    let mut panes = Vec::new();
    let mut last: Option<(&str, f64, f64)> = None;
    let mut insets: Vec<[f64; 2]> = Vec::new();
    for (index, o) in page
        .openings
        .iter()
        .enumerate()
        .filter(|(_, o)| relevant(o))
    {
        let Some(([a, b], y)) = pane(o, &f, &sill, by_id) else {
            return Some(None);
        };
        if referenced.contains(o.id.as_str())
            || !routes
                .get(o.id.as_str())
                .is_some_and(|r| review_route(o, r))
            || !page
                .walls
                .iter()
                .zip(page.placement_decisions.iter().flatten())
                .any(|(w, d)| {
                    w.id == o.host_wall_id
                        && d.status == WallStatus::Review
                        && w.curve.is_none()
                        && f.eq(f.local(w.start).y, y)
                        && f.eq(f.local(w.end).y, y)
                        && f.local(w.start).x.min(f.local(w.end).x) <= f.eps
                        && f.local(w.start).x.max(f.local(w.end).x) >= f.length - f.eps
                })
        {
            return Some(None);
        }
        if let Some((host, pane_y, pane_width)) = last {
            if host != o.host_wall_id || !f.eq(pane_y, y) || !f.eq(pane_width, b - a) {
                return Some(None);
            }
        } else {
            last = Some((&o.host_wall_id, y, b - a));
        }
        if insets.iter().any(|&[x, z]| b.min(z) - a.max(x) > -f.eps) {
            return Some(None);
        }
        insets.push([a, b]);
        panes.push(index);
    }
    let source_ids = pair
        .iter()
        .flat_map(|s| s.sources())
        .chain(sill.ids)
        .chain(motif)
        .chain(
            panes
                .iter()
                .flat_map(|&i| page.openings[i].source_ids.iter().map(String::as_str)),
        )
        .collect::<BTreeSet<_>>()
        .into_iter()
        .map(str::to_owned)
        .collect();
    let ids = pair
        .iter()
        .filter_map(|s| s.index.map(|i| page.walls[i].id.clone()))
        .collect::<Vec<_>>();
    let opening = Opening {
        id: String::new(),
        page: page.page_number,
        kind: OpeningKind::Door,
        operation: "Overhead".into(),
        host_wall_id: ids.first().cloned().unwrap_or_default(),
        host_wall_ids: ids,
        start: f.world(0., sill.center),
        end: f.world(f.length, sill.center),
        hinge: None,
        swing_observations: vec![],
        source_ids,
        evidence: EVIDENCE,
        requires_review: true,
        physical_jambs: None,
    };
    let zone = [
        f.world(from, surround[0]),
        f.world(to, surround[0]),
        f.world(to, surround[1]),
        f.world(from, surround[1]),
    ];
    Some(Some(Proposal {
        flanks: pair,
        opening,
        panes,
        zone,
    }))
}

struct ResolvedStock {
    index: Option<usize>,
    extent: PlacementExtent,
    thickness: f64,
}

struct Recovery {
    opening: Opening,
    panes: Vec<usize>,
    stock: [ResolvedStock; 2],
}

fn proposals(page: &PageScan, terminal: bool, work: &mut usize) -> Option<Vec<Recovery>> {
    let paint_ids = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    if paint_ids.is_empty() || page.placement_decisions.is_none() {
        return Some(vec![]);
    }
    charge(work, page.primitives.len())?;
    let mut ink = Vec::new();
    let mut paints = Vec::new();
    for p in &page.primitives {
        if p.layer.is_some() || !native(&p.id) {
            continue;
        }
        if paint_ids.contains(p.id.as_str())
            && let Some(points) = paint_points(&p.shape)
        {
            paints.push(Paint { id: &p.id, points });
        }
        if let (Shape::Line { start, end }, Some(pen)) = (&p.shape, p.stroke_width)
            && pen.is_finite()
            && pen > 0.
            && *start != *end
        {
            ink.push(Line {
                a: *start,
                b: *end,
                pen,
                id: &p.id,
            });
        }
    }
    let by_id = ink.iter().map(|&p| (p.id, p)).collect::<HashMap<_, _>>();
    let stock = if terminal {
        terminal_stock(page, &ink, &paints, work)?
    } else {
        stock(page, &ink, &by_id, &paints, work)?
    };
    if stock.len() < 2 {
        return Some(vec![]);
    }
    let occupied = occupied(page);
    let referenced = page
        .edges
        .iter()
        .flat_map(|e| e.opening_ids.iter().map(String::as_str))
        .chain(page.room_connections.iter().map(|c| c.opening_id.as_str()))
        .chain(
            page.semantics
                .room_clusters
                .iter()
                .flat_map(|c| c.opening_ids.iter().map(String::as_str)),
        )
        .collect::<HashSet<_>>();
    let routes = page
        .semantics
        .routing
        .iter()
        .map(|r| (r.entity_id.as_str(), r))
        .collect::<HashMap<_, _>>();
    charge(work, stock.len().saturating_pow(2))?;
    let mut out = Vec::new();
    for a in &stock {
        // Canonical along direction avoids proposing the same gate twice.
        if (if a.outward.x.abs() >= a.outward.y.abs() {
            a.outward.x
        } else {
            a.outward.y
        }) <= 0.
        {
            continue;
        }
        for b in &stock {
            if terminal
                && (!(a.terminal_remainder || b.terminal_remainder)
                    || path(a.paint) != path(b.paint))
            {
                continue;
            }
            if let Some(p) = proof(
                page,
                [a, b],
                &ink,
                &by_id,
                &paints,
                &occupied,
                &referenced,
                &routes,
                work,
            )? {
                out.push(p);
            }
        }
    }
    charge(work, out.len().saturating_pow(2))?;
    let mut conflict = vec![false; out.len()];
    for (i, a) in out.iter().enumerate() {
        let f = Frame::new(a.zone[0], a.zone[1], a.flanks[0].width);
        let cross = [0., f.local(a.zone[3]).y];
        for (j, b) in out.iter().enumerate().skip(i + 1) {
            if a.flanks.iter().any(|s| {
                b.flanks
                    .iter()
                    .any(|r| (s.index.is_some() && s.index == r.index) || s.paint == r.paint)
            }) || a.panes.iter().any(|p| b.panes.contains(p))
                || f.overlap([0., f.length], cross, b.zone)
            {
                conflict[i] = true;
                conflict[j] = true;
            }
        }
    }
    let mut result = out
        .into_iter()
        .zip(conflict)
        .filter(|&(_, conflict)| !conflict)
        .map(|(p, _)| {
            let stock = p.flanks.map(|s| ResolvedStock {
                index: s.index,
                extent: PlacementExtent {
                    start: s.start,
                    end: s.end,
                    thickness: None,
                    source_ids: s.sources().map(str::to_owned).collect(),
                },
                thickness: s.width,
            });
            Recovery {
                opening: p.opening,
                panes: p.panes,
                stock,
            }
        })
        .collect::<Vec<_>>();
    result.sort_unstable_by(|a, b| {
        a.opening
            .start
            .x
            .total_cmp(&b.opening.start.x)
            .then(a.opening.start.y.total_cmp(&b.opening.start.y))
    });
    Some(result)
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    append_projection(page, false)
}

pub(crate) fn append_terminal_stock(page: &mut PageScan) -> Result<(), String> {
    if page.terminal_stock.is_empty() {
        return Ok(());
    }
    append_projection(page, true)
}

fn append_projection(page: &mut PageScan, terminal: bool) -> Result<(), String> {
    let mut work = MAX_WORK;
    let Some(recovered) = proposals(page, terminal, &mut work) else {
        return Ok(());
    };
    if recovered.is_empty() {
        return Ok(());
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("overhead stock requires placement decisions")?;
    let mut panes = BTreeMap::new();
    let mut openings = Vec::new();
    let prefix = if terminal {
        "terminal-overhead-door"
    } else {
        "layerless-overhead-door"
    };
    let number = page.page_number;
    for (
        i,
        Recovery {
            mut opening,
            panes: indexes,
            stock,
        },
    ) in recovered.into_iter().enumerate()
    {
        let ordinal = i + 1;
        opening.id = format!("p{number}:{prefix}:{ordinal}");
        for (
            side,
            ResolvedStock {
                index,
                extent,
                thickness,
            },
        ) in stock.into_iter().enumerate()
        {
            if let Some(index) = index {
                let d = &mut decisions[index];
                d.status = WallStatus::Ready;
                d.reason = READY;
                d.support_source_ids = extent.source_ids.clone();
                d.opening_ids = vec![opening.id.clone()];
                d.placement_extent = Some(extent);
                continue;
            }
            let id = format!("{TERMINAL_PREFIX}{number}:{ordinal}:{}", side + 1);
            let wall = Wall {
                id: id.clone(),
                page: number,
                start: extent.start,
                end: extent.end,
                thickness,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: extent.source_ids.clone(),
                requires_review: false,
            };
            page.semantics
                .routing
                .push(crate::semantics::wall_route(&wall));
            page.walls.push(wall);
            decisions.push(WallDecision {
                wall_id: id.clone(),
                status: WallStatus::Ready,
                reason: READY,
                component_id: format!("p{number}:{prefix}-stock:{ordinal}:{}", side + 1),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![],
                support_source_ids: extent.source_ids,
                opening_ids: vec![opening.id.clone()],
                room_ids: vec![],
                placement_extent: None,
            });
            opening.host_wall_ids.push(id);
        }
        opening.host_wall_id = opening
            .host_wall_ids
            .first()
            .cloned()
            .ok_or("missing overhead gate host")?;
        for index in indexes {
            panes.insert(index, opening.id.clone());
        }
        openings.push(opening);
    }
    let mut removed = HashSet::new();
    for (opening_index, observation) in std::mem::take(&mut page.openings).into_iter().enumerate() {
        if let Some(frame_opening_id) = panes.remove(&opening_index) {
            removed.insert(observation.id.clone());
            page.window_panes.push(WindowPane {
                frame_opening_id,
                opening_index,
                observation,
            });
        } else {
            page.openings.push(observation);
        }
    }
    page.semantics
        .routing
        .retain(|r| !removed.contains(&r.entity_id));
    page.semantics.routing.extend(openings.iter().map(route));
    page.openings.extend(openings);
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    before_projection(page, |o| o.evidence == EVIDENCE && !terminal_gate(o))
}

pub(crate) fn before_terminal_recovery(
    page: &PageScan,
    supported: bool,
) -> Result<Cow<'_, PageScan>, String> {
    let first = page
        .walls
        .iter()
        .position(is_terminal_stock)
        .unwrap_or(page.walls.len());
    if !supported {
        if first < page.walls.len()
            || !page.terminal_stock.is_empty()
            || page.opening_evidence().any(terminal_gate)
        {
            return Err("terminal overhead stock requires scan v40 or newer".into());
        }
        return Ok(Cow::Borrowed(page));
    }
    let mut base = before_projection(page, terminal_gate)?;
    if first < page.walls.len() {
        let ids: HashSet<_> = page.walls[first..].iter().map(|w| w.id.as_str()).collect();
        let base = base.to_mut();
        base.walls.truncate(first);
        base.semantics
            .routing
            .retain(|r| !ids.contains(r.entity_id.as_str()));
        base.placement_decisions
            .as_mut()
            .ok_or("missing terminal overhead stock decisions")?
            .truncate(first);
    }
    Ok(base)
}

fn before_projection(
    page: &PageScan,
    projected: impl Fn(&Opening) -> bool,
) -> Result<Cow<'_, PageScan>, String> {
    let gates = page
        .openings
        .iter()
        .filter(|o| projected(o))
        .map(|o| o.id.as_str())
        .collect::<HashSet<_>>();
    if gates.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut restored = BTreeMap::new();
    for p in page
        .window_panes
        .iter()
        .filter(|p| gates.contains(p.frame_opening_id.as_str()))
    {
        if p.observation.kind != OpeningKind::Window
            || p.observation.evidence != OpeningEvidence::FiniteWindowAssembly
            || restored.insert(p.opening_index, &p.observation).is_some()
        {
            return Err("invalid retained overhead glazing pane".into());
        }
    }
    let mut base = page.clone();
    base.openings.retain(|o| !projected(o));
    base.window_panes
        .retain(|p| !gates.contains(p.frame_opening_id.as_str()));
    base.semantics
        .routing
        .retain(|r| !gates.contains(r.entity_id.as_str()));
    for (i, p) in restored {
        if i > base.openings.len() {
            return Err("invalid retained overhead glazing pane order".into());
        }
        base.openings.insert(i, p.clone());
        base.semantics.routing.push(route(p));
    }
    Ok(Cow::Owned(base))
}
