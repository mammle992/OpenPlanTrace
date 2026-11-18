//! Authored room boundaries supplement closed graph faces. Retain the exact
//! source ring and decline ambiguous overlaps instead of modifying graph walls.
use super::{Bounds, boundaries_meet, charge};
use crate::{
    model::*,
    room_fill::PaintedFloors,
    room_labels::{FloorFootprint, RoomAnnotation},
};
use std::collections::BTreeSet;

fn edges(p: &[Point]) -> impl Iterator<Item = (Point, Point)> + '_ {
    p.iter().copied().zip(p.iter().copied().cycle().skip(1))
}
fn epsilon(b: Bounds) -> f64 {
    64. * f64::EPSILON
        * b.lo
            .x
            .abs()
            .max(b.lo.y.abs())
            .max(b.hi.x.abs())
            .max(b.hi.y.abs())
            .max(1.)
}
fn explicit_perimeter(p: &Primitive) -> bool {
    let layer = p.layer.as_deref();
    p.layer_kind() == LayerKind::Room
        && !layer
            .unwrap_or("")
            .split(|c: char| !c.is_ascii_alphanumeric())
            .any(|w| {
                [
                    "TEXT", "TAG", "NAME", "NOTE", "ID", "NUMBER", "ANNO", "HATCH",
                ]
                .iter()
                .any(|s| w.eq_ignore_ascii_case(s))
            })
}
struct Ring {
    points: Vec<Point>,
    bounds: Bounds,
    area: f64,
}
impl Ring {
    fn from_source(p: &Primitive, budget: &mut usize) -> Result<Option<Self>, String> {
        let Shape::Polyline {
            points,
            closed: true,
            ..
        } = &p.shape
        else {
            return Ok(None);
        };
        let mut ring = points.clone();
        ring.dedup();
        if ring.len() > 1 && ring.first() == ring.last() {
            ring.pop();
        }
        if ring.len() < 3 {
            return Ok(None);
        }
        let bounds = Bounds::new(&ring);
        let eps = epsilon(bounds);
        let origin = ring[0];
        let signed = edges(&ring)
            .map(|(a, b)| (a - origin).cross(b - origin) * 0.5)
            .sum::<f64>();
        if !signed.is_finite() || signed.abs() <= eps * eps {
            return Ok(None);
        }
        charge(budget, ring.len().saturating_mul(ring.len()))?;
        // A single room ring cannot describe a self crossing, a hole linked by a
        // retraced seam, or a spur which returns along the preceding edge.
        for (i, (a, b)) in edges(&ring).enumerate() {
            let c = ring[(i + 2) % ring.len()];
            let u = b - a;
            let v = c - b;
            if u.norm() <= eps || (u.cross(v).abs() <= eps * u.norm() && u.dot(v) < 0.) {
                return Ok(None);
            }
            for (j, (c, d)) in edges(&ring).enumerate().skip(i + 2) {
                if !(i == 0 && j + 1 == ring.len()) && boundaries_meet(a, b, c, d, eps) {
                    return Ok(None);
                }
            }
        }
        if signed < 0. {
            ring.reverse();
        }
        Ok(Some(Self {
            points: ring,
            bounds,
            area: signed.abs(),
        }))
    }
}

// Rings are counter-clockwise in the same page frame. A shared edge with
// opposite winding is legitimate adjacency. Equal winding puts both interiors
// on the same side, including two partly coincident rectangular footprints.
fn overlap(a: (&[Point], Bounds), b: (&[Point], Bounds), work: &mut usize) -> Result<bool, String> {
    let ((aa, x), (bb, y)) = (a, b);
    let eps = epsilon(x).max(epsilon(y));
    if x.hi.x.min(y.hi.x) <= x.lo.x.max(y.lo.x) + eps
        || x.hi.y.min(y.hi.y) <= x.lo.y.max(y.lo.y) + eps
    {
        return Ok(false);
    }
    charge(work, aa.len().saturating_mul(bb.len()).saturating_mul(5))?;
    for (a, b) in edges(aa) {
        let u = (b - a).unit();
        for (c, d) in edges(bb) {
            let v = (d - c).unit();
            let ca = u.cross(c - a);
            let da = u.cross(d - a);
            let ac = v.cross(a - c);
            let bc = v.cross(b - c);
            if ca.min(da) < -eps && ca.max(da) > eps && ac.min(bc) < -eps && ac.max(bc) > eps {
                return Ok(true);
            }
            if ca.abs() <= eps && da.abs() <= eps && u.dot(v) > 0. {
                let l = (c - a).dot(u);
                let r = (d - a).dot(u);
                if r.min(a.distance(b)) > l.max(0.) + eps {
                    return Ok(true);
                }
            }
        }
    }
    let interior = |ring: &[Point], other: &[Point]| {
        edges(ring).any(|(a, b)| {
            [a, (a + b) * 0.5].into_iter().any(|p| {
                inside(p, other)
                    && edges(other).all(|(c, d)| on_segment(p, c, d).0.distance(p) > eps)
            })
        })
    };
    Ok(interior(aa, bb) || interior(bb, aa))
}

fn evidence(p: &Primitive, floors: &PaintedFloors) -> Option<RoomBoundaryEvidence> {
    if !matches!(p.shape, Shape::Polyline { closed: true, .. }) {
        return None;
    }
    if explicit_perimeter(p) {
        return Some(RoomBoundaryEvidence::RoomLayerPerimeter {
            source_id: p.id.clone(),
        });
    }
    floors
        .get(&p.id)
        .map(|ids| RoomBoundaryEvidence::MeasuredFloorFill {
            source_id: p.id.clone(),
            witness_source_ids: ids.witnesses.clone(),
            perimeter_source_ids: vec![],
            entrance_opening_ids: vec![],
        })
}

/// A rectangular piece of paint has finite area. Its edge may support an
/// observed floor perimeter, without making the paint a selected wall.
struct Strip<'a> {
    id: &'a str,
    corners: [Point; 4],
    bounds: Bounds,
}
pub(crate) struct Materials<'a>(Vec<Strip<'a>>);
impl<'a> Materials<'a> {
    pub(crate) fn new(primitives: &'a [Primitive], floors: &PaintedFloors, walls: &[Wall]) -> Self {
        if floors.is_empty() {
            return Self(vec![]);
        }
        let max_width = walls.iter().map(|w| w.thickness).fold(0., f64::max);
        let mut strips = vec![];
        for p in primitives {
            let Shape::Polyline {
                points,
                closed: true,
                filled: true,
            } = &p.shape
            else {
                continue;
            };
            if floors.contains_key(&p.id)
                || !matches!(p.layer_kind(), LayerKind::Wall | LayerKind::Unknown)
            {
                continue;
            }
            let vertices = if points.len() == 5 && points.first() == points.last() {
                &points[..4]
            } else {
                points
            };
            let &[a, b, c, d] = vertices else { continue };
            let u = b - a;
            let v = d - a;
            let width = u.norm().min(v.norm());
            if width <= 1e-8
                || width > max_width
                || u.norm().max(v.norm()) < 2. * width
                || u.unit().dot(v.unit()).abs() > 0.01
                || c.distance(a + u + v) > 0.01 * width
            {
                continue;
            }
            let corners = [a, b, c, d];
            strips.push(Strip {
                id: &p.id,
                bounds: Bounds::new(&corners),
                corners,
            });
        }
        strips.sort_by(|a, b| a.bounds.lo.x.total_cmp(&b.bounds.lo.x));
        Self(strips)
    }
}

#[derive(Default, PartialEq)]
struct Support {
    walls: BTreeSet<String>,
    material: BTreeSet<String>,
    doors: BTreeSet<String>,
}
enum Owner<'a> {
    Wall(&'a str),
    Passage(&'a [String]),
    Material(&'a str),
}

/// Every side must be covered by finite selected wall material. Observed
/// wall bodies still span hosted door cuts. An actual break in their extents
/// requires a separately established door between terminal walls.
fn enclosure(
    ring: &Ring,
    walls: &[Wall],
    openings: &[Opening],
    source: &str,
    material: &Materials<'_>,
    work: &mut usize,
) -> Result<Option<Support>, String> {
    let mut owners = Support::default();
    let mut wall_direction = None;
    let mut nonparallel = false;
    let eps = epsilon(ring.bounds).max(1e-7);
    for (a, b) in edges(&ring.points) {
        charge(work, walls.len() + openings.len())?;
        let length = a.distance(b);
        let dir = (b - a).unit();
        let mut spans = Vec::new();
        let interval = |p: Point, q: Point, half_width: f64| {
            let d = q - p;
            if d.unit().dot(dir).abs() < 0.999
                || (p - a).cross(dir).abs().max((q - a).cross(dir).abs()) > half_width + eps
            {
                return None;
            }
            let x = (p - a).dot(dir);
            let y = (q - a).dot(dir);
            let mut lo = x.min(y);
            let mut hi = x.max(y);
            if hi <= 0. || lo >= length {
                return None;
            }
            // A correct butt joint can end at the face of a transverse return.
            // Only an actual projected wall interval supports the side interior.
            if lo <= eps {
                lo = 0.
            }
            if hi >= length - eps {
                hi = length
            }
            (hi > lo).then_some((lo, hi))
        };
        for wall in walls {
            if wall.curve.is_none()
                && wall.source_ids.iter().any(|s| s != source)
                && let Some((lo, hi)) = interval(wall.start, wall.end, wall.thickness * 0.5)
            {
                spans.push((lo, hi, Owner::Wall(&wall.id)));
            }
        }
        for o in openings
            .iter()
            .filter(|o| o.kind == OpeningKind::Door && !o.swing_observations.is_empty())
        {
            let half = o
                .host_wall_ids
                .iter()
                .filter_map(|id| walls.iter().find(|w| &w.id == id))
                .map(|w| w.thickness * 0.5)
                .fold(0., f64::max);
            charge(work, o.host_wall_ids.len().saturating_mul(walls.len()))?;
            let x = (o.start - a).dot(dir);
            let y = (o.end - a).dot(dir);
            if x.min(y) >= -eps
                && x.max(y) <= length + eps
                && let Some((lo, hi)) = interval(o.start, o.end, half)
            {
                owners.doors.insert(o.id.clone());
                if o.evidence == OpeningEvidence::WallGapSwing {
                    spans.push((lo, hi, Owner::Passage(&o.host_wall_ids)));
                }
            }
        }
        for strip in material
            .0
            .iter()
            .take_while(|s| s.bounds.lo.x <= a.x.max(b.x) + eps)
        {
            charge(work, 1)?;
            if strip.bounds.hi.x < a.x.min(b.x) - eps
                || strip.bounds.hi.y < a.y.min(b.y) - eps
                || strip.bounds.lo.y > a.y.max(b.y) + eps
            {
                continue;
            }
            // All paint must lie outside the counter-clockwise floor contour.
            if strip.corners.iter().any(|p| dir.cross(*p - a) > eps) {
                continue;
            }
            for (p, q) in edges(&strip.corners) {
                if let Some((lo, hi)) = interval(p, q, eps) {
                    spans.push((lo, hi, Owner::Material(strip.id)));
                }
            }
        }
        // Resolve the union in two passes. Prefer physical wall observations
        // whenever they cover a point. Painted trim fills only observed gaps.
        spans.sort_by(|a, b| a.0.total_cmp(&b.0).then(b.1.total_cmp(&a.1)));
        let mut covered = 0.;
        while covered < length - eps {
            charge(work, spans.len())?;
            let Some((_, end, owner)) = spans
                .iter()
                .take_while(|(lo, _, _)| *lo <= covered + eps)
                .filter(|(_, hi, _)| *hi > covered + eps)
                .max_by(|a, b| {
                    (!matches!(a.2, Owner::Material(_)))
                        .cmp(&(!matches!(b.2, Owner::Material(_))))
                        .then(a.1.total_cmp(&b.1))
                })
            else {
                return Ok(None);
            };
            match owner {
                Owner::Wall(id) => {
                    owners.walls.insert((*id).into());
                    if wall_direction.is_some_and(|u: Point| u.cross(dir).abs() > 0.5) {
                        nonparallel = true;
                    }
                    wall_direction.get_or_insert(dir);
                }
                Owner::Passage(ids) => owners.walls.extend(ids.iter().cloned()),
                Owner::Material(id) => {
                    owners.material.insert((*id).into());
                }
            }
            covered = *end;
        }
    }
    if !owners.material.is_empty() && (!nonparallel || owners.doors.is_empty()) {
        return Ok(None);
    }
    Ok(Some(owners))
}

fn resolve(
    proof: &mut RoomBoundaryEvidence,
    ring: &Ring,
    walls: &[Wall],
    openings: &[Opening],
    materials: &Materials<'_>,
    work: &mut usize,
) -> Result<Option<Vec<String>>, String> {
    match proof {
        RoomBoundaryEvidence::RoomLayerPerimeter { .. } => Ok(Some(vec![])),
        RoomBoundaryEvidence::MeasuredFloorFill {
            source_id,
            perimeter_source_ids,
            entrance_opening_ids,
            ..
        } => {
            let Some(support) = enclosure(ring, walls, openings, source_id, materials, work)?
            else {
                return Ok(None);
            };
            *perimeter_source_ids = support.material.into_iter().collect();
            *entrance_opening_ids = support.doors.into_iter().collect();
            Ok(Some(support.walls.into_iter().collect()))
        }
    }
}

pub(crate) fn append(
    page: &Page,
    floors: &PaintedFloors,
    annotations: &mut [RoomAnnotation],
    walls: &[Wall],
    openings: &[Opening],
    rooms: &mut Vec<Room>,
    o: &Options,
) -> Result<(), String> {
    let mut work = 32_000_000;
    let mut perimeters = Vec::new();
    for p in &page.primitives {
        if let Some(proof) = evidence(p, floors)
            && let Some(ring) = Ring::from_source(p, &mut work)?
        {
            if let Some(f) = floors.get(&p.id) {
                annotations[f.annotation].floor_footprint = Some(FloorFootprint {
                    source_id: p.id.clone(),
                    polygon: ring.points.clone(),
                    witness_source_ids: f.witnesses.clone(),
                });
            }
            perimeters.push((ring, proof));
        }
    }
    if perimeters.is_empty() {
        return Ok(());
    }
    let mut conflicts = vec![false; perimeters.len()];
    let existing = rooms
        .iter()
        .map(|r| (r.polygon.as_slice(), Bounds::new(&r.polygon)))
        .collect::<Vec<_>>();
    for (i, (ring, _)) in perimeters.iter().enumerate() {
        charge(&mut work, i + existing.len())?;
        let current = (ring.points.as_slice(), ring.bounds);
        for &graph_face in &existing {
            if overlap(current, graph_face, &mut work)? {
                conflicts[i] = true;
                break;
            }
        }
        for (j, (other, _)) in perimeters[..i].iter().enumerate() {
            if overlap(current, (&other.points, other.bounds), &mut work)? {
                conflicts[i] = true;
                conflicts[j] = true;
            }
        }
    }
    let old_count = rooms.len();
    let material = Materials::new(&page.primitives, floors, walls);
    for ((ring, mut proof), conflict) in perimeters.into_iter().zip(conflicts) {
        if conflict || ring.area < o.min_room_area {
            continue;
        }
        let Some(wall_ids) = resolve(&mut proof, &ring, walls, openings, &material, &mut work)?
        else {
            continue;
        };
        rooms.push(Room {
            id: format!("p{}:room:{}", page.number, rooms.len() + 1),
            polygon: ring.points,
            area: ring.area,
            boundary_evidence: Some(proof),
            wall_ids,
            label: None,
            label_source_id: None,
            requires_review: true,
        });
    }
    super::assign_room_layer_labels(page, &mut rooms[old_count..], &mut work)
}

pub(crate) fn validate(
    room: &Room,
    p: &Primitive,
    floors: &PaintedFloors,
    walls: &[Wall],
    openings: &[Opening],
    materials: &Materials<'_>,
    work: &mut usize,
) -> Result<(), String> {
    let Some(mut proof) = evidence(p, floors) else {
        return Err("missing room boundary evidence".into());
    };
    if !room.requires_review {
        return Err("room boundary evidence disagrees with its source".into());
    }
    let ring = Ring::from_source(p, work)?.ok_or("invalid authored room perimeter")?;
    if ring.points != room.polygon {
        return Err("authored room polygon differs from its primitive".into());
    }
    let owners = resolve(&mut proof, &ring, walls, openings, materials, work)?
        .ok_or("painted room lacks enclosure")?;
    if Some(proof) != room.boundary_evidence || owners != room.wall_ids {
        return Err("room enclosure does not match observed material".into());
    }
    Ok(())
}
pub(crate) fn validate_observation(
    foot: &FloorFootprint,
    p: &Primitive,
    index: usize,
    floors: &PaintedFloors,
    work: &mut usize,
) -> Result<(), String> {
    let source = floors
        .get(&foot.source_id)
        .ok_or("uncorroborated floor footprint")?;
    if index != source.annotation
        || foot.witness_source_ids != source.witnesses
        || Ring::from_source(p, work)?.is_none_or(|r| r.points != foot.polygon)
    {
        return Err("floor footprint differs from its printed area or source geometry".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn p(x: f64, y: f64) -> Point {
        Point::new(x, y)
    }
    fn wall(id: &str, start: Point, end: Point) -> Wall {
        Wall {
            id: id.into(),
            page: 1,
            start,
            end,
            thickness: 2.,
            body: BodyEvidence::ParallelFaces,
            source_ids: vec![format!("faces-{id}")],
            curve: None,
            requires_review: true,
        }
    }
    fn strip(id: &'static str, corners: [Point; 4]) -> Strip<'static> {
        Strip {
            id,
            bounds: Bounds::new(&corners),
            corners,
        }
    }
    #[test]
    fn entered_room_requires_every_remaining_side_to_have_its_own_exterior_material() {
        let points = vec![p(0., 0.), p(40., 0.), p(40., 30.), p(0., 30.)];
        let bounds = Bounds::new(&points);
        let ring = Ring {
            points,
            bounds,
            area: 1200.,
        };
        let walls = [
            wall("south", p(0., -1.), p(40., -1.)),
            wall("east", p(41., 0.), p(41., 30.)),
        ];
        let hinge = p(12., -1.);
        let tip = p(24., -1.);
        let door = Opening {
            physical_jambs: None,
            id: "door".into(),
            page: 1,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: "south".into(),
            host_wall_ids: vec!["south".into()],
            start: hinge,
            end: tip,
            hinge: Some(hinge),
            swing_observations: vec![SwingObservation {
                arc_source_id: "arc".into(),
                hinge,
                closed_tip: tip,
                open_tip: p(12., 11.),
                leaf_tolerance: 0.1,
                open_leaf_source_ids: vec!["leaf".into()],
                closed_leaf_source_ids: vec![],
            }],
            source_ids: vec!["arc".into(), "leaf".into()],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
        };
        let material = |left: f64, stop: f64| {
            Materials(vec![
                strip(
                    "return",
                    [
                        p(left, 0.),
                        p(left - 0.25, 0.),
                        p(left - 0.25, 30.),
                        p(left, 30.),
                    ],
                ),
                strip(
                    "back",
                    [p(0., 30.), p(stop, 30.), p(stop, 30.4), p(0., 30.4)],
                ),
            ])
        };
        let detect = |ws: &[Wall], doors: &[Opening], m: &Materials| {
            enclosure(&ring, ws, doors, "floor", m, &mut 10000).unwrap()
        };
        let doors = std::slice::from_ref(&door);
        let s = detect(&walls, doors, &material(0., 40.)).unwrap();
        assert_eq!(
            s.material.into_iter().collect::<Vec<_>>(),
            ["back", "return"]
        );
        assert!(detect(&walls, &[], &material(0., 40.)).is_none());
        assert!(detect(&walls, doors, &material(0., 37.)).is_none());
        assert!(detect(&walls, doors, &material(0.25, 40.)).is_none());
        assert!(detect(&walls[..1], doors, &material(0., 40.)).is_none());
    }
}
