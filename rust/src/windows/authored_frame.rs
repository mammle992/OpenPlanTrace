//! Recover a finite exterior aperture from its casing and independent glazing ink.
use crate::{
    model::{
        LayerKind, Opening, OpeningEvidence, OpeningKind, Options, PageScan, Point, Primitive,
        Shape, on_segment,
    },
    placement::{WallStatus, effective_wall},
};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};

pub(super) struct AuthoredFrame {
    pub members: Vec<usize>,
    pub opening: Opening,
}

const MAX_WORK: usize = 16_000_000;

fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("authored window frame evidence budget exceeded")?;
    Ok(())
}

use crate::layers::named_exterior_wall as exterior;

#[derive(Clone, Copy)]
struct Line<'a> {
    ends: [Point; 2],
    source: &'a str,
    pen: bool,
}

impl<'a> Line<'a> {
    fn source(p: &'a Primitive) -> Option<Self> {
        let Shape::Line { start, end } = p.shape else {
            return None;
        };
        (start.finite() && end.finite() && start != end).then_some(Self {
            ends: [start, end],
            source: &p.id,
            pen: p.stroke_width.is_some(),
        })
    }

    fn local(self, axis: Point) -> Self {
        let [a, b] = self.ends.map(|p| Point::new(p.dot(axis), axis.cross(p)));
        let ends = if a.x <= b.x { [a, b] } else { [b, a] };
        Self { ends, ..self }
    }
}

#[derive(Clone)]
struct Casing<'a> {
    axis: Point,
    cross: [f64; 2],
    ends: [f64; 2],
    eps: f64,
    layer: &'a str,
    rails: [&'a str; 2],
    glazing: [&'a str; 5],
    caps: Vec<&'a str>,
}

impl Casing<'_> {
    fn at(&self, p: Point) -> Point {
        Point::new(p.dot(self.axis), self.axis.cross(p))
    }

    fn width(&self) -> f64 {
        self.cross[1] - self.cross[0]
    }

    fn corners(&self) -> [Point; 4] {
        let [a, b] = self.ends;
        let [lo, hi] = self.cross;
        let normal = Point::new(-self.axis.y, self.axis.x);
        [(a, lo), (a, hi), (b, hi), (b, lo)].map(|(x, y)| self.axis * x + normal * y)
    }

    fn extent(&self, points: impl Iterator<Item = Point>, margin: f64) -> bool {
        let mut lo = Point::new(f64::INFINITY, f64::INFINITY);
        let mut hi = Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY);
        for p in points.map(|p| self.at(p)) {
            lo = Point::new(lo.x.min(p.x), lo.y.min(p.y));
            hi = Point::new(hi.x.max(p.x), hi.y.max(p.y));
        }
        hi.x + margin > self.ends[0] + self.eps
            && lo.x - margin < self.ends[1] - self.eps
            && hi.y + margin > self.cross[0] + self.eps
            && lo.y - margin < self.cross[1] - self.eps
    }

    fn contains(&self, ends: [Point; 2]) -> bool {
        let [a, b] = ends.map(|p| self.at(p));
        (a.y - b.y).abs() <= self.eps && self.encloses([a, b])
    }

    fn encloses(&self, local: [Point; 2]) -> bool {
        local.into_iter().all(|p| {
            p.x >= self.ends[0] - self.eps
                && p.x <= self.ends[1] + self.eps
                && p.y >= self.cross[0] - self.eps
                && p.y <= self.cross[1] + self.eps
        })
    }

    fn conflicts(&self, opening: &Opening) -> bool {
        // Test the actual finite throat and closed leaf, not the swing square.
        // Equal oriented intervals on a nearby finished layer are competing too.
        self.extent([opening.start, opening.end].into_iter(), self.eps)
            || (opening.evidence == OpeningEvidence::ExteriorFrameGap
                && self.extent([opening.start, opening.end].into_iter(), self.width() * 0.2))
            || opening
                .swing_observations
                .iter()
                .any(|s| self.extent([s.hinge, s.closed_tip].into_iter(), s.leaf_tolerance))
    }
}

fn cap<'a>(
    a: Point,
    b: Point,
    ink: &[Line<'a>],
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<&'a str>>, String> {
    charge(work, ink.len())?;
    let reach = (a.y - b.y).abs() * 0.3;
    let lo = Point::new(a.x.min(b.x) - reach, a.y.min(b.y) - eps);
    let hi = Point::new(a.x.max(b.x) + reach, a.y.max(b.y) + eps);
    let nearby = ink
        .iter()
        .copied()
        .filter(|l| {
            let [p, q] = l.ends;
            p.x >= lo.x
                && q.x <= hi.x
                && p.y.min(q.y) >= lo.y
                && p.y.max(q.y) <= hi.y
                && (p.x - q.x).abs().min((p.y - q.y).abs()) <= eps
        })
        .collect::<Vec<_>>();
    if nearby.len() > 128 {
        return Ok(None);
    }
    let distance = |p, l: Line<'_>| on_segment(p, l.ends[0], l.ends[1]).0.distance(p);
    let mut prior = vec![None; nearby.len()];
    let mut queue = VecDeque::new();
    for (i, &l) in nearby.iter().enumerate() {
        if distance(a, l) <= eps {
            queue.push_back(i);
            prior[i] = Some(i);
        }
    }
    while let Some(i) = queue.pop_front() {
        let l = nearby[i];
        if distance(b, l) <= eps {
            let mut path = vec![l.source];
            let mut j = i;
            while let Some(p) = prior[j].filter(|&p| p != j) {
                path.push(nearby[p].source);
                j = p;
            }
            return Ok(Some(path));
        }
        charge(work, nearby.len())?;
        for (j, &r) in nearby.iter().enumerate() {
            if prior[j].is_none()
                && (l.ends.iter().any(|&p| distance(p, r) <= eps)
                    || r.ends.iter().any(|&p| distance(p, l) <= eps))
            {
                queue.push_back(j);
                prior[j] = Some(i);
            }
        }
    }
    Ok(None)
}

fn glazing<'a>(
    ink: &[Line<'a>],
    ends: [f64; 2],
    cross: [f64; 2],
    eps: f64,
    work: &mut usize,
) -> Result<Option<[&'a str; 5]>, String> {
    let length = ends[1] - ends[0];
    let width = cross[1] - cross[0];
    charge(work, ink.len())?;
    let rails = ink
        .iter()
        .copied()
        .filter(|l| {
            let [p, q] = l.ends;
            // Unpainted observations may persist after later PDF paint.
            // All five glazing strokes need retained visible pen authority.
            l.pen
                && p.x >= ends[0] + eps
                && q.x <= ends[1] - eps
                && q.x - p.x >= length * 0.45
                && p.y.min(q.y) > cross[0] + eps
                && p.y.max(q.y) < cross[1] - eps
                && (p.y - q.y).abs() <= eps
        })
        .collect::<Vec<_>>();
    for (i, middle) in rails.iter().enumerate() {
        let [p, q] = middle.ends;
        let mid = p.y.midpoint(q.y);
        let same_ends =
            |l: &Line<'_>| (l.ends[0].x - p.x).abs() <= eps && (l.ends[1].x - q.x).abs() <= eps;
        charge(work, rails.len())?;
        for low in rails
            .iter()
            .filter(|l| l.ends[0].y < mid - eps && same_ends(l))
        {
            let y0 = low.ends[0].y.midpoint(low.ends[1].y);
            let depth = (mid - y0) * 2.;
            if depth < width * 0.10 || depth > width * 0.70 {
                continue;
            }
            charge(work, rails.len().saturating_add(ink.len()))?;
            let Some(high) = rails.iter().find(|l| {
                same_ends(l)
                    && (l.ends[0].y - y0 - depth).abs() <= eps
                    && l.source != rails[i].source
                    && l.source != low.source
            }) else {
                continue;
            };
            let y1 = high.ends[0].y.midpoint(high.ends[1].y);
            let tick = |x: f64| {
                ink.iter().find(|l| {
                    let [a, b] = l.ends;
                    l.pen
                        && (a.x - x).abs().max((b.x - x).abs()) <= eps
                        && (a.y.min(b.y) - y0).abs() <= eps
                        && (a.y.max(b.y) - y1).abs() <= eps
                })
            };
            if let (Some(a), Some(b)) = (tick(p.x), tick(q.x)) {
                return Ok(Some([
                    low.source,
                    middle.source,
                    high.source,
                    a.source,
                    b.source,
                ]));
            }
        }
    }
    Ok(None)
}

fn casings<'a>(
    ink: &[Line<'a>],
    layer: &'a str,
    work: &mut usize,
) -> Result<Vec<Casing<'a>>, String> {
    // Angle bins bound the number of projections for rotated vector sheets.
    // Every final comparison uses measured offsets, endpoints, and parallel ink.
    let mut axes = BTreeMap::<i64, Point>::new();
    for rail in ink {
        let [a, b] = rail.ends;
        let mut axis = (b - a).unit();
        if axis.x.abs() >= axis.y.abs() && axis.x < 0. || axis.x.abs() < axis.y.abs() && axis.y < 0.
        {
            axis = axis * (-1.);
        }
        if a.distance(b) >= 2. * Options::default().min_opening_gap {
            axes.entry((axis.y.atan2(axis.x) * 100_000.).round() as i64)
                .or_insert(axis);
        }
    }
    let opts = Options::default();
    let max = opts.max_wall_pair_separation;
    let min = 2. * opts.min_wall_pair_separation;
    let mut result = vec![];
    for axis in axes.into_values() {
        charge(work, ink.len().saturating_mul(2))?;
        let mut local = ink.iter().map(|l| l.local(axis)).collect::<Vec<_>>();
        local.sort_by(|a, b| {
            a.ends[0]
                .x
                .total_cmp(&b.ends[0].x)
                .then(a.source.cmp(b.source))
        });
        for (i, a) in local.iter().enumerate() {
            let [p, q] = a.ends;
            if q.x - p.x < opts.min_opening_gap || (p.y - q.y).abs() > max * 0.005 {
                continue;
            }
            let upper = local.partition_point(|l| l.ends[0].x <= p.x + max * 0.25);
            charge(work, upper.saturating_sub(i + 1))?;
            for b in &local[i + 1..upper] {
                let [s, t] = b.ends;
                let width = (s.y - p.y).abs();
                let eps = width * 0.005;
                let trim = s.x - p.x;
                let ends = [p.x.max(s.x), q.x.min(t.x)];
                let length = ends[1] - ends[0];
                if !(min..=max).contains(&width)
                    || length < width * 1.8
                    || length > width * 26.
                    || trim > width * 0.24
                    || (trim - (q.x - t.x)).abs() > eps
                    || (p.y - q.y).abs() > eps
                    || (s.y - t.y).abs() > eps
                    || !(a.pen || b.pen)
                    || a.source.split(":subpath:").next() == b.source.split(":subpath:").next()
                {
                    continue;
                }
                let cross = [p.y.min(s.y), p.y.max(s.y)];
                let left = local.partition_point(|l| l.ends[0].x < ends[0] - width * 0.6);
                let right = local.partition_point(|l| l.ends[0].x <= ends[1] + width * 0.6);
                let nearby = &local[left..right];
                let Some(glazing) = glazing(nearby, ends, cross, eps, work)? else {
                    continue;
                };
                let Some(mut caps) = cap(p, s, nearby, eps, work)? else {
                    continue;
                };
                let Some(other) = cap(q, t, nearby, eps, work)? else {
                    continue;
                };
                caps.extend(other);
                if result.iter().any(|c: &Casing<'_>| {
                    c.axis.cross(axis).abs() <= 1e-5
                        && c.ends
                            .into_iter()
                            .zip(ends)
                            .all(|(a, b)| (a - b).abs() <= eps)
                        && c.cross
                            .into_iter()
                            .zip(cross)
                            .all(|(a, b)| (a - b).abs() <= eps)
                }) {
                    continue;
                }
                result.push(Casing {
                    axis,
                    cross,
                    ends,
                    eps,
                    layer,
                    rails: [a.source, b.source],
                    glazing,
                    caps,
                });
            }
        }
    }
    Ok(result)
}

fn independent_of_door(c: &Casing<'_>, page: &PageScan, work: &mut usize) -> Result<bool, String> {
    // Check native analytic arcs even if an earlier door detector lacked a host.
    let length = c.ends[1] - c.ends[0];
    for p in &page.primitives {
        let Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } = p.shape
        else {
            continue;
        };
        charge(work, 1)?;
        if !(length * 0.32..=length * 1.15).contains(&radius) {
            continue;
        }
        let hinge = c.at(center);
        if hinge.y < c.cross[0] - c.eps
            || hinge.y > c.cross[1] + c.eps
            || c.ends.iter().all(|x| (x - hinge.x).abs() > c.width() * 0.6)
        {
            continue;
        }
        let tips = [start_angle, start_angle + sweep]
            .map(|a| c.at(center + Point::new(a.cos(), a.sin()) * radius));
        if (tips[0].y - hinge.y).abs().min((tips[1].y - hinge.y).abs()) <= c.width() * 0.3
            && (tips[0].y - hinge.y).abs().max((tips[1].y - hinge.y).abs()) > c.width()
            && tips.iter().any(|t| {
                (t.y - hinge.y).abs() <= c.width() * 0.3
                    && t.x > c.ends[0] + c.eps
                    && t.x < c.ends[1] - c.eps
            })
        {
            return Ok(false);
        }
    }
    Ok(true)
}

pub(super) fn proposals(
    page: &PageScan,
    already_owned: &[bool],
    completed_frames: &[Opening],
) -> Result<Vec<AuthoredFrame>, String> {
    if already_owned.len() != page.openings.len() {
        return Err("incomplete authored window ownership inventory".into());
    }
    let Some(decisions) = &page.placement_decisions else {
        return Ok(vec![]);
    };
    let mut work = MAX_WORK;
    charge(&mut work, page.primitives.len().saturating_mul(2))?;
    let mut by_layer = BTreeMap::<&str, Vec<Line<'_>>>::new();
    let mut source = HashMap::new();
    for p in &page.primitives {
        if let Some(layer) = exterior(p)
            && let Some(line) = Line::source(p)
        {
            by_layer.entry(layer).or_default().push(line);
            source.insert(p.id.as_str(), (layer, line));
        }
    }
    let mut frames = vec![];
    for (layer, lines) in by_layer {
        frames.extend(casings(&lines, layer, &mut work)?);
    }
    if frames.is_empty() {
        return Ok(vec![]);
    }
    let status: HashMap<_, _> = decisions.iter().map(|d| (d.wall_id.as_str(), d)).collect();
    let mut referenced = HashSet::new();
    for row in page.edges.iter().map(|e| &e.opening_ids).chain(
        decisions
            .iter()
            .filter(|d| d.status == WallStatus::Ready)
            .map(|d| &d.opening_ids),
    ) {
        charge(&mut work, row.len().saturating_add(1))?;
        referenced.extend(row.iter().map(String::as_str));
    }
    referenced.extend(page.room_connections.iter().map(|c| c.opening_id.as_str()));
    let ready = page
        .walls
        .iter()
        .filter_map(|w| {
            let d = status.get(w.id.as_str()).copied()?;
            (d.status == WallStatus::Ready).then(|| effective_wall(w, Some(d)))
        })
        .collect::<Vec<_>>();
    let paint = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let paints = page
        .primitives
        .iter()
        .filter_map(|p| {
            if !paint.contains(p.id.as_str())
                || !(p.layer_kind() == LayerKind::Wall
                    || p.layer_category_override.is_none()
                        && p.layer.as_deref().is_some_and(crate::layers::is_named_wall))
            {
                return None;
            }
            if let Shape::Polyline {
                points,
                filled: true,
                closed: true,
            } = &p.shape
            {
                Some(points.as_slice())
            } else {
                None
            }
        })
        .collect::<Vec<_>>();
    let mut result = vec![];
    'frames: for c in frames {
        charge(
            &mut work,
            page.openings
                .len()
                .saturating_add(completed_frames.len())
                .saturating_add(ready.len())
                .saturating_add(page.walls.len()),
        )?;
        if completed_frames.iter().any(|o| c.conflicts(o)) {
            continue;
        }
        let mut members = vec![];
        let mut member_sources = BTreeSet::new();
        for (i, o) in page.openings.iter().enumerate() {
            if !c.conflicts(o) {
                continue;
            }
            if already_owned[i]
                || o.kind != OpeningKind::Window
                || o.evidence != OpeningEvidence::FiniteWindowAssembly
                || referenced.contains(o.id.as_str())
                || o.host_wall_ids.is_empty()
                || o.host_wall_ids.iter().any(|host| {
                    !status
                        .get(host.as_str())
                        .is_some_and(|d| d.status != WallStatus::Ready)
                })
                || !c.contains([o.start, o.end])
                || !o.source_ids.iter().all(|id| {
                    source.get(id.as_str()).is_some_and(|(layer, line)| {
                        *layer == c.layer && c.encloses(line.ends.map(|point| c.at(point)))
                    })
                })
            {
                continue 'frames;
            }
            let [a, b] = [o.start, o.end].map(|p| c.at(p).x);
            // There is nothing to improve if the existing unit already measures the full casing.
            if (a.min(b) - c.ends[0])
                .abs()
                .max((a.max(b) - c.ends[1]).abs())
                <= c.eps
            {
                continue 'frames;
            }
            members.push(i);
            member_sources.extend(o.source_ids.iter().map(String::as_str));
        }
        if !independent_of_door(&c, page, &mut work)? {
            continue;
        }
        for paint in &paints {
            charge(&mut work, paint.len())?;
            if c.extent(paint.iter().copied(), 0.) {
                continue 'frames;
            }
        }
        for w in &ready {
            if let Some(arc) = w.curve {
                if c.extent(std::iter::once(arc.center), arc.radius + w.thickness * 0.5) {
                    continue 'frames;
                }
            } else {
                let u = (w.end - w.start).unit();
                let n = Point::new(-u.y, u.x) * (w.thickness * 0.5);
                if c.extent(
                    [w.start - n, w.start + n, w.end - n, w.end + n].into_iter(),
                    0.,
                ) {
                    continue 'frames;
                }
            }
        }
        let mut host = None;
        for w in page.walls.iter().filter(|w| {
            w.curve.is_none()
                && w.body == crate::model::BodyEvidence::ParallelFaces
                && status
                    .get(w.id.as_str())
                    .is_some_and(|d| d.status != WallStatus::Ready)
        }) {
            let [a, b] = [w.start, w.end].map(|p| c.at(p));
            if (a.y - b.y).abs() > c.eps
                || a.x.min(b.x) > c.ends[0] + c.eps
                || a.x.max(b.x) < c.ends[1] - c.eps
                || a.y < c.cross[0] - c.eps
                || a.y > c.cross[1] + c.eps
            {
                continue;
            }
            charge(&mut work, w.source_ids.len().saturating_add(members.len()))?;
            let shared_outer = w.source_ids.iter().any(|id| c.rails.contains(&id.as_str()));
            let known_glazing = members.iter().any(|&i| {
                page.openings[i].host_wall_id == w.id
                    && page.openings[i]
                        .source_ids
                        .iter()
                        .any(|id| c.glazing.contains(&id.as_str()))
            });
            if !shared_outer && !known_glazing {
                continue;
            }
            if host.replace(w).is_some() {
                continue 'frames;
            }
        }
        let Some(host) = host else { continue };
        let cross = c.cross[0].midpoint(c.cross[1]);
        let point = |x| c.axis * x + Point::new(-c.axis.y, c.axis.x) * cross;
        let proof = member_sources
            .into_iter()
            .chain(c.rails)
            .chain(c.glazing)
            .chain(c.caps.iter().copied())
            .collect::<BTreeSet<_>>()
            .into_iter()
            .map(str::to_owned)
            .collect();
        let frame = AuthoredFrame {
            members,
            opening: Opening {
                id: String::new(),
                page: page.page_number,
                kind: OpeningKind::Window,
                operation: "Unknown".into(),
                host_wall_id: host.id.clone(),
                host_wall_ids: vec![host.id.clone()],
                start: point(c.ends[0]),
                end: point(c.ends[1]),
                hinge: None,
                swing_observations: vec![],
                source_ids: proof,
                evidence: OpeningEvidence::AuthoredWindowFrame,
                requires_review: true,
                physical_jambs: None,
            },
        };
        result.push((c, frame));
    }
    disjoint(result, &mut work)
}

fn disjoint(
    frames: Vec<(Casing<'_>, AuthoredFrame)>,
    work: &mut usize,
) -> Result<Vec<AuthoredFrame>, String> {
    charge(work, frames.len().saturating_pow(2))?;
    let mut disputed = vec![false; frames.len()];
    for (i, (a_casing, a)) in frames.iter().enumerate() {
        for (j, (b_casing, b)) in frames.iter().enumerate().skip(i + 1) {
            // Overlapping parallel casing bodies compete even when the two
            // measurements put their centerlines on different depth lanes.
            let same_aperture = a_casing.axis.cross(b_casing.axis).abs() <= 1e-5
                && a_casing.extent(b_casing.corners().into_iter(), 0.)
                && b_casing.extent(a_casing.corners().into_iter(), 0.);
            if a.members.iter().any(|m| b.members.contains(m)) || same_aperture {
                disputed[i] = true;
                disputed[j] = true;
            }
        }
    }
    Ok(frames
        .into_iter()
        .zip(disputed)
        .filter_map(|((_, f), blocked)| (!blocked).then_some(f))
        .collect())
}
