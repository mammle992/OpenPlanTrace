//! Source-native hinged doors whose swing is authored as separate ink dashes.
use crate::{
    model::{
        BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape,
        SwingObservation, Wall,
    },
    placement::{WallDecision, WallReason, WallStatus, effective_wall, wall_layers},
    semantics::{RoutingItem, RoutingKind, Span, wall_route},
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashMap, HashSet},
    f64::consts::FRAC_PI_2,
};

const EVIDENCE: OpeningEvidence = OpeningEvidence::LayerlessDashedSwing;
const REASON: WallReason = WallReason::SourceBoundedDashedSwingStock;
const STOCK_PREFIX: &str = "dashed-swing-stock:";
pub(crate) fn is_stock(wall: &Wall) -> bool {
    wall.id.starts_with(STOCK_PREFIX)
}
const MAX_WORK: usize = 4_000_000;

#[derive(Clone, Copy)]
struct Line<'a> {
    id: &'a str,
    a: Point,
    b: Point,
    pen: f64,
}

impl<'a> Line<'a> {
    fn native(p: &'a Primitive) -> Option<Self> {
        let Shape::Line { start: a, end: b } = p.shape else {
            return None;
        };
        let pen = p.stroke_width?;
        (p.layer.is_none()
            && p.id.starts_with("pdf:")
            && p.id.contains(":path:")
            && !p.id.contains(":clip:")
            && pen > 0.
            && pen.is_finite()
            && a != b)
            .then_some(Self {
                id: &p.id,
                a,
                b,
                pen,
            })
    }

    fn project(self, origin: Point, u: Point) -> Self {
        let project = |p: Point| {
            let d = p - origin;
            Point::new(d.dot(u), u.cross(d))
        };
        Self {
            a: project(self.a),
            b: project(self.b),
            ..self
        }
    }

    fn x(self) -> [f64; 2] {
        order(self.a.x, self.b.x)
    }
    fn y(self) -> [f64; 2] {
        order(self.a.y, self.b.y)
    }
}

fn order(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}
fn near(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}
fn bounds(points: impl IntoIterator<Item = Point>) -> [[f64; 2]; 2] {
    points
        .into_iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], p| {
            [
                [x[0].min(p.x), x[1].max(p.x)],
                [y[0].min(p.y), y[1].max(p.y)],
            ]
        })
}
fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(v, _)| v)
}
fn spend(work: &mut usize, n: usize) -> Option<()> {
    *work = work.checked_sub(n)?;
    Some(())
}
fn rect(a: Point, b: Point, width: f64) -> [Point; 4] {
    let u = (b - a).unit();
    let n = Point::new(-u.y, u.x) * width * 0.5;
    [a + n, b + n, b - n, a - n]
}

#[derive(Clone, Copy)]
struct Stock<'a> {
    far: Point,
    jamb: Point,
    width: f64,
    cap: Line<'a>,
    paint: &'a str,
    faces: [&'a str; 2],
}

impl Stock<'_> {
    fn outline(self) -> [Point; 4] {
        rect(self.far, self.jamb, self.width)
    }
    fn sources(self) -> Vec<String> {
        [self.paint, self.cap.id, self.faces[0], self.faces[1]]
            .into_iter()
            .map(str::to_owned)
            .collect()
    }
}

fn stock_cap<'a>(
    ink: &[Line<'a>],
    origin: Point,
    u: Point,
    at: f64,
    w: f64,
    eps: f64,
) -> Option<Line<'a>> {
    let mut found = ink.iter().copied().filter(|&p| {
        let l = p.project(origin, u);
        let [lo, hi] = l.y();
        near(l.a.x, at, eps)
            && near(l.b.x, at, eps)
            && near((lo + hi) * 0.5, 0., eps)
            && lo <= -w * 0.5 + eps
            && hi >= w * 0.5 - eps
            && hi - lo <= 1.20 * w
            && (0.23..=0.50).contains(&(l.pen / w))
    });
    let c = found.next()?;
    found.next().is_none().then_some(c)
}

fn rectangular_sibling<'a>(
    primary: Stock<'a>,
    p: &'a Primitive,
    ink: &[Line<'a>],
    work: &mut usize,
) -> Option<Option<Stock<'a>>> {
    let u = (primary.jamb - primary.far).unit();
    let width = primary.width;
    let half = 0.5 * width;
    let eps = width * 0.0001;
    let origin = primary.jamb;
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return Some(None);
    };
    if p.layer.is_some() || !(4..=8).contains(&points.len()) {
        return Some(None);
    }
    let outline = points
        .iter()
        .copied()
        .map(|p| {
            let d = p - origin;
            Point::new(d.dot(u), u.cross(d))
        })
        .collect::<Vec<_>>();
    let [[jamb, outer], [lo, hi]] = bounds(outline.iter().copied());
    if jamb <= eps
        || outer - jamb < 6. * width
        || !near(lo, -half, eps)
        || !near(hi, half, eps)
        || !outline
            .iter()
            .zip(outline.iter().cycle().skip(1))
            .all(|(a, b)| {
                near(a.y, b.y, eps) && (near(a.y, lo, eps) || near(a.y, hi, eps))
                    || near(a.x, b.x, eps) && (near(a.x, jamb, eps) || near(a.x, outer, eps))
            })
        || ![jamb, outer].into_iter().all(|x| {
            [lo, hi].into_iter().all(|y| {
                outline
                    .iter()
                    .any(|p| near(p.x, x, eps) && near(p.y, y, eps))
            })
        })
    {
        return Some(None);
    }
    spend(work, 3usize.saturating_mul(ink.len()))?;
    let face = |side| {
        let mut faces = ink.iter().copied().filter_map(|i| {
            let l = i.project(origin, u);
            let [a, b] = l.x();
            (near(l.a.y, side, eps)
                && near(l.b.y, side, eps)
                && near(a, jamb, eps)
                && b <= outer + eps
                && b - jamb >= 6. * width
                && (0.025..=0.22).contains(&(l.pen / width)))
            .then_some((i, b))
        });
        let first = faces.next()?;
        faces.next().is_none().then_some(first)
    };
    let (Some((a, x)), Some((b, y)), Some(cap)) = (
        face(-half),
        face(half),
        stock_cap(ink, origin, u, jamb, width, eps),
    ) else {
        return Some(None);
    };
    if [a.id, b.id, cap.id]
        .into_iter()
        .map(path)
        .collect::<BTreeSet<_>>()
        .len()
        != 3
        || !near(a.pen, b.pen, eps)
    {
        return Some(None);
    }
    Some(Some(Stock {
        far: origin + u * x.min(y),
        jamb: origin + u * jamb,
        width,
        cap,
        paint: &p.id,
        faces: [a.id, b.id],
    }))
}

#[derive(Clone, Copy)]
struct Pose {
    hinge: Point,
    closed: Point,
    open: Point,
    radius: f64,
    eps: f64,
}

impl Pose {
    fn point(self, p: Point) -> Option<f64> {
        let d = p - self.hinge;
        let x = d.dot((self.closed - self.hinge).unit());
        let y = d.dot((self.open - self.hinge).unit());
        if x < -self.eps || y < -self.eps || (d.norm() - self.radius).abs() > 0.0045 * self.radius {
            return None;
        }
        Some(y.max(0.).atan2(x.max(0.)))
    }
}

fn segments<'a>(
    f: Pose,
    ink: &[Line<'a>],
    pen: f64,
    leaf_sources: &[&str],
) -> Option<Vec<&'a str>> {
    let eps = f.eps / f.radius;
    let mut parts = ink
        .iter()
        .copied()
        .filter_map(|p| {
            if !near(p.pen, pen, f.eps) || leaf_sources.contains(&p.id) {
                return None;
            }
            let a = f.point(p.a)?;
            let b = f.point(p.b)?;
            let [lo, hi] = order(a, b);
            (0.005..=0.20).contains(&(hi - lo)).then_some((
                [lo, hi],
                p,
                if a <= b { [p.a, p.b] } else { [p.b, p.a] },
            ))
        })
        .collect::<Vec<_>>();
    parts.sort_unstable_by(|a, b| a.0[0].total_cmp(&b.0[0]).then(a.1.id.cmp(b.1.id)));
    if parts.len() < 6
        || parts.len() > 32
        || parts.first()?.2[0].distance(f.closed) > f.eps
        || parts.last()?.2[1].distance(f.open) > f.eps
    {
        return None;
    }
    let mut gaps = 0;
    let mut covered = 0.;
    for ([a, b], _, _) in &parts {
        covered += b - a;
    }
    for p in parts.windows(2) {
        let gap = p[1].0[0] - p[0].0[1];
        if gap.abs() <= eps && p[0].2[1].distance(p[1].2[0]) <= f.eps {
            continue;
        }
        if !(0.015..=0.20).contains(&gap) {
            return None;
        }
        gaps += 1;
    }
    if gaps < 4
        || !(0.36..=0.80).contains(&(covered / FRAC_PI_2))
        || parts
            .iter()
            .map(|p| path(p.1.id))
            .collect::<BTreeSet<_>>()
            .len()
            != parts.len()
    {
        return None;
    }
    Some(parts.into_iter().map(|p| p.1.id).collect())
}

struct Leaf<'a> {
    pen: f64,
    depth: f64,
    ids: Vec<&'a str>,
}

fn leaf<'a>(f: Pose, ink: &[Line<'a>], work: &mut usize) -> Option<Option<Leaf<'a>>> {
    let u = (f.open - f.hinge).unit();
    let sign = u.cross(f.closed - f.hinge).signum();
    let local: Vec<_> = ink
        .iter()
        .map(|&p| p.project(f.hinge, u))
        .filter(|&p| {
            let [a, b] = p.x();
            a >= -f.eps
                && b <= f.radius + f.eps
                && p.a.y.abs().max(p.b.y.abs()) <= 0.07 * f.radius
                && (0.005..=0.025).contains(&(p.pen / f.radius))
        })
        .collect();
    spend(work, local.len().saturating_pow(2))?;
    let rails = |at, pen| {
        let mut rows = local
            .iter()
            .copied()
            .filter(|&p| {
                near(p.a.y, at, f.eps)
                    && near(p.b.y, at, f.eps)
                    && near(p.pen, pen, f.eps)
                    && p.x()[1] - p.x()[0] > f.eps
            })
            .collect::<Vec<_>>();
        rows.sort_unstable_by(|a, b| a.x()[0].total_cmp(&b.x()[0]).then(a.id.cmp(b.id)));
        if rows.len() > 16 {
            return None;
        }
        let mut right = 0.;
        let mut ids = Vec::new();
        for p in rows {
            let [lo, hi] = p.x();
            if !near(lo, right, f.eps) {
                return None;
            }
            right = hi;
            ids.push(p.id);
        }
        near(right, f.radius, f.eps).then_some(ids)
    };
    let mut out = None;
    let mut seen = Vec::new();
    for &m in &local {
        let depth = m.a.y * sign;
        let [y0, y1] = m.y();
        if !(0.018..=0.058).contains(&(depth / f.radius))
            || !near(y0, y1, f.eps)
            || seen
                .iter()
                .any(|&(a, b)| near(a, m.a.y, f.eps) && near(b, m.pen, f.eps))
        {
            continue;
        }
        seen.push((m.a.y, m.pen));
        let endcap = |x| {
            let mut caps = local.iter().copied().filter(|&p| {
                let [lo, hi] = p.y();
                let [a, b] = order(0., m.a.y);
                near(p.a.x, x, f.eps)
                    && near(p.b.x, x, f.eps)
                    && near(lo, a, f.eps)
                    && near(hi, b, f.eps)
                    && near(p.pen, m.pen, f.eps)
            });
            let p = caps.next()?;
            caps.next().is_none().then_some(p.id)
        };
        let (Some(a), Some(b), Some(c), Some(d)) = (
            rails(0., m.pen),
            rails(m.a.y, m.pen),
            endcap(0.),
            endcap(f.radius),
        ) else {
            continue;
        };
        if out.is_some() {
            return Some(None);
        }
        out = Some(Leaf {
            pen: m.pen,
            depth,
            ids: a.into_iter().chain(b).chain([c, d]).collect(),
        });
    }
    Some(out)
}

#[derive(Clone)]
struct Proposal<'a> {
    stock: [Stock<'a>; 2],
    swing: SwingObservation,
    throat: [Point; 4],
}

fn prove<'a>(
    pair: [Stock<'a>; 2],
    ink: &[Line<'a>],
    work: &mut usize,
) -> Option<Option<Proposal<'a>>> {
    let [a, b] = pair;
    let radius = a.jamb.distance(b.jamb);
    let eps = a.width * 0.0001;
    let u = (b.jamb - a.jamb).unit();
    let n = Point::new(-u.y, u.x);
    if !(5.0..=45.).contains(&(radius / a.width))
        || !near(a.width, b.width, eps)
        || (a.far - a.jamb).dot(u) >= 0.
        || (b.far - b.jamb).dot(u) <= 0.
        || (a.far - a.jamb).cross(u).abs() > eps
        || (b.far - b.jamb).cross(u).abs() > eps
        || a.cap.id == b.cap.id
    {
        return Some(None);
    }
    spend(work, ink.len())?;
    let near_ink = ink
        .iter()
        .copied()
        .filter(|p| {
            [p.a, p.b].into_iter().all(|q| {
                let d = q - a.jamb;
                d.dot(u) >= -a.width
                    && d.dot(u) <= radius + a.width
                    && d.dot(n).abs() <= radius + a.width
            })
        })
        .collect::<Vec<_>>();
    let mut found = None;
    for [first, second] in [[a, b], [b, a]] {
        let axis = (second.jamb - first.jamb).unit();
        let side = Point::new(-axis.y, axis.x);
        for hinge in [first.cap.a, first.cap.b] {
            let closed = hinge + axis * radius;
            if second
                .cap
                .a
                .distance(closed)
                .min(second.cap.b.distance(closed))
                > eps
            {
                continue;
            }
            // The opening swings into the half-plane indicated by its actual cap tip.
            let sign = (hinge - first.jamb).dot(side).signum();
            let f = Pose {
                hinge,
                closed,
                open: hinge + side * sign * radius,
                radius,
                eps,
            };
            let Some(leaf) = leaf(f, &near_ink, work)? else {
                continue;
            };
            let Some(chain) = segments(f, &near_ink, leaf.pen, &leaf.ids) else {
                continue;
            };
            if found.is_some() {
                return Some(None);
            }
            let swing = SwingObservation {
                arc_source_id: chain[0].to_owned(),
                segment_source_ids: chain.into_iter().map(str::to_owned).collect(),
                hinge,
                closed_tip: f.closed,
                open_tip: f.open,
                leaf_tolerance: leaf.depth,
                open_leaf_source_ids: leaf.ids.into_iter().map(str::to_owned).collect(),
                closed_leaf_source_ids: vec![],
            };
            found = Some(Proposal {
                stock: [first, second],
                swing,
                throat: rect(first.jamb, second.jamb, a.width),
            });
        }
    }
    Some(found)
}

fn overlap(area: [Point; 4], points: impl IntoIterator<Item = Point>) -> bool {
    let u = (area[1] - area[0]).unit();
    let origin = area[0];
    let w = (area[3] - origin).dot(Point::new(-u.y, u.x));
    let [lo, hi] = order(0., w);
    let [[a, b], [c, d]] = bounds(points.into_iter().map(|p| {
        let v = p - origin;
        Point::new(v.dot(u), u.cross(v))
    }));
    b.min(area[1].distance(origin)) - a.max(0.) > 1e-7 && d.min(hi) - c.max(lo) > 1e-7
}

fn unoccupied(page: &PageScan, p: &Proposal<'_>) -> bool {
    let areas = [p.stock[0].outline(), p.throat, p.stock[1].outline()];
    let active = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready);
    if active.into_iter().any(|(w, d)| {
        let w = effective_wall(w, Some(d));
        let poly = if let Some(c) = w.curve {
            let reach = c.radius + w.thickness * 0.5;
            let u = Point::new(reach, 0.);
            rect(c.center - u, c.center + u, 2. * reach)
        } else {
            rect(w.start, w.end, w.thickness)
        };
        areas.into_iter().any(|r| overlap(r, poly))
    }) {
        return false;
    }
    let by_wall: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    if wall_layers::layers(page).any(|layer| {
        by_wall.get(layer.owner_id).is_some_and(|w| {
            let f = layer.footprint(w);
            areas
                .into_iter()
                .any(|r| overlap(r, rect(f.start, f.end, f.thickness)))
        })
    }) || wall_layers::contacts(page)
        .iter()
        .any(|c| areas.into_iter().any(|r| overlap(r, c.outline)))
    {
        return false;
    }
    if page
        .objects
        .iter()
        .filter_map(|o| o.footprint.as_ref())
        .any(|f| areas.into_iter().any(|r| overlap(r, f.polygon)))
        || page
            .withheld_walls
            .iter()
            .flatten()
            .filter_map(|w| w.furniture_outline)
            .any(|q| areas.into_iter().any(|r| overlap(r, q)))
    {
        return false;
    }
    !page.opening_evidence().any(|o| {
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|id| by_wall.get(id.as_str()))
            .map(|w| w.thickness)
            .fold(p.stock[0].width, f64::max);
        let poly = rect(o.start, o.end, width);
        areas.into_iter().any(|r| overlap(r, poly))
    })
}

fn proposals<'a>(page: &'a PageScan, work: &mut usize) -> Option<Vec<Proposal<'a>>> {
    if page.local_stock.is_empty() {
        return Some(vec![]);
    }
    let ink = page
        .primitives
        .iter()
        .filter_map(Line::native)
        .collect::<Vec<_>>();
    let by_id = ink.iter().map(|&i| (i.id, i)).collect::<HashMap<_, _>>();
    let prim = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let paint = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<BTreeSet<_>>();
    let mut out = Vec::new();
    for r in &page.local_stock {
        let [a, b, c, d] = r.outline;
        let width = a.distance(d);
        let a = (a + d) * 0.5;
        let b = (b + c) * 0.5;
        let cap = *by_id.get(r.cap_source_id.as_str())?;
        let center = (cap.a + cap.b) * 0.5;
        let eps = width * 0.0001;
        let (jamb, far) = if center.distance(a) <= eps {
            (a, b)
        } else if center.distance(b) <= eps {
            (b, a)
        } else {
            continue;
        };
        let primary = Stock {
            far,
            jamb,
            width,
            cap,
            paint: &r.paint_source_id,
            faces: r.face_source_ids.each_ref().map(String::as_str),
        };
        spend(work, paint.len())?;
        let mut matched = None;
        let mut ambiguous = false;
        for id in paint
            .iter()
            .copied()
            .filter(|&id| id != primary.paint && path(id) == path(primary.paint))
        {
            let Some(p) = prim.get(id) else { continue };
            let Some(other) = rectangular_sibling(primary, p, &ink, work)? else {
                continue;
            };
            let Some(door) = prove([primary, other], &ink, work)? else {
                continue;
            };
            spend(
                work,
                page.walls
                    .len()
                    .saturating_add(page.objects.len())
                    .saturating_add(page.openings.len())
                    .saturating_add(page.window_panes.len()),
            )?;
            if !unoccupied(page, &door) {
                continue;
            }
            if matched.replace(door).is_some() {
                ambiguous = true;
                break;
            }
        }
        if !ambiguous && let Some(door) = matched {
            out.push(door);
        }
    }
    spend(work, out.len().saturating_pow(2))?;
    let mut ambiguous = vec![false; out.len()];
    for (i, a) in out.iter().enumerate() {
        for (j, b) in out.iter().enumerate().skip(i + 1) {
            if a.stock
                .iter()
                .any(|s| b.stock.iter().any(|r| s.paint == r.paint))
                || [a.stock[0].outline(), a.throat, a.stock[1].outline()]
                    .into_iter()
                    .any(|r| {
                        [b.stock[0].outline(), b.throat, b.stock[1].outline()]
                            .into_iter()
                            .any(|q| overlap(r, q))
                    })
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    out = out
        .into_iter()
        .zip(ambiguous)
        .filter_map(|(p, a)| (!a).then_some(p))
        .collect();
    out.sort_unstable_by(|a, b| {
        a.swing
            .hinge
            .x
            .total_cmp(&b.swing.hinge.x)
            .then(a.swing.hinge.y.total_cmp(&b.swing.hinge.y))
    });
    Some(out)
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    struct VerifiedStock {
        far: Point,
        jamb: Point,
        width: f64,
        sources: Vec<String>,
    }

    let mut work = MAX_WORK;
    let Some(proposals) = proposals(page, &mut work) else {
        return Ok(());
    };
    if proposals.is_empty() {
        return Ok(());
    }
    let proposals = proposals
        .into_iter()
        .map(|Proposal { stock, swing, .. }| {
            (
                stock.map(|s| VerifiedStock {
                    far: s.far,
                    jamb: s.jamb,
                    width: s.width,
                    sources: s.sources(),
                }),
                swing,
            )
        })
        .collect::<Vec<_>>();
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("missing dashed swing wall decisions")?;
    let number = page.page_number;
    for (i, (stock, swing)) in proposals.into_iter().enumerate() {
        let ordinal = i + 1;
        let door_id = format!("p{number}:dashed-swing-door:{ordinal}");
        let hosts = [1, 2].map(|side| format!("{STOCK_PREFIX}{number}:{ordinal}:{side}"));
        let source_ids = stock
            .iter()
            .flat_map(|s| s.sources.iter().cloned())
            .chain(swing.segment_source_ids.iter().cloned())
            .chain(swing.open_leaf_source_ids.iter().cloned())
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect::<Vec<_>>();
        let start = stock[0].jamb;
        let end = stock[1].jamb;
        for (s, id) in stock.into_iter().zip(&hosts) {
            let sources = s.sources;
            let wall = Wall {
                id: id.clone(),
                page: number,
                start: s.far,
                end: s.jamb,
                thickness: s.width,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: sources.clone(),
                requires_review: false,
            };
            page.semantics.routing.push(wall_route(&wall));
            page.walls.push(wall);
            decisions.push(WallDecision {
                wall_id: id.clone(),
                status: WallStatus::Ready,
                reason: REASON,
                component_id: format!("p{number}:{id}"),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![],
                support_source_ids: sources,
                opening_ids: vec![door_id.clone()],
                room_ids: vec![],
                placement_extent: None,
            });
        }
        page.semantics.routing.push(RoutingItem {
            kind: RoutingKind::DoorPassageCandidate,
            entity_id: door_id.clone(),
            source_ids: source_ids.clone(),
            span: Some(Span { start, end }),
            curve: None,
            room_ids: vec![],
            usable_for_routing: false,
            requires_review: true,
        });
        page.openings.push(Opening {
            id: door_id,
            page: number,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: hosts[0].clone(),
            host_wall_ids: Vec::from(hosts),
            start,
            end,
            hinge: Some(swing.hinge),
            swing_observations: vec![swing],
            source_ids,
            evidence: EVIDENCE,
            requires_review: true,
            physical_jambs: None,
        });
    }
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let walls = page
        .walls
        .iter()
        .filter(|w| w.id.starts_with(STOCK_PREFIX))
        .map(|w| w.id.as_str())
        .collect::<HashSet<_>>();
    let doors = page
        .openings
        .iter()
        .filter(|o| o.evidence == EVIDENCE)
        .map(|o| o.id.as_str())
        .collect::<HashSet<_>>();
    if walls.is_empty() && doors.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    base.walls.retain(|w| !walls.contains(w.id.as_str()));
    base.openings.retain(|o| !doors.contains(o.id.as_str()));
    base.placement_decisions
        .as_mut()
        .ok_or("missing dashed swing decisions")?
        .retain(|d| !walls.contains(d.wall_id.as_str()));
    base.semantics
        .routing
        .retain(|r| !walls.contains(r.entity_id.as_str()) && !doors.contains(r.entity_id.as_str()));
    Ok(Cow::Owned(base))
}

pub(crate) fn validate_swing(o: &Opening, sources: &HashMap<&str, &Primitive>) -> bool {
    let [s] = o.swing_observations.as_slice() else {
        return false;
    };
    let radius = o.start.distance(o.end);
    let eps = radius * 0.00001;
    let f = Pose {
        hinge: s.hinge,
        open: s.open_tip,
        closed: s.closed_tip,
        radius,
        eps,
    };
    let u = (o.end - o.start).unit();
    let n = s.hinge - o.start;
    if o.kind != OpeningKind::Door
        || o.operation != "Hinged"
        || o.hinge != Some(s.hinge)
        || !o.requires_review
        || o.physical_jambs.is_some()
        || !s.closed_leaf_source_ids.is_empty()
        || !matches!(o.host_wall_ids.as_slice(),[a,b] if a==&o.host_wall_id&&a!=b)
        || !(0.018..=0.058).contains(&(s.leaf_tolerance / radius))
        || n.norm() > radius * 0.2
        || n.dot(u).abs() > eps
        || (s.closed_tip - s.hinge - u * radius).norm() > eps
        || (s.open_tip - s.hinge).dot(u).abs() > eps
        || !near(s.open_tip.distance(s.hinge), radius, eps)
        || s.segment_source_ids.first() != Some(&s.arc_source_id)
    {
        return false;
    }
    let get = |ids: &[String]| {
        ids.iter()
            .map(|id| {
                o.source_ids
                    .contains(id)
                    .then(|| sources.get(id.as_str()).copied().and_then(Line::native))
                    .flatten()
            })
            .collect::<Option<Vec<_>>>()
    };
    let (Some(arc), Some(leaf_ink)) = (get(&s.segment_source_ids), get(&s.open_leaf_source_ids))
    else {
        return false;
    };
    let mut work = MAX_WORK;
    let Some(Some(l)) = leaf(f, &leaf_ink, &mut work) else {
        return false;
    };
    l.ids
        .iter()
        .copied()
        .eq(s.open_leaf_source_ids.iter().map(String::as_str))
        && near(l.depth, s.leaf_tolerance, eps)
        && segments(f, &arc, l.pen, &l.ids).is_some_and(|c| {
            c.into_iter()
                .eq(s.segment_source_ids.iter().map(String::as_str))
        })
}
