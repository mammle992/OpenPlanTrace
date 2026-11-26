//! Reconcile adjacent sashes inside a single measured casing.
use crate::{
    model::{
        LayerKind, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape, Wall,
        on_segment,
    },
    placement::{WallStatus, effective_wall},
};
use std::collections::{BTreeSet, HashMap, HashSet, VecDeque};

const MAX_WORK: usize = 12_000_000;

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("composite window evidence budget exceeded")?;
    Ok(())
}

pub(super) struct Composite {
    pub members: [usize; 2],
    pub opening: Opening,
}

#[derive(Clone, Copy)]
struct Axis {
    origin: Point,
    along: Point,
}

impl Axis {
    fn project(self, p: Point) -> Point {
        let v = p - self.origin;
        Point::new(v.dot(self.along), self.along.cross(v))
    }

    fn restore(self, p: Point) -> Point {
        self.origin + self.along * p.x + Point::new(-self.along.y, self.along.x) * p.y
    }
}

#[derive(Clone, Copy)]
struct Line<'a> {
    a: Point,
    b: Point,
    id: &'a str,
}

impl<'a> Line<'a> {
    fn from(p: &'a Primitive) -> Option<Self> {
        if let Shape::Line { start, end } = p.shape {
            (start.finite() && end.finite() && start != end).then_some(Self {
                a: start,
                b: end,
                id: &p.id,
            })
        } else {
            None
        }
    }

    fn local(self, axis: Axis) -> Self {
        let (a, b) = (axis.project(self.a), axis.project(self.b));
        let (a, b) = if a.x <= b.x { (a, b) } else { (b, a) };
        Self { a, b, id: self.id }
    }
}

fn structural(p: &Primitive) -> bool {
    p.layer_category_override.map_or_else(
        || p.layer.as_deref().is_some_and(crate::layers::is_named_wall),
        |_| p.layer_kind() == LayerKind::Wall,
    )
}

#[derive(Clone, Copy)]
struct Pane {
    index: usize,
    ends: [f64; 2],
    sides: [f64; 2],
}

fn measure_pane<'a>(
    index: usize,
    o: &'a Opening,
    wall: &Wall,
    axis: Axis,
    sources: &HashMap<&str, &'a Primitive>,
    layers: &HashSet<&str>,
) -> Option<(&'a str, Pane)> {
    let a = axis.project(o.start);
    let b = axis.project(o.end);
    let eps = wall.thickness * 0.005;
    if !a.finite() || !b.finite() || !eps.is_finite() || eps <= 0. || (a.y - b.y).abs() > eps {
        return None;
    }
    let ends = [a.x.min(b.x), a.x.max(b.x)];
    let mut lines = Vec::new();
    let mut layer = None;
    for id in &o.source_ids {
        let p = sources.get(id.as_str())?;
        let name = p.layer.as_deref().filter(|&l| layers.contains(l))?;
        if layer.replace(name).is_some_and(|old| old != name) || !structural(p) {
            return None;
        }
        lines.push(Line::from(p)?.local(axis));
    }
    let mut sides = [f64::INFINITY, f64::NEG_INFINITY];
    for l in &lines {
        if (l.a.y - l.b.y).abs() <= eps
            && (l.a.x - ends[0]).abs() <= eps
            && (l.b.x - ends[1]).abs() <= eps
        {
            let y = l.a.y.midpoint(l.b.y);
            sides = [sides[0].min(y), sides[1].max(y)];
        }
    }
    let rail_depth = sides[1] - sides[0];
    if !rail_depth.is_finite() || rail_depth <= eps {
        return None;
    }
    // The center rail may be in the sash receipts in place of the other
    // face. Its two independent end caps still measure the same full depth.
    let caps = ends.map(|x| {
        lines
            .iter()
            .filter_map(|l| {
                let lo = l.a.y.min(l.b.y);
                let hi = l.a.y.max(l.b.y);
                ((l.a.x - x).abs().max((l.b.x - x).abs()) <= eps
                    && lo <= sides[0] + eps
                    && hi >= sides[1] - eps
                    && ((lo - sides[0]).abs() <= eps || (hi - sides[1]).abs() <= eps)
                    && ends[1] - ends[0] >= 4. * (hi - lo))
                    .then_some([lo, hi])
            })
            .collect::<Vec<_>>()
    });
    let compatible =
        |a: [f64; 2], b: [f64; 2]| a.into_iter().zip(b).all(|(x, y)| (x - y).abs() <= eps);
    let mut measured = None;
    for &cap in &caps[0] {
        if caps[1].iter().any(|&other| compatible(cap, other)) {
            if measured.is_some_and(|old| !compatible(old, cap)) {
                return None;
            }
            measured.get_or_insert(cap);
        }
    }
    Some((
        layer?,
        Pane {
            index,
            ends,
            sides: measured?,
        },
    ))
}

fn touches(a: Line<'_>, b: Line<'_>, eps: f64) -> bool {
    [a.a, a.b]
        .into_iter()
        .any(|p| on_segment(p, b.a, b.b).0.distance(p) <= eps)
        || [b.a, b.b]
            .into_iter()
            .any(|p| on_segment(p, a.a, a.b).0.distance(p) <= eps)
}

fn cap<'a>(
    a: Point,
    b: Point,
    ink: &[Line<'a>],
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<&'a str>>, String> {
    charge(work, ink.len())?;
    let reach = (b.y - a.y).abs() * 0.22;
    let x0 = a.x.min(b.x) - reach;
    let x1 = a.x.max(b.x) + reach;
    let y0 = a.y.min(b.y) - eps;
    let y1 = a.y.max(b.y) + eps;
    let lines: Vec<_> = ink
        .iter()
        .copied()
        .filter(|l| {
            l.a.x >= x0
                && l.b.x <= x1
                && l.a.y.min(l.b.y) >= y0
                && l.a.y.max(l.b.y) <= y1
                && (l.a.x - l.b.x).abs().min((l.a.y - l.b.y).abs()) <= eps
        })
        .collect();
    if lines.len() > 128 {
        return Ok(None);
    }
    let mut queue = VecDeque::new();
    let mut previous = vec![None; lines.len()];
    for (i, l) in lines.iter().enumerate() {
        if on_segment(a, l.a, l.b).0.distance(a) <= eps {
            queue.push_back(i);
            previous[i] = Some(i);
        }
    }
    while let Some(i) = queue.pop_front() {
        let l = lines[i];
        if on_segment(b, l.a, l.b).0.distance(b) <= eps {
            let mut proof = vec![l.id];
            let mut at = i;
            while let Some(parent) = previous[at].filter(|&p| p != at) {
                proof.push(lines[parent].id);
                at = parent;
            }
            return Ok(Some(proof));
        }
        charge(work, lines.len())?;
        for (j, &other) in lines.iter().enumerate() {
            if previous[j].is_none() && touches(l, other, eps) {
                previous[j] = Some(i);
                queue.push_back(j);
            }
        }
    }
    Ok(None)
}

#[derive(Clone, Copy)]
struct Aperture {
    axis: Axis,
    ends: [f64; 2],
    sides: [f64; 2],
    eps: f64,
}

impl Aperture {
    fn extent_enters(self, points: impl Iterator<Item = Point>, margin: f64) -> bool {
        let mut lo = Point::new(f64::INFINITY, f64::INFINITY);
        let mut hi = Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY);
        for p in points.map(|p| self.axis.project(p)) {
            lo = Point::new(lo.x.min(p.x), lo.y.min(p.y));
            hi = Point::new(hi.x.max(p.x), hi.y.max(p.y));
        }
        hi.x + margin > self.ends[0] + self.eps
            && lo.x - margin < self.ends[1] - self.eps
            && hi.y + margin > self.sides[0] + self.eps
            && lo.y - margin < self.sides[1] - self.eps
    }
}

fn blocked(
    area: Aperture,
    members: [usize; 2],
    page: &PageScan,
    walls: &HashMap<&str, &Wall>,
    ready: &[std::borrow::Cow<'_, Wall>],
    paints: &[&[Point]],
    work: &mut usize,
) -> Result<bool, String> {
    for points in paints {
        charge(work, points.len())?;
        if area.extent_enters(points.iter().copied(), 0.) {
            return Ok(true);
        }
    }
    charge(work, ready.len().saturating_add(page.openings.len()))?;
    for w in ready {
        if let Some(curve) = w.curve {
            if area.extent_enters(
                std::iter::once(curve.center),
                curve.radius + w.thickness * 0.5,
            ) {
                return Ok(true);
            }
        } else {
            let u = (w.end - w.start).unit();
            let n = Point::new(-u.y, u.x) * (w.thickness * 0.5);
            if area.extent_enters(
                [w.start - n, w.start + n, w.end - n, w.end + n].into_iter(),
                0.,
            ) {
                return Ok(true);
            }
        }
    }
    for (i, o) in page.openings.iter().enumerate() {
        if members.contains(&i) {
            continue;
        }
        charge(
            work,
            o.host_wall_ids
                .len()
                .saturating_add(o.swing_observations.len()),
        )?;
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|id| walls.get(id.as_str()))
            .map(|w| w.thickness)
            .fold(0., f64::max);
        // Use its finite throat and measured closed leaf, never the full swing box.
        if area.extent_enters([o.start, o.end].into_iter(), width * 0.5)
            || o.swing_observations
                .iter()
                .any(|s| area.extent_enters([s.hinge, s.closed_tip].into_iter(), s.leaf_tolerance))
        {
            return Ok(true);
        }
    }
    Ok(false)
}

fn find_casing<'a>(
    panes: [Pane; 2],
    axis: Axis,
    ink: &[Line<'a>],
    known: &HashSet<&str>,
    work: &mut usize,
) -> Result<Option<(Aperture, Vec<&'a str>)>, String> {
    let [a, b] = panes;
    let gap = b.ends[0] - a.ends[1];
    if gap <= 0. || gap > 0.4 * (a.ends[1] - a.ends[0]).min(b.ends[1] - b.ends[0]) {
        return Ok(None);
    }
    let sides = [a.sides[0].min(b.sides[0]), a.sides[1].max(b.sides[1])];
    let maximum = 5. * (a.sides[1] - a.sides[0]).max(b.sides[1] - b.sides[0]);
    let mut lower = Vec::new();
    let mut upper = Vec::new();
    charge(work, ink.len())?;
    for &line in ink {
        if known.contains(line.id)
            || line.a.x >= a.ends[0]
            || line.b.x <= b.ends[1]
            || a.ends[0] - line.a.x > maximum
            || line.b.x - b.ends[1] > maximum
            || (line.b.y - line.a.y).abs() > maximum * 0.005
        {
            continue;
        }
        if line.a.y.max(line.b.y) < sides[0] && sides[0] - line.a.y <= maximum {
            lower.push(line);
        }
        if line.a.y.min(line.b.y) > sides[1] && line.b.y - sides[1] <= maximum {
            upper.push(line);
        }
    }
    charge(work, lower.len().saturating_mul(upper.len()))?;
    let mut found = None;
    for low in lower {
        for &high in &upper {
            if low.id.split(":subpath:").next() == high.id.split(":subpath:").next() {
                continue;
            }
            let width = high.a.y - low.a.y;
            let eps = width * 0.005;
            let left_step = low.a.x - high.a.x;
            let right_step = high.b.x - low.b.x;
            if width > maximum
                || gap > width * 0.65
                || left_step.abs().max(right_step.abs()) > width * 0.2
                || (left_step - right_step).abs() > eps
                || (high.b.y - low.b.y - width).abs() > eps
            {
                continue;
            }
            let ends = [low.a.x.max(high.a.x), low.b.x.min(high.b.x)];
            if a.ends[0] - ends[0] <= eps
                || ends[1] - b.ends[1] <= eps
                || a.ends[0] - ends[0] > width * 0.5
                || ends[1] - b.ends[1] > width * 0.5
            {
                continue;
            }
            let Some(left) = cap(low.a, high.a, ink, eps, work)? else {
                continue;
            };
            let Some(right) = cap(low.b, high.b, ink, eps, work)? else {
                continue;
            };
            if found.is_some() {
                return Ok(None);
            }
            let area = Aperture {
                axis,
                ends,
                sides: [low.a.y, high.a.y],
                eps,
            };
            let proof = [low.id, high.id]
                .into_iter()
                .chain(left)
                .chain(right)
                .collect();
            found = Some((area, proof));
        }
    }
    Ok(found)
}

pub(super) fn proposals(
    page: &PageScan,
    owners: &[Option<usize>],
) -> Result<Vec<Composite>, String> {
    let Some(decisions) = page.placement_decisions.as_ref() else {
        return Ok(vec![]);
    };
    if owners.len() != page.openings.len() {
        return Err("incomplete window ownership inventory".into());
    }
    let mut work = MAX_WORK;
    let decisions: HashMap<_, _> = decisions.iter().map(|d| (d.wall_id.as_str(), d)).collect();
    let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let mut grouped = HashMap::<(&str, &str), Vec<Pane>>::new();
    let mut blocked_ids = HashSet::new();
    for ids in page.edges.iter().map(|e| &e.opening_ids).chain(
        decisions
            .values()
            .filter(|d| d.status == WallStatus::Ready)
            .map(|d| &d.opening_ids),
    ) {
        charge(&mut work, ids.len().saturating_add(1))?;
        blocked_ids.extend(ids.iter().map(String::as_str));
    }
    blocked_ids.extend(page.room_connections.iter().map(|c| c.opening_id.as_str()));
    if !page.openings.iter().zip(owners).any(|(o, owner)| {
        o.kind == OpeningKind::Window
            && o.evidence == OpeningEvidence::FiniteWindowAssembly
            && owner.is_none()
    }) {
        return Ok(vec![]);
    }
    charge(&mut work, page.primitives.len())?;
    let layers: HashSet<_> = page
        .primitives
        .iter()
        .filter(|p| structural(p))
        .filter_map(|p| p.layer.as_deref())
        .collect();
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let axis_of = |w: &Wall| Axis {
        origin: w.start,
        along: (w.end - w.start).unit(),
    };
    for (i, (o, owner)) in page.openings.iter().zip(owners).enumerate() {
        let [host] = o.host_wall_ids.as_slice() else {
            continue;
        };
        if owner.is_some()
            || o.kind != OpeningKind::Window
            || o.evidence != OpeningEvidence::FiniteWindowAssembly
            || blocked_ids.contains(o.id.as_str())
            || !decisions
                .get(host.as_str())
                .is_some_and(|d| d.status != WallStatus::Ready)
        {
            continue;
        }
        let Some(w) = walls.get(host.as_str()).filter(|w| w.curve.is_none()) else {
            continue;
        };
        charge(&mut work, o.source_ids.len().saturating_mul(3))?;
        if let Some((layer, pane)) = measure_pane(i, o, w, axis_of(w), &sources, &layers) {
            grouped.entry((host, layer)).or_default().push(pane);
        }
    }
    grouped.retain(|_, panes| panes.len() > 1);
    if grouped.is_empty() {
        return Ok(vec![]);
    }
    let needed: HashSet<_> = grouped.keys().map(|&(_, layer)| layer).collect();
    let mut by_layer = HashMap::<&str, Vec<Line<'_>>>::new();
    let authorized: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect();
    let mut paints = vec![];
    for p in &page.primitives {
        if !structural(p) {
            continue;
        }
        if let Some(layer) = p.layer.as_deref().filter(|&l| needed.contains(l))
            && let Some(line) = Line::from(p)
        {
            by_layer.entry(layer).or_default().push(line);
        }
        if authorized.contains(p.id.as_str())
            && let Shape::Polyline {
                points,
                filled: true,
                closed: true,
            } = &p.shape
        {
            paints.push(points.as_slice());
        }
    }
    let ready: Vec<_> = page
        .walls
        .iter()
        .filter_map(|w| {
            let d = decisions.get(w.id.as_str()).copied()?;
            (d.status == WallStatus::Ready).then(|| effective_wall(w, Some(d)))
        })
        .collect();
    let mut found = vec![];
    let mut memberships = vec![0u8; owners.len()];
    // Stable group traversal keeps resource handling independent of hash seeds.
    let mut groups: Vec<_> = grouped.into_iter().collect();
    groups.sort_by_key(|(key, _)| *key);
    for ((host, layer), mut panes) in groups {
        let axis = axis_of(walls[host]);
        let Some(raw) = by_layer.get(layer) else {
            continue;
        };
        charge(&mut work, raw.len())?;
        let local: Vec<_> = raw.iter().map(|l| l.local(axis)).collect();
        panes.sort_by(|a, b| a.ends[0].total_cmp(&b.ends[0]).then(a.index.cmp(&b.index)));
        for pair in panes.windows(2) {
            let [a, b] = [pair[0], pair[1]];
            let members = [a.index, b.index];
            let ids: HashSet<_> = members
                .iter()
                .flat_map(|&i| &page.openings[i].source_ids)
                .map(String::as_str)
                .collect();
            let Some((bounds, proof)) = find_casing([a, b], axis, &local, &ids, &mut work)? else {
                continue;
            };
            if blocked(bounds, members, page, &walls, &ready, &paints, &mut work)? {
                continue;
            }
            let center = bounds.sides[0].midpoint(bounds.sides[1]);
            for i in members {
                memberships[i] = memberships[i].saturating_add(1);
            }
            found.push(Composite {
                members,
                opening: Opening {
                    id: String::new(),
                    page: page.page_number,
                    kind: OpeningKind::Window,
                    operation: "Unknown".into(),
                    host_wall_id: host.into(),
                    host_wall_ids: vec![host.into()],
                    start: axis.restore(Point::new(bounds.ends[0], center)),
                    end: axis.restore(Point::new(bounds.ends[1], center)),
                    hinge: None,
                    swing_observations: vec![],
                    physical_jambs: None,
                    source_ids: ids
                        .into_iter()
                        .chain(proof)
                        .collect::<BTreeSet<_>>()
                        .into_iter()
                        .map(str::to_owned)
                        .collect(),
                    evidence: OpeningEvidence::CompositeWindowFrame,
                    requires_review: true,
                },
            });
        }
    }
    found.retain(|c| c.members.iter().all(|&i| memberships[i] == 1));
    found.sort_by_key(|c| c.members);
    Ok(found)
}
