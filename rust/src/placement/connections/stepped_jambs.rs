//! Attach measured exterior apertures to certified rough jamb paint.
use super::finite_paint_contacts::{Arm, convex_span, stock_contains_terminal};
use super::painted_contact::{BodyIndex, bounds};
use super::{
    Aperture, ConnectionKind, Link, MAX_EDGES, Opening, OpeningEvidence, OpeningKind, PageScan,
    Point, Rect, Solid, charge, crosses_aperture, ids,
};
use crate::input::raster::PdfRoute;
use crate::layers::named_exterior_wall;
use crate::model::{Primitive, Shape};
use std::collections::{BTreeMap, HashMap, HashSet};

pub(super) const THRESHOLD: &str = "source_bounded_stepped_exterior_aperture_threshold";
pub(super) const JAMB: &str = "source_bounded_transverse_exterior_jamb_paint";
const MAX_WORK: usize = 3_000_000;

#[derive(Clone, Copy)]
struct Stroke<'a> {
    id: &'a str,
    layer: &'a str,
    ends: [Point; 2],
    visible: bool,
}

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    layer: &'a str,
    outline: &'a [Point],
}

struct Native<'a> {
    strokes: Vec<Stroke<'a>>,
    pens: HashMap<&'a str, usize>,
    stroke_bounds: BodyIndex,
    paint: Vec<Paint<'a>>,
    fills: HashMap<&'a str, usize>,
    paint_bounds: BodyIndex,
}

fn outline(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        filled: true,
        closed: true,
    } = &p.shape
    else {
        return None;
    };
    let count = points.len() - usize::from(!points.is_empty() && points.first() == points.last());
    ((3..=8).contains(&count) && points.iter().all(|p| p.finite())).then_some(&points[..count])
}

impl<'a> Native<'a> {
    fn new(page: &'a PageScan, work: &mut usize) -> Result<Self, String> {
        charge(work, page.primitives.len())?;
        let certificates = page
            .placement_paint_ids
            .iter()
            .flatten()
            .chain(page.placement_polygon_paint_ids.iter().flatten())
            .map(String::as_str)
            .collect::<HashSet<_>>();
        let masked = page
            .placement_contact_paint
            .iter()
            .filter(|r| !r.occluder_source_ids.is_empty())
            .map(|r| r.source_id.as_str())
            .collect::<HashSet<_>>();
        let mut strokes = Vec::new();
        let mut paint = Vec::new();
        for p in &page.primitives {
            let Some(layer) = named_exterior_wall(p) else {
                continue;
            };
            if let Shape::Line { start, end } = p.shape
                && start.finite()
                && end.finite()
                && start != end
            {
                strokes.push(Stroke {
                    id: &p.id,
                    layer,
                    ends: [start, end],
                    visible: p.stroke_width.is_some_and(|w| w.is_finite() && w > 0.),
                });
            } else if certificates.contains(p.id.as_str())
                && !masked.contains(p.id.as_str())
                && let Some(outline) = outline(p)
            {
                paint.push(Paint {
                    id: &p.id,
                    layer,
                    outline,
                });
            }
        }
        Ok(Self {
            pens: strokes.iter().enumerate().map(|(i, x)| (x.id, i)).collect(),
            stroke_bounds: BodyIndex::from_bounds(
                strokes.iter().map(|x| bounds(&x.ends)).collect(),
                work,
            )?,
            fills: paint.iter().enumerate().map(|(i, x)| (x.id, i)).collect(),
            paint_bounds: BodyIndex::from_bounds(
                paint.iter().map(|x| bounds(x.outline)).collect(),
                work,
            )?,
            strokes,
            paint,
        })
    }
}

#[derive(Clone, Copy)]
struct Frame<'a> {
    start: Point,
    axis: Point,
    length: f64,
    depth: [f64; 2],
    clear: [f64; 2],
    layer: &'a str,
    rails: [&'a str; 2],
}

impl Frame<'_> {
    fn local(self, p: Point) -> Point {
        let v = p - self.start;
        Point::new(v.dot(self.axis), self.axis.cross(v))
    }
    fn world(self, u: f64, v: f64) -> Point {
        self.start + self.axis * u + Point::new(-self.axis.y, self.axis.x) * v
    }
    fn width(self) -> f64 {
        self.depth[1] - self.depth[0]
    }
}

fn supported(o: &Opening) -> bool {
    match o.kind {
        OpeningKind::Window => matches!(
            o.evidence,
            OpeningEvidence::AuthoredWindowFrame | OpeningEvidence::AuthoredExteriorRoughGlazing
        ),
        OpeningKind::Door => {
            o.evidence == OpeningEvidence::AuthoredExteriorSwingCasing
                && !o.swing_observations.is_empty()
        }
    }
}

fn frame<'a>(
    o: &Opening,
    native: &Native<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Frame<'a>>, String> {
    let length = o.start.distance(o.end);
    if !length.is_finite() || length <= eps {
        return Ok(None);
    }
    let axis = (o.end - o.start).unit();
    let local = |p: Point| Point::new((p - o.start).dot(axis), axis.cross(p - o.start));
    charge(work, o.source_ids.len())?;
    let rails = o
        .source_ids
        .iter()
        .filter_map(|id| native.pens.get(id.as_str()))
        .map(|&i| native.strokes[i])
        .filter_map(|s| {
            let [a, b] = s.ends.map(local);
            let ends = [a.x.min(b.x), a.x.max(b.x)];
            ((a.y - b.y).abs() <= eps && ends[1].min(length) - ends[0].max(0.) >= length * 0.85)
                .then_some((s, ends, a.y.midpoint(b.y)))
        })
        .collect::<Vec<_>>();
    let mut best: Option<Frame> = None;
    charge(work, rails.len().saturating_pow(2))?;
    for &(lo, a, y) in &rails {
        for &(hi, b, z) in &rails {
            let width = z - y;
            if lo.layer != hi.layer
                || !(lo.visible || hi.visible)
                || y >= -eps
                || z <= eps
                || width * 2. > length
                || (-y).min(z) < width * 0.2
                || (a[0] - b[0]).abs().max((a[1] - b[1]).abs()) > width * 0.2
                || [a, b]
                    .iter()
                    .any(|r| r[0].abs().max((r[1] - length).abs()) > width * 0.24)
            {
                continue;
            }
            let choice = Frame {
                start: o.start,
                axis,
                length,
                depth: [y, z],
                clear: [a[0].max(b[0]).max(0.), a[1].min(b[1]).min(length)],
                layer: lo.layer,
                rails: [lo.id, hi.id],
            };
            if best.is_none_or(|current| choice.width() > current.width()) {
                best = Some(choice);
            }
        }
    }
    Ok(best)
}

#[derive(Clone, Copy)]
struct End {
    solid: usize,
    cap: Point,
    outside: Point,
}

struct Jamb<'a> {
    end: End,
    jamb: Point,
    setback: f64,
    sources: Vec<&'a str>,
}

fn cap_pen<'a>(
    frame: Frame<'a>,
    station: f64,
    native: &Native<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<&'a str>, String> {
    let probe = frame.depth.map(|v| frame.world(station, v));
    let mut nearby = Vec::new();
    native
        .stroke_bounds
        .visit(0, bounds(&probe), eps, &mut nearby, work)?;
    Ok(nearby
        .into_iter()
        .map(|i| native.strokes[i])
        .filter(|s| {
            let [a, b] = s.ends.map(|p| frame.local(p));
            let [lo, hi] = [a.y.min(b.y), a.y.max(b.y)];
            s.visible
                && s.layer == frame.layer
                && (a.x - station).abs().max((b.x - station).abs()) <= eps
                && lo >= frame.depth[0] - eps
                && hi <= frame.depth[1] + eps
                && hi - lo >= frame.width() * 0.15
                && lo <= frame.width() * 0.18
                && hi >= -frame.width() * 0.18
        })
        .map(|s| s.id)
        .min())
}

fn jamb_paint<'a>(
    frame: Frame<'a>,
    cap: Point,
    jamb: Point,
    native: &Native<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Vec<&'a str>>, String> {
    let length = cap.distance(jamb);
    if length <= eps {
        return Ok(Some(Vec::new()));
    }
    let mut nearby = Vec::new();
    native
        .paint_bounds
        .visit(0, bounds(&[cap, jamb]), eps, &mut nearby, work)?;
    charge(work, nearby.len().saturating_mul(16))?;
    let mut spans = nearby
        .into_iter()
        .filter_map(|i| {
            let p = native.paint[i];
            (p.layer == frame.layer).then_some(())?;
            let extent = convex_span(p.outline, cap, jamb, eps)?;
            (extent[1] > extent[0]).then_some((extent, p.id))
        })
        .collect::<Vec<_>>();
    spans.sort_unstable_by(|a, b| {
        a.0[0]
            .total_cmp(&b.0[0])
            .then(b.0[1].total_cmp(&a.0[1]))
            .then(a.1.cmp(b.1))
    });
    let mut at = 0.;
    let mut sources = Vec::new();
    let tol = eps / length;
    for ([lo, hi], p) in spans {
        if lo > at + tol {
            break;
        }
        if hi > at {
            at = hi;
            sources.push(p);
            if at >= 1. - tol {
                return Ok(Some(sources));
            }
        }
    }
    Ok(None)
}

fn find_jamb<'a>(
    page: &PageScan,
    o: &Opening,
    frame: Frame<'a>,
    side: usize,
    ends: &[End],
    end_bounds: &BodyIndex,
    solids: &[Solid],
    native: &Native<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Jamb<'a>>, String> {
    let rough_opening =
        o.kind == OpeningKind::Door || o.evidence == OpeningEvidence::AuthoredExteriorRoughGlazing;
    let reach = if rough_opening {
        eps
    } else {
        frame.width() * 0.24
    };
    let along = if side == 0 { 0. } else { frame.length };
    let inward = if side == 0 {
        frame.axis
    } else {
        frame.axis * -1.
    };
    let edge = if side == 0 {
        along - reach
    } else {
        along + reach
    };
    let region = bounds(&[
        frame.world(along, frame.depth[0]),
        frame.world(along, frame.depth[1]),
        frame.world(edge, frame.depth[0]),
        frame.world(edge, frame.depth[1]),
    ]);
    let mut nearby = Vec::new();
    end_bounds.visit(0, region, eps, &mut nearby, work)?;
    let mut candidates = BTreeMap::new();
    for i in nearby {
        charge(work, 1)?;
        let end = ends[i];
        let measured = frame.local(end.cap);
        let solid = &solids[end.solid];
        let w = &page.walls[solid.wall];
        let setback = (frame.world(along, 0.) - end.cap).dot(inward);
        if setback < -eps
            || setback > reach + eps
            || end.outside.dot(inward) <= eps
            || end.outside.cross(inward).abs() > eps
            || solid.axis_offset.norm() > eps
            || measured.y < frame.depth[0] - eps
            || measured.y > frame.depth[1] + eps
            || (o.evidence == OpeningEvidence::AuthoredExteriorRoughGlazing
                && !o.host_wall_ids.contains(&w.id))
        {
            continue;
        }
        charge(work, w.source_ids.len().saturating_mul(16))?;
        let stock = w
            .source_ids
            .iter()
            .filter_map(|id| native.fills.get(id.as_str()))
            .map(|&i| native.paint[i])
            .filter(|p| p.layer == frame.layer)
            .find(|p| {
                stock_contains_terminal(
                    Arm {
                        at: end.cap,
                        outside: end.outside,
                        width: solid.half * 2.,
                        source_id: p.id,
                        layer: p.layer,
                    },
                    p.outline,
                    solid.length(),
                    eps,
                )
            });
        let Some(stock) = stock else { continue };
        let jamb = frame.world(measured.x, 0.);
        let Some(pen) = cap_pen(frame, measured.x, native, eps, work)? else {
            continue;
        };
        let Some(mut sources) = jamb_paint(frame, end.cap, jamb, native, eps, work)? else {
            continue;
        };
        sources.extend([stock.id, pen]);
        candidates.entry(end.solid).or_insert(Jamb {
            end,
            jamb,
            setback,
            sources,
        });
    }
    if candidates.len() != 1 {
        return Ok(None);
    }
    Ok(candidates.into_values().next())
}

fn clear_painted_throat(
    frame: Frame<'_>,
    native: &Native<'_>,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    let [a, b] = frame.clear.map(|t| frame.world(t, 0.));
    let length = a.distance(b);
    let mut nearby = Vec::new();
    native
        .paint_bounds
        .visit(0, bounds(&[a, b]), eps, &mut nearby, work)?;
    charge(work, nearby.len().saturating_mul(16))?;
    Ok(nearby.into_iter().all(|i| {
        convex_span(native.paint[i].outline, a, b, eps)
            .is_none_or(|[lo, hi]| (hi - lo) * length <= eps)
    }))
}

fn archived_openings<'a>(page: &'a PageScan) -> Vec<(Aperture<'a>, &'a str)> {
    let widths = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness * 0.5))
        .collect::<HashMap<_, _>>();
    page.window_panes
        .iter()
        .map(|p| {
            let o = &p.observation;
            let half = o
                .host_wall_ids
                .iter()
                .filter_map(|id| widths.get(id.as_str()).copied())
                .fold(0_f64, f64::max);
            (
                Aperture {
                    opening: o,
                    rect: Rect {
                        a: o.start,
                        b: o.end,
                        half,
                    },
                    framed_door: None,
                },
                p.frame_opening_id.as_str(),
            )
        })
        .collect()
}

fn candidates(
    page: &PageScan,
    solids: &[Solid],
    apertures: &[Aperture],
    links: &[Link],
    eps: f64,
    work: &mut usize,
) -> Result<Vec<Link>, String> {
    let resolved = links
        .iter()
        .filter(|link| {
            matches!(
                link.kind,
                ConnectionKind::DoorThreshold | ConnectionKind::WindowThreshold
            )
        })
        .flat_map(|link| &link.opening_ids)
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let undecided = apertures
        .iter()
        .map(|a| a.opening)
        .filter(|o| supported(o) && !resolved.contains(o.id.as_str()))
        .collect::<Vec<_>>();
    if undecided.is_empty() {
        return Ok(vec![]);
    }
    let native = Native::new(page, work)?;
    charge(
        work,
        solids
            .len()
            .saturating_mul(3)
            .saturating_add(page.walls.len())
            .saturating_add(page.window_panes.len()),
    )?;
    let ends = solids
        .iter()
        .enumerate()
        .flat_map(|(i, s)| {
            [
                End {
                    solid: i,
                    cap: s.a,
                    outside: s.a - s.b,
                },
                End {
                    solid: i,
                    cap: s.b,
                    outside: s.b - s.a,
                },
            ]
        })
        .collect::<Vec<_>>();
    let end_bounds = BodyIndex::from_bounds(ends.iter().map(|p| bounds(&[p.cap])).collect(), work)?;
    let body_bounds =
        BodyIndex::from_bounds(solids.iter().map(|s| s.rect().bbox()).collect(), work)?;
    let archived = archived_openings(page);
    let mut bodies = Vec::new();
    let mut pending = Vec::new();
    for o in undecided {
        let Some(frame) = frame(o, &native, eps, work)? else {
            continue;
        };
        let Some(left) = find_jamb(
            page,
            o,
            frame,
            0,
            &ends,
            &end_bounds,
            solids,
            &native,
            eps,
            work,
        )?
        else {
            continue;
        };
        let Some(right) = find_jamb(
            page,
            o,
            frame,
            1,
            &ends,
            &end_bounds,
            solids,
            &native,
            eps,
            work,
        )?
        else {
            continue;
        };
        let [a, b] = [left.jamb, right.jamb];
        let thickness_step = left.end.cap.distance(a).max(right.end.cap.distance(b));
        let left_id = &page.walls[solids[left.end.solid].wall].id;
        let right_id = &page.walls[solids[right.end.solid].wall].id;
        if left_id == right_id
            || (left.setback - right.setback).abs() > frame.width() * 0.04
            || (o.evidence != OpeningEvidence::AuthoredExteriorRoughGlazing
                && thickness_step < frame.width() * 0.125)
        {
            continue;
        }
        let [pa, pb] = [left.end.cap, right.end.cap];
        let mut blocked = false;
        for (u, v) in [(pa, a), (a, b), (b, pb)] {
            if crosses_aperture(u, v, apertures, Some(&o.id), eps, work)? {
                blocked = true;
                break;
            }
            for (pane, owner) in &archived {
                if *owner != o.id.as_str()
                    && crosses_aperture(u, v, std::slice::from_ref(pane), None, eps, work)?
                {
                    blocked = true;
                    break;
                }
            }
            if blocked {
                break;
            }
        }
        if blocked || !clear_painted_throat(frame, &native, eps, work)? {
            continue;
        }
        bodies.clear();
        body_bounds.visit(0, bounds(&[a, b]), eps, &mut bodies, work)?;
        charge(work, bodies.len())?;
        if bodies.iter().any(|&i| {
            solids[i]
                .rect()
                .clip(a, b, eps)
                .is_some_and(|(lo, hi)| (hi - lo) * a.distance(b) > 4. * eps)
        }) {
            continue;
        }
        let wall_ids = ids([left_id.clone(), right_id.clone()]);
        let source_ids = ids(o
            .source_ids
            .iter()
            .cloned()
            .chain(frame.rails.into_iter().map(str::to_owned))
            .chain(
                left.sources
                    .iter()
                    .chain(&right.sources)
                    .map(|id| (*id).to_owned()),
            ));
        pending.push(Link {
            a,
            b,
            kind: if o.kind == OpeningKind::Door {
                ConnectionKind::DoorThreshold
            } else {
                ConnectionKind::WindowThreshold
            },
            wall_ids,
            opening_ids: vec![o.id.clone()],
            source_ids,
            evidence: THRESHOLD,
            anchors: [None, None],
        });
        for (join, id) in [(left, left_id), (right, right_id)] {
            if join.jamb.distance(join.end.cap) <= eps {
                continue;
            }
            pending.push(Link {
                a: join.jamb,
                b: join.end.cap,
                kind: ConnectionKind::OpeningJambContact,
                wall_ids: vec![id.clone()],
                opening_ids: vec![o.id.clone()],
                source_ids: ids(join.sources.into_iter().map(str::to_owned)),
                evidence: JAMB,
                anchors: [None, Some(join.end.solid)],
            });
        }
    }
    Ok(pending)
}

pub(super) fn append(
    page: &PageScan,
    solids: &[Solid],
    apertures: &[Aperture],
    links: &mut Vec<Link>,
    eps: f64,
    work: &mut usize,
) -> bool {
    if solids.len() < 2
        || page
            .pdf_input
            .as_ref()
            .is_none_or(|info| info.route == PdfRoute::Raster)
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
