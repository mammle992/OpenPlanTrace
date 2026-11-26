//! Recover finite exterior masonry between measured jambs and adjoining wall paint.
use super::{WallDecision, WallReason, WallStatus, effective_wall, insulation, painted_rectangle};
use crate::model::{
    BodyEvidence, LayerKind, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive,
    Shape, Wall,
};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 22_000_000;
const MAX_SIGNED_WORK: usize = 4_000_000;
const BUDGET_EXCEEDED: &str = "exterior pier evidence budget exceeded";

fn charge(work: &mut usize, amount: usize) -> Result<(), String> {
    *work = work.checked_sub(amount).ok_or(BUDGET_EXCEEDED)?;
    Ok(())
}

#[derive(Clone, Copy)]
struct Bounds {
    min: Point,
    max: Point,
}

impl Bounds {
    fn of<const N: usize>(points: [Point; N]) -> Self {
        let mut bounds = Self {
            min: points[0],
            max: points[0],
        };
        for p in points.into_iter().skip(1) {
            bounds.min = Point::new(bounds.min.x.min(p.x), bounds.min.y.min(p.y));
            bounds.max = Point::new(bounds.max.x.max(p.x), bounds.max.y.max(p.y));
        }
        bounds
    }

    fn near(self, other: Self, margin: f64) -> bool {
        self.min.x <= other.max.x + margin
            && other.min.x <= self.max.x + margin
            && self.min.y <= other.max.y + margin
            && other.min.y <= self.max.y + margin
    }
}

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    layer: Option<&'a str>,
    native_layer: Option<&'a str>,
    corners: [Point; 4],
    bounds: Bounds,
}

struct Triangle<'a> {
    id: &'a str,
    points: [Point; 3],
    bounds: Bounds,
}

struct Ink<'a> {
    id: &'a str,
    layer: &'a str,
    start: Point,
    end: Point,
    bounds: Bounds,
}

struct SignedEvidence<'a> {
    openings: &'a [Opening],
    lines: Vec<(Ink<'a>, bool)>,
    arc_layers: HashMap<&'a str, &'a str>,
}

fn rectangle(shape: &Shape) -> Option<[Point; 4]> {
    if let Some(corners) = painted_rectangle(shape) {
        return Some(corners);
    }
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = shape
    else {
        return None;
    };
    if !(5..=24).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
        return None;
    }
    let mut corners = points.clone();
    if corners.first() == corners.last() {
        corners.pop();
    }
    while corners.len() > 4 {
        let n = corners.len();
        let at = (0..n).find(|&i| {
            let prev = corners[i] - corners[(i + n - 1) % n];
            let next = corners[(i + 1) % n] - corners[i];
            prev.cross(next) == 0. && prev.dot(next) > 0.
        })?;
        corners.remove(at);
    }
    let corners = corners.try_into().ok()?;
    insulation::rectangle(&corners).then_some(corners)
}

fn triangle(shape: &Shape) -> Option<[Point; 3]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = shape
    else {
        return None;
    };
    let points = match points.as_slice() {
        &[a, b, c] => [a, b, c],
        &[a, b, c, last] if a == last => [a, b, c],
        _ => return None,
    };
    let [a, b, c] = points;
    (points.iter().all(|p| p.finite()) && (b - a).cross(c - a).abs() > 1e-10).then_some(points)
}

fn structural(p: &Primitive) -> bool {
    match p.layer_category_override {
        Some(_) => p.layer_kind() == LayerKind::Wall,
        None => p.layer.as_deref().is_some_and(crate::layers::is_named_wall),
    }
}

fn draw_path(s: &str) -> &str {
    s.split_once(":subpath:").map_or(s, |(root, _)| root)
}

struct Frame {
    from: Point,
    to: Point,
    axis: Point,
    length: f64,
    half: f64,
    eps: f64,
}

impl Frame {
    fn new(c: [Point; 4], orientation: usize) -> Option<Self> {
        let (a, b) = if orientation == 0 {
            ((c[0] + c[3]) * 0.5, (c[1] + c[2]) * 0.5)
        } else {
            ((c[0] + c[1]) * 0.5, (c[2] + c[3]) * 0.5)
        };
        let width = if orientation == 0 {
            c[0].distance(c[3])
        } else {
            c[0].distance(c[1])
        };
        let length = a.distance(b);
        if width < 2. || length < width * 0.20 || length > width * 5. {
            return None;
        }
        let (from, to) = if (a.x, a.y) <= (b.x, b.y) {
            (a, b)
        } else {
            (b, a)
        };
        let eps = width * 1e-5;
        Some(Self {
            from,
            to,
            axis: (to - from).unit(),
            length,
            half: width * 0.5,
            eps,
        })
    }

    fn at(&self, p: Point) -> Point {
        let q = p - self.from;
        Point::new(q.dot(self.axis), self.axis.cross(q))
    }

    fn rectangle(&self, p: Paint<'_>) -> Option<Bounds> {
        let points = p.corners.map(|p| self.at(p));
        let b = Bounds::of(points);
        let [lo, hi] = [b.min, b.max];
        ([lo.x, hi.x].into_iter().all(|x| {
            [lo.y, hi.y].into_iter().all(|y| {
                points
                    .iter()
                    .any(|p| p.distance(Point::new(x, y)) <= self.eps)
            })
        }))
        .then_some(b)
    }

    fn pier(&self, page: u32, paint: &str) -> Wall {
        Wall {
            id: String::new(),
            page,
            start: self.from,
            end: self.to,
            thickness: 2. * self.half,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids: vec![paint.into()],
            requires_review: true,
        }
    }
}

type Interval<'a> = (f64, f64, &'a str);

fn union<'a>(parts: &mut [Interval<'a>], extent: [f64; 2], eps: f64) -> (f64, Vec<&'a str>) {
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = extent[0];
    let mut coverage = 0.;
    let mut sources = vec![];
    for &(lo, hi, id) in parts.iter() {
        let l = lo.max(end);
        let r = hi.min(extent[1]);
        if r > l + eps {
            coverage += r - l;
            end = r;
            sources.push(id);
        }
    }
    (coverage, sources)
}

struct Proof {
    wall: Wall,
    sources: Vec<String>,
    signed: bool,
}

fn signed_terminal<'a>(
    frame: &Frame,
    core: Paint<'a>,
    paints: &[Paint<'a>],
    source: &SignedEvidence<'a>,
    work: &mut usize,
) -> Result<Option<Vec<&'a str>>, String> {
    let Some(layer) = core.layer else {
        return Ok(None);
    };
    let width = frame.half * 2.;
    let eps = frame.eps;
    let same = |a: f64, b: f64| (a - b).abs() <= eps;
    charge(work, source.openings.len())?;
    for opening in source.openings {
        if opening.kind != OpeningKind::Door
            || !matches!(
                opening.evidence,
                OpeningEvidence::FiniteJambSwing
                    | OpeningEvidence::WallGapSwing
                    | OpeningEvidence::AuthoredExteriorSwingCasing
            )
            || !opening.source_ids.iter().any(|id| id == core.id)
            || !opening
                .swing_observations
                .iter()
                .any(|s| source.arc_layers.get(s.arc_source_id.as_str()) == Some(&layer))
        {
            continue;
        }
        let [a, b] = [opening.start, opening.end].map(|p| frame.at(p));
        if !same(a.y, b.y) || a.y.abs() > frame.half + eps {
            continue;
        }
        let side = if (-0.4 * width..=eps).contains(&a.x.max(b.x)) {
            0
        } else if (-eps..=0.4 * width).contains(&(a.x.min(b.x) - frame.length)) {
            1
        } else {
            continue;
        };
        let terminal = [0., frame.length][side];
        let far = [frame.length, 0.][side];
        let outward = if side == 0 { 1. } else { -1. };
        let mut caps = Vec::new();
        let mut face = [None; 2];
        let mut casing = [None; 2];
        charge(
            work,
            source
                .lines
                .len()
                .saturating_mul(opening.source_ids.len().saturating_add(1)),
        )?;
        for (line, stroked) in source
            .lines
            .iter()
            .filter(|(r, _)| r.layer == layer && draw_path(r.id) != draw_path(core.id))
        {
            let [p, q] = [line.start, line.end].map(|p| frame.at(p));
            if *stroked
                && same(p.x, terminal)
                && same(q.x, terminal)
                && opening.source_ids.iter().any(|id| id == line.id)
            {
                caps.push((p.y.min(q.y), p.y.max(q.y), line.id));
            }
            if !same(p.y, q.y) {
                continue;
            }
            let [lo, hi] = [p.x.min(q.x), p.x.max(q.x)];
            for (i, sign) in [-1., 1.].into_iter().enumerate() {
                if same(lo, 0.) && same(hi, frame.length) && same(p.y, sign * frame.half) {
                    face[i].get_or_insert(line.id);
                }
                if *stroked
                    && (width * 0.55..=width * 1.1).contains(&(p.y * sign))
                    && if side == 0 {
                        same(lo, 0.) && hi >= frame.length - eps
                    } else {
                        same(hi, frame.length) && lo <= eps
                    }
                {
                    casing[i].get_or_insert(line.id);
                }
            }
        }
        let (coverage, mut cap_sources) = union(&mut caps, [-frame.half, frame.half], eps);
        if coverage < width - eps {
            continue;
        }
        let Some((face, casing)) =
            face.into_iter()
                .zip(casing.into_iter().rev())
                .find_map(|(f, c)| {
                    let (f, c) = (f?, c?);
                    (draw_path(f) != draw_path(c)).then_some((f, c))
                })
        else {
            continue;
        };

        // A visible structural facing must meet the whole opposite terminal,
        // with an independently visible same-layer body immediately behind it.
        let mut stock = Vec::new();
        for p in paints
            .iter()
            .take_while(|p| p.bounds.min.x <= core.bounds.max.x + width * 3.)
        {
            charge(work, 1)?;
            if p.native_layer.is_none()
                || draw_path(p.id) == draw_path(core.id)
                || !p.bounds.near(core.bounds, width * 3.)
            {
                continue;
            }
            let Some(b) = frame.rectangle(*p) else {
                continue;
            };
            if !same(b.min.y, -frame.half) || !same(b.max.y, frame.half) {
                continue;
            }
            let [x0, x1] = [(b.min.x - far) * outward, (b.max.x - far) * outward];
            let [lo, hi] = [x0.min(x1), x0.max(x1)];
            if lo >= -eps && hi <= width * 3. {
                stock.push((lo, hi, p));
            }
        }
        charge(work, stock.len().saturating_pow(2))?;
        for &(lo, hi, p) in &stock {
            if !same(lo, 0.) || !(width * 0.035..=width * 0.5).contains(&hi) {
                continue;
            }
            if let Some((_, _, back)) = stock.iter().find(|&&(start, end, back)| {
                same(start, hi)
                    && end - start >= width * 0.35
                    && back.native_layer == p.native_layer
                    && draw_path(back.id) != draw_path(p.id)
            }) {
                cap_sources.extend([face, casing, p.id, back.id]);
                return Ok(Some(cap_sources));
            }
        }
    }
    Ok(None)
}

fn prove<'a>(
    frame: &Frame,
    core: Paint<'a>,
    paints: &[Paint<'a>],
    triangles: &[Triangle<'a>],
    ink: &[Ink<'a>],
    signed: Option<&SignedEvidence<'a>>,
    signed_work: &mut usize,
    page: u32,
    work: &mut usize,
) -> Result<Option<Proof>, String> {
    let [along0, along1] = [0., frame.length];
    let [lo, hi] = [-frame.half, frame.half];
    let eps = frame.eps;
    let mut finish = [vec![], vec![]];
    let mut end = [vec![], vec![]];
    let mut side = [vec![], vec![]];
    for p in paints
        .iter()
        .take_while(|p| p.bounds.min.x <= core.bounds.max.x + eps)
    {
        charge(work, 1)?;
        if !p.bounds.near(core.bounds, eps) || draw_path(p.id) == draw_path(core.id) {
            continue;
        }
        let Some(r) = frame.rectangle(*p) else {
            continue;
        };
        if (r.max.x - along0).abs() <= eps {
            end[0].push((r.min.y, r.max.y, p.id));
        }
        if (r.min.x - along1).abs() <= eps {
            end[1].push((r.min.y, r.max.y, p.id));
        }
        for (index, touch, depth) in [
            (0, r.max.y - lo, lo - r.min.y),
            (1, r.min.y - hi, r.max.y - hi),
        ] {
            if touch.abs() > eps {
                continue;
            }
            if depth <= frame.half * 0.9 && p.layer == core.layer {
                finish[index].push((r.min.x, r.max.x, p.id));
            } else if depth >= frame.half * 0.9 {
                side[index].push((r.min.x, r.max.x, p.id));
            }
        }
    }
    let edge = |i| [along0, along1][i];
    for t in triangles
        .iter()
        .take_while(|t| t.bounds.min.x <= core.bounds.max.x + eps)
    {
        charge(work, 1)?;
        if !t.bounds.near(core.bounds, eps) || draw_path(t.id) == draw_path(core.id) {
            continue;
        }
        let points = t.points.map(|p| frame.at(p));
        for [i, j, k] in [[0, 1, 2], [1, 2, 0], [2, 0, 1]] {
            let [p, q, far] = [points[i], points[j], points[k]];
            for ((side, ends), outside) in end.iter_mut().enumerate().zip([-1., 1.]) {
                let terminal = edge(side);
                if (p.x - terminal).abs().max((q.x - terminal).abs()) <= eps
                    && (far.x - terminal) * outside > eps
                {
                    ends.push((p.y.min(q.y), p.y.max(q.y), t.id));
                }
            }
        }
    }
    let mut caps = [vec![], vec![]];
    for s in ink
        .iter()
        .take_while(|s| s.bounds.min.x <= core.bounds.max.x + eps)
    {
        charge(work, 1)?;
        if Some(s.layer) != core.layer
            || !s.bounds.near(core.bounds, eps)
            || draw_path(s.id) == draw_path(core.id)
        {
            continue;
        }
        let [p, q] = [s.start, s.end].map(|v| frame.at(v));
        for (i, intervals) in caps.iter_mut().enumerate() {
            if (p.x - edge(i)).abs().max((q.x - edge(i)).abs()) <= eps {
                intervals.push((p.y.min(q.y), p.y.max(q.y), s.id));
            }
        }
    }
    let finish = finish.each_mut().map(|p| union(p, [along0, along1], eps));
    let attached = end.each_mut().map(|p| union(p, [lo, hi], eps));
    let sided = side.each_mut().map(|p| union(p, [along0, along1], eps));
    let caps = caps.each_mut().map(|p| union(p, [lo, hi], eps));
    let along_proof = [0, 1]
        .into_iter()
        .all(|i| attached[i].0 >= 1.95 * frame.half || caps[i].0 >= 0.75 * frame.half);
    let skin_proof = finish
        .iter()
        .any(|(length, _)| *length >= frame.length * 0.75);
    let measured_corner = (frame.length - 2. * frame.half).abs() <= eps
        && skin_proof
        && attached
            .iter()
            .any(|(length, _)| *length >= 1.95 * frame.half)
        && sided
            .iter()
            .any(|(length, _)| *length >= frame.length * 0.975);
    let uses_signed_terminal = !(skin_proof && along_proof || measured_corner);
    let extra = if !uses_signed_terminal {
        Vec::new()
    } else if let Some(source) = signed.filter(|_| *signed_work > 0) {
        match signed_terminal(frame, core, paints, source, signed_work) {
            Ok(Some(proof)) => proof,
            Ok(None) => return Ok(None),
            Err(_) => {
                *signed_work = 0;
                return Ok(None);
            }
        }
    } else {
        return Ok(None);
    };
    let sources = std::iter::once(core.id)
        .chain(finish.into_iter().flat_map(|v| v.1))
        .chain(attached.into_iter().flat_map(|v| v.1))
        .chain(caps.into_iter().flat_map(|v| v.1))
        .chain(sided.into_iter().flat_map(|v| v.1))
        .chain(extra)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .map(str::to_owned)
        .collect();
    Ok(Some(Proof {
        wall: frame.pier(page, core.id),
        sources,
        signed: uses_signed_terminal,
    }))
}

pub(super) fn corners(start: Point, end: Point, width: f64) -> [Point; 4] {
    let axis = (end - start).unit();
    let across = Point::new(-axis.y, axis.x) * (width * 0.5);
    [start + across, end + across, end - across, start - across]
}

pub(super) fn overlaps(a: [Point; 4], b: [Point; 4], eps: f64) -> bool {
    [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]]
        .into_iter()
        .all(|dir| {
            let axis = dir.unit();
            if axis.norm() == 0. {
                return true;
            }
            let project = |c: [Point; 4]| {
                let t = c.map(|p| (p - a[0]).dot(axis));
                (
                    t.into_iter().fold(f64::INFINITY, f64::min),
                    t.into_iter().fold(f64::NEG_INFINITY, f64::max),
                )
            };
            let (p, q) = project(a);
            let (r, s) = project(b);
            q.min(s) - p.max(r) > eps
        })
}

pub(super) fn beside_ready_wall(pier: &Wall, other: [Point; 4]) -> bool {
    let axis = (pier.end - pier.start).unit();
    let existing_axis = (other[1] - other[0]).unit();
    if existing_axis.norm() == 0. || axis.cross(existing_axis).abs() > 1e-5 {
        return false;
    }
    let small_width = pier.thickness.min(other[0].distance(other[3]));
    let a = (other[0] + other[3]) * 0.5 - pier.start;
    let b = (other[1] + other[2]) * 0.5 - pier.start;
    let gap = axis.cross(a).abs().min(axis.cross(b).abs())
        - 0.5 * (pier.thickness + other[0].distance(other[3]));
    let [t, u] = [axis.dot(a), axis.dot(b)];
    let length = pier.start.distance(pier.end);
    let overlap = length.min(t.max(u)) - 0.0_f64.max(t.min(u));
    gap <= small_width * 0.045
        && overlap >= (length.min((t - u).abs()) * 0.55).max(4. * small_width)
}

pub(super) fn aperture_guard(page: &PageScan) -> Vec<[Point; 4]> {
    let widths = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness))
        .collect::<HashMap<_, _>>();
    page.opening_evidence()
        .flat_map(|o| {
            let width = o
                .host_wall_ids
                .iter()
                .filter_map(|id| widths.get(id.as_str()).copied())
                .fold(0., f64::max);
            std::iter::once(corners(o.start, o.end, width.max(1e-5))).chain(
                o.swing_observations
                    .iter()
                    .map(|s| corners(s.hinge, s.closed_tip, (2. * s.leaf_tolerance).max(1e-5))),
            )
        })
        .collect()
}

pub(super) fn append(
    page: &mut PageScan,
    decisions: &mut Vec<WallDecision>,
    signed_terminal_proof: bool,
) -> Result<(), String> {
    match append_measured(page, decisions, signed_terminal_proof) {
        Err(reason) if reason == BUDGET_EXCEEDED => Ok(()),
        result => result,
    }
}

fn append_measured(
    page: &mut PageScan,
    decisions: &mut Vec<WallDecision>,
    signed_terminal_proof: bool,
) -> Result<(), String> {
    let allowed = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    if allowed.is_empty() {
        return Ok(());
    }
    let mut work = MAX_WORK;
    let mut signed_work = MAX_SIGNED_WORK;
    charge(&mut work, page.primitives.len())?;
    let polygon_ids = page
        .placement_polygon_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let mut paints = vec![];
    let mut triangles = vec![];
    let mut ink = vec![];
    let mut signed = signed_terminal_proof.then(|| SignedEvidence {
        openings: &page.openings,
        lines: Vec::new(),
        arc_layers: HashMap::new(),
    });
    for p in &page.primitives {
        let layer = crate::layers::named_exterior_wall(p);
        if (layer.is_some() || structural(p)) && allowed.contains(p.id.as_str()) {
            if let Some(corners) = rectangle(&p.shape) {
                paints.push(Paint {
                    id: &p.id,
                    layer,
                    native_layer: p.layer.as_deref(),
                    corners,
                    bounds: Bounds::of(corners),
                });
            } else if polygon_ids.contains(p.id.as_str())
                && let Some(points) = triangle(&p.shape)
            {
                triangles.push(Triangle {
                    id: &p.id,
                    points,
                    bounds: Bounds::of(points),
                });
            }
        }
        if let Some(layer) = layer
            && p.stroke_width.is_some()
            && let Shape::Line { start, end } = p.shape
        {
            ink.push(Ink {
                id: &p.id,
                layer,
                start,
                end,
                bounds: Bounds::of([start, end, start, end]),
            });
        }
        if let (Some(data), Some(layer)) = (&mut signed, layer) {
            match p.shape {
                Shape::Arc { .. } => {
                    data.arc_layers.insert(p.id.as_str(), layer);
                }
                Shape::Line { start, end } => data.lines.push((
                    Ink {
                        id: &p.id,
                        layer,
                        start,
                        end,
                        bounds: Bounds::of([start, end, start, end]),
                    },
                    p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.),
                )),
                _ => {}
            }
        }
    }
    if ink.is_empty() {
        return Ok(());
    }
    paints.sort_by(|a, b| {
        a.bounds
            .min
            .x
            .total_cmp(&b.bounds.min.x)
            .then(a.id.cmp(b.id))
    });
    triangles.sort_by(|a, b| {
        a.bounds
            .min
            .x
            .total_cmp(&b.bounds.min.x)
            .then(a.id.cmp(b.id))
    });
    ink.sort_by(|a, b| {
        a.bounds
            .min
            .x
            .total_cmp(&b.bounds.min.x)
            .then(a.id.cmp(b.id))
    });
    let occupied = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| effective_wall(w, Some(d)))
        .filter(|w| w.curve.is_none())
        .map(|w| corners(w.start, w.end, w.thickness))
        .collect::<Vec<_>>();
    let apertures = aperture_guard(page);
    let mut proposals = vec![];
    for &paint in paints.iter().filter(|p| p.layer.is_some()) {
        let mut found: Option<Proof> = None;
        for orientation in [0, 1] {
            let Some(frame) = Frame::new(paint.corners, orientation) else {
                continue;
            };
            let Some(proof) = prove(
                &frame,
                paint,
                &paints,
                &triangles,
                &ink,
                signed.as_ref(),
                &mut signed_work,
                page.page_number,
                &mut work,
            )?
            else {
                continue;
            };
            if found
                .as_ref()
                .is_none_or(|previous| previous.signed && !proof.signed)
            {
                found = Some(proof);
            }
        }
        if let Some(proof) = found {
            let cost = occupied.len().saturating_add(apertures.len());
            if proof.signed {
                if charge(&mut signed_work, cost).is_err() {
                    signed_work = 0;
                    continue;
                }
            } else {
                charge(&mut work, cost)?;
            }
            if !occupied
                .iter()
                .any(|&b| overlaps(paint.corners, b, 1e-7) || beside_ready_wall(&proof.wall, b))
                && !apertures.iter().any(|&b| overlaps(paint.corners, b, 1e-7))
            {
                proposals.push((paint, proof));
            }
        }
    }
    let legacy = proposals.iter().filter(|(_, proof)| !proof.signed).count();
    charge(&mut work, legacy.saturating_pow(2))?;
    if charge(
        &mut signed_work,
        proposals
            .len()
            .saturating_pow(2)
            .saturating_sub(legacy.saturating_pow(2)),
    )
    .is_err()
    {
        proposals.retain(|(_, proof)| !proof.signed);
    }
    proposals.sort_by_key(|(_, proof)| proof.signed);
    let mut disputed = vec![false; proposals.len()];
    for (i, (a, first)) in proposals.iter().enumerate() {
        for (j, (b, second)) in proposals.iter().enumerate().skip(i + 1) {
            if a.bounds.near(b.bounds, 1e-7) && overlaps(a.corners, b.corners, 1e-7) {
                disputed[i] |= first.signed || !second.signed;
                disputed[j] |= second.signed || !first.signed;
            }
        }
    }
    let mut index = 0;
    for ((_, mut proof), ambiguous) in proposals.into_iter().zip(disputed) {
        if ambiguous {
            continue;
        }
        index += 1;
        proof.wall.id = format!("exterior-pier:{}:{index}", page.page_number);
        decisions.push(WallDecision {
            wall_id: proof.wall.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::SourceBoundedExteriorPier,
            component_id: format!("p{}:{}", page.page_number, proof.wall.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: proof.sources,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        });
        page.semantics
            .routing
            .push(crate::semantics::wall_route(&proof.wall));
        page.walls.push(proof.wall);
    }
    Ok(())
}
