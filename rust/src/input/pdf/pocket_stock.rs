//! Keep the solid distal part of authored pocket-wall paint.
use super::{
    Graphics, Point, Rect, Subpath, material,
    visibility::{NativeInk, VisibleFills},
};
use crate::model::{LayerKind, PageScan, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

const MAX_SEEDS: usize = 128;
const MAX_WORK: usize = 4_000_000;
const MAX_RETURN_PIECES: usize = 4;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Receipt {
    pub paint_source_id: String,
    pub pocket_source_id: String,
    pub pocket_outline: [Point; 4],
    pub face_source_ids: [String; 2],
    pub outline: [Point; 4],
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authored_faces: Option<AuthoredFaces>,
}

impl Receipt {
    /// Additional proof IDs for the placement decision, after native validation.
    pub(crate) fn authored_source_ids(&self) -> impl Iterator<Item = &str> {
        self.authored_faces
            .iter()
            .flat_map(|p| {
                p.paint_source_ids
                    .iter()
                    .chain(p.continuation.iter().flat_map(|c| &c.stock_source_ids))
            })
            .map(String::as_str)
    }

    /// Rectangular extent outside the placed U that supports a continuing pen.
    /// The placement caller can independently veto an aperture on this extent.
    pub(crate) fn continued_extent(&self, page: &PageScan) -> Option<[Point; 4]> {
        let tail = self
            .authored_faces
            .as_ref()?
            .continuation
            .as_ref()?
            .stock_source_ids
            .last()?;
        let pen = page.primitives.iter().find(|p| p.id == *tail)?;
        let stock = page
            .primitives
            .iter()
            .find(|p| p.id == self.paint_source_id)?;
        let form = Form::new(filled(stock)?)?;
        let terminal = native_rectangle(pen, page.page_number)?;
        let [[start, _], _] = form.local_rectangle(terminal.corners)?;
        Some([
            form.world(start, form.half),
            form.world(0., form.half),
            form.world(0., -form.half),
            form.world(start, -form.half),
        ])
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AuthoredFaces {
    pub paint_source_ids: [String; 2],
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub continuation: Option<Continuation>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Continuation {
    pub face_index: usize,
    pub stock_source_ids: Vec<String>,
}

#[derive(Clone, Copy)]
struct Face {
    first: f64,
    shift: f64,
}

#[derive(Clone, Copy)]
struct Form {
    cap: Point,
    axis: Point,
    half: f64,
    length: f64,
    chase_start: f64,
    chase: [f64; 2],
    eps: f64,
}

fn range(values: impl IntoIterator<Item = f64>) -> [f64; 2] {
    values
        .into_iter()
        .fold([f64::INFINITY, f64::NEG_INFINITY], |[a, b], x| {
            [a.min(x), b.max(x)]
        })
}

impl Form {
    fn new(points: &[Point]) -> Option<Self> {
        let points = match points {
            [a, b, c, d, e, f, g, h] => [*a, *b, *c, *d, *e, *f, *g, *h],
            [a, b, c, d, e, f, g, h, end] if a.distance(*end) < 1e-7 => {
                [*a, *b, *c, *d, *e, *f, *g, *h]
            }
            _ => return None,
        };
        if !points.iter().all(|p| p.finite()) {
            return None;
        }
        let mut result = None;
        for (a, b) in points
            .iter()
            .copied()
            .zip(points.iter().copied().cycle().skip(1))
        {
            let half = a.distance(b) * 0.5;
            if half <= 1e-5 {
                continue;
            }
            let cap = (a + b) * 0.5;
            let mut axis = Point::new(a.y - b.y, b.x - a.x).unit();
            if points.iter().map(|p| (*p - cap).dot(axis)).sum::<f64>() < 0. {
                axis = axis * -1.;
            }
            let local = points.map(|p| Point::new((p - cap).dot(axis), axis.cross(p - cap)));
            let [lo, length] = range(local.iter().map(|p| p.x));
            let [bottom, top] = range(local.iter().map(|p| p.y));
            let eps = (half * 2e-5).max(1e-7);
            let eq = |x: f64, y: f64| (x - y).abs() <= eps;
            if !eq(lo, 0.) || !eq(bottom, -half) || !eq(top, half) || length < 12. * half {
                continue;
            }
            let inside = local
                .into_iter()
                .filter(|p| !eq(p.x, 0.) && !eq(p.x, length))
                .collect::<Vec<_>>();
            if inside.len() != 2 || !eq(inside[0].x, inside[1].x) {
                continue;
            }
            let chase_start = inside[0].x;
            let chase = range(inside.iter().map(|p| p.y));
            let pocket_width = chase[1] - chase[0];
            let width = half * 2.;
            if chase_start < 5. * width
                || !(5. * width..=24. * width).contains(&(length - chase_start))
                || !(0.35 * width..=0.70 * width).contains(&pocket_width)
                || (chase[0] + chase[1]).abs() > 0.05 * width
            {
                continue;
            }
            let expected = [
                Point::new(0., -half),
                Point::new(0., half),
                Point::new(length, half),
                Point::new(length, chase[1]),
                Point::new(chase_start, chase[1]),
                Point::new(chase_start, chase[0]),
                Point::new(length, chase[0]),
                Point::new(length, -half),
            ];
            if !local.iter().enumerate().any(|(i, p)| {
                p.distance(expected[0]) <= eps
                    && [1, 7].into_iter().any(|step| {
                        expected
                            .iter()
                            .enumerate()
                            .all(|(k, q)| local[(i + step * k) % 8].distance(*q) <= eps)
                    })
            }) {
                continue;
            }
            if result.is_some() {
                return None;
            }
            result = Some(Self {
                cap,
                axis,
                half,
                length,
                chase_start,
                chase,
                eps,
            });
        }
        result
    }

    fn local(self, p: Point) -> Point {
        let d = p - self.cap;
        Point::new(d.dot(self.axis), self.axis.cross(d))
    }

    fn world(self, x: f64, y: f64) -> Point {
        self.cap + self.axis * x + Point::new(-self.axis.y, self.axis.x) * y
    }

    fn outline(self) -> [Point; 4] {
        [
            self.world(0., self.half),
            self.world(self.chase_start, self.half),
            self.world(self.chase_start, -self.half),
            self.world(0., -self.half),
        ]
    }

    fn white_chase(self, corners: &[Point; 4]) -> bool {
        let p = corners.map(|v| self.local(v));
        let [a, b] = range(p.iter().map(|q| q.x));
        let [c, d] = range(p.iter().map(|q| q.y));
        let eq = |u: f64, v: f64| (u - v).abs() <= self.eps;
        eq(a, self.chase_start)
            && b >= self.length - self.eps
            && b <= self.length + 0.5 * self.half
            && eq(c, self.chase[0])
            && eq(d, self.chase[1])
            && p.iter()
                .all(|q| (eq(q.x, a) || eq(q.x, b)) && (eq(q.y, c) || eq(q.y, d)))
    }

    fn width(self) -> f64 {
        2. * self.half
    }

    fn eq(self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }

    fn face(self, pen: Pen<'_>, side: f64) -> Option<Face> {
        let [a, b] = pen.points.map(|p| self.local(p));
        let shift = a.y * side - self.half;
        let width = self.width();
        let first = a.x.min(b.x);
        ((a.y - b.y).abs() <= self.eps
            && (0.02 * width..=0.22 * width).contains(&shift)
            && first >= -10. * width
            && first <= 0.16 * width
            && (a.x.max(b.x) - self.length).abs() <= self.eps
            && (0.015 * width..=0.16 * width).contains(&pen.width))
        .then_some(Face { first, shift })
    }

    fn regular_face(self, face: Face) -> bool {
        face.first >= -1.1 * self.width()
    }

    fn small_depth_error(self, a: Face, b: Face) -> bool {
        (a.shift - b.shift).abs() <= a.shift.min(b.shift) * 0.0005
    }

    fn local_rectangle(self, corners: [Point; 4]) -> Option<[[f64; 2]; 2]> {
        let p = corners.map(|p| self.local(p));
        let [a, b] = range(p.iter().map(|p| p.x));
        let [c, d] = range(p.iter().map(|p| p.y));
        (b - a > self.eps
            && d - c > self.eps
            && [a, b].into_iter().all(|x| {
                [c, d]
                    .into_iter()
                    .all(|y| p.iter().any(|p| self.eq(p.x, x) && self.eq(p.y, y)))
            }))
        .then_some([[a, b], [c, d]])
    }

    fn supports_face(self, range: [[f64; 2]; 2], face: Face, side: f64) -> bool {
        let [[a, b], [c, d]] = range;
        let [inner, outer] = if side < 0. { [-d, -c] } else { [c, d] };
        self.eq(inner, self.half)
            && self.eq(outer, self.half + face.shift)
            && self.eq(a, face.first)
            && self.eq(b, self.length)
    }

    fn terminal_interval(self, range: [[f64; 2]; 2], limit: f64) -> Option<[f64; 2]> {
        let [[a, b], [c, d]] = range;
        (self.eq(c, -self.half) && self.eq(d, self.half) && b <= self.eps && a >= limit - self.eps)
            .then_some([a, b])
    }
}

fn named_interior(name: &str) -> bool {
    crate::layers::is_named_wall(name)
        && !crate::layers::is_named_exterior_wall(name)
        && name
            .to_lowercase()
            .split(|ch: char| !ch.is_alphanumeric())
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
            })
}

fn gray(ink: NativeInk) -> Option<f64> {
    match ink {
        NativeInk::Gray([v]) => Some(f64::from_bits(v)),
        NativeInk::Rgb([a, b, c]) if a == b && a == c => Some(f64::from_bits(a)),
        NativeInk::Cmyk([a, b, c, d]) if a == 0 && b == 0 && c == 0 => Some(1. - f64::from_bits(d)),
        _ => None,
    }
}

fn dark_rgb_text(g: &Graphics) -> bool {
    let Some(NativeInk::Rgb(rgb)) = g.opaque_native_ink() else {
        return false;
    };
    let linear = rgb.map(|bits| {
        let v = f64::from_bits(bits);
        if v <= 0.04045 {
            v / 12.92
        } else {
            ((v + 0.055) / 1.055).powf(2.4)
        }
    });
    // A known dark foreground glyph can overprint wall stock like black ink.
    linear
        .into_iter()
        .zip([0.2126, 0.7152, 0.0722])
        .map(|(v, w)| v * w)
        .sum::<f64>()
        <= 0.12
}

#[derive(Clone)]
struct Seed {
    order: u64,
    layer: String,
    form: Form,
    masks: Vec<(u64, [Point; 4])>,
}

pub(super) struct Tracker {
    stocks: VisibleFills<u64>,
    masks: VisibleFills<u64>,
    seeds: Vec<Seed>,
}

impl Tracker {
    pub(super) fn new(bounds: Rect) -> Self {
        Self {
            stocks: VisibleFills::new(bounds),
            masks: VisibleFills::new(bounds),
            seeds: Vec::new(),
        }
    }

    pub(super) fn unknown(&mut self, bounds: Option<Rect>) {
        self.stocks.unknown(bounds);
        self.masks.unknown(bounds);
    }

    pub(super) fn text(&mut self, g: &Graphics, known_font: bool) {
        let visible = match g.tr {
            0 => g.fill_alpha != 0. && !(g.black_fill && g.fill_alpha == 1. || dark_rgb_text(g)),
            1 => !(g.black_pen && g.stroke_alpha == 1.) && g.stroke_alpha != 0.,
            2 => !(g.black_fill && g.black_pen && g.fill_alpha == 1. && g.stroke_alpha == 1.),
            3 => false,
            _ => true,
        };
        if visible
            || !known_font
            || g.masked
            || !g.material_state_safe
            || !g.pen_state_safe
            || g.finish_clip_unsafe
            || g.uncertain_clip.is_some()
        {
            self.unknown(g.clip.or(g.uncertain_clip));
        }
    }

    pub(super) fn paint(
        &mut self,
        path: u64,
        subs: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
        layer: Option<&str>,
        even_odd: bool,
    ) {
        if fill && g.fill_alpha != 0. {
            self.stocks.paint_clipped(subs, g, even_odd, |_| true);
            self.masks.paint_clipped(subs, g, even_odd, |_| true);
        }
        if stroke && g.stroke_alpha != 0. && (g.white_pen || g.masked || !g.pen_state_safe) {
            self.stocks.white_stroke(subs, g);
            self.masks.white_stroke(subs, g);
        }
        if !fill
            || stroke
            || g.finish_clip_unsafe
            || g.uncertain_clip.is_some()
            || g.masked
            || !g.material_state_safe
            || !g.pen_state_safe
            || !g.solid_fill_safe
            || !g.default_rendering_intent
            || g.fill_device.is_none()
            || g.fill_alpha != 1.
        {
            return;
        }
        let Some(tone) = g.fill_ink.and_then(gray) else {
            return;
        };
        // Ordinary rectangles and distant white fills cannot seed this proof.
        let eligible = if tone == 1. {
            self.seeds
                .last()
                .is_some_and(|s| path > s.order && path - s.order <= 64)
        } else {
            (0.15..=0.93).contains(&tone)
                && self.seeds.len() < MAX_SEEDS
                && matches!(subs, [s] if s.curves == 0 && matches!(s.ps.len(), 8 | 9))
        };
        if !eligible {
            return;
        }
        let Some(name) = layer.filter(|s| named_interior(s)) else {
            return;
        };
        if (0.15..=0.93).contains(&tone)
            && self.seeds.len() < MAX_SEEDS
            && let [s] = subs
            && s.curves == 0
            && let Some(form) = Form::new(&s.ps)
            && g.contains_footprint(&form.outline())
        {
            self.stocks.add(path, form.outline());
            self.seeds.push(Seed {
                order: path,
                layer: name.to_owned(),
                form,
                masks: Vec::new(),
            });
        }
        if tone == 1.
            && let Some(outline) = material::paint_rectangle(subs)
            && g.contains_footprint(&outline)
        {
            let mut uses = false;
            for s in &mut self.seeds {
                if path > s.order
                    && path - s.order <= 64
                    && name == s.layer
                    && s.form.white_chase(&outline)
                {
                    if s.masks.len() < 2 {
                        s.masks.push((path, outline));
                    }
                    uses = true;
                }
            }
            if uses {
                self.masks.add(path, outline);
            }
        }
    }

    pub(super) fn finish(
        self,
        primitives: &[Primitive],
        paint_ids: &[String],
        page: u32,
    ) -> Vec<Receipt> {
        let stocks = self.stocks.ids();
        let masks = self.masks.ids();
        if stocks.is_empty() || masks.is_empty() {
            return vec![];
        }
        let index: HashMap<_, _> = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
        let layers: HashSet<_> = self.seeds.iter().map(|s| s.layer.as_str()).collect();
        let pens = primitives
            .iter()
            .filter(|p| {
                p.layer
                    .as_deref()
                    .is_some_and(|layer| layers.contains(layer))
            })
            .filter_map(|p| native_pen(p, page))
            .collect::<Vec<_>>();
        let empty = HashSet::new();
        let mut backing = None;
        let mut work = MAX_WORK;
        let mut out = Vec::new();
        for s in &self.seeds {
            let [(mask, mask_outline)] = s.masks.as_slice() else {
                continue;
            };
            if stocks.binary_search(&s.order).is_err() || masks.binary_search(mask).is_err() {
                continue;
            }
            let Some(left) = work.checked_sub(pens.len()) else {
                return vec![];
            };
            work = left;
            let mut matches: [Vec<Pen<'_>>; 2] = [Vec::new(), Vec::new()];
            for &pen in pens
                .iter()
                .filter(|p| p.order > *mask && p.layer == s.layer)
            {
                for (side, found) in [-1., 1.].into_iter().zip(&mut matches) {
                    if s.form.face(pen, side).is_some()
                        && !found.iter().any(|q| same_line(*q, pen, s.form.eps))
                    {
                        found.push(pen);
                    }
                }
            }
            let regular = |slot: usize, side| {
                unique(
                    matches[slot]
                        .iter()
                        .copied()
                        .filter(|&p| s.form.face(p, side).is_some_and(|f| s.form.regular_face(f))),
                )
            };
            let legacy = regular(0, -1.)
                .zip(regular(1, 1.))
                .map(|(a, b)| [a, b])
                .filter(|&pair| compatible_legacy(s.form, pair));
            let Some(([a, b], authored_faces)) = legacy.map(|pair| (pair, None)).or_else(|| {
                let (rects, _) = backing.get_or_insert_with(|| {
                    let rects = paint_ids
                        .iter()
                        .filter_map(|id| {
                            let p = index.get(id.as_str())?;
                            layers.contains(p.layer.as_deref()?).then_some(())?;
                            native_rectangle(p, page)
                        })
                        .collect::<Vec<_>>();
                    let ids = rects.iter().map(|p| p.id).collect::<HashSet<_>>();
                    (rects, ids)
                });
                choose_backed_pair(s.form, &matches, rects, &mut work)
                    .map(|(pair, proof)| (pair, Some(proof)))
            }) else {
                continue;
            };
            let r = Receipt {
                paint_source_id: source_id(page, s.order),
                pocket_source_id: source_id(page, *mask),
                pocket_outline: *mask_outline,
                face_source_ids: [a.id.to_owned(), b.id.to_owned()],
                outline: s.form.outline(),
                authored_faces,
            };
            let certified = backing.as_ref().map(|(_, ids)| ids).unwrap_or(&empty);
            if interpreted(&r, &index, certified, page).is_some() {
                out.push(r);
            }
        }
        out
    }
}

fn source_id(page: u32, path: u64) -> String {
    format!("pdf:p{page}:path:{path}:subpath:1")
}

fn order(id: &str, page: u32) -> Option<u64> {
    let (start, rest) = id.split_once(":path:")?;
    let (order, sub) = rest.split_once(":subpath:")?;
    let n = order.parse().ok()?;
    (sub == "1"
        && n > 0
        && start.strip_prefix("pdf:p")?.parse::<u32>().ok()? == page
        && source_id(page, n) == id)
        .then_some(n)
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
    ((p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall)
        && p.layer.as_deref().is_some_and(named_interior))
    .then_some(points)
}

#[derive(Clone, Copy)]
struct Pen<'a> {
    id: &'a str,
    layer: &'a str,
    order: u64,
    points: [Point; 2],
    width: f64,
}

fn native_pen(p: &Primitive, page: u32) -> Option<Pen<'_>> {
    let Shape::Line { start, end } = p.shape else {
        return None;
    };
    let layer = p.layer.as_deref()?;
    let width = p.stroke_width?;
    ((p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall)
        && start != end
        && width > 0.
        && width.is_finite()
        && named_interior(layer))
    .then_some(Pen {
        id: &p.id,
        layer,
        order: order(&p.id, page)?,
        points: [start, end],
        width,
    })
}

fn same_line(first: Pen<'_>, second: Pen<'_>, eps: f64) -> bool {
    (first.width - second.width).abs() <= eps
        && [second.points, [second.points[1], second.points[0]]]
            .into_iter()
            .any(|v| {
                first
                    .points
                    .into_iter()
                    .zip(v)
                    .all(|(a, b)| a.distance(b) <= eps)
            })
}

fn unique<T>(mut choices: impl Iterator<Item = T>) -> Option<T> {
    let first = choices.next()?;
    choices.next().is_none().then_some(first)
}

#[derive(Clone, Copy)]
struct RectPaint<'a> {
    id: &'a str,
    layer: &'a str,
    corners: [Point; 4],
}

fn native_rectangle(p: &Primitive, page: u32) -> Option<RectPaint<'_>> {
    order(&p.id, page)?;
    let points = filled(p)?;
    let corners = material::paint_rectangle(&[Subpath {
        ps: points.to_vec(),
        closed: true,
        ..Subpath::default()
    }])?;
    Some(RectPaint {
        id: &p.id,
        layer: p.layer.as_deref()?,
        corners,
    })
}

fn pair_metrics(form: Form, pens: [Pen<'_>; 2]) -> Option<[Face; 2]> {
    (pens[0].layer == pens[1].layer && form.eq(pens[0].width, pens[1].width)).then_some(())?;
    Some([form.face(pens[0], -1.)?, form.face(pens[1], 1.)?])
}

fn compatible_legacy(form: Form, pens: [Pen<'_>; 2]) -> bool {
    pair_metrics(form, pens).is_some_and(|[a, b]| {
        form.regular_face(a) && form.regular_face(b) && form.eq(a.shift, b.shift)
    })
}

fn continuation(form: Form, start: f64, paint: &[(&str, [[f64; 2]; 2])]) -> Option<Vec<String>> {
    let mut point = 0.;
    let mut chain = Vec::new();
    while !form.eq(point, start) {
        if chain.len() >= MAX_RETURN_PIECES {
            return None;
        }
        let (id, [lo, hi]) = unique(paint.iter().filter_map(|&(id, r)| {
            let span = form.terminal_interval(r, start)?;
            form.eq(span[1], point).then_some((id, span))
        }))?;
        if hi - lo <= form.eps || lo >= point - form.eps {
            return None;
        }
        chain.push(id.to_owned());
        point = lo;
    }
    (!chain.is_empty()).then_some(chain)
}

fn backed_faces(
    form: Form,
    pens: [Pen<'_>; 2],
    rectangles: &[RectPaint<'_>],
    work: &mut usize,
) -> Option<AuthoredFaces> {
    let [a, b] = pair_metrics(form, pens)?;
    let regular = [form.regular_face(a), form.regular_face(b)];
    if compatible_legacy(form, pens) || !form.small_depth_error(a, b) || regular == [false, false] {
        return None;
    }
    *work = work.checked_sub(rectangles.len().saturating_mul(1 + MAX_RETURN_PIECES))?;
    let widths = rectangles
        .iter()
        .filter(|p| p.layer == pens[0].layer)
        .filter_map(|p| Some((p.id, form.local_rectangle(p.corners)?)))
        .collect::<Vec<_>>();
    let facing = |f, side| {
        unique(
            widths
                .iter()
                .filter_map(|&(id, r)| form.supports_face(r, f, side).then_some(id)),
        )
    };
    let paint_source_ids = [facing(a, -1.)?.to_owned(), facing(b, 1.)?.to_owned()];
    let continuation = if let Some(face_index) = regular.iter().position(|&formal| !formal) {
        let start = [a, b][face_index].first;
        Some(Continuation {
            face_index,
            stock_source_ids: continuation(form, start, &widths)?,
        })
    } else {
        None
    };
    Some(AuthoredFaces {
        paint_source_ids,
        continuation,
    })
}

fn choose_backed_pair<'a>(
    form: Form,
    candidates: &[Vec<Pen<'a>>; 2],
    rects: &[RectPaint<'_>],
    work: &mut usize,
) -> Option<([Pen<'a>; 2], AuthoredFaces)> {
    let pairs = candidates[0].len().checked_mul(candidates[1].len())?;
    if pairs > 16 {
        return None;
    }
    *work = work.checked_sub(pairs)?;
    let mut accepted = None;
    for &a in &candidates[0] {
        for &b in &candidates[1] {
            let pens = [a, b];
            if let Some(proof) = backed_faces(form, pens, rects, work) {
                if accepted.is_some() {
                    return None;
                }
                accepted = Some((pens, proof));
            }
        }
    }
    accepted
}

fn interpreted(
    r: &Receipt,
    by_id: &HashMap<&str, &Primitive>,
    certified: &HashSet<&str>,
    page: u32,
) -> Option<[Point; 4]> {
    let paint = by_id.get(r.paint_source_id.as_str())?;
    let before = order(&r.paint_source_id, page)?;
    let after = order(&r.pocket_source_id, page)?;
    // The PDF geometry importer deliberately leaves pure white fills out of
    // its primitive inventory. The tracker retains this finite mask separately.
    if after <= before || after - before > 64 || by_id.contains_key(r.pocket_source_id.as_str()) {
        return None;
    }
    let form = Form::new(filled(paint)?)?;
    let mask = material::paint_rectangle(&[Subpath {
        ps: r.pocket_outline.to_vec(),
        closed: true,
        ..Subpath::default()
    }])?;
    if !form.white_chase(&mask) {
        return None;
    }
    let pen = |s: &str| native_pen(by_id.get(s)?, page);
    let pens @ [a, b] = [pen(&r.face_source_ids[0])?, pen(&r.face_source_ids[1])?];
    if a.order <= after
        || b.order <= after
        || a.layer != paint.layer.as_deref()?
        || a.layer != b.layer
    {
        return None;
    }
    if let Some(proof) = &r.authored_faces {
        let tail = proof
            .continuation
            .as_ref()
            .map(|x| x.stock_source_ids.as_slice())
            .unwrap_or_default();
        if tail.len() > MAX_RETURN_PIECES {
            return None;
        }
        let mut used = HashSet::new();
        let rects = proof
            .paint_source_ids
            .iter()
            .chain(tail)
            .map(|id| {
                if !used.insert(id.as_str()) || !certified.contains(id.as_str()) {
                    return None;
                }
                native_rectangle(by_id.get(id.as_str())?, page)
            })
            .collect::<Option<Vec<_>>>()?;
        let mut work = MAX_WORK;
        let expected = backed_faces(form, pens, &rects, &mut work)?;
        if *proof != expected {
            return None;
        }
    } else if !compatible_legacy(form, pens) {
        return None;
    }
    let expected = form.outline();
    expected
        .into_iter()
        .zip(r.outline)
        .all(|(a, b)| a.distance(b) <= form.eps)
        .then_some(expected)
}

pub(crate) fn verified(page: &PageScan) -> Option<Vec<[Point; 4]>> {
    if page.pocket_stock.is_empty() {
        return Some(Vec::new());
    }
    if page.pocket_stock.len() > MAX_SEEDS {
        return None;
    }
    let by_id: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let certified = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let mut previous = 0;
    let mut bodies = Vec::new();
    for r in &page.pocket_stock {
        let n = order(&r.paint_source_id, page.page_number)?;
        if n <= previous {
            return None;
        }
        previous = n;
        bodies.push(interpreted(r, &by_id, &certified, page.page_number)?);
    }
    Some(bodies)
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    verified(page)
        .map(|_| ())
        .ok_or_else(|| "invalid native pocket wall stock".into())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn backed_face_depths_and_overrun_require_exact_independent_stock() {
        let f = Form {
            cap: Point::new(0., 0.),
            axis: Point::new(1., 0.),
            half: 2.,
            length: 64.,
            chase_start: 24.,
            chase: [-1., 1.],
            eps: 4e-5,
        };
        let line = |id, start, y| Pen {
            id,
            order: 10,
            layer: "interior wall",
            points: [Point::new(start, y), Point::new(64., y)],
            width: 0.15,
        };
        let rect = |id, a, b, lo, hi| RectPaint {
            id,
            layer: "interior wall",
            corners: [
                Point::new(a, lo),
                Point::new(b, lo),
                Point::new(b, hi),
                Point::new(a, hi),
            ],
        };
        let a = line("left", 0., -2.4);
        let b = line("right", 0.35, 2.40016);
        let base = [
            rect("left paint", 0., 64., -2.4, -2.),
            rect("right paint", 0.35, 64., 2., 2.40016),
        ];
        let probe = |pair, rs: &[RectPaint<'_>]| backed_faces(f, pair, rs, &mut 1000);
        assert!(!compatible_legacy(f, [a, b]));
        assert!(probe([a, b], &base).unwrap().continuation.is_none());
        assert!(probe([a, b], &base[..1]).is_none());
        let long = line("long left", -20., -2.4);
        let mut supported = [
            rect("long paint", -20., 64., -2.4, -2.),
            base[1],
            rect("first stock", -4., 0., -2., 2.),
            rect("second stock", -20., -4., -2., 2.),
        ];
        let claim = probe([long, b], &supported).unwrap();
        assert_eq!(
            claim.continuation,
            Some(Continuation {
                face_index: 0,
                stock_source_ids: vec!["first stock".into(), "second stock".into()]
            })
        );
        assert!(probe([long, b], &supported[..2]).is_none());
        supported[3] = rect("gapped material", -20., -4.25, -2., 2.);
        assert!(probe([long, b], &supported).is_none());
        supported[3] = RectPaint {
            layer: "furniture",
            ..rect("furniture", -20., -4., -2., 2.)
        };
        assert!(probe([long, b], &supported).is_none());
        assert!(probe([long, line("another long", -20., 2.40016)], &supported).is_none());
    }

    #[test]
    fn pocket_tracking_distinguishes_dark_fill_text_from_possible_erasure() {
        use super::super::{Mat, strokes::Device};

        let bounds = Rect {
            l: 0.,
            t: 0.,
            r: 100.,
            b: 100.,
        };
        let corners = [
            Point::new(20., 20.),
            Point::new(70., 20.),
            Point::new(70., 24.),
            Point::new(20., 24.),
        ];
        for (rgb, known, mode, masked, linear_state, keep) in [
            ([0., 0., 1.], true, 0, false, true, true),
            ([1., 1., 1.], true, 0, false, true, false),
            ([0., 1., 0.], true, 0, false, true, false),
            ([0., 0., 1.], false, 0, false, true, false),
            ([0., 0., 1.], true, 0, true, true, false),
            ([0., 0., 1.], true, 0, false, false, false),
            ([0., 0., 1.], true, 4, false, true, false),
        ] {
            let mut tracker = Tracker::new(bounds);
            tracker.stocks.add(1, corners);
            tracker.masks.add(2, corners);
            let mut g = Graphics::new(Mat::ID, bounds);
            g.fill_device = Some(Device::Rgb);
            g.fill_ink = Some(NativeInk::Rgb(rgb.map(f64::to_bits)));
            g.black_fill = false;
            g.tr = mode;
            g.masked = masked;
            g.default_rendering_intent = linear_state;
            tracker.text(&g, known);
            assert_eq!(
                !tracker.stocks.ids().is_empty(),
                keep,
                "{rgb:?}/{known}/{mode}"
            );
            assert_eq!(
                !tracker.masks.ids().is_empty(),
                keep,
                "{rgb:?}/{known}/{mode}"
            );
        }
    }

    #[test]
    fn pocket_distal_body_preserves_the_chase_on_both_axes_and_windings() {
        let points = [
            Point::new(0., -2.),
            Point::new(0., 2.),
            Point::new(64., 2.),
            Point::new(64., 1.),
            Point::new(24., 1.),
            Point::new(24., -1.),
            Point::new(64., -1.),
            Point::new(64., -2.),
        ];
        for q in [0, 1, 2] {
            let transform = |p: Point| match q {
                0 => p,
                1 => Point::new(p.y + 600., p.x),
                _ => Point::new(500. - p.x, 120. + p.y),
            };
            let source = points.map(transform);
            let form = Form::new(&source).expect("native recessed course");
            let ideal = [
                Point::new(24., -1.),
                Point::new(24., 1.),
                Point::new(64.5, 1.),
                Point::new(64.5, -1.),
            ]
            .map(transform);
            assert!(form.white_chase(&ideal));
            let shifted = ideal.map(|p| p - form.axis * 0.25);
            assert!(!form.white_chase(&shifted));
            let mut reverse = source;
            reverse.reverse();
            assert_eq!(form.outline(), Form::new(&reverse).unwrap().outline());
            assert_eq!(form.chase_start, 24.);
        }
        let mut open = points;
        open[4].y += 0.05;
        assert!(Form::new(&open).is_none());
        assert!(Form::new(&points[..4]).is_none());
    }
}
