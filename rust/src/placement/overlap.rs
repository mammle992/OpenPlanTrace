use super::{Support, WallReason, hatching::HatchIndex, material::Sources, roundoff};
use crate::model::{
    BodyEvidence, LayerKind, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Shape, Wall,
};

/// Coaxial measured solids can share an end or a face, but not an interior strip.
pub(super) fn parallel_bodies_overlap(a: &Wall, b: &Wall) -> bool {
    if a.curve.is_some() || b.curve.is_some() {
        return false;
    }
    let axis = (a.end - a.start).unit();
    let other = (b.end - b.start).unit();
    let eps = roundoff(a)
        .max(roundoff(b))
        .max(a.thickness.min(b.thickness) * 1e-5);
    if axis.cross(other).abs() > 1e-5 {
        return false;
    }
    let ends = [b.start - a.start, b.end - a.start];
    let [x0, x1] = ends.map(|v| v.dot(axis));
    let lo = x0.min(x1).max(0.);
    let hi = x0.max(x1).min(a.start.distance(a.end));
    if hi - lo <= eps {
        return false;
    }
    let [y0, y1] = ends.map(|v| axis.cross(v));
    let y = |x| y0 + (x - x0) * (y1 - y0) / (x1 - x0);
    let (left, right) = (y(lo), y(hi));
    let half = (a.thickness + b.thickness) * 0.5 - eps;
    left.min(right) < half && left.max(right) > -half
}

/// Match separately authored strokes to the two faces of this finite strip.
/// A longer room, dimension or furniture line may coincide with a face without
/// recording either of its ends.
pub(super) fn face_pair<'a>(
    ids: &'a [String],
    sources: &Sources<'_>,
    w: &Wall,
    width: f64,
) -> Option<[&'a str; 2]> {
    let axis = (w.end - w.start).unit();
    let offset = Point::new(-axis.y, axis.x) * (0.5 * width);
    let eps = roundoff(w);
    let mut faces = [None; 2];
    for id in ids {
        let Some(source) = sources.get(id.as_str()).filter(|s| s.exclusive_wall) else {
            continue;
        };
        let Shape::Line { start, end } = source.primitive.shape else {
            continue;
        };
        for (side, delta) in [offset, offset * -1.].into_iter().enumerate() {
            let a = w.start + delta;
            let b = w.end + delta;
            if (start.distance(a) <= eps && end.distance(b) <= eps)
                || (start.distance(b) <= eps && end.distance(a) <= eps)
            {
                faces[side].get_or_insert(id.as_str());
            }
        }
    }
    let pair = [faces[0]?, faces[1]?];
    (pair[0] != pair[1]).then_some(pair)
}

/// Clip the aperture segment in wall-local coordinates, including perpendicular
/// and diagonal openings assigned to a different host.
pub(super) fn aperture_crosses(w: &Wall, opening: &Opening) -> bool {
    let axis = (w.end - w.start).unit();
    let a = opening.start - w.start;
    let b = opening.end - w.start;
    let eps = roundoff(w);
    let mut enter: f64 = 0.;
    let mut exit: f64 = 1.;
    for (start, end, lo, hi) in [
        (a.dot(axis), b.dot(axis), eps, w.start.distance(w.end) - eps),
        (
            a.cross(axis),
            b.cross(axis),
            -w.thickness / 2. + eps,
            w.thickness / 2. - eps,
        ),
    ] {
        if (start < lo && end < lo) || (start > hi && end > hi) {
            return false;
        }
        if start != end {
            let t = (lo - start) / (end - start);
            let u = (hi - start) / (end - start);
            enter = enter.max(t.min(u));
            exit = exit.min(t.max(u));
        }
    }
    enter <= exit
}

/// The opening's own transverse jamb stroke must meet a corner of the
/// candidate and enter its width at that end.
fn jamb<'a>(opening: &'a Opening, w: &Wall, at: Point, sources: &Sources<'_>) -> Option<&'a str> {
    let axis = (w.end - w.start).unit();
    let eps = roundoff(w);
    let half = w.thickness / 2.;
    opening.source_ids.iter().find_map(|id| {
        let source = sources.get(id.as_str())?;
        if !source.exclusive_wall && source.primitive.layer_kind() != LayerKind::Door {
            return None;
        }
        let Shape::Line { start, end } = source.primitive.shape else {
            return None;
        };
        let a = start - at;
        let b = end - at;
        let (u, v) = (a.cross(axis), b.cross(axis));
        let at_corner = (u.abs() - half).abs() <= eps || (v.abs() - half).abs() <= eps;
        (at_corner
            && a.dot(axis).abs() <= eps
            && b.dot(axis).abs() <= eps
            && u.max(v).min(half) - u.min(v).max(-half) > eps)
            .then_some(id.as_str())
    })
}

fn signed_jamb<'a>(
    w: &Wall,
    host: &Wall,
    o: &'a Opening,
    sources: &Sources<'_>,
) -> Option<(Point, [&'a str; 2])> {
    if o.kind != OpeningKind::Door || o.evidence != OpeningEvidence::FiniteJambSwing {
        return None;
    }
    let axis = (w.end - w.start).unit();
    let a = o.start - w.start;
    let b = o.end - w.start;
    let eps = roundoff(host).max(roundoff(w));
    if a.cross(axis).abs() > eps || b.cross(axis).abs() > eps {
        return None;
    }
    let len = w.start.distance(w.end);
    let lo = a.dot(axis).min(b.dot(axis));
    let hi = a.dot(axis).max(b.dot(axis));
    let reach = host.thickness / 2. + eps;
    let at = if hi <= eps && hi >= -reach {
        w.start
    } else if lo >= len - eps && lo <= len + reach {
        w.end
    } else {
        return None;
    };
    let swing = o.swing_observations.iter().find(|s| {
        o.source_ids.contains(&s.arc_source_id)
            && sources
                .get(s.arc_source_id.as_str())
                .is_some_and(|p| matches!(p.primitive.shape, Shape::Arc { .. }))
    })?;
    Some((at, [swing.arc_source_id.as_str(), jamb(o, w, at, sources)?]))
}

/// Both facing skins start at the jamb. Their remote ends can meet a return
/// at different positions, within the depth measured by the repeated hatch.
fn skin_faces<'a>(
    host: &'a Wall,
    w: &Wall,
    at: Point,
    depth: f64,
    sources: &Sources<'_>,
) -> Option<[&'a str; 2]> {
    let end = if at == w.start { w.end } else { w.start };
    let axis = (end - at).unit();
    let eps = roundoff(host).max(roundoff(w));
    let mut sides = [None; 2];
    for id in &host.source_ids {
        let Some(s) = sources.get(id.as_str()).filter(|s| s.exclusive_wall) else {
            continue;
        };
        let Shape::Line { start, end: tip } = s.primitive.shape else {
            continue;
        };
        let a = start - at;
        let b = tip - at;
        let offset = a.cross(axis);
        let u = a.dot(axis);
        let v = b.dot(axis);
        if (offset.abs() - 0.5 * host.thickness).abs() <= eps
            && (b.cross(axis) - offset).abs() <= eps
            && u.min(v).abs() <= eps
            && u.max(v) >= at.distance(end) - depth - eps
        {
            sides[usize::from(offset > 0.)].get_or_insert(id.as_str());
        }
    }
    Some([sides[0]?, sides[1]?])
}

pub(super) fn door_flank(
    w: &Wall,
    s: &Support,
    host_index: usize,
    page: &PageScan,
    support: &[Support],
    sources: &Sources<'_>,
    hatches: Option<&HatchIndex<'_>>,
) -> Option<(WallReason, Vec<String>)> {
    let host = &page.walls[host_index];
    let eps = roundoff(w).max(roundoff(host));
    if !s.complete
        || s.has_open_edge
        || !s.openings.is_empty()
        || host.curve.is_some()
        || host.body != BodyEvidence::ParallelFaces
        || w.body != BodyEvidence::ParallelFaces
        || w.thickness <= eps
        || host.thickness - w.thickness <= 2. * eps
        || support[host_index].openings.is_empty()
    {
        return None;
    }
    let axis = (host.end - host.start).unit();
    let length = host.start.distance(host.end);
    if length <= w.start.distance(w.end) + eps
        || [w.start, w.end]
            .into_iter()
            .any(|p| (p - host.start).cross(axis).abs() > eps)
    {
        return None;
    }
    let (at, jamb) = support[host_index]
        .openings
        .iter()
        .find_map(|&n| signed_jamb(w, host, &page.openings[n], sources))?;
    let along = |p: Point| (p - host.start).dot(axis);
    if along(at) < -eps || along(at) > length + eps {
        return None;
    }
    let distal = if at == w.start { w.end } else { w.start };
    let overrun = (-along(distal)).max(along(distal) - length);
    let inner = face_pair(&w.source_ids, sources, w, w.thickness)?;
    let exact_outer = face_pair(&host.source_ids, sources, w, host.thickness);
    let (reason, outer, mut ids) = if overrun <= eps
        && let Some(faces) = exact_outer
    {
        (WallReason::NestedFacesAtDoorJamb, faces, Vec::new())
    } else {
        let expected_depth = (host.thickness - w.thickness) * 0.5;
        if overrun > expected_depth + eps {
            return None;
        }
        let proof = hatches?.witnesses(w, page, sources)?;
        if (proof.depth - expected_depth).abs() > eps.max(w.thickness * 1e-5) {
            return None;
        }
        let faces = skin_faces(host, w, at, expected_depth, sources)?;
        (WallReason::HatchedFacesAtDoorJamb, faces, proof.ids)
    };
    if inner.iter().any(|id| outer.contains(id)) {
        return None;
    }
    if page.openings.iter().any(|o| aperture_crosses(w, o)) {
        return None;
    }
    ids.extend(inner.into_iter().chain(outer).chain(jamb).map(String::from));
    ids.sort();
    ids.dedup();
    Some((reason, ids))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nearly_parallel_spans_can_cross_between_distant_endpoints() {
        let wall = |x, y, end_x, end_y| Wall {
            id: String::new(),
            page: 1,
            start: Point::new(x, y),
            end: Point::new(end_x, end_y),
            thickness: 1.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![],
            requires_review: true,
        };
        let a = wall(-5., 0., 5., 0.);
        let long = wall(-1e6, -2., 1e6, 2.);
        assert!(parallel_bodies_overlap(&a, &long));
        assert!(parallel_bodies_overlap(&long, &a));
        assert!(!parallel_bodies_overlap(&a, &wall(5., 0., 15., 0.)));
        assert!(!parallel_bodies_overlap(&a, &wall(-5., 1., 5., 1.)));
    }

    #[test]
    fn crossing_and_touching_apertures_on_a_rotated_wall() {
        let axis = Point::new(3., 4.).unit();
        let normal = Point::new(-axis.y, axis.x);
        let point = |x, y| Point::new(50., 70.) + axis * x + normal * y;
        let mut wall = Wall {
            id: "short".into(),
            page: 1,
            start: point(0., 0.),
            end: point(20., 0.),
            thickness: 4.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![],
            requires_review: true,
        };
        // A diagonal may cross between two outside ends, or miss an end corner
        // even though its axis-aligned bounds overlap the wall.
        let cases = [
            ((8., -9.), (11., 9.), true),
            ((-1., 0.), (7., 0.), true),
            ((19., 3.), (25., -1.), false),
            ((0., -5.), (0., 5.), false),
            ((20., 0.), (30., 0.), false),
            ((0., -2.), (20., -2.), false),
        ];
        for _ in 0..2 {
            for (a, b, conflict) in cases {
                let mut opening = Opening {
                    physical_jambs: None,
                    id: "cut".into(),
                    page: 1,
                    kind: OpeningKind::Door,
                    operation: "Hinged".into(),
                    host_wall_id: "other".into(),
                    host_wall_ids: vec!["other".into()],
                    start: point(a.0, a.1),
                    end: point(b.0, b.1),
                    hinge: None,
                    swing_observations: vec![],
                    source_ids: vec![],
                    evidence: OpeningEvidence::FiniteJambSwing,
                    requires_review: true,
                };
                assert_eq!(aperture_crosses(&wall, &opening), conflict);
                std::mem::swap(&mut opening.start, &mut opening.end);
                assert_eq!(aperture_crosses(&wall, &opening), conflict);
            }
            std::mem::swap(&mut wall.start, &mut wall.end);
        }
    }
}
