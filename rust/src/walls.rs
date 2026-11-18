//! Physical body interpretation. Everything emitted here remains reviewable:
//! observing two faces is not a decision about structural inclusion or routing.
//! Distances are in source drawing units, as in the reference scanner.
use crate::model::{ArcGeometry, BodyEvidence, LayerKind, Options, Page, Point, Shape, Wall};
use std::collections::{BTreeMap, BTreeSet};
use std::f64::consts::{PI, TAU};

mod selection;
pub use selection::select;

const EPS: f64 = 1e-7;
const ANGLE: f64 = 2.0 * PI / 180.0;

#[derive(Clone, Copy, Debug)]
struct V(f64, f64);
impl V {
    fn from(p: &Point) -> Self {
        Self(p.x, p.y)
    }
    fn point(self) -> Point {
        Point::new(self.0, self.1)
    }
    fn plus(self, b: Self) -> Self {
        Self(self.0 + b.0, self.1 + b.1)
    }
    fn minus(self, b: Self) -> Self {
        Self(self.0 - b.0, self.1 - b.1)
    }
    fn times(self, t: f64) -> Self {
        Self(self.0 * t, self.1 * t)
    }
    fn dot(self, b: Self) -> f64 {
        self.0 * b.0 + self.1 * b.1
    }
    fn cross(self, b: Self) -> f64 {
        self.0 * b.1 - self.1 * b.0
    }
    fn norm(self) -> f64 {
        self.0.hypot(self.1)
    }
    fn normal(self) -> Self {
        Self(-self.1, self.0)
    }
    fn finite(self) -> bool {
        self.0.is_finite() && self.1.is_finite()
    }
}
#[derive(Clone, Copy)]
struct Box2(V, V);
impl Box2 {
    fn new(a: V, b: V) -> Self {
        Self(V(a.0.min(b.0), a.1.min(b.1)), V(a.0.max(b.0), a.1.max(b.1)))
    }
    fn union(self, b: Self) -> Self {
        Self(Self::new(self.0, b.0).0, Self::new(self.1, b.1).1)
    }
    fn pad(self, s: f64) -> Self {
        Self(self.0.minus(V(s, s)), self.1.plus(V(s, s)))
    }
    fn touches(self, b: Self) -> bool {
        self.0.0 <= b.1.0 && self.0.1 <= b.1.1 && self.1.0 >= b.0.0 && self.1.1 >= b.0.1
    }
    fn mid(self, vertical: bool) -> f64 {
        if vertical {
            (self.0.1 + self.1.1) * 0.5
        } else {
            (self.0.0 + self.1.0) * 0.5
        }
    }
}
struct Node {
    bounds: Box2,
    start: usize,
    count: usize,
    children: Option<(usize, usize)>,
}
struct Index {
    indices: Vec<usize>,
    nodes: Vec<Node>,
}
impl Index {
    fn build(boxes: &[Box2]) -> Self {
        let mut t = Self {
            indices: (0..boxes.len()).collect(),
            nodes: Vec::new(),
        };
        if !boxes.is_empty() {
            t.split(boxes, 0, boxes.len());
        }
        t
    }
    fn split(&mut self, boxes: &[Box2], start: usize, count: usize) -> usize {
        let mut bounds = boxes[self.indices[start]];
        for i in &self.indices[start + 1..start + count] {
            bounds = bounds.union(boxes[*i]);
        }
        let idx = self.nodes.len();
        self.nodes.push(Node {
            bounds,
            start,
            count,
            children: None,
        });
        if count > 12 {
            let vertical = bounds.1.1 - bounds.0.1 > bounds.1.0 - bounds.0.0;
            let m = count / 2;
            self.indices[start..start + count].select_nth_unstable_by(m, |a, b| {
                boxes[*a]
                    .mid(vertical)
                    .total_cmp(&boxes[*b].mid(vertical))
                    .then(a.cmp(b))
            });
            let left = self.split(boxes, start, m);
            let right = self.split(boxes, start + m, count - m);
            self.nodes[idx].children = Some((left, right));
        }
        idx
    }
    fn neighbors(&self, b: Box2, out: &mut Vec<usize>) {
        out.clear();
        if !self.nodes.is_empty() {
            self.visit(0, b, out);
        }
    }
    fn visit(&self, idx: usize, b: Box2, out: &mut Vec<usize>) {
        let n = &self.nodes[idx];
        if !n.bounds.touches(b) {
            return;
        }
        if let Some((a, c)) = n.children {
            self.visit(a, b, out);
            self.visit(c, b, out);
        } else {
            out.extend_from_slice(&self.indices[n.start..n.start + n.count]);
        }
    }
}

#[derive(Clone, Copy)]
struct Segment {
    a: V,
    b: V,
    dir: V,
    len: f64,
}
impl Segment {
    fn new(mut a: V, mut b: V) -> Option<Self> {
        if !a.finite() || !b.finite() {
            return None;
        }
        let d = b.minus(a);
        // Canonical endpoints independent of source traversal, including reversed obliques.
        if d.1 < -EPS || (d.1.abs() <= EPS && d.0 < 0.0) {
            std::mem::swap(&mut a, &mut b);
        }
        let v = b.minus(a);
        let len = v.norm();
        if !len.is_finite() || len <= EPS {
            return None;
        }
        Some(Self {
            a,
            b,
            dir: v.times(1.0 / len),
            len,
        })
    }
    fn bbox(self) -> Box2 {
        Box2::new(self.a, self.b)
    }
    fn at(self, t: f64) -> V {
        self.a.plus(self.dir.times(t))
    }
    fn offset(self, p: V) -> f64 {
        p.minus(self.a).dot(self.dir)
    }
}
struct Seed {
    line: Segment,
    id: usize,
    explicit: bool,
}
struct Face {
    line: Segment,
    parts: Vec<(Segment, usize)>,
    explicit: bool,
}
impl Face {
    fn sources(&self, axis: Segment) -> impl Iterator<Item = usize> + '_ {
        self.parts.iter().filter_map(move |(p, id)| {
            let a = axis.offset(p.a);
            let b = axis.offset(p.b);
            (a.max(b).min(axis.len) - a.min(b).max(0.0) > EPS).then_some(*id)
        })
    }
}

/// Join only continuous observed coverage. Two collinear pieces separated by a
/// real gap remain distinct faces; this stage may not invent intervening wall.
fn merge_faces(seeds: Vec<Seed>) -> Vec<Face> {
    let mut groups: BTreeMap<(i64, i64, bool), Vec<Seed>> = BTreeMap::new();
    for s in seeds {
        let key = (
            (s.line.dir.1.atan2(s.line.dir.0) * 1e8).round() as i64,
            (s.line.a.dot(s.line.dir.normal()) * 1e5).round() as i64,
            s.explicit,
        );
        groups.entry(key).or_default().push(s);
    }
    let mut result = Vec::new();
    for mut group in groups.into_values() {
        let basis = group[0].line;
        group.sort_unstable_by(|p, q| {
            basis
                .offset(p.line.a)
                .total_cmp(&basis.offset(q.line.a))
                .then_with(|| q.line.len.total_cmp(&p.line.len))
                .then(p.id.cmp(&q.id))
        });
        let mut run: Option<Face> = None;
        for seed in group {
            if let Some(r) = run.as_mut() {
                let line = r.line;
                if line.offset(seed.line.a) <= line.len + EPS
                    && seed.line.a.minus(line.a).cross(line.dir).abs() < 1e-5
                    && seed.line.b.minus(line.a).cross(line.dir).abs() < 1e-5
                {
                    let end = line.len.max(line.offset(seed.line.b));
                    r.line = Segment {
                        b: line.at(end),
                        len: end,
                        ..line
                    };
                    r.parts.push((seed.line, seed.id));
                    continue;
                }
            }
            if let Some(done) = run.take() {
                result.push(done);
            }
            run = Some(Face {
                line: seed.line,
                parts: vec![(seed.line, seed.id)],
                explicit: seed.explicit,
            });
        }
        if let Some(done) = run {
            result.push(done);
        }
    }
    result
}

struct Body {
    axis: Segment,
    thickness: f64,
    kind: BodyEvidence,
    curve: Option<ArcGeometry>,
    ids: BTreeSet<usize>,
}

/// Occupied near-rectangular polygon, using the overlap of two observed
/// opposite edges. A bounding box alone is insufficient (L-shapes, hatches).
fn filled_body(points: &[Point], opt: &Options, id: usize) -> Option<Body> {
    if points.len() < 4 {
        return None;
    }
    let vs: Vec<_> = points.iter().map(V::from).collect();
    if vs.iter().any(|p| !p.finite()) {
        return None;
    }
    let edges: Vec<_> = vs
        .iter()
        .copied()
        .zip(vs.iter().copied().cycle().skip(1))
        .take(vs.len())
        .filter_map(|(a, b)| Segment::new(a, b))
        .collect();
    let first = *edges.iter().max_by(|a, b| a.len.total_cmp(&b.len))?;
    let origin = first.a;
    let n = first.dir.normal();
    let area = vs
        .iter()
        .zip(vs.iter().cycle().skip(1))
        .take(vs.len())
        .map(|(a, b)| a.minus(origin).cross(b.minus(origin)))
        .sum::<f64>()
        .abs()
        * 0.5;
    let mut best = None;
    let mut max_len = 0.0;
    for edge in edges {
        if edge.dir.cross(first.dir).abs() > ANGLE.sin() {
            continue;
        }
        let t0 = first.offset(edge.a);
        let t1 = first.offset(edge.b);
        let start = t0.min(t1).max(0.0);
        let end = t0.max(t1).min(first.len);
        let len = end - start;
        if len < (1.15 * opt.min_wall_length).max(30.0) || len <= max_len {
            continue;
        }
        let d0 = edge.a.minus(origin).dot(n);
        let d1 = edge.b.minus(origin).dot(n);
        let signed = (d0 + d1) * 0.5;
        let thickness = signed.abs();
        if d0 * d1 <= 0.0
            || thickness < (0.6 * opt.min_wall_pair_separation).max(1.2)
            || thickness > opt.max_wall_pair_separation
            || len < thickness * 3.0
            || edge.len / first.len < 0.72
            || len / edge.len < 0.72
        {
            continue;
        }
        let area_ratio = area / (len * thickness);
        if !(0.90..=1.10).contains(&area_ratio) {
            continue;
        }
        // Prevent accepting one limb of a glyph or an adjacent hollow shape.
        let slack = opt.snap_tolerance.min(thickness * 0.10);
        if vs.iter().any(|p| {
            let q = p.minus(origin).dot(n);
            q < signed.min(0.0) - slack || q > signed.max(0.0) + slack
        }) {
            continue;
        }
        let axis = Segment::new(
            first.at(start).plus(n.times(signed * 0.5)),
            first.at(end).plus(n.times(signed * 0.5)),
        )?;
        max_len = len;
        best = Some(Body {
            axis,
            thickness,
            kind: BodyEvidence::FilledBody,
            curve: None,
            ids: BTreeSet::from([id]),
        });
    }
    best
}

#[derive(Clone, Copy)]
struct Pair {
    a: usize,
    b: usize,
    axis: Segment,
    width: f64,
    score: f64,
}
fn paired(a: usize, b: usize, faces: &[Face], o: &Options) -> Option<Pair> {
    let p = faces[a].line;
    let q = faces[b].line;
    if p.dir.cross(q.dir).abs() > ANGLE.sin() {
        return None;
    }
    let t0 = p.offset(q.a);
    let t1 = p.offset(q.b);
    let lo = 0f64.max(t0.min(t1));
    let hi = p.len.min(t0.max(t1));
    let overlap = hi - lo;
    let coverage = overlap / p.len.min(q.len);
    if overlap < o.min_wall_length || coverage < o.min_wall_pair_overlap_ratio {
        return None;
    }
    let n = p.dir.normal();
    let cross = q.dir.dot(p.dir);
    let offset = |t: f64| q.a.plus(q.dir.times((t - t0) / cross)).minus(p.a).dot(n);
    let d0 = offset(lo);
    let d1 = offset(hi);
    // A wedge crossing the face is not a parallel strip. Both finite ends
    // must be within the accepted thickness range.
    let min_width = d0.abs().min(d1.abs());
    let max_width = d0.abs().max(d1.abs());
    if d0 * d1 <= 0.0
        || min_width + EPS < o.min_wall_pair_separation
        || max_width > o.max_wall_pair_separation + EPS
    {
        return None;
    }
    let width = (min_width + max_width) * 0.5;
    let axis = Segment::new(
        p.at(lo).plus(n.times(d0 * 0.5)),
        p.at(hi).plus(n.times(d1 * 0.5)),
    )?;
    let balance = p.len.min(q.len) / p.len.max(q.len);
    let score = 0.58 * coverage.min(1.0)
        + 0.26 * balance
        + 0.16 * (1.0 - width / o.max_wall_pair_separation).clamp(0., 1.)
        + 0.08 * (overlap / (10. * o.min_wall_length)).min(1.);
    Some(Pair {
        a,
        b,
        axis,
        width,
        score,
    })
}

struct Groups(Vec<usize>);
impl Groups {
    fn root(&mut self, mut i: usize) -> usize {
        while self.0[i] != i {
            self.0[i] = self.0[self.0[i]];
            i = self.0[i];
        }
        i
    }
    fn join(&mut self, a: usize, b: usize) {
        let x = self.root(a);
        let y = self.root(b);
        self.0[y] = x;
    }
}

/// Reject membership in an observed sequence of four or more regularly spaced
/// coextensive lines. Unrelated nearby parallel faces do not inherit rejection.
fn repeated_members(faces: &[Face], groups: &mut Groups, o: &Options) -> Vec<bool> {
    let mut members: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
    for (i, f) in faces.iter().enumerate() {
        if !f.explicit {
            members.entry(groups.root(i)).or_default().push(i);
        }
    }
    let mut rejected = vec![false; faces.len()];
    for mut ids in members.into_values().filter(|v| v.len() >= 4) {
        let normal = faces[ids[0]].line.dir.normal();
        let n = |i: usize| faces[i].line.a.dot(normal);
        ids.sort_unstable_by(|a, b| n(*a).total_cmp(&n(*b)).then(a.cmp(b)));
        for m in ids.windows(4) {
            let gaps = [n(m[1]) - n(m[0]), n(m[2]) - n(m[1]), n(m[3]) - n(m[2])];
            let smallest = gaps.iter().copied().fold(f64::INFINITY, f64::min);
            let largest = gaps.iter().copied().fold(0., f64::max);
            if smallest >= o.min_wall_pair_separation - EPS
                && largest <= o.max_wall_pair_separation + EPS
                && largest - smallest <= (0.12 * smallest).max(EPS)
            {
                for &i in m {
                    rejected[i] = true;
                }
            }
        }
    }
    rejected
}

#[derive(Clone, Copy)]
struct ArcObs {
    center: V,
    r: f64,
    start: f64,
    sweep: f64,
    source: usize,
    fitted: bool,
}
impl ArcObs {
    fn canonical(mut self) -> Self {
        if self.sweep < 0.0 {
            self.start += self.sweep;
            self.sweep = -self.sweep;
        }
        self.start = self.start.rem_euclid(TAU);
        self
    }
    fn valid(self, page: &Page, o: &Options) -> bool {
        self.center.finite()
            && self.r.is_finite()
            && self.start.is_finite()
            && self.sweep.is_finite()
            && self.r >= (2. * o.min_wall_pair_separation).max(4.)
            && self.r <= 5. * page.width.hypot(page.height)
            && (PI / 15. ..=1.95 * PI).contains(&self.sweep)
            && self.r * self.sweep >= 1.15 * o.min_wall_length
    }
    fn point(self, t: f64) -> V {
        self.center.plus(V(t.cos(), t.sin()).times(self.r))
    }
}
/// Exact intersection on the angular circle, retaining one contiguous finite
/// overlap as in the reference detector; crossing zero is not a 360-degree gap.
fn shared_angles(a: ArcObs, b: ArcObs) -> Option<(f64, f64)> {
    [-TAU, 0., TAU]
        .iter()
        .filter_map(|shift| {
            let start = a.start.max(b.start + shift);
            let end = (a.start + a.sweep).min(b.start + b.sweep + shift);
            (end > start + EPS).then_some((start, end - start))
        })
        .max_by(|a, b| a.1.total_cmp(&b.1))
}
fn fit_arc(points: &[Point], source: usize, o: &Options) -> Option<ArcObs> {
    if points.len() < 5 {
        return None;
    }
    let a = V::from(points.first()?);
    let m = V::from(&points[points.len() / 2]).minus(a);
    let e = V::from(points.last()?).minus(a);
    let k = 2. * m.cross(e);
    if k.abs() <= EPS * m.norm().max(e.norm()) {
        return None;
    }
    let center = a.plus(V(
        (m.dot(m) * e.1 - e.dot(e) * m.1) / k,
        (m.0 * e.dot(e) - e.0 * m.dot(m)) / k,
    ));
    let r = a.minus(center).norm();
    if !center.finite() || !r.is_finite() || r <= 0.0 {
        return None;
    }
    let tol = (0.025 * r).min(o.snap_tolerance.max(2.5)).max(0.55);
    let start = (a.1 - center.1).atan2(a.0 - center.0);
    let mut prev = start;
    let mut sweep: f64 = 0.0;
    for p in points.iter().skip(1) {
        let radial = V::from(p).minus(center);
        if !radial.finite() || (radial.norm() - r).abs() > tol {
            return None;
        }
        let angle = radial.1.atan2(radial.0);
        let delta = (angle - prev + PI).rem_euclid(TAU) - PI;
        if delta.abs() > PI * 0.5 || (sweep * delta < -EPS) {
            return None;
        }
        sweep += delta;
        prev = angle;
    }
    Some(
        ArcObs {
            center,
            r,
            start,
            sweep,
            source,
            fitted: true,
        }
        .canonical(),
    )
}

fn detect_arcs(
    arcs: &mut [ArcObs],
    faces: &[Face],
    index: &Index,
    page: &Page,
    o: &Options,
    budget: &mut usize,
) -> Result<Vec<Body>, String> {
    arcs.sort_unstable_by(|a, b| {
        a.center
            .0
            .total_cmp(&b.center.0)
            .then(a.center.1.total_cmp(&b.center.1))
            .then(a.r.total_cmp(&b.r))
            .then(a.start.total_cmp(&b.start))
            .then(a.source.cmp(&b.source))
    });
    let diag = page.width.hypot(page.height);
    let mut hits = Vec::new();
    let mut neighbors = Vec::new();
    for (ai, &a) in arcs.iter().enumerate() {
        for (bi, &b) in arcs.iter().enumerate().skip(ai + 1) {
            if b.center.0 - a.center.0 > (1.5 * o.snap_tolerance).max(diag * 0.01) {
                break;
            }
            work(budget, 1)?;
            let width = (a.r - b.r).abs();
            if width < (o.min_wall_pair_separation * 0.55).max(1.0)
                || width > o.max_wall_pair_separation
                || a.source == b.source
            {
                continue;
            }
            let center_tol = (1.5 * o.snap_tolerance).max((diag * 0.01).min(a.r.min(b.r) * 0.025));
            let dist = a.center.minus(b.center).norm();
            if dist > center_tol {
                continue;
            }
            let Some((start, sweep)) = shared_angles(a, b) else {
                continue;
            };
            let coverage = sweep / a.sweep.min(b.sweep);
            let r = (a.r + b.r) * 0.5;
            if coverage < o.min_wall_pair_overlap_ratio.max(0.62)
                || sweep * r < o.min_wall_length * 1.15
                || ((a.fitted || b.fitted) && width < 1.5 * o.min_wall_pair_separation)
                || (a.fitted && b.fitted && sweep >= 1.25 * PI && r < 2. * o.min_wall_length)
            {
                continue;
            }
            let center = a.center.plus(b.center).times(0.5);
            let bounds = Box2::new(center, center).pad(1.55 * r);
            index.neighbors(bounds, &mut neighbors);
            work(budget, neighbors.len())?;
            let mut spokes = 0;
            for &fi in &neighbors {
                let line = faces[fi].line;
                let min_len = (o.min_wall_length * 0.5).max(width * 2.);
                if line.len < min_len || line.len > (min_len * 1.5).max(r * 1.4) {
                    continue;
                }
                let radial = line.at(line.len * 0.5).minus(center);
                let len = radial.norm();
                if len < 0.55 * r || len > 1.55 * r || line.dir.dot(radial).abs() < 0.94 * len {
                    continue;
                }
                if (radial.1.atan2(radial.0) - start).rem_euclid(TAU) <= sweep + EPS {
                    spokes += 1;
                }
                if spokes >= 4 {
                    break;
                }
            }
            if spokes < 4 {
                if hits.len() >= o.max_wall_seeds.saturating_mul(32).max(128) {
                    return Err(
                        "curved wall pair proposal limit exceeded; no partial wall result emitted"
                            .into(),
                    );
                }
                hits.push((
                    coverage * 0.62 + (1. - dist / center_tol.max(EPS)) * 0.22 + 0.16,
                    ai,
                    bi,
                    ArcObs {
                        center,
                        r,
                        start,
                        sweep,
                        source: a.source,
                        fitted: false,
                    },
                    width,
                ));
            }
        }
    }
    hits.sort_unstable_by(|a, b| b.0.total_cmp(&a.0).then(a.1.cmp(&b.1)).then(a.2.cmp(&b.2)));
    let mut used = vec![false; arcs.len()];
    let mut out = Vec::new();
    for (_, a, b, arc, width) in hits {
        if used[a] || used[b] {
            continue;
        }
        let Some(chord) = Segment::new(arc.point(arc.start), arc.point(arc.start + arc.sweep))
        else {
            continue;
        };
        used[a] = true;
        used[b] = true;
        out.push(Body {
            axis: chord,
            thickness: width,
            kind: BodyEvidence::ConcentricArcs,
            curve: Some(ArcGeometry {
                center: arc.center.point(),
                radius: arc.r,
                start_angle: arc.start.rem_euclid(TAU),
                sweep: arc.sweep,
            }),
            ids: BTreeSet::from([arcs[a].source, arcs[b].source]),
        });
    }
    Ok(out)
}
fn work(remaining: &mut usize, amount: usize) -> Result<(), String> {
    *remaining = remaining.checked_sub(amount).ok_or_else(|| {
        "physical-wall neighborhood work limit exceeded; no partial wall result emitted".to_string()
    })?;
    Ok(())
}
fn claim_interval(face: Segment, axis: Segment) -> (f64, f64) {
    let a = face.offset(axis.a);
    let b = face.offset(axis.b);
    (a.min(b).max(0.0), a.max(b).min(face.len))
}

/// Detect bounded physical candidates. Explicit CAD centerlines are kept in a
/// distinct unmeasured category. Unknown single strokes alone are insufficient.
pub fn detect(page: &Page, options: &Options) -> Result<Vec<Wall>, String> {
    let o = options;
    if ![
        o.min_wall_length,
        o.min_wall_fragment_length,
        o.max_wall_pair_separation,
        o.min_wall_pair_separation,
        o.min_wall_pair_overlap_ratio,
        o.snap_tolerance,
    ]
    .iter()
    .all(|x| x.is_finite() && *x > 0.)
        || o.max_wall_pair_separation < o.min_wall_pair_separation
        || o.min_wall_pair_overlap_ratio > 1.0
        || o.max_wall_seeds == 0
    {
        return Err("invalid physical wall detection limits".into());
    }
    let mut seeds = Vec::new();
    let mut arcs = Vec::new();
    let mut bodies = Vec::new();
    for (id, primitive) in page.primitives.iter().enumerate() {
        let role = primitive.layer_kind();
        if !matches!(role, LayerKind::Wall | LayerKind::Unknown) {
            continue;
        }
        let explicit = matches!(role, LayerKind::Wall);
        let available = o.max_wall_seeds.saturating_sub(arcs.len() + bodies.len());
        let mut add = |a: &Point, b: &Point| -> Result<(), String> {
            if let Some(line) = Segment::new(V::from(a), V::from(b)) {
                if line.len >= o.min_wall_fragment_length {
                    if seeds.len() >= available {
                        return Err(format!(
                            "page {} exceeds {} physical wall seeds",
                            page.number, o.max_wall_seeds
                        ));
                    }
                    seeds.push(Seed { line, id, explicit });
                }
            }
            Ok(())
        };
        match &primitive.shape {
            Shape::Line { start, end } => add(start, end)?,
            Shape::Polyline {
                points,
                closed,
                filled,
            } => {
                if *filled {
                    if *closed {
                        if let Some(body) = filled_body(points, o, id) {
                            if seeds.len() >= available {
                                return Err(format!(
                                    "page {} exceeds {} physical wall seeds",
                                    page.number, o.max_wall_seeds
                                ));
                            }
                            bodies.push(body);
                        }
                    }
                    continue;
                }
                if !closed {
                    if let Some(arc) = fit_arc(points, id, o).filter(|a| a.valid(page, o)) {
                        if seeds.len() >= available {
                            return Err(format!(
                                "page {} exceeds {} physical wall seeds",
                                page.number, o.max_wall_seeds
                            ));
                        }
                        arcs.push(arc);
                        continue;
                    }
                }
                for e in points.windows(2) {
                    add(&e[0], &e[1])?;
                }
                if *closed && points.len() > 2 {
                    add(points.last().unwrap(), &points[0])?;
                }
            }
            Shape::Arc {
                center,
                radius,
                start_angle,
                sweep,
            } => {
                let a = ArcObs {
                    center: V::from(center),
                    r: *radius,
                    start: *start_angle,
                    sweep: *sweep,
                    source: id,
                    fitted: false,
                }
                .canonical();
                if a.valid(page, o) {
                    if seeds.len() >= available {
                        return Err(format!(
                            "page {} exceeds {} physical wall seeds",
                            page.number, o.max_wall_seeds
                        ));
                    }
                    arcs.push(a);
                }
            }
            _ => {}
        }
    }
    if seeds.len() + arcs.len() + bodies.len() > o.max_wall_seeds {
        return Err(format!(
            "page {} exceeds {} physical wall seeds",
            page.number, o.max_wall_seeds
        ));
    }
    let faces = merge_faces(seeds);
    let boxes: Vec<_> = faces.iter().map(|f| f.line.bbox()).collect();
    let index = Index::build(&boxes);
    let mut nearby = Vec::new();
    let mut pairs = Vec::new();
    let mut groups = Groups((0..faces.len()).collect());
    // Explicit budgets bound pathological all-overlapping input, independently of source names.
    let mut budget = o.max_wall_seeds.saturating_mul(2048).min(32_000_000);
    for (a, face) in faces.iter().enumerate() {
        if face.line.len < o.min_wall_length {
            continue;
        }
        let area = boxes[a].pad(o.max_wall_pair_separation);
        index.neighbors(area, &mut nearby);
        work(&mut budget, nearby.len())?;
        for &b in &nearby {
            if b <= a || !area.touches(boxes[b]) || face.explicit != faces[b].explicit {
                continue;
            }
            if let Some(p) = paired(a, b, &faces, o) {
                if !face.explicit
                    && (p.axis.len - face.line.len).abs() <= o.snap_tolerance
                    && (p.axis.len - faces[b].line.len).abs() <= o.snap_tolerance
                {
                    groups.join(a, b);
                }
                if pairs.len() >= o.max_wall_seeds.saturating_mul(32).max(128) {
                    return Err("physical wall pair proposal limit exceeded; no partial wall result emitted".into());
                }
                pairs.push(p);
            }
        }
    }
    let repeated = repeated_members(&faces, &mut groups, o);
    pairs.sort_unstable_by(|a, b| {
        b.score
            .total_cmp(&a.score)
            .then(b.axis.len.total_cmp(&a.axis.len))
            .then(a.a.cmp(&b.a))
            .then(a.b.cmp(&b.b))
    });
    let mut claims = vec![Vec::<(f64, f64)>::new(); faces.len()];
    for p in pairs {
        if repeated[p.a] || repeated[p.b] {
            continue;
        }
        let ia = claim_interval(faces[p.a].line, p.axis);
        let ib = claim_interval(faces[p.b].line, p.axis);
        let overlaps = |a: (f64, f64), b: (f64, f64)| a.1.min(b.1) - a.0.max(b.0) > EPS;
        if claims[p.a].iter().any(|&x| overlaps(x, ia))
            || claims[p.b].iter().any(|&x| overlaps(x, ib))
        {
            continue;
        }
        claims[p.a].push(ia);
        claims[p.b].push(ib);
        let ids = faces[p.a]
            .sources(p.axis)
            .chain(faces[p.b].sources(p.axis))
            .collect();
        bodies.push(Body {
            axis: p.axis,
            thickness: p.width,
            kind: BodyEvidence::ParallelFaces,
            curve: None,
            ids,
        });
    }
    for (i, f) in faces.iter().enumerate() {
        if f.explicit && f.line.len >= o.min_wall_length && claims[i].is_empty() {
            bodies.push(Body {
                axis: f.line,
                thickness: 0.0,
                kind: BodyEvidence::ExplicitCenterline,
                curve: None,
                ids: f.parts.iter().map(|(_, id)| *id).collect(),
            });
        }
    }
    bodies.extend(detect_arcs(
        &mut arcs,
        &faces,
        &index,
        page,
        o,
        &mut budget,
    )?);
    finish(bodies, page)
}

#[cfg(test)]
mod tests;

// Repeated painting is provenance, not an additional physical body. Do not
// merge near neighbors or fill a gap merely because their axes align.
fn finish(mut bodies: Vec<Body>, page: &Page) -> Result<Vec<Wall>, String> {
    let key = |b: &Body| {
        let q = |x: f64| (x * 1e6).round() as i64;
        let mut k = [0i64; 10];
        k[0] = i64::from(b.curve.is_some());
        k[1] = q(b.axis.a.0);
        k[2] = q(b.axis.a.1);
        k[3] = q(b.axis.b.0);
        k[4] = q(b.axis.b.1);
        k[5] = q(b.thickness);
        if let Some(c) = &b.curve {
            k[6] = q(c.center.x);
            k[7] = q(c.center.y);
            k[8] = q(c.start_angle);
            k[9] = q(c.sweep);
        }
        k
    };
    bodies.sort_by_key(&key);
    let mut unique: Vec<Body> = Vec::new();
    for b in bodies {
        if let Some(last) = unique.last_mut() {
            if key(last) == key(&b)
                && last.axis.a.minus(b.axis.a).norm() <= 2e-6
                && last.axis.b.minus(b.axis.b).norm() <= 2e-6
                && (last.thickness - b.thickness).abs() <= 2e-6
            {
                last.ids.extend(b.ids);
                if matches!(b.kind, BodyEvidence::FilledBody) {
                    last.kind = BodyEvidence::FilledBody;
                }
                continue;
            }
        }
        unique.push(b);
    }
    Ok(unique
        .into_iter()
        .enumerate()
        .map(|(i, b)| {
            let (start, end) = if let Some(c) = &b.curve {
                let p = |t: f64| {
                    Point::new(
                        c.center.x + c.radius * t.cos(),
                        c.center.y + c.radius * t.sin(),
                    )
                };
                (p(c.start_angle), p(c.start_angle + c.sweep))
            } else {
                (b.axis.a.point(), b.axis.b.point())
            };
            let mut source_ids: Vec<_> = b
                .ids
                .into_iter()
                .map(|n| page.primitives[n].id.clone())
                .collect();
            source_ids.sort();
            source_ids.dedup();
            Wall {
                id: format!("page:{}:wall:{}", page.number, i + 1),
                page: page.number,
                start,
                end,
                thickness: b.thickness,
                body: b.kind,
                curve: b.curve,
                source_ids,
                requires_review: true,
            }
        })
        .collect())
}
