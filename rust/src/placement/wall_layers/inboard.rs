//! A perpendicular batt cap can prove a lining and a finite junction together.
use super::{Frame, WallLayer, Witnesses, band, overlap, roundoff};
use crate::{
    model::{LayerKind, PageScan, Point, Primitive, Shape, Wall, on_segment},
    placement::{WallDecision, WallReason, WallStatus, effective_wall, insulation::MaterialTile},
};

pub(crate) struct JunctionMaterial<'a> {
    pub owner_id: &'a str,
    pub target_id: &'a str,
    pub outline: [Point; 4],
    /// Contiguous material path through the attached lining and cap. The
    /// export exposes only the added cap, not the already exported lining.
    pub support_outline: [Point; 4],
    pub source_ids: Vec<&'a str>,
}

fn corners(a: Point, b: Point, offset: Point) -> [Point; 4] {
    [a + offset, b + offset, b - offset, a - offset]
}

fn on_layer(p: &Primitive, layer: &str) -> bool {
    p.layer.as_deref() == Some(layer)
        && (p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall)
}

/// Accept only the two exact finite sides and the full receiving face.
fn outline_rails<'a>(
    page: &'a PageScan,
    layer: &str,
    [a, b, c, d]: [Point; 4],
    eps: f64,
) -> Option<[&'a str; 3]> {
    let mut rails = [None; 3];
    for p in &page.primitives {
        if !on_layer(p, layer) || !p.stroke_width.is_some_and(|w| w > 0.) {
            continue;
        }
        let Shape::Line { start, end } = p.shape else {
            continue;
        };
        for (slot, (u, v)) in rails.iter_mut().zip([(a, b), (c, d), (b, c)]) {
            if (start.distance(u).max(end.distance(v)) <= eps
                || start.distance(v).max(end.distance(u)) <= eps)
                && slot.is_none()
            {
                *slot = Some(p.id.as_str());
            }
        }
    }
    let [Some(a), Some(b), Some(c)] = rails else {
        return None;
    };
    (a != b && a != c && b != c).then_some([a, b, c])
}

fn continuing_batt<'a>(
    page: &'a PageScan,
    tile: &MaterialTile,
    frame: &Frame,
) -> Option<Vec<&'a str>> {
    let layer = tile
        .layer
        .as_deref()
        .filter(|s| crate::layers::is_named_wall(s))?;
    let repeat_sq = tile.repeat.dot(tile.repeat);
    let eps = frame.half * 0.001;
    if repeat_sq <= eps * eps {
        return None;
    }
    let mut proof = Vec::new();
    let mut slope_signs = [false; 2];
    let mut along = [f64::INFINITY, f64::NEG_INFINITY];
    let mut across = along;
    for p in &page.primitives {
        if !on_layer(p, layer) {
            continue;
        }
        let Shape::Line { start, end } = p.shape else {
            continue;
        };
        let [a, b] = [frame.at(start), frame.at(end)];
        let slope = (a[0] - b[0]) * (a[1] - b[1]);
        if a[0].min(b[0]) < -eps
            || a[0].max(b[0]) > frame.length + eps
            || a[1].abs().max(b[1].abs()) > frame.half + eps
            || (a[0] - b[0]).abs().min((a[1] - b[1]).abs()) <= 2. * eps
        {
            continue;
        }
        let cell = ((((start + end) * 0.5 - tile.origin).dot(tile.repeat)) / repeat_sq).floor();
        let matches = tile.strokes.iter().any(|s| {
            [-1., 0., 1.].into_iter().any(|offset| {
                let [u, v] = s.map(|pt| {
                    tile.origin + tile.repeat * (cell + offset + pt.x) + tile.across * pt.y
                });
                on_segment(start, u, v).0.distance(start) <= eps
                    && on_segment(end, u, v).0.distance(end) <= eps
            })
        });
        if matches {
            proof.push(p.id.as_str());
            slope_signs[usize::from(slope > 0.)] = true;
            along[0] = along[0].min(a[0]).min(b[0]);
            along[1] = along[1].max(a[0]).max(b[0]);
            across[0] = across[0].min(a[1]).min(b[1]);
            across[1] = across[1].max(a[1]).max(b[1]);
        }
    }
    (proof.len() >= 3
        && slope_signs.iter().all(|&found| found)
        && along[0] <= eps
        && along[1] >= frame.length - eps
        && across[1] - across[0] >= 1.7 * frame.half)
        .then_some(proof)
}

fn contact<'a>(
    page: &'a PageScan,
    owner: &'a str,
    layer: &Wall,
    layer_name: &str,
    observed: &'a Wall,
    target: &Wall,
    d: &WallDecision,
) -> Option<JunctionMaterial<'a>> {
    let assembly = Frame::new(layer)?;
    let stem = Frame::new(target)?;
    if observed.id == owner || assembly.axis.dot(stem.axis).abs() > 1e-8 {
        return None;
    }
    let eps = roundoff(layer).max(roundoff(target));
    for (tip, other, tile_end) in [
        (target.start, target.end, 0.),
        (target.end, target.start, stem.length),
    ] {
        let [t, y] = assembly.at(tip);
        let travel = (tip - other).unit();
        let facing = Point::new(-assembly.axis.y, assembly.axis.x) * y.signum();
        let depth = y.abs() - assembly.half;
        if travel.dot(facing) > -1. + 1e-8
            || !(eps..=stem.half * 0.25).contains(&depth)
            || t - stem.half < -eps
            || t + stem.half > assembly.length + eps
        {
            continue;
        }
        let face = tip + travel * depth;
        let offset = Point::new(-stem.axis.y, stem.axis.x) * stem.half;
        let outline = corners(tip, face, offset);
        let cap = Wall {
            start: tip,
            end: face,
            ..target.clone()
        };
        if page
            .openings
            .iter()
            .any(|o| overlap::aperture_crosses(&cap, o))
        {
            continue;
        }
        let Some(rails) = outline_rails(page, layer_name, outline, eps) else {
            continue;
        };
        let Some(cap_frame) = Frame::new(&cap) else {
            continue;
        };
        for tile in page
            .material_tiles
            .iter()
            .filter(|t| d.support_source_ids.contains(&t.id))
        {
            let w = band(&stem, tile.outline);
            if !crate::placement::insulation::aligned_row(target, tile)
                || (w.across[0] + stem.half).abs() > eps
                || (w.across[1] - stem.half).abs() > eps
                || w.along.iter().all(|&s| (s - tile_end).abs() > eps)
            {
                continue;
            }
            let Some(mut source_ids) = continuing_batt(page, tile, &cap_frame) else {
                continue;
            };
            source_ids.extend(rails);
            source_ids.push(&tile.id);
            source_ids.sort_unstable();
            source_ids.dedup();
            return Some(JunctionMaterial {
                owner_id: owner,
                target_id: &observed.id,
                outline,
                support_outline: corners(tip, face + travel * layer.thickness, offset),
                source_ids,
            });
        }
    }
    None
}

fn unique_contact<'a>(
    page: &'a PageScan,
    owner_id: &'a str,
    footprint: &Wall,
    layer_name: &str,
    decisions: &[WallDecision],
) -> Option<JunctionMaterial<'a>> {
    let mut contacts = page.walls.iter().zip(decisions).filter_map(|(w, d)| {
        (d.status == WallStatus::Ready).then_some(())?;
        let target = effective_wall(w, Some(d));
        contact(page, owner_id, footprint, layer_name, w, &target, d)
    });
    let first = contacts.next()?;
    contacts.next().is_none().then_some(first)
}

pub(super) fn recover(page: &PageScan, decisions: &mut [WallDecision], witnesses: &Witnesses<'_>) {
    if page.walls.len().saturating_mul(page.material_tiles.len()) > 500_000 {
        return;
    }
    let mut recovered = Vec::new();
    for (index, (lining, ld)) in page.walls.iter().zip(decisions.iter()).enumerate() {
        if ld.status != WallStatus::Review
            || !matches!(
                ld.reason,
                WallReason::IsolatedWall | WallReason::ConnectedWallNeedsReview
            )
            || ld.placement_extent.is_some()
            || !ld.opening_ids.is_empty()
            || page
                .openings
                .iter()
                .any(|o| o.host_wall_ids.contains(&lining.id))
        {
            continue;
        }
        let Some(frame) = Frame::new(lining) else {
            continue;
        };
        let eps = roundoff(lining);
        let mut tiles = page.material_tiles.iter().filter_map(|tile| {
            let layer = tile
                .layer
                .as_deref()
                .filter(|s| crate::layers::is_named_wall(s))?;
            let bounds = band(&frame, tile.outline);
            // A cap or aperture frame can hide part of a longer authored
            // tile. Recover only the observed liner, within that single tile.
            (bounds.along[0] <= eps
                && bounds.along[1] >= frame.length - eps
                && (bounds.across[0] + frame.half).abs() <= eps
                && (bounds.across[1] - frame.half).abs() <= eps
                && crate::placement::insulation::aligned_row(lining, tile))
            .then_some((tile, layer))
        });
        let Some((tile, layer)) = tiles.next() else {
            continue;
        };
        if tiles.next().is_some() {
            continue;
        }
        let mut cited = ld.clone();
        cited.support_source_ids.push(tile.id.clone());
        let mut owners = page
            .walls
            .iter()
            .zip(decisions.iter())
            .filter_map(|(w, d)| {
                if d.status != WallStatus::Ready
                    || !matches!(
                        d.reason,
                        WallReason::TiledInsulationBetweenFaces
                            | WallReason::SourceBoundedVisibleInsulationAssembly
                            | WallReason::SourceBoundedExteriorCore
                    )
                {
                    return None;
                }
                let owner = effective_wall(w, Some(d));
                let mut proof = witnesses.prove(&owner, d, lining, &cited)?;
                let profile = WallLayer {
                    owner_id: &w.id,
                    wall: lining,
                    decision: ld,
                }
                .footprint(&owner);
                if page
                    .openings
                    .iter()
                    .any(|o| overlap::aperture_crosses(&profile, o))
                {
                    return None;
                }
                let cap = unique_contact(page, &w.id, &profile, layer, decisions)?;
                proof.extend(cap.source_ids.into_iter().map(str::to_owned));
                Some((&w.id, proof))
            });
        if let Some((owner, proof)) = owners.next()
            && owners.next().is_none()
        {
            recovered.push((index, owner, proof));
        }
    }
    for (index, owner, proof) in recovered {
        let d = &mut decisions[index];
        d.status = WallStatus::EvidenceOnly;
        d.reason = WallReason::SourceBoundedWallLining;
        d.support_wall_ids = vec![owner.clone()];
        d.support_source_ids.extend(proof);
        d.support_source_ids.sort();
        d.support_source_ids.dedup();
    }
}

pub(crate) fn contacts(page: &PageScan) -> Vec<JunctionMaterial<'_>> {
    let Some(decisions) = &page.placement_decisions else {
        return vec![];
    };
    super::layers(page)
        .filter_map(|layer| {
            let (owner, d) = page
                .walls
                .iter()
                .zip(decisions)
                .find(|(w, d)| w.id == layer.owner_id && d.status == WallStatus::Ready)?;
            let owner = effective_wall(owner, Some(d));
            let proof = &layer.decision.support_source_ids;
            // Identify the liner's own tile, even when the cap cites a tile
            // on the perpendicular wall's separate architectural layer.
            let frame = Frame::new(layer.wall)?;
            let eps = roundoff(layer.wall);
            let layer_name = page.material_tiles.iter().find_map(|tile| {
                let bounds = band(&frame, tile.outline);
                (proof.contains(&tile.id)
                    && (bounds.across[0] + frame.half).abs() <= eps
                    && (bounds.across[1] - frame.half).abs() <= eps)
                    .then_some(tile.layer.as_deref())?
            })?;
            let shape = layer.footprint(&owner);
            let mut cap = unique_contact(page, layer.owner_id, &shape, layer_name, decisions)?;
            // Replay connects only the assemblies whose original support
            // included the cap evidence at selection.
            if !cap
                .source_ids
                .iter()
                .all(|id| proof.iter().any(|p| p == id))
            {
                return None;
            }
            cap.source_ids.extend(proof.iter().map(String::as_str));
            cap.source_ids.sort_unstable();
            cap.source_ids.dedup();
            Some(cap)
        })
        .collect()
}
