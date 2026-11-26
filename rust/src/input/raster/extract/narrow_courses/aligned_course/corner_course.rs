//! Independently measure an upright and its two offset, nonmaterial seats.
use super::{
    Inventory, Plan, RasterBand, Span, THRESHOLD, bounds, hatch, hatch_quality, overlaps,
    protected, same_overlap, verified_aligned_courses, verify_continuation_inventory,
};
use super::{Pixels, Samples};
use crate::{
    input::raster::{
        PdfInput, PdfRoute, RasterAlignedCourseReceipt, RasterCornerCourseReceipt,
        RasterCoursePatch, RasterFaceContinuationReceipt, RasterHingedDoorReceipt,
        RasterSectionCourseReceipt,
    },
    model::{Point, Primitive},
};

const MAX_LENGTH: i32 = 128;
const MAX_RECEIPTS: usize = 16;
const MAX_PATCH: usize = 4_096;
const MAX_WORK: usize = 2_000_000;
const LIGHT_INSIDE_FLANK: u8 = 210;
const EDGE_CONTRAST: u8 = 32;

/// Signed native coordinates increase toward the corner and toward the facing.
#[derive(Clone, Copy)]
struct Order(bool);

impl Order {
    fn span(self, [lo, hi]: [i32; 2]) -> [i32; 2] {
        if self.0 { [lo, hi] } else { [-hi, -lo] }
    }

    fn cell(self, p: i32) -> i32 {
        if self.0 { p } else { -p - 1 }
    }

    fn point(self, p: f64) -> f64 {
        if self.0 { p } else { -p }
    }
}

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    toward: Order,
    out: Order,
    terminal: i32,
    horizontal_cross: [i32; 2],
    old_across: [i32; 2],
    old_tip: i32,
    pitch: f64,
}

#[derive(Clone, Copy, Eq, PartialEq)]
struct Body {
    along: [i32; 2],
    cross: [i32; 2],
}

impl Frame {
    fn new(plan: &Plan, band: &RasterBand, scale: [f64; 2]) -> Option<Self> {
        let end = plan.second.as_ref()?.span;
        let old = Span::from_band(band, scale)?;
        let h = band.hatch.as_ref().filter(|h| hatch_quality(h))?;
        if end.vertical == old.vertical
            || band.boundary_coverage.iter().any(|&n| n < 0.95)
            || (h.mean_pitch_pixels - plan.guide.pitch).abs() > 0.20 * plan.guide.pitch
        {
            return None;
        }
        let toward = Order(plan.guide.face >= end.cross[1]);
        let out = Order(plan.guide.forward);
        let horizontal_cross = toward.span(end.cross);
        let old_across = out.span(old.cross);
        let old_along = toward.span(old.along);
        let terminal = out.span(end.along)[1];
        let width = horizontal_cross[1] - horizontal_cross[0];
        let room = horizontal_cross[0] - old_along[1];
        if room < 3 * width
            || room > MAX_LENGTH + width
            || old_along[1] - old_along[0] < 3 * width
            || old_across[0] < terminal
            || old_across[0] - terminal > width / 2
            || old_across[1] - old_across[0] < width + 4
            || old_across[1] - old_across[0] > 2 * width
        {
            return None;
        }
        Some(Self {
            vertical: old.vertical,
            toward,
            out,
            terminal,
            horizontal_cross,
            old_across,
            old_tip: old_along[1],
            pitch: plan.guide.pitch,
        })
    }

    fn width(self) -> i32 {
        self.horizontal_cross[1] - self.horizontal_cross[0]
    }

    fn rect(self, a: [i32; 2], c: [i32; 2]) -> [i32; 4] {
        bounds(self.vertical, self.toward.span(a), self.out.span(c))
    }

    fn physical(self, body: Body) -> [i32; 4] {
        self.rect(body.along, body.cross)
    }

    fn at(self, s: Samples<'_>, a: i32, c: i32) -> Option<(u8, bool)> {
        s.at(self.vertical, self.toward.cell(a), self.out.cell(c))
    }

    fn ink(self, s: Samples<'_>, a: i32, c: i32) -> bool {
        self.at(s, a, c).is_some_and(|(value, _)| value < THRESHOLD)
    }

    fn station(self, s: Samples<'_>, t: i32, [left, right]: [i32; 2]) -> bool {
        let read = |c| self.at(s, t, c).map(|(luma, _)| luma);
        let Some(flank) = read(left - 1) else {
            return false;
        };
        if flank < LIGHT_INSIDE_FLANK
            || [left, left + 1]
                .into_iter()
                .any(|c| read(c).is_none_or(|inside| flank.saturating_sub(inside) < EDGE_CONTRAST))
            || !self.ink(s, t, right - 1)
        {
            return false;
        }
        // One variable edge-fringe cell can precede the independently light
        // gap. The facing line's own antialias cell is outside that gap too.
        let face = self.old_across[1] - 1;
        (right + 1..face - 1).all(|c| read(c).is_some_and(|luma| luma >= THRESHOLD))
            && self.ink(s, t, face)
    }

    fn plausible_cross(self, [left, right]: [i32; 2]) -> bool {
        let width = right - left;
        let outer_clear = self.old_across[1] - right - 3;
        left >= self.old_across[0]
            && left > self.terminal
            && left - self.terminal <= self.width() / 2
            && width * 4 >= self.width() * 3
            && width * 4 <= self.width() * 5
            && (3..=self.width() / 2).contains(&outer_clear)
    }

    fn plausible_body(self, p: Body) -> bool {
        let length = p.along[1] - p.along[0];
        let north = p.along[0] - self.old_tip;
        self.plausible_cross(p.cross)
            && p.along[1] == self.horizontal_cross[0]
            && (self.width() * 4..=MAX_LENGTH).contains(&length)
            && (1..=self.width()).contains(&north)
    }

    fn body_rect(self, p: Body) -> [i32; 4] {
        self.rect(p.along, [p.cross[0] - 1, self.old_across[1]])
    }

    fn north_rect(self, p: Body) -> [i32; 4] {
        self.rect(
            [self.old_tip - 1, p.along[0] + 1],
            [p.cross[0] - 1, self.old_across[1]],
        )
    }

    fn corner_rect(self) -> [i32; 4] {
        self.rect(
            [self.horizontal_cross[0] - 1, self.horizontal_cross[1]],
            [self.terminal - 1, self.old_across[1]],
        )
    }

    fn phase(self, p: Body, patch: &RasterCoursePatch) -> Option<()> {
        let expected = self.body_rect(p);
        let s = Samples::from_patch(patch, expected)?;
        if !(p.along[0]..p.along[1]).all(|t| self.station(s, t, p.cross)) {
            return None;
        }
        let [x, y, u, v] = expected;
        let (t0, c0) = if self.vertical { (y, x) } else { (x, y) };
        let [a, z] = self.toward.span(p.along);
        let [inner, outer] = p.cross;
        // Leave the two quiet-facing side pens out of the interior profiles.
        let [l, h] = self.out.span([inner + 2, outer - 1]);
        let verify = |t: i32, end: i32| {
            hatch::measure(
                &patch.luma,
                (u - x) as usize,
                (v - y) as usize,
                self.vertical,
                (t - t0) as usize,
                (end - t0) as usize,
                (l - c0) as usize,
                (h - c0) as usize,
                [0.5, 0.5],
                None,
            )
            .is_some_and(|w| {
                hatch_quality(&w) && (w.mean_pitch_pixels - self.pitch).abs() <= self.pitch * 0.20
            })
        };
        let n = ((8. * self.pitch).ceil() as i32).max(28);
        (z - a >= n && verify(a, z) && verify(a, a + n) && verify(z - n, z)).then_some(())
    }

    fn measure_north(
        self,
        p: Body,
        patch: &RasterCoursePatch,
        body: &RasterCoursePatch,
    ) -> Option<()> {
        let s = Samples::from_patch(patch, self.north_rect(p))?;
        if !same_overlap(patch, body) {
            return None;
        }
        let [left, right] = p.cross;
        let start = p.along[0];
        // The outer structural pen continues through the setback. On the
        // inside face, an occupied flank ends the independently bounded body.
        (self.station(s, start, p.cross)
            && self.ink(s, start - 1, left - 1)
            && (self.old_tip - 1..=start).all(|a| self.ink(s, a, right - 1)))
        .then_some(())
    }

    fn measure_corner(
        self,
        p: Body,
        patch: &RasterCoursePatch,
        body: &RasterCoursePatch,
        terminal: &RasterCoursePatch,
    ) -> Option<()> {
        let s = Samples::from_patch(patch, self.corner_rect())?;
        let south = self.horizontal_cross;
        let [left, right] = p.cross;
        if !same_overlap(patch, body)
            || !same_overlap(patch, terminal)
            || !self.station(s, south[0] - 1, p.cross)
            || !self.ink(s, south[0], left - 1)
        {
            return None;
        }
        let upper = [south[0], south[0] + 1]
            .into_iter()
            .all(|a| (self.terminal - 1..=left + 1).all(|c| self.ink(s, a, c)));
        let lower = [south[1] - 2, south[1] - 1]
            .into_iter()
            .all(|a| (self.terminal - 1..right).all(|c| self.ink(s, a, c)));
        let outer = (south[0] - 1..south[1]).all(|a| self.ink(s, a, right - 1));
        (upper && lower && outer).then_some(())
    }

    fn paths(
        self,
        p: Body,
        scale: [f64; 2],
        incoming: &RasterBand,
        previous: [Point; 2],
    ) -> [Vec<Point>; 2] {
        let at = |a: f64, c: f64| {
            let a = self.toward.point(a);
            let c = self.out.point(c);
            if self.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let [start, end] = p.along.map(f64::from);
        let old_tip = f64::from(self.old_tip);
        let [l, r] = p.cross.map(f64::from);
        let near = (l + r) * 0.5;
        let outer_pen = r - 0.5;
        let terminal = f64::from(self.terminal);
        let lip = end + 1.5;
        let [north_old, south_old] = [
            [incoming.start, incoming.end][usize::from(self.toward.0)],
            previous[usize::from(self.out.0)],
        ];
        [
            vec![
                north_old,
                at(old_tip, outer_pen),
                at(start, outer_pen),
                at(start, near),
            ],
            vec![
                south_old,
                at(lip, terminal),
                at(lip, l + 0.5),
                at(end, l + 0.5),
                at(end, near),
            ],
        ]
    }
}

fn valid([x, y, u, v]: [i32; 4], image: [usize; 2]) -> bool {
    x >= 0
        && y >= 0
        && u as usize <= image[0]
        && v as usize <= image[1]
        && (1..=MAX_LENGTH).contains(&(u - x))
        && (1..=MAX_LENGTH).contains(&(v - y))
        && ((u - x) * (v - y)) as usize <= MAX_PATCH
}

fn boxes(frame: Frame, body: Body) -> [[i32; 4]; 3] {
    [
        frame.body_rect(body),
        frame.north_rect(body),
        frame.corner_rect(),
    ]
}

fn isolated(
    frame: Frame,
    body: Body,
    doors: &[RasterHingedDoorReceipt],
    image: [usize; 2],
) -> bool {
    frame.plausible_body(body)
        && boxes(frame, body)
            .into_iter()
            .all(|q| valid(q, image) && !protected(q, doors))
}

fn verify_patches(
    frame: Frame,
    body: Body,
    receipt: &RasterCornerCourseReceipt,
    terminal: &RasterCoursePatch,
) -> Option<()> {
    frame.phase(body, &receipt.body)?;
    frame.measure_north(body, &receipt.north_seat, &receipt.body)?;
    frame.measure_corner(body, &receipt.corner_seat, &receipt.body, terminal)
}

#[allow(clippy::too_many_arguments)]
pub(in crate::input::raster::extract) fn collect_corner_courses(
    pixels: &Pixels<'_>,
    sources: &[Primitive],
    bands: &[RasterBand],
    continuations: &[RasterFaceContinuationReceipt],
    section_courses: &[RasterSectionCourseReceipt],
    aligned_courses: &[RasterAlignedCourseReceipt],
    doors: &[RasterHingedDoorReceipt],
) -> Vec<RasterCornerCourseReceipt> {
    collect(
        pixels,
        sources,
        bands,
        continuations,
        section_courses,
        aligned_courses,
        doors,
    )
    .unwrap_or_default()
}

#[allow(clippy::too_many_arguments)]
fn collect(
    pixels: &Pixels<'_>,
    sources: &[Primitive],
    bands: &[RasterBand],
    continuations: &[RasterFaceContinuationReceipt],
    section_courses: &[RasterSectionCourseReceipt],
    aligned_courses: &[RasterAlignedCourseReceipt],
    doors: &[RasterHingedDoorReceipt],
) -> Option<Vec<RasterCornerCourseReceipt>> {
    if aligned_courses.is_empty() || aligned_courses.len() > MAX_RECEIPTS {
        return Some(vec![]);
    }
    let image = [pixels.width, pixels.height];
    let decoded =
        verify_continuation_inventory(bands, sources, pixels.scale, image, continuations).ok()?;
    let inputs = Inventory::new(
        bands,
        decoded,
        continuations,
        section_courses,
        doors,
        pixels.scale,
        image,
    );
    let samples = Samples::from_pixels(pixels);
    let mut work = MAX_WORK;
    let mut result = Vec::new();
    let mut used = Vec::new();
    for (index, old) in aligned_courses.iter().enumerate() {
        let Some(terminal) = old.second.as_ref() else {
            continue;
        };
        let Some(plan) = inputs.plan(old.section_course_index, &mut work) else {
            continue;
        };
        work = work.checked_sub(bands.len())?;
        let mut choice = None;
        let mut ambiguous = false;
        'candidate: for (incoming, band) in bands.iter().enumerate() {
            let Some(frame) = Frame::new(&plan, band, pixels.scale) else {
                continue;
            };
            let end = frame.horizontal_cross[0];
            for left in frame.old_across[0]..frame.old_across[1] {
                for right in left + 1..frame.old_across[1] {
                    let cross = [left, right];
                    if !frame.plausible_cross(cross) {
                        continue;
                    }
                    let mut start = end;
                    while start > frame.old_tip {
                        work = work.checked_sub(frame.width() as usize)?;
                        if !frame.station(samples, start - 1, cross) {
                            break;
                        }
                        start -= 1;
                    }
                    let body = Body {
                        along: [start, end],
                        cross,
                    };
                    if !isolated(frame, body, doors, image) {
                        continue;
                    }
                    let [a, n, k] = boxes(frame, body);
                    let receipt = RasterCornerCourseReceipt {
                        aligned_course_index: index,
                        incoming_band_index: incoming,
                        body: samples.capture(a)?,
                        north_seat: samples.capture(n)?,
                        corner_seat: samples.capture(k)?,
                    };
                    if verify_patches(frame, body, &receipt, terminal).is_none() {
                        continue;
                    }
                    if choice.is_some() {
                        ambiguous = true;
                        break 'candidate;
                    }
                    choice = Some((frame.physical(body), receipt));
                }
            }
        }
        if !ambiguous
            && let Some((q, r)) = choice
            && !used.iter().any(|&a| overlaps(a, q))
        {
            used.push(q);
            result.push(r);
        }
    }
    Some(result)
}

pub(crate) struct VerifiedRasterCornerCourse {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub incoming_start: Point,
    pub incoming_end: Point,
    pub incoming_thickness: f64,
    pub incoming_sources: [String; 2],
    pub terminal_start: Point,
    pub terminal_end: Point,
    pub terminal_thickness: f64,
    pub north_path: Vec<Point>,
    pub corner_path: Vec<Point>,
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_corner_courses(
    info: &PdfInput,
    sources: &[Primitive],
    w: f64,
    h: f64,
) -> Result<Vec<VerifiedRasterCornerCourse>, String> {
    let records = &info.raster_corner_courses;
    if records.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || info.render_width == 0
        || info.render_height == 0
        || records.len() > MAX_RECEIPTS
        || records
            .windows(2)
            .any(|r| r[0].aligned_course_index >= r[1].aligned_course_index)
    {
        return Err("invalid measured raster corner course inventory".into());
    }
    let error =
        || "raster corner course disagrees with its painted stock or measured seats".to_string();
    let prior = verified_aligned_courses(info, sources, w, h)?;
    let image = [info.render_width, info.render_height];
    let scale = [w / image[0] as f64, h / image[1] as f64];
    let decoded = verify_continuation_inventory(
        &info.bands,
        sources,
        scale,
        image,
        &info.raster_face_continuations,
    )?;
    let inputs = Inventory::new(
        &info.bands,
        decoded,
        &info.raster_face_continuations,
        &info.raster_section_courses,
        &info.raster_hinged_doors,
        scale,
        image,
    );
    let mut work = MAX_WORK;
    let mut used = Vec::new();
    let mut result = Vec::new();
    for record in records {
        let previous = info
            .raster_aligned_courses
            .get(record.aligned_course_index)
            .ok_or_else(error)?;
        let terminal_pixels = previous.second.as_ref().ok_or_else(error)?;
        let old = prior.get(record.aligned_course_index).ok_or_else(error)?;
        let terminal = old.second.as_ref().ok_or_else(error)?;
        let plan = inputs
            .plan(previous.section_course_index, &mut work)
            .ok_or_else(error)?;
        let incoming = info
            .bands
            .get(record.incoming_band_index)
            .ok_or_else(error)?;
        let frame = Frame::new(&plan, incoming, scale).ok_or_else(error)?;
        let [x, y, u, v] = record.body.bounds.map(|n| n as i32);
        if !valid([x, y, u, v], image) {
            return Err(error());
        }
        let (a, c) = if frame.vertical {
            ([y, v], [x, u])
        } else {
            ([x, u], [y, v])
        };
        let along = frame.toward.span(a);
        let [lo, hi] = frame.out.span(c);
        if hi != frame.old_across[1] {
            return Err(error());
        }
        let mut selected = None;
        for edge in lo + 2..hi {
            let body = Body {
                along,
                cross: [lo + 1, edge],
            };
            if !isolated(frame, body, &info.raster_hinged_doors, image) {
                continue;
            }
            let q = frame.physical(body);
            if used.iter().any(|&p| overlaps(p, q))
                || verify_patches(frame, body, record, terminal_pixels).is_none()
            {
                continue;
            }
            if selected.replace(body).is_some() {
                return Err(error());
            }
        }
        let body = selected.ok_or_else(error)?;
        used.push(frame.physical(body));
        let [start, end] = frame.toward.span(body.along).map(f64::from);
        let [left, right] = frame.out.span(body.cross).map(f64::from);
        let c = (left + right) * 0.5;
        let x = |a: f64| {
            if frame.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let [north_path, corner_path] =
            frame.paths(body, scale, incoming, [terminal.start, terminal.end]);
        let mut source_ids = terminal.source_ids.clone();
        source_ids.extend(incoming.face_source_ids.iter().cloned());
        source_ids.sort_unstable();
        source_ids.dedup();
        result.push(VerifiedRasterCornerCourse {
            start: x(start),
            end: x(end),
            thickness: (right - left) * scale[usize::from(!frame.vertical)],
            incoming_start: incoming.start,
            incoming_end: incoming.end,
            incoming_thickness: incoming.thickness,
            incoming_sources: incoming.face_source_ids.clone(),
            terminal_start: terminal.start,
            terminal_end: terminal.end,
            terminal_thickness: old.thickness,
            north_path,
            corner_path,
            source_ids,
        });
    }
    Ok(result)
}
