//! Finite vector stair flights. A repeated ladder alone is deliberately not a
//! staircase: the measured flight must contain its own drawn travel arrow.
use crate::model::*;
use std::collections::{BTreeSet, HashMap};

const MAX_SEGMENTS: usize = 60_000;
const MAX_WORK: usize = 12_000_000;
const MAX_ARROWS: usize = 128;

#[derive(Clone, Debug)]
pub(super) struct StairCandidate {
    pub polygon: [Point; 4],
    pub source_ids: Vec<String>,
    pub tread_count: usize,
    /// Actual source arrow endpoints; they do not imply up/down or storey.
    pub axis_start: Point,
    pub axis_end: Point,
}

#[derive(Clone, Copy)]
struct Edge {
    a: Point,
    b: Point,
    source: usize,
    detail: bool,
}

/// Collinear painted dashes can describe one finite object stroke. Keep the
/// original pieces as provenance; this never manufactures wall material.
fn dashed_strokes(
    page: &Page,
    edges: &mut Vec<Edge>,
    eps: f64,
    work: &mut usize,
) -> Result<HashMap<usize, Vec<usize>>, String> {
    struct Group {
        axis: Point,
        origin: Point,
        pieces: Vec<usize>,
    }
    struct Ink {
        lo: f64,
        hi: f64,
        pieces: Vec<usize>,
    }
    const BINS: i32 = 720;
    let angle_step = std::f64::consts::PI / BINS as f64;
    let coarse = (page.width.max(page.height) * 0.005).max(eps * 16.);
    let mut groups: Vec<Group> = vec![];
    let mut buckets: HashMap<(i32, i64, &str), Vec<usize>> = HashMap::new();
    for (i, e) in edges.iter().copied().enumerate().filter(|(_, e)| e.detail) {
        let d = (e.b - e.a).unit();
        let angle = d.y.atan2(d.x).rem_euclid(std::f64::consts::PI);
        let bin = (angle / angle_step).round() as i32 % BINS;
        let layer = page.primitives[e.source].layer_name();
        let mut found = None;
        'search: for delta in -1..=1 {
            let b = (bin + delta).rem_euclid(BINS);
            let q = Point::new((b as f64 * angle_step).cos(), (b as f64 * angle_step).sin());
            let position = (e.a.cross(q) / coarse).round() as i64;
            for offset in -1..=1 {
                if let Some(ids) = buckets.get(&(b, position + offset, layer)) {
                    charge(work, ids.len())?;
                    for &g in ids {
                        let group = &groups[g];
                        if d.dot(group.axis).abs() > 0.999998
                            && [e.a, e.b]
                                .iter()
                                .all(|p| (*p - group.origin).cross(group.axis).abs() <= eps * 2.)
                        {
                            found = Some(g);
                            break 'search;
                        }
                    }
                }
            }
        }
        if let Some(g) = found {
            groups[g].pieces.push(i);
        } else {
            let q = Point::new(
                (bin as f64 * angle_step).cos(),
                (bin as f64 * angle_step).sin(),
            );
            let position = (e.a.cross(q) / coarse).round() as i64;
            let axis = Point::new(angle.cos(), angle.sin());
            buckets
                .entry((bin, position, layer))
                .or_default()
                .push(groups.len());
            groups.push(Group {
                axis,
                origin: e.a,
                pieces: vec![i],
            });
        }
    }
    let mut added = vec![];
    for group in groups {
        if group.pieces.len() < 3 {
            continue;
        }
        let mut spans: Vec<_> = group
            .pieces
            .into_iter()
            .map(|i| {
                let e = edges[i];
                let a = (e.a - group.origin).dot(group.axis);
                let b = (e.b - group.origin).dot(group.axis);
                (a.min(b), a.max(b), i)
            })
            .collect();
        spans.sort_by(|a, b| {
            a.0.total_cmp(&b.0)
                .then(a.1.total_cmp(&b.1))
                .then(a.2.cmp(&b.2))
        });
        let mut ink: Vec<Ink> = vec![];
        for (lo, hi, i) in spans {
            if let Some(last) = ink.last_mut()
                && lo <= last.hi + eps * 2.
            {
                last.hi = last.hi.max(hi);
                last.pieces.push(i);
            } else {
                ink.push(Ink {
                    lo,
                    hi,
                    pieces: vec![i],
                });
            }
        }
        let mut begin = 0;
        for end in 1..=ink.len() {
            if end < ink.len()
                && ink[end].lo - ink[end - 1].hi
                    <= 2. * (ink[end].hi - ink[end].lo).max(ink[end - 1].hi - ink[end - 1].lo)
            {
                continue;
            }
            let whole = &ink[begin..end];
            begin = end;
            if whole.len() < 3 || whole.len() > 128 {
                continue;
            }
            // A CAD path can restart its dash phase at a real turn. Treat a
            // longer touching terminal ink as a boundary between separately
            // regular trains rather than rejecting the whole measured stroke.
            let mut typical: Vec<_> = whole[1..whole.len() - 1]
                .iter()
                .map(|r| r.hi - r.lo)
                .collect();
            typical.sort_by(f64::total_cmp);
            let typical = typical[typical.len() / 2];
            let mut ranges = vec![];
            let mut start = 0;
            for i in 1..whole.len() - 1 {
                if ((whole[i].hi - whole[i].lo) - typical).abs() > typical * 0.3 {
                    ranges.push((start, i + 1));
                    start = i;
                }
            }
            ranges.push((start, whole.len()));
            for (start, end) in ranges {
                let run = &whole[start..end];
                if run.len() < 3 {
                    continue;
                }
                charge(work, run.len())?;
                let mut lengths: Vec<_> =
                    run[1..run.len() - 1].iter().map(|r| r.hi - r.lo).collect();
                lengths.sort_by(f64::total_cmp);
                let dash = lengths[lengths.len() / 2];
                let mut gaps: Vec<_> = run.windows(2).map(|r| r[1].lo - r[0].hi).collect();
                gaps.sort_by(f64::total_cmp);
                let gap = gaps[gaps.len() / 2];
                if gap < dash * 0.15
                    || gap > dash * 2.
                    || gaps.iter().any(|v| (*v - gap).abs() > gap * 0.3)
                    || run.iter().enumerate().any(|(i, r)| {
                        let length = r.hi - r.lo;
                        if i == 0 || i + 1 == run.len() {
                            length < dash * 0.3 || length > dash * 1.8
                        } else {
                            (length - dash).abs() > dash * 0.3
                        }
                    })
                {
                    continue;
                }
                let density = run.iter().map(|r| r.hi - r.lo).sum::<f64>()
                    / (run[run.len() - 1].hi - run[0].lo);
                if !(0.3..=0.9).contains(&density) {
                    continue;
                }
                let parts: Vec<_> = run.iter().flat_map(|r| r.pieces.iter().copied()).collect();
                if parts
                    .iter()
                    .map(|&i| edges[i].source)
                    .collect::<BTreeSet<_>>()
                    .len()
                    < 3
                {
                    continue;
                }
                if added.len() == 4096 {
                    return Err("stair dashed stroke limit exceeded".into());
                }
                added.push((
                    Edge {
                        a: group.origin + group.axis * run[0].lo,
                        b: group.origin + group.axis * run[run.len() - 1].hi,
                        source: edges[parts[0]].source,
                        detail: true,
                    },
                    parts,
                ));
            }
        }
    }
    let mut sources = HashMap::new();
    for (edge, parts) in added {
        sources.insert(
            edges.len(),
            parts.into_iter().map(|i| edges[i].source).collect(),
        );
        edges.push(edge);
    }
    Ok(sources)
}
impl Edge {
    fn ends(self) -> [Point; 2] {
        [self.a, self.b]
    }
    fn other(self, p: Point) -> Point {
        if p.distance(self.a) < p.distance(self.b) {
            self.b
        } else {
            self.a
        }
    }
}

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("stair source geometry work limit exceeded")?;
    Ok(())
}

fn key(p: Point, eps: f64) -> (i64, i64) {
    ((p.x / eps).round() as i64, (p.y / eps).round() as i64)
}

struct Arrow {
    shaft: usize,
    wings: [usize; 2],
    start: Point,
    end: Point,
}

struct TriangleHead {
    tip: Point,
    base: Point,
    width: f64,
    sides: [usize; 2],
}

fn arrows(
    edges: &[Edge],
    heads: &[TriangleHead],
    eps: f64,
    work: &mut usize,
) -> Result<Vec<Arrow>, String> {
    let mut nodes: HashMap<(i64, i64), Vec<usize>> = HashMap::new();
    for (i, e) in edges.iter().enumerate().filter(|(_, e)| e.detail) {
        for p in e.ends() {
            nodes.entry(key(p, eps)).or_default().push(i);
        }
    }
    let mut filled: HashMap<(i64, i64), Vec<usize>> = HashMap::new();
    for (i, h) in heads.iter().enumerate() {
        filled.entry(key(h.base, eps)).or_default().push(i);
    }
    let mut out = vec![];
    for (i, e) in edges.iter().copied().enumerate().filter(|(_, e)| e.detail) {
        let length = e.a.distance(e.b);
        if length <= eps * 20. {
            continue;
        }
        for end in e.ends() {
            let start = e.other(end);
            let u = (end - start).unit();
            let k = key(end, eps);
            // A filled arrow joins the shaft at the center of its base, not
            // at its tip. Both triangle sides and its finite base are source
            // geometry; a free-floating triangle cannot corroborate a flight.
            for dx in -1..=1 {
                for dy in -1..=1 {
                    if let Some(indices) = filled.get(&(k.0 + dx, k.1 + dy)) {
                        charge(work, indices.len())?;
                        for &j in indices {
                            let h = &heads[j];
                            let delta = h.tip - end;
                            let height = delta.norm();
                            if h.base.distance(end) > eps * 2.
                                || delta.dot(u) <= 0.
                                || delta.cross(u).abs() > eps * 2.
                                || height < length * 0.025
                                || height > length * 0.4
                                || h.width < length * 0.008
                                || h.width > length * 0.4
                                || height < h.width * 0.3
                                || height > h.width * 3.
                            {
                                continue;
                            }
                            if out.len() == MAX_ARROWS {
                                return Err("stair arrow candidate limit exceeded".into());
                            }
                            out.push(Arrow {
                                shaft: i,
                                wings: h.sides,
                                start,
                                end: h.tip,
                            });
                        }
                    }
                }
            }
            let mut incident = BTreeSet::new();
            for dx in -1..=1 {
                for dy in -1..=1 {
                    if let Some(ids) = nodes.get(&(k.0 + dx, k.1 + dy)) {
                        charge(work, ids.len())?;
                        for &j in ids {
                            if j != i && edges[j].ends().iter().any(|p| p.distance(end) <= eps) {
                                incident.insert(j);
                            }
                        }
                    }
                }
            }
            if incident.len() > 24 {
                continue;
            }
            let wings: Vec<_> = incident
                .into_iter()
                .filter_map(|j| {
                    let v = edges[j].other(end) - end;
                    let n = v.norm();
                    (n >= length * 0.025
                        && n <= length * 0.4
                        && n > eps * 2.
                        && v.dot(u) < -n * 0.25
                        && v.dot(u) > -n * 0.95)
                        .then_some((j, v, n))
                })
                .collect();
            for (position, &(a, va, la)) in wings.iter().enumerate() {
                for &(b, vb, lb) in &wings[position + 1..] {
                    charge(work, 1)?;
                    if va.cross(u) * vb.cross(u) >= 0.
                        || la / lb < 0.6
                        || la / lb > 1.667
                        || (va.dot(u) - vb.dot(u)).abs() > la.max(lb) * 0.3
                        || (va.cross(u).abs() - vb.cross(u).abs()).abs() > la.max(lb) * 0.3
                    {
                        continue;
                    }
                    if out.len() == MAX_ARROWS {
                        return Err("stair arrow candidate limit exceeded".into());
                    }
                    out.push(Arrow {
                        shaft: i,
                        wings: [a, b],
                        start,
                        end,
                    });
                    break;
                }
                if out.last().is_some_and(|a| a.shaft == i && a.end == end) {
                    break;
                }
            }
        }
    }
    Ok(out)
}

#[derive(Clone, Copy)]
struct Rung {
    edge: usize,
    at: f64,
    low: f64,
    high: f64,
}

fn interior_segment(
    a: Point,
    b: Point,
    u: Point,
    v: Point,
    origin: Point,
    bounds: [f64; 4],
    pad: f64,
) -> bool {
    let p = a - origin;
    let q = b - origin;
    let values = [
        (p.dot(u), q.dot(u), bounds[0] + pad, bounds[1] - pad),
        (p.dot(v), q.dot(v), bounds[2] + pad, bounds[3] - pad),
    ];
    let (mut lo, mut hi) = (0.0_f64, 1.0_f64);
    for (a, b, min, max) in values {
        if min >= max {
            return false;
        }
        let d = b - a;
        if d.abs() < 1e-12 {
            if a <= min || a >= max {
                return false;
            }
        } else {
            let (x, y) = ((min - a) / d, (max - a) / d);
            lo = lo.max(x.min(y));
            hi = hi.min(x.max(y));
            if hi <= lo {
                return false;
            }
        }
    }
    hi > lo
}

fn flight(
    page: &Page,
    edges: &[Edge],
    arrow: &Arrow,
    dashed: &HashMap<usize, Vec<usize>>,
    walls: &[Wall],
    openings: &[Opening],
    eps: f64,
    work: &mut usize,
) -> Result<Vec<StairCandidate>, String> {
    let u = (arrow.end - arrow.start).unit();
    let v = Point::new(-u.y, u.x);
    let length = arrow.start.distance(arrow.end);
    let mut rungs = vec![];
    for (i, e) in edges.iter().copied().enumerate().filter(|(_, e)| e.detail) {
        charge(work, 1)?;
        if i == arrow.shaft || arrow.wings.contains(&i) {
            continue;
        }
        let a = e.a - arrow.start;
        let b = e.b - arrow.start;
        let width = e.a.distance(e.b);
        if width < length * 0.12 || width > length * 1.5 || (e.b - e.a).unit().dot(u).abs() > 0.002
        {
            continue;
        }
        let x = a.dot(v);
        let y = b.dot(v);
        let low = x.min(y);
        let high = x.max(y);
        let at = (a.dot(u) + b.dot(u)) * 0.5;
        if low >= -width * 0.12 || high <= width * 0.12 || at < -length * 0.6 || at > length * 1.6 {
            continue;
        }
        rungs.push(Rung {
            edge: i,
            at,
            low,
            high,
        });
    }
    if rungs.len() < 6 || rungs.len() > 512 {
        return Ok(vec![]);
    }
    let mut out = vec![];
    let mut used = BTreeSet::new();
    for seed in &rungs {
        if used.contains(&seed.edge) {
            continue;
        }
        let width = seed.high - seed.low;
        let tol = (width * 0.035).max(eps * 2.);
        let mut row: Vec<_> = rungs
            .iter()
            .copied()
            .filter(|r| (r.low - seed.low).abs() <= tol && (r.high - seed.high).abs() <= tol)
            .collect();
        charge(work, rungs.len())?;
        row.sort_by(|a, b| a.at.total_cmp(&b.at).then(a.edge.cmp(&b.edge)));
        row.dedup_by(|a, b| (a.at - b.at).abs() <= eps * 2.);
        if row.len() < 6 || row.len() > 48 {
            continue;
        }
        let mut gaps: Vec<_> = row.windows(2).map(|p| p[1].at - p[0].at).collect();
        gaps.sort_by(f64::total_cmp);
        let pitch = gaps[gaps.len() / 2];
        if pitch < width * 0.10 || pitch > width * 0.40 {
            continue;
        }
        // Split at actual missing or irregular tread intervals instead of
        // projecting a stair across a landing, wall or unmeasured empty gap.
        let mut from = 0;
        for end in 1..=row.len() {
            if end < row.len() && ((row[end].at - row[end - 1].at) - pitch).abs() <= pitch * 0.15 {
                continue;
            }
            let run = &row[from..end];
            from = end;
            if run.len() < 6 {
                continue;
            }
            let lo = run[0].at;
            let hi = run[run.len() - 1].at;
            if hi - lo < width * 0.8
                || length < (hi - lo) * 0.45
                || length > (hi - lo) * 1.45
                || 0. < lo - pitch * 1.5
                || 0. > hi + pitch * 1.5
                || length < lo - pitch * 1.5
                || length > hi + pitch * 1.5
            {
                continue;
            }
            let rails: Vec<_> = edges
                .iter()
                .copied()
                .enumerate()
                .filter_map(|(i, e)| {
                    let a = e.a - arrow.start;
                    let b = e.b - arrow.start;
                    ((e.b - e.a).unit().dot(u).abs() > 0.999998
                        && (a.dot(v) - b.dot(v)).abs() <= eps * 2.
                        && (a.dot(u).max(b.dot(u)).min(hi) - a.dot(u).min(b.dot(u)).max(lo))
                            .max(0.)
                            >= (hi - lo) * 0.8)
                        .then_some((i, (a.dot(v) + b.dot(v)) * 0.5))
                })
                .collect();
            charge(work, edges.len())?;
            let left = rails
                .iter()
                .filter(|(_, x)| (*x - seed.low).abs() <= tol)
                .min_by(|a, b| {
                    (a.1 - seed.low)
                        .abs()
                        .total_cmp(&(b.1 - seed.low).abs())
                        .then(a.0.cmp(&b.0))
                });
            let right = rails
                .iter()
                .filter(|(_, x)| (*x - seed.high).abs() <= tol)
                .min_by(|a, b| {
                    (a.1 - seed.high)
                        .abs()
                        .total_cmp(&(b.1 - seed.high).abs())
                        .then(a.0.cmp(&b.0))
                });
            let (Some(&(left, l)), Some(&(right, r))) = (left, right) else {
                continue;
            };
            if left == right || r <= l {
                continue;
            }
            // Keep only measured cross-lines inside both finite rails. An
            // overhanging edge does not justify extending a rail or erasing
            // the remainder of an otherwise proved flight.
            let span = |i: usize| {
                let e = edges[i];
                let a = (e.a - arrow.start).dot(u);
                let b = (e.b - arrow.start).dot(u);
                (a.min(b), a.max(b))
            };
            let (a, b) = span(left);
            let (c, d) = span(right);
            let supported: Vec<_> = run
                .iter()
                .copied()
                .filter(|r| r.at >= a.max(c) - eps && r.at <= b.min(d) + eps)
                .collect();
            if supported.len() < 6 {
                continue;
            }
            let run = supported.as_slice();
            let lo = run[0].at;
            let hi = run[run.len() - 1].at;
            if hi - lo < width * 0.8
                || length > (hi - lo) * 1.45
                || 0. < lo - pitch * 1.5
                || 0. > hi + pitch * 1.5
                || length < lo - pitch * 1.5
                || length > hi + pitch * 1.5
            {
                continue;
            }
            let bounds = [lo, hi, l, r];
            let mut ids = BTreeSet::new();
            for i in [arrow.shaft, arrow.wings[0], arrow.wings[1], left, right]
                .into_iter()
                .chain(run.iter().map(|r| r.edge))
            {
                if let Some(parts) = dashed.get(&i) {
                    ids.extend(
                        parts
                            .iter()
                            .map(|&source| page.primitives[source].id.clone()),
                    );
                } else {
                    ids.insert(page.primitives[edges[i].source].id.clone());
                }
            }
            // Structural material may bound a flight, but cannot run through
            // its interior. A false wall paired from the same stair linework
            // is not independent material evidence and is left for wall review.
            charge(work, walls.len().saturating_add(openings.len()))?;
            let independent_wall = walls.iter().any(|w| {
                if w.curve.is_some() || w.thickness <= 0. {
                    return false;
                }
                let same_linework = w.body == BodyEvidence::ParallelFaces
                    && !w.source_ids.is_empty()
                    && w.source_ids.iter().all(|id| ids.contains(id));
                !same_linework
                    && interior_segment(
                        w.start,
                        w.end,
                        u,
                        v,
                        arrow.start,
                        bounds,
                        (width * 0.06 - w.thickness * 0.5).max(eps),
                    )
            });
            if independent_wall
                || openings
                    .iter()
                    .any(|o| interior_segment(o.start, o.end, u, v, arrow.start, bounds, eps))
            {
                continue;
            }
            if ids.is_empty() {
                continue;
            }
            let at = |x, y| arrow.start + u * x + v * y;
            let polygon = [at(lo, l), at(hi, l), at(hi, r), at(lo, r)];
            for rung in run {
                used.insert(rung.edge);
            }
            out.push(StairCandidate {
                polygon,
                source_ids: ids.into_iter().collect(),
                tread_count: run.len(),
                axis_start: arrow.start,
                axis_end: arrow.end,
            });
        }
    }
    Ok(out)
}

pub(super) fn detect(
    page: &Page,
    walls: &[Wall],
    openings: &[Opening],
    opts: &Options,
) -> Result<Vec<StairCandidate>, String> {
    let eps = (page.width.max(page.height) * 1e-6)
        .max(opts.snap_tolerance * 0.005)
        .max(1e-8);
    let mut edges = vec![];
    let mut heads = vec![];
    for (source, p) in page.primitives.iter().enumerate() {
        let kind = p.layer_kind();
        if matches!(
            kind,
            LayerKind::Door
                | LayerKind::Window
                | LayerKind::Dimension
                | LayerKind::Grid
                | LayerKind::Annotation
                | LayerKind::Room
        ) {
            continue;
        }
        let detail = matches!(kind, LayerKind::Unknown | LayerKind::Object)
            && !matches!(p.shape, Shape::Polyline { filled: true, .. });
        let first = edges.len();
        let mut add_edge = |a: Point, b: Point| -> Result<(), String> {
            if a.distance(b) <= eps * 2. {
                return Ok(());
            }
            if edges.len() == MAX_SEGMENTS {
                return Err("stair source segment limit exceeded".into());
            }
            edges.push(Edge {
                a,
                b,
                source,
                detail,
            });
            Ok(())
        };
        match &p.shape {
            Shape::Line { start, end } => add_edge(*start, *end)?,
            Shape::Polyline { points, closed, .. } => {
                for pair in points.windows(2) {
                    add_edge(pair[0], pair[1])?;
                }
                if *closed && points.len() > 2 && points.first() != points.last() {
                    add_edge(points[points.len() - 1], points[0])?;
                }
            }
            _ => {}
        }
        if let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
            && matches!(kind, LayerKind::Unknown | LayerKind::Object)
        {
            let points = if points.len() > 1 && points.first() == points.last() {
                &points[..points.len() - 1]
            } else {
                points.as_slice()
            };
            if points.len() == 3 && edges.len() - first == 3 {
                for tip in points {
                    let base: Vec<_> = points.iter().copied().filter(|p| p != tip).collect();
                    if base.len() != 2 {
                        continue;
                    }
                    let sides: Vec<_> = (first..edges.len())
                        .filter(|&i| edges[i].ends().contains(tip))
                        .collect();
                    if sides.len() != 2 {
                        continue;
                    }
                    heads.push(TriangleHead {
                        tip: *tip,
                        base: (base[0] + base[1]) * 0.5,
                        width: base[0].distance(base[1]),
                        sides: [sides[0], sides[1]],
                    });
                }
            }
        }
    }
    let mut work = MAX_WORK;
    let dashed = dashed_strokes(page, &mut edges, eps, &mut work)?;
    let arrows = arrows(&edges, &heads, eps, &mut work)?;
    let mut out: Vec<StairCandidate> = vec![];
    for arrow in arrows {
        for candidate in flight(
            page, &edges, &arrow, &dashed, walls, openings, eps, &mut work,
        )? {
            if out.iter().any(|old| {
                old.polygon
                    .iter()
                    .all(|p| candidate.polygon.iter().any(|q| p.distance(*q) <= eps * 2.))
            }) {
                continue;
            }
            out.push(candidate);
        }
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture(angle: f64, scale: f64, arrow: bool) -> Page {
        let transform = |x, y| {
            Point::new(
                120. + scale * (x * angle.cos() - y * angle.sin()),
                80. + scale * (x * angle.sin() + y * angle.cos()),
            )
        };
        let mut primitives = vec![];
        let mut line = |x, y, xx, yy| {
            primitives.push(Primitive {
                id: format!("source-{}", primitives.len()),
                layer: None,
                stroke_width: Some(0.3 * scale),
                layer_category_override: None,
                shape: Shape::Line {
                    start: transform(x, y),
                    end: transform(xx, yy),
                },
            });
        };
        line(-20., -2., -20., 122.);
        line(20., -2., 20., 122.);
        for i in 0..=12 {
            line(-20., i as f64 * 10., 20., i as f64 * 10.);
        }
        if arrow {
            line(0., 5., 0., 115.);
            line(0., 115., -6., 105.);
            line(0., 115., 6., 105.);
        }
        Page {
            finish_history: None,
            number: 1,
            width: 500. * scale,
            height: 400. * scale,
            units_to_mm: None,
            primitives,
            material_tiles: vec![],
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            pdf_input: None,
        }
    }
    #[test]
    fn measured_flight_is_rotation_and_scale_independent() {
        for (angle, scale) in [(0., 1.), (0.42, 0.1), (1.6, 12.)] {
            let p = fixture(angle, scale, true);
            let d = detect(&p, &[], &[], &Options::default()).unwrap();
            assert_eq!(d.len(), 1);
            assert_eq!(d[0].tread_count, 13);
            assert!(d[0].source_ids.len() >= 18);
            assert_eq!(
                d[0].axis_start,
                match p.primitives[15].shape {
                    Shape::Line { start, .. } => start,
                    _ => unreachable!(),
                }
            );
        }
    }
    #[test]
    fn shelves_and_treads_without_a_travel_arrow_are_unclassified() {
        let p = fixture(0., 1., false);
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
        let mut p = fixture(0., 1., true);
        // A single slash or T junction is not an arrow head.
        p.primitives.pop();
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn filled_arrow_joins_at_its_base_but_a_floating_head_cannot_prove_travel() {
        let mut p = fixture(0., 1., true);
        p.primitives.truncate(16);
        p.primitives.push(Primitive {
            id: "filled-head".into(),
            layer: None,
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Polyline {
                points: vec![
                    Point::new(120., 205.),
                    Point::new(114., 195.),
                    Point::new(126., 195.),
                ],
                closed: true,
                filled: true,
            },
        });
        let found = detect(&p, &[], &[], &Options::default()).unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].axis_end, Point::new(120., 205.));
        assert!(found[0].source_ids.iter().any(|id| id == "filled-head"));
        let Shape::Polyline { points, .. } = &mut p.primitives[16].shape else {
            unreachable!()
        };
        for point in points {
            point.x += 2.;
        }
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn missing_rail_and_irregular_steps_cannot_borrow_an_arrow() {
        let mut p = fixture(0., 1., true);
        p.primitives.remove(0);
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
        let mut p = fixture(0., 1., true);
        for (i, p) in p.primitives[2..15].iter_mut().enumerate() {
            if let Shape::Line { start, end } = &mut p.shape {
                let shift = (i * i) as f64;
                start.y += shift;
                end.y += shift;
            }
        }
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn apertures_and_structural_material_through_the_flight_veto_it() {
        let p = fixture(0., 1., true);
        let wall = Wall {
            id: "structural".into(),
            page: 1,
            start: Point::new(90., 140.),
            end: Point::new(150., 140.),
            thickness: 4.,
            body: BodyEvidence::FilledBody,
            curve: None,
            source_ids: vec![],
            requires_review: false,
        };
        assert!(
            detect(&p, &[wall], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
        let door = Opening {
            physical_jambs: None,
            id: "door".into(),
            page: 1,
            kind: OpeningKind::Door,
            operation: "hinged".into(),
            host_wall_id: String::new(),
            host_wall_ids: vec![],
            start: Point::new(90., 140.),
            end: Point::new(150., 140.),
            hinge: None,
            swing_observations: vec![],
            source_ids: vec![],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
        };
        assert!(
            detect(&p, &[], &[door], &Options::default())
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn a_bare_swiss_style_footprint_has_no_symbol_evidence() {
        let mut p = fixture(0., 1., false);
        p.primitives.truncate(2);
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn regularly_painted_dashes_preserve_every_piece_as_object_evidence() {
        let source = fixture(0.37, 1., true);
        let mut p = source.clone();
        p.primitives.clear();
        for primitive in &source.primitives {
            let Shape::Line { start, end } = primitive.shape else {
                unreachable!()
            };
            for (piece, (lo, hi)) in [(0., 0.2), (0.4, 0.6), (0.8, 1.)].into_iter().enumerate() {
                let mut part = primitive.clone();
                part.id = format!("{}:paint:{}", part.id, piece);
                part.shape = Shape::Line {
                    start: start + (end - start) * lo,
                    end: start + (end - start) * hi,
                };
                p.primitives.push(part);
            }
        }
        let found = detect(&p, &[], &[], &Options::default()).unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].tread_count, 13);
        assert_eq!(found[0].source_ids.len(), 54);
        // Remove the middle painted interval of the travel shaft. Two remote
        // fragments cannot establish a regular dashed arrow by themselves.
        p.primitives.retain(|s| s.id != "source-15:paint:1");
        assert!(
            detect(&p, &[], &[], &Options::default())
                .unwrap()
                .is_empty()
        );
    }

    #[test]
    fn an_overhanging_crossline_cannot_extend_the_measured_rails() {
        let mut p = fixture(0., 1., true);
        p.primitives.push(Primitive {
            id: "extra-cross-line".into(),
            layer: None,
            stroke_width: Some(0.3),
            layer_category_override: None,
            shape: Shape::Line {
                start: Point::new(100., 70.),
                end: Point::new(140., 70.),
            },
        });
        let found = detect(&p, &[], &[], &Options::default()).unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].tread_count, 13);
        assert!(found[0].polygon.iter().all(|p| p.y >= 80.));
        assert!(
            !found[0]
                .source_ids
                .iter()
                .any(|id| id == "extra-cross-line")
        );
    }

    #[test]
    fn a_dash_phase_reset_preserves_the_regular_trains_on_both_sides() {
        let mut p = fixture(0., 1., false);
        p.primitives.clear();
        let mut edges = vec![];
        for (source, (lo, hi)) in [
            (0., 4.),
            (8., 12.),
            (16., 20.),
            (24., 30.),
            (34., 38.),
            (42., 46.),
        ]
        .into_iter()
        .enumerate()
        {
            let a = Point::new(20., lo);
            let b = Point::new(20., hi);
            p.primitives.push(Primitive {
                id: format!("dash-{source}"),
                layer: None,
                stroke_width: Some(0.2),
                layer_category_override: None,
                shape: Shape::Line { start: a, end: b },
            });
            edges.push(Edge {
                a,
                b,
                source,
                detail: true,
            });
        }
        let mut work = MAX_WORK;
        let sources = dashed_strokes(&p, &mut edges, 0.01, &mut work).unwrap();
        assert_eq!(sources.len(), 2);
        assert!(sources.values().any(|parts| parts.len() == 4));
        assert!(sources.values().any(|parts| parts.len() == 3));
        assert!(edges.iter().all(|e| e.a.y >= 0. && e.b.y <= 46.));
    }
}
