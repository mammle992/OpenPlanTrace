//! Track complete rectangular fills through area paint, white erasing strokes
//! and bounded opaque objects. Drawing annotations and nonwhite face strokes
//! stay independent of fill evidence. Complex occluders may withhold a receipt
//! anywhere in their bounds. This is source evidence, not raster compositing.
use super::{Graphics, Point, Rect, Subpath, material};

const GRID: usize = 32;
const BUDGET: usize = 4_000_000;

struct Fill<T> {
    id: Option<T>,
    bounds: Rect,
    corners: [Point; 4],
    visited: usize,
}

pub(super) struct VisibleFills<T = String> {
    page: Rect,
    cells: Vec<Vec<usize>>,
    fills: Vec<Fill<T>>,
    visits: usize,
    budget: usize,
}

impl<T> VisibleFills<T> {
    pub(super) fn new(page: Rect) -> Self {
        Self {
            page,
            cells: vec![Vec::new(); GRID * GRID],
            fills: vec![],
            visits: 0,
            budget: BUDGET,
        }
    }

    fn cells(&self, r: Rect) -> Option<[usize; 4]> {
        let r = self.page.intersect(r)?;
        let index = |v, lo, hi| (((v - lo) / (hi - lo) * GRID as f64) as usize).min(GRID - 1);
        Some([
            index(r.l, self.page.l, self.page.r),
            index(r.r, self.page.l, self.page.r),
            index(r.t, self.page.t, self.page.b),
            index(r.b, self.page.t, self.page.b),
        ])
    }

    fn charge(&mut self, n: usize) -> bool {
        self.budget = self.budget.saturating_sub(n);
        self.budget > 0
    }

    pub(super) fn add(&mut self, id: T, corners: [Point; 4]) {
        if self.budget == 0 {
            return;
        }
        let Some(bounds) = Rect::points(&corners) else {
            return;
        };
        let Some([x0, x1, y0, y1]) = self.cells(bounds) else {
            return;
        };
        if !self.charge((x1 - x0 + 1) * (y1 - y0 + 1)) {
            return;
        }
        for y in y0..=y1 {
            for x in x0..=x1 {
                self.cells[y * GRID + x].push(self.fills.len());
            }
        }
        self.fills.push(Fill {
            id: Some(id),
            bounds,
            corners,
            visited: 0,
        });
    }

    pub(super) fn paint(&mut self, paths: &[Subpath], clip: Option<Rect>, even_odd: bool) {
        self.paint_where(paths, clip, even_odd, |_| true);
    }

    pub(super) fn paint_where(
        &mut self,
        paths: &[Subpath],
        clip: Option<Rect>,
        even_odd: bool,
        obscures: impl Fn(&T) -> bool,
    ) {
        if self.fills.is_empty() || self.budget == 0 {
            return;
        }
        let Some(clip) = clip else { return };
        let Some(bounds) = paths
            .iter()
            .filter_map(Subpath::bounds)
            .reduce(Rect::union)
            .and_then(|r| r.intersect(clip))
        else {
            return;
        };
        let outline = material::paint_rectangle(paths);
        let n: usize = paths.iter().map(|p| p.ps.len()).sum();
        // Unresolved curved or very complex shapes can obscure a band. Their
        // enclosing bounds are sufficient to withhold a receipt without deriving a wall.
        let simple = outline.is_none() && n <= 512 && paths.iter().all(|p| p.curves == 0);
        self.obscure(bounds, if simple { n.max(1) } else { 1 }, |f| {
            if !f.id.as_ref().is_some_and(&obscures) {
                return false;
            }
            if let Some(rect) = &outline {
                material::overlaps(&f.corners, rect)
            } else if simple {
                polygon_overlaps(&f.corners, paths, even_odd)
            } else {
                true
            }
        });
    }

    pub(super) fn unknown(&mut self, bounds: Option<Rect>) {
        if let Some(r) = bounds {
            self.obscure(r, 1, |_| true);
        }
    }

    pub(super) fn white_stroke(&mut self, paths: &[Subpath], g: &Graphics) {
        let Some(clip) = g.clip.or(g.uncertain_clip) else {
            return;
        };
        if self.fills.is_empty() || self.budget == 0 {
            return;
        }
        let [a, b, c, d, _, _] = g.ctm.0;
        let transform = |v: Point| Point::new(a * v.x + c * v.y, b * v.x + d * v.y);
        // A zero-width PDF pen still draws a device hairline. Count interior
        // crossings without assuming a raster output resolution.
        let radius = (g.pen_width * 0.5).max(0.00001);
        for path in paths {
            if let [p, q] = path.ps.as_slice()
                && path.curves == 0
                && p != q
            {
                let delta = *q - *p;
                // Invert the linear CTM to find the source-space pen normal.
                // Its sign is immaterial except for projecting caps.
                let dir = Point::new(d * delta.x - c * delta.y, a * delta.y - b * delta.x).unit();
                let offset = transform(Point::new(-dir.y, dir.x) * radius);
                let tip = if g.projecting_cap {
                    transform(dir * (radius * (a * d - b * c).signum()))
                } else {
                    Point::default()
                };
                let cover = Subpath {
                    ps: vec![
                        *p - tip + offset,
                        *q + tip + offset,
                        *q + tip - offset,
                        *p - tip - offset,
                    ],
                    closed: true,
                    ..Subpath::default()
                };
                self.paint(&[cover], Some(clip), false);
            } else if let Some(r) = path.bounds() {
                // A curve, joined path or round cap can cover more than the
                // centerline. Include the pen transformed into page space.
                let reach = radius * g.miter_limit.max(2_f64.sqrt());
                let dx = reach * a.hypot(c);
                let dy = reach * b.hypot(d);
                self.unknown(clip.intersect(Rect {
                    l: r.l - dx,
                    r: r.r + dx,
                    t: r.t - dy,
                    b: r.b + dy,
                }));
            }
        }
    }

    fn obscure(&mut self, r: Rect, cost: usize, mut hits: impl FnMut(&Fill<T>) -> bool) {
        if self.budget == 0 || self.fills.is_empty() {
            return;
        }
        let Some([x0, x1, y0, y1]) = self.cells(r) else {
            return;
        };
        self.visits += 1;
        for y in y0..=y1 {
            for x in x0..=x1 {
                let cell = &self.cells[y * GRID + x];
                self.budget = self.budget.saturating_sub(cell.len() + 1);
                if self.budget == 0 {
                    return;
                }
                for &i in cell {
                    let f = &mut self.fills[i];
                    if f.id.is_none() || f.visited == self.visits {
                        continue;
                    }
                    f.visited = self.visits;
                    if f.bounds.intersect(r).is_some() {
                        self.budget = self.budget.saturating_sub(cost);
                        if self.budget == 0 {
                            return;
                        }
                        if hits(f) {
                            f.id = None;
                        }
                    }
                }
            }
        }
    }

    pub(super) fn ids(self) -> Vec<T>
    where
        T: Ord,
    {
        if self.budget == 0 {
            return Vec::new();
        }
        let mut ids: Vec<_> = self.fills.into_iter().filter_map(|f| f.id).collect();
        ids.sort_unstable();
        ids
    }

    pub(super) fn exhausted(&self) -> bool {
        self.budget == 0
    }
}

// Either a fill boundary runs inside the measured band, or the entire band
// sits inside a painted face. Counting signed crossings preserves holes.
fn polygon_overlaps(rect: &[Point; 4], paths: &[Subpath], even_odd: bool) -> bool {
    let u = (rect[1] - rect[0]).unit();
    let v = (rect[3] - rect[0]).unit();
    let local = |p: Point| Point::new((p - rect[0]).dot(u), (p - rect[0]).dot(v));
    let eps = 1e-7;
    let interior = Rect {
        l: eps,
        t: eps,
        r: rect[1].distance(rect[0]) - eps,
        b: rect[3].distance(rect[0]) - eps,
    };
    let center = (rect[0] + rect[2]) * 0.5;
    let mut winding = 0_i32;
    for path in paths {
        for (&a, &b) in path.ps.iter().zip(path.ps.iter().cycle().skip(1)) {
            if interior
                .clip(local(a), local(b))
                .is_some_and(|(p, q)| p.distance(q) > eps)
            {
                return true;
            }
            let side = (b - a).cross(center - a);
            if a.y <= center.y && b.y > center.y && side > 0. {
                winding += 1;
            } else if a.y > center.y && b.y <= center.y && side < 0. {
                winding -= 1;
            }
        }
    }
    if even_odd {
        winding % 2 != 0
    } else {
        winding != 0
    }
}
