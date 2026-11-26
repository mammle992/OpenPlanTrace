//! Connect finite bodies through visible authored material and measured door jambs.
use super::*;
use crate::model::{LayerKind, OpeningEvidence, Primitive, Shape, Wall};

fn named_wall(p: &Primitive) -> bool {
    p.layer_kind() == LayerKind::Wall
        || (p.layer_category_override.is_none()
            && p.layer.as_deref().is_some_and(crate::layers::is_named_wall))
}

struct Stroke<'a> {
    id: &'a str,
    a: Point,
    b: Point,
}

struct Patch<'a> {
    outline: Vec<Point>,
    source_ids: Vec<&'a str>,
    owner: Option<&'a str>,
    target: Option<&'a str>,
}

pub(super) struct Source<'a> {
    patches: Vec<Patch<'a>>,
    tile_layers: HashMap<&'a str, &'a str>,
    tiles: &'a [crate::placement::insulation::MaterialTile],
    ink: HashMap<&'a str, Vec<Stroke<'a>>>,
    decisions: HashMap<&'a str, &'a crate::placement::WallDecision>,
}

fn corners(w: &Wall) -> [Point; 4] {
    let u = (w.end - w.start).unit();
    let face = Point::new(-u.y, u.x) * (w.thickness * 0.5);
    [w.start + face, w.end + face, w.end - face, w.start - face]
}

impl<'a> Source<'a> {
    pub(super) fn new(page: &'a PageScan) -> Self {
        let paint: HashSet<_> = page
            .placement_paint_ids
            .iter()
            .flatten()
            .chain(page.placement_polygon_paint_ids.iter().flatten())
            .map(String::as_str)
            .collect();
        let mut patches = Vec::new();
        let mut ink: HashMap<&str, Vec<Stroke<'_>>> = HashMap::new();
        for p in &page.primitives {
            if !named_wall(p) {
                continue;
            }
            if let (Some(layer), Some(_), Shape::Line { start, end }) =
                (p.layer.as_deref(), p.stroke_width, &p.shape)
            {
                ink.entry(layer).or_default().push(Stroke {
                    id: &p.id,
                    a: *start,
                    b: *end,
                });
            }
            if paint.contains(p.id.as_str())
                && let Shape::Polyline {
                    points,
                    filled: true,
                    closed: true,
                } = &p.shape
            {
                let points = if points.len() > 1 && points.first() == points.last() {
                    &points[..points.len() - 1]
                } else {
                    points
                };
                if (3..=16).contains(&points.len()) {
                    patches.push(Patch {
                        outline: points.to_vec(),
                        source_ids: vec![&p.id],
                        owner: None,
                        target: None,
                    });
                }
            }
        }
        let mut tile_layers = HashMap::new();
        for tile in &page.material_tiles {
            let Some(layer) = tile
                .layer
                .as_deref()
                .filter(|s| crate::layers::is_named_wall(s))
            else {
                continue;
            };
            tile_layers.insert(tile.id.as_str(), layer);
            patches.push(Patch {
                outline: tile.outline.to_vec(),
                source_ids: vec![&tile.id],
                owner: None,
                target: None,
            });
        }
        let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
        for layer in crate::placement::wall_layers::layers(page) {
            if let Some(owner) = walls.get(layer.owner_id) {
                let footprint = layer.footprint(owner);
                patches.push(Patch {
                    outline: corners(&footprint).to_vec(),
                    source_ids: layer
                        .decision
                        .support_source_ids
                        .iter()
                        .map(String::as_str)
                        .collect(),
                    owner: Some(layer.owner_id),
                    target: None,
                });
            }
        }
        for joint in crate::placement::wall_layers::contacts(page) {
            patches.push(Patch {
                outline: joint.support_outline.to_vec(),
                source_ids: joint.source_ids,
                owner: Some(joint.owner_id),
                target: Some(joint.target_id),
            });
        }
        Self {
            patches,
            tile_layers,
            tiles: &page.material_tiles,
            ink,
            decisions: page
                .placement_decisions
                .iter()
                .flatten()
                .map(|d| (d.wall_id.as_str(), d))
                .collect(),
        }
    }

    fn wall_layer(&self, w: &Wall) -> Option<&'a str> {
        let d = self.decisions.get(w.id.as_str())?;
        let mut layers = d
            .support_source_ids
            .iter()
            .filter_map(|id| self.tile_layers.get(id.as_str()).copied());
        if let Some(layer) = layers.next() {
            return layers.all(|other| other == layer).then_some(layer);
        }
        if d.reason != WallReason::ContrastingCappedWallFaces {
            return None;
        }
        // Older placement proof can cite the same capped outline without
        // naming its retained batt tile. Verify the exact four corners.
        let outline = corners(w);
        let eps = w.thickness * 3e-5;
        let mut exact = self.tiles.iter().filter_map(|t| {
            let layer = self.tile_layers.get(t.id.as_str())?;
            (t.outline
                .iter()
                .all(|&a| outline.iter().any(|&b| a.distance(b) <= eps))
                && outline
                    .iter()
                    .all(|&b| t.outline.iter().any(|&a| a.distance(b) <= eps)))
            .then_some(*layer)
        });
        let layer = exact.next()?;
        exact.all(|other| other == layer).then_some(layer)
    }

    pub(super) fn exact_touch(
        &self,
        wa: &Wall,
        wb: &Wall,
        a: &Solid,
        b: &Solid,
        eps: f64,
    ) -> Option<Point> {
        if a.axis().dot(b.axis()).abs() > 1e-5 || self.wall_layer(wa)? != self.wall_layer(wb)? {
            return None;
        }
        let rect = b.rect();
        let body = a.rect();
        let normal = Point::new(-a.axis().y, a.axis().x) * a.half;
        let shape = [
            body.a + normal,
            body.b + normal,
            body.b - normal,
            body.a - normal,
        ];
        let aim = b.project(a.project(b.a).0).0;
        let point = nearest_intersection(&rect, &shape, aim, eps)?;
        (body.contains(point, eps) && rect.contains(point, eps)).then_some(point)
    }

    pub(super) fn exterior_walls(&self, page: &'a PageScan) -> HashSet<&'a str> {
        page.walls
            .iter()
            .filter(|w| {
                self.decisions.get(w.id.as_str()).is_some_and(|d| {
                    d.status == WallStatus::Ready
                        && (d.reason == WallReason::SourceBoundedExteriorCore
                            || self
                                .wall_layer(w)
                                .is_some_and(crate::placement::exterior_cores::exterior_layer))
                })
            })
            .map(|w| w.id.as_str())
            .collect()
    }

    pub(super) fn door(
        &self,
        page: &PageScan,
        o: &Opening,
        eligible: &[usize],
        eps: f64,
        work: &mut usize,
    ) -> Result<Option<(Rect, FramedDoor)>, String> {
        if o.kind != OpeningKind::Door
            || o.evidence != OpeningEvidence::FiniteJambSwing
            || !matches!(o.swing_observations.as_slice(), [s] if !s.open_leaf_source_ids.is_empty() && !s.closed_leaf_source_ids.is_empty())
        {
            return Ok(None);
        }
        let swing = &o.swing_observations[0];
        let axis = (o.end - o.start).unit();
        let len = o.start.distance(o.end);
        if !len.is_finite() || len <= eps {
            return Ok(None);
        }
        let local = |p: Point| (p - o.start).dot(axis);
        let mut matches = [Vec::new(), Vec::new()];
        for &i in eligible {
            charge(work, 1)?;
            let w = &page.walls[i];
            let tolerance = w.thickness * 3e-5;
            if (w.end - w.start).unit().cross(axis).abs() > 1e-6
                || [swing.hinge, swing.closed_tip].iter().any(|p| {
                    (*p - w.start).cross(axis).abs()
                        > w.thickness * 0.65 + swing.leaf_tolerance + tolerance
                })
            {
                continue;
            }
            let mut ends = [w.start, w.end];
            if local(ends[0]) > local(ends[1]) {
                ends.swap(0, 1);
            }
            let (side, end) = if (-w.thickness * 0.85..=tolerance).contains(&local(ends[1])) {
                (0, ends[1])
            } else if (len - tolerance..=len + w.thickness * 0.85).contains(&local(ends[0])) {
                (1, ends[0])
            } else {
                continue;
            };
            let Some(layer) = self.wall_layer(w) else {
                continue;
            };
            let Some(lines) = self.ink.get(layer) else {
                continue;
            };
            charge(work, lines.len())?;
            if let Some(sources) = cap(end, axis, w.thickness * 0.5, lines, tolerance) {
                matches[side].push((w, layer, sources));
            }
        }
        let ([a], [b]) = (matches[0].as_slice(), matches[1].as_slice()) else {
            return Ok(None);
        };
        if a.1 != b.1
            || !a
                .2
                .iter()
                .chain(&b.2)
                .any(|id| o.source_ids.iter().any(|s| s == id))
        {
            return Ok(None);
        }
        // Compare the signed leaf in the measured cap lane. The observed
        // leaf and its originally associated review host stay immutable.
        let mut measured = o.clone();
        let on_lane = |p: Point| a.0.start + axis * ((p - a.0.start).dot(axis));
        measured.start = on_lane(o.start);
        measured.end = on_lane(o.end);
        let Some((rect, mut proof)) = framed_door(
            &measured,
            &[a.0, b.0],
            eps.max(a.0.thickness.min(b.0.thickness) * 3e-5),
        ) else {
            return Ok(None);
        };
        proof.source_ids = ids(proof
            .source_ids
            .into_iter()
            .chain(a.2.iter().chain(&b.2).map(|s| (*s).to_owned())));
        Ok(Some((rect, proof)))
    }

    pub(super) fn join(
        &self,
        page: &PageScan,
        solids: &mut [Solid],
        apertures: &[Aperture],
        links: &mut Vec<Link>,
        eps: f64,
        work: &mut usize,
    ) -> Result<(), String> {
        // Reuse exact intersections with existing material. Bound the optional
        // search independently on pages with dense authored geometry.
        if solids
            .len()
            .saturating_pow(2)
            .saturating_mul(self.patches.len())
            > 2_000_000
        {
            return Ok(());
        }
        let mut linked: HashSet<_> = links
            .iter()
            .filter(|l| l.kind == ConnectionKind::MaterialContact)
            .filter_map(|l| {
                Some((
                    l.anchors[0]?.min(l.anchors[1]?),
                    l.anchors[0]?.max(l.anchors[1]?),
                ))
            })
            .collect();
        let known_pairs: HashSet<_> = links
            .iter()
            .filter(|e| {
                matches!(
                    e.kind,
                    ConnectionKind::MaterialContact | ConnectionKind::MeasuredCorner
                )
            })
            .filter_map(|e| {
                let [a, b] = e.wall_ids.as_slice() else {
                    return None;
                };
                Some((a.clone(), b.clone()))
            })
            .collect();
        for i in 0..solids.len() {
            for j in i + 1..solids.len() {
                charge(work, 1)?;
                let (a, b) = (&solids[i], &solids[j]);
                if a.wall == b.wall
                    || a.axis().dot(b.axis()).abs() > 1e-5
                    || linked.contains(&(i, j))
                {
                    continue;
                }
                let wa = &page.walls[a.wall];
                let wb = &page.walls[b.wall];
                let key = if wa.id < wb.id {
                    (wa.id.clone(), wb.id.clone())
                } else {
                    (wb.id.clone(), wa.id.clone())
                };
                if known_pairs.contains(&key) {
                    continue;
                }
                let (r, s) = (a.rect(), b.rect());
                let (ab, bb) = (r.bbox(), s.bbox());
                let radius = 2. * a.half.max(b.half);
                if ab[0] > bb[2] + radius
                    || bb[0] > ab[2] + radius
                    || ab[1] > bb[3] + radius
                    || bb[1] > ab[3] + radius
                {
                    continue;
                }
                for patch in &self.patches {
                    charge(work, 1)?;
                    if patch
                        .owner
                        .is_some_and(|owner| owner != wa.id && owner != wb.id)
                        || patch
                            .target
                            .is_some_and(|target| target != wa.id && target != wb.id)
                    {
                        continue;
                    }
                    let p = || intersection(&r, &patch.outline, eps);
                    let q = || intersection(&s, &patch.outline, eps);
                    let (p, q) = match patch.owner {
                        Some(owner) if owner == wa.id => {
                            let toward = b.project(a.project(b.a).0).0;
                            let Some(q) = nearest_intersection(&s, &patch.outline, toward, eps)
                            else {
                                continue;
                            };
                            (q, q)
                        }
                        Some(_) => {
                            let toward = a.project(b.project(a.a).0).0;
                            let Some(p) = nearest_intersection(&r, &patch.outline, toward, eps)
                            else {
                                continue;
                            };
                            (p, p)
                        }
                        None => {
                            let (Some(p), Some(q)) = (p(), q()) else {
                                continue;
                            };
                            (p, q)
                        }
                    };
                    let center = (p + q) * 0.5;
                    let from = a.project(p).0;
                    let to = b.project(q).0;
                    // Keep the join local to the meeting faces even when the
                    // source paint is one continuous long wall layer.
                    if p.distance(q) > radius
                        || from.distance(to) > 2. * radius
                        || !covered_by_joint(from, center, &[r], &patch.outline, eps)
                        || !covered_by_joint(to, center, &[s], &patch.outline, eps)
                        || crosses_aperture(from, center, apertures, None, eps, work)?
                        || crosses_aperture(to, center, apertures, None, eps, work)?
                    {
                        continue;
                    }
                    let proof = ids(patch
                        .source_ids
                        .iter()
                        .map(|id| (*id).to_owned())
                        .chain(wa.source_ids.iter().chain(&wb.source_ids).cloned()));
                    let wall_ids = ids([wa.id.clone(), wb.id.clone()]);
                    for (index, point) in [(i, from), (j, to)] {
                        solids[index].cut(point);
                        push_link(
                            links,
                            Link {
                                a: point,
                                b: center,
                                kind: ConnectionKind::MaterialContact,
                                wall_ids: wall_ids.clone(),
                                opening_ids: vec![],
                                source_ids: proof.clone(),
                                evidence: "finite_contact_through_authored_wall_material",
                                anchors: [Some(index), None],
                            },
                        )?;
                    }
                    linked.insert((i, j));
                    break;
                }
            }
        }
        Ok(())
    }
}

fn intersection(rect: &Rect, polygon: &[Point], eps: f64) -> Option<Point> {
    polygon
        .iter()
        .zip(polygon.iter().cycle().skip(1))
        .filter_map(|(&a, &b)| {
            rect.clip(a, b, eps)
                .map(|(lo, hi)| a + (b - a) * lo.midpoint(hi))
        })
        .min_by(|a, b| {
            let distance = |&p: &Point| on_segment(p, rect.a, rect.b).0.distance(p);
            distance(a).total_cmp(&distance(b))
        })
}

fn nearest_intersection(rect: &Rect, polygon: &[Point], target: Point, eps: f64) -> Option<Point> {
    polygon
        .iter()
        .zip(polygon.iter().cycle().skip(1))
        .filter_map(|(&a, &b)| {
            let (lo, hi) = rect.clip(a, b, eps)?;
            let d = b - a;
            Some(on_segment(target, a + d * lo, a + d * hi).0)
        })
        .min_by(|a, b| a.distance(target).total_cmp(&b.distance(target)))
}

fn cap<'a>(
    at: Point,
    axis: Point,
    half: f64,
    ink: &[Stroke<'a>],
    eps: f64,
) -> Option<Vec<&'a str>> {
    let mut intervals: Vec<_> = ink
        .iter()
        .filter_map(|s| {
            let a = s.a - at;
            let b = s.b - at;
            if a.dot(axis).abs().max(b.dot(axis).abs()) > eps {
                return None;
            }
            let (u, v) = (axis.cross(a), axis.cross(b));
            let (lo, hi) = (u.min(v).max(-half), u.max(v).min(half));
            (hi - lo > eps).then_some((lo, hi, s.id))
        })
        .collect();
    intervals.sort_by(|a, b| {
        a.0.total_cmp(&b.0)
            .then(b.1.total_cmp(&a.1))
            .then(a.2.cmp(b.2))
    });
    let mut end = -half;
    let mut proof = Vec::new();
    for (lo, hi, id) in intervals {
        if hi <= end + eps {
            continue;
        }
        if lo > end + eps {
            return None;
        }
        proof.push(id);
        end = hi;
        if end >= half - eps {
            return Some(proof);
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn split_cap_requires_full_transverse_ink() {
        let p = Point::new;
        let center = p(10., 20.);
        let axis = p(1., 0.);
        let mut strokes = [
            Stroke {
                id: "one face",
                a: p(10., 17.),
                b: p(10., 19.),
            },
            Stroke {
                id: "other face",
                a: p(10., 19.),
                b: p(10., 23.),
            },
        ];
        assert_eq!(
            cap(center, axis, 3., &strokes, 1e-8).unwrap(),
            ["one face", "other face"]
        );
        strokes[1].a.y += 0.001;
        assert!(cap(center, axis, 3., &strokes, 1e-8).is_none());
    }
}
