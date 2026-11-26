//! A source-linked connection graph, separate from physical wall material.
//!
//! Only ready, finite straight wall bodies participate. Openings remain logical
//! threshold edges; no swing, image crop, or nearest-end guess creates material.
use crate::model::{Opening, OpeningEvidence, OpeningKind, PageScan, Point, Wall, on_segment};
use crate::placement::{WallReason, WallStatus};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};

mod authored;
mod exterior_boundaries;
mod exterior_finite_contacts;
mod exterior_paint_joints;
mod exterior_thresholds;
mod finite_paint_contacts;
mod nominal_axis;
mod painted_contact;
mod stepped_jambs;

const MAX_WALLS: usize = 12_000;
const MAX_SPANS: usize = 40_000;
const MAX_EDGES: usize = 100_000;
const MAX_WORK: usize = 16_000_000;
const MAX_FACE_VERTICES: usize = 4096;
const MAX_GAPS: usize = 4096;
const BODY_CONTACT: &str = "finite_aperture_clipped_wall_material_contact";
pub(crate) const STEPPED_APERTURE_THRESHOLD: &str = stepped_jambs::THRESHOLD;
pub(crate) const STEPPED_APERTURE_JAMB: &str = stepped_jambs::JAMB;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionPass {
    pub revision: u32,
    pub page_number: u32,
    pub nodes: Vec<ConnectionNode>,
    pub edges: Vec<ConnectionEdge>,
    pub components: Vec<ConnectionComponent>,
    pub boundary_candidates: Vec<BoundaryCandidate>,
    pub uncertain_gaps: Vec<UncertainGap>,
    pub omitted_walls: Vec<OmittedWall>,
    pub diagnostics: Vec<String>,
    pub requires_review: bool,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionNode {
    pub id: String,
    pub position: Point,
    pub wall_ids: Vec<String>,
    pub opening_ids: Vec<String>,
    pub degree: usize,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ConnectionKind {
    Wall,
    MaterialContact,
    MeasuredCorner,
    OpeningJambContact,
    DoorThreshold,
    WindowThreshold,
    SectionMarkContact,
    RasterShoulderContact,
    RasterCourseContact,
    RecessedStockContact,
    RasterCornerSeat,
    MaskedStockContact,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionEdge {
    pub id: String,
    pub start_node_id: String,
    pub end_node_id: String,
    pub kind: ConnectionKind,
    pub wall_ids: Vec<String>,
    pub opening_ids: Vec<String>,
    pub source_ids: Vec<String>,
    pub evidence: String,
    pub contributes_wall_material: bool,
    pub adds_wall_material: bool,
    pub requires_review: bool,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionComponent {
    pub id: String,
    pub node_ids: Vec<String>,
    pub edge_ids: Vec<String>,
    pub wall_ids: Vec<String>,
    /// Every node has degree two: an unbranched cycle. Branching graphs can
    /// contain separately reported boundaries; neither is a completed-house claim.
    pub closed: bool,
    pub branching: bool,
    pub requires_review: bool,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BoundaryCandidate {
    pub id: String,
    pub node_ids: Vec<String>,
    pub edge_ids: Vec<String>,
    pub polygon: Vec<Point>,
    pub area_drawing_units_squared: f64,
    pub area_basis: String,
    pub wall_ids: Vec<String>,
    pub opening_ids: Vec<String>,
    pub exterior_status: String,
    pub requires_review: bool,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UncertainGap {
    pub id: String,
    pub start_node_id: String,
    pub end_node_id: Option<String>,
    pub start: Point,
    pub end: Option<Point>,
    pub distance: Option<f64>,
    pub reason: String,
    pub wall_ids: Vec<String>,
    pub opening_ids: Vec<String>,
    pub source_ids: Vec<String>,
    pub connected: bool,
    pub requires_review: bool,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OmittedWall {
    pub wall_id: String,
    pub reason: String,
}

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("connection pass work budget exceeded")?;
    Ok(())
}
fn ids(values: impl IntoIterator<Item = String>) -> Vec<String> {
    values
        .into_iter()
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}
fn epsilon(page: &PageScan) -> f64 {
    [page.width, page.height]
        .into_iter()
        .chain(
            page.walls
                .iter()
                .flat_map(|w| [w.start.x, w.start.y, w.end.x, w.end.y]),
        )
        .map(f64::abs)
        .fold(1., f64::max)
        * 128.
        * f64::EPSILON
}
fn point_key(p: Point) -> (u64, u64) {
    let bit = |v: f64| if v == 0. { 0 } else { v.to_bits() };
    (bit(p.x), bit(p.y))
}

#[derive(Clone)]
struct Solid {
    a: Point,
    b: Point,
    axis_offset: Point,
    half: f64,
    wall: usize,
    cuts: Vec<f64>,
}
impl Solid {
    fn axis(&self) -> Point {
        (self.b - self.a).unit()
    }
    fn length(&self) -> f64 {
        self.a.distance(self.b)
    }
    fn at(&self, t: f64) -> Point {
        if t <= 0. {
            self.a
        } else if t >= 1. {
            self.b
        } else {
            self.a + (self.b - self.a) * t
        }
    }
    fn project(&self, p: Point) -> (Point, f64) {
        on_segment(p, self.a, self.b)
    }
    fn rect(&self) -> Rect {
        Rect {
            a: self.a - self.axis_offset,
            b: self.b - self.axis_offset,
            half: self.half,
        }
    }
    fn cut(&mut self, p: Point) -> f64 {
        let (_, t) = self.project(p);
        self.cuts.push(t);
        t
    }
}
#[derive(Clone, Copy)]
struct Rect {
    a: Point,
    b: Point,
    half: f64,
}
impl Rect {
    /// Exact finite-body clipping of a segment; no endpoint proximity allowance.
    fn clip(&self, a: Point, b: Point, eps: f64) -> Option<(f64, f64)> {
        let axis = (self.b - self.a).unit();
        let len = self.a.distance(self.b);
        if len <= eps || self.half < 0. {
            return None;
        }
        let p = a - self.a;
        let d = b - a;
        let mut lo: f64 = 0.;
        let mut hi: f64 = 1.;
        for (start, delta, min, max) in [
            (p.dot(axis), d.dot(axis), 0., len),
            (p.cross(axis), d.cross(axis), -self.half, self.half),
        ] {
            if delta.abs() <= eps {
                if start < min - eps || start > max + eps {
                    return None;
                }
            } else {
                let x = (min - start) / delta;
                let y = (max - start) / delta;
                lo = lo.max(x.min(y));
                hi = hi.min(x.max(y));
                if hi < lo - eps {
                    return None;
                }
            }
        }
        (hi >= lo - eps).then_some((lo.clamp(0., 1.), hi.clamp(0., 1.)))
    }
    fn contains(&self, p: Point, eps: f64) -> bool {
        self.clip(p, p, eps).is_some()
    }
    fn bbox(&self) -> [f64; 4] {
        let u = (self.b - self.a).unit();
        let n = Point::new(-u.y, u.x) * self.half;
        let ps = [self.a + n, self.a - n, self.b + n, self.b - n];
        [
            ps.iter().map(|p| p.x).fold(f64::INFINITY, f64::min),
            ps.iter().map(|p| p.y).fold(f64::INFINITY, f64::min),
            ps.iter().map(|p| p.x).fold(f64::NEG_INFINITY, f64::max),
            ps.iter().map(|p| p.y).fold(f64::NEG_INFINITY, f64::max),
        ]
    }
}
struct Aperture<'a> {
    opening: &'a Opening,
    rect: Rect,
    framed_door: Option<FramedDoor>,
}

pub(crate) const FRAMED_DOOR_THRESHOLD: &str = "source_bounded_framed_door_threshold";

struct FramedDoor {
    host_ids: [String; 2],
    source_ids: Vec<String>,
}

/// The preserved swing measures the leaf lane. Two independently replayed
/// exterior caps can supply its actual material-to-material aperture.
fn framed_door(o: &Opening, hosts: &[&Wall], eps: f64) -> Option<(Rect, FramedDoor)> {
    let [first, second] = hosts else { return None };
    if o.kind != OpeningKind::Door
        || o.evidence != OpeningEvidence::FiniteJambSwing
        || o.swing_observations.len() != 1
        || first.id == second.id
        || !o.start.finite()
        || !o.end.finite()
        || o.start.distance(o.end) <= eps
    {
        return None;
    }
    let axis = (o.end - o.start).unit();
    let reach = o.start.distance(o.end);
    let local = |p: Point| (p - o.start).dot(axis);
    let caps = |w: &Wall| {
        let span = w.end - w.start;
        let half = w.thickness * 0.5;
        if w.curve.is_some()
            || !w.start.finite()
            || !w.end.finite()
            || !half.is_finite()
            || half <= eps
            || span.norm() <= eps
            || span.cross(axis).abs() > eps
            || (o.start - w.start).cross(axis).abs() > half + eps
            || (o.end - w.start).cross(axis).abs() > half + eps
        {
            return None;
        }
        let (a, b) = (local(w.start), local(w.end));
        if a.max(b) <= eps && a.max(b) >= -w.thickness * 0.85 - eps {
            Some((0, if a >= b { w.start } else { w.end }))
        } else if a.min(b) >= reach - eps && a.min(b) <= reach + w.thickness * 0.85 + eps {
            Some((1, if a <= b { w.start } else { w.end }))
        } else {
            None
        }
    };
    let (side, a) = caps(first)?;
    let (other, b) = caps(second)?;
    if side == other || (b - a).cross(axis).abs() > eps {
        return None;
    }
    let (start, end) = if side == 0 { (a, b) } else { (b, a) };
    let host_ids = if side == 0 {
        [first.id.clone(), second.id.clone()]
    } else {
        [second.id.clone(), first.id.clone()]
    };
    Some((
        Rect {
            a: start,
            b: end,
            half: first.thickness.max(second.thickness) * 0.5,
        },
        FramedDoor {
            host_ids,
            source_ids: ids(o
                .source_ids
                .iter()
                .chain(&first.source_ids)
                .chain(&second.source_ids)
                .cloned()),
        },
    ))
}

// The measured sill belongs to the opening. The logical threshold uses the
// two verified wall-axis endpoints, which can be inset from that sill.
fn finite_capped_wall_axis(
    opening: &Opening,
    hosts: &[&Wall],
    eps: f64,
) -> Option<(Rect, FramedDoor)> {
    let [a, b] = hosts else { return None };
    let [first, second] = opening.host_wall_ids.as_slice() else {
        return None;
    };
    let [left, right] = if &a.id == first && &b.id == second {
        [*a, *b]
    } else if &b.id == first && &a.id == second {
        [*b, *a]
    } else {
        return None;
    };
    let span = opening.end - opening.start;
    if !opening.start.finite() || !opening.end.finite() || span.norm() <= eps {
        return None;
    }
    let axis = span.unit();
    let cap = |wall: &Wall, sill: Point, approach: Point| {
        let [a, b] = [wall.start, wall.end];
        let [x, y] = [a, b].map(|p| (p - sill).dot(approach));
        let p = if x >= y { a } else { b };
        (wall.curve.is_none()
            && wall.thickness.is_finite()
            && wall.thickness > eps
            && (b - a).cross(axis).abs() <= eps
            && x.max(y).abs() <= eps
            && (p - sill).cross(axis).abs() <= 2. * wall.thickness + eps)
            .then_some(p)
    };
    let start = cap(left, opening.start, axis)?;
    let end = cap(right, opening.end, axis * -1.)?;
    if (end - start).cross(axis).abs() > eps {
        return None;
    }
    Some((
        Rect {
            a: start,
            b: end,
            half: left.thickness.max(right.thickness) * 0.5,
        },
        FramedDoor {
            host_ids: [first.clone(), second.clone()],
            source_ids: ids(opening
                .source_ids
                .iter()
                .chain(&left.source_ids)
                .chain(&right.source_ids)
                .cloned()),
        },
    ))
}

fn raster_jamb_axis(opening: &Opening, hosts: &[&Wall], eps: f64) -> Option<(Rect, FramedDoor)> {
    let [a, b] = hosts else { return None };
    let length = opening.start.distance(opening.end);
    if length <= eps || !length.is_finite() || opening.kind != OpeningKind::Door {
        return None;
    }
    let axis = (opening.end - opening.start).unit();
    let at_cap = |wall: &Wall, p: Point, approach: Point| -> Option<f64> {
        let direction = wall.end - wall.start;
        let half = wall.thickness * 0.5;
        if wall.curve.is_some()
            || !half.is_finite()
            || half <= eps
            || direction.cross(axis).abs() > eps
        {
            return None;
        }
        let [x, y] = [wall.start, wall.end].map(|q| (q - p).dot(approach));
        let offset = (wall.start - p).cross(axis).abs();
        (x.max(y).abs() <= eps && x.min(y) < -eps && offset <= half + eps).then_some(half + offset)
    };
    let match_caps = |left: &Wall, right: &Wall| {
        let l = at_cap(left, opening.start, axis)?;
        let r = at_cap(right, opening.end, axis * -1.)?;
        Some((
            Rect {
                a: opening.start,
                b: opening.end,
                half: l.max(r),
            },
            FramedDoor {
                host_ids: [left.id.clone(), right.id.clone()],
                source_ids: ids(opening
                    .source_ids
                    .iter()
                    .chain(&left.source_ids)
                    .chain(&right.source_ids)
                    .cloned()),
            },
        ))
    };
    match_caps(a, b).or_else(|| match_caps(b, a))
}

fn crosses_aperture(
    a: Point,
    b: Point,
    apertures: &[Aperture],
    except: Option<&str>,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    charge(work, apertures.len())?;
    let length = a.distance(b);
    if length <= eps {
        return Ok(false);
    }
    Ok(apertures.iter().any(|p| {
        if except == Some(p.opening.id.as_str()) {
            return false;
        }
        let Some((lo, hi)) = p.rect.clip(a, b, 0.) else {
            return false;
        };
        // The jamb plane is a legitimate attachment. Positive longitudinal
        // penetration into a finite aperture is not material contact.
        if p.rect.half > 0. && (hi - lo) * length <= eps {
            return false;
        }
        let mid = a + (b - a) * ((lo + hi) * 0.5);
        let axis = (p.rect.b - p.rect.a).unit();
        let t = (mid - p.rect.a).dot(axis);
        t > eps && t < p.rect.a.distance(p.rect.b) - eps
    }))
}

#[derive(Clone)]
struct Link {
    a: Point,
    b: Point,
    kind: ConnectionKind,
    wall_ids: Vec<String>,
    opening_ids: Vec<String>,
    source_ids: Vec<String>,
    evidence: &'static str,
    // A graph endpoint cut is resolved only on its proven incident solid.
    anchors: [Option<usize>; 2],
}

struct SourcePath {
    wall_ids: [String; 2],
    path: Vec<Point>,
    source_ids: Vec<String>,
    kind: ConnectionKind,
    evidence: &'static str,
}

impl SourcePath {
    fn facade(p: super::stepped_facade::Joint, evidence: &'static str) -> Self {
        let [start, end] = p.points;
        Self {
            wall_ids: p.wall_ids,
            path: [start]
                .into_iter()
                .chain(p.support_points)
                .chain([end])
                .collect(),
            source_ids: p.source_ids,
            kind: ConnectionKind::MeasuredCorner,
            evidence,
        }
    }
}
/// Reconcile only endpoints joined by proven source incidence. A near-zero
/// material/jamb connector can have differently rounded projections on its two
/// incident solids; selecting the first wall's cut would strand the other wall.
/// This union never searches for a nearby node or joins an unsupported gap.
fn incidence_aliases(
    links: &[Link],
    eps: f64,
    work: &mut usize,
) -> Result<HashMap<(u64, u64), Point>, String> {
    let mut points = BTreeSet::new();
    let mut joins = vec![];
    for link in links {
        charge(work, 1)?;
        if link.a.distance(link.b) <= eps {
            let a = point_key(link.a);
            let b = point_key(link.b);
            points.insert(a);
            points.insert(b);
            joins.push((a, b));
        }
    }
    let points: Vec<_> = points.into_iter().collect();
    let indexes: HashMap<_, _> = points.iter().enumerate().map(|(i, &p)| (p, i)).collect();
    let mut parents: Vec<_> = (0..points.len()).collect();
    let mut ranks = vec![0u8; points.len()];
    let mut canonical: Vec<_> = (0..points.len()).collect();
    fn root(parents: &mut [usize], mut i: usize) -> usize {
        let mut r = i;
        while parents[r] != r {
            r = parents[r];
        }
        while parents[i] != r {
            let next = parents[i];
            parents[i] = r;
            i = next;
        }
        r
    }
    for (a, b) in joins {
        charge(work, 1)?;
        let (mut a, mut b) = (
            root(&mut parents, indexes[&a]),
            root(&mut parents, indexes[&b]),
        );
        if a == b {
            continue;
        }
        if ranks[a] < ranks[b] {
            std::mem::swap(&mut a, &mut b);
        }
        parents[b] = a;
        canonical[a] = canonical[a].min(canonical[b]);
        if ranks[a] == ranks[b] {
            ranks[a] += 1;
        }
    }
    Ok(points
        .iter()
        .enumerate()
        .map(|(i, &p)| {
            let q = points[canonical[root(&mut parents, i)]];
            (p, Point::new(f64::from_bits(q.0), f64::from_bits(q.1)))
        })
        .collect())
}
fn push_link(links: &mut Vec<Link>, link: Link) -> Result<(), String> {
    if links.len() >= MAX_EDGES {
        return Err("connection link budget exceeded".into());
    }
    links.push(link);
    Ok(())
}
fn covered_by_union(a: Point, b: Point, rects: &[Rect], eps: f64) -> bool {
    if a.distance(b) <= eps {
        return rects.iter().any(|r| r.contains(a, eps));
    }
    let mut spans: Vec<_> = rects.iter().filter_map(|r| r.clip(a, b, eps)).collect();
    spans.sort_by(|x, y| x.0.total_cmp(&y.0));
    let mut end: f64 = 0.;
    let tol = eps / a.distance(b);
    for (lo, hi) in spans {
        if lo > end + tol {
            return false;
        }
        end = end.max(hi);
    }
    end >= 1. - tol
}
fn polygon_interval(a: Point, b: Point, polygon: &[Point], eps: f64) -> Option<(f64, f64)> {
    if polygon.len() < 3 || polygon.iter().any(|p| !p.finite()) {
        return None;
    }
    let area: f64 = polygon
        .iter()
        .zip(polygon.iter().cycle().skip(1))
        .map(|(p, q)| p.cross(*q))
        .sum();
    if area.abs() <= eps * eps {
        return None;
    }
    let sign = area.signum();
    let mut lo: f64 = 0.;
    let mut hi: f64 = 1.;
    for (&p, &q) in polygon.iter().zip(polygon.iter().cycle().skip(1)) {
        let edge = q - p;
        let at = sign * edge.cross(a - p);
        let delta = sign * edge.cross(b - a);
        let margin = eps * edge.norm();
        if delta.abs() <= margin {
            if at < -margin {
                return None;
            }
        } else {
            let t = (-margin - at) / delta;
            if delta > 0. {
                lo = lo.max(t);
            } else {
                hi = hi.min(t);
            }
            if hi < lo {
                return None;
            }
        }
    }
    (hi >= lo).then_some((lo.clamp(0., 1.), hi.clamp(0., 1.)))
}
fn covered_by_joint(a: Point, b: Point, rects: &[Rect], polygon: &[Point], eps: f64) -> bool {
    if a.distance(b) <= eps {
        return rects.iter().any(|r| r.contains(a, eps))
            || polygon_interval(a, a, polygon, eps).is_some();
    }
    let mut intervals: Vec<_> = rects.iter().filter_map(|r| r.clip(a, b, eps)).collect();
    intervals.extend(polygon_interval(a, b, polygon, eps));
    intervals.sort_by(|a, b| a.0.total_cmp(&b.0));
    let mut end: f64 = 0.;
    let tol = eps / a.distance(b);
    for (lo, hi) in intervals {
        if lo > end + tol {
            return false;
        }
        end = end.max(hi);
    }
    end >= 1. - tol
}
fn rings_near(a: &[Point], b: &[Point], tol: f64) -> bool {
    let directed = |p: &[Point], q: &[Point]| {
        p.iter().zip(p.iter().cycle().skip(1)).all(|(&a, &b)| {
            [a, (a + b) * 0.5].iter().all(|&p| {
                q.iter()
                    .zip(q.iter().cycle().skip(1))
                    .any(|(&c, &d)| on_segment(p, c, d).0.distance(p) <= tol)
            })
        })
    };
    directed(a, b) && directed(b, a)
}
fn contact(a: &Solid, b: &Solid, eps: f64) -> Option<(Point, Point)> {
    let u = a.axis();
    let v = b.axis();
    let den = u.cross(v);
    let (p, q) = if den.abs() > 1e-10 {
        let at = a.a + u * ((b.a - a.a).cross(v) / den);
        (a.project(at).0, b.project(at).0)
    } else {
        let x = (b.a - a.a).dot(u);
        let y = (b.b - a.a).dot(u);
        let lo = 0_f64.max(x.min(y));
        let hi = a.length().min(x.max(y));
        if hi < lo - eps {
            return None;
        }
        let p = a.a + u * ((lo + hi) * 0.5);
        (p, b.project(p).0)
    };
    covered_by_union(p, q, &[a.rect(), b.rect()], eps).then_some((p, q))
}

fn shared_terminal_corner(a: &Solid, b: &Solid, eps: f64) -> Option<Point> {
    if a.axis().dot(b.axis()).abs() > 1e-5 {
        return None;
    }
    let corners = |s: &Solid| {
        let u = s.axis();
        let off = Point::new(-u.y, u.x) * s.half;
        let body = s.rect();
        [body.a + off, body.a - off, body.b + off, body.b - off]
    };
    let other = corners(b);
    let points = corners(a);
    let mut shared = points.into_iter().filter(|p| {
        other.iter().any(|q| q.distance(*p) <= eps)
            && a.rect().contains(*p, eps)
            && b.rect().contains(*p, eps)
    });
    let point = shared.next()?;
    shared.next().is_none().then_some(point)
}

struct Builder {
    page: u32,
    nodes: Vec<ConnectionNode>,
    edges: Vec<ConnectionEdge>,
    at: HashMap<(u64, u64), usize>,
    seen: BTreeMap<(usize, usize, u8, Vec<String>), usize>,
    aliases: HashMap<(u64, u64), Point>,
}
impl Builder {
    fn new(page: u32) -> Self {
        Self {
            page,
            nodes: vec![],
            edges: vec![],
            at: HashMap::new(),
            seen: BTreeMap::new(),
            aliases: HashMap::new(),
        }
    }
    fn node(&mut self, p: Point) -> usize {
        let p = self.aliases.get(&point_key(p)).copied().unwrap_or(p);
        *self.at.entry(point_key(p)).or_insert_with(|| {
            let i = self.nodes.len();
            self.nodes.push(ConnectionNode {
                id: format!("p{}:connection-node:{}", self.page, i + 1),
                position: p,
                wall_ids: vec![],
                opening_ids: vec![],
                degree: 0,
            });
            i
        })
    }
    fn edge(&mut self, link: Link) -> Result<(), String> {
        if !link.a.finite() || !link.b.finite() {
            return Err("connection edge has nonfinite source point".into());
        }
        let a = self.node(link.a);
        let b = self.node(link.b);
        let key = (
            a.min(b),
            a.max(b),
            link.kind as u8,
            link.opening_ids.clone(),
        );
        if a == b {
            return Ok(());
        }
        if let Some(&index) = self.seen.get(&key) {
            let old = &mut self.edges[index];
            old.wall_ids = ids(old
                .wall_ids
                .iter()
                .cloned()
                .chain(link.wall_ids.iter().cloned()));
            old.source_ids = ids(old
                .source_ids
                .iter()
                .cloned()
                .chain(link.source_ids.iter().cloned()));
            for i in [a, b] {
                self.nodes[i].wall_ids = ids(self.nodes[i]
                    .wall_ids
                    .iter()
                    .cloned()
                    .chain(link.wall_ids.iter().cloned()));
            }
            return Ok(());
        }
        self.seen.insert(key, self.edges.len());
        if self.edges.len() >= MAX_EDGES {
            return Err("connection edge budget exceeded".into());
        }
        for i in [a, b] {
            self.nodes[i].degree += 1;
            self.nodes[i].wall_ids = ids(self.nodes[i]
                .wall_ids
                .iter()
                .cloned()
                .chain(link.wall_ids.iter().cloned()));
            self.nodes[i].opening_ids = ids(self.nodes[i]
                .opening_ids
                .iter()
                .cloned()
                .chain(link.opening_ids.iter().cloned()));
        }
        self.edges.push(ConnectionEdge {
            id: format!("p{}:connection-edge:{}", self.page, self.edges.len() + 1),
            start_node_id: self.nodes[a].id.clone(),
            end_node_id: self.nodes[b].id.clone(),
            kind: link.kind,
            wall_ids: link.wall_ids,
            opening_ids: link.opening_ids,
            source_ids: link.source_ids,
            evidence: link.evidence.into(),
            // This labels existing support, never permission to extrude a
            // connection edge. Apertures and section overprints carry topology.
            contributes_wall_material: !matches!(
                link.kind,
                ConnectionKind::DoorThreshold
                    | ConnectionKind::WindowThreshold
                    | ConnectionKind::SectionMarkContact
                    | ConnectionKind::RasterShoulderContact
                    | ConnectionKind::RasterCourseContact
                    | ConnectionKind::RecessedStockContact
                    | ConnectionKind::RasterCornerSeat
                    | ConnectionKind::MaskedStockContact
            ),
            adds_wall_material: false,
            requires_review: true,
        });
        Ok(())
    }
}

/// Build a separate graph from a validated native scan. Observed walls, existing
/// graph nodes, aperture measurements and placement material remain unchanged.
pub fn build(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        true,
        ExteriorLinks::CertifiedFacades,
    )
}

pub(crate) fn build_scan_v42(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        true,
        ExteriorLinks::SteppedJambs,
    )
}

pub(crate) fn build_scan_v34(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        true,
        ExteriorLinks::Exact,
    )
}

pub(crate) fn build_scan_v33(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        true,
        ExteriorLinks::Legacy,
    )
}

pub(crate) fn build_preview33(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        true,
        ExteriorLinks::None,
    )
}

pub(crate) fn build_preview31(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::supports(page),
        false,
        ExteriorLinks::None,
    )
}

pub(crate) fn build_preview28(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        super::authored_scope::legacy(page),
        false,
        ExteriorLinks::None,
    )
}

pub(crate) fn build_preview26(page: &PageScan) -> Result<ConnectionPass, String> {
    build_measured(
        &crate::placement::effective_page(page),
        false,
        false,
        ExteriorLinks::None,
    )
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum ExteriorLinks {
    None,
    Legacy,
    Exact,
    SteppedJambs,
    CertifiedFacades,
}

impl ExteriorLinks {
    fn exact_contacts(self) -> bool {
        matches!(
            self,
            Self::Exact | Self::SteppedJambs | Self::CertifiedFacades
        )
    }

    fn stepped_jambs(self) -> bool {
        matches!(self, Self::SteppedJambs | Self::CertifiedFacades)
    }
}

fn build_measured(
    page: &PageScan,
    authored_vectors: bool,
    painted_contacts: bool,
    exterior: ExteriorLinks,
) -> Result<ConnectionPass, String> {
    if !page.width.is_finite() || !page.height.is_finite() || page.width <= 0. || page.height <= 0.
    {
        return Err("connection pass requires positive finite page dimensions".into());
    }
    let eps = epsilon(page);
    let mut work = MAX_WORK;
    let wall_by_id: HashMap<_, _> = page
        .walls
        .iter()
        .enumerate()
        .map(|(i, w)| (w.id.as_str(), i))
        .collect();
    if wall_by_id.len() != page.walls.len() {
        return Err("connection pass requires unique wall IDs".into());
    }
    let ready: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.wall_id.as_str())
        .collect();
    let mut report = ConnectionPass {
        revision: 1,
        page_number: page.page_number,
        nodes: vec![],
        edges: vec![],
        components: vec![],
        boundary_candidates: vec![],
        uncertain_gaps: vec![],
        omitted_walls: vec![],
        diagnostics: vec![],
        requires_review: true,
    };
    let mut eligible = vec![];
    for (i, w) in page.walls.iter().enumerate() {
        let reason = if !ready.contains(w.id.as_str()) {
            Some("wall_not_placement_ready")
        } else if w.curve.is_some() {
            Some("curved_wall_connection_requires_review")
        } else if !w.start.finite()
            || !w.end.finite()
            || !w.thickness.is_finite()
            || w.thickness <= 0.
            || w.start.distance(w.end) <= eps
        {
            Some("wall_material_extent_unresolved")
        } else {
            None
        };
        if let Some(reason) = reason {
            report.omitted_walls.push(OmittedWall {
                wall_id: w.id.clone(),
                reason: reason.into(),
            });
        } else {
            eligible.push(i);
        }
    }
    if eligible.len() > MAX_WALLS {
        return Err("connection wall budget exceeded".into());
    }
    if eligible.is_empty() {
        report.diagnostics.push("No placement-ready finite wall bodies; exterior connectivity is unresolved, not an empty complete house.".into());
        return Ok(report);
    }
    let mut framed_hosts = HashMap::<&str, Vec<&Wall>>::new();
    for decision in page.placement_decisions.iter().flatten() {
        charge(&mut work, 1)?;
        if matches!(
            decision.reason,
            WallReason::SourceBoundedExteriorCore
                | WallReason::SourceBoundedOverheadGateStock
                | WallReason::SourceBoundedRasterJambStock
                | WallReason::SourceBoundedDashedSwingStock
        ) && decision.status == WallStatus::Ready
            && let [opening] = decision.opening_ids.as_slice()
            && let Some(&i) = wall_by_id.get(decision.wall_id.as_str())
        {
            framed_hosts
                .entry(opening.as_str())
                .or_default()
                .push(&page.walls[i]);
        }
    }
    let mut apertures = vec![];
    let source = authored_vectors.then(|| authored::Source::new(page));
    for o in &page.openings {
        charge(&mut work, 1)?;
        charge(&mut work, o.host_wall_ids.len())?;
        if let Some((rect, proof)) =
            framed_hosts
                .get(o.id.as_str())
                .and_then(|hosts| match o.evidence {
                    OpeningEvidence::LayerlessOverheadProjection
                    | OpeningEvidence::LayerlessDashedSwing => {
                        finite_capped_wall_axis(o, hosts, eps)
                    }
                    OpeningEvidence::RasterFiniteHingedDoor => raster_jamb_axis(o, hosts, eps),
                    _ => framed_door(o, hosts, eps),
                })
        {
            apertures.push(Aperture {
                opening: o,
                rect,
                framed_door: Some(proof),
            });
            continue;
        }
        if let Some((rect, proof)) = source
            .as_ref()
            .map(|s| s.door(page, o, &eligible, eps, &mut work))
            .transpose()?
            .flatten()
        {
            apertures.push(Aperture {
                opening: o,
                rect,
                framed_door: Some(proof),
            });
            continue;
        }
        let half = o
            .host_wall_ids
            .iter()
            .filter_map(|id| wall_by_id.get(id.as_str()).map(|&i| &page.walls[i]))
            .filter(|w| w.thickness.is_finite())
            .map(|w| w.thickness * 0.5)
            .fold(0_f64, f64::max);
        if o.start.finite() && o.end.finite() && o.start.distance(o.end) > eps {
            if half <= 0. {
                report.diagnostics.push(format!("Opening {} has no measured host width; its finite source aperture still blocks graph contacts, and its threshold remains unresolved.",o.id));
            }
            apertures.push(Aperture {
                opening: o,
                rect: Rect {
                    a: o.start,
                    b: o.end,
                    half,
                },
                framed_door: None,
            });
        } else {
            report.diagnostics.push(format!(
                "Opening {} has no finite resolvable aperture/host body; its threshold remains unresolved.",
                o.id
            ));
        }
    }
    let mut solids = vec![];
    let offsets = if authored_vectors {
        nominal_axis::offsets(page, &eligible)
    } else {
        HashMap::new()
    };
    for &wi in &eligible {
        let w = &page.walls[wi];
        charge(&mut work, page.openings.len())?;
        let (_, spans) = crate::export::coverage(w, &page.openings);
        for s in spans {
            if s.start.distance(s.end) <= eps {
                continue;
            }
            if crosses_aperture(s.start, s.end, &apertures, None, eps, &mut work)? {
                report.diagnostics.push(format!("Wall {} has a non-host aperture conflict; its conflicted solid span is withheld from the connection graph.",w.id));
                continue;
            }
            let axis_offset = offsets.get(&wi).copied().unwrap_or_default();
            solids.push(Solid {
                a: s.start + axis_offset,
                b: s.end + axis_offset,
                axis_offset,
                half: w.thickness * 0.5,
                wall: wi,
                cuts: vec![0., 1.],
            });
        }
    }
    if solids.len() > MAX_SPANS {
        return Err("connection solid span budget exceeded".into());
    }
    let mut by_wall = HashMap::<&str, Vec<usize>>::new();
    for (i, s) in solids.iter().enumerate() {
        by_wall
            .entry(page.walls[s.wall].id.as_str())
            .or_default()
            .push(i);
    }
    let core_decisions: HashMap<_, _> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| {
            d.reason == WallReason::SourceBoundedExteriorCore && d.status == WallStatus::Ready
        })
        .map(|d| (d.wall_id.as_str(), d))
        .collect();
    let mut core_hosts: HashMap<&str, Vec<usize>> = HashMap::new();
    for (&id, d) in &core_decisions {
        for raw in &d.support_wall_ids {
            core_hosts
                .entry(raw.as_str())
                .or_default()
                .extend(by_wall.get(id).into_iter().flatten().copied());
        }
    }
    for alternatives in core_hosts.values_mut() {
        alternatives.sort_unstable();
        alternatives.dedup();
    }
    let mut links = vec![];
    let mut order: Vec<_> = (0..solids.len()).collect();
    order.sort_by(|&a, &b| {
        solids[a].rect().bbox()[0]
            .total_cmp(&solids[b].rect().bbox()[0])
            .then(a.cmp(&b))
    });
    for (pos, &i) in order.iter().enumerate() {
        let aa = solids[i].rect().bbox();
        for &j in &order[pos + 1..] {
            charge(&mut work, 1)?;
            let bb = solids[j].rect().bbox();
            if bb[0] > aa[2] + eps {
                break;
            }
            if solids[i].wall == solids[j].wall || bb[1] > aa[3] + eps || aa[1] > bb[3] + eps {
                continue;
            }
            let wa = &page.walls[solids[i].wall];
            let wb = &page.walls[solids[j].wall];
            let Some((a, b)) = contact(&solids[i], &solids[j], eps) else {
                let exterior = (core_decisions.contains_key(wa.id.as_str())
                    && core_decisions.contains_key(wb.id.as_str()))
                .then(|| shared_terminal_corner(&solids[i], &solids[j], eps))
                .flatten();
                let proof = exterior
                    .map(|point| (point, "shared_finite_exterior_core_corner"))
                    .or_else(|| {
                        Some((
                            source
                                .as_ref()?
                                .exact_touch(wa, wb, &solids[i], &solids[j], eps)?,
                            "shared_finite_authored_wall_face",
                        ))
                    });
                let Some((corner, evidence)) = proof else {
                    continue;
                };
                let a = solids[i].project(corner).0;
                let b = solids[j].project(corner).0;
                if crosses_aperture(a, corner, &apertures, None, eps, &mut work)?
                    || crosses_aperture(b, corner, &apertures, None, eps, &mut work)?
                {
                    continue;
                }
                // The direct axis-to-axis shortcut would cut across an empty
                // quadrant. Two links meet on the actual material corner.
                let proof = ids(wa
                    .source_ids
                    .iter()
                    .cloned()
                    .chain(wb.source_ids.iter().cloned()));
                for (index, endpoint) in [(i, a), (j, b)] {
                    solids[index].cut(endpoint);
                    push_link(
                        &mut links,
                        Link {
                            a: endpoint,
                            b: corner,
                            kind: ConnectionKind::MeasuredCorner,
                            wall_ids: ids([wa.id.clone(), wb.id.clone()]),
                            opening_ids: vec![],
                            source_ids: proof.clone(),
                            evidence,
                            anchors: [Some(index), None],
                        },
                    )?;
                }
                continue;
            };
            if crosses_aperture(a, b, &apertures, None, eps, &mut work)? {
                continue;
            }
            solids[i].cut(a);
            solids[j].cut(b);
            push_link(
                &mut links,
                Link {
                    a,
                    b,
                    kind: ConnectionKind::MaterialContact,
                    wall_ids: ids([wa.id.clone(), wb.id.clone()]),
                    opening_ids: vec![],
                    source_ids: ids(wa
                        .source_ids
                        .iter()
                        .cloned()
                        .chain(wb.source_ids.iter().cloned())),
                    evidence: BODY_CONTACT,
                    anchors: [Some(i), Some(j)],
                },
            )?;
        }
    }
    for proof in super::cavity_material::coextensive::assemblies(page) {
        let hit = |id: &str, point: Point, terminal| {
            let mut matches = by_wall.get(id)?.iter().copied().filter(|&i| {
                let s = &solids[i];
                if !s.rect().contains(point, eps) || s.project(point).0.distance(point) > eps {
                    return false;
                }
                let endpoint = point.distance(s.a).min(point.distance(s.b)) <= eps;
                endpoint == terminal
            });
            let index = matches.next()?;
            matches.next().is_none().then_some(index)
        };
        let (Some(i), Some(j)) = (
            hit(proof.stem, proof.tip, true),
            hit(proof.owner, proof.axis_contact, false),
        ) else {
            continue;
        };
        if crosses_aperture(
            proof.tip,
            proof.axis_contact,
            &apertures,
            None,
            eps,
            &mut work,
        )? {
            continue;
        }
        solids[i].cut(proof.tip);
        solids[j].cut(proof.axis_contact);
        push_link(
            &mut links,
            Link {
                a: proof.tip,
                b: proof.axis_contact,
                kind: ConnectionKind::MaterialContact,
                wall_ids: ids([proof.stem.to_owned(), proof.owner.to_owned()]),
                opening_ids: vec![],
                source_ids: proof.source_ids.into_iter().map(str::to_owned).collect(),
                evidence: "coextensive_wall_through_certified_transverse_paint",
                anchors: [Some(i), Some(j)],
            },
        )?;
    }
    for proof in super::raster_narrow_course::contacts(page)? {
        let anchor = |id: &str, at: Point| {
            let mut found = by_wall.get(id)?.iter().copied().filter(|&i| {
                let s = &solids[i];
                s.a.distance(at).min(s.b.distance(at)) <= eps
                    && s.rect().contains(proof.contact, eps)
            });
            let index = found.next()?;
            found.next().is_none().then_some(index)
        };
        let endpoints = [proof.junction_end, proof.jamb_far_end];
        let (Some(a), Some(b)) = (
            anchor(&proof.wall_id, endpoints[0]),
            anchor(&proof.jamb_id, endpoints[1]),
        ) else {
            continue;
        };
        if links.iter().any(|l| {
            l.kind == ConnectionKind::MaterialContact
                && (l.anchors == [Some(a), Some(b)] || l.anchors == [Some(b), Some(a)])
        }) || crosses_aperture(
            endpoints[0],
            proof.contact,
            &apertures,
            None,
            eps,
            &mut work,
        )? || crosses_aperture(
            endpoints[1],
            proof.contact,
            &apertures,
            None,
            eps,
            &mut work,
        )? {
            continue;
        }
        for (i, from) in [a, b].into_iter().zip(endpoints) {
            solids[i].cut(from);
            push_link(
                &mut links,
                Link {
                    a: from,
                    b: proof.contact,
                    kind: ConnectionKind::MeasuredCorner,
                    wall_ids: ids([proof.wall_id.clone(), proof.jamb_id.clone()]),
                    opening_ids: vec![],
                    source_ids: proof.source_ids.clone(),
                    evidence: "source_bounded_raster_jamb_return_contact",
                    anchors: [Some(i), None],
                },
            )?;
        }
    }
    for proof in super::raster_narrow_course::section_contacts(page)? {
        let endpoints = [proof.previous_end, proof.new_end];
        let at_end = |id: &str, at: Point| {
            let mut hits = by_wall
                .get(id)?
                .iter()
                .copied()
                .filter(|&i| at.distance(solids[i].a).min(at.distance(solids[i].b)) <= eps);
            let i = hits.next()?;
            hits.next().is_none().then_some(i)
        };
        let (Some(a), Some(b)) = (
            at_end(&proof.previous_wall_id, endpoints[0]),
            at_end(&proof.wall_id, endpoints[1]),
        ) else {
            continue;
        };
        if crosses_aperture(endpoints[0], endpoints[1], &apertures, None, eps, &mut work)? {
            continue;
        }
        push_link(
            &mut links,
            Link {
                a: endpoints[0],
                b: endpoints[1],
                kind: ConnectionKind::SectionMarkContact,
                wall_ids: ids([proof.previous_wall_id, proof.wall_id]),
                opening_ids: vec![],
                source_ids: proof.source_ids,
                evidence: "source_bounded_raster_section_mark_contact",
                anchors: [Some(a), Some(b)],
            },
        )?;
    }
    let facade_paths = super::stepped_facade::joints(page)
        .into_iter()
        .map(|p| SourcePath::facade(p, "source_bounded_stepped_facade_contact"))
        .chain(
            super::capped_facade::joints(page)
                .into_iter()
                .map(|p| SourcePath::facade(p, "source_bounded_capped_facade_contact")),
        );
    let shoulders = super::raster_jamb_return::contacts(page)?
        .into_iter()
        .map(|p| SourcePath {
            wall_ids: [p.host_wall_id, p.wall_id],
            path: p.points,
            source_ids: p.source_ids,
            kind: ConnectionKind::RasterShoulderContact,
            evidence: "source_bounded_raster_jamb_shoulder",
        });
    let aligned_courses = super::raster_aligned_course::contacts(page)?
        .into_iter()
        .map(|p| SourcePath {
            wall_ids: p.wall_ids,
            path: p.points.to_vec(),
            source_ids: p.source_ids,
            kind: ConnectionKind::RasterCourseContact,
            evidence: "source_bounded_raster_course_seam",
        });
    let recessed_branches = super::recessed_stock::branches(page)
        .into_iter()
        .filter(|p| page.openings.iter().any(|o| o.id == p.opening_id))
        .map(|p| SourcePath {
            wall_ids: [p.branch_wall_id, p.stock_wall_id],
            path: vec![p.branch_point, p.toe, p.stock_point],
            source_ids: p.source_ids,
            kind: ConnectionKind::RecessedStockContact,
            evidence: "source_bounded_recessed_stock_branch",
        });
    let raster_corner_seats = super::raster_corner_course::contacts(page)?
        .into_iter()
        .map(|p| SourcePath {
            wall_ids: p.wall_ids,
            path: p.path,
            source_ids: p.source_ids,
            kind: ConnectionKind::RasterCornerSeat,
            evidence: "source_bounded_raster_corner_seat",
        });
    let masked_junctions = super::masked_junction_stock::joints(page)
        .into_iter()
        .map(|p| SourcePath {
            wall_ids: p.wall_ids,
            path: p.points.to_vec(),
            source_ids: p.source_ids,
            kind: ConnectionKind::MaskedStockContact,
            evidence: "source_bounded_masked_stock_junction",
        });
    for proof in facade_paths
        .chain(shoulders)
        .chain(aligned_courses)
        .chain(recessed_branches)
        .chain(raster_corner_seats)
        .chain(masked_junctions)
    {
        let mut path = proof.path;
        path.dedup_by(|a, b| a.distance(*b) <= eps);
        if path.len() < 2 {
            continue;
        }
        let locate = |id: &str, at: Point| {
            let mut hits = by_wall
                .get(id)?
                .iter()
                .copied()
                .filter(|&i| solids[i].project(at).0.distance(at) <= eps);
            let i = hits.next()?;
            hits.next().is_none().then_some(i)
        };
        let [start, end] = [path[0], path[path.len() - 1]];
        let (Some(a), Some(b)) = (
            locate(&proof.wall_ids[0], start),
            locate(&proof.wall_ids[1], end),
        ) else {
            continue;
        };
        if links.iter().any(|link| {
            link.kind == ConnectionKind::MaterialContact
                && ((link.anchors == [Some(a), Some(b)]
                    && link.a.distance(start) <= eps
                    && link.b.distance(end) <= eps)
                    || (link.anchors == [Some(b), Some(a)]
                        && link.b.distance(start) <= eps
                        && link.a.distance(end) <= eps))
        }) {
            continue;
        }
        let mut obstructed = false;
        for p in path.windows(2) {
            obstructed |= crosses_aperture(p[0], p[1], &apertures, None, eps, &mut work)?;
        }
        if obstructed {
            continue;
        }
        solids[a].cut(start);
        solids[b].cut(end);
        for (i, p) in path.windows(2).enumerate() {
            push_link(
                &mut links,
                Link {
                    a: p[0],
                    b: p[1],
                    kind: proof.kind,
                    wall_ids: ids(proof.wall_ids.clone()),
                    opening_ids: vec![],
                    source_ids: proof.source_ids.clone(),
                    evidence: proof.evidence,
                    anchors: [(i == 0).then_some(a), (i + 2 == path.len()).then_some(b)],
                },
            )?;
        }
    }
    // Measured face turns can connect finite axes across already-supported
    // corner material. These are existing placement joints, never new solids.
    // Reserve optional measured-source work separately. Failure leaves the
    // independently proven finite-body graph intact and explicitly incomplete.
    let corner_budget = work.min(4_000_000);
    charge(&mut work, corner_budget)?;
    let corners = match crate::placement::stroked::connection_corner_joints(page, corner_budget) {
        Ok(corners) => corners,
        Err(reason) => {
            report.diagnostics.push(format!("Measured corner evidence is incomplete: {reason}; only independently supported wall contacts remain, and exterior roles require review."));
            vec![]
        }
    };
    let bounded: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.placement_extent.is_some())
        .map(|d| d.wall_id.as_str())
        .collect();
    for joint in corners.into_iter().filter(|j| {
        j.wall_ids
            .iter()
            .all(|id| !bounded.contains(id.as_str()) && !core_decisions.contains_key(id.as_str()))
    }) {
        let center = (joint.polygon[0] + joint.polygon[2]) * 0.5;
        let mut anchors = vec![];
        for (side, wid) in joint.wall_ids.iter().enumerate() {
            let desired = joint.contact_points.map_or_else(
                || {
                    let w = &page.walls[wall_by_id[wid.as_str()]];
                    if joint.ends[side] == 0 {
                        w.start
                    } else {
                        w.end
                    }
                },
                |ps| ps[side],
            );
            let candidates = by_wall
                .get(wid.as_str())
                .map(Vec::as_slice)
                .unwrap_or_default();
            charge(&mut work, candidates.len())?;
            let best = candidates
                .iter()
                .map(|&i| (i, solids[i].project(desired).0))
                .filter(|(_, p)| p.distance(desired) <= eps)
                .min_by(|a, b| a.0.cmp(&b.0));
            if let Some((i, p)) = best {
                anchors.push((i, p));
            }
        }
        if anchors.len() != 2 {
            continue;
        }
        charge(&mut work, links.len())?;
        if let Some(existing) = links.iter_mut().find(|l| {
            l.kind == ConnectionKind::MaterialContact
                && joint.wall_ids.iter().all(|id| l.wall_ids.contains(id))
        }) {
            existing.source_ids = ids(existing
                .source_ids
                .iter()
                .cloned()
                .chain(joint.source_ids.iter().cloned()));
            existing.evidence = "finite_wall_material_contact_with_measured_source_corner";
            continue;
        }
        // Existing joint polygon must cover each connector; a source joint is
        // not permission to traverse an unrelated opening or blank interval.
        for &(i, p) in &anchors {
            if crosses_aperture(p, center, &apertures, None, eps, &mut work)? {
                continue;
            }
            let rects: Vec<_> = anchors.iter().map(|&(s, _)| solids[s].rect()).collect();
            if !covered_by_joint(p, center, &rects, &joint.polygon, eps) {
                continue;
            }
            solids[i].cut(p);
            push_link(
                &mut links,
                Link {
                    a: p,
                    b: center,
                    kind: ConnectionKind::MeasuredCorner,
                    wall_ids: joint.wall_ids.to_vec(),
                    opening_ids: vec![],
                    source_ids: joint.source_ids.clone(),
                    evidence: if joint.adds_material {
                        "existing_measured_face_turn_junction"
                    } else {
                        "existing_measured_wall_body_overlap"
                    },
                    anchors: [Some(i), None],
                },
            )?;
        }
    }
    if let Some(source) = &source {
        source.join(page, &mut solids, &apertures, &mut links, eps, &mut work)?;
    } else if painted_contacts
        && !painted_contact::append(page, &mut solids, &apertures, &mut links, eps, &mut work)
    {
        report
            .diagnostics
            .push("Painted terminal contact review reached its work limit.".into());
    }
    if exterior != ExteriorLinks::None {
        exterior_paint_joints::append(page, &mut solids, &apertures, &mut links, eps, &mut work)?;
    }
    // Canonical opening nodes use the measured aperture, including windows.
    // A portal is retained only when BOTH jambs attach through real host body.
    for portal in &apertures {
        let o = portal.opening;
        let framed = portal.framed_door.as_ref();
        let paired = o.kind == OpeningKind::Door && o.swing_observations.len() == 2;
        let host_ids = framed.map_or(o.host_wall_ids.as_slice(), |f| f.host_ids.as_slice());
        let ends = [portal.rect.a, portal.rect.b];
        let mut attachments = [vec![], vec![]];
        for (side, p) in ends.into_iter().enumerate() {
            for &i in host_ids.iter().flat_map(|id| {
                by_wall.get(id.as_str()).into_iter().flatten().chain(
                    paired
                        .then(|| core_hosts.get(id.as_str()))
                        .flatten()
                        .into_iter()
                        .flatten(),
                )
            }) {
                charge(&mut work, 1)?;
                let s = &solids[i];
                if attachments[side].iter().any(|&(anchor, _)| anchor == i) {
                    continue;
                }
                let q = s.project(p).0;
                if s.rect().contains(p, eps)
                    && covered_by_union(p, q, &[s.rect()], eps)
                    && !crosses_aperture(p, q, &apertures, Some(&o.id), eps, &mut work)?
                {
                    attachments[side].push((i, q));
                }
            }
        }
        if attachments.iter().any(Vec::is_empty) {
            report.diagnostics.push(format!("Opening {} has an unsupported jamb in the ready-wall graph; its threshold remains unresolved.",o.id));
            continue;
        }
        if crosses_aperture(ends[0], ends[1], &apertures, Some(&o.id), eps, &mut work)? {
            report.diagnostics.push(format!(
                "Opening {} overlaps another aperture; threshold withheld.",
                o.id
            ));
            continue;
        }
        let wall_ids = ids(attachments
            .iter()
            .flatten()
            .map(|&(i, _)| page.walls[solids[i].wall].id.clone()));
        push_link(
            &mut links,
            Link {
                a: ends[0],
                b: ends[1],
                kind: if o.kind == OpeningKind::Door {
                    ConnectionKind::DoorThreshold
                } else {
                    ConnectionKind::WindowThreshold
                },
                wall_ids: wall_ids.clone(),
                opening_ids: vec![o.id.clone()],
                source_ids: framed.map_or_else(|| o.source_ids.clone(), |f| f.source_ids.clone()),
                evidence: if framed.is_some() {
                    FRAMED_DOOR_THRESHOLD
                } else {
                    "finite_source_hosted_opening_threshold_no_material"
                },
                anchors: [None, None],
            },
        )?;
        for (side, list) in attachments.into_iter().enumerate() {
            let p = ends[side];
            for (i, q) in list {
                solids[i].cut(q);
                push_link(
                    &mut links,
                    Link {
                        a: p,
                        b: q,
                        kind: ConnectionKind::OpeningJambContact,
                        wall_ids: vec![page.walls[solids[i].wall].id.clone()],
                        opening_ids: vec![o.id.clone()],
                        source_ids: o.source_ids.clone(),
                        evidence: "opening_jamb_contained_in_finite_host_material",
                        anchors: [None, Some(i)],
                    },
                )?;
            }
        }
    }
    if exterior != ExteriorLinks::None {
        let previous = links.len();
        exterior_thresholds::append(page, &mut solids, &apertures, &mut links, eps, &mut work)?;
        let resolved: HashSet<_> = links[previous..].iter().filter(|link| {
            matches!(link.kind, ConnectionKind::DoorThreshold | ConnectionKind::WindowThreshold)
        }).flat_map(|link| link.opening_ids.iter()).flat_map(|id| [
            format!("Opening {id} has an unsupported jamb in the ready-wall graph; its threshold remains unresolved."),
            format!("Opening {id} overlaps another aperture; threshold withheld."),
        ]).collect();
        report
            .diagnostics
            .retain(|diagnostic| !resolved.contains(diagnostic));
    }
    if exterior.exact_contacts()
        && !exterior_finite_contacts::append(page, &solids, &apertures, &mut links, eps, &mut work)
    {
        report
            .diagnostics
            .push("Exact exterior painted contacts exceeded the optional graph work limit.".into());
    }
    if exterior.stepped_jambs() {
        let previous = links.len();
        if stepped_jambs::append(page, &solids, &apertures, &mut links, eps, &mut work) {
            let resolved: HashSet<_> = links[previous..].iter()
                .filter(|link| link.evidence == STEPPED_APERTURE_THRESHOLD)
                .flat_map(|link| &link.opening_ids).flat_map(|id| [
                    format!("Opening {id} has an unsupported jamb in the ready-wall graph; its threshold remains unresolved."),
                    format!("Opening {id} overlaps another aperture; threshold withheld."),
                ]).collect();
            report
                .diagnostics
                .retain(|message| !resolved.contains(message));
        } else {
            report
                .diagnostics
                .push("Exact stepped jamb review exceeded the optional graph work limit.".into());
        }
    }
    // Resolve all cuts before emitting links so exact shared points retain one
    // deterministic coordinate even when source projections differ by roundoff.
    for s in &mut solids {
        s.cuts.sort_by(f64::total_cmp);
        s.cuts
            .dedup_by(|a, b| (*a - *b).abs() * s.a.distance(s.b) <= eps);
    }
    for link in &mut links {
        for (p, anchor) in [
            (&mut link.a, link.anchors[0]),
            (&mut link.b, link.anchors[1]),
        ] {
            if let Some(si) = anchor {
                charge(&mut work, 1)?;
                let s = &solids[si];
                let (_, t) = s.project(*p);
                let at = s.cuts.partition_point(|&x| x < t);
                let replacement = [at.checked_sub(1), Some(at)]
                    .into_iter()
                    .filter_map(|ci| ci.and_then(|ci| s.cuts.get(ci)))
                    .map(|&ct| s.at(ct))
                    .find(|q| q.distance(*p) <= eps);
                *p = replacement.ok_or("connection incidence lost its proven solid cut")?;
            }
        }
    }
    let mut graph = Builder::new(page.page_number);
    graph.aliases = incidence_aliases(&links, eps, &mut work)?;
    for s in &solids {
        let w = &page.walls[s.wall];
        for ts in s.cuts.windows(2) {
            if (ts[1] - ts[0]) * s.length() <= eps {
                continue;
            }
            graph.edge(Link {
                a: s.at(ts[0]),
                b: s.at(ts[1]),
                kind: ConnectionKind::Wall,
                wall_ids: vec![w.id.clone()],
                opening_ids: vec![],
                source_ids: w.source_ids.clone(),
                evidence: "placement_ready_aperture_clipped_wall_axis",
                anchors: [None, None],
            })?;
        }
    }
    for link in links {
        graph.edge(link)?;
    }
    report.nodes = graph.nodes;
    report.edges = graph.edges;
    let exterior_walls = source
        .as_ref()
        .map_or_else(HashSet::new, |s| s.exterior_walls(page));
    finish(page, &mut report, eps, &mut work, &exterior_walls, exterior)?;
    if !report.omitted_walls.is_empty() {
        report.diagnostics.push(format!("{} observed walls are outside the eligible finite-ready subset; closed graph cycles do not establish complete house coverage.",report.omitted_walls.len()));
    }
    Ok(report)
}

fn finish(
    page: &PageScan,
    report: &mut ConnectionPass,
    eps: f64,
    work: &mut usize,
    exterior_walls: &HashSet<&str>,
    exterior: ExteriorLinks,
) -> Result<(), String> {
    let wall_by_id: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let mut openings_by_wall: HashMap<&str, Vec<usize>> = HashMap::new();
    let mut opening_reach = Vec::with_capacity(page.openings.len());
    for (i, o) in page.openings.iter().enumerate() {
        charge(work, o.host_wall_ids.len() + 1)?;
        opening_reach.push(
            o.host_wall_ids
                .iter()
                .filter_map(|id| wall_by_id.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(eps, f64::max),
        );
        for id in &o.host_wall_ids {
            openings_by_wall.entry(id).or_default().push(i);
        }
    }
    let lookup: HashMap<_, _> = report
        .nodes
        .iter()
        .enumerate()
        .map(|(i, n)| (n.id.as_str(), i))
        .collect();
    let mut adj = vec![vec![]; report.nodes.len()];
    let mut pairs = vec![];
    for (i, e) in report.edges.iter().enumerate() {
        let a = lookup[e.start_node_id.as_str()];
        let b = lookup[e.end_node_id.as_str()];
        adj[a].push((b, i));
        adj[b].push((a, i));
        pairs.push((a, b));
    }
    let mut component = vec![usize::MAX; adj.len()];
    for start in 0..adj.len() {
        if component[start] != usize::MAX || adj[start].is_empty() {
            continue;
        }
        let c = report.components.len();
        let mut todo = vec![start];
        component[start] = c;
        let mut nodes = BTreeSet::new();
        let mut edges = BTreeSet::new();
        while let Some(i) = todo.pop() {
            charge(work, adj[i].len() + 1)?;
            nodes.insert(i);
            for &(j, e) in &adj[i] {
                edges.insert(e);
                if component[j] == usize::MAX {
                    component[j] = c;
                    todo.push(j);
                }
            }
        }
        report.components.push(ConnectionComponent {
            id: format!("p{}:connection-component:{}", page.page_number, c + 1),
            node_ids: nodes.iter().map(|&i| report.nodes[i].id.clone()).collect(),
            edge_ids: edges.iter().map(|&i| report.edges[i].id.clone()).collect(),
            wall_ids: ids(edges
                .iter()
                .flat_map(|&i| report.edges[i].wall_ids.iter().cloned())),
            closed: nodes.iter().all(|&i| adj[i].len() == 2),
            branching: nodes.iter().any(|&i| adj[i].len() > 2),
            requires_review: true,
        });
    }
    append_boundaries(page, report, &adj, &wall_by_id, None, eps, work)?;
    if report.boundary_candidates.is_empty() && !exterior_walls.is_empty() {
        // Interior branches can change the face walk of the complete graph.
        // Replay its existing measured edges on the authored exterior subset.
        let exterior: Vec<Vec<_>> = adj
            .iter()
            .map(|links| {
                links
                    .iter()
                    .copied()
                    .filter(|&(_, i)| {
                        let edge = &report.edges[i];
                        !edge.wall_ids.is_empty()
                            && edge
                                .wall_ids
                                .iter()
                                .all(|id| exterior_walls.contains(id.as_str()))
                    })
                    .collect()
            })
            .collect();
        append_boundaries(page, report, &exterior, &wall_by_id, None, eps, work)?;
    }
    if exterior.exact_contacts() {
        exterior_boundaries::append(
            page,
            report,
            &adj,
            &wall_by_id,
            exterior.stepped_jambs(),
            exterior == ExteriorLinks::CertifiedFacades,
            eps,
            work,
        );
    }
    let ends: Vec<_> = adj
        .iter()
        .enumerate()
        .filter(|(_, n)| n.len() == 1)
        .map(|(i, _)| i)
        .collect();
    if ends.len() > MAX_GAPS {
        return Err("connection gap diagnostic budget exceeded".into());
    }
    for &i in &ends {
        let a = report.nodes[i].position;
        let mut nearest = None;
        for &j in &ends {
            charge(work, 1)?;
            if i == j
                || report.nodes[i]
                    .wall_ids
                    .iter()
                    .any(|id| report.nodes[j].wall_ids.contains(id))
            {
                continue;
            }
            let d = a.distance(report.nodes[j].position);
            if nearest.is_none_or(|(old, k): (f64, usize)| d < old || (d == old && j < k)) {
                nearest = Some((d, j));
            }
        }
        let (end, distance, end_node_id) = nearest.map_or((None, None, None), |(d, j)| {
            (
                Some(report.nodes[j].position),
                Some(d),
                Some(report.nodes[j].id.clone()),
            )
        });
        let mut nearby_openings = BTreeSet::new();
        charge(work, report.nodes[i].wall_ids.len())?;
        for id in &report.nodes[i].wall_ids {
            if let Some(list) = openings_by_wall.get(id.as_str()) {
                charge(work, list.len())?;
                nearby_openings.extend(list.iter().copied());
            }
        }
        charge(work, nearby_openings.len())?;
        let openings: Vec<_> = nearby_openings
            .into_iter()
            .filter(|&j| {
                [page.openings[j].start, page.openings[j].end]
                    .iter()
                    .any(|p| p.distance(a) <= opening_reach[j])
            })
            .map(|j| &page.openings[j])
            .collect();
        let reason = if !openings.is_empty() {
            "opening_jamb_or_other_host_unresolved"
        } else if end.is_some() {
            "nearest_unconnected_end_without_source_support"
        } else {
            "open_end_without_supported_connection"
        };
        report.uncertain_gaps.push(UncertainGap {
            id: format!(
                "p{}:connection-gap:{}",
                page.page_number,
                report.uncertain_gaps.len() + 1
            ),
            start_node_id: report.nodes[i].id.clone(),
            end_node_id,
            start: a,
            end,
            distance,
            reason: reason.into(),
            wall_ids: report.nodes[i].wall_ids.clone(),
            opening_ids: ids(openings.iter().map(|o| o.id.clone())),
            source_ids: ids(report.nodes[i]
                .wall_ids
                .iter()
                .filter_map(|id| wall_by_id.get(id.as_str()))
                .flat_map(|w| w.source_ids.iter().cloned())),
            connected: false,
            requires_review: true,
        });
    }
    if report.boundary_candidates.is_empty() {
        report.diagnostics.push("No supported closed boundary candidate in the eligible wall subset; uncertain gaps remain unconnected.".into());
    }
    Ok(())
}

fn append_boundaries(
    page: &PageScan,
    report: &mut ConnectionPass,
    adj: &[Vec<(usize, usize)>],
    wall_by_id: &HashMap<&str, &crate::model::Wall>,
    exterior: Option<&exterior_boundaries::Proof<'_>>,
    eps: f64,
    work: &mut usize,
) -> Result<(), String> {
    // Peel dangling branches for cycle walking, retaining the original graph and
    // diagnostics. Pruning is not a supported gap connection.
    let mut cycle_adj = adj.to_vec();
    let mut todo: VecDeque<_> = cycle_adj
        .iter()
        .enumerate()
        .filter(|(_, a)| a.len() < 2)
        .map(|(i, _)| i)
        .collect();
    while let Some(i) = todo.pop_front() {
        for (j, _) in std::mem::take(&mut cycle_adj[i]) {
            charge(work, cycle_adj[j].len() + 1)?;
            cycle_adj[j].retain(|&(n, _)| n != i);
            if cycle_adj[j].len() == 1 {
                todo.push_back(j);
            }
        }
    }
    for (i, neighbors) in cycle_adj.iter_mut().enumerate() {
        neighbors.sort_by(|&(a, ea), &(b, eb)| {
            let x = report.nodes[a].position - report.nodes[i].position;
            let y = report.nodes[b].position - report.nodes[i].position;
            x.y.atan2(x.x).total_cmp(&y.y.atan2(y.x)).then(ea.cmp(&eb))
        });
    }
    let mut seen = HashSet::new();
    let mut face_limit_reached = false;
    for start in 0..cycle_adj.len() {
        for &(next, edge) in &cycle_adj[start] {
            if seen.contains(&(start, next, edge)) {
                continue;
            }
            let mut ns = vec![];
            let mut es = vec![];
            let (mut a, mut b, mut e) = (start, next, edge);
            loop {
                charge(work, 1)?;
                if !seen.insert((a, b, e)) {
                    break;
                }
                if ns.len() >= MAX_FACE_VERTICES {
                    face_limit_reached = true;
                    break;
                }
                ns.push(a);
                es.push(e);
                charge(work, cycle_adj[b].len())?;
                let Some(at) = cycle_adj[b].iter().position(|&(n, x)| n == a && x == e) else {
                    break;
                };
                let (c, ce) = cycle_adj[b][(at + cycle_adj[b].len() - 1) % cycle_adj[b].len()];
                a = b;
                b = c;
                e = ce;
                if (a, b, e) == (start, next, edge) {
                    break;
                }
            }
            if (a, b, e) != (start, next, edge)
                || ns.len() < 3
                || ns.iter().collect::<HashSet<_>>().len() != ns.len()
                || exterior.is_some_and(|proof| !es.iter().any(|i| proof.exact_edges.contains(i)))
            {
                continue;
            }
            let polygon: Vec<_> = ns.iter().map(|&i| report.nodes[i].position).collect();
            let origin = polygon[0];
            let signed: f64 = polygon
                .iter()
                .zip(polygon.iter().cycle().skip(1))
                .map(|(a, b)| (*a - origin).cross(*b - origin) * 0.5)
                .sum();
            // Keep only the outer face of a connected cycle component, not each
            // interior room. Its architectural exterior role still needs evidence.
            if signed >= -eps * eps {
                continue;
            }
            let perimeter: f64 = polygon
                .iter()
                .zip(polygon.iter().cycle().skip(1))
                .map(|(a, b)| a.distance(*b))
                .sum();
            let wall_ids = ids(es
                .iter()
                .flat_map(|&i| report.edges[i].wall_ids.iter().cloned()));
            charge(work, wall_ids.len())?;
            let thick = wall_ids
                .iter()
                .filter_map(|id| wall_by_id.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(0_f64, f64::max);
            if -signed <= perimeter * thick * 2. || !simple(&polygon, eps, work)? {
                continue;
            }
            if let Some(proof) = exterior {
                if !exterior_boundaries::contains_source_lane(
                    report, proof.lane, &ns, &es, &polygon, wall_by_id, eps, work,
                )? {
                    continue;
                }
                let mut duplicate = false;
                for old in &report.boundary_candidates {
                    charge(
                        work,
                        polygon
                            .len()
                            .saturating_mul(old.polygon.len())
                            .saturating_mul(4),
                    )?;
                    if rings_near(&polygon, &old.polygon, thick + eps) {
                        duplicate = true;
                        break;
                    }
                }
                if duplicate {
                    continue;
                }
            }
            let mut known = false;
            for c in page
                .exterior_enclosures
                .iter()
                .flatten()
                .filter(|c| c.closed)
            {
                charge(
                    work,
                    polygon
                        .len()
                        .checked_mul(c.polygon.len())
                        .and_then(|n| n.checked_mul(4))
                        .ok_or("connection enclosure comparison work overflow")?,
                )?;
                if rings_near(&polygon, &c.polygon, thick + eps) {
                    known = true;
                    break;
                }
            }
            let mut polygon = polygon;
            polygon.reverse();
            ns.reverse();
            es.reverse();
            es.rotate_left(1);
            report.boundary_candidates.push(BoundaryCandidate {
                id: format!(
                    "p{}:connection-boundary:{}",
                    page.page_number,
                    report.boundary_candidates.len() + 1
                ),
                node_ids: ns.iter().map(|&i| report.nodes[i].id.clone()).collect(),
                edge_ids: es.iter().map(|&i| report.edges[i].id.clone()).collect(),
                polygon,
                area_drawing_units_squared: -signed,
                area_basis: "wall_axis_connection_boundary".into(),
                wall_ids,
                opening_ids: ids(es
                    .iter()
                    .flat_map(|&i| report.edges[i].opening_ids.iter().cloned())),
                exterior_status: if known {
                    "matches_existing_source_exterior_enclosure"
                } else if exterior.is_some() {
                    "source_authored_exterior_wall_axis_cycle"
                } else {
                    "exterior_role_unresolved"
                }
                .into(),
                requires_review: true,
            });
        }
    }
    if face_limit_reached {
        report.diagnostics.push(format!("Connection boundary review is incomplete: a supported graph face exceeded the {MAX_FACE_VERTICES}-vertex review limit; that walk does not establish a closed boundary candidate."));
    }
    Ok(())
}

fn simple(ps: &[Point], eps: f64, work: &mut usize) -> Result<bool, String> {
    for i in 0..ps.len() {
        let (a, b) = (ps[i], ps[(i + 1) % ps.len()]);
        for j in i + 2..ps.len() {
            charge(work, 1)?;
            if i == 0 && j + 1 == ps.len() {
                continue;
            }
            let (c, d) = (ps[j], ps[(j + 1) % ps.len()]);
            let u = b - a;
            let v = d - c;
            let den = u.cross(v);
            if den.abs() > eps * (u.norm() + v.norm()) {
                let t = (c - a).cross(v) / den;
                let q = (c - a).cross(u) / den;
                let (tu, tv) = (eps / u.norm(), eps / v.norm());
                if t >= -tu && t <= 1. + tu && q >= -tv && q <= 1. + tv {
                    return Ok(false);
                }
            } else if [a, b]
                .iter()
                .any(|&p| on_segment(p, c, d).0.distance(p) <= eps)
                || [c, d]
                    .iter()
                    .any(|&p| on_segment(p, a, b).0.distance(p) <= eps)
            {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::*;
    use crate::placement::{WallDecision, WallReason};

    fn page() -> PageScan {
        crate::pipeline::scan_document(
            Document {
                pages: vec![Page {
                    finish_history: None,
                    number: 1,
                    width: 1000.,
                    height: 1000.,
                    units_to_mm: None,
                    primitives: vec![],
                    pdf_input: None,
                    material_tiles: vec![],
                    placement_paint_ids: None,
                    placement_polygon_paint_ids: None,
                    placement_contact_paint: vec![],
                    placement_paint_remainders: vec![],
                    white_nested_walls: vec![],
                    terminal_stock: vec![],
                    local_stock: vec![],
                    stepped_stock: vec![],
                    capped_stock: vec![],
                    recessed_stock: vec![],
                    masked_junction_stock: vec![],
                    pocket_stock: vec![],
                }],
                warnings: vec![],
            },
            &Options::default(),
            None,
            true,
        )
        .unwrap()
        .pages
        .remove(0)
    }
    #[test]
    fn proven_incidence_unions_both_rounded_solid_cuts_and_never_nearby_gaps() {
        let a = Point::new(411.2460084343, 540.6547433059);
        let b = Point::new(f64::from_bits(a.x.to_bits() + 1), a.y);
        let unsupported = Point::new(a.x + 0.001, a.y);
        let link = Link {
            a,
            b,
            kind: ConnectionKind::MaterialContact,
            wall_ids: vec!["a".into(), "b".into()],
            opening_ids: vec![],
            source_ids: vec![],
            evidence: "test_measured_contact",
            anchors: [Some(0), Some(1)],
        };
        let mut work = MAX_WORK;
        let mut graph = Builder::new(1);
        graph.aliases = incidence_aliases(&[link], 1e-10, &mut work).unwrap();
        assert_eq!(graph.node(a), graph.node(b));
        assert_ne!(graph.node(a), graph.node(unsupported));
    }
    #[test]
    fn rotated_scale_and_translation_do_not_strand_proven_wall_contacts() {
        for (angle, scale, offset) in [
            (0.371_f64, 0.001, 0.),
            (1.231, 1., 100_000.),
            (0.789, 1000., 10_000_000.),
        ] {
            let mut p = house(angle, false, false);
            for w in &mut p.walls {
                w.start = w.start * scale + Point::new(offset, offset);
                w.end = w.end * scale + Point::new(offset, offset);
                w.thickness *= scale;
            }
            p.width = 2000. * scale + offset;
            p.height = 2000. * scale + offset;
            let c = build(&p).unwrap();
            assert_eq!(
                c.boundary_candidates.len(),
                1,
                "angle {angle} scale {scale}"
            );
            assert!(c.uncertain_gaps.is_empty(), "angle {angle} scale {scale}");
            assert_eq!(c.components.len(), 1);
            assert!(c.components[0].closed);
        }
    }
    #[test]
    fn nonadjacent_touch_and_contained_collinear_overlap_are_not_simple_boundaries() {
        for polygon in [
            vec![
                Point::new(0., 0.),
                Point::new(10., 0.),
                Point::new(10., 10.),
                Point::new(3., 0.),
                Point::new(7., 0.),
                Point::new(0., 10.),
            ],
            vec![
                Point::new(0., 0.),
                Point::new(10., 0.),
                Point::new(10., 10.),
                Point::new(5., 0.),
                Point::new(0., 10.),
            ],
        ] {
            assert!(!simple(&polygon, 1e-10, &mut MAX_WORK.clone()).unwrap());
        }
    }
    #[test]
    fn aperture_work_and_link_memory_limits_fail_explicitly() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        let a = Aperture {
            opening: &p.openings[0],
            rect: Rect {
                a: p.openings[0].start,
                b: p.openings[0].end,
                half: 1.,
            },
            framed_door: None,
        };
        assert!(
            crosses_aperture(
                Point::new(200., 200.),
                Point::new(300., 200.),
                &[a],
                None,
                1e-10,
                &mut 0
            )
            .unwrap_err()
            .contains("budget")
        );
        let link = Link {
            a: Point::new(0., 0.),
            b: Point::new(1., 0.),
            kind: ConnectionKind::MaterialContact,
            wall_ids: vec![],
            opening_ids: vec![],
            source_ids: vec![],
            evidence: "test",
            anchors: [None, None],
        };
        let mut links = vec![link.clone(); MAX_EDGES];
        assert!(push_link(&mut links, link).unwrap_err().contains("budget"));
    }
    #[test]
    fn unhosted_finite_aperture_vetoes_contacts_even_without_a_host_width() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        p.openings[0].host_wall_ids.clear();
        p.openings[0].host_wall_id.clear();
        let aperture = Aperture {
            opening: &p.openings[0],
            rect: Rect {
                a: p.openings[0].start,
                b: p.openings[0].end,
                half: 0.,
            },
            framed_door: None,
        };
        assert!(
            crosses_aperture(
                Point::new(245., 190.),
                Point::new(245., 210.),
                &[aperture],
                None,
                1e-10,
                &mut MAX_WORK.clone()
            )
            .unwrap()
        );
        let c = build(&p).unwrap();
        assert!(
            c.edges
                .iter()
                .all(|e| e.kind != ConnectionKind::DoorThreshold)
        );
        assert!(c.boundary_candidates.is_empty());
        assert!(
            c.diagnostics
                .iter()
                .any(|d| d.contains("finite source aperture still blocks"))
        );
        assert!(
            crate::placement::stroked::connection_corner_joints(&p, 0)
                .err()
                .unwrap()
                .contains("budget")
        );
    }
    #[test]
    fn two_source_face_turns_supply_a_corner_but_an_aperture_vetoes_it() {
        for angle in [0_f64, 0.7] {
            let axis = Point::new(angle.cos(), angle.sin());
            let at = |x, y| Point::new(100., 100.) + axis * x + Point::new(-axis.y, axis.x) * y;
            let mut p = page();
            let mut line = |id: String, a, b, width| {
                p.primitives.push(Primitive {
                    id,
                    layer: None,
                    layer_category_override: None,
                    stroke_width: Some(width),
                    shape: Shape::Line { start: a, end: b },
                })
            };
            for (id, a, b) in [
                ("outer-horizontal", at(0., 0.), at(90., 0.)),
                ("inner-horizontal", at(0., 10.), at(80., 10.)),
                ("outer-vertical", at(90., 0.), at(90., 70.)),
                ("inner-vertical", at(80., 10.), at(80., 70.)),
                ("west-end", at(0., 0.), at(0., 10.)),
                ("south-end", at(80., 70.), at(90., 70.)),
            ] {
                line(id.into(), a, b, 1.);
            }
            for i in 0..14 {
                line(
                    format!("fine-{i}"),
                    at(-20., i as f64 * 4.),
                    at(-16., i as f64 * 4.),
                    0.5,
                );
            }
            let scan = crate::pipeline::scan_document(
                Document {
                    pages: vec![Page {
                        finish_history: None,
                        number: 1,
                        width: 1000.,
                        height: 1000.,
                        units_to_mm: None,
                        primitives: p.primitives,
                        pdf_input: None,
                        material_tiles: vec![],
                        placement_paint_ids: None,
                        placement_polygon_paint_ids: None,
                        placement_contact_paint: vec![],
                        placement_paint_remainders: vec![],
                        white_nested_walls: vec![],
                        terminal_stock: vec![],
                        local_stock: vec![],
                        stepped_stock: vec![],
                        capped_stock: vec![],
                        recessed_stock: vec![],
                        masked_junction_stock: vec![],
                        pocket_stock: vec![],
                    }],
                    warnings: vec![],
                },
                &Options::default(),
                None,
                true,
            )
            .unwrap();
            let mut p = scan.pages.into_iter().next().unwrap();
            let c = build(&p).unwrap();
            assert_eq!(c.components.len(), 1);
            assert_eq!(
                c.edges
                    .iter()
                    .filter(|e| e.kind == ConnectionKind::MeasuredCorner)
                    .count(),
                2
            );
            assert!(c.boundary_candidates.is_empty());
            p.openings.push(Opening {
                physical_jambs: None,
                id: "corner-aperture".into(),
                page: 1,
                kind: OpeningKind::Window,
                operation: "Fixed".into(),
                host_wall_id: String::new(),
                host_wall_ids: vec![],
                start: at(82., 5.),
                end: at(88., 5.),
                hinge: None,
                swing_observations: vec![],
                source_ids: vec![],
                evidence: OpeningEvidence::ExplicitWindowFrame,
                requires_review: true,
            });
            let c = build(&p).unwrap();
            assert!(
                c.edges
                    .iter()
                    .all(|e| e.kind != ConnectionKind::MeasuredCorner)
            );
            assert_eq!(c.components.len(), 2);
        }
    }
    fn wall(p: &mut PageScan, id: &str, a: Point, b: Point, thickness: f64) {
        let source = format!("source:{id}");
        let axis = (b - a).unit();
        let n = Point::new(-axis.y, axis.x) * (thickness * 0.5);
        p.primitives.push(Primitive {
            id: source.clone(),
            layer: Some("Wall".into()),
            stroke_width: Some(0.2),
            layer_category_override: None,
            shape: Shape::Polyline {
                points: vec![a + n, b + n, b - n, a - n],
                closed: true,
                filled: true,
            },
        });
        p.walls.push(Wall {
            id: id.into(),
            page: 1,
            start: a,
            end: b,
            thickness,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids: vec![source],
            requires_review: true,
        });
        p.placement_decisions
            .get_or_insert_default()
            .push(WallDecision {
                placement_extent: None,
                wall_id: id.into(),
                status: WallStatus::Ready,
                reason: WallReason::AuthoredFilledPartition,
                component_id: "component".into(),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![],
                support_source_ids: vec![],
                opening_ids: vec![],
                room_ids: vec![],
            });
    }
    fn house(angle: f64, split: bool, tiny_gap: bool) -> PageScan {
        let mut p = page();
        let at = |x, y| {
            Point::new(200., 200.)
                + Point::new(angle.cos(), angle.sin()) * x
                + Point::new(-angle.sin(), angle.cos()) * y
        };
        if split {
            let (a, b) = if tiny_gap { (49.95, 50.05) } else { (35., 55.) };
            wall(&mut p, "north-left", at(0., 0.), at(a, 0.), 2.);
            wall(&mut p, "north-right", at(b, 0.), at(100., 0.), 2.);
        } else {
            wall(&mut p, "north", at(0., 0.), at(100., 0.), 2.);
        }
        wall(&mut p, "east", at(100., 0.), at(100., 100.), 2.);
        wall(&mut p, "south", at(100., 100.), at(0., 100.), 2.);
        wall(&mut p, "west", at(0., 100.), at(0., 0.), 2.);
        p
    }
    fn opening(p: &mut PageScan, kind: OpeningKind, gap: bool, angle: f64) {
        let at = |x, y| {
            Point::new(200., 200.)
                + Point::new(angle.cos(), angle.sin()) * x
                + Point::new(-angle.sin(), angle.cos()) * y
        };
        let hosts: Vec<String> = if gap {
            vec!["north-left".into(), "north-right".into()]
        } else {
            vec!["north".into()]
        };
        p.openings.push(Opening {
            physical_jambs: None,
            id: "aperture".into(),
            page: 1,
            kind,
            operation: if kind == OpeningKind::Door {
                "Hinged"
            } else {
                "Fixed"
            }
            .into(),
            host_wall_id: hosts[0].clone(),
            host_wall_ids: hosts,
            start: at(35., 0.),
            end: at(55., 0.),
            hinge: Some(at(35., 0.)),
            swing_observations: vec![],
            source_ids: vec!["opening-source".into()],
            evidence: if gap {
                OpeningEvidence::WallGapSwing
            } else if kind == OpeningKind::Door {
                OpeningEvidence::FiniteJambSwing
            } else {
                OpeningEvidence::FiniteWindowAssembly
            },
            requires_review: true,
        });
    }

    #[test]
    fn exact_supported_ring_is_rotation_independent_and_not_certified_as_a_house() {
        for angle in [0., 0.67] {
            let p = house(angle, false, false);
            let before = serde_json::to_value(&p).unwrap();
            let c = build(&p).unwrap();
            assert_eq!(before, serde_json::to_value(&p).unwrap());
            assert_eq!(c.boundary_candidates.len(), 1);
            let b = &c.boundary_candidates[0];
            assert!((b.area_drawing_units_squared - 10_000.).abs() < 1e-6);
            assert_eq!(b.area_basis, "wall_axis_connection_boundary");
            assert_eq!(b.exterior_status, "exterior_role_unresolved");
            assert!(b.requires_review);
            assert!(c.uncertain_gaps.is_empty());
            assert!(c.edges.iter().all(|e| !e.adds_wall_material));
        }
    }
    #[test]
    fn real_body_butt_contact_connects_separate_axes_without_moving_source_ends() {
        let mut p = house(0., false, false);
        p.walls.iter_mut().find(|w| w.id == "east").unwrap().start.y += 1.;
        let source_end = p.walls.iter().find(|w| w.id == "east").unwrap().start;
        let c = build(&p).unwrap();
        assert_eq!(
            p.walls.iter().find(|w| w.id == "east").unwrap().start,
            source_end
        );
        assert!(
            c.edges
                .iter()
                .any(|e| e.kind == ConnectionKind::MaterialContact)
        );
        assert_eq!(c.boundary_candidates.len(), 1);
        assert!(c.uncertain_gaps.is_empty());
    }
    #[test]
    fn a_small_blank_gap_stays_open_instead_of_nearest_node_snapping() {
        let p = house(0., true, true);
        let c = build(&p).unwrap();
        assert!(c.boundary_candidates.is_empty());
        assert_eq!(c.uncertain_gaps.len(), 2);
        assert!(
            c.uncertain_gaps
                .iter()
                .all(|g| !g.connected && g.requires_review)
        );
        assert!(
            c.uncertain_gaps
                .iter()
                .all(|g| g.distance.is_some_and(|d| (d - 0.1).abs() < 1e-8))
        );
    }
    #[test]
    fn measured_raster_jambs_attach_to_the_original_laterally_offset_portal() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        let o = &mut p.openings[0];
        o.evidence = OpeningEvidence::RasterFiniteHingedDoor;
        o.host_wall_ids.clear();
        o.host_wall_id.clear();
        o.start.y += 0.6;
        o.end.y += 0.6;
        let original = o.clone();
        for d in p.placement_decisions.iter_mut().flatten().take(2) {
            d.reason = WallReason::SourceBoundedRasterJambStock;
            d.opening_ids.push(original.id.clone());
        }
        let graph = build(&p).unwrap();
        let edge = graph
            .edges
            .iter()
            .find(|e| e.kind == ConnectionKind::DoorThreshold)
            .unwrap();
        assert_eq!(edge.evidence, FRAMED_DOOR_THRESHOLD);
        assert!(!edge.adds_wall_material && !edge.contributes_wall_material);
        let position = |id: &str| graph.nodes.iter().find(|n| n.id == id).unwrap().position;
        assert_eq!(position(&edge.start_node_id), original.start);
        assert_eq!(position(&edge.end_node_id), original.end);
        assert_eq!(
            graph
                .edges
                .iter()
                .filter(|e| {
                    e.kind == ConnectionKind::OpeningJambContact
                        && !e.adds_wall_material
                        && e.contributes_wall_material
                })
                .count(),
            2
        );
        assert_eq!(p.openings[0], original);
        p.walls[1].start.x += 0.1;
        assert!(
            build(&p)
                .unwrap()
                .edges
                .iter()
                .all(|e| e.kind != ConnectionKind::DoorThreshold)
        );
    }
    #[test]
    fn signed_exterior_caps_connect_actual_doorway_without_moving_the_observation() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        let o = &mut p.openings[0];
        o.host_wall_id = "reviewed-frame".into();
        o.host_wall_ids = vec![o.host_wall_id.clone()];
        o.evidence = OpeningEvidence::FiniteJambSwing;
        o.start = Point::new(254., 200.6);
        o.end = Point::new(236., 200.6);
        o.swing_observations.push(SwingObservation {
            arc_source_id: "signed-leaf".into(),
            segment_source_ids: vec![],
            hinge: o.start,
            closed_tip: o.end,
            open_tip: Point::new(254., 218.6),
            leaf_tolerance: 0.01,
            open_leaf_source_ids: vec![],
            closed_leaf_source_ids: vec![],
        });
        let observed = o.clone();
        for d in p.placement_decisions.iter_mut().flatten().take(2) {
            d.reason = WallReason::SourceBoundedExteriorCore;
            d.opening_ids.push(observed.id.clone());
        }
        let graph = build(&p).unwrap();
        let edge = graph
            .edges
            .iter()
            .find(|e| e.kind == ConnectionKind::DoorThreshold)
            .unwrap();
        assert_eq!(edge.evidence, FRAMED_DOOR_THRESHOLD);
        assert_eq!(edge.opening_ids, [observed.id.clone()]);
        assert!(!edge.adds_wall_material && !edge.contributes_wall_material);
        let position = |id: &str| graph.nodes.iter().find(|n| n.id == id).unwrap().position;
        assert_eq!(position(&edge.start_node_id), Point::new(255., 200.));
        assert_eq!(position(&edge.end_node_id), Point::new(235., 200.));
        assert_eq!(p.openings[0], observed);
        p.placement_decisions.as_mut().unwrap()[0]
            .opening_ids
            .clear();
        assert!(
            build(&p)
                .unwrap()
                .edges
                .iter()
                .all(|e| e.kind != ConnectionKind::DoorThreshold)
        );
    }
    #[test]
    fn source_door_gap_closes_graph_without_wall_material_and_ignores_swing_extent() {
        for angle in [0., 0.67] {
            let mut p = house(angle, true, false);
            opening(&mut p, OpeningKind::Door, true, angle);
            let c = build(&p).unwrap();
            assert_eq!(c.boundary_candidates.len(), 1);
            let portals: Vec<_> = c
                .edges
                .iter()
                .filter(|e| e.kind == ConnectionKind::DoorThreshold)
                .collect();
            assert_eq!(portals.len(), 1);
            assert!(!portals[0].contributes_wall_material && !portals[0].adds_wall_material);
            let (start, end) = (p.openings[0].start, p.openings[0].end);
            p.openings[0].swing_observations.push(SwingObservation {
                arc_source_id: "display-only-arc".into(),
                segment_source_ids: vec![],
                hinge: start,
                closed_tip: end,
                open_tip: Point::new(900., 900.),
                leaf_tolerance: 0.1,
                open_leaf_source_ids: vec![],
                closed_leaf_source_ids: vec![],
            });
            assert_eq!(build(&p).unwrap(), c);
        }
    }
    #[test]
    fn hosted_window_is_a_zero_material_graph_span_and_cuts_the_host_axis() {
        let mut p = house(0., false, false);
        opening(&mut p, OpeningKind::Window, false, 0.);
        let c = build(&p).unwrap();
        assert_eq!(c.boundary_candidates.len(), 1);
        assert_eq!(
            c.edges
                .iter()
                .filter(|e| e.kind == ConnectionKind::WindowThreshold)
                .count(),
            1
        );
        let positions: HashMap<_, _> = c
            .nodes
            .iter()
            .map(|n| (n.id.as_str(), n.position))
            .collect();
        for e in c
            .edges
            .iter()
            .filter(|e| e.kind == ConnectionKind::Wall && e.wall_ids.contains(&"north".into()))
        {
            let (a, b) = (
                positions[e.start_node_id.as_str()],
                positions[e.end_node_id.as_str()],
            );
            assert!(a.x.max(b.x) <= 235. + 1e-9 || a.x.min(b.x) >= 255. - 1e-9);
        }
    }
    #[test]
    fn nonhost_material_crossing_a_door_is_not_used_for_connection() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        wall(
            &mut p,
            "conflict",
            Point::new(245., 190.),
            Point::new(245., 210.),
            2.,
        );
        let c = build(&p).unwrap();
        assert!(
            c.edges
                .iter()
                .all(|e| !e.wall_ids.contains(&"conflict".into()))
        );
        assert!(
            c.diagnostics
                .iter()
                .any(|d| d.contains("non-host aperture conflict"))
        );
        assert!(
            c.edges
                .iter()
                .any(|e| e.kind == ConnectionKind::DoorThreshold)
        );
    }
    #[test]
    fn missing_host_or_unready_wall_cannot_supply_a_threshold_or_force_a_ring() {
        let mut p = house(0., true, false);
        opening(&mut p, OpeningKind::Door, true, 0.);
        p.placement_decisions
            .as_mut()
            .unwrap()
            .iter_mut()
            .find(|d| d.wall_id == "north-right")
            .unwrap()
            .status = WallStatus::Review;
        let c = build(&p).unwrap();
        assert!(
            c.edges
                .iter()
                .all(|e| e.kind != ConnectionKind::DoorThreshold)
        );
        assert!(c.boundary_candidates.is_empty());
        assert!(c.diagnostics.iter().any(|d| d.contains("unsupported jamb")));
        assert!(c.omitted_walls.iter().any(|w| w.wall_id == "north-right"));
    }
    #[test]
    fn duplicate_walls_merge_edge_provenance_and_do_not_create_a_wall_ribbon_area() {
        let mut p = house(0., false, false);
        wall(
            &mut p,
            "north-copy",
            Point::new(200., 200.),
            Point::new(300., 200.),
            2.,
        );
        let c = build(&p).unwrap();
        assert_eq!(c.boundary_candidates.len(), 1);
        assert!(c.edges.iter().any(|e| e.kind == ConnectionKind::Wall
            && e.wall_ids.contains(&"north".into())
            && e.wall_ids.contains(&"north-copy".into())));
        assert!((c.boundary_candidates[0].area_drawing_units_squared - 10_000.).abs() < 1e-6);
    }
    #[test]
    fn partition_t_junction_does_not_become_an_uncertain_exterior_gap() {
        let mut p = house(0., false, false);
        wall(
            &mut p,
            "partition",
            Point::new(250., 200.),
            Point::new(250., 300.),
            2.,
        );
        let c = build(&p).unwrap();
        assert_eq!(c.boundary_candidates.len(), 1);
        assert!(c.uncertain_gaps.is_empty());
        assert_eq!(c.components.len(), 1);
        assert!(!c.components[0].closed && c.components[0].branching);
    }
    #[test]
    fn interior_cycle_is_not_promoted_by_merely_being_inside_an_old_house_outline() {
        let mut p = house(0., false, false);
        p.exterior_enclosures = Some(vec![crate::placement::enclosures::ExteriorEnclosure {
            id: "source-exterior".into(),
            page_number: 1,
            closed: true,
            polygon: vec![
                Point::new(180., 180.),
                Point::new(320., 180.),
                Point::new(320., 320.),
                Point::new(180., 320.),
            ],
            area_drawing_units_squared: 19_600.,
            boundary_edges: vec![],
            boundary_ready_wall_ids: vec![],
            requires_review: true,
        }]);
        let c = build(&p).unwrap();
        assert_eq!(
            c.boundary_candidates[0].exterior_status,
            "exterior_role_unresolved"
        );
    }
    #[test]
    fn no_ready_material_is_explicitly_unresolved_and_curves_are_not_chords() {
        let mut p = house(0., false, false);
        for d in p.placement_decisions.as_mut().unwrap() {
            d.status = WallStatus::Review;
        }
        let c = build(&p).unwrap();
        assert!(c.nodes.is_empty() && c.boundary_candidates.is_empty());
        assert_eq!(c.omitted_walls.len(), 4);
        assert!(
            c.diagnostics
                .iter()
                .any(|d| d.contains("No placement-ready"))
        );
        p.placement_decisions.as_mut().unwrap()[0].status = WallStatus::Ready;
        p.walls[0].curve = Some(ArcGeometry {
            center: Point::new(250., 200.),
            radius: 50.,
            start_angle: 0.,
            sweep: std::f64::consts::PI,
        });
        let c = build(&p).unwrap();
        assert!(c.edges.is_empty());
        assert!(
            c.omitted_walls
                .iter()
                .any(|w| w.reason == "curved_wall_connection_requires_review")
        );
    }
    #[test]
    fn dense_unrelated_annotation_does_not_remove_an_otherwise_supported_boundary() {
        let mut p = house(0., false, false);
        for i in 0..3000 {
            p.primitives.push(Primitive {
                id: format!("note:{i}"),
                layer: Some("TEXT".into()),
                stroke_width: Some(0.1),
                layer_category_override: None,
                shape: Shape::Line {
                    start: Point::new(240., 240.),
                    end: Point::new(241., 241.),
                },
            });
        }
        let c = build(&p).unwrap();
        assert_eq!(c.boundary_candidates.len(), 1);
    }
    #[test]
    fn graph_boundary_node_and_edge_order_agree_and_replay_is_deterministic() {
        let p = house(0.67, false, false);
        let a = build(&p).unwrap();
        let b = build(&p).unwrap();
        assert_eq!(a, b);
        let edges: HashMap<_, _> = a.edges.iter().map(|e| (e.id.as_str(), e)).collect();
        for ring in &a.boundary_candidates {
            for i in 0..ring.node_ids.len() {
                let edge = edges[ring.edge_ids[i].as_str()];
                let x = &ring.node_ids[i];
                let y = &ring.node_ids[(i + 1) % ring.node_ids.len()];
                assert!(
                    (&edge.start_node_id == x && &edge.end_node_id == y)
                        || (&edge.end_node_id == x && &edge.start_node_id == y)
                );
            }
        }
    }
    #[test]
    fn over_limit_face_is_explicitly_incomplete_despite_a_degree_two_component() {
        let p = page();
        let mut report = build(&p).unwrap();
        let mut graph = Builder::new(1);
        let count = MAX_FACE_VERTICES + 1;
        let at = |i: usize| {
            let a = std::f64::consts::TAU * (i % count) as f64 / count as f64;
            Point::new(200. + a.cos() * 100., 200. + a.sin() * 100.)
        };
        for i in 0..count {
            graph
                .edge(Link {
                    a: at(i),
                    b: at(i + 1),
                    kind: ConnectionKind::Wall,
                    wall_ids: vec![],
                    opening_ids: vec![],
                    source_ids: vec![],
                    evidence: "bounded_walk_test",
                    anchors: [None, None],
                })
                .unwrap();
        }
        report.nodes = graph.nodes;
        report.edges = graph.edges;
        finish(
            &p,
            &mut report,
            epsilon(&p),
            &mut MAX_WORK.clone(),
            &HashSet::new(),
            ExteriorLinks::None,
        )
        .unwrap();
        assert!(report.components[0].closed);
        assert!(report.boundary_candidates.is_empty());
        assert!(
            report
                .diagnostics
                .iter()
                .any(|d| d.contains("4096-vertex review limit"))
        );
    }
}
#[test]
fn measured_terminal_corner_has_no_direct_shortcut_across_empty_quadrant() {
    let a = Solid {
        axis_offset: Point::default(),
        a: Point::new(0., 0.),
        b: Point::new(10., 0.),
        half: 1.,
        wall: 0,
        cuts: vec![0., 1.],
    };
    let mut b = Solid {
        axis_offset: Point::default(),
        a: Point::new(11., 1.),
        b: Point::new(11., 8.),
        half: 1.,
        wall: 1,
        cuts: vec![0., 1.],
    };
    let eps = 1e-10;
    assert!(contact(&a, &b, eps).is_none());
    let point = shared_terminal_corner(&a, &b, eps).unwrap();
    assert_eq!(point, Point::new(10., 1.));
    for s in [&a, &b] {
        assert!(covered_by_union(
            s.project(point).0,
            point,
            &[s.rect()],
            eps
        ));
    }
    b.a.x += 0.001;
    b.b.x += 0.001;
    assert!(shared_terminal_corner(&a, &b, eps).is_none());
}
