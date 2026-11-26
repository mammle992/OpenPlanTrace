//! A short exterior return can belong to a perimeter wall through visible stock.
use super::super::{charge, exterior_finite_contacts, exterior_paint_joints, painted_contact};
use super::{Adjacency, ConnectionKind, ConnectionPass, HashMap, HashSet, PageScan, Point, Wall};
use crate::placement::staggered_cores::certified_rectangle;
use crate::{
    layers::named_exterior_wall,
    model::{BodyEvidence, Primitive},
};

pub(super) struct Catalog<'a> {
    paints: HashMap<&'a str, &'a Primitive>,
}

impl<'a> Catalog<'a> {
    pub(super) fn new(page: &'a PageScan, work: &mut usize) -> Result<Self, String> {
        let visible = page
            .placement_paint_ids
            .iter()
            .flatten()
            .chain(page.placement_polygon_paint_ids.iter().flatten())
            .map(String::as_str)
            .collect::<HashSet<_>>();
        let masked = page
            .placement_contact_paint
            .iter()
            .filter(|p| !p.occluder_source_ids.is_empty())
            .map(|p| p.source_id.as_str())
            .collect::<HashSet<_>>();
        charge(
            work,
            visible
                .len()
                .saturating_add(masked.len())
                .saturating_add(page.primitives.len()),
        )?;
        let paints = page
            .primitives
            .iter()
            .filter(|p| {
                visible.contains(p.id.as_str())
                    && !masked.contains(p.id.as_str())
                    && named_exterior_wall(p).is_some()
            })
            .map(|p| (p.id.as_str(), p))
            .collect();
        Ok(Self { paints })
    }

    fn shares_full_stock(&self, trim: &Wall, owner: &Wall, sources: &[String], eps: f64) -> bool {
        let ([child], [host]) = (trim.source_ids.as_slice(), owner.source_ids.as_slice()) else {
            return false;
        };
        if child == host
            || trim.curve.is_some()
            || owner.curve.is_some()
            || trim.body != BodyEvidence::FilledBody
            || owner.body != BodyEvidence::FilledBody
            || sources.len() != 3
            || !sources.contains(child)
            || !sources.contains(host)
        {
            return false;
        }
        let Some(bridge) = sources.iter().find(|id| *id != child && *id != host) else {
            return false;
        };
        let [Some(child), Some(host), Some(bridge)] =
            [child, host, bridge].map(|id| self.paints.get(id.as_str()).copied())
        else {
            return false;
        };
        if child.layer != host.layer || child.layer != bridge.layer {
            return false;
        }
        let length = trim.start.distance(trim.end);
        let axis = (trim.end - trim.start).unit();
        if length <= eps
            || axis.dot((owner.end - owner.start).unit()).abs() > 1e-8
            || length > 0.3 * trim.thickness
        {
            return false;
        }
        let band = |p: &Primitive| {
            let points = certified_rectangle(&p.shape)?.map(|p| {
                let v = p - trim.start;
                [v.dot(axis), axis.cross(v)]
            });
            let out = |i| {
                points
                    .iter()
                    .map(|p| p[i])
                    .fold([f64::INFINITY, f64::NEG_INFINITY], |[a, b], x| {
                        [a.min(x), b.max(x)]
                    })
            };
            let [a, b] = out(0);
            let [c, d] = out(1);
            [a, b]
                .into_iter()
                .all(|x| {
                    [c, d].into_iter().all(|y| {
                        points
                            .iter()
                            .any(|p| (p[0] - x).abs() <= eps && (p[1] - y).abs() <= eps)
                    })
                })
                .then_some(([a, b], [c, d]))
        };
        let [
            Some((child_run, cross)),
            Some((host_run, host_cross)),
            Some((bridge_run, bridge_cross)),
        ] = [child, host, bridge].map(band)
        else {
            return false;
        };
        let same = |[a, b]: [f64; 2], [c, d]: [f64; 2]| (a - c).abs().max((b - d).abs()) <= eps;
        let overlap = bridge_run[1] - bridge_run[0];
        if !same(child_run, [0., length])
            || !same(cross, [-trim.thickness * 0.5, trim.thickness * 0.5])
            || !same(cross, host_cross)
            || !same(cross, bridge_cross)
            || overlap <= eps
            || overlap > trim.thickness * 0.2
            || host_run[1] - host_run[0] < 3. * length
        {
            return false;
        }
        // The certified rectangles also have to match both finite wall bodies.
        // This excludes a short substitute axis inside some unrelated large fill.
        let u = (owner.end - owner.start).unit();
        let owner_corners = certified_rectangle(&host.shape).unwrap_or_default();
        let n = Point::new(-u.y, u.x) * (owner.thickness * 0.5);
        if [
            owner.start + n,
            owner.end + n,
            owner.end - n,
            owner.start - n,
        ]
        .iter()
        .any(|p| owner_corners.iter().all(|q| p.distance(*q) > eps))
        {
            return false;
        }
        same(bridge_run, [child_run[1], host_run[0]])
            || same(bridge_run, [host_run[1], child_run[0]])
    }
}

pub(super) struct Lane<'a, 'b> {
    pub catalog: &'a Catalog<'b>,
    pub report: &'a ConnectionPass,
    pub adjacency: &'a Adjacency,
    pub walls: &'a HashMap<&'b str, &'b Wall>,
    pub cycle_edges: &'a HashSet<usize>,
    pub eps: f64,
}

impl Lane<'_, '_> {
    pub(super) fn attached(
        &self,
        edge: usize,
        ends: [usize; 2],
        work: &mut usize,
    ) -> Result<bool, String> {
        let [id] = self.report.edges[edge].wall_ids.as_slice() else {
            return Ok(false);
        };
        let Some(&wall) = self.walls.get(id.as_str()) else {
            return Ok(false);
        };
        if wall.start.distance(wall.end) > 0.3 * wall.thickness {
            return Ok(false);
        }
        let on_cycle = |at: usize, host: &str| {
            self.adjacency[at].iter().any(|&(_, j)| {
                let e = &self.report.edges[j];
                self.cycle_edges.contains(&j)
                    && e.kind == ConnectionKind::Wall
                    && matches!(e.wall_ids.as_slice(), [id] if id == host)
            })
        };
        let qualifies = |e: &super::super::ConnectionEdge| {
            e.kind == ConnectionKind::MaterialContact
                && e.opening_ids.is_empty()
                && e.contributes_wall_material
                && !e.adds_wall_material
                && matches!(
                    e.evidence.as_str(),
                    painted_contact::PROOF
                        | exterior_finite_contacts::EVIDENCE
                        | exterior_paint_joints::EVIDENCE
                )
        };
        let mut owner = None;
        for &i in &ends {
            charge(work, self.adjacency[i].len())?;
            for &(j, k) in &self.adjacency[i] {
                let e = &self.report.edges[k];
                let [a, b] = e.wall_ids.as_slice() else {
                    continue;
                };
                let other = if a == id {
                    b
                } else if b == id {
                    a
                } else {
                    continue;
                };
                if !qualifies(e) {
                    continue;
                }
                let Some(&w) = self.walls.get(other.as_str()) else {
                    continue;
                };
                charge(
                    work,
                    self.adjacency[j].len().saturating_mul(4).saturating_add(18),
                )?;
                if !self
                    .catalog
                    .shares_full_stock(wall, w, &e.source_ids, self.eps)
                {
                    continue;
                }
                let connected = on_cycle(j, other)
                    || self.adjacency[j].iter().any(|&(to, leg)| {
                        let link = &self.report.edges[leg];
                        leg != k
                            && qualifies(link)
                            && link.wall_ids == e.wall_ids
                            && link.source_ids == e.source_ids
                            && on_cycle(to, other)
                    });
                if connected {
                    if owner.is_some_and(|p| p != other) {
                        return Ok(false);
                    }
                    owner = Some(other);
                }
            }
        }
        Ok(owner.is_some())
    }
}
