//! A verified raster jamb can anchor a separately measured perpendicular core.
use super::{
    RasterBand, RasterHatch, THRESHOLD, hatch,
    hinged_doors::Samples,
    jamb_stocks::{BandFootprint, VerifiedRasterJambStock, verified_jamb_stocks},
    residual_hatch::Pixels,
};
use crate::{
    input::raster::{
        PdfInput, PdfRoute, RasterCoursePatch, RasterHingedDoorReceipt, RasterJambStockReceipt,
        RasterNarrowCourseReceipt,
    },
    model::{Point, Primitive},
};
mod aligned_course;
mod jamb_return;
mod section;
pub(crate) use aligned_course::{
    VerifiedRasterAlignedCourse, VerifiedRasterCornerCourse, verified_aligned_courses,
    verified_corner_courses,
};
pub(super) use aligned_course::{collect_aligned_courses, collect_corner_courses};
pub(super) use jamb_return::collect_jamb_returns;
pub(crate) use jamb_return::{VerifiedRasterJambReturn, verified_jamb_returns};
pub(super) use section::collect_sections;
pub(crate) use section::{VerifiedRasterSectionCourse, verified_section_courses};

const MAX_COURSES: usize = 16;
const MAX_PATCH: usize = 4_096;
const MAX_SIDE: i32 = 128;
const MAX_WORK: usize = 2_000_000;

fn native(v: f64) -> Option<i32> {
    (v.is_finite() && (0. ..=32_768.).contains(&v) && (v.round() - v).abs() <= 1e-7)
        .then_some(v.round() as i32)
}

fn range(values: [u32; 2]) -> Option<[i32; 2]> {
    (values[0] < values[1] && values[1] <= 32_768).then_some(values.map(|v| v as i32))
}

fn bounds(vertical: bool, a: [i32; 2], c: [i32; 2]) -> [i32; 4] {
    if vertical {
        [c[0], a[0], c[1], a[1]]
    } else {
        [a[0], c[0], a[1], c[1]]
    }
}

fn overlaps([x, y, u, v]: [i32; 4], [a, b, c, d]: [i32; 4]) -> bool {
    x.max(a) < u.min(c) && y.max(b) < v.min(d)
}

fn hatch_quality(h: &RasterHatch) -> bool {
    h.crossing_count >= 6
        && (2. ..=8.).contains(&h.mean_pitch_pixels)
        && h.diagonal_support >= 0.6
        && h.stationary_correlation <= -0.15
        && h.longitudinal_coverage >= 0.82
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Frame {
    vertical: bool,
    along: [i32; 2],
    core: [i32; 2],
    broad: [i32; 2],
    same_side: usize,
    pens: [i32; 2],
    contact: [i32; 2],
    pitch: f64,
}

impl Frame {
    fn new(
        jamb: &RasterJambStockReceipt,
        door: &RasterHingedDoorReceipt,
        broad: (&RasterBand, BandFootprint),
        inner: (&RasterBand, BandFootprint),
    ) -> Option<Self> {
        let (b, outer) = broad;
        let (n, narrow) = inner;
        let nh = n.hatch.as_ref().filter(|h| hatch_quality(h))?;
        let bh = b.hatch.as_ref().filter(|h| hatch_quality(h))?;
        let side = usize::from(!jamb.at_high_end);
        let other = 1 - side;
        if outer.vertical == door.vertical
            || narrow.vertical != outer.vertical
            || b.face_source_ids[side] != n.face_source_ids[side]
            || b.face_source_ids[other] == n.face_source_ids[other]
            || (nh.mean_pitch_pixels - bh.mean_pitch_pixels).abs() > 0.2 * nh.mean_pitch_pixels
        {
            return None;
        }

        let a = [native(outer.along[0])?, native(outer.along[1])?];
        let base = [native(outer.cross[0])?, native(outer.cross[1])?];
        let core = [native(narrow.cross[0])?, native(narrow.cross[1])?];
        let stok = range(jamb.along)?;
        let leaf = range(jamb.cross)?;
        let width = core[1] - core[0];
        let quiet = (base[other] - core[other]).abs() - 1;
        let length = a[1] - a[0];
        let [inner_start, inner_end] = [native(narrow.along[0])?, native(narrow.along[1])?];
        if core[side] != base[side]
            || core[side] != stok[other]
            || core[0] < base[0]
            || core[1] > base[1]
            || !(8..=40).contains(&width)
            || !(2..=12).contains(&quiet)
            || quiet * 2 > width
            || length < 48
            || length > MAX_SIDE
            || inner_start < a[0]
            || inner_end > a[1]
            || (inner_end - inner_start) * 2 < length
            || (inner_start - a[0]).max(a[1] - inner_end) > width * 2
        {
            return None;
        }

        // Only a terminal corner may borrow this authority. An interior crossing,
        // remote near-miss or long parallel shoulder is a different observation.
        let contact = [a[0].max(leaf[0]), a[1].min(leaf[1])];
        let shared = contact[1] - contact[0];
        if !(1..=(width / 4).max(1)).contains(&shared)
            || (a[0] == contact[0]) == (a[1] == contact[1])
        {
            return None;
        }
        let [p, q] = nh.face_ink_half_widths_pixels.map(|half| native(2. * half));
        let pens = [p?, q?];
        if pens.into_iter().any(|p| !(1..=4).contains(&p)) || width - pens[0] - pens[1] < 7 {
            return None;
        }
        Some(Self {
            vertical: outer.vertical,
            along: a,
            core,
            broad: base,
            same_side: side,
            pens,
            contact,
            pitch: nh.mean_pitch_pixels,
        })
    }

    fn body(self) -> [i32; 4] {
        bounds(self.vertical, self.along, self.core)
    }

    fn patch(self) -> Option<[i32; 4]> {
        let mut cross = self.broad;
        cross[self.same_side] += if self.same_side == 0 { -2 } else { 2 };
        let r = bounds(self.vertical, self.along, cross);
        let [x, y, u, v] = r;
        (x >= 0
            && y >= 0
            && (1..=MAX_SIDE).contains(&(u - x))
            && (1..=MAX_SIDE).contains(&(v - y))
            && ((u - x) * (v - y)) as usize <= MAX_PATCH)
            .then_some(r)
    }

    fn prove(self, patch: &RasterCoursePatch, jamb: &RasterJambStockReceipt) -> Option<()> {
        let bounds = self.patch()?;
        let samples = Samples::from_patch(patch, bounds)?;
        let [x, y, u, v] = bounds;
        let expected = jamb.patch.bounds.map(|p| p as i32);
        let [a, b, c, d] = expected;
        let old = Samples::from_patch(&jamb.patch, expected)?;
        if !overlaps(bounds, expected) {
            return None;
        }
        for yy in y.max(b)..v.min(d) {
            for xx in x.max(a)..u.min(c) {
                if samples.xy(xx, yy)? != old.xy(xx, yy)? {
                    return None;
                }
            }
        }

        let at = |a, c| samples.at(self.vertical, a, c);
        let plane = self.core[self.same_side];
        for along in self.contact[0]..self.contact[1] {
            for across in plane - 2..plane + 2 {
                if !at(along, across)?.1 {
                    return None;
                }
            }
        }

        let [lo, hi] = self.core;
        let [p, q] = self.pens;
        let other = 1 - self.same_side;
        // Test the quiet physical space between the inner pen and the distinct
        // facing pen. The facing has no authority to widen the inner stock.
        let (quiet, facing) = if other == 0 {
            ((self.broad[0] + 1)..lo, self.broad[0])
        } else {
            (hi..(self.broad[1] - 1), self.broad[1] - 1)
        };
        for along in self.along[0]..self.along[1] {
            for pen in [lo..lo + p, hi - q..hi] {
                if !(pen.clone())
                    .any(|across| at(along, across).is_some_and(|(l, _)| l < THRESHOLD))
                {
                    return None;
                }
            }
            if at(along, facing)?.0 >= THRESHOLD
                || quiet
                    .clone()
                    .any(|c| at(along, c).is_none_or(|(l, _)| l < THRESHOLD))
            {
                return None;
            }
        }

        let origin = if self.vertical { x } else { y };
        let axis = if self.vertical { y } else { x };
        let f = |a: i32, z: i32| {
            hatch::measure(
                &patch.luma,
                (u - x) as usize,
                (v - y) as usize,
                self.vertical,
                (a - axis) as usize,
                (z - axis) as usize,
                (lo + p - origin) as usize,
                (hi - q - origin) as usize,
                self.pens.map(|p| f64::from(p) * 0.5),
                None,
            )
            .is_some_and(|h| {
                hatch_quality(&h) && (h.mean_pitch_pixels - self.pitch).abs() <= 0.2 * self.pitch
            })
        };
        let [a, z] = self.along;
        let end_sample = ((self.pitch * 8.).ceil() as i32).max(28);
        (z - a > end_sample && f(a, z) && f(a, a + end_sample) && f(z - end_sample, z))
            .then_some(())
    }
}

fn protected(body: [i32; 4], doors: &[RasterHingedDoorReceipt]) -> bool {
    doors.iter().any(|d| {
        range(d.rough_jambs)
            .zip(range(d.cross))
            .is_none_or(|(a, c)| overlaps(body, bounds(d.vertical, a, c)))
    })
}

pub(super) fn collect(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
) -> Vec<RasterNarrowCourseReceipt> {
    collect_bounded(pixels, doors, jambs, bands).unwrap_or_default()
}

fn collect_bounded(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
) -> Option<Vec<RasterNarrowCourseReceipt>> {
    if jambs.is_empty() {
        return Some(vec![]);
    }
    let measured: Vec<_> = bands
        .iter()
        .map(|b| BandFootprint::read(b, pixels.scale))
        .collect();
    let samples = Samples::from_pixels(pixels);
    let mut work = MAX_WORK;
    let mut result = Vec::new();
    for (jamb_stock_index, jamb) in jambs.iter().enumerate() {
        let door = doors.get(jamb.door_index)?;
        let i = jamb.return_band_index;
        let Some(outer) = bands.get(i).zip(measured.get(i).and_then(|p| *p)) else {
            continue;
        };
        let mut choice: Option<(Frame, RasterNarrowCourseReceipt)> = None;
        let mut ambiguous = false;
        work = work.checked_sub(bands.len())?;
        for (inner_band_index, (b, p)) in bands.iter().zip(&measured).enumerate() {
            let Some(frame) = p.and_then(|p| Frame::new(jamb, door, outer, (b, p))) else {
                continue;
            };
            if protected(frame.body(), doors) {
                continue;
            }
            let Some(r) = frame.patch() else { continue };
            work = work.checked_sub((20 * (r[2] - r[0]) * (r[3] - r[1])) as usize)?;
            let Some(patch) = samples.capture(r) else {
                continue;
            };
            if frame.prove(&patch, jamb).is_none() {
                continue;
            }
            if let Some((first, _)) = choice.as_ref() {
                ambiguous |= frame.body() != first.body();
                continue;
            }
            choice = Some((
                frame,
                RasterNarrowCourseReceipt {
                    jamb_stock_index,
                    inner_band_index,
                    patch,
                },
            ));
        }
        if !ambiguous && let Some(row) = choice {
            result.push(row);
        }
    }
    if result.len() > MAX_COURSES {
        return None;
    }
    Some(
        result
            .iter()
            .enumerate()
            .filter(|&(i, (frame, _))| {
                result
                    .iter()
                    .enumerate()
                    .all(|(j, (other, _))| i == j || !overlaps(frame.body(), other.body()))
            })
            .map(|(_, (_, receipt))| receipt.clone())
            .collect(),
    )
}

pub(crate) struct VerifiedRasterNarrowCourse {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub source_ids: Vec<String>,
    pub jamb_start: Point,
    pub jamb_end: Point,
    pub jamb_thickness: f64,
    pub junction_end: Point,
    pub jamb_far_end: Point,
    pub contact: Point,
}

pub(crate) fn verified_narrow_courses(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterNarrowCourse>, String> {
    let receipts = &info.raster_narrow_courses;
    if receipts.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || info.render_width == 0
        || info.render_height == 0
        || receipts.len() > MAX_COURSES
        || receipts
            .windows(2)
            .any(|r| r[0].jamb_stock_index >= r[1].jamb_stock_index)
    {
        return Err("invalid raster narrow course inventory".into());
    }
    let error = || {
        "raster narrow course disagrees with its verified jamb, inner band or sampled material"
            .to_string()
    };
    let jambs: Vec<VerifiedRasterJambStock> =
        verified_jamb_stocks(info, sources, page_width, page_height)?;
    let scale = [
        page_width / info.render_width as f64,
        page_height / info.render_height as f64,
    ];
    let mut occupied = vec![];
    let mut result = Vec::new();
    for r in receipts {
        let j = info
            .raster_jamb_stocks
            .get(r.jamb_stock_index)
            .ok_or_else(error)?;
        let jamb = jambs.get(r.jamb_stock_index).ok_or_else(error)?;
        let door = info
            .raster_hinged_doors
            .get(j.door_index)
            .ok_or_else(error)?;
        let b = info.bands.get(j.return_band_index).ok_or_else(error)?;
        let n = info.bands.get(r.inner_band_index).ok_or_else(error)?;
        let f = Frame::new(
            j,
            door,
            (b, BandFootprint::read(b, scale).ok_or_else(error)?),
            (n, BandFootprint::read(n, scale).ok_or_else(error)?),
        )
        .ok_or_else(error)?;
        let [_, _, u, v] = f.patch().ok_or_else(error)?;
        if u as usize > info.render_width
            || v as usize > info.render_height
            || protected(f.body(), &info.raster_hinged_doors)
            || occupied.iter().any(|&b| overlaps(f.body(), b))
        {
            return Err(error());
        }
        f.prove(&r.patch, j).ok_or_else(error)?;
        occupied.push(f.body());
        let xy = |a: f64, c: f64| {
            if f.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let middle = |p: [i32; 2]| f64::from(p[0] + p[1]) * 0.5;
        let [start, end] = f.along.map(|a| xy(f64::from(a), middle(f.core)));
        let mut source_ids = jamb.source_ids.clone();
        source_ids.extend(n.face_source_ids.iter().cloned());
        source_ids.sort_unstable();
        source_ids.dedup();
        result.push(VerifiedRasterNarrowCourse {
            start,
            end,
            thickness: f64::from(f.core[1] - f.core[0]) * scale[usize::from(!f.vertical)],
            source_ids,
            jamb_start: jamb.start,
            jamb_end: jamb.end,
            jamb_thickness: jamb.thickness,
            junction_end: if f.along[0] == f.contact[0] {
                start
            } else {
                end
            },
            jamb_far_end: if j.at_high_end { jamb.end } else { jamb.start },
            contact: xy(middle(f.contact), f64::from(f.core[f.same_side])),
        });
    }
    Ok(result)
}
