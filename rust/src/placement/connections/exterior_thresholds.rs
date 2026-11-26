//! Logical thresholds between independently visible exterior stock terminals.
use super::*;
use crate::layers::named_exterior_wall;
use crate::model::{Primitive, Shape};

const EVIDENCE: &str = "authored_exterior_stock_terminal_threshold_no_material";
const SHARED_JAMB: &str = "authored_exterior_shared_jamb_threshold_no_material";

struct Source<'a> {
    primitive: &'a Primitive,
    layer: &'a str,
}

struct Stock<'a> {
    solid: usize,
    source: &'a Source<'a>,
    outline: &'a [Point],
}

struct Attachment<'a> {
    stock: &'a Stock<'a>,
    jamb: &'a Source<'a>,
    terminal: Point,
    distance: f64,
}

fn paint_outline(primitive: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &primitive.shape
    else {
        return None;
    };
    ((4..=16).contains(&points.len()) && points.iter().all(|p| p.finite())).then_some(points)
}

fn measured_aperture(o: &Opening) -> bool {
    match o.kind {
        OpeningKind::Window => matches!(
            o.evidence,
            OpeningEvidence::AuthoredWindowFrame
                | OpeningEvidence::CompositeWindowFrame
                | OpeningEvidence::FiniteWindowAssembly
        ),
        OpeningKind::Door => {
            matches!(
                o.evidence,
                OpeningEvidence::FiniteJambSwing
                    | OpeningEvidence::WallGapSwing
                    | OpeningEvidence::AuthoredExteriorSwingCasing
            ) && !o.swing_observations.is_empty()
        }
    }
}

fn transverse_interval(
    a: Point,
    b: Point,
    origin: Point,
    axis: Point,
    eps: f64,
) -> Option<[f64; 2]> {
    let span = b - a;
    if span.norm() <= eps || span.dot(axis).abs() > eps {
        return None;
    }
    let s = (a - origin).cross(axis);
    let t = (b - origin).cross(axis);
    Some([s.min(t), s.max(t)])
}

fn paint_cap(
    outline: &[Point],
    physical: Point,
    lane: Point,
    inward: Point,
    eps: f64,
) -> Option<[f64; 2]> {
    let local = |p: Point| (p - physical).dot(inward);
    if outline.iter().any(|&p| local(p) > eps) {
        return None;
    }
    let mut segments = outline
        .iter()
        .zip(outline.iter().cycle().skip(1))
        .filter(|&(&p, &q)| local(p).abs() <= eps && local(q).abs() <= eps)
        .filter_map(|(&p, &q)| transverse_interval(p, q, physical, inward, eps))
        .collect::<Vec<_>>();
    segments.sort_by(|a, b| a[0].total_cmp(&b[0]));

    // PDF geometry can split a single cap at the core/lining seam. Union
    // contiguous edges only, so the proof cannot cross a notch in the paint.
    let x = (lane - physical).cross(inward);
    let encloses = |[lo, hi]: [f64; 2]| lo <= x.min(0.) + eps && hi >= x.max(0.) - eps;
    let mut segments = segments.into_iter();
    let mut cap = segments.next()?;
    for [lo, hi] in segments {
        if lo <= cap[1] + eps {
            cap[1] = cap[1].max(hi);
        } else if encloses(cap) {
            return Some(cap);
        } else {
            cap = [lo, hi];
        }
    }
    encloses(cap).then_some(cap)
}

fn attachment<'a>(
    stock: &'a Stock<'a>,
    solid: &Solid,
    jamb_sources: &[&'a Source<'a>],
    lane: Point,
    inward: Point,
    width: f64,
    full_casing: bool,
    eps: f64,
    work: &mut usize,
) -> Result<Option<Attachment<'a>>, String> {
    let (a, b) = (solid.a - solid.axis_offset, solid.b - solid.axis_offset);
    if (a - b).cross(inward).abs() > eps || (a - lane).cross(inward).abs() > solid.half + eps {
        return Ok(None);
    }
    // The endpoint facing the aperture must terminate at or outward of its
    // measured jamb. This leaves only the shallow frame-rebate range to check.
    let (physical, terminal) = if (b - a).dot(inward) > 0. {
        (b, solid.b)
    } else {
        (a, solid.a)
    };
    let distance = (lane - physical).dot(inward);
    let max_setback = if full_casing {
        0.
    } else {
        solid.half.min(width * 0.1)
    };
    if distance < -eps || distance > max_setback + eps {
        return Ok(None);
    }
    charge(work, stock.outline.len().saturating_add(jamb_sources.len()))?;
    let local = |p: Point| (p - physical).dot(inward);
    let Some([lo, hi]) = paint_cap(stock.outline, physical, lane, inward, eps) else {
        return Ok(None);
    };
    let x = (lane - physical).cross(inward);
    // Both the measured aperture and the terminal must be independently
    // witnessed. Accept only native transverse opening ink within the rebate
    // and close to the independently measured stock cap.
    let jamb = jamb_sources
        .iter()
        .copied()
        .filter_map(|source| {
            if source.layer != stock.source.layer
                || source.primitive.id == stock.source.primitive.id
                || !source
                    .primitive
                    .stroke_width
                    .is_some_and(|w| w.is_finite() && w > 0.)
            {
                return None;
            }
            let Shape::Line { start, end } = source.primitive.shape else {
                return None;
            };
            let inset = local(start);
            let [s, t] = transverse_interval(start, end, physical, inward, eps)?;
            let overlap = hi.min(t) - lo.max(s);
            let reaches_lane = s <= x + eps && t >= x - eps;
            (inset >= -eps
                && inset <= distance.min(solid.half / 3.) + eps
                && (reaches_lane || overlap >= (hi - lo) * 0.2))
                .then_some((source, inset.abs()))
        })
        .min_by(|(a, ta), (b, tb)| ta.total_cmp(tb).then(a.primitive.id.cmp(&b.primitive.id)))
        .map(|(source, _)| source);
    Ok(jamb.map(|jamb| Attachment {
        stock,
        jamb,
        terminal,
        distance,
    }))
}

fn closest<'a>(candidates: Vec<Attachment<'a>>, eps: f64) -> Option<Attachment<'a>> {
    let mut candidates = candidates;
    candidates.sort_by(|a, b| a.distance.total_cmp(&b.distance));
    let mut ordered = candidates.into_iter();
    let nearest = ordered.next()?;
    for other in ordered.take_while(|other| other.distance - nearest.distance <= eps) {
        if other.stock.solid != nearest.stock.solid
            || other.stock.source.layer != nearest.stock.source.layer
        {
            return None;
        }
    }
    Some(nearest)
}

fn stroke(source: &Source<'_>) -> Option<[Point; 2]> {
    let Shape::Line { start, end } = source.primitive.shape else {
        return None;
    };
    source
        .primitive
        .stroke_width
        .filter(|w| w.is_finite() && *w > 0.)?;
    Some([start, end])
}

fn cap_overlap(a: [f64; 2], b: [f64; 2]) -> bool {
    a[1].min(b[1]) - a[0].max(b[0]) >= (a[1] - a[0]) * 0.2
}

type Native<'a> = HashMap<&'a str, Source<'a>>;
type Paint<'a> = HashMap<&'a str, (&'a Source<'a>, &'a [Point])>;

/// Verify a frame rebate against the exact rough door jamb plane. The
/// visible fill must have real terminal edges on both the window's source
/// cap and the door plane; native transverse ink must overlap both edges.
fn shared_finish<'a>(
    inner: Point,
    door: Point,
    outward: Point,
    window_ink: &[&'a Source<'a>],
    layer: &str,
    native: &'a Native<'a>,
    paint: &Paint<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<[&'a str; 3]>, String> {
    let gap = (inner - door).dot(outward);
    let mut proof = None;
    for source in window_ink.iter().filter(|s| s.layer == layer) {
        let Some([a, b]) = stroke(source) else {
            continue;
        };
        let inset = (a - door).dot(outward);
        if inset <= eps || inset >= gap - eps {
            continue;
        }
        let from = door + outward * inset;
        let Some(window_cap) = transverse_interval(a, b, from, outward, eps) else {
            continue;
        };
        charge(work, paint.len())?;
        for (finish, outline) in paint.values().filter(|(finish, _)| finish.layer == layer) {
            if outline.iter().any(|p| {
                let t = (*p - door).dot(outward);
                t < -eps || t > inset + eps
            }) {
                continue;
            }
            let Some(from_cap) = paint_cap(outline, from, from, outward, eps) else {
                continue;
            };
            let Some(to_cap) = paint_cap(outline, door, door, outward * -1., eps) else {
                continue;
            };
            if !cap_overlap(from_cap, window_cap) {
                continue;
            }
            charge(work, native.len())?;
            let door_cap = native
                .values()
                .filter(|s| s.layer == layer)
                .filter_map(|s| {
                    let [a, b] = stroke(s)?;
                    if (a - door).dot(outward).abs() > eps {
                        return None;
                    }
                    let range = transverse_interval(a, b, door, outward * -1., eps)?;
                    cap_overlap(to_cap, range).then_some(s.primitive.id.as_str())
                })
                .min();
            if let Some(door_cap) = door_cap {
                let new = [
                    source.primitive.id.as_str(),
                    finish.primitive.id.as_str(),
                    door_cap,
                ];
                proof = Some(proof.map_or(new, |old: [&str; 3]| old.min(new)));
            }
        }
    }
    Ok(proof)
}

struct ChainEnd<'a> {
    attachment: Attachment<'a>,
    jamb: Point,
    sidelight: Option<(&'a Opening, [&'a str; 3])>,
}

fn sidelight<'a>(
    door: Point,
    outward: Point,
    layer: &str,
    apertures: &'a [Aperture],
    stocks: &'a [Stock<'a>],
    solids: &[Solid],
    resolved: &HashSet<String>,
    native: &'a Native<'a>,
    paint: &Paint<'a>,
    eps: f64,
    work: &mut usize,
) -> Result<Option<ChainEnd<'a>>, String> {
    let mut found = None;
    for a in apertures {
        charge(work, 1)?;
        if a.opening.evidence != OpeningEvidence::AuthoredWindowFrame
            || resolved.contains(&a.opening.id)
        {
            continue;
        }
        let [mut inner, mut outer] = [a.rect.a, a.rect.b];
        let at = |p: Point| (p - door).dot(outward);
        if at(inner) > at(outer) {
            std::mem::swap(&mut inner, &mut outer);
        }
        let length = inner.distance(outer);
        if (inner - door)
            .cross(outward)
            .abs()
            .max((outer - door).cross(outward).abs())
            > eps
            || at(inner) <= eps
            || at(inner) > length * 0.12
        {
            continue;
        }
        charge(
            work,
            a.opening.source_ids.len().saturating_add(stocks.len()),
        )?;
        let ink = a
            .opening
            .source_ids
            .iter()
            .filter_map(|id| native.get(id.as_str()))
            .collect::<Vec<_>>();
        let Some(finish) =
            shared_finish(inner, door, outward, &ink, layer, native, paint, eps, work)?
        else {
            continue;
        };
        let mut candidates = Vec::new();
        for stock in stocks.iter().filter(|s| s.source.layer == layer) {
            if let Some(witness) = attachment(
                stock,
                &solids[stock.solid],
                &ink,
                outer,
                outward * -1.,
                length,
                false,
                eps,
                work,
            )? {
                candidates.push(witness);
            }
        }
        let Some(attachment) = closest(candidates, eps) else {
            continue;
        };
        if found.is_some() {
            return Ok(None);
        }
        let jamb = attachment.terminal - outward * at(attachment.terminal);
        found = Some(ChainEnd {
            attachment,
            jamb,
            sidelight: Some((a.opening, finish)),
        });
    }
    Ok(found)
}

fn append_sidelights(
    page: &PageScan,
    solids: &[Solid],
    stocks: &[Stock],
    apertures: &[Aperture],
    native: &Native,
    paint: &Paint,
    links: &mut Vec<Link>,
    resolved: &mut HashSet<String>,
    eps: f64,
    work: &mut usize,
) -> Result<(), String> {
    for aperture in apertures {
        let o = aperture.opening;
        if o.evidence != OpeningEvidence::AuthoredExteriorSwingCasing || resolved.contains(&o.id) {
            continue;
        }
        charge(work, o.source_ids.len())?;
        let mut layers = o
            .source_ids
            .iter()
            .filter_map(|id| native.get(id.as_str()).map(|s| s.layer));
        let Some(layer) = layers.next() else { continue };
        if layers.any(|s| s != layer) {
            continue;
        }
        let [d0, d1] = [aperture.rect.a, aperture.rect.b];
        let axis = (d1 - d0).unit();
        let ink = o
            .source_ids
            .iter()
            .filter_map(|id| native.get(id.as_str()))
            .collect::<Vec<_>>();
        let mut ends = [None, None];
        for (i, (p, outward)) in [(d0, axis * -1.), (d1, axis)].into_iter().enumerate() {
            charge(work, stocks.len())?;
            let mut direct = Vec::new();
            for stock in stocks.iter().filter(|s| s.source.layer == layer) {
                if let Some(witness) = attachment(
                    stock,
                    &solids[stock.solid],
                    &ink,
                    p,
                    outward * -1.,
                    d0.distance(d1),
                    true,
                    eps,
                    work,
                )? {
                    direct.push(witness);
                }
            }
            ends[i] = if direct.is_empty() {
                sidelight(
                    p, outward, layer, apertures, stocks, solids, resolved, native, paint, eps,
                    work,
                )?
            } else {
                closest(direct, eps).map(|attachment| ChainEnd {
                    jamb: attachment.terminal,
                    attachment,
                    sidelight: None,
                })
            };
        }
        let [Some(a), Some(b)] = ends else { continue };
        if a.sidelight.is_none() && b.sidelight.is_none() {
            continue;
        }
        let [first, second] = [&a.attachment, &b.attachment];
        if first.stock.source.primitive.id == second.stock.source.primitive.id
            || solids[first.stock.solid].wall == solids[second.stock.solid].wall
            || a.sidelight
                .zip(b.sidelight)
                .is_some_and(|((_, left), (_, right))| left[1] == right[1])
            || (b.jamb - a.jamb).cross(axis).abs() > eps
        {
            continue;
        }
        let legs = [
            a.sidelight.map(|(window, _)| {
                (
                    first.terminal,
                    a.jamb,
                    window,
                    [Some(first.stock.solid), None],
                )
            }),
            Some((
                a.jamb,
                b.jamb,
                o,
                [
                    a.sidelight.is_none().then_some(first.stock.solid),
                    b.sidelight.is_none().then_some(second.stock.solid),
                ],
            )),
            b.sidelight.map(|(window, _)| {
                (
                    b.jamb,
                    second.terminal,
                    window,
                    [None, Some(second.stock.solid)],
                )
            }),
        ];
        let mut blocked = false;
        for &(start, end, own, _) in legs.iter().flatten() {
            blocked |= crosses_aperture(start, end, apertures, Some(&own.id), eps, work)?;
        }
        if blocked {
            continue;
        }
        let wall_ids =
            ids([first, second].map(|s| page.walls[solids[s.stock.solid].wall].id.clone()));
        let receipts = [
            first.stock.source.primitive.id.as_str(),
            first.jamb.primitive.id.as_str(),
            second.stock.source.primitive.id.as_str(),
            second.jamb.primitive.id.as_str(),
        ];
        let support = ids(legs
            .iter()
            .flatten()
            .flat_map(|(_, _, opening, _)| opening.source_ids.iter().cloned())
            .chain(
                receipts
                    .into_iter()
                    .chain(
                        [a.sidelight, b.sidelight]
                            .into_iter()
                            .flatten()
                            .flat_map(|(_, proof)| proof),
                    )
                    .map(str::to_owned),
            ));
        for (start, end, own, anchors) in legs.into_iter().flatten() {
            push_link(
                links,
                Link {
                    a: start,
                    b: end,
                    kind: if own.kind == OpeningKind::Door {
                        ConnectionKind::DoorThreshold
                    } else {
                        ConnectionKind::WindowThreshold
                    },
                    wall_ids: wall_ids.clone(),
                    opening_ids: vec![own.id.clone()],
                    source_ids: support.clone(),
                    evidence: SHARED_JAMB,
                    anchors,
                },
            )?;
            resolved.insert(own.id.clone());
        }
    }
    Ok(())
}

pub(super) fn append(
    page: &PageScan,
    solids: &mut [Solid],
    apertures: &[Aperture],
    links: &mut Vec<Link>,
    eps: f64,
    work: &mut usize,
) -> Result<(), String> {
    if !apertures.iter().any(|a| measured_aperture(a.opening)) {
        return Ok(());
    }
    charge(work, page.primitives.len().saturating_add(links.len()))?;
    let native = page
        .primitives
        .iter()
        .filter_map(|p| {
            named_exterior_wall(p).map(|layer| {
                (
                    p.id.as_str(),
                    Source {
                        primitive: p,
                        layer,
                    },
                )
            })
        })
        .collect::<HashMap<_, _>>();
    let paint_ids = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten());
    let paint = paint_ids
        .filter_map(|id| native.get(id.as_str()).map(|source| (id.as_str(), source)))
        .filter_map(|(id, source)| {
            paint_outline(source.primitive).map(|outline| (id, (source, outline)))
        })
        .collect::<HashMap<_, _>>();
    let mut stocks = Vec::new();
    for (i, s) in solids.iter().enumerate() {
        let sources = &page.walls[s.wall].source_ids;
        charge(work, sources.len().saturating_add(1))?;
        for (source, outline) in sources.iter().filter_map(|id| paint.get(id.as_str())) {
            stocks.push(Stock {
                solid: i,
                source,
                outline,
            });
        }
    }
    if stocks.len() < 2 {
        return Ok(());
    }
    let mut resolved = HashSet::new();
    for link in links.iter().filter(|l| {
        matches!(
            l.kind,
            ConnectionKind::DoorThreshold | ConnectionKind::WindowThreshold
        )
    }) {
        resolved.extend(link.opening_ids.iter().cloned());
    }
    for aperture in apertures {
        let o = aperture.opening;
        if !measured_aperture(o) || resolved.contains(&o.id) {
            continue;
        }
        let cap_ids = o
            .physical_jambs
            .iter()
            .flat_map(|jambs| &jambs.cap_source_ids);
        charge(
            work,
            o.source_ids
                .len()
                .saturating_add(stocks.len().saturating_mul(2)),
        )?;
        let jamb_sources = o
            .source_ids
            .iter()
            .chain(cap_ids)
            .filter_map(|id| native.get(id.as_str()))
            .collect::<Vec<_>>();
        if jamb_sources.len() < 2 {
            continue;
        }
        let lane = [aperture.rect.a, aperture.rect.b];
        let axis = (lane[1] - lane[0]).unit();
        let width = lane[0].distance(lane[1]);
        let full_casing = o.evidence == OpeningEvidence::AuthoredExteriorSwingCasing;
        let mut terminal = [None, None];
        for (i, (position, inward)) in [(lane[0], axis), (lane[1], axis * -1.)]
            .into_iter()
            .enumerate()
        {
            let mut candidates = Vec::new();
            for stock in &stocks {
                if let Some(witness) = attachment(
                    stock,
                    &solids[stock.solid],
                    &jamb_sources,
                    position,
                    inward,
                    width,
                    full_casing,
                    eps,
                    work,
                )? {
                    candidates.push(witness);
                }
            }
            terminal[i] = closest(candidates, eps);
        }
        let [Some(a), Some(b)] = terminal else {
            continue;
        };
        let a_solid = &solids[a.stock.solid];
        let b_solid = &solids[b.stock.solid];
        if a_solid.wall == b_solid.wall
            || a.stock.source.layer != b.stock.source.layer
            || a.stock.source.primitive.id == b.stock.source.primitive.id
            || a.jamb.primitive.id == b.jamb.primitive.id
            || (b.terminal - a.terminal).cross(axis).abs() > eps
        {
            continue;
        }
        // Window panes already owned by a physical frame are kept outside the
        // aperture inventory. Every remaining competing opening blocks the link.
        if crosses_aperture(a.terminal, b.terminal, apertures, Some(&o.id), eps, work)?
            || crosses_aperture(lane[0], lane[1], apertures, Some(&o.id), eps, work)?
        {
            continue;
        }
        push_link(
            links,
            Link {
                a: a.terminal,
                b: b.terminal,
                kind: match o.kind {
                    OpeningKind::Door => ConnectionKind::DoorThreshold,
                    OpeningKind::Window => ConnectionKind::WindowThreshold,
                },
                wall_ids: ids([
                    page.walls[a_solid.wall].id.clone(),
                    page.walls[b_solid.wall].id.clone(),
                ]),
                opening_ids: vec![o.id.clone()],
                source_ids: ids(o.source_ids.iter().cloned().chain([
                    a.stock.source.primitive.id.clone(),
                    b.stock.source.primitive.id.clone(),
                    a.jamb.primitive.id.clone(),
                    b.jamb.primitive.id.clone(),
                ])),
                evidence: EVIDENCE,
                anchors: [Some(a.stock.solid), Some(b.stock.solid)],
            },
        )?;
        resolved.insert(o.id.clone());
    }
    append_sidelights(
        page,
        solids,
        &stocks,
        apertures,
        &native,
        &paint,
        links,
        &mut resolved,
        eps,
        work,
    )
}
