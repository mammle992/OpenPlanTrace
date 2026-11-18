//! Recover a finite batt core whose two original faces were claimed by different
//! wall observations. The signed doorway and the enclosing skin must end at its jamb.
use super::{
    WallDecision, WallReason, WallStatus,
    insulation::{self, MaterialTile},
    material::{self, EdgeKey, edge_key},
    overlap, roundoff,
};
use crate::model::{
    BodyEvidence, LayerKind, Opening, OpeningEvidence, OpeningKind, Options, Page, PageScan, Point,
    Primitive, Shape, Wall,
};
use std::collections::{HashMap, HashSet};

const RECOVERED: &str = "jamb-core:";

pub(crate) fn is_recovered(wall: &Wall) -> bool {
    wall.id.starts_with(RECOVERED)
}

pub(super) struct Proof {
    pub host: String,
    pub sources: Vec<String>,
}

struct Ink<'a> {
    by_id: HashMap<&'a str, &'a Primitive>,
    faces: HashMap<(&'a str, EdgeKey), &'a str>,
}

impl<'a> Ink<'a> {
    fn new(primitives: &'a [Primitive], tiles: &[MaterialTile], needed: &HashSet<&str>) -> Self {
        let edges: HashSet<_> = tiles
            .iter()
            .flat_map(|t| {
                (0..4).map(move |i| {
                    (
                        t.layer.as_deref(),
                        edge_key(t.outline[i], t.outline[(i + 1) % 4]),
                    )
                })
            })
            .collect();
        let mut names = HashMap::new();
        let mut faces = HashMap::new();
        let mut by_id = HashMap::with_capacity(needed.len());
        for p in primitives {
            if needed.contains(p.id.as_str()) {
                by_id.insert(p.id.as_str(), p);
            }
            let Shape::Line { start, end } = p.shape else {
                continue;
            };
            let Some(layer) = p.layer.as_deref() else {
                continue;
            };
            let key = edge_key(start, end);
            if !edges.contains(&(Some(layer), key)) {
                continue;
            }
            let structural = if p.layer_category_override.is_some() {
                p.layer_kind() == LayerKind::Wall
            } else {
                *names
                    .entry(layer)
                    .or_insert_with(|| material::wall_name(layer).1)
            };
            if structural {
                faces.entry((layer, key)).or_insert(p.id.as_str());
            }
        }
        Self { by_id, faces }
    }
}

fn drawn_core(page: u32, tile: &MaterialTile, ink: &Ink<'_>) -> Option<Wall> {
    let layer = tile.layer.as_deref()?;
    let mut corners = tile.outline;
    if (corners[1] - corners[0]).dot(tile.repeat).abs()
        < (corners[3] - corners[0]).dot(tile.repeat).abs()
    {
        corners.rotate_left(1);
    }
    let [a, b, c, d] = corners;
    let mut faces = vec![
        ink.faces.get(&(layer, edge_key(a, b)))?.to_string(),
        ink.faces.get(&(layer, edge_key(d, c)))?.to_string(),
    ];
    faces.sort();
    let core = Wall {
        id: format!("{RECOVERED}{}", tile.id),
        page,
        start: (a + d) * 0.5,
        end: (b + c) * 0.5,
        thickness: a.distance(d),
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: faces,
        requires_review: true,
    };
    insulation::aligned_row(&core, tile).then_some(core)
}

/// Check the face ends of the wider host in a coordinate frame facing the door.
/// The far ends may stagger by the thickness of the measured skin.
fn enclosing_faces<'a>(
    host: &'a Wall,
    ink: &Ink<'_>,
    layer: &str,
    at: Point,
    axis: Point,
    length: f64,
    skin: f64,
    eps: f64,
) -> Option<[&'a str; 2]> {
    let mut sides = [None, None];
    for id in &host.source_ids {
        let source = ink.by_id.get(id.as_str())?;
        if source.layer.as_deref() != Some(layer) {
            continue;
        }
        let Shape::Line { start, end } = source.shape else {
            continue;
        };
        let (a, b) = (start - at, end - at);
        let v = axis.cross(a);
        let x = a.dot(axis);
        let y = b.dot(axis);
        if (v.abs() - host.thickness / 2.).abs() <= eps
            && (axis.cross(b) - v).abs() <= eps
            && x.max(y).abs() <= eps
            && x.min(y) <= -length + skin + eps
        {
            sides[usize::from(v > 0.)].get_or_insert(id.as_str());
        }
    }
    Some([sides[0]?, sides[1]?])
}

fn witnesses(
    core: &Wall,
    host: &Wall,
    door: &Opening,
    tile: &MaterialTile,
    ink: &Ink<'_>,
) -> Option<Proof> {
    let eps = roundoff(host)
        .max(roundoff(core))
        .max(core.thickness * 1e-5);
    let skin = (host.thickness - core.thickness) * 0.5;
    if host.body != BodyEvidence::ParallelFaces
        || host.curve.is_some()
        || skin <= eps
        || skin >= core.thickness / 2.
    {
        return None;
    }
    let host_axis = (host.end - host.start).unit();
    let host_len = host.start.distance(host.end);
    for p in [core.start, core.end, door.start, door.end] {
        let d = p - host.start;
        if host_axis.cross(d).abs() > eps
            || d.dot(host_axis) < -skin - eps
            || d.dot(host_axis) > host_len + skin + eps
        {
            return None;
        }
    }
    // The aperture must lie wholly beyond the original paint and both face ends.
    let distance_to_door = |p: Point| p.distance(door.start).min(p.distance(door.end));
    let (at, distal) = if distance_to_door(core.start) < distance_to_door(core.end) {
        (core.start, core.end)
    } else {
        (core.end, core.start)
    };
    let axis = (at - distal).unit();
    let gap = (door.start - at).dot(axis).min((door.end - at).dot(axis));
    if gap < -eps || gap > host.thickness / 2. + eps {
        return None;
    }
    let swing = door.swing_observations.iter().find(|s| {
        door.source_ids.contains(&s.arc_source_id)
            && ink
                .by_id
                .get(s.arc_source_id.as_str())
                .is_some_and(|p| matches!(p.shape, Shape::Arc { .. }))
            && !s.open_leaf_source_ids.is_empty()
            && s.open_leaf_source_ids
                .iter()
                .all(|id| door.source_ids.contains(id) && ink.by_id.contains_key(id.as_str()))
    })?;
    let layer = tile.layer.as_deref()?;
    let jamb = door.source_ids.iter().find(|id| {
        let Some(p) = ink.by_id.get(id.as_str()) else {
            return false;
        };
        if p.layer.as_deref() != Some(layer) && p.layer_kind() != LayerKind::Door {
            return false;
        }
        let Shape::Line { start, end } = p.shape else {
            return false;
        };
        let (a, b) = (start - at, end - at);
        let (l, r) = (axis.cross(a), axis.cross(b));
        a.dot(axis).abs() <= eps
            && b.dot(axis).abs() <= eps
            && l.min(r) <= eps
            && l.max(r) >= -eps
            && ((l.abs() - core.thickness / 2.).abs() <= eps
                || (r.abs() - core.thickness / 2.).abs() <= eps)
    })?;
    let outer = enclosing_faces(host, ink, layer, at, axis, at.distance(distal), skin, eps)?;
    let mut sources = core.source_ids.clone();
    sources.extend(outer.map(str::to_owned));
    sources.extend([jamb.clone(), tile.id.clone(), swing.arc_source_id.clone()]);
    sources.extend(swing.open_leaf_source_ids.iter().cloned());
    sources.sort();
    sources.dedup();
    Some(Proof {
        host: host.id.clone(),
        sources,
    })
}

fn observations(
    page: u32,
    primitives: &[Primitive],
    tiles: &[MaterialTile],
    walls: &[Wall],
    openings: &[Opening],
) -> Result<Vec<(Wall, Proof)>, String> {
    if tiles.is_empty() {
        return Ok(vec![]);
    }
    let doors: Vec<_> = openings
        .iter()
        .filter(|o| {
            o.kind == OpeningKind::Door
                && o.evidence == OpeningEvidence::FiniteJambSwing
                && !o.swing_observations.is_empty()
        })
        .collect();
    if doors.is_empty() {
        return Ok(vec![]);
    }
    let by_id: HashMap<_, _> = walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let needed = doors
        .iter()
        .flat_map(|d| {
            d.source_ids.iter().chain(
                by_id
                    .get(d.host_wall_id.as_str())
                    .into_iter()
                    .flat_map(|w| w.source_ids.iter()),
            )
        })
        .map(String::as_str)
        .collect();
    let ink = Ink::new(primitives, tiles, &needed);
    let mut work = 8_000_000usize;
    let mut result = Vec::new();
    for tile in tiles {
        let Some(core) = drawn_core(page, tile, &ink) else {
            continue;
        };
        work = work
            .checked_sub(doors.len())
            .ok_or("jamb comparison budget exceeded")?;
        let mut matches = doors.iter().filter_map(|d| {
            let host = by_id.get(d.host_wall_id.as_str())?;
            witnesses(&core, host, d, tile, &ink)
        });
        let Some(proof) = matches.next() else {
            continue;
        };
        if matches.next().is_some() {
            continue;
        }
        work = work
            .checked_sub(openings.len())
            .ok_or("jamb aperture budget exceeded")?;
        if !openings.iter().any(|o| overlap::aperture_crosses(&core, o)) {
            result.push((core, proof));
        }
    }
    Ok(result)
}

fn same_span(a: &Wall, b: &Wall) -> bool {
    let eps = roundoff(a).max(roundoff(b)).max(a.thickness * 1e-5);
    b.curve.is_none()
        && (a.thickness - b.thickness).abs() <= eps
        && ((a.start.distance(b.start) <= eps && a.end.distance(b.end) <= eps)
            || (a.start.distance(b.end) <= eps && a.end.distance(b.start) <= eps))
}

/// Append after opening detection so the signed host identity remains stable.
pub(crate) fn recover(
    page: &Page,
    walls: &mut Vec<Wall>,
    openings: &[Opening],
    opts: &Options,
) -> Result<(), String> {
    let mut work = 8_000_000usize;
    for (core, _) in observations(
        page.number,
        &page.primitives,
        &page.material_tiles,
        walls,
        openings,
    )? {
        work = work
            .checked_sub(walls.len())
            .ok_or("jamb wall comparison budget exceeded")?;
        if core.start.distance(core.end) >= opts.min_wall_length
            && core.thickness >= opts.min_wall_pair_separation
            && core.thickness <= opts.max_wall_pair_separation
            && !walls.iter().any(|w| same_span(&core, w))
        {
            walls.push(core);
        }
    }
    Ok(())
}

pub(super) fn proofs(page: &PageScan) -> Result<HashMap<String, Proof>, String> {
    let mut result = HashMap::new();
    let mut work = 8_000_000usize;
    for (core, proof) in observations(
        page.page_number,
        &page.primitives,
        &page.material_tiles,
        &page.walls,
        &page.openings,
    )? {
        work = work
            .checked_sub(page.walls.len())
            .ok_or("jamb wall comparison budget exceeded")?;
        if let Some(w) = page.walls.iter().find(|w| {
            same_span(&core, w)
                && w.body == BodyEvidence::ParallelFaces
                && core.source_ids.iter().all(|id| w.source_ids.contains(id))
        }) {
            if is_recovered(w) {
                let eps = roundoff(&core);
                if w.id != core.id
                    || w.start.distance(core.start) > eps
                    || w.end.distance(core.end) > eps
                    || (w.thickness - core.thickness).abs() > eps
                    || w.source_ids != core.source_ids
                    || !w.requires_review
                {
                    return Err(
                        "recovered jamb disagrees with its original faces or material".into(),
                    );
                }
            }
            result.insert(w.id.clone(), proof);
        }
    }
    if page
        .walls
        .iter()
        .any(|w| is_recovered(w) && !result.contains_key(&w.id))
    {
        return Err("recovered jamb lacks its original material, faces or signed opening".into());
    }
    Ok(result)
}

/// Give existing ready observations priority when two parallel barriers would
/// claim the same solid material. Perpendicular wall junctions remain valid.
pub(super) fn check_overlaps(page: &PageScan, decisions: &mut [WallDecision]) {
    let is_new = |d: &WallDecision| {
        d.status == WallStatus::Ready && d.reason == WallReason::InsulatedDoorJamb
    };
    if !decisions.iter().any(is_new) {
        return;
    }
    let mut ready: Vec<_> = decisions
        .iter()
        .zip(&page.walls)
        .filter(|(d, _)| d.status == WallStatus::Ready && !is_new(d))
        .map(|(_, w)| w)
        .collect();
    for (d, w) in decisions
        .iter_mut()
        .zip(&page.walls)
        .filter(|(d, _)| is_new(d))
    {
        if ready
            .iter()
            .any(|other| overlap::parallel_bodies_overlap(w, other))
        {
            d.status = WallStatus::Review;
            d.reason = WallReason::OverlappingWallAxes;
        } else {
            ready.push(w);
        }
    }
}
