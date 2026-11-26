//! A painted interior run with one visible finish and two independent end plates.
use super::{WallDecision, WallReason, WallStatus, effective_wall, painted_rectangle, roundoff};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Wall};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 16_000_000;

#[derive(Clone, Copy)]
struct Rect {
    start: Point,
    axis: Point,
    length: f64,
    width: f64,
}

impl Rect {
    fn new(a: Point, b: Point, width: f64) -> Option<Self> {
        let length = a.distance(b);
        (a.finite()
            && b.finite()
            && length.is_finite()
            && length > 0.
            && width.is_finite()
            && width > 0.)
            .then_some(Self {
                start: a,
                axis: (b - a).unit(),
                length,
                width,
            })
    }

    fn at(self, p: Point) -> Point {
        let v = p - self.start;
        Point::new(v.dot(self.axis), self.axis.cross(v))
    }

    fn normal(self) -> Point {
        Point::new(-self.axis.y, self.axis.x)
    }

    fn end(self) -> Point {
        self.start + self.axis * self.length
    }

    fn conflicts(self, other: Self, eps: f64) -> bool {
        let delta = (other.start - self.start)
            + (other.axis * other.length - self.axis * self.length) * 0.5;
        let radius = |r: Self, direction: Point| {
            0.5 * (r.length * direction.dot(r.axis).abs()
                + r.width * direction.dot(r.normal()).abs())
        };
        [self.axis, self.normal(), other.axis, other.normal()]
            .into_iter()
            .all(|axis| radius(self, axis) + radius(other, axis) - delta.dot(axis).abs() > eps)
    }

    fn same_wall_course(self, other: Self) -> bool {
        if self.axis.cross(other.axis).abs() > 1e-5 {
            return false;
        }
        let [a, b] = [self.at(other.start), self.at(other.end())];
        let overlap = self.length.min(a.x.max(b.x)) - 0.0_f64.max(a.x.min(b.x));
        let margin = a.y.abs().min(b.y.abs()) - 0.5 * (self.width + other.width);
        overlap >= 0.55 * self.length.min(other.length)
            && margin <= 0.10 * self.width.min(other.width)
    }
}

#[derive(Clone, Copy)]
struct Band<'a> {
    id: &'a str,
    corners: [Point; 4],
}

impl Band<'_> {
    fn projected(self, rect: Rect, eps: f64) -> Option<([f64; 2], [f64; 2])> {
        let points = self.corners.map(|p| rect.at(p));
        let bounds = |get: fn(Point) -> f64| {
            points
                .iter()
                .copied()
                .map(get)
                .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], x| {
                    [lo.min(x), hi.max(x)]
                })
        };
        let along = bounds(|p| p.x);
        let across = bounds(|p| p.y);
        along
            .into_iter()
            .all(|x| {
                across
                    .into_iter()
                    .all(|y| points.iter().any(|&p| p.distance(Point::new(x, y)) <= eps))
            })
            .then_some((along, across))
    }
}

fn source_layer(p: &Primitive) -> Option<&str> {
    let name = p.layer.as_deref()?;
    if !crate::layers::is_named_wall(name)
        || crate::layers::is_named_exterior_wall(name)
        || (p.layer_category_override.is_some() && p.layer_kind() != LayerKind::Wall)
    {
        return None;
    }
    name.to_lowercase()
        .split(|ch: char| !ch.is_alphanumeric())
        .any(|word| {
            matches!(
                word,
                "interior" | "internal" | "innervegg" | "innervegger" | "partition" | "partitions"
            )
        })
        .then_some(name)
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(path, _)| path)
}

fn proof<'a>(
    body: Rect,
    core: &'a str,
    layer: &[Band<'a>],
    eps: f64,
    work: &mut usize,
) -> Option<Option<[&'a str; 4]>> {
    *work = work.checked_sub(layer.len())?;
    let half = body.width * 0.5;
    let equal = |a: f64, b: f64| (a - b).abs() <= eps;
    let mut has_core = false;
    let mut finishes = Vec::new();
    let mut caps = [Vec::new(), Vec::new()];
    for &band in layer {
        let Some(([a, b], [lo, hi])) = band.projected(body, eps) else {
            continue;
        };
        let full_width = equal(lo, -half) && equal(hi, half);
        if band.id == core {
            has_core = full_width && equal(a, 0.) && equal(b, body.length);
            continue;
        }
        if path(band.id) == path(core) {
            continue;
        }
        let depth = hi - lo;
        let uncovered = a.max(0.) + (body.length - b).max(0.);
        if depth > eps
            && depth <= body.width * 0.275
            && uncovered <= depth + eps.max(depth * 0.01)
            && (equal(lo, half) || equal(hi, -half))
        {
            finishes.push((depth, band.id));
        }
        let cap_depth = b - a;
        if full_width && cap_depth > eps && cap_depth <= body.width * 1.05 {
            for (i, touches) in [equal(b, 0.), equal(a, body.length)]
                .into_iter()
                .enumerate()
            {
                if touches {
                    caps[i].push((cap_depth, band.id));
                }
            }
        }
    }
    if !has_core || caps.iter().any(Vec::is_empty) {
        return Some(None);
    }
    let matches_depth = |a: f64, b: f64| (a - b).abs() <= eps.max(b * 0.01);
    for (depth, finish) in finishes {
        for &(lead, first) in &caps[0] {
            for &(trail, last) in &caps[1] {
                *work = work.checked_sub(1)?;
                let trim = matches_depth(lead, depth) && matches_depth(trail, depth);
                let returns = matches_depth(lead, body.width) && matches_depth(trail, body.width);
                let paths = [finish, first, last].map(path);
                if (trim || returns)
                    && paths[0] != paths[1]
                    && paths[0] != paths[2]
                    && paths[1] != paths[2]
                {
                    return Some(Some([core, finish, first, last]));
                }
            }
        }
    }
    Some(None)
}

fn ready_bounds(w: &Wall) -> Option<Rect> {
    if let Some(curve) = w.curve {
        let radius = curve.radius + 0.5 * w.thickness;
        let h = Point::new(radius, 0.);
        return Rect::new(curve.center - h, curve.center + h, 2. * radius);
    }
    Rect::new(w.start, w.end, w.thickness)
}

fn candidate_runs<'a>(
    page: &'a PageScan,
    decisions: &[WallDecision],
    work: &mut usize,
) -> Option<Vec<(usize, Rect, [&'a str; 4])>> {
    let receipts: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    if receipts.is_empty() {
        return Some(vec![]);
    }
    *work = work.checked_sub(page.primitives.len())?;
    let mut layers = HashMap::<&str, Vec<Band<'_>>>::new();
    let mut native_layers = HashMap::new();
    for p in page
        .primitives
        .iter()
        .filter(|p| receipts.contains(p.id.as_str()))
    {
        if let (Some(layer), Some(corners)) = (source_layer(p), painted_rectangle(&p.shape)) {
            layers
                .entry(layer)
                .or_default()
                .push(Band { id: &p.id, corners });
            native_layers.insert(p.id.as_str(), layer);
        }
    }
    if layers.is_empty() {
        return Some(vec![]);
    }
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(decisions)
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .filter_map(|(w, d)| ready_bounds(&effective_wall(w, Some(d))))
        .collect();
    let wall_width: HashMap<_, _> = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness))
        .collect();
    let mut host_ids = HashSet::new();
    let mut cuts = Vec::new();
    for o in page.opening_evidence() {
        host_ids.extend(o.host_wall_ids.iter().map(String::as_str));
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|id| wall_width.get(id.as_str()).copied())
            .fold(1e-5_f64, f64::max);
        cuts.extend(Rect::new(o.start, o.end, width));
        cuts.extend(
            o.swing_observations.iter().filter_map(|s| {
                Rect::new(s.hinge, s.closed_tip, (2. * s.leaf_tolerance).max(1e-5))
            }),
        );
    }
    let mut result = Vec::new();
    for (i, (w, d)) in page.walls.iter().zip(decisions).enumerate() {
        if w.body != BodyEvidence::FilledBody
            || w.curve.is_some()
            || d.status != WallStatus::Review
            || d.placement_extent.is_some()
            || !d.opening_ids.is_empty()
            || host_ids.contains(w.id.as_str())
        {
            continue;
        }
        let Some(body) =
            Rect::new(w.start, w.end, w.thickness).filter(|r| r.length >= 8. * r.width)
        else {
            continue;
        };
        let mut candidate = w.source_ids.iter().filter_map(|id| {
            native_layers
                .get(id.as_str())
                .map(|&layer| (id.as_str(), layer))
        });
        let Some((core, layer)) = candidate.next() else {
            continue;
        };
        if candidate.next().is_some() {
            continue;
        }
        let eps = roundoff(w).max(w.thickness * 1e-5);
        let Some(sources) = proof(body, core, &layers[layer], eps, work)? else {
            continue;
        };
        *work = work.checked_sub(ready.len().saturating_add(cuts.len()))?;
        if ready
            .iter()
            .any(|&r| body.conflicts(r, eps) || body.same_wall_course(r))
            || cuts.iter().any(|&c| body.conflicts(c, 1e-7))
        {
            continue;
        }
        result.push((i, body, sources));
    }
    Some(result)
}

pub(super) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let mut work = MAX_WORK;
    let Some(runs) = candidate_runs(page, decisions, &mut work) else {
        return;
    };
    if runs.len().saturating_pow(2) > work {
        return;
    }
    for &(i, body, sources) in &runs {
        let eps = roundoff(&page.walls[i]).max(body.width * 1e-5);
        if runs.iter().any(|&(j, r, proof)| {
            i != j && (sources[0] == proof[0] || body.conflicts(r, eps) || body.same_wall_course(r))
        }) {
            continue;
        }
        decisions[i].status = WallStatus::Ready;
        decisions[i].reason = WallReason::SourceBoundedInteriorPaintedRun;
        decisions[i].support_source_ids = sources
            .into_iter()
            .collect::<BTreeSet<_>>()
            .into_iter()
            .map(str::to_owned)
            .collect();
    }
}
