//! An exposed authored wall end behind a white mask, with a measured side branch.
use super::{Cover, Event, Key, Mask, Pen, Point, Rect, capped_stock, precision, recessed_stock};
use crate::model::{PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};

const MAX_RECEIPTS: usize = 24;
const MAX_MASKS: usize = 12;
const MAX_GRAY: usize = 16;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalPen {
    pub source_id: String,
    pub width: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MaskProof {
    pub source_id: String,
    pub outline: [Point; 4],
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub face_pens: [LocalPen; 2],
    pub backing_pens: [LocalPen; 2],
    pub authored_cap: LocalPen,
    pub opaque_masks: Vec<MaskProof>,
    pub neutral_overlays: Vec<LocalPen>,
    pub adjacent_paint_source_id: String,
    pub stem_paint_source_id: String,
    pub inside_turn: LocalPen,
    pub outside_turn: LocalPen,
}

pub(crate) struct Verified {
    /// First and last corners are the mask boundary, never a structural cap.
    pub outline: [Point; 4],
    /// From the old stem's axis endpoint to the new owner's axis.
    pub stem_path: [Point; 2],
    /// From the new owner's true cap center to the old adjacent cap center.
    pub cap_path: [Point; 2],
    pub joint_source_ids: Vec<String>,
}

type Sources<'a> = HashMap<&'a str, &'a Primitive>;

fn sorted(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}

fn range(points: impl IntoIterator<Item = Point>) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], p| {
            [
                [x[0].min(p.x), x[1].max(p.x)],
                [y[0].min(p.y), y[1].max(p.y)],
            ]
        })
}

fn length_intersection([a, b]: [f64; 2], [c, d]: [f64; 2]) -> f64 {
    (b.min(d) - a.max(c)).max(0.)
}

fn unique<T>(mut values: impl Iterator<Item = T>) -> Option<T> {
    let first = values.next()?;
    values.next().is_none().then_some(first)
}

fn draw_path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(root, _)| root)
}

fn filled(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    (p.layer.is_none() && points.iter().all(|p| p.finite())).then_some(points)
}

fn source_pen(p: &Primitive, key: Key, width: f64, gray: f64) -> Option<Pen> {
    let Shape::Line { start, end } = p.shape else {
        return None;
    };
    (p.layer.is_none()
        && start.finite()
        && end.finite()
        && start != end
        && width.is_finite()
        && width > 0.
        && p.stroke_width
            .is_none_or(|w| (w - width).abs() <= 1e-6 * width))
    .then_some(Pen {
        key,
        points: [start, end],
        width,
        gray,
        layerless: true,
    })
}

#[derive(Clone, Copy)]
struct Frame {
    origin: Point,
    along: Point,
    across: Point,
    length: f64,
    bevel: f64,
    width: f64,
    eps: f64,
}

impl Frame {
    fn new(points: &[Point]) -> Option<Self> {
        if !(4..=12).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
            return None;
        }
        let mut ring = points.to_vec();
        if ring.first() == ring.last() {
            ring.pop();
        }
        let math = precision(&ring);
        let c = ring
            .iter()
            .copied()
            .enumerate()
            .filter_map(|(i, b)| {
                let a = ring[(i + ring.len() - 1) % ring.len()];
                let d = ring[(i + 1) % ring.len()];
                (b != a
                    && b != d
                    && ((b - a).unit().cross((d - b).unit()).abs() > math
                        || (b - a).dot(d - b) < 0.))
                    .then_some(b)
            })
            .collect::<Vec<_>>();
        let c: [Point; 4] = c.try_into().ok()?;
        let (a, b) = (0..4)
            .map(|i| (c[i], c[(i + 1) % 4]))
            .max_by(|(a, b), (x, y)| a.distance(*b).total_cmp(&x.distance(*y)))?;
        let first = (b - a).unit();
        let length = a.distance(b);
        for (origin, along) in [(a, first), (b, first * -1.)] {
            let side = c
                .iter()
                .map(|&p| along.cross(p - origin))
                .find(|d| d.abs() > math)?
                .signum();
            let across = Point::new(-along.y, along.x) * side;
            let local = c.map(|p| Point::new((p - origin).dot(along), (p - origin).dot(across)));
            let [[_, _], [_, width]] = range(local);
            let ends = local
                .iter()
                .filter(|p| (p.y - width).abs() <= math)
                .map(|p| p.x)
                .collect::<Vec<_>>();
            let [x, y] = ends.as_slice() else {
                continue;
            };
            let [bevel, end] = sorted(*x, *y);
            if width <= math
                || length < 24. * width
                || !(0.008..=0.25).contains(&(bevel / width))
                || (end - length).abs() > math
            {
                continue;
            }
            let expected = [
                Point::new(0., 0.),
                Point::new(length, 0.),
                Point::new(length, width),
                Point::new(bevel, width),
            ];
            let turn = (0..4)
                .map(|i| {
                    (local[(i + 1) % 4] - local[i]).cross(local[(i + 2) % 4] - local[(i + 1) % 4])
                })
                .collect::<Vec<_>>();
            if expected
                .iter()
                .all(|a| local.iter().any(|p| a.distance(*p) <= math))
                && (turn.iter().all(|x| *x > math) || turn.iter().all(|x| *x < -math))
            {
                return Some(Self {
                    origin,
                    along,
                    across,
                    length,
                    bevel,
                    width,
                    eps: math.max(width * 0.0001),
                });
            }
        }
        None
    }

    fn local(self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(d.dot(self.along), d.dot(self.across))
    }
    fn world(self, x: f64, y: f64) -> Point {
        self.origin + self.along * x + self.across * y
    }
    fn eq(self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }
    fn pass(self, p: Pen) -> [[f64; 2]; 2] {
        range(p.points.map(|p| self.local(p)))
    }
    fn rail(self, p: Pen, at: f64) -> Option<[f64; 2]> {
        let [x, [a, b]] = self.pass(p);
        (self.eq(a, at) && self.eq(b, at)).then_some(x)
    }
    fn outline(self, cut: f64) -> [Point; 4] {
        [
            self.world(cut, self.width),
            self.world(self.length, self.width),
            self.world(self.length, 0.),
            self.world(cut, 0.),
        ]
    }
}

pub(super) fn eligible(points: &[Point]) -> bool {
    Frame::new(points).is_some()
}

#[derive(Clone, Copy)]
struct Pens {
    thin: [Pen; 2],
    thick: [Pen; 2],
    cap: Pen,
}

impl Pens {
    fn verify(self, f: Frame) -> Option<[f64; 2]> {
        let [top, bottom] = self.thin;
        let [back_top, back_bottom] = self.thick;
        let [[cap_lo, cap_hi], [outside_lo, outside_hi]] = f.pass(self.cap);
        let edge = f.width + outside_lo;
        if !f.eq(cap_lo, f.length)
            || !f.eq(cap_hi, f.length)
            || !f.eq(edge + outside_hi, 2. * f.width)
            || !(0.015..=0.08).contains(&(-outside_lo / f.width))
            || !(0.025..=0.22).contains(&(top.width / f.width))
            || !(0.23..=0.48).contains(&(self.cap.width / f.width))
            || !f.eq(top.width, bottom.width)
            || top.width > 0.45 * self.cap.width
            || !f.eq(back_top.width, self.cap.width)
            || !f.eq(back_bottom.width, self.cap.width)
        {
            return None;
        }
        let starts = [0., f.bevel];
        for (i, at) in [0., f.width].into_iter().enumerate() {
            let [a, b] = f.rail(self.thin[i], at)?;
            let [c, d] = f.rail(self.thick[i], [outside_lo, outside_hi][i])?;
            if !f.eq(a, starts[i])
                || !f.eq(b, f.length)
                || !f.eq(d, f.length)
                || (c - a).abs() > 0.025 * f.width
            {
                return None;
            }
        }
        (self
            .all()
            .into_iter()
            .map(|p| p.key.0)
            .collect::<HashSet<_>>()
            .len()
            == 5)
            .then_some([outside_lo, outside_hi])
    }

    fn all(self) -> [Pen; 5] {
        [
            self.thin[0],
            self.thin[1],
            self.thick[0],
            self.thick[1],
            self.cap,
        ]
    }

    fn find(f: Frame, ink: &[Pen]) -> Option<Self> {
        let black = |p: Pen| p.gray == 0.;
        let cap = unique(ink.iter().copied().filter(|&p| {
            let [[a, b], [lo, hi]] = f.pass(p);
            black(p)
                && f.eq(a, f.length)
                && f.eq(b, f.length)
                && (0.015..=0.08).contains(&(-lo / f.width))
                && f.eq(hi, f.width - lo)
                && (0.23..=0.48).contains(&(p.width / f.width))
        }))?;
        let [_x, back] = f.pass(cap);
        let rail = |at: f64, start: f64, thick: bool| {
            unique(ink.iter().copied().filter(|&p| {
                black(p)
                    && (if thick {
                        f.eq(p.width, cap.width)
                    } else {
                        (0.025..=0.22).contains(&(p.width / f.width))
                    })
                    && f.rail(p, at).is_some_and(|[a, b]| {
                        f.eq(b, f.length)
                            && if thick {
                                (a - start).abs() <= 0.025 * f.width
                            } else {
                                f.eq(a, start)
                            }
                    })
            }))
        };
        let pens = Self {
            thin: [rail(0., 0., false)?, rail(f.width, f.bevel, false)?],
            thick: [rail(back[0], 0., true)?, rail(back[1], f.bevel, true)?],
            cap,
        };
        pens.verify(f)?;
        Some(pens)
    }
}

fn strip_masks(f: Frame, pens: Pens, masks: &[Mask]) -> Option<f64> {
    if masks.is_empty() || masks.len() > MAX_MASKS {
        return None;
    }
    let [low, high] = pens.verify(f)?;
    let half = pens.cap.width * 0.5;
    let last_pen = pens.all().into_iter().map(|p| p.key.0).max()?;
    let mut end = f.bevel;
    for m in masks {
        let points = m.outline.map(|p| f.local(p));
        let [[a, b], [lo, hi]] = range(points);
        if m.key.0 <= last_pen
            || a < end - f.eps
            || b - a <= f.eps
            || f.length - b < 8. * f.width
            || lo >= low - half - f.eps
            || hi <= high + half + f.eps
            || ![a, b].into_iter().all(|x| {
                [lo, hi]
                    .into_iter()
                    .all(|y| points.iter().any(|p| p.distance(Point::new(x, y)) <= f.eps))
            })
        {
            return None;
        }
        end = b;
    }
    Some(end)
}

fn neutral(f: Frame, pens: Pens, cut: f64, gray: &[Pen]) -> Option<()> {
    if gray.len() > MAX_GRAY {
        return None;
    }
    let [lo, hi] = pens.verify(f)?;
    let backing = pens.cap.width;
    let thin = pens.thin[0].width;
    let last = pens.all().into_iter().map(|p| p.key.0).max()?;
    let mut end = cut;
    let mut total = 0.;
    for &p in gray {
        let [a, b] = f.rail(p, hi)?;
        let a = a.max(cut);
        let b = b.min(f.length);
        if p.key.0 <= last
            || !(0.45..=0.86).contains(&p.gray)
            || !f.eq(p.width, thin)
            || p.width > 0.45 * backing
            || b - a <= f.eps
            || a < end - f.eps
        {
            return None;
        }
        total += b - a;
        end = b;
    }
    // Preserve a continuous black lane independently on each original face.
    if total > 0.80 * (f.length - cut) {
        return None;
    }
    if !gray.is_empty()
        && [(0., thin), (f.width, thin), (lo, backing), (hi, backing)]
            .into_iter()
            .any(|(at, width)| {
                let [a, b] = [at - width * 0.5, at + width * 0.5];
                let remain = (b.min(hi - thin * 0.5) - a).max(b - a.max(hi + thin * 0.5));
                remain < 0.15 * width
            })
    {
        return None;
    }
    Some(())
}

fn area_in(points: &[Point], f: Frame, bounds: [[f64; 2]; 2]) -> f64 {
    let mut ring = points
        .iter()
        .copied()
        .map(|p| f.local(p))
        .collect::<Vec<_>>();
    for (axis, [lo, hi]) in bounds.into_iter().enumerate() {
        for (at, scale) in [(lo, 1.), (hi, -1.)] {
            let signed = |p: Point| ((if axis == 0 { p.x } else { p.y }) - at) * scale;
            let mut clipped = Vec::new();
            for (&a, &b) in ring.iter().zip(ring.iter().cycle().skip(1)) {
                let (x, y) = (signed(a), signed(b));
                if x >= 0. {
                    clipped.push(a);
                }
                if x * y < 0. {
                    clipped.push(a + (b - a) * (x / (x - y)));
                }
            }
            ring = clipped;
        }
    }
    ring.iter()
        .zip(ring.iter().cycle().skip(1))
        .map(|(&a, &b)| a.cross(b))
        .sum::<f64>()
        .abs()
        * 0.5
}

fn proof_pen(p: Pen, page: u32) -> LocalPen {
    LocalPen {
        source_id: p.key.id(page),
        width: p.width,
    }
}

fn recorded(sources: &Sources<'_>, p: &LocalPen, gray: f64, page: u32) -> Option<Pen> {
    let key = Key::from_id(&p.source_id, page)?;
    source_pen(sources.get(p.source_id.as_str())?, key, p.width, gray)
}

fn certified(sources: &Sources<'_>, id: &str, page: u32) -> Option<Pen> {
    let p = sources.get(id)?;
    source_pen(p, Key::from_id(id, page)?, p.stroke_width?, 0.)
}

struct Neighbors<'a> {
    adjacent: &'a capped_stock::Receipt,
    stem: &'a recessed_stock::Receipt,
}

impl Neighbors<'_> {
    fn measure(
        &self,
        f: Frame,
        pens: Pens,
        cut: f64,
        connector: &str,
        sources: &Sources<'_>,
        new_turns: [Pen; 2],
        page: u32,
    ) -> Option<Verified> {
        let [inside, outside] = new_turns;
        let [back_lo, back_hi] = pens.verify(f)?;
        let offset = -back_lo;
        let w = self.adjacent;
        let s = self.stem;
        if !w.terminal_trim_source_ids.iter().any(|id| id == connector)
            || !w.terminal_trim_source_ids.contains(&s.trim_source_id)
            || draw_path(&w.paint_source_id) == draw_path(connector)
            || draw_path(&s.paint_source_id) == draw_path(connector)
        {
            return None;
        }
        let [[a, b], [u, v]] = range(w.outline.map(|p| f.local(p)));
        if !f.eq(a, f.length)
            || b - a < 8. * f.width
            || (v - u - f.width).abs() > f.eps
            || length_intersection([u, v], [0., f.width]) < 0.9 * f.width
            || !w
                .outline
                .iter()
                .map(|&p| f.local(p))
                .all(|p| (f.eq(p.x, a) || f.eq(p.x, b)) && (f.eq(p.y, u) || f.eq(p.y, v)))
        {
            return None;
        }
        let thick_inside = certified(sources, s.backing_face_source_ids.first()?, page)?;
        let thick_outside = certified(sources, s.backing_face_source_ids.get(2)?, page)?;
        let thin_inside = certified(sources, &s.outer_face_source_ids[0], page)?;
        let thin_outside = certified(sources, s.inner_face_source_ids.first()?, page)?;
        let backing_neighbor = certified(sources, &w.backing_pen_source_ids[1], page)?;
        if !f.eq(inside.width, pens.thin[0].width)
            || !f.eq(outside.width, pens.thin[0].width)
            || !f.eq(thick_inside.width, pens.cap.width)
            || !f.eq(thick_outside.width, pens.cap.width)
            || !f.eq(backing_neighbor.width, pens.cap.width)
        {
            return None;
        }
        let [turn_from, turn_at] = f.rail(inside, back_hi)?;
        let [out_point, out_back] = f.rail(outside, back_hi)?;
        let [[inner_x, inner_x2], [i0, i1]] = f.pass(thick_inside);
        let [[outer_x, outer_x2], [o0, o1]] = f.pass(thick_outside);
        let [[lo, lo2], _] = f.pass(thin_inside);
        let [[hi, hi2], [target, t1]] = f.pass(thin_outside);
        let [adj_from, _] = f.rail(backing_neighbor, back_hi + offset)?;
        if !f.eq(inner_x, inner_x2)
            || !f.eq(outer_x, outer_x2)
            || !f.eq(lo, lo2)
            || !f.eq(hi, hi2)
            || !f.eq(turn_at, inner_x)
            || !f.eq(turn_from, f.rail(pens.thick[1], back_hi)?[0])
            || !f.eq(lo, inner_x + offset)
            || !f.eq(out_point, hi)
            || !f.eq(out_back, outer_x)
            || !f.eq(hi + offset, outer_x)
            || !f.eq(outer_x, adj_from)
            || !f.eq(target, back_hi)
            || !f.eq(o0, back_hi + offset)
            || !f.eq(hi - lo, f.width)
            || i0 >= back_hi - f.eps
            || i1 <= back_hi + f.width
            || o1 <= o0 + f.width
            || t1 <= target + f.width
            || lo < cut + f.width
            || hi >= f.length - f.eps
        {
            return None;
        }
        // Both original paint extents must have a measured shared cross-section.
        let paint = filled(sources.get(s.paint_source_id.as_str())?)?;
        if paint.len() > 24
            || area_in(paint, f, [[lo, hi], [0., f.width]]) < 0.9 * f.width * f.width
        {
            return None;
        }
        let trim = filled(sources.get(s.trim_source_id.as_str())?)?;
        if trim.len() > 12
            || !trim
                .iter()
                .zip(trim.iter().cycle().skip(1))
                .any(|(&a, &b)| {
                    let [[x, y], [u, v]] = range([a, b].map(|p| f.local(p)));
                    f.eq(u, back_hi) && f.eq(v, back_hi) && x <= lo + f.eps && y >= hi - f.eps
                })
        {
            return None;
        }
        let x = (lo + hi) * 0.5;
        let joint_source_ids = [
            connector,
            &w.paint_source_id,
            &s.paint_source_id,
            &s.trim_source_id,
        ]
        .into_iter()
        .map(str::to_owned)
        .chain(
            [
                inside,
                outside,
                thick_inside,
                thick_outside,
                thin_inside,
                thin_outside,
                backing_neighbor,
                pens.thick[1],
                pens.cap,
            ]
            .into_iter()
            .map(|p| p.key.id(page)),
        )
        .collect::<std::collections::BTreeSet<_>>()
        .into_iter()
        .collect();
        Some(Verified {
            outline: f.outline(cut),
            stem_path: [f.world(x, back_hi), f.world(x, f.width * 0.5)],
            cap_path: [
                f.world(f.length, f.width * 0.5),
                f.world(f.length, (u + v) * 0.5),
            ],
            joint_source_ids,
        })
    }
}

fn local_rect(f: Frame, r: Rect) -> [[f64; 2]; 2] {
    range(
        [
            Point::new(r.l, r.t),
            Point::new(r.r, r.t),
            Point::new(r.r, r.b),
            Point::new(r.l, r.b),
        ]
        .map(|p| f.local(p)),
    )
}

fn mask_events(
    f: Frame,
    pens: Pens,
    nearby: &[&Event],
    sources: &Sources<'_>,
    page: u32,
) -> Option<Vec<Mask>> {
    let [lo, hi] = pens.verify(f)?;
    let coat = 0.5 * pens.cap.width;
    let mut out = nearby
        .iter()
        .filter_map(|e| {
            let Cover::Mask(m) = e.cover else {
                return None;
            };
            let [a, b] = range(m.outline.map(|p| f.local(p)));
            (length_intersection(a, [f.bevel, f.length]) > f.eps
                && length_intersection(b, [lo - coat, hi + coat]) > f.eps)
                .then_some(m)
        })
        .collect::<Vec<_>>();
    if out.len() > MAX_MASKS
        || out
            .iter()
            .any(|m| sources.contains_key(m.key.id(page).as_str()))
    {
        return None;
    }
    out.sort_by(|a, b| {
        let start = |m: &Mask| range(m.outline.map(|p| f.local(p)))[0][0];
        start(a).total_cmp(&start(b))
    });
    Some(out)
}

fn visible(
    f: Frame,
    pens: Pens,
    cut: f64,
    nearby: &[&Event],
    masks: &[Mask],
    ink: &[Pen],
) -> Option<Vec<Pen>> {
    let back = pens.verify(f)?;
    let thin = pens.thin[0].width;
    let face_bands = [
        (pens.thin[0], 0.),
        (pens.thin[1], f.width),
        (pens.thick[0], back[0]),
        (pens.thick[1], back[1]),
    ];
    let earliest = pens.all().into_iter().map(|p| p.key.0 * 2).min()?;
    let cap_range = [
        f.length - 0.5 * pens.cap.width,
        f.length + 0.5 * pens.cap.width,
    ];
    let cap_t = [
        back[0] - 0.5 * pens.cap.width,
        back[1] + 0.5 * pens.cap.width,
    ];
    let mut gray = Vec::new();
    for e in nearby {
        let [x, y] = local_rect(f, e.bounds);
        let positive = |a, b| length_intersection(a, b) > f.eps;
        let body = positive(x, [cut, f.length]) && positive(y, [0., f.width]);
        let old_pen = positive(x, [cut, f.length])
            && face_bands.into_iter().any(|(p, at)| {
                e.order > 2 * p.key.0 && positive(y, [at - p.width * 0.5, at + p.width * 0.5])
            });
        let cap = e.order > 2 * pens.cap.key.0 && positive(x, cap_range) && positive(y, cap_t);
        if !body && !old_pen && !cap {
            continue;
        }
        match e.cover {
            Cover::Mask(m) if masks.iter().any(|a| a.key == m.key) => {
                // The exact original white boundary trims the slab itself.
                // No virtual pen end is invented at the chosen cut plane.
                let stop = range(m.outline.map(|p| f.local(p)))[0][1];
                if cap || stop > cut + f.eps {
                    return None;
                }
            }
            Cover::Stroke(p) if p.gray == 0. => {}
            Cover::Stroke(p) if e.order < earliest => {
                let [[a, b], [u, v]] = f.pass(p);
                if !p.layerless
                    || !(0.08..=0.72).contains(&p.gray)
                    || !f.eq(p.width, thin)
                    || (b - a - v + u).abs() > 0.03 * f.width
                    || u < back[0] - 0.18 * f.width
                    || v > back[1] + 0.18 * f.width
                {
                    return None;
                }
            }
            Cover::Stroke(p) => {
                if gray.len() >= MAX_GRAY
                    || !ink.iter().any(|q| q.key == p.key)
                    || f.rail(p, back[1]).is_none()
                {
                    return None;
                }
                // A muted lower corner can touch the existing native cap.
                // A partial or crosswise occlusion cannot replace that cap.
                if cap && (p.points[1] - p.points[0]).dot(f.along).abs() < f.width {
                    return None;
                }
                gray.push(p);
            }
            _ => return None,
        }
    }
    gray.sort_by(|a, b| f.pass(*a)[0][0].total_cmp(&f.pass(*b)[0][0]));
    neutral(f, pens, cut, &gray)?;
    Some(gray)
}

fn turn_pens(
    f: Frame,
    pens: Pens,
    near: &Neighbors<'_>,
    sources: &Sources<'_>,
    ink: &[Pen],
    page: u32,
) -> Option<[Pen; 2]> {
    let back = pens.verify(f)?[1];
    let inward = certified(sources, near.stem.backing_face_source_ids.first()?, page)?;
    let outward = certified(sources, near.stem.backing_face_source_ids.get(2)?, page)?;
    let thin_out = certified(sources, near.stem.inner_face_source_ids.first()?, page)?;
    let in_from = f.rail(pens.thick[1], back)?[0];
    let in_to = f.pass(inward)[0][0];
    let out_from = f.pass(thin_out)[0][0];
    let out_to = f.pass(outward)[0][0];
    let find = |a, b| {
        let mut matches = ink.iter().copied().filter(|&p| {
            p.gray == 0.
                && f.eq(p.width, pens.thin[0].width)
                && f.rail(p, back)
                    .is_some_and(|[x, y]| f.eq(x, a) && f.eq(y, b))
        });
        let first = matches.next()?;
        // Coincident retracing is one measured turn. A merely nearby or
        // different-width competitor still invalidates authorship.
        matches.try_fold(first, |a, p| {
            let identical = p.width == a.width
                && (p.points == a.points || p.points == [a.points[1], a.points[0]]);
            identical.then_some(if p.key < a.key { p } else { a })
        })
    };
    Some([find(in_from, in_to)?, find(out_from, out_to)?])
}

pub(super) fn derive(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    capped: &[capped_stock::Receipt],
    recessed: &[recessed_stock::Receipt],
    page: u32,
) -> Vec<Receipt> {
    collect(keys, events, primitives, capped, recessed, page).unwrap_or_default()
}

fn collect(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    capped: &[capped_stock::Receipt],
    recessed: &[recessed_stock::Receipt],
    page: u32,
) -> Option<Vec<Receipt>> {
    if keys.is_empty() || capped.is_empty() || recessed.is_empty() {
        return Some(vec![]);
    }
    let sources: Sources<'_> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut work = MAX_WORK;
    let mut out = BTreeMap::new();
    for &key in keys {
        let id = key.id(page);
        let Some(f) = sources
            .get(id.as_str())
            .and_then(|p| filled(p))
            .and_then(Frame::new)
        else {
            continue;
        };
        let mut related = capped
            .iter()
            .filter(|p| p.terminal_trim_source_ids.contains(&id));
        let Some(adjacent) = related.next() else {
            continue;
        };
        if related.next().is_some() {
            continue;
        }
        work = work.checked_sub(events.len())?;
        let region = Rect::points(&[
            f.world(-2. * f.width, -3. * f.width),
            f.world(f.length + 6. * f.width, -3. * f.width),
            f.world(-2. * f.width, 4. * f.width),
            f.world(f.length + 6. * f.width, 4. * f.width),
        ])?;
        let nearby = events
            .iter()
            .filter(|e| e.order > 2 * key.0 && region.intersect(e.bounds).is_some())
            .collect::<Vec<_>>();
        let ink = nearby
            .iter()
            .filter_map(|e| {
                let Cover::Stroke(p) = e.cover else {
                    return None;
                };
                let q = source_pen(
                    sources.get(p.key.id(page).as_str())?,
                    p.key,
                    p.width,
                    p.gray,
                )?;
                (p.layerless && q.points == p.points).then_some(p)
            })
            .collect::<Vec<_>>();
        work = work.checked_sub(
            nearby
                .len()
                .saturating_mul(ink.len().min(MAX_GRAY) + 1)
                .saturating_add(
                    ink.len()
                        .saturating_mul(6 + recessed.len().saturating_mul(2)),
                ),
        )?;
        let Some(pens) = Pens::find(f, &ink) else {
            continue;
        };
        let Some(masks) = mask_events(f, pens, &nearby, &sources, page) else {
            continue;
        };
        let Some(cut) = strip_masks(f, pens, &masks) else {
            continue;
        };
        let Some(gray) = visible(f, pens, cut, &nearby, &masks, &ink) else {
            continue;
        };
        let proven = unique(recessed.iter().filter_map(|stem| {
            let near = Neighbors { adjacent, stem };
            let turns = turn_pens(f, pens, &near, &sources, &ink, page)?;
            near.measure(f, pens, cut, &id, &sources, turns, page)?;
            Some((stem, turns))
        }));
        let Some((stem, [inside, outside])) = proven else {
            continue;
        };
        let p = |pen| proof_pen(pen, page);
        out.insert(
            key,
            Receipt {
                paint_source_id: id,
                face_pens: pens.thin.map(p),
                backing_pens: pens.thick.map(p),
                authored_cap: p(pens.cap),
                opaque_masks: masks
                    .into_iter()
                    .map(|m| MaskProof {
                        source_id: m.key.id(page),
                        outline: m.outline,
                    })
                    .collect(),
                neutral_overlays: gray.into_iter().map(p).collect(),
                adjacent_paint_source_id: adjacent.paint_source_id.clone(),
                stem_paint_source_id: stem.paint_source_id.clone(),
                inside_turn: p(inside),
                outside_turn: p(outside),
            },
        );
        if out.len() > MAX_RECEIPTS {
            return None;
        }
    }
    Some(out.into_values().collect())
}

impl Receipt {
    pub(crate) fn retained_source_ids(&self) -> impl Iterator<Item = &str> {
        [
            &self.paint_source_id,
            &self.adjacent_paint_source_id,
            &self.stem_paint_source_id,
        ]
        .into_iter()
        .map(String::as_str)
        .chain(
            self.face_pens
                .iter()
                .chain(&self.backing_pens)
                .chain([&self.authored_cap, &self.inside_turn, &self.outside_turn])
                .chain(&self.neutral_overlays)
                .map(|p| p.source_id.as_str()),
        )
    }
}

fn interpret(page: &PageScan, r: &Receipt, sources: &Sources<'_>) -> Option<Verified> {
    let number = page.page_number;
    let key = Key::from_id(&r.paint_source_id, number)?;
    let f = Frame::new(filled(sources.get(r.paint_source_id.as_str())?)?)?;
    if r.opaque_masks.len() > MAX_MASKS || r.neutral_overlays.len() > MAX_GRAY {
        return None;
    }
    let mut seen = HashSet::new();
    if r.retained_source_ids().any(|id| !seen.insert(id)) {
        return None;
    }
    let get = |p: &LocalPen, gray| {
        let p = recorded(sources, p, gray, number)?;
        (p.key.0 > key.0).then_some(p)
    };
    let pens = Pens {
        thin: [get(&r.face_pens[0], 0.)?, get(&r.face_pens[1], 0.)?],
        thick: [get(&r.backing_pens[0], 0.)?, get(&r.backing_pens[1], 0.)?],
        cap: get(&r.authored_cap, 0.)?,
    };
    let masks = r
        .opaque_masks
        .iter()
        .map(|m| {
            let key = Key::from_id(&m.source_id, number)?;
            (!sources.contains_key(m.source_id.as_str())
                && seen.insert(m.source_id.as_str())
                && m.outline.iter().all(|p| p.finite()))
            .then_some(Mask {
                key,
                outline: m.outline,
            })
        })
        .collect::<Option<Vec<_>>>()?;
    let cut = strip_masks(f, pens, &masks)?;
    let gray = r
        .neutral_overlays
        .iter()
        .map(|p| get(p, 0.75))
        .collect::<Option<Vec<_>>>()?;
    neutral(f, pens, cut, &gray)?;
    let adjacent = unique(
        page.capped_stock
            .iter()
            .filter(|p| p.paint_source_id == r.adjacent_paint_source_id),
    )?;
    let stem = unique(
        page.recessed_stock
            .iter()
            .filter(|p| p.paint_source_id == r.stem_paint_source_id),
    )?;
    Neighbors { adjacent, stem }.measure(
        f,
        pens,
        cut,
        &r.paint_source_id,
        sources,
        [get(&r.inside_turn, 0.)?, get(&r.outside_turn, 0.)?],
        number,
    )
}

pub(crate) fn verified(page: &PageScan, r: &Receipt) -> Option<Verified> {
    let sources = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    interpret(page, r, &sources)
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    if page.masked_junction_stock.is_empty() {
        return Ok(());
    }
    let bad = || "invalid source-bounded masked junction stock".to_owned();
    if page.masked_junction_stock.len() > MAX_RECEIPTS
        || !page.pdf_input.as_ref().is_some_and(|p| {
            matches!(
                p.route,
                crate::input::raster::PdfRoute::Vector | crate::input::raster::PdfRoute::Mixed
            )
        })
    {
        return Err(bad());
    }
    let sources = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut prev = None;
    for r in &page.masked_junction_stock {
        let key = Key::from_id(&r.paint_source_id, page.page_number).ok_or_else(bad)?;
        if prev.is_some_and(|a| a >= key) || interpret(page, r, &sources).is_none() {
            return Err(bad());
        }
        prev = Some(key);
    }
    Ok(())
}
