//! Exact visible paint across short, otherwise unconnected exterior terminals.
use super::finite_paint_contacts::{
    Arm, Cell, Obstruction, prove_contact, stock_contains_terminal,
};
use super::painted_contact::{BodyIndex, Bounds, bounds};
use super::{
    Aperture, ConnectionKind, Link, MAX_EDGES, PageScan, Point, Rect, Solid, charge,
    crosses_aperture, ids, point_key,
};
use crate::input::raster::PdfRoute;
use crate::layers::{is_named_wall, named_exterior_wall};
use crate::model::{LayerKind, Primitive, Shape};
use std::collections::{HashMap, HashSet};

const MAX_WORK: usize = 3_000_000;
pub(super) const EVIDENCE: &str = "finite_exterior_terminal_contact_through_exact_visible_paint";

struct Paint<'a> {
    id: &'a str,
    layer: &'a str,
    order: u64,
    points: &'a [Point],
    obscurers: Vec<Obstruction<'a>>,
    exterior: bool,
    whole: bool,
}

impl Paint<'_> {
    fn cell(&self) -> Cell<'_> {
        Cell {
            id: self.id,
            layer: self.layer,
            order: self.order,
            points: self.points,
            obscurers: &self.obscurers,
        }
    }
}

fn outline(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    let count = points.len() - usize::from(!points.is_empty() && points.first() == points.last());
    ((3..=8).contains(&count) && points.iter().all(|p| p.finite())).then_some(&points[..count])
}

fn source_order(id: &str, page: u32) -> Option<u64> {
    let (number, path) = id.strip_prefix("pdf:p")?.split_once(":path:")?;
    let (order, subpath) = path.split_once(":subpath:")?;
    (number.parse::<u32>().ok()? == page && subpath.parse::<u32>().ok()? > 0)
        .then_some(order.parse().ok()?)
}

fn paint<'a>(page: &'a PageScan, work: &mut usize) -> Result<Vec<Paint<'a>>, String> {
    let whole = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let receipts = page
        .placement_contact_paint
        .iter()
        .map(|r| (r.source_id.as_str(), r))
        .collect::<HashMap<_, _>>();
    charge(
        work,
        page.primitives
            .len()
            .saturating_mul(2)
            .saturating_add(whole.len())
            .saturating_add(receipts.len()),
    )?;
    let primitives = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let mut paint = Vec::new();
    for primitive in &page.primitives {
        let id = primitive.id.as_str();
        let whole = whole.contains(id);
        let receipt = receipts.get(id);
        if !whole && receipt.is_none() {
            continue;
        }
        let Some(layer) = primitive.layer.as_deref().filter(|s| is_named_wall(s)) else {
            continue;
        };
        if primitive.layer_category_override.is_some() && primitive.layer_kind() != LayerKind::Wall
        {
            continue;
        }
        let Some(points) = outline(primitive) else {
            continue;
        };
        let Some(order) = source_order(id, page.page_number) else {
            continue;
        };
        let mut obscurers = Vec::new();
        if let Some(receipt) = receipt {
            charge(work, receipt.occluder_source_ids.len())?;
            let resolved = receipt
                .occluder_source_ids
                .iter()
                .map(|id| {
                    let later = source_order(id, page.page_number)?;
                    (later > order).then_some(Obstruction {
                        id,
                        order: later,
                        points: outline(primitives.get(id.as_str())?)?,
                    })
                })
                .collect::<Option<Vec<_>>>();
            let Some(resolved) = resolved else { continue };
            obscurers = resolved;
        }
        paint.push(Paint {
            id,
            layer,
            order,
            points,
            obscurers,
            exterior: named_exterior_wall(primitive).is_some(),
            whole,
        });
    }
    Ok(paint)
}

fn extra_apertures<'a>(
    page: &'a PageScan,
    eps: f64,
    work: &mut usize,
) -> Result<Vec<Aperture<'a>>, String> {
    charge(work, page.walls.len())?;
    let widths = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness * 0.5))
        .collect::<HashMap<_, _>>();
    let mut apertures = Vec::new();
    let mut add = |opening, a: Point, b: Point, half: f64| {
        if a.finite() && b.finite() && a.distance(b) > eps && half.is_finite() && half >= 0. {
            apertures.push(Aperture {
                opening,
                rect: Rect { a, b, half },
                framed_door: None,
            });
        }
    };
    for pane in &page.window_panes {
        let o = &pane.observation;
        charge(work, o.host_wall_ids.len().saturating_add(1))?;
        let half = o
            .host_wall_ids
            .iter()
            .filter_map(|id| widths.get(id.as_str()).copied())
            .fold(0_f64, f64::max);
        add(o, o.start, o.end, half);
    }
    for o in page.opening_evidence() {
        charge(work, o.swing_observations.len().saturating_add(1))?;
        for swing in &o.swing_observations {
            for leaf in [swing.closed_tip, swing.open_tip] {
                add(o, swing.hinge, leaf, swing.leaf_tolerance.max(eps));
            }
        }
    }
    Ok(apertures)
}

struct Terminal<'a> {
    solid: usize,
    arm: Arm<'a>,
}

fn at_bounds(p: Point, reach: f64) -> Bounds {
    [p.x - reach, p.y - reach, p.x + reach, p.y + reach]
}

fn terminals<'a>(
    page: &PageScan,
    solids: &[Solid],
    links: &[Link],
    paint: &[Paint<'a>],
    by_source: &HashMap<&str, usize>,
    eps: f64,
    work: &mut usize,
) -> Result<Vec<Terminal<'a>>, String> {
    charge(
        work,
        links.len().saturating_add(solids.len().saturating_mul(2)),
    )?;
    let incidences = links
        .iter()
        .flat_map(|link| [link.a, link.b])
        .map(|p| at_bounds(p, 0.))
        .collect();
    let incidences = BodyIndex::from_bounds(incidences, work)?;
    let mut repeated = HashSet::new();
    let mut ends = HashSet::new();
    for p in solids.iter().flat_map(|s| [s.a, s.b]).map(point_key) {
        if !ends.insert(p) {
            repeated.insert(p);
        }
    }
    let mut terminals = Vec::new();
    let mut nearby = Vec::new();
    for (i, solid) in solids.iter().enumerate() {
        let source = &page.walls[solid.wall].source_ids;
        charge(work, source.len())?;
        let stocks = source
            .iter()
            .filter_map(|id| by_source.get(id.as_str()))
            .map(|&i| &paint[i])
            .filter(|p| p.whole && p.exterior && p.obscurers.is_empty());
        for (at, other) in [(solid.a, solid.b), (solid.b, solid.a)] {
            if repeated.contains(&point_key(at)) {
                continue;
            }
            let mut matching = stocks.clone().filter_map(|p| {
                let arm = Arm {
                    at,
                    outside: at - other,
                    width: solid.half * 2.,
                    source_id: p.id,
                    layer: p.layer,
                };
                stock_contains_terminal(arm, p.points, solid.length(), eps).then_some(arm)
            });
            let Some(arm) = matching.next() else { continue };
            if matching.next().is_some() {
                continue;
            }
            nearby.clear();
            incidences.visit(0, at_bounds(at, 0.), eps, &mut nearby, work)?;
            if nearby.is_empty() {
                terminals.push(Terminal { solid: i, arm });
            }
        }
    }
    Ok(terminals)
}

fn pair(a: usize, b: usize) -> [usize; 2] {
    [a.min(b), a.max(b)]
}

fn candidates(
    page: &PageScan,
    solids: &[Solid],
    apertures: &[Aperture],
    links: &[Link],
    eps: f64,
    work: &mut usize,
) -> Result<Vec<Link>, String> {
    let paint = paint(page, work)?;
    if paint.iter().all(|p| p.whole) {
        return Ok(vec![]);
    }
    let by_source = paint
        .iter()
        .enumerate()
        .map(|(i, p)| (p.id, i))
        .collect::<HashMap<_, _>>();
    let terminals = terminals(page, solids, links, &paint, &by_source, eps, work)?;
    if terminals.len() < 2 {
        return Ok(vec![]);
    }
    let paint_bounds =
        BodyIndex::from_bounds(paint.iter().map(|p| bounds(p.points)).collect(), work)?;
    let reach = |t: &Terminal| at_bounds(t.arm.at, t.arm.width * 0.875);
    let terminal_bounds = BodyIndex::from_bounds(terminals.iter().map(reach).collect(), work)?;
    let extras = extra_apertures(page, eps, work)?;
    let walls = page
        .walls
        .iter()
        .enumerate()
        .map(|(i, w)| (w.id.as_str(), i))
        .collect::<HashMap<_, _>>();
    let existing = links
        .iter()
        .filter_map(|link| {
            let [a, b] = link.wall_ids.as_slice() else {
                return None;
            };
            Some(pair(*walls.get(a.as_str())?, *walls.get(b.as_str())?))
        })
        .collect::<HashSet<_>>();
    let mut neighbors = Vec::new();
    let mut local = Vec::new();
    let mut cells = Vec::new();
    let mut pending = Vec::new();
    let mut matches = vec![0_usize; terminals.len()];
    let mut wall_matches = HashMap::new();
    for (i, left) in terminals.iter().enumerate() {
        neighbors.clear();
        terminal_bounds.visit(0, reach(left), eps, &mut neighbors, work)?;
        neighbors.sort_unstable();
        for &j in neighbors.iter().filter(|&&j| j > i) {
            charge(work, 1)?;
            let right = &terminals[j];
            let walls = pair(solids[left.solid].wall, solids[right.solid].wall);
            let [a, b] = [left.arm.at, right.arm.at];
            let length = a.distance(b);
            if walls[0] == walls[1]
                || existing.contains(&walls)
                || left.arm.layer != right.arm.layer
                || length <= eps
                || length > 0.875 * (left.arm.width + right.arm.width)
            {
                continue;
            }
            local.clear();
            paint_bounds.visit(0, bounds(&[a, b]), eps, &mut local, work)?;
            if local.iter().all(|&i| paint[i].whole) {
                continue;
            }
            cells.clear();
            cells.extend(local.iter().map(|&i| paint[i].cell()));
            let proof = prove_contact([left.arm, right.arm], &cells, &[], work);
            if *work == 0 {
                return Err("exact exterior contact work budget exceeded".into());
            }
            let Some(proof) = proof else { continue };
            if proof.source_ids.iter().all(|id| paint[by_source[id]].whole)
                || crosses_aperture(a, b, apertures, None, eps, work)?
                || crosses_aperture(a, b, &extras, None, eps, work)?
            {
                continue;
            }
            let source_ids = ids(proof.source_ids.into_iter().flat_map(|id| {
                std::iter::once(id.to_owned()).chain(
                    paint[by_source[id]]
                        .obscurers
                        .iter()
                        .map(|o| o.id.to_owned()),
                )
            }));
            let wall_ids = ids(walls.map(|i| page.walls[i].id.clone()));
            matches[i] += 1;
            matches[j] += 1;
            *wall_matches.entry(walls).or_insert(0_usize) += 1;
            pending.push((
                [i, j],
                walls,
                Link {
                    a,
                    b,
                    kind: ConnectionKind::MaterialContact,
                    wall_ids,
                    opening_ids: vec![],
                    source_ids,
                    evidence: EVIDENCE,
                    anchors: [Some(left.solid), Some(right.solid)],
                },
            ));
        }
    }
    Ok(pending
        .into_iter()
        .filter_map(|([i, j], walls, link)| {
            (matches[i] == 1 && matches[j] == 1 && wall_matches[&walls] == 1).then_some(link)
        })
        .collect())
}

/// When a page is too dense for this optional proof, keep the existing graph.
pub(super) fn append(
    page: &PageScan,
    solids: &[Solid],
    apertures: &[Aperture],
    links: &mut Vec<Link>,
    eps: f64,
    work: &mut usize,
) -> bool {
    if page.placement_contact_paint.is_empty()
        || solids.len() < 2
        || page
            .pdf_input
            .as_ref()
            .is_none_or(|input| input.route == PdfRoute::Raster)
    {
        return true;
    }
    let budget = (*work / 4).min(MAX_WORK);
    let mut remaining = budget;
    let pending = candidates(page, solids, apertures, links, eps, &mut remaining);
    *work -= budget - remaining;
    let Ok(pending) = pending else { return false };
    if links.len().saturating_add(pending.len()) > MAX_EDGES {
        return false;
    }
    links.extend(pending);
    true
}
