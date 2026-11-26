//! Visible finite terminal rectangles inside a notched native paint contour.
use super::{Cover, Event, Key, Pen, Point, Rect, Stock, precision, rectangle};
use crate::model::{PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};

const MAX_RECEIPTS: usize = 64;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub face_source_ids: [String; 2],
    pub cap_source_id: String,
    pub outline: [Point; 4],
}

struct Contour {
    stock: Stock,
    local: Vec<Point>,
}

pub(super) fn eligible(points: &[Point]) -> bool {
    Contour::new(points).is_some()
}

fn span(points: impl IntoIterator<Item = Point>) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], p| {
            [
                [x[0].min(p.x), x[1].max(p.x)],
                [y[0].min(p.y), y[1].max(p.y)],
            ]
        })
}

impl Contour {
    fn new(points: &[Point]) -> Option<Self> {
        if !(6..=24).contains(&points.len())
            || rectangle(points).is_some()
            || !points.iter().all(|p| p.finite())
        {
            return None;
        }
        let mut ring = points.to_vec();
        if ring.first() == ring.last() {
            ring.pop();
        }
        let eps = precision(&ring);
        let delta = ring
            .iter()
            .copied()
            .zip(ring.iter().copied().cycle().skip(1))
            .map(|(a, b)| b - a)
            .max_by(|a, b| a.norm().total_cmp(&b.norm()))?;
        let mut u = delta.unit();
        if (if u.x.abs() >= u.y.abs() { u.x } else { u.y }) < 0. {
            u = u * -1.;
        }
        let n = Point::new(-u.y, u.x);
        let origin = ring[0];
        let raw_local = |p: Point| Point::new((p - origin).dot(u), (p - origin).dot(n));
        let [[lo, hi], [a, b]] = span(ring.iter().map(|&p| raw_local(p)));
        let stock = Stock {
            start: origin + u * lo + n * a,
            u,
            length: hi - lo,
            width: b - a,
            eps,
        };
        if !stock.length.is_finite()
            || !stock.width.is_finite()
            || stock.width <= eps
            || stock.length < 8. * stock.width
        {
            return None;
        }
        let local = ring.into_iter().map(|p| stock.local(p)).collect::<Vec<_>>();
        let edges = local
            .iter()
            .copied()
            .zip(local.iter().copied().cycle().skip(1))
            .collect::<Vec<_>>();
        if edges.iter().any(|(a, b)| {
            !((a.x - b.x).abs() <= eps && (a.y - b.y).abs() > eps
                || (a.y - b.y).abs() <= eps && (a.x - b.x).abs() > eps)
        }) {
            return None;
        }
        // A multiply covered or self-touching contour cannot prove occupancy.
        for (i, &(a, b)) in edges.iter().enumerate() {
            for (j, &(c, d)) in edges.iter().enumerate().skip(i + 2) {
                if i == 0 && j + 1 == edges.len() {
                    continue;
                }
                let [[x0, x1], [y0, y1]] = span([a, b]);
                let [[z0, z1], [w0, w1]] = span([c, d]);
                if x1.min(z1) >= x0.max(z0) - eps && y1.min(w1) >= y0.max(w0) - eps {
                    return None;
                }
            }
        }
        Some(Self { stock, local })
    }

    fn fits(&self, [lo, hi]: [f64; 2]) -> bool {
        let s = self.stock;
        if lo < -s.eps_thin() || hi > s.length + s.eps_thin() || hi - lo < 8. * s.width {
            return false;
        }
        let middle = (lo + hi) * 0.5;
        let mut crosses = 0;
        let edges = self
            .local
            .iter()
            .copied()
            .zip(self.local.iter().copied().cycle().skip(1));
        let eps = s.eps_thin();
        for (a, b) in edges {
            let [[x0, x1], [y0, y1]] = span([a, b]);
            if x1 > lo + eps && x0 < hi - eps && y1 > eps && y0 < s.width - eps {
                return false;
            }
            if (a.x > middle) != (b.x > middle) && y0 > s.width * 0.5 {
                crosses += 1;
            }
        }
        crosses % 2 == 1
    }
}

fn rail(stock: Stock, line: [Point; 2], at: f64) -> Option<[f64; 2]> {
    let [a, b] = line.map(|p| stock.local(p));
    let extent = [a.x.min(b.x), a.x.max(b.x)];
    (stock.eq(a.y, at)
        && stock.eq(b.y, at)
        && extent[0] >= -stock.eps_thin()
        && extent[1] <= stock.length + stock.eps_thin()
        && extent[1] - extent[0] >= 8. * stock.width)
        .then_some(extent)
}

fn cap(stock: Stock, line: [Point; 2], pen: f64, at: f64) -> bool {
    let [[a, b], [lo, hi]] = span(line.map(|p| stock.local(p)));
    stock.eq(a, at)
        && stock.eq(b, at)
        && stock.eq((lo + hi) * 0.5, stock.width * 0.5)
        && lo <= stock.eps_thin()
        && hi >= stock.width - stock.eps_thin()
        && hi - lo <= 1.20 * stock.width
        && (0.23..=0.50).contains(&(pen / stock.width))
}

fn common(stock: Stock, a: [f64; 2], b: [f64; 2]) -> Option<([f64; 2], f64)> {
    let [lo, hi] = [a[0].max(b[0]), a[1].min(b[1])];
    match (
        stock.eq(a[0], 0.) && stock.eq(b[0], 0.),
        stock.eq(a[1], stock.length) && stock.eq(b[1], stock.length),
    ) {
        (true, false) => Some(([0., hi], 0.)),
        (false, true) => Some(([lo, stock.length], stock.length)),
        _ => None,
    }
}

pub(super) fn derive(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    visible_paint: &[String],
    page: u32,
) -> Vec<Receipt> {
    collect(keys, events, primitives, visible_paint, page).unwrap_or_default()
}

fn collect(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    visible_paint: &[String],
    page: u32,
) -> Option<Vec<Receipt>> {
    if keys.is_empty() {
        return Some(vec![]);
    }
    let mut work = MAX_WORK;
    let primitives: HashMap<_, _> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut out = BTreeMap::new();
    for &key in keys {
        let Some(p) = primitives.get(key.id(page).as_str()) else {
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
        if !visible_paint
            .iter()
            .filter_map(|id| Key::from_id(id, page))
            .any(|p| p.0 == key.0 && p != key)
        {
            continue;
        }
        let Some(contour) = Contour::new(points) else {
            continue;
        };
        let s = contour.stock;
        let area = s.envelope([0., s.length], s.width * 0.50);
        work = work.checked_sub(events.len())?;
        let nearby = events
            .iter()
            .filter(|e| e.order > key.0 * 2 && area.intersect(e.bounds).is_some())
            .collect::<Vec<_>>();
        let ink = nearby
            .iter()
            .filter_map(|e| {
                let Cover::Stroke(p) = e.cover else {
                    return None;
                };
                let prim = primitives.get(p.key.id(page).as_str())?;
                (p.layerless && p.gray == 0. && prim.stroke_width.is_some_and(|w| s.eq(w, p.width)))
                    .then_some(p)
            })
            .collect::<Vec<_>>();
        let side = |at| {
            ink.iter()
                .copied()
                .filter_map(|p| {
                    (s.valid_width(p.width))
                        .then(|| rail(s, p.points, at))
                        .flatten()
                        .map(|extent| (p, extent))
                })
                .collect::<Vec<_>>()
        };
        let [low, high] = [0., s.width].map(side);
        work = work.checked_sub(
            low.len()
                .saturating_mul(high.len())
                .saturating_mul(ink.len() + nearby.len()),
        )?;
        let mut possible = None;
        let mut ambiguous = false;
        for &(a, x) in &low {
            for &(b, y) in &high {
                if a.key.0 == b.key.0 || !s.eq(a.width, b.width) {
                    continue;
                }
                let Some((interval, terminal)) = common(s, x, y) else {
                    continue;
                };
                if !contour.fits(interval) {
                    continue;
                }
                let mut jamb = ink
                    .iter()
                    .copied()
                    .filter(|p| cap(s, p.points, p.width, terminal));
                let Some(j) = jamb.next() else { continue };
                if jamb.next().is_some() || [a.key.0, b.key.0].contains(&j.key.0) {
                    continue;
                }
                if !visible(s, interval, [a, b], &nearby) {
                    continue;
                }
                let receipt = Receipt {
                    paint_source_id: key.id(page),
                    face_source_ids: [a.key.id(page), b.key.id(page)],
                    cap_source_id: j.key.id(page),
                    outline: s.outline(interval),
                };
                if possible.replace(receipt).is_some() {
                    ambiguous = true;
                    break;
                }
            }
            if ambiguous {
                break;
            }
        }
        if !ambiguous && let Some(receipt) = possible {
            out.insert(key, receipt);
        }
        if out.len() > MAX_RECEIPTS {
            return None;
        }
    }
    Some(out.into_values().collect())
}

fn visible(s: Stock, length: [f64; 2], faces: [Pen; 2], events: &[&Event]) -> bool {
    let Some(body) = Rect::points(&s.outline(length)) else {
        return false;
    };
    let pens = [(faces[0], 0.), (faces[1], s.width)]
        .map(|(p, at)| (p.key.0 * 2, s.face_envelope(length, at, p.width)));
    let mut hatch = Vec::new();
    for o in events {
        if matches!(o.cover,Cover::Stroke(p) if p.gray==0.) {
            continue;
        }
        let crosses_body = body.intersect(o.bounds).is_some();
        if pens
            .iter()
            .any(|(order, b)| o.order > *order && b.intersect(o.bounds).is_some())
        {
            return false;
        }
        if !crosses_body {
            continue;
        }
        let Cover::Stroke(p) = o.cover else {
            return false;
        };
        let [a, b] = p.points.map(|q| s.local(q));
        let [u, v] = [a.y.min(b.y), a.y.max(b.y)];
        // Dark diagonal hatching drawn on solid material before both face pens
        // preserves the body. It needs repeated finite strokes bounded by
        // the original two faces, with neither face losing its own ink.
        if !p.layerless
            || !(0.08..=0.72).contains(&p.gray)
            || !s.eq(p.width, faces[0].width)
            || o.order >= pens[0].0.min(pens[1].0)
            || !s.eq(u, 0.)
            || !s.eq(v, s.width)
            || a.x.min(b.x) < length[0] - s.eps_thin()
            || a.x.max(b.x) > length[1] + s.eps_thin()
            || !s.eq((a.x - b.x).abs(), s.width)
            || hatch.len() >= 64
        {
            return false;
        }
        hatch.push((
            (a.x + b.x) * 0.5,
            p.gray,
            (a.x - b.x).signum() * (a.y - b.y).signum(),
        ));
    }
    if hatch.is_empty() {
        return true;
    }
    hatch.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
    let consistent_direction = hatch.iter().all(|h| h.2 == hatch[0].2)
        || hatch.len() >= 6 && hatch.windows(2).all(|p| p[0].2 != p[1].2);
    hatch.len() >= 3
        && consistent_direction
        && hatch.iter().all(|h| h.1 == hatch[0].1)
        && hatch
            .windows(2)
            .all(|p| (1.25..=8.).contains(&((p[1].0 - p[0].0) / s.width)))
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    if page.local_stock.is_empty() {
        return Ok(());
    }
    let bad = || "invalid visible native stock subsection receipt".to_owned();
    let source: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    if page.local_stock.len() > MAX_RECEIPTS
        || !page.pdf_input.as_ref().is_some_and(|p| {
            matches!(
                p.route,
                crate::input::raster::PdfRoute::Vector | crate::input::raster::PdfRoute::Mixed
            )
        })
    {
        return Err(bad());
    }
    let mut prev = None;
    let parse = |id: &str| Key::from_id(id, page.page_number).ok_or_else(bad);
    for r in &page.local_stock {
        let key = parse(&r.paint_source_id)?;
        if prev.is_some_and(|p| p >= key) {
            return Err(bad());
        }
        prev = Some(key);
        let p = source
            .get(r.paint_source_id.as_str())
            .filter(|p| p.layer.is_none())
            .ok_or_else(bad)?;
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
        else {
            return Err(bad());
        };
        let contour = Contour::new(points).ok_or_else(bad)?;
        let s = contour.stock;
        let get_pen = |id: &str| {
            let stamp = parse(id)?;
            let p = source
                .get(id)
                .filter(|p| p.layer.is_none() && stamp.0 > key.0)
                .ok_or_else(bad)?;
            let (Shape::Line { start, end }, Some(width)) = (p.shape.clone(), p.stroke_width)
            else {
                return Err(bad());
            };
            Ok((stamp, [start, end], width))
        };
        let (a, p, aw) = get_pen(&r.face_source_ids[0])?;
        let (b, q, bw) = get_pen(&r.face_source_ids[1])?;
        let (j, tip, jw) = get_pen(&r.cap_source_id)?;
        let visible_sibling = page
            .placement_paint_ids
            .iter()
            .flatten()
            .chain(page.placement_polygon_paint_ids.iter().flatten())
            .filter_map(|p| Key::from_id(p, page.page_number))
            .any(|p| p.0 == key.0 && p != key);
        let x = rail(s, p, 0.).ok_or_else(bad)?;
        let y = rail(s, q, s.width).ok_or_else(bad)?;
        let (interval, term) = common(s, x, y).ok_or_else(bad)?;
        if [a.0, b.0].contains(&j.0)
            || a.0 == b.0
            || !s.valid_width(aw)
            || !s.eq(aw, bw)
            || !visible_sibling
            || !contour.fits(interval)
            || !cap(s, tip, jw, term)
            || !r
                .outline
                .into_iter()
                .zip(s.outline(interval))
                .all(|(a, b)| a.finite() && a.distance(b) <= s.eps_thin())
        {
            return Err(bad());
        }
    }
    Ok(())
}
