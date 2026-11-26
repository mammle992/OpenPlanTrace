//! Recover short wall flanks beside a signed vector door from exact source ink.
use super::{interior_corners, interior_seams::overlap_rect, *};
use std::collections::{BTreeSet, HashSet};

const PREFIX: &str = "interior-jamb:";

struct Flank {
    wall: Wall,
    base: Point,
    door: usize,
    side: usize,
    tile: String,
}

fn covered_from<'a>(mut parts: Vec<(f64, f64, &'a str)>, eps: f64) -> (f64, Vec<&'a str>) {
    parts.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(b.1.total_cmp(&a.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = 0.;
    let mut ids = vec![];
    for (lo, hi, id) in parts {
        if lo > end + eps {
            break;
        }
        if hi > end + eps {
            end = hi;
            ids.push(id);
        }
    }
    (end, ids)
}

fn cap<'a>(f: &Frame, ink: &'a [Rail<'a>], plane: f64, eps: f64) -> Option<Vec<&'a str>> {
    let parts = ink
        .iter()
        .filter(|s| s.stroked)
        .filter_map(|s| {
            let [a, b] = s.ends.map(|p| f.local(p));
            let lo = a.1.min(b.1);
            let hi = a.1.max(b.1);
            ((a.0 - plane).abs().max((b.0 - plane).abs()) <= eps
                && lo >= -f.half - eps
                && hi <= f.half + eps)
                .then_some((lo + f.half, hi + f.half, s.id))
        })
        .collect();
    let (end, ids) = covered_from(parts, eps);
    (end >= 2. * f.half - eps).then_some(ids)
}

fn rail<'a>(
    f: &Frame,
    ink: &'a [Rail<'a>],
    plane: f64,
    direction: f64,
    side: f64,
    eps: f64,
) -> (f64, Vec<&'a str>) {
    let parts = ink
        .iter()
        .filter_map(|s| {
            let [a, b] = s.ends.map(|p| f.local(p));
            let u = (a.0 - plane) * direction;
            let v = (b.0 - plane) * direction;
            ((a.1 - side).abs().max((b.1 - side).abs()) <= eps).then_some((
                u.min(v),
                u.max(v),
                s.id,
            ))
        })
        .collect();
    covered_from(parts, eps)
}

fn paint<'a>(
    f: &Frame,
    ink: &'a [Rail<'a>],
    template: &insulation::MaterialTile,
    tiles: &[(&insulation::MaterialTile, Wall)],
) -> Option<Vec<&'a str>> {
    let repeat = template.repeat.dot(f.axis);
    let across = f.axis.cross(template.across);
    let phase = f.local(template.origin).0;
    let width = 2. * f.half;
    let tol = f.half * 0.006;
    if (across.abs() - width).abs() > tol || !(0.75 * width..=1.3 * width).contains(&repeat.abs()) {
        return None;
    }
    let edge = -across.signum() * f.half;
    let mut slopes = [0, 0];
    let mut ids = vec![];
    let mut paths = HashSet::new();
    let mut longitudinal = [f64::INFINITY, f64::NEG_INFINITY];
    let mut lateral = longitudinal;
    for s in ink {
        let [a, b] = s.ends.map(|p| f.local(p));
        if a.0.min(b.0) < -tol
            || a.0.max(b.0) > f.length + tol
            || a.1.abs().max(b.1.abs()) > f.half + tol
            || (a.0 - b.0).abs().min((a.1 - b.1).abs()) <= tol * 4.
        {
            continue;
        }
        let k = ((a.0.midpoint(b.0) - phase) / repeat).round();
        let a = Point::new(a.0, a.1);
        let b = Point::new(b.0, b.1);
        let witnessed = template.strokes.iter().any(|stroke| {
            [-1., 0., 1.].into_iter().any(|cycle| {
                let [c, d] = stroke
                    .map(|p| Point::new(phase + (k + cycle + p.x) * repeat, edge + p.y * across));
                crate::model::on_segment(a, c, d).0.distance(a) <= tol
                    && crate::model::on_segment(b, c, d).0.distance(b) <= tol
            })
        });
        if !witnessed || !paths.insert(draw_path(s.id)) {
            continue;
        }
        slopes[usize::from((a.x - b.x) * (a.y - b.y) > 0.)] += 1;
        longitudinal = [
            longitudinal[0].min(a.x).min(b.x),
            longitudinal[1].max(a.x).max(b.x),
        ];
        lateral = [lateral[0].min(a.y).min(b.y), lateral[1].max(a.y).max(b.y)];
        ids.push(s.id);
    }
    if slopes.iter().any(|&n| n < 2) || lateral[0] > -0.72 * f.half || lateral[1] < 0.72 * f.half {
        return None;
    }
    if longitudinal[0] <= f.length * 0.12 && longitudinal[1] >= f.length * 0.88 {
        return Some(ids);
    }
    // A cropped remainder can abut an independently parsed batt tile. Its
    // local pattern witnesses and the full painted tile must meet directly.
    let eps = tol * 0.15;
    for (tile, w) in tiles {
        let [a, b] = [w.start, w.end].map(|p| f.local(p));
        let lo = a.0.min(b.0);
        let hi = a.0.max(b.0);
        if tile.id != template.id
            || a.1.abs().max(b.1.abs()) > eps
            || (w.thickness - width).abs() > eps
            || lo < -eps
            || hi > f.length + eps
            || hi - lo < f.length * 0.8
        {
            continue;
        }
        if (lo <= longitudinal[1] + eps && (hi - f.length).abs() <= eps && longitudinal[0] <= eps)
            || (lo.abs() <= eps && hi >= longitudinal[0] - eps && longitudinal[1] >= f.length - eps)
        {
            return Some(ids);
        }
    }
    None
}

fn anchored(flank: &Flank, body: &Wall) -> bool {
    let f = Frame::new(&flank.wall);
    let b = Frame::new(body);
    if body.curve.is_some() || body.thickness <= 0. || b.length <= 0. {
        return false;
    }
    let pen = Point::new(-b.axis.y, b.axis.x) * b.half;
    let corners = [
        body.start + pen,
        body.start - pen,
        body.end + pen,
        body.end - pen,
    ];
    let base = f.local(flank.base).0;
    let eps = super::super::roundoff(&flank.wall).max(super::super::roundoff(body));
    let local = corners.map(|p| f.local(p));
    let x0 = local.iter().map(|p| p.0).fold(f64::INFINITY, f64::min);
    let x1 = local.iter().map(|p| p.0).fold(f64::NEG_INFINITY, f64::max);
    let y0 = local.iter().map(|p| p.1).fold(f64::INFINITY, f64::min);
    let y1 = local.iter().map(|p| p.1).fold(f64::NEG_INFINITY, f64::max);
    (b.axis.dot(f.axis).abs() <= 1e-5 || b.axis.cross(f.axis).abs() <= 1e-5)
        && ((base - x0).abs() <= eps || (base - x1).abs() <= eps)
        && y1 >= -f.half - eps
        && y0 <= f.half + eps
}

fn other_jamb(flank: &Flank, opening: &Opening, body: &Wall) -> bool {
    if body.curve.is_some() || body.thickness <= 0. {
        return false;
    }
    let f = Frame::new(&flank.wall);
    let eps = super::super::roundoff(body).max(super::super::roundoff(&flank.wall));
    let [a, b] = [opening.start, opening.end].map(|p| f.local(p).0);
    let [u, v] = [body.start, body.end].map(|p| f.local(p));
    if u.1.abs().max(v.1.abs()) > flank.wall.thickness * 0.025
        || (u.1 - v.1).abs() > eps
        || (body.thickness - flank.wall.thickness).abs() > flank.wall.thickness * 0.1
    {
        return false;
    }
    let inset = if flank.side == 0 {
        u.0.min(v.0) - a.max(b)
    } else {
        a.min(b) - u.0.max(v.0)
    };
    (-eps..=0.85 * flank.wall.thickness).contains(&inset)
}

/// A long core can be split into a normal authored tile and one terminal
/// clipped hatch section. Both pieces must explain one finite observation.
fn continuations(
    page: &PageScan,
    decisions: &[WallDecision],
    ready: &[Wall],
    tiles: &[(&insulation::MaterialTile, Wall)],
    ink: &HashMap<&str, Vec<Rail<'_>>>,
) -> Vec<(Wall, WallDecision)> {
    let mut candidates = vec![];
    for (observed, d) in page.walls.iter().zip(decisions) {
        if d.status != WallStatus::Review
            || d.reason != WallReason::IsolatedWall
            || observed.curve.is_some()
            || observed.body != BodyEvidence::ParallelFaces
        {
            continue;
        }
        let f = Frame::new(observed);
        let eps = super::super::roundoff(observed);
        if f.length < 5. * observed.thickness
            || ready
                .iter()
                .any(|w| overlap::parallel_bodies_overlap(observed, w))
            || page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(observed, o))
        {
            continue;
        }
        let mut options = vec![];
        for (tile, tile_wall) in tiles {
            let [a, b] = [tile_wall.start, tile_wall.end].map(|p| f.local(p));
            let lo = a.0.min(b.0);
            let hi = a.0.max(b.0);
            if (observed.thickness - tile_wall.thickness).abs() > eps
                || a.1.abs().max(b.1.abs()) > eps
                || hi - lo < 0.8 * f.length
            {
                continue;
            }
            let (from, to) =
                if lo.abs() <= eps && (eps..=observed.thickness).contains(&(f.length - hi)) {
                    (hi, f.length)
                } else if (hi - f.length).abs() <= eps && (eps..=observed.thickness).contains(&lo) {
                    (0., lo)
                } else {
                    continue;
                };
            let bars = &ink[tile.layer.as_deref().expect("checked wall layer")];
            let observed_rails: Vec<_> = bars
                .iter()
                .filter(|r| observed.source_ids.iter().any(|id| id == r.id))
                .map(|r| Rail {
                    id: r.id,
                    ends: r.ends,
                    stroked: r.stroked,
                })
                .collect();
            let (l, left) = rail(&f, &observed_rails, 0., 1., -f.half, eps);
            let (r, right) = rail(&f, &observed_rails, 0., 1., f.half, eps);
            if (l - f.length).abs().max((r - f.length).abs()) > eps
                || left.len() < 2
                || right.len() < 2
                || left
                    .iter()
                    .any(|a| right.iter().any(|b| draw_path(a) == draw_path(b)))
            {
                continue;
            }
            let mut tail = observed.clone();
            tail.start = f.origin + f.axis * from;
            tail.end = f.origin + f.axis * to;
            let Some(hatch) = paint(&Frame::new(&tail), bars, tile, tiles) else {
                continue;
            };
            let mut wall = observed.clone();
            wall.id = format!(
                "interior-core:{}:continued:{}",
                page.page_number,
                tile_wall.id.rsplit(':').next().expect("derived tile index")
            );
            wall.source_ids = left
                .into_iter()
                .chain(right)
                .chain(hatch)
                .collect::<BTreeSet<_>>()
                .into_iter()
                .map(str::to_owned)
                .collect();
            let mut support_source_ids = wall.source_ids.clone();
            support_source_ids.push(tile.id.clone());
            support_source_ids.sort();
            let decision = WallDecision {
                wall_id: wall.id.clone(),
                status: WallStatus::Ready,
                reason: WallReason::SourceBoundedInteriorCore,
                component_id: format!("p{}:{}", wall.page, wall.id),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![observed.id.clone()],
                support_source_ids,
                opening_ids: vec![],
                room_ids: vec![],
                placement_extent: None,
            };
            options.push((wall, decision));
        }
        if options.len() == 1 {
            candidates.extend(options);
        }
    }
    let mut rejected = vec![false; candidates.len()];
    for (i, (a, _)) in candidates.iter().enumerate() {
        for (j, (b, _)) in candidates.iter().enumerate().skip(i + 1) {
            if a.id == b.id || overlap::parallel_bodies_overlap(a, b) {
                rejected[i] = true;
                rejected[j] = true;
            }
        }
    }
    candidates
        .into_iter()
        .zip(rejected)
        .filter_map(|(c, conflict)| (!conflict).then_some(c))
        .collect()
}

pub(super) fn append(page: &mut PageScan, decisions: &mut Vec<WallDecision>) {
    if page.material_tiles.is_empty() {
        return;
    }
    let doors:Vec<_>=page.openings.iter().enumerate().filter(|(_,o)| {
        o.kind==OpeningKind::Door && o.evidence==OpeningEvidence::FiniteJambSwing
            && matches!(o.swing_observations.as_slice(),[s] if !s.closed_leaf_source_ids.is_empty() && !s.open_leaf_source_ids.is_empty())
    }).collect();
    if doors.is_empty()
        || doors
            .len()
            .saturating_mul(page.material_tiles.len())
            .saturating_mul(page.primitives.len())
            > MAX_WORK
    {
        return;
    }
    let mut ink: HashMap<&str, Vec<Rail<'_>>> = HashMap::new();
    for source in &page.primitives {
        let Some(layer) = source
            .layer
            .as_deref()
            .filter(|name| super::interior::interior_layer(name))
        else {
            continue;
        };
        if source.layer_category_override.is_some()
            && source.layer_kind() != crate::model::LayerKind::Wall
        {
            continue;
        }
        if let Shape::Line { start, end } = source.shape {
            ink.entry(layer).or_default().push(Rail {
                id: &source.id,
                ends: [start, end],
                stroked: source.stroke_width.is_some(),
            });
        }
    }
    let tiles: Vec<_> = page
        .material_tiles
        .iter()
        .enumerate()
        .filter_map(|(i, tile)| {
            ink.get(tile.layer.as_deref()?)?;
            Some((tile, batt_body(tile, page.page_number, i, 1.)?))
        })
        .collect();
    let mut ready: Vec<Wall> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| effective_wall(w, Some(d)).into_owned())
        .collect();
    let linings: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .filter(|(_, d)| d.reason == WallReason::SourceBoundedWallLining)
        .map(|(w, _)| w.clone())
        .collect();
    for (wall, decision) in continuations(page, decisions, &ready, &tiles, &ink) {
        if linings
            .iter()
            .any(|w| overlap::parallel_bodies_overlap(&wall, w))
        {
            continue;
        }
        page.semantics
            .routing
            .push(crate::semantics::wall_route(&wall));
        page.walls.push(wall.clone());
        decisions.push(decision);
        ready.push(wall);
    }
    let mut proposals = vec![];
    for (index, door) in doors {
        let axis = (door.end - door.start).unit();
        let reach = door.end.distance(door.start);
        if proposals.len() > 128 || reach <= 0. {
            break;
        }
        for (tile, base) in &tiles {
            let f = Frame::new(base);
            let eps = super::super::roundoff(base);
            let [a, b] = [door.start, door.end].map(|p| f.local(p));
            let swing = &door.swing_observations[0];
            let [c, d] = [swing.hinge, swing.closed_tip].map(|p| f.local(p));
            let gauge = base.thickness;
            if axis.cross(f.axis).abs() > 1e-5
                || a.1.abs().max(b.1.abs()) > gauge * 0.85
                || c.1.abs().max(d.1.abs()) > gauge * 0.65 + swing.leaf_tolerance
                || !(3. * gauge..=20. * gauge).contains(&reach)
            {
                continue;
            }
            let bars = &ink[tile.layer.as_deref().expect("checked wall layer")];
            for (side, (at, direction)) in [(a.0.min(b.0), -1.), (a.0.max(b.0), 1.)]
                .into_iter()
                .enumerate()
            {
                let mut planes = vec![];
                for s in bars.iter().filter(|s| s.stroked) {
                    let [u, v] = s.ends.map(|p| f.local(p));
                    let offset = (u.0 - at) * direction;
                    if (-eps..=gauge * 0.85).contains(&offset)
                        && (u.0 - v.0).abs() <= eps
                        && u.1.abs().max(v.1.abs()) <= f.half + eps
                        && (u.1 - v.1).abs() > eps
                    {
                        planes.push(u.0);
                    }
                }
                planes.sort_by(f64::total_cmp);
                planes.dedup_by(|a, b| (*a - *b).abs() <= eps);
                for plane in planes {
                    let Some(mut cap_sources) = cap(&f, bars, plane, eps) else {
                        continue;
                    };
                    let (l, left) = rail(&f, bars, plane, direction, -f.half, eps);
                    let (r, right) = rail(&f, bars, plane, direction, f.half, eps);
                    let len = l.min(r);
                    if !(0.35 * gauge..=1.55 * gauge).contains(&len) {
                        continue;
                    }
                    let terminal = f.origin + f.axis * (plane + len * direction);
                    let jamb = f.origin + f.axis * plane;
                    let (start, end) = if side == 0 {
                        (terminal, jamb)
                    } else {
                        (jamb, terminal)
                    };
                    let mut wall = base.clone();
                    wall.start = start;
                    wall.end = end;
                    if ready
                        .iter()
                        .chain(&linings)
                        .any(|w| overlap::parallel_bodies_overlap(&wall, w))
                        || page
                            .openings
                            .iter()
                            .any(|o| overlap::aperture_crosses(&wall, o))
                    {
                        continue;
                    }
                    let Some(hatch) = paint(&Frame::new(&wall), bars, tile, &tiles) else {
                        continue;
                    };
                    cap_sources.extend(left);
                    cap_sources.extend(right);
                    cap_sources.extend(hatch);
                    wall.source_ids = cap_sources
                        .into_iter()
                        .collect::<BTreeSet<_>>()
                        .into_iter()
                        .map(str::to_owned)
                        .collect();
                    wall.id = format!("{PREFIX}{}:{}:{side}", page.page_number, index + 1);
                    if !proposals.iter().any(|p: &Flank| {
                        p.wall.id == wall.id
                            && p.wall.start.distance(wall.start) <= eps
                            && p.wall.end.distance(wall.end) <= eps
                            && (p.wall.thickness - wall.thickness).abs() <= eps
                    }) {
                        proposals.push(Flank {
                            wall,
                            base: terminal,
                            door: index,
                            side,
                            tile: tile.id.clone(),
                        });
                    }
                }
            }
        }
    }
    // Competing shapes at one jamb or overlapping physical bodies are review
    // evidence. Do not choose based on which source or door was listed first.
    let mut rejected = vec![false; proposals.len()];
    for (i, a) in proposals.iter().enumerate() {
        for (j, b) in proposals.iter().enumerate().skip(i + 1) {
            if a.wall.id == b.wall.id || overlap::parallel_bodies_overlap(&a.wall, &b.wall) {
                rejected[i] = true;
                rejected[j] = true;
            }
        }
    }
    let eligible: Vec<_> = proposals
        .iter()
        .enumerate()
        .map(|(i, a)| {
            !rejected[i]
                && (ready
                    .iter()
                    .any(|b| other_jamb(a, &page.openings[a.door], b))
                    || proposals.iter().enumerate().any(|(j, b)| {
                        !rejected[j]
                            && b.door == a.door
                            && b.side != a.side
                            && other_jamb(a, &page.openings[a.door], &b.wall)
                    }))
        })
        .collect();
    let mut admitted = ready;
    let mut consumed = vec![false; proposals.len()];
    for _ in 0..proposals.len() {
        let previous = admitted.len();
        for (i, proposal) in proposals.iter().enumerate() {
            if consumed[i] || !eligible[i] {
                continue;
            }
            let anchor = if let Some(anchor) = admitted.iter().find(|w| anchored(proposal, w)) {
                anchor.id.clone()
            } else {
                let Some(patches) = interior_corners::jamb_shoulder(
                    page,
                    &proposal.wall,
                    proposal.base,
                    &admitted,
                    &tiles,
                    &ink,
                ) else {
                    continue;
                };
                if patches
                    .iter()
                    .any(|patch| linings.iter().any(|w| overlap_rect(w, &patch.wall)))
                {
                    continue;
                }
                let anchor = patches[0].wall.id.clone();
                for patch in patches {
                    let (wall, decision) = patch.into_decision();
                    page.semantics
                        .routing
                        .push(crate::semantics::wall_route(&wall));
                    admitted.push(wall.clone());
                    page.walls.push(wall);
                    decisions.push(decision);
                }
                anchor
            };
            let wall = proposal.wall.clone();
            let mut support_source_ids = wall.source_ids.clone();
            support_source_ids.push(proposal.tile.clone());
            support_source_ids.sort();
            decisions.push(WallDecision {
                wall_id: wall.id.clone(),
                status: WallStatus::Ready,
                reason: WallReason::SourceBoundedInteriorCore,
                component_id: format!("p{}:{}", wall.page, wall.id),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![anchor],
                support_source_ids,
                opening_ids: vec![page.openings[proposal.door].id.clone()],
                room_ids: vec![],
                placement_extent: None,
            });
            page.semantics
                .routing
                .push(crate::semantics::wall_route(&wall));
            page.walls.push(wall.clone());
            admitted.push(wall);
            consumed[i] = true;
        }
        if admitted.len() == previous {
            break;
        }
    }
    for patch in interior_corners::mitered_ends(page, &admitted, &tiles, &ink) {
        if linings.iter().any(|w| overlap_rect(w, &patch.wall)) {
            continue;
        }
        let (wall, decision) = patch.into_decision();
        page.semantics
            .routing
            .push(crate::semantics::wall_route(&wall));
        page.walls.push(wall);
        decisions.push(decision);
    }
}
