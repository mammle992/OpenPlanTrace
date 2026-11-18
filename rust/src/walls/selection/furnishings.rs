//! An outlined counter or work surface may directly abut a structural wall.
//! Require a complete outline, an independent narrower backing, and actual
//! closed equipment contours inside it. Hatching and isolated strokes do not
//! enclose a fitting. The narrower wall keeps its own geometry.
use super::*;
#[cfg(test)]
mod tests;

pub(super) struct Context {
    arcs: Vec<(usize, ArcGeometry)>,
    arc_index: Index,
    backs: Vec<(Segment, f64, Vec<usize>)>,
    back_index: Index,
}
impl Context {
    pub(super) fn new(page: &Page, walls: &[Wall], ids: &HashMap<&str, usize>) -> Self {
        let mut arcs = vec![];
        let mut boxes = vec![];
        for (i, p) in page.primitives.iter().enumerate() {
            if !matches!(p.layer_kind(), LayerKind::Unknown | LayerKind::Object) {
                continue;
            }
            if let Shape::Arc {
                center,
                radius,
                start_angle,
                sweep,
            } = p.shape
            {
                arcs.push((
                    i,
                    ArcGeometry {
                        center,
                        radius,
                        start_angle,
                        sweep,
                    },
                ));
                let c = V::from(&center);
                boxes.push(Box2::new(c, c).pad(radius));
            }
        }
        let arc_index = Index::build(&boxes);
        boxes.clear();
        let mut backs = vec![];
        for w in walls
            .iter()
            .filter(|w| w.body == BodyEvidence::ParallelFaces && w.curve.is_none())
        {
            let Some(l) = Segment::new(V::from(&w.start), V::from(&w.end)) else {
                continue;
            };
            let sources = w
                .source_ids
                .iter()
                .filter_map(|s| ids.get(s.as_str()).copied())
                .collect();
            backs.push((l, w.thickness, sources));
            boxes.push(l.bbox().pad(w.thickness / 2.));
        }
        Self {
            arcs,
            arc_index,
            backs,
            back_index: Index::build(&boxes),
        }
    }
    // Unfilled double-line drawings can still supply the backing if the
    // narrower band continues beyond the outlined fixture and its faces are
    // independent of the broad pair that is being tested.
    fn backing(
        &self,
        painted: &casework::Context,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        side: f64,
        excluded: &BTreeSet<usize>,
        enclosed_overhang: bool,
        opts: &Options,
    ) -> Result<Option<Vec<usize>>, String> {
        if let Some(s) = painted.backing(ink, axis, width, side, opts)? {
            return Ok(Some(vec![s]));
        }
        let mut near = vec![];
        self.back_index
            .neighbors(axis.bbox().pad(width / 2. + opts.snap_tolerance), &mut near);
        ink.charge(near.len())?;
        for i in near {
            let (l, depth, ids) = &self.backs[i];
            if 2. * depth > width || l.dir.cross(axis.dir).abs() > ANGLE.sin() {
                continue;
            }
            let (lo, hi) = range(axis, *l);
            let tol = opts.snap_tolerance;
            if lo > tol
                || hi < axis.len - tol
                || (!enclosed_overhang && (-lo).max(hi - axis.len) < *depth)
            {
                continue;
            }
            let fit = |p: V| (height(axis, p) * side - (width + depth) / 2.).abs() <= tol / 2.;
            if fit(l.a) && fit(l.b) && ids.len() >= 2 && ids.iter().all(|s| !excluded.contains(s)) {
                return Ok(Some(ids.clone()));
            }
        }
        Ok(None)
    }
    pub(super) fn surface(
        &self,
        painted: &casework::Context,
        fills: &enclosure::Context,
        page: &Page,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        sources: &BTreeSet<usize>,
        nearby: &[usize],
        opts: &Options,
        join: f64,
    ) -> Result<Option<Furniture>, String> {
        for side in [-1., 1.] {
            let Some((bounds, overhang, perimeter)) =
                surface_outline(ink, axis, width, side, nearby, opts, join)?
            else {
                continue;
            };
            // Ordinary broad pairs still require a long independent backing.
            // A shorter frame can be decisive when both real tabletop ends
            // overhang it and its face differs from the real rear edge.
            if !overhang && axis.len >= 3. * width {
                continue;
            }
            let mut excluded = sources.clone();
            if overhang {
                excluded.extend(bounds);
            }
            let Some(mut witnesses) =
                self.backing(painted, ink, axis, width, side, &excluded, overhang, opts)?
            else {
                continue;
            };
            let mut graph = Contours::new(join.min(width * 0.001));
            let mut circles = vec![];
            let local = |p: V| V(axis.offset(p), height(axis, p));
            let inside =
                |p: V| p.0 > join && p.0 < axis.len - join && p.1.abs() < width / 2. - join;
            for &i in nearby {
                let s = ink.strokes[i];
                let (a, b) = (local(s.line.a), local(s.line.b));
                if inside(a) && inside(b) {
                    graph.edge(a, b, s.source);
                }
            }
            let mut arcs = vec![];
            self.arc_index
                .neighbors(axis.bbox().pad(width / 2.), &mut arcs);
            ink.charge(arcs.len())?;
            for j in arcs {
                let (source, arc) = self.arcs[j];
                // Include analytic end points and a bounded circular polygon.
                let n = (arc.sweep.abs() / (PI / 24.)).ceil().clamp(2., 64.) as usize;
                ink.charge(n)?;
                let points: Vec<V> = (0..=n)
                    .map(|i| local(V::from(&arc.at(i as f64 / n as f64))))
                    .collect();
                if points.iter().copied().all(inside) {
                    if (arc.sweep.abs() - TAU).abs() <= EPS {
                        // An explicit full circle remains one closed contour
                        // even when a coarse polygon retraces parts of it.
                        let mut ring = points;
                        ring.pop();
                        if let Some(r) = Ring::from_loop(ring, BTreeSet::from([source]), graph.tol)
                        {
                            circles.push(r);
                        }
                    } else {
                        for e in points.windows(2) {
                            graph.edge(e[0], e[1], source);
                        }
                    }
                }
            }
            if let Some(marks) = graph.symbols(ink, circles)? {
                if fills.spans_interior(page, ink, axis, width, join)? {
                    continue;
                }
                witnesses.extend(bounds);
                witnesses.extend(marks);
                return Ok(Some(Furniture {
                    sources: witnesses,
                    perimeter,
                }));
            }
        }
        Ok(None)
    }
}

// Follow the real outline. Its rear edge may run a little way into the
// adjacent structural band; no missing edge is supplied by the wall axis.
fn surface_outline(
    ink: &mut Ink,
    axis: Segment,
    width: f64,
    back: f64,
    nearby: &[usize],
    opts: &Options,
    join: f64,
) -> Result<Option<([usize; 4], bool, [Point; 4])>, String> {
    let half = width / 2.;
    for &i in nearby {
        let front = ink.strokes[i];
        let l = front.line;
        let (lo, hi) = range(axis, l);
        let face = |p: V| (back * height(axis, p) + half).abs() <= join;
        let extra = hi - axis.len;
        let flush = lo.abs() <= join && extra.abs() <= join;
        let overhang = lo < -join
            && extra > join
            && -lo <= opts.snap_tolerance
            && extra <= opts.snap_tolerance;
        if (!flush && !overhang) || !face(l.a) || !face(l.b) {
            continue;
        }
        let first = ink.returns(l.a, axis, join)?;
        let second = ink.returns(l.b, axis, join)?;
        for &(p, x) in &first {
            for &(q, y) in &second {
                ink.charge(1)?;
                let a = back * height(axis, p);
                let b = back * height(axis, q);
                let reaches_back = if overhang {
                    (a - half).abs() <= opts.snap_tolerance
                        && (a - half).abs() > join
                        && (b - half).abs() > join
                } else {
                    a >= half - join && a <= width
                };
                if !reaches_back || (a - b).abs() > join {
                    continue;
                }
                if let Some(z) = ink.joins(p, q, join)? {
                    return Ok(Some((
                        [front.source, x, y, z],
                        overhang,
                        [l.a.point(), l.b.point(), q.point(), p.point()],
                    )));
                }
            }
        }
    }
    Ok(None)
}

struct Contours {
    tol: f64,
    points: Vec<V>,
    cells: HashMap<(i64, i64), Vec<usize>>,
    edges: BTreeMap<(usize, usize), usize>,
}
struct Ring {
    poly: Vec<V>,
    bounds: Box2,
    area: f64,
    ids: BTreeSet<usize>,
}
impl Ring {
    fn from_loop(poly: Vec<V>, ids: BTreeSet<usize>, tol: f64) -> Option<Self> {
        let mut area = 0.;
        let mut bounds = Box2::new(poly[0], poly[0]);
        for (&a, &b) in poly.iter().zip(poly.iter().cycle().skip(1)) {
            area += a.cross(b);
            bounds = bounds.union(Box2::new(a, b));
        }
        (area.abs() > tol * tol).then_some(Self {
            poly,
            bounds,
            area: area / 2.,
            ids,
        })
    }
}
impl Contours {
    fn new(tol: f64) -> Self {
        Self {
            tol,
            points: vec![],
            cells: HashMap::new(),
            edges: BTreeMap::new(),
        }
    }
    fn node(&mut self, p: V) -> usize {
        let cell = (
            (p.0 / self.tol).round() as i64,
            (p.1 / self.tol).round() as i64,
        );
        for x in -1..=1 {
            for y in -1..=1 {
                if let Some(ids) = self.cells.get(&(cell.0 + x, cell.1 + y)) {
                    if let Some(&id) = ids
                        .iter()
                        .find(|&&i| self.points[i].minus(p).norm() <= self.tol)
                    {
                        return id;
                    }
                }
            }
        }
        let id = self.points.len();
        self.points.push(p);
        self.cells.entry(cell).or_default().push(id);
        id
    }
    fn edge(&mut self, a: V, b: V, source: usize) {
        let u = self.node(a);
        let v = self.node(b);
        if u != v {
            self.edges.entry((u.min(v), u.max(v))).or_insert(source);
        }
    }
    fn symbols(self, ink: &mut Ink, mut rings: Vec<Ring>) -> Result<Option<Vec<usize>>, String> {
        ink.charge(self.edges.len())?;
        let mut adj = vec![BTreeSet::new(); self.points.len()];
        for &(a, b) in self.edges.keys() {
            adj[a].insert(b);
            adj[b].insert(a);
        }
        let mut leaves: Vec<_> = (0..adj.len()).filter(|&i| adj[i].len() == 1).collect();
        while let Some(i) = leaves.pop() {
            if let Some(j) = adj[i].pop_first() {
                adj[j].remove(&i);
                if adj[j].len() == 1 {
                    leaves.push(j);
                }
            }
        }
        let mut seen = vec![false; adj.len()];
        for start in 0..adj.len() {
            if seen[start] || adj[start].len() != 2 {
                continue;
            }
            let (mut prev, mut now) = (usize::MAX, start);
            let mut ids = BTreeSet::new();
            let mut poly = vec![];
            while !seen[now] && adj[now].len() == 2 {
                seen[now] = true;
                poly.push(self.points[now]);
                let next = *adj[now].iter().find(|&&n| n != prev).unwrap();
                ids.insert(self.edges[&(now.min(next), now.max(next))]);
                prev = now;
                now = next;
            }
            if now != start || poly.len() < 3 {
                continue;
            }
            if let Some(r) = Ring::from_loop(poly, ids, self.tol) {
                rings.push(r);
            }
        }
        // A fitting has a closed outline *inside* another real contour. A
        // second separated fitting distinguishes an occupied work surface
        // from one embedded mark or a set of hatch lines. Boundaries that
        // touch or share a source cannot supply independent evidence.
        for outer in &rings {
            for inner in &rings {
                ink.charge(1)?;
                let a = outer.bounds;
                let b = inner.bounds;
                let t = self.tol;
                if b.0.0 <= a.0.0 + t
                    || b.0.1 <= a.0.1 + t
                    || b.1.0 >= a.1.0 - t
                    || b.1.1 >= a.1.1 - t
                    || !outer.ids.is_disjoint(&inner.ids)
                {
                    continue;
                }
                ink.charge(outer.poly.len().saturating_mul(inner.poly.len()))?;
                let contains = outer
                    .poly
                    .iter()
                    .zip(outer.poly.iter().cycle().skip(1))
                    .all(|(&u, &v)| {
                        let d = v.minus(u);
                        inner
                            .poly
                            .iter()
                            .all(|p| outer.area.signum() * d.cross(p.minus(u)) > t * d.norm())
                    });
                if !contains {
                    continue;
                }
                for other in &rings {
                    ink.charge(1)?;
                    if !a.pad(t).touches(other.bounds)
                        && other.area.abs() >= inner.area.abs() / 4.
                        && outer.ids.is_disjoint(&other.ids)
                        && inner.ids.is_disjoint(&other.ids)
                    {
                        return Ok(Some(
                            outer
                                .ids
                                .iter()
                                .chain(&inner.ids)
                                .chain(&other.ids)
                                .copied()
                                .collect(),
                        ));
                    }
                }
            }
        }
        Ok(None)
    }
}
