//! Reconcile independently observed plies into the same finite wall assembly.
use super::{
    WallDecision, WallReason, WallStatus, effective_wall, insulation::MaterialTile, overlap,
    roundoff,
};
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall};
use std::collections::HashMap;

mod inboard;
pub(crate) use inboard::contacts;

pub(crate) struct WallLayer<'a> {
    pub owner_id: &'a str,
    pub wall: &'a Wall,
    pub decision: &'a WallDecision,
}

/// The observation and the lining stay available after selection. Consumers
/// use the enclosing wall's axis; the lining contributes finite material only.
pub(crate) fn layers(page: &PageScan) -> impl Iterator<Item = WallLayer<'_>> {
    page.walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter_map(|(wall, decision)| {
            let [owner_id] = decision.support_wall_ids.as_slice() else {
                return None;
            };
            (decision.status == WallStatus::EvidenceOnly
                && matches!(
                    decision.reason,
                    WallReason::SourceBoundedWallLining | WallReason::SourceBoundedCavityWallLeaf
                ))
            .then_some(WallLayer {
                owner_id,
                wall,
                decision,
            })
        })
}

impl WallLayer<'_> {
    /// Include the thin seam only because source stroke paint spans it along
    /// the entire lining. The other three boundaries are the authored ply.
    pub(crate) fn footprint(&self, owner: &Wall) -> Wall {
        if self.wall.body == BodyEvidence::FilledBody {
            // A certified painted lining already has its exact finite material.
            // Its independently drawn seam can end at a different plane.
            return self.wall.clone();
        }
        let axis = (owner.end - owner.start).unit();
        let normal = Point::new(-axis.y, axis.x);
        let delta = normal.dot(self.wall.start - owner.start);
        let facing = delta.signum() * owner.thickness * 0.5;
        let gap = ((delta - facing).abs() - self.wall.thickness * 0.5).max(0.);
        let offset = normal * (-delta.signum() * gap * 0.5);
        let mut patch = self.wall.clone();
        patch.start = patch.start + offset;
        patch.end = patch.end + offset;
        patch.thickness += gap;
        patch
    }
}

struct Frame {
    start: Point,
    axis: Point,
    length: f64,
    half: f64,
}
impl Frame {
    fn new(wall: &Wall) -> Option<Self> {
        let length = wall.start.distance(wall.end);
        (wall.body == BodyEvidence::ParallelFaces
            && wall.curve.is_none()
            && length.is_finite()
            && length > 0.
            && wall.thickness.is_finite()
            && wall.thickness > 0.)
            .then(|| Self {
                start: wall.start,
                axis: (wall.end - wall.start) * (1. / length),
                length,
                half: wall.thickness * 0.5,
            })
    }

    fn at(&self, point: Point) -> [f64; 2] {
        let delta = point - self.start;
        [self.axis.dot(delta), self.axis.cross(delta)]
    }
}

struct PlyBand {
    along: [f64; 2],
    across: [f64; 2],
}

fn band(frame: &Frame, points: impl IntoIterator<Item = Point>) -> PlyBand {
    let mut lo = [f64::INFINITY; 2];
    let mut hi = [f64::NEG_INFINITY; 2];
    for p in points {
        for (i, v) in frame.at(p).into_iter().enumerate() {
            lo[i] = lo[i].min(v);
            hi[i] = hi[i].max(v);
        }
    }
    PlyBand {
        along: [lo[0], hi[0]],
        across: [lo[1], hi[1]],
    }
}

fn pair_matches(a: &MaterialTile, b: &MaterialTile) -> bool {
    // The authored cells use the same normalized motif. Its scale can differ
    // across the plies, as when the inner service lining is thinner.
    a.strokes.len() == b.strokes.len()
        && a.strokes
            .iter()
            .zip(&b.strokes)
            .all(|(a, b)| [0, 1].into_iter().all(|i| a[i].distance(b[i]) <= 1e-4))
}

struct Witnesses<'a> {
    tiles: HashMap<&'a str, &'a MaterialTile>,
    strokes: HashMap<&'a str, &'a Primitive>,
}
impl<'a> Witnesses<'a> {
    fn new(page: &'a PageScan) -> Self {
        Self {
            tiles: page
                .material_tiles
                .iter()
                .map(|t| (t.id.as_str(), t))
                .collect(),
            strokes: page
                .primitives
                .iter()
                .filter(|p| {
                    p.layer.as_deref().is_some_and(crate::layers::is_named_wall)
                        && (p.layer_category_override.is_none()
                            || p.layer_kind() == LayerKind::Wall)
                })
                .map(|p| (p.id.as_str(), p))
                .collect(),
        }
    }

    fn rail(
        &self,
        wall: &Wall,
        frame: &Frame,
        interval: [f64; 2],
        offset: f64,
        layer: &str,
        gap: f64,
        eps: f64,
        exact_owner_rail: bool,
    ) -> Option<&'a str> {
        let matches = |p: &'a Primitive, span: [f64; 2]| {
            let Shape::Line { start, end } = p.shape else {
                return None;
            };
            let width = p.stroke_width?;
            let edge = band(frame, [start, end]);
            // Both source pens must reach the other nominal face. This
            // permits rounded centre coordinates but no proximity-only join.
            (p.layer.as_deref() == Some(layer)
                && width.is_finite()
                && width * 0.5 >= gap
                && edge.along[0] <= span[0] + eps
                && edge.along[1] >= span[1] - eps
                && edge.across.iter().all(|x| (x - offset).abs() <= eps))
            .then_some(p.id.as_str())
        };
        wall.source_ids
            .iter()
            .find_map(|id| matches(self.strokes.get(id.as_str())?, interval))
            .or_else(|| {
                exact_owner_rail.then_some(())?;
                // A recovered core's former support can cite the facing
                // stroke across the seam. Require an independent exact
                // rail covering the entire authored core for this case.
                self.strokes
                    .values()
                    .filter_map(|p| matches(p, [0., frame.length]))
                    .min()
            })
    }

    fn prove(
        &self,
        owner: &Wall,
        owner_decision: &WallDecision,
        lining: &Wall,
        lining_decision: &WallDecision,
    ) -> Option<Vec<String>> {
        let frame = Frame::new(owner)?;
        let inner = Frame::new(lining)?;
        let eps = roundoff(owner).max(roundoff(lining));
        let [[a, ay], [b, by]] = [frame.at(lining.start), frame.at(lining.end)];
        let interval = [a.min(b), a.max(b)];
        let side = if ay > 0. { 1 } else { 0 };
        let sign = if side == 1 { 1. } else { -1. };
        let face = sign * frame.half;
        let gap = ay.abs() - frame.half - inner.half;
        if inner.half >= frame.half * (2. / 3.)
            || (ay - by).abs() > eps
            || gap < -eps
            || interval[0] < -eps
            || interval[1] > frame.length + eps
            || (interval[0].abs() > eps && (frame.length - interval[1]).abs() > eps)
        {
            return None;
        }
        let lining_face = face + sign * gap;
        for id in &lining_decision.support_source_ids {
            let Some(tile) = self.tiles.get(id.as_str()) else {
                continue;
            };
            let Some(layer) = tile
                .layer
                .as_deref()
                .filter(|s| crate::layers::is_named_wall(s))
            else {
                continue;
            };
            let ply = band(&frame, tile.outline);
            if !super::insulation::aligned_row(lining, tile)
                || ply.along[0] > interval[0] + eps
                || ply.along[1] < interval[1] - eps
                || (ply.across[1 - side] - lining_face).abs() > eps
                || (ply.across[side] - (lining_face + sign * lining.thickness)).abs() > eps
            {
                continue;
            }
            let Some(inside) = self.rail(
                owner,
                &frame,
                interval,
                face,
                layer,
                gap,
                eps,
                owner_decision.reason == WallReason::SourceBoundedExteriorCore,
            ) else {
                continue;
            };
            let Some(outside) = self.rail(
                lining,
                &frame,
                interval,
                lining_face,
                layer,
                gap,
                eps,
                false,
            ) else {
                continue;
            };
            if inside == outside {
                continue;
            }
            let Some(core) = owner_decision.support_source_ids.iter().find_map(|id| {
                let core = self.tiles.get(id.as_str())?;
                let bounds = band(&frame, core.outline);
                (core.layer.as_deref() == Some(layer)
                    && core.repeat.unit().cross(frame.axis).abs() <= 1e-5
                    && bounds.along[0] <= interval[0] + eps
                    && bounds.along[1] >= interval[1] - eps
                    && bounds.across[1] - bounds.across[0] >= owner.thickness * 0.75
                    && (bounds.across[side] - face).abs() <= eps
                    && bounds.across[1 - side].abs() <= frame.half + eps
                    && pair_matches(core, tile))
                .then_some(core)
            }) else {
                continue;
            };
            return Some(vec![
                inside.into(),
                outside.into(),
                core.id.clone(),
                tile.id.clone(),
            ]);
        }
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_short_material_lining_uses_the_host_axis_only_with_painted_seam_proof() {
        let point = Point::new;
        let make_wall = |id: &str, rail: &str, y, width, len| Wall {
            id: id.into(),
            page: 1,
            start: point(0., y),
            end: point(len, y),
            thickness: width,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![rail.into()],
            requires_review: true,
        };
        let core = make_wall("wall", "a", 0., 6., 40.);
        let lining = make_wall("lining", "b", 4.0625, 2., 20.);
        let decision = |wall: &Wall, tile: &str| WallDecision {
            wall_id: wall.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::TiledInsulationBetweenFaces,
            component_id: String::new(),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: vec![],
            support_source_ids: vec![tile.into()],
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        let material = |id: &str, lo, hi| MaterialTile {
            id: id.into(),
            layer: Some("Walls".into()),
            outline: [point(0., lo), point(20., lo), point(20., hi), point(0., hi)],
            origin: point(0., lo),
            repeat: point(hi - lo, 0.),
            across: point(0., hi - lo),
            strokes: vec![[point(0., 0.), point(0.5, 1.)]],
        };
        let core_tile = material("core", -2.5, 3.);
        let lining_tile = material("ply", 3.0625, 5.0625);
        let rail = |id: &str, y, thickness| Primitive {
            id: id.into(),
            layer: Some("Walls".into()),
            stroke_width: Some(thickness),
            layer_category_override: None,
            shape: Shape::Line {
                start: point(0., y),
                end: point(20., y),
            },
        };
        let [a, b, dry] = [
            rail("a", 3., 0.2),
            rail("b", 3.0625, 0.25),
            rail("b", 3.0625, 0.03),
        ];
        let [cd, ld] = [decision(&core, "core"), decision(&lining, "ply")];
        let mut witnesses = Witnesses {
            tiles: HashMap::from([("core", &core_tile), ("ply", &lining_tile)]),
            strokes: HashMap::from([("a", &a), ("b", &b)]),
        };
        assert!(witnesses.prove(&core, &cd, &lining, &ld).is_some());
        let attached = WallLayer {
            owner_id: &core.id,
            wall: &lining,
            decision: &ld,
        };
        let footprint = attached.footprint(&core);
        assert_eq!(footprint.start.y - footprint.thickness * 0.5, 3.);
        assert_eq!(footprint.end.y + footprint.thickness * 0.5, 5.0625);
        witnesses.strokes.insert("b", &dry);
        assert!(witnesses.prove(&core, &cd, &lining, &ld).is_none());
    }
}

pub(crate) fn reconcile(page: &PageScan, decisions: &mut [WallDecision]) {
    if page.material_tiles.is_empty() {
        return;
    }
    let candidates: Vec<_> = page
        .walls
        .iter()
        .zip(decisions.iter())
        .enumerate()
        .filter(|(_, (_, d))| {
            d.status == WallStatus::Ready
                && matches!(
                    d.reason,
                    WallReason::TiledInsulationBetweenFaces
                        | WallReason::SourceBoundedVisibleInsulationAssembly
                )
        })
        .map(|(i, (wall, d))| (i, effective_wall(wall, Some(d)), d))
        .collect();
    if candidates.len() < 2 || candidates.len() > 1024 {
        return;
    }
    let witnesses = Witnesses::new(page);
    let mut groups = HashMap::new();
    for (i, ply, pd) in &candidates {
        if pd.reason != WallReason::TiledInsulationBetweenFaces
            || pd.placement_extent.is_some()
            || !pd.opening_ids.is_empty()
            || page
                .openings
                .iter()
                .any(|o| o.host_wall_ids.contains(&ply.id))
        {
            continue;
        }
        let mut found = candidates.iter().filter_map(|(j, owner, od)| {
            let proof = witnesses.prove(owner, od, ply, pd)?;
            let layer = WallLayer {
                owner_id: &owner.id,
                wall: ply,
                decision: pd,
            };
            (!page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(&layer.footprint(owner), o)))
            .then_some((*j, proof))
        });
        if let Some(proof) = found.next()
            && found.next().is_none()
        {
            groups.insert(*i, proof);
        }
    }
    drop(candidates);
    for (&i, (owner, source_ids)) in &groups {
        // A demoted ply cannot itself own another layer.
        if groups.contains_key(owner) {
            continue;
        }
        let d = &mut decisions[i];
        d.status = WallStatus::EvidenceOnly;
        d.reason = WallReason::SourceBoundedWallLining;
        d.support_wall_ids = vec![page.walls[*owner].id.clone()];
        d.support_source_ids.extend(source_ids.iter().cloned());
        d.support_source_ids.sort();
        d.support_source_ids.dedup();
    }
}

/// An interior core selected after the main lining reconciliation can supply
/// the perpendicular witness for a short cap.
pub(crate) fn reconcile_attached(page: &PageScan, decisions: &mut [WallDecision]) {
    if !page.material_tiles.is_empty() {
        inboard::recover(page, decisions, &Witnesses::new(page));
    }
}
