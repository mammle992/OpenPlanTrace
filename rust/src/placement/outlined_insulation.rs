//! Complete batt assemblies with independently closed, thin structural finishes.
//! Closed ink only supplies the missing facing beside certified batt material;
//! neither a room outline nor borrowed furnishing strokes authorize wall material.
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, insulation, roundoff};
use crate::finish_visibility::{ContourEdge, FinishRequirement, receipt_for};
use crate::model::{BodyEvidence, PageScan, Point, Primitive, Shape, Wall};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 32_000_000;
const MAX_SEGMENTS: usize = 4096;
const MAX_EDGES: usize = 20_000;
const MAX_NODES: usize = 12_000;
const MAX_CUTS: usize = 60_000;
const MAX_RING: usize = 256;
const MAX_PROPOSALS: usize = 4096;

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("outlined insulation evidence budget exceeded")?;
    Ok(())
}

#[derive(Clone, Copy)]
struct Frame {
    start: Point,
    axis: Point,
    normal: Point,
    length: f64,
    half: f64,
    eps: f64,
}
impl Frame {
    fn new(w: &Wall) -> Option<Self> {
        let length = w.start.distance(w.end);
        let eps = roundoff(w);
        (w.start.finite()
            && w.end.finite()
            && length.is_finite()
            && length > eps
            && w.thickness.is_finite()
            && w.thickness > eps
            && w.curve.is_none())
        .then(|| {
            let axis = (w.end - w.start).unit();
            Self {
                start: w.start,
                axis,
                normal: Point::new(-axis.y, axis.x),
                length,
                half: w.thickness * 0.5,
                eps,
            }
        })
    }
    fn local(self, p: Point) -> Point {
        let p = p - self.start;
        Point::new(p.dot(self.axis), p.dot(self.normal))
    }
    fn world(self, x: f64, y: f64) -> Point {
        self.start + self.axis * x + self.normal * y
    }
}

#[derive(Clone, Copy)]
struct Box2 {
    x0: f64,
    x1: f64,
    y0: f64,
    y1: f64,
}
impl Box2 {
    fn polygon(points: impl IntoIterator<Item = Point>) -> Option<Self> {
        let mut b = Self {
            x0: f64::INFINITY,
            x1: f64::NEG_INFINITY,
            y0: f64::INFINITY,
            y1: f64::NEG_INFINITY,
        };
        for p in points {
            if !p.finite() {
                return None;
            }
            b.x0 = b.x0.min(p.x);
            b.x1 = b.x1.max(p.x);
            b.y0 = b.y0.min(p.y);
            b.y1 = b.y1.max(p.y);
        }
        (b.x0.is_finite() && b.x1 >= b.x0 && b.y1 >= b.y0).then_some(b)
    }
    fn overlaps(self, b: Self, eps: f64) -> bool {
        self.x1.min(b.x1) - self.x0.max(b.x0) > eps && self.y1.min(b.y1) - self.y0.max(b.y0) > eps
    }
}

fn segments(
    p: &Primitive,
    work: &mut usize,
    mut add: impl FnMut(Point, Point) -> Result<(), String>,
) -> Result<(), String> {
    match &p.shape {
        Shape::Line { start, end } => {
            charge(work, 1)?;
            add(*start, *end)?;
        }
        Shape::Polyline {
            points,
            closed,
            filled: false,
        } => {
            charge(work, points.len())?;
            for pair in points.windows(2) {
                add(pair[0], pair[1])?;
            }
            if *closed && points.len() > 2 && points.first() != points.last() {
                add(*points.last().unwrap(), points[0])?;
            }
        }
        _ => {}
    }
    Ok(())
}

fn covering<'a>(
    parts: &mut [(f64, f64, &'a str)],
    lo: f64,
    hi: f64,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<&'a str>>, String> {
    charge(
        work,
        parts
            .len()
            .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
    )?;
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut at = lo;
    let mut ids = BTreeSet::new();
    for &(a, b, id) in parts.iter() {
        if b <= at + eps {
            continue;
        }
        if a > at + eps {
            return Ok(None);
        }
        at = b;
        ids.insert(id);
        if at >= hi - eps {
            return Ok(Some(ids.into_iter().collect()));
        }
    }
    Ok(None)
}

#[derive(Clone)]
struct Raw<'a> {
    h: bool,
    c: f64,
    lo: f64,
    hi: f64,
    id: &'a str,
}
#[derive(Clone)]
struct Line<'a> {
    h: bool,
    c: usize,
    lo: usize,
    hi: usize,
    ids: BTreeSet<&'a str>,
}

fn structural(p: &Primitive) -> bool {
    crate::openings::physical_jambs::structural(p)
}

fn ink<'a>(
    page: &'a PageScan,
    layer: &Option<String>,
    role: Option<crate::layers::LayerCategory>,
    frame: Frame,
    work: &mut usize,
) -> Result<Vec<Raw<'a>>, String> {
    let mut out = vec![];
    for p in &page.primitives {
        charge(work, 1)?;
        if p.layer != *layer || p.layer_category_override != role || !structural(p) {
            continue;
        }
        segments(p, work, |a, b| {
            let (a, b) = (frame.local(a), frame.local(b));
            if !a.finite() || !b.finite() {
                return Err("nonfinite insulation source ink".into());
            }
            let bounds = Box2::polygon([a, b]).unwrap();
            // Bound local closure search. Rejecting distant source ink cannot
            // create an artificial boundary or an otherwise absent source edge.
            if bounds.x0 < -frame.length
                || bounds.x1 > 2. * frame.length
                || bounds.y0 < -frame.length - frame.half
                || bounds.y1 > frame.length + frame.half
            {
                return Ok(());
            }
            let (h, c, lo, hi) = if (a.y - b.y).abs() <= frame.eps {
                (true, (a.y + b.y) * 0.5, a.x.min(b.x), a.x.max(b.x))
            } else if (a.x - b.x).abs() <= frame.eps {
                (false, (a.x + b.x) * 0.5, a.y.min(b.y), a.y.max(b.y))
            } else {
                return Ok(());
            };
            if hi - lo <= frame.eps {
                return Ok(());
            }
            if out.len() == MAX_SEGMENTS {
                return Err("outlined insulation source segment limit exceeded".into());
            }
            out.push(Raw {
                h,
                c,
                lo,
                hi,
                id: p.id.as_str(),
            });
            Ok(())
        })?;
    }
    Ok(out)
}

/// Coordinate identity tolerates only the wall's floating-point roundoff. Each
/// cluster is measured from its first value, so a chain cannot close a real gap.
fn coordinates(
    mut values: Vec<f64>,
    eps: f64,
    work: &mut usize,
) -> Result<(Vec<f64>, HashMap<u64, usize>), String> {
    charge(
        work,
        values
            .len()
            .saturating_mul(values.len().max(1).ilog2() as usize + 1),
    )?;
    values.sort_by(f64::total_cmp);
    let mut at = Vec::<f64>::new();
    let mut ids = HashMap::new();
    for value in values {
        if at.last().is_none_or(|&v| value - v > eps) {
            at.push(value);
        }
        ids.insert(value.to_bits(), at.len() - 1);
    }
    Ok((at, ids))
}

struct Ring<'a> {
    points: Vec<Point>,
    ids: BTreeSet<&'a str>,
    vertices: HashSet<usize>,
    area: f64,
    perimeter: f64,
    contour_edges: Vec<(Point, Point, &'a str)>,
}
struct Graph<'a> {
    points: Vec<Point>,
    rings: Vec<Ring<'a>>,
}

fn graph<'a>(raw: &[Raw<'a>], eps: f64, work: &mut usize) -> Result<Graph<'a>, String> {
    let mut xs = vec![];
    let mut ys = vec![];
    for s in raw {
        if s.h {
            xs.extend([s.lo, s.hi]);
            ys.push(s.c);
        } else {
            ys.extend([s.lo, s.hi]);
            xs.push(s.c);
        }
    }
    let (xs, xi) = coordinates(xs, eps, work)?;
    let (ys, yi) = coordinates(ys, eps, work)?;
    let mut unique = BTreeMap::<(bool, usize, usize, usize), BTreeSet<&str>>::new();
    for s in raw {
        let (a, b) = if s.h { (&yi, &xi) } else { (&xi, &yi) };
        let key = (
            s.h,
            a[&s.c.to_bits()],
            b[&s.lo.to_bits()],
            b[&s.hi.to_bits()],
        );
        if key.2 != key.3 {
            unique.entry(key).or_default().insert(s.id);
        }
    }
    let lines: Vec<_> = unique
        .into_iter()
        .map(|((h, c, lo, hi), ids)| Line { h, c, lo, hi, ids })
        .collect();
    let mut groups = BTreeMap::<(bool, usize), BTreeSet<usize>>::new();
    for s in &lines {
        groups.entry((s.h, s.c)).or_default().extend([s.lo, s.hi]);
    }
    let mut cuts = Vec::<BTreeSet<usize>>::new();
    let mut count = 0;
    for s in &lines {
        let group = &groups[&(s.h, s.c)];
        charge(work, group.len())?;
        let set: BTreeSet<_> = group.range(s.lo..=s.hi).copied().collect();
        count += set.len();
        if count > MAX_CUTS {
            return Err("outlined insulation graph cut limit exceeded".into());
        }
        cuts.push(set);
    }
    let hs: Vec<_> = lines
        .iter()
        .enumerate()
        .filter(|(_, s)| s.h)
        .map(|(i, _)| i)
        .collect();
    let vs: Vec<_> = lines
        .iter()
        .enumerate()
        .filter(|(_, s)| !s.h)
        .map(|(i, _)| i)
        .collect();
    for &h in &hs {
        for &v in &vs {
            charge(work, 1)?;
            let (a, b) = (&lines[h], &lines[v]);
            if a.lo <= b.c && b.c <= a.hi && b.lo <= a.c && a.c <= b.hi {
                count += usize::from(cuts[h].insert(b.c)) + usize::from(cuts[v].insert(a.c));
                if count > MAX_CUTS {
                    return Err("outlined insulation graph intersection limit exceeded".into());
                }
            }
        }
    }
    let mut nodes = BTreeMap::<(usize, usize), usize>::new();
    let mut points = Vec::new();
    let mut edges = BTreeMap::<(usize, usize), BTreeSet<&str>>::new();
    for (s, cut) in lines.iter().zip(&cuts) {
        let cut: Vec<_> = cut.iter().copied().collect();
        for pair in cut.windows(2) {
            charge(work, s.ids.len() + 1)?;
            let mut ends = [0; 2];
            for (k, &at) in pair.iter().enumerate() {
                let key = if s.h { (at, s.c) } else { (s.c, at) };
                ends[k] = *nodes.entry(key).or_insert_with(|| {
                    let i = points.len();
                    points.push(Point::new(xs[key.0], ys[key.1]));
                    i
                });
            }
            if points.len() > MAX_NODES {
                return Err("outlined insulation graph node limit exceeded".into());
            }
            ends.sort_unstable();
            edges
                .entry((ends[0], ends[1]))
                .or_default()
                .extend(s.ids.iter().copied());
            if edges.len() > MAX_EDGES {
                return Err("outlined insulation graph edge limit exceeded".into());
            }
        }
    }
    let mut adjacent = vec![vec![]; points.len()];
    for &(a, b) in edges.keys() {
        adjacent[a].push(b);
        adjacent[b].push(a);
    }
    let direction = |a: usize, b: usize| {
        let d = points[b] - points[a];
        if d.x > eps {
            0
        } else if d.y > eps {
            1
        } else if d.x < -eps {
            2
        } else {
            3
        }
    };
    for (a, ns) in adjacent.iter_mut().enumerate() {
        ns.sort_by_key(|&b| direction(a, b));
        if ns.len() > 4 {
            return Err("nonplanar insulation source graph".into());
        }
    }
    let mut visited = HashSet::new();
    let mut rings = vec![];
    for &(a, b) in edges.keys() {
        for (mut u, mut v) in [(a, b), (b, a)] {
            if visited.contains(&(u, v)) {
                continue;
            }
            let start = (u, v);
            let mut path = vec![];
            let mut ids = BTreeSet::new();
            let mut contour_edges = vec![];
            let mut closed = false;
            // Even an overlong face must consume its half-edges deterministically;
            // reject that face rather than treating an incomplete path as a polygon.
            for _ in 0..=edges.len().saturating_mul(2) {
                charge(work, 1)?;
                if !visited.insert((u, v)) {
                    closed = (u, v) == start;
                    break;
                }
                path.push(u);
                if path.len() <= MAX_RING {
                    let key = if u < v { (u, v) } else { (v, u) };
                    charge(work, edges[&key].len())?;
                    ids.extend(edges[&key].iter().copied());
                    contour_edges.extend(edges[&key].iter().map(|&id| (points[u], points[v], id)));
                }
                let ns = &adjacent[v];
                let pos = ns
                    .iter()
                    .position(|&x| x == u)
                    .ok_or("invalid insulation graph incidence")?;
                let next = ns[(pos + ns.len() - 1) % ns.len()];
                u = v;
                v = next;
            }
            if !closed || !(4..=MAX_RING).contains(&path.len()) {
                continue;
            }
            let vertices: HashSet<_> = path.iter().copied().collect();
            if vertices.len() != path.len() {
                continue;
            }
            let polygon: Vec<_> = path.iter().map(|&i| points[i]).collect();
            let area = polygon
                .iter()
                .zip(polygon.iter().cycle().skip(1))
                .map(|(a, b)| a.cross(*b))
                .sum::<f64>()
                * 0.5;
            if area <= eps * eps {
                continue;
            }
            let perimeter = polygon
                .iter()
                .zip(polygon.iter().cycle().skip(1))
                .map(|(a, b)| a.distance(*b))
                .sum();
            rings.push(Ring {
                points: polygon,
                ids,
                vertices,
                area,
                perimeter,
                contour_edges,
            });
        }
    }
    Ok(Graph { points, rings })
}

fn inside(p: Point, polygon: &[Point], eps: f64) -> bool {
    let mut yes = false;
    for (&a, &b) in polygon.iter().zip(polygon.iter().cycle().skip(1)) {
        if crate::model::on_segment(p, a, b).0.distance(p) <= eps {
            return false;
        }
        if (a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x {
            yes = !yes;
        }
    }
    yes
}

/// On an orthogonal simple polygon, intersections are constant between its
/// vertex planes. Checking every slab proves the entire rectangle, not corners
/// or the polygon's bounding box, and rejects a concave notch through the band.
fn contains_rectangle(
    polygon: &[Point],
    b: Box2,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    let mut planes = vec![b.x0, b.x1];
    planes.extend(
        polygon
            .iter()
            .map(|p| p.x)
            .filter(|&x| x > b.x0 + eps && x < b.x1 - eps),
    );
    planes.sort_by(f64::total_cmp);
    planes.dedup_by(|a, b| (*a - *b).abs() <= eps);
    for pair in planes.windows(2) {
        let x = (pair[0] + pair[1]) * 0.5;
        let mut ys = vec![];
        charge(work, polygon.len())?;
        for (&a, &c) in polygon.iter().zip(polygon.iter().cycle().skip(1)) {
            if (a.y - c.y).abs() <= eps && a.x.min(c.x) < x && x < a.x.max(c.x) {
                ys.push((a.y + c.y) * 0.5);
            }
        }
        ys.sort_by(f64::total_cmp);
        // In the supported longitudinal extent, the source contour must be
        // exactly this one thin facing. A large containing cavity or a second
        // disconnected arm is not evidence for missing wall material.
        if ys.len() != 2 || (ys[0] - b.y0).abs() > eps || (ys[1] - b.y1).abs() > eps {
            return Ok(false);
        }
    }
    Ok(true)
}

fn finish<'g, 'a>(
    g: &'g Graph<'a>,
    b: Box2,
    adjacent_core_width: f64,
    eps: f64,
    work: &mut usize,
) -> Result<Option<&'g Ring<'a>>, String> {
    let depth = b.y1 - b.y0;
    if depth <= eps || depth >= adjacent_core_width - eps {
        return Ok(None);
    }
    for ring in &g.rings {
        charge(work, 1)?;
        // A room/cabinet cavity cannot supply a narrow finishing layer simply
        // because the requested band happens to lie inside its bounding box.
        if 2. * ring.area > 1.25 * depth * ring.perimeter + eps * ring.perimeter {
            continue;
        }
        if !contains_rectangle(&ring.points, b, eps, work)? {
            continue;
        }
        let mut hole = false;
        for (i, &p) in g.points.iter().enumerate() {
            charge(work, ring.points.len())?;
            if !ring.vertices.contains(&i) && inside(p, &ring.points, eps) {
                hole = true;
                break;
            }
        }
        if !hole {
            return Ok(Some(ring));
        }
    }
    Ok(None)
}

fn spatial_claim(page: &PageScan, frame: Frame, work: &mut usize) -> Result<bool, String> {
    let body = Box2 {
        x0: 0.,
        x1: frame.length,
        y0: -frame.half,
        y1: frame.half,
    };
    for o in &page.objects {
        let points = o
            .footprint
            .as_ref()
            .map(|f| f.polygon.as_slice())
            .or_else(|| o.geometry.as_ref().map(|g| g.polygon.as_slice()));
        if let Some(points) = points {
            charge(work, points.len() + 1)?;
            if Box2::polygon(points.iter().map(|&p| frame.local(p)))
                .is_none_or(|b| body.overlaps(b, frame.eps))
            {
                return Ok(true);
            }
        }
    }
    for o in page.withheld_walls.iter().flatten() {
        if let Some(points) = &o.furniture_outline {
            charge(work, points.len() + 1)?;
            if Box2::polygon(points.iter().map(|&p| frame.local(p)))
                .is_none_or(|b| body.overlaps(b, frame.eps))
            {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

fn duplicate(a: &Wall, b: &Wall) -> bool {
    if a.curve.is_some() || b.curve.is_some() {
        return false;
    }
    if (a.end - a.start).unit().dot((b.end - b.start).unit()).abs() < 0.995 {
        return false;
    }
    // Conservative nearly parallel veto, independent of graph/source identity.
    super::overlap::parallel_bodies_overlap(a, b)
}

/// PDF pattern matrices can round the lattice slightly differently from its
/// exact painted outline. Bind BOTH transverse paint edges to consecutive
/// cell boundaries using the established cell-phase tolerance. This never
/// changes the actual material outline used by the exact coverage proof.
fn aligned_cell(w: &Wall, tile: &insulation::MaterialTile) -> bool {
    if !tile.origin.finite()
        || !tile.repeat.finite()
        || !tile.across.finite()
        || tile.repeat.norm() <= 0.
        || tile.across.norm() <= 0.
    {
        return false;
    }
    let axis = (w.end - w.start).unit();
    if tile.repeat.unit().cross(axis).abs() > 1e-5
        || tile.across.unit().dot(axis).abs() > 1e-5
        || w.start.distance(w.end) < 2. * tile.repeat.norm()
    {
        return false;
    }
    let phases = tile
        .outline
        .map(|p| (p - tile.origin).dot(tile.across) / tile.across.dot(tile.across));
    if phases
        .iter()
        .any(|p| !p.is_finite() || (p - p.round()).abs() > 0.001)
    {
        return false;
    }
    let lo = phases.iter().copied().fold(f64::INFINITY, f64::min).round();
    let hi = phases
        .iter()
        .copied()
        .fold(f64::NEG_INFINITY, f64::max)
        .round();
    hi - lo == 1.
}

struct AssemblyProof {
    source_ids: Vec<String>,
    finishes: Vec<FinishRequirement>,
}

fn proof(
    page: &PageScan,
    w: &Wall,
    layer: &Option<String>,
    role: Option<crate::layers::LayerCategory>,
    work: &mut usize,
) -> Result<Option<AssemblyProof>, String> {
    let Some(f) = Frame::new(w) else {
        return Ok(None);
    };
    if spatial_claim(page, f, work)? {
        return Ok(None);
    }
    for o in &page.openings {
        charge(work, 1)?;
        if super::overlap::aperture_crosses(w, o) {
            return Ok(None);
        }
    }
    let mut tiles = vec![];
    for t in &page.material_tiles {
        charge(work, 1)?;
        if t.layer != *layer || !aligned_cell(w, t) {
            continue;
        }
        charge(
            work,
            t.strokes
                .len()
                .saturating_mul(t.strokes.len())
                .saturating_add(16),
        )?;
        if !insulation::rectangle(&t.outline) || !insulation::batting(&t.strokes) {
            continue;
        }
        let points = t.outline.map(|p| f.local(p));
        let Some(b) = Box2::polygon(points) else {
            continue;
        };
        if points.iter().any(|p| {
            ![b.x0, b.x1].iter().any(|x| (p.x - x).abs() <= f.eps)
                || ![b.y0, b.y1].iter().any(|y| (p.y - y).abs() <= f.eps)
        }) || b.y0 < -f.half - f.eps
            || b.y1 > f.half + f.eps
            || b.x1 <= f.eps
            || b.x0 >= f.length - f.eps
        {
            continue;
        }
        tiles.push((b, t.id.as_str()));
    }
    if tiles.is_empty() {
        return Ok(None);
    }
    let raw = ink(page, layer, role, f, work)?;
    let owned: HashSet<_> = w.source_ids.iter().map(String::as_str).collect();
    let mut faces = [vec![], vec![]];
    for s in &raw {
        charge(work, 1)?;
        if s.h && owned.contains(s.id) {
            for (side, y) in [-f.half, f.half].into_iter().enumerate() {
                if (s.c - y).abs() <= f.eps {
                    faces[side].push((s.lo, s.hi, s.id));
                }
            }
        }
    }
    let Some(left) = covering(&mut faces[0], 0., f.length, f.eps, work)? else {
        return Ok(None);
    };
    let Some(right) = covering(&mut faces[1], 0., f.length, f.eps, work)? else {
        return Ok(None);
    };
    if left.iter().any(|id| right.contains(id)) {
        return Ok(None);
    }
    let mut ys = vec![-f.half, f.half];
    for (b, _) in &tiles {
        ys.extend([b.y0.max(-f.half), b.y1.min(f.half)]);
    }
    ys.sort_by(f64::total_cmp);
    ys.dedup_by(|a, b| (*a - *b).abs() <= f.eps);
    let mut core = vec![];
    let mut ids: BTreeSet<_> = left.into_iter().chain(right).collect();
    for pair in ys.windows(2) {
        if pair[1] - pair[0] <= f.eps {
            continue;
        }
        charge(work, tiles.len())?;
        let mut parts: Vec<_> = tiles
            .iter()
            .filter(|(b, _)| b.y0 <= pair[0] + f.eps && b.y1 >= pair[1] - f.eps)
            .map(|(b, id)| (b.x0, b.x1, *id))
            .collect();
        if let Some(used) = covering(&mut parts, 0., f.length, f.eps, work)? {
            core.push((pair[0], pair[1]));
            ids.extend(used);
        }
    }
    if core.is_empty() || core.windows(2).any(|p| p[1].0 - p[0].1 > f.eps) {
        return Ok(None);
    }
    let low = core[0].0;
    let high = core.last().unwrap().1;
    if low <= -f.half + f.eps && high >= f.half - f.eps {
        return Ok(None);
    }
    let g = graph(&raw, f.eps, work)?;
    let mut finishes = vec![];
    for (a, b, edge) in [(-f.half, low, low), (high, f.half, high)] {
        if b - a <= f.eps {
            continue;
        }
        let width = tiles
            .iter()
            .filter(|(r, _)| (r.y0 - edge).abs() <= f.eps || (r.y1 - edge).abs() <= f.eps)
            .map(|(r, _)| r.y1 - r.y0)
            .fold(f64::INFINITY, f64::min);
        if !width.is_finite() {
            return Ok(None);
        }
        let Some(ring) = finish(
            &g,
            Box2 {
                x0: 0.,
                x1: f.length,
                y0: a,
                y1: b,
            },
            width,
            f.eps,
            work,
        )?
        else {
            return Ok(None);
        };
        let source_ids: Vec<_> = ring.ids.iter().map(|s| (*s).to_owned()).collect();
        finishes.push(FinishRequirement {
            rectangle: [
                f.world(0., a),
                f.world(f.length, a),
                f.world(f.length, b),
                f.world(0., b),
            ],
            source_ids,
            contour_edges: ring
                .contour_edges
                .iter()
                .map(|&(a, b, id)| ContourEdge {
                    source_id: id.to_owned(),
                    start: f.world(a.x, a.y),
                    end: f.world(b.x, b.y),
                })
                .collect(),
        });
        ids.extend(ring.ids.iter().copied());
    }
    Ok(Some(AssemblyProof {
        source_ids: ids.into_iter().map(str::to_owned).collect(),
        finishes,
    }))
}

struct Proposal {
    wall_index: usize,
    source_ids: Vec<String>,
    finishes: Vec<FinishRequirement>,
}

struct LegacyFacing {
    wall_index: usize,
    finishes: Vec<FinishRequirement>,
}

/// Recheck only inherited authority that actually extrudes material beyond its
/// referenced batt paint. Core-only receipts retain their historical identity.
/// This does not change the frozen selector or expand its physical geometry.
fn legacy_facings(
    page: &PageScan,
    decisions: &[WallDecision],
    work: &mut usize,
) -> Result<Vec<LegacyFacing>, String> {
    if decisions.len() != page.walls.len()
        || decisions
            .iter()
            .zip(&page.walls)
            .any(|(d, w)| d.wall_id != w.id)
    {
        return Err("legacy insulation decisions are not aligned with source walls".into());
    }
    let by: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let tiles: HashMap<_, _> = page
        .material_tiles
        .iter()
        .map(|t| (t.id.as_str(), t))
        .collect();
    charge(
        work,
        page.primitives
            .len()
            .saturating_add(page.material_tiles.len()),
    )?;
    let mut out = vec![];
    for (i, (w, d)) in page.walls.iter().zip(decisions).enumerate() {
        charge(work, 1)?;
        if d.status != WallStatus::Ready || d.reason != WallReason::TiledInsulationBetweenFaces {
            continue;
        }
        let measured = super::effective_wall(w, Some(d));
        let Some(f) = Frame::new(&measured) else {
            continue;
        };
        let legacy_eps = f.eps.max(measured.thickness * 1e-5);
        let mut supported = vec![];
        let mut same_strip = false;
        for id in &d.support_source_ids {
            charge(work, 1)?;
            let Some(t) = tiles.get(id.as_str()).copied() else {
                continue;
            };
            charge(work, 16)?;
            let p = t.outline.map(|p| f.local(p));
            let Some(b) = Box2::polygon(p) else {
                continue;
            };
            // Exactly the legacy same-strip tolerance classifies its tiny
            // authoring-roundoff cases; it grants no new material authority.
            let same = [
                (0., -f.half),
                (f.length, -f.half),
                (f.length, f.half),
                (0., f.half),
            ]
            .iter()
            .all(|&(x, y)| p.iter().any(|p| p.distance(Point::new(x, y)) <= legacy_eps));
            if same {
                same_strip = true;
                break;
            }
            if b.x0 > legacy_eps
                || b.x1 < f.length - legacy_eps
                || b.y0 < -f.half - legacy_eps
                || b.y1 > f.half + legacy_eps
                || p.iter().any(|p| {
                    ![b.x0, b.x1].iter().any(|x| (p.x - x).abs() <= legacy_eps)
                        || ![b.y0, b.y1].iter().any(|y| (p.y - y).abs() <= legacy_eps)
                })
            {
                continue;
            }
            supported.push((b.y0.max(-f.half), b.y1.min(f.half)));
        }
        if same_strip || supported.is_empty() {
            continue;
        }
        supported.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
        let mut merged: Vec<(f64, f64)> = vec![];
        for (lo, hi) in supported {
            if let Some(last) = merged.last_mut()
                && lo <= last.1 + f.eps
            {
                last.1 = last.1.max(hi);
            } else {
                merged.push((lo, hi));
            }
        }
        // Existing tiled-between-faces proof has one contiguous batt core.
        // Other material families have their own frozen selectors.
        if merged.len() != 1 {
            continue;
        }
        let (lo, hi) = merged[0];
        let source_ids: Vec<_> = d
            .support_source_ids
            .iter()
            .filter(|id| {
                by.get(id.as_str()).is_some_and(|p| {
                    structural(p)
                        && matches!(
                            p.shape,
                            Shape::Line { .. } | Shape::Polyline { filled: false, .. }
                        )
                })
            })
            .cloned()
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        let mut finishes = vec![];
        for (a, b) in [(-f.half, lo), (hi, f.half)] {
            if b - a <= legacy_eps {
                continue;
            }
            finishes.push(FinishRequirement {
                rectangle: [
                    f.world(0., a),
                    f.world(f.length, a),
                    f.world(f.length, b),
                    f.world(0., b),
                ],
                source_ids: source_ids.clone(),
                contour_edges: vec![],
            });
        }
        if !finishes.is_empty() {
            out.push(LegacyFacing {
                wall_index: i,
                finishes,
            });
        }
    }
    Ok(out)
}

/// Geometric requirements are importer queries, never placement authority.
/// Receipts may be shared by proposals; a missing receipt cannot be inferred
/// from raw source ink or from the adjacent batt's independent visibility.
pub(crate) fn requirements(
    page: &PageScan,
    decisions: &[WallDecision],
) -> Result<Vec<FinishRequirement>, String> {
    let mut work = MAX_WORK;
    let mut out: Vec<_> = legacy_facings(page, decisions, &mut work)?
        .into_iter()
        .flat_map(|p| p.finishes)
        .collect();
    out.extend(
        geometric_proposals(page, decisions, &mut work)?
            .into_iter()
            .flat_map(|p| p.finishes),
    );
    let key_bytes = out
        .iter()
        .flat_map(|r| &r.source_ids)
        .fold(0usize, |n, id| n.saturating_add(id.len().saturating_add(1)));
    charge(
        &mut work,
        key_bytes
            .saturating_add(out.len().saturating_mul(8))
            .saturating_mul(out.len().max(1).ilog2() as usize + 2),
    )?;
    out.sort_by(|a, b| {
        a.source_ids.cmp(&b.source_ids).then_with(|| {
            for (a, b) in a.rectangle.iter().zip(&b.rectangle) {
                let order = a.x.total_cmp(&b.x).then(a.y.total_cmp(&b.y));
                if !order.is_eq() {
                    return order;
                }
            }
            std::cmp::Ordering::Equal
        })
    });
    out.dedup_by(|a, b| {
        a.rectangle == b.rectangle
            && a.source_ids == b.source_ids
            && a.contour_edges == b.contour_edges
    });
    Ok(out)
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) -> Result<(), String> {
    select_with_budget(page, decisions, MAX_WORK)
}

fn select_with_budget(
    page: &PageScan,
    decisions: &mut [WallDecision],
    budget: usize,
) -> Result<(), String> {
    let mut work = budget;
    let legacy = legacy_facings(page, decisions, &mut work)?;
    // Preserve the producer's candidate set: inherited authority that later
    // fails visibility cannot silently unblock an uncaptured replacement.
    let proposed = geometric_proposals(page, decisions, &mut work)?;
    let mut missing = vec![];
    for facing in legacy {
        let mut complete = true;
        for need in &facing.finishes {
            charge(&mut work, need.source_ids.len() + 1)?;
            if receipt_for(page, need).is_none() {
                complete = false;
                break;
            }
        }
        if !complete {
            missing.push(facing.wall_index);
        }
    }
    let mut accepted: Vec<(usize, Vec<String>)> = vec![];
    for proposal in proposed {
        let mut ids = proposal.source_ids;
        let mut complete = true;
        for requirement in &proposal.finishes {
            charge(&mut work, requirement.source_ids.len() + 1)?;
            if let Some(receipt) = receipt_for(page, requirement) {
                ids.push(receipt.id.clone());
            } else {
                complete = false;
                break;
            }
        }
        if !complete {
            continue;
        }
        let w = &page.walls[proposal.wall_index];
        let mut conflict = false;
        for (other, _) in &accepted {
            charge(&mut work, 1)?;
            if duplicate(w, &page.walls[*other]) {
                conflict = true;
                break;
            }
        }
        if conflict {
            continue;
        }
        ids.sort();
        ids.dedup();
        accepted.push((proposal.wall_index, ids));
    }
    // No mutation until all evidence and work checks complete.
    for i in missing {
        decisions[i].status = WallStatus::Review;
        decisions[i].reason = WallReason::InsulationFinishVisibilityMissing;
    }
    for (i, ids) in accepted {
        let w = &page.walls[i];
        let d = &mut decisions[i];
        d.status = WallStatus::Ready;
        d.reason = WallReason::SourceBoundedVisibleInsulationAssembly;
        d.support_source_ids = ids.clone();
        d.support_wall_ids.clear();
        d.placement_extent = Some(PlacementExtent {
            thickness: None,
            start: w.start,
            end: w.end,
            source_ids: ids,
        });
    }
    Ok(())
}

fn printed_number(value: &str) -> Option<f64> {
    let value = value.trim().replace(',', ".").parse::<f64>().ok()?;
    (value.is_finite() && value > 0.).then_some(value)
}

fn printed_unit(value: &str) -> bool {
    matches!(
        value.trim().to_lowercase().as_str(),
        "m2" | "m²" | "m^2" | "㎡"
    )
}

fn printed_area(value: &str) -> Option<f64> {
    let value = value.trim().to_lowercase();
    ["m²", "m^2", "m2", "㎡"]
        .into_iter()
        .find_map(|unit| value.strip_suffix(unit).and_then(printed_number))
}

fn context_text<'a>(by: &HashMap<&str, &'a Primitive>, id: &str) -> Option<(Point, &'a str, f64)> {
    let p = *by.get(id)?;
    if !matches!(
        p.layer_kind(),
        crate::model::LayerKind::Room | crate::model::LayerKind::Unknown
    ) {
        return None;
    }
    let Shape::Text { at, value, height } = &p.shape else {
        return None;
    };
    (at.finite() && height.is_finite() && height.abs() > 0. && !value.trim().is_empty())
        .then_some((*at, value.as_str(), *height))
}

/// Paired printed room labels remain architectural context when the room graph
/// has not closed. Bind their actual text and existing annotation association;
/// both lie on the same side within the finite structural search frame.
fn local_context(
    page: &PageScan,
    w: &Wall,
    by: &HashMap<&str, &Primitive>,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    if let Some(ids) = super::painted_junctions::context(page, w, by, work)? {
        return Ok(Some(ids));
    }
    let Some(frame) = Frame::new(w) else {
        return Ok(None);
    };
    for annotation in &page.room_annotations {
        charge(work, annotation.area_source_ids.len() + 1)?;
        let (Some(label), Some(label_id)) = (&annotation.label, &annotation.label_source_id) else {
            continue;
        };
        if !annotation.area_square_meters.is_finite()
            || annotation.area_square_meters <= 0.
            || !(1..=2).contains(&annotation.area_source_ids.len())
            || annotation.area_source_ids.iter().any(|id| id == label_id)
            || annotation
                .area_source_ids
                .iter()
                .collect::<HashSet<_>>()
                .len()
                != annotation.area_source_ids.len()
        {
            continue;
        }
        let Some((label_at, value, _)) = context_text(by, label_id) else {
            continue;
        };
        if value.trim() != label.trim() {
            continue;
        }
        let area: Option<Vec<_>> = annotation
            .area_source_ids
            .iter()
            .map(|id| context_text(by, id))
            .collect();
        let Some(area) = area else {
            continue;
        };
        let (at, text, height) = area[0];
        if at != annotation.anchor {
            continue;
        }
        let h = height.abs().clamp(1., 30.);
        let measured = if area.len() == 1 {
            printed_area(text)
        } else {
            let (number_at, number, _) = area[1];
            if !printed_unit(text)
                || number_at.x >= at.x
                || (number_at.y - at.y).abs() > h * 0.4
                || at.x - number_at.x > 6. * h
            {
                continue;
            }
            printed_number(number)
        };
        if measured != Some(annotation.area_square_meters)
            || (label_at.x - at.x).abs() > 6. * h
            || (label_at.y - at.y).abs() > 2.5 * h
        {
            continue;
        }
        let label_at = frame.local(label_at);
        let area_at = frame.local(at);
        if [label_at, area_at].iter().any(|p| {
            p.x < -frame.eps
                || p.x > frame.length + frame.eps
                || p.y.abs() <= frame.half + frame.eps
                || p.y.abs() > frame.length + frame.half + frame.eps
        }) || label_at.y.signum() != area_at.y.signum()
        {
            continue;
        }
        let mut ids = annotation.area_source_ids.clone();
        ids.push(label_id.clone());
        ids.sort();
        ids.dedup();
        return Ok(Some(ids));
    }
    Ok(None)
}

fn geometric_proposals(
    page: &PageScan,
    decisions: &[WallDecision],
    work: &mut usize,
) -> Result<Vec<Proposal>, String> {
    if decisions.len() != page.walls.len()
        || decisions
            .iter()
            .zip(&page.walls)
            .any(|(d, w)| d.wall_id != w.id)
    {
        return Err("outlined insulation decisions are not aligned with source walls".into());
    }
    charge(work, page.primitives.len() + page.walls.len())?;
    if page.material_tiles.is_empty() {
        return Ok(vec![]);
    }
    let by: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut order: Vec<_> = page
        .walls
        .iter()
        .enumerate()
        .filter(|(i, w)| {
            decisions[*i].status == WallStatus::Review
                && w.body == BodyEvidence::ParallelFaces
                && w.requires_review
                && w.curve.is_none()
        })
        .map(|(i, _)| i)
        .collect();
    order.sort_by(|&a, &b| {
        page.walls[b]
            .thickness
            .total_cmp(&page.walls[a].thickness)
            .then_with(|| {
                page.walls[b]
                    .start
                    .distance(page.walls[b].end)
                    .total_cmp(&page.walls[a].start.distance(page.walls[a].end))
            })
            .then(page.walls[a].id.cmp(&page.walls[b].id))
    });
    let mut proposed = vec![];
    for i in order {
        let w = &page.walls[i];
        let mut anchor = None;
        for id in &w.source_ids {
            charge(work, 1)?;
            if let Some(p) = by
                .get(id.as_str())
                .copied()
                .filter(|p| structural(p) && p.layer.is_some())
            {
                anchor = Some((p.layer.clone(), p.layer_category_override));
                break;
            }
        }
        let Some((layer, role)) = anchor else {
            continue;
        };
        let Some(context_ids) = local_context(page, w, &by, work)? else {
            continue;
        };
        let Some(proof) = proof(page, w, &layer, role, work)? else {
            continue;
        };
        let mut ids = proof.source_ids;
        ids.extend(context_ids);
        ids.sort();
        ids.dedup();
        let mut conflict = false;
        for (other, d) in page.walls.iter().zip(decisions.iter()) {
            charge(work, 1)?;
            if d.status == WallStatus::Ready {
                let effective = super::effective_wall(other, Some(d));
                if duplicate(w, &effective) {
                    conflict = true;
                    break;
                }
            }
        }
        if conflict {
            continue;
        }
        if proposed.len() == MAX_PROPOSALS {
            return Err("outlined insulation proposal limit exceeded".into());
        }
        proposed.push(Proposal {
            wall_index: i,
            source_ids: ids,
            finishes: proof.finishes,
        });
    }
    Ok(proposed)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::*;

    fn batt() -> Vec<[Point; 2]> {
        let half = [
            (0., 1.),
            (0.13, 0.90),
            (0.17, 0.80),
            (0.07, 0.25),
            (0.12, 0.12),
            (0.25, 0.),
        ];
        let mut p = vec![];
        for offset in [0., 0.5] {
            p.extend(half.iter().map(|&(x, y)| Point::new(offset + x, y)));
            p.extend(
                half.iter()
                    .rev()
                    .skip(1)
                    .map(|&(x, y)| Point::new(offset + 0.5 - x, y)),
            );
        }
        p.dedup();
        p.windows(2).map(|s| [s[0], s[1]]).collect()
    }
    fn line(id: &str, a: Point, b: Point) -> Primitive {
        Primitive {
            id: id.into(),
            layer: Some("A-WALL".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Line { start: a, end: b },
        }
    }
    fn outline(page: &mut PageScan, name: &str, points: &[Point]) {
        for (i, (&a, &b)) in points.iter().zip(points.iter().cycle().skip(1)).enumerate() {
            page.primitives.push(line(&format!("{name}:{i}"), a, b));
        }
    }
    fn fixture() -> (PageScan, Vec<WallDecision>) {
        let mut p = PageScan {
            finish_visibility: None,
            pdf_input: None,
            page_number: 1,
            exterior_enclosures: None,
            exterior_connections: None,
            exterior_jamb_backings: vec![],
            placement_paint_ids: Some(vec![]),
            placement_polygon_paint_ids: Some(vec![]),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            recessed_stock: vec![],
            masked_junction_stock: vec![],
            pocket_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 1000.,
            height: 1000.,
            units_to_millimeters: None,
            scale_source: None,
            calibration_source_ids: vec![],
            primitives: vec![
                line("raw lower", Point::new(0., -6.), Point::new(80., -6.)),
                line("raw upper", Point::new(0., 6.), Point::new(80., 6.)),
            ],
            walls: vec![Wall {
                id: "wall".into(),
                page: 1,
                start: Point::new(0., 0.),
                end: Point::new(80., 0.),
                thickness: 12.,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: vec![
                    "raw lower".into(),
                    "raw upper".into(),
                    "borrowed furniture".into(),
                ],
                requires_review: true,
            }],
            placement_decisions: None,
            withheld_walls: Some(vec![]),
            openings: vec![],
            window_panes: vec![],
            nodes: vec![],
            edges: vec![],
            rooms: vec![],
            objects: vec![],
            object_groups: vec![],
            room_connections: vec![],
            drawing_features: vec![],
            semantics: Default::default(),
            room_annotations: vec![],
            warnings: vec![],
        };
        let mut borrowed = line(
            "borrowed furniture",
            Point::new(0., 6.),
            Point::new(80., 6.),
        );
        borrowed.layer = Some("Furniture".into());
        p.primitives.push(borrowed);
        outline(
            &mut p,
            "outer finish",
            &[
                Point::new(-20., -6.),
                Point::new(90., -6.),
                Point::new(90., 30.),
                Point::new(89., 30.),
                Point::new(89., -5.),
                Point::new(-20., -5.),
            ],
        );
        outline(
            &mut p,
            "inner finish",
            &[
                Point::new(-10., 5.),
                Point::new(90., 5.),
                Point::new(90., 30.),
                Point::new(89., 30.),
                Point::new(89., 6.),
                Point::new(-9., 6.),
                Point::new(-9., 30.),
                Point::new(-10., 30.),
            ],
        );
        for (id, a, b, step) in [("outer batt", -5., 2., 10.), ("inner batt", 2., 5., 6.)] {
            p.material_tiles.push(insulation::MaterialTile {
                id: id.into(),
                layer: Some("A-WALL".into()),
                outline: [
                    Point::new(-5., a),
                    Point::new(85., a),
                    Point::new(85., b),
                    Point::new(-5., b),
                ],
                origin: Point::new(-5., a),
                repeat: Point::new(step, 0.),
                across: Point::new(0., b - a),
                strokes: batt(),
            });
        }
        p.primitives.push(Primitive {
            id: "room label".into(),
            layer: Some("Room".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Text {
                at: Point::new(40., 20.),
                value: "Room".into(),
                height: 3.,
            },
        });
        p.rooms.push(Room {
            id: "room".into(),
            boundary_evidence: None,
            polygon: vec![
                Point::new(0., 6.),
                Point::new(80., 6.),
                Point::new(80., 40.),
                Point::new(0., 40.),
            ],
            area: 2720.,
            wall_ids: vec!["wall".into()],
            label: Some("Room".into()),
            label_source_id: Some("room label".into()),
            requires_review: true,
        });
        let ds = vec![WallDecision {
            wall_id: "wall".into(),
            status: WallStatus::Review,
            reason: WallReason::OverlappingWallAxes,
            component_id: "component".into(),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: vec![],
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        }];
        (p, ds)
    }
    fn attach_visibility(p: &mut PageScan, decisions: &[WallDecision]) {
        let queries = requirements(p, decisions).unwrap();
        let mut history = crate::finish_visibility::PaintHistory::default();
        for (i, primitive) in p.primitives.iter().enumerate() {
            let order = primitive
                .id
                .strip_prefix("pdf:p1:path:")
                .and_then(|s| s.split(':').next())
                .and_then(|s| s.parse::<u64>().ok())
                .map(|n| 2 * n)
                .unwrap_or(i as u64 + 1);
            history.record_ink(primitive.id.clone(), order);
        }
        p.finish_visibility = Some(history.certify(p.page_number, queries));
    }

    fn run(p: &PageScan) -> Vec<WallDecision> {
        let mut p = p.clone();
        let mut ds = fixture().1;
        attach_visibility(&mut p, &ds);
        select(&p, &mut ds).unwrap();
        ds
    }

    fn legacy_fixture(core_only: bool) -> (PageScan, Vec<WallDecision>) {
        let (mut p, _) = fixture();
        p.primitives = vec![
            line("lower face", Point::new(0., -5.), Point::new(80., -5.)),
            line("upper face", Point::new(0., 5.), Point::new(80., 5.)),
            line("finish seam", Point::new(0., 4.), Point::new(80., 4.)),
            line("finish start", Point::new(0., 4.), Point::new(0., 5.)),
            line("finish end", Point::new(80., 5.), Point::new(80., 4.)),
        ];
        p.rooms.clear();
        p.walls[0].thickness = 10.;
        p.walls[0].source_ids = vec!["lower face".into(), "upper face".into()];
        p.material_tiles.truncate(1);
        let high = if core_only { 5. } else { 4. };
        let t = &mut p.material_tiles[0];
        t.outline = [
            Point::new(0., -5.),
            Point::new(80., -5.),
            Point::new(80., high),
            Point::new(0., high),
        ];
        t.origin = Point::new(0., -5.);
        t.across = Point::new(0., high + 5.);
        p.nodes = vec![
            Node {
                id: "a".into(),
                position: p.walls[0].start,
                wall_ids: vec!["wall".into()],
                requires_review: true,
            },
            Node {
                id: "b".into(),
                position: p.walls[0].end,
                wall_ids: vec!["wall".into()],
                requires_review: true,
            },
        ];
        p.edges = vec![Edge {
            start_node: 0,
            end_node: 1,
            wall_ids: vec!["wall".into()],
            opening_ids: vec![],
        }];
        let d = super::super::select_preview19(&p).unwrap();
        assert_eq!(d[0].status, WallStatus::Ready);
        assert_eq!(d[0].reason, WallReason::TiledInsulationBetweenFaces);
        (p, d)
    }

    #[test]
    fn legacy_finish_authority_needs_visibility_but_valid_batt_core_remains_exact() {
        for core_only in [false, true] {
            let (mut p, baseline) = legacy_fixture(core_only);
            let needs = requirements(&p, &baseline).unwrap();
            assert_eq!(needs.len(), usize::from(!core_only));
            if !core_only {
                let need = &needs[0];
                assert_eq!(
                    need.rectangle,
                    [
                        Point::new(0., 4.),
                        Point::new(80., 4.),
                        Point::new(80., 5.),
                        Point::new(0., 5.)
                    ]
                );
                assert_eq!(need.source_ids.len(), 5);
                assert!(!need.source_ids.iter().any(|id| id.contains("batt")));
            }
            let mut ds = baseline.clone();
            select(&p, &mut ds).unwrap();
            if core_only {
                assert_eq!(ds, baseline, "unaffected core needs no finish inventory");
            } else {
                let mut expected = baseline.clone();
                expected[0].status = WallStatus::Review;
                expected[0].reason = WallReason::InsulationFinishVisibilityMissing;
                assert_eq!(
                    ds, expected,
                    "missing finish changes only readiness and reason"
                );
            }
            attach_visibility(&mut p, &baseline);
            let mut ds = baseline.clone();
            select(&p, &mut ds).unwrap();
            assert_eq!(
                serde_json::to_vec(&ds).unwrap(),
                serde_json::to_vec(&baseline).unwrap(),
                "verified legacy rows retain exact old support and geometry"
            );
        }
    }

    #[test]
    fn legacy_finish_interior_erase_cannot_inherit_adjacent_batt_authority() {
        let (mut p, baseline) = legacy_fixture(false);
        let needs = requirements(&p, &baseline).unwrap();
        let mut history = crate::finish_visibility::PaintHistory::default();
        for (i, ink) in p.primitives.iter().enumerate() {
            history.record_ink(ink.id.clone(), i as u64 + 1);
        }
        history.record_bounds(20, Point::new(30., 4.2), Point::new(40., 4.8));
        p.finish_visibility = Some(history.certify(1, needs.clone()));
        assert!(p.finish_visibility.as_ref().unwrap().receipts.is_empty());
        assert_eq!(requirements(&p, &baseline).unwrap(), needs);
        let mut ds = baseline.clone();
        select(&p, &mut ds).unwrap();
        assert_eq!(ds[0].status, WallStatus::Review);
        assert_eq!(ds[0].reason, WallReason::InsulationFinishVisibilityMissing);
        assert_eq!(ds[0].support_source_ids, baseline[0].support_source_ids);
        assert_eq!(p.material_tiles[0].outline[2].y, 4.);
    }

    #[test]
    fn paired_room_area_is_finite_context_only_and_rejects_forged_or_missing_evidence() {
        let (mut source, _) = fixture();
        source.rooms.clear();
        source.primitives.push(Primitive {
            id: "printed area".into(),
            layer: Some("Room".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Text {
                at: Point::new(40., 24.),
                value: "9,3 m²".into(),
                height: 3.,
            },
        });
        source
            .room_annotations
            .push(crate::room_labels::RoomAnnotation {
                floor_footprint: None,
                label: Some("Room".into()),
                label_source_id: Some("room label".into()),
                area_square_meters: 9.3,
                area_source_ids: vec!["printed area".into()],
                anchor: Point::new(40., 24.),
                room_id: None,
            });
        assert_eq!(run(&source)[0].status, WallStatus::Ready);
        for change in 0..9 {
            let mut p = source.clone();
            match change {
                0 => p.primitives.retain(|p| p.id != "printed area"),
                1 => {
                    if let Shape::Text { value, .. } = &mut p
                        .primitives
                        .iter_mut()
                        .find(|p| p.id == "printed area")
                        .unwrap()
                        .shape
                    {
                        *value = "99 m²".into();
                    }
                }
                2 => p.room_annotations[0].area_square_meters = 10.,
                3 => p.room_annotations[0].label = Some("forged".into()),
                4 => {
                    let a = &mut p.room_annotations[0];
                    a.anchor.x = 90.;
                    if let Shape::Text { at, .. } = &mut p
                        .primitives
                        .iter_mut()
                        .find(|p| p.id == "printed area")
                        .unwrap()
                        .shape
                    {
                        at.x = 90.;
                    }
                }
                5 => p.room_annotations.clear(),
                6 => {
                    p.material_tiles[0].outline[1].x = 79.99;
                    p.material_tiles[0].outline[2].x = 79.99;
                }
                7 => p.room_annotations[0].area_source_ids = vec!["room label".into()],
                _ => p.room_annotations[0].anchor.y += 0.01,
            }
            assert_eq!(
                run(&p)[0].status,
                WallStatus::Review,
                "context change{change}"
            );
        }
    }

    #[test]
    fn outlined_batts_require_entire_source_material_and_keep_observations() {
        let (mut p, mut ds) = fixture();
        attach_visibility(&mut p, &ds);
        let before = serde_json::to_value(&p).unwrap();
        insulation::validate(&p.material_tiles).unwrap();
        select(&p, &mut ds).unwrap();
        assert_eq!(ds[0].status, WallStatus::Ready);
        assert_eq!(
            ds[0].reason,
            WallReason::SourceBoundedVisibleInsulationAssembly
        );
        assert_eq!(
            ds[0].placement_extent.as_ref().unwrap().start,
            p.walls[0].start
        );
        assert_eq!(ds[0].placement_extent.as_ref().unwrap().end, p.walls[0].end);
        assert!(ds[0].support_source_ids.contains(&"outer batt".into()));
        assert!(ds[0].support_source_ids.contains(&"inner batt".into()));
        assert!(
            !ds[0]
                .support_source_ids
                .contains(&"borrowed furniture".into())
        );
        assert_eq!(before, serde_json::to_value(&p).unwrap());
    }

    #[test]
    fn outlined_batts_reject_missing_receipts_faces_closure_and_real_gaps() {
        for mode in 0..6 {
            let (mut p, _) = fixture();
            match mode {
                0 => {
                    p.material_tiles.remove(0);
                }
                1 => {
                    p.material_tiles.remove(1);
                }
                2 => {
                    // The far return cap is shared with the inner C contour;
                    // removing that duplicate does not actually open a face.
                    // Remove the unique near closing edge instead.
                    p.primitives.retain(|s| s.id != "outer finish:5");
                }
                3 => {
                    if let Shape::Line { end, .. } = &mut p.primitives[0].shape {
                        end.x = 79.99999;
                    }
                }
                4 => {
                    p.material_tiles[1].outline[1].x = 79.99;
                    p.material_tiles[1].outline[2].x = 79.99;
                }
                _ => p.rooms.clear(),
            }
            assert_eq!(run(&p)[0].status, WallStatus::Review, "mode {mode}");
        }
    }

    #[test]
    fn outlined_batts_reject_bbox_containment_extra_arms_and_nested_holes() {
        let b = Box2 {
            x0: 0.,
            x1: 80.,
            y0: 5.,
            y1: 6.,
        };
        let broad = [
            Point::new(-10., 4.),
            Point::new(90., 4.),
            Point::new(90., 20.),
            Point::new(-10., 20.),
        ];
        assert!(!contains_rectangle(&broad, b, 1e-10, &mut MAX_WORK.clone()).unwrap());
        let notch = [
            Point::new(-10., 5.),
            Point::new(90., 5.),
            Point::new(90., 6.),
            Point::new(42., 6.),
            Point::new(42., 5.5),
            Point::new(40., 5.5),
            Point::new(40., 6.),
            Point::new(-10., 6.),
        ];
        assert!(!contains_rectangle(&notch, b, 1e-10, &mut MAX_WORK.clone()).unwrap());
        let (mut p, _) = fixture();
        outline(
            &mut p,
            "hole",
            &[
                Point::new(40., 5.2),
                Point::new(42., 5.2),
                Point::new(42., 5.8),
                Point::new(40., 5.8),
            ],
        );
        assert_eq!(run(&p)[0].status, WallStatus::Review);
    }

    #[test]
    fn outlined_batts_use_existing_cell_phase_roundoff_but_never_widen_paint() {
        let (mut p, _) = fixture();
        p.material_tiles[1].across = p.material_tiles[1].across * 1.0002;
        assert!(aligned_cell(&p.walls[0], &p.material_tiles[1]));
        assert_eq!(run(&p)[0].status, WallStatus::Ready);
        for mode in 0..4 {
            let (mut p, _) = fixture();
            let t = &mut p.material_tiles[1];
            match mode {
                0 => t.origin.y += 0.006,
                1 => t.across = t.across * 0.5,
                2 => t.repeat = Point::new(1., 1.),
                _ => t.across = t.across * 0.99,
            }
            assert!(!aligned_cell(&p.walls[0], t), "mode {mode}");
            assert_eq!(run(&p)[0].status, WallStatus::Review);
        }
    }

    #[test]
    fn outlined_batts_keep_furniture_doors_windows_and_old_ready_out() {
        for touch in [false, true] {
            let (mut p, _) = fixture();
            let y = if touch { 6. } else { 5.9 };
            p.objects.push(Object {
                geometry: None,
                profile_signature: None,
                footprint: Some(FurnitureFootprint {
                    selection_id: "fixture".into(),
                    polygon: [
                        Point::new(20., y),
                        Point::new(40., y),
                        Point::new(40., 20.),
                        Point::new(20., 20.),
                    ],
                }),
                id: "fixture".into(),
                at: Point::new(30., 10.),
                category: "Cabinet".into(),
                name: "Cabinet".into(),
                room_id: None,
                source_ids: vec!["borrowed furniture".into()],
                requires_review: true,
            });
            assert_eq!(
                run(&p)[0].status,
                if touch {
                    WallStatus::Ready
                } else {
                    WallStatus::Review
                }
            );
        }
        for kind in [OpeningKind::Door, OpeningKind::Window] {
            let (mut p, _) = fixture();
            p.openings.push(Opening {
                id: "opening".into(),
                page: 1,
                kind,
                operation: "Test".into(),
                host_wall_id: "other".into(),
                host_wall_ids: vec!["other".into()],
                start: Point::new(30., -10.),
                end: Point::new(30., 10.),
                hinge: None,
                swing_observations: vec![],
                source_ids: vec![],
                evidence: OpeningEvidence::FiniteJambSwing,
                requires_review: true,
                physical_jambs: None,
            });
            assert_eq!(run(&p)[0].status, WallStatus::Review);
        }
        let (mut p, mut ds) = fixture();
        let mut old = p.walls[0].clone();
        old.id = "old".into();
        p.walls.push(old);
        let mut d = ds[0].clone();
        d.wall_id = "old".into();
        d.status = WallStatus::Ready;
        ds.push(d.clone());
        select(&p, &mut ds).unwrap();
        assert_eq!(ds[0].status, WallStatus::Review);
        assert_eq!(ds[1], d);
    }

    #[test]
    fn outlined_batts_budget_and_alignment_errors_publish_no_partial_decisions() {
        let (p, mut ds) = fixture();
        let before = ds.clone();
        assert!(select_with_budget(&p, &mut ds, 10).is_err());
        assert_eq!(ds, before);
        ds[0].wall_id = "wrong".into();
        let before = ds.clone();
        assert!(select(&p, &mut ds).is_err());
        assert_eq!(ds, before);
    }

    #[test]
    fn geometric_requirements_preserve_actual_rotated_scaled_bands_and_fail_closed() {
        for angle in [0_f64, 0.61, 1.57] {
            for scale in [0.01, 1., 12.] {
                for reverse in [false, true] {
                    let (mut p, mut ds) = fixture();
                    let u = Point::new(angle.cos(), angle.sin());
                    let n = Point::new(-u.y, u.x);
                    let vector = |v: Point| (u * v.x + n * v.y) * scale;
                    let point = |v: Point| Point::new(1200., 900.) + vector(v);
                    p.width = 4000.;
                    p.height = 4000.;
                    for primitive in &mut p.primitives {
                        match &mut primitive.shape {
                            Shape::Line { start, end } => {
                                *start = point(*start);
                                *end = point(*end);
                            }
                            Shape::Text { at, height, .. } => {
                                *at = point(*at);
                                *height *= scale;
                            }
                            _ => panic!("unexpected synthetic source shape"),
                        }
                    }
                    for t in &mut p.material_tiles {
                        t.outline = t.outline.map(point);
                        t.origin = point(t.origin);
                        t.repeat = vector(t.repeat);
                        t.across = vector(t.across);
                    }
                    for w in &mut p.walls {
                        w.start = point(w.start);
                        w.end = point(w.end);
                        w.thickness *= scale;
                        if reverse {
                            std::mem::swap(&mut w.start, &mut w.end);
                        }
                    }
                    for room in &mut p.rooms {
                        room.polygon = room.polygon.iter().copied().map(point).collect();
                        room.area *= scale * scale;
                    }
                    let r = requirements(&p, &ds).unwrap();
                    assert_eq!(r.len(), 2, "angle={angle},scale={scale},reverse={reverse}");
                    for need in &r {
                        assert!(!need.source_ids.is_empty());
                        let a = need.rectangle[1] - need.rectangle[0];
                        let b = need.rectangle[3] - need.rectangle[0];
                        assert!((a.norm() - 80. * scale).abs() < 1e-8 * scale.max(1.));
                        assert!((b.norm() - scale).abs() < 1e-8 * scale.max(1.));
                        assert!(a.unit().dot(b.unit()).abs() < 1e-8);
                    }
                    let before = ds.clone();
                    select(&p, &mut ds).unwrap();
                    assert_eq!(
                        ds, before,
                        "geometry without visibility is not placement authority"
                    );
                    attach_visibility(&mut p, &ds);
                    select(&p, &mut ds).unwrap();
                    assert_eq!(
                        ds[0].status,
                        WallStatus::Ready,
                        "valid local receipts angle={angle},scale={scale},reverse={reverse}"
                    );
                }
            }
        }
    }

    #[test]
    fn every_facing_needs_an_exact_available_local_visibility_receipt() {
        let (mut page, initial) = fixture();
        attach_visibility(&mut page, &initial);
        assert_eq!(page.finish_visibility.as_ref().unwrap().receipts.len(), 2);
        for change in 0..7 {
            let mut p = page.clone();
            match change {
                0 => p.finish_visibility = None,
                1 => {
                    p.finish_visibility.as_mut().unwrap().receipts.pop();
                }
                2 => p.finish_visibility.as_mut().unwrap().revision = 2,
                3 => {
                    p.finish_visibility.as_mut().unwrap().status =
                        crate::finish_visibility::VisibilityStatus::Unavailable
                }
                4 => {
                    p.finish_visibility.as_mut().unwrap().status =
                        crate::finish_visibility::VisibilityStatus::BudgetExhausted
                }
                5 => p.finish_visibility.as_mut().unwrap().receipts[0].rectangle[0].x += 0.01,
                _ => p.finish_visibility.as_mut().unwrap().receipts[0]
                    .source_ids
                    .pop()
                    .map(|_| ())
                    .unwrap(),
            }
            let mut decisions = initial.clone();
            select(&p, &mut decisions).unwrap();
            assert_eq!(decisions, initial, "receipt change {change}");
        }
        let mut decisions = initial.clone();
        select(&page, &mut decisions).unwrap();
        let receipts = &page.finish_visibility.as_ref().unwrap().receipts;
        for receipt in receipts {
            assert!(decisions[0].support_source_ids.contains(&receipt.id));
            assert!(
                decisions[0]
                    .placement_extent
                    .as_ref()
                    .unwrap()
                    .source_ids
                    .contains(&receipt.id)
            );
        }
    }

    #[test]
    fn later_finish_only_erasures_withdraw_the_assembly_but_keep_geometric_requirements() {
        let (mut page, initial) = fixture();
        let queries = requirements(&page, &initial).unwrap();
        assert_eq!(queries.len(), 2);
        for change in [false, true] {
            let mut history = crate::finish_visibility::PaintHistory::default();
            for (i, p) in page.primitives.iter().enumerate() {
                history.record_ink(p.id.clone(), i as u64 + 1);
            }
            let order = page.primitives.len() as u64 + 1;
            if change {
                // Erases only the outer finish, entirely outside both batt rows.
                history.record_bounds(order, Point::new(30., -6.1), Point::new(40., -5.1));
            } else {
                // Outside the claimed body; irrelevant overprint remains harmless.
                history.record_bounds(order, Point::new(30., 20.), Point::new(40., 22.));
            }
            page.finish_visibility = Some(history.certify(page.page_number, queries.clone()));
            assert_eq!(requirements(&page, &initial).unwrap(), queries);
            let mut decisions = initial.clone();
            select(&page, &mut decisions).unwrap();
            assert_eq!(
                decisions[0].status,
                if change {
                    WallStatus::Review
                } else {
                    WallStatus::Ready
                }
            );
        }
    }

    fn native_fixture() -> Scan {
        let (mut page, _) = fixture();
        let names: HashMap<_, _> = page
            .primitives
            .iter()
            .enumerate()
            .map(|(i, p)| (p.id.clone(), format!("pdf:p1:path:{}:subpath:1", i + 1)))
            .collect();
        for p in &mut page.primitives {
            p.id = names[&p.id].clone();
        }
        for w in &mut page.walls {
            w.source_ids = w.source_ids.iter().map(|id| names[id].clone()).collect();
        }
        for r in &mut page.rooms {
            r.label_source_id = r.label_source_id.as_ref().map(|id| names[id].clone());
        }
        let w = &page.walls[0];
        // The original observation graph is deliberately coalesced off its
        // measured source axis; exact placement must not inherit that drift.
        page.nodes = vec![
            Node {
                id: "observed a".into(),
                position: w.start - Point::new(3., 2.),
                wall_ids: vec![w.id.clone()],
                requires_review: true,
            },
            Node {
                id: "observed b".into(),
                position: w.end + Point::new(4., 2.),
                wall_ids: vec![w.id.clone()],
                requires_review: true,
            },
        ];
        page.edges = vec![Edge {
            start_node: 0,
            end_node: 1,
            wall_ids: vec![w.id.clone()],
            opening_ids: vec![],
        }];
        let source = Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            width: page.width,
            height: page.height,
            units_to_mm: None,
            primitives: page.primitives.clone(),
            material_tiles: page.material_tiles.clone(),
            placement_paint_ids: page.placement_paint_ids.clone(),
            placement_polygon_paint_ids: page.placement_polygon_paint_ids.clone(),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            recessed_stock: vec![],
            masked_junction_stock: vec![],
            pocket_stock: vec![],
            placement_contact_paint: vec![],
        };
        page.semantics = crate::semantics::interpret(
            &source,
            &page.walls,
            &page.rooms,
            &page.openings,
            &page.objects,
            &page.room_connections,
            None,
        );
        let baseline = super::super::select_preview19(&page).unwrap();
        assert_eq!(baseline[0].status, WallStatus::Review);
        attach_visibility(&mut page, &baseline);
        page.placement_decisions = Some(super::super::select(&page).unwrap());
        page.exterior_enclosures = Some(super::super::enclosures::build(&page));
        page.exterior_connections = Some(super::super::connections::build(&page).unwrap());
        Scan {
            schema_version: "openplantrace.rust.scan.v16".into(),
            engine_version: env!("CARGO_PKG_VERSION").into(),
            layer_profile: None,
            object_label_profile: None,
            visual_classification: None,
            pages: vec![page],
            warnings: vec![],
        }
    }

    fn native_legacy_fixture(core_only: bool, erased: bool) -> (Scan, Vec<WallDecision>) {
        let (mut page, _) = legacy_fixture(core_only);
        let names: HashMap<_, _> = page
            .primitives
            .iter()
            .enumerate()
            .map(|(i, p)| (p.id.clone(), format!("pdf:p1:path:{}:subpath:1", i + 1)))
            .collect();
        for p in &mut page.primitives {
            p.id = names[&p.id].clone();
        }
        for w in &mut page.walls {
            w.source_ids = w.source_ids.iter().map(|id| names[id].clone()).collect();
        }
        let source = Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            width: page.width,
            height: page.height,
            units_to_mm: None,
            primitives: page.primitives.clone(),
            material_tiles: page.material_tiles.clone(),
            placement_paint_ids: page.placement_paint_ids.clone(),
            placement_polygon_paint_ids: page.placement_polygon_paint_ids.clone(),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            recessed_stock: vec![],
            masked_junction_stock: vec![],
            pocket_stock: vec![],
            placement_contact_paint: vec![],
        };
        page.semantics = crate::semantics::interpret(
            &source,
            &page.walls,
            &page.rooms,
            &page.openings,
            &page.objects,
            &page.room_connections,
            None,
        );
        let baseline = super::super::select_preview19(&page).unwrap();
        let needs = requirements(&page, &baseline).unwrap();
        let mut history = crate::finish_visibility::PaintHistory::default();
        for (i, p) in page.primitives.iter().enumerate() {
            history.record_ink(p.id.clone(), 2 * (i as u64 + 1));
        }
        if erased {
            history.record_bounds(11, Point::new(30., 4.2), Point::new(40., 4.8));
        }
        page.finish_visibility = Some(history.certify(1, needs));
        page.placement_decisions = Some(super::super::select(&page).unwrap());
        page.exterior_enclosures = Some(super::super::enclosures::build(&page));
        page.exterior_connections = Some(super::super::connections::build(&page).unwrap());
        let scan = Scan {
            schema_version: "openplantrace.rust.scan.v16".into(),
            engine_version: env!("CARGO_PKG_VERSION").into(),
            layer_profile: None,
            object_label_profile: None,
            visual_classification: None,
            pages: vec![page],
            warnings: vec![],
        };
        (scan, baseline)
    }

    #[test]
    fn native_legacy_facing_guard_replays_without_rewriting_valid_core_or_support() {
        for core_only in [false, true] {
            for erased in [false, true] {
                let (scan, baseline) = native_legacy_fixture(core_only, erased);
                crate::export::validate(&scan).unwrap();
                let restored: Scan =
                    serde_json::from_slice(&serde_json::to_vec(&scan).unwrap()).unwrap();
                crate::export::validate(&restored).unwrap();
                let page = &scan.pages[0];
                let decisions = page.placement_decisions.as_ref().unwrap();
                if core_only || !erased {
                    assert_eq!(decisions, &baseline);
                    assert_eq!(
                        crate::export::placement(&scan)["pages"][0]["wallSets"]["readyWallIds"]
                            .as_array()
                            .unwrap()
                            .len(),
                        1
                    );
                } else {
                    assert_eq!(
                        decisions[0].reason,
                        WallReason::InsulationFinishVisibilityMissing
                    );
                    assert_eq!(decisions[0].status, WallStatus::Review);
                    assert_eq!(
                        decisions[0].support_source_ids,
                        baseline[0].support_source_ids
                    );
                    assert!(
                        crate::export::placement(&scan)["pages"][0]["wallSets"]["readyWallIds"]
                            .as_array()
                            .unwrap()
                            .is_empty()
                    );
                    let mut forged = scan.clone();
                    forged.pages[0].placement_decisions = Some(baseline);
                    assert!(crate::export::validate(&forged).is_err());
                }
            }
        }
        let (mut scan, _) = native_legacy_fixture(false, false);
        scan.pages[0].finish_visibility = None;
        assert!(crate::export::validate(&scan).is_err());
    }

    #[test]
    fn native_replay_uses_complete_visible_bands_and_exact_source_axis() {
        use crate::export;
        let scan = native_fixture();
        export::validate(&scan).unwrap();
        let restored: Scan = serde_json::from_slice(&serde_json::to_vec(&scan).unwrap()).unwrap();
        export::validate(&restored).unwrap();
        assert_eq!(export::placement(&scan), export::placement(&restored));
        let page = &scan.pages[0];
        let wall = &page.walls[0];
        assert_eq!(
            page.placement_decisions.as_ref().unwrap()[0].reason,
            WallReason::SourceBoundedVisibleInsulationAssembly
        );
        let placement = export::placement(&scan);
        let p = &placement["pages"][0];
        let a: Point =
            serde_json::from_value(p["walls"][0]["centerLine"]["start"].clone()).unwrap();
        let b: Point = serde_json::from_value(p["walls"][0]["centerLine"]["end"].clone()).unwrap();
        assert_eq!((a, b), (wall.start, wall.end));
        assert!(
            p["walls"][0].get("observedCenterLine").is_none(),
            "untrimmed bodies need no duplicate line"
        );
        let nodes: HashMap<String, Point> = p["wallGraph"]["nodes"]
            .as_array()
            .unwrap()
            .iter()
            .map(|n| {
                (
                    n["id"].as_str().unwrap().to_owned(),
                    serde_json::from_value(n["position"].clone()).unwrap(),
                )
            })
            .collect();
        let axis = (wall.end - wall.start).unit();
        let length = wall.start.distance(wall.end);
        let mut total = 0.;
        for edge in p["wallGraph"]["edges"].as_array().unwrap() {
            if !edge["wallIds"]
                .as_array()
                .unwrap()
                .iter()
                .any(|id| id == &wall.id)
            {
                continue;
            }
            let a = nodes[edge["startNodeId"].as_str().unwrap()];
            let b = nodes[edge["endNodeId"].as_str().unwrap()];
            for point in [a, b] {
                let v = point - wall.start;
                assert!(v.cross(axis).abs() < 1e-9);
                assert!(v.dot(axis) >= -1e-9 && v.dot(axis) <= length + 1e-9);
            }
            total += a.distance(b);
        }
        assert!((total - length).abs() < 1e-9);
        let graph = page.exterior_connections.as_ref().unwrap();
        for node in &graph.nodes {
            let v = node.position - wall.start;
            assert!(v.cross(axis).abs() < 1e-9);
            assert!(v.dot(axis) >= -1e-9 && v.dot(axis) <= length + 1e-9);
        }
    }

    #[test]
    fn native_validation_rejects_missing_or_forged_finish_authority_and_downgrade() {
        let scan = native_fixture();
        crate::export::validate(&scan).unwrap();
        for change in 0..15 {
            let mut bad = scan.clone();
            let p = &mut bad.pages[0];
            match change {
                0 => p.finish_visibility = None,
                1 => {
                    p.finish_visibility.as_mut().unwrap().receipts.pop();
                }
                2 => p.finish_visibility.as_mut().unwrap().receipts[0].source_orders[0] += 2,
                3 => p.finish_visibility.as_mut().unwrap().receipts[0].anchor_order += 2,
                4 => p.finish_visibility.as_mut().unwrap().receipts[0].rectangle[0].x += 0.01,
                5 => p.finish_visibility.as_mut().unwrap().receipts[0].id = "forged".into(),
                6 => {
                    p.placement_decisions.as_mut().unwrap()[0]
                        .support_source_ids
                        .pop();
                }
                7 => {
                    p.placement_decisions.as_mut().unwrap()[0]
                        .placement_extent
                        .as_mut()
                        .unwrap()
                        .end
                        .x += 0.01
                }
                8 => bad.schema_version = "openplantrace.rust.scan.v15".into(),
                9 => {
                    p.finish_visibility.as_mut().unwrap().receipts[0].contour_edges[0]
                        .start
                        .x += 0.01
                }
                10 => {
                    p.finish_visibility.as_mut().unwrap().receipts[0]
                        .contour_edges
                        .pop();
                }
                11 => {
                    let e = &mut p.finish_visibility.as_mut().unwrap().receipts[0].contour_edges[0];
                    std::mem::swap(&mut e.start, &mut e.end);
                }
                12 => {
                    let r = &mut p.finish_visibility.as_mut().unwrap().receipts[0];
                    r.contour_edges.push(r.contour_edges[0].clone());
                }
                13 => {
                    let e = &mut p.finish_visibility.as_mut().unwrap().receipts[0].contour_edges[0];
                    e.end = e.start;
                }
                _ => {
                    let source = p.finish_visibility.as_ref().unwrap().receipts[0].contour_edges[0]
                        .source_id
                        .clone();
                    if let Shape::Line { start, .. } = &mut p
                        .primitives
                        .iter_mut()
                        .find(|p| p.id == source)
                        .unwrap()
                        .shape
                    {
                        start.y += 0.1;
                    }
                }
            }
            assert!(
                crate::export::validate(&bad).is_err(),
                "changed authority {change}"
            );
        }
    }

    #[test]
    fn native_receipt_ids_remain_canonical_when_all_cached_support_is_coherently_rebuilt() {
        for suffix in ["bad", "0", "01", "+1", "18446744073709551616", "1x", ""] {
            let mut bad = native_fixture();
            let p = &mut bad.pages[0];
            let id = format!("p1:finish-visibility:{suffix}");
            p.finish_visibility.as_mut().unwrap().receipts[0].id = id.clone();
            p.placement_decisions = Some(super::super::select(p).unwrap());
            assert!(
                p.placement_decisions.as_ref().unwrap()[0]
                    .support_source_ids
                    .contains(&id)
            );
            p.exterior_enclosures = Some(super::super::enclosures::build(p));
            p.exterior_connections = Some(super::super::connections::build(p).unwrap());
            assert!(
                crate::export::validate(&bad).is_err(),
                "coherent invalid receipt suffix{suffix}"
            );
        }
    }
}
