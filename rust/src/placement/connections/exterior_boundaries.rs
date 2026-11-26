//! Walk exterior faces on the certified stock lane when contact links overlap.
use super::{
    BODY_CONTACT, ConnectionKind, ConnectionPass, PageScan, Point, Rect, Wall, append_boundaries,
    charge, exterior_finite_contacts, exterior_paint_joints, stepped_jambs,
};
use crate::layers::named_exterior_wall;
use crate::model::Shape;
use std::collections::{HashMap, HashSet};

mod trim;

const MAX_WORK: usize = 2_000_000;
type Adjacency = Vec<Vec<(usize, usize)>>;

pub(super) struct Proof<'a> {
    pub(super) exact_edges: &'a HashSet<usize>,
    pub(super) lane: &'a [Vec<(usize, usize)>],
}

fn exterior_walls<'a>(page: &'a PageScan, work: &mut usize) -> Result<HashSet<&'a str>, String> {
    let ids = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str);
    let mut certified = HashSet::new();
    for id in ids {
        charge(work, 1)?;
        certified.insert(id);
    }
    charge(work, page.primitives.len())?;
    let paint: HashSet<_> = page
        .primitives
        .iter()
        .filter(|p| {
            certified.contains(p.id.as_str())
                && matches!(
                    p.shape,
                    Shape::Polyline {
                        filled: true,
                        closed: true,
                        ..
                    }
                )
                && named_exterior_wall(p).is_some()
        })
        .map(|p| p.id.as_str())
        .collect();
    let mut walls = HashSet::new();
    for wall in &page.walls {
        charge(work, wall.source_ids.len().saturating_add(1))?;
        if wall.source_ids.iter().any(|id| paint.contains(id.as_str())) {
            walls.insert(wall.id.as_str());
        }
    }
    Ok(walls)
}

fn parallel_ray(span: Point, axis: Point, eps: f64) -> bool {
    span.cross(axis).abs() <= eps && span.dot(axis) > eps
}

fn continues_stock(
    report: &ConnectionPass,
    neighbors: &[(usize, usize)],
    at: Point,
    axis: Point,
    wall: &str,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    charge(work, neighbors.len())?;
    Ok(neighbors.iter().any(|&(to, link)| {
        let e = &report.edges[link];
        e.kind == ConnectionKind::Wall
            && matches!(e.wall_ids.as_slice(), [id] if id == wall)
            && parallel_ray(report.nodes[to].position - at, axis, eps)
    }))
}

fn exterior_lane(
    page: &PageScan,
    report: &ConnectionPass,
    adj: &[Vec<(usize, usize)>],
    admit_stepped_jambs: bool,
    eps: f64,
    work: &mut usize,
) -> Result<(Adjacency, HashSet<usize>, HashSet<usize>), String> {
    let walls = exterior_walls(page, work)?;
    charge(
        work,
        report
            .edges
            .len()
            .saturating_mul(2)
            .saturating_add(page.openings.len()),
    )?;
    let apertures: HashSet<_> = page.openings.iter().map(|o| o.id.as_str()).collect();
    let mut exact = HashSet::new();
    let mut eligible = HashSet::new();
    for (i, edge) in report.edges.iter().enumerate() {
        charge(
            work,
            edge.wall_ids.len().saturating_add(edge.opening_ids.len()),
        )?;
        let supported = match edge.kind {
            ConnectionKind::DoorThreshold | ConnectionKind::WindowThreshold => {
                !edge.contributes_wall_material
                    && matches!(edge.opening_ids.as_slice(), [id] if apertures.contains(id.as_str()))
            }
            _ => edge.contributes_wall_material,
        };
        if supported
            && !edge.adds_wall_material
            && !edge.wall_ids.is_empty()
            && edge.wall_ids.iter().all(|id| walls.contains(id.as_str()))
        {
            eligible.insert(i);
            if boundary_anchor(&edge.evidence, admit_stepped_jambs) {
                exact.insert(i);
            }
        }
    }
    let lane: Adjacency = adj
        .iter()
        .map(|neighbors| {
            neighbors
                .iter()
                .copied()
                .filter(|(_, i)| eligible.contains(i))
                .collect()
        })
        .collect();
    let mut redundant = HashSet::new();
    for (at, neighbors) in lane.iter().enumerate() {
        let origin = report.nodes[at].position;
        for &(short_end, short) in neighbors {
            let short_edge = &report.edges[short];
            if short_edge.kind != ConnectionKind::MaterialContact
                || short_edge.evidence != BODY_CONTACT
            {
                continue;
            }
            let short_ray = report.nodes[short_end].position - origin;
            let short_length = short_ray.norm();
            let axis = short_ray.unit();
            charge(work, neighbors.len())?;
            for &(long_end, long) in neighbors {
                let long_edge = &report.edges[long];
                if long_edge.kind != ConnectionKind::MaterialContact
                    || !matches!(
                        long_edge.evidence.as_str(),
                        exterior_paint_joints::EVIDENCE | exterior_finite_contacts::EVIDENCE
                    )
                    || long_edge.wall_ids.len() != 2
                {
                    continue;
                }
                let remote = report.nodes[long_end].position;
                let long_ray = remote - origin;
                if !parallel_ray(long_ray, axis, eps) || long_ray.dot(axis) <= short_length + eps {
                    continue;
                }
                for (near_stock, far_stock) in [
                    (&long_edge.wall_ids[0], &long_edge.wall_ids[1]),
                    (&long_edge.wall_ids[1], &long_edge.wall_ids[0]),
                ] {
                    if short_edge.wall_ids.contains(near_stock)
                        && continues_stock(
                            report,
                            neighbors,
                            origin,
                            axis * -1.,
                            near_stock,
                            eps,
                            work,
                        )?
                        && continues_stock(
                            report,
                            &lane[long_end],
                            remote,
                            axis,
                            far_stock,
                            eps,
                            work,
                        )?
                    {
                        // The shorter link duplicates a segment of the continuous stock
                        // lane. Its separate interior incidence remains in the raw graph.
                        redundant.insert(short);
                        break;
                    }
                }
            }
        }
    }
    Ok((lane, exact, redundant))
}

fn boundary_anchor(evidence: &str, stepped: bool) -> bool {
    evidence == exterior_finite_contacts::EVIDENCE
        || (stepped && evidence == stepped_jambs::THRESHOLD)
}

/// A loop inside a larger incomplete exterior lane is an internal face. Check
/// every connected certified axis against the candidate and its wall-width band.
pub(super) fn contains_source_lane(
    report: &ConnectionPass,
    adj: &[Vec<(usize, usize)>],
    nodes: &[usize],
    edges: &[usize],
    polygon: &[Point],
    walls: &HashMap<&str, &Wall>,
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    checked_lane(report, adj, nodes, edges, polygon, walls, eps, None, work)
        .map(|(complete, _)| complete)
}

fn checked_lane(
    report: &ConnectionPass,
    adj: &[Vec<(usize, usize)>],
    nodes: &[usize],
    edges: &[usize],
    polygon: &[Point],
    walls: &HashMap<&str, &Wall>,
    eps: f64,
    trim: Option<&trim::Lane<'_, '_>>,
    work: &mut usize,
) -> Result<(bool, bool), String> {
    charge(work, nodes.len().saturating_add(edges.len()))?;
    let mut visited = nodes.iter().copied().collect::<HashSet<_>>();
    let mut seen = edges.iter().copied().collect::<HashSet<_>>();
    let mut todo = nodes.to_vec();
    let mut includes_trim = false;
    while let Some(i) = todo.pop() {
        charge(work, adj[i].len().saturating_add(1))?;
        for &(j, id) in &adj[i] {
            if visited.insert(j) {
                todo.push(j);
            }
            let edge = &report.edges[id];
            if edge.kind != ConnectionKind::Wall || !seen.insert(id) {
                continue;
            }
            let width = edge
                .wall_ids
                .iter()
                .filter_map(|id| walls.get(id.as_str()))
                .map(|w| w.thickness)
                .fold(0_f64, f64::max);
            let [a, b] = [report.nodes[i].position, report.nodes[j].position];
            if !covers_stock(a, b, width, polygon, eps, work)? {
                if !trim
                    .map(|proof| proof.attached(id, [i, j], work))
                    .transpose()?
                    .unwrap_or(false)
                {
                    return Ok((false, false));
                }
                includes_trim = true;
            }
        }
    }
    Ok((true, includes_trim))
}

fn certify_existing(
    page: &PageScan,
    report: &ConnectionPass,
    lane: &Adjacency,
    exact: &HashSet<usize>,
    walls: &HashMap<&str, &Wall>,
    eps: f64,
    work: &mut usize,
) -> Result<Vec<usize>, String> {
    if report
        .boundary_candidates
        .iter()
        .all(|b| b.exterior_status != "exterior_role_unresolved")
    {
        return Ok(vec![]);
    }
    charge(
        work,
        report
            .nodes
            .len()
            .saturating_add(report.edges.len().saturating_mul(3)),
    )?;
    let eligible = lane
        .iter()
        .flatten()
        .map(|&(_, i)| i)
        .collect::<HashSet<_>>();
    let node_ids = report
        .nodes
        .iter()
        .enumerate()
        .map(|(i, n)| (n.id.as_str(), i))
        .collect::<HashMap<_, _>>();
    let edges = report
        .edges
        .iter()
        .enumerate()
        .map(|(i, e)| (e.id.as_str(), i))
        .collect::<HashMap<_, _>>();
    let catalog = trim::Catalog::new(page, work)?;
    let mut certified = Vec::new();
    for (i, b) in report.boundary_candidates.iter().enumerate() {
        if b.exterior_status != "exterior_role_unresolved" {
            continue;
        }
        charge(
            work,
            b.node_ids
                .len()
                .saturating_add(b.edge_ids.len())
                .saturating_mul(3),
        )?;
        let nodes = b
            .node_ids
            .iter()
            .filter_map(|id| node_ids.get(id.as_str()).copied())
            .collect::<Vec<_>>();
        let walk = b
            .edge_ids
            .iter()
            .filter_map(|id| edges.get(id.as_str()).copied())
            .collect::<Vec<_>>();
        if nodes.len() != b.node_ids.len()
            || walk.len() != b.edge_ids.len()
            || walk.iter().any(|id| !eligible.contains(id))
            || !walk.iter().any(|id| exact.contains(id))
        {
            continue;
        }
        let cycle_edges = walk.iter().copied().collect::<HashSet<_>>();
        let witness = trim::Lane {
            catalog: &catalog,
            report,
            adjacency: lane,
            walls,
            cycle_edges: &cycle_edges,
            eps,
        };
        if checked_lane(
            report,
            lane,
            &nodes,
            &walk,
            &b.polygon,
            walls,
            eps,
            Some(&witness),
            work,
        )? == (true, true)
        {
            certified.push(i);
        }
    }
    Ok(certified)
}

fn covers_stock(
    a: Point,
    b: Point,
    width: f64,
    polygon: &[Point],
    eps: f64,
    work: &mut usize,
) -> Result<bool, String> {
    let span = b - a;
    let length = span.norm();
    let tolerance = eps / length;
    let sides = || {
        polygon
            .iter()
            .copied()
            .zip(polygon.iter().copied().cycle().skip(1))
    };
    let mut cuts = vec![0., 1.];
    let mut covered = Vec::new();
    charge(work, polygon.len().saturating_mul(4))?;
    for (c, d) in sides() {
        let side = d - c;
        let reach = side.unit() * width;
        let band = Rect {
            a: c - reach,
            b: d + reach,
            half: width,
        };
        if let Some(overlap) = band.clip(a, b, eps) {
            covered.push(overlap);
        }
        let den = span.cross(side);
        if den.abs() > eps * (length + side.norm()) {
            let t = (c - a).cross(side) / den;
            let u = (c - a).cross(span) / den;
            if t > 0. && t < 1. && (0. ..=1.).contains(&u) {
                cuts.push(t);
            }
        }
    }
    cuts.sort_unstable_by(f64::total_cmp);
    for pair in cuts.windows(2) {
        let [lo, hi] = [pair[0], pair[1]];
        if hi - lo <= tolerance {
            continue;
        }
        charge(work, polygon.len())?;
        let p = a + span * lo.midpoint(hi);
        let inside = sides().fold(false, |inside, (c, d)| {
            inside
                ^ ((c.y > p.y) != (d.y > p.y)
                    && p.x < c.x + (d.x - c.x) * (p.y - c.y) / (d.y - c.y))
        });
        if inside {
            covered.push((lo, hi));
        }
    }
    charge(
        work,
        covered
            .len()
            .saturating_mul(covered.len().max(1).ilog2() as usize + 1),
    )?;
    covered.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
    let mut end = 0_f64;
    for (lo, hi) in covered {
        if lo > end + tolerance {
            break;
        }
        end = end.max(hi);
    }
    Ok(end >= 1. - tolerance)
}

pub(super) fn append(
    page: &PageScan,
    report: &mut ConnectionPass,
    adj: &[Vec<(usize, usize)>],
    walls: &HashMap<&str, &Wall>,
    admit_stepped_jambs: bool,
    certify_measured_roles: bool,
    eps: f64,
    work: &mut usize,
) {
    if !report
        .edges
        .iter()
        .any(|e| boundary_anchor(&e.evidence, admit_stepped_jambs))
    {
        return;
    }
    let budget = (*work / 4).min(MAX_WORK);
    let mut remaining = budget;
    let candidates = report.boundary_candidates.len();
    let diagnostics = report.diagnostics.len();
    let result = exterior_lane(page, report, adj, admit_stepped_jambs, eps, &mut remaining)
        .and_then(|(lane, exact, redundant)| {
            if exact.is_empty() {
                return Ok(());
            }
            let proof = Proof {
                exact_edges: &exact,
                lane: &lane,
            };
            let mut walk = lane.clone();
            for neighbors in &mut walk {
                neighbors.retain(|&(_, i)| !redundant.contains(&i));
            }
            append_boundaries(
                page,
                report,
                &walk,
                walls,
                Some(&proof),
                eps,
                &mut remaining,
            )?;
            if certify_measured_roles
                && let Ok(approved) =
                    certify_existing(page, report, &lane, &exact, walls, eps, &mut remaining)
            {
                for i in approved {
                    report.boundary_candidates[i].exterior_status =
                        "source_authored_exterior_wall_axis_cycle".into();
                }
            }
            Ok(())
        });
    *work -= budget - remaining;
    if result.is_err() {
        report.boundary_candidates.truncate(candidates);
        report.diagnostics.truncate(diagnostics);
        report
            .diagnostics
            .push("The authored exterior boundary walk exceeded its optional work limit.".into());
    }
}
