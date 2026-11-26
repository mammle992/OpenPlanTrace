//! Preserve a long native wall strip when the visible backing is independently signed.
use super::{Cover, Event, Key, Pen, Point, Rect, precision};
use crate::model::{PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};

const MAX_RECEIPTS: usize = 48;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub face_source_ids: [String; 2],
    pub backing_pen_source_ids: [String; 2],
    pub end_cap_source_id: String,
    pub terminal_trim_source_ids: Vec<String>,
    pub neutral_overlap_source_ids: Vec<String>,
    pub outline: [Point; 4],
}

struct Form {
    origin: Point,
    axis: Point,
    side: f64,
    length: f64,
    width: f64,
    common: f64,
    eps: f64,
}

fn near(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}
fn order(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}
fn ranges(points: impl IntoIterator<Item = Point>) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], q| {
            [
                [x[0].min(q.x), x[1].max(q.x)],
                [y[0].min(q.y), y[1].max(q.y)],
            ]
        })
}

impl Form {
    fn new(points: &[Point]) -> Option<Self> {
        if !(4..=10).contains(&points.len()) || !points.iter().all(|p| p.finite()) {
            return None;
        }
        let mut ring = points.to_vec();
        if ring.first() == ring.last() {
            ring.pop();
        }
        let math = precision(&ring);
        let corners = ring
            .iter()
            .copied()
            .enumerate()
            .filter_map(|(i, b)| {
                let a = ring[(i + ring.len() - 1) % ring.len()];
                let c = ring[(i + 1) % ring.len()];
                (b != a
                    && b != c
                    && ((b - a).unit().cross((c - b).unit()).abs() > math
                        || (b - a).dot(c - b) < 0.))
                    .then_some(b)
            })
            .collect::<Vec<_>>();
        let [a, b, c, d]: [Point; 4] = corners.try_into().ok()?;
        let ring = [a, b, c, d];
        let (a, b) = (0..4)
            .map(|i| (ring[i], ring[(i + 1) % 4]))
            .max_by(|(a, b), (c, d)| a.distance(*b).total_cmp(&c.distance(*d)))?;
        let axis = (b - a).unit();
        let side = ring
            .iter()
            .map(|p| axis.cross(*p - a))
            .find(|v| v.abs() > math)?
            .signum();
        for (origin, d, sign) in [(a, axis, side), (b, axis * -1., -side)] {
            let mut f = Self {
                origin,
                axis: d,
                side: sign,
                length: a.distance(b),
                width: 0.,
                common: 0.,
                eps: math,
            };
            let local = ring.map(|q| f.local(q));
            let [[lo, x], [bottom, w]] = ranges(local);
            if !near(lo, 0., math)
                || !near(bottom, 0., math)
                || !near(x, f.length, math)
                || w <= math
                || f.length < 32. * w
            {
                return None;
            }
            let mut inner = local.into_iter().filter(|p| near(p.y, w, math));
            let p = inner.next()?;
            let q = inner.next()?;
            let [t0, t1] = order(p.x, q.x);
            if inner.next().is_some()
                || !near(t1, f.length, math)
                || !(0.008..=0.09).contains(&(t0 / w))
            {
                continue;
            }
            // A single genuine bevel at one end and square at the signed end.
            let expected = [
                Point::new(0., 0.),
                Point::new(f.length, 0.),
                Point::new(f.length, w),
                Point::new(t0, w),
            ];
            if !expected
                .iter()
                .all(|p| local.iter().any(|q| p.distance(*q) <= math))
            {
                continue;
            }
            let turn = local
                .iter()
                .zip(local.iter().cycle().skip(1))
                .zip(local.iter().cycle().skip(2))
                .map(|((&a, &b), &c)| (b - a).cross(c - b))
                .collect::<Vec<_>>();
            if !turn.iter().all(|&v| v > math) && !turn.iter().all(|&v| v < -math) {
                continue;
            }
            f.width = w;
            f.common = t0;
            f.eps = math.max(w * 0.0001);
            return Some(f);
        }
        None
    }

    fn local(&self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(d.dot(self.axis), self.side * self.axis.cross(d))
    }
    fn world(&self, x: f64, y: f64) -> Point {
        self.origin + self.axis * x + Point::new(-self.axis.y, self.axis.x) * (self.side * y)
    }
    fn outline(&self, x: f64) -> [Point; 4] {
        [
            self.world(x, self.width),
            self.world(self.length, self.width),
            self.world(self.length, 0.),
            self.world(x, 0.),
        ]
    }
    fn pass(&self, p: [Point; 2]) -> [[f64; 2]; 2] {
        ranges(p.map(|p| self.local(p)))
    }
    fn eq(&self, a: f64, b: f64) -> bool {
        near(a, b, self.eps)
    }
    fn rail(&self, p: [Point; 2], lane: f64, end: f64) -> bool {
        let [[a, b], [lo, hi]] = self.pass(p);
        self.eq(a, self.common) && self.eq(b, end) && self.eq(lo, lane) && self.eq(hi, lane)
    }
    fn cap(&self, p: [Point; 2], at: f64) -> bool {
        let [[a, b], [lo, hi]] = self.pass(p);
        self.eq(a, at) && self.eq(b, at) && self.eq(lo, 0.) && self.eq(hi, self.width)
    }
    fn trim(&self, points: &[Point]) -> Option<f64> {
        if !(3..=10).contains(&points.len()) || !points.iter().all(|p| p.finite()) {
            return None;
        }
        let [[a, b], [lo, hi]] = ranges(points.iter().map(|&p| self.local(p)));
        (a <= -0.5 * self.width
            && b > self.common + self.eps
            && b < self.common + 0.85 * self.width
            && lo >= -0.10 * self.width
            && hi <= 1.10 * self.width
            && hi - lo > self.eps)
            .then_some(b)
    }
    fn neutral(&self, p: [Point; 2], width: f64, thin: f64, backing: f64) -> Option<[f64; 2]> {
        let [[a, b], [lo, hi]] = self.pass(p);
        (self.eq(lo, self.width)
            && self.eq(hi, self.width)
            && self.eq(width, thin)
            && width <= 0.45 * backing
            && b - a >= 3. * self.width
            && a >= self.common - 3. * self.width
            && b <= self.length + 9. * self.width)
            .then_some([a, b])
    }
}

pub(super) fn eligible(points: &[Point]) -> bool {
    Form::new(points).is_some()
}

#[derive(Clone, Copy)]
struct Stroke {
    points: [Point; 2],
    width: f64,
}

fn native(p: &Primitive) -> Option<Stroke> {
    let Shape::Line { start, end } = p.shape else {
        return None;
    };
    let width = p.stroke_width?;
    (p.layer.is_none() && width.is_finite() && width > 0.).then_some(Stroke {
        points: [start, end],
        width,
    })
}

fn fill(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    p.layer.is_none().then_some(points.as_slice())
}

fn one(ink: &[Pen], f: impl Fn(Pen) -> bool) -> Option<Pen> {
    let mut hits = ink.iter().copied().filter(|&p| f(p));
    let m = hits.next()?;
    hits.next().is_none().then_some(m)
}

pub(super) fn derive(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    page: u32,
) -> Vec<Receipt> {
    collect(keys, events, primitives, page).unwrap_or_default()
}

fn collect(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    page: u32,
) -> Option<Vec<Receipt>> {
    if keys.is_empty() {
        return Some(vec![]);
    }
    let sources: HashMap<_, _> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut work = MAX_WORK;
    let mut out = BTreeMap::new();
    'stock: for &key in keys {
        let Some(f) = sources
            .get(key.id(page).as_str())
            .and_then(|p| fill(p))
            .and_then(Form::new)
        else {
            continue;
        };
        let region = Rect::points(&f.outline(f.common))?;
        work = work.checked_sub(events.len())?;
        let nearby = events
            .iter()
            .filter(|o| o.order > key.0 * 2 && region.intersect(o.bounds).is_some())
            .collect::<Vec<_>>();
        // Thick witness centerlines sit just beyond both gray faces.
        let halo = Rect::points(&[
            f.world(f.common - 0.75 * f.width, -0.75 * f.width),
            f.world(f.length + 0.75 * f.width, -0.75 * f.width),
            f.world(f.common - 0.75 * f.width, 1.75 * f.width),
            f.world(f.length + 0.75 * f.width, 1.75 * f.width),
        ])?;
        work = work.checked_sub(events.len())?;
        let ink = events
            .iter()
            .filter_map(|e| {
                let Cover::Stroke(p) = e.cover else {
                    return None;
                };
                (e.order > key.0 * 2
                    && p.layerless
                    && p.gray == 0.
                    && halo.intersect(e.bounds).is_some()
                    && sources
                        .get(p.key.id(page).as_str())
                        .and_then(|p| native(p))
                        .is_some_and(|s| f.eq(s.width, p.width)))
                .then_some(p)
            })
            .collect::<Vec<_>>();
        work = work.checked_sub(ink.len().saturating_mul(5))?;
        let Some(a) = one(&ink, |p| {
            f.rail(p.points, 0., f.length) && (0.025..=0.22).contains(&(p.width / f.width))
        }) else {
            continue;
        };
        let Some(b) = one(&ink, |p| {
            f.rail(p.points, f.width, f.length - f.width) && f.eq(p.width, a.width)
        }) else {
            continue;
        };
        let Some(c) = one(&ink, |p| {
            let offset = -f.pass(p.points)[1][0];
            (0.015..=0.08).contains(&(offset / f.width))
                && (0.23..=0.48).contains(&(p.width / f.width))
                && a.width <= 0.45 * p.width
                && f.rail(p.points, -offset, f.length + offset)
        }) else {
            continue;
        };
        let delta = -f.pass(c.points)[1][0];
        let Some(d) = one(&ink, |p| {
            f.rail(p.points, f.width + delta, f.length - f.width - delta) && f.eq(p.width, c.width)
        }) else {
            continue;
        };
        let Some(cap) = one(&ink, |p| {
            f.cap(p.points, f.length + delta) && f.eq(p.width, c.width)
        }) else {
            continue;
        };
        let oldest = [a, b, c, d, cap].into_iter().map(|p| p.key.0 * 2).min()?;
        let mut trim = Vec::new();
        let mut survey = Vec::new();
        let mut start = f.common;
        for e in nearby {
            match e.cover {
                Cover::NeutralArea { key, .. } => {
                    let Some(limit) = sources
                        .get(key.id(page).as_str())
                        .and_then(|p| fill(p))
                        .and_then(|p| f.trim(p))
                    else {
                        continue 'stock;
                    };
                    if e.order >= oldest
                        || trim.len() >= 4
                        || start > f.common + f.eps && !f.eq(limit, start)
                    {
                        continue 'stock;
                    }
                    start = limit;
                    trim.push(key);
                }
                Cover::Stroke(p) if p.gray == 0. => {}
                Cover::Stroke(p)
                    if p.layerless
                        && e.order < oldest
                        && (0.08..=0.72).contains(&p.gray)
                        && f.eq(p.width, a.width) =>
                {
                    let [[x, y], [u, v]] = f.pass(p.points);
                    let legal = (y - x - (v - u)).abs() <= 0.03 * f.width
                        && u >= -0.10 * f.width
                        && v <= 1.10 * f.width;
                    if !legal {
                        continue 'stock;
                    }
                }
                Cover::Stroke(p) if p.layerless && (0.45..=0.86).contains(&p.gray) => {
                    let Some(x) = f.neutral(p.points, p.width, a.width, d.width) else {
                        continue 'stock;
                    };
                    if survey.len() >= 64
                        || sources
                            .get(p.key.id(page).as_str())
                            .and_then(|p| native(p))
                            .is_none()
                    {
                        continue 'stock;
                    }
                    survey.push((x, p.key));
                }
                _ => continue 'stock,
            }
        }
        survey.sort_by(|a, b| a.0[0].total_cmp(&b.0[0]));
        if survey.windows(2).any(|p| p[1].0[0] <= p[0].0[1] + f.eps)
            || survey
                .iter()
                .map(|(x, _)| (x[1].min(f.length) - x[0].max(start)).max(0.))
                .sum::<f64>()
                > 0.80 * (f.length - start)
        {
            continue;
        }
        let r = Receipt {
            paint_source_id: key.id(page),
            face_source_ids: [a.key.id(page), b.key.id(page)],
            backing_pen_source_ids: [c.key.id(page), d.key.id(page)],
            end_cap_source_id: cap.key.id(page),
            terminal_trim_source_ids: trim.into_iter().map(|p| p.id(page)).collect(),
            neutral_overlap_source_ids: survey.into_iter().map(|(_, p)| p.id(page)).collect(),
            outline: f.outline(start),
        };
        if interpreted(&r, &sources, page).is_some() {
            out.insert(key, r);
        }
        if out.len() > MAX_RECEIPTS {
            return None;
        }
    }
    Some(out.into_values().collect())
}

fn interpreted(r: &Receipt, sources: &HashMap<&str, &Primitive>, page: u32) -> Option<[Point; 4]> {
    let paint = Key::from_id(&r.paint_source_id, page)?;
    let f = Form::new(fill(sources.get(r.paint_source_id.as_str())?)?)?;
    let line = |id: &str| {
        let key = Key::from_id(id, page)?;
        (key.0 > paint.0)
            .then(|| sources.get(id).and_then(|p| native(p)).map(|p| (key, p)))
            .flatten()
    };
    let (ka, a) = line(&r.face_source_ids[0])?;
    let (kb, b) = line(&r.face_source_ids[1])?;
    let (kc, c) = line(&r.backing_pen_source_ids[0])?;
    let (kd, d) = line(&r.backing_pen_source_ids[1])?;
    let (ke, cap) = line(&r.end_cap_source_id)?;
    let mut all = [ka.0, kb.0, kc.0, kd.0, ke.0];
    all.sort_unstable();
    let delta = -f.pass(c.points)[1][0];
    if all.windows(2).any(|p| p[0] == p[1])
        || !(0.015..=0.08).contains(&(delta / f.width))
        || !(0.025..=0.22).contains(&(a.width / f.width))
        || !(0.23..=0.48).contains(&(c.width / f.width))
        || a.width > 0.45 * c.width
        || !f.eq(a.width, b.width)
        || !f.eq(c.width, d.width)
        || !f.eq(c.width, cap.width)
        || !f.rail(a.points, 0., f.length)
        || !f.rail(b.points, f.width, f.length - f.width)
        || !f.rail(c.points, -delta, f.length + delta)
        || !f.rail(d.points, f.width + delta, f.length - f.width - delta)
        || !f.cap(cap.points, f.length + delta)
        || r.terminal_trim_source_ids.len() > 4
        || r.neutral_overlap_source_ids.len() > 64
    {
        return None;
    }
    let mut start = f.common;
    let mut last = paint;
    for id in &r.terminal_trim_source_ids {
        let key = Key::from_id(id, page)?;
        if key <= last || key.0 >= all[0] {
            return None;
        }
        last = key;
        let limit = f.trim(fill(sources.get(id.as_str())?)?)?;
        if start > f.common + f.eps && !f.eq(start, limit) {
            return None;
        }
        start = limit;
    }
    let mut last = f64::NEG_INFINITY;
    let mut length = 0.;
    for id in &r.neutral_overlap_source_ids {
        let (key, p) = line(id)?;
        let [x, y] = f.neutral(p.points, p.width, a.width, d.width)?;
        if key.0 <= kd.0 || x <= last + f.eps {
            return None;
        }
        last = y;
        length += (y.min(f.length) - x.max(start)).max(0.);
    }
    let outline = f.outline(start);
    if length > 0.80 * (f.length - start)
        || !r
            .outline
            .into_iter()
            .zip(outline)
            .all(|(a, b)| a.finite() && a.distance(b) <= f.eps)
    {
        return None;
    }
    Some(outline)
}

pub(crate) fn verified(page: &PageScan, receipt: &Receipt) -> Option<[Point; 4]> {
    let sources = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    interpreted(receipt, &sources, page.page_number)
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    if page.capped_stock.is_empty() {
        return Ok(());
    }
    let bad = || "invalid source-bounded capped wall stock".to_owned();
    if page.capped_stock.len() > MAX_RECEIPTS
        || !page.pdf_input.as_ref().is_some_and(|p| {
            matches!(
                p.route,
                crate::input::raster::PdfRoute::Vector | crate::input::raster::PdfRoute::Mixed
            )
        })
    {
        return Err(bad());
    }
    let sources = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let mut prev = None;
    for r in &page.capped_stock {
        let key = Key::from_id(&r.paint_source_id, page.page_number).ok_or_else(bad)?;
        if prev.is_some_and(|p| p >= key) || interpreted(r, &sources, page.page_number).is_none() {
            return Err(bad());
        }
        prev = Some(key);
    }
    Ok(())
}
