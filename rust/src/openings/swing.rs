use super::*;

#[derive(Clone, Copy)]
struct Stroke<'a> {
    a: Point,
    b: Point,
    id: &'a str,
}
/// Index finite strokes which could depict a leaf in its open or closed position.
pub(super) struct LeafStrokes<'a> {
    strokes: Vec<Stroke<'a>>,
    visits: usize,
}

#[derive(Default)]
pub(super) struct Fit {
    pub ids: Vec<String>,
    pub error: f64,
}
impl<'a> LeafStrokes<'a> {
    pub fn new(page: &'a Page, o: &Options) -> Self {
        Self::from_primitives(page.primitives.iter(), o.max_opening_gap + o.snap_tolerance)
    }
    fn from_primitives(primitives: impl Iterator<Item = &'a Primitive>, max_length: f64) -> Self {
        let mut strokes = Vec::new();
        for p in primitives {
            if !matches!(
                p.layer_kind(),
                LayerKind::Door | LayerKind::Unknown | LayerKind::Wall
            ) || matches!(p.shape, Shape::Polyline { filled: true, .. })
            {
                continue;
            }
            for (a, b) in segments(&p.shape) {
                if a.distance(b) > 1e-6 && a.distance(b) <= max_length {
                    strokes.push(Stroke { a, b, id: &p.id });
                }
            }
        }
        strokes.sort_by(|a, b| a.a.x.min(a.b.x).total_cmp(&b.a.x.min(b.b.x)));
        Self { strokes, visits: 0 }
    }
    pub(super) fn validates(
        a: Point,
        b: Point,
        eps: f64,
        ids: &[String],
        sources: &std::collections::HashMap<&str, &'a Primitive>,
    ) -> bool {
        if ids.is_empty() {
            return true;
        }
        let primitives = ids
            .iter()
            .filter_map(|id| sources.get(id.as_str()).copied());
        let mut index = Self::from_primitives(primitives, a.distance(b) + 3. * eps);
        index.fit(a, b, eps).is_ok_and(|fit| fit.ids == ids)
    }

    /// Chase connected finite paint from pivot to tip. Isolated or overlong crossing lines
    /// do not witness a leaf. Adjacent trim and panel strokes can jointly depict one leaf.
    pub fn fit(&mut self, a: Point, b: Point, eps: f64) -> Result<Fit, String> {
        let d = (b - a).unit();
        let length = a.distance(b);
        let lo = self
            .strokes
            .partition_point(|s| s.a.x.min(s.b.x) < a.x.min(b.x) - eps);
        let mut pieces = Vec::new();
        for s in &self.strokes[lo..] {
            if s.a.x.min(s.b.x) > a.x.max(b.x) + eps {
                break;
            }
            self.visits += 1;
            if self.visits > 40_000_000 {
                return Err("swing leaf search budget exceeded".into());
            }
            if (s.b - s.a).unit().dot(d).abs() < 0.995 {
                continue;
            }
            let error = (s.a - a).cross(d).abs().max((s.b - a).cross(d).abs());
            let x = (s.a - a).dot(d);
            let y = (s.b - a).dot(d);
            let (lo, hi) = (x.min(y), x.max(y));
            if error <= eps && lo >= -eps && hi <= length + eps {
                pieces.push((lo, hi, error, s.id));
            }
        }
        pieces.sort_by(|x, y| {
            x.0.total_cmp(&y.0)
                .then(x.2.total_cmp(&y.2))
                .then(x.3.cmp(y.3))
        });
        let mut step = 0;
        let mut reached = 0.;
        let mut proof = Fit::default();
        while reached < length - eps {
            let mut best = None;
            while step < pieces.len() && pieces[step].0 <= reached + eps {
                let p = pieces[step];
                step += 1;
                if p.1 > reached
                    && best.is_none_or(|q: (f64, f64, f64, &str)| {
                        p.1 > q.1 || (p.1 == q.1 && p.2 < q.2)
                    })
                {
                    best = Some(p)
                }
            }
            let Some((lo, hi, error, id)) = best else {
                return Ok(Fit::default());
            };
            let gap = if proof.ids.is_empty() {
                lo.abs()
            } else {
                (lo - reached).max(0.)
            };
            proof.error = proof.error.max(error).max(gap);
            proof.ids.push(id.to_owned());
            reached = hi;
        }
        proof.error = proof.error.max((reached - length).abs());
        proof.ids.sort();
        proof.ids.dedup();
        Ok(proof)
    }
}

pub(super) fn same_jamb(walls: &[&Wall], p: Point, eps: f64) -> bool {
    let Some(first) = walls.first() else {
        return false;
    };
    let a = projected(first, p);
    let axis = (first.end - first.start).unit();
    walls.iter().skip(1).all(|w| {
        first.thickness > 0.
            && w.thickness > 0.
            && (w.thickness - first.thickness).abs() <= eps
            && a.distance(projected(w, p)) <= eps
            && axis.cross((w.end - w.start).unit()).abs() <= 0.01
    })
}

/// A complete opening may include the thin closed leaf drawn within the host footprint.
/// Limit secondary cuts to pieces fully enclosed by both the host faces and the aperture.
pub(super) fn inside_aperture(w: &Wall, host: &Wall, a: Point, b: Point, eps: f64) -> bool {
    if w.id == host.id || w.curve.is_some() || w.thickness <= 0. || w.thickness >= host.thickness {
        return false;
    }
    let d = (b - a).unit();
    let n = a.distance(b);
    if !same_axis(w, d) {
        return false;
    }
    let contained = [w.start, w.end].iter().all(|&p| {
        let off = (p - a).cross(d).abs() + w.thickness * 0.5;
        let along = (p - a).dot(d);
        off <= host.thickness * 0.5 + eps * 0.25 && along >= -eps && along <= n + eps
    });
    // A small object lying in a door is insufficient. Both ends must coincide with its jambs.
    let dx = (w.start - a).dot(d);
    let dy = (w.end - a).dot(d);
    contained && dx.min(dy).abs() <= eps && (dx.max(dy) - n).abs() <= eps
}

pub(super) struct Proposal {
    pub door: Opening,
    pub fit_error: f64,
}
pub(super) fn same_flank(flank: &Wall, through: &Wall, a: Point, b: Point, eps: f64) -> bool {
    if through.thickness <= 0.
        || flank.thickness <= 0.
        || (through.thickness - flank.thickness).abs() > eps
    {
        return false;
    }
    if !same_axis(flank, (through.end - through.start).unit()) {
        return false;
    }
    let end = [flank.start, flank.end]
        .into_iter()
        .min_by(|u, v| {
            u.distance(a)
                .min(u.distance(b))
                .total_cmp(&v.distance(a).min(v.distance(b)))
        })
        .unwrap();
    end.distance(projected(through, end)) <= eps
}
/// Match a compact painted jamb block immediately *outside* one endpoint of
/// the closed radial. Two oppositely facing blocks can bound a glazed gap
/// even when the drawing shows the leaf only in its open position.
pub(super) fn terminal_block(
    contacts: &BTreeSet<String>,
    sources: &std::collections::HashMap<&str, &Shape>,
    endpoint: Point,
    outward: Point,
    thickness: f64,
    eps: f64,
) -> bool {
    contacts.iter().any(|id| {
        let Some(Shape::Polyline {
            points,
            closed: true,
            filled: true,
        }) = sources.get(id.as_str()).copied()
        else {
            return false;
        };
        let corners = if points.len() == 5 && points[0].distance(points[4]) < 1e-6 {
            &points[..4]
        } else {
            points.as_slice()
        };
        if corners.len() != 4 {
            return false;
        }
        let mut lo = f64::INFINITY;
        let mut hi = f64::NEG_INFINITY;
        let mut left = f64::INFINITY;
        let mut right = f64::NEG_INFINITY;
        let mut edges = [false; 4];
        for (i, &p) in corners.iter().enumerate() {
            let v = corners[(i + 1) % 4] - p;
            if v.norm() < 1e-6 {
                return false;
            }
            let cos = v.unit().dot(outward).abs();
            if cos > 0.995 {
                edges[i] = true
            } else if cos > 0.1 {
                return false;
            }
            let along = (p - endpoint).dot(outward);
            let cross = (p - endpoint).cross(outward);
            lo = lo.min(along);
            hi = hi.max(along);
            left = left.min(cross);
            right = right.max(cross);
        }
        let depth = hi - lo;
        let width = right - left;
        let near = eps.min(depth.min(width) * 0.25);
        depth > 1e-6
            && width > 1e-6
            && depth <= thickness + eps
            && width <= thickness + eps
            && (0..4).all(|i| edges[i] != edges[(i + 1) % 4])
            && lo.abs() <= near
            && left <= near
            && right >= -near
    })
}
/// A second face pair can be drawn exactly from jamb to jamb (often a frame or
/// a superimposed drafting line). Both finite cap strokes must already have
/// been witnessed on the continuing wall. Its axis must lie inside the other
/// observation at both jambs, and its body must continue beyond both jambs.
pub(super) fn bounded_frame_overlay(w: &Wall, host: &Wall, a: Point, b: Point, eps: f64) -> bool {
    if w.id == host.id || w.body != BodyEvidence::ParallelFaces || w.thickness <= 0. {
        return false;
    }
    let d = (b - a).unit();
    let n = a.distance(b);
    if !same_axis(w, d) {
        return false;
    }
    let a1 = (host.start - a).dot(d);
    let b1 = (host.end - a).dot(d);
    let a2 = (w.start - a).dot(d);
    let b2 = (w.end - a).dot(d);
    a1.min(b1) < -eps
        && a1.max(b1) > n + eps
        && a2.min(b2).abs() <= eps
        && (a2.max(b2) - n).abs() <= eps
        && [a, b]
            .into_iter()
            .all(|p| (projected(w, p) - p).cross(d).abs() <= w.thickness * 0.5 + eps * 0.25)
}
/// The accepted cut also belongs to a second rendering of the same wall
/// where both measured faces coincide across the *full finite aperture*.
pub(super) fn coincident_body(w: &Wall, host: &Wall, a: Point, b: Point, snap: f64) -> bool {
    if w.body != BodyEvidence::ParallelFaces
        || host.body != BodyEvidence::ParallelFaces
        || w.thickness <= 0.
        || host.thickness <= 0.
    {
        return false;
    }
    let d = (b - a).unit();
    if !same_axis(w, d) {
        return false;
    }
    let eps = (snap * 0.5).min(w.thickness.min(host.thickness) * 0.1);
    let half_width_delta = (w.thickness - host.thickness).abs() * 0.5;
    [a, b].into_iter().all(|p| {
        let delta = projected(w, p) - p;
        delta.dot(d).abs() <= eps && delta.cross(d).abs() + half_width_delta <= eps
    })
}
/// The same stroke must witness the drawn open leaf and a parallel face of this finite panel.
/// Continuing wall faces, separate jambs and independent parallel ink do not qualify.
pub(super) fn leaf_panel(w: &Wall, s: &SwingObservation, eps: f64) -> bool {
    if w.body != BodyEvidence::ParallelFaces
        || w.thickness <= 0.
        || s.open_leaf_source_ids.is_empty()
    {
        return false;
    }
    let a = s.hinge;
    let b = s.open_tip;
    let d = (b - a).unit();
    let x = (w.start - a).dot(d);
    let y = (w.end - a).dot(d);
    same_axis(w, d)
        && x.min(y).abs() <= eps
        && (x.max(y) - a.distance(b)).abs() <= eps
        && [w.start, w.end]
            .into_iter()
            .all(|p| ((p - a).cross(d).abs() - w.thickness * 0.5).abs() <= eps * 0.25)
        && w.source_ids
            .iter()
            .any(|id| s.open_leaf_source_ids.contains(id))
}
pub fn door_leaf_walls(
    page: &Page,
    walls: &[Wall],
    openings: &[Opening],
    o: &Options,
) -> Result<std::collections::BTreeMap<String, Vec<String>>, String> {
    use std::collections::{BTreeMap, HashMap, HashSet};
    let hosts: HashSet<_> = openings
        .iter()
        .flat_map(|p| p.host_wall_ids.iter().map(String::as_str))
        .collect();
    let mut by_source = HashMap::<&str, Vec<&Wall>>::new();
    let explicit = explicit_wall_sources(page);
    for w in walls.iter().filter(|w| {
        !hosts.contains(w.id.as_str())
            && w.body == BodyEvidence::ParallelFaces
            && !w.source_ids.iter().any(|id| explicit.contains(id.as_str()))
    }) {
        for id in &w.source_ids {
            by_source.entry(id.as_str()).or_default().push(w)
        }
    }
    let mut result = BTreeMap::<String, BTreeSet<String>>::new();
    let mut work = 0usize;
    for s in openings.iter().flat_map(|p| &p.swing_observations) {
        for source in &s.open_leaf_source_ids {
            if let Some(ws) = by_source.get(source.as_str()) {
                for &w in ws {
                    work = work.saturating_add(
                        w.source_ids
                            .len()
                            .saturating_mul(s.open_leaf_source_ids.len()),
                    );
                    if work > 12_000_000 {
                        return Err("door panel ownership budget exceeded".into());
                    }
                    if leaf_panel(w, s, o.snap_tolerance) {
                        let ids = result.entry(w.id.clone()).or_default();
                        ids.insert(s.arc_source_id.clone());
                        ids.insert(source.clone());
                    }
                }
            }
        }
    }
    Ok(result
        .into_iter()
        .map(|(id, ids)| (id, ids.into_iter().collect()))
        .collect())
}
pub(super) fn explicit_wall_sources(page: &Page) -> std::collections::HashSet<&str> {
    page.primitives
        .iter()
        .filter(|p| p.layer.is_some() && p.layer_kind() == LayerKind::Wall)
        .map(|p| p.id.as_str())
        .collect()
}
/// Opposing authored leaves at the same two jambs can depict the same aperture twice.
/// Keep both orientations and their own source pivots; cut the union actually observed.
pub(super) fn shared_apertures(doors: Vec<Proposal>, eps: f64) -> Result<Vec<Opening>, String> {
    let mut peer = vec![Vec::new(); doors.len()];
    let mut work = 0usize;
    for (i, one) in doors.iter().enumerate() {
        let a = &one.door;
        if a.swing_observations[0].open_leaf_source_ids.is_empty() {
            continue;
        }
        for (j, two) in doors.iter().enumerate().skip(i + 1) {
            work += 1;
            if work > 12_000_000 {
                return Err("authored swing comparison budget exceeded".into());
            }
            let b = &two.door;
            if !b.swing_observations[0].open_leaf_source_ids.is_empty()
                && a.host_wall_ids == b.host_wall_ids
                && a.evidence == b.evidence
                && a.start.distance(b.end) <= eps
                && a.end.distance(b.start) <= eps
                && (a.end - a.start).unit().dot((b.end - b.start).unit()) < -0.995
            {
                peer[i].push(j);
                peer[j].push(i)
            }
        }
    }
    let mut consumed = vec![false; doors.len()];
    let mut out = Vec::new();
    for (i, p) in doors.iter().enumerate() {
        if consumed[i] {
            continue;
        }
        if let [j] = peer[i].as_slice() {
            let j = *j;
            if peer[j].as_slice() == [i] {
                consumed[j] = true;
                let q = &doors[j];
                let (best, other) = if p
                    .fit_error
                    .total_cmp(&q.fit_error)
                    .then_with(|| {
                        p.door.swing_observations[0]
                            .arc_source_id
                            .cmp(&q.door.swing_observations[0].arc_source_id)
                    })
                    .is_le()
                {
                    (p, q)
                } else {
                    (q, p)
                };
                let mut door = best.door.clone();
                let alt = &other.door;
                let axis = (door.end - door.start).unit();
                if (alt.end - door.start).dot(axis) < 0. {
                    door.start = alt.end
                }
                if (alt.start - door.end).dot(axis) > 0. {
                    door.end = alt.start
                }
                door.swing_observations
                    .extend(alt.swing_observations.clone());
                door.source_ids.extend(alt.source_ids.clone());
                door.source_ids.sort();
                door.source_ids.dedup();
                out.push(door);
                continue;
            }
        }
        out.push(p.door.clone());
    }
    Ok(out)
}
