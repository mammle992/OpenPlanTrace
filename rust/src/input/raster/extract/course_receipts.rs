//! Local positive raster evidence for a finite wall between a capped void and a return.
use super::residual_hatch::Pixels;
use crate::{
    input::raster::{PdfInput, PdfRoute, RasterBand, RasterCoursePatch, RasterCourseReceipt},
    model::Primitive,
};
use std::collections::BTreeMap;

const EPS: f64 = 1e-7;
const MAX_WORK: usize = 2_000_000;
const MAX_PATCH: usize = 4096;
const MAX_PATCH_WIDTH: usize = 128;
pub(crate) const MAX_COURSE_RECEIPTS: usize = 128;

#[derive(Clone, Copy)]
struct Span {
    vertical: bool,
    along: [f64; 2],
    cross: [f64; 2],
}

impl Span {
    fn from_band(b: &RasterBand, sx: f64, sy: f64) -> Option<Self> {
        let x = [b.start.x / sx, b.end.x / sx];
        let y = [b.start.y / sy, b.end.y / sy];
        let (vertical, along, center, half_width) = if eq(x[0], x[1]) && !eq(y[0], y[1]) {
            (true, y, x[0], b.thickness / (2. * sx))
        } else if eq(y[0], y[1]) && !eq(x[0], x[1]) {
            (false, x, y[0], b.thickness / (2. * sy))
        } else {
            return None;
        };
        (along.iter().all(|v| v.is_finite())
            && center.is_finite()
            && half_width.is_finite()
            && half_width > 0.)
            .then_some(Self {
                vertical,
                along: [along[0].min(along[1]), along[0].max(along[1])],
                cross: [center - half_width, center + half_width],
            })
    }

    fn rect(self) -> [f64; 4] {
        let [u, v] = self.along;
        let [a, b] = self.cross;
        if self.vertical {
            [a, u, b, v]
        } else {
            [u, a, v, b]
        }
    }

    fn width(self) -> f64 {
        self.cross[1] - self.cross[0]
    }

    fn length(self) -> f64 {
        self.along[1] - self.along[0]
    }
}

fn eq(a: f64, b: f64) -> bool {
    (a - b).abs() <= EPS
}

fn supported(b: &RasterBand) -> bool {
    b.boundary_coverage.iter().all(|v| *v >= 0.95)
        && b.hatch.as_ref().is_some_and(|h| {
            h.crossing_count >= 8
                && h.pitch_cv <= 0.35
                && h.diagonal_support >= 0.65
                && h.stationary_correlation <= -0.3
                && h.longitudinal_coverage >= 0.85
                && h.contrast >= 60.
                && h.mean_pitch_pixels >= 2.
        })
}

struct Geometry {
    spans: [Span; 3],
    common_side: usize,
    common_face: String,
}

impl Geometry {
    fn new(bands: [&RasterBand; 3], spans: [Span; 3]) -> Option<Self> {
        let [first, middle, last] = spans;
        if !bands.into_iter().all(supported)
            || first.vertical != middle.vertical
            || middle.vertical != last.vertical
            || !eq(first.cross[0], last.cross[0])
            || !eq(first.cross[1], last.cross[1])
            || first.along[0] >= middle.along[0]
            || middle.along[0] >= first.along[1]
            || first.along[1] >= last.along[0]
            || last.along[0] >= middle.along[1]
            || middle.along[1] >= last.along[1]
            || !(0.5 * first.width()..0.92 * first.width()).contains(&middle.width())
            || first.length() < 1.5 * first.width()
            || last.length() < 1.5 * last.width()
        {
            return None;
        }
        let mut common = bands[0]
            .face_source_ids
            .iter()
            .filter(|id| bands[1..].iter().all(|b| b.face_source_ids.contains(id)));
        let id = common.next()?;
        if common.next().is_some() {
            return None;
        }
        // RasterBand source IDs follow low-to-high transverse geometry. A match
        // must keep that authored face on one side in all three original bands.
        let side = bands[0].face_source_ids.iter().position(|i| i == id)?;
        if !bands[1..].iter().all(|b| &b.face_source_ids[side] == id)
            || !eq(middle.cross[side], first.cross[side])
            || middle.cross[0] < first.cross[0] - EPS
            || middle.cross[1] > first.cross[1] + EPS
        {
            return None;
        }
        let lengths = [first.length(), last.length()];
        let middle_length = last.along[0] - first.along[1];
        if middle_length > middle.width() || lengths.iter().any(|n| *n <= middle_length) {
            return None;
        }
        Some(Self {
            spans: [
                first,
                Span {
                    along: [first.along[1], last.along[0]],
                    ..middle
                },
                last,
            ],
            common_side: side,
            common_face: id.clone(),
        })
    }
}

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    along: [usize; 2],
    cross: [usize; 2],
}

impl Frame {
    fn bounds(self) -> [u32; 4] {
        let [a, b] = self.along.map(|n| n as u32);
        let [x, y] = self.cross.map(|n| n as u32);
        if self.vertical {
            [x, a, y, b]
        } else {
            [a, x, b, y]
        }
    }

    fn len(self) -> Option<usize> {
        let a = self.along[1].checked_sub(self.along[0])?;
        let b = self.cross[1].checked_sub(self.cross[0])?;
        (a > 0 && b > 0 && a <= MAX_PATCH_WIDTH && b <= MAX_PATCH_WIDTH && a * b <= MAX_PATCH)
            .then_some(a * b)
    }

    fn index(self, along: usize, across: usize) -> usize {
        if self.vertical {
            (along - self.along[0]) * (self.cross[1] - self.cross[0]) + across - self.cross[0]
        } else {
            (across - self.cross[0]) * (self.along[1] - self.along[0]) + along - self.along[0]
        }
    }

    fn samples(self, patch: &RasterCoursePatch, threshold: u8) -> bool {
        let Some(len) = self.len() else {
            return false;
        };
        patch.bounds == self.bounds()
            && patch.luma.len() == len
            && patch.neutral_ink.len() == len
            && patch
                .neutral_ink
                .iter()
                .zip(&patch.luma)
                .all(|(&ink, &luma)| !ink || luma < threshold)
    }

    fn inside_image(self, width: usize, height: usize) -> bool {
        let (x, y) = if self.vertical {
            (self.cross[1], self.along[1])
        } else {
            (self.along[1], self.cross[1])
        };
        self.len().is_some()
            && x <= width
            && y <= height
            && x <= u32::MAX as usize
            && y <= u32::MAX as usize
    }
}

fn native_samples(range: [f64; 2]) -> Option<[usize; 2]> {
    if !range.iter().all(|p| p.is_finite()) || range[0] < -EPS || range[1] > u32::MAX as f64 {
        return None;
    }
    let lo = (range[0] - EPS).ceil() as usize;
    let hi = (range[1] + EPS).floor() as usize;
    (lo < hi).then_some([lo, hi])
}

#[derive(Clone, Copy)]
struct Terminals {
    cap: Frame,
    plane: usize,
    cap_low: bool,
    shoulder: Frame,
    gap: [usize; 2],
}

struct ShortHatch {
    frame: Frame,
    piece: [usize; 2],
    pitch: f64,
}

impl ShortHatch {
    fn new(parent: Span, piece: Span, band: &RasterBand) -> Option<Self> {
        let proof = band.hatch.as_ref()?;
        let [lo, hi] = native_samples(piece.along)?;
        let pad = (proof.mean_pitch_pixels * 2.).ceil().clamp(4., 24.) as usize;
        let limits = native_samples(parent.along)?;
        let along = [
            lo.saturating_sub(pad).max(limits[0]),
            hi.saturating_add(pad).min(limits[1]),
        ];
        // Exclude the face-colored columns and keep only the distinct interior.
        let cross = native_samples([
            piece.cross[0] + proof.face_ink_half_widths_pixels[0] + 1.,
            piece.cross[1] - proof.face_ink_half_widths_pixels[1] - 1.,
        ])?;
        let frame = Frame {
            vertical: piece.vertical,
            along,
            cross,
        };
        (frame.len().is_some() && along[1] - along[0] >= 24 && cross[1] - cross[0] >= 7).then_some(
            Self {
                frame,
                piece: [lo, hi],
                pitch: proof.mean_pitch_pixels,
            },
        )
    }

    fn prove(&self, patch: &RasterCoursePatch, threshold: u8) -> bool {
        let f = self.frame;
        if !f.samples(patch, threshold) {
            return false;
        }
        let height = f.along[1] - f.along[0];
        let mut low = f.cross[0];
        let mut widest = [low, low];
        for x in f.cross[0]..f.cross[1] {
            let mut values = (f.along[0]..f.along[1])
                .map(|t| patch.luma[f.index(t, x)])
                .collect::<Vec<_>>();
            values.sort_unstable();
            if values[height * 9 / 10] - values[height / 10] >= 48 {
                if x + 1 - low > widest[1] - widest[0] {
                    widest = [low, x + 1];
                }
            } else {
                low = x + 1;
            }
        }
        if widest[1] - widest[0] < 7 || (widest[1] - widest[0]) * 2 < f.cross[1] - f.cross[0] {
            return false;
        }
        let middle = (widest[0] + widest[1]) / 2;
        let average = |t: usize| {
            u16::from(patch.luma[f.index(t, middle - 1)])
                + u16::from(patch.luma[f.index(t, middle)])
                + u16::from(patch.luma[f.index(t, middle + 1)])
        };
        let mut main = (f.along[0]..f.along[1]).map(average).collect::<Vec<_>>();
        main.sort_unstable();
        let span = main[height * 9 / 10] - main[height / 10];
        if span < 3 * 32 {
            return false;
        }
        let boundary = (main[height * 9 / 10] + main[height / 10]) / 2;
        let [start, end] = self.piece;
        let mut runs = vec![];
        let mut active = None;
        for t in start..=end {
            if t < end && average(t) < boundary {
                active.get_or_insert(t);
            } else if let Some(lo) = active.take() {
                runs.push([lo, t]);
            }
        }
        let pause = |length: usize| length as f64 <= self.pitch * 1.75;
        let typical = runs
            .windows(2)
            .filter(|w| {
                let shift = ((w[1][0] + w[1][1]) as f64 - (w[0][0] + w[0][1]) as f64) * 0.5;
                (0.5 * self.pitch..=1.6 * self.pitch).contains(&shift)
            })
            .count();
        runs.len() >= 3
            && typical >= 2
            && pause(runs[0][0] - start)
            && pause(end - runs[runs.len() - 1][1])
            && runs.windows(2).all(|w| pause(w[1][0] - w[0][1]))
    }
}

impl Terminals {
    fn new(geometry: &Geometry, return_span: Span, cap_low: bool, cap_pitch: f64) -> Option<Self> {
        let near = geometry.spans[if cap_low { 0 } else { 2 }];
        let far = geometry.spans[if cap_low { 2 } else { 0 }];
        if return_span.vertical == near.vertical || return_span.length() < 2. * return_span.width()
        {
            return None;
        }
        let side = geometry.common_side;
        let return_tip = return_span.along[1 - side];
        let inner_face = far.cross[side];
        let offset = if side == 0 {
            inner_face - return_tip
        } else {
            return_tip - inner_face
        };
        if offset < -EPS
            || offset > far.width().min(return_span.width()) * 0.17
            || !cap_pitch.is_finite()
        {
            return None;
        }
        let terminal = far.along[usize::from(cap_low)];
        if terminal < return_span.cross[0] - EPS || terminal > return_span.cross[1] + EPS {
            return None;
        }
        let overlap = [
            return_span.cross[0].max(far.along[0]),
            return_span.cross[1].min(far.along[1]),
        ];
        if overlap[1] - overlap[0] < return_span.width() * 0.3 {
            return None;
        }
        // Sample either side of the actual cross-junction, separate from the tip gap.
        let grip = (return_span.width() * 0.17).floor().clamp(2., 6.);
        let bridge_cross = native_samples([
            inner_face.min(return_tip) - grip,
            inner_face.max(return_tip) + grip,
        ])?;
        let gap = if eq(offset, 0.) {
            let at = inner_face.round() as usize;
            [at.checked_sub(1)?, at + 1]
        } else {
            native_samples([inner_face.min(return_tip), inner_face.max(return_tip)])?
        };
        let near_plane = near.along[usize::from(!cap_low)];
        if !eq(near_plane, near_plane.round())
            || near_plane < 0.
            || near_plane > (u32::MAX - 64) as f64
        {
            return None;
        }
        let outside = (cap_pitch * 3.0).ceil().clamp(8., 32.) as usize;
        let plane = near_plane.round() as usize;
        let end_rows = if cap_low {
            [plane.checked_sub(outside)?, plane + 3]
        } else {
            [plane.checked_sub(3)?, plane + outside + 1]
        };
        Some(Self {
            cap: Frame {
                vertical: near.vertical,
                along: end_rows,
                cross: native_samples(near.cross)?,
            },
            plane,
            cap_low,
            shoulder: Frame {
                vertical: near.vertical,
                along: native_samples(overlap)?,
                cross: bridge_cross,
            },
            gap,
        })
    }

    fn prove_cap(self, patch: &RasterCoursePatch, threshold: u8) -> bool {
        let c = self.cap;
        if !c.samples(patch, threshold) {
            return false;
        }
        let paint = |row: usize, x: usize| patch.luma[c.index(row, x)] < threshold;
        let [lo, hi] = c.cross;
        let width = hi - lo;
        let contact = self.plane - usize::from(!self.cap_low);
        let near_row = |row: usize| {
            let mut best = 0;
            let mut in_run = 0;
            let mut neutral = 0;
            for x in lo..hi {
                in_run = if paint(row, x) { in_run + 1 } else { 0 };
                best = best.max(in_run);
                neutral += usize::from(patch.neutral_ink[c.index(row, x)]);
            }
            best * 100 >= width * 85 && neutral * 100 >= width * 40
        };
        if ![contact - 1, contact, contact + 1]
            .into_iter()
            .any(near_row)
        {
            return false;
        }
        let outside = if self.cap_low {
            c.along[0]..self.plane - 4
        } else {
            self.plane + 4..c.along[1]
        };
        let center_lo = lo + width / 5;
        let center_hi = hi - width / 5;
        let center_width = center_hi - center_lo;
        let mut dark = 0;
        let mut sample = 0;
        for row in outside {
            let tone = (center_lo..center_hi).filter(|&x| paint(row, x)).count();
            if tone * 100 >= center_width * 75 {
                return false;
            }
            dark += tone;
            sample += center_width;
        }
        sample > 0 && dark * 100 <= sample * 35
    }

    fn prove_shoulder(self, patch: &RasterCoursePatch, threshold: u8) -> bool {
        let r = self.shoulder;
        if !r.samples(patch, threshold) || self.gap[0] < r.cross[0] || self.gap[1] > r.cross[1] {
            return false;
        }
        let mut run = 0;
        let mut best = 0;
        for row in r.along[0]..r.along[1] {
            let continuous =
                (r.cross[0]..r.cross[1]).all(|x| patch.luma[r.index(row, x)] < threshold);
            let shoulder = (self.gap[0]..self.gap[1]).all(|x| patch.neutral_ink[r.index(row, x)]);
            run = if continuous && shoulder { run + 1 } else { 0 };
            best = best.max(run);
        }
        best >= 3 && best * 100 >= (r.along[1] - r.along[0]) * 55
    }
}

pub(crate) struct VerifiedRasterCourse {
    pub sections: [[f64; 4]; 3],
    pub common_face: String,
    pub cap_at_low_end: bool,
}

pub(crate) fn verified_course(
    info: &PdfInput,
    page_width: f64,
    page_height: f64,
    receipt: &RasterCourseReceipt,
) -> Option<VerifiedRasterCourse> {
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.render_width == 0
        || info.render_height == 0
        || info.threshold != super::THRESHOLD
    {
        return None;
    }
    let sx = page_width / info.render_width as f64;
    let sy = page_height / info.render_height as f64;
    let [a, b, c] = receipt.band_indices;
    let bands = [
        &*info.bands.get(a)?,
        &*info.bands.get(b)?,
        &*info.bands.get(c)?,
    ];
    let spans = [
        Span::from_band(bands[0], sx, sy)?,
        Span::from_band(bands[1], sx, sy)?,
        Span::from_band(bands[2], sx, sy)?,
    ];
    let geometry = Geometry::new(bands, spans)?;
    let middle = ShortHatch::new(spans[1], geometry.spans[1], bands[1])?;
    if !middle
        .frame
        .inside_image(info.render_width, info.render_height)
        || !middle.prove(&receipt.middle, info.threshold)
    {
        return None;
    }
    let return_band = info.bands.get(receipt.return_band_index)?;
    if !independent_return(bands, return_band) {
        return None;
    }
    let far = Span::from_band(return_band, sx, sy)?;
    let verify = |cap_low| {
        let cap_band = bands[if cap_low { 0 } else { 2 }];
        Terminals::new(
            &geometry,
            far,
            cap_low,
            cap_band.hatch.as_ref()?.mean_pitch_pixels,
        )
        .filter(|t| {
            t.cap.inside_image(info.render_width, info.render_height)
                && t.shoulder
                    .inside_image(info.render_width, info.render_height)
                && t.prove_cap(&receipt.cap, info.threshold)
                && t.prove_shoulder(&receipt.shoulder, info.threshold)
        })
    };
    let cap_at_low_end = match (verify(true), verify(false)) {
        (Some(_), None) => true,
        (None, Some(_)) => false,
        _ => return None,
    };
    Some(VerifiedRasterCourse {
        sections: geometry.spans.map(Span::rect),
        common_face: geometry.common_face,
        cap_at_low_end,
    })
}

fn independent_return(bands: [&RasterBand; 3], r: &RasterBand) -> bool {
    supported(r)
        && r.face_source_ids
            .iter()
            .all(|id| bands.iter().all(|b| !b.face_source_ids.contains(id)))
}

fn patch(pixels: &Pixels<'_>, frame: Frame) -> Option<RasterCoursePatch> {
    if !frame.inside_image(pixels.width, pixels.height) {
        return None;
    }
    let bounds = frame.bounds();
    let [x0, y0, x1, y1] = bounds.map(|v| v as usize);
    let len = frame.len()?;
    let mut luma = Vec::with_capacity(len);
    let mut neutral_ink = Vec::with_capacity(len);
    for y in y0..y1 {
        for x in x0..x1 {
            let at = y * pixels.width + x;
            let eligible = pixels.mask.is_none_or(|m| m[at]);
            luma.push(if eligible { *pixels.luma.get(at)? } else { 255 });
            neutral_ink.push(eligible && *pixels.ink.get(at)?);
        }
    }
    Some(RasterCoursePatch {
        bounds,
        luma,
        neutral_ink,
    })
}

pub(super) fn derive(
    pixels: &Pixels<'_>,
    bands: &[RasterBand],
    _primitives: &[Primitive],
) -> Vec<RasterCourseReceipt> {
    collect(pixels, bands).unwrap_or_default()
}

fn collect(pixels: &Pixels<'_>, bands: &[RasterBand]) -> Option<Vec<RasterCourseReceipt>> {
    let threshold = super::THRESHOLD;
    let mut work = MAX_WORK;
    let mut groups = BTreeMap::<&str, Vec<usize>>::new();
    let [sx, sy] = pixels.scale;
    let spans = bands
        .iter()
        .map(|b| {
            if supported(b) {
                Span::from_band(b, sx, sy)
            } else {
                None
            }
        })
        .collect::<Vec<_>>();
    let mut returns = Vec::new();
    for (i, b) in bands.iter().enumerate() {
        if spans[i].is_none() {
            continue;
        }
        returns.push(i);
        for id in &b.face_source_ids {
            groups.entry(id).or_default().push(i);
        }
    }
    let mut result = Vec::new();
    for ids in groups.values() {
        work = work.checked_sub(ids.len().saturating_pow(2))?;
        for &i in ids {
            let first = spans[i]?;
            for &j in ids {
                let last = spans[j]?;
                if first.vertical != last.vertical
                    || !eq(first.cross[0], last.cross[0])
                    || !eq(first.cross[1], last.cross[1])
                    || first.along[1] >= last.along[0]
                    || last.along[0] - first.along[1] > first.width()
                {
                    continue;
                }
                work = work.checked_sub(ids.len())?;
                for &k in ids {
                    let authorities = [&bands[i], &bands[k], &bands[j]];
                    let Some(g) = Geometry::new(authorities, [first, spans[k]?, last]) else {
                        continue;
                    };
                    let Some(short) = ShortHatch::new(spans[k]?, g.spans[1], &bands[k]) else {
                        continue;
                    };
                    work = work.checked_sub(short.frame.len()?.saturating_mul(8))?;
                    let Some(middle) = patch(pixels, short.frame) else {
                        continue;
                    };
                    if !short.prove(&middle, threshold) {
                        continue;
                    }
                    work = work.checked_sub(returns.len())?;
                    let mut matches = Vec::new();
                    for &r in &returns {
                        let ret = spans[r]?;
                        if ret.vertical == first.vertical
                            || !independent_return(authorities, &bands[r])
                        {
                            continue;
                        }
                        for cap_low in [true, false] {
                            let cap_band = authorities[if cap_low { 0 } else { 2 }];
                            let Some(t) = Terminals::new(
                                &g,
                                ret,
                                cap_low,
                                cap_band.hatch.as_ref()?.mean_pitch_pixels,
                            ) else {
                                continue;
                            };
                            let (Some(cap_len), Some(shoulder_len)) =
                                (t.cap.len(), t.shoulder.len())
                            else {
                                continue;
                            };
                            work = work.checked_sub(cap_len.saturating_add(shoulder_len))?;
                            let Some(cap) = patch(pixels, t.cap) else {
                                continue;
                            };
                            if !t.prove_cap(&cap, threshold) {
                                continue;
                            }
                            let Some(shoulder) = patch(pixels, t.shoulder) else {
                                continue;
                            };
                            if !t.prove_shoulder(&shoulder, threshold) {
                                continue;
                            }
                            matches.push(RasterCourseReceipt {
                                band_indices: [i, k, j],
                                return_band_index: r,
                                cap,
                                shoulder,
                                middle: middle.clone(),
                            });
                        }
                    }
                    if matches.len() == 1 {
                        result.append(&mut matches);
                    }
                    if result.len() > MAX_COURSE_RECEIPTS {
                        return None;
                    }
                }
            }
        }
    }
    result.sort_unstable_by_key(|r| (r.band_indices, r.return_band_index));
    result.dedup_by_key(|r| (r.band_indices, r.return_band_index));
    let mut caps = BTreeMap::new();
    let mut returns = BTreeMap::new();
    for r in &result {
        *caps.entry(r.cap.bounds).or_insert(0) += 1;
        *returns.entry(r.shoulder.bounds).or_insert(0) += 1;
    }
    result.retain(|r| caps[&r.cap.bounds] == 1 && returns[&r.shoulder.bounds] == 1);
    Some(result)
}
