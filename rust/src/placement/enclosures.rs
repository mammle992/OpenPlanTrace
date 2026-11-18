//! Exterior source rings include opening thresholds without adding placement solids.
use crate::{
    model::{PageScan, Point, Primitive, Shape, Wall, inside, on_segment, segments},
    placement::WallStatus,
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashMap, HashSet, VecDeque};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BoundaryEdge {
    pub start: Point,
    pub end: Point,
    pub kind: String,
    pub source_ids: Vec<String>,
    pub wall_ids: Vec<String>,
    pub evidence: String,
    pub requires_review: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExteriorEnclosure {
    pub id: String,
    pub page_number: u32,
    pub closed: bool,
    pub polygon: Vec<Point>,
    pub area_drawing_units_squared: f64,
    pub boundary_edges: Vec<BoundaryEdge>,
    pub boundary_ready_wall_ids: Vec<String>,
    pub requires_review: bool,
}

const MAX_SEGMENTS: usize = 2800;
const MAX_FACE_VERTICES: usize = 2048;

#[derive(Clone)]
struct SourceEdge {
    start: Point,
    end: Point,
    width: f64,
    ids: Vec<String>,
    threshold: bool,
}

struct Leaf {
    points: [Point; 4],
    axis: Point,
    length: f64,
    thickness: f64,
    id: String,
}

impl Leaf {
    fn new(p: &Primitive) -> Option<Self> {
        p.stroke_width?;
        let Shape::Polyline {
            points,
            closed: true,
            filled: false,
        } = &p.shape
        else {
            return None;
        };
        let points = if points.first() == points.last() {
            &points[..points.len().checked_sub(1)?]
        } else {
            points
        };
        let points: [Point; 4] = points.try_into().ok()?;
        let a = points[1] - points[0];
        let b = points[2] - points[1];
        let (long, short) = if a.norm() > b.norm() { (a, b) } else { (b, a) };
        let length = long.norm();
        if length < 4. * short.norm()
            || short.norm() == 0.
            || long.unit().dot(short.unit()).abs() > 0.05
            || (points[0] + points[2]).distance(points[1] + points[3]) > short.norm() * 0.2
        {
            return None;
        }
        Some(Self {
            points,
            axis: long.unit(),
            length,
            thickness: short.norm(),
            id: p.id.clone(),
        })
    }
}

fn leaf_witness(a: Point, b: Point, width: f64, leaves: &[Leaf]) -> Option<&Leaf> {
    let axis = (b - a).unit();
    let gap = a.distance(b);
    leaves.iter().find(|leaf| {
        leaf.length >= gap * 0.55
            && leaf.length <= gap * 1.35
            && leaf.thickness < width * 0.25
            && leaf.axis.dot(axis).abs() < 0.5
            && leaf
                .points
                .iter()
                .map(|p| p.distance(a).min(p.distance(b)))
                .fold(f64::INFINITY, f64::min)
                <= width * 2.
            && leaf
                .points
                .iter()
                .any(|p| axis.cross(*p - a).abs() > width * 1.2)
    })
}

fn cap_has_return(cap: &SourceEdge, ink: &[SourceEdge], ordinary: f64, eps: f64) -> bool {
    let axis = (cap.end - cap.start).unit();
    [cap.start, cap.end].into_iter().all(|p| {
        ink.iter().any(|s| {
            s.width > ordinary * 1.25
                && s.start.distance(s.end) > cap.width * 4.
                && (s.end - s.start).unit().dot(axis).abs() < 1e-5
                && (s.start.distance(p) <= eps || s.end.distance(p) <= eps)
        })
    })
}

fn thresholds(
    ink: &[SourceEdge],
    primitives: &[Primitive],
    ordinary: f64,
    eps: f64,
) -> (Vec<SourceEdge>, HashSet<String>) {
    let caps: Vec<_> = ink
        .iter()
        .filter(|s| {
            let len = s.start.distance(s.end);
            s.width > ordinary * 1.25
                && len >= 8. * s.width
                && len <= 32. * s.width
                && cap_has_return(s, ink, ordinary, eps)
        })
        .collect();
    let leaves: Vec<_> = primitives.iter().filter_map(Leaf::new).collect();
    if caps.len() > 256 || leaves.len() > 512 {
        return (vec![], HashSet::new());
    }
    let mut out = vec![];
    let mut used_leaves = HashSet::new();
    for (i, a) in caps.iter().enumerate() {
        for b in caps.iter().skip(i + 1) {
            let width = (a.start.distance(a.end) + b.start.distance(b.end)) * 0.5;
            if (a.start.distance(a.end) - b.start.distance(b.end)).abs() > width * 0.03 {
                continue;
            }
            let axis = (a.end - a.start).unit();
            if axis.cross((b.end - b.start).unit()).abs() > 1e-5 {
                continue;
            }
            let displacement = (b.start + b.end - a.start - a.end) * 0.5;
            let normal = axis.cross(displacement).abs();
            let along = axis.dot(displacement).abs();
            let mut joins = vec![];
            if normal <= eps {
                let (p, q) = [a.start, a.end]
                    .into_iter()
                    .flat_map(|p| [b.start, b.end].into_iter().map(move |q| (p, q)))
                    .min_by(|(p, q), (r, s)| p.distance(*q).total_cmp(&r.distance(*s)))
                    .unwrap();
                joins.push((p, q));
            } else if along <= width * 0.03 {
                for p in [a.start, a.end] {
                    let q = [b.start, b.end]
                        .into_iter()
                        .min_by(|q, r| (axis.dot(*q - p)).abs().total_cmp(&axis.dot(*r - p).abs()))
                        .unwrap();
                    joins.push((p, q));
                }
            }
            let Some(&(p, q)) = joins.first() else {
                continue;
            };
            let gap = p.distance(q);
            if gap < width * 1.5 || gap > width * 10. {
                continue;
            }
            let Some(leaf) = leaf_witness(p, q, width, &leaves) else {
                continue;
            };
            // A doorway threshold cannot replace an already drawn wall face.
            if joins.iter().any(|(p, q)| {
                ink.iter().any(|s| {
                    s.width > ordinary * 1.25
                        && on_segment(*p, s.start, s.end).0.distance(*p) <= eps
                        && on_segment(*q, s.start, s.end).0.distance(*q) <= eps
                })
            }) {
                continue;
            }
            let mut ids = a.ids.clone();
            ids.extend(b.ids.iter().cloned());
            ids.push(leaf.id.clone());
            ids.sort();
            ids.dedup();
            used_leaves.insert(leaf.id.clone());
            out.extend(joins.into_iter().map(|(start, end)| SourceEdge {
                start,
                end,
                width: 0.,
                ids: ids.clone(),
                threshold: true,
            }));
        }
    }
    (out, used_leaves)
}

struct Graph {
    points: Vec<Point>,
    adjacent: Vec<Vec<usize>>,
    owners: HashMap<(usize, usize), Vec<usize>>,
}

fn key(a: usize, b: usize) -> (usize, usize) {
    (a.min(b), a.max(b))
}

impl Graph {
    fn new(ink: &[SourceEdge], eps: f64) -> Self {
        let mut points = Vec::<Point>::new();
        let mut buckets = HashMap::<(i64, i64), Vec<usize>>::new();
        let mut owners = HashMap::<(usize, usize), Vec<usize>>::new();
        let mut index = |p: Point| {
            let cell = ((p.x / eps).floor() as i64, (p.y / eps).floor() as i64);
            for dx in -1..=1 {
                for dy in -1..=1 {
                    if let Some(ids) =
                        buckets.get(&(cell.0.saturating_add(dx), cell.1.saturating_add(dy)))
                    {
                        if let Some(&id) = ids.iter().find(|&&i| points[i].distance(p) <= eps) {
                            return id;
                        }
                    }
                }
            }
            let id = points.len();
            points.push(p);
            buckets.entry(cell).or_default().push(id);
            id
        };
        let ends: Vec<_> = ink.iter().flat_map(|s| [s.start, s.end]).collect();
        for (i, s) in ink.iter().enumerate() {
            let mut cuts = vec![(0., s.start), (1., s.end)];
            let lower = Point::new(s.start.x.min(s.end.x) - eps, s.start.y.min(s.end.y) - eps);
            let upper = Point::new(s.start.x.max(s.end.x) + eps, s.start.y.max(s.end.y) + eps);
            for &p in &ends {
                if p.x < lower.x || p.y < lower.y || p.x > upper.x || p.y > upper.y {
                    continue;
                }
                let (q, t) = on_segment(p, s.start, s.end);
                if t > 0. && t < 1. && p.distance(q) <= eps {
                    cuts.push((t, p));
                }
            }
            cuts.sort_by(|a, b| a.0.total_cmp(&b.0));
            for row in cuts.windows(2) {
                let a = index(row[0].1);
                let b = index(row[1].1);
                if a != b {
                    owners.entry(key(a, b)).or_default().push(i);
                }
            }
        }
        let mut adjacent = vec![vec![]; points.len()];
        for &(a, b) in owners.keys() {
            adjacent[a].push(b);
            adjacent[b].push(a);
        }
        // A dangling annotation or open leaf cannot contribute an area boundary.
        let mut todo: VecDeque<_> = adjacent
            .iter()
            .enumerate()
            .filter(|(_, ns)| ns.len() < 2)
            .map(|(i, _)| i)
            .collect();
        while let Some(a) = todo.pop_front() {
            for b in std::mem::take(&mut adjacent[a]) {
                adjacent[b].retain(|&i| i != a);
                if adjacent[b].len() == 1 {
                    todo.push_back(b);
                }
            }
        }
        for (i, ns) in adjacent.iter_mut().enumerate() {
            ns.sort_by(|&a, &b| {
                (points[a] - points[i])
                    .y
                    .atan2((points[a] - points[i]).x)
                    .total_cmp(&(points[b] - points[i]).y.atan2((points[b] - points[i]).x))
                    .then(a.cmp(&b))
            });
        }
        Self {
            points,
            adjacent,
            owners,
        }
    }

    fn exteriors(&self) -> Vec<Vec<usize>> {
        let mut seen = HashSet::new();
        let mut out = vec![];
        for (u, ns) in self.adjacent.iter().enumerate() {
            for &v in ns {
                if seen.contains(&(u, v)) {
                    continue;
                }
                let mut walk = vec![];
                let (mut a, mut b) = (u, v);
                loop {
                    if !seen.insert((a, b)) || walk.len() > MAX_FACE_VERTICES {
                        break;
                    }
                    walk.push(a);
                    let ns = &self.adjacent[b];
                    let at = ns.iter().position(|&x| x == a).unwrap();
                    let c = ns[(at + ns.len() - 1) % ns.len()];
                    a = b;
                    b = c;
                    if (a, b) == (u, v) {
                        break;
                    }
                }
                if (a, b) != (u, v)
                    || walk.len() < 3
                    || walk.iter().collect::<HashSet<_>>().len() != walk.len()
                {
                    continue;
                }
                let origin = self.points[walk[0]];
                let signed: f64 = walk
                    .iter()
                    .zip(walk.iter().cycle().skip(1))
                    .map(|(&a, &b)| (self.points[a] - origin).cross(self.points[b] - origin) * 0.5)
                    .sum();
                if signed < 0. {
                    walk.reverse();
                    out.push(walk);
                }
            }
        }
        out
    }
}

fn wall_owners<'a>(
    a: Point,
    b: Point,
    page: &'a PageScan,
    ids: &BTreeSet<String>,
    eps: f64,
) -> Vec<&'a Wall> {
    let axis = (b - a).unit();
    let mid = (a + b) * 0.5;
    page.walls
        .iter()
        .filter(|w| {
            w.curve.is_none()
                && w.source_ids.iter().any(|id| ids.contains(id))
                && axis.cross((w.end - w.start).unit()).abs() < 1e-5
                && on_segment(mid, w.start, w.end).0.distance(mid) <= w.thickness * 0.5 + eps
        })
        .collect()
}

fn simple_ring(points: &[Point], eps: f64) -> bool {
    for i in 0..points.len() {
        let (a, b) = (points[i], points[(i + 1) % points.len()]);
        for j in i + 2..points.len() {
            if i == 0 && j + 1 == points.len() {
                continue;
            }
            let (c, d) = (points[j], points[(j + 1) % points.len()]);
            if a.x.min(b.x) > c.x.max(d.x) + eps
                || c.x.min(d.x) > a.x.max(b.x) + eps
                || a.y.min(b.y) > c.y.max(d.y) + eps
                || c.y.min(d.y) > a.y.max(b.y) + eps
            {
                continue;
            }
            if [a, b]
                .iter()
                .any(|&p| on_segment(p, c, d).0.distance(p) <= eps)
                || [c, d]
                    .iter()
                    .any(|&p| on_segment(p, a, b).0.distance(p) <= eps)
            {
                return false;
            }
            let u = b - a;
            let v = d - c;
            let cross = u.cross(v);
            if cross.abs() > eps * u.norm().max(v.norm()) {
                let t = (c - a).cross(v) / cross;
                let q = (c - a).cross(u) / cross;
                if t > 0. && t < 1. && q > 0. && q < 1. {
                    return false;
                }
            }
        }
    }
    true
}

pub fn build(page: &PageScan) -> Vec<ExteriorEnclosure> {
    let view = crate::placement::effective_page(page);
    build_measured(&view)
}

fn build_measured(page: &PageScan) -> Vec<ExteriorEnclosure> {
    let ready_ids: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.wall_id.as_str())
        .collect();
    let ready: Vec<_> = page
        .walls
        .iter()
        .filter(|w| ready_ids.contains(w.id.as_str()))
        .collect();
    if ready.is_empty() {
        return vec![];
    }
    let mut widths: Vec<_> = page
        .primitives
        .iter()
        .filter_map(|p| p.stroke_width)
        .collect();
    if widths.is_empty() {
        return vec![];
    }
    widths.sort_by(f64::total_cmp);
    let ordinary = widths[widths.len() / 2];
    let eps = (ordinary * 0.02).max(f64::EPSILON);
    let body = ready.iter().map(|w| w.thickness).fold(0., f64::max);
    let pad = body * 12.;
    let lower = Point::new(
        ready
            .iter()
            .flat_map(|w| [w.start.x, w.end.x])
            .fold(f64::INFINITY, f64::min)
            - pad,
        ready
            .iter()
            .flat_map(|w| [w.start.y, w.end.y])
            .fold(f64::INFINITY, f64::min)
            - pad,
    );
    let upper = Point::new(
        ready
            .iter()
            .flat_map(|w| [w.start.x, w.end.x])
            .fold(f64::NEG_INFINITY, f64::max)
            + pad,
        ready
            .iter()
            .flat_map(|w| [w.start.y, w.end.y])
            .fold(f64::NEG_INFINITY, f64::max)
            + pad,
    );
    let mut ink = vec![];
    for p in &page.primitives {
        let Some(width) = p.stroke_width else {
            continue;
        };
        if !matches!(
            p.shape,
            Shape::Line { .. } | Shape::Polyline { filled: false, .. }
        ) {
            continue;
        }
        for (start, end) in segments(&p.shape) {
            if [start, end]
                .into_iter()
                .all(|p| p.x >= lower.x && p.y >= lower.y && p.x <= upper.x && p.y <= upper.y)
                && start.distance(end) > eps * 0.001
            {
                if ink.len() == MAX_SEGMENTS {
                    return vec![];
                }
                ink.push(SourceEdge {
                    start,
                    end,
                    width,
                    ids: vec![p.id.clone()],
                    threshold: false,
                });
                if ink.len() > MAX_SEGMENTS {
                    return vec![];
                }
            }
        }
    }
    let (portals, leaves) = thresholds(&ink, &page.primitives, ordinary, eps);
    ink.retain(|s| s.ids.iter().all(|id| !leaves.contains(id)));
    ink.extend(portals);
    if ink.len() > MAX_SEGMENTS {
        return vec![];
    }
    let graph = Graph::new(&ink, eps);
    let mut out = vec![];
    for ring in graph.exteriors() {
        let polygon: Vec<_> = ring.iter().map(|&i| graph.points[i]).collect();
        let origin = polygon[0];
        let area: f64 = polygon
            .iter()
            .zip(polygon.iter().cycle().skip(1))
            .map(|(a, b)| (*a - origin).cross(*b - origin) * 0.5)
            .sum();
        if area < 16. * body * body || !simple_ring(&polygon, eps) {
            continue;
        }
        let inside_ready: Vec<_> = ready
            .iter()
            .filter(|w| inside((w.start + w.end) * 0.5, &polygon))
            .collect();
        if inside_ready.is_empty() {
            continue;
        }
        let mut boundary = vec![];
        let mut total = 0.;
        let mut heavy = 0.;
        let mut boundary_ready = BTreeSet::new();
        for (&a, &b) in ring.iter().zip(ring.iter().cycle().skip(1)) {
            let entries = &graph.owners[&key(a, b)];
            let (start, end) = (graph.points[a], graph.points[b]);
            let ids: BTreeSet<_> = entries
                .iter()
                .flat_map(|&i| ink[i].ids.iter().cloned())
                .collect();
            let threshold = entries.iter().any(|&i| ink[i].threshold);
            let strong = entries.iter().any(|&i| ink[i].width > ordinary * 1.25);
            let walls = wall_owners(start, end, page, &ids, eps);
            let wall_ids: Vec<_> = walls.iter().map(|w| w.id.as_str()).collect();
            boundary_ready.extend(
                wall_ids
                    .iter()
                    .filter(|id| ready_ids.contains(**id))
                    .copied(),
            );
            let length = start.distance(end);
            total += length;
            if strong {
                heavy += length;
            }
            let kind = if threshold {
                "opening"
            } else if !strong || walls.is_empty() {
                "review"
            } else {
                "wall"
            };
            boundary.push(BoundaryEdge {
                start,
                end,
                kind: kind.to_owned(),
                source_ids: ids.into_iter().collect(),
                wall_ids: wall_ids.into_iter().map(str::to_owned).collect(),
                evidence: if threshold {
                    "capped_gap_with_open_leaf"
                } else if !strong {
                    "source_outline_path"
                } else {
                    "source_wall_face"
                }
                .to_owned(),
                requires_review: true,
            });
        }
        // An open building's wall ribbon is itself a closed thin polygon. Its
        // material area must never be reported as the enclosed house interior.
        if heavy < total * 0.4 || boundary_ready.is_empty() || area < total * body * 2. {
            continue;
        }
        out.push(ExteriorEnclosure {
            id: format!("p{}:exterior-enclosure:{}", page.page_number, out.len() + 1),
            page_number: page.page_number,
            closed: true,
            polygon,
            area_drawing_units_squared: area,
            boundary_edges: boundary,
            boundary_ready_wall_ids: boundary_ready.into_iter().map(str::to_owned).collect(),
            requires_review: true,
        });
    }
    out
}
