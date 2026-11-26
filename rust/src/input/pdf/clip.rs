//! Finite straight PDF clipping rings. Keep the polygon's actual visible intervals.
use super::{Point, Rect};

#[derive(Debug)]
pub(super) struct Ring {
    points: Vec<Point>,
    bounds: Rect,
    eps: f64,
}

fn pairs(points: &[Point]) -> impl Iterator<Item = (Point, Point)> + '_ {
    points
        .iter()
        .copied()
        .zip(points.iter().copied().cycle().skip(1))
}

impl Ring {
    pub(super) fn simple(points: &[Point], bounds: Rect) -> Option<Self> {
        let magnitude = [bounds.l, bounds.r, bounds.t, bounds.b]
            .into_iter()
            .map(f64::abs)
            .fold(1., f64::max);
        let eps = (64. * f64::EPSILON * magnitude).max(1e-9);
        let points = if points.first()?.distance(*points.last()?) <= eps {
            &points[..points.len() - 1]
        } else {
            points
        };
        if points.len() < 3 {
            return None;
        }
        for (i, (a, b)) in pairs(points).enumerate() {
            let c = points[(i + 2) % points.len()];
            let (u, v) = (b - a, c - b);
            if u.norm() <= eps || (side(a, b, c, eps) == 0 && u.dot(v) < 0.) {
                return None;
            }
            for (j, (c, d)) in pairs(points).enumerate().skip(i + 2) {
                if (j + 1) % points.len() != i && meets(a, b, c, d, eps) {
                    return None;
                }
            }
        }
        Some(Self {
            points: points.to_vec(),
            bounds,
            eps,
        })
    }

    pub(super) fn len(&self) -> usize {
        self.points.len()
    }

    pub(super) fn contains(&self, p: Point) -> bool {
        self.bounds.inside(p) && contains(&self.points, p, self.eps)
    }

    pub(super) fn overlaps_footprint(&self, points: &[Point]) -> bool {
        Rect::points(points)
            .and_then(|r| self.bounds.intersect(r))
            .is_some()
            && (points.iter().any(|&p| self.contains(p))
                || self.points.iter().any(|&p| contains(points, p, self.eps))
                || pairs(points)
                    .any(|(a, b)| pairs(&self.points).any(|(c, d)| meets(a, b, c, d, self.eps))))
    }

    pub(super) fn covers_segment(&self, a: Point, b: Point) -> bool {
        if a.distance(b) <= self.eps {
            return self.contains(a) && self.contains(b);
        }
        // Checking only both endpoints can bridge a notch. Check each portion
        // between boundary events, including the two ends of the segment.
        self.contains(a) && self.contains(b) && self.intervals(a, b).as_slice() == [(0., 1.)]
    }

    pub(super) fn intervals(&self, a: Point, b: Point) -> Vec<(f64, f64)> {
        let d = b - a;
        let d2 = d.dot(d);
        if d2 <= self.eps * self.eps {
            return if self.contains(a) && self.contains(b) {
                vec![(0., 1.)]
            } else {
                vec![]
            };
        }
        let len = d2.sqrt();
        let mut events = vec![0., 1.];
        for (p, q) in pairs(&self.points) {
            let e = q - p;
            let cross = d.cross(e);
            if cross.abs() <= 32. * f64::EPSILON * len * e.norm() {
                if (p - a).cross(d).abs() <= self.eps * len {
                    events.extend([p, q].map(|r| ((r - a).dot(d) / d2).clamp(0., 1.)));
                }
            } else {
                let t = (p - a).cross(e) / cross;
                let u = (p - a).cross(d) / cross;
                let at_vertex = self.eps / e.norm();
                if (0. ..1.).contains(&t) && (-at_vertex..=1. + at_vertex).contains(&u) {
                    events.push(t);
                }
            }
        }
        events.sort_unstable_by(f64::total_cmp);
        events.dedup();
        let mut inside: Vec<(f64, f64)> = Vec::new();
        for span in events.windows(2) {
            let (lo, hi) = (span[0], span[1]);
            if self.contains(a + d * ((lo + hi) * 0.5)) {
                if let Some((_, end)) = inside.last_mut()
                    && *end == lo
                {
                    *end = hi;
                } else {
                    inside.push((lo, hi));
                }
            }
        }
        inside
    }
}

fn contains(points: &[Point], p: Point, eps: f64) -> bool {
    let mut inside = false;
    for (a, b) in pairs(points) {
        if on_edge(a, b, p, eps) {
            return true;
        }
        if (a.y > p.y) != (b.y > p.y) && p.x < a.x + (b.x - a.x) * (p.y - a.y) / (b.y - a.y) {
            inside = !inside;
        }
    }
    inside
}

fn side(a: Point, b: Point, p: Point, eps: f64) -> i8 {
    let delta = b - a;
    let cross = delta.cross(p - a);
    let tolerance = eps * delta.norm();
    if cross > tolerance {
        1
    } else if cross < -tolerance {
        -1
    } else {
        0
    }
}

fn on_edge(a: Point, b: Point, p: Point, eps: f64) -> bool {
    p.x >= a.x.min(b.x) - eps
        && p.x <= a.x.max(b.x) + eps
        && p.y >= a.y.min(b.y) - eps
        && p.y <= a.y.max(b.y) + eps
        && side(a, b, p, eps) == 0
}

fn meets(a: Point, b: Point, c: Point, d: Point, eps: f64) -> bool {
    (side(a, b, c, eps) * side(a, b, d, eps) < 0 && side(c, d, a, eps) * side(c, d, b, eps) < 0)
        || on_edge(a, b, c, eps)
        || on_edge(a, b, d, eps)
        || on_edge(c, d, a, eps)
        || on_edge(c, d, b, eps)
}
