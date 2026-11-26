//! Capture source receipts for a white wall core bounded by authored framing.
use super::{Graphics, Point, Rect, Subpath, material, visibility::NativeInk};
use crate::model::{Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

const SIDE: usize = 32;
const EVENT_LIMIT: usize = 160_000;
const WORK_LIMIT: usize = 16_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Footprint {
    pub source_id: String,
    pub layer: String,
    pub outline: [Point; 4],
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BoxedDimension {
    pub mask: Footprint,
    pub borders: [String; 4],
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub core: Footprint,
    pub facings: [Footprint; 2],
    pub caps: [Footprint; 2],
    pub rails: [String; 4],
    pub returns: [[String; 2]; 2],
    pub dimensions: Vec<BoxedDimension>,
}

impl Receipt {
    pub(crate) fn extent(&self) -> Option<([Point; 2], f64)> {
        let frame = Frame::new(self.core.outline)?;
        Some((
            [frame.origin, frame.origin + frame.axis * frame.length],
            frame.width(),
        ))
    }

    pub(crate) fn footprints(&self) -> impl Iterator<Item = &Footprint> {
        std::iter::once(&self.core)
            .chain(&self.facings)
            .chain(&self.caps)
    }

    pub(crate) fn source_ids(&self) -> impl Iterator<Item = &str> {
        self.footprints()
            .map(|p| p.source_id.as_str())
            .chain(self.rails.iter().map(String::as_str))
            .chain(self.returns.iter().flatten().map(String::as_str))
            .chain(self.dimensions.iter().flat_map(|d| {
                std::iter::once(d.mask.source_id.as_str())
                    .chain(d.borders.iter().map(String::as_str))
            }))
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Role {
    Interior,
    Wall,
    Dimension,
    Other,
}

fn role(name: &str) -> Role {
    if crate::layers::is_named_wall(name) {
        return if name
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .any(|word| {
                matches!(
                    word,
                    "interior"
                        | "internal"
                        | "innervegg"
                        | "innervegger"
                        | "partition"
                        | "partitions"
                )
            }) {
            Role::Interior
        } else {
            Role::Wall
        };
    }
    if name
        .to_lowercase()
        .split(|c: char| !c.is_alphanumeric())
        .any(|word| {
            matches!(
                word,
                "dimension"
                    | "dimensions"
                    | "dim"
                    | "dims"
                    | "målsetting"
                    | "målsetning"
                    | "maalsetting"
                    | "måttsättning"
            )
        })
    {
        Role::Dimension
    } else {
        Role::Other
    }
}

fn white(g: &Graphics) -> bool {
    g.fill_alpha == 1.
        && g.default_rendering_intent
        && g.solid_fill_safe
        && g.fill_device.is_some()
        && safe(g)
        && match g.fill_ink {
            Some(NativeInk::Gray(v)) => v.map(f64::from_bits) == [1.],
            Some(NativeInk::Rgb(v)) => v.map(f64::from_bits) == [1., 1., 1.],
            Some(NativeInk::Cmyk(v)) => v.map(f64::from_bits) == [0., 0., 0., 0.],
            None => false,
        }
}

fn safe(g: &Graphics) -> bool {
    !g.masked
        && !g.finish_clip_unsafe
        && g.uncertain_clip.is_none()
        && g.material_state_safe
        && g.pen_state_safe
}

fn corners(r: Rect) -> [Point; 4] {
    [
        Point::new(r.l, r.t),
        Point::new(r.r, r.t),
        Point::new(r.r, r.b),
        Point::new(r.l, r.b),
    ]
}

fn key(r: Rect) -> [u64; 4] {
    [r.l, r.t, r.r, r.b].map(f64::to_bits)
}

#[derive(Clone, Copy)]
struct Quad {
    order: u64,
    layer: usize,
    outline: [Point; 4],
}

#[derive(Clone, Copy)]
struct Rail {
    order: u64,
    layer: usize,
    ends: [Point; 2],
    scope: Option<[u64; 4]>,
}

#[derive(Clone, Copy)]
struct Cover {
    order: u64,
    bounds: Rect,
    eraser: bool,
    box_seed: Option<(Quad, [u64; 4])>,
}

pub(super) struct Tracker {
    page: u32,
    bounds: Rect,
    layers: Vec<(String, Role)>,
    layer_ids: HashMap<String, usize>,
    quads: Vec<Quad>,
    rails: Vec<Rail>,
    covers: Vec<Cover>,
    exhausted: bool,
}

impl Tracker {
    pub(super) fn new(page: u32, bounds: Rect) -> Self {
        Self {
            page,
            bounds,
            layers: Vec::new(),
            layer_ids: HashMap::new(),
            quads: Vec::new(),
            rails: Vec::new(),
            covers: Vec::new(),
            exhausted: false,
        }
    }

    fn layer(&mut self, name: Option<&str>) -> Option<(usize, Role)> {
        let name = name?;
        if let Some(&i) = self.layer_ids.get(name) {
            return Some((i, self.layers[i].1));
        }
        if self.layers.len() >= 8192 {
            self.exhausted = true;
            return None;
        }
        let i = self.layers.len();
        let tag = role(name);
        self.layers.push((name.to_owned(), tag));
        self.layer_ids.insert(name.to_owned(), i);
        Some((i, tag))
    }

    fn can_record(&mut self) -> bool {
        self.exhausted |= self.quads.len() + self.rails.len() + self.covers.len() >= EVENT_LIMIT;
        !self.exhausted
    }

    #[allow(clippy::too_many_arguments)]
    pub(super) fn observe(
        &mut self,
        order: u64,
        subs: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
        layer: Option<&str>,
        _even_odd: bool,
    ) {
        let Some(clip) = g.clip.or(g.uncertain_clip) else {
            return;
        };
        if !self.can_record() {
            return;
        }
        let fill_seed = fill && !stroke && white(g);
        let hairline = stroke
            && !fill
            && g.stroke
            && !g.white_pen
            && g.stroke_alpha == 1.
            && safe(g)
            && g.default_rendering_intent
            && g.stroke_device.is_some()
            && g.pen_width == 0.
            && g.undashed_pen
            && !g.dashed
            && matches!(subs, [sub] if sub.curves == 0 && !sub.closed && sub.ps.len() == 2);
        let scope =
            if (fill_seed || hairline) && g.clip_planes.is_empty() && g.concave_clips.is_empty() {
                g.clip.map(key)
            } else {
                None
            };
        let label = (fill_seed || hairline).then(|| self.layer(layer)).flatten();
        let mut box_seed = None;
        let order = order.saturating_mul(2);
        if fill_seed
            && let Some((layer, role)) = label.filter(|(_, role)| *role != Role::Other)
            && let Some(outline) = material::paint_rectangle(subs)
            && g.contains_footprint(&outline)
        {
            let quad = Quad {
                order,
                layer,
                outline,
            };
            if role == Role::Dimension {
                box_seed = scope.map(|scope| (quad, scope));
            } else {
                self.quads.push(quad);
            }
        }
        if hairline
            && let Some((layer, role)) = label.filter(|(_, role)| *role != Role::Other)
            && (role != Role::Dimension || g.black_pen)
            && let [sub] = subs
            && let [a, b] = sub.ps.as_slice()
            && a.finite()
            && b.finite()
            && a.distance(*b) > 1e-8
            && g.contains_subpath(sub, false)
        {
            self.rails.push(Rail {
                order,
                layer,
                ends: [*a, *b],
                scope,
            });
        }
        let eraser = stroke && g.stroke_alpha != 0. && (g.white_pen || !safe(g));
        if !(fill && g.fill_alpha != 0. || eraser) {
            return;
        }
        let Some(bounds) = subs.iter().filter_map(Subpath::bounds).reduce(Rect::union) else {
            return;
        };
        let bounds = if eraser {
            let [a, b, c, d, _, _] = g.ctm.0;
            let reach = g.pen_width.abs() * (a.hypot(b) + c.hypot(d)) * g.miter_limit.max(2.);
            if reach.is_finite() {
                Rect {
                    l: bounds.l - reach,
                    t: bounds.t - reach,
                    r: bounds.r + reach,
                    b: bounds.b + reach,
                }
            } else {
                self.bounds
            }
        } else {
            bounds
        };
        if let Some(bounds) = inclusive_intersection(bounds, clip) {
            self.covers.push(Cover {
                order,
                bounds,
                eraser,
                box_seed,
            });
        }
    }

    pub(super) fn unknown(&mut self, order: u64, bounds: Option<Rect>) {
        if let Some(bounds) = bounds
            && self.can_record()
        {
            self.covers.push(Cover {
                order: order.saturating_mul(2).saturating_add(1),
                bounds,
                eraser: true,
                box_seed: None,
            });
        }
    }

    pub(super) fn text(&mut self, order: u64, g: &Graphics, known: bool) {
        let mode = g.tr.rem_euclid(4);
        let unknown = !known || g.masked || !g.material_state_safe || !g.pen_state_safe;
        let fill = matches!(mode, 0 | 2) && (g.fill_alpha != 0. || unknown) && (!g.fill || unknown);
        let stroke = matches!(mode, 1 | 2)
            && (g.stroke_alpha != 0. || unknown)
            && (g.white_pen || !g.stroke || unknown);
        if (4..=7).contains(&g.tr) || fill || stroke {
            self.unknown(order, g.clip.or(g.uncertain_clip));
        }
    }

    fn id(&self, order: u64) -> String {
        format!("pdf:p{}:path:{}:subpath:1", self.page, order / 2)
    }

    fn footprint(&self, q: Quad) -> Footprint {
        Footprint {
            source_id: self.id(q.order),
            layer: self.layers[q.layer].0.clone(),
            outline: q.outline,
        }
    }

    pub(super) fn finish(self, primitives: &[Primitive]) -> Vec<Receipt> {
        if self.exhausted || self.quads.is_empty() || self.rails.is_empty() {
            return Vec::new();
        }
        let mut work = WORK_LIMIT;
        let mut receipts = self.finish_checked(&mut work).unwrap_or_default();
        if !receipts.is_empty() {
            let by_id = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
            receipts.retain(|receipt| validate_one(self.page, receipt, &by_id));
        }
        receipts
    }

    fn finish_checked(&self, work: &mut usize) -> Option<Vec<Receipt>> {
        let quads = Index::build(
            self.bounds,
            self.quads.iter().map(|q| Rect::points(&q.outline)),
            work,
        )?;
        let rails = Index::build(
            self.bounds,
            self.rails.iter().map(|r| Rect::points(&r.ends)),
            work,
        )?;
        let covers = Index::build(
            self.bounds,
            self.covers.iter().map(|c| Some(c.bounds)),
            work,
        )?;
        let mut result = Vec::new();
        let mut seen = HashSet::new();
        let mut ambiguous = HashSet::new();
        for &q in &self.quads {
            if self.layers[q.layer].1 != Role::Interior {
                continue;
            }
            let Some(f) = Frame::new(q.outline) else {
                continue;
            };
            if f.length < f.width() * 10. {
                continue;
            }
            let rect = Rect::points(&q.outline)?;
            let pad = f.width() * 3.;
            let local = Rect {
                l: rect.l - pad,
                t: rect.t - pad,
                r: rect.r + pad,
                b: rect.b + pad,
            };
            let bounds = quads.near(local, work)?;
            *work = work.checked_sub(bounds.len().saturating_mul(3))?;
            if bounds.iter().map(|&i| self.quads[i]).any(|p| {
                p.layer == q.layer
                    && p.order != q.order
                    && f.rectangle(p.outline).is_some_and(|r| {
                        f.equal(r.x, [0., f.length]) && f.equal(r.y, [-f.half, f.half])
                    })
            }) {
                continue;
            }
            if self.facing(q, &f, -1., &bounds).is_none()
                || self.facing(q, &f, 1., &bounds).is_none()
            {
                continue;
            }
            let ink = rails.near(local, work)?;
            let paint = covers.near(local, work)?;
            let proof = self.prove(q, &f, &bounds, &ink, &paint, work)?;
            if let Some(proof) = proof {
                let extent = f.enclosure_key();
                if !seen.insert(extent) {
                    ambiguous.insert(extent);
                }
                result.push((extent, proof));
            }
        }
        Some(
            result
                .into_iter()
                .filter_map(|(k, p)| (!ambiguous.contains(&k)).then_some(p))
                .collect(),
        )
    }
}

fn inclusive_intersection(a: Rect, b: Rect) -> Option<Rect> {
    let r = Rect {
        l: a.l.max(b.l),
        t: a.t.max(b.t),
        r: a.r.min(b.r),
        b: a.b.min(b.b),
    };
    (r.l <= r.r && r.t <= r.b).then_some(r)
}

struct Index {
    page: Rect,
    bins: Vec<Vec<usize>>,
}

impl Index {
    fn build(
        page: Rect,
        events: impl Iterator<Item = Option<Rect>>,
        work: &mut usize,
    ) -> Option<Self> {
        let mut index = Self {
            page,
            bins: vec![Vec::new(); SIDE * SIDE],
        };
        for (i, rect) in events.enumerate() {
            if let Some(rect) = rect {
                for slot in Self::cells(page, rect) {
                    *work = work.checked_sub(1)?;
                    index.bins[slot].push(i);
                }
            }
        }
        Some(index)
    }

    fn cells(page: Rect, r: Rect) -> impl Iterator<Item = usize> {
        let at = |p: f64, lo: f64, hi: f64| {
            (((p - lo) / (hi - lo) * SIDE as f64) as usize).min(SIDE - 1)
        };
        let x = at(r.l, page.l, page.r)..=at(r.r, page.l, page.r);
        let y = at(r.t, page.t, page.b)..=at(r.b, page.t, page.b);
        y.flat_map(move |y| x.clone().map(move |x| y * SIDE + x))
    }

    fn near(&self, rect: Rect, work: &mut usize) -> Option<Vec<usize>> {
        let mut hits = Vec::new();
        for slot in Self::cells(self.page, rect) {
            *work = work.checked_sub(self.bins[slot].len().saturating_add(1))?;
            hits.extend(self.bins[slot].iter().copied());
        }
        hits.sort_unstable();
        hits.dedup();
        Some(hits)
    }
}

#[derive(Clone, Copy)]
struct Span {
    x: [f64; 2],
    y: [f64; 2],
}

impl Span {
    fn contains(self, other: Span, eps: f64) -> bool {
        let inside = |a: [f64; 2], b: [f64; 2]| a[0] <= b[0] + eps && a[1] >= b[1] - eps;
        inside(self.x, other.x) && inside(self.y, other.y)
    }
    fn overlap(self, other: Span, margin: f64) -> bool {
        self.x[1].min(other.x[1]) - self.x[0].max(other.x[0]) > margin
            && self.y[1].min(other.y[1]) - self.y[0].max(other.y[0]) > margin
    }
    fn joints(self) -> [Point; 4] {
        let [a, b] = self.x;
        let [c, d] = self.y;
        [
            Point::new(a, c),
            Point::new(b, c),
            Point::new(b, d),
            Point::new(a, d),
        ]
    }
}

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    half: f64,
    eps: f64,
}

impl Frame {
    fn new(q: [Point; 4]) -> Option<Self> {
        if !q.iter().all(|p| p.finite()) || !crate::placement::insulation::rectangle(&q) {
            return None;
        }
        let a = q[1] - q[0];
        let b = q[3] - q[0];
        let (long, short) = if a.norm() >= b.norm() { (a, b) } else { (b, a) };
        let length = long.norm();
        let width = short.norm();
        let mut origin = q[0] + short * 0.5;
        let mut axis = long.unit();
        let end = origin + long;
        if (origin.x, origin.y) > (end.x, end.y) {
            origin = end;
            axis = axis * -1.;
        }
        let eps =
            (q.iter().map(|p| p.norm()).fold(1., f64::max) * 128. * f64::EPSILON).max(width * 1e-6);
        (width > eps && length > eps && axis.dot(short).abs() <= eps).then_some(Self {
            origin,
            axis,
            length,
            half: width * 0.5,
            eps,
        })
    }
    fn at(&self, p: Point) -> Point {
        let p = p - self.origin;
        Point::new(self.axis.dot(p), self.axis.cross(p))
    }
    fn width(&self) -> f64 {
        2. * self.half
    }
    fn range<const N: usize>(&self, p: [Point; N]) -> Span {
        let p = p.map(|p| self.at(p));
        let extent = |coords: [f64; N]| {
            coords
                .into_iter()
                .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], v| {
                    [lo.min(v), hi.max(v)]
                })
        };
        Span {
            x: extent(p.map(|p| p.x)),
            y: extent(p.map(|p| p.y)),
        }
    }
    fn rectangle(&self, p: [Point; 4]) -> Option<Span> {
        if !p.iter().all(|p| p.finite()) {
            return None;
        }
        let ranges = self.range(p);
        let p = p.map(|p| self.at(p));
        (ranges.x[1] - ranges.x[0] > self.eps
            && ranges.y[1] - ranges.y[0] > self.eps
            && ranges
                .joints()
                .iter()
                .all(|&v| p.iter().any(|&q| q.distance(v) <= self.eps)))
        .then_some(ranges)
    }
    fn equal(&self, a: [f64; 2], b: [f64; 2]) -> bool {
        (a[0] - b[0]).abs().max((a[1] - b[1]).abs()) <= self.eps
    }
    fn segment(&self, a: [Point; 2], [b, c]: [Point; 2]) -> bool {
        let [x, y] = a.map(|p| self.at(p));
        x.distance(b) <= self.eps && y.distance(c) <= self.eps
            || x.distance(c) <= self.eps && y.distance(b) <= self.eps
    }
    fn enclosure_key(&self) -> [u64; 5] {
        let end = self.origin + self.axis * self.length;
        [self.origin.x, self.origin.y, end.x, end.y, self.width()].map(f64::to_bits)
    }
}

type WitnessQuad = (Quad, Span);

fn unique<T>(mut candidates: impl Iterator<Item = T>) -> Option<T> {
    let first = candidates.next()?;
    candidates.next().is_none().then_some(first)
}

impl Tracker {
    fn facing(&self, q: Quad, f: &Frame, side: f64, quads: &[usize]) -> Option<(Quad, f64)> {
        unique(
            quads
                .iter()
                .copied()
                .map(|i| self.quads[i])
                .filter_map(|p| {
                    if p.layer != q.layer || p.order == q.order {
                        return None;
                    }
                    let b = f.rectangle(p.outline)?;
                    let [inner, outer] = if side > 0. { b.y } else { [b.y[1], b.y[0]] };
                    let d = (outer - inner) * side;
                    (f.equal(b.x, [0., f.length])
                        && (inner - side * f.half).abs() <= f.eps
                        && (f.width() * 0.07..=f.width() * 0.23).contains(&d))
                    .then_some((p, outer))
                }),
        )
    }

    fn cap(
        &self,
        q: Quad,
        f: &Frame,
        side: usize,
        depth: f64,
        quads: &[usize],
        ink: &[usize],
        work: &mut usize,
    ) -> Option<Option<(WitnessQuad, [Rail; 2])>> {
        *work = work.checked_sub(quads.len().saturating_add(ink.len()))?;
        let terminal = [0., f.length][side];
        let cap = unique(
            quads
                .iter()
                .copied()
                .map(|i| self.quads[i])
                .filter_map(|p| {
                    if p.layer != q.layer || p.order == q.order {
                        return None;
                    }
                    let b = f.rectangle(p.outline)?;
                    (f.equal(b.y, [-f.half, f.half])
                        && (b.x[1 - side] - terminal).abs() <= f.eps
                        && (b.x[1] - b.x[0] - depth).abs() <= f.eps + depth * 0.01)
                        .then_some((p, b))
                }),
        );
        let Some(cap) = cap else {
            return Some(None);
        };
        let far = cap.1.x[side];
        let mut arms = [Vec::new(), Vec::new()];
        for &i in ink {
            let r = self.rails[i];
            if !matches!(self.layers[r.layer].1, Role::Interior | Role::Wall) {
                continue;
            }
            let b = f.range(r.ends);
            if !f.equal(b.x, [far, far]) || b.y[1] - b.y[0] < f.width() * 2. {
                continue;
            }
            if (b.y[1] + f.half).abs() <= f.eps {
                arms[0].push(r);
            }
            if (b.y[0] - f.half).abs() <= f.eps {
                arms[1].push(r);
            }
        }
        *work = work.checked_sub(arms[0].len().saturating_mul(arms[1].len()))?;
        for a in &arms[0] {
            if let Some(b) = arms[1]
                .iter()
                .find(|r| r.layer == a.layer && r.order != a.order)
            {
                return Some(Some((cap, [*a, *b])));
            }
        }
        Some(None)
    }

    fn annotated(&self, f: &Frame, c: Cover, work: &mut usize) -> Option<Option<BoxedDimension>> {
        let Some((seed, scope)) = c.box_seed else {
            return Some(None);
        };
        let Some(b) = f.rectangle(seed.outline) else {
            return Some(None);
        };
        let first = self.rails.partition_point(|r| r.order <= c.order);
        let last = self
            .rails
            .partition_point(|r| r.order <= c.order.saturating_add(16));
        let rails = &self.rails[first..last];
        *work = work.checked_sub(rails.len().saturating_mul(4))?;
        let corners = b.joints();
        let mut borders = [0; 4];
        for i in 0..4 {
            let Some(r) = rails.iter().find(|r| {
                r.layer == seed.layer
                    && r.scope == Some(scope)
                    && f.segment(r.ends, [corners[i], corners[(i + 1) % 4]])
            }) else {
                return Some(None);
            };
            borders[i] = r.order;
            if borders[..i].contains(&r.order) {
                return Some(None);
            }
        }
        Some(Some(BoxedDimension {
            mask: self.footprint(seed),
            borders: borders.map(|o| self.id(o)),
        }))
    }

    #[allow(clippy::too_many_arguments)]
    fn prove(
        &self,
        q: Quad,
        f: &Frame,
        quads: &[usize],
        ink: &[usize],
        paint: &[usize],
        work: &mut usize,
    ) -> Option<Option<Receipt>> {
        *work = work.checked_sub(quads.len().saturating_mul(2))?;
        let Some((left, y0)) = self.facing(q, f, -1., quads) else {
            return Some(None);
        };
        let Some((right, y1)) = self.facing(q, f, 1., quads) else {
            return Some(None);
        };
        let [d0, d1] = [-f.half - y0, y1 - f.half];
        let depth = d0.min(d1);
        if (d0 - d1).abs() > f.eps + depth * 0.02 {
            return Some(None);
        }
        let after = [q.order, left.order, right.order].into_iter().max()?;
        *work = work.checked_sub(ink.len().saturating_mul(4))?;
        let lines = [y0, -f.half, f.half, y1].map(|y| {
            ink.iter().map(|&i| self.rails[i]).find(|r| {
                r.layer == q.layer
                    && r.order > after
                    && f.segment(r.ends, [Point::new(0., y), Point::new(f.length, y)])
            })
        });
        let [Some(a), Some(b), Some(c), Some(d)] = lines else {
            return Some(None);
        };
        let Some((first, first_arms)) = self.cap(q, f, 0, depth, quads, ink, work)? else {
            return Some(None);
        };
        let Some((last, last_arms)) = self.cap(q, f, 1, depth, quads, ink, work)? else {
            return Some(None);
        };
        if [left.order, right.order, first.0.order, last.0.order]
            .into_iter()
            .any(|o| o == q.order)
            || first.0.order == last.0.order
        {
            return Some(None);
        }
        let material = Span {
            x: [0., f.length],
            y: [y0, y1],
        };
        let owned = [
            q.order,
            left.order,
            right.order,
            first.0.order,
            last.0.order,
        ];
        let earliest = *owned.iter().min()?;
        let mut dimensions = Vec::new();
        let mut intervals = Vec::new();
        *work = work.checked_sub(paint.len())?;
        for cover in paint
            .iter()
            .map(|&i| self.covers[i])
            .filter(|c| !owned.contains(&c.order))
        {
            let r = f.range(corners(cover.bounds));
            // A structural return must still reach the cap. A distant cover
            // elsewhere along that perpendicular wall is irrelevant.
            let near_arms = first_arms.iter().chain(&last_arms).any(|rail| {
                if cover.order <= rail.order {
                    return false;
                }
                let a = f.range(rail.ends);
                let y = if a.y[1] < 0. {
                    [a.y[1] - f.width() * 2., a.y[1]]
                } else {
                    [a.y[0], a.y[0] + f.width() * 2.]
                };
                let strip = Span {
                    x: [a.x[0] - f.eps * 4., a.x[1] + f.eps * 4.],
                    y,
                };
                if cover.eraser {
                    r.overlap(strip, -f.eps)
                } else {
                    r.contains(strip, f.eps)
                        || r.x[0] < a.x[0] - f.eps
                            && r.x[1] > a.x[0] + f.eps
                            && r.overlap(strip, f.eps)
                }
            });
            if near_arms {
                return Some(None);
            }
            if cover.order <= earliest {
                continue;
            }
            let margin = if cover.eraser { -f.eps } else { f.eps };
            if r.overlap(first.1, margin) || r.overlap(last.1, margin) {
                return Some(None);
            }
            if !r.overlap(material, margin) {
                continue;
            }
            if cover.eraser
                || r.x[0] <= f.eps
                || r.x[1] >= f.length - f.eps
                || r.x[1] - r.x[0] > f.width() * 3.
            {
                return Some(None);
            }
            let Some(mask) = self.annotated(f, cover, work)? else {
                return Some(None);
            };
            intervals.push(r.x);
            dimensions.push(mask);
        }
        intervals.sort_by(|a, b| a[0].total_cmp(&b[0]));
        let mut end = 0.0_f64;
        let mut total = 0.;
        for [a, b] in intervals {
            total += (b - end.max(a)).max(0.);
            end = end.max(b);
        }
        if total > f.length * 0.12 {
            return Some(None);
        }
        let rails = [a, b, c, d].map(|r| self.id(r.order));
        let returns = [first_arms, last_arms].map(|r| r.map(|r| self.id(r.order)));
        Some(Some(Receipt {
            core: self.footprint(q),
            facings: [left, right].map(|p| self.footprint(p)),
            caps: [first.0, last.0].map(|p| self.footprint(p)),
            rails,
            returns,
            dimensions,
        }))
    }
}

fn source_order(page: u32, id: &str) -> Option<u64> {
    let mut p = id.split(':');
    if p.next()? != "pdf"
        || p.next()?.strip_prefix('p')?.parse::<u32>().ok()? != page
        || p.next()? != "path"
    {
        return None;
    }
    let order = p.next()?.parse::<u64>().ok()?.checked_mul(2)?;
    (order > 0 && p.next()? == "subpath" && p.next()? == "1" && p.next().is_none()).then_some(order)
}

/// Recompute each finite proof and require every named rail to be the exact
/// normalized line from the same native operation. The white footprints
/// remain independent of the published primitive and normal paint arrays.
pub(crate) fn validate(
    page: u32,
    receipts: &[Receipt],
    primitives: &[Primitive],
) -> Result<(), String> {
    if receipts.is_empty() {
        return Ok(());
    }
    let by_id: HashMap<_, _> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut used = HashSet::new();
    for receipt in receipts {
        if !used.insert(receipt.core.source_id.as_str())
            || receipt.dimensions.len() > 24
            || !validate_one(page, receipt, &by_id)
        {
            return Err("invalid native white-wall framing receipt".into());
        }
    }
    Ok(())
}

fn validate_one(page: u32, receipt: &Receipt, primitives: &HashMap<&str, &Primitive>) -> bool {
    fn measured(
        page: u32,
        receipt: &Receipt,
        primitives: &HashMap<&str, &Primitive>,
    ) -> Option<bool> {
        let frame = Frame::new(receipt.core.outline)?;
        if frame.length < frame.width() * 10. {
            return Some(false);
        }
        let bounds = Rect::points(&receipt.core.outline)?;
        let mut t = Tracker::new(page, bounds);
        for p in receipt.footprints() {
            let (layer, role) = t.layer(Some(&p.layer))?;
            if role != Role::Interior {
                return Some(false);
            }
            t.quads.push(Quad {
                order: source_order(page, &p.source_id)?,
                layer,
                outline: p.outline,
            });
        }
        for id in receipt
            .rails
            .iter()
            .chain(receipt.returns.iter().flatten())
            .chain(receipt.dimensions.iter().flat_map(|m| m.borders.iter()))
        {
            let p = *primitives.get(id.as_str())?;
            let Shape::Line { start, end } = p.shape else {
                return Some(false);
            };
            let (layer, role) = t.layer(p.layer.as_deref())?;
            if role == Role::Other || !start.finite() || !end.finite() || p.stroke_width.is_some() {
                return Some(false);
            }
            t.rails.push(Rail {
                order: source_order(page, id)?,
                layer,
                ends: [start, end],
                scope: Some([0; 4]),
            });
        }
        t.rails.sort_unstable_by_key(|r| r.order);
        let mut proof_ids = HashSet::new();
        if !receipt.source_ids().all(|id| proof_ids.insert(id)) {
            return Some(false);
        }
        for d in &receipt.dimensions {
            let m = &d.mask;
            let (layer, role) = t.layer(Some(&m.layer))?;
            if role != Role::Dimension {
                return Some(false);
            }
            let order = source_order(page, &m.source_id)?;
            t.covers.push(Cover {
                order,
                bounds: Rect::points(&m.outline)?,
                eraser: false,
                box_seed: Some((
                    Quad {
                        order,
                        layer,
                        outline: m.outline,
                    },
                    [0; 4],
                )),
            });
        }
        let index = |n: usize| (0..n).collect::<Vec<_>>();
        let mut work = WORK_LIMIT;
        let candidate = t.prove(
            t.quads[0],
            &frame,
            &index(t.quads.len()),
            &index(t.rails.len()),
            &index(t.covers.len()),
            &mut work,
        )?;
        Some(candidate.as_ref() == Some(receipt))
    }
    measured(page, receipt, primitives).unwrap_or(false)
}
