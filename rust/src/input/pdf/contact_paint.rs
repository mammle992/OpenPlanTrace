//! Finite convex source paint for graph contacts. A later resolved fill cuts
//! the cell instead of erasing its entire bounding box.
use super::{Graphics, Point, Rect, Subpath, material, rectangular_remainders::convex_ring};
use crate::model::{ContactPaint, Primitive, Shape};
use std::collections::{HashMap, HashSet};

const SIDE: usize = 32;
const MAX_CELLS: usize = 8192;
const MAX_MASKS: usize = 16;
const WORK: usize = 8_000_000;
const EPS: f64 = 1e-7;

fn intersects(a: &[Point], b: &[Point]) -> bool {
    let axes = |ring: &[Point]| {
        (0..ring.len()).all(|i| {
            let e = (ring[(i + 1) % ring.len()] - ring[i]).unit();
            let n = Point::new(-e.y, e.x);
            let range = |points: &[Point]| {
                points
                    .iter()
                    .map(|p| (*p - ring[i]).dot(n))
                    .fold((f64::INFINITY, f64::NEG_INFINITY), |(lo, hi), t| {
                        (lo.min(t), hi.max(t))
                    })
            };
            let (lo, hi) = range(a);
            let (u, v) = range(b);
            hi.min(v) - lo.max(u) > EPS
        })
    };
    axes(a) && axes(b)
}

struct Cell {
    order: u64,
    hull: Vec<Point>,
    bounds: Rect,
    masks: Vec<u64>,
    active: bool,
    visited: usize,
}

pub(super) struct Tracker {
    page: Rect,
    index: Vec<Vec<usize>>,
    cells: Vec<Cell>,
    work: usize,
    visit: usize,
}

impl Tracker {
    pub(super) fn new(page: Rect) -> Self {
        Self {
            page,
            index: vec![vec![]; SIDE * SIDE],
            cells: vec![],
            work: WORK,
            visit: 0,
        }
    }

    fn grid(&self, bounds: Rect) -> Option<[usize; 4]> {
        let r = self.page.intersect(bounds)?;
        let coordinate = |x: f64, lo: f64, hi: f64| {
            (((x - lo) / (hi - lo) * SIDE as f64) as usize).min(SIDE - 1)
        };
        Some([
            coordinate(r.l, self.page.l, self.page.r),
            coordinate(r.r, self.page.l, self.page.r),
            coordinate(r.t, self.page.t, self.page.b),
            coordinate(r.b, self.page.t, self.page.b),
        ])
    }

    fn seed(&mut self, order: u64, hull: &[Point], bounds: Rect) {
        let Some([x0, x1, y0, y1]) = self.grid(bounds) else {
            return;
        };
        if self.cells.len() == MAX_CELLS {
            self.work = 0;
            return;
        }
        let i = self.cells.len();
        self.cells.push(Cell {
            order,
            hull: hull.to_vec(),
            bounds,
            masks: vec![],
            active: true,
            visited: 0,
        });
        for y in y0..=y1 {
            for x in x0..=x1 {
                self.index[y * SIDE + x].push(i);
            }
        }
    }

    fn cover(&mut self, bounds: Rect, outline: &[Point], order: Option<u64>) {
        if self.work == 0 || self.cells.is_empty() {
            return;
        }
        let Some([x0, x1, y0, y1]) = self.grid(bounds) else {
            return;
        };
        self.visit += 1;
        for y in y0..=y1 {
            for x in x0..=x1 {
                let indices = &self.index[y * SIDE + x];
                self.work = self.work.saturating_sub(indices.len() + 1);
                for &i in indices {
                    if self.work == 0 {
                        return;
                    }
                    let cell = &mut self.cells[i];
                    if !cell.active
                        || cell.visited == self.visit
                        || cell.bounds.intersect(bounds).is_none()
                    {
                        continue;
                    }
                    cell.visited = self.visit;
                    self.work = self.work.saturating_sub(16);
                    if intersects(&cell.hull, outline) {
                        match order {
                            Some(n) if cell.masks.len() < MAX_MASKS => cell.masks.push(n),
                            _ => cell.active = false,
                        }
                    }
                }
            }
        }
    }

    pub(super) fn unknown(&mut self, bounds: Option<Rect>) {
        if let Some(r) = bounds {
            self.cover(r, &r.corners(), None);
        }
    }

    pub(super) fn paint(
        &mut self,
        order: u64,
        paths: &[Subpath],
        g: &Graphics,
        fill: bool,
        stroke: bool,
        layer: Option<&str>,
    ) {
        let uncertain = g.masked || !g.material_state_safe || !g.pen_state_safe;
        let fill = fill && (g.fill_alpha != 0. || uncertain);
        let eraser = stroke
            && (g.stroke_alpha != 0. || uncertain)
            && (g.white_pen || !g.stroke || uncertain);
        if self.work == 0 || !(fill || eraser) {
            return;
        }
        let Some(clip) = g.clip.or(g.uncertain_clip) else {
            return;
        };
        let bounds = paths.iter().filter_map(Subpath::bounds).reduce(Rect::union);
        if fill {
            let exact = match paths {
                [path]
                    if !stroke
                        && path.curves == 0
                        && !g.finish_clip_unsafe
                        && g.opaque_native_ink().is_some()
                        && g.contains_subpath(path, true) =>
                {
                    convex_ring(&path.ps)
                }
                _ => None,
            };
            if let Some(r) = bounds.and_then(|r| r.intersect(clip)) {
                if let Some(hull) = exact {
                    self.cover(r, hull, Some(order));
                    if self.work != 0
                        && layer.is_some_and(crate::layers::is_named_wall)
                        && material::paint_rectangle(paths).is_none()
                    {
                        self.seed(order, hull, r);
                    }
                } else {
                    self.unknown(Some(r));
                }
            }
        }
        if eraser && let Some(r) = bounds {
            let [a, b, c, d, _, _] = g.ctm.0;
            let reach = g.pen_width.abs() * (a.hypot(c) + b.hypot(d)) * g.miter_limit.max(2.);
            let r = if reach.is_finite() {
                let e = reach + 4. * EPS;
                Rect {
                    l: r.l - e,
                    t: r.t - e,
                    r: r.r + e,
                    b: r.b + e,
                }
            } else {
                clip
            };
            self.unknown(r.intersect(clip));
        }
    }

    pub(super) fn text(&mut self, g: &Graphics, known: bool) {
        let mode = g.tr.rem_euclid(4);
        let uncertain = !known || g.masked || !g.material_state_safe || !g.pen_state_safe;
        let fill =
            matches!(mode, 0 | 2) && (g.fill_alpha != 0. || uncertain) && (!g.fill || uncertain);
        let stroke = matches!(mode, 1 | 2)
            && (g.stroke_alpha != 0. || uncertain)
            && (g.white_pen || !g.stroke || uncertain);
        if (4..=7).contains(&g.tr) || fill || stroke {
            self.unknown(g.clip.or(g.uncertain_clip));
        }
    }

    pub(super) fn finish(
        self,
        primitives: &[Primitive],
        page: u32,
        legacy: &[String],
    ) -> Vec<ContactPaint> {
        if self.work == 0 || self.cells.is_empty() {
            return vec![];
        }
        let index = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
        let already_whole: HashSet<_> = legacy.iter().map(String::as_str).collect();
        let path = |n| format!("pdf:p{page}:path:{n}:subpath:1");
        let mut receipts = Vec::new();
        for c in self.cells.into_iter().filter(|c| c.active) {
            let source_id = path(c.order);
            if c.masks.is_empty() && already_whole.contains(source_id.as_str()) {
                continue;
            }
            let receipt = ContactPaint {
                source_id,
                occluder_source_ids: c.masks.into_iter().map(path).collect(),
            };
            if valid(&receipt, &index, page) {
                receipts.push(receipt);
            }
        }
        receipts.sort_unstable_by(|a, b| a.source_id.cmp(&b.source_id));
        receipts
    }
}

fn order(id: &str, page: u32) -> Option<u64> {
    let (p, n) = id.strip_prefix("pdf:p")?.split_once(":path:")?;
    if p.parse::<u32>().ok()? != page {
        return None;
    }
    n.strip_suffix(":subpath:1")?.parse().ok()
}

fn ring(p: &Primitive) -> Option<&[Point]> {
    match &p.shape {
        Shape::Polyline {
            points,
            filled: true,
            closed: true,
        } => convex_ring(points),
        _ => None,
    }
}

fn valid(receipt: &ContactPaint, index: &HashMap<&str, &Primitive>, page: u32) -> bool {
    let Some(source) = index.get(receipt.source_id.as_str()) else {
        return false;
    };
    let (Some(hull), Some(mut after)) = (ring(source), order(&source.id, page)) else {
        return false;
    };
    after > 0
        && source
            .layer
            .as_deref()
            .is_some_and(crate::layers::is_named_wall)
        && receipt.occluder_source_ids.len() <= MAX_MASKS
        && receipt.occluder_source_ids.iter().all(|id| {
            let Some(n) = order(id, page).filter(|n| *n > after) else {
                return false;
            };
            after = n;
            index
                .get(id.as_str())
                .and_then(|p| ring(p))
                .is_some_and(|mask| intersects(hull, mask))
        })
}

pub(crate) fn validate(
    primitives: &[Primitive],
    page: u32,
    receipts: &[ContactPaint],
) -> Result<(), String> {
    if receipts.is_empty() {
        return Ok(());
    }
    if receipts.len() > MAX_CELLS
        || receipts
            .windows(2)
            .any(|r| r[0].source_id >= r[1].source_id)
    {
        return Err("invalid contact paint inventory".into());
    }
    let index = primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    for r in receipts {
        if !valid(r, &index, page) {
            return Err(format!("invalid contact paint source `{}`", r.source_id));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn contact_masks_require_positive_convex_overlap() {
        let point = |(x, y)| Point::new(x, y);
        let stock = [(0., 0.), (5., 0.), (0., 5.)].map(point);
        let separate = [(5., 1.), (6., 6.), (1., 5.)].map(point);
        let shared_side = [(5., 0.), (5., 5.), (0., 5.)].map(point);
        let sliver = [(5., 0.), (5., 5.), (0., 4.99)].map(point);
        assert!(!intersects(&stock, &separate));
        assert!(!intersects(&stock, &shared_side));
        assert!(intersects(&stock, &sliver));
    }
}
