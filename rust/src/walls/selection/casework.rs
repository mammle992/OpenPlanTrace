//! Cabinet fronts can draw several pivoted leaves along one side of a rectangular bay.
//! Require the finite returns, bay dividers and an independently observed wall behind the bay.
use super::*;

struct Backing {
    axis: Segment,
    width: f64,
    source: usize,
}
pub(super) struct Context {
    returns: Vec<usize>,
    ends: Index,
    backings: Vec<Backing>,
    bodies: Index,
}

fn ends(shape: &Shape) -> Option<(V, V)> {
    match shape {
        Shape::Polyline {
            points,
            closed: false,
            filled: false,
        } if points.len() >= 3 => Some((V::from(points.first()?), V::from(points.last()?))),
        Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } => {
            let at = |a: f64| V(center.x + radius * a.cos(), center.y + radius * a.sin());
            Some((at(*start_angle), at(start_angle + sweep)))
        }
        _ => None,
    }
}

impl Context {
    pub(super) fn new(page: &Page, walls: &[Wall], ids: &HashMap<&str, usize>) -> Self {
        let mut returns = Vec::new();
        let mut endpoints = Vec::new();
        for (source, p) in page.primitives.iter().enumerate() {
            if !matches!(
                p.layer_kind(),
                LayerKind::Unknown | LayerKind::Object | LayerKind::Door
            ) {
                continue;
            }
            if let Some((a, b)) = ends(&p.shape) {
                for point in [a, b] {
                    returns.push(source);
                    endpoints.push(Box2::new(point, point));
                }
            }
        }
        let mut backings = Vec::new();
        let mut boxes = Vec::new();
        for w in walls.iter().filter(|w| w.body == BodyEvidence::FilledBody) {
            let Some(axis) = Segment::new(V::from(&w.start), V::from(&w.end)) else {
                continue;
            };
            let Some(source) = w
                .source_ids
                .iter()
                .filter_map(|id| ids.get(id.as_str()))
                .find(|&&i| {
                    matches!(
                        page.primitives[i].shape,
                        Shape::Polyline { filled: true, .. }
                    )
                })
            else {
                continue;
            };
            boxes.push(axis.bbox().pad(w.thickness / 2.));
            backings.push(Backing {
                axis,
                width: w.thickness,
                source: *source,
            });
        }
        Self {
            returns,
            ends: Index::build(&endpoints),
            backings,
            bodies: Index::build(&boxes),
        }
    }

    pub(super) fn backing(
        &self,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        side: f64,
        opts: &Options,
    ) -> Result<Option<usize>, String> {
        let mut candidates = Vec::new();
        self.bodies.neighbors(
            axis.bbox().pad(width / 2. + opts.snap_tolerance),
            &mut candidates,
        );
        ink.charge(candidates.len())?;
        for i in candidates {
            let b = &self.backings[i];
            let (lo, hi) = range(axis, b.axis);
            if b.width >= width
                || b.axis.dir.cross(axis.dir).abs() > ANGLE.sin()
                || lo > opts.snap_tolerance
                || hi < axis.len - opts.snap_tolerance
            {
                continue;
            }
            // Both ends of the proposed bay must abut the same outside face.
            let near = |p| (height(axis, p) * side - b.width / 2. - width / 2.).abs();
            let join = (b.width / 2.).min(opts.snap_tolerance / 2.);
            if near(b.axis.a) <= join && near(b.axis.b) <= join {
                return Ok(Some(b.source));
            }
        }
        Ok(None)
    }

    pub(super) fn bay(
        &self,
        page: &Page,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        nearby: &[usize],
        opts: &Options,
        join: f64,
    ) -> Result<Option<Furniture>, String> {
        let half = width / 2.;
        let mut dividers = Vec::new();
        for &i in nearby {
            let s = ink.strokes[i];
            let (a, b) = (height(axis, s.line.a), height(axis, s.line.b));
            if (a.min(b) + half).abs() <= join
                && (a.max(b) - half).abs() <= join
                && s.line.dir.dot(axis.dir).abs() <= ANGLE.sin()
            {
                dividers.push((axis.offset(s.line.at(s.line.len / 2.)), s));
            }
        }
        let Some(&(_, start)) = dividers.iter().find(|(t, _)| t.abs() <= join) else {
            return Ok(None);
        };
        let Some(&(_, end)) = dividers.iter().find(|(t, _)| (t - axis.len).abs() <= join) else {
            return Ok(None);
        };
        let Some(&(_, middle)) = dividers
            .iter()
            .find(|(t, _)| *t > opts.snap_tolerance && *t < axis.len - opts.snap_tolerance)
        else {
            return Ok(None);
        };
        for sign in [1., -1.] {
            let Some(back) = self.backing(ink, axis, width, -sign, opts)? else {
                continue;
            };
            let mut panels = Vec::new();
            for &i in nearby {
                let stroke = ink.strokes[i];
                for (hinge, tip) in [
                    (stroke.line.a, stroke.line.b),
                    (stroke.line.b, stroke.line.a),
                ] {
                    let t = axis.offset(hinge);
                    if (height(axis, hinge) * sign - half).abs() > join
                        || height(axis, tip) * sign <= half + join
                        || t < -join
                        || t > axis.len + join
                    {
                        continue;
                    }
                    let on_front = |far| {
                        let u = axis.offset(far);
                        (height(axis, far) * sign - half).abs() <= join
                            && (-join..=axis.len + join).contains(&u)
                            && (u - t).abs() > opts.snap_tolerance
                    };
                    for (far, source) in
                        self.closed_positions(page, ink, hinge, tip, join, on_front)?
                    {
                        let u = axis.offset(far);
                        panels.push((t.min(u), t.max(u), u > t, stroke.source, source));
                    }
                }
            }
            panels.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
            // Three successive leaves, opening the same way, distinguish a cabinet front
            // from an ordinary single or double door or duplicate renderings of one leaf.
            let mut chain: Vec<(f64, f64, bool, usize, usize)> = Vec::new();
            for &(lo, hi, forward, leaf, arc) in &panels {
                if let Some(&(prev_lo, prev_hi, prev_hand, _, _)) = chain.last() {
                    if (lo - prev_lo).abs() <= join && (hi - prev_hi).abs() <= join {
                        continue;
                    }
                    if (lo - prev_hi).abs() > join || forward != prev_hand {
                        chain.clear()
                    }
                }
                chain.push((lo, hi, forward, leaf, arc));
                if chain.len() >= 3 && chain[0].0.abs() <= join && (hi - axis.len).abs() <= join {
                    let mut ids = vec![start.source, middle.source, end.source, back];
                    for &(_, _, _, l, a) in &chain {
                        ids.extend([l, a])
                    }
                    let ordered = |s: Stroke| {
                        if height(axis, s.line.a) < height(axis, s.line.b) {
                            (s.line.a, s.line.b)
                        } else {
                            (s.line.b, s.line.a)
                        }
                    };
                    let (a, d) = ordered(start);
                    let (b, c) = ordered(end);
                    return Ok(Some(Furniture {
                        sources: ids,
                        perimeter: [a.point(), b.point(), c.point(), d.point()],
                    }));
                }
            }
        }
        Ok(None)
    }

    /// A cross-panel and an outer side of a two-leaf cabinet can look like
    /// paired wall faces across the depth of one compartment. Its other
    /// compartment, front leaves and interior detail must actually be drawn.
    pub(super) fn two_leaf_bay(
        &self,
        page: &Page,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        nearby: &[usize],
        join: f64,
    ) -> Result<Option<Furniture>, String> {
        let normal = axis.dir.normal();
        'orientation: for orientation in [-1., 1.] {
            let offset = normal.times(orientation * width);
            let side = axis.a.minus(offset.times(0.5));
            let middle = side.plus(offset);
            let far = middle.plus(offset);
            let across = axis.b.minus(axis.a);
            let edge = |p: V| (p, p.plus(across));
            let mut outline = vec![];
            let mut sides = vec![];
            for (a, b) in [
                edge(side),
                edge(middle),
                edge(far),
                (side, far),
                (side.plus(across), far.plus(across)),
            ] {
                if let Some(s) = ink.joined_stroke(a, b, join)? {
                    outline.push(s.source);
                    sides.push(if s.line.a.minus(a).norm() <= join {
                        (s.line.a, s.line.b)
                    } else {
                        (s.line.b, s.line.a)
                    });
                } else {
                    continue 'orientation;
                }
            }
            let Some(detail) = compartment_marks(ink, axis, width, nearby, join)? else {
                continue;
            };
            for (end, outward) in [(V(0., 0.), axis.dir.times(-1.)), (across, axis.dir)] {
                let hinge = side.plus(end);
                let meeting = middle.plus(end);
                let other = far.plus(end);
                if let (Some(a), Some(b)) = (
                    self.panel(page, ink, hinge, meeting, outward, join)?,
                    self.panel(page, ink, other, meeting, outward, join)?,
                ) {
                    outline.extend(detail);
                    outline.extend(a);
                    outline.extend(b);
                    let (a, b) = sides[0];
                    let (d, c) = sides[2];
                    return Ok(Some(Furniture {
                        sources: outline,
                        perimeter: [a.point(), b.point(), c.point(), d.point()],
                    }));
                }
            }
        }
        Ok(None)
    }

    fn panel(
        &self,
        page: &Page,
        ink: &mut Ink,
        hinge: V,
        closure: V,
        outward: V,
        join: f64,
    ) -> Result<Option<[usize; 2]>, String> {
        let radial = closure.minus(hinge);
        for i in ink.nearby(Box2::new(hinge, hinge).pad(join))? {
            let leaf = ink.strokes[i];
            for (a, tip) in [(leaf.line.a, leaf.line.b), (leaf.line.b, leaf.line.a)] {
                let swung = tip.minus(hinge);
                if a.minus(hinge).norm() > join
                    || (swung.norm() - radial.norm()).abs() > join
                    || swung.dot(outward) <= join
                    || swung.dot(radial) <= 0.
                {
                    continue;
                }
                if let Some(&(_, source)) = self
                    .closed_positions(page, ink, hinge, tip, join, |p| {
                        p.minus(closure).norm() <= join
                    })?
                    .first()
                {
                    return Ok(Some([leaf.source, source]));
                }
            }
        }
        Ok(None)
    }

    fn closed_positions(
        &self,
        page: &Page,
        ink: &mut Ink,
        hinge: V,
        tip: V,
        join: f64,
        accept: impl Fn(V) -> bool,
    ) -> Result<Vec<(V, usize)>, String> {
        let radius = tip.minus(hinge).norm();
        let mut found = vec![];
        self.ends
            .neighbors(Box2::new(tip, tip).pad(join), &mut found);
        ink.charge(found.len())?;
        let mut out = vec![];
        for i in found {
            let source = self.returns[i];
            let shape = &page.primitives[source].shape;
            let (a, b) = ends(shape).unwrap();
            let closure = if a.minus(tip).norm() <= join {
                b
            } else if b.minus(tip).norm() <= join {
                a
            } else {
                continue;
            };
            if accept(closure) && circular_return(shape, hinge, radius, ink)? {
                out.push((closure, source));
            }
        }
        Ok(out)
    }
}

/// A second divider crosses the depth of the compartment, with two distinct,
/// detached detail strokes drawn across it but wholly inside the enclosure.
fn compartment_marks(
    ink: &mut Ink,
    axis: Segment,
    width: f64,
    nearby: &[usize],
    join: f64,
) -> Result<Option<[usize; 3]>, String> {
    let half = width / 2.;
    let bounded = |p| height(axis, p).abs() < half - join;
    for &i in nearby {
        let divider = ink.strokes[i];
        let d = divider.line;
        let t = axis.offset(d.at(d.len / 2.));
        if t <= join || t >= axis.len - join {
            continue;
        }
        let at = axis.at(t);
        let n = axis.dir.normal().times(half);
        let Some(expected) = Segment::new(at.minus(n), at.plus(n)) else {
            continue;
        };
        if !same_ends(d, expected, join) {
            continue;
        }
        ink.charge(nearby.len())?;
        let mut first: Option<Stroke> = None;
        for &j in nearby {
            let mark = ink.strokes[j];
            let line = mark.line;
            let (lo, hi) = range(axis, line);
            if lo <= join
                || hi >= axis.len - join
                || lo + join >= t
                || hi - join <= t
                || !bounded(line.a)
                || !bounded(line.b)
                || line.dir.dot(axis.dir).abs() < 0.5
                || !ink.free_ends(j, join)?
            {
                continue;
            }
            if let Some(prior) = first {
                if !same_ends(prior.line, line, join) {
                    return Ok(Some([divider.source, prior.source, mark.source]));
                }
            } else {
                first = Some(mark);
            }
        }
    }
    Ok(None)
}

fn circular_return(shape: &Shape, hinge: V, radius: f64, ink: &mut Ink) -> Result<bool, String> {
    // Test the radius about the actual pivot. A straight or zigzag return is insufficient.
    let tol = radius * 0.002;
    Ok(match shape {
        Shape::Arc {
            center,
            radius: r,
            sweep,
            ..
        } => {
            V::from(center).minus(hinge).norm() <= tol
                && (radius - r).abs() <= tol
                && sweep.abs() <= std::f64::consts::FRAC_PI_2
        }
        Shape::Polyline { points, .. } => {
            ink.charge(points.len())?;
            let (first, last) = ends(shape).unwrap();
            let chord = last.minus(first);
            let length = chord.norm();
            length > tol
                && first.minus(hinge).dot(last.minus(hinge)) > 0.
                && points
                    .iter()
                    .all(|p| (V::from(p).minus(hinge).norm() - radius).abs() <= tol)
                && points
                    .iter()
                    .any(|p| chord.cross(V::from(p).minus(first)).abs() > length * tol)
        }
        _ => false,
    })
}
