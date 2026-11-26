//! Reuse exact measured course sections and preserve the short unfilled seam.
use super::super::{
    face_continuation::{VerifiedRasterFaceContinuation, verify_continuation_inventory},
    hinged_doors::Samples,
    residual_hatch::Pixels,
};
use super::{
    BandFootprint, RasterBand, THRESHOLD, bounds, hatch, hatch_quality, native, overlaps,
    protected, section::same_overlap, verified_section_courses,
};
use crate::{
    input::raster::{
        PdfInput, PdfRoute, RasterAlignedCourseReceipt, RasterCoursePatch,
        RasterFaceContinuationReceipt, RasterHingedDoorReceipt, RasterSectionCourseReceipt,
    },
    model::{Point, Primitive},
};

mod corner_course;
pub(in crate::input::raster::extract) use corner_course::collect_corner_courses;
pub(crate) use corner_course::{VerifiedRasterCornerCourse, verified_corner_courses};

const MAX_RECEIPTS: usize = 16;
const MAX_PATCH: usize = 4_096;
const MAX_LENGTH: i32 = 128;
const MAX_PIECES: usize = 12;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Copy)]
struct Span {
    vertical: bool,
    along: [i32; 2],
    cross: [i32; 2],
}

impl Span {
    fn from_band(band: &RasterBand, scale: [f64; 2]) -> Option<Self> {
        let b = BandFootprint::read(band, scale)?;
        let [a, z] = b.along.map(native);
        let [c, d] = b.cross.map(native);
        Some(Self {
            vertical: b.vertical,
            along: [a?, z?],
            cross: [c?, d?],
        })
    }

    fn from_continuation(p: &VerifiedRasterFaceContinuation, scale: [f64; 2]) -> Option<Self> {
        let a = [p.start.x, p.start.y];
        let b = [p.end.x, p.end.y];
        let vertical = a[0] == b[0];
        let i = usize::from(vertical);
        if a[1 - i] != b[1 - i] {
            return None;
        }
        let mid = a[1 - i] / scale[1 - i];
        let half = p.thickness / (2. * scale[1 - i]);
        Some(Self {
            vertical,
            along: [native(a[i] / scale[i])?, native(b[i] / scale[i])?],
            cross: [native(mid - half)?, native(mid + half)?],
        })
    }

    fn footprint(self) -> [i32; 4] {
        bounds(self.vertical, self.along, self.cross)
    }
}

fn along(b: [u32; 4], vertical: bool) -> [i32; 2] {
    let i = usize::from(vertical);
    [b[i] as i32, b[i + 2] as i32]
}

fn in_page([x, y, u, v]: [i32; 4], image: [usize; 2]) -> bool {
    x >= 0
        && y >= 0
        && u as usize <= image[0]
        && v as usize <= image[1]
        && (1..=MAX_LENGTH + 2).contains(&(u - x))
        && (1..=MAX_LENGTH + 2).contains(&(v - y))
        && ((u - x) * (v - y)) as usize <= MAX_PATCH
}

fn shares(a: &[String], b: &[String]) -> bool {
    a.iter().any(|id| b.contains(id))
}

struct Piece {
    span: Span,
    pitch: f64,
    sources: Vec<String>,
    phase_context: Option<[i32; 2]>,
}

struct Inventory<'a> {
    bands: &'a [RasterBand],
    previous: &'a [RasterSectionCourseReceipt],
    old_continuations: &'a [RasterFaceContinuationReceipt],
    doors: &'a [RasterHingedDoorReceipt],
    pieces: Vec<Piece>,
    scale: [f64; 2],
    image: [usize; 2],
}

impl<'a> Inventory<'a> {
    #[allow(clippy::too_many_arguments)]
    fn new(
        bands: &'a [RasterBand],
        decoded: Vec<VerifiedRasterFaceContinuation>,
        old_continuations: &'a [RasterFaceContinuationReceipt],
        previous: &'a [RasterSectionCourseReceipt],
        doors: &'a [RasterHingedDoorReceipt],
        scale: [f64; 2],
        image: [usize; 2],
    ) -> Self {
        let mut pieces: Vec<_> = bands
            .iter()
            .filter_map(|b| {
                let h = b.hatch.as_ref().filter(|h| hatch_quality(h))?;
                Some(Piece {
                    span: Span::from_band(b, scale)?,
                    pitch: h.mean_pitch_pixels,
                    sources: b.face_source_ids.to_vec(),
                    phase_context: None,
                })
            })
            .collect();
        pieces.extend(
            decoded
                .into_iter()
                .zip(old_continuations)
                .filter_map(|(p, receipt)| {
                    let index = receipt.anchor_band_indices.into_iter().flatten().next()?;
                    let pitch = bands
                        .get(index)?
                        .hatch
                        .as_ref()
                        .filter(|h| hatch_quality(h))?
                        .mean_pitch_pixels;
                    let span = Span::from_continuation(&p, scale)?;
                    Some(Piece {
                        span,
                        pitch,
                        sources: p.source_ids,
                        phase_context: Some(along(receipt.patch.bounds, span.vertical)),
                    })
                }),
        );
        Self {
            bands,
            previous,
            old_continuations,
            doors,
            pieces,
            scale,
            image,
        }
    }

    fn guide(&self, index: usize) -> Option<Guide> {
        let prior = self.previous.get(index)?;
        let outer = Span::from_band(self.bands.get(prior.outer_band_index)?, self.scale)?;
        let inner = self.bands.get(prior.inner_band_index)?;
        let core = Span::from_band(inner, self.scale)?;
        let h = inner.hatch.as_ref().filter(|h| hatch_quality(h))?;
        let [a, z] = outer.along;
        let [c, d] = core.cross;
        let mark = along(prior.section_patch.bounds, outer.vertical);
        let forward = match (
            mark[0] < a && a < mark[1] && mark[1] < z,
            a < mark[0] && mark[0] < z && z < mark[1],
        ) {
            (true, false) => true,
            (false, true) => false,
            _ => return None,
        };
        let side = match (
            outer.cross[0] < c && outer.cross[1] == d,
            outer.cross[0] == c && outer.cross[1] > d,
        ) {
            (true, false) => 0,
            (false, true) => 1,
            _ => return None,
        };
        let width = d - c;
        let [p, q] = h.face_ink_half_widths_pixels.map(|v| native(2. * v));
        let pens = [p?, q?];
        if outer.vertical != core.vertical
            || !(8..=40).contains(&width)
            || pens.into_iter().any(|q| !(1..=4).contains(&q))
            || width - pens[0] - pens[1] < 7
        {
            return None;
        }
        let samples = Samples::from_patch(
            &prior.course_patch,
            bounds(outer.vertical, outer.along, outer.cross),
        )?;
        let outside = outer.cross[side];
        let face = if side == 0 {
            (outside..c - 1).rev().find(|&y| {
                (a..z).all(|s| {
                    samples
                        .at(outer.vertical, s, y)
                        .is_some_and(|(l, _)| l < THRESHOLD)
                })
            })?
        } else {
            (d + 2..outside).find(|&y| {
                (a..z).all(|s| {
                    samples
                        .at(outer.vertical, s, y)
                        .is_some_and(|(l, _)| l < THRESHOLD)
                })
            })?
        };
        let quiet = if side == 0 { face + 1..c } else { d..face };
        if quiet.len() < 2
            || quiet.len() * 2 > width as usize
            || quiet.clone().any(|y| {
                (a..z).any(|s| {
                    samples
                        .at(outer.vertical, s, y)
                        .is_none_or(|(l, _)| l < THRESHOLD)
                })
            })
        {
            return None;
        }
        Some(Guide {
            previous: Span {
                along: outer.along,
                ..core
            },
            forward,
            face,
            quiet,
            pens,
            pitch: h.mean_pitch_pixels,
            sources: inner.face_source_ids.to_vec(),
        })
    }

    fn plan(&self, index: usize, work: &mut usize) -> Option<Plan> {
        let guide = self.guide(index)?;
        *work = work.checked_sub(self.pieces.len())?;
        let pieces: Vec<_> = self
            .pieces
            .iter()
            .filter(|p| {
                p.span.vertical == guide.previous.vertical
                    && p.span.cross == guide.previous.cross
                    && (p.pitch - guide.pitch).abs() <= guide.pitch * 0.2
            })
            .collect();
        let previous = guide.orient(guide.previous.along);
        let first = guide.walk(
            &pieces,
            previous[1],
            Some(previous[0]),
            &guide.sources,
            work,
        )?;
        if protected(first.span.footprint(), self.doors) {
            return None;
        }
        let end = guide.orient(first.span.along)[1];
        *work = work.checked_sub(pieces.len())?;
        let next = pieces
            .iter()
            .filter_map(|p| {
                let [a, _] = guide.orient(p.span.along);
                (a > end
                    && f64::from(a - end) < guide.pitch.min(p.pitch)
                    && shares(&p.sources, &first.last_sources))
                .then_some(a)
            })
            .min();
        let second = next
            .and_then(|a| guide.walk(&pieces, a, None, &first.last_sources, work))
            .filter(|p| !protected(p.span.footprint(), self.doors));
        Some(Plan {
            guide,
            first,
            second,
        })
    }

    fn consistent(&self, p: &RasterCoursePatch, index: usize) -> bool {
        let a = p.bounds.map(|v| v as i32);
        let matches = |previous: &RasterCoursePatch| {
            !overlaps(a, previous.bounds.map(|v| v as i32)) || same_overlap(p, previous)
        };
        self.old_continuations.iter().all(|r| matches(&r.patch))
            && self
                .previous
                .get(index)
                .is_some_and(|r| matches(&r.course_patch) && matches(&r.section_patch))
    }
}

struct Guide {
    previous: Span,
    forward: bool,
    face: i32,
    quiet: std::ops::Range<i32>,
    pens: [i32; 2],
    pitch: f64,
    sources: Vec<String>,
}

struct Course {
    span: Span,
    sources: Vec<String>,
    last_sources: Vec<String>,
    terminal_context: Option<[i32; 2]>,
}

struct Plan {
    guide: Guide,
    first: Course,
    second: Option<Course>,
}

impl Guide {
    fn orient(&self, [a, z]: [i32; 2]) -> [i32; 2] {
        if self.forward { [a, z] } else { [-z, -a] }
    }

    fn walk(
        &self,
        pieces: &[&Piece],
        start: i32,
        initial_overlap: Option<i32>,
        start_sources: &[String],
        work: &mut usize,
    ) -> Option<Course> {
        let mut at = start;
        let mut sources = Vec::new();
        let mut last = start_sources;
        let mut terminal_context = None;
        for i in 0..MAX_PIECES {
            *work = work.checked_sub(pieces.len())?;
            let mut hits = pieces.iter().copied().filter_map(|p| {
                let [a, z] = self.orient(p.span.along);
                let start_matches =
                    a == at || (i == 0 && initial_overlap.is_some_and(|s| s <= a && a < at));
                (start_matches && z > at && z - start <= MAX_LENGTH && shares(last, &p.sources))
                    .then_some((z, p))
            });
            let Some((end, p)) = hits.next() else { break };
            if hits.any(|(z, other)| z != end || !shares(&p.sources, &other.sources)) {
                return None;
            }
            sources.extend(p.sources.iter().cloned());
            last = &p.sources;
            terminal_context = p.phase_context;
            at = end;
        }
        if at - start < 28 {
            return None;
        }
        sources.sort_unstable();
        sources.dedup();
        Some(Course {
            span: Span {
                along: self.orient([start, at]),
                ..self.previous
            },
            sources,
            last_sources: last.to_vec(),
            terminal_context,
        })
    }

    fn patch_bounds(&self, a: [i32; 2]) -> [i32; 4] {
        let [l, h] = self.previous.cross;
        bounds(
            self.previous.vertical,
            a,
            [(l - 1).min(self.face), (h + 1).max(self.face + 1)],
        )
    }

    fn pen_range(&self, side: usize) -> std::ops::Range<i32> {
        let [l, h] = self.previous.cross;
        if side == 0 {
            l..l + self.pens[0]
        } else {
            h - self.pens[1]..h
        }
    }

    fn pen_luma(&self, s: Samples<'_>, a: i32, side: usize) -> Option<u8> {
        self.pen_range(side).try_fold(u8::MAX, |l, c| {
            Some(l.min(s.at(self.previous.vertical, a, c)?.0))
        })
    }

    fn clear_facing(&self, s: Samples<'_>, a: i32) -> bool {
        s.at(self.previous.vertical, a, self.face)
            .is_some_and(|(l, _)| l < THRESHOLD)
            && self.quiet.clone().all(|c| {
                s.at(self.previous.vertical, a, c)
                    .is_some_and(|(l, _)| l >= THRESHOLD)
            })
    }

    fn measure(&self, course: &Course, p: &RasterCoursePatch) -> Option<()> {
        let expected = self.patch_bounds(course.span.along);
        let s = Samples::from_patch(p, expected)?;
        let [a, z] = course.span.along;
        let mut weak = 0;
        for t in a..z {
            if !self.clear_facing(s, t) {
                return None;
            }
            for side in 0..2 {
                let l = self.pen_luma(s, t, side)?;
                if l < THRESHOLD {
                    continue;
                }
                let edge = self.previous.cross[side] - i32::from(side == 0);
                let (flank, _) = s.at(self.previous.vertical, t, edge)?;
                if t <= a
                    || t + 1 >= z
                    || flank.saturating_sub(l) < 48
                    || self.pen_luma(s, t - 1, side)? >= THRESHOLD
                    || self.pen_luma(s, t + 1, side)? >= THRESHOLD
                {
                    return None;
                }
                weak += 1;
            }
        }
        if weak > 1 {
            return None;
        }
        let [x, y, u, v] = expected;
        let (along_origin, cross_origin) = if self.previous.vertical {
            (y, x)
        } else {
            (x, y)
        };
        let [min, max] = self.previous.cross;
        let check = |p0: i32, p1: i32| {
            hatch::measure(
                &p.luma,
                (u - x) as usize,
                (v - y) as usize,
                self.previous.vertical,
                (p0 - along_origin) as usize,
                (p1 - along_origin) as usize,
                (min + self.pens[0] - cross_origin) as usize,
                (max - self.pens[1] - cross_origin) as usize,
                self.pens.map(|t| f64::from(t) * 0.5),
                None,
            )
            .is_some_and(|h| {
                hatch_quality(&h) && (h.mean_pitch_pixels - self.pitch).abs() <= self.pitch * 0.20
            })
        };
        let end_sample = ((8. * self.pitch).ceil() as i32).max(28);
        if z - a < end_sample {
            return None;
        }
        let forward_end = |sample: i32| {
            if self.forward {
                [z - sample, z]
            } else {
                [a, a + sample]
            }
        };
        let [near_a, near_z] = if self.forward {
            [a, a + end_sample]
        } else {
            [z - end_sample, z]
        };
        // A short terminal continuation already proved a longer finite phase
        // window. Recheck that exact original context as well as the whole body.
        let far_sample = course.terminal_context.map_or(end_sample, |[lo, hi]| {
            let n = if self.forward && hi >= z {
                z - lo
            } else if !self.forward && lo <= a {
                hi - a
            } else {
                0
            };
            n.clamp(end_sample, z - a)
        });
        let [far_a, far_z] = forward_end(far_sample);
        (check(a, z) && check(near_a, near_z) && check(far_a, far_z)).then_some(())
    }

    fn measure_seam(
        &self,
        gap: [i32; 2],
        p: &RasterCoursePatch,
        first: &RasterCoursePatch,
        second: &RasterCoursePatch,
    ) -> Option<()> {
        let [a, z] = gap;
        let s = Samples::from_patch(p, self.patch_bounds([a - 1, z + 1]))?;
        if !same_overlap(p, first) || !same_overlap(p, second) {
            return None;
        }
        let [min, max] = self.previous.cross;
        for t in a..z {
            if !self.clear_facing(s, t) {
                return None;
            }
            for side in 0..2 {
                if !self
                    .pen_range(side)
                    .any(|c| s.at(self.previous.vertical, t, c).is_some_and(|(_, n)| n))
                {
                    return None;
                }
            }
            let interior = min + self.pens[0]..max - self.pens[1];
            let dark = interior
                .clone()
                .filter(|&c| {
                    s.at(self.previous.vertical, t, c)
                        .is_some_and(|(l, _)| l < THRESHOLD)
                })
                .count();
            if dark == 0 || dark + 2 > interior.len() {
                return None;
            }
        }
        Some(())
    }
}

impl Plan {
    fn gap(&self) -> Option<[i32; 2]> {
        let other = self.second.as_ref()?;
        let a = self.guide.orient(self.first.span.along)[1];
        let z = self.guide.orient(other.span.along)[0];
        Some(self.guide.orient([a, z]))
    }
}

pub(in crate::input::raster::extract) fn collect_aligned_courses(
    pixels: &Pixels<'_>,
    sources: &[Primitive],
    bands: &[RasterBand],
    continuations: &[RasterFaceContinuationReceipt],
    section_courses: &[RasterSectionCourseReceipt],
    doors: &[RasterHingedDoorReceipt],
) -> Vec<RasterAlignedCourseReceipt> {
    collect(
        pixels,
        sources,
        bands,
        continuations,
        section_courses,
        doors,
    )
    .unwrap_or_default()
}

fn collect(
    pixels: &Pixels<'_>,
    sources: &[Primitive],
    bands: &[RasterBand],
    continuations: &[RasterFaceContinuationReceipt],
    section_courses: &[RasterSectionCourseReceipt],
    doors: &[RasterHingedDoorReceipt],
) -> Option<Vec<RasterAlignedCourseReceipt>> {
    if section_courses.is_empty() || section_courses.len() > MAX_RECEIPTS {
        return Some(vec![]);
    }
    let image = [pixels.width, pixels.height];
    let decoded =
        verify_continuation_inventory(bands, sources, pixels.scale, image, continuations).ok()?;
    let inventory = Inventory::new(
        bands,
        decoded,
        continuations,
        section_courses,
        doors,
        pixels.scale,
        image,
    );
    let mut work = MAX_WORK;
    let mut occupied = Vec::new();
    let s = Samples::from_pixels(pixels);
    let mut result = vec![];
    for index in 0..section_courses.len() {
        let Some(plan) = inventory.plan(index, &mut work) else {
            continue;
        };
        let capture = |p: &Course| {
            let expected = plan.guide.patch_bounds(p.span.along);
            let patch = in_page(expected, inventory.image)
                .then(|| s.capture(expected))
                .flatten()?;
            plan.guide.measure(p, &patch)?;
            inventory.consistent(&patch, index).then_some(patch)
        };
        let footprint = plan.first.span.footprint();
        if occupied.iter().any(|&q| overlaps(footprint, q)) {
            continue;
        }
        let Some(first) = capture(&plan.first) else {
            continue;
        };
        occupied.push(footprint);
        let second = plan.second.as_ref().and_then(|piece| {
            let body = piece.span.footprint();
            if occupied.iter().any(|&q| overlaps(body, q)) {
                return None;
            }
            let patch = capture(piece)?;
            occupied.push(body);
            Some(patch)
        });
        let seam = second.as_ref().and_then(|p| {
            let [a, z] = plan.gap()?;
            let guide = &plan.guide;
            if protected(
                bounds(guide.previous.vertical, [a, z], guide.previous.cross),
                doors,
            ) {
                return None;
            }
            let bb = guide.patch_bounds([a - 1, z + 1]);
            let patch = in_page(bb, inventory.image)
                .then(|| s.capture(bb))
                .flatten()?;
            guide.measure_seam([a, z], &patch, &first, p)?;
            inventory.consistent(&patch, index).then_some(patch)
        });
        result.push(RasterAlignedCourseReceipt {
            section_course_index: index,
            first,
            second,
            seam,
        });
    }
    Some(result)
}

pub(crate) struct VerifiedAlignedBody {
    pub start: Point,
    pub end: Point,
    pub source_ids: Vec<String>,
}

pub(crate) struct VerifiedRasterAlignedCourse {
    pub previous_start: Point,
    pub previous_end: Point,
    pub thickness: f64,
    pub first: VerifiedAlignedBody,
    pub second: Option<VerifiedAlignedBody>,
    pub seam: Option<[Point; 2]>,
}

pub(crate) fn verified_aligned_courses(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterAlignedCourse>, String> {
    let records = &info.raster_aligned_courses;
    if records.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || records.len() > MAX_RECEIPTS
        || info.render_width == 0
        || info.render_height == 0
        || records
            .windows(2)
            .any(|r| r[0].section_course_index >= r[1].section_course_index)
    {
        return Err("invalid aligned raster course inventory".into());
    }
    let old = verified_section_courses(info, sources, page_width, page_height)?;
    let image = [info.render_width, info.render_height];
    let scale = [page_width / image[0] as f64, page_height / image[1] as f64];
    let decoded = verify_continuation_inventory(
        &info.bands,
        sources,
        scale,
        image,
        &info.raster_face_continuations,
    )?;
    let inventory = Inventory::new(
        &info.bands,
        decoded,
        &info.raster_face_continuations,
        &info.raster_section_courses,
        &info.raster_hinged_doors,
        scale,
        image,
    );
    let error = || {
        "aligned raster course disagrees with source phases, finite faces or the short seam"
            .to_string()
    };
    let mut work = MAX_WORK;
    let mut occupied = Vec::new();
    let mut result = Vec::new();
    for r in records {
        let index = r.section_course_index;
        let prior = old.get(index).ok_or_else(error)?;
        let plan = inventory.plan(index, &mut work).ok_or_else(error)?;
        let guide = &plan.guide;
        let validate = |c: &Course, p: &RasterCoursePatch, occupied: &mut Vec<[i32; 4]>| {
            let footprint = c.span.footprint();
            if !in_page(guide.patch_bounds(c.span.along), image)
                || occupied.iter().any(|&q| overlaps(footprint, q))
            {
                return None;
            }
            guide.measure(c, p)?;
            if !inventory.consistent(p, index) {
                return None;
            }
            occupied.push(footprint);
            Some(())
        };
        validate(&plan.first, &r.first, &mut occupied).ok_or_else(error)?;
        if let Some(p) = r.second.as_ref() {
            validate(plan.second.as_ref().ok_or_else(error)?, p, &mut occupied)
                .ok_or_else(error)?;
        }
        let middle = f64::from(guide.previous.cross[0] + guide.previous.cross[1]) * 0.5;
        let xy = |t: i32| {
            if guide.previous.vertical {
                Point::new(middle * scale[0], f64::from(t) * scale[1])
            } else {
                Point::new(f64::from(t) * scale[0], middle * scale[1])
            }
        };
        let seam = if let Some(p) = r.seam.as_ref() {
            let second = r.second.as_ref().ok_or_else(error)?;
            let [a, z] = plan.gap().ok_or_else(error)?;
            if protected(
                bounds(guide.previous.vertical, [a, z], guide.previous.cross),
                &info.raster_hinged_doors,
            ) || !in_page(guide.patch_bounds([a - 1, z + 1]), image)
                || !inventory.consistent(p, index)
            {
                return Err(error());
            }
            guide
                .measure_seam([a, z], p, &r.first, second)
                .ok_or_else(error)?;
            Some(if guide.forward {
                [xy(a), xy(z)]
            } else {
                [xy(z), xy(a)]
            })
        } else {
            None
        };
        let body = |c: &Course| {
            let [start, end] = c.span.along.map(xy);
            let mut ids = c.sources.clone();
            ids.extend(prior.source_ids.iter().cloned());
            ids.sort_unstable();
            ids.dedup();
            VerifiedAlignedBody {
                start,
                end,
                source_ids: ids,
            }
        };
        result.push(VerifiedRasterAlignedCourse {
            previous_start: prior.start,
            previous_end: prior.end,
            thickness: prior.thickness,
            first: body(&plan.first),
            second: r
                .second
                .as_ref()
                .zip(plan.second.as_ref())
                .map(|(_, c)| body(c)),
            seam,
        });
    }
    Ok(result)
}
