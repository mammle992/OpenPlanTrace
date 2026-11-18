use super::{MAX_ITEMS, Mat, Warnings, finite};
mod finish;
mod material;
mod strokes;
mod visibility;
use crate::model::{Document, Page, Point, Primitive, Shape};
use crate::placement::insulation::MaterialTile;
use lopdf::{
    Dictionary, Document as Pdf, Encoding, LoadOptions, Object, ObjectId, content::Content,
};
use std::{
    collections::{HashMap, HashSet},
    f64::consts::{PI, TAU},
    rc::Rc,
};

const STREAM_LIMIT: usize = 48 << 20;
const DOC_BUDGET: usize = 384 << 20;
fn num(o: &Object) -> Option<f64> {
    match o {
        Object::Integer(i) => Some(*i as f64),
        Object::Real(n) => Some(*n as f64),
        _ => None,
    }
    .filter(|n| n.is_finite() && n.abs() < 1e15)
}
fn numbers<const N: usize>(v: &[Object]) -> Option<[f64; N]> {
    if v.len() != N {
        return None;
    }
    let mut out = [0.; N];
    for (i, o) in v.iter().enumerate() {
        out[i] = num(o)?
    }
    Some(out)
}
fn obj<'a>(d: &'a Pdf, o: &'a Object) -> Option<&'a Object> {
    d.dereference(o).ok().map(|(_, v)| v)
}
fn dict<'a>(d: &'a Pdf, o: &'a Object) -> Option<&'a Dictionary> {
    obj(d, o)?.as_dict().ok()
}
fn inherited<'a>(d: &'a Pdf, mut id: ObjectId, key: &[u8]) -> Option<&'a Object> {
    let mut seen = HashSet::new();
    for _ in 0..64 {
        if !seen.insert(id) {
            return None;
        }
        let page = d.get_dictionary(id).ok()?;
        if let Ok(o) = page.get(key) {
            return obj(d, o);
        }
        id = page.get(b"Parent").ok()?.as_reference().ok()?;
    }
    None
}
fn resource<'a>(
    d: &'a Pdf,
    r: Option<&'a Dictionary>,
    kind: &[u8],
    name: &[u8],
) -> Option<&'a Object> {
    dict(d, r?.get(kind).ok()?)?.get(name).ok()
}
#[derive(Clone, Copy, Debug)]
struct Rect {
    l: f64,
    t: f64,
    r: f64,
    b: f64,
}
impl Rect {
    fn union(self, other: Self) -> Self {
        Self {
            l: self.l.min(other.l),
            t: self.t.min(other.t),
            r: self.r.max(other.r),
            b: self.b.max(other.b),
        }
    }
    fn points(points: &[Point]) -> Option<Self> {
        if points.is_empty() || !points.iter().copied().all(finite) {
            return None;
        }
        let p = points[0];
        let mut v = Self {
            l: p.x,
            t: p.y,
            r: p.x,
            b: p.y,
        };
        for p in &points[1..] {
            v.l = v.l.min(p.x);
            v.r = v.r.max(p.x);
            v.t = v.t.min(p.y);
            v.b = v.b.max(p.y)
        }
        Some(v)
    }
    fn intersect(self, v: Self) -> Option<Self> {
        let r = Self {
            l: self.l.max(v.l),
            r: self.r.min(v.r),
            t: self.t.max(v.t),
            b: self.b.min(v.b),
        };
        (r.l < r.r && r.t < r.b).then_some(r)
    }
    fn inside(self, p: Point) -> bool {
        p.x >= self.l - 1e-6 && p.x <= self.r + 1e-6 && p.y >= self.t - 1e-6 && p.y <= self.b + 1e-6
    }
    fn clip(self, a: Point, b: Point) -> Option<(Point, Point)> {
        let d = b - a;
        let (mut lo, mut hi) = (0_f64, 1_f64);
        for (p, q) in [
            (-d.x, a.x - self.l),
            (d.x, self.r - a.x),
            (-d.y, a.y - self.t),
            (d.y, self.b - a.y),
        ] {
            if p.abs() < 1e-14 {
                if q < 0. {
                    return None;
                }
            } else if p < 0. {
                lo = lo.max(q / p)
            } else {
                hi = hi.min(q / p)
            }
        }
        (lo < hi).then(|| (a + d * lo, a + d * hi))
    }
}
fn rect_path(t: Mat, [x, y, w, h]: [f64; 4]) -> Vec<Point> {
    [(x, y), (x + w, y), (x + w, y + h), (x, y + h)]
        .into_iter()
        .map(|(x, y)| t.point(Point::new(x, y)))
        .collect()
}
fn rectangular(ps: &[Point]) -> Option<Rect> {
    let ps = if ps.len() == 5 && ps[0].distance(ps[4]) < 1e-5 {
        &ps[..4]
    } else {
        ps
    };
    if ps.len() != 4 {
        return None;
    }
    let r = Rect::points(ps)?;
    let eps = 1e-5;
    if r.r - r.l <= eps || r.b - r.t <= eps {
        return None;
    }
    let mut mask = 0u8;
    for i in 0..4 {
        let p = ps[i];
        let q = ps[(i + 1) % 4];
        if (p.x - q.x).abs() > eps && (p.y - q.y).abs() > eps {
            return None;
        }
        let x = if (p.x - r.l).abs() <= eps {
            0
        } else if (p.x - r.r).abs() <= eps {
            1
        } else {
            return None;
        };
        let y = if (p.y - r.t).abs() <= eps {
            0
        } else if (p.y - r.b).abs() <= eps {
            2
        } else {
            return None;
        };
        mask |= 1 << (x + y);
    }
    (mask == 15).then_some(r)
}
#[derive(Clone, Default)]
struct Subpath {
    ps: Vec<Point>,
    closed: bool,
    lines: usize,
    curves: usize,
    curve_bounds: Option<Rect>,
}
impl Subpath {
    fn bounds(&self) -> Option<Rect> {
        let r = Rect::points(&self.ps)?;
        Some(self.curve_bounds.map_or(r, |b| b.union(r)))
    }
}
fn circular(ps: &[Point]) -> Option<Shape> {
    if ps.len() < 7 {
        return None;
    }
    let a = ps[0];
    let b = ps[ps.len() / 3];
    let c = ps[2 * ps.len() / 3];
    let u = b - a;
    let v = c - a;
    let det = 2. * u.cross(v);
    if det.abs() < 1e-8 * u.norm() * v.norm() {
        return None;
    }
    let center = a + Point::new(
        (u.dot(u) * v.y - v.dot(v) * u.y) / det,
        (v.dot(v) * u.x - u.dot(u) * v.x) / det,
    );
    let radius = a.distance(center);
    if !(1e-5..1e10).contains(&radius) {
        return None;
    }
    // Cubic quarter-circle approximation has a small radial error; validate every sampled point.
    if ps
        .iter()
        .any(|p| (p.distance(center) - radius).abs() > 0.0015 * radius + 1e-6)
    {
        return None;
    }
    let start = (a.y - center.y).atan2(a.x - center.x);
    let (mut prev, mut sweep, mut sign) = (start, 0., 0_f64);
    for p in &ps[1..] {
        let angle = (p.y - center.y).atan2(p.x - center.x);
        let step = (angle - prev + PI).rem_euclid(TAU) - PI;
        if step.abs() > 1e-9 {
            if sign != 0. && step.signum() != sign {
                return None;
            }
            sign = step.signum();
            sweep += step
        }
        prev = angle;
    }
    if sweep.abs() < PI / 9. || sweep.abs() > TAU + 1e-3 {
        return None;
    }
    Some(Shape::Arc {
        center,
        radius,
        start_angle: start,
        sweep,
    })
}
#[derive(Clone)]
struct Graphics {
    ctm: Mat,
    clip: Option<Rect>,
    // Inward half-planes for oblique convex clip polygons; axis-aligned clips use only the AABB.
    clip_planes: Vec<(Point, Point)>,
    masked: bool,
    // Glyph clipping is not resolved by the vector importer. Keep this uncertainty
    // separate from frozen observations and suppress new finish source authority.
    finish_clip_unsafe: bool,
    fill: bool,
    stroke: bool,
    fill_alpha: f64,
    stroke_alpha: f64,
    white_pen: bool,
    stroke_device: Option<strokes::Device>,
    fill_device: Option<strokes::Device>,
    black_pen: bool,
    black_fill: bool,
    pen_width: f64,
    pen_state_safe: bool,
    undashed_pen: bool,
    projecting_cap: bool,
    miter_limit: f64,
    dashed: bool,
    font: Vec<u8>,
    size: f64,
    lead: f64,
    tr: i64,
    spacing: f64,
    word_spacing: f64,
    hscale: f64,
    rise: f64,
    fill_space: Vec<u8>,
    stroke_space: Vec<u8>,
    material_cell: Option<Rc<material::Cell>>,
    material_state_safe: bool,
    solid_fill_safe: bool,
    uncertain_clip: Option<Rect>,
}
impl Graphics {
    fn new(ctm: Mat, clip: Rect) -> Self {
        Self {
            ctm,
            clip: Some(clip),
            clip_planes: Vec::new(),
            masked: false,
            finish_clip_unsafe: false,
            fill: true,
            stroke: true,
            fill_alpha: 1.,
            stroke_alpha: 1.,
            white_pen: false,
            stroke_device: Some(strokes::Device::Gray),
            fill_device: Some(strokes::Device::Gray),
            black_pen: true,
            black_fill: true,
            pen_width: 1.,
            pen_state_safe: true,
            undashed_pen: true,
            projecting_cap: false,
            miter_limit: 10.,
            dashed: false,
            font: vec![],
            size: 0.,
            lead: 0.,
            tr: 0,
            spacing: 0.,
            word_spacing: 0.,
            hscale: 1.,
            rise: 0.,
            fill_space: b"DeviceGray".to_vec(),
            stroke_space: b"DeviceGray".to_vec(),
            material_cell: None,
            material_state_safe: true,
            solid_fill_safe: true,
            uncertain_clip: None,
        }
    }
    fn inside(&self, p: Point) -> bool {
        self.clip.is_some_and(|r| r.inside(p))
            && self
                .clip_planes
                .iter()
                .all(|(a, n)| (p - *a).dot(*n) >= -1e-7)
    }
    fn segment(&self, a: Point, b: Point) -> Option<(Point, Point)> {
        let (a, b) = self.clip?.clip(a, b)?;
        let (mut lo, mut hi) = (0_f64, 1_f64);
        let d = b - a;
        for &(p, n) in &self.clip_planes {
            let u = (a - p).dot(n);
            let v = d.dot(n);
            if v.abs() <= 1e-12 {
                if u < -1e-7 {
                    return None;
                }
            } else if v > 0. {
                lo = lo.max(-u / v)
            } else {
                hi = hi.min(-u / v)
            }
        }
        (lo < hi).then(|| (a + d * lo, a + d * hi))
    }
    fn clip_to_polygon(&mut self, points: &[Point]) -> bool {
        if let Some(r) = rectangular(points) {
            self.clip = self.clip.and_then(|c| c.intersect(r));
            return true;
        }
        if points.len() < 3 || points.len() > 128 {
            self.clip = None;
            return false;
        }
        let Some(r) = Rect::points(points) else {
            self.clip = None;
            return false;
        };
        let origin = points[0];
        let area: f64 = (0..points.len())
            .map(|i| (points[i] - origin).cross(points[(i + 1) % points.len()] - origin))
            .sum();
        if area.abs() < 1e-8 {
            self.clip = None;
            return false;
        }
        let mut planes = vec![];
        for i in 0..points.len() {
            let p = points[i];
            let d = points[(i + 1) % points.len()] - p;
            if d.norm() <= 1e-10 {
                continue;
            }
            let n = Point::new(-d.y, d.x).unit() * area.signum();
            if points.iter().any(|q| (*q - p).dot(n) < -1e-7) {
                self.clip = None;
                return false;
            }
            planes.push((p, n));
        }
        if self.clip_planes.len() + planes.len() > 256 {
            self.clip = None;
            return false;
        }
        self.clip_planes.extend(planes);
        self.clip = self.clip.and_then(|c| c.intersect(r));
        true
    }
}
struct Draw<'a> {
    finish_history: crate::finish_visibility::PaintHistory,
    doc: &'a Pdf,
    page: u32,
    items: Vec<Primitive>,
    material_tiles: Vec<MaterialTile>,
    placement_paint: visibility::VisibleFills,
    placement_polygon_paint: visibility::VisibleFills,
    stroke_widths: strokes::StrokeWidths,
    material_cells: HashMap<Vec<u8>, Option<Rc<material::Cell>>>,
    cell_budget: usize,
    material_strokes: usize,
    warnings: &'a mut Warnings,
    operations: usize,
    decoded: usize,
    point_work: usize,
    path_count: u64,
    stack: Vec<ObjectId>,
    off: &'a HashSet<ObjectId>,
    fonts: HashMap<usize, (Encoding<'a>, Option<CidWidths>)>,
    previous_text: Option<GlyphRun>,
    limit: usize,
}

/// A convex source path gets only its conservative rectangular occlusion
/// footprint. The original polygon is retained and measured by placement.
fn convex_paint_bounds(sub: &Subpath) -> Option<[Point; 4]> {
    if sub.curves != 0 {
        return None;
    }
    let mut points = sub.ps.as_slice();
    if points.first() == points.last() {
        points = &points[..points.len().checked_sub(1)?];
    }
    if !(3..=64).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
        return None;
    }
    let base = points[0];
    let area: f64 = points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .map(|(a, b)| (*a - base).cross(*b - base))
        .sum();
    let bounds = Rect::points(points)?;
    let scale = (bounds.r - bounds.l).max(bounds.b - bounds.t).max(1.);
    let eps = scale * scale * 64. * f64::EPSILON;
    if area.abs() <= eps {
        return None;
    }
    let sign = area.signum();
    for (&a, &b) in points.iter().zip(points.iter().cycle().skip(1)) {
        if a.distance(b) <= scale * 64. * f64::EPSILON {
            return None;
        }
        if points.iter().any(|&p| sign * (b - a).cross(p - a) < -eps) {
            return None;
        }
    }
    Some([
        Point::new(bounds.l, bounds.t),
        Point::new(bounds.r, bounds.t),
        Point::new(bounds.r, bounds.b),
        Point::new(bounds.l, bounds.b),
    ])
}
struct GlyphRun {
    index: usize,
    font: usize,
    axis: Point,
    next: Point,
    last: Point,
    count: usize,
}
struct CidWidths {
    default: f64,
    ranges: Vec<(u32, u32, f64)>,
}
impl CidWidths {
    fn new(d: &Pdf, font: &Dictionary) -> Option<Self> {
        if font.get(b"Encoding").ok()?.as_name().ok()? != b"Identity-H" {
            return None;
        }
        let descendant = dict(
            d,
            obj(d, font.get(b"DescendantFonts").ok()?)?
                .as_array()
                .ok()?
                .first()?,
        )?;
        let default = descendant.get(b"DW").ok().and_then(num).unwrap_or(1000.);
        let mut ranges = vec![];
        if let Some(w) = descendant
            .get(b"W")
            .ok()
            .and_then(|o| obj(d, o))
            .and_then(|o| o.as_array().ok())
        {
            let mut i = 0;
            while i < w.len() {
                let first = u32::try_from(w.get(i)?.as_i64().ok()?).ok()?;
                i += 1;
                let o = w.get(i)?;
                i += 1;
                if let Ok(widths) = o.as_array() {
                    for (j, v) in widths.iter().enumerate() {
                        let cid = first.checked_add(j as u32)?;
                        if ranges.len() >= 65536 {
                            return None;
                        }
                        ranges.push((cid, cid, num(v)?))
                    }
                } else {
                    let last = u32::try_from(o.as_i64().ok()?).ok()?;
                    if last < first || ranges.len() >= 65536 {
                        return None;
                    }
                    ranges.push((first, last, num(w.get(i)?)?));
                    i += 1
                }
            }
        }
        ranges.sort_by_key(|r| r.0);
        Some(Self { default, ranges })
    }
    fn width(&self, cid: u32) -> f64 {
        let i = self.ranges.partition_point(|r| r.0 <= cid);
        if i > 0 && self.ranges[i - 1].1 >= cid {
            self.ranges[i - 1].2
        } else {
            self.default
        }
    }
}
impl<'a> Draw<'a> {
    fn emit_stroke(
        &mut self,
        id: String,
        layer: Option<&str>,
        shape: Shape,
        sub: &Subpath,
        g: &Graphics,
    ) -> Result<(), String> {
        finish::observe(
            &mut self.finish_history,
            &id,
            &shape,
            sub,
            g,
            self.path_count * 2,
        );
        self.stroke_widths.observe(self.items.len(), &shape, sub, g);
        self.emit(id, layer, shape)
    }
    fn cover(&mut self, bounds: Option<Rect>) {
        if let Some(r) = bounds {
            self.finish_history.record_bounds(
                self.path_count * 2 + 1,
                Point::new(r.l, r.t),
                Point::new(r.r, r.b),
            );
        }
        self.placement_paint.unknown(bounds);
        self.placement_polygon_paint.unknown(bounds);
        self.stroke_widths.footprints.unknown(bounds);
    }
    fn emit(&mut self, id: String, layer: Option<&str>, shape: Shape) -> Result<(), String> {
        if self.items.len() >= self.limit {
            return Err("PDF primitive limit exceeded".into());
        }
        self.items.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id,
            layer: layer.map(str::to_owned),
            shape,
        });
        Ok(())
    }
    fn paint(
        &mut self,
        subs: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
        layer: Option<&str>,
        even_odd: bool,
    ) -> Result<(), String> {
        self.path_count += 1;
        finish::paint(
            &mut self.finish_history,
            subs,
            g,
            fill,
            stroke,
            even_odd,
            self.path_count * 2,
        );
        // Separate v14 evidence: preserve the frozen rectangular tracker while
        // conservatively retiring nonrectangular receipts under later paint.
        if fill && g.fill_alpha != 0. {
            self.placement_polygon_paint
                .paint(subs, g.clip.or(g.uncertain_clip), even_odd);
        }
        if stroke
            && g.stroke_alpha != 0.
            && (g.white_pen || g.masked || !g.material_state_safe || !g.pen_state_safe)
        {
            self.placement_polygon_paint.white_stroke(subs, g);
        }
        if fill && (g.fill_alpha == 1. || !g.material_state_safe && g.fill_alpha != 0.) {
            self.placement_paint
                .paint(subs, g.clip.or(g.uncertain_clip), even_odd);
            // Opaque black composited over identical native black leaves that
            // pen visible; a black glyph can still obscure a colored stroke.
            let black = g
                .fill_device
                .filter(|_| g.black_fill && g.fill_alpha == 1. && g.pen_state_safe && !g.masked);
            self.stroke_widths.footprints.paint_where(
                subs,
                g.clip.or(g.uncertain_clip),
                even_odd,
                |&(_, source)| black.is_none() || black != source,
            );
        }
        let erasing_stroke = stroke && g.white_pen && g.stroke_alpha == 1. && g.material_state_safe;
        if erasing_stroke {
            self.placement_paint.white_stroke(subs, g);
        }
        if stroke && g.white_pen && g.stroke_alpha != 0. {
            self.stroke_widths.footprints.white_stroke(subs, g);
        }
        if g.clip.is_none() || g.masked {
            return Ok(());
        }
        let rectangular_fill = if fill
            && g.fill
            && g.fill_alpha == 1.
            && g.material_state_safe
            && g.solid_fill_safe
            && !erasing_stroke
        {
            material::paint_rectangle(subs)
        } else {
            None
        };
        if fill
            && g.fill_alpha > 0.
            && (!self.material_tiles.is_empty() || g.material_cell.is_some())
            && let Some(outline) = material::paint_rectangle(subs)
        {
            // A later solid rectangle, including white paint, can erase the original
            // material. Retire its placement evidence independently of wall detection.
            if g.fill_alpha == 1.
                && let Some(clip) = g.clip.and_then(|r| r.intersect(Rect::points(&outline)?))
            {
                self.point_work += self.material_tiles.len();
                self.material_tiles.retain(|t| {
                    !Rect::points(&t.outline).is_some_and(|bounds| bounds.intersect(clip).is_some())
                        || !material::overlaps(&outline, &t.outline)
                });
            }
            if g.fill_alpha == 1.
                && g.stroke_alpha == 1.
                && g.material_state_safe
                && self.stack.is_empty()
                && let Some(cell) = &g.material_cell
                && outline.iter().all(|p| g.inside(*p))
            {
                self.material_strokes += cell.strokes.len();
                if self.material_strokes > MAX_ITEMS {
                    return Err("PDF material stroke limit exceeded".into());
                }
                self.material_tiles.push(MaterialTile {
                    id: format!(
                        "pdf:p{}:path:{}:subpath:1:material",
                        self.page, self.path_count
                    ),
                    layer: layer.map(str::to_owned),
                    outline,
                    origin: cell.origin,
                    repeat: cell.repeat,
                    across: cell.across,
                    strokes: cell.strokes.clone(),
                });
            }
        }
        let fill = fill && g.fill && g.fill_alpha > 0.;
        let stroke = stroke && g.stroke && g.stroke_alpha > 0. && !g.dashed;
        if !fill && !stroke {
            return Ok(());
        }
        let boxes: Vec<_> = subs.iter().map(|s| Rect::points(&s.ps)).collect();
        for (i, sub) in subs.iter().enumerate() {
            let ps = &sub.ps;
            if ps.len() < 2 || boxes[i].is_none() {
                continue;
            }
            let id = format!(
                "pdf:p{}:path:{}:subpath:{}",
                self.page,
                self.path_count,
                i + 1
            );
            let inside = ps.iter().all(|p| g.inside(*p));
            // A compound fill can describe a hole. Do not report such subpaths as independent solid observations.
            let solid = fill
                && ps.len() >= 3
                && if subs.len() > 128 {
                    false
                } else {
                    !boxes.iter().enumerate().any(|(j, b)| {
                        j != i && b.is_some_and(|b| b.intersect(boxes[i].unwrap()).is_some())
                    })
                };
            if fill && !solid && ps.len() >= 3 {
                self.warnings
                    .add("Ambiguous compound PDF fill not emitted as solid")
            }
            if solid && inside {
                if (ps.len() == 4 || ps.first() == ps.last())
                    && let Some(corners) = rectangular_fill
                {
                    self.placement_paint.add(id.clone(), corners);
                }
                if g.fill_alpha == 1.
                    && g.material_state_safe
                    && g.solid_fill_safe
                    && !erasing_stroke
                    && !g.masked
                    && material::paint_rectangle(std::slice::from_ref(sub)).is_none()
                    && let Some(bounds) = convex_paint_bounds(sub)
                {
                    self.placement_polygon_paint.add(id.clone(), bounds);
                }
                self.emit(
                    id,
                    layer,
                    Shape::Polyline {
                        points: ps.clone(),
                        closed: true,
                        filled: true,
                    },
                )?;
                continue;
            }
            if fill && !inside {
                self.warnings.add("Partially clipped PDF fill omitted")
            }
            if !stroke {
                continue;
            }
            if inside {
                if sub.curves > 0 && sub.lines == 0 {
                    if let Some(shape) = circular(ps) {
                        self.emit_stroke(id, layer, shape, sub, g)?;
                        continue;
                    }
                }
                if ps.len() == 2 {
                    if ps[0].distance(ps[1]) > 1e-8 {
                        self.emit_stroke(
                            id,
                            layer,
                            Shape::Line {
                                start: ps[0],
                                end: ps[1],
                            },
                            sub,
                            g,
                        )?
                    }
                } else {
                    self.emit_stroke(
                        id,
                        layer,
                        Shape::Polyline {
                            points: ps.clone(),
                            closed: sub.closed,
                            filled: false,
                        },
                        sub,
                        g,
                    )?
                }
            } else {
                for (j, (p, q)) in ps
                    .windows(2)
                    .map(|p| (p[0], p[1]))
                    .chain(sub.closed.then(|| (*ps.last().unwrap(), ps[0])))
                    .enumerate()
                {
                    if let Some((start, end)) = g.segment(p, q) {
                        if start.distance(end) > 1e-8 {
                            self.emit_stroke(
                                format!("{id}:clip:{}", j + 1),
                                layer,
                                Shape::Line { start, end },
                                sub,
                                g,
                            )?
                        }
                    }
                }
            }
        }
        Ok(())
    }
    fn text(
        &mut self,
        res: Option<&'a Dictionary>,
        g: &Graphics,
        tm: &mut Mat,
        values: &[Object],
        visible: bool,
        layer: Option<&str>,
    ) -> Result<(), String> {
        let has_text = visible
            && values
                .iter()
                .any(|v| v.as_str().is_ok_and(|s| !s.is_empty()));
        if has_text {
            finish::text(&mut self.finish_history, g, self.path_count * 2 + 1);
        }
        let Some(font) = resource(self.doc, res, b"Font", &g.font).and_then(|o| dict(self.doc, o))
        else {
            if has_text {
                finish::unknown_text(&mut self.finish_history, g, self.path_count * 2 + 1);
            }
            self.warnings
                .add("PDF text without resolvable font omitted");
            return Ok(());
        };
        if has_text
            && !matches!(
                font.get(b"Subtype").ok().and_then(|o| o.as_name().ok()),
                Some(b"Type1" | b"MMType1" | b"TrueType" | b"Type0")
            )
        {
            finish::unknown_text(&mut self.finish_history, g, self.path_count * 2 + 1);
        }
        let key = font as *const _ as usize;
        if self.fonts.len() >= 256 && !self.fonts.contains_key(&key) {
            return Err("PDF font resource limit exceeded".into());
        }
        if let std::collections::hash_map::Entry::Vacant(e) = self.fonts.entry(key) {
            if let Ok(enc) = font.get_font_encoding_with_limit(self.doc, 2 << 20) {
                e.insert((enc, CidWidths::new(self.doc, font)));
            } else {
                if has_text {
                    finish::unknown_text(&mut self.finish_history, g, self.path_count * 2 + 1);
                }
                self.warnings.add("PDF font encoding unavailable");
                return Ok(());
            }
        }
        let mut text = String::new();
        let mut advance = 0.;
        let first = font.get(b"FirstChar").ok().and_then(num).unwrap_or(0.) as i64;
        let widths = font
            .get(b"Widths")
            .ok()
            .and_then(|o| obj(self.doc, o))
            .and_then(|o| o.as_array().ok());
        for v in values {
            if let Some(n) = num(v) {
                advance -= n / 1000. * g.size;
                continue;
            }
            let Ok(raw) = v.as_str() else { continue };
            if let Ok(s) = Pdf::decode_text(&self.fonts[&key].0, raw) {
                // Widths are font glyph metrics for simple fonts. Without them only the next text advance is approximate.
                if let Some(ws) = widths {
                    advance += raw
                        .iter()
                        .map(|b| {
                            ws.get((*b as i64 - first) as usize)
                                .and_then(num)
                                .unwrap_or(500.)
                                / 1000.
                                * g.size
                                + g.spacing
                                + if *b == b' ' { g.word_spacing } else { 0. }
                        })
                        .sum::<f64>();
                } else if let Some(cid) = &self.fonts[&key].1 {
                    advance += raw
                        .chunks_exact(2)
                        .map(|b| {
                            cid.width(u16::from_be_bytes([b[0], b[1]]) as u32) / 1000. * g.size
                                + g.spacing
                        })
                        .sum::<f64>();
                } else {
                    advance += s.chars().count() as f64 * (g.size * 0.5 + g.spacing)
                        + s.chars().filter(|c| *c == ' ').count() as f64 * g.word_spacing
                }
                text.push_str(&s);
            }
        }
        let m = g.ctm.then(*tm);
        let at = m.point(Point::new(0., g.rise));
        let height = m.0[2].hypot(m.0[3]) * g.size.abs();
        let paint = match g.tr.rem_euclid(4) {
            0 => g.fill && g.fill_alpha > 0.,
            1 => g.stroke && g.stroke_alpha > 0.,
            2 => (g.fill && g.fill_alpha > 0.) || (g.stroke && g.stroke_alpha > 0.),
            _ => false,
        };
        if visible
            && paint
            && height > 0.
            && height.is_finite()
            && finite(at)
            && !text.is_empty()
            && !g.masked
            && g.inside(at)
        {
            let axis = Point::new(m.0[0], m.0[1]).unit() * (g.size * g.hscale).signum();
            let next = m.point(Point::new(advance * g.hscale, g.rise));
            let mut joined = false;
            if let Some(run) = self.previous_text.as_mut() {
                if run.index + 1 == self.items.len()
                    && run.count < 512
                    && run.axis.dot(axis) > 0.9998
                {
                    let last = &mut self.items[run.index];
                    let delta = at - run.next;
                    let gap = delta.dot(axis);
                    if let Shape::Text {
                        height: h, value, ..
                    } = &mut last.shape
                    {
                        let up = Point::new(m.0[2], m.0[3]).unit();
                        let raised = delta.dot(up);
                        let superscript = if text.len() == 1
                            && text.as_bytes()[0].is_ascii_digit()
                            && (0.35..=0.82).contains(&(height / *h))
                            && (0.18..=0.90).contains(&(raised / *h))
                            && (-0.15..=0.45).contains(&(gap / *h))
                            && last.layer.as_deref() == layer
                        {
                            Some(
                                ['⁰', '¹', '²', '³', '⁴', '⁵', '⁶', '⁷', '⁸', '⁹']
                                    [(text.as_bytes()[0] - b'0') as usize],
                            )
                        } else {
                            None
                        };
                        if let Some(c) = superscript {
                            value.push(c);
                            last.id.push_str(&format!(",{}", self.operations));
                            run.next = next - up * raised;
                            run.last = at - up * raised;
                            run.count += 1;
                            joined = true;
                        } else if run.font == key
                            && last.layer.as_deref() == layer
                            && (height - *h).abs() <= height * 0.06
                            && delta.cross(axis).abs() <= height * 0.13
                            && gap >= -0.32 * height
                            && gap <= 0.7 * height
                            && (at - run.last).dot(axis) > 0.01 * height
                            && value.len() + text.len() < 4096
                        {
                            if gap > 0.19 * height
                                && !value.ends_with(char::is_whitespace)
                                && !text.starts_with(char::is_whitespace)
                            {
                                value.push(' ')
                            }
                            value.push_str(&text);
                            last.id.push_str(&format!(",{}", self.operations));
                            run.next = next;
                            run.last = at;
                            run.count += 1;
                            joined = true;
                        }
                    }
                }
            }
            if !joined && !text.trim().is_empty() {
                self.emit(
                    format!("pdf:p{}:text:{}", self.page, self.operations),
                    layer,
                    Shape::Text {
                        at,
                        value: text,
                        height,
                    },
                )?;
                self.previous_text = Some(GlyphRun {
                    index: self.items.len() - 1,
                    font: key,
                    axis,
                    next,
                    last: at,
                    count: 1,
                });
            }
        }
        *tm = tm.then(Mat::translate(advance * g.hscale, 0.));
        Ok(())
    }
    fn image(&mut self, g: &Graphics, layer: Option<&str>) -> Result<(), String> {
        if g.fill_alpha == 0. && (!g.pen_state_safe || g.masked) {
            if let Some(bounds) = Rect::points(&rect_path(g.ctm, [0., 0., 1., 1.]))
                .and_then(|r| g.clip.or(g.uncertain_clip)?.intersect(r))
            {
                self.finish_history.record_bounds(
                    self.path_count * 2 + 1,
                    Point::new(bounds.l, bounds.t),
                    Point::new(bounds.r, bounds.b),
                );
            }
        }
        if g.fill_alpha > 0. {
            self.cover(
                Rect::points(&rect_path(g.ctm, [0., 0., 1., 1.]))
                    .and_then(|bounds| g.clip.or(g.uncertain_clip)?.intersect(bounds)),
            );
        }
        if g.masked || g.fill_alpha <= 0. {
            return Ok(());
        }
        if !g.clip_planes.is_empty()
            && !rect_path(g.ctm, [0., 0., 1., 1.])
                .iter()
                .all(|p| g.inside(*p))
        {
            self.warnings
                .add("Partially clipped oblique PDF image footprint omitted");
            return Ok(());
        }
        if let Some(r) = Rect::points(&rect_path(g.ctm, [0., 0., 1., 1.]))
            .and_then(|r| g.clip.and_then(|c| c.intersect(r)))
        {
            self.emit(
                format!("pdf:p{}:image:{}", self.page, self.operations),
                layer,
                Shape::Image {
                    min: Point::new(r.l, r.t),
                    max: Point::new(r.r, r.b),
                },
            )?;
            self.warnings
                .add("PDF image footprint retained; raster contents not vectorized");
        }
        Ok(())
    }

    fn run(
        &mut self,
        data: &[u8],
        res: Option<&'a Dictionary>,
        mut g: Graphics,
        base_layer: Option<&str>,
    ) -> Result<(), String> {
        self.decoded = self.decoded.saturating_add(data.len());
        // Resource replacement of a device space makes cross-paint equality unknown.
        for space in [&mut g.stroke_device, &mut g.fill_device] {
            *space = space.and_then(|s| strokes::Device::native(self.doc, res, s.name()));
        }
        if self.decoded > DOC_BUDGET {
            return Err("PDF expanded content budget exceeded".into());
        }
        let mut graphics = Vec::new();
        // A tiling pattern's matrix maps to the default, not the current user space.
        let default_space = g.ctm;
        let mut paths: Vec<Subpath> = vec![];
        let mut clipping = false;
        let mut text_active = false;
        let (mut tm, mut line) = (Mat::ID, Mat::ID);
        let mut marks: Vec<(bool, Option<String>)> = vec![];
        for op in Content::decode_iter(data) {
            let op = op.map_err(|e| format!("Invalid PDF content: {e}"))?;
            self.operations += 1;
            if self.operations > 6_000_000 || self.point_work > 8_000_000 {
                return Err("PDF expanded geometry budget exceeded".into());
            }
            let a = &op.operands;
            let code = op.operator.as_str();
            // h/re leave the current point at the beginning of the closed subpath.
            if matches!(code, "l" | "c" | "v" | "y") && paths.last().is_some_and(|p| p.closed) {
                if let Some(p) = paths.last().and_then(|p| p.ps.first().copied()) {
                    paths.push(Subpath {
                        ps: vec![p],
                        ..Default::default()
                    });
                    self.point_work += 1;
                }
            }
            let visible = marks.iter().all(|v| v.0);
            let layer = marks
                .iter()
                .rev()
                .find_map(|v| v.1.as_deref())
                .or(base_layer);
            match code {
                "q" => {
                    if graphics.len() >= 128 {
                        return Err("PDF graphics stack limit exceeded".into());
                    }
                    graphics.push(g.clone())
                }
                "Q" => {
                    if let Some(v) = graphics.pop() {
                        g = v
                    } else {
                        self.warnings.add("Unbalanced PDF graphics restore")
                    }
                }
                "cm" => {
                    if let Some(m) = numbers(a) {
                        g.ctm = g.ctm.then(Mat(m))
                    }
                }
                "m" => {
                    if let Some([x, y]) = numbers(a) {
                        paths.push(Subpath {
                            ps: vec![g.ctm.point(Point::new(x, y))],
                            ..Default::default()
                        });
                        self.point_work += 1
                    }
                }
                "l" => {
                    if let (Some(s), Some([x, y])) = (paths.last_mut(), numbers(a)) {
                        s.ps.push(g.ctm.point(Point::new(x, y)));
                        s.lines += 1;
                        self.point_work += 1
                    }
                }
                "re" => {
                    if let Some(r) = numbers(a) {
                        paths.push(Subpath {
                            ps: rect_path(g.ctm, r),
                            lines: 4,
                            closed: true,
                            curves: 0,
                            curve_bounds: None,
                        });
                        self.point_work += 4
                    }
                }
                "h" => {
                    if let Some(s) = paths.last_mut() {
                        s.closed = true
                    }
                }
                "c" | "v" | "y" => {
                    if let Some(s) = paths.last_mut() {
                        if let Some(&p0) = s.ps.last() {
                            let p = |x, y| g.ctm.point(Point::new(x, y));
                            let tr = match code {
                                "c" => numbers(a)
                                    .map(|[x, y, x2, y2, x3, y3]| (p(x, y), p(x2, y2), p(x3, y3))),
                                "v" => {
                                    numbers(a).map(|[x2, y2, x3, y3]| (p0, p(x2, y2), p(x3, y3)))
                                }
                                _ => numbers(a)
                                    .map(|[x1, y1, x3, y3]| (p(x1, y1), p(x3, y3), p(x3, y3))),
                            };
                            if let Some((p1, p2, p3)) = tr {
                                if let Some(bounds) = Rect::points(&[p0, p1, p2, p3]) {
                                    s.curve_bounds =
                                        Some(s.curve_bounds.map_or(bounds, |b| b.union(bounds)));
                                }
                                for i in 1..=16 {
                                    let t = i as f64 / 16.;
                                    let u = 1. - t;
                                    s.ps.push(
                                        p0 * (u * u * u)
                                            + p1 * (3. * u * u * t)
                                            + p2 * (3. * u * t * t)
                                            + p3 * (t * t * t),
                                    )
                                }
                                s.curves += 1;
                                self.point_work += 16;
                            }
                        }
                    }
                }
                "W" | "W*" => clipping = true,
                "w" => {
                    g.pen_width = numbers(a).map_or(0., |[width]| width.max(0.));
                }
                "J" => {
                    if let Some([cap]) = numbers(a) {
                        g.projecting_cap = cap != 0.;
                    }
                }
                "M" => {
                    if let Some([limit]) = numbers(a) {
                        g.miter_limit = limit.max(1.);
                    }
                }
                "S" | "s" | "B" | "B*" | "b" | "b*" | "f" | "F" | "f*" | "n" => {
                    if matches!(code, "s" | "b" | "b*") {
                        if let Some(s) = paths.last_mut() {
                            s.closed = true
                        }
                    }
                    if visible && code != "n" {
                        self.paint(
                            &paths,
                            &g,
                            !matches!(code, "S" | "s"),
                            matches!(code, "S" | "s" | "B" | "B*" | "b" | "b*"),
                            layer,
                            matches!(code, "B*" | "b*" | "f*"),
                        )?
                    }
                    if clipping {
                        let before = g.clip.or(g.uncertain_clip);
                        if paths.len() != 1 || !g.clip_to_polygon(&paths[0].ps) {
                            self.warnings
                                .add("Complex PDF clip: uncertain geometry omitted until restore");
                            g.clip = None;
                            g.uncertain_clip = before.and_then(|r| {
                                paths
                                    .iter()
                                    .filter_map(Subpath::bounds)
                                    .reduce(Rect::union)?
                                    .intersect(r)
                            });
                        }
                        clipping = false;
                    }
                    paths.clear();
                }
                "d" => {
                    g.undashed_pen = a.len() == 2
                        && a[0].as_array().is_ok_and(|arr| arr.is_empty())
                        && num(&a[1]).is_some();
                    g.dashed = a
                        .first()
                        .and_then(|o| o.as_array().ok())
                        .is_some_and(|arr| !arr.is_empty())
                }
                "g" | "G" | "rg" | "RG" | "k" | "K" | "sc" | "SC" | "scn" | "SCN" => {
                    let stroke = code.as_bytes()[0].is_ascii_uppercase();
                    if !stroke {
                        g.material_cell = None;
                    }
                    let space = match code.to_ascii_lowercase().as_str() {
                        "g" => b"DeviceGray".as_slice(),
                        "rg" => b"DeviceRGB",
                        "k" => b"DeviceCMYK",
                        _ => {
                            if stroke {
                                &g.stroke_space
                            } else {
                                &g.fill_space
                            }
                        }
                    };
                    let ink = match space {
                        b"DeviceGray" | b"G" => numbers(a).is_some_and(|[v]| v < 0.99999),
                        b"DeviceRGB" | b"RGB" => {
                            numbers(a).is_some_and(|[r, g, b]| r.min(g).min(b) < 0.99999)
                        }
                        b"DeviceCMYK" | b"CMYK" => {
                            numbers(a).is_some_and(|[c, m, y, k]| c.max(m).max(y).max(k) > 1e-5)
                        }
                        b"Pattern" if code == "scn" && self.stack.is_empty() && a.len() == 1 => {
                            if let Ok(name) = a[0].as_name() {
                                if !self.material_cells.contains_key(name)
                                    && self.material_cells.len() < 4096
                                {
                                    let remaining = self.cell_budget;
                                    let cell = resource(self.doc, res, b"Pattern", name)
                                        .and_then(|raw| {
                                            material::Cell::read(
                                                self.doc,
                                                raw,
                                                default_space,
                                                &mut self.cell_budget,
                                            )
                                        })
                                        .map(Rc::new);
                                    self.decoded += remaining - self.cell_budget;
                                    if self.decoded > DOC_BUDGET {
                                        return Err("PDF expanded content budget exceeded".into());
                                    }
                                    self.material_cells.insert(name.into(), cell);
                                }
                                g.material_cell = self.material_cells.get(name).cloned().flatten();
                            }
                            if g.material_cell.is_none() {
                                self.warnings
                                    .add("Unsupported PDF tiling pattern: uncertain paint omitted");
                            }
                            false // Material patterns remain separate from solid drawing geometry.
                        }
                        _ => {
                            self.warnings
                                .add("Unsupported PDF color space: uncertain paint omitted");
                            false
                        }
                    };
                    let selected = if matches!(code, "g" | "G" | "rg" | "RG" | "k" | "K") {
                        strokes::Device::native(self.doc, res, space)
                    } else if stroke {
                        g.stroke_device
                    } else {
                        g.fill_device
                    };
                    let black = selected.is_some_and(|s| s.exact_black(a));
                    if stroke {
                        g.stroke_device = selected;
                        g.black_pen = black;
                        g.white_pen = match space {
                            b"DeviceGray" | b"G" => numbers(a) == Some([1.]),
                            b"DeviceRGB" | b"RGB" => numbers(a) == Some([1., 1., 1.]),
                            b"DeviceCMYK" | b"CMYK" => numbers(a) == Some([0., 0., 0., 0.]),
                            _ => false,
                        };
                        g.stroke_space = space.to_vec();
                        g.stroke = ink
                    } else {
                        g.fill_device = selected;
                        g.black_fill = black;
                        // Only valid, bounded components in a device colour space authorize a solid receipt.
                        g.solid_fill_safe = matches!(
                            space,
                            b"DeviceGray" | b"G" | b"DeviceRGB" | b"RGB" | b"DeviceCMYK" | b"CMYK"
                        ) && a
                            .iter()
                            .all(|v| num(v).is_some_and(|c| (0.0..=1.).contains(&c)));
                        g.fill_space = space.to_vec();
                        g.fill = ink
                    }
                }
                "cs" | "CS" => {
                    // PDF device spaces initialize to black. Calibrated, ICC,
                    // spot and pattern colors cannot establish the same ink.
                    let device = if let [name] = a.as_slice() {
                        name.as_name()
                            .ok()
                            .and_then(|n| strokes::Device::native(self.doc, res, n))
                    } else {
                        None
                    };
                    if code == "CS" {
                        g.stroke_device = device;
                        g.black_pen = device.is_some();
                    } else {
                        g.fill_device = device;
                        g.black_fill = device.is_some();
                    }
                    if code == "CS" {
                        g.white_pen = false;
                    }
                    if code == "cs" {
                        g.material_cell = None;
                        g.solid_fill_safe = false;
                    }
                    if let Some(n) = a.first().and_then(|o| o.as_name().ok()) {
                        let alias = resource(self.doc, res, b"ColorSpace", n)
                            .and_then(|o| obj(self.doc, o));
                        let array = alias.and_then(|o| o.as_array().ok());
                        let family = array
                            .and_then(|arr| arr.first())
                            .and_then(|o| o.as_name().ok());
                        let space = match family {
                            Some(b"CalGray") => b"DeviceGray".as_slice(),
                            Some(b"CalRGB") => b"DeviceRGB".as_slice(),
                            // Alternate is the PDF-defined fallback when ICC transform is not applied.
                            Some(b"ICCBased") => {
                                let profile = array
                                    .and_then(|arr| arr.get(1))
                                    .and_then(|o| obj(self.doc, o))
                                    .and_then(|o| o.as_stream().ok());
                                self.warnings.add("PDF ICC color interpreted through its alternate device color space");
                                profile
                                    .and_then(|p| p.dict.get(b"Alternate").ok())
                                    .and_then(|o| o.as_name().ok())
                                    .or_else(|| {
                                        match profile
                                            .and_then(|p| p.dict.get(b"N").ok())
                                            .and_then(num)
                                            .map(|n| n as i32)
                                        {
                                            Some(1) => Some(b"DeviceGray".as_slice()),
                                            Some(3) => Some(b"DeviceRGB".as_slice()),
                                            Some(4) => Some(b"DeviceCMYK".as_slice()),
                                            _ => None,
                                        }
                                    })
                                    .unwrap_or(n)
                            }
                            _ => alias.and_then(|o| o.as_name().ok()).unwrap_or(n),
                        };
                        let known = matches!(
                            space,
                            b"DeviceGray" | b"DeviceRGB" | b"DeviceCMYK" | b"G" | b"RGB" | b"CMYK"
                        );
                        if code == "CS" {
                            g.stroke_space = space.into();
                            g.stroke = known
                        } else {
                            g.fill_space = space.into();
                            g.solid_fill_safe = known;
                            g.fill = known
                        }
                    }
                }
                "gs" => {
                    let ext = a
                        .first()
                        .and_then(|n| n.as_name().ok())
                        .and_then(|n| resource(self.doc, res, b"ExtGState", n))
                        .and_then(|o| dict(self.doc, o));
                    g.material_state_safe &= a.len() == 1
                        && ext.is_some_and(|state| {
                            state.iter().all(|(key, value)| match key.as_slice() {
                                b"Type" => value.as_name().ok() == Some(b"ExtGState"),
                                b"ca" | b"CA" => {
                                    num(value).is_some_and(|v| (0.0..=1.).contains(&v))
                                }
                                b"SMask" => value.as_name().ok() == Some(b"None"),
                                b"BM" => value.as_name().ok() == Some(b"Normal"),
                                _ => false,
                            })
                        });
                    if let Some(ext) = ext {
                        g.pen_state_safe &= a.len() == 1
                            && ext.iter().all(|(key, value)| match key.as_slice() {
                                b"Type" => value.as_name().ok() == Some(b"ExtGState"),
                                b"ca" | b"CA" => {
                                    num(value).is_some_and(|v| (0.0..=1.).contains(&v))
                                }
                                b"SMask" => value.as_name().ok() == Some(b"None"),
                                b"BM" => value.as_name().ok() == Some(b"Normal"),
                                // Overprint mode has no effect while overprinting itself is disabled.
                                b"OPM" => matches!(value.as_i64().ok(), Some(0 | 1)),
                                b"OP" | b"op" => value.as_bool().ok() == Some(false),
                                b"LW" | b"LC" | b"LJ" | b"ML" => {
                                    num(value).is_some_and(|v| v >= 0.)
                                }
                                b"D" => value.as_array().is_ok_and(|v| {
                                    v.len() == 2 && v[0].as_array().is_ok() && num(&v[1]).is_some()
                                }),
                                _ => false,
                            });
                        if let Ok(value) = ext.get(b"LW") {
                            g.pen_width = num(value).unwrap_or(0.);
                        }
                        if let Some(cap) = ext.get(b"LC").ok().and_then(num) {
                            g.projecting_cap = cap != 0.;
                        }
                        if let Some(limit) = ext.get(b"ML").ok().and_then(num) {
                            g.miter_limit = limit.max(1.);
                        }
                        if let Ok(value) = ext.get(b"D") {
                            g.undashed_pen = value.as_array().is_ok_and(|v| {
                                v.first()
                                    .is_some_and(|a| a.as_array().is_ok_and(|a| a.is_empty()))
                            });
                        }
                        if let Some(v) = ext.get(b"CA").ok().and_then(num) {
                            g.stroke_alpha = v
                        }
                        if let Some(v) = ext.get(b"ca").ok().and_then(num) {
                            g.fill_alpha = v
                        }
                        if let Ok(mask) = ext.get(b"SMask") {
                            g.masked = mask.as_name().ok() != Some(b"None");
                            if g.masked {
                                self.warnings.add("Soft-masked PDF content omitted")
                            }
                        }
                    } else {
                        g.pen_state_safe = false;
                    }
                }
                "BT" => {
                    tm = Mat::ID;
                    line = Mat::ID;
                    text_active = true
                }
                "ET" => text_active = false,
                "Tf" => {
                    if a.len() == 2 {
                        if let (Ok(font), Some(size)) = (a[0].as_name(), num(&a[1])) {
                            g.font = font.into();
                            g.size = size
                        }
                    }
                }
                "TL" | "Tr" | "Tw" | "Tc" | "Ts" | "Tz" => {
                    if let Some([v]) = numbers(a) {
                        match code {
                            "TL" => g.lead = v,
                            "Tr" => g.tr = v as i64,
                            "Tw" => g.word_spacing = v,
                            "Tc" => g.spacing = v,
                            "Ts" => g.rise = v,
                            _ => g.hscale = v / 100.,
                        }
                    }
                }
                "Tm" => {
                    if let Some(m) = numbers(a) {
                        line = Mat(m);
                        tm = line
                    }
                }
                "Td" | "TD" => {
                    if let Some([x, y]) = numbers(a) {
                        if code == "TD" {
                            g.lead = -y
                        }
                        line = line.then(Mat::translate(x, y));
                        tm = line
                    }
                }
                "T*" => {
                    line = line.then(Mat::translate(0., -g.lead));
                    tm = line
                }
                "Tj" | "TJ" | "'" | "\"" => {
                    if text_active {
                        if matches!(code, "'" | "\"") {
                            line = line.then(Mat::translate(0., -g.lead));
                            tm = line
                        }
                        if code == "\"" && a.len() == 3 {
                            g.word_spacing = num(&a[0]).unwrap_or(g.word_spacing);
                            g.spacing = num(&a[1]).unwrap_or(g.spacing)
                        }
                        if let Some(o) = a.last() {
                            if let Ok(arr) = o.as_array() {
                                self.text(res, &g, &mut tm, arr, visible, layer)?
                            } else {
                                self.text(
                                    res,
                                    &g,
                                    &mut tm,
                                    std::slice::from_ref(o),
                                    visible,
                                    layer,
                                )?
                            }
                            if (4..=7).contains(&g.tr)
                                && (o.as_str().is_ok_and(|s| !s.is_empty())
                                    || o.as_array().is_ok_and(|a| {
                                        a.iter().any(|v| v.as_str().is_ok_and(|s| !s.is_empty()))
                                    }))
                            {
                                g.finish_clip_unsafe = true;
                            }
                        }
                    }
                }
                "BMC" | "BDC" => {
                    if marks.len() >= 128 {
                        return Err("PDF marked-content nesting limit exceeded".into());
                    }
                    let prop = if a.len() == 2 && a[0].as_name().ok() == Some(b"OC") {
                        a[1].as_name()
                            .ok()
                            .and_then(|n| resource(self.doc, res, b"Properties", n))
                    } else {
                        None
                    };
                    let on = prop
                        .is_none_or(|o| !o.as_reference().is_ok_and(|id| self.off.contains(&id)));
                    let name = prop
                        .and_then(|o| dict(self.doc, o))
                        .and_then(|d| d.get(b"Name").ok())
                        .and_then(|o| lopdf::decode_text_string(o).ok());
                    marks.push((on, name));
                }
                "EMC" => {
                    marks.pop();
                }
                "BI" if visible => self.image(&g, layer)?,
                "Do" if visible => {
                    if let Some(x) = a
                        .first()
                        .and_then(|o| o.as_name().ok())
                        .and_then(|n| resource(self.doc, res, b"XObject", n))
                    {
                        let id = x.as_reference().ok();
                        let Some(s) = obj(self.doc, x).and_then(|o| o.as_stream().ok()) else {
                            continue;
                        };
                        if s.dict
                            .get(b"OC")
                            .ok()
                            .and_then(|o| o.as_reference().ok())
                            .is_some_and(|id| self.off.contains(&id))
                        {
                            continue;
                        }
                        match s.dict.get(b"Subtype").ok().and_then(|o| o.as_name().ok()) {
                            Some(b"Image") => self.image(&g, layer)?,
                            Some(b"Form") => {
                                if self.stack.len() >= 24
                                    || id.is_some_and(|id| self.stack.contains(&id))
                                {
                                    return Err("Cyclic/deep PDF form nesting".into());
                                }
                                let mut child = g.clone();
                                if s.dict.has(b"Group") {
                                    child.material_state_safe = false;
                                    child.pen_state_safe = false;
                                }
                                if let Ok(o) = s.dict.get(b"Matrix") {
                                    let m = obj(self.doc, o)
                                        .and_then(|o| o.as_array().ok())
                                        .and_then(|v| numbers(v))
                                        .ok_or("Invalid PDF form matrix")?;
                                    child.ctm = g.ctm.then(Mat(m));
                                }
                                let b = s
                                    .dict
                                    .get(b"BBox")
                                    .ok()
                                    .and_then(|o| obj(self.doc, o))
                                    .and_then(|o| o.as_array().ok())
                                    .and_then(|v| numbers::<4>(v))
                                    .ok_or("PDF form needs a valid bounding box")?;
                                let ps =
                                    rect_path(child.ctm, [b[0], b[1], b[2] - b[0], b[3] - b[1]]);
                                if !child.clip_to_polygon(&ps) {
                                    self.warnings.add(
                                        "Oblique form bounding clip unsupported; form omitted",
                                    );
                                    continue;
                                }
                                if child.clip.is_none() {
                                    continue;
                                }
                                let bytes = s
                                    .decompressed_content_with_limit(
                                        STREAM_LIMIT.min(DOC_BUDGET.saturating_sub(self.decoded)),
                                    )
                                    .map_err(|e| format!("PDF form decoding failed: {e}"))?;
                                let child_res = s
                                    .dict
                                    .get(b"Resources")
                                    .ok()
                                    .and_then(|o| dict(self.doc, o))
                                    .or(res);
                                self.stack.push(id.unwrap_or((0, 0)));
                                self.run(&bytes, child_res, child, layer)?;
                                self.stack.pop();
                            }
                            _ => {
                                self.cover(g.clip.or(g.uncertain_clip));
                                self.warnings.add("Unsupported PDF XObject omitted")
                            }
                        }
                    }
                }
                "sh" => {
                    if visible && g.fill_alpha > 0. {
                        self.cover(g.clip.or(g.uncertain_clip));
                    }
                    self.warnings.add("PDF shading omitted")
                }
                _ => {}
            }
        }
        Ok(())
    }
}

pub(super) fn read(bytes: &[u8]) -> Result<Document, String> {
    let doc = Pdf::load_mem_with_options(
        bytes,
        LoadOptions {
            max_decompressed_size: Some(STREAM_LIMIT),
            ..Default::default()
        },
    )
    .map_err(|e| format!("Invalid PDF: {e}"))?;
    if doc.is_encrypted() {
        return Err("Encrypted PDF requires a decrypted input".into());
    }
    let pages = doc.get_pages();
    if pages.is_empty() || pages.len() > 2048 {
        return Err("PDF page count outside supported range".into());
    }
    let mut result = vec![];
    let mut warnings = Warnings::default();
    let mut off = HashSet::new();
    if let Some(cfg) = doc
        .catalog()
        .ok()
        .and_then(|d| d.get(b"OCProperties").ok())
        .and_then(|o| dict(&doc, o))
        .and_then(|d| d.get(b"D").ok())
        .and_then(|o| dict(&doc, o))
    {
        if let Some(arr) = cfg
            .get(b"OFF")
            .ok()
            .and_then(|o| obj(&doc, o))
            .and_then(|o| o.as_array().ok())
        {
            for o in arr {
                if let Ok(id) = o.as_reference() {
                    off.insert(id);
                }
            }
        }
        if cfg.get(b"BaseState").ok().and_then(|o| o.as_name().ok()) == Some(b"OFF") {
            warnings.add("PDF optional-content default OFF configuration unsupported");
            return Err("Unsupported PDF optional-content default OFF configuration".into());
        }
    }
    let (mut decoded, mut point_work, mut operations, mut nitems) = (0, 0, 0, 0);
    for (number, id) in pages {
        let box_of = |k| {
            inherited(&doc, id, k)
                .and_then(|o| o.as_array().ok())
                .and_then(|v| numbers::<4>(v))
                .map(|[x, y, r, t]| Rect {
                    l: x.min(r),
                    t: y.min(t),
                    r: x.max(r),
                    b: y.max(t),
                })
        };
        let media = box_of(b"MediaBox").ok_or("Missing PDF MediaBox")?;
        let b = if let Some(c) = box_of(b"CropBox") {
            media.intersect(c).ok_or("Empty page CropBox")?
        } else {
            media
        };
        let unit = doc
            .get_dictionary(id)
            .ok()
            .and_then(|d| d.get(b"UserUnit").ok())
            .and_then(num)
            .unwrap_or(1.);
        if !(0.000001..=75000.).contains(&unit) {
            return Err("Invalid PDF UserUnit".into());
        }
        let (w, h) = ((b.r - b.l) * unit, (b.b - b.t) * unit);
        if !(w > 0. && h > 0. && w.is_finite() && h.is_finite()) {
            return Err("Invalid PDF page bounds".into());
        }
        let r = inherited(&doc, id, b"Rotate")
            .and_then(num)
            .unwrap_or(0.)
            .rem_euclid(360.);
        if r.fract() != 0. {
            return Err("Unsupported PDF page rotation".into());
        }
        let (ctm, width, height) = match r as i64 {
            0 => (Mat([unit, 0., 0., -unit, -b.l * unit, b.b * unit]), w, h),
            90 => (Mat([0., unit, unit, 0., -b.t * unit, -b.l * unit]), h, w),
            180 => (Mat([-unit, 0., 0., unit, b.r * unit, -b.t * unit]), w, h),
            270 => (Mat([0., -unit, -unit, 0., b.b * unit, b.r * unit]), h, w),
            _ => return Err("Unsupported PDF page rotation".into()),
        };
        let res = inherited(&doc, id, b"Resources").and_then(|o| o.as_dict().ok());
        let bytes = doc
            .get_page_content_with_limit(id, STREAM_LIMIT.min(DOC_BUDGET - decoded))
            .map_err(|e| format!("PDF page content decoding failed: {e}"))?;
        let page_bounds = Rect {
            l: 0.,
            t: 0.,
            r: width,
            b: height,
        };
        let mut draw = Draw {
            finish_history: crate::finish_visibility::PaintHistory::default(),
            doc: &doc,
            page: number,
            items: vec![],
            material_tiles: vec![],
            placement_paint: visibility::VisibleFills::new(page_bounds),
            placement_polygon_paint: visibility::VisibleFills::new(page_bounds),
            stroke_widths: strokes::StrokeWidths::new(page_bounds),
            material_cells: HashMap::new(),
            cell_budget: STREAM_LIMIT.min(DOC_BUDGET - decoded),
            material_strokes: 0,
            warnings: &mut warnings,
            operations,
            decoded,
            point_work,
            path_count: 0,
            stack: vec![],
            off: &off,
            fonts: HashMap::new(),
            previous_text: None,
            limit: MAX_ITEMS - nitems,
        };
        draw.run(&bytes, res, Graphics::new(ctm, page_bounds), None)?;
        operations = draw.operations;
        decoded = draw.decoded;
        point_work = draw.point_work;
        nitems += draw.items.len();
        if draw.placement_paint.exhausted() {
            draw.warnings.add("PDF rectangular paint visibility budget exhausted; layered wall placement withheld on page");
        }
        if draw.stroke_widths.footprints.exhausted() {
            draw.warnings
                .add("PDF stroke visibility budget exhausted; source lineweights withheld on page");
        }
        if draw.placement_polygon_paint.exhausted() {
            draw.warnings.add("PDF polygon paint visibility budget exhausted; source-bounded material placement withheld on page");
        }
        draw.stroke_widths.finish(&mut draw.items);
        draw.finish_history.last_order(draw.path_count * 2 + 1);
        result.push(Page {
            finish_history: Some(draw.finish_history),
            pdf_input: None,
            number,
            width,
            height,
            units_to_mm: None,
            primitives: draw.items,
            material_tiles: draw.material_tiles,
            placement_paint_ids: Some(draw.placement_paint.ids()),
            placement_polygon_paint_ids: Some(draw.placement_polygon_paint.ids()),
        });
    }
    Ok(Document {
        pages: result,
        warnings: warnings.finish(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use lopdf::{Stream, dictionary};

    fn bytes(content: &str, rotate: i64, resources: Dictionary, mut d: Pdf) -> Vec<u8> {
        let tree = d.new_object_id();
        let stream = d.add_object(Stream::new(dictionary! {}, content.as_bytes().to_vec()));
        let page = d.add_object(
            dictionary! {"Type"=>"Page","Parent"=>tree,"Contents"=>stream,"Rotate"=>rotate},
        );
        d.objects.insert(
            tree,
            Object::Dictionary(
                dictionary! {"Type"=>"Pages","Kids"=>vec![page.into()],"Count"=>1,
                "MediaBox"=>vec![10.into(),20.into(),110.into(),220.into()],"Resources"=>resources},
            ),
        );
        let catalog = d.add_object(dictionary! {"Type"=>"Catalog","Pages"=>tree});
        d.trailer.set("Root", catalog);
        let mut b = vec![];
        d.save_to(&mut b).unwrap();
        b
    }
    fn simple(content: &str, rotate: i64) -> Document {
        read(&bytes(
            content,
            rotate,
            dictionary! {},
            Pdf::with_version("1.5"),
        ))
        .unwrap()
    }

    #[test]
    fn finish_visibility_accounts_for_potentially_erasing_text_before_font_resolution() {
        use crate::finish_visibility::FinishRequirement;
        for (suffix, expected) in [
            ("BT /Missing 12 Tf 1 g (erase) Tj ET", false),
            ("BT /Missing 12 Tf 1 G 1 Tr (erase) Tj ET", false),
            ("BT /Missing 12 Tf 1 g 3 Tr (invisible) Tj ET", true),
            ("BT /Missing 12 Tf 0 g (unresolved annotation) Tj ET", false),
        ] {
            let mut doc = simple(&format!("0.1 w 20 40 m 20 80 l S {suffix}"), 0);
            let page = &mut doc.pages[0];
            let source = page.primitives[0].id.clone();
            let proof = page.finish_history.take().unwrap().certify(
                1,
                vec![FinishRequirement {
                    rectangle: [
                        Point::new(10., 140.),
                        Point::new(12., 140.),
                        Point::new(12., 180.),
                        Point::new(10., 180.),
                    ],
                    source_ids: vec![source],
                    contour_edges: vec![],
                }],
            );
            assert_eq!(!proof.receipts.is_empty(), expected, "{suffix}");
        }
    }

    #[test]
    fn source_stroke_width_uses_the_transformed_normal_and_survives_round_trip() {
        let input = "q 2 0 0 3 10 20 cm 2 w 10 10 m 30 10 l S 10 10 m 10 30 l S 10 10 m 30 10 l 30 30 l S Q 0 w 40 60 m 80 60 l S";
        let doc = simple(input, 90);
        let page = &doc.pages[0];
        let widths: Vec<_> = page.primitives.iter().map(|p| p.stroke_width).collect();
        assert_eq!(widths, vec![Some(6.), Some(4.), None, None]);
        let shear = simple("2 0 1 1 10 20 cm 2 w 10 10 m 20 20 l S", 90);
        // Source diagonal maps to (3,1). Area scales by det(M) = 2, hence
        // transverse width scales by 2 / (sqrt(10) / sqrt(2)).
        let measured = shear.pages[0].primitives[0].stroke_width.unwrap();
        assert!((measured - 4. / 5_f64.sqrt()).abs() < 1e-10);
        let mut scan =
            crate::pipeline::scan_document(doc, &Default::default(), None, true).unwrap();
        let bytes = serde_json::to_vec(&scan).unwrap();
        scan = serde_json::from_slice(&bytes).unwrap();
        assert_eq!(scan.schema_version, "openplantrace.rust.scan.v16");
        crate::export::validate(&scan).unwrap();
        scan.schema_version = "openplantrace.rust.scan.v7".into();
        assert!(crate::export::validate(&scan).is_err());
        scan.pages[0].exterior_enclosures = None;
        scan.pages[0].exterior_connections = None;
        scan.pages[0].placement_polygon_paint_ids = None;
        scan.pages[0].finish_visibility = None;
        crate::export::validate(&scan).unwrap();
        scan.schema_version = "openplantrace.rust.scan.v6".into();
        assert!(crate::export::validate(&scan).is_err());
        for p in &mut scan.pages[0].primitives {
            p.stroke_width = None;
        }
        crate::export::validate(&scan).unwrap();
        scan.schema_version = "openplantrace.rust.scan.v7".into();
        scan.pages[0].primitives[0].stroke_width = Some(-1.);
        assert!(crate::export::validate(&scan).is_err());
    }

    #[test]
    fn source_stroke_width_requires_the_complete_unobscured_opaque_pen() {
        let content = "3 w 20 40 m 60 40 l 60 80 l S";
        for (name, before, after, width) in [
            ("uncovered", "", "", Some(3.)),
            ("white first", "1 g 44 38 4 4 re f", "", Some(3.)),
            ("one segment covered", "", "1 g 44 38 4 4 re f", None),
            ("inside elbow clear", "", "1 g 40 55 5 7 re f", Some(3.)),
            ("edge contact", "", "1 g 40 41.5 8 4 re f", Some(3.)),
            ("visible sliver erased", "", "1 g 40 41.25 8 4 re f", None),
            (
                "white stroke across",
                "",
                "q 1 G 3 w 40 35 m 40 49 l S Q",
                None,
            ),
            (
                "masked fill",
                "",
                "q 2 2 5 5 re W n 1 g 44 38 4 4 re f Q",
                Some(3.),
            ),
            ("half alpha pen", "/Faint gs", "", None),
            ("custom blending", "/Blend gs", "", None),
            ("inactive overprint mode", "/Mode gs", "", Some(3.)),
            ("active overprint", "/Over gs", "", None),
            ("dash in graphics state", "/Dash gs", "", None),
        ] {
            let res = dictionary! {"ExtGState"=>dictionary!{
                "Faint"=>dictionary!{"CA"=>0.5},
                "Blend"=>dictionary!{"BM"=>"Multiply"},
                "Mode"=>dictionary!{"OPM"=>1}, "Over"=>dictionary!{"OP"=>true},
                "Dash"=>dictionary!{"D"=>vec![Object::Array(vec![3.into(),2.into()]), 0.into()]}
            }};
            let doc = read(&bytes(
                &format!("{before} {content} {after}"),
                0,
                res,
                Pdf::with_version("1.7"),
            ))
            .unwrap();
            let path = doc.pages[0]
                .primitives
                .iter()
                .find(|p| matches!(p.shape, Shape::Polyline { filled: false, .. }))
                .unwrap();
            assert_eq!(path.stroke_width, width, "{name}");
        }
        let res = dictionary! {"ExtGState"=>dictionary!{"Wide"=>dictionary!{"LW"=>5}}};
        let doc = read(&bytes(
            "q /Wide gs 20 40 m 60 40 l S Q 20 50 m 60 50 l S",
            0,
            res,
            Pdf::with_version("1.7"),
        ))
        .unwrap();
        assert_eq!(
            doc.pages[0]
                .primitives
                .iter()
                .map(|p| p.stroke_width)
                .collect::<Vec<_>>(),
            vec![Some(5.), Some(1.)]
        );
    }

    #[test]
    fn only_identical_native_black_under_ordinary_paint_preserves_the_stroke() {
        let glyph = "45 39 m 55 41 l 56 46 l 49 43 l h f*";
        for (name, pen, paint, yes) in [
            ("black glyph on black pen", "0 0 0 RG", "0 0 0 rg", true),
            ("black on default gray", "", "0 g", true),
            (
                "native selected space",
                "/DeviceRGB CS 0 0 0 SC",
                "/DeviceRGB cs 0 0 0 sc",
                true,
            ),
            ("inactive overprint mode", "/Mode gs 0 G", "0 g", true),
            ("white mask", "0 0 0 RG", "1 1 1 rg", false),
            ("colored mask", "0 0 0 RG", "0 0.01 0 rg", false),
            ("colored underneath black", "0.01 0 0 RG", "0 0 0 rg", false),
            ("distinct native spaces", "0 G", "0 0 0 rg", false),
            ("unknown color", "0 G", "/Spot cs 0 sc", false),
            ("overprinting black", "0 G", "/Over gs 0 g", false),
            ("unknown blend", "0 G", "/Blend gs 0 g", false),
            ("calibrated black", "0 0 0 RG", "/Cal cs 0 0 0 sc", false),
        ] {
            let res = dictionary! {
                "ColorSpace"=>dictionary!{"Cal"=>vec![Object::Name(b"CalRGB".to_vec()),Object::Dictionary(dictionary!{})]},
                "ExtGState"=>dictionary!{"Over"=>dictionary!{"op"=>true},"Blend"=>dictionary!{"BM"=>"Multiply"},"Mode"=>dictionary!{"OPM"=>1}}
            };
            let source = format!("{pen} 2 w 20 40 m 80 40 l S {paint} {glyph}");
            let doc = read(&bytes(&source, 0, res, Pdf::with_version("1.7"))).unwrap();
            assert_eq!(
                doc.pages[0].primitives[0].stroke_width,
                yes.then_some(2.),
                "{name}"
            );
        }
        let res = dictionary! {"ColorSpace"=>dictionary!{"DefaultRGB"=>"DeviceRGB"}};
        let doc = read(&bytes(
            &format!("0 0 0 RG 1 w 20 40 m 80 40 l S 0 0 0 rg {glyph}"),
            0,
            res,
            Pdf::with_version("1.7"),
        ))
        .unwrap();
        assert_eq!(doc.pages[0].primitives[0].stroke_width, None);
        let mut pdf = Pdf::with_version("1.7");
        let profile = pdf.add_object(Stream::new(
            dictionary! {"N"=>3,"Alternate"=>"DeviceRGB"},
            vec![],
        ));
        let res = dictionary! {"ColorSpace"=>dictionary!{"Profile"=>vec![Object::Name(b"ICCBased".to_vec()),profile.into()]}};
        let doc = read(&bytes(
            &format!("0 0 0 RG 2 w 20 40 m 80 40 l S /Profile cs 0 0 0 sc {glyph}"),
            0,
            res,
            pdf,
        ))
        .unwrap();
        assert_eq!(doc.pages[0].primitives[0].stroke_width, None);
    }

    #[test]
    fn painted_assembly_visibility_follows_order_compound_holes_and_clips() {
        let faces = "20 50 m 100 50 l S 100 58 m 20 58 l S";
        let bands = "0.2 g 20 50 80 1 re f 0.5 g 20 51 80 6 re f 0.2 g 20 57 80 1 re f";
        for (name, before, after, count) in [
            ("original", "", "", 3),
            ("white overpaint", "", "1 g 55 48 5 12 re f", 0),
            (
                "white erasing stroke",
                "",
                "q 1 G 4 w 53 45 m 53 64 l S Q",
                0,
            ),
            (
                "white pen boundary contact",
                "",
                "q 1 G 2 w 27 59 m 82 59 l S Q",
                3,
            ),
            (
                "white square cap",
                "",
                "q 1 G 4 w 2 J 48 60 m 48 59 l S Q",
                2,
            ),
            (
                "colored face stroke",
                "",
                "q 0.2 G 1 w 20 58 m 100 58 l S Q",
                3,
            ),
            ("white beneath", "1 g 55 48 5 12 re f", "", 3),
            ("touching edge", "", "1 g 55 58 5 4 re f", 3),
            (
                "small opaque colored cover",
                "",
                "0.7 0.1 0.2 rg 40 57.25 3 0.25 re f",
                2,
            ),
            (
                "translucent cover",
                "",
                "q /Faint gs 1 g 55 48 5 12 re f Q",
                3,
            ),
            (
                "clip avoids wall",
                "",
                "q 55 59 5 8 re W n 1 g 55 48 5 25 re f Q",
                3,
            ),
            (
                "clip covers lower finish",
                "",
                "q 55 50 5 0.8 re W n 1 g 55 48 5 25 re f Q",
                2,
            ),
            (
                "compound hole",
                "",
                "1 g 12 25 94 95 re 17 47 86 14 re f*",
                3,
            ),
            (
                "triangle covers wall",
                "",
                "1 g 48 46 m 53 46 l 51 64 l h f",
                0,
            ),
            ("shaded clip", "", "q 55 48 5 12 re W n /Shade sh Q", 0),
            ("transparent fill as source", "/Faint gs", "", 0),
        ] {
            let mut pdf = Pdf::with_version("1.6");
            let wall = pdf
                .add_object(dictionary! {"Type"=>"OCG","Name"=>Object::string_literal("A-WALL")});
            let res = dictionary! {"Properties"=>dictionary!{"Wall"=>wall}, "ExtGState"=>dictionary!{"Faint"=>dictionary!{"ca"=>0.5}}};
            let path = format!("{before} /OC /Wall BDC {faces} {bands} EMC {after}");
            let doc = read(&bytes(&path, 0, res, pdf)).unwrap();
            let page = &doc.pages[0];
            let positive = page.placement_paint_ids.as_ref().unwrap();
            let wall_fills: Vec<_> = page
                .primitives
                .iter()
                .filter(|p| {
                    p.layer.as_deref() == Some("A-WALL")
                        && matches!(p.shape, Shape::Polyline { filled: true, .. })
                })
                .collect();
            assert_eq!(wall_fills.len(), 3, "{name}: original geometry");
            assert_eq!(
                wall_fills
                    .iter()
                    .filter(|p| positive.contains(&p.id))
                    .count(),
                count,
                "{name}: visible complete bands"
            );
            let scan =
                crate::pipeline::scan_document(doc, &Default::default(), None, true).unwrap();
            crate::export::validate(&scan).unwrap();
            let placed = scan.pages[0]
                .placement_decisions
                .as_ref()
                .unwrap()
                .iter()
                .filter(|d| {
                    matches!(
                        d.reason,
                        crate::placement::WallReason::PaintedWallLayersBetweenFaces
                            | crate::placement::WallReason::PaintedCoreBetweenWallFaces
                    ) && d.status == crate::placement::WallStatus::Ready
                })
                .count();
            assert_eq!(
                placed,
                usize::from(count == 3),
                "{name}: ready painted assembly"
            );
        }
    }
    #[test]
    fn unresolved_graphics_state_and_malformed_cell_filter_cannot_certify_paint() {
        let receipts = |graphics: &str, invalid_filter: bool| {
            let mut pdf = Pdf::with_version("1.6");
            let mut header = dictionary! {
                "PatternType"=>1, "PaintType"=>1, "TilingType"=>1,
                "BBox"=>vec![0.into(),0.into(),100.into(),100.into()],
                "XStep"=>100, "YStep"=>100, "Resources"=>dictionary!{}
            };
            if invalid_filter {
                header.set("Filter", 1);
            }
            let pattern = pdf.add_object(Stream::new(header,
                b"0 G 1 w 0 100 m 18 80 l 9 20 l 25 0 l 41 20 l 32 80 l 50 100 l 68 80 l 59 20 l 75 0 l 91 20 l 82 80 l 100 100 l S".to_vec()));
            let resources = dictionary! {
                "Pattern"=>dictionary!{"Bat"=>pattern},
                "ExtGState"=>dictionary!{"Wrong"=>dictionary!{"ca"=>"Opaque"}}
            };
            let content = format!("{graphics} /Pattern cs /Bat scn 20 40 60 8 re f");
            read(&bytes(&content, 0, resources, pdf)).unwrap().pages[0]
                .material_tiles
                .len()
        };
        assert_eq!(receipts("", false), 1);
        assert_eq!(receipts("/Missing gs", false), 0);
        assert_eq!(receipts("/Wrong gs", false), 0);
        assert_eq!(receipts("q /Wrong gs Q", false), 1);
        assert_eq!(receipts("", true), 0);
    }
    #[test]
    fn only_visible_paint_and_finite_clipped_line_are_ingested() {
        let d = simple(
            "11 21 m 90 21 l n 1 G 20 30 m 40 30 l S q 0 G 20 40 20 30 re W n 10 45 m 70 45 l S Q 0.5 g 20 80 2 12 re f",
            0,
        );
        let ps = &d.pages[0].primitives;
        assert_eq!(ps.len(), 2);
        let Shape::Line { start, end } = ps[0].shape else {
            panic!()
        };
        assert_eq!(start, Point::new(10., 175.));
        assert_eq!(end, Point::new(30., 175.));
        assert!(matches!(
            &ps[1].shape,
            Shape::Polyline {
                filled: true,
                closed: true,
                ..
            }
        ));
        assert_eq!(d.pages[0].units_to_mm, None);
    }
    #[test]
    fn inherited_media_box_and_quarter_turn_rotate_both_geometry_and_size() {
        let d = simple("10 20 m 110 220 l S", 90);
        let p = &d.pages[0];
        assert_eq!((p.width, p.height), (200., 100.));
        let Shape::Line { start, end } = p.primitives[0].shape else {
            panic!()
        };
        assert_eq!(start, Point::new(0., 0.));
        assert_eq!(end, Point::new(200., 100.));
    }
    #[test]
    fn repeated_form_instances_get_local_matrix_clip_and_distinct_provenance() {
        let mut d = Pdf::with_version("1.5");
        let object=d.add_object(Stream::new(dictionary!{"Type"=>"XObject","Subtype"=>"Form","BBox"=>vec![0.into(),0.into(),10.into(),10.into()],
            "Matrix"=>vec![1.into(),0.into(),0.into(),1.into(),20.into(),40.into()]},b"0 0 m 20 0 l S".to_vec()));
        let d = read(&bytes(
            "/Fm Do q 1 0 0 1 30 0 cm /Fm Do Q",
            0,
            dictionary! {"XObject"=>dictionary!{"Fm"=>object}},
            d,
        ))
        .unwrap();
        let p = &d.pages[0].primitives;
        assert_eq!(p.len(), 2);
        assert_ne!(p[0].id, p[1].id);
        let Shape::Line { start: a, end: b } = p[0].shape else {
            panic!()
        };
        let Shape::Line { start: c, end: d } = p[1].shape else {
            panic!()
        };
        assert_eq!((a, b), (Point::new(10., 180.), Point::new(20., 180.)));
        assert_eq!((c, d), (Point::new(40., 180.), Point::new(50., 180.)));
    }
    #[test]
    fn circular_cubic_is_signed_arc_and_affine_ellipse_is_not() {
        let curve = "60 50 m 60 55.52285 55.52285 60 50 60 c S";
        let d = simple(curve, 0);
        let Shape::Arc {
            center,
            radius,
            sweep,
            ..
        } = d.pages[0].primitives[0].shape
        else {
            panic!()
        };
        assert!(center.distance(Point::new(40., 170.)) < 0.04);
        assert!((radius - 10.).abs() < 0.04);
        assert!((sweep + PI / 2.).abs() < 0.005);
        let e = simple(&format!("q 1.5 0 0 1 0 0 cm {curve} Q"), 0);
        assert!(matches!(
            e.pages[0].primitives[0].shape,
            Shape::Polyline { filled: false, .. }
        ));
    }
    #[test]
    fn holes_and_zero_alpha_are_not_positive_solid_observations() {
        let r = dictionary! {"ExtGState"=>dictionary!{"Invisible"=>dictionary!{"ca"=>0.0}}};
        let d = read(&bytes(
            "20 30 30 30 re 25 35 10 10 re f* q /Invisible gs 60 70 4 20 re f Q",
            0,
            r,
            Pdf::with_version("1.5"),
        ))
        .unwrap();
        assert!(d.pages[0].primitives.is_empty());
        assert!(d.warnings.iter().any(|x| x.contains("compound")));
    }
    #[test]
    fn clips_strokes_to_a_convex_triangle_and_restores_the_outer_state() {
        let d = simple(
            "q 20 30 m 40 30 l 20 50 l h W n 10 40 m 60 40 l S Q 10 40 m 60 40 l S",
            0,
        );
        let p = &d.pages[0].primitives;
        assert_eq!(p.len(), 2);
        let Shape::Line { start, end } = p[0].shape else {
            panic!()
        };
        assert!(start.distance(Point::new(10., 180.)) < 1e-7);
        assert!(end.distance(Point::new(20., 180.)) < 1e-7);
        let Shape::Line { start, end } = p[1].shape else {
            panic!()
        };
        assert!(start.distance(end) > 49.);
    }
    #[test]
    fn positioned_glyphs_form_one_run_with_a_raised_exponent_but_not_an_adjacent_line() {
        let r = dictionary! {"Font"=>dictionary!{"F"=>dictionary!{"Type"=>"Font","Subtype"=>"Type1","BaseFont"=>"Helvetica","Encoding"=>"WinAnsiEncoding"}}};
        let d=read(&bytes("BT /F 10 Tf 1 0 0 1 20 60 Tm (3) Tj 1 0 0 1 27 60 Tm (m) Tj /F 6 Tf 1 0 0 1 33 64 Tm (2) Tj /F 10 Tf 1 0 0 1 20 40 Tm (Next) Tj ET",0,r,Pdf::with_version("1.5"))).unwrap();
        let p = &d.pages[0].primitives;
        assert_eq!(p.len(), 2);
        let Shape::Text { value, .. } = &p[0].shape else {
            panic!()
        };
        assert_eq!(value, "3 m²");
        assert_eq!(p[0].id.matches(',').count(), 2);
    }
}
