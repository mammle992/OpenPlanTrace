//! A finite pair of drawn wall faces enclosing a complete stack of painted plies.
use super::{
    WallDecision, WallReason, WallStatus,
    material::{EdgeKey, Sources, edge_key},
    overlap, roundoff,
};
use crate::model::{BodyEvidence, PageScan, Point, Primitive, Shape, Wall};
use std::{
    cell::OnceCell,
    collections::{HashMap, HashSet},
};

struct Band<'a> {
    primitive: &'a Primitive,
    corners: [Point; 4],
    center: Point,
}

impl<'a> Band<'a> {
    fn new(primitive: &'a Primitive) -> Option<Self> {
        let corners = super::painted_rectangle(&primitive.shape)?;
        Some(Self {
            primitive,
            center: (corners[0] + corners[2]) * 0.5,
            corners,
        })
    }

    /// Both ends of each painted band must terminate exactly on the end planes of the two face strokes.
    fn interval(
        &self,
        w: &Wall,
        axis: Point,
        length: f64,
        limit: f64,
        eps: f64,
    ) -> Option<(f64, f64)> {
        let coords = self.corners.map(|corner| {
            let v = corner - w.start;
            Point::new(v.dot(axis), axis.cross(v))
        });
        let lo = coords.iter().map(|p| p.y).fold(f64::INFINITY, f64::min);
        let hi = coords.iter().map(|p| p.y).fold(f64::NEG_INFINITY, f64::max);
        if hi - lo <= eps || lo < -limit - eps || hi > limit + eps {
            return None;
        }
        for x in [0., length] {
            for y in [lo, hi] {
                if !coords.iter().any(|p| p.distance(Point::new(x, y)) <= eps) {
                    return None;
                }
            }
        }
        Some((lo, hi))
    }
}

struct Bands<'a> {
    bands: Vec<Band<'a>>,
    x: Vec<(f64, usize)>,
    y: Vec<(f64, usize)>,
    strokes: OnceCell<HashMap<(&'a str, EdgeKey), &'a str>>,
}

impl<'a> Bands<'a> {
    fn new(page: &'a PageScan, layers: &HashSet<&str>) -> Self {
        let paint: HashSet<_> = page
            .placement_paint_ids
            .iter()
            .flatten()
            .map(String::as_str)
            .collect();
        let bands: Vec<_> = page
            .primitives
            .iter()
            .filter(|p| {
                p.layer.as_deref().is_some_and(|l| layers.contains(l))
                    && paint.contains(p.id.as_str())
            })
            .filter_map(Band::new)
            .collect();
        let order = |coord: fn(Point) -> f64| {
            let mut index = bands
                .iter()
                .enumerate()
                .map(|(i, b)| (coord(b.center), i))
                .collect::<Vec<_>>();
            index.sort_by(|a, b| a.0.total_cmp(&b.0));
            index
        };
        Self {
            x: order(|p| p.x),
            y: order(|p| p.y),
            bands,
            strokes: OnceCell::new(),
        }
    }

    fn proof(&self, w: &Wall, page: &'a PageScan, sources: &Sources<'_>) -> Option<Vec<String>> {
        let mut ids = Vec::new();
        let core = w.body == BodyEvidence::FilledBody;
        let layer = match w.body {
            BodyEvidence::ParallelFaces => {
                let [a, b] = overlap::face_pair(&w.source_ids, sources, w, w.thickness)?;
                let layer = sources.get(a)?.primitive.layer.as_deref()?;
                if sources.get(b)?.primitive.layer.as_deref() != Some(layer) {
                    return None;
                }
                ids.extend([a.to_owned(), b.to_owned()]);
                layer
            }
            BodyEvidence::FilledBody => {
                let p = w.source_ids.iter().find_map(|id| {
                    sources.get(id.as_str()).filter(|s| {
                        s.exclusive_wall
                            && matches!(s.primitive.shape, Shape::Polyline { filled: true, .. })
                    })
                })?;
                p.primitive.layer.as_deref()?
            }
            _ => return None,
        };
        if w.curve.is_some() {
            return None;
        }
        let axis = (w.end - w.start).unit();
        let length = w.start.distance(w.end);
        let half = w.thickness * 0.5;
        // On an observed core, each facing must be thinner than the core itself.
        let limit = if core { half + w.thickness } else { half };
        let eps = roundoff(w).max(w.thickness * 1e-5);
        let mid = (w.start + w.end) * 0.5;
        let near = |index: &[(f64, usize)], v: f64| {
            index.partition_point(|row| row.0 < v - limit - eps)
                ..index.partition_point(|row| row.0 <= v + limit + eps)
        };
        let xs = &self.x[near(&self.x, mid.x)];
        let ys = &self.y[near(&self.y, mid.y)];
        let candidates = if xs.len() < ys.len() { xs } else { ys };
        if candidates.len() > 4096 {
            return None;
        }
        let mut intervals = Vec::new();
        for &(_, i) in candidates {
            let band = &self.bands[i];
            if band.primitive.layer.as_deref() == Some(layer)
                && let Some((lo, hi)) = band.interval(w, axis, length, limit, eps)
            {
                intervals.push((lo, hi, band));
            }
        }
        if intervals.len() < 2 {
            return None;
        }
        intervals.sort_by(|a, b| a.0.total_cmp(&b.0));
        let (lo, _, first) = *intervals.first()?;
        let (_, hi, last) = *intervals.last()?;
        if core {
            if hi <= half + eps
                || hi >= limit - eps
                || lo >= -half - eps
                || lo <= -limit + eps
                || !intervals.iter().any(|(a, b, band)| {
                    (*a + half).abs() <= eps
                        && (*b - half).abs() <= eps
                        && w.source_ids.contains(&band.primitive.id)
                })
            {
                return None;
            }
            // A central material strip may be observed as a wall on its own. The two
            // actual outer ink strokes certify the stack; only the measured core is placed.
            let strokes = self.strokes.get_or_init(|| {
                let mut wanted = HashSet::new();
                for band in &self.bands {
                    if let Some(layer) = band.primitive.layer.as_deref() {
                        for i in 0..4 {
                            wanted.insert((
                                layer,
                                edge_key(band.corners[i], band.corners[(i + 1) % 4]),
                            ));
                        }
                    }
                }
                let mut map = HashMap::new();
                for p in &page.primitives {
                    if let (Some(layer), Shape::Line { start, end }) =
                        (p.layer.as_deref(), &p.shape)
                    {
                        let key = (layer, edge_key(*start, *end));
                        if wanted.remove(&key) {
                            map.insert(key, p.id.as_str());
                        }
                    }
                }
                map
            });
            for (offset, band) in [(lo, first), (hi, last)] {
                let mut ends = band
                    .corners
                    .into_iter()
                    .filter(|p| (axis.cross(*p - w.start) - offset).abs() <= eps);
                let key = edge_key(ends.next()?, ends.next()?);
                ids.push(strokes.get(&(layer, key))?.to_string());
            }
        } else if (lo + half).abs() > eps || (hi - half).abs() > eps {
            return None;
        }
        let mut end = lo;
        for (lo, hi, band) in intervals {
            if (lo - end).abs() > eps {
                return None;
            }
            end = hi;
            ids.push(band.primitive.id.clone());
        }
        if page
            .openings
            .iter()
            .any(|o| o.host_wall_ids.contains(&w.id) || overlap::aperture_crosses(w, o))
        {
            return None;
        }
        ids.sort();
        Some(ids)
    }
}

pub(super) fn select(page: &PageScan, sources: &Sources<'_>, decisions: &mut [WallDecision]) {
    let layers = sources
        .values()
        .filter(|s| s.exclusive_wall)
        .filter_map(|s| s.primitive.layer.as_deref())
        .collect::<HashSet<_>>();
    if layers.is_empty() {
        return;
    }
    let bands = Bands::new(page, &layers);
    if bands.bands.len() < 2 {
        return;
    }
    let ready: Vec<_> = decisions
        .iter()
        .zip(&page.walls)
        .filter(|(d, _)| d.status == WallStatus::Ready)
        .map(|(_, w)| w)
        .collect();
    let proposals: Vec<_> = decisions
        .iter()
        .zip(&page.walls)
        .enumerate()
        .filter(|(_, (d, _))| d.status == WallStatus::Review)
        .filter_map(|(i, (_, w))| bands.proof(w, page, sources).map(|ids| (i, ids)))
        .collect();
    // Leave the prior decisions intact when a dense page exceeds the comparison budget.
    if proposals
        .len()
        .saturating_mul(ready.len() + proposals.len())
        > 8_000_000
    {
        return;
    }
    for (i, ids) in &proposals {
        let w = &page.walls[*i];
        if ready.iter().any(|b| overlap::parallel_bodies_overlap(w, b))
            || proposals.iter().any(|(j, _)| {
                i != j
                    && (w.body == BodyEvidence::FilledBody
                        || page.walls[*j].body == BodyEvidence::ParallelFaces)
                    && overlap::parallel_bodies_overlap(w, &page.walls[*j])
            })
        {
            continue;
        }
        let d = &mut decisions[*i];
        d.status = WallStatus::Ready;
        d.reason = if w.body == BodyEvidence::FilledBody {
            WallReason::PaintedCoreBetweenWallFaces
        } else {
            WallReason::PaintedWallLayersBetweenFaces
        };
        d.support_source_ids = ids.clone();
    }
}
