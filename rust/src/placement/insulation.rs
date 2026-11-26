//! A finite PDF paint operation and its repeating material cell.
mod finish;
pub(super) mod nested;
use super::{material::Sources, overlap};
use crate::model::{LayerKind, PageScan, Point, Wall};
use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MaterialTile {
    pub id: String,
    pub layer: Option<String>,
    /// Outline and pattern lattice are in page coordinates. Strokes are in unit-cell coordinates.
    pub outline: [Point; 4],
    pub origin: Point,
    pub repeat: Point,
    pub across: Point,
    pub strokes: Vec<[Point; 2]>,
}

const CELL_EPS: f64 = 0.00005;
pub(crate) const MAX_STROKES: usize = 128;

/// Reconstruct the stroked path regardless of stroke order or pen direction. Batt
/// insulation doubles back within each leg, reaching alternating opposite faces.
/// Straight crosshatching, a sinusoid and an outline grid lack these return bends.
pub(crate) fn batting(strokes: &[[Point; 2]]) -> bool {
    if !(6..=MAX_STROKES).contains(&strokes.len()) {
        return false;
    }
    let mut vertices = Vec::<Point>::new();
    let mut neighbours = Vec::<Vec<usize>>::new();
    for segment in strokes {
        let mut ids = [0; 2];
        for (i, &p) in segment.iter().enumerate() {
            if !p.finite() || p.x.min(p.y) < -CELL_EPS || p.x.max(p.y) > 1. + CELL_EPS {
                return false;
            }
            ids[i] = vertices
                .iter()
                .position(|q| p.distance(*q) <= CELL_EPS)
                .unwrap_or_else(|| {
                    vertices.push(p);
                    neighbours.push(Vec::new());
                    vertices.len() - 1
                });
        }
        let [a, b] = ids;
        if a == b
            || neighbours[a].contains(&b)
            || neighbours[a].len() == 2
            || neighbours[b].len() == 2
        {
            return false;
        }
        neighbours[a].push(b);
        neighbours[b].push(a);
    }
    let ends: Vec<_> = neighbours
        .iter()
        .enumerate()
        .filter(|(_, v)| v.len() == 1)
        .map(|(i, _)| i)
        .collect();
    let [mut a, mut b] = ends[..] else {
        return false;
    };
    if vertices[a].x > vertices[b].x {
        std::mem::swap(&mut a, &mut b);
    }
    let start = vertices[a];
    if start.x.abs() > CELL_EPS
        || (vertices[b].x - 1.).abs() > CELL_EPS
        || (start.y - vertices[b].y).abs() > CELL_EPS
        || start.y.min(1. - start.y) > CELL_EPS
    {
        return false;
    }
    let mut sign_y = if start.y < 0.5 { 1. } else { -1. };
    let mut last_tip = start.x;
    let mut leg_widths = Vec::new();
    let (mut min_dx, mut max_dx) = (0_f64, 0_f64);
    let (mut prev, mut curr) = (usize::MAX, a);
    for _ in 0..strokes.len() {
        let Some(&next) = neighbours[curr].iter().find(|&&n| n != prev) else {
            return false;
        };
        let q = vertices[next];
        let delta = q - vertices[curr];
        if delta.y * sign_y <= CELL_EPS {
            return false;
        }
        min_dx = min_dx.min(delta.x);
        max_dx = max_dx.max(delta.x);
        let target = if sign_y > 0. { 1. } else { 0. };
        if (q.y - target).abs() <= CELL_EPS {
            let width = q.x - last_tip;
            if width <= CELL_EPS || -min_dx < width * 0.1 || max_dx < width * 0.1 {
                return false;
            }
            leg_widths.push(width);
            last_tip = q.x;
            sign_y = -sign_y;
            min_dx = 0.;
            max_dx = 0.;
        }
        prev = curr;
        curr = next;
    }
    curr == b
        && leg_widths.len() >= 2
        && leg_widths.len().is_multiple_of(2)
        && leg_widths
            .iter()
            .all(|w| (w - leg_widths[0]).abs() <= 0.02 * leg_widths[0] + CELL_EPS)
}

pub(crate) fn rectangle(p: &[Point; 4]) -> bool {
    let u = p[1] - p[0];
    let v = p[3] - p[0];
    let (a, b) = (u.norm(), v.norm());
    p.iter().all(|q| q.finite())
        && a > 1e-6
        && b > 1e-6
        && u.unit().dot(v.unit()).abs() < 1e-5
        && (p[0] + u + v).distance(p[2]) < 1e-5 * a.min(b)
}

pub(crate) fn validate(tiles: &[MaterialTile]) -> Result<(), String> {
    if tiles.len() > 16384 || tiles.iter().map(|t| t.strokes.len()).sum::<usize>() > 1_000_000 {
        return Err("painted material cell budget exceeded".into());
    }
    if tiles.iter().any(|t| {
        !rectangle(&t.outline)
            || !t.origin.finite()
            || !t.repeat.finite()
            || !t.across.finite()
            || t.repeat.norm() <= 1e-6
            || t.across.norm() <= 1e-6
            || t.repeat.unit().dot(t.across.unit()).abs() > 1e-5
            || !batting(&t.strokes)
    }) {
        return Err("invalid painted material cell or finite paint geometry".into());
    }
    Ok(())
}

fn same_strip(w: &Wall, tile: &MaterialTile) -> bool {
    let axis = (w.end - w.start).unit();
    let off = Point::new(-axis.y, axis.x) * (w.thickness * 0.5);
    let corners = [w.start + off, w.end + off, w.end - off, w.start - off];
    let eps = super::roundoff(w).max(w.thickness * 1e-5);
    tile.outline
        .iter()
        .all(|p| corners.iter().any(|q| p.distance(*q) <= eps))
}

pub(super) fn aligned_row(w: &Wall, tile: &MaterialTile) -> bool {
    aligned_row_with_width_roundoff(w, tile, 1e-5)
}

pub(super) fn aligned_row_with_width_roundoff(
    w: &Wall,
    tile: &MaterialTile,
    relative_roundoff: f64,
) -> bool {
    aligned_row_with_minimum_cells(w, tile, relative_roundoff, 2.)
}

pub(super) fn aligned_row_with_minimum_cells(
    w: &Wall,
    tile: &MaterialTile,
    relative_roundoff: f64,
    minimum_cells: f64,
) -> bool {
    let axis = (w.end - w.start).unit();
    let eps = super::roundoff(w).max(w.thickness * relative_roundoff);
    let across = tile.across.unit();
    let v = (tile.outline[1] - tile.outline[0]).dot(across).abs();
    let u = (tile.outline[3] - tile.outline[0]).dot(across).abs();
    if tile.repeat.unit().cross(axis).abs() > 1e-5
        || v.min(u) > eps
        || (v.max(u) - tile.across.norm()).abs() > eps
        || w.start.distance(w.end) < minimum_cells * tile.repeat.norm()
    {
        return false;
    }
    // The row of tiles must extend from face to face. A centre-shifted strip
    // of a repeating pattern would instead cut two partial rows.
    let phase = (tile.outline[0] - tile.origin).dot(tile.across) / tile.across.dot(tile.across);
    (phase - phase.round()).abs() <= 0.001
}

pub(super) struct TileIndex<'a> {
    x: Vec<(f64, &'a MaterialTile)>,
    y: Vec<(f64, &'a MaterialTile)>,
}
impl<'a> TileIndex<'a> {
    pub(super) fn new(page: &'a PageScan) -> Self {
        let mut x = Vec::with_capacity(page.material_tiles.len());
        let mut y = Vec::with_capacity(page.material_tiles.len());
        for t in &page.material_tiles {
            let center = (t.outline[0] + t.outline[2]) * 0.5;
            x.push((center.x, t));
            y.push((center.y, t));
        }
        x.sort_by(|a, b| a.0.total_cmp(&b.0));
        y.sort_by(|a, b| a.0.total_cmp(&b.0));
        Self { x, y }
    }
    fn near<'b>(&'b self, w: &Wall) -> &'b [(f64, &'a MaterialTile)] {
        fn slice<'a, 'b>(
            a: &'b [(f64, &'a MaterialTile)],
            v: f64,
            delta: f64,
        ) -> &'b [(f64, &'a MaterialTile)] {
            &a[a.partition_point(|(x, _)| *x < v - delta)
                ..a.partition_point(|(x, _)| *x <= v + delta)]
        }
        let center = (w.start + w.end) * 0.5;
        let delta = w.thickness * 0.5 + super::roundoff(w);
        let xs = slice(&self.x, center.x, delta);
        let ys = slice(&self.y, center.y, delta);
        if xs.len() < ys.len() { xs } else { ys }
    }
    fn candidates<'b>(&'b self, w: &'b Wall) -> impl Iterator<Item = &'a MaterialTile> + 'b {
        let near = self.near(w);
        let bounded = if near.len() > 4096 { &[][..] } else { near };
        bounded
            .iter()
            .map(|(_, t)| *t)
            .filter(|t| aligned_row(w, t))
    }
    pub(super) fn proof(
        &self,
        w: &Wall,
        page: &PageScan,
        sources: &Sources<'_>,
    ) -> Option<Vec<String>> {
        if w.source_ids
            .iter()
            .filter_map(|id| sources.get(id.as_str()))
            .any(|s| {
                matches!(
                    s.primitive.layer_kind(),
                    LayerKind::Object | LayerKind::Window | LayerKind::Door
                )
            })
        {
            return None;
        }
        let pair = overlap::face_pair(&w.source_ids, sources, w, w.thickness)?;
        let a = sources.get(pair[0])?.primitive;
        let b = sources.get(pair[1])?.primitive;
        if a.layer != b.layer || a.layer.is_none() {
            return None;
        }
        for t in self.candidates(w).filter(|t| t.layer == a.layer) {
            let mut ids = if same_strip(w, t) {
                Vec::new()
            } else if let Some(ids) = finish::proof(w, t, page, sources) {
                ids
            } else {
                continue;
            };
            if page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(w, o))
            {
                return None;
            }
            ids.extend([pair[0].to_owned(), pair[1].to_owned(), t.id.clone()]);
            ids.sort();
            ids.dedup();
            return Some(ids);
        }
        None
    }
}
