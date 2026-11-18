//! Page-space lineweights are usable only while the entire stroked primitive stays visible.
use super::{
    Dictionary, Graphics, Object, Pdf, Point, Primitive, Rect, Shape, Subpath, numbers, resource,
    visibility::VisibleFills,
};

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub(super) enum Device {
    Gray,
    Rgb,
    Cmyk,
}

impl Device {
    pub(super) fn native(doc: &Pdf, resources: Option<&Dictionary>, name: &[u8]) -> Option<Self> {
        let (space, substitution): (Self, &[u8]) = match name {
            b"DeviceGray" => (Self::Gray, b"DefaultGray"),
            b"DeviceRGB" => (Self::Rgb, b"DefaultRGB"),
            b"DeviceCMYK" => (Self::Cmyk, b"DefaultCMYK"),
            _ => return None,
        };
        resource(doc, resources, b"ColorSpace", substitution)
            .is_none()
            .then_some(space)
    }
    pub(super) fn exact_black(self, components: &[Object]) -> bool {
        match self {
            Self::Gray => numbers(components) == Some([0.]),
            Self::Rgb => numbers(components) == Some([0., 0., 0.]),
            Self::Cmyk => numbers(components) == Some([0., 0., 0., 1.]),
        }
    }
    pub(super) fn name(self) -> &'static [u8] {
        match self {
            Self::Gray => b"DeviceGray",
            Self::Rgb => b"DeviceRGB",
            Self::Cmyk => b"DeviceCMYK",
        }
    }
}

pub(super) struct StrokeWidths {
    pub(super) footprints: VisibleFills<(usize, Option<Device>)>,
    pending: Vec<(usize, f64, usize)>,
}

impl StrokeWidths {
    pub(super) fn new(page: Rect) -> Self {
        Self {
            footprints: VisibleFills::new(page),
            pending: vec![],
        }
    }

    pub(super) fn observe(&mut self, idx: usize, shape: &Shape, sub: &Subpath, g: &Graphics) {
        if self.footprints.exhausted()
            || !g.pen_state_safe
            || !g.undashed_pen
            || !g.stroke
            || g.stroke_alpha != 1.
            || g.pen_width <= 0.
            || !g.pen_width.is_finite()
        {
            return;
        }
        let id = (idx, g.stroke_device.filter(|_| g.black_pen));
        let uniform = g.ctm.similarity().map(|scale| scale * g.pen_width);
        let [a, b, c, d, _, _] = g.ctm.0;
        let scale = |v: Point| (a * v.x + b * v.y).hypot(c * v.x + d * v.y);
        // An arc or flattened Bezier has a unique width only under a similarity.
        // Bounding the true control points avoids certifying a masked curve between samples.
        if sub.curves != 0 {
            if let (Some(width), Some(r)) = (uniform, sub.bounds()) {
                let reach = width / 2. * g.miter_limit.max(2_f64.sqrt());
                let corners = [
                    Point::new(r.l - reach, r.t - reach),
                    Point::new(r.r + reach, r.t - reach),
                    Point::new(r.r + reach, r.b + reach),
                    Point::new(r.l - reach, r.b + reach),
                ];
                if width.is_finite() && corners.iter().all(|p| g.inside(*p)) {
                    self.footprints.add(id, corners);
                    self.pending.push((idx, width, 1));
                }
            }
            return;
        }
        let (points, closed) = match shape {
            Shape::Line { start, end } => (&[*start, *end][..], false),
            Shape::Polyline {
                points,
                closed,
                filled: false,
            } => (points.as_slice(), *closed),
            _ => return,
        };
        let mut width: Option<f64> = None;
        let mut count = 0usize;
        for (&p, &q) in points
            .iter()
            .zip(points.iter().cycle().skip(1))
            .take(if closed {
                points.len()
            } else {
                points.len().saturating_sub(1)
            })
        {
            if p == q {
                continue;
            }
            let u = (q - p).unit();
            let n = Point::new(-u.y, u.x);
            let w = uniform.unwrap_or_else(|| g.pen_width * scale(n));
            if !w.is_finite() || w <= 0. || width.is_some_and(|v| (v - w).abs() > 1e-9 * w) {
                return;
            }
            width = Some(w);
            // Under a shear the transformed butt end is oblique. Project its
            // pen radius along the centerline as a conservative bound.
            let half = w * 0.5;
            let tip = if uniform.is_some() && !g.projecting_cap {
                0.
            } else {
                g.pen_width * scale(u) * 0.5 * 2_f64.sqrt()
            };
            let corners = [
                p - u * tip + n * half,
                q + u * tip + n * half,
                q + u * tip - n * half,
                p - u * tip - n * half,
            ];
            if !corners.iter().all(|p| g.inside(*p)) {
                return;
            }
            self.footprints.add(id, corners);
            count += 1;
        }
        if let Some(width) = width {
            self.pending.push((idx, width, count));
        }
    }

    pub(super) fn finish(self, primitives: &mut [Primitive]) {
        let ids = self.footprints.ids();
        let mut cursor = 0;
        for (idx, width, count) in self.pending {
            while cursor < ids.len() && ids[cursor].0 < idx {
                cursor += 1
            }
            let first = cursor;
            while cursor < ids.len() && ids[cursor].0 == idx {
                cursor += 1
            }
            if cursor - first == count {
                primitives[idx].stroke_width = Some(width);
            }
        }
    }
}
