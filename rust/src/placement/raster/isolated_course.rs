//! Finite raster wall courses with measured solid, hatch and end evidence.
use crate::input::raster::{PdfRoute, RasterBand, dense_material_tone};
use crate::model::{PageScan, Point, Primitive, Shape, Wall};
use crate::placement::{WallDecision, WallReason, WallStatus, effective_wall};
use std::collections::{BTreeMap, HashMap, HashSet};

const MAX_WORK: usize = 6_000_000;

struct Course {
    pieces: Vec<usize>,
    source_ids: Vec<String>,
}

struct Frame {
    origin: Point,
    axis: Point,
    length: f64,
    half: f64,
    pixel: f64,
    eps: f64,
}

impl Frame {
    fn new(b: &RasterBand, pixel: f64) -> Self {
        let length = b.start.distance(b.end);
        Self {
            origin: b.start,
            axis: (b.end - b.start).unit(),
            length,
            half: 0.5 * b.thickness,
            pixel,
            eps: 128. * f64::EPSILON * b.start.norm().max(b.end.norm()).max(1.),
        }
    }

    fn project(&self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(d.dot(self.axis), self.axis.cross(d))
    }

    fn parallel(&self, a: Point, b: Point) -> Option<([f64; 2], f64)> {
        let [a, b] = [a, b].map(|p| self.project(p));
        ((a.y - b.y).abs() <= self.eps && (a.x - b.x).abs() > self.eps)
            .then_some(([a.x.min(b.x), a.x.max(b.x)], 0.5 * (a.y + b.y)))
    }

    fn finite_span(&self, a: Point, b: Point) -> Option<[f64; 2]> {
        let ([lo, hi], lane) = self.parallel(a, b)?;
        (lo >= -self.eps && hi <= self.length + self.eps && lane.abs() <= 2. * self.pixel)
            .then_some([lo, hi])
    }

    fn face_pair(&self, a: &Primitive, b: &Primitive) -> bool {
        let parallel = |p: &Primitive| {
            let Shape::Line { start, end } = p.shape else {
                return None;
            };
            let ([x, y], across) = self.parallel(start, end)?;
            (x <= self.eps && y >= self.length - self.eps).then_some(across)
        };
        let (Some(x), Some(y)) = (parallel(a), parallel(b)) else {
            return false;
        };
        (x.min(y) + self.half).abs() <= self.eps && (x.max(y) - self.half).abs() <= self.eps
    }

    fn caps<'a>(&self, lines: &[&'a Primitive]) -> Option<[&'a str; 2]> {
        let mut caps = [None, None];
        for line in lines {
            let Shape::Line { start, end } = line.shape else {
                continue;
            };
            let [a, b] = [self.project(start), self.project(end)];
            if (a.x - b.x).abs() > self.eps {
                continue;
            }
            let [lo, hi] = [a.y.min(b.y), a.y.max(b.y)];
            if lo > -self.half + self.eps || hi < self.half - self.eps || hi - lo > 6. * self.half {
                continue;
            }
            let overrun = (-self.half - lo).max(hi - self.half);
            for (index, end) in [0., self.length].into_iter().enumerate() {
                let distance = (a.x - end).abs();
                if distance > 2. * self.pixel {
                    continue;
                }
                let key = (distance, line.id.as_str(), overrun);
                if caps[index].is_none_or(|(d, id, _)| (distance, line.id.as_str()) < (d, id)) {
                    caps[index] = Some(key);
                }
            }
        }
        let [a, b] = [caps[0]?, caps[1]?];
        (a.1 != b.1 && a.2.max(b.2) >= 0.5 * self.half).then_some([a.1, b.1])
    }
}

fn dense(b: &RasterBand) -> bool {
    b.hatch.is_none()
        && b.width_pixels >= 6.
        && b.start.distance(b.end) >= 6. * b.thickness
        && b.boundary_coverage.into_iter().all(|v| v >= 0.98)
        && dense_material_tone(b.dark_fraction, b.mean_luma)
}

fn strong_hatch(b: &RasterBand) -> bool {
    b.boundary_coverage.into_iter().all(|v| v >= 0.98)
        && b.hatch.as_ref().is_some_and(|h| {
            h.crossing_count >= 8
                && h.pitch_cv <= 0.20
                && h.diagonal_support >= 0.65
                && h.stationary_correlation <= -0.5
                && h.longitudinal_coverage >= 0.9
                && h.contrast >= 60.
        })
}

pub(super) fn rect(a: Point, b: Point, width: f64) -> Option<[Point; 4]> {
    if a == b || width <= 0. || !width.is_finite() {
        return None;
    }
    let u = (b - a).unit();
    let n = Point::new(-u.y, u.x) * (width * 0.5);
    Some([a + n, b + n, b - n, a - n])
}

pub(super) fn overlap(a: [Point; 4], b: [Point; 4], eps: f64) -> bool {
    [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]]
        .into_iter()
        .all(|v| {
            let axis = v.unit();
            let span = |p: [Point; 4]| {
                p.into_iter()
                    .map(|p| (p - a[0]).dot(axis))
                    .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], u| {
                        [lo.min(u), hi.max(u)]
                    })
            };
            let [lo, hi] = span(a);
            let [x, y] = span(b);
            hi.min(y) - lo.max(x) > eps
        })
}

pub(super) fn occupied(page: &PageScan, decisions: &[WallDecision]) -> Vec<[Point; 4]> {
    let widths: HashMap<_, _> = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness))
        .collect();
    let mut occupied = Vec::new();
    for (w, d) in page
        .walls
        .iter()
        .zip(decisions)
        .filter(|(_, d)| d.status == WallStatus::Ready)
    {
        let w = effective_wall(w, Some(d));
        if let Some(curve) = w.curve {
            let reach = curve.radius + w.thickness * 0.5;
            let v = Point::new(reach, 0.);
            occupied.extend(rect(curve.center - v, curve.center + v, 2. * reach));
        } else {
            occupied.extend(rect(w.start, w.end, w.thickness));
        }
    }
    for o in page.opening_evidence() {
        let width = o
            .host_wall_ids
            .iter()
            .filter_map(|k| widths.get(k.as_str()).copied())
            .fold(1e-5_f64, f64::max);
        occupied.extend(rect(o.start, o.end, width));
        occupied.extend(
            o.swing_observations
                .iter()
                .filter_map(|s| rect(s.hinge, s.closed_tip, 2. * s.leaf_tolerance)),
        );
    }
    occupied
}

fn same_pair(ids: &[String], a: &str, b: &str) -> bool {
    matches!(ids,[x,y] if (x==a && y==b)||(x==b && y==a))
}

fn witnessed_piece(frame: &Frame, w: &Wall, b: &RasterBand) -> bool {
    if (w.thickness - b.thickness).abs() > frame.eps {
        return false;
    }
    let (Some(([a, z], x)), Some(([l, h], y))) = (
        frame.parallel(w.start, w.end),
        frame.parallel(b.start, b.end),
    ) else {
        return false;
    };
    (x - y).abs() <= frame.eps && a >= l - frame.eps && z <= h + frame.eps
}

fn prove(page: &PageScan, decisions: &[WallDecision], work: &mut usize) -> Option<Vec<Course>> {
    let input = page.pdf_input.as_ref()?;
    if input.route != PdfRoute::Raster
        || input.analysis_revision != 1
        || input.render_width == 0
        || input.render_height == 0
    {
        return Some(vec![]);
    }
    let pixel =
        (page.width / input.render_width as f64).max(page.height / input.render_height as f64);
    let author: HashSet<_> = input.raster_source_ids.iter().map(String::as_str).collect();
    *work = work.checked_sub(page.primitives.len().saturating_add(input.bands.len()))?;
    let native: HashMap<_, _> = page
        .primitives
        .iter()
        .filter(|p| author.contains(p.id.as_str()))
        .map(|p| (p.id.as_str(), p))
        .collect();
    let lines = native.values().copied().collect::<Vec<_>>();
    let mut paired = BTreeMap::<(&str, &str), Vec<&RasterBand>>::new();
    for b in &input.bands {
        let [a, z] = b.face_source_ids.each_ref().map(String::as_str);
        paired.entry((a.min(z), a.max(z))).or_default().push(b);
    }
    let blocked = occupied(page, decisions);
    let mut out = Vec::new();
    for ((a, z), bands) in paired {
        let mut whole = bands.iter().copied().filter(|b| dense(b));
        let Some(body) = whole.next() else { continue };
        if whole.next().is_some() {
            continue;
        }
        let frame = Frame::new(body, pixel);
        let (Some(left), Some(right)) = (native.get(a), native.get(z)) else {
            continue;
        };
        if !frame.face_pair(left, right) {
            continue;
        }
        *work = work.checked_sub(bands.len())?;
        let mut hatches = bands
            .iter()
            .copied()
            .filter(|b| strong_hatch(b))
            .filter_map(|b| {
                let extent = frame.finite_span(b.start, b.end)?;
                let h = b.hatch.as_ref()?;
                (b.thickness >= body.thickness - frame.eps
                    && b.thickness <= body.thickness + 4. * pixel)
                    .then_some((extent, h.mean_pitch_pixels, b))
            })
            .collect::<Vec<_>>();
        if hatches.len() < 3 {
            continue;
        }
        *work = work.checked_sub(hatches.len().saturating_pow(2))?;
        hatches.sort_unstable_by(|x, y| x.0[0].total_cmp(&y.0[0]));
        let pitch = hatches[0].1;
        if hatches
            .iter()
            .any(|(_, p, _)| (*p - pitch).abs() > pitch * 0.15)
            || hatches
                .windows(2)
                .any(|w| w[0].0[1] > w[1].0[0] + frame.eps)
            || hatches.iter().map(|(r, _, _)| r[1] - r[0]).sum::<f64>() < 0.60 * frame.length
        {
            continue;
        }
        *work = work.checked_sub(
            page.walls
                .len()
                .saturating_mul(1 + hatches.len() + blocked.len())
                .saturating_add(lines.len()),
        )?;
        let Some(caps) = frame.caps(&lines) else {
            continue;
        };
        if caps.iter().any(|&p| p == a || p == z) {
            continue;
        }
        let mut pieces = Vec::new();
        let mut reject = false;
        let mut witnesses = HashSet::new();
        for (i, (w, d)) in page.walls.iter().zip(decisions).enumerate() {
            if !same_pair(&w.source_ids, a, z) {
                continue;
            }
            let Some(span) = frame.finite_span(w.start, w.end) else {
                continue;
            };
            if d.status != WallStatus::Review
                || d.reason != WallReason::RasterFacesNeedReview
                || d.placement_extent.is_some()
                || !d.opening_ids.is_empty()
                || w.curve.is_some()
            {
                reject = true;
                break;
            }
            let hatch = hatches
                .iter()
                .position(|(_, _, b)| witnessed_piece(&frame, w, b));
            if let Some(hatch) = hatch {
                witnesses.insert(hatch);
            }
            if hatch.is_none() && !witnessed_piece(&frame, w, body) {
                reject = true;
                break;
            }
            let Some(p) = rect(w.start, w.end, w.thickness) else {
                reject = true;
                break;
            };
            if blocked.iter().any(|&b| overlap(p, b, frame.eps)) {
                reject = true;
                break;
            }
            pieces.push((span, i, p));
        }
        if reject || pieces.len() < 3 || witnesses.len() != hatches.len() {
            continue;
        }
        *work = work.checked_sub(pieces.len().saturating_pow(2))?;
        pieces.sort_unstable_by(|a, b| a.0[0].total_cmp(&b.0[0]).then(a.1.cmp(&b.1)));
        let first = &decisions[pieces[0].1];
        if first.component_wall_count != pieces.len()
            || pieces
                .iter()
                .any(|(_, i, _)| decisions[*i].component_id != first.component_id)
            || pieces[0].0[0].abs() > frame.eps
            || (pieces.last()?.0[1] - frame.length).abs() > frame.eps
            || pieces
                .windows(2)
                .any(|q| (q[0].0[1] - q[1].0[0]).abs() > frame.eps)
        {
            continue;
        }
        let proof = Course {
            pieces: pieces.iter().map(|(_, i, _)| *i).collect(),
            source_ids: [a, z, caps[0], caps[1]]
                .into_iter()
                .map(str::to_owned)
                .collect(),
        };
        out.push((
            proof,
            pieces.into_iter().map(|(_, _, p)| p).collect::<Vec<_>>(),
        ));
    }
    let count = out.iter().map(|(_, poses)| poses.len()).sum::<usize>();
    *work = work.checked_sub(count.saturating_pow(2))?;
    let mut unique = vec![true; out.len()];
    for (i, (_, a)) in out.iter().enumerate() {
        for (j, (_, b)) in out.iter().enumerate().skip(i + 1) {
            if a.iter().any(|&p| b.iter().any(|&q| overlap(p, q, 1e-7))) {
                unique[i] = false;
                unique[j] = false;
            }
        }
    }
    Some(
        out.into_iter()
            .zip(unique)
            .filter_map(|((p, _), ok)| ok.then_some(p))
            .collect(),
    )
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let mut work = MAX_WORK;
    let Some(courses) = prove(page, decisions, &mut work) else {
        return;
    };
    for mut course in courses {
        course.source_ids.sort_unstable();
        for (position, &index) in course.pieces.iter().enumerate() {
            let decision = &mut decisions[index];
            decision.status = WallStatus::Ready;
            decision.reason = WallReason::SourceBoundedRasterIsolatedCourse;
            decision.support_source_ids.clone_from(&course.source_ids);
            let from = position.saturating_sub(1);
            let to = (position + 2).min(course.pieces.len());
            decision.support_wall_ids = course.pieces[from..to]
                .iter()
                .filter(|&&neighbor| neighbor != index)
                .map(|&neighbor| page.walls[neighbor].id.clone())
                .collect();
        }
    }
}
