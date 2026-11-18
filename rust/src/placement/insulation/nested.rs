//! Resolve a narrower measured strip whose two face strokes end at a staggered joint.
use super::{MaterialTile, TileIndex};
use crate::model::{BodyEvidence, PageScan, Point, Shape, Wall};
use crate::placement::{Support, material::Sources, overlap, roundoff};

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    eps: f64,
}

impl Frame {
    fn local(&self, p: Point) -> Point {
        let v = p - self.origin;
        Point::new(v.dot(self.axis), self.axis.cross(v))
    }

    fn painted_ends(&self, tile: &MaterialTile, thickness: f64) -> Option<[f64; 2]> {
        let corners = tile.outline.map(|p| self.local(p));
        let lo = corners.iter().map(|p| p.x).fold(f64::INFINITY, f64::min);
        let hi = corners
            .iter()
            .map(|p| p.x)
            .fold(f64::NEG_INFINITY, f64::max);
        for along in [lo, hi] {
            for across in [-thickness / 2., thickness / 2.] {
                if !corners
                    .iter()
                    .any(|p| p.distance(Point::new(along, across)) <= self.eps)
                {
                    return None;
                }
            }
        }
        Some([lo, hi])
    }

    fn faces<'a>(
        &self,
        w: &'a Wall,
        sources: &Sources<'_>,
        tile: &MaterialTile,
        accept: impl Fn(f64, f64) -> bool,
    ) -> [Option<&'a str>; 2] {
        let mut found = [None, None];
        for id in &w.source_ids {
            let Some(s) = sources
                .get(id.as_str())
                .filter(|s| s.exclusive_wall && s.primitive.layer == tile.layer)
            else {
                continue;
            };
            let Shape::Line { start, end } = s.primitive.shape else {
                continue;
            };
            let (a, b) = (self.local(start), self.local(end));
            if (a.y - b.y).abs() <= self.eps
                && (a.y.abs() - w.thickness / 2.).abs() <= self.eps
                && accept(a.x.min(b.x), a.x.max(b.x))
            {
                found[usize::from(a.y > 0.)].get_or_insert(id.as_str());
            }
        }
        found
    }
}

pub(in crate::placement) fn proof(
    w: &Wall,
    wider: &Wall,
    support: &Support,
    page: &PageScan,
    sources: &Sources<'_>,
    index: &TileIndex<'_>,
) -> Option<Vec<String>> {
    let frame = Frame {
        origin: w.start,
        axis: (w.end - w.start).unit(),
        length: w.start.distance(w.end),
        eps: roundoff(w).max(w.thickness * 1e-5),
    };
    let eps = frame.eps;
    let len = frame.length;
    let skin = (wider.thickness - w.thickness) * 0.5;
    if w.body != BodyEvidence::ParallelFaces
        || wider.body != BodyEvidence::ParallelFaces
        || wider.curve.is_some()
        || skin <= eps
        || skin >= w.thickness / 2.
        || w.source_ids
            .iter()
            .any(|id| !sources.get(id.as_str()).is_some_and(|s| s.exclusive_wall))
        || support.transverse_drift > eps
        || support.spans.first()?.0.abs() > eps
        || (support.spans.last()?.1 - len).abs() > eps
    {
        return None;
    }
    // The co-owned graph represents the same finite centerline in both directions.
    let ends = [wider.start, wider.end].map(|p| frame.local(p));
    if ends.iter().any(|p| p.y.abs() > eps)
        || ends[0].x.min(ends[1].x).abs() > eps
        || (ends[0].x.max(ends[1].x) - len).abs() > eps
    {
        return None;
    }
    for tile in index.candidates(w) {
        let Some([lo, hi]) = frame.painted_ends(tile, w.thickness) else {
            continue;
        };
        // A single finish thickness lies beyond one end of the shared span.
        let at_start = (lo + skin).abs() <= eps && (hi - len).abs() <= eps;
        let at_end = lo.abs() <= eps && (hi - len - skin).abs() <= eps;
        if !at_start && !at_end {
            continue;
        }
        let observed = frame.faces(w, sources, tile, |a, b| {
            a.abs() <= eps && (b - len).abs() <= eps
        });
        let painted = frame.faces(w, sources, tile, |a, b| {
            (a - lo).abs() <= eps && (b - hi).abs() <= eps
        });
        let Some((short, long)) = observed[0].zip(painted[1]).or(observed[1].zip(painted[0]))
        else {
            continue;
        };
        // Separate structural strokes must bound the wider observation and both
        // reach the common, unstepped end. Area or cabinet boundaries cannot vouch for it.
        let outer = frame.faces(wider, sources, tile, |a, b| {
            if at_start {
                a <= eps && (b - len).abs() <= eps
            } else {
                a.abs() <= eps && b >= len - eps
            }
        });
        let [Some(left), Some(right)] = outer else {
            continue;
        };
        if page
            .openings
            .iter()
            .any(|o| overlap::aperture_crosses(w, o))
        {
            return None;
        }
        let mut ids = vec![
            short.to_owned(),
            long.to_owned(),
            left.to_owned(),
            right.to_owned(),
            tile.id.clone(),
        ];
        ids.sort();
        return Some(ids);
    }
    None
}
