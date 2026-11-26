//! Ready material bounded by independently drawn physical doorway jambs.
//! Original observations remain immutable; the explicit placement extent is a
//! conservative subset covered in full by certified visible paint and face ink.
use super::{PlacementExtent, WallDecision, WallReason, WallStatus, overlap, roundoff};
use crate::model::{BodyEvidence, Opening, OpeningKind, PageScan, Point, Primitive, Shape, Wall};
use crate::openings::physical_jambs::structural;
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 32_000_000;
const MAX_PAINTS: usize = 4096;
const MAX_POINTS: usize = 64;
fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("source-bounded jamb work budget exceeded")?;
    Ok(())
}

fn segments(
    p: &Primitive,
    mut add: impl FnMut(Point, Point) -> Result<(), String>,
) -> Result<(), String> {
    match &p.shape {
        Shape::Line { start, end } => add(*start, *end)?,
        Shape::Polyline {
            points,
            closed,
            filled: false,
        } => {
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

fn union(parts: &mut [(f64, f64, &str)], start: f64, end: f64, eps: f64) -> Option<Vec<String>> {
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut at = start;
    let mut ids = BTreeSet::new();
    for &(lo, hi, id) in parts.iter() {
        if hi <= at + eps {
            continue;
        }
        if lo > at + eps {
            return None;
        }
        at = hi;
        ids.insert(id.to_owned());
        if at >= end - eps {
            return Some(ids.into_iter().collect());
        }
    }
    None
}

/// The terminal-connected union, not a percentage of an observed face.
fn face_component(
    parts: &mut [(f64, f64, &str)],
    plane: f64,
    before: bool,
    eps: f64,
) -> Option<(f64, Vec<String>)> {
    if !before {
        for p in parts.iter_mut() {
            let lo = p.0;
            p.0 = -p.1;
            p.1 = -lo;
        }
    }
    let plane = if before { plane } else { -plane };
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(a.1.total_cmp(&b.1))
            .then(a.2.cmp(b.2))
    });
    let mut lo = f64::NAN;
    let mut hi = f64::NAN;
    let mut ids = BTreeSet::new();
    for &(a, b, id) in parts.iter() {
        if b > plane + eps {
            continue;
        }
        if !lo.is_finite() || a > hi + eps {
            lo = a;
            hi = b;
            ids.clear();
        } else {
            hi = hi.max(b);
        }
        ids.insert(id.to_owned());
        if (hi - plane).abs() <= eps {
            return Some((if before { lo } else { -lo }, ids.into_iter().collect()));
        }
    }
    None
}

fn cap(
    by_id: &HashMap<&str, &Primitive>,
    o: &Opening,
    w: &Wall,
    t: f64,
    axis: Point,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let Some(proof) = &o.physical_jambs else {
        return Ok(None);
    };
    let mut parts = vec![];
    let half = w.thickness * 0.5;
    for id in &proof.cap_source_ids {
        charge(work, 1)?;
        let Some(p) = by_id
            .get(id.as_str())
            .copied()
            .filter(|p| structural(p) && p.layer.as_deref() == Some(proof.source_layer.as_str()))
        else {
            return Ok(None);
        };
        segments(p, |a, b| {
            charge(work, 1)?;
            let a = a - w.start;
            let b = b - w.start;
            let (x, y) = (a.dot(axis), b.dot(axis));
            let (u, v) = (axis.cross(a), axis.cross(b));
            if (x - t).abs() <= eps
                && (y - t).abs() <= eps
                && u.min(v) >= -half - eps
                && u.max(v) <= half + eps
                && (u - v).abs() > eps
            {
                parts.push((u.min(v), u.max(v), p.id.as_str()));
            }
            Ok(())
        })?;
    }
    charge(
        work,
        parts
            .len()
            .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
    )?;
    Ok(union(&mut parts, -half, half, eps))
}

#[derive(Clone)]
struct Paint<'a> {
    id: &'a str,
    points: Vec<Point>,
}

fn convex(points: &[Point], eps: f64, work: &mut usize) -> Result<bool, String> {
    if !(3..=MAX_POINTS).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
        return Ok(false);
    }
    charge(work, points.len().saturating_mul(points.len()))?;
    let origin = points[0];
    let area: f64 = points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .map(|(a, b)| (*a - origin).cross(*b - origin))
        .sum();
    if area.abs() <= eps * eps {
        return Ok(false);
    }
    let sign = area.signum();
    Ok(points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .all(|(&a, &b)| {
            let e = b - a;
            e.norm() > eps
                && points
                    .iter()
                    .all(|&p| sign * e.cross(p - a) >= -eps * e.norm())
        }))
}

fn section(points: &[Point], x: f64, eps: f64) -> Option<(f64, f64)> {
    let mut ys = vec![];
    for (&a, &b) in points.iter().zip(points.iter().cycle().skip(1)) {
        if x < a.x.min(b.x) - eps || x > a.x.max(b.x) + eps {
            continue;
        }
        if (a.x - b.x).abs() <= eps {
            if (x - a.x).abs() <= eps {
                ys.extend([a.y, b.y]);
            }
        } else {
            let t = ((x - a.x) / (b.x - a.x)).clamp(0., 1.);
            ys.push(a.y + (b.y - a.y) * t);
        }
    }
    if ys.is_empty() {
        None
    } else {
        Some((
            ys.iter().copied().fold(f64::INFINITY, f64::min),
            ys.into_iter().fold(f64::NEG_INFINITY, f64::max),
        ))
    }
}

/// A convex paint covers the constant band shared by both end cross-sections.
/// Convexity proves the whole band between those planes, including a mitered
/// source polygon clipped to a rectangular supported flank.
fn painted(
    paints: &[Paint<'_>],
    lo: f64,
    hi: f64,
    half: f64,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    let mut parts = vec![];
    for p in paints {
        charge(work, p.points.len().saturating_mul(2).saturating_add(1))?;
        let (Some(a), Some(b)) = (section(&p.points, lo, eps), section(&p.points, hi, eps)) else {
            continue;
        };
        let y0 = a.0.max(b.0).max(-half);
        let y1 = a.1.min(b.1).min(half);
        if y1 - y0 > eps {
            parts.push((y0, y1, p.id));
        }
    }
    charge(
        work,
        parts
            .len()
            .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
    )?;
    Ok(union(&mut parts, -half, half, eps))
}

fn clip(points: &[Point], axis: usize, value: f64, greater: bool) -> Vec<Point> {
    let mut out = vec![];
    let coord = |p: Point| if axis == 0 { p.x } else { p.y };
    for (&a, &b) in points.iter().zip(points.iter().cycle().skip(1)) {
        let av = coord(a);
        let bv = coord(b);
        let inside = |v: f64| if greater { v >= value } else { v <= value };
        let (ia, ib) = (inside(av), inside(bv));
        if ia {
            out.push(a);
        }
        if ia != ib {
            out.push(a + (b - a) * ((value - av) / (bv - av)));
        }
    }
    out
}
fn paint_enters_gap(
    paints: &[Paint<'_>],
    lo: f64,
    hi: f64,
    half: f64,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    for p in paints {
        charge(work, p.points.len().saturating_mul(20))?;
        let mut ps = p.points.clone();
        for (a, v, g) in [
            (0, lo + eps, true),
            (0, hi - eps, false),
            (1, -half + eps, true),
            (1, half - eps, false),
        ] {
            ps = clip(&ps, a, v, g);
            if ps.is_empty() {
                break;
            }
        }
        if ps.len() < 3 {
            continue;
        }
        let origin = ps[0];
        let area: f64 = ps
            .iter()
            .zip(ps.iter().cycle().skip(1))
            .map(|(a, b)| (*a - origin).cross(*b - origin))
            .sum();
        if area.abs() > eps * eps {
            return Ok(true);
        }
    }
    Ok(false)
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) -> Result<(), String> {
    if decisions.len() != page.walls.len()
        || decisions
            .iter()
            .zip(&page.walls)
            .any(|(d, w)| d.wall_id != w.id)
    {
        return Err("bounded jamb decisions are not aligned with observations".into());
    }
    let mut work = MAX_WORK;
    charge(
        &mut work,
        page.primitives.len().saturating_add(page.walls.len()),
    )?;
    let by_id: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut certified: HashSet<&str> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    certified.extend(
        page.placement_polygon_paint_ids
            .iter()
            .flatten()
            .map(String::as_str),
    );
    charge(&mut work, certified.len())?;
    let mut claimed = HashSet::new();
    for item in page.withheld_walls.iter().flatten() {
        charge(&mut work, item.wall.source_ids.len() + 1)?;
        claimed.extend(item.wall.source_ids.iter().map(String::as_str));
    }
    for item in &page.objects {
        charge(&mut work, item.source_ids.len() + 1)?;
        if item.footprint.is_some() || item.geometry.is_some() {
            claimed.extend(item.source_ids.iter().map(String::as_str));
        }
    }
    let hosts: HashMap<_, _> = page
        .walls
        .iter()
        .enumerate()
        .map(|(i, w)| (w.id.as_str(), i))
        .collect();
    let mut proposals: Vec<(usize, PlacementExtent)> = vec![];
    for o in &page.openings {
        charge(&mut work, 1)?;
        let Some(proof) = &o.physical_jambs else {
            continue;
        };
        if o.kind != OpeningKind::Door
            || proof.revision != 1
            || proof.host_wall_id != o.host_wall_id
            || o.host_wall_ids.len() != 1
        {
            continue;
        }
        let Some(&i) = hosts.get(o.host_wall_id.as_str()) else {
            continue;
        };
        let w = &page.walls[i];
        if decisions[i].status != WallStatus::Review
            || w.curve.is_some()
            || w.body != BodyEvidence::ParallelFaces
            || w.thickness <= 0.
            || !w.thickness.is_finite()
            || !w.start.finite()
            || !w.end.finite()
        {
            continue;
        }
        charge(&mut work, page.openings.len())?;
        if page
            .openings
            .iter()
            .filter(|p| p.host_wall_ids.contains(&w.id))
            .count()
            != 1
        {
            continue;
        }
        let axis = (w.end - w.start).unit();
        let length = w.start.distance(w.end);
        let half = w.thickness * 0.5;
        let eps = roundoff(w) * 2.;
        let x = (o.start - w.start).dot(axis);
        let y = (o.end - w.start).dot(axis);
        let (gap_lo, gap_hi) = (x.min(y), x.max(y));
        if gap_lo <= eps
            || gap_hi >= length - eps
            || (o.start - w.start).cross(axis).abs() > eps
            || (o.end - w.start).cross(axis).abs() > eps
        {
            continue;
        }
        let mut north = [vec![], vec![]];
        let mut south = [vec![], vec![]];
        for id in &proof.rail_source_ids {
            charge(&mut work, w.source_ids.len().saturating_add(1))?;
            if !w.source_ids.contains(id) {
                continue;
            }
            let Some(p) = by_id.get(id.as_str()).copied().filter(|p| {
                structural(p) && p.layer.as_deref() == Some(proof.source_layer.as_str())
            }) else {
                continue;
            };
            segments(p, |a, b| {
                charge(&mut work, 1)?;
                let a = a - w.start;
                let b = b - w.start;
                let (u, v) = (axis.cross(a), axis.cross(b));
                if (u - v).abs() > eps || (u.abs() - half).abs() > eps {
                    return Ok(());
                }
                let (a, b) = (a.dot(axis), b.dot(axis));
                let (lo, hi) = (a.min(b), a.max(b));
                let side = usize::from(u > 0.);
                if hi <= gap_lo + eps {
                    north[side].push((lo, hi, p.id.as_str()));
                }
                if lo >= gap_hi - eps {
                    south[side].push((lo, hi, p.id.as_str()));
                }
                Ok(())
            })?;
        }
        let mut near = vec![];
        let mut far = vec![];
        let mut ids = BTreeSet::new();
        for parts in &mut north {
            charge(
                &mut work,
                parts
                    .len()
                    .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
            )?;
            if let Some((t, src)) = face_component(parts, gap_lo, true, eps) {
                near.push(t);
                ids.extend(src);
            }
        }
        for parts in &mut south {
            charge(
                &mut work,
                parts
                    .len()
                    .saturating_mul(parts.len().max(1).ilog2() as usize + 1),
            )?;
            if let Some((t, src)) = face_component(parts, gap_hi, false, eps) {
                far.push(t);
                ids.extend(src);
            }
        }
        if near.len() != 2 || far.len() != 2 {
            continue;
        }
        let lo = near.into_iter().fold(0_f64, f64::max);
        let hi = far.into_iter().fold(length, f64::min);
        if gap_lo - lo < 2. * w.thickness - eps || hi - gap_hi < 2. * w.thickness - eps {
            continue;
        }
        let (Some(ca), Some(cb)) = (
            cap(&by_id, o, w, gap_lo, axis, eps, &mut work)?,
            cap(&by_id, o, w, gap_hi, axis, eps, &mut work)?,
        ) else {
            continue;
        };
        ids.extend(ca);
        ids.extend(cb);
        let mut paints = vec![];
        for p in &page.primitives {
            charge(&mut work, 1)?;
            if !certified.contains(p.id.as_str())
                || claimed.contains(p.id.as_str())
                || p.layer.as_deref() != Some(proof.source_layer.as_str())
                || !structural(p)
            {
                continue;
            }
            let Shape::Polyline {
                points,
                filled: true,
                closed: true,
            } = &p.shape
            else {
                continue;
            };
            let source = if points.first() == points.last() {
                &points[..points.len().saturating_sub(1)]
            } else {
                points.as_slice()
            };
            if source.len() > MAX_POINTS {
                continue;
            }
            charge(&mut work, source.len())?;
            let local: Vec<_> = source
                .iter()
                .map(|&p| {
                    let v = p - w.start;
                    Point::new(v.dot(axis), axis.cross(v))
                })
                .collect();
            let minx = local.iter().map(|p| p.x).fold(f64::INFINITY, f64::min);
            let maxx = local.iter().map(|p| p.x).fold(f64::NEG_INFINITY, f64::max);
            let miny = local.iter().map(|p| p.y).fold(f64::INFINITY, f64::min);
            let maxy = local.iter().map(|p| p.y).fold(f64::NEG_INFINITY, f64::max);
            if maxx < lo - eps
                || minx > hi + eps
                || maxy < -half - eps
                || miny > half + eps
                || !convex(&local, eps, &mut work)?
            {
                continue;
            }
            paints.push(Paint {
                id: &p.id,
                points: local,
            });
            if paints.len() > MAX_PAINTS {
                return Err("bounded jamb paint candidate limit exceeded".into());
            }
        }
        if paint_enters_gap(&paints, gap_lo, gap_hi, half, eps, &mut work)? {
            continue;
        }
        let (Some(a), Some(b)) = (
            painted(&paints, lo, gap_lo, half, eps, &mut work)?,
            painted(&paints, gap_hi, hi, half, eps, &mut work)?,
        ) else {
            continue;
        };
        ids.extend(a);
        ids.extend(b);
        let spans = [(lo, gap_lo), (gap_hi, hi)];
        let mut blocked = false;
        for (a, b) in spans {
            let mut flank = w.clone();
            flank.start = w.start + axis * a;
            flank.end = w.start + axis * b;
            charge(
                &mut work,
                page.openings.len().saturating_add(page.walls.len()),
            )?;
            if page
                .openings
                .iter()
                .any(|other| other.id != o.id && overlap::aperture_crosses(&flank, other))
                || page.walls.iter().zip(decisions.iter()).any(|(old, d)| {
                    d.status == WallStatus::Ready && overlap::parallel_bodies_overlap(&flank, old)
                })
            {
                blocked = true;
                break;
            }
        }
        if blocked {
            continue;
        }
        // Resolve all competing candidates before publishing any decision.
        // Existing Ready observations always retain precedence. New candidates
        // also cannot duplicate one another's independently supported material.
        for &(other_i, ref extent) in &proposals {
            charge(&mut work, page.openings.len().saturating_add(5))?;
            if i == other_i {
                return Err("competing physical jamb placement extents".into());
            }
            let other = &page.walls[other_i];
            let Some(opening) = page.openings.iter().find(|p| p.host_wall_id == other.id) else {
                continue;
            };
            let v = (other.end - other.start).unit();
            let a = (opening.start - other.start).dot(v);
            let b = (opening.end - other.start).dot(v);
            let other_parts = [
                (extent.start, other.start + v * a.min(b)),
                (other.start + v * a.max(b), extent.end),
            ];
            for (a, b) in spans {
                let mut flank = w.clone();
                flank.start = w.start + axis * a;
                flank.end = w.start + axis * b;
                for (c, d) in other_parts {
                    let mut existing = other.clone();
                    existing.start = c;
                    existing.end = d;
                    blocked |= overlap::parallel_bodies_overlap(&flank, &existing);
                }
            }
        }
        if blocked {
            continue;
        }
        ids.extend(proof.rail_source_ids.iter().cloned());
        ids.extend(proof.cap_source_ids.iter().cloned());
        proposals.push((
            i,
            PlacementExtent {
                thickness: None,
                start: w.start + axis * lo,
                end: w.start + axis * hi,
                source_ids: ids.into_iter().collect(),
            },
        ));
    }
    // Apply only after all bounded evidence work has succeeded.
    for (i, extent) in proposals {
        decisions[i].status = WallStatus::Ready;
        decisions[i].reason = WallReason::SourceBoundedDoorJambFlanks;
        decisions[i].support_source_ids = extent.source_ids.clone();
        decisions[i].placement_extent = Some(extent);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{OpeningEvidence, WallSelectionReason, WithheldWall};
    use crate::openings::physical_jambs::{proposals, tests::fixture as frame_fixture};

    fn fill(id: &str, points: &[(f64, f64)]) -> Primitive {
        Primitive {
            id: id.into(),
            layer: Some("A-WALL".into()),
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Polyline {
                points: points.iter().map(|&(x, y)| Point::new(x, y)).collect(),
                closed: true,
                filled: true,
            },
        }
    }

    fn fixture() -> (PageScan, Vec<WallDecision>) {
        let (mut primitives, w, mut door) = frame_fixture();
        let correction = proposals(
            &primitives,
            std::slice::from_ref(&w),
            std::slice::from_ref(&door),
        )
        .unwrap()
        .pop()
        .unwrap();
        door.start = correction.start;
        door.end = correction.end;
        door.physical_jambs = Some(correction.evidence);
        primitives.extend([
            fill(
                "upper core",
                &[(95., 10.), (102., 6.5), (102., 45.), (95., 45.)],
            ),
            fill(
                "upper facing",
                &[(102., 6.5), (105., 5.), (105., 45.), (102., 45.)],
            ),
            fill(
                "lower core",
                &[(95., 70.), (102., 70.), (102., 105.), (95., 105.)],
            ),
            fill(
                "lower facing",
                &[(102., 70.), (105., 70.), (105., 105.), (102., 105.)],
            ),
        ]);
        let decision = WallDecision {
            wall_id: w.id.clone(),
            status: WallStatus::Review,
            reason: WallReason::IncompletePhysicalSpan,
            component_id: "observed component".into(),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: vec![],
            opening_ids: vec![door.id.clone()],
            room_ids: vec![],
            placement_extent: None,
        };
        let page = PageScan {
            finish_visibility: None,
            pdf_input: None,
            page_number: 1,
            exterior_enclosures: None,
            exterior_connections: None,
            placement_paint_ids: Some(vec!["lower core".into(), "lower facing".into()]),
            placement_polygon_paint_ids: Some(vec!["upper core".into(), "upper facing".into()]),
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 200.,
            height: 200.,
            units_to_millimeters: None,
            scale_source: None,
            calibration_source_ids: vec![],
            primitives,
            walls: vec![w],
            placement_decisions: None,
            withheld_walls: None,
            openings: vec![door],
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
        (page, vec![decision])
    }

    #[test]
    fn bounded_flanks_place_full_layered_material_and_leave_unknown_tail_out() {
        let (page, mut decisions) = fixture();
        let before = serde_json::to_value(&page).unwrap();
        select(&page, &mut decisions).unwrap();
        assert_eq!(decisions[0].status, WallStatus::Ready);
        assert_eq!(decisions[0].reason, WallReason::SourceBoundedDoorJambFlanks);
        let e = decisions[0].placement_extent.as_ref().unwrap();
        assert_eq!(e.start, Point::new(100., 10.));
        assert_eq!(e.end, Point::new(100., 105.));
        for id in [
            "west north",
            "east north",
            "west south",
            "east south",
            "north cap west",
            "north cap east",
            "south cap west",
            "south cap east",
            "upper core",
            "upper facing",
            "lower core",
            "lower facing",
        ] {
            assert!(e.source_ids.contains(&id.to_owned()), "missing {id}");
        }
        assert_eq!(decisions[0].support_source_ids, e.source_ids);
        assert_eq!(serde_json::to_value(&page).unwrap(), before);
        assert!(page.walls[0].requires_review);
        assert_eq!(page.walls[0].end, Point::new(100., 110.));
    }

    #[test]
    fn bounded_flanks_require_visibility_and_every_material_layer() {
        for removed in ["upper core", "upper facing", "lower core", "lower facing"] {
            let (mut page, mut d) = fixture();
            page.placement_paint_ids
                .as_mut()
                .unwrap()
                .retain(|id| id != removed);
            page.placement_polygon_paint_ids
                .as_mut()
                .unwrap()
                .retain(|id| id != removed);
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{removed}");
        }
    }

    #[test]
    fn bounded_flanks_do_not_use_ninety_nine_percent_paint_or_partial_caps() {
        for id in ["lower facing", "south cap east"] {
            let (mut page, mut d) = fixture();
            let p = page.primitives.iter_mut().find(|p| p.id == id).unwrap();
            match &mut p.shape {
                Shape::Polyline { points, .. } => {
                    for p in points {
                        if p.y == 105. {
                            p.y -= 0.01;
                        }
                    }
                }
                Shape::Line { end, .. } => end.x -= 0.01,
                _ => unreachable!(),
            }
            select(&page, &mut d).unwrap();
            assert_eq!(d[0].status, WallStatus::Review, "{id}");
        }
    }

    #[test]
    fn bounded_flanks_keep_visible_paint_and_other_openings_out_of_apertures() {
        let (mut page, mut d) = fixture();
        page.primitives.push(fill(
            "continuous visible material",
            &[(95., 40.), (105., 40.), (105., 80.), (95., 80.)],
        ));
        page.placement_paint_ids
            .as_mut()
            .unwrap()
            .push("continuous visible material".into());
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        let (mut page, mut d) = fixture();
        let mut window = page.openings[0].clone();
        window.id = "signed window".into();
        window.kind = OpeningKind::Window;
        window.host_wall_id = "different wall".into();
        window.host_wall_ids = vec!["different wall".into()];
        window.evidence = OpeningEvidence::ExplicitWindowFrame;
        window.physical_jambs = None;
        window.start = Point::new(94., 30.);
        window.end = Point::new(106., 30.);
        page.openings.push(window);
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn bounded_flanks_reject_furniture_claimed_paint_and_existing_ready_duplicates() {
        let (mut page, mut d) = fixture();
        let mut furniture = page.walls[0].clone();
        furniture.id = "withheld furniture".into();
        furniture.source_ids = vec!["upper core".into()];
        page.withheld_walls = Some(vec![WithheldWall {
            wall: furniture,
            furniture_outline: None,
            reason: WallSelectionReason::CabinetFront,
            witness_source_ids: vec![],
        }]);
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        let (mut page, mut d) = fixture();
        let mut existing = page.walls[0].clone();
        existing.id = "earlier ready".into();
        existing.end = Point::new(100., 40.);
        let mut old = d[0].clone();
        old.wall_id = existing.id.clone();
        old.status = WallStatus::Ready;
        old.reason = WallReason::SupportedPhysicalEndpoints;
        page.walls.push(existing);
        d.push(old.clone());
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
        assert_eq!(d[1], old);
    }

    #[test]
    fn bounded_flanks_never_rewrite_previously_ready_or_unowned_rails() {
        let (page, mut d) = fixture();
        d[0].status = WallStatus::Ready;
        let before = d.clone();
        select(&page, &mut d).unwrap();
        assert_eq!(d, before);
        let (mut page, mut d) = fixture();
        page.walls[0].source_ids.retain(|id| id != "east north");
        select(&page, &mut d).unwrap();
        assert_eq!(d[0].status, WallStatus::Review);
    }

    #[test]
    fn bounded_flanks_budget_and_alignment_errors_publish_nothing() {
        let (page, mut d) = fixture();
        d[0].wall_id = "wrong wall".into();
        let before = d.clone();
        assert!(select(&page, &mut d).is_err());
        assert_eq!(d, before);
        let (mut page, mut d) = fixture();
        for i in 0..MAX_PAINTS {
            let id = format!("excess paint {i}");
            page.primitives.push(fill(
                &id,
                &[(95., 70.), (105., 70.), (105., 105.), (95., 105.)],
            ));
            page.placement_paint_ids.as_mut().unwrap().push(id);
        }
        let before = d.clone();
        assert!(select(&page, &mut d).is_err());
        assert_eq!(d, before);
    }

    #[test]
    fn convex_miter_proof_rejects_concave_and_nonfinite_shapes() {
        let mut work = MAX_WORK;
        assert!(
            !convex(
                &[
                    Point::new(0., 0.),
                    Point::new(4., 0.),
                    Point::new(2., 1.),
                    Point::new(4., 4.),
                    Point::new(0., 4.)
                ],
                1e-12,
                &mut work
            )
            .unwrap()
        );
        assert!(
            !convex(
                &[
                    Point::new(0., 0.),
                    Point::new(f64::NAN, 0.),
                    Point::new(0., 4.)
                ],
                1e-12,
                &mut work
            )
            .unwrap()
        );
    }
}
