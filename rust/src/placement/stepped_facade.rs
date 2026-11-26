//! Preserve a stepped source silhouette and attach its signed square turn.
use super::{WallDecision, WallReason, WallStatus, effective_wall, wall_layers};
use crate::{
    input::stepped_stock::{self, Receipt},
    model::{BodyEvidence, OpeningEvidence, PageScan, Point, Primitive, Shape, Wall},
    semantics::wall_route,
};
use std::{
    borrow::Cow,
    collections::{BTreeSet, HashMap, HashSet},
};

const PREFIX: &str = "stepped-facade-stock:";
const REASON: WallReason = WallReason::SourceBoundedSteppedFacade;
const MAX_WORK: usize = 2_000_000;

pub(crate) fn is_stock(wall: &Wall) -> bool {
    wall.id.starts_with(PREFIX)
}

pub(crate) struct Joint {
    pub wall_ids: [String; 2],
    pub points: [Point; 2],
    pub support_points: Vec<Point>,
    pub source_ids: Vec<String>,
}

#[derive(Clone, Copy)]
struct Piece {
    a: Point,
    b: Point,
    width: f64,
    outline: [Point; 4],
}

impl Piece {
    fn measured(outline: [Point; 4], width: f64) -> Self {
        let [a, b, c, d] = outline;
        Self {
            a: (a + d) * 0.5,
            b: (b + c) * 0.5,
            width,
            outline,
        }
    }
}

#[derive(Clone, Copy)]
struct Line<'a> {
    id: &'a str,
    a: Point,
    b: Point,
    pen: f64,
}

impl<'a> Line<'a> {
    fn native(p: &'a Primitive) -> Option<Self> {
        let Shape::Line { start: a, end: b } = p.shape else {
            return None;
        };
        let pen = p.stroke_width?;
        (p.layer.is_none()
            && p.id.starts_with("pdf:")
            && p.id.contains(":path:")
            && !p.id.contains(":clip:")
            && a != b
            && pen.is_finite()
            && pen > 0.)
            .then_some(Self {
                id: &p.id,
                a,
                b,
                pen,
            })
    }
}

fn charge(work: &mut usize, n: usize) -> Option<()> {
    *work = work.checked_sub(n)?;
    Some(())
}
fn near(a: f64, b: f64, eps: f64) -> bool {
    (a - b).abs() <= eps
}
fn order(a: f64, b: f64) -> [f64; 2] {
    [a.min(b), a.max(b)]
}
fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

fn outline(a: Point, b: Point, w: f64) -> [Point; 4] {
    let u = (b - a).unit();
    let n = Point::new(-u.y, u.x) * (w * 0.5);
    [a + n, b + n, b - n, a - n]
}

struct TurnFrame {
    origin: Point,
    into: Point,
    south: Point,
    width: f64,
    eps: f64,
}

impl TurnFrame {
    fn at(&self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(d.dot(self.into), d.dot(self.south))
    }

    fn extents(&self, p: Line<'_>) -> [[f64; 2]; 2] {
        let [a, b] = [p.a, p.b].map(|p| self.at(p));
        [order(a.x, b.x), order(a.y, b.y)]
    }

    fn equal(&self, a: f64, b: f64) -> bool {
        near(a, b, self.eps)
    }

    fn horizontal(&self, l: Line<'_>, y: f64) -> bool {
        let [[a, b], [lo, hi]] = self.extents(l);
        b - a > self.eps && self.equal(lo, y) && self.equal(hi, y)
    }

    fn vertical(&self, l: Line<'_>, x: f64) -> bool {
        let [[lo, hi], [a, b]] = self.extents(l);
        b - a > self.eps && self.equal(lo, x) && self.equal(hi, x)
    }
}

struct Corner {
    host: String,
    from: Point,
    to: Point,
    toe: Point,
    sources: Vec<String>,
}

fn strict_one<T>(items: impl Iterator<Item = T>) -> Option<T> {
    let mut all = items;
    let a = all.next()?;
    all.next().is_none().then_some(a)
}

fn corner_chain<'a>(
    f: &TurnFrame,
    ink: &[Line<'a>],
    face_x: f64,
    host_len: f64,
    work: &mut usize,
) -> Option<Option<Vec<&'a str>>> {
    charge(work, ink.len())?;
    let thick = ink
        .iter()
        .copied()
        .filter(|p| (0.23..=0.48).contains(&(p.pen / f.width)))
        .collect::<Vec<_>>();
    let leads = thick
        .iter()
        .copied()
        .filter(|&p| {
            let [[a, b], [y, z]] = f.extents(p);
            f.equal(y, z)
                && f.equal(a, -host_len)
                && f.equal(b, 0.)
                && (0.50..=0.58).contains(&(y / f.width))
        })
        .collect::<Vec<_>>();
    charge(
        work,
        leads.len().saturating_mul(thick.len()).saturating_mul(4),
    )?;
    let mut candidate = None;
    for lead in leads {
        let y = f.extents(lead)[1][0];
        let shift = y - f.width * 0.5;
        let far_x = face_x + shift;
        let match_pen = |p: Line<'_>| f.equal(p.pen, lead.pen);
        let outer_run = strict_one(thick.iter().copied().filter(|&p| {
            let [x, _] = f.extents(p);
            match_pen(p) && f.horizontal(p, y) && f.equal(x[0], 0.) && f.equal(x[1], far_x)
        }));
        let outer_side = strict_one(thick.iter().copied().filter(|&p| {
            let [_, s] = f.extents(p);
            match_pen(p) && f.vertical(p, far_x) && f.equal(s[1], y) && y - s[0] >= 8. * f.width
        }));
        let inner_run = strict_one(thick.iter().copied().filter(|&p| {
            let [x, _] = f.extents(p);
            match_pen(p)
                && f.horizontal(p, -y)
                && f.equal(x[1], 0.)
                && x[0] <= -0.5 * f.width
                && x[0] >= -host_len - f.eps
        }));
        let inner_side = strict_one(thick.iter().copied().filter(|&p| {
            let [_, s] = f.extents(p);
            match_pen(p) && f.vertical(p, 0.) && f.equal(s[1], -y) && -y - s[0] >= 8. * f.width
        }));
        let (Some(a), Some(b), Some(c), Some(d)) = (outer_run, outer_side, inner_run, inner_side)
        else {
            continue;
        };
        let ids = [lead.id, a.id, b.id, c.id, d.id];
        if ids.into_iter().map(path).collect::<BTreeSet<_>>().len() != 5 {
            continue;
        }
        if candidate.is_some() {
            return Some(None);
        }
        candidate = Some(ids.to_vec());
    }
    Some(candidate)
}

fn sibling_corner(
    page: &PageScan,
    last: Piece,
    r: &Receipt,
    ink: &[Line<'_>],
    work: &mut usize,
) -> Option<Option<Corner>> {
    let walls = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten());
    let primitives = page
        .primitives
        .iter()
        .map(|p| (p.id.as_str(), p))
        .collect::<HashMap<_, _>>();
    let paint = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let south = (last.b - last.a).unit();
    let w = last.width;
    let eps = 0.0001 * w;
    let mut found = None;
    for (host, d) in walls.filter(|(_, d)| {
        d.status == WallStatus::Ready && d.reason == WallReason::SourceBoundedOverheadGateStock
    }) {
        let effective = effective_wall(host, Some(d));
        if effective.curve.is_some() || !near(effective.thickness, w, eps) {
            continue;
        }
        let [only_gate] = d.opening_ids.as_slice() else {
            continue;
        };
        let Some(gate) = page.openings.iter().find(|o| {
            o.id == *only_gate
                && o.evidence == OpeningEvidence::LayerlessOverheadProjection
                && o.operation == "Overhead"
                && o.host_wall_ids.contains(&host.id)
        }) else {
            continue;
        };
        let toward_gate = |p: Point, other: Point| {
            let u = (other - p).unit();
            [gate.start, gate.end].into_iter().any(|v| {
                let d = v - p;
                d.dot(u).abs() <= eps && d.cross(u).abs() <= 1.6 * w
            })
        };
        let endpoints = [effective.start, effective.end];
        for [start, tip] in [endpoints, [endpoints[1], endpoints[0]]] {
            if !toward_gate(start, tip) || toward_gate(tip, start) {
                continue;
            }
            let into = (tip - start).unit();
            if into.dot(south).abs() > 1e-8 || !near((last.b - tip).dot(south), 0.5 * w, eps) {
                continue;
            }
            let target = last.b - south * (0.5 * w);
            let x = (target - tip).dot(into);
            let separation = x - 0.5 * w;
            if !(0.0..=0.08 * w).contains(&separation)
                || (target - tip).cross(into).abs() > eps
                || (target - last.a).dot(south) <= eps
            {
                continue;
            }
            let toe = last.b - into * (0.5 * w);
            let f = TurnFrame {
                origin: tip,
                into,
                south,
                width: w,
                eps,
            };
            let host_len = tip.distance(start);
            let mut painted = host.source_ids.iter().filter_map(|id| {
                if !paint.contains(id.as_str()) || id == &r.paint_source_id {
                    return None;
                }
                let p = primitives.get(id.as_str())?;
                let Shape::Polyline {
                    points,
                    closed: true,
                    filled: true,
                } = &p.shape
                else {
                    return None;
                };
                if p.layer.is_some() || !(4..=8).contains(&points.len()) {
                    return None;
                }
                let points = points.iter().map(|&p| f.at(p)).collect::<Vec<_>>();
                // The independently visible source quadrilateral includes
                // the inscribed host and exactly meets the stepped stock toe.
                let wanted = [
                    Point::new(-host_len, -0.5 * w),
                    Point::new(0., -0.5 * w),
                    f.at(toe),
                    Point::new(-host_len, 0.5 * w),
                ];
                (points
                    .iter()
                    .all(|p| wanted.iter().any(|q| p.distance(*q) <= eps))
                    && wanted
                        .iter()
                        .all(|p| points.iter().any(|q| p.distance(*q) <= eps)))
                .then_some(id.as_str())
            });
            let Some(host_paint) = painted.next() else {
                continue;
            };
            if painted.next().is_some() {
                continue;
            }
            let Some(chain) = corner_chain(&f, ink, x + 0.5 * w, host_len, work)? else {
                continue;
            };
            charge(work, ink.len())?;
            let Some(toe_link) = strict_one(ink.iter().copied().filter(|&p| {
                let [a, b] = f.extents(p)[0];
                (0.025..=0.22).contains(&(p.pen / w))
                    && f.horizontal(p, 0.5 * w)
                    && f.equal(a, 0.)
                    && f.equal(b, separation)
            })) else {
                continue;
            };
            let mut ids = BTreeSet::from([
                host_paint.to_owned(),
                r.paint_source_id.clone(),
                r.outer_face_source_id.clone(),
                r.end_cap_source_id.clone(),
                toe_link.id.to_owned(),
            ]);
            ids.extend(chain.into_iter().map(str::to_owned));
            if found.is_some() {
                return Some(None);
            }
            found = Some(Corner {
                host: host.id.clone(),
                from: tip,
                to: target,
                toe,
                sources: ids.into_iter().collect(),
            });
        }
    }
    Some(found)
}

fn ranges(p: &[Point], o: Point, u: Point) -> [[f64; 2]; 2] {
    p.iter()
        .fold([[f64::INFINITY, f64::NEG_INFINITY]; 2], |[x, y], &p| {
            let d = p - o;
            let a = d.dot(u);
            let b = u.cross(d);
            [[x[0].min(a), x[1].max(a)], [y[0].min(b), y[1].max(b)]]
        })
}

fn overlaps(area: &[Point; 4], other: &[Point]) -> bool {
    let u = (area[1] - area[0]).unit();
    let [[a, b], [c, d]] = ranges(area, area[0], u);
    let [[e, f], [g, h]] = ranges(other, area[0], u);
    b.min(f) - a.max(e) > 1e-7 && d.min(h) - c.max(g) > 1e-7
}

fn occupied(page: &PageScan, parts: &[Piece]) -> bool {
    let overlap = |p: &[Point]| parts.iter().any(|b| overlaps(&b.outline, p));
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
            let footprint = if let Some(c) = w.curve {
                let r = c.radius + w.thickness * 0.5;
                let offset = Point::new(r, 0.);
                outline(c.center - offset, c.center + offset, 2. * r)
            } else {
                outline(w.start, w.end, w.thickness)
            };
            overlap(&footprint)
        })
        || wall_layers::layers(page).any(|p| {
            walls.get(p.owner_id).is_some_and(|w| {
                let f = p.footprint(w);
                overlap(&outline(f.start, f.end, f.thickness))
            })
        })
        || wall_layers::contacts(page)
            .iter()
            .any(|c| overlap(&c.outline))
        || page
            .objects
            .iter()
            .filter_map(|o| o.footprint.as_ref())
            .any(|p| overlap(&p.polygon))
        || page
            .withheld_walls
            .iter()
            .flatten()
            .filter_map(|m| m.furniture_outline.as_ref())
            .any(|p| overlap(p))
        || page.opening_evidence().any(|o| {
            let w = o
                .host_wall_ids
                .iter()
                .filter_map(|id| walls.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(parts[0].width, f64::max);
            overlap(&outline(o.start, o.end, w))
        })
}

struct Candidate<'a> {
    number: usize,
    receipt: &'a Receipt,
    parts: Vec<Piece>,
    corner: Corner,
}

fn candidates(page: &PageScan) -> Option<Vec<Candidate<'_>>> {
    if page.stepped_stock.is_empty() {
        return Some(vec![]);
    }
    let mut work = MAX_WORK;
    let ink = page
        .primitives
        .iter()
        .filter_map(Line::native)
        .collect::<Vec<_>>();
    let mut out: Vec<Candidate<'_>> = Vec::new();
    for (i, r) in page.stepped_stock.iter().enumerate() {
        let Some(v) = stepped_stock::verified(page, r) else {
            continue;
        };
        let parts = v
            .outlines
            .into_iter()
            .zip(v.widths)
            .map(|(a, b)| Piece::measured(a, b))
            .collect::<Vec<_>>();
        let last = *parts.last()?;
        let Some(corner) = sibling_corner(page, last, r, &ink, &mut work)? else {
            continue;
        };
        charge(&mut work, page.walls.len().saturating_mul(parts.len()))?;
        if occupied(page, &parts) {
            continue;
        }
        out.push(Candidate {
            number: i + 1,
            receipt: r,
            parts,
            corner,
        });
    }
    charge(&mut work, out.len().saturating_pow(2).saturating_mul(81))?;
    let mut ambiguous = vec![false; out.len()];
    for (i, a) in out.iter().enumerate() {
        for (j, b) in out.iter().enumerate().skip(i + 1) {
            if a.corner.host == b.corner.host
                || a.parts
                    .iter()
                    .any(|p| b.parts.iter().any(|q| overlaps(&p.outline, &q.outline)))
            {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    Some(
        out.into_iter()
            .zip(ambiguous)
            .filter_map(|(p, no)| (!no).then_some(p))
            .collect(),
    )
}

fn id(page: u32, number: usize, part: usize) -> String {
    format!("{PREFIX}{page}:{number}:{}", part + 1)
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let number = page.page_number;
    let mut additions = Vec::new();
    let Some(found) = candidates(page) else {
        return Ok(());
    };
    for c in found {
        for (i, p) in c.parts.iter().enumerate() {
            let id = id(number, c.number, i);
            let mut sources = BTreeSet::from([
                c.receipt.paint_source_id.clone(),
                c.receipt.outer_face_source_id.clone(),
                c.receipt.inner_face_source_ids[i].clone(),
            ]);
            if i > 0 {
                sources.insert(c.receipt.return_source_ids[i - 1].clone());
            }
            if i + 1 < c.parts.len() {
                sources.insert(c.receipt.return_source_ids[i].clone());
            } else {
                sources.insert(c.receipt.end_cap_source_id.clone());
            }
            sources.extend(c.receipt.neutral_crossing_source_ids.iter().cloned());
            let sources = sources.into_iter().collect::<Vec<_>>();
            let wall = Wall {
                id: id.clone(),
                page: number,
                start: p.a,
                end: p.b,
                thickness: p.width,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: sources.clone(),
                requires_review: false,
            };
            let decision = WallDecision {
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
            additions.push((wall, decision));
        }
    }
    if additions.is_empty() {
        return Ok(());
    }
    let decisions = page
        .placement_decisions
        .as_mut()
        .ok_or("stepped facade requires existing Ready decisions")?;
    for (wall, decision) in additions {
        page.semantics.routing.push(wall_route(&wall));
        page.walls.push(wall);
        decisions.push(decision);
    }
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let ids = page
        .walls
        .iter()
        .filter(|w| is_stock(w))
        .map(|w| w.id.as_str())
        .collect::<HashSet<_>>();
    if ids.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut base = page.clone();
    base.walls.retain(|w| !ids.contains(w.id.as_str()));
    base.semantics
        .routing
        .retain(|r| !ids.contains(r.entity_id.as_str()));
    base.placement_decisions
        .as_mut()
        .ok_or("missing stepped facade wall decisions")?
        .retain(|d| !ids.contains(d.wall_id.as_str()));
    Ok(Cow::Owned(base))
}

pub(crate) fn joints(page: &PageScan) -> Vec<Joint> {
    let Some(found) = candidates(page) else {
        return vec![];
    };
    let ready = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, _)| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    let mut out = Vec::new();
    for c in found {
        let ids = (0..c.parts.len())
            .map(|i| id(page.page_number, c.number, i))
            .collect::<Vec<_>>();
        if !ids.iter().zip(&c.parts).all(|(id, p)| {
            ready
                .get(id.as_str())
                .is_some_and(|w| w.start == p.a && w.end == p.b && w.thickness == p.width)
        }) {
            continue;
        }
        for (i, p) in c.parts.windows(2).enumerate() {
            let points = [p[0].b, p[1].a];
            out.push(Joint {
                wall_ids: [ids[i].clone(), ids[i + 1].clone()],
                points,
                support_points: vec![(points[0] + points[1]) * 0.5],
                source_ids: vec![
                    c.receipt.paint_source_id.clone(),
                    c.receipt.outer_face_source_id.clone(),
                    c.receipt.return_source_ids[i].clone(),
                ],
            });
        }
        let b = c.corner;
        let Some(last) = ids.last() else {
            continue;
        };
        out.push(Joint {
            wall_ids: [b.host, last.clone()],
            points: [b.from, b.to],
            support_points: vec![b.toe],
            source_ids: b.sources,
        });
    }
    out
}
