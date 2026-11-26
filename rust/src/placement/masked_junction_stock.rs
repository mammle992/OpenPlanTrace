//! Place the independently visible end of a masked stock and sign its original stem.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall,
    exterior_piers::{aperture_guard, beside_ready_wall, corners, overlaps},
    wall_layers,
};
use crate::{
    input::{
        masked_junction_stock::{self, Receipt, Verified},
        recessed_stock,
    },
    model::{BodyEvidence, PageScan, Point, Wall},
    semantics::wall_route,
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashMap},
};

const PREFIX: &str = "masked-junction-stock:";
const REASON: WallReason = WallReason::SourceBoundedMaskedJunctionStock;
const MAX_WORK: usize = 2_000_000;

pub(crate) struct Joint {
    pub wall_ids: [String; 2],
    pub points: [Point; 2],
    pub source_ids: Vec<String>,
}

struct Candidate {
    wall: Wall,
    area: [Point; 4],
    cap_wall_id: String,
    stem_wall_id: String,
    stem_path: [Point; 2],
    joint_sources: Vec<String>,
}

struct Obstacle<'a> {
    host: Option<&'a str>,
    outline: [Point; 4],
}

type Ready<'a> = [(Cow<'a, Wall>, &'a WallDecision)];

pub(crate) fn is_stock(w: &Wall) -> bool {
    w.id.starts_with(PREFIX)
}

fn center([a, b, c, d]: [Point; 4]) -> (Point, Point, f64) {
    ((a + d) * 0.5, (b + c) * 0.5, a.distance(d))
}

fn unique<T>(mut values: impl Iterator<Item = T>) -> Option<T> {
    let a = values.next()?;
    values.next().is_none().then_some(a)
}

fn matching_ready<'a>(
    ready: &'a Ready<'_>,
    reason: WallReason,
    paint: &str,
    outline: [Point; 4],
    eps: f64,
) -> Option<&'a Wall> {
    let (a, b, w) = center(outline);
    unique(ready.iter().filter_map(|(wall, decision)| {
        (decision.reason == reason
            && wall.curve.is_none()
            && (wall.thickness - w).abs() <= eps
            && wall
                .source_ids
                .iter()
                .chain(&decision.support_source_ids)
                .any(|id| id == paint)
            && ((wall.start.distance(a) <= eps && wall.end.distance(b) <= eps)
                || (wall.end.distance(a) <= eps && wall.start.distance(b) <= eps)))
            .then_some(wall.as_ref())
    }))
}

fn occupied<'a>(page: &'a PageScan, ready: &'a Ready<'_>) -> Vec<Obstacle<'a>> {
    let outside = |outline| Obstacle {
        host: None,
        outline,
    };
    let mut occupied = ready
        .iter()
        .map(|(w, _)| {
            let outline = if let Some(c) = w.curve {
                let r = c.radius + w.thickness * 0.5;
                let offset = Point::new(r, 0.);
                corners(c.center - offset, c.center + offset, 2. * r)
            } else {
                corners(w.start, w.end, w.thickness)
            };
            Obstacle {
                host: Some(w.id.as_str()),
                outline,
            }
        })
        .collect::<Vec<_>>();
    let hosts: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    occupied.extend(wall_layers::layers(page).filter_map(|l| {
        let w = hosts.get(l.owner_id)?;
        let layer = l.footprint(w);
        Some(outside(corners(layer.start, layer.end, layer.thickness)))
    }));
    occupied.extend(
        wall_layers::contacts(page)
            .into_iter()
            .map(|c| outside(c.outline)),
    );
    occupied.extend(
        page.objects
            .iter()
            .filter_map(|o| o.footprint.as_ref().map(|p| outside(p.polygon))),
    );
    occupied.extend(
        page.withheld_walls
            .iter()
            .flatten()
            .filter_map(|w| w.furniture_outline.map(outside)),
    );
    occupied.extend(aperture_guard(page).into_iter().map(outside));
    occupied
}

/// A finite logical segment can touch an unrelated boundary, but it may not
/// move through unrelated stock or run through any opening's interior.
fn crosses([a, b]: [Point; 2], [p, q, _, r]: [Point; 4], eps: f64) -> bool {
    let mut t = [0_f64, 1_f64];
    for edge in [q - p, r - p] {
        let length = edge.norm();
        if length <= 2. * eps {
            return false;
        }
        let axis = edge * (1. / length);
        let [u, v] = [a - p, b - p].map(|a| a.dot(axis));
        let [lo, hi] = [eps, length - eps];
        if (v - u).abs() <= eps {
            if u <= lo || u >= hi {
                return false;
            }
            continue;
        }
        let x = (lo - u) / (v - u);
        let y = (hi - u) / (v - u);
        t[0] = t[0].max(x.min(y));
        t[1] = t[1].min(x.max(y));
        if t[1] <= t[0] {
            return false;
        }
    }
    a.distance(b) * (t[1] - t[0]) > eps
}

fn observe(
    page: &PageScan,
    r: &Receipt,
    v: Verified,
    ready: &Ready<'_>,
    occupied: &[Obstacle<'_>],
    ordinal: usize,
) -> Option<Candidate> {
    let (start, end, width) = center(v.outline);
    let eps = width * 1e-4;
    let cap = unique(
        page.capped_stock
            .iter()
            .filter(|p| p.paint_source_id == r.adjacent_paint_source_id),
    )?;
    let stem = unique(
        page.recessed_stock
            .iter()
            .filter(|p| p.paint_source_id == r.stem_paint_source_id),
    )?;
    let cap = matching_ready(
        ready,
        WallReason::SourceBoundedCappedFacade,
        &r.adjacent_paint_source_id,
        cap.outline,
        eps,
    )?;
    let stem_outer = *recessed_stock::verified(page, stem)?.outlines.first()?;
    let stem = matching_ready(
        ready,
        WallReason::SourceBoundedRecessedStock,
        &r.stem_paint_source_id,
        stem_outer,
        eps,
    )?;
    if cap.id == stem.id
        || v.cap_path[0].distance(end) > eps
        || [cap.start, cap.end]
            .iter()
            .all(|p| p.distance(v.cap_path[1]) > eps)
        || [stem.start, stem.end]
            .iter()
            .all(|p| p.distance(v.stem_path[0]) > eps)
        || crate::model::on_segment(v.stem_path[1], start, end)
            .0
            .distance(v.stem_path[1])
            > eps
    {
        return None;
    }
    let id = format!("{PREFIX}{}:{ordinal}", page.page_number);
    let sources = r
        .retained_source_ids()
        .map(str::to_owned)
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    let wall = Wall {
        id,
        page: page.page_number,
        start,
        end,
        thickness: width,
        body: BodyEvidence::ParallelFaces,
        curve: None,
        source_ids: sources,
        requires_review: false,
    };
    if occupied.iter().any(|o| {
        overlaps(v.outline, o.outline, eps)
            || beside_ready_wall(&wall, o.outline)
            || (o.host != Some(cap.id.as_str())
                && o.host != Some(stem.id.as_str())
                && (crosses(v.stem_path, o.outline, eps) || crosses(v.cap_path, o.outline, eps)))
    }) {
        return None;
    }
    Some(Candidate {
        wall,
        area: v.outline,
        cap_wall_id: cap.id.clone(),
        stem_wall_id: stem.id.clone(),
        stem_path: v.stem_path,
        joint_sources: v.joint_source_ids,
    })
}

fn candidates(page: &PageScan) -> Vec<Candidate> {
    if page.masked_junction_stock.is_empty() {
        return vec![];
    }
    let ready = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(w, d)| !is_stock(w) && d.status == WallStatus::Ready)
        .map(|(w, d)| (effective_wall(w, Some(d)), d))
        .collect::<Vec<_>>();
    let occupied = occupied(page, &ready);
    if occupied
        .len()
        .saturating_mul(page.masked_junction_stock.len())
        .saturating_mul(8)
        > MAX_WORK
    {
        return vec![];
    }
    let candidates = page
        .masked_junction_stock
        .iter()
        .enumerate()
        .filter_map(|(i, r)| {
            observe(
                page,
                r,
                masked_junction_stock::verified(page, r)?,
                &ready,
                &occupied,
                i + 1,
            )
        })
        .collect::<Vec<_>>();
    let conflicts = candidates
        .iter()
        .enumerate()
        .map(|(i, a)| {
            candidates.iter().enumerate().any(|(j, b)| {
                i != j
                    && (a.cap_wall_id == b.cap_wall_id
                        || a.stem_wall_id == b.stem_wall_id
                        || overlaps(a.area, b.area, 1e-7)
                        || beside_ready_wall(&a.wall, b.area)
                        || crosses(a.stem_path, b.area, 1e-7))
            })
        })
        .collect::<Vec<_>>();
    candidates
        .into_iter()
        .zip(conflicts)
        .filter_map(|(c, ambiguous)| (!ambiguous).then_some(c))
        .collect()
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    if page.masked_junction_stock.is_empty() {
        return Ok(());
    }
    if page.walls.iter().any(is_stock) {
        return Err("duplicate masked junction stock recovery".into());
    }
    let proposed = candidates(page);
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("masked junction stock requires existing placement decisions")?;
    for entry in proposed {
        let w = entry.wall;
        let mut support = BTreeSet::from_iter(w.source_ids.iter().cloned());
        support.extend(entry.joint_sources);
        let d = WallDecision {
            wall_id: w.id.clone(),
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{}:{}", w.page, w.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![entry.cap_wall_id, entry.stem_wall_id],
            support_source_ids: support.into_iter().collect(),
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        page.semantics.routing.push(wall_route(&w));
        page.walls.push(w);
        decisions.push(d);
    }
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let Some(first) = page.walls.iter().position(is_stock) else {
        return Ok(Cow::Borrowed(page));
    };
    let decisions = page
        .placement_decisions
        .as_ref()
        .ok_or("missing masked junction stock decisions")?;
    if decisions.len() != page.walls.len()
        || !page.walls[first..]
            .iter()
            .zip(&decisions[first..])
            .all(|(w, d)| {
                is_stock(w)
                    && d.status == WallStatus::Ready
                    && d.reason == REASON
                    && d.wall_id == w.id
            })
    {
        return Err("masked junction stock must be a consecutive placement suffix".into());
    }
    let ids = page.walls[first..]
        .iter()
        .map(|w| w.id.as_str())
        .collect::<BTreeSet<_>>();
    let mut base = page.clone();
    base.walls.truncate(first);
    base.placement_decisions
        .as_mut()
        .expect("placement decisions verified")
        .truncate(first);
    base.semantics
        .routing
        .retain(|r| !ids.contains(r.entity_id.as_str()));
    Ok(Cow::Owned(base))
}

pub(crate) fn joints(page: &PageScan) -> Vec<Joint> {
    if !page.walls.iter().any(is_stock) {
        return vec![];
    }
    candidates(page)
        .into_iter()
        .filter_map(|c| {
            page.walls
                .iter()
                .zip(page.placement_decisions.iter().flatten())
                .find(|(w, d)| {
                    **w == c.wall && d.status == WallStatus::Ready && d.reason == REASON
                })?;
            Some(Joint {
                wall_ids: [c.stem_wall_id, c.wall.id],
                points: c.stem_path,
                source_ids: c.joint_sources,
            })
        })
        .collect()
}
