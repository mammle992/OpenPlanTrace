//! Measured native paint where one continuous face has shallow authored steps.
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
    pub outer_face_source_id: String,
    pub inner_face_source_ids: Vec<String>,
    pub return_source_ids: Vec<String>,
    pub end_cap_source_id: String,
    pub neutral_crossing_source_ids: Vec<String>,
}

pub(crate) struct Verified {
    pub outlines: Vec<[Point; 4]>,
    pub widths: Vec<f64>,
}

#[derive(Clone, Copy)]
pub(super) struct Band {
    pub(super) a: f64,
    pub(super) b: f64,
    pub(super) depth: f64,
}

pub(super) struct Form {
    pub(super) origin: Point,
    pub(super) axis: Point,
    pub(super) side: f64,
    pub(super) length: f64,
    pub(super) width: f64,
    pub(super) eps: f64,
    pub(super) bands: Vec<Band>,
}

fn range(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}
fn eq(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}

impl Form {
    pub(super) fn new(points: &[Point]) -> Option<Self> {
        if !(10..=24).contains(&points.len()) || !points.iter().all(|p| p.finite()) {
            return None;
        }
        let mut points = points.to_vec();
        if points.first() == points.last() {
            points.pop();
        }
        let math = precision(&points);
        let ring = points
            .iter()
            .copied()
            .enumerate()
            .filter_map(|(i, b)| {
                let a = points[(i + points.len() - 1) % points.len()];
                let c = points[(i + 1) % points.len()];
                (b != a
                    && b != c
                    && ((b - a).unit().cross((c - b).unit()).abs() > math
                        || (b - a).dot(c - b) < 0.))
                    .then_some(b)
            })
            .collect::<Vec<_>>();
        let edges = ring
            .iter()
            .copied()
            .zip(ring.iter().copied().cycle().skip(1))
            .collect::<Vec<_>>();
        let &(x, y) = edges
            .iter()
            .max_by(|(a, b), (c, d)| a.distance(*b).total_cmp(&c.distance(*d)))?;
        let axis = (y - x).unit();
        let side = ring
            .iter()
            .map(|p| axis.cross(*p - x))
            .find(|v| v.abs() > math)?
            .signum();
        for (origin, direction, sign) in [(x, axis, side), (y, axis * -1., -side)] {
            let mut f = Self {
                origin,
                axis: direction,
                side: sign,
                length: x.distance(y),
                width: 0.,
                eps: math,
                bands: vec![],
            };
            let local: Vec<_> = ring.iter().map(|&p| f.local(p)).collect();
            f.width = local.iter().map(|p| p.y).fold(0., f64::max);
            if f.width <= math || f.length < 30. * f.width || local.iter().any(|p| p.y < -math) {
                return None;
            }
            f.eps = f.eps.max(f.width * 0.0001);
            let mut other = vec![];
            let mut outer = 0;
            for (&a, &b) in local.iter().zip(local.iter().cycle().skip(1)) {
                let [u, v] = range(a.x, b.x);
                if eq(a.y, b.y, math) && a.y > math && v - u > math {
                    f.bands.push(Band {
                        a: u,
                        b: v,
                        depth: (a.y + b.y) * 0.5,
                    });
                } else if eq(a.y, 0., math)
                    && eq(b.y, 0., math)
                    && eq(u, 0., math)
                    && eq(v, f.length, math)
                {
                    outer += 1;
                } else {
                    other.push((a, b));
                }
            }
            f.bands.sort_by(|a, b| a.a.total_cmp(&b.a));
            if outer != 1
                || !matches!(f.bands.len(), 3 | 5 | 7 | 9)
                || other.len() != f.bands.len() + 1
            {
                continue;
            }
            let first = f.bands.first()?;
            let last = f.bands.last()?;
            if first.a < -math || first.a > f.width * 0.08 || !eq(last.b, f.length, math) {
                continue;
            }
            let endpoints =
                |x: f64, a: f64, b: f64| f.has_edge(&other, Point::new(x, a), Point::new(x, b));
            if !f.has_edge(&other, Point::new(0., 0.), Point::new(first.a, f.width))
                || !endpoints(f.length, f.width, 0.)
            {
                continue;
            }
            if !f.bands.iter().enumerate().all(|(i, b)| {
                if i % 2 == 0 {
                    eq(b.depth, f.width, math) && b.b - b.a >= 8. * f.width
                } else {
                    (0.04..=0.22).contains(&((f.width - b.depth) / f.width))
                        && (0.65..=3.).contains(&((b.b - b.a) / f.width))
                }
            }) || !f
                .bands
                .windows(2)
                .all(|p| eq(p[0].b, p[1].a, math) && endpoints(p[0].b, p[0].depth, p[1].depth))
            {
                continue;
            }
            return Some(f);
        }
        None
    }

    pub(super) fn local(&self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(d.dot(self.axis), self.side * self.axis.cross(d))
    }

    pub(super) fn world(&self, x: f64, y: f64) -> Point {
        self.origin + self.axis * x + Point::new(-self.axis.y, self.axis.x) * (self.side * y)
    }

    fn has_edge(&self, edges: &[(Point, Point)], p: Point, q: Point) -> bool {
        edges.iter().any(|&(a, b)| {
            a.distance(p) <= self.eps && b.distance(q) <= self.eps
                || a.distance(q) <= self.eps && b.distance(p) <= self.eps
        })
    }

    pub(super) fn outline(&self, b: Band) -> [Point; 4] {
        [
            self.world(b.a, b.depth),
            self.world(b.b, b.depth),
            self.world(b.b, 0.),
            self.world(b.a, 0.),
        ]
    }

    fn pass(&self, p: Point, q: Point) -> ([f64; 2], [f64; 2]) {
        let a = self.local(p);
        let b = self.local(q);
        (range(a.x, b.x), range(a.y, b.y))
    }

    fn rail(&self, p: [Point; 2], depth: f64, x: [f64; 2], bounded_terminal: bool) -> bool {
        let ([a, b], [lo, hi]) = self.pass(p[0], p[1]);
        eq(lo, depth, self.eps)
            && eq(hi, depth, self.eps)
            && eq(a, x[0], self.eps)
            && (eq(b, x[1], self.eps)
                || bounded_terminal && b < x[1] && x[1] - b <= 1.10 * self.width)
    }

    fn cross(&self, p: [Point; 2], at: f64, depth: [f64; 2]) -> bool {
        let ([a, b], [lo, hi]) = self.pass(p[0], p[1]);
        eq(a, at, self.eps)
            && eq(b, at, self.eps)
            && lo <= depth[0] + self.eps
            && hi >= depth[1] - self.eps
            && lo >= depth[0] - 0.08 * self.width
            && hi <= depth[1] + 0.08 * self.width
    }

    fn neutral_crossing(&self, p: [Point; 2], width: f64) -> Option<f64> {
        let ([a, b], [lo, hi]) = self.pass(p[0], p[1]);
        (eq(a, b, self.eps)
            && lo <= -5. * self.width
            && hi >= 6. * self.width
            && (0.025..=0.17).contains(&(width / self.width))
            && a >= self.bands[0].a - 0.25 * self.width
            && a <= self.length + 0.25 * self.width)
            .then_some(a)
    }

    fn inside_centerline(&self, p: [Point; 2]) -> bool {
        let [a, b] = p.map(|p| self.local(p));
        self.bands.iter().any(|s| {
            let r = Rect {
                l: s.a + self.eps,
                r: s.b - self.eps,
                t: self.eps,
                b: s.depth - self.eps,
            };
            r.clip(a, b).is_some()
        })
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

fn unique(ink: &[Pen], test: impl Fn(Pen) -> bool) -> Option<Pen> {
    let mut all = ink.iter().copied().filter(|&p| test(p));
    let a = all.next()?;
    all.next().is_none().then_some(a)
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
        let Some(p) = sources.get(key.id(page).as_str()) else {
            continue;
        };
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
        else {
            continue;
        };
        let Some(f) = Form::new(points) else { continue };
        let first = f.bands.first()?;
        let region = Rect::points(&f.outline(Band {
            a: first.a,
            b: f.length,
            depth: f.width,
        }))?;
        work = work.checked_sub(events.len())?;
        let nearby = events
            .iter()
            .filter(|o| o.order > key.0 * 2 && region.intersect(o.bounds).is_some())
            .collect::<Vec<_>>();
        let ink = nearby
            .iter()
            .filter_map(|o| {
                let Cover::Stroke(p) = o.cover else {
                    return None;
                };
                let source = native(sources.get(p.key.id(page).as_str())?)?;
                (p.layerless
                    && p.gray == 0.
                    && (0.025..=0.22).contains(&(p.width / f.width))
                    && eq(p.width, source.width, f.eps))
                .then_some(p)
            })
            .collect::<Vec<_>>();
        work = work.checked_sub(
            ink.len()
                .saturating_mul(f.bands.len().saturating_mul(3) + 2),
        )?;
        let Some(outer) = unique(&ink, |p| f.rail(p.points, 0., [first.a, f.length], false)) else {
            continue;
        };
        let mut inners = Vec::new();
        for (i, b) in f.bands.iter().enumerate() {
            let Some(p) = unique(&ink, |p| {
                eq(p.width, outer.width, f.eps)
                    && f.rail(p.points, b.depth, [b.a, b.b], i + 1 == f.bands.len())
            }) else {
                continue 'stock;
            };
            inners.push(p);
        }
        let mut returns = Vec::new();
        for q in f.bands.windows(2) {
            let Some(p) = unique(&ink, |p| {
                eq(p.width, outer.width, f.eps)
                    && f.cross(p.points, q[0].b, range(q[0].depth, q[1].depth))
            }) else {
                continue 'stock;
            };
            returns.push(p);
        }
        let Some(cap) = unique(&ink, |p| {
            eq(p.width, outer.width, f.eps) && f.cross(p.points, f.length, [0., f.width])
        }) else {
            continue;
        };
        let earliest = std::iter::once(&outer)
            .chain(&inners)
            .chain(&returns)
            .chain(std::iter::once(&cap))
            .map(|p| p.key.0 * 2)
            .min()?;
        let mut muted = vec![];
        for e in nearby {
            let Cover::Stroke(p) = e.cover else {
                continue 'stock;
            };
            if p.gray == 0. {
                continue;
            }
            if e.order < earliest
                && p.layerless
                && (0.08..=0.72).contains(&p.gray)
                && eq(p.width, outer.width, f.eps)
            {
                let ([a, b], [lo, hi]) = f.pass(p.points[0], p.points[1]);
                // An early thin diagonal over the opaque stock is a texture
                // only when it fits the material's own finite width.
                if !f.inside_centerline(p.points)
                    || ((b - a - (hi - lo)).abs() <= 0.03 * f.width
                        && lo >= -0.08 * f.width
                        && hi <= 1.08 * f.width)
                {
                    continue;
                }
            }
            let Some(at) = f.neutral_crossing(p.points, p.width) else {
                continue 'stock;
            };
            if !p.layerless
                || !(0.45..=0.86).contains(&p.gray)
                || sources
                    .get(p.key.id(page).as_str())
                    .and_then(|p| native(p))
                    .is_none()
                || muted.len() >= 12
            {
                continue 'stock;
            }
            muted.push((at, p));
        }
        muted.sort_by(|a, b| a.0.total_cmp(&b.0));
        if muted.windows(2).any(|p| p[1].0 - p[0].0 < 4. * f.width)
            || muted.iter().map(|(_, p)| p.width).sum::<f64>() > 0.02 * f.length
        {
            continue;
        }
        out.insert(
            key,
            Receipt {
                paint_source_id: key.id(page),
                outer_face_source_id: outer.key.id(page),
                inner_face_source_ids: inners.into_iter().map(|p| p.key.id(page)).collect(),
                return_source_ids: returns.into_iter().map(|p| p.key.id(page)).collect(),
                end_cap_source_id: cap.key.id(page),
                neutral_crossing_source_ids: muted
                    .into_iter()
                    .map(|(_, p)| p.key.id(page))
                    .collect(),
            },
        );
        if out.len() > MAX_RECEIPTS {
            return None;
        }
    }
    Some(out.into_values().collect())
}

fn interpreted(r: &Receipt, sources: &HashMap<&str, &Primitive>, page: u32) -> Option<Verified> {
    let proof = Key::from_id(&r.paint_source_id, page)?;
    let p = sources.get(r.paint_source_id.as_str())?;
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    if p.layer.is_some() {
        return None;
    }
    let f = Form::new(points)?;
    let get = |id: &str| {
        let key = Key::from_id(id, page)?;
        (key.0 > proof.0)
            .then(|| sources.get(id).and_then(|p| native(p)))
            .flatten()
    };
    let outer = get(&r.outer_face_source_id)?;
    let cap = get(&r.end_cap_source_id)?;
    if r.inner_face_source_ids.len() != f.bands.len()
        || r.return_source_ids.len() + 1 != f.bands.len()
        || r.neutral_crossing_source_ids.len() > 12
        || !(0.025..=0.22).contains(&(outer.width / f.width))
        || !eq(outer.width, cap.width, f.eps)
        || !f.rail(outer.points, 0., [f.bands[0].a, f.length], false)
        || !f.cross(cap.points, f.length, [0., f.width])
    {
        return None;
    }
    for (i, (b, id)) in f.bands.iter().zip(&r.inner_face_source_ids).enumerate() {
        let p = get(id)?;
        if !eq(p.width, outer.width, f.eps)
            || !f.rail(p.points, b.depth, [b.a, b.b], i + 1 == f.bands.len())
        {
            return None;
        }
    }
    for (q, id) in f.bands.windows(2).zip(&r.return_source_ids) {
        let p = get(id)?;
        if !eq(p.width, outer.width, f.eps)
            || !f.cross(p.points, q[0].b, range(q[0].depth, q[1].depth))
        {
            return None;
        }
    }
    let mut prev = None;
    let mut total = 0.;
    for id in &r.neutral_crossing_source_ids {
        let p = get(id)?;
        let x = f.neutral_crossing(p.points, p.width)?;
        if prev.is_some_and(|q| x - q < 4. * f.width) {
            return None;
        }
        prev = Some(x);
        total += p.width;
    }
    if total > 0.02 * f.length {
        return None;
    }
    Some(Verified {
        outlines: f.bands.iter().map(|&b| f.outline(b)).collect(),
        widths: f.bands.iter().map(|b| b.depth).collect(),
    })
}

pub(crate) fn verified(page: &PageScan, receipt: &Receipt) -> Option<Verified> {
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    interpreted(receipt, &sources, page.page_number)
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    if page.stepped_stock.is_empty() {
        return Ok(());
    }
    let bad = || "invalid independently visible stepped stock".to_owned();
    if page.stepped_stock.len() > MAX_RECEIPTS
        || !page.pdf_input.as_ref().is_some_and(|p| {
            matches!(
                p.route,
                crate::input::raster::PdfRoute::Vector | crate::input::raster::PdfRoute::Mixed
            )
        })
    {
        return Err(bad());
    }
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut prior = None;
    for r in &page.stepped_stock {
        let key = Key::from_id(&r.paint_source_id, page.page_number).ok_or_else(bad)?;
        if prior.is_some_and(|p| p >= key) || interpreted(r, &sources, page.page_number).is_none() {
            return Err(bad());
        }
        prior = Some(key);
    }
    Ok(())
}
