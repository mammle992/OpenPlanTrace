//! A compact outlined enclosure can draw its title inside the apparent "wall".
//! Require both separately painted rims, interior lettering on two rows, and a
//! clear cavity. Background washes spanning the whole area have no local edge.
use super::*;

pub(super) struct Context {
    sources: Vec<usize>,
    index: Index,
}

impl Context {
    pub(super) fn new(page: &Page, ink: &mut Ink) -> Result<Self, String> {
        ink.charge(page.primitives.len())?;
        let mut sources = vec![];
        let mut bounds = vec![];
        for (source, p) in page.primitives.iter().enumerate() {
            let bbox = match &p.shape {
                Shape::Text { at, value, height }
                    if *height > 0.
                        && value.chars().filter(|c| c.is_alphabetic()).take(2).count() == 2 =>
                {
                    Box2::new(V::from(at), V::from(at))
                }
                Shape::Polyline {
                    closed: true,
                    filled: true,
                    points,
                } if points.len() >= 3 => {
                    ink.charge(points.len())?;
                    let mut b = Box2::new(V::from(&points[0]), V::from(&points[0]));
                    for p in &points[1..] {
                        b = b.union(Box2::new(V::from(p), V::from(p)));
                    }
                    b
                }
                _ => continue,
            };
            sources.push(source);
            bounds.push(bbox);
        }
        Ok(Self {
            sources,
            index: Index::build(&bounds),
        })
    }
    /// A fill confined to this body and spanning its interior is stronger
    /// evidence than equipment linework overlaid on it. A floor wash with
    /// no boundary at this footprint cannot establish wall material here.
    pub(super) fn spans_interior(
        &self,
        page: &Page,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        margin: f64,
    ) -> Result<bool, String> {
        let mut near = vec![];
        self.index
            .neighbors(axis.bbox().pad(width / 2. + margin), &mut near);
        ink.charge(near.len())?;
        let footprint = Box2(
            V(-margin, -width / 2. - margin),
            V(axis.len + margin, width / 2. + margin),
        );
        for i in near {
            let Shape::Polyline { points, .. } = &page.primitives[self.sources[i]].shape else {
                continue;
            };
            ink.charge(points.len())?;
            let local = |p: &Point| {
                let v = V::from(p);
                V(axis.offset(v), height(axis, v))
            };
            let mut boundary = false;
            let mut coverage = [false; 4];
            let (mut near_face, mut far_face) = (f64::INFINITY, f64::NEG_INFINITY);
            let corners = [
                V(axis.len / 4., -width / 4.),
                V(3. * axis.len / 4., -width / 4.),
                V(axis.len / 4., width / 4.),
                V(3. * axis.len / 4., width / 4.),
            ];
            for (a, b) in points.iter().zip(points.iter().cycle().skip(1)) {
                let (a, b) = (local(a), local(b));
                boundary |= clips(a, b, footprint);
                near_face = near_face.min(a.1);
                far_face = far_face.max(a.1);
                for (j, p) in corners.iter().enumerate() {
                    if (a.1 > p.1) != (b.1 > p.1)
                        && a.0 + (p.1 - a.1) * (b.0 - a.0) / (b.1 - a.1) > p.0
                    {
                        coverage[j] = !coverage[j];
                    }
                }
            }
            if boundary
                && near_face >= -width / 2. - margin
                && far_face <= width / 2. + margin
                && coverage.into_iter().all(|v| v)
            {
                return Ok(true);
            }
        }
        Ok(false)
    }
    pub(super) fn labeled(
        &self,
        page: &Page,
        ink: &mut Ink,
        axis: Segment,
        width: f64,
        opts: &Options,
    ) -> Result<Option<Vec<usize>>, String> {
        // Use the same 3:1 material aspect boundary as physical filled strips.
        if axis.len >= 3. * width {
            return Ok(None);
        }
        let half = width / 2.;
        let b = axis.bbox().pad(half + opts.snap_tolerance);
        let mut near = vec![];
        self.index.neighbors(b, &mut near);
        ink.charge(near.len())?;
        let mut text = vec![];
        let mut fills = vec![];
        for idx in near {
            let src = self.sources[idx];
            match &page.primitives[src].shape {
                Shape::Text {
                    at, height: size, ..
                } => {
                    let p = V::from(at);
                    let t = axis.offset(p);
                    let h = height(axis, p);
                    if t >= *size && t <= axis.len - size && h.abs() + size <= half {
                        text.push((p, *size, src));
                    }
                }
                Shape::Polyline { points, .. } => fills.push((src, points)),
                _ => {}
            }
        }
        let mut rows = None;
        'rows: for (i, &(a, sa, src_a)) in text.iter().enumerate() {
            for &(b, sb, src_b) in &text[..i] {
                ink.charge(1)?;
                let step = b.minus(a);
                let along = step.dot(axis.dir).abs();
                let across = step.cross(axis.dir).abs();
                // Two distinct similarly sized baselines, aligned along one
                // local axis. One string, duplicated paint and adjacent words
                // on a single page baseline are insufficient.
                if (b.1 - a.1).abs() >= sa.min(sb)
                    && sa.max(sb) <= 1.5 * sa.min(sb)
                    && along.min(across) <= 0.35 * sa.min(sb)
                    && (sa.min(sb)..=2. * sa.max(sb)).contains(&along.max(across))
                {
                    rows = Some([src_a, src_b]);
                    break 'rows;
                }
            }
        }
        let Some(rows) = rows else {
            return Ok(None);
        };
        let mut rims = [None; 2];
        let margin = opts.snap_tolerance;
        let footprint = Box2(
            V(-margin, -half - margin),
            V(axis.len + margin, half + margin),
        );
        let core = Box2(V(margin, -half / 2.), V(axis.len - margin, half / 2.));
        for (src, points) in fills {
            ink.charge(points.len())?;
            let local: Vec<V> = points
                .iter()
                .map(|q| {
                    let p = V::from(q);
                    V(axis.offset(p), height(axis, p))
                })
                .collect();
            let edges = local
                .iter()
                .copied()
                .zip(local.iter().copied().cycle().skip(1))
                .take(local.len());
            let mut bounds = Box2::new(local[0], local[0]);
            let mut twice_area = 0.;
            let mut local_boundary = false;
            let mut contains_center = false;
            for (a, b) in edges {
                bounds = bounds.union(Box2::new(a, b));
                twice_area += a.cross(b);
                // Any paint boundary through the interior makes this reading
                // ambiguous. A fill surrounding the entire core also vetoes
                // selection when its perimeter belongs to this footprint.
                if clips(a, b, core) {
                    return Ok(None);
                }
                local_boundary |= clips(a, b, footprint);
                if (a.1 > 0.) != (b.1 > 0.) && a.0 - a.1 * (b.0 - a.0) / (b.1 - a.1) > axis.len / 2.
                {
                    contains_center = !contains_center;
                }
            }
            if local_boundary && contains_center {
                return Ok(None);
            }
            let cover = bounds.1.0.min(axis.len) - bounds.0.0.max(0.);
            let depth = bounds.1.1 - bounds.0.1;
            let length = bounds.1.0 - bounds.0.0;
            // Both opposite sides need their own near-rectangular strip of
            // finite material covering most of this axis. Its occupied depth
            // leaves the central half of the presumed wall unpainted.
            if cover < 0.75 * axis.len
                || cover < 0.75 * length
                || depth <= EPS
                || depth > width / 4.
                || twice_area.abs() < 1.8 * length * depth
            {
                continue;
            }
            for (i, side) in [-half, half].into_iter().enumerate() {
                if (bounds.0.1 - side).abs().min((bounds.1.1 - side).abs()) <= margin
                    && ((bounds.0.1 + bounds.1.1) / 2.).signum() == side.signum()
                {
                    rims[i] = Some(src);
                }
            }
        }
        let [Some(a), Some(b)] = rims else {
            return Ok(None);
        };
        Ok(Some(vec![rows[0], rows[1], a, b]))
    }
}

fn clips(a: V, b: V, r: Box2) -> bool {
    let (mut low, mut high) = (0f64, 1f64);
    for (start, delta, min, max) in [
        (a.0, b.0 - a.0, r.0.0, r.1.0),
        (a.1, b.1 - a.1, r.0.1, r.1.1),
    ] {
        if delta.abs() <= EPS {
            if start < min || start > max {
                return false;
            }
        } else {
            let near = (min - start) / delta;
            let far = (max - start) / delta;
            low = low.max(near.min(far));
            high = high.min(near.max(far));
            if low > high {
                return false;
            }
        }
    }
    true
}
