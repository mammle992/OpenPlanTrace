//! Track complete rectangular fills through area paint, white erasing strokes
//! and bounded opaque objects. Drawing annotations and nonwhite face strokes
//! stay independent of fill evidence. Complex occluders may withhold a receipt
//! anywhere in their bounds. This is source evidence, not raster compositing.
use super::{Graphics, Object, Point, Rect, Subpath, material, numbers, strokes::Device};

const GRID: usize = 32;
const BUDGET: usize = 4_000_000;

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum NativeInk {
    Gray([u64; 1]),
    Rgb([u64; 3]),
    Cmyk([u64; 4]),
}

impl NativeInk {
    pub(super) fn from_operands(device: Device, operands: &[Object]) -> Option<Self> {
        fn components<const N: usize>(operands: &[Object]) -> Option<[u64; N]> {
            let values = numbers::<N>(operands)?;
            values
                .iter()
                .all(|v| (0.0..=1.0).contains(v))
                .then(|| values.map(f64::to_bits))
        }
        match device {
            Device::Gray => components(operands).map(Self::Gray),
            Device::Rgb => components(operands).map(Self::Rgb),
            Device::Cmyk => components(operands).map(Self::Cmyk),
        }
    }
}

pub(super) type RectangularFills = VisibleFills<(String, Option<NativeInk>)>;

impl RectangularFills {
    pub(super) fn paint(
        &mut self,
        paths: &[Subpath],
        graphics: &Graphics,
        even_odd: bool,
        ink: Option<NativeInk>,
    ) {
        self.paint_clipped_where(
            paths,
            graphics,
            even_odd,
            |(_, previous), corners, cover| {
                // Equal native ink across a transverse junction preserves the
                // earlier painted strip. Collinear redraws still retire it.
                !(ink.is_some()
                    && ink == *previous
                    && cover.is_some_and(|cover| transverse(corners, cover)))
            },
        );
    }
}

fn transverse(first: &[Point; 4], second: &[Point; 4]) -> bool {
    fn long_axis(corners: &[Point; 4]) -> Option<Point> {
        let (a, b) = (corners[1] - corners[0], corners[3] - corners[0]);
        let (long, short) = if a.norm() >= b.norm() { (a, b) } else { (b, a) };
        (long.norm() >= 3.0 * short.norm()).then(|| long.unit())
    }
    long_axis(first)
        .zip(long_axis(second))
        .is_some_and(|(a, b)| a.dot(b).abs() <= 0.15)
}

struct Fill<T> {
    id: Option<T>,
    bounds: Rect,
    corners: [Point; 4],
    exact: Option<ExactFootprint>,
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
        self.add_footprint(id, corners, None);
    }

    pub(super) fn add_footprint(
        &mut self,
        id: T,
        corners: [Point; 4],
        exact: Option<ExactFootprint>,
    ) {
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
            exact,
            visited: 0,
        });
    }

    pub(super) fn paint_clipped(
        &mut self,
        paths: &[Subpath],
        graphics: &Graphics,
        even_odd: bool,
        obscures: impl Fn(&T) -> bool,
    ) {
        self.paint_clipped_where(paths, graphics, even_odd, |id, _, _| obscures(id));
    }

    fn paint_clipped_where(
        &mut self,
        paths: &[Subpath],
        graphics: &Graphics,
        even_odd: bool,
        obscures: impl Fn(&T, &[Point; 4], Option<&[Point; 4]>) -> bool,
    ) {
        self.paint_where(
            paths,
            graphics.clip.or(graphics.uncertain_clip),
            even_odd,
            obscures,
            graphics.concave_edge_count(),
            |points| {
                graphics
                    .concave_clips
                    .iter()
                    .all(|c| c.overlaps_footprint(points))
            },
        );
    }

    fn paint_where(
        &mut self,
        paths: &[Subpath],
        clip: Option<Rect>,
        even_odd: bool,
        obscures: impl Fn(&T, &[Point; 4], Option<&[Point; 4]>) -> bool,
        clip_work: usize,
        may_clip: impl Fn(&[Point; 4]) -> bool,
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
        let cover_triangle = if let [path] = paths {
            triangle(path)
        } else {
            None
        };
        let n: usize = paths.iter().map(|p| p.ps.len()).sum();
        // Unresolved curved or very complex shapes can obscure a band. Their
        // enclosing bounds are sufficient to withhold a receipt without deriving a wall.
        let simple = outline.is_none() && n <= 512 && paths.iter().all(|p| p.curves == 0);
        let cost = if simple { n.max(1) } else { 1 };
        self.obscure(bounds, cost.saturating_add(clip_work), |f| {
            if !f
                .id
                .as_ref()
                .is_some_and(|id| obscures(id, &f.corners, outline.as_ref()))
                || !may_clip(&f.corners)
            {
                return false;
            }
            if let (Some(a), Some(b)) = (&f.exact, &cover_triangle) {
                convex_overlap(a.points(), b)
            } else if let Some(rect) = &outline {
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
                self.paint_clipped(&[cover], g, false, |_| true);
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

fn triangle(path: &Subpath) -> Option<[Point; 3]> {
    if path.curves != 0 {
        return None;
    }
    let points = match path.ps.as_slice() {
        [a, b, c] => [*a, *b, *c],
        [a, b, c, close] if a == close => [*a, *b, *c],
        _ => return None,
    };
    let [a, b, c] = points;
    ((b - a).cross(c - a).abs() > 1e-12 && points.iter().all(|p| p.finite())).then_some(points)
}

pub(super) enum ExactFootprint {
    Triangle([Point; 3]),
    Stock([Point; 4]),
}

impl ExactFootprint {
    fn points(&self) -> &[Point] {
        match self {
            Self::Triangle(points) => points,
            Self::Stock(points) => points,
        }
    }
}

/// Store the actual outline only for stock-sized, already-certified exterior
/// fills. Drafting hairlines retain the original visibility behavior.
pub(super) fn exterior_footprint(path: &Subpath) -> Option<ExactFootprint> {
    if let Some([a, b, c]) = triangle(path) {
        return (a.distance(b).min(b.distance(c)).min(c.distance(a)) >= 2.)
            .then_some(ExactFootprint::Triangle([a, b, c]));
    }
    let ring = match path.ps.as_slice() {
        [a, b, c, d] => [*a, *b, *c, *d],
        [a, b, c, d, end] if a == end => [*a, *b, *c, *d],
        _ => return None,
    };
    let edge = |i| ring[(i + 1) % 4] - ring[i];
    let side = (0..4).max_by(|&a, &b| edge(a).norm().total_cmp(&edge(b).norm()))?;
    let near = edge(side);
    let far = edge((side + 2) % 4);
    let width = near.unit().cross(ring[(side + 3) % 4] - ring[side]).abs();
    (width >= 2.
        && near.norm().min(far.norm()) >= 6. * width
        && near.unit().cross(far.unit()).abs() <= 1e-8)
        .then_some(ExactFootprint::Stock(ring))
}

fn convex_overlap(a: &[Point], b: &[Point]) -> bool {
    // A shared edge has zero interior overlap even if both bounding
    // rectangles intersect. Test both sets of actual edge normals.
    let origin = a[0];
    [a, b]
        .into_iter()
        .flat_map(|ring| {
            ring.iter()
                .zip(ring.iter().cycle().skip(1))
                .map(|(&p, &q)| q - p)
        })
        .all(|edge| {
            let range = |points: &[Point]| {
                points
                    .iter()
                    .map(|&p| edge.cross(p - origin))
                    .fold((f64::INFINITY, f64::NEG_INFINITY), |(lo, hi), value| {
                        (lo.min(value), hi.max(value))
                    })
            };
            let (lo, hi) = range(a);
            let (from, to) = range(b);
            let precision =
                hi.abs().max(to.abs()).max(lo.abs()).max(from.abs()) * 32. * f64::EPSILON;
            hi.min(to) - lo.max(from) > precision
        })
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
