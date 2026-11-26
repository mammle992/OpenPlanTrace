//! Attach equal, coextensive painted leaves and their exact interior joint.
use super::{
    Band, Frame, Paint, Stock, exact_stock, exterior_piers, named_visible_paint, protected_bodies,
};
use crate::{
    input::raster::PdfRoute,
    model::{PageScan, Point},
    placement::{WallDecision, WallReason, WallStatus},
};
use std::collections::{BTreeSet, HashMap};

const REASON: WallReason = WallReason::SourceBoundedCoextensiveWallLeaf;
const MAX_WORK: usize = 3_000_000;

pub(crate) struct Assembly<'a> {
    pub owner: &'a str,
    pub mate: &'a str,
    pub stem: &'a str,
    pub leaf: [Point; 4],
    pub leaf_source: &'a str,
    pub center: [Point; 4],
    pub center_source: &'a str,
    pub cap_cells: Vec<(&'a str, [Point; 4])>,
    pub tip: Point,
    pub axis_contact: Point,
    pub source_ids: Vec<&'a str>,
}

fn spend(work: &mut usize, n: usize) -> Option<()> {
    *work = work.checked_sub(n)?;
    Some(())
}

fn oriented(band: Band<'_>, side: f64) -> Band<'_> {
    if side < 0. {
        return Band {
            across: [-band.across[1], -band.across[0]],
            ..band
        };
    }
    band
}

fn end_witnesses<'a>(
    frame: Frame,
    span: [f64; 2],
    width: f64,
    bands: &[Band<'a>],
    ready: &[Stock<'a>],
    work: &mut usize,
) -> Option<Vec<&'a str>> {
    spend(
        work,
        bands
            .len()
            .saturating_mul(2)
            .saturating_add(ready.len().saturating_mul(2)),
    )?;
    let mut result = None;
    for [first, other] in [[0., frame.length], [frame.length, 0.]] {
        let outside = if first == 0. { 0 } else { 1 };
        let matches =
            |b: &Band<'_>, at: f64, side: usize| (b.along[1 - side] - at).abs() <= frame.eps;
        let mut terminal = bands
            .iter()
            .filter(|b| {
                matches(b, first, outside)
                    && b.along[1] - b.along[0] <= width * 0.25
                    && b.across[0] >= span[0] - width * 0.05
                    && b.across[1] <= span[1] + width * 0.05
            })
            .collect::<Vec<_>>();
        terminal.sort_unstable_by(|a, b| a.across[0].total_cmp(&b.across[0]));
        if terminal.is_empty()
            || terminal.len() > 4
            || terminal
                .windows(2)
                .any(|w| (w[0].across[1] - w[1].across[0]).abs() > frame.eps)
            || terminal[0].across[0] > span[0] + width * 0.05
            || terminal.last()?.across[1] < span[1] - width * 0.05
        {
            continue;
        }
        let mut continuations = ready.iter().filter_map(|r| {
            let perpendicular = frame.axis.dot((r.wall.end - r.wall.start).unit()).abs() <= 1e-8;
            let band = frame.band(r.paint)?;
            (perpendicular
                && matches(&band, other, 1 - outside)
                && band.across[1].min(span[1]) - band.across[0].max(span[0])
                    >= (span[1] - span[0]) * (2. / 3.))
                .then_some(band.id)
        });
        let Some(continuation) = continuations.next() else {
            continue;
        };
        if continuations.next().is_some() || result.is_some() {
            return None;
        }
        result = Some(
            terminal
                .into_iter()
                .map(|b| b.id)
                .chain([continuation])
                .collect(),
        );
    }
    result
}

struct Join<'a> {
    stem: &'a str,
    stem_source: &'a str,
    cap_cells: Vec<(&'a str, [Point; 4])>,
    tip: Point,
    axis_contact: Point,
}

fn junction<'a>(
    receiver: Frame,
    outer: f64,
    side: f64,
    stem: &Stock<'a>,
    paints: &[Paint<'a>],
    work: &mut usize,
) -> Option<Join<'a>> {
    let w = stem.wall;
    let f = Frame::from(w)?;
    let eps = receiver.eps.max(f.eps);
    if receiver.axis.dot(f.axis).abs() > 1e-8 {
        return None;
    }
    let half = w.thickness * 0.5;
    for (tip, far) in [(w.start, w.end), (w.end, w.start)] {
        let p = receiver.local(tip);
        let depth = p.y * side - outer;
        if depth <= eps || depth > half || p.x <= half + eps || p.x >= receiver.length - half - eps
        {
            continue;
        }
        let q = receiver.at + receiver.axis * p.x;
        let u = (tip - far).unit();
        if u.dot((q - tip).unit()) < 1. - 1e-8 {
            continue;
        }
        spend(work, paints.len())?;
        let [start, end] = if tip == w.start {
            [-depth, 0.]
        } else {
            [f.length, f.length + depth]
        };
        let mut cells = paints
            .iter()
            .copied()
            .filter_map(|p| {
                if p.layer != stem.paint.layer {
                    return None;
                }
                let b = f.band(p)?;
                (f.same(b.across, [-half, half])
                    && b.along[0] >= start - eps
                    && b.along[1] <= end + eps)
                    .then_some((b.along, p))
            })
            .collect::<Vec<_>>();
        cells.sort_unstable_by(|a, b| a.0[0].total_cmp(&b.0[0]));
        if cells.is_empty()
            || cells.len() > 3
            || (cells[0].0[0] - start).abs() > eps
            || (cells.last()?.0[1] - end).abs() > eps
            || cells
                .windows(2)
                .any(|w| (w[0].0[1] - w[1].0[0]).abs() > eps)
        {
            continue;
        }
        return Some(Join {
            stem: w.id.as_str(),
            stem_source: stem.paint.id,
            cap_cells: cells.into_iter().map(|(_, p)| (p.id, p.outline)).collect(),
            tip,
            axis_contact: q,
        });
    }
    None
}

fn assess(page: &PageScan, committed: bool) -> Option<Vec<Assembly<'_>>> {
    let eligible_mate = |d: &WallDecision| {
        if committed {
            d.status == WallStatus::EvidenceOnly && d.reason == REASON
        } else {
            d.status == WallStatus::Review && d.reason == WallReason::OverlappingWallAxes
        }
    };
    if !page.placement_decisions.iter().flatten().any(eligible_mate)
        || !page
            .pdf_input
            .as_ref()
            .is_some_and(|input| matches!(input.route, PdfRoute::Vector | PdfRoute::Mixed))
    {
        return Some(vec![]);
    }
    let mut work = MAX_WORK;
    spend(&mut work, page.primitives.len())?;
    let paints = named_visible_paint(page);
    let ids = paints
        .iter()
        .copied()
        .map(|p| (p.id, p))
        .collect::<HashMap<_, _>>();
    let walls = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .collect::<Vec<_>>();
    let ready = walls
        .iter()
        .filter_map(|&(w, d)| {
            (d.status == WallStatus::Ready).then_some(())?;
            exact_stock(w, d, &ids)
        })
        .collect::<Vec<_>>();
    let mates = walls
        .iter()
        .filter_map(|&(w, d)| {
            eligible_mate(d).then_some(())?;
            exact_stock(w, d, &ids)
        })
        .collect::<Vec<_>>();
    spend(&mut work, ready.len().saturating_mul(mates.len()))?;
    let mut results = Vec::new();
    for mate in &mates {
        let mut choices = Vec::new();
        for owner in &ready {
            if owner.decision.reason != WallReason::SourceBoundedStaggeredPaintedCore
                || owner.paint.layer != mate.paint.layer
                || crate::layers::is_named_exterior_wall(owner.paint.layer)
                || (committed && mate.decision.support_wall_ids.first() != Some(&owner.wall.id))
            {
                continue;
            }
            let f = Frame::from(owner.wall)?;
            let width = owner.wall.thickness;
            let half = 0.5 * width;
            if (width - mate.wall.thickness).abs() > f.eps {
                continue;
            }
            let Some(parallel) = f.band(mate.paint) else {
                continue;
            };
            if !f.same(parallel.along, [0., f.length]) {
                continue;
            }
            let side = if parallel.across[0] > 0. { 1. } else { -1. };
            let leaf = oriented(parallel, side);
            if !(width * 0.12..=width * 0.55).contains(&(leaf.across[0] - half)) {
                continue;
            }
            spend(&mut work, paints.len())?;
            let bands = paints
                .iter()
                .copied()
                .filter(|p| p.layer == owner.paint.layer)
                .filter_map(|p| f.band(p))
                .collect::<Vec<_>>();
            let mut centers = bands.iter().copied().filter(|&p| {
                f.same(p.along, [0., f.length])
                    && f.same(oriented(p, side).across, [half, leaf.across[0]])
                    && owner
                        .decision
                        .support_source_ids
                        .iter()
                        .any(|id| id == p.id)
            });
            let Some(center) = centers.next() else {
                continue;
            };
            if centers.next().is_some() {
                continue;
            }
            let span = if side > 0. {
                [-half, leaf.across[1]]
            } else {
                [-leaf.across[1], half]
            };
            let Some(ends) = end_witnesses(f, span, width, &bands, &ready, &mut work) else {
                continue;
            };
            spend(&mut work, ready.len())?;
            let possible = ready.iter().filter(|s| {
                s.wall.id != owner.wall.id
                    && (!committed || mate.decision.support_wall_ids.get(1) == Some(&s.wall.id))
            });
            let mut joins = Vec::new();
            for stem in possible {
                if let Some(j) = junction(f, leaf.across[1], side, stem, &paints, &mut work) {
                    joins.push(j);
                }
            }
            let [j] = joins.as_slice() else { continue };
            let source_ids = [mate.paint.id, owner.paint.id, center.id, j.stem_source]
                .into_iter()
                .chain(ends)
                .chain(j.cap_cells.iter().map(|(id, _)| *id))
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect();
            choices.push(Assembly {
                owner: &owner.wall.id,
                mate: &mate.wall.id,
                stem: j.stem,
                leaf: mate.paint.outline,
                leaf_source: mate.paint.id,
                center: ids[center.id].outline,
                center_source: center.id,
                cap_cells: j.cap_cells.clone(),
                tip: j.tip,
                axis_contact: j.axis_contact,
                source_ids,
            });
        }
        if choices.len() == 1 {
            results.extend(choices);
        }
    }
    if committed || results.is_empty() {
        return Some(results);
    }
    let occupied = protected_bodies(page);
    let eps = 256. * f64::EPSILON * page.width.max(page.height).max(1.);
    spend(
        &mut work,
        results
            .len()
            .saturating_mul(5)
            .saturating_mul(occupied.len() + 5 * results.len()),
    )?;
    let mut conflicts = vec![false; results.len()];
    for (i, a) in results.iter().enumerate() {
        let outlines = |a: &Assembly<'_>| {
            [a.leaf, a.center]
                .into_iter()
                .chain(a.cap_cells.iter().map(|(_, q)| *q))
                .collect::<Vec<_>>()
        };
        let stock = outlines(a);
        if stock.iter().any(|&s| {
            occupied
                .iter()
                .any(|&o| exterior_piers::overlaps(s, o, eps))
        }) {
            conflicts[i] = true;
        }
        for (j, b) in results.iter().enumerate().skip(i + 1) {
            if a.owner == b.owner
                || stock.iter().any(|&s| {
                    outlines(b)
                        .into_iter()
                        .any(|o| exterior_piers::overlaps(s, o, eps))
                })
            {
                conflicts[i] = true;
                conflicts[j] = true;
            }
        }
    }
    Some(
        results
            .into_iter()
            .zip(conflicts)
            .filter_map(|(a, c)| (!c).then_some(a))
            .collect(),
    )
}

pub(crate) fn apply(page: &mut PageScan) {
    let changes = assess(page, false)
        .unwrap_or_default()
        .into_iter()
        .map(|a| {
            (
                a.mate.to_owned(),
                [a.owner, a.stem].map(str::to_owned),
                a.source_ids.into_iter().map(str::to_owned).collect(),
            )
        })
        .collect::<Vec<_>>();
    for (mate, hosts, sources) in changes {
        if let Some(d) = page
            .placement_decisions
            .iter_mut()
            .flatten()
            .find(|d| d.wall_id == mate)
        {
            d.status = WallStatus::EvidenceOnly;
            d.reason = REASON;
            d.support_wall_ids = hosts.into();
            d.support_source_ids = sources;
        }
    }
}

pub(crate) fn assemblies(page: &PageScan) -> Vec<Assembly<'_>> {
    assess(page, true).unwrap_or_default()
}
