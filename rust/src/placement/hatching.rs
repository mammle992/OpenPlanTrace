//! Repeated diagonal material marks along both faces of a finite wall.
use super::{material::Sources, overlap, roundoff};
use crate::model::{PageScan, Point, Primitive, Shape, Wall};
use std::collections::HashMap;

type RangeIndex<'a> = Vec<(f64, &'a Primitive)>;

#[derive(Default)]
struct LayerStrokes<'a> {
    x: RangeIndex<'a>,
    y: RangeIndex<'a>,
}

pub(super) struct HatchIndex<'a> {
    layers: HashMap<&'a str, LayerStrokes<'a>>,
}

pub(super) struct HatchProof {
    pub(super) ids: Vec<String>,
    pub(super) depth: f64,
}

fn within<'a, 'b>(index: &'b RangeIndex<'a>, lo: f64, hi: f64) -> &'b [(f64, &'a Primitive)] {
    &index[index.partition_point(|(v, _)| *v < lo)..index.partition_point(|(v, _)| *v <= hi)]
}

struct Mark<'a> {
    id: &'a str,
    along: f64,
    tip: f64,
    depth: f64,
    advance: f64,
}

struct Strip {
    start: Point,
    axis: Point,
    length: f64,
    half: f64,
    eps: f64,
}

impl Strip {
    fn local(&self, p: Point) -> Point {
        let v = p - self.start;
        Point::new(v.dot(self.axis), self.axis.cross(v))
    }

    fn mark<'a>(&self, p: &'a Primitive) -> Option<(usize, Mark<'a>)> {
        let Shape::Line { start, end } = p.shape else {
            return None;
        };
        let mut a = self.local(start);
        let mut b = self.local(end);
        if a.y.abs() > b.y.abs() {
            std::mem::swap(&mut a, &mut b);
        }
        let depth = b.y.abs() - a.y.abs();
        let reach = b.x - a.x;
        if (a.y.abs() - self.half).abs() > self.eps
            || a.y * b.y <= 0.
            || a.x.min(b.x) < -self.eps
            || a.x.max(b.x) > self.length + self.eps
            || depth <= self.eps
            || depth > self.half * 0.5
            || reach.abs() < 0.25 * depth
            || reach.abs() > 4. * depth
        {
            return None;
        }
        // Express the direction in a common frame on both sides of the wall.
        Some((
            usize::from(a.y > 0.),
            Mark {
                id: &p.id,
                along: a.x,
                tip: b.x,
                depth,
                advance: reach * a.y.signum(),
            },
        ))
    }

    fn pitch(&self, marks: &mut [Mark<'_>]) -> Option<f64> {
        // A pair of end ticks or several isolated crossbars cannot establish a hatch.
        if marks.len() < 6 {
            return None;
        }
        marks.sort_by(|a, b| a.along.total_cmp(&b.along));
        let first = &marks[0];
        let last = marks.last()?;
        let full = &marks[1];
        let pitch = (last.along - first.along) / (marks.len() - 1) as f64;
        let tolerance = 0.01 * pitch + self.eps;
        if pitch <= self.eps
            || first.along > pitch + full.advance.abs() + self.eps
            || self.length - last.along > pitch + full.advance.abs() + self.eps
        {
            return None;
        }
        if marks
            .windows(2)
            .any(|w| (w[1].along - w[0].along - pitch).abs() > tolerance)
            || marks.iter().enumerate().any(|(i, m)| {
                if (m.depth - full.depth).abs() <= self.eps
                    && (m.advance - full.advance).abs() <= self.eps
                {
                    return false;
                }
                // At a square or mitred end a diagonal can be clipped inside
                // the corner. It must still run in the measured hatch direction.
                let at_corner = (i == 0 || i + 1 == marks.len())
                    && m.tip.min(self.length - m.tip) <= full.depth + self.eps;
                !at_corner
                    || m.depth >= full.depth
                    || (m.advance - full.advance * m.depth / full.depth).abs()
                        > self.eps + 0.01 * m.depth
            })
        {
            return None;
        }
        Some(pitch)
    }
}

impl<'a> HatchIndex<'a> {
    pub(super) fn new(page: &'a PageScan, sources: &Sources<'a>) -> Self {
        let mut layers = HashMap::<_, LayerStrokes>::new();
        for source in sources.values().filter(|s| s.exclusive_wall) {
            layers.entry(source.primitive.layer_name()).or_default();
        }
        for p in &page.primitives {
            if let Shape::Line { start, end } = p.shape
                && let Some(layer) = layers.get_mut(p.layer_name())
            {
                let mid = (start + end) * 0.5;
                layer.x.push((mid.x, p));
                layer.y.push((mid.y, p));
            }
        }
        for layer in layers.values_mut() {
            layer.x.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
            layer.y.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
        }
        Self { layers }
    }

    pub(super) fn witnesses(
        &self,
        w: &Wall,
        page: &PageScan,
        sources: &Sources<'_>,
    ) -> Option<HatchProof> {
        let faces = overlap::face_pair(&w.source_ids, sources, w, w.thickness)?;
        let first = sources.get(faces[0])?.primitive;
        let other = sources.get(faces[1])?.primitive;
        if first.layer != other.layer
            || first.layer_category_override != other.layer_category_override
        {
            return None;
        }
        let index = self.layers.get(first.layer_name())?;
        // The marks end within one quarter of the wall thickness outside each face.
        let pad = w.thickness * 0.75 + roundoff(w);
        let xs = within(
            &index.x,
            w.start.x.min(w.end.x) - pad,
            w.start.x.max(w.end.x) + pad,
        );
        let ys = within(
            &index.y,
            w.start.y.min(w.end.y) - pad,
            w.start.y.max(w.end.y) + pad,
        );
        let near = if xs.len() < ys.len() { xs } else { ys };
        // Bound optional evidence work on crowded sheets.
        if near.len() > 8192 {
            return None;
        }
        let strip = Strip {
            start: w.start,
            axis: (w.end - w.start).unit(),
            length: w.start.distance(w.end),
            half: w.thickness * 0.5,
            eps: roundoff(w).max(w.thickness * 1e-5),
        };
        let mut marks = [Vec::new(), Vec::new()];
        for &(_, p) in near {
            if p.layer_category_override == first.layer_category_override
                && let Some((side, mark)) = strip.mark(p)
            {
                marks[side].push(mark);
            }
        }
        let left_pitch = strip.pitch(&mut marks[0])?;
        let right_pitch = strip.pitch(&mut marks[1])?;
        let a = &marks[0][1];
        let b = &marks[1][1];
        if (left_pitch - right_pitch).abs() > strip.eps + left_pitch.min(right_pitch) * 0.01
            || (a.depth - b.depth).abs() > strip.eps
            || (a.advance - b.advance).abs() > strip.eps
            || page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(w, o))
        {
            return None;
        }
        let mut ids: Vec<_> = faces
            .into_iter()
            .chain(marks.iter().flatten().map(|m| m.id))
            .map(str::to_owned)
            .collect();
        ids.sort();
        Some(HatchProof {
            ids,
            depth: a.depth,
        })
    }
}
