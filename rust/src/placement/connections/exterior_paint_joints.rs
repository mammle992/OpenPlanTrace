//! Join authored exterior terminals through the union of retained paint.
use super::*;
use crate::layers::named_exterior_wall;
use crate::model::{LayerKind, Shape};

pub(super) const EVIDENCE: &str = "finite_exterior_terminal_contact_through_visible_paint_union";

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    points: &'a [Point],
    min: Point,
    max: Point,
}

struct Leg<'a> {
    a: Point,
    b: Point,
    sources: Vec<&'a str>,
}

fn clear_leg<'a>(
    paints: &[Paint<'a>],
    apertures: &[Aperture],
    a: Point,
    b: Point,
    eps: f64,
    work: &mut usize,
) -> Option<Leg<'a>> {
    if a.distance(b) <= eps {
        return None;
    }
    charge(work, paints.len().saturating_mul(12)).ok()?;
    if crosses_aperture(a, b, apertures, None, eps, work).ok()? {
        return None;
    }
    Some(Leg {
        a,
        b,
        sources: covers(paints, a, b, eps)?,
    })
}

fn corner_path<'a>(
    paint: &[Paint<'a>],
    apertures: &[Aperture],
    from: Point,
    to: Point,
    outward: [Point; 2],
    eps: f64,
    work: &mut usize,
) -> Option<Vec<Leg<'a>>> {
    let max_length = from.distance(to) * 1.45;
    let nearby = paint
        .iter()
        .copied()
        .filter(|p| {
            let distance = |q: Point| {
                let x = q.x.clamp(p.min.x, p.max.x);
                let y = q.y.clamp(p.min.y, p.max.y);
                Point::new(x, y).distance(q)
            };
            distance(from) + distance(to) <= max_length
        })
        .collect::<Vec<_>>();
    charge(work, paint.len()).ok()?;
    let mut bends = vec![];
    for p in nearby.iter().flat_map(|p| p.points) {
        if p.distance(from) + p.distance(to) <= max_length
            && p.distance(from).min(p.distance(to)) > eps
            && bends.iter().all(|q: &Point| q.distance(*p) > eps)
        {
            if bends.len() == 24 {
                return None;
            }
            bends.push(*p);
        }
    }
    let first: Vec<_> = bends
        .iter()
        .filter(|&&p| (p - from).dot(outward[0].unit()) >= -eps)
        .filter_map(|&p| clear_leg(&nearby, apertures, from, p, eps, work))
        .collect();
    let last: Vec<_> = bends
        .iter()
        .filter(|&&p| (p - to).dot(outward[1].unit()) >= -eps)
        .filter_map(|&p| clear_leg(&nearby, apertures, p, to, eps, work))
        .collect();
    let mut chosen = None;
    let mut length = max_length;
    for a in &first {
        for b in &last {
            let total = a.a.distance(a.b) + a.b.distance(b.a) + b.a.distance(b.b);
            if total >= length {
                continue;
            }
            let outer = |l: &Leg<'a>| Leg {
                a: l.a,
                b: l.b,
                sources: l.sources.clone(),
            };
            if a.b == b.a {
                chosen = Some(vec![outer(a), outer(b)]);
            } else if let Some(bridge) = clear_leg(&nearby, apertures, a.b, b.a, eps, work) {
                chosen = Some(vec![outer(a), bridge, outer(b)]);
            } else {
                continue;
            }
            length = total;
        }
    }
    chosen
}

fn covers<'a>(paints: &[Paint<'a>], a: Point, b: Point, eps: f64) -> Option<Vec<&'a str>> {
    let [xmin, xmax] = [a.x.min(b.x), a.x.max(b.x)];
    let [ymin, ymax] = [a.y.min(b.y), a.y.max(b.y)];
    let tol = eps / a.distance(b);
    let mut spans: Vec<_> = paints
        .iter()
        .filter_map(|p| {
            if p.min.x > xmax + eps
                || p.max.x < xmin - eps
                || p.min.y > ymax + eps
                || p.max.y < ymin - eps
            {
                return None;
            }
            polygon_interval(a, b, p.points, eps * 0.25)
                .filter(|(lo, hi)| hi - lo > tol)
                .map(|(lo, hi)| (lo, hi, p.id))
        })
        .collect();
    spans.sort_unstable_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(b.1.total_cmp(&a.1))
            .then(a.2.cmp(b.2))
    });
    let mut end: f64 = 0.;
    let mut source = vec![];
    for (lo, hi, id) in spans {
        if lo > end + tol {
            break;
        }
        if hi > end {
            end = hi;
            source.push(id);
            if end >= 1. - tol {
                return Some(source);
            }
        }
    }
    None
}

pub(super) fn append(
    page: &PageScan,
    solids: &mut [Solid],
    apertures: &[Aperture],
    links: &mut Vec<Link>,
    eps: f64,
    work: &mut usize,
) -> Result<(), String> {
    if !page
        .walls
        .iter()
        .any(|w| w.id.starts_with("exterior-pier:"))
    {
        // The separately replayed pier proof supplies the local facade context.
        return Ok(());
    }
    let authorized: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect();
    let mut stock = HashMap::new();
    let mut paint = vec![];
    for p in page
        .primitives
        .iter()
        .filter(|p| authorized.contains(p.id.as_str()))
    {
        if let Some(layer) = named_exterior_wall(p) {
            stock.insert(p.id.as_str(), layer);
        }
        if p.layer_kind() != LayerKind::Wall
            && (p.layer_category_override.is_some()
                || !p.layer.as_deref().is_some_and(crate::layers::is_named_wall))
        {
            continue;
        }
        let Shape::Polyline {
            points,
            filled: true,
            closed: true,
        } = &p.shape
        else {
            continue;
        };
        if !(3..=16).contains(&points.len()) || points.iter().any(|p| !p.finite()) {
            continue;
        }
        let (min, max) = points[1..]
            .iter()
            .fold((points[0], points[0]), |(a, b), p| {
                (
                    Point::new(a.x.min(p.x), a.y.min(p.y)),
                    Point::new(b.x.max(p.x), b.y.max(p.y)),
                )
            });
        paint.push(Paint {
            id: &p.id,
            points,
            min,
            max,
        });
    }
    let exterior: Vec<_> = solids
        .iter()
        .enumerate()
        .filter_map(|(i, s)| {
            let wall = &page.walls[s.wall];
            let layer = wall
                .source_ids
                .iter()
                .find_map(|id| stock.get(id.as_str()))?;
            Some((i, *layer))
        })
        .collect();
    let pair = |a, b| if a < b { (a, b) } else { (b, a) };
    let explicit: HashSet<_> = links
        .iter()
        .filter_map(|link| {
            let [a, b] = link.wall_ids.as_slice() else {
                return None;
            };
            matches!(
                link.kind,
                ConnectionKind::MaterialContact | ConnectionKind::MeasuredCorner
            )
            .then(|| pair(a.as_str(), b.as_str()))
        })
        .collect();
    let mut pending = vec![];
    for (position, &(i, layer)) in exterior.iter().enumerate() {
        for &(j, other) in &exterior[position + 1..] {
            if *work < 1 {
                return Ok(());
            }
            *work -= 1;
            let (a, b) = (&solids[i], &solids[j]);
            let (w, v) = (&page.walls[a.wall], &page.walls[b.wall]);
            if a.wall == b.wall
                || layer != other
                || explicit.contains(&pair(w.id.as_str(), v.id.as_str()))
            {
                continue;
            }
            let collinear = a.axis().cross(b.axis()).abs() <= 1e-4;
            if !collinear && a.axis().dot(b.axis()).abs() > 1e-4 {
                continue;
            }
            let mut straight = vec![];
            let mut bent = vec![];
            for from in [a.a, a.b] {
                for to in [b.a, b.b] {
                    let direction = (to - from).unit();
                    let reach = (a.half + b.half) * if collinear { 1.75 } else { 2.15 };
                    let outward = [from - (a.a + a.b) * 0.5, to - (b.a + b.b) * 0.5];
                    let direct =
                        outward[0].dot(direction) >= -eps && outward[1].dot(direction) <= eps;
                    if from.distance(to) <= eps
                        || from.distance(to) > reach
                        || (collinear && (!direct || direction.cross(a.axis()).abs() > 0.12))
                    {
                        continue;
                    }
                    let cost = paint
                        .len()
                        .saturating_mul(12)
                        .saturating_add(apertures.len());
                    if *work <= cost {
                        return Ok(());
                    }
                    *work -= cost - apertures.len();
                    if direct
                        && !crosses_aperture(from, to, apertures, None, eps, work)?
                        && let Some(sources) = covers(&paint, from, to, eps)
                    {
                        straight.push(vec![Leg {
                            a: from,
                            b: to,
                            sources,
                        }]);
                    } else if !collinear
                        && let Some(path) =
                            corner_path(&paint, apertures, from, to, outward, eps, work)
                    {
                        bent.push(path);
                    }
                }
            }
            // Prefer the direct measured incidence. A longer route around the
            // same painted corner does not disqualify its exact straight join.
            let mut witnessed = if straight.is_empty() { bent } else { straight };
            if witnessed.len() == 1 {
                let path = witnessed.pop().expect("unique source-supported path");
                let count = path.len();
                for (segment, leg) in path.into_iter().enumerate() {
                    pending.push(Link {
                        a: leg.a,
                        b: leg.b,
                        kind: ConnectionKind::MaterialContact,
                        wall_ids: ids([w.id.clone(), v.id.clone()]),
                        opening_ids: vec![],
                        source_ids: ids(w
                            .source_ids
                            .iter()
                            .chain(&v.source_ids)
                            .cloned()
                            .chain(leg.sources.into_iter().map(str::to_owned))),
                        evidence: EVIDENCE,
                        anchors: [
                            (segment == 0).then_some(i),
                            (segment + 1 == count).then_some(j),
                        ],
                    });
                }
            }
        }
    }
    if links.len() + pending.len() <= MAX_EDGES {
        links.extend(pending);
    }
    Ok(())
}
