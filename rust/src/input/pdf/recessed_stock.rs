//! Visible recessed stock with a finite side branch supported by native paint.
use super::{
    Cover, Event, Key, Pen, Point, Rect,
    stepped_stock::{Band, Form},
};
use crate::model::{PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap};

const MAX_RECEIPTS: usize = 32;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub trim_source_id: String,
    pub outer_face_source_ids: [String; 2],
    pub inner_face_source_ids: Vec<String>,
    pub return_source_ids: Vec<String>,
    pub backing_face_source_ids: Vec<String>,
    pub branch_paint_source_id: String,
    pub branch_face_source_ids: [String; 2],
    pub branch_return_source_ids: [String; 2],
    pub branch_backing_face_source_ids: [String; 2],
    pub neutral_crossing_source_ids: Vec<String>,
}

impl Receipt {
    pub(crate) fn source_ids(&self) -> impl Iterator<Item = &String> {
        [
            &self.paint_source_id,
            &self.trim_source_id,
            &self.branch_paint_source_id,
        ]
        .into_iter()
        .chain(&self.outer_face_source_ids)
        .chain(&self.inner_face_source_ids)
        .chain(&self.return_source_ids)
        .chain(&self.backing_face_source_ids)
        .chain(&self.branch_face_source_ids)
        .chain(&self.branch_return_source_ids)
        .chain(&self.branch_backing_face_source_ids)
        .chain(&self.neutral_crossing_source_ids)
    }
}

pub(crate) struct Verified {
    pub outlines: Vec<[Point; 4]>,
    pub branch_outline: [Point; 4],
    pub toe: Point,
}

fn ring(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    let n = points.len();
    (p.layer.is_none() && (4..=24).contains(&n) && points.iter().all(|p| p.finite())).then(|| {
        if points.first() == points.last() {
            &points[..n - 1]
        } else {
            points
        }
    })
}

fn pen(p: &Primitive, key: Key, gray: f64) -> Option<Pen> {
    let Shape::Line { start, end } = p.shape else {
        return None;
    };
    let width = p.stroke_width?;
    (p.layer.is_none()
        && start.finite()
        && end.finite()
        && start != end
        && width.is_finite()
        && width > 0.)
        .then_some(Pen {
            key,
            points: [start, end],
            width,
            gray,
            layerless: true,
        })
}

fn eq(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}

fn span(f: &Form, [a, b]: [Point; 2]) -> [[f64; 2]; 2] {
    let [a, b] = [a, b].map(|p| f.local(p));
    [[a.x.min(b.x), a.x.max(b.x)], [a.y.min(b.y), a.y.max(b.y)]]
}

fn unique<T>(mut it: impl Iterator<Item = T>) -> Option<T> {
    let first = it.next()?;
    it.next().is_none().then_some(first)
}

fn reverse(f: &Form) -> Form {
    Form {
        origin: f.world(f.length, 0.),
        axis: f.axis * -1.,
        side: -f.side,
        length: f.length,
        width: f.width,
        eps: f.eps,
        bands: f
            .bands
            .iter()
            .rev()
            .map(|p| Band {
                a: f.length - p.b,
                b: f.length - p.a,
                depth: p.depth,
            })
            .collect(),
    }
}

pub(super) fn eligible(points: &[Point]) -> bool {
    Form::new(points).is_some_and(|f| f.bands.len() >= 5)
}

fn trim_paint(f: &Form, points: &[Point], trim: f64) -> bool {
    points.iter().all(|p| f.local(*p).x <= trim + f.eps)
        && points
            .iter()
            .zip(points.iter().cycle().skip(1))
            .any(|(&a, &b)| {
                let [[lo, hi], [from, to]] = span(f, [a, b]);
                eq(lo, trim, f.eps) && eq(hi, trim, f.eps) && from <= f.eps && to >= f.width - f.eps
            })
}

fn branch_paint(f: &Form, points: &[Point], cut: [f64; 2], foot: [f64; 2]) -> bool {
    let [from, to] = cut;
    let [root, face] = foot;
    let expected = [
        f.world(from, root),
        f.world(from, face),
        f.world(to, 0.),
        f.world(to, root),
    ];
    let on_edge = |p| {
        expected
            .iter()
            .zip(expected.iter().cycle().skip(1))
            .any(|(&a, &b)| crate::model::on_segment(p, a, b).0.distance(p) <= f.eps)
    };
    expected
        .iter()
        .all(|a| points.iter().any(|b| a.distance(*b) <= f.eps))
        && points.iter().copied().all(on_edge)
        && points
            .iter()
            .zip(points.iter().cycle().skip(1))
            .all(|(&a, &b)| on_edge((a + b) * 0.5))
}

fn neutral(f: &Form, p: Pen, from: f64, to: f64) -> Option<f64> {
    let [[a, b], [lo, hi]] = span(f, p.points);
    (eq(a, b, f.eps)
        && a >= from - f.eps
        && a <= to + f.eps
        && lo >= 0.18 * f.width
        && lo <= 0.49 * f.width
        && hi >= 3. * f.width
        && (0.025..=0.17).contains(&(p.width / f.width))
        && (0.45..=0.86).contains(&p.gray))
    .then_some(a)
}

fn visible(f: &Form, geometry: &Verified, r: &Receipt, nearby: &[&Event], page: u32) -> bool {
    let originals = geometry
        .outlines
        .iter()
        .filter_map(|s| Rect::points(s))
        .collect::<Vec<_>>();
    let local = geometry
        .outlines
        .iter()
        .filter_map(|s| Rect::points(&s.map(|p| f.local(p))))
        .collect::<Vec<_>>();
    let earliest = r
        .outer_face_source_ids
        .iter()
        .chain(&r.inner_face_source_ids)
        .chain(&r.return_source_ids)
        .filter_map(|id| Key::from_id(id, page))
        .map(|key| key.0 * 2)
        .min()
        .unwrap_or(0);
    let thin = Key::from_id(&r.outer_face_source_ids[0], page)
        .and_then(|key| {
            nearby.iter().find_map(|e| match e.cover {
                Cover::Stroke(p) if p.key == key => Some(p.width),
                _ => None,
            })
        })
        .unwrap_or(0.);
    nearby.iter().all(|e| {
        if !originals.iter().any(|body| {
            body.intersect(e.bounds)
                .is_some_and(|q| q.r - q.l > f.eps && q.b - q.t > f.eps)
        }) {
            return true;
        }
        let Cover::Stroke(p) = e.cover else {
            return false;
        };
        if p.gray == 0. || r.neutral_crossing_source_ids.contains(&p.key.id(page)) {
            return true;
        }
        let [a, b] = p.points.map(|p| f.local(p));
        let pad = p.width * std::f64::consts::FRAC_1_SQRT_2;
        if !local.iter().any(|body| {
            (Rect {
                l: body.l - pad,
                r: body.r + pad,
                t: body.t - pad,
                b: body.b + pad,
            })
            .clip(a, b)
            .is_some()
        }) {
            return true;
        }
        // A serial pen hatch constrained to the original stock is texture.
        // This exception ends before the independently penned faces.
        let [[alo, ahi], [blo, bhi]] = span(f, p.points);
        e.order < earliest
            && p.layerless
            && (0.08..=0.72).contains(&p.gray)
            && eq(p.width, thin, f.eps)
            && blo >= -f.eps
            && bhi <= f.width + f.eps
            && eq(ahi - alo, bhi - blo, 0.03 * f.width)
            && bhi - blo >= 0.7 * f.width
    })
}

fn witness(
    f: &Form,
    key: Key,
    ink: &[Pen],
    areas: &[(Key, &[Point])],
    page: u32,
) -> Option<(Receipt, Verified)> {
    let e = f.eps;
    let [first, last] = [*f.bands.first()?, *f.bands.last()?];
    if first.a.abs() > e || f.bands.len() < 5 {
        return None;
    }
    let fine = |p: Pen| p.gray == 0. && (0.025..=0.22).contains(&(p.width / f.width));
    let find = |test: &dyn Fn(Pen) -> bool| unique(ink.iter().copied().filter(|&p| test(p)));
    let rail = |p: Pen, at: f64| {
        let [x, [lo, hi]] = span(f, p.points);
        (eq(lo, at, e) && eq(hi, at, e)).then_some(x)
    };
    let outside_before = find(&|p| {
        fine(p)
            && rail(p, 0.).is_some_and(|[a, b]| {
                eq(a, 0., e) && b > last.a + f.width && b < last.b - 2. * f.width
            })
    })?;
    let outside_after = find(&|p| {
        fine(p) && rail(p, 0.).is_some_and(|[a, b]| a > last.a + f.width && eq(b, f.length, e))
    })?;
    let thin = |p: Pen| fine(p) && eq(p.width, outside_before.width, e);
    if !thin(outside_after) {
        return None;
    }
    let cut = [rail(outside_before, 0.)?[1], rail(outside_after, 0.)?[0]];
    if !(0.85..=1.15).contains(&((cut[1] - cut[0]) / f.width)) {
        return None;
    }
    let inside_first = find(&|p| {
        thin(p)
            && rail(p, first.depth)
                .is_some_and(|[a, b]| a > 0.75 * f.width && a <= 1.3 * f.width && eq(b, first.b, e))
    })?;
    let start = rail(inside_first, first.depth)?[0];
    let trim = unique(
        areas
            .iter()
            .filter(|(k, r)| k.0 > key.0 && trim_paint(f, r, start)),
    )?
    .0;
    let mut inside = vec![inside_first];
    for b in f.bands.iter().skip(1) {
        inside.push(find(&|p| {
            thin(p) && rail(p, b.depth).is_some_and(|[lo, hi]| eq(lo, b.a, e) && eq(hi, b.b, e))
        })?);
    }
    let mut returns = Vec::new();
    for q in f.bands.windows(2) {
        let shallow = q[0].depth.min(q[1].depth);
        let deep = q[0].depth.max(q[1].depth);
        returns.push(find(&|p| {
            let [[a, b], [lo, hi]] = span(f, p.points);
            thin(p)
                && eq(a, q[0].b, e)
                && eq(b, q[0].b, e)
                && eq(lo, shallow, e)
                && hi >= deep - e
                && hi <= deep + 0.08 * f.width
        })?);
    }

    let across = |p: Pen, x: f64| {
        let [[a, b], y] = span(f, p.points);
        (eq(a, x, e) && eq(b, x, e)).then_some(y)
    };
    let turn = |at| {
        find(&|p| {
            thin(p)
                && across(p, at).is_some_and(|[lo, hi]| {
                    lo < -0.008 * f.width && lo >= -0.09 * f.width && hi.abs() <= e
                })
        })
    };
    let turns = [turn(cut[0])?, turn(cut[1])?];
    let foot = across(turns[0], cut[0])?[0];
    if !eq(across(turns[1], cut[1])?[0], foot, e) {
        return None;
    }
    let branch_face = |at| {
        find(&|p| {
            thin(p)
                && across(p, at).is_some_and(|[a, b]| {
                    eq(b, foot, e) && b - a >= 8. * f.width && b - a <= 48. * f.width
                })
        })
    };
    let faces = [branch_face(cut[0])?, branch_face(cut[1])?];
    let root = across(faces[0], cut[0])?[0];
    if !eq(across(faces[1], cut[1])?[0], root, e) {
        return None;
    }
    let branch = unique(
        areas
            .iter()
            .filter(|(k, r)| *k != trim && k.0 > key.0 && branch_paint(f, r, cut, [root, foot])),
    )?
    .0;

    let heavy = |p: Pen| p.gray == 0. && (0.23..=0.50).contains(&(p.width / f.width));
    let outer_thick = [
        find(&|p| {
            heavy(p)
                && rail(p, foot)
                    .is_some_and(|[a, b]| a <= e && a >= foot - e && eq(b, cut[0] + foot, e))
        })?,
        find(&|p| {
            heavy(p)
                && rail(p, foot).is_some_and(|[a, b]| eq(a, cut[1] - foot, e) && eq(b, f.length, e))
        })?,
    ];
    let backing = |p: Pen| heavy(p) && eq(p.width, outer_thick[0].width, e);
    if !backing(outer_thick[1]) {
        return None;
    }
    let backing_branch = |at| {
        find(&|p| {
            backing(p) && across(p, at).is_some_and(|[a, b]| eq(a, root, e) && eq(b, foot, e))
        })
    };
    let branch_heavy = [
        backing_branch(cut[0] + foot)?,
        backing_branch(cut[1] - foot)?,
    ];
    let mut backs = outer_thick.to_vec();
    for (i, b) in f.bands.iter().enumerate().step_by(2) {
        let at = if i == 0 { start - foot } else { b.a };
        backs.push(find(&|p| {
            backing(p)
                && rail(p, b.depth - foot).is_some_and(|[a, z]| eq(a, at, e) && eq(z, b.b, e))
        })?);
    }

    let mut outlines = f.bands.clone();
    outlines.first_mut()?.a = start;
    outlines.last_mut()?.b = cut[1];
    let id = |p: Pen| p.key.id(page);
    let mut marks = ink
        .iter()
        .copied()
        .filter_map(|p| neutral(f, p, start, cut[1]).map(|a| (a, p)))
        .collect::<Vec<_>>();
    marks.sort_by(|(a, _), (b, _)| a.total_cmp(b));
    if marks.len() > 12
        || marks.windows(2).any(|p| p[1].0 - p[0].0 < 4. * f.width)
        || marks.iter().map(|p| p.1.width).sum::<f64>() > 0.02 * (cut[1] - start)
    {
        return None;
    }
    Some((
        Receipt {
            paint_source_id: key.id(page),
            trim_source_id: trim.id(page),
            outer_face_source_ids: [outside_before, outside_after].map(id),
            inner_face_source_ids: inside.into_iter().map(id).collect(),
            return_source_ids: returns.into_iter().map(id).collect(),
            backing_face_source_ids: backs.into_iter().map(id).collect(),
            branch_paint_source_id: branch.id(page),
            branch_face_source_ids: faces.map(id),
            branch_return_source_ids: turns.map(id),
            branch_backing_face_source_ids: branch_heavy.map(id),
            neutral_crossing_source_ids: marks.into_iter().map(|(_, p)| id(p)).collect(),
        },
        Verified {
            outlines: outlines.into_iter().map(|b| f.outline(b)).collect(),
            branch_outline: [
                f.world(cut[0], root),
                f.world(cut[0], foot),
                f.world(cut[1], foot),
                f.world(cut[1], root),
            ],
            toe: f.world(cut[1], 0.),
        },
    ))
}

pub(super) fn derive(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    page: u32,
) -> Vec<Receipt> {
    derive_inner(keys, events, primitives, page).unwrap_or_default()
}

fn derive_inner(
    keys: &[Key],
    events: &[Event],
    primitives: &[Primitive],
    page: u32,
) -> Option<Vec<Receipt>> {
    if keys.is_empty() {
        return Some(vec![]);
    }
    let sources: HashMap<_, _> = primitives
        .iter()
        .filter_map(|p| Some((Key::from_id(&p.id, page)?, p)))
        .collect();
    let mut work = MAX_WORK;
    let mut out = BTreeMap::new();
    for &key in keys {
        let Some(f) = sources.get(&key).and_then(|p| ring(p)).and_then(Form::new) else {
            continue;
        };
        if f.bands.len() < 5 {
            continue;
        }
        let box_ = Rect::points(&[
            f.world(0., -49. * f.width),
            f.world(f.length, -49. * f.width),
            f.world(0., 8. * f.width),
            f.world(f.length, 8. * f.width),
        ])?;
        work = work.checked_sub(events.len())?;
        let nearby = events
            .iter()
            .filter(|p| p.order > 2 * key.0 && box_.intersect(p.bounds).is_some())
            .collect::<Vec<_>>();
        let ink = nearby
            .iter()
            .filter_map(|e| {
                let Cover::Stroke(authored) = e.cover else {
                    return None;
                };
                let native = pen(sources.get(&authored.key)?, authored.key, authored.gray)?;
                (authored.layerless
                    && eq(native.width, authored.width, f.eps)
                    && native.points == authored.points)
                    .then_some(native)
            })
            .collect::<Vec<_>>();
        let areas = nearby
            .iter()
            .filter_map(|e| {
                let (Cover::NeutralArea { key } | Cover::RecessedArea { key }) = e.cover else {
                    return None;
                };
                Some((key, ring(sources.get(&key)?)?))
            })
            .collect::<Vec<_>>();
        work = work.checked_sub(
            ink.len()
                .saturating_mul(50)
                .saturating_add(areas.len().saturating_mul(4)),
        )?;
        let reversed = reverse(&f);
        let found = unique([&reversed, &f].into_iter().filter_map(|f| {
            let (r, v) = witness(f, key, &ink, &areas, page)?;
            visible(f, &v, &r, &nearby, page).then_some(r)
        }));
        let Some(receipt) = found else {
            continue;
        };
        out.insert(key, receipt);
        if out.len() > MAX_RECEIPTS {
            return None;
        }
    }
    Some(out.into_values().collect())
}

fn interpret(r: &Receipt, sources: &HashMap<&str, &Primitive>, page: u32) -> Option<Verified> {
    let key = Key::from_id(&r.paint_source_id, page)?;
    let f = Form::new(ring(sources.get(r.paint_source_id.as_str())?)?)?;
    let areas = [&r.trim_source_id, &r.branch_paint_source_id]
        .map(|id| Some((Key::from_id(id, page)?, ring(sources.get(id.as_str())?)?)));
    let areas = [areas[0]?, areas[1]?];
    let mut seen = std::collections::HashSet::new();
    let pens = r
        .source_ids()
        .skip(3)
        .map(|id| {
            let k = Key::from_id(id, page)?;
            if k.0 <= key.0 || !seen.insert(k) {
                return None;
            }
            pen(
                sources.get(id.as_str())?,
                k,
                if r.neutral_crossing_source_ids.contains(id) {
                    0.75
                } else {
                    0.
                },
            )
        })
        .collect::<Option<Vec<_>>>()?;
    let (receipt, geometry) = unique(
        [reverse(&f), f]
            .iter()
            .filter_map(|f| witness(f, key, &pens, &areas, page)),
    )?;
    (receipt == *r).then_some(geometry)
}

pub(crate) fn verified(page: &PageScan, r: &Receipt) -> Option<Verified> {
    let sources = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    interpret(r, &sources, page.page_number)
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    if page.recessed_stock.is_empty() {
        return Ok(());
    }
    let bad = || "invalid source-bounded recessed stock".to_owned();
    if page.recessed_stock.len() > MAX_RECEIPTS
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
    for r in &page.recessed_stock {
        let key = Key::from_id(&r.paint_source_id, page.page_number).ok_or_else(bad)?;
        if prev.is_some_and(|p| p >= key) || interpret(r, &sources, page.page_number).is_none() {
            return Err(bad());
        }
        prev = Some(key);
    }
    Ok(())
}
