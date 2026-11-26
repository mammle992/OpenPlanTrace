//! Recover terminal connections through independently visible structural paint.
use super::{
    Aperture, ConnectionKind, Link, MAX_EDGES, PageScan, Point, Rect, Solid, charge,
    covered_by_joint, crosses_aperture, ids, on_segment,
};
use crate::input::raster::PdfRoute;
use crate::model::{LayerKind, Primitive, Shape};
use std::collections::{BTreeMap, HashSet};

const MAX_WORK: usize = 3_000_000;
pub(super) const PROOF: &str = "finite_terminal_contact_through_visible_wall_paint";
pub(super) type Bounds = [f64; 4];

fn overlap(a: Bounds, b: Bounds, epsilon: f64) -> bool {
    a[0] <= b[2] + epsilon
        && b[0] <= a[2] + epsilon
        && a[1] <= b[3] + epsilon
        && b[1] <= a[3] + epsilon
}

fn union(a: Bounds, b: Bounds) -> Bounds {
    [
        a[0].min(b[0]),
        a[1].min(b[1]),
        a[2].max(b[2]),
        a[3].max(b[3]),
    ]
}

pub(super) fn bounds(points: &[Point]) -> Bounds {
    let p = points[0];
    points[1..]
        .iter()
        .fold([p.x, p.y, p.x, p.y], |b, p| union(b, [p.x, p.y, p.x, p.y]))
}

struct Node {
    bounds: Bounds,
    leaf: std::ops::Range<usize>,
    children: Option<[usize; 2]>,
}

/// A tree over finite body bounds. A long wall needs only one entry.
pub(super) struct BodyIndex {
    bodies: Vec<Bounds>,
    order: Vec<usize>,
    nodes: Vec<Node>,
}

impl BodyIndex {
    fn new(solids: &[Solid], work: &mut usize) -> Result<Self, String> {
        charge(work, solids.len())?;
        Self::from_bounds(solids.iter().map(|s| s.rect().bbox()).collect(), work)
    }

    pub(super) fn from_bounds(bodies: Vec<Bounds>, work: &mut usize) -> Result<Self, String> {
        let order = (0..bodies.len()).collect();
        let mut index = Self {
            bodies,
            order,
            nodes: Vec::new(),
        };
        if !index.bodies.is_empty() {
            index.split(0..index.bodies.len(), work)?;
        }
        Ok(index)
    }

    fn split(&mut self, range: std::ops::Range<usize>, work: &mut usize) -> Result<usize, String> {
        charge(work, range.len())?;
        let entries = &mut self.order[range.clone()];
        let body = self.bodies[entries[0]];
        let bounds = entries[1..]
            .iter()
            .fold(body, |a, &i| union(a, self.bodies[i]));
        let id = self.nodes.len();
        self.nodes.push(Node {
            bounds,
            leaf: range.clone(),
            children: None,
        });
        if entries.len() > 12 {
            charge(work, entries.len())?;
            let axis = usize::from(bounds[3] - bounds[1] > bounds[2] - bounds[0]);
            let middle = entries.len() / 2;
            entries.select_nth_unstable_by(middle, |&a, &b| {
                let center = |i: usize| self.bodies[i][axis].midpoint(self.bodies[i][axis + 2]);
                center(a).total_cmp(&center(b)).then(a.cmp(&b))
            });
            let middle = range.start + middle;
            let left = self.split(range.start..middle, work)?;
            let right = self.split(middle..range.end, work)?;
            self.nodes[id].children = Some([left, right]);
        }
        Ok(id)
    }

    pub(super) fn visit(
        &self,
        node: usize,
        region: Bounds,
        epsilon: f64,
        nearby: &mut Vec<usize>,
        work: &mut usize,
    ) -> Result<(), String> {
        charge(work, 1)?;
        let Some(node) = self.nodes.get(node) else {
            return Ok(());
        };
        if !overlap(region, node.bounds, epsilon) {
            return Ok(());
        }
        if let Some([a, b]) = node.children {
            self.visit(a, region, epsilon, nearby, work)?;
            self.visit(b, region, epsilon, nearby, work)?;
        } else {
            let entries = &self.order[node.leaf.clone()];
            charge(work, entries.len())?;
            nearby.extend(
                entries
                    .iter()
                    .copied()
                    .filter(|&i| overlap(region, self.bodies[i], epsilon)),
            );
        }
        Ok(())
    }
}

fn structural(p: &Primitive) -> bool {
    if p.layer_category_override.is_some() {
        p.layer_kind() == LayerKind::Wall
    } else {
        p.layer.as_deref().is_some_and(crate::layers::is_named_wall)
    }
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
    let points = if points.len() > 1 && points.first() == points.last() {
        &points[..points.len() - 1]
    } else {
        points.as_slice()
    };
    ((3..=16).contains(&points.len()) && points.iter().all(|p| p.finite())).then_some(points)
}

struct Touch {
    point: Point,
    finite: bool,
}

fn touch(body: &Rect, patch: &[Point], epsilon: f64) -> Option<Touch> {
    let mut finite = false;
    let point = patch
        .iter()
        .zip(patch.iter().cycle().skip(1))
        .filter_map(|(&a, &b)| {
            let (lo, hi) = body.clip(a, b, epsilon)?;
            finite |= (hi - lo) * a.distance(b) > epsilon;
            Some(a + (b - a) * lo.midpoint(hi))
        })
        .min_by(|a, b| {
            let distance = |p: Point| on_segment(p, body.a, body.b).0.distance(p);
            distance(*a).total_cmp(&distance(*b))
        })?;
    Some(Touch { point, finite })
}

fn path(a: &Solid, b: &Solid, patch: &[Point], epsilon: f64) -> Option<[Point; 3]> {
    let [r, s] = [a.rect(), b.rect()];
    let [pa, pb] = [touch(&r, patch, epsilon)?, touch(&s, patch, epsilon)?];
    let radius = 2. * a.half.max(b.half);
    if (!pa.finite && !pb.finite) || pa.point.distance(pb.point) > radius {
        return None;
    }
    let from = a.project(pa.point).0;
    let to = b.project(pb.point).0;
    let near_end = |solid: &Solid, p: Point| {
        p.distance(solid.a).min(p.distance(solid.b)) <= solid.half + epsilon
    };
    if !near_end(a, from) || !near_end(b, to) || from.distance(to) > 2. * radius {
        return None;
    }
    let at = (pa.point + pb.point) * 0.5;
    (covered_by_joint(from, at, &[r], patch, epsilon)
        && covered_by_joint(to, at, &[s], patch, epsilon))
    .then_some([from, at, to])
}

fn clear_path(
    [a, at, b]: [Point; 3],
    apertures: &[Aperture],
    epsilon: f64,
    work: &mut usize,
) -> Result<bool, String> {
    Ok(!crosses_aperture(a, at, apertures, None, epsilon, work)?
        && !crosses_aperture(b, at, apertures, None, epsilon, work)?)
}

struct Contact<'a> {
    source: &'a str,
    path: [Point; 3],
}

fn contacts<'a>(
    page: &'a PageScan,
    solids: &[Solid],
    apertures: &[Aperture],
    links: &[Link],
    epsilon: f64,
    work: &mut usize,
) -> Result<BTreeMap<(usize, usize), Option<Contact<'a>>>, String> {
    let pair = |a, b| if a <= b { (a, b) } else { (b, a) };
    charge(work, links.len())?;
    let already_connected: HashSet<_> = links
        .iter()
        .filter_map(|link| {
            let [a, b] = link.wall_ids.as_slice() else {
                return None;
            };
            matches!(
                link.kind,
                ConnectionKind::MaterialContact | ConnectionKind::MeasuredCorner
            )
            .then_some(pair(a.as_str(), b.as_str()))
        })
        .collect();
    let paint = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten());
    let mut authorized = HashSet::new();
    for id in paint {
        charge(work, 1)?;
        authorized.insert(id.as_str());
    }
    let mut result = BTreeMap::<_, Option<Contact<'_>>>::new();
    if authorized.is_empty() {
        return Ok(result);
    }
    let index = BodyIndex::new(solids, work)?;
    let mut nearby = Vec::new();
    charge(work, page.primitives.len())?;
    for p in &page.primitives {
        if !authorized.contains(p.id.as_str()) || !structural(p) {
            continue;
        }
        let Some(patch) = outline(p) else {
            continue;
        };
        nearby.clear();
        index.visit(0, bounds(patch), epsilon, &mut nearby, work)?;
        nearby.sort_unstable();
        for (position, &i) in nearby.iter().enumerate() {
            let a = &solids[i];
            let wa = &page.walls[a.wall].id;
            for &j in &nearby[position + 1..] {
                charge(work, 1)?;
                let b = &solids[j];
                let wb = &page.walls[b.wall].id;
                if a.wall == b.wall
                    || a.axis().dot(b.axis()).abs() > 1e-5
                    || already_connected.contains(&pair(wa.as_str(), wb.as_str()))
                {
                    continue;
                }
                charge(work, patch.len() * 4)?;
                let Some(path) = path(a, b, patch, epsilon) else {
                    continue;
                };
                if !clear_path(path, apertures, epsilon, work)? {
                    continue;
                }
                // Independent paint can corroborate the same path. Alternative
                // routes through the same pair of bodies remain under review.
                result
                    .entry((i, j))
                    .and_modify(|chosen| {
                        if chosen.as_ref().is_some_and(|c| {
                            c.path
                                .into_iter()
                                .zip(path)
                                .any(|(a, b)| a.distance(b) > epsilon)
                        }) {
                            *chosen = None;
                        }
                    })
                    .or_insert(Some(Contact {
                        path,
                        source: &p.id,
                    }));
            }
        }
    }
    Ok(result)
}

/// Returns false if the optional pass could not finish within its own budget.
/// No partial links or cuts reach the graph on that path.
pub(super) fn append(
    page: &PageScan,
    solids: &mut [Solid],
    apertures: &[Aperture],
    links: &mut Vec<Link>,
    epsilon: f64,
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
    let pending = contacts(page, solids, apertures, links, epsilon, &mut remaining);
    *work -= budget - remaining;
    let Ok(pending) = pending else {
        return false;
    };
    if links.len() + 2 * pending.values().filter(|c| c.is_some()).count() > MAX_EDGES {
        return false;
    }
    for ((i, j), proof) in pending {
        let Some(Contact {
            source,
            path: [from, at, to],
        }) = proof
        else {
            continue;
        };
        let wa = &page.walls[solids[i].wall];
        let wb = &page.walls[solids[j].wall];
        let wall_ids = ids([wa.id.clone(), wb.id.clone()]);
        let source_ids = ids(wa
            .source_ids
            .iter()
            .chain(&wb.source_ids)
            .cloned()
            .chain(std::iter::once(source.to_owned())));
        for (index, point) in [(i, from), (j, to)] {
            solids[index].cut(point);
            links.push(Link {
                a: point,
                b: at,
                kind: ConnectionKind::MaterialContact,
                wall_ids: wall_ids.clone(),
                opening_ids: vec![],
                source_ids: source_ids.clone(),
                evidence: PROOF,
                anchors: [Some(index), None],
            });
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Opening, OpeningEvidence, OpeningKind};

    #[test]
    fn finite_visible_patch_does_not_override_a_signed_aperture() {
        let p = Point::new;
        let solid = |a, b, wall| Solid {
            a,
            b,
            axis_offset: Point::default(),
            half: 1.,
            wall,
            cuts: vec![0., 1.],
        };
        let west = solid(p(0., 0.), p(10., 0.), 0);
        let north = solid(p(11.1, 1.1), p(11.1, 11.), 1);
        let patch = [p(9.8, 0.1), p(11.6, 0.1), p(11.6, 1.4), p(9.8, 1.4)];
        let epsilon = 1e-9;
        let measured = path(&west, &north, &patch, epsilon).expect("continuous terminal material");
        let mut work = 100;
        assert!(clear_path(measured, &[], epsilon, &mut work).unwrap());

        let door = Opening {
            physical_jambs: None,
            id: "signed".into(),
            page: 1,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: "one".into(),
            host_wall_ids: vec!["one".into()],
            start: p(10.5, 0.),
            end: p(10.5, 2.),
            hinge: None,
            swing_observations: vec![],
            source_ids: vec![],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
        };
        let apertures = [Aperture {
            opening: &door,
            rect: Rect {
                a: door.start,
                b: door.end,
                half: 0.1,
            },
            framed_door: None,
        }];
        assert!(!clear_path(measured, &apertures, epsilon, &mut work).unwrap());
        let clipped = [p(9.8, 0.4), p(10.09, 0.4), p(10.09, 1.4), p(9.8, 1.4)];
        assert!(path(&west, &north, &clipped, epsilon).is_none());
    }
}
