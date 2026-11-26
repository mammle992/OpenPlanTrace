//! Use the measured core axis along a continuous authored wall course.
use super::*;
use crate::model::Wall;

pub(super) fn offsets(page: &PageScan, eligible: &[usize]) -> HashMap<usize, Point> {
    let mut offsets = HashMap::new();
    if eligible.len().saturating_pow(2) > 500_000 {
        return offsets;
    }
    let tiles: HashMap<_, _> = page
        .material_tiles
        .iter()
        .map(|t| (t.id.as_str(), t))
        .collect();
    let decisions: HashMap<_, _> = page
        .placement_decisions
        .iter()
        .flatten()
        .map(|d| (d.wall_id.as_str(), d))
        .collect();
    for &i in eligible {
        let wall = &page.walls[i];
        let Some(d) = decisions.get(wall.id.as_str()) else {
            continue;
        };
        if d.reason != WallReason::SourceBoundedVisibleInsulationAssembly {
            continue;
        }
        let axis = (wall.end - wall.start).unit();
        let len = wall.start.distance(wall.end);
        let eps = wall.thickness * 3e-5;
        let frame = |p: Point| {
            let v = p - wall.start;
            (v.dot(axis), axis.cross(v))
        };
        let layers: Vec<_> = d
            .support_source_ids
            .iter()
            .filter_map(|id| tiles.get(id.as_str()))
            .filter_map(|t| {
                if t.repeat.unit().cross(axis).abs() > 1e-6
                    || !t.layer.as_deref().is_some_and(crate::layers::is_named_wall)
                {
                    return None;
                }
                let coords = t.outline.map(frame);
                let extent = |axis: fn((f64, f64)) -> f64| {
                    [
                        coords.into_iter().map(axis).fold(f64::INFINITY, f64::min),
                        coords
                            .into_iter()
                            .map(axis)
                            .fold(f64::NEG_INFINITY, f64::max),
                    ]
                };
                let along = extent(|q| q.0);
                let [lo, hi] = extent(|q| q.1);
                let offset = lo.midpoint(hi);
                if lo < -wall.thickness * 0.5 - eps
                    || hi > wall.thickness * 0.5 + eps
                    || offset.abs() < eps
                    || offset.abs() > wall.thickness * 0.15
                    || along[0] > eps
                    || along[1] < len - eps
                {
                    return None;
                }
                Some((offset, hi - lo, along, t.layer.as_deref()?))
            })
            .collect();
        let [(offset, width, [lo, hi], layer)] = layers.as_slice() else {
            continue;
        };
        let mut witness = [false, false];
        for &j in eligible {
            if i == j {
                continue;
            }
            let other = &page.walls[j];
            let [a, b] = [other.start, other.end].map(frame);
            if (a.1 - offset).abs().max((b.1 - offset).abs()) > eps
                || (other.thickness - width).abs() > eps
                || !has_independent_tile(other, &decisions, &tiles, layer, *width, eps)
            {
                continue;
            }
            let [start, end] = [a.0.min(b.0), a.0.max(b.0)];
            witness[0] |= (-eps..=*width + eps).contains(&(lo - end));
            witness[1] |= (-eps..=*width + eps).contains(&(start - hi));
        }
        if witness == [true, true] {
            offsets.insert(i, Point::new(-axis.y, axis.x) * *offset);
        }
    }
    offsets
}

fn has_independent_tile(
    wall: &Wall,
    decisions: &HashMap<&str, &crate::placement::WallDecision>,
    tiles: &HashMap<&str, &crate::placement::insulation::MaterialTile>,
    layer: &str,
    width: f64,
    eps: f64,
) -> bool {
    let axis = (wall.end - wall.start).unit();
    decisions.get(wall.id.as_str()).is_some_and(|d| {
        d.support_source_ids.iter().any(|id| {
            tiles.get(id.as_str()).is_some_and(|t| {
                t.layer.as_deref() == Some(layer)
                    && t.repeat.unit().cross(axis).abs() <= 1e-6
                    && (t.across.norm() - width).abs() <= eps
                    && t.outline
                        .iter()
                        .all(|p| (*p - wall.start).cross(axis).abs() <= width * 0.5 + eps)
            })
        })
    })
}
