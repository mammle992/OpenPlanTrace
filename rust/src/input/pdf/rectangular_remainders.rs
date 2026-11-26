//! Retain an independently proved part of a rectangle when a wall junction
//! overpaints a limited terminal portion. Whole-fill receipts are independent.
use super::{Graphics, Point, Rect, Subpath, visibility::NativeInk};
use crate::model::{PageScan, PaintRemainder, Primitive, Shape};
use std::collections::HashMap;

const GRID: usize = 32;
const MAX_TRACKED: usize = 8192;
const MAX_WORK: usize = 4_000_000;
const END_FRACTION: f64 = 0.125;

#[derive(Clone, Copy)]
struct Stock {
    origin: Point,
    axis: Point,
    length: f64,
    cross: [f64; 2],
    epsilon: f64,
}

impl Stock {
    fn new(outline: [Point; 4]) -> Option<Self> {
        let origin = outline[0];
        let a = outline[1] - origin;
        let b = outline[3] - origin;
        let (long, short) = if a.norm() >= b.norm() { (a, b) } else { (b, a) };
        let length = long.norm();
        let width = short.norm();
        let size = outline.iter().map(|p| p.norm()).fold(1., f64::max);
        let epsilon = 128. * f64::EPSILON * size;
        if !outline.iter().all(|p| p.finite())
            || width <= epsilon
            || length < 8. * width
            || length <= epsilon
        {
            return None;
        }
        let axis = long.unit();
        let depth = axis.cross(short);
        if axis.dot(short).abs() > epsilon || outline[2].distance(origin + long + short) > epsilon {
            return None;
        }
        Some(Self {
            origin,
            axis,
            length,
            cross: [0.0_f64.min(depth), 0.0_f64.max(depth)],
            epsilon,
        })
    }

    fn outline(self, [start, end]: [f64; 2]) -> [Point; 4] {
        let normal = Point::new(-self.axis.y, self.axis.x);
        let at = |x, y| self.origin + self.axis * x + normal * y;
        [
            at(start, self.cross[0]),
            at(end, self.cross[0]),
            at(end, self.cross[1]),
            at(start, self.cross[1]),
        ]
    }

    fn overlap(self, polygon: &[Point], [lo, hi]: [f64; 2]) -> Option<[f64; 2]> {
        let [left, right] = self.cross;
        let mut local = polygon
            .iter()
            .map(|&p| {
                let d = p - self.origin;
                Point::new(d.dot(self.axis), self.axis.cross(d))
            })
            .collect::<Vec<_>>();
        for (normal, plane) in [
            (Point::new(1., 0.), lo),
            (Point::new(-1., 0.), -hi),
            (Point::new(0., 1.), left),
            (Point::new(0., -1.), -right),
        ] {
            if local.is_empty() {
                return None;
            }
            let side = |p: Point| p.dot(normal) - plane;
            let mut next = Vec::with_capacity(local.len() + 1);
            for (&a, &b) in local.iter().zip(local.iter().cycle().skip(1)) {
                let (u, v) = (side(a), side(b));
                if u >= 0. {
                    next.push(a);
                }
                if (u < 0. && v > 0.) || (v < 0. && u > 0.) {
                    next.push(a + (b - a) * (u / (u - v)));
                }
            }
            local = next;
        }
        let &origin = local.first()?;
        let area = local
            .iter()
            .zip(local.iter().cycle().skip(1))
            .map(|(&a, &b)| (a - origin).cross(b - origin))
            .sum::<f64>()
            .abs();
        if area <= self.epsilon * self.epsilon {
            return None;
        }
        let along = local
            .iter()
            .map(|p| p.x)
            .fold([hi, lo], |[a, b], x| [a.min(x), b.max(x)]);
        (along[1] > along[0]).then_some(along)
    }
}

struct Witnesses {
    start: bool,
    anchor: Option<(usize, String)>,
    limit: (usize, String),
}

struct Seed {
    source_id: String,
    order: usize,
    ink: NativeInk,
    stock: Stock,
    bounds: Rect,
    remaining: [f64; 2],
    witnesses: Option<Witnesses>,
    active: bool,
    visited: usize,
}

impl Seed {
    fn cover(&mut self, polygon: &[Point], source: Option<(&str, usize, NativeInk)>) {
        let Some([lo, hi]) = self.stock.overlap(polygon, [0., self.stock.length]) else {
            return;
        };
        let touched = lo < self.remaining[1] && hi > self.remaining[0];
        let in_zone = self.stock.length * END_FRACTION;
        let start = hi <= in_zone;
        let end = lo >= self.stock.length - in_zone;
        let trusted = source.filter(|&(_, order, ink)| order > self.order && ink == self.ink);
        let Some((id, order, _)) = trusted.filter(|_| start || end) else {
            self.active &= !touched;
            return;
        };
        if self
            .witnesses
            .as_ref()
            .is_some_and(|cut| cut.start != start)
        {
            self.active &= !touched;
            return;
        }
        let anchor = if start {
            lo <= self.stock.epsilon
        } else {
            hi >= self.stock.length - self.stock.epsilon
        };
        if !touched {
            if anchor && let Some(cut) = &mut self.witnesses {
                cut.anchor.get_or_insert_with(|| (order, id.to_owned()));
            }
            return;
        }
        let cut = self.witnesses.get_or_insert_with(|| Witnesses {
            start,
            anchor: None,
            limit: (order, id.to_owned()),
        });
        if anchor {
            cut.anchor.get_or_insert_with(|| (order, id.to_owned()));
        }
        cut.limit = (order, id.to_owned());
        if start {
            self.remaining[0] = hi;
        } else {
            self.remaining[1] = lo;
        }
    }

    fn receipt(self) -> Option<PaintRemainder> {
        let cut = self.witnesses?;
        let anchor = cut.anchor?;
        if !self.active {
            return None;
        }
        let outline = self.stock.outline(self.remaining);
        let mut sources = vec![anchor, cut.limit];
        sources.sort_unstable();
        sources.dedup();
        Some(PaintRemainder {
            source_id: self.source_id,
            outline,
            occluder_source_ids: sources.into_iter().map(|(_, id)| id).collect(),
        })
    }
}

fn original_order(id: &str) -> Option<usize> {
    let (page, token) = id.split_once(":path:")?;
    page.strip_prefix("pdf:p")?.parse::<u32>().ok()?;
    let path = token.strip_suffix(":subpath:1")?;
    path.parse().ok()
}

fn interior(layer: &str) -> bool {
    crate::layers::is_named_wall(layer)
        && !crate::layers::is_named_exterior_wall(layer)
        && layer
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

fn convex_paint(path: &Subpath) -> Option<&[Point]> {
    if path.curves != 0 {
        return None;
    }
    convex_ring(&path.ps)
}

pub(super) fn convex_ring(mut points: &[Point]) -> Option<&[Point]> {
    if points.first() == points.last() {
        points = points.get(..points.len().checked_sub(1)?)?;
    }
    if !(3..=4).contains(&points.len()) || !points.iter().all(|p| p.finite()) {
        return None;
    }
    let turn = |i: usize| {
        let a = points[i];
        let b = points[(i + 1) % points.len()];
        let c = points[(i + 2) % points.len()];
        (b - a).cross(c - b)
    };
    let sign = turn(0).signum();
    (sign != 0. && (0..points.len()).all(|i| turn(i) * sign > 0.)).then_some(points)
}

fn replay(receipt: &PaintRemainder, prefix: &str, by_id: &HashMap<&str, &Primitive>) -> Option<()> {
    let source = by_id.get(receipt.source_id.as_str())?;
    let order = original_order(&receipt.source_id)?;
    let stock = crate::placement::painted_rectangle(&source.shape).and_then(Stock::new)?;
    if !source.id.starts_with(prefix)
        || !source.layer.as_deref().is_some_and(interior)
        || !(1..=2).contains(&receipt.occluder_source_ids.len())
    {
        return None;
    }
    let mut seed = Seed {
        source_id: receipt.source_id.clone(),
        order,
        ink: NativeInk::Gray([0]), // Stored witnesses have their ink authenticated during import.
        stock,
        bounds: Rect::points(&receipt.outline)?,
        remaining: [0., stock.length],
        witnesses: None,
        active: true,
        visited: 0,
    };
    let mut previous = order;
    for id in &receipt.occluder_source_ids {
        let cutter = by_id.get(id.as_str())?;
        let later = original_order(id)?;
        let Shape::Polyline {
            points,
            filled: true,
            closed: true,
        } = &cutter.shape
        else {
            return None;
        };
        if !id.starts_with(prefix)
            || later <= previous
            || !cutter
                .layer
                .as_deref()
                .is_some_and(crate::layers::is_named_wall)
        {
            return None;
        }
        seed.cover(
            convex_ring(points)?,
            Some((id, later, NativeInk::Gray([0]))),
        );
        previous = later;
    }
    let measured = seed.receipt()?;
    (measured.occluder_source_ids == receipt.occluder_source_ids
        && measured.outline.iter().all(|p| {
            receipt
                .outline
                .iter()
                .any(|q| p.distance(*q) <= stock.epsilon)
        }))
    .then_some(())
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    let receipts = &page.placement_paint_remainders;
    if receipts.is_empty() {
        return Ok(());
    }
    if receipts.len() > MAX_TRACKED
        || receipts
            .windows(2)
            .any(|r| r[0].source_id >= r[1].source_id)
    {
        return Err(format!(
            "invalid terminal paint remainder inventory on page {}",
            page.page_number
        ));
    }
    let prefix = format!("pdf:p{}:path:", page.page_number);
    let by_id: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    for r in receipts {
        replay(r, &prefix, &by_id)
            .ok_or_else(|| format!("invalid terminal paint remainder `{}`", r.source_id))?;
    }
    Ok(())
}

pub(super) struct Tracker {
    page: Rect,
    cells: Vec<Vec<usize>>,
    seeds: Vec<Seed>,
    visit: usize,
    budget: usize,
}

impl Tracker {
    pub(super) fn new(page: Rect) -> Self {
        Self {
            page,
            cells: vec![Vec::new(); GRID * GRID],
            seeds: Vec::new(),
            visit: 0,
            budget: MAX_WORK,
        }
    }

    fn cells(&self, r: Rect) -> Option<[usize; 4]> {
        let r = r.intersect(self.page)?;
        let index = |q: f64, lo, hi| ((GRID as f64 * (q - lo) / (hi - lo)) as usize).min(GRID - 1);
        Some([
            index(r.l, self.page.l, self.page.r),
            index(r.r, self.page.l, self.page.r),
            index(r.t, self.page.t, self.page.b),
            index(r.b, self.page.t, self.page.b),
        ])
    }

    fn charge(&mut self, amount: usize) -> bool {
        self.budget = self.budget.saturating_sub(amount);
        self.budget > 0
    }

    pub(super) fn observe(
        &mut self,
        id: String,
        layer: Option<&str>,
        outline: [Point; 4],
        g: &Graphics,
        stroke: bool,
    ) {
        if self.budget == 0
            || stroke
            || !layer.is_some_and(interior)
            || g.finish_clip_unsafe
            || !g.contains_footprint(&outline)
        {
            return;
        }
        let (Some(order), Some(ink), Some(stock), Some(bounds)) = (
            original_order(&id),
            g.opaque_native_ink(),
            Stock::new(outline),
            Rect::points(&outline),
        ) else {
            return;
        };
        let Some([x0, x1, y0, y1]) = self.cells(bounds) else {
            return;
        };
        if self.seeds.len() >= MAX_TRACKED {
            self.budget = 0;
            return;
        }
        if !self.charge((x1 - x0 + 1) * (y1 - y0 + 1)) {
            return;
        }
        let index = self.seeds.len();
        self.seeds.push(Seed {
            source_id: id,
            order,
            ink,
            stock,
            bounds,
            remaining: [0., stock.length],
            witnesses: None,
            active: true,
            visited: 0,
        });
        for y in y0..=y1 {
            for x in x0..=x1 {
                self.cells[y * GRID + x].push(index);
            }
        }
    }

    fn visit(&mut self, bounds: Rect, cost: usize, mut apply: impl FnMut(&mut Seed)) {
        let Some([x0, x1, y0, y1]) = self.cells(bounds) else {
            return;
        };
        self.visit += 1;
        for y in y0..=y1 {
            for x in x0..=x1 {
                let indices = &self.cells[y * GRID + x];
                self.budget = self.budget.saturating_sub(indices.len() + 1);
                if self.budget == 0 {
                    return;
                }
                for &i in indices {
                    let seed = &mut self.seeds[i];
                    if !seed.active
                        || seed.visited == self.visit
                        || seed.bounds.intersect(bounds).is_none()
                    {
                        continue;
                    }
                    seed.visited = self.visit;
                    self.budget = self.budget.saturating_sub(cost);
                    if self.budget == 0 {
                        return;
                    }
                    apply(seed);
                }
            }
        }
    }

    pub(super) fn unknown(&mut self, bounds: Option<Rect>) {
        if self.budget == 0 || self.seeds.is_empty() {
            return;
        }
        if let Some(r) = bounds {
            let polygon = r.corners();
            self.visit(r, 16, |s| s.cover(&polygon, None));
        }
    }

    pub(super) fn paint(
        &mut self,
        id: &str,
        layer: Option<&str>,
        paths: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
    ) {
        if self.budget == 0 || self.seeds.is_empty() {
            return;
        }
        let clip = g.clip.or(g.uncertain_clip).unwrap_or(self.page);
        let bounds = paths.iter().filter_map(Subpath::bounds).reduce(Rect::union);
        if fill && (g.fill_alpha != 0. || !g.material_state_safe || g.masked) {
            let exact = match paths {
                [path] if !g.finish_clip_unsafe && !g.masked && g.contains_subpath(path, true) => {
                    convex_paint(path)
                }
                _ => None,
            };
            if let Some(polygon) = exact {
                let source = original_order(id)
                    .zip(g.opaque_native_ink())
                    .filter(|_| !stroke && layer.is_some_and(crate::layers::is_named_wall))
                    .map(|(order, ink)| (id, order, ink));
                if let Some(r) = bounds.and_then(|r| r.intersect(clip)) {
                    self.visit(r, 16, |s| s.cover(polygon, source));
                }
            } else {
                self.unknown(bounds.and_then(|r| r.intersect(clip)));
            }
        }
        if stroke
            && (g.stroke_alpha != 0. || !g.pen_state_safe || g.masked)
            && (g.white_pen || !g.stroke || g.masked || !g.pen_state_safe || !g.material_state_safe)
        {
            let [a, b, c, d, _, _] = g.ctm.0;
            let radius = g.pen_width * 0.5 * g.miter_limit.max(2_f64.sqrt());
            let (x, y) = (radius * a.hypot(c), radius * b.hypot(d));
            let padded = bounds.map(|r| Rect {
                l: r.l - x,
                r: r.r + x,
                t: r.t - y,
                b: r.b + y,
            });
            self.unknown(if radius <= 0. {
                Some(clip)
            } else {
                padded.and_then(|r| r.intersect(clip))
            });
        }
    }

    pub(super) fn text(&mut self, g: &Graphics, resolved: bool) {
        let mode = g.tr.rem_euclid(4);
        let unsafe_ink = g.masked || !g.pen_state_safe || !g.material_state_safe;
        let fill = matches!(mode, 0 | 2)
            && (g.fill_alpha != 0. || unsafe_ink)
            && (!resolved || !g.fill || unsafe_ink);
        let stroke = matches!(mode, 1 | 2)
            && (g.stroke_alpha != 0. || unsafe_ink)
            && (!resolved || g.white_pen || !g.stroke || unsafe_ink);
        if (4..=7).contains(&g.tr) || fill || stroke {
            self.unknown(Some(g.clip.or(g.uncertain_clip).unwrap_or(self.page)));
        }
    }

    pub(super) fn finish(self, primitives: &[Primitive], page_number: u32) -> Vec<PaintRemainder> {
        if self.budget == 0 {
            return Vec::new();
        }
        let mut receipts: Vec<_> = self.seeds.into_iter().filter_map(Seed::receipt).collect();
        if !receipts.is_empty() {
            let by_id = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
            let prefix = format!("pdf:p{page_number}:path:");
            receipts.retain(|r| replay(r, &prefix, &by_id).is_some());
        }
        receipts.sort_unstable_by(|a, b| a.source_id.cmp(&b.source_id));
        receipts
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn rectangle(l: f64, t: f64, r: f64, b: f64) -> Rect {
        Rect { l, t, r, b }
    }

    fn seed() -> Seed {
        let bounds = rectangle(0., 0., 100., 3.);
        Seed {
            source_id: "pdf:p1:path:1:subpath:1".into(),
            order: 1,
            ink: NativeInk::Gray([0.5_f64.to_bits()]),
            stock: Stock::new(bounds.corners()).unwrap(),
            bounds,
            remaining: [0., 100.],
            witnesses: None,
            active: true,
            visited: 0,
        }
    }

    fn terminal_cover() -> Seed {
        let mut seed = seed();
        let ink = seed.ink;
        let limit_id = "pdf:p1:path:2:subpath:1";
        let anchor_id = "pdf:p1:path:3:subpath:1";
        seed.cover(
            &rectangle(4., -1., 7., 4.).corners(),
            Some((limit_id, 2, ink)),
        );
        let anchor = [
            Point::new(-1., -1.),
            Point::new(4., -1.),
            Point::new(-1., 4.),
        ];
        seed.cover(&anchor, Some((anchor_id, 3, ink)));
        seed
    }

    #[test]
    fn later_opaque_terminal_covers_prove_only_the_full_width_remainder() {
        let receipt = terminal_cover().receipt().unwrap();
        assert_eq!(receipt.outline, rectangle(7., 0., 100., 3.).corners());
        assert_eq!(
            receipt.occluder_source_ids,
            ["pdf:p1:path:2:subpath:1", "pdf:p1:path:3:subpath:1"]
        );

        let mut unsupported = terminal_cover();
        unsupported.cover(&rectangle(55., 1., 58., 4.).corners(), None);
        assert!(unsupported.receipt().is_none());

        let mut conflict = terminal_cover();
        let ink = conflict.ink;
        conflict.cover(
            &rectangle(55., 1., 58., 4.).corners(),
            Some(("pdf:p1:path:4:subpath:1", 4, ink)),
        );
        assert!(conflict.receipt().is_none());
    }
}
