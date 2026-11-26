//! Preserve staggered exterior finish planes around a certified thin seam.
use super::{
    Band, Frame, Paint, Stock, exact_stock, exterior_piers, named_visible_paint, protected_bodies,
};
use crate::{
    input::raster::PdfRoute,
    model::{PageScan, Point},
    placement::{WallDecision, WallReason, WallStatus},
};
use std::collections::HashMap;

const REASON: WallReason = WallReason::SourceBoundedExteriorLining;
const MAX_WORK: usize = 2_000_000;

pub(crate) struct Lining<'a> {
    pub owner: &'a str,
    pub leaf: &'a str,
    pub materials: [(&'a str, [Point; 4]); 2],
    sources: Vec<&'a str>,
}

fn close(f: Frame, a: f64, b: f64) -> bool {
    (a - b).abs() <= f.eps
}

fn only<T>(mut choices: impl Iterator<Item = T>) -> Option<T> {
    let v = choices.next()?;
    choices.next().is_none().then_some(v)
}

fn return_paint<'a>(
    f: Frame,
    leaf: Band<'a>,
    at_start: bool,
    bands: &[Band<'a>],
) -> Option<&'a str> {
    let width = leaf.across[1] - leaf.across[0];
    let station = usize::from(!at_start);
    only(bands.iter().filter_map(|b| {
        let end = close(f, b.along[1 - station], leaf.along[station]);
        let side = close(f, b.across[1], leaf.across[0]) || close(f, b.across[0], leaf.across[1]);
        (end && side
            && (b.along[1] - b.along[0] - width).abs() <= width * 0.005
            && b.across[1] - b.across[0] >= 2. * width)
            .then_some(b.id)
    }))
}

fn prove<'a>(
    leaf: &Stock<'a>,
    owner: &Stock<'a>,
    paints: &[Paint<'a>],
    work: &mut usize,
) -> Option<Lining<'a>> {
    if leaf.paint.layer != owner.paint.layer {
        return None;
    }
    let f = Frame::from(owner.wall)?;
    let width = leaf.wall.thickness;
    let half = 0.5 * owner.wall.thickness;
    if !(0.14..=0.4).contains(&(width / owner.wall.thickness)) {
        return None;
    }
    let b = f.band(leaf.paint)?;
    let sign = if b.across[0] > 0. { 1. } else { -1. };
    let from = if sign > 0. { b.across[0] } else { -b.across[1] };
    let gap = from - half;
    if gap <= f.eps || gap > width * 0.10 {
        return None;
    }
    let common = [b.along[0].max(0.), b.along[1].min(f.length)];
    let length = common[1] - common[0];
    if length < 0.95 * f.length.max(b.along[1] - b.along[0])
        || b.along[0].abs().max((b.along[1] - f.length).abs()) > owner.wall.thickness * 1.1
    {
        return None;
    }
    *work = work.checked_sub(paints.len().saturating_mul(4))?;
    let bands = paints
        .iter()
        .copied()
        .filter(|p| p.layer == owner.paint.layer)
        .filter_map(|p| f.band(p))
        .collect::<Vec<_>>();
    let support = |id| owner.decision.support_source_ids.iter().any(|s| s == id);
    let across = if sign > 0. {
        [half, from]
    } else {
        [-from, -half]
    };
    let seam = only(bands.iter().filter(|s| {
        support(s.id)
            && f.same(s.across, across)
            && s.along[0] <= common[0] + f.eps
            && s.along[1] >= common[1] - f.eps
    }))?;
    let opposing = only(bands.iter().filter(|s| {
        let plane = if sign > 0. { s.across[1] } else { -s.across[0] };
        support(s.id)
            && close(f, plane, -half)
            && (gap..=width).contains(&(s.across[1] - s.across[0]))
            && s.along[1].min(common[1]) - s.along[0].max(common[0]) >= length * 0.95
    }))?;
    let ends = [
        return_paint(f, b, true, &bands)?,
        return_paint(f, b, false, &bands)?,
    ];
    let mut sources = vec![
        owner.paint.id,
        leaf.paint.id,
        seam.id,
        opposing.id,
        ends[0],
        ends[1],
    ];
    sources.sort_unstable();
    sources.dedup();
    if sources.len() != 6 {
        return None;
    }
    Some(Lining {
        owner: &owner.wall.id,
        leaf: &leaf.wall.id,
        materials: [
            (leaf.paint.id, leaf.paint.outline),
            (seam.id, f.outline(common, seam.across)),
        ],
        sources,
    })
}

fn proposals(page: &PageScan, committed: bool) -> Option<Vec<Lining<'_>>> {
    let candidate = |d: &WallDecision| {
        if committed {
            d.reason == REASON && d.status == WallStatus::EvidenceOnly
        } else {
            d.reason == WallReason::IncompletePhysicalSpan && d.status == WallStatus::Review
        }
    };
    if !page
        .pdf_input
        .as_ref()
        .is_some_and(|p| matches!(p.route, PdfRoute::Vector | PdfRoute::Mixed))
        || !page.placement_decisions.iter().flatten().any(candidate)
    {
        return Some(vec![]);
    }
    let mut work = MAX_WORK.checked_sub(page.primitives.len())?;
    let paints = named_visible_paint(page)
        .into_iter()
        .filter(|p| crate::layers::is_named_exterior_wall(p.layer))
        .collect::<Vec<_>>();
    let by_id = paints
        .iter()
        .copied()
        .map(|p| (p.id, p))
        .collect::<HashMap<_, _>>();
    let mut leaves = Vec::new();
    let mut owners = Vec::new();
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        let is_owner = d.status == WallStatus::Ready
            && d.reason == WallReason::SourceBoundedStaggeredPaintedCore;
        if !candidate(d) && !is_owner {
            continue;
        }
        let Some(stock) = exact_stock(w, d, &by_id) else {
            continue;
        };
        if is_owner {
            owners.push(stock);
        } else {
            leaves.push(stock);
        }
    }
    work = work.checked_sub(leaves.len().saturating_mul(owners.len()))?;
    let mut out = Vec::new();
    for leaf in leaves {
        let mut choices = owners
            .iter()
            .filter(|owner| {
                !committed || leaf.decision.support_wall_ids == [owner.wall.id.as_str()]
            })
            .filter_map(|owner| prove(&leaf, owner, &paints, &mut work));
        if let Some(a) = only(&mut choices) {
            out.push(a);
        }
    }
    if committed || out.is_empty() {
        return Some(out);
    }

    let mut occupied = protected_bodies(page);
    for a in super::coextensive::assemblies(page) {
        occupied.extend([a.leaf, a.center]);
        occupied.extend(a.cap_cells.into_iter().map(|(_, q)| q));
    }
    work.checked_sub(
        out.len()
            .saturating_mul(4)
            .saturating_mul(occupied.len() + out.len()),
    )?;
    let eps = 256. * f64::EPSILON * page.width.max(page.height).max(1.);
    let conflicts = |a: &Lining<'_>, b: &Lining<'_>| {
        a.owner == b.owner
            || a.materials.iter().any(|&(_, u)| {
                b.materials
                    .iter()
                    .any(|&(_, v)| exterior_piers::overlaps(u, v, eps))
            })
    };
    let safe = out
        .iter()
        .enumerate()
        .map(|(i, a)| {
            !a.materials.iter().any(|&(_, u)| {
                occupied
                    .iter()
                    .any(|&v| exterior_piers::overlaps(u, v, eps))
            }) && !out
                .iter()
                .enumerate()
                .any(|(j, b)| j != i && conflicts(a, b))
        })
        .collect::<Vec<_>>();
    Some(
        out.into_iter()
            .zip(safe)
            .filter_map(|(x, ok)| ok.then_some(x))
            .collect(),
    )
}

pub(crate) fn apply(page: &mut PageScan) {
    let assignments = proposals(page, false)
        .unwrap_or_default()
        .into_iter()
        .map(|a| {
            (
                a.leaf.to_owned(),
                a.owner.to_owned(),
                a.sources.into_iter().map(str::to_owned).collect(),
            )
        })
        .collect::<Vec<_>>();
    for (leaf, owner, sources) in assignments {
        if let Some(d) = page
            .placement_decisions
            .iter_mut()
            .flatten()
            .find(|d| d.wall_id == leaf)
        {
            d.status = WallStatus::EvidenceOnly;
            d.reason = REASON;
            d.support_wall_ids = vec![owner];
            d.support_source_ids = sources;
        }
    }
}

pub(crate) fn linings(page: &PageScan) -> Vec<Lining<'_>> {
    proposals(page, true).unwrap_or_default()
}
