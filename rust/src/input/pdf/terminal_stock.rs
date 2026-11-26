//! Native terminal stock supported by paint and two pens beyond an opaque mask.
#[path = "capped_stock.rs"]
pub mod capped_stock;
#[path = "local_stock.rs"]
pub mod local_stock;
#[path = "masked_junction_stock.rs"]
pub mod masked_junction_stock;
#[path = "recessed_stock.rs"]
pub mod recessed_stock;
#[path = "stepped_stock.rs"]
pub mod stepped_stock;
use super::{Graphics, Point, Rect, Subpath, visibility::NativeInk};
use crate::model::{PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};

const MAX_EVENTS: usize = 16_384;
const MAX_SEEDS: usize = 256;
const MAX_RECEIPTS: usize = 64;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub face_source_ids: [String; 2],
    pub face_overlay_source_ids: [Option<String>; 2],
    pub face_width: f64,
    pub outline: [Point; 4],
    pub occluder_source_id: String,
    pub occluder_outline: [Point; 4],
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
struct Key(u64, usize);

impl Key {
    fn id(self, page: u32) -> String {
        format!("pdf:p{page}:path:{}:subpath:{}", self.0, self.1)
    }

    fn from_id(id: &str, page: u32) -> Option<Self> {
        let (p, tail) = id.split_once(":path:")?;
        let (order, sub) = tail.split_once(":subpath:")?;
        let key = Self(order.parse().ok()?, sub.parse().ok()?);
        (p.strip_prefix("pdf:p")?.parse::<u32>().ok()? == page
            && key.0 > 0
            && (1..=128).contains(&key.1)
            && key.id(page) == id)
            .then_some(key)
    }
}

#[derive(Clone, Copy)]
struct Stock {
    start: Point,
    u: Point,
    length: f64,
    width: f64,
    eps: f64,
}

impl Stock {
    fn from_points(points: &[Point]) -> Option<Self> {
        let ring = rectangle(points)?;
        let i = (0..4).max_by(|&a, &b| length(ring, a).total_cmp(&length(ring, b)))?;
        let mut u = (ring[(i + 1) % 4] - ring[i]).unit();
        if (if u.x.abs() >= u.y.abs() { u.x } else { u.y }) < 0. {
            u = u * -1.;
        }
        let n = Point::new(-u.y, u.x);
        let o = ring[0];
        let [[lo, hi], [low, high]] =
            bounds(ring.map(|p| Point::new((p - o).dot(u), (p - o).dot(n))));
        let width = high - low;
        let length = hi - lo;
        (width.is_finite() && length.is_finite() && width > 0. && length >= 6. * width).then_some(
            Self {
                start: o + u * lo + n * low,
                u,
                length,
                width,
                eps: precision(&ring),
            },
        )
    }

    fn local(self, p: Point) -> Point {
        let d = p - self.start;
        Point::new(d.dot(self.u), self.u.cross(d))
    }

    fn world(self, x: f64, y: f64) -> Point {
        self.start + self.u * x + Point::new(-self.u.y, self.u.x) * y
    }

    fn outline(self, [lo, hi]: [f64; 2]) -> [Point; 4] {
        [
            self.world(lo, self.width),
            self.world(hi, self.width),
            self.world(hi, 0.),
            self.world(lo, 0.),
        ]
    }

    fn eps_thin(self) -> f64 {
        self.eps.max(self.width * 0.0001)
    }

    fn eq(self, x: f64, y: f64) -> bool {
        (x - y).abs() <= self.eps_thin()
    }

    fn face(self, points: [Point; 2], at: f64, span: [f64; 2], exact: bool) -> bool {
        let [a, b] = points.map(|p| self.local(p));
        let [lo, hi] = [a.x.min(b.x), a.x.max(b.x)];
        self.eq(a.y, at)
            && self.eq(b.y, at)
            && if exact {
                self.eq(lo, span[0]) && self.eq(hi, span[1])
            } else {
                lo <= span[0] + self.eps_thin() && hi >= span[1] - self.eps_thin()
            }
    }

    fn valid_width(self, width: f64) -> bool {
        (0.025..=0.22).contains(&(width / self.width))
    }

    fn trim(self, mask: [Point; 4], pen: f64) -> Option<[f64; 2]> {
        let m = mask.map(|p| self.local(p));
        let [[lo, hi], [a, b]] = bounds(m);
        // The mask is rectangular and aligned with the measured stock.
        if !m
            .into_iter()
            .all(|p| (self.eq(p.x, lo) || self.eq(p.x, hi)) && (self.eq(p.y, a) || self.eq(p.y, b)))
            || a > -0.5 * pen
            || b < self.width + 0.5 * pen
        {
            return None;
        }
        let pad = pen * std::f64::consts::FRAC_1_SQRT_2;
        let result = if lo <= -pad && hi > self.eps_thin() && hi < self.length {
            [hi + pad, self.length]
        } else if hi >= self.length + pad && lo < self.length - self.eps_thin() && lo > 0. {
            [0., lo - pad]
        } else {
            return None;
        };
        (result[1] - result[0] >= 3. * self.width).then_some(result)
    }

    fn envelope(self, [lo, hi]: [f64; 2], pen: f64) -> Rect {
        let pad = pen * std::f64::consts::FRAC_1_SQRT_2;
        let half = pen * 0.5;
        Rect::points(&[
            self.world(lo - pad, -half),
            self.world(hi + pad, -half),
            self.world(hi + pad, self.width + half),
            self.world(lo - pad, self.width + half),
        ])
        .expect("finite stock envelope")
    }

    fn face_envelope(self, span: [f64; 2], at: f64, pen: f64) -> Rect {
        let pad = pen * std::f64::consts::FRAC_1_SQRT_2;
        let half = 0.5 * pen;
        Rect::points(&[
            self.world(span[0] - pad, at - half),
            self.world(span[1] + pad, at - half),
            self.world(span[1] + pad, at + half),
            self.world(span[0] - pad, at + half),
        ])
        .expect("finite face envelope")
    }
}

fn length(p: [Point; 4], i: usize) -> f64 {
    p[i].distance(p[(i + 1) % 4])
}

fn bounds(points: [Point; 4]) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], p| {
            [
                [x[0].min(p.x), x[1].max(p.x)],
                [y[0].min(p.y), y[1].max(p.y)],
            ]
        })
}

fn precision(points: &[Point]) -> f64 {
    128. * f64::EPSILON * points.iter().map(|p| p.norm()).fold(1., f64::max)
}

fn rectangle(points: &[Point]) -> Option<[Point; 4]> {
    if !(4..=10).contains(&points.len()) || !points.iter().all(|p| p.finite()) {
        return None;
    }
    let epsilon = precision(points);
    let mut ring = points.to_vec();
    ring.dedup_by(|a, b| a.distance(*b) <= epsilon);
    if ring.first()?.distance(*ring.last()?) <= epsilon {
        ring.pop();
    }
    if ring.len() < 4 {
        return None;
    }
    let mut corners = Vec::with_capacity(4);
    for (i, &b) in ring.iter().enumerate() {
        let a = ring[(i + ring.len() - 1) % ring.len()];
        let c = ring[(i + 1) % ring.len()];
        if (b - a).unit().cross(c - b).abs() > epsilon || (b - a).dot(c - b) < 0. {
            corners.push(b);
        }
    }
    let [a, b, c, d]: [Point; 4] = corners.try_into().ok()?;
    let u = b - a;
    let n = d - a;
    (u.norm() > epsilon
        && n.norm() > epsilon
        && u.unit().dot(n).abs() <= epsilon
        && (a + u + n).distance(c) <= epsilon)
        .then_some([a, b, c, d])
}

fn tone(ink: NativeInk) -> Option<f64> {
    match ink {
        NativeInk::Gray([gray]) => Some(f64::from_bits(gray)),
        NativeInk::Rgb([r, g, b]) if r == g && g == b => Some(f64::from_bits(r)),
        NativeInk::Cmyk([c, m, y, k]) if [c, m, y].into_iter().all(|p| f64::from_bits(p) == 0.) => {
            Some(1. - f64::from_bits(k))
        }
        _ => None,
    }
}

fn safe(g: &Graphics) -> bool {
    g.default_rendering_intent
        && g.material_state_safe
        && g.pen_state_safe
        && !g.masked
        && !g.finish_clip_unsafe
}

#[derive(Clone, Copy)]
struct Pen {
    key: Key,
    points: [Point; 2],
    width: f64,
    gray: f64,
    layerless: bool,
}

#[derive(Clone, Copy)]
struct Mask {
    key: Key,
    outline: [Point; 4],
}

#[derive(Clone, Copy)]
enum Cover {
    Other,
    Mask(Mask),
    Stroke(Pen),
    NeutralArea { key: Key },
    RecessedArea { key: Key },
}

struct Event {
    order: u64,
    bounds: Rect,
    cover: Cover,
}

struct Seed {
    key: Key,
    stock: Stock,
    gray: f64,
}

pub(super) struct Tracker {
    seeds: Vec<Seed>,
    local_sources: Vec<Key>,
    stepped_sources: Vec<Key>,
    recessed_sources: Vec<Key>,
    masked_junction_sources: Vec<Key>,
    capped_sources: Vec<Key>,
    events: Vec<Event>,
    masks: usize,
    exhausted: bool,
}

impl Tracker {
    pub(super) fn new() -> Self {
        Self {
            seeds: vec![],
            local_sources: vec![],
            stepped_sources: vec![],
            recessed_sources: vec![],
            masked_junction_sources: vec![],
            capped_sources: vec![],
            events: vec![],
            masks: 0,
            exhausted: false,
        }
    }

    fn add(&mut self, order: u64, bounds: Option<Rect>, cover: Cover) {
        if self.events.len() == MAX_EVENTS {
            self.exhausted = true;
        }
        if !self.exhausted
            && let Some(bounds) = bounds
        {
            self.events.push(Event {
                order,
                bounds,
                cover,
            });
        }
    }

    pub(super) fn unknown(&mut self, path: u64, bounds: Option<Rect>) {
        if self.tracking() {
            self.add(path * 2 + 1, bounds, Cover::Other);
        }
    }

    pub(super) fn text(&mut self, path: u64, g: &Graphics, known_font: bool) {
        // Standard opaque black text cannot erase the dark stock or black pen.
        // Unknown font programs and potentially light text are unresolved paint.
        let visible = match g.tr {
            0 => !(g.black_fill && g.fill_alpha == 1.) && g.fill_alpha != 0.,
            1 => !(g.black_pen && g.stroke_alpha == 1.) && g.stroke_alpha != 0.,
            2 => !(g.black_fill && g.black_pen && g.fill_alpha == 1. && g.stroke_alpha == 1.),
            3 => false,
            _ => true,
        };
        if visible || !known_font || !safe(g) {
            self.unknown(path, g.clip.or(g.uncertain_clip));
        }
    }

    pub(super) fn paint(
        &mut self,
        path: u64,
        subs: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
        layerless: bool,
    ) {
        if self.exhausted || subs.is_empty() {
            return;
        }
        let fill_tone = (fill
            && !stroke
            && safe(g)
            && g.fill_device.is_some()
            && g.solid_fill_safe
            && g.fill_alpha == 1.)
            .then(|| g.fill_ink.and_then(tone))
            .flatten();
        let area = subs.iter().filter_map(Subpath::bounds).reduce(Rect::union);
        let clip = g.clip.or(g.uncertain_clip);
        let clipped = |r: Rect| clip.and_then(|c| r.intersect(c));
        if self.tracking() && fill && (g.fill_alpha != 0. || !safe(g)) {
            let simple_mask = if let [s] = subs
                && s.curves == 0
                && fill_tone == Some(1.)
            {
                rectangle(&s.ps).filter(|p| g.contains_footprint(p))
            } else {
                None
            };
            if let Some(outline) = simple_mask {
                self.masks += 1;
                self.add(
                    path * 2,
                    Rect::points(&outline),
                    Cover::Mask(Mask {
                        key: Key(path, 1),
                        outline,
                    }),
                );
            } else {
                // Bounding unresolved fills individually also avoids treating
                // the clear interval between disjoint solid contours as paint.
                if subs.len() <= 128 {
                    for (i, s) in subs.iter().enumerate() {
                        let Some(r) = s.bounds() else {
                            continue;
                        };
                        let cover = if (!self.capped_sources.is_empty()
                            || !self.recessed_sources.is_empty())
                            && fill_tone.is_some_and(|gray| (0.08..=0.95).contains(&gray))
                            && layerless
                            && s.curves == 0
                            && g.contains_footprint(&s.ps)
                            && !subs.iter().enumerate().any(|(j, other)| {
                                j != i && other.bounds().is_some_and(|b| r.intersect(b).is_some())
                            }) {
                            let key = Key(path, i + 1);
                            if self.capped_sources.is_empty() {
                                Cover::RecessedArea { key }
                            } else {
                                Cover::NeutralArea { key }
                            }
                        } else {
                            Cover::Other
                        };
                        self.add(path * 2, clipped(r), cover);
                    }
                } else {
                    self.add(path * 2, area.and_then(clipped), Cover::Other);
                }
            }
        }
        if let Some(gray) = fill_tone
            && layerless
            && (0.08..=0.90).contains(&gray)
            && subs.len() <= 128
        {
            for (i, s) in subs.iter().enumerate().filter(|(_, s)| s.curves == 0) {
                if self.capped_sources.len() < 64
                    && capped_stock::eligible(&s.ps)
                    && g.contains_footprint(&s.ps)
                    && let Some(bounds) = s.bounds()
                    && !subs.iter().enumerate().any(|(j, other)| {
                        j != i
                            && other
                                .bounds()
                                .is_some_and(|b| bounds.intersect(b).is_some())
                    })
                {
                    self.capped_sources.push(Key(path, i + 1));
                }
                if self.stepped_sources.len() < 64
                    && stepped_stock::eligible(&s.ps)
                    && g.contains_footprint(&s.ps)
                    && let Some(bounds) = s.bounds()
                    && !subs.iter().enumerate().any(|(j, other)| {
                        j != i
                            && other
                                .bounds()
                                .is_some_and(|b| bounds.intersect(b).is_some())
                    })
                {
                    self.stepped_sources.push(Key(path, i + 1));
                }
                if self.recessed_sources.len() < 32
                    && recessed_stock::eligible(&s.ps)
                    && g.contains_footprint(&s.ps)
                    && let Some(bounds) = s.bounds()
                    && !subs.iter().enumerate().any(|(j, other)| {
                        j != i
                            && other
                                .bounds()
                                .is_some_and(|b| bounds.intersect(b).is_some())
                    })
                {
                    self.recessed_sources.push(Key(path, i + 1));
                }
                // This stock requires two previously tracked owner families.
                // It can consume their ordered events without changing when
                // the older parsers begin recording or exhaust their budget.
                if self.tracking()
                    && self.masked_junction_sources.len() < 48
                    && masked_junction_stock::eligible(&s.ps)
                    && g.contains_footprint(&s.ps)
                    && let Some(bounds) = s.bounds()
                    && !subs.iter().enumerate().any(|(j, other)| {
                        j != i
                            && other
                                .bounds()
                                .is_some_and(|b| bounds.intersect(b).is_some())
                    })
                {
                    self.masked_junction_sources.push(Key(path, i + 1));
                }
                if self.local_sources.len() < 64
                    && subs.len() > 1
                    && local_stock::eligible(&s.ps)
                    && g.contains_footprint(&s.ps)
                    && let Some(bounds) = s.bounds()
                    && !subs.iter().enumerate().any(|(j, other)| {
                        j != i
                            && other
                                .bounds()
                                .is_some_and(|b| bounds.intersect(b).is_some())
                    })
                {
                    self.local_sources.push(Key(path, i + 1));
                }
                let Some(stock) = Stock::from_points(&s.ps) else {
                    continue;
                };
                let outline = stock.outline([0., stock.length]);
                let Some(box_) = Rect::points(&outline) else {
                    continue;
                };
                if !g.contains_footprint(&outline)
                    || subs.iter().enumerate().any(|(j, other)| {
                        j != i && other.bounds().is_some_and(|b| box_.intersect(b).is_some())
                    })
                {
                    continue;
                }
                if self.seeds.len() == MAX_SEEDS {
                    self.exhausted = true;
                    return;
                }
                self.seeds.push(Seed {
                    key: Key(path, i + 1),
                    stock,
                    gray,
                });
            }
        }
        if self.tracking() && stroke && (g.stroke_alpha != 0. || !safe(g)) {
            self.stroke(path, subs, g, layerless);
        }
    }

    fn tracking(&self) -> bool {
        !self.seeds.is_empty()
            || !self.stepped_sources.is_empty()
            || !self.recessed_sources.is_empty()
            || !self.capped_sources.is_empty()
    }

    fn stroke(&mut self, path: u64, subs: &[Subpath], g: &Graphics, layerless: bool) {
        let gray = if g.black_pen && g.stroke_device.is_some() {
            Some(0.)
        } else {
            g.stroke_ink.and_then(tone)
        };
        let pen = (safe(g) && g.stroke && g.stroke_alpha == 1. && g.undashed_pen)
            .then(|| g.ctm.similarity().zip(gray))
            .flatten()
            .map(|(scale, tone)| (scale * g.pen_width, tone));
        let [a, b, c, d, _, _] = g.ctm.0;
        for (i, s) in subs.iter().enumerate() {
            if i < 128
                && let [a, b] = s.ps.as_slice()
                && s.curves == 0
                && !s.closed
                && a != b
                && let Some((width, gray)) = pen
                && width.is_finite()
                && width > 0.
            {
                let dir = (*b - *a).unit();
                let tangent = dir * (width * std::f64::consts::FRAC_1_SQRT_2);
                let normal = Point::new(-dir.y, dir.x) * (width * 0.5);
                let outline = [
                    *a - tangent + normal,
                    *b + tangent + normal,
                    *b + tangent - normal,
                    *a - tangent - normal,
                ];
                if g.contains_footprint(&outline) {
                    self.add(
                        path * 2,
                        Rect::points(&outline),
                        Cover::Stroke(Pen {
                            key: Key(path, i + 1),
                            points: [*a, *b],
                            width,
                            gray,
                            layerless,
                        }),
                    );
                    continue;
                }
            }
            // Known fully opaque black joins cannot erase black/gray material.
            if safe(g) && g.stroke_alpha == 1. && gray == Some(0.) {
                continue;
            }
            let reach = (g.pen_width * 0.5).max(0.00001) * g.miter_limit.max(2_f64.sqrt());
            if let Some(r) = s.bounds() {
                let dx = reach * a.hypot(c);
                let dy = reach * b.hypot(d);
                let bounds = Rect {
                    l: r.l - dx,
                    r: r.r + dx,
                    t: r.t - dy,
                    b: r.b + dy,
                };
                self.add(
                    path * 2,
                    g.clip
                        .or(g.uncertain_clip)
                        .and_then(|c| bounds.intersect(c)),
                    Cover::Other,
                );
            }
        }
    }

    pub(super) fn finish(self, primitives: &[Primitive], page: u32) -> Vec<Receipt> {
        if self.exhausted || self.masks == 0 || self.seeds.is_empty() {
            return vec![];
        }
        let mut receipts = self.receipts(page).unwrap_or_default();
        if !receipts.is_empty() {
            let retained: HashSet<_> = primitives.iter().map(|p| p.id.as_str()).collect();
            receipts.retain(|r| {
                !retained.contains(r.occluder_source_id.as_str())
                    && std::iter::once(&r.paint_source_id)
                        .chain(&r.face_source_ids)
                        .chain(r.face_overlay_source_ids.iter().flatten())
                        .all(|id| retained.contains(id.as_str()))
            });
        }
        receipts
    }

    pub(super) fn local_stock(
        &self,
        primitives: &[Primitive],
        visible_paint: &[String],
        page: u32,
    ) -> Vec<local_stock::Receipt> {
        if self.exhausted {
            return vec![];
        }
        local_stock::derive(
            &self.local_sources,
            &self.events,
            primitives,
            visible_paint,
            page,
        )
    }

    pub(super) fn stepped_stock(
        &self,
        primitives: &[Primitive],
        page: u32,
    ) -> Vec<stepped_stock::Receipt> {
        if self.exhausted {
            return vec![];
        }
        stepped_stock::derive(&self.stepped_sources, &self.events, primitives, page)
    }

    pub(super) fn capped_stock(
        &self,
        primitives: &[Primitive],
        page: u32,
    ) -> Vec<capped_stock::Receipt> {
        if self.exhausted {
            return vec![];
        }
        capped_stock::derive(&self.capped_sources, &self.events, primitives, page)
    }

    pub(super) fn recessed_stock(
        &self,
        primitives: &[Primitive],
        page: u32,
    ) -> Vec<recessed_stock::Receipt> {
        if self.exhausted {
            return vec![];
        }
        recessed_stock::derive(&self.recessed_sources, &self.events, primitives, page)
    }

    pub(super) fn masked_junction_stock(
        &self,
        primitives: &[Primitive],
        capped: &[capped_stock::Receipt],
        recessed: &[recessed_stock::Receipt],
        page: u32,
    ) -> Vec<masked_junction_stock::Receipt> {
        if self.exhausted {
            return vec![];
        }
        masked_junction_stock::derive(
            &self.masked_junction_sources,
            &self.events,
            primitives,
            capped,
            recessed,
            page,
        )
    }

    fn receipts(self, page: u32) -> Option<Vec<Receipt>> {
        let mut budget = MAX_WORK;
        let mut out = BTreeMap::new();
        for seed in &self.seeds {
            budget = budget.checked_sub(self.events.len())?;
            let stock = seed.stock;
            let full = stock.envelope([0., stock.length], stock.width * 0.22);
            let local = self
                .events
                .iter()
                .filter(|e| e.order > seed.key.0 * 2 && full.intersect(e.bounds).is_some())
                .collect::<Vec<_>>();
            // One independently opaque full-depth terminal mask. A second
            // competing or interior mask cannot authorize a new endpoint.
            let mut masks = local.iter().filter_map(|e| match e.cover {
                Cover::Mask(m) => Some(m),
                _ => None,
            });
            let Some(mask) = masks.next() else { continue };
            if masks.next().is_some() {
                continue;
            }
            let faces = [0., stock.width].map(|at| {
                let mut pens = local.iter().filter_map(|e| {
                    let Cover::Stroke(p) = e.cover else {
                        return None;
                    };
                    (p.key.0 < mask.key.0
                        && p.layerless
                        && p.gray == 0.
                        && stock.valid_width(p.width)
                        && stock.face(p.points, at, [0., stock.length], true))
                    .then_some(p)
                });
                let p = pens.next()?;
                pens.next().is_none().then_some(p)
            });
            let [Some(low), Some(high)] = faces else {
                continue;
            };
            if low.key.0 == high.key.0 || !stock.eq(low.width, high.width) {
                continue;
            }
            let Some(span) = stock.trim(mask.outline, low.width) else {
                continue;
            };
            let body = Rect::points(&stock.outline(span))?;
            let faces = [(low, 0.), (high, stock.width)]
                .map(|(p, at)| (p.key.0 * 2, stock.face_envelope(span, at, p.width)));
            budget = budget.checked_sub(local.len())?;
            let mut overlays = [None, None];
            let mut ambiguous = false;
            for e in local {
                // A face painted after a neighboring fill keeps its own
                // complete pen. The earlier stock and the two later faces
                // have distinct paint-order visibility intervals.
                if body.intersect(e.bounds).is_none()
                    && !faces.iter().any(|(order, footprint)| {
                        e.order > *order && footprint.intersect(e.bounds).is_some()
                    })
                {
                    continue;
                }
                match e.cover {
                    Cover::Mask(m) if m.key == mask.key => {}
                    Cover::Stroke(p) if p.gray == 0. => {}
                    Cover::Stroke(p) => {
                        let face =
                            [0., stock.width]
                                .into_iter()
                                .enumerate()
                                .find_map(|(index, at)| {
                                    (p.layerless
                                        && p.key.0 > [low, high][index].key.0
                                        && stock.eq(p.width, low.width)
                                        && (0.05..=0.90).contains(&p.gray)
                                        && (p.gray - seed.gray).abs() >= 0.08
                                        && stock.face(p.points, at, span, false))
                                    .then_some(index)
                                });
                        if let Some(i) = face
                            && overlays[i].is_none()
                        {
                            overlays[i] = Some(p.key);
                        } else {
                            ambiguous = true;
                            break;
                        }
                    }
                    _ => {
                        ambiguous = true;
                        break;
                    }
                }
            }
            if ambiguous {
                continue;
            }
            out.insert(
                seed.key,
                Receipt {
                    paint_source_id: seed.key.id(page),
                    face_source_ids: [low.key.id(page), high.key.id(page)],
                    face_overlay_source_ids: overlays.map(|o| o.map(|key| key.id(page))),
                    face_width: low.width,
                    outline: stock.outline(span),
                    occluder_source_id: mask.key.id(page),
                    occluder_outline: mask.outline,
                },
            );
            if out.len() > MAX_RECEIPTS {
                return None;
            }
        }
        Some(out.into_values().collect())
    }
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    let receipts = &page.terminal_stock;
    if receipts.is_empty() {
        return Ok(());
    }
    let error = || "invalid native terminal stock visibility receipt".to_owned();
    let source: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    if receipts.len() > MAX_RECEIPTS
        || !matches!(
            page.pdf_input.as_ref().map(|p| p.route),
            Some(crate::input::raster::PdfRoute::Vector | crate::input::raster::PdfRoute::Mixed)
        )
    {
        return Err(error());
    }
    let mut last = None;
    for r in receipts {
        let parse = |id| Key::from_id(id, page.page_number).ok_or_else(error);
        let paint = parse(&r.paint_source_id)?;
        let mask = parse(&r.occluder_source_id)?;
        if last.is_some_and(|prior| prior >= paint)
            || mask.1 != 1
            || mask.0 <= paint.0
            || source.contains_key(r.occluder_source_id.as_str())
        {
            return Err(error());
        }
        last = Some(paint);
        let Some(p) = source
            .get(r.paint_source_id.as_str())
            .filter(|p| p.layer.is_none())
        else {
            return Err(error());
        };
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
        else {
            return Err(error());
        };
        let stock = Stock::from_points(points).ok_or_else(error)?;
        if !stock.valid_width(r.face_width)
            || !r.occluder_outline.iter().all(|p| {
                p.finite()
                    && p.x >= -stock.eps
                    && p.x <= page.width + stock.eps
                    && p.y >= -stock.eps
                    && p.y <= page.height + stock.eps
            })
        {
            return Err(error());
        }
        let contour = rectangle(&r.occluder_outline).ok_or_else(error)?;
        let span = stock.trim(contour, r.face_width).ok_or_else(error)?;
        if !r
            .outline
            .into_iter()
            .zip(stock.outline(span))
            .all(|(a, b)| a.finite() && a.distance(b) <= stock.eps_thin())
        {
            return Err(error());
        }
        let line = |id: &str, at, span, exact| {
            source.get(id).is_some_and(|p| p.layer.is_none()
                && p.stroke_width.is_none_or(|w|stock.eq(w,r.face_width))
                && matches!(p.shape,Shape::Line {start,end} if stock.face([start,end],at,span,exact)))
        };
        let [a, b] = r.face_source_ids.each_ref().map(|s| parse(s));
        let (a, b) = (a?, b?);
        if !(paint.0 < a.0 && a.0 < mask.0 && paint.0 < b.0 && b.0 < mask.0 && a.0 != b.0)
            || ![0., stock.width]
                .into_iter()
                .enumerate()
                .all(|(i, at)| line(&r.face_source_ids[i], at, [0., stock.length], true))
        {
            return Err(error());
        }
        for (i, id) in r.face_overlay_source_ids.iter().enumerate() {
            if let Some(id) = id {
                let overlay = parse(id)?;
                if overlay.0 <= [a, b][i].0 || !line(id, [0., stock.width][i], span, false) {
                    return Err(error());
                }
            }
        }
    }
    Ok(())
}
