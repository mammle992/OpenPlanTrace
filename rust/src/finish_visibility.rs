//! Local finish-area visibility through later PDF paint. Paint history is input-only.
use crate::model::{PageScan, Point, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};

const MAX_EVENTS: usize = 200_000;
const MAX_POINTS: usize = 2_000_000;
const MAX_RECEIPTS: usize = 16_384;
const MAX_WORK: usize = 32_000_000;
const MAX_CONTOUR_EDGES: usize = 8192;

/// An actual split portion of an original contour stroke, never its whole bbox.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ContourEdge {
    pub source_id: String,
    pub start: Point,
    pub end: Point,
}

#[derive(Clone, Debug, PartialEq)]
pub struct FinishRequirement {
    pub rectangle: [Point; 4],
    pub source_ids: Vec<String>,
    pub contour_edges: Vec<ContourEdge>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FinishReceipt {
    pub id: String,
    pub rectangle: [Point; 4],
    pub source_ids: Vec<String>,
    pub source_orders: Vec<u64>,
    pub anchor_order: u64,
    pub last_order: u64,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub contour_edges: Vec<ContourEdge>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum VisibilityStatus {
    Available,
    Unavailable,
    BudgetExhausted,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FinishVisibility {
    pub revision: u32,
    pub status: VisibilityStatus,
    pub receipts: Vec<FinishReceipt>,
}
impl FinishVisibility {
    pub fn unavailable() -> Self {
        Self {
            revision: 1,
            status: VisibilityStatus::Unavailable,
            receipts: vec![],
        }
    }
}

fn canonical(mut q: FinishRequirement) -> FinishRequirement {
    q.source_ids.sort();
    q.source_ids.dedup();
    let cmp = |a: &Point, b: &Point| a.x.total_cmp(&b.x).then(a.y.total_cmp(&b.y));
    let start = (0..4)
        .min_by(|&a, &b| cmp(&q.rectangle[a], &q.rectangle[b]))
        .unwrap();
    let r = q.rectangle;
    q.rectangle = std::array::from_fn(|i| r[(start + i) % 4]);
    if cmp(&q.rectangle[1], &q.rectangle[3]).is_gt() {
        q.rectangle.swap(1, 3);
    }
    for edge in &mut q.contour_edges {
        if cmp(&edge.start, &edge.end).is_gt() {
            std::mem::swap(&mut edge.start, &mut edge.end);
        }
    }
    q.contour_edges.sort_by(|a, b| {
        a.source_id
            .cmp(&b.source_id)
            .then_with(|| cmp(&a.start, &b.start))
            .then_with(|| cmp(&a.end, &b.end))
    });
    q.contour_edges.dedup();
    q
}

pub fn receipt_for<'a>(page: &'a PageScan, q: &FinishRequirement) -> Option<&'a FinishReceipt> {
    let inventory = page.finish_visibility.as_ref()?;
    if inventory.revision != 1 || inventory.status != VisibilityStatus::Available {
        return None;
    }
    let q = canonical(q.clone());
    inventory.receipts.iter().find(|r| {
        r.rectangle == q.rectangle
            && r.source_ids == q.source_ids
            && r.contour_edges == q.contour_edges
    })
}

#[derive(Clone, Debug)]
struct Event {
    order: u64,
    bounds: [f64; 4],
    paths: Vec<Vec<Point>>,
    clip: Vec<Point>,
    even_odd: bool,
}

/// Bounded temporary input evidence. It is deliberately not serialized in a scan.
#[derive(Clone, Debug, Default)]
pub struct PaintHistory {
    orders: BTreeMap<String, u64>,
    events: Vec<Event>,
    points: usize,
    exhausted: bool,
    last_order: u64,
}

fn bbox(points: &[Point]) -> Option<[f64; 4]> {
    let mut r = [
        f64::INFINITY,
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::NEG_INFINITY,
    ];
    for p in points {
        if !p.finite() {
            return None;
        }
        r[0] = r[0].min(p.x);
        r[1] = r[1].min(p.y);
        r[2] = r[2].max(p.x);
        r[3] = r[3].max(p.y);
    }
    (r[0] < r[2] && r[1] < r[3]).then_some(r)
}
fn overlaps(a: [f64; 4], b: [f64; 4]) -> bool {
    a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3]
}
fn area(p: &[Point]) -> f64 {
    let Some(&o) = p.first() else {
        return 0.;
    };
    p.iter()
        .zip(p.iter().cycle().skip(1))
        .map(|(a, b)| (*a - o).cross(*b - o))
        .sum::<f64>()
        * 0.5
}
fn epsilon(p: &[Point]) -> f64 {
    64. * f64::EPSILON
        * p.iter()
            .flat_map(|p| [p.x.abs(), p.y.abs()])
            .fold(1., f64::max)
}

/// Clip a convex query to a convex graphics clip. Boundary-only contact has no area.
fn clip_polygon(mut p: Vec<Point>, clip: &[Point]) -> Vec<Point> {
    if clip.is_empty() {
        return p;
    }
    let sign = area(clip).signum();
    if clip.len() < 3 || sign == 0. {
        return vec![];
    }
    for (&a, &b) in clip.iter().zip(clip.iter().cycle().skip(1)) {
        if p.is_empty() {
            break;
        }
        let mut out = vec![];
        let e = b - a;
        for (&u, &v) in p.iter().zip(p.iter().cycle().skip(1)) {
            let du = sign * e.cross(u - a);
            let dv = sign * e.cross(v - a);
            let (iu, iv) = (du >= 0., dv >= 0.);
            if iu {
                out.push(u);
            }
            if iu != iv {
                out.push(u + (v - u) * (du / (du - dv)));
            }
        }
        p = out;
    }
    p
}

fn segment_inside(a: Point, b: Point, convex: &[Point], eps: f64) -> bool {
    let sign = area(convex).signum();
    let mut lo = 0_f64;
    let mut hi = 1_f64;
    for (&p, &q) in convex.iter().zip(convex.iter().cycle().skip(1)) {
        let edge = q - p;
        let norm = edge.norm();
        if norm <= eps {
            continue;
        }
        let x = sign * edge.cross(a - p) / norm - eps;
        let y = sign * edge.cross(b - p) / norm - eps;
        if x < 0. && y < 0. {
            return false;
        }
        if x < 0. {
            lo = lo.max(x / (x - y));
        }
        if y < 0. {
            hi = hi.min(x / (x - y));
        }
        if lo >= hi {
            return false;
        }
    }
    (b - a).norm() * (hi - lo) > eps
}

fn winding(at: Point, paths: &[Vec<Point>]) -> i64 {
    let mut n = 0_i64;
    for p in paths {
        for (&a, &b) in p.iter().zip(p.iter().cycle().skip(1)) {
            let side = (b - a).cross(at - a);
            if a.y <= at.y && b.y > at.y && side > 0. {
                n += 1;
            } else if a.y > at.y && b.y <= at.y && side < 0. {
                n -= 1;
            }
        }
    }
    n
}

fn event_hits(rect: &[Point; 4], e: &Event, work: &mut usize) -> Result<bool, ()> {
    let n = e.paths.iter().map(Vec::len).sum::<usize>();
    let cost = e
        .clip
        .len()
        .saturating_mul(e.clip.len() + 4)
        .saturating_add(n.saturating_mul(e.clip.len() + 4));
    *work = work.checked_sub(cost.saturating_add(1)).ok_or(())?;
    let q = clip_polygon(rect.to_vec(), &e.clip);
    if q.len() < 3 {
        return Ok(false);
    }
    let eps = epsilon(&q);
    let perimeter = q
        .iter()
        .zip(q.iter().cycle().skip(1))
        .map(|(a, b)| a.distance(*b))
        .sum::<f64>();
    if area(&q).abs() <= eps * perimeter {
        return Ok(false);
    }
    // A fill boundary entering the strict query interior withholds evidence.
    // This is conservative for cancelling duplicate paths, while correctly
    // preserving a complete hole or merely touching adjacent material.
    if e.paths.iter().any(|p| {
        p.iter()
            .zip(p.iter().cycle().skip(1))
            .any(|(&a, &b)| segment_inside(a, b, &q, eps))
    }) {
        return Ok(true);
    }
    let center = q.iter().copied().fold(Point::default(), |a, b| a + b) * (1. / q.len() as f64);
    let w = winding(center, &e.paths);
    Ok(if e.even_odd { w % 2 != 0 } else { w != 0 })
}

fn spend(work: &mut usize, cost: usize) -> Result<(), ()> {
    *work = work.checked_sub(cost).ok_or(())?;
    Ok(())
}

fn touches_segment(a: Point, b: Point, bounds: [f64; 4]) -> bool {
    a.x.min(b.x) <= bounds[2]
        && a.x.max(b.x) >= bounds[0]
        && a.y.min(b.y) <= bounds[3]
        && a.y.max(b.y) >= bounds[1]
}

/// Test the strict centerline only on the selected split contour edge. Cuts at
/// every paint/clip boundary make winding constant on each open subinterval,
/// including nonzero/even-odd holes and clipping. Boundary-only contact is safe.
fn edge_event_hits(a: Point, b: Point, e: &Event, work: &mut usize) -> Result<bool, ()> {
    let count = e.paths.iter().map(Vec::len).sum::<usize>() + e.clip.len();
    spend(work, count.saturating_add(1))?;
    let v = b - a;
    let length = v.norm();
    let eps = epsilon(&[a, b]);
    if length <= eps || (!e.clip.is_empty() && e.clip.len() < 3) || !touches_segment(a, b, e.bounds)
    {
        return Ok(false);
    }
    let mut cuts = vec![0., 1.];
    for path in e.paths.iter().chain(std::iter::once(&e.clip)) {
        for (&p, &q) in path.iter().zip(path.iter().cycle().skip(1)) {
            let d = q - p;
            let den = v.cross(d);
            let n = d.norm();
            if den.abs() > eps * (length + n) {
                let t = (p - a).cross(d) / den;
                let s = (p - a).cross(v) / den;
                if t >= -eps / length
                    && t <= 1. + eps / length
                    && s >= -eps / n.max(eps)
                    && s <= 1. + eps / n.max(eps)
                {
                    cuts.push(t.clamp(0., 1.));
                }
            } else if (p - a).cross(v).abs() <= eps * length {
                for p in [p, q] {
                    let t = (p - a).dot(v) / v.dot(v);
                    if t > 0. && t < 1. {
                        cuts.push(t);
                    }
                }
            }
        }
    }
    spend(
        work,
        cuts.len()
            .saturating_mul(cuts.len().max(1).ilog2() as usize + 1),
    )?;
    cuts.sort_by(f64::total_cmp);
    cuts.dedup_by(|a, b| (*a - *b).abs() * length <= eps);
    for pair in cuts.windows(2) {
        if (pair[1] - pair[0]) * length <= eps {
            continue;
        }
        spend(work, count.saturating_mul(3).saturating_add(1))?;
        let at = a + v * ((pair[0] + pair[1]) * 0.5);
        let clip_sign = area(&e.clip).signum();
        if (!e.clip.is_empty() && clip_sign == 0.)
            || e.clip
                .iter()
                .zip(e.clip.iter().cycle().skip(1))
                .any(|(&p, &q)| {
                    let d = q - p;
                    d.norm() > eps && clip_sign * d.cross(at - p) <= eps * d.norm()
                })
        {
            continue;
        }
        let boundary = e.paths.iter().find_map(|p| {
            p.iter().zip(p.iter().cycle().skip(1)).find_map(|(&p, &q)| {
                (p.distance(q) > eps && crate::model::on_segment(at, p, q).0.distance(at) <= eps)
                    .then_some(q - p)
            })
        });
        if let Some(direction) = boundary {
            // A subpath boundary can be INTERNAL to the painted union. The
            // actual winding must be filled on both normal sides; sampling is
            // bounded by the nearest nonincident edge/clip, never a fixed gap.
            spend(work, count.saturating_mul(4).saturating_add(1))?;
            let mut gap = f64::INFINITY;
            for path in e.paths.iter().chain(std::iter::once(&e.clip)) {
                for (&p, &q) in path.iter().zip(path.iter().cycle().skip(1)) {
                    let distance = crate::model::on_segment(at, p, q).0.distance(at);
                    if distance > eps {
                        gap = gap.min(distance);
                    }
                }
            }
            if !gap.is_finite() || gap <= eps {
                return Ok(true);
            }
            let normal = Point::new(-direction.y, direction.x).unit() * (gap * 0.25);
            let filled = |p| {
                let w = winding(p, &e.paths);
                if e.even_odd { w % 2 != 0 } else { w != 0 }
            };
            if filled(at + normal) && filled(at - normal) {
                return Ok(true);
            }
            continue;
        }
        let w = winding(at, &e.paths);
        if if e.even_odd { w % 2 != 0 } else { w != 0 } {
            return Ok(true);
        }
    }
    Ok(false)
}

/// A background operation can predate the finite finish's local boundary even
/// when old remote return edges already existed. Both complete long sides are
/// mandatory; retain the EARLIEST order of all their contributing source edges.
fn local_boundary_order(
    query: &FinishRequirement,
    orders: &BTreeMap<String, u64>,
    work: &mut usize,
) -> Result<Option<u64>, ()> {
    if query.contour_edges.is_empty() {
        return Ok(None);
    }
    let r = query.rectangle;
    let (u, n) = if r[0].distance(r[1]) >= r[0].distance(r[3]) {
        (r[1] - r[0], r[3] - r[0])
    } else {
        (r[3] - r[0], r[1] - r[0])
    };
    let length = u.norm();
    let depth = n.norm();
    let axis = u.unit();
    let normal = n.unit();
    let eps = epsilon(&r);
    let mut sides = [vec![], vec![]];
    let mut earliest = u64::MAX;
    spend(work, query.contour_edges.len())?;
    for edge in &query.contour_edges {
        let a = edge.start - r[0];
        let b = edge.end - r[0];
        for (i, y) in [0., depth].into_iter().enumerate() {
            if (a.dot(normal) - y).abs() <= eps && (b.dot(normal) - y).abs() <= eps {
                let lo = a.dot(axis).min(b.dot(axis)).max(0.);
                let hi = a.dot(axis).max(b.dot(axis)).min(length);
                if hi - lo > eps {
                    sides[i].push((lo, hi));
                    earliest = earliest.min(*orders.get(&edge.source_id).ok_or(())?);
                }
            }
        }
    }
    for side in &mut sides {
        spend(
            work,
            side.len()
                .saturating_mul(side.len().max(1).ilog2() as usize + 1),
        )?;
        side.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
        let mut at = 0.;
        for &(lo, hi) in side.iter() {
            if lo > at + eps {
                return Ok(None);
            }
            at = at.max(hi);
        }
        if at < length - eps {
            return Ok(None);
        }
    }
    Ok((earliest != u64::MAX).then_some(earliest))
}

fn valid_contour(query: &FinishRequirement) -> bool {
    if query.contour_edges.is_empty() {
        return true;
    }
    if query.contour_edges.len() > MAX_CONTOUR_EDGES {
        return false;
    }
    let ids: std::collections::BTreeSet<_> = query
        .contour_edges
        .iter()
        .map(|e| e.source_id.as_str())
        .collect();
    if !ids
        .iter()
        .copied()
        .eq(query.source_ids.iter().map(String::as_str))
    {
        return false;
    }
    query.contour_edges.iter().all(|e| {
        [e.start, e.end]
            .iter()
            .all(|p| p.finite() && p.x.abs() <= 1e12 && p.y.abs() <= 1e12)
            && e.start.distance(e.end) > epsilon(&[e.start, e.end])
    })
}

impl PaintHistory {
    pub fn record_ink(&mut self, id: String, order: u64) {
        self.last_order = self.last_order.max(order);
        if self.exhausted {
            return;
        }
        if self.orders.len() >= 500_000 {
            self.fail();
            return;
        }
        self.orders.insert(id, order);
    }
    pub fn last_order(&mut self, order: u64) {
        self.last_order = self.last_order.max(order);
    }
    fn fail(&mut self) {
        self.exhausted = true;
        self.events.clear();
        self.orders.clear();
    }
    pub fn record_paths(
        &mut self,
        order: u64,
        paths: Vec<Vec<Point>>,
        even_odd: bool,
        clip: Vec<Point>,
    ) {
        self.last_order = self.last_order.max(order);
        if self.exhausted {
            return;
        }
        let Some(bounds) = bbox(&clip) else {
            return;
        };
        let count = paths
            .iter()
            .map(Vec::len)
            .sum::<usize>()
            .saturating_add(clip.len());
        self.points = self.points.saturating_add(count);
        if self.events.len() >= MAX_EVENTS || self.points > MAX_POINTS {
            self.fail();
            return;
        }
        if paths.iter().flatten().any(|p| !p.finite()) {
            self.fail();
            return;
        }
        let Some(pbounds) = bbox(&paths.iter().flatten().copied().collect::<Vec<_>>()) else {
            return;
        };
        if !overlaps(bounds, pbounds) {
            return;
        }
        self.events.push(Event {
            order,
            bounds: [
                bounds[0].max(pbounds[0]),
                bounds[1].max(pbounds[1]),
                bounds[2].min(pbounds[2]),
                bounds[3].min(pbounds[3]),
            ],
            paths,
            clip,
            even_odd,
        });
    }
    pub fn record_bounds(&mut self, order: u64, min: Point, max: Point) {
        let r = vec![min, Point::new(max.x, min.y), max, Point::new(min.x, max.y)];
        self.record_paths(order, vec![r.clone()], false, r);
    }
    pub fn certify(
        self,
        page_number: u32,
        requirements: Vec<FinishRequirement>,
    ) -> FinishVisibility {
        let empty = |status| FinishVisibility {
            revision: 1,
            status,
            receipts: vec![],
        };
        if self.exhausted {
            return empty(VisibilityStatus::BudgetExhausted);
        }
        if requirements.len() > MAX_RECEIPTS {
            return empty(VisibilityStatus::BudgetExhausted);
        }
        let mut work = MAX_WORK;
        let mut receipts = vec![];
        let mut seen = vec![];
        for query in requirements {
            let Some(remaining) = work.checked_sub(
                self.events
                    .len()
                    .saturating_mul(query.contour_edges.len().saturating_add(1))
                    .saturating_add(seen.len())
                    .saturating_add(query.source_ids.len())
                    .saturating_add(
                        query
                            .contour_edges
                            .len()
                            .saturating_mul(query.contour_edges.len().max(1).ilog2() as usize + 2),
                    )
                    .saturating_add(1),
            ) else {
                return empty(VisibilityStatus::BudgetExhausted);
            };
            work = remaining;
            let query = canonical(query);
            if seen.contains(&query) {
                continue;
            }
            seen.push(query.clone());
            if query.source_ids.is_empty()
                || query.source_ids.len() > 1024
                || !valid_rectangle(&query.rectangle)
                || !valid_contour(&query)
                || !closed_contour(&query.contour_edges)
            {
                continue;
            }
            let Some(source_orders) = query
                .source_ids
                .iter()
                .map(|id| self.orders.get(id).copied())
                .collect::<Option<Vec<_>>>()
            else {
                continue;
            };
            let anchor_order = *source_orders.iter().min().unwrap();
            let local_order = match local_boundary_order(&query, &self.orders, &mut work) {
                Ok(order) => order.unwrap_or(anchor_order),
                Err(()) => return empty(VisibilityStatus::BudgetExhausted),
            };
            let Some(bounds) = bbox(&query.rectangle) else {
                continue;
            };
            let mut obscured = false;
            for event in self.events.iter().filter(|e| e.order > anchor_order) {
                if event.order > local_order
                    && overlaps(bounds, event.bounds)
                    && event_hits(&query.rectangle, event, &mut work).unwrap_or_else(|_| {
                        work = 0;
                        true
                    })
                {
                    obscured = true;
                    break;
                }
                for edge in &query.contour_edges {
                    if event.order > self.orders[&edge.source_id]
                        && touches_segment(edge.start, edge.end, event.bounds)
                        && edge_event_hits(edge.start, edge.end, event, &mut work).unwrap_or_else(
                            |_| {
                                work = 0;
                                true
                            },
                        )
                    {
                        obscured = true;
                        break;
                    }
                }
                if obscured {
                    break;
                }
            }
            if work == 0 {
                return empty(VisibilityStatus::BudgetExhausted);
            }
            if obscured {
                continue;
            }
            receipts.push(FinishReceipt {
                id: format!("p{page_number}:finish-visibility:{}", receipts.len() + 1),
                rectangle: query.rectangle,
                source_ids: query.source_ids,
                source_orders,
                anchor_order,
                last_order: self.last_order,
                contour_edges: query.contour_edges,
            });
        }
        FinishVisibility {
            revision: 1,
            status: VisibilityStatus::Available,
            receipts,
        }
    }
}

fn valid_rectangle(p: &[Point; 4]) -> bool {
    if p.iter()
        .any(|p| !p.finite() || p.x.abs() > 1e12 || p.y.abs() > 1e12)
    {
        return false;
    }
    let a = p[1] - p[0];
    let b = p[3] - p[0];
    let eps = epsilon(p);
    a.norm() > eps
        && b.norm() > eps
        && a.dot(b).abs() <= eps * (a.norm() + b.norm())
        && (p[0] + a + b).distance(p[2]) <= eps
}

fn edge_bound_to_source(
    edge: &ContourEdge,
    primitive: &crate::model::Primitive,
    work: &mut usize,
) -> Result<bool, String> {
    let mut contains = |a: Point, b: Point| -> Result<bool, String> {
        spend(work, 1).map_err(|_| "finish contour binding budget exceeded")?;
        let eps = epsilon(&[a, b, edge.start, edge.end]);
        Ok(a.finite()
            && b.finite()
            && a.distance(b) > eps
            && [edge.start, edge.end]
                .iter()
                .all(|&p| crate::model::on_segment(p, a, b).0.distance(p) <= eps))
    };
    match &primitive.shape {
        Shape::Line { start, end } => contains(*start, *end),
        Shape::Polyline {
            points,
            closed,
            filled: false,
        } => {
            for pair in points.windows(2) {
                if contains(pair[0], pair[1])? {
                    return Ok(true);
                }
            }
            if *closed && points.len() > 2 && points.first() != points.last() {
                return contains(*points.last().unwrap(), points[0]);
            }
            Ok(false)
        }
        _ => Ok(false),
    }
}

fn closed_contour(edges: &[ContourEdge]) -> bool {
    if edges.is_empty() {
        return true;
    }
    let key = |p: Point| (p.x.to_bits(), p.y.to_bits());
    let mut adjacent: HashMap<_, HashSet<_>> = HashMap::new();
    for edge in edges {
        let (a, b) = (key(edge.start), key(edge.end));
        adjacent.entry(a).or_default().insert(b);
        adjacent.entry(b).or_default().insert(a);
    }
    if adjacent.len() < 4 || adjacent.values().any(|v| v.len() != 2) {
        return false;
    }
    let mut pending = vec![*adjacent.keys().next().unwrap()];
    let mut seen = HashSet::new();
    while let Some(p) = pending.pop() {
        if seen.insert(p) {
            pending.extend(adjacent[&p].iter().copied());
        }
    }
    seen.len() == adjacent.len()
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    let inventory = page
        .finish_visibility
        .as_ref()
        .ok_or("missing finish visibility inventory")?;
    if inventory.revision != 1 || inventory.receipts.len() > MAX_RECEIPTS {
        return Err("invalid finish visibility revision or size".into());
    }
    if inventory.status != VisibilityStatus::Available && !inventory.receipts.is_empty() {
        return Err("unavailable finish visibility cannot contain receipts".into());
    }
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut ids = HashSet::new();
    let mut work = MAX_WORK;
    for r in &inventory.receipts {
        if !r
            .id
            .strip_prefix(&format!("p{}:finish-visibility:", page.page_number))
            .is_some_and(|suffix| {
                suffix
                    .parse::<u64>()
                    .ok()
                    .is_some_and(|n| n > 0 && n.to_string() == suffix)
            })
            || !ids.insert(r.id.as_str())
            || sources.contains_key(r.id.as_str())
            || page.material_tiles.iter().any(|t| t.id == r.id)
            || !valid_rectangle(&r.rectangle)
            || r.source_ids.is_empty()
            || r.source_ids.len() > 1024
            || r.contour_edges.len() > MAX_CONTOUR_EDGES
            || r.source_ids.windows(2).any(|p| p[0] >= p[1])
            || r.source_orders.len() != r.source_ids.len()
            || r.anchor_order == 0
            || r.source_orders.iter().min() != Some(&r.anchor_order)
            || r.source_orders.iter().any(|o| *o == 0 || *o > r.last_order)
            || r.source_ids
                .iter()
                .zip(&r.source_orders)
                .any(|(id, &order)| pdf_source_order(id, page.page_number) != Some(order))
            || r.source_ids.iter().any(|id| {
                !sources.get(id.as_str()).is_some_and(|p| {
                    matches!(
                        p.shape,
                        Shape::Line { .. } | Shape::Polyline { filled: false, .. }
                    )
                })
            })
        {
            return Err("invalid finish visibility receipt binding".into());
        }
        spend(
            &mut work,
            r.contour_edges
                .len()
                .saturating_mul(r.contour_edges.len().max(1).ilog2() as usize + 5),
        )
        .map_err(|_| "finish contour validation budget exceeded")?;
        let q = FinishRequirement {
            rectangle: r.rectangle,
            source_ids: r.source_ids.clone(),
            contour_edges: r.contour_edges.clone(),
        };
        let normalized = canonical(q.clone());
        if !valid_contour(&q)
            || normalized.contour_edges != r.contour_edges
            || !closed_contour(&r.contour_edges)
        {
            return Err("invalid closed finish contour inventory".into());
        }
        for edge in &r.contour_edges {
            let Some(source) = sources.get(edge.source_id.as_str()) else {
                return Err("unresolved finish contour source".into());
            };
            if !crate::openings::physical_jambs::structural(source)
                || !edge_bound_to_source(edge, source, &mut work)?
            {
                return Err("finish contour edge is not bound to actual source ink".into());
            }
        }
    }
    Ok(())
}

fn pdf_source_order(id: &str, page: u32) -> Option<u64> {
    let tail = id.strip_prefix(&format!("pdf:p{page}:path:"))?;
    let (number, suffix) = tail.split_once(":subpath:")?;
    let subpath = suffix.split(':').next()?.parse::<u64>().ok()?;
    let number = number.parse::<u64>().ok()?;
    (number > 0 && subpath > 0)
        .then(|| number.checked_mul(2))
        .flatten()
}

#[cfg(test)]
mod tests {
    use super::*;
    fn rect(x0: f64, y0: f64, x1: f64, y1: f64) -> [Point; 4] {
        [
            Point::new(x0, y0),
            Point::new(x1, y0),
            Point::new(x1, y1),
            Point::new(x0, y1),
        ]
    }
    fn history() -> PaintHistory {
        let mut h = PaintHistory::default();
        h.record_ink("pdf:p1:path:1:subpath:1".into(), 2);
        h
    }
    fn query() -> FinishRequirement {
        FinishRequirement {
            rectangle: rect(10., 20., 12., 100.),
            source_ids: vec!["pdf:p1:path:1:subpath:1".into()],
            contour_edges: vec![],
        }
    }
    fn cover(h: &mut PaintHistory, order: u64, r: [Point; 4]) {
        h.record_paths(
            order,
            vec![r.to_vec()],
            false,
            rect(0., 0., 200., 200.).to_vec(),
        );
    }
    #[test]
    fn whole_band_rejects_later_interior_paint_even_when_all_boundary_ink_survives() {
        let mut h = history();
        cover(&mut h, 4, rect(10.1, 40., 11.9, 50.));
        assert!(h.certify(1, vec![query()]).receipts.is_empty());
    }
    #[test]
    fn adjacent_tile_boundary_and_remote_same_path_cover_do_not_erase_local_band() {
        for r in [rect(12., 20., 40., 100.), rect(10., 120., 12., 140.)] {
            let mut h = history();
            cover(&mut h, 4, r);
            assert_eq!(h.certify(1, vec![query()]).receipts.len(), 1);
        }
    }
    #[test]
    fn immediate_unknown_image_after_outline_is_later_than_its_even_source_order() {
        let mut h = history();
        h.record_bounds(3, Point::new(9., 40.), Point::new(13., 41.));
        assert!(h.certify(1, vec![query()]).receipts.is_empty());
        let mut h = history();
        cover(&mut h, 1, rect(9., 40., 13., 41.));
        assert_eq!(h.certify(1, vec![query()]).receipts.len(), 1);
    }
    #[test]
    fn complete_evenodd_hole_and_exact_graphics_clip_preserve_unpainted_band() {
        let mut h = history();
        h.record_paths(
            4,
            vec![
                rect(0., 0., 200., 200.).to_vec(),
                rect(9., 19., 13., 101.).to_vec(),
            ],
            true,
            rect(0., 0., 200., 200.).to_vec(),
        );
        assert_eq!(h.certify(1, vec![query()]).receipts.len(), 1);
        let mut h = history();
        h.record_paths(
            4,
            vec![rect(0., 0., 200., 200.).to_vec()],
            false,
            rect(20., 0., 30., 200.).to_vec(),
        );
        assert_eq!(h.certify(1, vec![query()]).receipts.len(), 1);
    }
    #[test]
    fn a_real_small_cover_and_erased_closure_endpoint_withhold_without_percentage_rounding() {
        for r in [
            rect(10.5, 50., 10.50001, 50.00001),
            rect(9., 19.9, 13., 20.1),
        ] {
            let mut h = history();
            cover(&mut h, 4, r);
            assert!(h.certify(1, vec![query()]).receipts.is_empty());
        }
    }
    #[test]
    fn rotated_translated_thin_rectangles_keep_roundoff_and_canonical_order() {
        for scale in [0.01, 1., 12.] {
            for a in [0., 0.37, 2.1] {
                let u = Point::new(f64::cos(a), f64::sin(a));
                let n = Point::new(-u.y, u.x);
                let mut q = query();
                q.rectangle = q
                    .rectangle
                    .map(|p| Point::new(1200., 900.) + (u * p.x + n * p.y) * scale);
                let r = history().certify(1, vec![q.clone(), q]);
                assert_eq!(r.receipts.len(), 1);
            }
        }
    }
    #[test]
    fn missing_source_order_and_exhaustion_fail_closed() {
        assert!(
            PaintHistory::default()
                .certify(1, vec![query()])
                .receipts
                .is_empty()
        );
        let mut h = history();
        h.fail();
        let out = h.certify(1, vec![query()]);
        assert_eq!(out.status, VisibilityStatus::BudgetExhausted);
        assert!(out.receipts.is_empty());
        assert_eq!(
            pdf_source_order("pdf:p3:path:17:subpath:2:part:1", 3),
            Some(34)
        );
        assert_eq!(pdf_source_order("pdf:p3:path:17:subpath:2", 2), None);
    }

    fn contour_fixture() -> (PaintHistory, FinishRequirement) {
        let mut h = PaintHistory::default();
        let edges = [
            (1, Point::new(10., 0.), Point::new(12., 0.)),
            (2, Point::new(12., 120.), Point::new(10., 120.)),
            (3, Point::new(10., 120.), Point::new(10., 0.)),
            (4, Point::new(12., 0.), Point::new(12., 120.)),
        ];
        let contour_edges: Vec<_> = edges
            .into_iter()
            .map(|(n, start, end)| {
                let source_id = format!("pdf:p1:path:{n}:subpath:1");
                h.record_ink(source_id.clone(), 2 * n);
                ContourEdge {
                    source_id,
                    start,
                    end,
                }
            })
            .collect();
        let q = FinishRequirement {
            rectangle: rect(10., 20., 12., 100.),
            source_ids: contour_edges.iter().map(|e| e.source_id.clone()).collect(),
            contour_edges,
        };
        (h, q)
    }

    #[test]
    fn complete_local_faces_after_background_keep_all_remote_source_orders() {
        let (mut h, q) = contour_fixture();
        cover(&mut h, 5, rect(10., 10., 12., 110.));
        let out = h.certify(1, vec![q]);
        assert_eq!(out.receipts.len(), 1);
        let r = &out.receipts[0];
        assert_eq!(
            r.anchor_order, 2,
            "global provenance anchor never moves forward"
        );
        assert_eq!(r.source_orders, vec![2, 4, 6, 8]);
        assert_eq!(r.contour_edges.len(), 4);
        assert!(
            serde_json::to_value(r)
                .unwrap()
                .get("contourEdges")
                .is_some()
        );
        assert!(
            serde_json::to_value(history().certify(1, vec![query()]).receipts.remove(0))
                .unwrap()
                .get("contourEdges")
                .is_none()
        );
    }

    #[test]
    fn local_earliest_side_rejects_interior_erasure_and_partial_redraw() {
        for order in [7, 9] {
            let (mut h, q) = contour_fixture();
            cover(&mut h, order, rect(10.2, 40., 11.8, 50.));
            assert!(
                h.certify(1, vec![q]).receipts.is_empty(),
                "erase order{order}: later right side alone cannot restore interior"
            );
        }
        let (mut h, mut q) = contour_fixture();
        // Removing part of a local side must not advance the timing authority.
        q.contour_edges[2].end.y = 40.;
        cover(&mut h, 5, rect(10., 10., 12., 110.));
        assert!(h.certify(1, vec![q]).receipts.is_empty());
    }

    #[test]
    fn every_remote_split_edge_stays_visible_after_its_own_source_order() {
        for unknown in [false, true] {
            for width in [0.2, 0.00001] {
                let (mut h, q) = contour_fixture();
                let r = rect(10.5, -0.1, 10.5 + width, 0.1);
                if unknown {
                    h.record_bounds(5, r[0], r[2]);
                } else {
                    h.record_paths(
                        5,
                        vec![r.to_vec()],
                        false,
                        rect(0., -10., 200., 200.).to_vec(),
                    );
                }
                assert!(
                    h.certify(1, vec![q]).receipts.is_empty(),
                    "remote closure erased before local sides; unknown={unknown},width={width}"
                );
            }
        }
    }

    #[test]
    fn remote_edge_holes_clips_and_boundary_touch_are_not_erasure() {
        let (mut h, q) = contour_fixture();
        h.record_paths(
            5,
            vec![
                rect(9., -1., 13., 1.).to_vec(),
                rect(9.5, -0.5, 12.5, 0.5).to_vec(),
            ],
            true,
            rect(0., -10., 200., 200.).to_vec(),
        );
        assert_eq!(h.certify(1, vec![q.clone()]).receipts.len(), 1);
        let (mut h, _) = contour_fixture();
        h.record_paths(
            5,
            vec![rect(9., -1., 13., 1.).to_vec()],
            false,
            rect(30., -10., 40., 10.).to_vec(),
        );
        assert_eq!(h.certify(1, vec![q.clone()]).receipts.len(), 1);
        let (mut h, _) = contour_fixture();
        h.record_bounds(5, Point::new(12., -1.), Point::new(13., 1.));
        assert_eq!(
            h.certify(1, vec![q]).receipts.len(),
            1,
            "mere endpoint touch is not positive-length erased ink"
        );
    }

    #[test]
    fn strict_edge_test_handles_unclipped_unknowns_and_only_actual_split_geometry() {
        let mut work = MAX_WORK;
        let event = Event {
            order: 5,
            bounds: [10.4, -0.1, 10.6, 0.1],
            paths: vec![rect(10.4, -0.1, 10.6, 0.1).to_vec()],
            clip: vec![],
            even_odd: false,
        };
        assert!(
            edge_event_hits(Point::new(10., 0.), Point::new(12., 0.), &event, &mut work).unwrap()
        );
        let (mut h, q) = contour_fixture();
        // An original source line may extend farther than its ring edge.
        // Ink beyond the selected closed ring is not part of this receipt.
        h.record_bounds(9, Point::new(9., -50.), Point::new(11., -40.));
        assert_eq!(h.certify(1, vec![q]).receipts.len(), 1);
    }

    #[test]
    fn compound_paint_internal_boundary_still_erases_actual_union_interior() {
        for even_odd in [false, true] {
            let (mut h, q) = contour_fixture();
            let outer = rect(10.4, -0.1, 10.6, 0.1).to_vec();
            let inner = rect(10.4, 0., 10.6, 0.05).to_vec();
            let mut paths = vec![outer, inner.clone()];
            if even_odd {
                paths.push(inner);
            }
            h.record_paths(5, paths, even_odd, rect(0., -10., 200., 200.).to_vec());
            assert!(
                h.certify(1, vec![q]).receipts.is_empty(),
                "internal compound boundary is painted on both sides, evenOdd={even_odd}"
            );
        }
    }

    #[test]
    fn contour_canonicalization_and_work_exhaustion_never_publish_partial_authority() {
        let (h, q) = contour_fixture();
        let mut reversed = q.clone();
        reversed.contour_edges.reverse();
        for edge in &mut reversed.contour_edges {
            std::mem::swap(&mut edge.start, &mut edge.end);
        }
        assert_eq!(
            h.clone()
                .certify(1, vec![q.clone(), reversed])
                .receipts
                .len(),
            1
        );
        let mut bad = q.clone();
        bad.contour_edges.push(ContourEdge {
            source_id: "unknown".into(),
            start: Point::new(0., 0.),
            end: Point::new(1., 0.),
        });
        assert!(h.clone().certify(1, vec![bad]).receipts.is_empty());
        let mut large = q.clone();
        large.contour_edges = vec![q.contour_edges[0].clone(); MAX_CONTOUR_EDGES + 1];
        let mut h = h;
        for _ in 0..4000 {
            h.record_bounds(9, Point::new(200., 200.), Point::new(201., 201.));
        }
        let out = h.certify(1, vec![q, large]);
        assert_eq!(out.status, VisibilityStatus::BudgetExhausted);
        assert!(out.receipts.is_empty());
    }
}
