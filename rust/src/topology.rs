//! Split a copy of the wall axes at junctions. Source-backed wall extents remain immutable.
use crate::model::*;
use std::collections::{BTreeMap, BTreeSet, HashMap, VecDeque};
pub(crate) mod perimeters;
#[derive(Clone, Copy)]
struct Seg {
    a: Point,
    b: Point,
    wall: Option<usize>,
    opening: Option<usize>,
}
fn add_cut(cuts: &mut Vec<f64>, p: Point, s: Seg, tol: f64) {
    let (q, t) = on_segment(p, s.a, s.b);
    if q.distance(p) <= tol {
        cuts.push(t)
    }
}
fn cell(p: Point, t: f64) -> (i64, i64) {
    ((p.x / t).floor() as i64, (p.y / t).floor() as i64)
}
struct NodeIndex {
    buckets: HashMap<(i64, i64), Vec<usize>>,
    points: Vec<Point>,
    tol: f64,
}
impl NodeIndex {
    fn insert(&mut self, p: Point) -> usize {
        let (x, y) = cell(p, self.tol);
        let mut best = None;
        for dx in -1i64..=1 {
            for dy in -1i64..=1 {
                if let Some(ids) = self
                    .buckets
                    .get(&(x.saturating_add(dx), y.saturating_add(dy)))
                {
                    for &i in ids {
                        let dis = p.distance(self.points[i]);
                        if dis <= self.tol
                            && best.is_none_or(|(d, j)| dis < d || (dis == d && i < j))
                        {
                            best = Some((dis, i))
                        }
                    }
                }
            }
        }
        if let Some((_, i)) = best {
            return i;
        }
        let i = self.points.len();
        self.points.push(p);
        self.buckets.entry((x, y)).or_default().push(i);
        i
    }
}
pub struct Topology {
    pub nodes: Vec<Node>,
    pub edges: Vec<Edge>,
    pub rooms: Vec<Room>,
}

// A room currently has one polygon, not an exterior ring plus holes. Nested
// disconnected cycles cannot both describe fully occupied room areas. Preserve
// the graph and wall evidence, withhold the enclosing face, and leave any
// independently bounded innermost room subject to the usual review requirement.
const MAX_FACE_HALF_EDGES: usize = 500_000;
const MAX_BOUNDED_FACES: usize = 32_768;
const FACE_WORK: usize = 32_000_000;
#[derive(Clone, Copy)]
struct Bounds {
    lo: Point,
    hi: Point,
}
impl Bounds {
    fn new(ps: &[Point]) -> Self {
        let mut b = Self {
            lo: ps[0],
            hi: ps[0],
        };
        for p in &ps[1..] {
            b.lo.x = b.lo.x.min(p.x);
            b.lo.y = b.lo.y.min(p.y);
            b.hi.x = b.hi.x.max(p.x);
            b.hi.y = b.hi.y.max(p.y)
        }
        b
    }
    fn point(self, p: Point) -> bool {
        p.x >= self.lo.x && p.x <= self.hi.x && p.y >= self.lo.y && p.y <= self.hi.y
    }
    fn wraps(self, b: Self) -> bool {
        self.point(b.lo) && self.point(b.hi)
    }
}
struct Face {
    component: usize,
    polygon: Vec<Point>,
    area: f64,
    bounds: Bounds,
    wall_ids: Vec<String>,
}
fn charge(remaining: &mut usize, n: usize) -> Result<(), String> {
    *remaining = remaining
        .checked_sub(n)
        .ok_or("topology work budget exceeded")?;
    Ok(())
}

// Reject touching or crossing boundaries, including a chord through a concave
// notch. A bounding rectangle or a centroid alone is not containment evidence.
fn boundaries_meet(a: Point, b: Point, c: Point, d: Point, eps: f64) -> bool {
    if a.x.min(b.x) > c.x.max(d.x) + eps
        || c.x.min(d.x) > a.x.max(b.x) + eps
        || a.y.min(b.y) > c.y.max(d.y) + eps
        || c.y.min(d.y) > a.y.max(b.y) + eps
    {
        return false;
    }
    let (v, w) = (b - a, d - c);
    let e1 = eps * v.norm();
    let e2 = eps * w.norm();
    let (ca, da, ac, bc) = (
        v.cross(c - a),
        v.cross(d - a),
        w.cross(a - c),
        w.cross(b - c),
    );
    !((ca > e1 && da > e1)
        || (ca < -e1 && da < -e1)
        || (ac > e2 && bc > e2)
        || (ac < -e2 && bc < -e2))
}
fn nested(outer: &Face, inner: &Face, remaining: &mut usize) -> Result<bool, String> {
    if outer.component == inner.component
        || outer.area <= inner.area
        || !outer.bounds.wraps(inner.bounds)
    {
        return Ok(false);
    }
    let (p, q) = (&outer.polygon, &inner.polygon);
    charge(remaining, p.len())?;
    if !inside(q[0], p) {
        return Ok(false);
    }
    charge(remaining, p.len().saturating_mul(q.len()))?;
    let b = outer.bounds;
    let eps = 64.
        * f64::EPSILON
        * b.lo
            .x
            .abs()
            .max(b.lo.y.abs())
            .max(b.hi.x.abs())
            .max(b.hi.y.abs())
            .max(1.);
    for (i, &a) in p.iter().enumerate() {
        let bb = p[(i + 1) % p.len()];
        for (j, &c) in q.iter().enumerate() {
            if boundaries_meet(a, bb, c, q[(j + 1) % q.len()], eps) {
                return Ok(false);
            }
        }
    }
    Ok(true)
}
pub fn build(
    page: &Page,
    walls: &[Wall],
    openings: &[Opening],
    o: &Options,
) -> Result<Topology, String> {
    let mut segs = Vec::new();
    for (i, w) in walls.iter().enumerate() {
        if let Some(c) = w.curve {
            // Graph-only approximation; the exported physical curve stays analytic.
            let step = (2. * (1. - 0.25 / c.radius.max(0.25)).clamp(-1., 1.).acos())
                .clamp(0.01, std::f64::consts::FRAC_PI_4);
            let n = (c.sweep.abs() / step).ceil().max(1.) as usize;
            if n > 4096 {
                return Err("curve subdivision limit exceeded".into());
            }
            for k in 0..n {
                segs.push(Seg {
                    a: c.at(k as f64 / n as f64),
                    b: c.at((k + 1) as f64 / n as f64),
                    wall: Some(i),
                    opening: None,
                });
            }
        } else {
            segs.push(Seg {
                a: w.start,
                b: w.end,
                wall: Some(i),
                opening: None,
            })
        }
    }
    for (i, p) in openings.iter().enumerate() {
        if p.evidence == OpeningEvidence::WallGapSwing {
            segs.push(Seg {
                a: p.start,
                b: p.end,
                wall: None,
                opening: Some(i),
            })
        }
    }
    segs.retain(|s| s.a.distance(s.b) > 1e-8);
    if segs.len() > 80_000 {
        return Err("topology segment budget exceeded".into());
    }
    let mut cuts = vec![vec![0., 1.]; segs.len()];
    // Resolve cuts on their designated hosts. A nearby parallel axis is insufficient.
    let wall_by_id: HashMap<_, _> = walls.iter().enumerate().map(|(i, w)| (&w.id, i)).collect();
    let mut door_spans = vec![Vec::new(); walls.len()];
    for (i, opening) in openings
        .iter()
        .enumerate()
        .filter(|(_, o)| o.kind == OpeningKind::Door)
    {
        for host in &opening.host_wall_ids {
            if let Some(&w) = wall_by_id.get(host) {
                if let Some((lo, hi)) = opening.interval_on(&walls[w]) {
                    door_spans[w].push((lo, hi, i));
                }
            }
        }
    }
    for (s, ts) in segs.iter().zip(&mut cuts) {
        if let Some(w) = s.wall {
            for &(lo, hi, _) in &door_spans[w] {
                ts.extend([lo, hi]);
            }
        }
    }
    let mut ord: Vec<_> = (0..segs.len()).collect();
    ord.sort_by(|&a, &b| {
        segs[a]
            .a
            .x
            .min(segs[a].b.x)
            .total_cmp(&segs[b].a.x.min(segs[b].b.x))
            .then(a.cmp(&b))
    });
    let tol = o.snap_tolerance;
    let mut pairs = 0usize;
    let mut cut_count: usize = cuts.iter().map(Vec::len).sum();
    let half_width = |s: Seg| {
        s.wall
            .map(|i| &walls[i])
            .filter(|w| w.curve.is_none())
            .map_or(0., |w| w.thickness * 0.5)
    };
    let broad_pad = tol + walls.iter().map(|w| w.thickness * 0.5).fold(0., f64::max);
    for (k, &i) in ord.iter().enumerate() {
        let a = segs[i];
        let v = a.b - a.a;
        for &j in &ord[k + 1..] {
            let b = segs[j];
            if b.a.x.min(b.b.x) > a.a.x.max(a.b.x) + broad_pad {
                break;
            }
            pairs += 1;
            if pairs > 40_000_000 {
                return Err("topology pair budget exceeded".into());
            }
            if b.a.y.min(b.b.y) > a.a.y.max(a.b.y) + broad_pad
                || a.a.y.min(a.b.y) > b.a.y.max(b.b.y) + broad_pad
            {
                continue;
            }
            let old = cuts[i].len() + cuts[j].len();
            let w = b.b - b.a;
            let den = v.cross(w);
            if den.abs() > 1e-10 * v.norm() * w.norm() {
                let d = b.a - a.a;
                let t = d.cross(w) / den;
                let u = d.cross(v) / den;
                let within = (0.0..=1.0).contains(&t) && (0.0..=1.0).contains(&u);
                // At a butt joint, the observed centerline can end at the other wall's face.
                // Connect graph axes only if these finite, near-perpendicular bodies meet.
                let (av, bv) = (v.norm(), w.norm());
                let (ah, bh) = (half_width(a), half_width(b));
                let butt = ah > 0.
                    && bh > 0.
                    && v.dot(w).abs() < 0.035 * av * bv
                    && t >= -bh / av
                    && t <= 1. + bh / av
                    && u >= -ah / bv
                    && u <= 1. + ah / bv;
                if within || butt {
                    cuts[i].push(t);
                    cuts[j].push(u);
                }
            }
            for p in [a.a, a.b] {
                add_cut(&mut cuts[j], p, b, tol)
            }
            for p in [b.a, b.b] {
                add_cut(&mut cuts[i], p, a, tol)
            }
            cut_count += cuts[i].len() + cuts[j].len() - old;
            if cut_count > 2_000_000 {
                return Err("topology junction budget exceeded".into());
            }
        }
    }
    let mut ni = NodeIndex {
        buckets: HashMap::new(),
        points: Vec::new(),
        tol,
    };
    type Owners = (BTreeSet<usize>, BTreeSet<usize>);
    let mut unique: BTreeMap<(usize, usize), Owners> = BTreeMap::new();
    let mut door_work = 32_000_000;
    for (s, mut t) in segs.into_iter().zip(cuts) {
        t.sort_by(f64::total_cmp);
        t.dedup_by(|a, b| (*a - *b).abs() < 1e-9);
        let mut prev = None;
        for u in t {
            let n = ni.insert(s.a + (s.b - s.a) * u);
            if let Some((last, last_t)) = prev {
                if n != last {
                    let key = (n.min(last), n.max(last));
                    let (ws, os) = unique.entry(key).or_default();
                    if let Some(w) = s.wall {
                        ws.insert(w);
                        let mid = (last_t + u) * 0.5;
                        charge(&mut door_work, door_spans[w].len())?;
                        os.extend(
                            door_spans[w]
                                .iter()
                                .filter(|&&(lo, hi, _)| lo < mid && mid < hi)
                                .map(|&(_, _, i)| i),
                        );
                    }
                    if let Some(v) = s.opening {
                        os.insert(v);
                    }
                }
            }
            prev = Some((n, u))
        }
    }
    if unique.len() > MAX_FACE_HALF_EDGES / 2 {
        return Err("room face half-edge budget exceeded".into());
    }
    let mut members = vec![BTreeSet::new(); ni.points.len()];
    let mut edges = Vec::new();
    let mut adj = vec![Vec::new(); ni.points.len()];
    for ((a, b), (ws, os)) in unique {
        for &w in &ws {
            members[a].insert(w);
            members[b].insert(w);
        }
        let e = edges.len();
        edges.push(Edge {
            start_node: a,
            end_node: b,
            wall_ids: ws.into_iter().map(|x| walls[x].id.clone()).collect(),
            opening_ids: os.into_iter().map(|x| openings[x].id.clone()).collect(),
        });
        adj[a].push((b, e));
        adj[b].push((a, e));
    }
    let nodes: Vec<_> = ni
        .points
        .iter()
        .zip(members)
        .enumerate()
        .map(|(i, (&p, ws))| Node {
            id: format!("p{}:node:{}", page.number, i + 1),
            position: p,
            requires_review: ws.iter().any(|&j| walls[j].requires_review),
            wall_ids: ws.into_iter().map(|j| walls[j].id.clone()).collect(),
        })
        .collect();
    // Peel dangling branches for room traversal only; graph and source walls retain them.
    let mut degree: Vec<_> = adj.iter().map(Vec::len).collect();
    let mut q: VecDeque<_> = (0..degree.len()).filter(|&i| degree[i] < 2).collect();
    let mut active = vec![true; edges.len()];
    while let Some(a) = q.pop_front() {
        for &(b, e) in &adj[a] {
            if active[e] {
                active[e] = false;
                degree[b] = degree[b].saturating_sub(1);
                if degree[b] == 1 {
                    q.push_back(b)
                }
            }
        }
    }
    for (a, ns) in adj.iter_mut().enumerate() {
        ns.retain(|&(_, e)| active[e]);
        ns.sort_by(|&(b, _), &(c, _)| {
            let u = ni.points[b] - ni.points[a];
            let v = ni.points[c] - ni.points[a];
            u.y.atan2(u.x).total_cmp(&v.y.atan2(v.x)).then(b.cmp(&c))
        });
    }
    let mut next = vec![usize::MAX; edges.len() * 2];
    let half = |e: usize, from: usize| 2 * e + usize::from(edges[e].start_node != from);
    for (a, ns) in adj.iter().enumerate() {
        for (i, &(b, e)) in ns.iter().enumerate() {
            let (_, out) = ns[(i + ns.len() - 1) % ns.len()];
            next[half(e, b)] = half(out, a)
        }
    }
    let mut component = vec![usize::MAX; adj.len()];
    for seed in 0..adj.len() {
        if adj[seed].is_empty() || component[seed] != usize::MAX {
            continue;
        }
        component[seed] = seed;
        let mut stack = vec![seed];
        while let Some(a) = stack.pop() {
            for &(b, _) in &adj[a] {
                if component[b] == usize::MAX {
                    component[b] = seed;
                    stack.push(b)
                }
            }
        }
    }
    let mut work = FACE_WORK;
    let mut seen = vec![false; next.len()];
    let mut faces = Vec::new();
    for start in 0..next.len() {
        if seen[start] || next[start] == usize::MAX {
            continue;
        }
        let mut h = start;
        let mut ids = BTreeSet::new();
        let mut nodes_in_face = BTreeSet::new();
        let mut poly = Vec::new();
        let mut simple = true;
        while !seen[h] {
            seen[h] = true;
            let edge = &edges[h / 2];
            let a = if h % 2 == 0 {
                edge.start_node
            } else {
                edge.end_node
            };
            charge(&mut work, 1 + edge.wall_ids.len())?;
            simple &= nodes_in_face.insert(a);
            poly.push(ni.points[a]);
            ids.extend(edge.wall_ids.iter().cloned());
            h = next[h];
        }
        if h != start || !simple || poly.len() < 3 {
            continue;
        }
        let origin = poly[0];
        let area = (1..poly.len() - 1)
            .map(|i| (poly[i] - origin).cross(poly[i + 1] - origin))
            .sum::<f64>()
            / 2.;
        if !area.is_finite() || area <= 0. {
            continue;
        }
        if faces.len() >= MAX_BOUNDED_FACES {
            return Err("room face count budget exceeded".into());
        }
        // Even a closed island below the room area floor can make an enclosing
        // polygon unrepresentable. It must still participate in containment.
        faces.push(Face {
            component: component[*nodes_in_face.first().unwrap()],
            bounds: Bounds::new(&poly),
            polygon: poly,
            area,
            wall_ids: ids.into_iter().collect(),
        });
    }
    let mut order: Vec<_> = (0..faces.len()).collect();
    order.sort_by(|&a, &b| {
        faces[a]
            .bounds
            .lo
            .x
            .total_cmp(&faces[b].bounds.lo.x)
            .then(a.cmp(&b))
    });
    let mut omit = vec![false; faces.len()];
    for (i, f) in faces.iter().enumerate() {
        if f.area < o.min_room_area {
            continue;
        }
        let start = order.partition_point(|&j| faces[j].bounds.lo.x < f.bounds.lo.x);
        for &j in &order[start..] {
            if faces[j].bounds.lo.x > f.bounds.hi.x {
                break;
            }
            charge(&mut work, 1)?;
            if nested(f, &faces[j], &mut work)? {
                omit[i] = true;
                break;
            }
        }
    }
    let mut rooms = Vec::new();
    for (i, f) in faces.into_iter().enumerate() {
        if omit[i] || f.area < o.min_room_area {
            continue;
        }
        rooms.push(Room {
            boundary_evidence: None,
            id: format!("p{}:room:{}", page.number, rooms.len() + 1),
            polygon: f.polygon,
            area: f.area,
            wall_ids: f.wall_ids,
            label: None,
            label_source_id: None,
            requires_review: true,
        });
    }
    assign_room_layer_labels(page, &mut rooms, &mut work)?;
    Ok(Topology {
        nodes,
        edges,
        rooms,
    })
}

fn assign_room_layer_labels(
    page: &Page,
    rooms: &mut [Room],
    work: &mut usize,
) -> Result<(), String> {
    if rooms.is_empty() {
        return Ok(());
    }
    let labels: Vec<_> = page
        .primitives
        .iter()
        .filter_map(|p| match &p.shape {
            Shape::Text { at, value, .. }
                if p.layer_kind() == LayerKind::Room && !value.trim().is_empty() =>
            {
                Some((*at, value, &p.id))
            }
            _ => None,
        })
        .collect();
    for room in rooms {
        let bounds = Bounds::new(&room.polygon);
        let mut label = None;
        let mut source = None;
        for &(at, v, id) in &labels {
            charge(work, 1)?;
            if !bounds.point(at) {
                continue;
            }
            charge(work, room.polygon.len())?;
            if inside(at, &room.polygon) {
                if label.is_some() {
                    label = None;
                    source = None;
                    break;
                }
                label = Some(v.clone());
                source = Some(id.clone());
            }
        }
        room.label = label;
        room.label_source_id = source;
    }
    Ok(())
}
