//! Select exactly measured raster material between a source cap and a distinct return.
use super::isolated_course::{occupied, overlap, rect};
use crate::{
    input::raster::{
        PdfInput, PdfRoute, RasterBand, RasterCourseReceipt, VerifiedRasterCourse, verified_course,
    },
    model::{PageScan, Point, Wall},
    placement::{PlacementExtent, WallDecision, WallReason, WallStatus},
};
use std::collections::{BTreeSet, HashSet};

const EPS: f64 = 1e-6;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Copy)]
struct Slice {
    along: [f64; 2],
    cross: [f64; 2],
}

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    sx: f64,
    sy: f64,
}

impl Frame {
    fn new(band: &RasterBand, page: &PageScan, input: &PdfInput) -> Self {
        Self {
            vertical: band.start.x == band.end.x,
            sx: page.width / input.render_width as f64,
            sy: page.height / input.render_height as f64,
        }
    }

    fn source(self, [x0, y0, x1, y1]: [f64; 4]) -> Slice {
        if self.vertical {
            Slice {
                along: [y0, y1],
                cross: [x0, x1],
            }
        } else {
            Slice {
                along: [x0, x1],
                cross: [y0, y1],
            }
        }
    }

    fn wall(self, w: &Wall) -> Option<Slice> {
        let [a, b] = [w.start, w.end];
        let (a, b, normal, half) = if self.vertical && (a.x - b.x).abs() <= EPS {
            (
                a.y / self.sy,
                b.y / self.sy,
                a.x / self.sx,
                w.thickness / (2. * self.sx),
            )
        } else if !self.vertical && (a.y - b.y).abs() <= EPS {
            (
                a.x / self.sx,
                b.x / self.sx,
                a.y / self.sy,
                w.thickness / (2. * self.sy),
            )
        } else {
            return None;
        };
        (w.curve.is_none()
            && [a, b, normal, half].iter().all(|v| v.is_finite())
            && a != b
            && half > 0.)
            .then_some(Slice {
                along: [a.min(b), a.max(b)],
                cross: [normal - half, normal + half],
            })
    }

    fn body(self, s: Slice, old: &Wall, thickness: f64) -> (Point, Point, f64) {
        let center = s.cross[0].midpoint(s.cross[1]);
        let world = |t| {
            if self.vertical {
                Point::new(center * self.sx, t * self.sy)
            } else {
                Point::new(t * self.sx, center * self.sy)
            }
        };
        let [lo, hi] = s.along.map(world);
        let reversed = if self.vertical {
            old.start.y > old.end.y
        } else {
            old.start.x > old.end.x
        };
        if reversed {
            (hi, lo, thickness)
        } else {
            (lo, hi, thickness)
        }
    }
}

#[derive(Clone)]
struct Candidate {
    owner: usize,
    section: Slice,
    body: [Point; 4],
    extent: Option<PlacementExtent>,
}

struct Course {
    walls: [Candidate; 3],
    support: Vec<String>,
}

fn cap_trim(target: Slice, observed: Slice, cap_side: Option<usize>) -> Option<Slice> {
    if observed.cross[0] > target.cross[0] + EPS || observed.cross[1] < target.cross[1] - EPS {
        return None;
    }
    let along = [
        observed.along[0].max(target.along[0]),
        observed.along[1].min(target.along[1]),
    ];
    let trimmed = [along[0] - target.along[0], target.along[1] - along[1]];
    if along[1] - along[0] <= EPS
        || trimmed.iter().enumerate().any(|(i, t)| {
            let allow = if Some(i) == cap_side {
                (0.10 * (target.cross[1] - target.cross[0])).min(4.)
            } else {
                0.
            };
            *t > allow + EPS
        })
    {
        return None;
    }
    Some(Slice { along, ..target })
}

fn select_piece(
    page: &PageScan,
    decisions: &[WallDecision],
    frame: Frame,
    target: Slice,
    cap_side: Option<usize>,
    band: &RasterBand,
) -> Option<Candidate> {
    let mut matches = Vec::new();
    for (i, (w, d)) in page.walls.iter().zip(decisions).enumerate() {
        if !super::is_raster(w)
            || d.status != WallStatus::Review
            || d.reason != WallReason::RasterFacesNeedReview
            || d.placement_extent.is_some()
            || !d.opening_ids.is_empty()
            || d.wall_id != w.id
        {
            continue;
        }
        let known_faces = w
            .source_ids
            .iter()
            .filter(|id| band.face_source_ids.contains(id))
            .count();
        if known_faces == 0 {
            continue;
        }
        let Some(observed) = frame.wall(w) else {
            continue;
        };
        let Some(section) = cap_trim(target, observed, cap_side) else {
            continue;
        };
        let (start, end, thickness) = frame.body(section, w, band.thickness);
        let Some(body) = rect(start, end, thickness) else {
            continue;
        };
        let exact = known_faces == 2
            && (w.start - start).norm() <= EPS
            && (w.end - end).norm() <= EPS
            && (w.thickness - thickness).abs() <= EPS;
        let extent = if exact {
            None
        } else {
            let mut source_ids = band.face_source_ids.to_vec();
            source_ids.sort_unstable();
            Some(PlacementExtent {
                start,
                end,
                thickness: Some(thickness),
                source_ids,
            })
        };
        let width_extra =
            (observed.cross[1] - observed.cross[0]) - (target.cross[1] - target.cross[0]);
        let length = section.along[1] - section.along[0];
        matches.push((
            known_faces,
            width_extra,
            length,
            Candidate {
                owner: i,
                section,
                body,
                extent,
            },
        ));
    }
    matches.sort_unstable_by(|a, b| {
        b.0.cmp(&a.0)
            .then(a.1.total_cmp(&b.1))
            .then(b.2.total_cmp(&a.2))
    });
    let best = matches.first()?;
    if matches.get(1).is_some_and(|next| {
        best.0 == next.0 && (best.1 - next.1).abs() <= EPS && (best.2 - next.2).abs() <= EPS
    }) {
        return None;
    }
    Some(best.3.clone())
}

fn bounds_rect(points: impl IntoIterator<Item = Point>) -> Option<[Point; 4]> {
    let mut lo = Point::new(f64::INFINITY, f64::INFINITY);
    let mut hi = Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY);
    for p in points {
        if !p.finite() {
            return None;
        }
        lo = Point::new(lo.x.min(p.x), lo.y.min(p.y));
        hi = Point::new(hi.x.max(p.x), hi.y.max(p.y));
    }
    (lo.x < hi.x && lo.y < hi.y).then(|| [lo, Point::new(hi.x, lo.y), hi, Point::new(lo.x, hi.y)])
}

fn blocked(page: &PageScan, decisions: &[WallDecision]) -> Vec<[Point; 4]> {
    let mut all = occupied(page, decisions);
    for object in &page.objects {
        if let Some(p) = &object.footprint {
            all.extend(bounds_rect(p.polygon.iter().copied()));
        }
        if let Some(p) = &object.geometry {
            all.extend(bounds_rect(p.polygon.iter().copied()));
        }
    }
    for held in page.withheld_walls.iter().flatten() {
        if let Some(points) = &held.furniture_outline {
            all.extend(bounds_rect(points.iter().copied()));
        }
    }
    all
}

fn course(
    page: &PageScan,
    decisions: &[WallDecision],
    input: &PdfInput,
    receipt: &RasterCourseReceipt,
    geometry: VerifiedRasterCourse,
    blocked: &[[Point; 4]],
) -> Option<Course> {
    let frame = Frame::new(&input.bands[receipt.band_indices[0]], page, input);
    let cap = if geometry.cap_at_low_end {
        (0, 0)
    } else {
        (2, 1)
    };
    let mut pieces = Vec::with_capacity(3);
    let mut occupied = HashSet::new();
    for (i, id) in receipt.band_indices.into_iter().enumerate() {
        let band = &input.bands[id];
        if !band.face_source_ids.contains(&geometry.common_face) {
            return None;
        }
        let candidate = select_piece(
            page,
            decisions,
            frame,
            frame.source(geometry.sections[i]),
            (cap.0 == i).then_some(cap.1),
            band,
        )?;
        if !occupied.insert(candidate.owner)
            || blocked.iter().any(|&v| overlap(candidate.body, v, EPS))
        {
            return None;
        }
        pieces.push(candidate);
    }
    if pieces.windows(2).any(|adjacent| {
        let [l, r] = [adjacent[0].section, adjacent[1].section];
        (l.along[1] - r.along[0]).abs() > EPS
            || l.cross[1].min(r.cross[1]) - l.cross[0].max(r.cross[0]) <= EPS
    }) {
        return None;
    }
    let support = receipt
        .band_indices
        .into_iter()
        .chain([receipt.return_band_index])
        .flat_map(|i| input.bands[i].face_source_ids.iter().cloned())
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect();
    Some(Course {
        walls: pieces.try_into().ok()?,
        support,
    })
}

pub(crate) fn select(page: &PageScan, decisions: &mut [WallDecision]) -> Result<(), String> {
    let Some(input) = page.pdf_input.as_ref() else {
        return Ok(());
    };
    if input.route != PdfRoute::Raster || input.raster_course_receipts.is_empty() {
        return Ok(());
    }
    if decisions.len() != page.walls.len() {
        return Err("invalid raster course wall inventory".into());
    }
    let blocked = blocked(page, decisions);
    let size = page
        .walls
        .len()
        .saturating_add(blocked.len())
        .saturating_mul(3);
    let mut work = MAX_WORK;
    let mut courses = Vec::new();
    for receipt in &input.raster_course_receipts {
        let Some(remaining) = work.checked_sub(size) else {
            return Ok(());
        };
        work = remaining;
        let geometry = verified_course(input, page.width, page.height, receipt)
            .ok_or("invalid measured raster course receipt")?;
        if let Some(option) = course(page, decisions, input, receipt, geometry, &blocked) {
            courses.push(option);
        }
    }
    let mut ambiguous = vec![false; courses.len()];
    for (i, a) in courses.iter().enumerate() {
        for (j, b) in courses.iter().enumerate().skip(i + 1) {
            if a.walls.iter().any(|x| {
                b.walls
                    .iter()
                    .any(|y| x.owner == y.owner || overlap(x.body, y.body, EPS))
            }) {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    for (p, rejected) in courses.into_iter().zip(ambiguous) {
        if rejected {
            continue;
        }
        let heads = p.walls.clone().map(|c| c.owner);
        for (i, c) in p.walls.into_iter().enumerate() {
            let d = &mut decisions[c.owner];
            d.status = WallStatus::Ready;
            d.reason = WallReason::SourceBoundedRasterCourse;
            d.support_source_ids.clone_from(&p.support);
            d.support_wall_ids = heads
                .iter()
                .enumerate()
                .filter(|(j, _)| i.abs_diff(*j) == 1)
                .map(|(_, &j)| page.walls[j].id.clone())
                .collect();
            d.placement_extent = c.extent;
        }
    }
    Ok(())
}
