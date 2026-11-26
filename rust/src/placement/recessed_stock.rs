//! Place independently measured recesses between an existing door flank and a signed side branch.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall,
    exterior_piers::{aperture_guard, beside_ready_wall, corners, overlaps},
};
use crate::{
    input::recessed_stock::{self, Receipt, Verified},
    model::{BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Wall},
    semantics::wall_route,
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashSet},
};

const PREFIX: &str = "recessed-stock:";
const REASON: WallReason = WallReason::SourceBoundedRecessedStock;

pub(crate) struct Branch {
    pub branch_wall_id: String,
    pub stock_wall_id: String,
    pub opening_id: String,
    pub toe: Point,
    pub branch_point: Point,
    pub stock_point: Point,
    pub source_ids: Vec<String>,
}

struct Stock {
    start: Point,
    end: Point,
    width: f64,
}

impl Stock {
    fn from([a, b, c, d]: [Point; 4]) -> Self {
        Self {
            start: (a + d) * 0.5,
            end: (b + c) * 0.5,
            width: a.distance(d),
        }
    }

    fn same(&self, wall: &Wall, eps: f64) -> bool {
        wall.curve.is_none()
            && (wall.thickness - self.width).abs() <= eps
            && ((wall.start.distance(self.start) <= eps && wall.end.distance(self.end) <= eps)
                || (wall.end.distance(self.start) <= eps && wall.start.distance(self.end) <= eps))
    }
}

pub(crate) fn is_stock(w: &Wall) -> bool {
    w.id.starts_with(PREFIX)
}

fn unique<T>(mut items: impl Iterator<Item = T>) -> Option<T> {
    let first = items.next()?;
    items.next().is_none().then_some(first)
}

fn signed_from<'a>(
    page: &'a PageScan,
    w: &Wall,
    decision: &WallDecision,
    point: Point,
    axis: Point,
    evidence: OpeningEvidence,
    eps: f64,
) -> Option<&'a Opening> {
    let [signed] = decision.opening_ids.as_slice() else {
        return None;
    };
    // The raw overhead sill is parallel to its measured wall threshold.
    // Retain the same facing-cap offset accepted by the connection pass.
    let reach = if evidence == OpeningEvidence::LayerlessOverheadProjection {
        2. * w.thickness + eps
    } else {
        eps
    };
    unique(page.openings.iter().filter(|o| {
        if &o.id != signed
            || o.kind != OpeningKind::Door
            || o.evidence != evidence
            || o.host_wall_ids.len() != 2
            || !o.host_wall_ids.contains(&w.id)
        {
            return false;
        }
        let [a, b] = [o.start - point, o.end - point];
        (b - a).cross(axis).abs() <= eps
            && a.cross(axis).abs() <= reach
            && (a.dot(axis).abs() <= eps && b.dot(axis) > eps
                || b.dot(axis).abs() <= eps && a.dot(axis) > eps)
    }))
}

fn original_paint(w: &Wall, d: &WallDecision, id: &str) -> bool {
    w.source_ids
        .iter()
        .chain(&d.support_source_ids)
        .any(|source| source == id)
}

struct Proposal {
    walls: Vec<Wall>,
    branch: Branch,
}

fn propose(
    page: &PageScan,
    receipt: &Receipt,
    v: Verified,
    ordinal: usize,
    ready: &[(Cow<'_, Wall>, &WallDecision)],
    apertures: &[[Point; 4]],
) -> Option<Proposal> {
    let last = Stock::from(*v.outlines.last()?);
    let eps = last.width * 1e-4;
    let into = (last.end - last.start).unit();
    let branch = Stock::from(v.branch_outline);
    let toward_branch = (branch.end - branch.start).unit();
    let (side, t) = crate::model::on_segment(branch.end, last.start, last.end);
    if t <= 0. || t >= 1. || (v.toe.distance(last.end) - last.width * 0.5).abs() > eps {
        return None;
    }
    let (flank, opening) = unique(ready.iter().filter_map(|(w, d)| {
        if d.reason != WallReason::SourceBoundedOverheadGateStock
            || !branch.same(w, eps)
            || !original_paint(w, d, &receipt.branch_paint_source_id)
        {
            return None;
        }
        Some((
            w,
            signed_from(
                page,
                w,
                d,
                branch.start,
                toward_branch * -1.,
                OpeningEvidence::LayerlessOverheadProjection,
                eps,
            )?,
        ))
    }))?;
    let continuation = unique(ready.iter().filter_map(|(w, d)| {
        if d.reason != WallReason::SourceBoundedDashedSwingStock
            || w.curve.is_some()
            || (w.thickness - last.width).abs() > eps
            || !original_paint(w, d, &receipt.paint_source_id)
        {
            return None;
        }
        let far = if w.start.distance(last.end) <= eps {
            w.end
        } else if w.end.distance(last.end) <= eps {
            w.start
        } else {
            return None;
        };
        let from = far - last.end;
        if from.cross(into).abs() > eps || from.dot(into) <= eps {
            return None;
        }
        Some((
            w,
            signed_from(
                page,
                w,
                d,
                far,
                into,
                OpeningEvidence::LayerlessDashedSwing,
                eps,
            )?,
        ))
    }))?;
    let mut sources = receipt.source_ids().cloned().collect::<Vec<_>>();
    sources.sort_unstable();
    sources.dedup();
    let mut additions = Vec::with_capacity(v.outlines.len());
    for (index, shape) in v.outlines.into_iter().enumerate() {
        let m = Stock::from(shape);
        let w = Wall {
            id: format!("{PREFIX}{}:{ordinal}:{}", page.page_number, index + 1),
            page: page.page_number,
            start: m.start,
            end: m.end,
            thickness: m.width,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: sources.clone(),
            requires_review: false,
        };
        if apertures.iter().any(|q| overlaps(shape, *q, eps))
            || ready.iter().any(|(other, _)| {
                if other.curve.is_some() || !other.thickness.is_finite() || other.thickness <= 0. {
                    return false;
                }
                let area = corners(other.start, other.end, other.thickness);
                overlaps(shape, area, eps) || beside_ready_wall(&w, area)
            })
        {
            return None;
        }
        additions.push(w);
    }
    let stock_wall_id = additions.last()?.id.clone();
    let mut branch_sources = sources;
    branch_sources.extend(opening.source_ids.iter().cloned());
    branch_sources.extend(continuation.1.source_ids.iter().cloned());
    branch_sources.sort_unstable();
    branch_sources.dedup();
    Some(Proposal {
        walls: additions,
        branch: Branch {
            branch_wall_id: flank.id.clone(),
            stock_wall_id,
            opening_id: opening.id.clone(),
            toe: v.toe,
            branch_point: branch.end,
            stock_point: side,
            source_ids: branch_sources,
        },
    })
}

fn proposals(page: &PageScan) -> Vec<Proposal> {
    if page.recessed_stock.is_empty() {
        return vec![];
    }
    let ready = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(w, d)| d.status == WallStatus::Ready && !is_stock(w))
        .map(|(w, d)| (effective_wall(w, Some(d)), d))
        .collect::<Vec<_>>();
    let apertures = aperture_guard(page);
    let mut proposals = page
        .recessed_stock
        .iter()
        .enumerate()
        .filter_map(|(i, r)| {
            propose(
                page,
                r,
                recessed_stock::verified(page, r)?,
                i + 1,
                &ready,
                &apertures,
            )
        })
        .collect::<Vec<_>>();
    let conflicts = proposals
        .iter()
        .enumerate()
        .map(|(i, a)| {
            proposals.iter().enumerate().any(|(j, b)| {
                i != j
                    && (a.branch.branch_wall_id == b.branch.branch_wall_id
                        || a.walls.iter().any(|a| {
                            b.walls.iter().any(|b| {
                                let area = corners(b.start, b.end, b.thickness);
                                overlaps(
                                    corners(a.start, a.end, a.thickness),
                                    area,
                                    a.thickness.min(b.thickness) * 1e-4,
                                ) || beside_ready_wall(a, area)
                            })
                        }))
            })
        })
        .collect::<Vec<_>>();
    proposals
        .drain(..)
        .zip(conflicts)
        .filter_map(|(p, conflict)| (!conflict).then_some(p))
        .collect()
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let proposals = proposals(page);
    if proposals.is_empty() {
        return Ok(());
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("recessed stock requires existing placement decisions")?;
    if page.walls.iter().any(is_stock) {
        return Err("duplicate recessed stock recovery".into());
    }
    for w in proposals.into_iter().flat_map(|p| p.walls) {
        let d = WallDecision {
            wall_id: w.id.clone(),
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{}:{}", w.page, w.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: w.source_ids.clone(),
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
    if page.walls[first..].iter().any(|w| !is_stock(w)) {
        return Err("recessed stock must be a consecutive placement suffix".into());
    }
    let expected = page.walls[first..]
        .iter()
        .map(|w| w.id.as_str())
        .collect::<BTreeSet<_>>();
    let mut base = page.clone();
    base.walls.truncate(first);
    base.semantics
        .routing
        .retain(|q| !expected.contains(q.entity_id.as_str()));
    let decisions = base
        .placement_decisions
        .as_mut()
        .ok_or("missing recessed stock decisions")?;
    if decisions
        .iter()
        .filter(|d| {
            expected.contains(d.wall_id.as_str())
                && d.reason == REASON
                && d.status == WallStatus::Ready
        })
        .count()
        != expected.len()
    {
        return Err("invalid recessed stock wall decisions".into());
    }
    decisions.retain(|d| !expected.contains(d.wall_id.as_str()));
    Ok(Cow::Owned(base))
}

pub(crate) fn branches(page: &PageScan) -> Vec<Branch> {
    if !page.walls.iter().any(is_stock) {
        return vec![];
    }
    let eligible = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(w, d)| is_stock(w) && d.reason == REASON && d.status == WallStatus::Ready)
        .collect::<Vec<_>>();
    let expected = eligible
        .iter()
        .map(|(w, _)| w.id.as_str())
        .collect::<HashSet<_>>();
    proposals(page)
        .into_iter()
        .filter_map(|p| {
            p.walls
                .iter()
                .all(|w| {
                    expected.contains(w.id.as_str()) && eligible.iter().any(|(s, _)| **s == *w)
                })
                .then_some(p.branch)
        })
        .collect()
}
