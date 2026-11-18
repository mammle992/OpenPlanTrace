//! Find host-specific apertures from finite drawing evidence. Never change an observed wall.
use crate::model::*;
use std::collections::BTreeSet;
pub mod physical_jambs;
mod physical_view;
pub(crate) use physical_view::{
    apply as refine_physical_jambs, validate as validate_physical_jambs,
};
mod swing;
#[cfg(test)]
mod tests;
pub use swing::door_leaf_walls;
/// V2 doors retain each source swing behind a physical cut. Check direct primitive
/// identity and the finite radial endpoint, including when the cut has two observed hands.
pub(crate) fn validate_swing_sources(
    o: &Opening,
    sources: &std::collections::HashMap<&str, &Primitive>,
) -> bool {
    if o.kind != OpeningKind::Door {
        return o.swing_observations.is_empty();
    }
    if o.operation != "Hinged"
        || o.swing_observations.is_empty()
        || o.hinge != Some(o.swing_observations[0].hinge)
    {
        return false;
    }
    let mut seen = BTreeSet::new();
    let owner_sources: BTreeSet<_> = o.source_ids.iter().map(String::as_str).collect();
    o.swing_observations.iter().enumerate().all(|(i, s)| {
        let Some(Primitive {
            shape:
                Shape::Arc {
                    center,
                    radius,
                    start_angle,
                    sweep,
                },
            ..
        }) = sources.get(s.arc_source_id.as_str()).copied()
        else {
            return false;
        };
        let endpoint = |a: f64| *center + Point::new(a.cos(), a.sin()) * *radius;
        let a = endpoint(*start_angle);
        let b = endpoint(*start_angle + *sweep);
        let ray = (s.closed_tip - s.hinge).unit();
        let sign = (o.end - o.start).unit().dot(ray);
        let x = (o.start - s.hinge).dot(ray);
        let y = (o.end - s.hinge).dot(ray);
        s.hinge.distance(*center) <= 1e-7
            && s.leaf_tolerance.is_finite()
            && s.leaf_tolerance > 0.
            && s.leaf_tolerance <= radius / 16. + 1e-9
            && sign.abs() >= 0.995 - 1e-9
            && (i != 0 || sign > 0.)
            && x.min(y) < radius * 0.5
            && x.max(y) > radius * 0.5
            && ((a.distance(s.closed_tip) <= 1e-7 && b.distance(s.open_tip) <= 1e-7)
                || (b.distance(s.closed_tip) <= 1e-7 && a.distance(s.open_tip) <= 1e-7))
            && seen.insert(&s.arc_source_id)
            && owner_sources.contains(s.arc_source_id.as_str())
            && s.open_leaf_source_ids
                .iter()
                .chain(&s.closed_leaf_source_ids)
                .all(|id| owner_sources.contains(id.as_str()))
            && swing::LeafStrokes::validates(
                s.hinge,
                s.open_tip,
                s.leaf_tolerance,
                &s.open_leaf_source_ids,
                sources,
            )
            && swing::LeafStrokes::validates(
                s.hinge,
                s.closed_tip,
                s.leaf_tolerance,
                &s.closed_leaf_source_ids,
                sources,
            )
    })
}

#[derive(Clone)]
struct Tick<'a> {
    a: Point,
    b: Point,
    id: &'a str,
}
#[derive(Default)]
struct Jamb {
    ordinary: BTreeSet<String>,
    ticks: Vec<usize>,
}
impl Jamb {
    /// A cap must cross from the authored leaf radius to the measured host axis at
    /// this jamb. This also admits tiny caps on thin walls where a graph snap
    /// distance would exceed the width of the drawn cap itself.
    fn contacts(
        &self,
        ticks: &[Tick<'_>],
        p: Point,
        axis: Point,
        d: Point,
        width: f64,
        snap: f64,
    ) -> BTreeSet<String> {
        let eps = snap * 0.25;
        let height = (axis - p).cross(d);
        self.ticks
            .iter()
            .filter_map(|&i| {
                let t = &ticks[i];
                let length = t.a.distance(t.b);
                let x = (t.a - p).cross(d);
                let y = (t.b - p).cross(d);
                (width > 0.
                    && length >= width * 0.5 - eps
                    && length <= width + 2. * eps
                    && (((t.a + t.b) * 0.5 - p).dot(d)).abs() <= snap
                    && x.min(y) <= height.min(0.) + eps
                    && x.max(y) >= height.max(0.) - eps)
                    .then(|| t.id.to_owned())
            })
            .collect()
    }
}
struct DoorMatch<'a> {
    primary: &'a Wall,
    hosts: Vec<String>,
    start: Point,
    end: Point,
    evidence: OpeningEvidence,
    sources: BTreeSet<String>,
    swing: SwingObservation,
    fit_error: f64,
    direction: usize,
    confirmed_caps: bool,
}
fn end_outside(w: &Wall, p: Point, d: Point, tol: f64) -> bool {
    w.curve.is_none()
        && [(w.start, w.end), (w.end, w.start)]
            .iter()
            .any(|&(near, far)| near.distance(p) <= tol && (far - near).unit().dot(d) < -0.995)
}
fn projected(w: &Wall, p: Point) -> Point {
    on_segment(p, w.start, w.end).0
}
fn same_axis(w: &Wall, d: Point) -> bool {
    w.curve.is_none() && (w.end - w.start).unit().dot(d).abs() >= 0.995
}
fn interval_overlap(a: &Opening, b: &Opening) -> bool {
    if !a.host_wall_ids.iter().any(|h| b.host_wall_ids.contains(h)) {
        return false;
    }
    let v = (a.end - a.start).unit();
    if v.dot((b.end - b.start).unit()).abs() < 0.995 {
        return false;
    }
    let x = (b.start - a.start).dot(v);
    let y = (b.end - a.start).dot(v);
    x.min(y).max(0.) + 1e-6 < x.max(y).min(a.start.distance(a.end))
}
pub fn detect(page: &Page, walls: &[Wall], o: &Options) -> Result<Vec<Opening>, String> {
    let tol = (2. * o.snap_tolerance).max(4.);
    let mut ticks = Vec::new();
    for p in &page.primitives {
        if matches!(
            p.layer_kind(),
            LayerKind::Dimension | LayerKind::Grid | LayerKind::Object | LayerKind::Annotation
        ) {
            continue;
        }
        for (a, b) in segments(&p.shape) {
            let l = a.distance(b);
            if l > 1e-7 && l <= o.max_wall_pair_separation + 2. * tol {
                ticks.push(Tick { a, b, id: &p.id });
            }
        }
    }
    ticks.sort_by(|a, b| a.a.x.min(a.b.x).total_cmp(&b.a.x.min(b.b.x)));
    let mut tick_work = 0usize;
    let mut find_jambs = |p: Point, d: Point| -> Result<Jamb, String> {
        let lo = ticks
            .partition_point(|s| s.a.x.min(s.b.x) < p.x - o.max_wall_pair_separation - 3. * tol);
        let mut jamb = Jamb::default();
        for (idx, t) in ticks[lo..].iter().enumerate() {
            if t.a.x.min(t.b.x) > p.x + tol {
                break;
            }
            tick_work += 1;
            if tick_work > 40_000_000 {
                return Err("finite jamb search budget exceeded".into());
            }
            if (t.b - t.a).unit().dot(d).abs() <= 0.1
                && on_segment(p, t.a, t.b).0.distance(p) <= tol
            {
                jamb.ticks.push(lo + idx);
                if t.a.distance(t.b) >= o.snap_tolerance {
                    jamb.ordinary.insert(t.id.to_owned());
                }
            }
        }
        Ok(jamb)
    };
    let mut door_proposals = Vec::new();
    let source_by_id: std::collections::HashMap<_, _> = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), &p.shape))
        .collect();
    let wall_by_id: std::collections::HashMap<_, _> =
        walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let explicit_wall_sources = swing::explicit_wall_sources(page);
    let mut strokes = swing::LeafStrokes::new(page, o);
    let paint_tolerance = o.snap_tolerance * 0.25;
    let mut visits = 0usize;
    for p in &page.primitives {
        let layer = p.layer_kind();
        if !matches!(
            layer,
            LayerKind::Door | LayerKind::Unknown | LayerKind::Wall | LayerKind::Window
        ) {
            continue;
        }
        let Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } = p.shape
        else {
            continue;
        };
        if radius < o.min_opening_gap
            || radius > o.max_opening_gap
            || !(45.0..=140.0).contains(&sweep.abs().to_degrees())
            || layer == LayerKind::Window
        {
            continue;
        }
        let mut matches = Vec::new();
        let mut contained_hosts = BTreeSet::new();
        let angles = [start_angle, start_angle + sweep];
        let tips = angles.map(|a| center + Point::new(a.cos(), a.sin()) * radius);
        // A loose graph snap setting must never authorize a stroke far from the authored radius.
        let leaf_tolerance = paint_tolerance.min(radius / 16.);
        let ink = [
            strokes.fit(center, tips[0], leaf_tolerance)?,
            strokes.fit(center, tips[1], leaf_tolerance)?,
        ];
        for (direction, angle) in angles.into_iter().enumerate() {
            let d = Point::new(angle.cos(), angle.sin());
            let end = tips[direction];
            let j1 = find_jambs(center, d)?;
            let j2 = find_jambs(end, d)?;
            let open_ink = &ink[1 - direction];
            let closed_ink = &ink[direction];
            let fit_error = if closed_ink.ids.is_empty() {
                f64::INFINITY
            } else {
                closed_ink.error.max(open_ink.error)
            };
            let swing = SwingObservation {
                arc_source_id: p.id.clone(),
                hinge: center,
                closed_tip: end,
                open_tip: tips[1 - direction],
                leaf_tolerance,
                open_leaf_source_ids: open_ink.ids.clone(),
                closed_leaf_source_ids: closed_ink.ids.clone(),
            };
            let mut left = Vec::new();
            let mut right = Vec::new();
            for w in walls {
                visits += 1;
                if visits > 60_000_000 {
                    return Err("opening host search budget exceeded".into());
                }
                if !same_axis(w, d) {
                    continue;
                }
                if end_outside(w, center, d, tol) {
                    left.push(w);
                }
                if end_outside(w, end, d * (-1.), tol) {
                    right.push(w);
                }
                let a = projected(w, center);
                let b = projected(w, end);
                let host_tolerance = if swing.open_leaf_source_ids.is_empty() {
                    tol
                } else {
                    tol.max(0.5 * w.thickness + paint_tolerance)
                };
                if center.distance(a) > host_tolerance
                    || end.distance(b) > host_tolerance
                    || (b - a).dot(d) < radius - tol * 0.25
                {
                    continue;
                }
                visits = visits.saturating_add(j1.ticks.len() + j2.ticks.len());
                if visits > 60_000_000 {
                    return Err("opening jamb contact budget exceeded".into());
                }
                let cap1 = j1.contacts(&ticks, center, a, d, w.thickness, o.snap_tolerance);
                let cap2 = j2.contacts(&ticks, end, b, d, w.thickness, o.snap_tolerance);
                let confirmed = !cap1.is_empty() && !cap2.is_empty() && cap1.is_disjoint(&cap2);
                let long_jambs = !j1.ordinary.is_empty()
                    && !j2.ordinary.is_empty()
                    && j1.ordinary.is_disjoint(&j2.ordinary);
                let short_jambs = confirmed
                    && !swing.open_leaf_source_ids.is_empty()
                    && (!swing.closed_leaf_source_ids.is_empty()
                        || (swing::terminal_block(
                            &cap1,
                            &source_by_id,
                            center,
                            d * (-1.),
                            w.thickness,
                            leaf_tolerance,
                        ) && swing::terminal_block(
                            &cap2,
                            &source_by_id,
                            end,
                            d,
                            w.thickness,
                            leaf_tolerance,
                        )));
                if !long_jambs && !short_jambs {
                    continue;
                }
                let mut ids = BTreeSet::from([p.id.clone()]);
                if long_jambs {
                    ids.extend(j1.ordinary.iter().chain(&j2.ordinary).cloned());
                }
                if confirmed {
                    ids.extend(cap1.into_iter().chain(cap2));
                }
                let mut hosts = vec![w.id.clone()];
                if !swing.open_leaf_source_ids.is_empty() {
                    visits += walls.len();
                    if visits > 60_000_000 {
                        return Err("opening host search budget exceeded".into());
                    }
                    hosts.extend(
                        walls
                            .iter()
                            .filter(|other| {
                                swing::inside_aperture(other, w, a, b, o.snap_tolerance)
                                    || (confirmed
                                        && !other
                                            .source_ids
                                            .iter()
                                            .any(|id| explicit_wall_sources.contains(id.as_str()))
                                        && swing::bounded_frame_overlay(
                                            other,
                                            w,
                                            a,
                                            b,
                                            o.snap_tolerance,
                                        ))
                            })
                            .map(|inner| inner.id.clone()),
                    );
                    contained_hosts.extend(hosts.iter().skip(1).map(|id| (direction, id.clone())));
                }
                matches.push(DoorMatch {
                    primary: w,
                    hosts,
                    start: a,
                    end: b,
                    evidence: OpeningEvidence::FiniteJambSwing,
                    sources: ids,
                    swing: swing.clone(),
                    fit_error,
                    direction,
                    confirmed_caps: confirmed,
                });
            }
            if swing::same_jamb(&left, center, paint_tolerance)
                && swing::same_jamb(&right, end, paint_tolerance)
                && left[0].id != right[0].id
            {
                let a = projected(left[0], center);
                let b = projected(right[0], end);
                if (b - a).unit().dot(d) > 0.995 {
                    let ids = left
                        .iter()
                        .chain(&right)
                        .flat_map(|w| w.source_ids.iter().cloned())
                        .chain([p.id.clone()])
                        .collect();
                    matches.push(DoorMatch {
                        primary: left[0],
                        hosts: left.iter().chain(&right).map(|w| w.id.clone()).collect(),
                        start: a,
                        end: b,
                        evidence: OpeningEvidence::WallGapSwing,
                        sources: ids,
                        swing,
                        fit_error,
                        direction,
                        confirmed_caps: false,
                    });
                }
            }
        }
        matches.retain(|m| !contained_hosts.contains(&(m.direction, m.primary.id.clone())));
        if let [a, _] = &matches[..] {
            // Some plans draw a long wall face over a real gap between a pair of
            // finite flanks. Their agreeing physical faces corroborate the same
            // doorway. Only the overdraw carries material across the aperture.
            let order = if a.evidence == OpeningEvidence::FiniteJambSwing {
                (0, 1)
            } else {
                (1, 0)
            };
            let (through, gap) = (&matches[order.0], &matches[order.1]);
            if through.direction == gap.direction
                && through.confirmed_caps
                && !through.swing.open_leaf_source_ids.is_empty()
                && gap.evidence == OpeningEvidence::WallGapSwing
                && through.evidence == OpeningEvidence::FiniteJambSwing
                && gap.hosts.iter().all(|id| {
                    let flank = wall_by_id[id.as_str()];
                    swing::same_flank(flank, through.primary, gap.start, gap.end, paint_tolerance)
                })
            {
                let gap = matches.remove(order.1);
                matches[0].sources.extend(gap.sources);
            }
        }
        if let [a, b] = &matches[..] {
            if a.direction != b.direction {
                let is_drawn_leaf = |candidate: &Wall, obs: &SwingObservation| {
                    !candidate
                        .source_ids
                        .iter()
                        .any(|id| explicit_wall_sources.contains(id.as_str()))
                        && swing::leaf_panel(candidate, obs, o.snap_tolerance)
                };
                let leaf_a = is_drawn_leaf(a.primary, &b.swing);
                let leaf_b = is_drawn_leaf(b.primary, &a.swing);
                if leaf_a != leaf_b {
                    matches.remove(if leaf_a { 0 } else { 1 });
                }
            }
        }
        if matches.len() == 1 {
            let DoorMatch {
                mut hosts,
                start,
                end,
                evidence,
                sources: mut ids,
                swing,
                fit_error,
                primary,
                ..
            } = matches.pop().unwrap();
            if evidence == OpeningEvidence::FiniteJambSwing
                && !swing.open_leaf_source_ids.is_empty()
            {
                visits += walls.len();
                if visits > 60_000_000 {
                    return Err("opening overlay ownership budget exceeded".into());
                }
                for extra in walls {
                    if !hosts.contains(&extra.id)
                        && !extra
                            .source_ids
                            .iter()
                            .any(|id| explicit_wall_sources.contains(id.as_str()))
                        && swing::coincident_body(extra, primary, start, end, o.snap_tolerance)
                    {
                        hosts.push(extra.id.clone());
                    }
                }
            }
            ids.extend(
                swing
                    .open_leaf_source_ids
                    .iter()
                    .chain(&swing.closed_leaf_source_ids)
                    .cloned(),
            );
            door_proposals.push(swing::Proposal {
                fit_error,
                door: Opening {
                    physical_jambs: None,
                    id: String::new(),
                    page: page.number,
                    kind: OpeningKind::Door,
                    operation: "Hinged".into(),
                    host_wall_id: hosts[0].clone(),
                    host_wall_ids: hosts,
                    start,
                    end,
                    hinge: Some(center),
                    swing_observations: vec![swing],
                    source_ids: ids.into_iter().collect(),
                    evidence,
                    requires_review: true,
                },
            });
        }
    }
    let mut proposals = swing::shared_apertures(door_proposals, o.snap_tolerance)?;
    // An explicit CAD window stroke must project onto one finite and parallel host.
    for p in &page.primitives {
        if p.layer_kind() != LayerKind::Window {
            continue;
        }
        let mut lines = segments(&p.shape);
        lines.sort_by(|a, b| b.0.distance(b.1).total_cmp(&a.0.distance(a.1)));
        let Some((a, b)) = lines.first().copied() else {
            continue;
        };
        let d = (b - a).unit();
        if a.distance(b) < o.min_opening_gap {
            continue;
        }
        let mut hosts = Vec::new();
        for w in walls {
            visits += 1;
            if visits > 60_000_000 {
                return Err("opening host search budget exceeded".into());
            }
            if same_axis(w, d)
                && projected(w, a).distance(a) <= tol
                && projected(w, b).distance(b) <= tol
            {
                hosts.push(w)
            }
        }
        if hosts.len() == 1 {
            let w = hosts[0];
            proposals.push(Opening {
                physical_jambs: None,
                id: String::new(),
                page: page.number,
                kind: OpeningKind::Window,
                operation: "Unknown".into(),
                host_wall_id: w.id.clone(),
                host_wall_ids: vec![w.id.clone()],
                start: projected(w, a),
                end: projected(w, b),
                hinge: None,
                swing_observations: Vec::new(),
                source_ids: vec![p.id.clone()],
                evidence: OpeningEvidence::ExplicitWindowFrame,
                requires_review: true,
            });
        }
    }
    let inferred = crate::windows::assemblies(page, walls, &proposals, o)?;
    let mut conflict_work = 0usize;
    for w in inferred {
        conflict_work = conflict_work.saturating_add(proposals.len());
        if conflict_work > 12_000_000 {
            return Err("opening conflict budget exceeded".into());
        }
        if !proposals
            .iter()
            .any(|p| p.kind == OpeningKind::Door && interval_overlap(&w, p))
        {
            proposals.push(w)
        }
    }
    // Collapse coincident observations; genuinely competing finite intervals are ambiguous.
    let mut unique: Vec<Opening> = Vec::new();
    for p in proposals {
        conflict_work = conflict_work.saturating_add(unique.len());
        if conflict_work > 12_000_000 {
            return Err("opening conflict budget exceeded".into());
        }
        if let Some(q) = unique.iter_mut().find(|q| {
            q.kind == p.kind
                && q.host_wall_ids == p.host_wall_ids
                && ((q.start.distance(p.start) < 1e-5 && q.end.distance(p.end) < 1e-5)
                    || (q.start.distance(p.end) < 1e-5 && q.end.distance(p.start) < 1e-5))
                && q.hinge == p.hinge
        }) {
            for swing in p.swing_observations {
                if !q
                    .swing_observations
                    .iter()
                    .any(|s| s.arc_source_id == swing.arc_source_id)
                {
                    q.swing_observations.push(swing);
                }
            }
            q.source_ids.extend(p.source_ids);
            q.source_ids.sort();
            q.source_ids.dedup()
        } else {
            unique.push(p)
        }
    }
    let mut blocked = vec![false; unique.len()];
    for i in 0..unique.len() {
        for j in i + 1..unique.len() {
            conflict_work += 1;
            if conflict_work > 12_000_000 {
                return Err("opening conflict budget exceeded".into());
            }
            let (a, b) = (&unique[i], &unique[j]);
            let shared_window = a.evidence == OpeningEvidence::FiniteWindowAssembly
                && b.evidence == OpeningEvidence::FiniteWindowAssembly
                && a.host_wall_ids != b.host_wall_ids
                && a.source_ids
                    .iter()
                    .filter(|id| b.source_ids.contains(id))
                    .take(2)
                    .count()
                    == 2;
            if shared_window || interval_overlap(a, b) {
                blocked[i] = true;
                blocked[j] = true
            }
        }
    }
    let mut out = Vec::new();
    for (mut p, skip) in unique.into_iter().zip(blocked) {
        if !skip {
            p.id = format!("p{}:opening:{}", page.number, out.len() + 1);
            out.push(p)
        }
    }
    Ok(out)
}
