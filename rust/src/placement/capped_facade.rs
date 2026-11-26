//! Bounded stock whose square end ties to a native visible return.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall,
    stepped_facade::{self, Joint},
    wall_layers,
};
use crate::{
    input::{
        capped_stock::{self, Receipt},
        stepped_stock,
    },
    model::{BodyEvidence, PageScan, Point, Primitive, Shape, Wall},
    semantics::wall_route,
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashMap, HashSet},
};

const PREFIX: &str = "capped-facade-stock:";
const REASON: WallReason = WallReason::SourceBoundedCappedFacade;
const MAX_WORK: usize = 2_000_000;

pub(crate) fn is_stock(w: &Wall) -> bool {
    w.id.starts_with(PREFIX)
}

#[derive(Clone, Copy)]
struct Ink<'a> {
    id: &'a str,
    points: [Point; 2],
    width: f64,
}

impl Ink<'_> {
    fn native(p: &Primitive) -> Option<Ink<'_>> {
        let Shape::Line { start, end } = p.shape else {
            return None;
        };
        let width = p.stroke_width?;
        (p.layer.is_none()
            && p.id.starts_with("pdf:")
            && p.id.contains(":path:")
            && !p.id.contains(":clip:")
            && start != end
            && width > 0.
            && width.is_finite())
        .then_some(Ink {
            id: &p.id,
            points: [start, end],
            width,
        })
    }
}

fn order(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}
fn eq(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}
fn spend(work: &mut usize, n: usize) -> Option<()> {
    *work = work.checked_sub(n)?;
    Some(())
}
fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

struct Frame {
    start: Point,
    transverse: Point,
    into: Point,
    eps: f64,
}

impl Frame {
    fn at(&self, p: Point) -> Point {
        let r = p - self.start;
        Point::new(r.dot(self.transverse), r.dot(self.into))
    }
    fn span(&self, line: Ink<'_>) -> [[f64; 2]; 2] {
        let [a, b] = line.points.map(|q| self.at(q));
        [order(a.x, b.x), order(a.y, b.y)]
    }
    fn eq(&self, x: f64, y: f64) -> bool {
        eq(x, y, self.eps)
    }
}

#[derive(Clone)]
struct Measured {
    start: Point,
    end: Point,
    width: f64,
    outline: [Point; 4],
}

impl Measured {
    fn from(outline: [Point; 4]) -> Self {
        let [a, b, c, d] = outline;
        Self {
            start: (a + d) * 0.5,
            end: (b + c) * 0.5,
            width: a.distance(d),
            outline,
        }
    }
}

struct Topology {
    host: String,
    points: [Point; 2],
    toe: Point,
    sources: Vec<String>,
}

fn preferred<T>(items: impl Iterator<Item = T>) -> Option<T> {
    let mut iter = items;
    let first = iter.next()?;
    iter.next().is_none().then_some(first)
}

fn turn(
    stock: &Measured,
    r: &Receipt,
    host: &Wall,
    host_paint: &Primitive,
    ink: &[Ink<'_>],
    by_id: &HashMap<&str, Ink<'_>>,
    work: &mut usize,
) -> Option<Option<Topology>> {
    let w = stock.width;
    let eps = 0.0001 * w;
    let u = (stock.end - stock.start).unit();
    let into = (host.start - host.end).unit();
    if !eq(w, host.thickness, eps)
        || u.dot(into).abs() > 1e-8
        || !eq((stock.end - host.start).dot(u), 0.5 * w, eps)
    {
        return Some(None);
    }
    let target = stock.end - u * (0.5 * w);
    let lead = (target - host.start).dot(into);
    let gap = lead - 0.5 * w;
    if !(0.008 * w..=0.09 * w).contains(&gap)
        || (target - host.start).cross(into).abs() > eps
        || (target - stock.start).dot(u) <= eps
    {
        return Some(None);
    }
    let toe = stock.end - into * (0.5 * w);
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &host_paint.shape
    else {
        return Some(None);
    };
    if !points.iter().any(|p| p.distance(toe) <= eps) {
        return Some(None);
    }
    let frame = Frame {
        start: host.start,
        transverse: u,
        into,
        eps,
    };
    let tf = frame.at(toe);
    // Require the actual beveled host edge between the shared toe and the
    // opposite end of the first east rectangle's transverse cut.
    let bevel = Point::new(-0.5 * w, 0.);
    if !points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .any(|(&a, &b)| {
            let [a, b] = [a, b].map(|q| frame.at(q));
            a.distance(tf) <= eps && b.distance(bevel) <= eps
                || b.distance(tf) <= eps && a.distance(bevel) <= eps
        })
    {
        return Some(None);
    }
    let Some(&top) = by_id.get(r.backing_pen_source_ids[0].as_str()) else {
        return Some(None);
    };
    let Some(&inner) = by_id.get(r.backing_pen_source_ids[1].as_str()) else {
        return Some(None);
    };
    let side = 0.5 * w + gap;
    let depth = w + 2. * gap;
    let [x, y] = frame.span(top);
    let [a, b] = frame.span(inner);
    if !frame.eq(x[1], side)
        || !frame.eq(y[0], depth)
        || !frame.eq(y[1], depth)
        || !frame.eq(a[1], -side)
        || !frame.eq(b[0], 0.)
        || !frame.eq(b[1], 0.)
        || !frame.eq(top.width, inner.width)
    {
        return Some(None);
    }
    spend(work, ink.len().saturating_mul(3))?;
    let thick = ink
        .iter()
        .copied()
        .filter(|p| frame.eq(p.width, top.width))
        .collect::<Vec<_>>();
    let side_line = |x| {
        preferred(thick.iter().copied().filter(|&p| {
            let [a, b] = frame.span(p);
            frame.eq(a[0], x) && frame.eq(a[1], x) && frame.eq(b[1], 0.) && b[1] - b[0] >= 8. * w
        }))
    };
    let (Some(east_outer), Some(east_inner)) = (side_line(side), side_line(-side)) else {
        return Some(None);
    };
    let mut cap = thick
        .iter()
        .copied()
        .filter_map(|p| {
            let [a, b] = frame.span(p);
            (frame.eq(a[0], side)
                && frame.eq(a[1], side)
                && b[0] >= -eps
                && b[1] <= depth + eps
                && b[1] - b[0] > eps)
                .then_some((b, p))
        })
        .collect::<Vec<_>>();
    cap.sort_unstable_by(|a, b| a.0[0].total_cmp(&b.0[0]).then(a.1.id.cmp(b.1.id)));
    if cap.is_empty() || cap.len() > 8 || !cap.iter().any(|(_, p)| p.id == r.end_cap_source_id) {
        return Some(None);
    }
    let mut next = 0.;
    for ([a, b], _) in &cap {
        if !frame.eq(*a, next) {
            return Some(None);
        }
        next = *b;
    }
    if !frame.eq(next, depth) {
        return Some(None);
    }
    let turns = [top.id, inner.id, east_outer.id, east_inner.id]
        .into_iter()
        .chain(cap.iter().map(|(_, p)| p.id))
        .collect::<Vec<_>>();
    if turns
        .iter()
        .copied()
        .map(path)
        .collect::<BTreeSet<_>>()
        .len()
        != turns.len()
    {
        return Some(None);
    }
    let mut ids = turns
        .into_iter()
        .map(str::to_owned)
        .collect::<BTreeSet<_>>();
    ids.extend([r.paint_source_id.clone(), host_paint.id.clone()]);
    Some(Some(Topology {
        host: host.id.clone(),
        points: [host.start, target],
        toe,
        sources: ids.into_iter().collect(),
    }))
}

fn rect(a: Point, b: Point, w: f64) -> [Point; 4] {
    let d = (b - a).unit();
    let n = Point::new(-d.y, d.x) * (w * 0.5);
    [a + n, b + n, b - n, a - n]
}

fn projected(p: &[Point], start: Point, u: Point) -> [[f64; 2]; 2] {
    p.iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], &p| {
            let q = p - start;
            let a = q.dot(u);
            let b = u.cross(q);
            [[x[0].min(a), x[1].max(a)], [y[0].min(b), y[1].max(b)]]
        })
}

fn overlaps(a: [Point; 4], p: &[Point]) -> bool {
    let u = (a[1] - a[0]).unit();
    let [[x, y], [a0, a1]] = projected(&a, a[0], u);
    let [[s, t], [b0, b1]] = projected(p, a[0], u);
    y.min(t) - x.max(s) > 1e-7 && a1.min(b1) - a0.max(b0) > 1e-7
}

fn occupied(page: &PageScan, stock: &Measured) -> bool {
    let rejected = |p: &[Point]| overlaps(stock.outline, p);
    let walls = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    page.walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready && d.reason != REASON)
        .any(|(w, d)| {
            let w = effective_wall(w, Some(d));
            let shape = if let Some(c) = w.curve {
                let r = c.radius + w.thickness * 0.5;
                let v = Point::new(r, 0.);
                rect(c.center - v, c.center + v, 2. * r)
            } else {
                rect(w.start, w.end, w.thickness)
            };
            rejected(&shape)
        })
        || wall_layers::layers(page).any(|p| {
            walls.get(p.owner_id).is_some_and(|w| {
                let f = p.footprint(w);
                rejected(&rect(f.start, f.end, f.thickness))
            })
        })
        || wall_layers::contacts(page)
            .iter()
            .any(|c| rejected(&c.outline))
        || page
            .objects
            .iter()
            .filter_map(|o| o.footprint.as_ref())
            .any(|p| rejected(&p.polygon))
        || page
            .withheld_walls
            .iter()
            .flatten()
            .filter_map(|m| m.furniture_outline.as_ref())
            .any(|p| rejected(p))
        || page.opening_evidence().any(|o| {
            let width = o
                .host_wall_ids
                .iter()
                .filter_map(|id| walls.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(stock.width, f64::max);
            rejected(&rect(o.start, o.end, width))
        })
}

struct Candidate<'a> {
    ordinal: usize,
    receipt: &'a Receipt,
    stock: Measured,
    connection: Topology,
}

fn candidates(page: &PageScan) -> Option<Vec<Candidate<'_>>> {
    if page.capped_stock.is_empty() || page.stepped_stock.is_empty() {
        return Some(vec![]);
    }
    let ink = page
        .primitives
        .iter()
        .filter_map(Ink::native)
        .collect::<Vec<_>>();
    let by_id = ink.iter().map(|&p| (p.id, p)).collect::<HashMap<_, _>>();
    let primitives = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let active = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(w, d)| stepped_facade::is_stock(w) && d.status == WallStatus::Ready)
        .map(|(w, _)| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    let mut hosts = Vec::new();
    for (i, r) in page.stepped_stock.iter().enumerate() {
        let id = format!("stepped-facade-stock:{}:{}:1", page.page_number, i + 1);
        let Some(w) = active.get(id.as_str()).copied() else {
            continue;
        };
        let Some(verified) = stepped_stock::verified(page, r) else {
            continue;
        };
        let [a, b, c, d] = *verified.outlines.first()?;
        if w.start != (a + d) * 0.5
            || w.end != (b + c) * 0.5
            || Some(&w.thickness) != verified.widths.first()
        {
            continue;
        }
        if let Some(p) = primitives.get(r.paint_source_id.as_str()) {
            hosts.push((w, *p));
        }
    }
    if hosts.is_empty() {
        return Some(vec![]);
    }
    let mut work = MAX_WORK;
    let mut out = Vec::<Candidate<'_>>::new();
    for (i, r) in page.capped_stock.iter().enumerate() {
        let Some(o) = capped_stock::verified(page, r) else {
            continue;
        };
        let stock = Measured::from(o);
        spend(
            &mut work,
            page.walls.len() + page.objects.len() + page.openings.len(),
        )?;
        if occupied(page, &stock) {
            continue;
        }
        let mut links = Vec::new();
        for &(w, p) in &hosts {
            if let Some(c) = turn(&stock, r, w, p, &ink, &by_id, &mut work)? {
                links.push(c);
            }
        }
        let [connection]: [Topology; 1] = match links.try_into() {
            Ok(v) => v,
            Err(_) => continue,
        };
        out.push(Candidate {
            ordinal: i + 1,
            receipt: r,
            stock,
            connection,
        });
    }
    spend(&mut work, out.len().saturating_pow(2))?;
    let mut conflict = vec![false; out.len()];
    for (i, a) in out.iter().enumerate() {
        for (j, b) in out.iter().enumerate().skip(i + 1) {
            if a.connection.host == b.connection.host || overlaps(a.stock.outline, &b.stock.outline)
            {
                conflict[i] = true;
                conflict[j] = true;
            }
        }
    }
    Some(
        out.into_iter()
            .zip(conflict)
            .filter_map(|(p, f)| (!f).then_some(p))
            .collect(),
    )
}

fn key(page: u32, ordinal: usize) -> String {
    format!("{PREFIX}{page}:{ordinal}")
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let Some(found) = candidates(page) else {
        return Ok(());
    };
    let number = page.page_number;
    let mut additions = Vec::new();
    for p in found {
        let r = p.receipt;
        let id = key(number, p.ordinal);
        let mut sources = BTreeSet::from([r.paint_source_id.clone(), r.end_cap_source_id.clone()]);
        sources.extend(
            r.face_source_ids
                .iter()
                .chain(&r.backing_pen_source_ids)
                .chain(&r.terminal_trim_source_ids)
                .chain(&r.neutral_overlap_source_ids)
                .cloned(),
        );
        let sources = sources.into_iter().collect::<Vec<_>>();
        let w = Wall {
            id: id.clone(),
            page: number,
            start: p.stock.start,
            end: p.stock.end,
            thickness: p.stock.width,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: sources.clone(),
            requires_review: false,
        };
        let d = WallDecision {
            wall_id: id.clone(),
            status: WallStatus::Ready,
            reason: REASON,
            component_id: format!("p{number}:{id}"),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: sources,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        additions.push((w, d));
    }
    if additions.is_empty() {
        return Ok(());
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("capped facade requires existing placement decisions")?;
    for (w, d) in additions {
        page.semantics.routing.push(wall_route(&w));
        page.walls.push(w);
        decisions.push(d);
    }
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let own = page
        .walls
        .iter()
        .filter(|w| is_stock(w))
        .map(|w| w.id.as_str())
        .collect::<HashSet<_>>();
    if own.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    base.walls.retain(|w| !own.contains(w.id.as_str()));
    base.semantics
        .routing
        .retain(|r| !own.contains(r.entity_id.as_str()));
    base.placement_decisions
        .as_mut()
        .ok_or("missing capped facade decisions")?
        .retain(|d| !own.contains(d.wall_id.as_str()));
    Ok(Cow::Owned(base))
}

pub(crate) fn joints(page: &PageScan) -> Vec<Joint> {
    let Some(found) = candidates(page) else {
        return vec![];
    };
    found
        .into_iter()
        .filter_map(|p| {
            let id = key(page.page_number, p.ordinal);
            page.walls
                .iter()
                .zip(page.placement_decisions.iter().flatten())
                .find(|(w, d)| {
                    w.id == id
                        && d.status == WallStatus::Ready
                        && w.start == p.stock.start
                        && w.end == p.stock.end
                        && w.thickness == p.stock.width
                })?;
            let c = p.connection;
            Some(Joint {
                wall_ids: [c.host, id],
                points: c.points,
                support_points: vec![c.toe],
                source_ids: c.sources,
            })
        })
        .collect()
}
