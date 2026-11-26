//! Source paint finishing a run of already placed structural wall cores.
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape};
use crate::placement::{WallStatus, effective_wall, painted_rectangle, wall_layers};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 24_000_000;

pub(crate) struct Attachment<'a> {
    pub owner_id: &'a str,
    pub paint_id: &'a str,
    pub outline: [Point; 4],
    pub source_ids: Vec<&'a str>,
}

#[derive(Clone, Copy)]
struct Paint<'a> {
    id: &'a str,
    outline: [Point; 4],
}

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    eps: f64,
}

#[derive(Clone, Copy)]
struct Section<'a> {
    id: &'a str,
    along: [f64; 2],
    across: [f64; 2],
}

impl Frame {
    fn from(p: &Primitive) -> Option<Self> {
        let Shape::Line { start, end } = p.shape else {
            return None;
        };
        let length = start.distance(end);
        let width = p.stroke_width?;
        let eps = 128. * f64::EPSILON * start.norm().max(end.norm()).max(length).max(1.);
        (width.is_finite() && width > 0. && length >= 10. * width).then_some(Self {
            origin: start,
            axis: (end - start).unit(),
            length,
            eps,
        })
    }

    fn section<'a>(&self, paint: Paint<'a>, side: f64) -> Option<Section<'a>> {
        let points = paint.outline.map(|p| {
            let v = p - self.origin;
            Point::new(v.dot(self.axis), self.axis.cross(v) * side)
        });
        let bounds = |get: fn(Point) -> f64| {
            points
                .into_iter()
                .map(get)
                .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], p| {
                    [lo.min(p), hi.max(p)]
                })
        };
        let along = bounds(|p| p.x);
        let across = bounds(|p| p.y);
        if along[1] - along[0] <= self.eps || across[1] - across[0] <= self.eps {
            return None;
        }
        [along[0], along[1]]
            .into_iter()
            .all(|x| {
                across.into_iter().all(|y| {
                    points
                        .iter()
                        .any(|&p| p.distance(Point::new(x, y)) <= self.eps)
                })
            })
            .then_some(Section {
                id: paint.id,
                along,
                across,
            })
    }

    fn outline(&self, [a, b]: [f64; 2], [lo, hi]: [f64; 2], side: f64) -> [Point; 4] {
        let normal = Point::new(-self.axis.y, self.axis.x) * side;
        let point = |x, y| self.origin + self.axis * x + normal * y;
        [point(a, lo), point(b, lo), point(b, hi), point(a, hi)]
    }

    fn equal(&self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

fn structural(p: &Primitive) -> bool {
    if p.layer_category_override.is_some() {
        p.layer_kind() == LayerKind::Wall
    } else {
        p.layer.as_deref().is_some_and(crate::layers::is_named_wall)
    }
}

#[derive(Clone, Copy)]
struct Owner<'a> {
    id: &'a str,
    paint: Paint<'a>,
}

struct Finish<'a> {
    strip: Section<'a>,
    cap_ids: [&'a str; 2],
}

fn cap<'a>(
    f: &Frame,
    strip: Section<'a>,
    start: bool,
    sections: &[Section<'a>],
) -> Option<&'a str> {
    let point = if start { 0. } else { f.length };
    let mut candidates = sections.iter().filter(|s| {
        let touching = if start { s.along[1] } else { s.along[0] };
        f.equal(touching, point)
            && path(s.id) != path(strip.id)
            && f.equal(s.across[0], strip.across[0])
            && f.equal(s.across[1], strip.across[1])
            && s.along[1] - s.along[0] <= 0.09 * f.length
    });
    let first = candidates.next()?;
    candidates.next().is_none().then_some(first.id)
}

fn stack<'a>(
    f: &Frame,
    sections: &[Section<'a>],
    rail: &str,
    work: &mut usize,
) -> Option<Vec<Finish<'a>>> {
    let mut stack = Vec::new();
    let mut edge = 0.;
    for _ in 0..5 {
        *work = work.checked_sub(sections.len().saturating_mul(3))?;
        let mut strips = sections.iter().copied().filter(|s| {
            f.equal(s.along[0], 0.)
                && f.equal(s.along[1], f.length)
                && f.equal(s.across[0], edge)
                && s.across[1] - edge <= 0.05 * f.length
                && path(s.id) != path(rail)
                && stack
                    .iter()
                    .all(|q: &Finish<'_>| path(q.strip.id) != path(s.id))
        });
        let Some(strip) = strips.next() else { break };
        if strips.next().is_some() {
            break;
        }
        let (Some(a), Some(b)) = (
            cap(f, strip, true, sections),
            cap(f, strip, false, sections),
        ) else {
            break;
        };
        if path(a) == path(b) || path(a) == path(rail) || path(b) == path(rail) {
            break;
        }
        stack.push(Finish {
            strip,
            cap_ids: [a, b],
        });
        edge = strip.across[1];
    }
    Some(stack)
}

fn gap_proof<'a>(
    f: &Frame,
    [a, b]: [f64; 2],
    across: [f64; 2],
    stock: &[Section<'a>],
    work: &mut usize,
) -> Option<Option<Vec<&'a str>>> {
    if b < a - f.eps {
        return Some(None);
    }
    if f.equal(a, b) {
        return Some(Some(Vec::new()));
    }
    *work = work.checked_sub(stock.len())?;
    let mut parts = stock
        .iter()
        .filter(|s| {
            f.equal(s.across[0], across[0])
                && f.equal(s.across[1], across[1])
                && s.along[0] >= a - f.eps
                && s.along[1] <= b + f.eps
        })
        .collect::<Vec<_>>();
    *work = work.checked_sub(parts.len().saturating_pow(2))?;
    parts.sort_unstable_by(|u, v| u.along[0].total_cmp(&v.along[0]).then(u.id.cmp(v.id)));
    let mut at = a;
    let mut ids = Vec::new();
    for s in parts {
        if s.along[0] > at + f.eps {
            break;
        }
        if s.along[1] > at + f.eps {
            at = s.along[1];
            ids.push(s.id);
        }
        if f.equal(at, b) {
            return Some(Some(ids));
        }
    }
    Some(None)
}

fn rectangle(a: Point, b: Point, width: f64) -> Option<[Point; 4]> {
    let axis = (b - a).unit();
    if a == b || width <= 0. || !width.is_finite() {
        return None;
    }
    let offset = Point::new(-axis.y, axis.x) * (0.5 * width);
    Some([a + offset, b + offset, b - offset, a - offset])
}

fn positive_overlap(a: [Point; 4], b: [Point; 4], eps: f64) -> bool {
    [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]]
        .into_iter()
        .all(|v| {
            let axis = v.unit();
            let range = |p: [Point; 4]| {
                p.into_iter()
                    .map(|p| (p - a[0]).dot(axis))
                    .fold([f64::INFINITY, f64::NEG_INFINITY], |[a, b], x| {
                        [a.min(x), b.max(x)]
                    })
            };
            let [lo, hi] = range(a);
            let [l, r] = range(b);
            hi.min(r) - lo.max(l) > eps
        })
}

fn propose<'a>(
    page: &'a PageScan,
    extra_occupied: &[[Point; 4]],
    work: &mut usize,
) -> Option<Vec<Attachment<'a>>> {
    let receipts: HashSet<_> = page
        .placement_paint_ids
        .iter()
        .flatten()
        .map(String::as_str)
        .collect();
    if receipts.is_empty() {
        return Some(Vec::new());
    }
    *work = work.checked_sub(page.primitives.len())?;
    let mut by_layer = HashMap::<&str, Vec<Paint>>::new();
    let mut by_id = HashMap::new();
    let mut all_paint = Vec::new();
    for p in page
        .primitives
        .iter()
        .filter(|p| receipts.contains(p.id.as_str()) && structural(p))
    {
        let (Some(layer), Some(outline)) = (p.layer.as_deref(), painted_rectangle(&p.shape)) else {
            continue;
        };
        let paint = Paint { id: &p.id, outline };
        all_paint.push(paint);
        if crate::layers::named_exterior_wall(p).is_some() {
            by_id.insert(p.id.as_str(), (layer, paint));
            by_layer.entry(layer).or_default().push(paint);
        }
    }
    if by_layer.is_empty() {
        return Some(Vec::new());
    }
    let mut owners = HashMap::<&str, Vec<Owner>>::new();
    let mut occupied = Vec::new();
    let widths = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .map(|(w, d)| (w.id.as_str(), effective_wall(w, Some(d)).thickness))
        .collect::<HashMap<_, _>>();
    for (w, d) in page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
    {
        if d.status != WallStatus::Ready {
            continue;
        }
        let wall = effective_wall(w, Some(d));
        if let Some(arc) = wall.curve {
            let radius = arc.radius + wall.thickness * 0.5;
            let offset = Point::new(radius, 0.);
            occupied.extend(rectangle(
                arc.center - offset,
                arc.center + offset,
                2. * radius,
            ));
            continue;
        }
        let outline = rectangle(wall.start, wall.end, wall.thickness);
        occupied.extend(outline);
        let [id] = w.source_ids.as_slice() else {
            continue;
        };
        if w.body == BodyEvidence::FilledBody
            && d.placement_extent.is_none()
            && let Some(&(layer, paint)) = by_id.get(id.as_str())
            && let Some(outline) = outline
            && paint
                .outline
                .iter()
                .all(|&p| outline.iter().any(|q| p.distance(*q) <= 1e-7))
        {
            owners
                .entry(layer)
                .or_default()
                .push(Owner { id: &w.id, paint });
        }
    }
    let walls = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    for layer in wall_layers::layers(page) {
        if let Some(owner) = walls.get(layer.owner_id) {
            let shape = layer.footprint(owner);
            let shape = effective_wall(&shape, Some(layer.decision));
            occupied.extend(rectangle(shape.start, shape.end, shape.thickness));
        }
    }
    occupied.extend(extra_occupied.iter().copied());
    let mut apertures = Vec::new();
    for o in page.opening_evidence() {
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|id| widths.get(id.as_str()).copied())
            .fold(1e-5_f64, f64::max);
        apertures.extend(rectangle(o.start, o.end, width));
        apertures.extend(
            o.swing_observations.iter().filter_map(|s| {
                rectangle(s.hinge, s.closed_tip, (2. * s.leaf_tolerance).max(1e-5))
            }),
        );
    }
    occupied.extend(apertures.iter().copied());
    let mut proposals = Vec::new();
    for rail in &page.primitives {
        let Some(layer) = crate::layers::named_exterior_wall(rail) else {
            continue;
        };
        let Some(f) = Frame::from(rail) else { continue };
        let (Some(paint), Some(owners)) = (by_layer.get(layer), owners.get(layer)) else {
            continue;
        };
        for side in [-1., 1.] {
            *work = work.checked_sub(paint.len())?;
            let sections = paint
                .iter()
                .filter_map(|&p| f.section(p, side))
                .collect::<Vec<_>>();
            let strips = stack(&f, &sections, &rail.id, work)?;
            if strips.len() < 2 {
                continue;
            }
            let depth = strips.last()?.strip.across[1];
            *work = work.checked_sub(owners.len())?;
            let mut courses = BTreeMap::<i64, Vec<(Owner, Section)>>::new();
            for &owner in owners {
                let Some(core) = f.section(owner.paint, side) else {
                    continue;
                };
                if f.equal(core.across[0], depth)
                    && depth <= 0.5 * (core.across[1] - depth)
                    && core.along[0] >= -f.eps
                    && core.along[1] <= f.length + f.eps
                {
                    let key = (core.across[1] / 1e-7).round() as i64;
                    courses.entry(key).or_default().push((owner, core));
                }
            }
            let mut accepted = None;
            for mut cells in courses.into_values() {
                *work = work.checked_sub(cells.len().saturating_pow(2))?;
                cells.sort_unstable_by(|a, b| {
                    a.1.along[0]
                        .total_cmp(&b.1.along[0])
                        .then(a.0.id.cmp(b.0.id))
                });
                if cells.len() < 2
                    || !f.equal(cells[0].1.along[0], 0.)
                    || !f.equal(cells.last()?.1.along[1], f.length)
                {
                    continue;
                }
                let across = cells[0].1.across;
                if cells.iter().any(|(_, s)| !f.equal(s.across[1], across[1]))
                    || cells
                        .iter()
                        .map(|(_, c)| c.along[1] - c.along[0])
                        .sum::<f64>()
                        < 0.8 * f.length
                {
                    continue;
                }
                *work = work.checked_sub(apertures.len().saturating_mul(cells.len()))?;
                if cells.iter().any(|(owner, _)| {
                    apertures
                        .iter()
                        .any(|&cut| positive_overlap(owner.paint.outline, cut, f.eps))
                }) {
                    continue;
                }
                *work = work.checked_sub(all_paint.len())?;
                let stock = all_paint
                    .iter()
                    .filter_map(|&p| f.section(p, side))
                    .collect::<Vec<_>>();
                let mut support = Vec::new();
                let mut complete = true;
                for pair in cells.windows(2) {
                    let gap = [pair[0].1.along[1], pair[1].1.along[0]];
                    let Some(mut ids) = gap_proof(&f, gap, across, &stock, work)? else {
                        complete = false;
                        break;
                    };
                    if gap[1] > gap[0] + f.eps {
                        *work = work.checked_sub(apertures.len())?;
                        let passage = f.outline(gap, across, side);
                        if apertures
                            .iter()
                            .any(|&cut| positive_overlap(passage, cut, f.eps))
                        {
                            complete = false;
                            break;
                        }
                    }
                    support.append(&mut ids);
                }
                if complete {
                    if accepted.is_some() {
                        accepted = None;
                        break;
                    }
                    accepted = Some((cells, support));
                }
            }
            let Some((cells, support)) = accepted else {
                continue;
            };
            let mut assembly = Vec::new();
            let mut valid = true;
            for (owner, core) in cells {
                for finish in &strips {
                    *work = work.checked_sub(occupied.len())?;
                    let outline = f.outline(core.along, finish.strip.across, side);
                    if occupied
                        .iter()
                        .any(|&p| positive_overlap(outline, p, f.eps))
                    {
                        valid = false;
                        break;
                    }
                    let source_ids = std::iter::once(rail.id.as_str())
                        .chain([owner.paint.id, finish.strip.id])
                        .chain(finish.cap_ids)
                        .chain(support.iter().copied())
                        .collect::<BTreeSet<_>>()
                        .into_iter()
                        .collect();
                    assembly.push(Attachment {
                        owner_id: owner.id,
                        paint_id: finish.strip.id,
                        outline,
                        source_ids,
                    });
                }
                if !valid {
                    break;
                }
            }
            if valid {
                proposals.push(assembly);
            }
        }
    }
    *work = work.checked_sub(
        proposals
            .iter()
            .map(Vec::len)
            .sum::<usize>()
            .saturating_pow(2),
    )?;
    let mut valid = vec![true; proposals.len()];
    for (i, a) in proposals.iter().enumerate() {
        for (j, b) in proposals.iter().enumerate().skip(i + 1) {
            if a.iter().any(|a| {
                b.iter()
                    .any(|b| positive_overlap(a.outline, b.outline, 1e-7))
            }) {
                valid[i] = false;
                valid[j] = false;
            }
        }
    }
    let mut out = proposals
        .into_iter()
        .zip(valid)
        .filter_map(|(a, valid)| valid.then_some(a))
        .flatten()
        .collect::<Vec<_>>();
    out.sort_unstable_by(|a, b| a.owner_id.cmp(b.owner_id).then(a.paint_id.cmp(b.paint_id)));
    Some(out)
}

/// `extra_occupied` contains existing placed junction/other material polygons.
/// The caller gates this projection to the new export revision.
pub(crate) fn attachments<'a>(
    page: &'a PageScan,
    extra_occupied: &[[Point; 4]],
) -> Vec<Attachment<'a>> {
    let mut work = MAX_WORK;
    propose(page, extra_occupied, &mut work).unwrap_or_default()
}
