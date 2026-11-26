//! Two independently measured inner courses can share a source-overprinted junction.
use super::super::{hinged_doors::Samples, residual_hatch::Pixels};
use super::{
    BandFootprint, Frame as PreviousFrame, MAX_PATCH, RasterBand, THRESHOLD, bounds, hatch,
    hatch_quality, native, overlaps, protected, verified_narrow_courses,
};
use crate::{
    input::raster::{
        PdfInput, PdfRoute, RasterCoursePatch, RasterHingedDoorReceipt, RasterJambStockReceipt,
        RasterNarrowCourseReceipt, RasterSectionCourseReceipt,
    },
    model::{Point, Primitive},
};

const MAX_RECEIPTS: usize = 16;
const MAX_WORK: usize = 2_000_000;

struct Inputs<'a> {
    bands: &'a [RasterBand],
    jambs: &'a [RasterJambStockReceipt],
    doors: &'a [RasterHingedDoorReceipt],
    previous: &'a [RasterNarrowCourseReceipt],
    measured: Vec<Option<BandFootprint>>,
}

impl<'a> Inputs<'a> {
    fn band(&self, index: usize) -> Option<(&'a RasterBand, BandFootprint)> {
        self.bands
            .get(index)
            .zip(self.measured.get(index).copied().flatten())
    }

    fn previous(&self, index: usize) -> Option<(PreviousFrame, &'a RasterNarrowCourseReceipt)> {
        let receipt = self.previous.get(index)?;
        let jamb = self.jambs.get(receipt.jamb_stock_index)?;
        let door = self.doors.get(jamb.door_index)?;
        let frame = PreviousFrame::new(
            jamb,
            door,
            self.band(jamb.return_band_index)?,
            self.band(receipt.inner_band_index)?,
        )?;
        Some((frame, receipt))
    }
}

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    along: [i32; 2],
    previous_end: i32,
    near: usize,
    cross: [i32; 2],
    outer: [i32; 2],
    edge: usize,
    pens: [i32; 2],
    outer_pen: i32,
    pitch: f64,
}

impl Frame {
    fn new(
        previous: PreviousFrame,
        left: &RasterBand,
        outer: (&RasterBand, BandFootprint),
        inner: (&RasterBand, BandFootprint),
    ) -> Option<Self> {
        let (ob, outer) = outer;
        let (ib, inner) = inner;
        let oh = ob.hatch.as_ref().filter(|h| hatch_quality(h))?;
        let ih = ib.hatch.as_ref().filter(|h| hatch_quality(h))?;
        if previous.vertical != outer.vertical
            || previous.vertical != inner.vertical
            || (oh.mean_pitch_pixels - previous.pitch).abs() > previous.pitch * 0.20
            || (ih.mean_pitch_pixels - previous.pitch).abs() > previous.pitch * 0.20
        {
            return None;
        }
        let side = previous.same_side;
        let other = 1 - side;
        if ob.face_source_ids[side] != left.face_source_ids[side]
            || ib.face_source_ids[side] != left.face_source_ids[side]
            || ib.face_source_ids[other] == ob.face_source_ids[other]
        {
            return None;
        }
        let [a, z] = outer.along.map(native);
        let [a, z] = [a?, z?];
        let [c, d] = outer.cross.map(native);
        let outer = [c?, d?];
        let [c, d] = inner.cross.map(native);
        let cross = [c?, d?];
        let [low, high] = inner.along.map(native);
        let [low, high] = [low?, high?];
        let far = usize::from(previous.along[0] == previous.contact[0]);
        let near = 1 - far;
        let previous_end = previous.along[far];
        let gap = if near == 0 {
            a - previous_end
        } else {
            previous_end - z
        };
        let length = z - a;
        let width = cross[1] - cross[0];
        let separation = (outer[other] - cross[other]).abs();
        let overlap = z.min(high) - a.max(low);
        let far_anchored = if near == 0 {
            low < z && high >= z && high - z <= previous.pitch.ceil() as i32
        } else {
            high > a && low <= a && a - low <= previous.pitch.ceil() as i32
        };
        if cross != previous.core
            || outer[side] != cross[side]
            || cross[0] < outer[0]
            || cross[1] > outer[1]
            || !(4..=14).contains(&separation)
            || !(48..=128).contains(&length)
            || gap < 3
            || gap * 2 > width
            || f64::from(gap) > 3. * previous.pitch
            || !far_anchored
            || f64::from(overlap) < 5. * previous.pitch
        {
            return None;
        }
        let [p, q] = ih.face_ink_half_widths_pixels.map(|half| native(2. * half));
        let pens = [p?, q?];
        let outer_pen = native(2. * oh.face_ink_half_widths_pixels[other])?;
        if pens.into_iter().any(|p| !(1..=4).contains(&p))
            || width - pens[0] - pens[1] < 7
            || !(1..=4).contains(&outer_pen)
        {
            return None;
        }
        Some(Self {
            vertical: previous.vertical,
            along: [a, z],
            previous_end,
            near,
            cross,
            outer,
            edge: other,
            pens,
            outer_pen,
            pitch: ih.mean_pitch_pixels,
        })
    }

    fn gap(self) -> [i32; 2] {
        let a = self.along[self.near];
        [self.previous_end.min(a), self.previous_end.max(a)]
    }

    fn course_bounds(self) -> Option<[i32; 4]> {
        let r = bounds(self.vertical, self.along, self.outer);
        valid_bounds(r, 128).then_some(r)
    }

    fn section_bounds(self, previous: PreviousFrame) -> Option<[i32; 4]> {
        let width = self.cross[1] - self.cross[0];
        let [a, z] = self.gap();
        let a = [a - 3, z + 3];
        let c = [
            self.outer[0].min(previous.broad[0]) - width * 5,
            self.outer[1].max(previous.broad[1]) + width * 5,
        ];
        let r = bounds(self.vertical, a, c);
        valid_bounds(r, 256).then_some(r)
    }

    fn pen_ranges(self) -> [std::ops::Range<i32>; 2] {
        [
            self.cross[0]..self.cross[0] + self.pens[0],
            self.cross[1] - self.pens[1]..self.cross[1],
        ]
    }

    fn body(self) -> [i32; 4] {
        bounds(self.vertical, self.along, self.cross)
    }

    fn measure_course(self, p: &RasterCoursePatch) -> Option<std::ops::Range<i32>> {
        let bounds = self.course_bounds()?;
        let s = Samples::from_patch(p, bounds)?;
        let at = |a, c| s.at(self.vertical, a, c);
        let [a, z] = self.along;
        for along in a..z {
            for r in self.pen_ranges() {
                if !r
                    .clone()
                    .any(|c| at(along, c).is_some_and(|(l, _)| l < THRESHOLD))
                {
                    return None;
                }
            }
        }
        // The facing starts at the far measured edge and may extend inward as a
        // continuous pen. Its true width never widens the independent wall core.
        let (first, step) = if self.edge == 0 {
            (self.outer[0], 1)
        } else {
            (self.outer[1] - 1, -1)
        };
        let facing = (0..=self.outer_pen + 2)
            .take_while(|i| {
                (a..z).all(|x| at(x, first + i * step).is_some_and(|(l, _)| l < THRESHOLD))
            })
            .count() as i32;
        if !(self.outer_pen..=self.outer_pen + 2).contains(&facing) {
            return None;
        }
        let limit = first + facing * step;
        let quiet = if self.edge == 0 {
            limit..self.cross[0]
        } else {
            self.cross[1]..limit + 1
        };
        if quiet.len() < 2
            || quiet.len() * 2 > (self.cross[1] - self.cross[0]) as usize
            || quiet
                .clone()
                .any(|c| (a..z).any(|x| at(x, c).is_none_or(|(l, _)| l < THRESHOLD)))
        {
            return None;
        }

        let [x, y, u, v] = bounds;
        let origin = if self.vertical { x } else { y };
        let axis = if self.vertical { y } else { x };
        let verify = |l: i32, h: i32| {
            hatch::measure(
                &p.luma,
                (u - x) as usize,
                (v - y) as usize,
                self.vertical,
                (l - axis) as usize,
                (h - axis) as usize,
                (self.cross[0] + self.pens[0] - origin) as usize,
                (self.cross[1] - self.pens[1] - origin) as usize,
                self.pens.map(|w| f64::from(w) * 0.5),
                None,
            )
            .is_some_and(|h| {
                hatch_quality(&h) && (h.mean_pitch_pixels - self.pitch).abs() <= 0.20 * self.pitch
            })
        };
        let sample = ((8. * self.pitch).ceil() as i32).max(28);
        (z - a > sample && verify(a, z) && verify(a, a + sample) && verify(z - sample, z))
            .then_some(quiet)
    }

    fn verify_section(
        self,
        previous: PreviousFrame,
        prior: &RasterCoursePatch,
        course: &RasterCoursePatch,
        patch: &RasterCoursePatch,
        quiet: std::ops::Range<i32>,
    ) -> Option<()> {
        let lower = self.section_bounds(previous)?;
        let s = Samples::from_patch(patch, lower)?;
        if !same_overlap(patch, prior) || !same_overlap(patch, course) {
            return None;
        }
        let at = |a, c| s.at(self.vertical, a, c);
        let [lo, hi] = self.gap();
        let [lc, hc] = self.cross;
        let width = hc - lc;
        let facing = if self.edge == 0 {
            quiet.start - 1
        } else {
            quiet.end
        };
        let mut stroke = vec![];
        let mut varied = 0;
        for a in lo..hi {
            for pen in self.pen_ranges() {
                if !pen
                    .clone()
                    .any(|c| at(a, c).is_some_and(|(l, _)| l < THRESHOLD))
                {
                    return None;
                }
            }
            if at(a, facing)?.0 >= THRESHOLD {
                return None;
            }
            if quiet
                .clone()
                .any(|c| at(a, c).is_some_and(|(l, _)| l < THRESHOLD))
            {
                stroke.push(a);
            } else if (lc..hc).all(|c| at(a, c).is_some_and(|(l, _)| l < THRESHOLD)) {
                // Other continuous transverse cuts are independent jamb evidence.
                return None;
            } else {
                let core = (lc + self.pens[0])..(hc - self.pens[1]);
                if core
                    .clone()
                    .any(|c| at(a, c).is_some_and(|(l, _)| l < THRESHOLD))
                    && core
                        .clone()
                        .any(|c| at(a, c).is_some_and(|(l, _)| l >= THRESHOLD))
                {
                    varied += 1;
                }
            }
        }
        let (a, z) = (*stroke.first()?, *stroke.last()? + 1);
        if !(1..=3).contains(&stroke.len())
            || (z - a) as usize != stroke.len()
            || varied * 2 < hi - lo - (z - a)
        {
            return None;
        }

        for i in 0..2 {
            let plane = if i == 0 {
                self.outer[0].min(previous.broad[0])
            } else {
                self.outer[1].max(previous.broad[1])
            };
            let row = |j| if i == 0 { plane - 1 - j } else { plane + j };
            let stroke_ink = |j| (a..z).any(|t| at(t, row(j)).is_some_and(|(_, n)| n));
            let flank_clear = |j| {
                [a - 2, z + 1]
                    .into_iter()
                    .all(|t| at(t, row(j)).is_some_and(|(_, n)| !n))
            };
            if (0..width / 2).filter(|&j| stroke_ink(j)).count() < 3 {
                return None;
            }
            // Independently observe a clear interruption and a long distant
            // dash. A single cap, grid tick or box outline cannot supply both.
            let mut blank = 0;
            let mut has_break = false;
            let mut dark = 0;
            let mut has_dash = false;
            for j in width / 2..width * 5 {
                if stroke_ink(j) && flank_clear(j) {
                    dark += 1;
                    blank = 0;
                    if has_break && dark >= width {
                        has_dash = true;
                        break;
                    }
                } else {
                    dark = 0;
                    blank = if stroke_ink(j) { 0 } else { blank + 1 };
                    has_break |= blank >= width / 2;
                }
            }
            if !has_dash {
                return None;
            }
        }
        Some(())
    }
}

fn valid_bounds([x, y, u, v]: [i32; 4], side: i32) -> bool {
    x >= 0
        && y >= 0
        && (1..=side).contains(&(u - x))
        && (1..=side).contains(&(v - y))
        && ((u - x) * (v - y)) as usize <= MAX_PATCH
}

pub(super) fn same_overlap(a: &RasterCoursePatch, b: &RasterCoursePatch) -> bool {
    let ar = a.bounds.map(|x| x as i32);
    let br = b.bounds.map(|x| x as i32);
    let Some(a) = Samples::from_patch(a, ar) else {
        return false;
    };
    let Some(b) = Samples::from_patch(b, br) else {
        return false;
    };
    overlaps(ar, br)
        && (ar[1].max(br[1])..ar[3].min(br[3]))
            .all(|y| (ar[0].max(br[0])..ar[2].min(br[2])).all(|x| a.xy(x, y) == b.xy(x, y)))
}

fn valid_frame(f: Frame, doors: &[RasterHingedDoorReceipt]) -> bool {
    !protected(f.body(), doors) && !protected(bounds(f.vertical, f.gap(), f.cross), doors)
}

pub(in crate::input::raster::extract) fn collect_sections(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
    previous: &[RasterNarrowCourseReceipt],
) -> Vec<RasterSectionCourseReceipt> {
    collect(pixels, doors, jambs, bands, previous).unwrap_or_default()
}

fn collect(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
    previous: &[RasterNarrowCourseReceipt],
) -> Option<Vec<RasterSectionCourseReceipt>> {
    if previous.is_empty() {
        return Some(vec![]);
    }
    let s = Samples::from_pixels(pixels);
    let inputs = Inputs {
        bands,
        jambs,
        doors,
        previous,
        measured: bands
            .iter()
            .map(|b| BandFootprint::read(b, pixels.scale))
            .collect(),
    };
    let mut work = MAX_WORK;
    let mut results = Vec::<(Frame, RasterSectionCourseReceipt)>::new();
    for previous_course_index in 0..previous.len() {
        let (p, r) = inputs.previous(previous_course_index)?;
        let (left, _) = inputs.band(r.inner_band_index)?;
        let lane = &left.face_source_ids[p.same_side];
        work = work.checked_sub(bands.len())?;
        let mut choice = None;
        let mut ambiguous = false;
        let along = p.along[usize::from(p.along[0] == p.contact[0])];
        let available = bands
            .iter()
            .enumerate()
            .filter(|(_, b)| b.face_source_ids[p.same_side] == *lane);
        for (outer_band_index, _) in available.clone() {
            let Some(outer) = inputs.band(outer_band_index) else {
                continue;
            };
            if outer
                .1
                .along
                .into_iter()
                .all(|end| (end - f64::from(along)).abs() > f64::from((p.core[1] - p.core[0]) / 2))
            {
                continue;
            }
            work = work.checked_sub(bands.len())?;
            for (inner_band_index, _) in available.clone() {
                let Some(inner) = inputs.band(inner_band_index) else {
                    continue;
                };
                let Some(f) = Frame::new(p, left, outer, inner) else {
                    continue;
                };
                if !valid_frame(f, doors) {
                    continue;
                }
                let (Some(c), Some(t)) = (f.course_bounds(), f.section_bounds(p)) else {
                    continue;
                };
                work = work.checked_sub(
                    (16 * (c[2] - c[0]) * (c[3] - c[1]) + (t[2] - t[0]) * (t[3] - t[1])) as usize,
                )?;
                let Some(course_patch) = s.capture(c) else {
                    continue;
                };
                let Some(quiet) = f.measure_course(&course_patch) else {
                    continue;
                };
                let Some(section_patch) = s.capture(t) else {
                    continue;
                };
                if f.verify_section(p, &r.patch, &course_patch, &section_patch, quiet)
                    .is_none()
                {
                    continue;
                }
                if let Some((first, _)) = choice {
                    ambiguous |= f.body() != first;
                    continue;
                }
                choice = Some((
                    f.body(),
                    (
                        f,
                        RasterSectionCourseReceipt {
                            previous_course_index,
                            outer_band_index,
                            inner_band_index,
                            course_patch,
                            section_patch,
                        },
                    ),
                ));
            }
        }
        if !ambiguous && let Some((_, entry)) = choice {
            results.push(entry);
        }
    }
    if results.len() > MAX_RECEIPTS {
        return None;
    }
    Some(
        results
            .iter()
            .enumerate()
            .filter(|&(i, (f, _))| {
                results
                    .iter()
                    .enumerate()
                    .all(|(j, (g, _))| i == j || !overlaps(f.body(), g.body()))
            })
            .map(|(_, (_, r))| r.clone())
            .collect(),
    )
}

pub(crate) struct VerifiedRasterSectionCourse {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub previous_start: Point,
    pub previous_end: Point,
    pub junction: [Point; 2],
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_section_courses(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterSectionCourse>, String> {
    let receipts = &info.raster_section_courses;
    if receipts.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || info.render_width == 0
        || info.render_height == 0
        || receipts.len() > MAX_RECEIPTS
        || receipts
            .windows(2)
            .any(|w| w[0].previous_course_index >= w[1].previous_course_index)
    {
        return Err("invalid raster section course inventory".into());
    }
    let verified = verified_narrow_courses(info, sources, page_width, page_height)?;
    let scale = [
        page_width / info.render_width as f64,
        page_height / info.render_height as f64,
    ];
    let inputs = Inputs {
        bands: &info.bands,
        jambs: &info.raster_jamb_stocks,
        doors: &info.raster_hinged_doors,
        previous: &info.raster_narrow_courses,
        measured: info
            .bands
            .iter()
            .map(|b| BandFootprint::read(b, scale))
            .collect(),
    };
    let error = || {
        "raster section course disagrees with its paired faces, adjacent material or crossing pen"
            .to_string()
    };
    let mut used = vec![];
    let mut output = vec![];
    for r in receipts {
        let (p, first) = inputs.previous(r.previous_course_index).ok_or_else(error)?;
        let old = verified.get(r.previous_course_index).ok_or_else(error)?;
        let (left, _) = inputs.band(first.inner_band_index).ok_or_else(error)?;
        let outer = inputs.band(r.outer_band_index).ok_or_else(error)?;
        let inner = inputs.band(r.inner_band_index).ok_or_else(error)?;
        let f = Frame::new(p, left, outer, inner).ok_or_else(error)?;
        let in_page = |[_, _, u, v]: [i32; 4]| {
            u as usize <= info.render_width && v as usize <= info.render_height
        };
        if !f.course_bounds().is_some_and(in_page)
            || !f.section_bounds(p).is_some_and(in_page)
            || !valid_frame(f, &info.raster_hinged_doors)
            || used.iter().any(|&o| overlaps(f.body(), o))
        {
            return Err(error());
        }
        let quiet = f.measure_course(&r.course_patch).ok_or_else(error)?;
        f.verify_section(p, &first.patch, &r.course_patch, &r.section_patch, quiet)
            .ok_or_else(error)?;
        used.push(f.body());
        let mid = f64::from(f.cross[0] + f.cross[1]) * 0.5;
        let xy = |a: i32| {
            if f.vertical {
                Point::new(mid * scale[0], f64::from(a) * scale[1])
            } else {
                Point::new(f64::from(a) * scale[0], mid * scale[1])
            }
        };
        let [start, end] = f.along.map(xy);
        let mut source_ids = old.source_ids.clone();
        source_ids.extend(inner.0.face_source_ids.iter().cloned());
        source_ids.sort_unstable();
        source_ids.dedup();
        output.push(VerifiedRasterSectionCourse {
            start,
            end,
            thickness: old.thickness,
            previous_start: old.start,
            previous_end: old.end,
            junction: [xy(f.previous_end), xy(f.along[f.near])],
            source_ids,
        });
    }
    Ok(output)
}
