//! Certified paint footprints on a fully proved two-ply interior wall.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall, exterior_piers,
    finish_attachments::{self, Attachment},
    painted_rectangle, wall_layers,
};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Shape, Wall};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 8_000_000;
pub(crate) mod coextensive;
pub(crate) mod exterior_lining;
pub(crate) mod jamb_backing;

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    layer: &'a str,
    outline: [Point; 4],
}

#[derive(Clone, Copy)]
struct Band<'a> {
    id: &'a str,
    along: [f64; 2],
    across: [f64; 2],
}

#[derive(Clone, Copy)]
struct Frame {
    at: Point,
    axis: Point,
    length: f64,
    eps: f64,
}

impl Frame {
    fn from(w: &Wall) -> Option<Self> {
        let length = w.start.distance(w.end);
        (w.body == BodyEvidence::FilledBody
            && w.curve.is_none()
            && length.is_finite()
            && w.thickness.is_finite()
            && w.thickness > 0.
            && length >= 8. * w.thickness)
            .then_some(Self {
                at: w.start,
                axis: (w.end - w.start).unit(),
                length,
                eps: 128. * f64::EPSILON * w.start.norm().max(w.end.norm()).max(length).max(1.),
            })
    }
    fn local(self, p: Point) -> Point {
        let q = p - self.at;
        Point::new(self.axis.dot(q), self.axis.cross(q))
    }
    fn bounds(self, points: [Point; 4]) -> [[f64; 2]; 2] {
        points.map(|v| self.local(v)).into_iter().fold(
            [[f64::INFINITY, f64::NEG_INFINITY]; 2],
            |[[a, b], [u, v]], p| [[a.min(p.x), b.max(p.x)], [u.min(p.y), v.max(p.y)]],
        )
    }
    fn band(self, paint: Paint<'_>) -> Option<Band<'_>> {
        let p = paint.outline.map(|q| self.local(q));
        let [along, across] = self.bounds(paint.outline);
        if along[1] - along[0] <= self.eps || across[1] - across[0] <= self.eps {
            return None;
        }
        along
            .into_iter()
            .all(|x| {
                across
                    .into_iter()
                    .all(|y| p.iter().any(|q| q.distance(Point::new(x, y)) <= self.eps))
            })
            .then_some(Band {
                id: paint.id,
                along,
                across,
            })
    }
    fn outline(self, [a, b]: [f64; 2], [lo, hi]: [f64; 2]) -> [Point; 4] {
        let n = Point::new(-self.axis.y, self.axis.x);
        let at = |x, y| self.at + self.axis * x + n * y;
        [at(a, lo), at(b, lo), at(b, hi), at(a, hi)]
    }
    fn same(self, a: [f64; 2], b: [f64; 2]) -> bool {
        (a[0] - b[0]).abs().max((a[1] - b[1]).abs()) <= self.eps
    }
}

struct Stock<'a> {
    wall: &'a Wall,
    decision: &'a WallDecision,
    paint: Paint<'a>,
}

fn exact_stock<'a>(
    wall: &'a Wall,
    decision: &'a WallDecision,
    sources: &HashMap<&str, Paint<'a>>,
) -> Option<Stock<'a>> {
    let [source] = wall.source_ids.as_slice() else {
        return None;
    };
    let paint = *sources.get(source.as_str())?;
    if decision.placement_extent.is_some() || !decision.opening_ids.is_empty() {
        return None;
    }
    let frame = Frame::from(wall)?;
    let band = frame.band(paint)?;
    (frame.same(band.along, [0., frame.length])
        && frame.same(band.across, [-wall.thickness * 0.5, wall.thickness * 0.5]))
    .then_some(Stock {
        wall,
        decision,
        paint,
    })
}

fn named_visible_paint(page: &PageScan) -> Vec<Paint<'_>> {
    let visible = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let masked = page
        .placement_contact_paint
        .iter()
        .filter(|r| !r.occluder_source_ids.is_empty())
        .map(|r| r.source_id.as_str())
        .collect::<HashSet<_>>();
    page.primitives
        .iter()
        .filter_map(|p| {
            let layer = p.layer.as_deref()?;
            (visible.contains(p.id.as_str())
                && !masked.contains(p.id.as_str())
                && crate::layers::is_named_wall(layer)
                && (p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall))
                .then_some(())?;
            Some(Paint {
                id: &p.id,
                layer,
                outline: painted_rectangle(&p.shape)?,
            })
        })
        .collect::<Vec<_>>()
}

fn protected_bodies(page: &PageScan) -> Vec<[Point; 4]> {
    let mut occupied = exterior_piers::aperture_guard(page);
    occupied.extend(page.primitives.iter().filter_map(|p| {
        let Shape::Image { min, max } = p.shape else {
            return None;
        };
        Some([min, Point::new(max.x, min.y), max, Point::new(min.x, max.y)])
    }));
    occupied.extend(
        crate::placement::corner_joints(page)
            .iter()
            .filter(|j| j.adds_material)
            .map(|j| j.polygon),
    );
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if d.status != WallStatus::Ready {
            continue;
        }
        let w = effective_wall(w, Some(d));
        if let Some(c) = w.curve {
            let r = c.radius + w.thickness * 0.5;
            let x = Point::new(r, 0.);
            occupied.push(exterior_piers::corners(c.center - x, c.center + x, 2. * r));
        } else {
            occupied.push(exterior_piers::corners(w.start, w.end, w.thickness));
        }
    }
    let by_wall = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    occupied.extend(wall_layers::layers(page).filter_map(|l| {
        let w = l.footprint(by_wall.get(l.owner_id)?);
        Some(exterior_piers::corners(w.start, w.end, w.thickness))
    }));
    occupied.extend(wall_layers::contacts(page).into_iter().map(|j| j.outline));
    let finishes = finish_attachments::attachments(page, &occupied);
    occupied.extend(finishes.into_iter().map(|f| f.outline));
    occupied.extend(
        attachments(page, &occupied)
            .into_iter()
            .map(|f| f.outline)
            .collect::<Vec<_>>(),
    );
    occupied.extend(
        page.withheld_walls
            .iter()
            .flatten()
            .filter_map(|w| w.furniture_outline),
    );
    occupied.extend(
        page.objects
            .iter()
            .filter_map(|o| o.footprint.as_ref().map(|f| f.polygon)),
    );
    occupied
}

fn cover(bands: &[Band<'_>], span: [f64; 2]) -> f64 {
    let mut parts = bands.iter().map(|b| b.along).collect::<Vec<_>>();
    parts.sort_unstable_by(|a, b| a[0].total_cmp(&b[0]));
    let mut end = span[0];
    let mut total = 0.;
    for [a, b] in parts {
        let [lo, hi] = [a.max(end), b.min(span[1])];
        if hi > lo {
            total += hi - lo;
            end = hi;
        }
    }
    total
}

fn wall_bands<'a>(
    frame: Frame,
    long: Band<'a>,
    short: Band<'a>,
    stock: &[Band<'a>],
    span: [f64; 2],
    ply_width: f64,
    support: &HashSet<&str>,
    work: &mut usize,
) -> Option<Option<Vec<Band<'a>>>> {
    let [left, right] = if long.across[0] < short.across[0] {
        [long, short]
    } else {
        [short, long]
    };
    let length = span[1] - span[0];
    let gap = [left.across[1], right.across[0]];
    *work = work.checked_sub(stock.len())?;
    let mut out = stock
        .iter()
        .copied()
        .filter(|s| support.contains(s.id) && frame.same(gap, s.across))
        .collect::<Vec<_>>();
    if cover(&out, span) < length - frame.eps {
        return Some(None);
    }
    for (boundary, side) in [(left.across[0], -1.), (right.across[1], 1.)] {
        let mut border = boundary;
        let mut prior_width = None::<f64>;
        // Keep each physically measured band. The limit bounds evidence
        // expansion away from either leaf.
        for _ in 0..2 {
            *work = work.checked_sub(stock.len())?;
            let mut choices = stock
                .iter()
                .copied()
                .filter_map(|s| {
                    let (near, far) = if side > 0. {
                        (s.across[0], s.across[1])
                    } else {
                        (s.across[1], s.across[0])
                    };
                    let width = (far - near).abs();
                    ((near - border).abs() <= frame.eps
                        && width <= ply_width * 0.5
                        && prior_width.is_none_or(|p| (p - width).abs() <= p * 0.01))
                    .then_some((far, s))
                })
                .collect::<Vec<_>>();
            choices.sort_unstable_by(|a, b| a.0.total_cmp(&b.0).then(a.1.id.cmp(b.1.id)));
            let mut result = None;
            for group in choices.chunk_by(|a, b| (a.0 - b.0).abs() <= frame.eps) {
                *work = work.checked_sub(group.len().saturating_mul(3))?;
                let bands = group.iter().map(|(_, p)| *p).collect::<Vec<_>>();
                if cover(&bands, span) >= length * 0.9 {
                    if result.is_some() {
                        return Some(None);
                    }
                    result = Some((group[0].0, bands));
                }
            }
            let Some((far, bands)) = result else {
                return Some(None);
            };
            prior_width = Some((far - border).abs());
            border = far;
            out.extend(bands);
        }
    }
    Some(Some(out))
}

fn unoccupied(
    frame: Frame,
    band: Band<'_>,
    span: [f64; 2],
    occupied: &[[Point; 4]],
    work: &mut usize,
) -> Option<Vec<[f64; 2]>> {
    let [start, end] = [span[0].max(band.along[0]), span[1].min(band.along[1])];
    if end - start <= frame.eps {
        return Some(vec![]);
    }
    *work = work.checked_sub(occupied.len())?;
    let outline = frame.outline([start, end], band.across);
    let mut masks = occupied
        .iter()
        .filter_map(|&p| {
            if !exterior_piers::overlaps(outline, p, frame.eps) {
                return None;
            }
            let [[a, b], _] = frame.bounds(p);
            Some([a.max(start), b.min(end)])
        })
        .collect::<Vec<_>>();
    *work = work.checked_sub(
        masks
            .len()
            .saturating_mul(masks.len().max(1).ilog2() as usize + 1),
    )?;
    masks.sort_unstable_by(|a, b| a[0].total_cmp(&b[0]));
    let mut result = vec![];
    let mut at = start;
    for [lo, hi] in masks {
        if lo > at + frame.eps {
            result.push([at, lo]);
        }
        at = hi.max(at);
    }
    if end > at + frame.eps {
        result.push([at, end]);
    }
    Some(result)
}

fn proposes<'a>(
    page: &'a PageScan,
    extra_occupied: &[[Point; 4]],
    work: &mut usize,
) -> Option<Vec<Attachment<'a>>> {
    let pairs = wall_layers::layers(page)
        .filter(|l| {
            l.decision.reason == WallReason::SourceBoundedCavityWallLeaf
                && l.decision.placement_extent.is_none()
        })
        .collect::<Vec<_>>();
    if pairs.is_empty() {
        return Some(vec![]);
    }
    let allowed = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let masked = page
        .placement_contact_paint
        .iter()
        .filter(|r| !r.occluder_source_ids.is_empty())
        .map(|r| r.source_id.as_str())
        .collect::<HashSet<_>>();
    *work = work.checked_sub(page.primitives.len())?;
    let paints = page
        .primitives
        .iter()
        .filter_map(|p| {
            let layer = p.layer.as_deref()?;
            (allowed.contains(p.id.as_str())
                && !masked.contains(p.id.as_str())
                && crate::layers::is_named_wall(layer)
                && !crate::layers::is_named_exterior_wall(layer)
                && (p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall))
                .then_some(())?;
            Some(Paint {
                id: &p.id,
                layer,
                outline: painted_rectangle(&p.shape)?,
            })
        })
        .collect::<Vec<_>>();
    let by_source = paints.iter().map(|p| (p.id, *p)).collect::<HashMap<_, _>>();
    let walls = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .map(|(w, d)| (w.id.as_str(), (w, d)))
        .collect::<HashMap<_, _>>();
    let paint_for = |wall: &Wall| {
        let mut ids = wall
            .source_ids
            .iter()
            .filter_map(|s| by_source.get(s.as_str()));
        let first = *ids.next()?;
        ids.next().is_none().then_some(first)
    };
    let mut occupied = extra_occupied.to_vec();
    occupied.extend(exterior_piers::aperture_guard(page));
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if d.status != WallStatus::Ready {
            continue;
        }
        if let Some(c) = w.curve {
            let r = c.radius + w.thickness * 0.5;
            let side = Point::new(r, 0.);
            occupied.push(exterior_piers::corners(
                c.center - side,
                c.center + side,
                2. * r,
            ));
        } else if w.thickness > 0. && w.start != w.end {
            occupied.push(exterior_piers::corners(w.start, w.end, w.thickness));
        }
    }
    for layer in wall_layers::layers(page) {
        if let Some((owner, _)) = walls.get(layer.owner_id) {
            let w = layer.footprint(owner);
            let w = effective_wall(&w, Some(layer.decision));
            occupied.push(exterior_piers::corners(w.start, w.end, w.thickness));
        }
    }
    let mut result = Vec::new();
    for layer in pairs {
        let Some(&(owner, decision)) = walls.get(layer.owner_id) else {
            continue;
        };
        if decision.status != WallStatus::Ready
            || decision.reason != WallReason::SourceBoundedDoubleInsetCavityWall
            || decision.placement_extent.is_some()
            || !matches!(decision.support_wall_ids.as_slice(),[id] if id==&layer.wall.id)
        {
            continue;
        }
        let [Some(a), Some(b)] = [paint_for(owner), paint_for(layer.wall)] else {
            continue;
        };
        let Some(frame) = Frame::from(owner) else {
            continue;
        };
        let [Some(long), Some(short)] = [frame.band(a), frame.band(b)] else {
            continue;
        };
        let span = short.along;
        let width = owner.thickness.min(layer.wall.thickness);
        if a.layer != b.layer
            || span[0] < width * 0.2
            || frame.length - span[1] < width * 0.2
            || !frame.same(long.along, [0., frame.length])
            || span[1] - span[0] < frame.length * 0.9
        {
            continue;
        }
        let support = decision
            .support_source_ids
            .iter()
            .map(String::as_str)
            .collect::<HashSet<_>>();
        *work = work.checked_sub(paints.len())?;
        let stock = paints
            .iter()
            .copied()
            .filter(|p| p.id != a.id && p.id != b.id && p.layer == a.layer)
            .filter_map(|p| frame.band(p))
            .filter(|p| p.along[1] > span[0] + frame.eps && p.along[0] < span[1] - frame.eps)
            .collect::<Vec<_>>();
        let Some(mut bands) = wall_bands(frame, long, short, &stock, span, width, &support, work)?
        else {
            continue;
        };
        bands.sort_unstable_by(|a, b| {
            a.across[0]
                .total_cmp(&b.across[0])
                .then(a.along[0].total_cmp(&b.along[0]))
                .then(a.id.cmp(b.id))
        });
        for band in bands {
            let sources = [a.id, b.id, band.id]
                .into_iter()
                .collect::<BTreeSet<_>>()
                .into_iter()
                .collect::<Vec<_>>();
            for kept in unoccupied(frame, band, span, &occupied, work)? {
                let outline = frame.outline(kept, band.across);
                occupied.push(outline);
                result.push(Attachment {
                    owner_id: layer.owner_id,
                    paint_id: band.id,
                    outline,
                    source_ids: sources.clone(),
                });
            }
        }
    }
    Some(result)
}

pub(crate) fn attachments<'a>(page: &'a PageScan, occupied: &[[Point; 4]]) -> Vec<Attachment<'a>> {
    proposes(page, occupied, &mut MAX_WORK.clone()).unwrap_or_default()
}
