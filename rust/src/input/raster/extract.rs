//! Pixel-supported line faces and material-band measurements.
//!
//! No OCR result, file name or drawing coordinate grants structural authority.
//! The measurements below remain observations: placement must corroborate them.
use super::{RasterBand, RasterHatch};
use crate::model::{Point, Primitive, Shape};
mod course_receipts;
mod face_continuation;
mod hatch;
mod hinged_doors;
mod jamb_stocks;
mod narrow_courses;
mod residual_hatch;
mod rule_matrix;
pub(crate) use course_receipts::{MAX_COURSE_RECEIPTS, VerifiedRasterCourse, verified_course};
pub(crate) use face_continuation::verified_face_continuations;
pub(crate) use hinged_doors::verified_hinged_doors;
pub(crate) use jamb_stocks::verified_jamb_stocks;
pub(crate) use narrow_courses::{
    VerifiedRasterJambReturn, VerifiedRasterNarrowCourse, VerifiedRasterSectionCourse,
    verified_jamb_returns, verified_narrow_courses, verified_section_courses,
};

pub struct Extracted {
    pub primitives: Vec<Primitive>,
    pub bands: Vec<RasterBand>,
    pub threshold: u8,
    /// The exact band prefix to retain if the document budget drops recovery.
    pub(super) bands_before_residuals: usize,
    pub(super) raster_course_receipts: Vec<super::RasterCourseReceipt>,
    pub(super) raster_face_continuations: Vec<super::RasterFaceContinuationReceipt>,
    pub(super) raster_hinged_doors: Vec<super::RasterHingedDoorReceipt>,
    pub(super) raster_jamb_stocks: Vec<super::RasterJambStockReceipt>,
    pub(super) raster_narrow_courses: Vec<super::RasterNarrowCourseReceipt>,
    pub(super) raster_section_courses: Vec<super::RasterSectionCourseReceipt>,
    pub(super) raster_jamb_returns: Vec<super::RasterJambReturnReceipt>,
}

const THRESHOLD: u8 = 174;
const MAX_PIXELS: usize = 32 * 1024 * 1024;
const MAX_FACES: usize = 12_000;
const MAX_BANDS: usize = 12_000;

/// Existing dense-material tone evidence, shared with placement. Recovered
/// faces need this evidence before they can add a non-hatch observation.
pub(crate) fn dense_material_tone(dark_fraction: f64, mean_luma: f64) -> bool {
    dark_fraction >= 0.28 && mean_luma <= 220. || mean_luma <= 175.
}

#[derive(Clone, Copy, Debug)]
struct Face {
    vertical: bool,
    coordinate: f64,
    start: f64,
    end: f64,
    // Measured half-width of a thin source stroke. Its ink cannot witness
    // material between two separate faces (e.g. a hollow doorway/header).
    ink_half_width: f64,
}

#[derive(Debug)]
struct Stripe {
    first: usize,
    last: usize,
    starts: Vec<usize>,
    ends: Vec<usize>,
}

fn median(values: &mut [usize]) -> usize {
    let k = values.len() / 2;
    values.select_nth_unstable(k);
    values[k]
}

fn edge_support(
    ink: &[bool],
    width: usize,
    height: usize,
    vertical: bool,
    inside: usize,
    outside: Option<usize>,
    start: usize,
    end: usize,
) -> f64 {
    let at = |along: usize, across: usize| {
        ink[if vertical {
            along * width + across
        } else {
            across * width + along
        }]
    };
    let across_limit = if vertical { width } else { height };
    let outside = outside.filter(|v| *v < across_limit);
    let n = end.saturating_sub(start);
    if n == 0 {
        return 0.;
    }
    (start..end)
        .filter(|&along| at(along, inside) && outside.is_none_or(|v| !at(along, v)))
        .count() as f64
        / n as f64
}

/// Recover only a rejected thin pen whose directly observed outer fringe is
/// one pixel wider on some scanlines. The extra ink is part of the pen, not
/// material between two faces. This does not apply to a broad filled stripe.
#[allow(clippy::too_many_arguments)]
fn fringed_thin_face(
    ink: &[bool],
    width: usize,
    height: usize,
    vertical: bool,
    first: usize,
    last: usize,
    start: usize,
    end: usize,
    thin_limit: f64,
) -> Option<Face> {
    let limit = if vertical { width } else { height };
    let left = first.checked_sub(2).is_some_and(|outside| {
        edge_support(
            ink,
            width,
            height,
            vertical,
            first,
            Some(outside),
            start,
            end,
        ) >= 0.80
    });
    let right = last
        .checked_add(2)
        .filter(|v| *v < limit)
        .is_some_and(|outside| {
            edge_support(
                ink,
                width,
                height,
                vertical,
                last,
                Some(outside),
                start,
                end,
            ) >= 0.80
        });
    if !left && !right {
        return None;
    }
    let first = first - usize::from(left);
    let last = last + usize::from(right);
    let thickness = (last - first + 1) as f64;
    (thickness < thin_limit).then_some(Face {
        vertical,
        coordinate: (first + last + 1) as f64 * 0.5,
        start: start as f64,
        end: end as f64,
        ink_half_width: thickness * 0.5,
    })
}

fn parallel_bands_overlap(a: &RasterBand, b: &RasterBand) -> bool {
    let vertical = a.start.x == a.end.x;
    if vertical != (b.start.x == b.end.x) {
        return false;
    }
    let (normal_a, normal_b, a0, a1, b0, b1) = if vertical {
        (a.start.x, b.start.x, a.start.y, a.end.y, b.start.y, b.end.y)
    } else {
        (a.start.y, b.start.y, a.start.x, a.end.x, b.start.x, b.end.x)
    };
    (normal_a - normal_b).abs() < (a.thickness + b.thickness) * 0.5
        && a0.min(a1).max(b0.min(b1)) < a0.max(a1).min(b0.max(b1))
}

/// Blocked prefix sums keep exact pixel measurements with substantially less
/// storage than a full-page integral image. Only a short remainder is sampled
/// for each cross-section; buffers are released between orientations.
struct CrossSums<'a> {
    ink: &'a [bool],
    luma: &'a [u8],
    width: usize,
    vertical: bool,
    stride: usize,
    sums: Vec<[u32; 2]>,
}
impl<'a> CrossSums<'a> {
    const BLOCK: usize = 8;
    fn new(ink: &'a [bool], luma: &'a [u8], width: usize, height: usize, vertical: bool) -> Self {
        let (major, minor) = if vertical {
            (height, width)
        } else {
            (width, height)
        };
        let stride = minor / Self::BLOCK + 1;
        let mut sums = vec![[0, 0]; major * stride];
        for along in 0..major {
            let mut value = [0, 0];
            for across in 0..minor {
                let index = if vertical {
                    along * width + across
                } else {
                    across * width + along
                };
                value[0] += u32::from(ink[index]);
                value[1] += luma[index] as u32;
                if (across + 1) % Self::BLOCK == 0 {
                    sums[along * stride + (across + 1) / Self::BLOCK] = value;
                }
            }
        }
        Self {
            ink,
            luma,
            width,
            vertical,
            stride,
            sums,
        }
    }
    fn sum(&self, along: usize, lo: usize, hi: usize) -> [u32; 2] {
        let first = lo.div_ceil(Self::BLOCK) * Self::BLOCK;
        let last = hi / Self::BLOCK * Self::BLOCK;
        let mut value = [0, 0];
        let mut sample = |across| {
            let index = if self.vertical {
                along * self.width + across
            } else {
                across * self.width + along
            };
            value[0] += u32::from(self.ink[index]);
            value[1] += self.luma[index] as u32;
        };
        if first >= last {
            for across in lo..hi {
                sample(across);
            }
            return value;
        }
        for across in lo..first {
            sample(across);
        }
        for across in last..hi {
            sample(across);
        }
        let a = self.sums[along * self.stride + first / Self::BLOCK];
        let b = self.sums[along * self.stride + last / Self::BLOCK];
        [value[0] + b[0] - a[0], value[1] + b[1] - a[1]]
    }
}

/// Extract orthogonal faces at the actual rendered scale. `region_mask` limits
/// eligibility to painted image footprints on a mixed page. White compositing,
/// image masks and clipping belong to the renderer, before this operation.
/// Thin-fringe recovery is an explicit pure-raster opt-in. Mixed pages retain
/// the established face interpretation beside their authoritative vectors.
#[cfg(test)]
pub fn extract(
    width: usize,
    height: usize,
    rgba: &[u8],
    page_width: f64,
    page_height: f64,
    region_mask: Option<&[bool]>,
    recover_thin_fringe: bool,
) -> Result<Extracted, String> {
    extract_with_residual_hatch(
        width,
        height,
        rgba,
        page_width,
        page_height,
        region_mask,
        recover_thin_fringe,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
        false,
    )
}

#[allow(clippy::too_many_arguments)]
pub(super) fn extract_with_residual_hatch(
    width: usize,
    height: usize,
    rgba: &[u8],
    page_width: f64,
    page_height: f64,
    region_mask: Option<&[bool]>,
    recover_thin_fringe: bool,
    recover_residual_hatch: bool,
    recover_course_receipts: bool,
    recover_face_continuations: bool,
    recover_hinged_doors: bool,
    recover_jamb_stocks: bool,
    recover_narrow_courses: bool,
    recover_section_courses: bool,
    recover_jamb_returns: bool,
) -> Result<Extracted, String> {
    let recover_residual_hatch = recover_thin_fringe && recover_residual_hatch;
    let mut stage = crate::stage_profile::scope("raster_luma_mask");
    let pixels = width
        .checked_mul(height)
        .filter(|n| *n > 0 && *n <= MAX_PIXELS)
        .ok_or("Raster geometry requires between 1 and 32 megapixels")?;
    if rgba.len() != pixels * 4
        || region_mask.is_some_and(|m| m.len() != pixels)
        || !page_width.is_finite()
        || !page_height.is_finite()
        || page_width <= 0.
        || page_height <= 0.
    {
        return Err("Raster geometry has inconsistent pixels, mask or page dimensions".into());
    }
    let mut luma = Vec::with_capacity(pixels);
    let mut ink = Vec::with_capacity(pixels);
    for (i, p) in rgba.chunks_exact(4).enumerate() {
        // Transparent inputs are composited here too, so the public API cannot
        // mistake invisible RGB data for source ink.
        let alpha = p[3] as u32;
        let gray = (77 * p[0] as u32 + 150 * p[1] as u32 + 29 * p[2] as u32 + 128) >> 8;
        let value = ((gray * alpha + 255 * (255 - alpha) + 127) / 255) as u8;
        let spread = p[..3].iter().max().unwrap() - p[..3].iter().min().unwrap();
        let eligible = region_mask.is_none_or(|m| m[i]);
        luma.push(value);
        // Colored room fills and highlighting cannot create a wall face.
        ink.push(
            eligible && value < THRESHOLD && (spread <= 25 || p[..3].iter().all(|v| *v <= 95)),
        );
    }
    let min_run = ((width.min(height) as f64 * 0.007).round() as usize).max(12);
    let max_width = (width.min(height) as f64 * 0.030).max(8.);
    stage.next("raster_stripe_faces");
    let mut faces = Vec::new();
    let mut recovered_faces = Vec::new();
    for vertical in [false, true] {
        let stripes = stripes(&ink, width, height, vertical, min_run)?;
        let mut thin_widths: Vec<_> = stripes
            .iter()
            .filter(|s| {
                let start = *s.starts.iter().max().unwrap();
                let end = *s.ends.iter().min().unwrap();
                start < end
                    && (edge_support(
                        &ink,
                        width,
                        height,
                        vertical,
                        s.first,
                        s.first.checked_sub(1),
                        start,
                        end,
                    ) >= 0.80
                        || edge_support(
                            &ink,
                            width,
                            height,
                            vertical,
                            s.last,
                            Some(s.last + 1),
                            start,
                            end,
                        ) >= 0.80)
            })
            .map(|s| s.last - s.first + 1)
            .filter(|n| *n <= 8)
            .collect();
        let pen = if thin_widths.is_empty() {
            1
        } else {
            median(&mut thin_widths)
        };
        for mut stripe in stripes {
            let a = median(&mut stripe.starts) as f64;
            let b = median(&mut stripe.ends) as f64;
            if b - a < min_run as f64 {
                continue;
            }
            let thickness = (stripe.last - stripe.first + 1) as f64;
            if thickness > max_width {
                continue;
            }
            if thickness >= (pen as f64 * 2.5).max(4.) {
                // A thick sustained run has two measurable outer boundaries;
                // this does not assert that everything between them is a wall.
                for (coordinate, inside, outside) in [
                    (
                        stripe.first as f64,
                        stripe.first,
                        stripe.first.checked_sub(1),
                    ),
                    ((stripe.last + 1) as f64, stripe.last, Some(stripe.last + 1)),
                ] {
                    // A run-length change inside a broad solid fill is not a
                    // source edge. Require the actual outward pixel transition.
                    if edge_support(
                        &ink, width, height, vertical, inside, outside, a as usize, b as usize,
                    ) < 0.80
                    {
                        continue;
                    }
                    faces.push(Face {
                        vertical,
                        coordinate,
                        start: a,
                        end: b,
                        ink_half_width: 0.,
                    });
                }
            } else {
                // A thin face needs background on its outward side. The other
                // side may contain real wall material or diagonal insulation.
                // An internal run-length split in a solid slab has neither.
                if edge_support(
                    &ink,
                    width,
                    height,
                    vertical,
                    stripe.first,
                    stripe.first.checked_sub(1),
                    a as usize,
                    b as usize,
                ) < 0.80
                    && edge_support(
                        &ink,
                        width,
                        height,
                        vertical,
                        stripe.last,
                        Some(stripe.last + 1),
                        a as usize,
                        b as usize,
                    ) < 0.80
                {
                    if recover_thin_fringe
                        && let Some(face) = fringed_thin_face(
                            &ink,
                            width,
                            height,
                            vertical,
                            stripe.first,
                            stripe.last,
                            a as usize,
                            b as usize,
                            (pen as f64 * 2.5).max(4.),
                        )
                    {
                        if recovered_faces.len() < MAX_FACES {
                            recovered_faces.push(face);
                        }
                    }
                    continue;
                }
                faces.push(Face {
                    vertical,
                    coordinate: (stripe.first + stripe.last + 1) as f64 * 0.5,
                    start: a,
                    end: b,
                    ink_half_width: thickness * 0.5,
                });
            }
            if faces.len() > MAX_FACES {
                return Err("Raster page exceeds 12000 sustained line faces".into());
            }
        }
    }
    faces.sort_by(|a, b| {
        a.vertical
            .cmp(&b.vertical)
            .then(a.coordinate.total_cmp(&b.coordinate))
            .then(a.start.total_cmp(&b.start))
            .then(a.end.total_cmp(&b.end))
    });
    // Exact duplicate face intervals add no source evidence and make every
    // subsequent pairing more expensive. Never merge over an unmeasured gap.
    faces.dedup_by(|a, b| {
        a.vertical == b.vertical
            && a.coordinate == b.coordinate
            && a.start == b.start
            && a.end == b.end
    });
    // Preserve every established face ID. New observations are appended and
    // their pairings run only after the original band sequence is complete.
    let original_faces = faces.len();
    if original_faces + recovered_faces.len() <= MAX_FACES {
        faces.extend(recovered_faces);
    }
    let mut face_order: Vec<_> = (0..faces.len()).collect();
    face_order.sort_by(|&a, &b| {
        faces[a]
            .vertical
            .cmp(&faces[b].vertical)
            .then(faces[a].coordinate.total_cmp(&faces[b].coordinate))
            .then(faces[a].start.total_cmp(&faces[b].start))
            .then(faces[a].end.total_cmp(&faces[b].end))
    });
    let sx = page_width / width as f64;
    let sy = page_height / height as f64;
    let source_pixels = residual_hatch::Pixels {
        width,
        height,
        luma: &luma,
        ink: &ink,
        mask: region_mask,
        scale: [sx, sy],
    };
    let point = |face: Face, along: f64, across: f64| {
        if face.vertical {
            Point::new(across * sx, along * sy)
        } else {
            Point::new(along * sx, across * sy)
        }
    };
    let mut primitives: Vec<_> = faces
        .iter()
        .enumerate()
        .map(|(i, f)| Primitive {
            id: format!("raster:face:{}", i + 1),
            layer: None,
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Line {
                start: point(*f, f.start, f.coordinate),
                end: point(*f, f.end, f.coordinate),
            },
        })
        .collect();
    let mut bands = Vec::new();
    let mut hatch_bands = Vec::new();
    let mut sample_budget = 180_000_000usize;
    let mut hatch_work_budget = 180_000_000usize;
    let mut sums: Option<CrossSums<'_>> = None;
    let mut recovered_bands = Vec::new();
    let mut recovered_hatch_bands = Vec::new();
    // Additional work cannot consume the old extraction's allowance. If it
    // exhausts the shared limit, discard recovery and retain the old result.
    let mut recovery_sample_budget = sample_budget;
    let mut recovery_hatch_budget = hatch_work_budget;
    // An optional layout veto must never consume extraction's proof budget.
    let mut rule_work_budget = 250_000;
    let mut recovery_exhausted = false;
    stage.next("raster_band_measurement");
    for (position, &i) in face_order.iter().enumerate() {
        let a = faces[i];
        if sums.as_ref().is_none_or(|s| s.vertical != a.vertical) {
            drop(sums.take());
            sums = Some(CrossSums::new(&ink, &luma, width, height, a.vertical));
        }
        let sums = sums.as_ref().unwrap();
        for &j in &face_order[position + 1..] {
            let b = faces[j];
            if a.vertical != b.vertical {
                break;
            }
            let thickness = b.coordinate - a.coordinate;
            if thickness > max_width {
                break;
            }
            if thickness < 3. {
                continue;
            }
            let recovered_pair = i >= original_faces || j >= original_faces;
            if recovered_pair && recovery_exhausted {
                continue;
            }
            let lo = a.start.max(b.start).ceil() as usize;
            let hi = a.end.min(b.end).floor().max(0.) as usize;
            let min_length = (thickness * 2.).max(min_run as f64 * 1.3) as usize;
            let hatch_min_length = (thickness * 1.5).max(min_run as f64) as usize;
            if hi <= lo || hi - lo < min_length.min(hatch_min_length) {
                continue;
            }
            let cross_lo = (a.coordinate + a.ink_half_width + 1.).ceil() as usize;
            let cross_hi = (b.coordinate - b.ink_half_width - 1.).floor().max(0.) as usize;
            if cross_hi <= cross_lo {
                continue;
            }
            let quarter = ((cross_hi - cross_lo) / 4).clamp(1, 2);
            let third = ((cross_hi - cross_lo) / 3).max(1);
            let middle_lo = cross_lo + (cross_hi - cross_lo - third) / 2;
            // A recovered pen may not reuse the same pixel as its near-face,
            // central and opposite-face evidence. These are independent
            // material checks only when their measured sample ranges are
            // disjoint. Preserve the established interpretation of old pairs.
            if recovered_pair
                && (cross_lo + quarter > middle_lo || middle_lo + third > cross_hi - quarter)
            {
                continue;
            }
            // Rejected corridors also consume work: adaptive column histograms
            // and any accepted segment's second profile pass are both bounded.
            // Include the 256-bin histogram traversal even for very short spans.
            let hatch_work = (hi - lo)
                .saturating_add(256)
                .saturating_mul(cross_hi - cross_lo)
                .saturating_mul(2);
            let budget = if recovered_pair {
                &mut recovery_hatch_budget
            } else {
                &mut hatch_work_budget
            };
            if let Some(remaining) = budget.checked_sub(hatch_work) {
                *budget = remaining;
            } else if recovered_pair {
                recovery_exhausted = true;
                continue;
            } else {
                return Err("Raster hatch analysis exceeds pixel work budget".into());
            }
            // Repeated diagonal material is measured separately from the
            // strict achromatic fill path. In particular, tinted insulation
            // does not become furniture or lose its outer faces merely because
            // highlighting changed the stroke's colour.
            for (first, end, witness) in hatch::segments(
                &luma,
                width,
                height,
                a.vertical,
                lo,
                hi,
                cross_lo,
                cross_hi,
                [a.ink_half_width, b.ink_half_width],
                region_mask,
            ) {
                if end <= first || end - first < hatch_min_length {
                    continue;
                }
                let seed = residual_hatch::Seed {
                    indices: [i, j],
                    along: [first, end],
                    interior: [cross_lo, cross_hi],
                    min_length: hatch_min_length,
                };
                let band = source_pixels.band(seed, &faces, &primitives, witness);
                if recovered_pair {
                    recovered_hatch_bands.push((band, seed));
                } else {
                    hatch_bands.push(band);
                }
                if bands.len() + hatch_bands.len() > MAX_BANDS {
                    return Err("Raster page exceeds 12000 measured material bands".into());
                }
                if recovered_pair
                    && bands.len()
                        + hatch_bands.len()
                        + recovered_bands.len()
                        + recovered_hatch_bands.len()
                        > MAX_BANDS
                {
                    recovery_exhausted = true;
                    break;
                }
            }
            if recovered_pair && recovery_exhausted {
                continue;
            }
            if hi - lo < min_length {
                continue;
            }
            let work = (hi - lo).saturating_mul(cross_hi - cross_lo);
            let budget = if recovered_pair {
                &mut recovery_sample_budget
            } else {
                &mut sample_budget
            };
            if let Some(remaining) = budget.checked_sub(work) {
                *budget = remaining;
            } else if recovered_pair {
                recovery_exhausted = true;
                continue;
            } else {
                return Err("Raster band measurement exceeds pixel work budget".into());
            }
            let mut density = Vec::with_capacity(hi - lo);
            let mut brightness = Vec::with_capacity(hi - lo);
            let mut edges = Vec::with_capacity(hi - lo);
            let mut core = Vec::with_capacity(hi - lo);
            for along in lo..hi {
                // Material must meet each measured face after its pen is
                // excluded. A broad quarter can reach a remote real wall
                // across white space and falsely validate a nearby grid line.
                let [count, total] = sums.sum(along, cross_lo, cross_hi);
                let edge_count = [
                    sums.sum(along, cross_lo, cross_lo + quarter)[0],
                    sums.sum(along, cross_hi - quarter, cross_hi)[0],
                ];
                core.push(sums.sum(along, middle_lo, middle_lo + third)[0] as f64 / third as f64);
                density.push(count as f64 / (cross_hi - cross_lo) as f64);
                brightness.push(total as f64 / (cross_hi - cross_lo) as f64);
                edges.push([
                    edge_count[0] as f64 / quarter as f64,
                    edge_count[1] as f64 / quarter as f64,
                ]);
            }
            // Average only a few pixels to tolerate insulation hatching. A long
            // blank section remains a gap; no closing/dilation joins a doorway.
            let radius = (thickness * 0.15).round().clamp(1., 4.) as usize;
            let mut prefix = vec![0.; density.len() + 1];
            let mut core_prefix = vec![0.; core.len() + 1];
            let mut edge_prefix = vec![[0.; 2]; edges.len() + 1];
            for (k, value) in density.iter().enumerate() {
                prefix[k + 1] = prefix[k] + value;
                core_prefix[k + 1] = core_prefix[k] + core[k];
                for side in 0..2 {
                    edge_prefix[k + 1][side] = edge_prefix[k][side] + edges[k][side];
                }
            }
            let solid: Vec<_> = (0..density.len())
                .map(|k| {
                    let x = k.saturating_sub(radius);
                    let y = (k + radius + 1).min(density.len());
                    (prefix[y] - prefix[x]) / (y - x) as f64 >= 0.12
                })
                .collect();
            let mut k = 0;
            while k < solid.len() {
                if !solid[k] {
                    k += 1;
                    continue;
                }
                let mut end = k + 1;
                while end < solid.len() && solid[end] {
                    end += 1;
                }
                // Trim the smoothed halo to actual nonempty material samples.
                let mut first = k;
                while first < end && density[first] == 0. {
                    first += 1;
                }
                while end > first && density[end - 1] == 0. {
                    end -= 1;
                }
                if end - first >= min_length {
                    let n = (end - first) as f64;
                    let dark = density[first..end].iter().sum::<f64>() / n;
                    let mean_luma = brightness[first..end].iter().sum::<f64>() / n;
                    let inner_support =
                        [0, 1].map(|s| edges[first..end].iter().map(|e| e[s]).sum::<f64>() / n);
                    // A wall face paired with a remote grid/dimension line has
                    // an empty near-edge quarter on that side.
                    let central_support = core[first..end].iter().sum::<f64>() / n;
                    // Repeated insulation can have a modest average darkness,
                    // but its material continues along the wall. A remote grid
                    // line plus occasional dimension lettering cannot supply
                    // that continuity in the central core. A window of about
                    // one measured width averages hatch periods while retaining
                    // independent checks even on short spans. This never fills
                    // or joins a measured gap.
                    let support_radius = (thickness * 0.5).ceil().clamp(2., 32.) as usize;
                    let supported_windows = (first..end)
                        .filter(|&along| {
                            let x = along.saturating_sub(support_radius).max(first);
                            let y = (along + support_radius + 1).min(end);
                            let count = (y - x) as f64;
                            (core_prefix[y] - core_prefix[x]) / count >= 0.10
                                && (prefix[y] - prefix[x]) / count >= 0.12
                                && (0..2).all(|side| {
                                    (edge_prefix[y][side] - edge_prefix[x][side]) / count >= 0.065
                                })
                        })
                        .count();
                    let continuous_core = supported_windows as f64 / n >= 0.85;
                    if dark >= 0.12
                        && central_support >= 0.065
                        && continuous_core
                        && inner_support.iter().all(|v| *v >= 0.065)
                        && (!recovered_pair || dense_material_tone(dark, mean_luma))
                        && !(recovered_pair
                            && mean_luma > 180.
                            && dark <= 0.45
                            && rule_matrix::contains(
                                &faces[..original_faces],
                                [a, b],
                                [lo + first, lo + end],
                                [width, height],
                                sums,
                                &mut rule_work_budget,
                            ))
                    {
                        let from = (lo + first) as f64;
                        let to = (lo + end) as f64;
                        let boundary_coverage = [a.coordinate, b.coordinate].map(|coordinate| {
                            let mut supported = 0usize;
                            for along in lo + first..lo + end {
                                let across = coordinate.floor() as isize;
                                if (-1..=1).any(|offset| {
                                    let cross = across + offset;
                                    let limit = if a.vertical { width } else { height };
                                    if cross < 0 || cross as usize >= limit {
                                        return false;
                                    }
                                    let index = if a.vertical {
                                        along * width + cross as usize
                                    } else {
                                        cross as usize * width + along
                                    };
                                    ink[index]
                                }) {
                                    supported += 1;
                                }
                            }
                            supported as f64 / n
                        });
                        let target = if recovered_pair {
                            &mut recovered_bands
                        } else {
                            &mut bands
                        };
                        target.push(RasterBand {
                            hatch: None,
                            face_source_ids: [primitives[i].id.clone(), primitives[j].id.clone()],
                            start: point(a, from, (a.coordinate + b.coordinate) * 0.5),
                            end: point(a, to, (a.coordinate + b.coordinate) * 0.5),
                            thickness: thickness * if a.vertical { sx } else { sy },
                            dark_fraction: dark,
                            mean_luma,
                            boundary_coverage,
                            width_pixels: thickness,
                        });
                        if bands.len() + hatch_bands.len() > MAX_BANDS {
                            return Err("Raster page exceeds 12000 measured material bands".into());
                        }
                        if recovered_pair
                            && bands.len()
                                + hatch_bands.len()
                                + recovered_bands.len()
                                + recovered_hatch_bands.len()
                                > MAX_BANDS
                        {
                            recovery_exhausted = true;
                            break;
                        }
                    }
                }
                k = end.max(k + 1);
                // Skip the remaining blank/smoothed halo without revisiting the
                // same segment after trimming it back.
                while k < solid.len() && solid[k] {
                    k += 1;
                }
            }
        }
    }
    // Complete the old strict + hatch sequence before adding any recovery.
    bands.append(&mut hatch_bands);
    let original_bands = bands.len();
    // Recovery is additive: it may not replace an established parallel body,
    // even if a new observation appears wider or has a higher material score.
    if recovery_exhausted
        || 180_000_000 - recovery_sample_budget > sample_budget
        || 180_000_000 - recovery_hatch_budget > hatch_work_budget
        || original_bands + recovered_bands.len() + recovered_hatch_bands.len() > MAX_BANDS
    {
        recovered_bands.clear();
        recovered_hatch_bands.clear();
    }
    let mut rejected_hatch = Vec::new();
    for (band, seed) in recovered_bands.into_iter().map(|band| (band, None)).chain(
        recovered_hatch_bands
            .into_iter()
            .map(|(band, seed)| (band, Some(seed))),
    ) {
        if bands[..original_bands]
            .iter()
            .any(|old| parallel_bands_overlap(old, &band))
        {
            if recover_residual_hatch && let Some(seed) = seed {
                rejected_hatch.push(seed);
            }
        } else {
            bands.push(band);
        }
    }
    let bands_before_residuals = bands.len();
    let raster_course_receipts = if recover_thin_fringe && recover_course_receipts {
        let _profile = crate::stage_profile::scope("raster_course_receipt_measurement");
        course_receipts::derive(&source_pixels, &bands, &primitives)
    } else {
        Vec::new()
    };
    if !rejected_hatch.is_empty() {
        let _profile = crate::stage_profile::scope("raster_residual_hatch_measurement");
        // Use only already-emitted face primitives so a residual cannot add
        // another face, renumber any source, or change previous witnesses.
        let kept: std::collections::HashSet<_> = bands[original_bands..]
            .iter()
            .flat_map(|b| b.face_source_ids.iter().map(String::as_str))
            .collect();
        rejected_hatch.retain(|seed| {
            seed.indices
                .iter()
                .all(|&i| i < original_faces || kept.contains(primitives[i].id.as_str()))
        });
        let capacity = MAX_BANDS - bands.len();
        residual_hatch::append(
            &source_pixels,
            &faces,
            &primitives,
            &mut bands,
            &rejected_hatch,
            capacity,
        );
    }
    let supported_faces: std::collections::HashSet<_> = bands[original_bands..]
        .iter()
        .flat_map(|b| b.face_source_ids.iter().map(String::as_str))
        .collect();
    let mut index = 0;
    primitives.retain(|p| {
        let keep = index < original_faces || supported_faces.contains(p.id.as_str());
        index += 1;
        keep
    });
    let raster_face_continuations = if recover_thin_fringe && recover_face_continuations {
        let _profile = crate::stage_profile::scope("raster_face_continuation_measurement");
        face_continuation::collect(&source_pixels, &bands, &primitives)
    } else {
        vec![]
    };
    let raster_hinged_doors = if recover_thin_fringe && recover_hinged_doors {
        let _profile = crate::stage_profile::scope("raster_hinged_door_measurement");
        hinged_doors::collect(&source_pixels, &faces[..original_faces])
    } else {
        vec![]
    };
    let raster_jamb_stocks = if recover_jamb_stocks && !raster_hinged_doors.is_empty() {
        let _profile = crate::stage_profile::scope("raster_jamb_stock_measurement");
        jamb_stocks::collect(&source_pixels, &raster_hinged_doors, &bands)
    } else {
        vec![]
    };
    let raster_narrow_courses = if recover_narrow_courses && !raster_jamb_stocks.is_empty() {
        let _profile = crate::stage_profile::scope("raster_narrow_course_measurement");
        narrow_courses::collect(
            &source_pixels,
            &raster_hinged_doors,
            &raster_jamb_stocks,
            &bands,
        )
    } else {
        vec![]
    };
    let raster_section_courses = if recover_section_courses && !raster_narrow_courses.is_empty() {
        let _profile = crate::stage_profile::scope("raster_section_course_measurement");
        narrow_courses::collect_sections(
            &source_pixels,
            &raster_hinged_doors,
            &raster_jamb_stocks,
            &bands,
            &raster_narrow_courses,
        )
    } else {
        vec![]
    };
    let raster_jamb_returns = if recover_jamb_returns && !raster_jamb_stocks.is_empty() {
        let _profile = crate::stage_profile::scope("raster_jamb_return_measurement");
        narrow_courses::collect_jamb_returns(
            &source_pixels,
            &raster_hinged_doors,
            &raster_jamb_stocks,
            &bands,
        )
    } else {
        vec![]
    };
    Ok(Extracted {
        primitives,
        bands,
        threshold: THRESHOLD,
        bands_before_residuals,
        raster_course_receipts,
        raster_face_continuations,
        raster_hinged_doors,
        raster_jamb_stocks,
        raster_narrow_courses,
        raster_section_courses,
        raster_jamb_returns,
    })
}

fn stripes(
    ink: &[bool],
    width: usize,
    height: usize,
    vertical: bool,
    min_run: usize,
) -> Result<Vec<Stripe>, String> {
    let (major, minor) = if vertical {
        (height, width)
    } else {
        (width, height)
    };
    let mut active: Vec<Stripe> = Vec::new();
    let mut complete = Vec::new();
    for across in 0..minor {
        let at = |along: usize| {
            ink[if vertical {
                along * width + across
            } else {
                across * width + along
            }]
        };
        let mut runs = Vec::new();
        let mut k = 0;
        while k < major {
            if !at(k) {
                k += 1;
                continue;
            }
            let start = k;
            let mut last = k;
            k += 1;
            while k < major {
                if at(k) {
                    last = k;
                    k += 1;
                } else if k + 1 < major && at(k + 1) {
                    k += 1;
                } else {
                    break;
                }
            }
            if last + 1 - start >= min_run {
                runs.push((start, last + 1));
            }
        }
        let mut next = Vec::new();
        for (start, end) in runs {
            let matching = active
                .iter()
                .enumerate()
                .filter(|(_, s)| s.last + 1 == across)
                .filter_map(|(i, s)| {
                    let a = *s.starts.last().unwrap();
                    let b = *s.ends.last().unwrap();
                    let overlap = end.min(b).saturating_sub(start.max(a));
                    let common = (end - start).max(b - a);
                    ((start.abs_diff(a) <= 2 && end.abs_diff(b) <= 2)
                        || overlap as f64 / common as f64 >= 0.94)
                        .then_some((i, overlap))
                })
                .max_by_key(|(_, overlap)| *overlap)
                .map(|(i, _)| i);
            let mut stripe = if let Some(i) = matching {
                active.swap_remove(i)
            } else {
                Stripe {
                    first: across,
                    last: across,
                    starts: vec![],
                    ends: vec![],
                }
            };
            stripe.last = across;
            stripe.starts.push(start);
            stripe.ends.push(end);
            next.push(stripe);
        }
        complete.extend(active);
        active = next;
        if complete.len() + active.len() > MAX_FACES * 4 {
            return Err("Raster page exceeds sustained-run budget".into());
        }
    }
    complete.extend(active);
    Ok(complete)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn canvas(w: usize, h: usize) -> Vec<u8> {
        vec![255; w * h * 4]
    }
    fn rect(p: &mut [u8], w: usize, x0: usize, y0: usize, x1: usize, y1: usize, gray: u8) {
        for y in y0..y1 {
            for x in x0..x1 {
                p[(y * w + x) * 4..(y * w + x) * 4 + 3].fill(gray);
            }
        }
    }
    #[test]
    fn material_band_keeps_door_gap_and_rejects_hollow_furniture() {
        let (w, h) = (600, 400);
        let mut p = canvas(w, h);
        rect(&mut p, w, 30, 80, 250, 92, 90);
        rect(&mut p, w, 300, 80, 560, 92, 90);
        // A hollow furniture rectangle contains no material evidence.
        for (a, b, c, d) in [
            (80, 160, 160, 162),
            (80, 180, 160, 182),
            (80, 160, 82, 182),
            (158, 160, 160, 182),
        ] {
            rect(&mut p, w, a, b, c, d, 30);
        }
        let r = extract(w, h, &p, 600., 400., None, false).unwrap();
        assert!(
            r.bands
                .iter()
                .any(|b| b.start.y > 79. && b.start.y < 93. && b.start.x < 35. && b.end.x > 245.)
        );
        assert!(r.bands.iter().any(|b| b.start.x >= 300. && b.end.x > 550.));
        assert!(
            !r.bands
                .iter()
                .any(|b| b.start.x < 250. && b.end.x > 300. && b.start.y > 79. && b.start.y < 93.)
        );
        assert!(!r.bands.iter().any(|b| b.start.y > 150. && b.start.y < 190.));
        assert!(
            r.primitives
                .iter()
                .all(|p| p.stroke_width.is_none() && p.layer.is_none())
        );
    }
    #[test]
    fn eligibility_and_transparency_cannot_invent_source_ink() {
        let (w, h) = (200, 160);
        let mut p = canvas(w, h);
        rect(&mut p, w, 20, 40, 180, 52, 70);
        let empty = vec![false; w * h];
        assert!(
            extract(w, h, &p, 200., 160., Some(&empty), false)
                .unwrap()
                .primitives
                .is_empty()
        );
        for a in p.chunks_exact_mut(4) {
            a[3] = 0;
        }
        assert!(
            extract(w, h, &p, 200., 160., None, false)
                .unwrap()
                .primitives
                .is_empty()
        );
        assert!(extract(w, h, &p[..4], 200., 160., None, false).is_err());
    }
    #[test]
    fn material_measurement_scales_in_source_coordinates() {
        let (w, h) = (600, 400);
        let mut p = canvas(w, h);
        rect(&mut p, w, 50, 80, 550, 92, 110);
        let a = extract(w, h, &p, 600., 400., None, false).unwrap();
        let b = extract(w, h, &p, 1200., 800., None, false).unwrap();
        assert_eq!(a.bands.len(), b.bands.len());
        assert!(!a.bands.is_empty());
        for (a, b) in a.bands.iter().zip(&b.bands) {
            assert!((a.thickness * 2. - b.thickness).abs() < 1e-8);
            assert!((a.start.x * 2. - b.start.x).abs() < 1e-8);
            assert_eq!(a.dark_fraction, b.dark_fraction);
        }
    }
    #[test]
    fn notches_in_a_broad_fill_cannot_manufacture_wall_faces() {
        let (w, h) = (600, 400);
        let mut p = canvas(w, h);
        rect(&mut p, w, 20, 30, 570, 220, 130);
        // White lettering/notches alter the lengths of sustained runs inside
        // a slab. Those run-group boundaries have no actual outward contrast.
        for (x, y) in [(80, 80), (180, 100), (280, 120), (380, 140)] {
            rect(&mut p, w, x, y, x + 20, y + 6, 255);
        }
        rect(&mut p, w, 50, 280, 550, 292, 100);
        let r = extract(w, h, &p, 600., 400., None, false).unwrap();
        assert!(r.bands.iter().any(|b| b.start.y >= 280.
            && b.start.y <= 292.
            && b.start.x <= 52.
            && b.end.x >= 548.));
        assert!(!r.bands.iter().any(|b| b.start.y < 250.));
    }
    #[test]
    fn face_pen_ink_cannot_supply_a_white_band_core() {
        let (w, h) = (600, 400);
        let mut p = canvas(w, h);
        for y in (200..230).step_by(3) {
            rect(&mut p, w, 30, y, 130, y + 1, 30);
        }
        rect(&mut p, w, 40, 70, 560, 74, 25);
        rect(&mut p, w, 40, 82, 560, 86, 25);
        let r = extract(w, h, &p, 600., 400., None, false).unwrap();
        // The two bold boundary pens have a genuinely white central core.
        assert!(!r.bands.iter().any(|b| b.start.y > 74. && b.start.y < 82.));
    }
    #[test]
    fn dimension_lettering_cannot_widen_a_continuous_wall_and_hatching_survives() {
        let (w, h) = (800, 600);
        let mut p = canvas(w, h);
        rect(&mut p, w, 30, 80, 770, 88, 110);
        // A nearby continuous dimension line and isolated dark lettering/ticks
        // add enough aggregate ink to fool a mean-only strip measurement.
        rect(&mut p, w, 30, 105, 770, 106, 30);
        for x in (80..750).step_by(120) {
            rect(&mut p, w, x, 92, x + 14, 102, 30);
            rect(&mut p, w, x + 15, 88, x + 17, 106, 30);
        }
        // A true thin outline with repeated diagonal insulation has low average
        // darkness, but sustained core support at the material's own scale.
        rect(&mut p, w, 30, 240, 770, 241, 30);
        rect(&mut p, w, 30, 252, 770, 253, 30);
        for y in 241..252 {
            for x in 30..770 {
                if (x + y) % 6 == 0 {
                    rect(&mut p, w, x, y, x + 1, y + 1, 30);
                }
            }
        }
        let r = extract(w, h, &p, 800., 600., None, false).unwrap();
        assert!(
            r.bands
                .iter()
                .any(|b| b.start.y >= 80. && b.start.y <= 88. && b.start.x < 40. && b.end.x > 760.)
        );
        assert!(
            !r.bands
                .iter()
                .any(|b| b.start.y > 88. && b.start.y < 105. && b.width_pixels > 15.)
        );
        assert!(
            r.bands
                .iter()
                .any(|b| b.start.y > 240. && b.start.y < 253. && b.start.x < 40. && b.end.x > 760.)
        );
    }
    #[test]
    fn tinted_zigzag_recovers_ink_bounds_without_filling_a_door_or_mask() {
        let (w, h) = (800, 600);
        let mut p = canvas(w, h);
        // Continuous frame strokes cannot authorize material through the gap.
        rect(&mut p, w, 40, 100, 740, 102, 25);
        rect(&mut p, w, 40, 117, 740, 119, 25);
        for x in 40..740 {
            if (320..380).contains(&x) {
                continue;
            }
            let phase = (x % 8) as f64;
            let at = if phase <= 4. {
                phase / 4.
            } else {
                (8. - phase) / 4.
            } * 12.;
            for y in 103..116 {
                if ((y - 103) as f64 - at).abs() <= 1.5 {
                    // Tinted printed strokes fail the strict neutral-ink test.
                    p[(y * w + x) * 4..(y * w + x) * 4 + 3].copy_from_slice(&[90, 135, 70]);
                }
            }
        }
        // A furniture outline provides no repeated material texture.
        for (a, b, c, d) in [
            (100, 300, 200, 302),
            (100, 330, 200, 332),
            (100, 300, 102, 332),
            (198, 300, 200, 332),
        ] {
            rect(&mut p, w, a, b, c, d, 25);
        }
        let r = extract(w, h, &p, 800., 600., None, false).unwrap();
        let bands: Vec<_> = r.bands.iter().filter(|b| b.hatch.is_some()).collect();
        assert!(bands.iter().any(|b| b.start.x < 50.
            && b.end.x > 310.
            && b.start.y - b.thickness / 2. <= 100.
            && b.start.y + b.thickness / 2. >= 119.));
        assert!(bands.iter().any(|b| b.start.x >= 380. && b.end.x > 730.));
        assert!(bands.iter().all(|b| b.end.x <= 320. || b.start.x >= 380.));
        assert!(!bands.iter().any(|b| b.start.y > 290.));
        let mut mask = vec![true; w * h];
        for y in 102..117 {
            mask[y * w + 380..y * w + 740].fill(false);
        }
        let masked = extract(w, h, &p, 800., 600., Some(&mask), false).unwrap();
        assert!(
            !masked
                .bands
                .iter()
                .any(|b| b.hatch.is_some() && b.end.x > 380.)
        );
        assert!(r.primitives.iter().all(|p| p.stroke_width.is_none()));
    }
    fn fringed_hatched_wall() -> (usize, usize, Vec<u8>) {
        let (w, h) = (800, 800);
        let mut p = canvas(w, h);
        rect(&mut p, w, 40, 80, 740, 82, 35);
        rect(&mut p, w, 40, 98, 740, 100, 35);
        for x in 40..740 {
            // The thin face is continuous; its outside pen edge varies by
            // exactly one observed pixel, in runs too short to be a new face.
            if x % 8 < 4 {
                rect(&mut p, w, x, 79, x + 1, 80, 35);
            }
            if (320..380).contains(&x) {
                continue;
            }
            let phase = (x % 8) as f64;
            let at = if phase <= 4. {
                phase / 4.
            } else {
                (8. - phase) / 4.
            } * 15.;
            for y in 82..98 {
                if ((y - 82) as f64 - at).abs() <= 4. {
                    rect(&mut p, w, x, y, x + 1, y + 1, 35);
                }
            }
        }
        (w, h, p)
    }
    #[test]
    fn thin_pen_fringe_recovers_finite_hatched_faces_without_filling_an_aperture() {
        let (w, h, p) = fringed_hatched_wall();
        let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        let wall: Vec<_> = r
            .bands
            .iter()
            .filter(|b| b.start.y > 79. && b.start.y < 100.)
            .collect();
        assert!(wall.iter().any(|b| b.start.x <= 42. && b.end.x >= 310.));
        assert!(wall.iter().any(|b| b.start.x >= 380. && b.end.x >= 730.));
        assert!(wall.iter().all(|b| b.end.x <= 320. || b.start.x >= 380.));
        assert!(wall.iter().all(|b| b.start.x >= 40. && b.end.x <= 740.));
        assert!(
            wall.iter().all(
                |b| b.start.y - b.thickness / 2. >= 79. && b.start.y + b.thickness / 2. <= 100.
            )
        );
        // The directly measured pen envelope is excluded from the material
        // core rather than borrowing its ink as proof of a wall body.
        assert!(wall.iter().any(|b| {
            b.hatch
                .as_ref()
                .is_some_and(|h| h.face_ink_half_widths_pixels[0] >= 1.5)
        }));
    }
    #[test]
    fn thin_pen_fringe_does_not_make_a_pen_only_white_core_material() {
        let (w, h, mut p) = fringed_hatched_wall();
        rect(&mut p, w, 40, 82, 740, 98, 255);
        // Fringes on both pen edges do not constitute interior wall material.
        for x in (40..740).filter(|x| x % 8 < 4) {
            rect(&mut p, w, x, 82, x + 1, 83, 35);
            rect(&mut p, w, x, 97, x + 1, 98, 35);
        }
        let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        assert!(!r.bands.iter().any(|b| b.start.y > 79. && b.start.y < 100.));
    }
    #[test]
    fn thin_pen_fringe_does_not_turn_internal_notches_into_slab_faces() {
        let (w, h) = (800, 600);
        let mut p = canvas(w, h);
        rect(&mut p, w, 40, 60, 740, 280, 100);
        // One-pixel white scars inside a broad slab can expose occasional
        // run boundaries. They are not an outward boundary of a thin pen.
        for x in (40..740).filter(|x| x % 8 < 4) {
            rect(&mut p, w, x, 79, x + 1, 80, 255);
            rect(&mut p, w, x, 98, x + 1, 99, 255);
        }
        let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        assert!(!r.bands.iter().any(|b| b.start.y < 280.));
    }
    #[test]
    fn thin_pen_fringe_preserves_existing_face_ids_and_band_order() {
        let (w, h, mut p) = fringed_hatched_wall();
        rect(&mut p, w, 30, 200, 770, 212, 100);
        let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        assert_eq!(r.primitives[0].id, "raster:face:1");
        assert!(matches!(r.primitives[0].shape, Shape::Line { start, end }
            if start == Point::new(40., 99.) && end == Point::new(740., 99.)));
        let original = &r.bands[0];
        assert_eq!(original.start, Point::new(30., 206.));
        assert_eq!(original.end, Point::new(770., 206.));
        assert_eq!(original.thickness, 12.);
        assert_eq!(original.face_source_ids, ["raster:face:2", "raster:face:3"]);
        assert!(r.bands[1..].iter().any(|b| b.start.y < 100.));
        assert!(
            !r.bands[1..]
                .iter()
                .any(|b| parallel_bands_overlap(original, b))
        );
        // Every appended face must actually witness an additional band.
        assert!(r.primitives[3..].iter().all(|p| {
            r.bands[1..]
                .iter()
                .any(|b| b.face_source_ids.contains(&p.id))
        }));
    }
    #[test]
    fn thin_pen_fringe_respects_axis_calibration_and_masks() {
        let (w, h, p) = fringed_hatched_wall();
        let a = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        let disabled = extract(w, h, &p, w as f64, h as f64, None, false).unwrap();
        assert!(disabled.bands.is_empty());
        assert!(!a.bands.is_empty());
        assert_eq!(disabled.primitives.len(), 1);
        assert_eq!(
            serde_json::to_value(&disabled.primitives).unwrap(),
            serde_json::to_value(&a.primitives[..disabled.primitives.len()]).unwrap()
        );
        let b = extract(w, h, &p, w as f64 * 2., h as f64 * 0.5, None, true).unwrap();
        assert_eq!(a.bands.len(), b.bands.len());
        for (a, b) in a.bands.iter().zip(&b.bands) {
            assert_eq!(a.start.x * 2., b.start.x);
            assert_eq!(a.start.y * 0.5, b.start.y);
            assert_eq!(a.thickness * 0.5, b.thickness);
            assert_eq!(a.face_source_ids, b.face_source_ids);
        }
        let mut transposed = vec![255; p.len()];
        for y in 0..h {
            for x in 0..w {
                transposed[(x * h + y) * 4..(x * h + y + 1) * 4]
                    .copy_from_slice(&p[(y * w + x) * 4..(y * w + x + 1) * 4]);
            }
        }
        let rotated = extract(h, w, &transposed, h as f64, w as f64, None, true).unwrap();
        assert!(a.bands.iter().all(|a| {
            rotated.bands.iter().any(|b| {
                b.start == Point::new(a.start.y, a.start.x)
                    && b.end == Point::new(a.end.y, a.end.x)
                    && b.thickness == a.thickness
            })
        }));
        let empty = vec![false; w * h];
        let masked = extract(w, h, &p, w as f64, h as f64, Some(&empty), true).unwrap();
        assert!(masked.bands.is_empty() && masked.primitives.is_empty());
    }
    fn fringed_narrow_core(core_width: usize) -> (usize, usize, Vec<u8>) {
        let (w, h) = (800, 800);
        let bottom = 84 + core_width;
        let mut p = canvas(w, h);
        rect(&mut p, w, 40, 80, 740, 82, 35);
        rect(&mut p, w, 40, bottom, 740, bottom + 1, 35);
        for x in 40..740 {
            if x % 8 < 4 {
                rect(&mut p, w, x, 79, x + 1, 80, 35);
            }
            for y in 82..bottom {
                if (x + y) % 8 < 6 {
                    rect(&mut p, w, x, y, x + 1, y + 1, 110);
                }
            }
        }
        (w, h, p)
    }
    #[test]
    fn recovered_pen_core_cannot_reuse_one_pixel_as_independent_material_support() {
        for core_width in [1, 2] {
            let (w, h, p) = fringed_narrow_core(core_width);
            let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
            assert!(
                r.bands.is_empty(),
                "{core_width}-pixel core reused: {:?}",
                r.bands
            );
            // Recovery-only face metadata cannot survive without a body.
            let original = extract(w, h, &p, w as f64, h as f64, None, false).unwrap();
            assert_eq!(
                serde_json::to_value(r.primitives).unwrap(),
                serde_json::to_value(original.primitives).unwrap()
            );
        }
    }
    #[test]
    fn three_distinct_core_samples_keep_a_thin_finite_material_strip() {
        let (w, h, p) = fringed_narrow_core(3);
        let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
        assert!(r.bands.iter().any(|b| b.start.x <= 42. && b.end.x >= 730.));
        assert!(r.bands.iter().all(|b| b.start.x >= 40. && b.end.x <= 740.));
    }
    #[test]
    fn recovered_dense_tone_rejects_sparse_grid_but_preserves_material_and_old_pairs() {
        let (w, h) = (800, 800);
        for columns in [1, 2] {
            let mut p = canvas(w, h);
            rect(&mut p, w, 40, 80, 740, 82, 35);
            rect(&mut p, w, 40, 100, 740, 102, 35);
            for x in 40..740 {
                if x % 8 < 4 {
                    rect(&mut p, w, x, 79, x + 1, 80, 35);
                }
                if x % 4 < columns {
                    rect(&mut p, w, x, 82, x + 1, 100, 35);
                }
            }
            // A separate established body is unaffected by recovery policy.
            rect(&mut p, w, 30, 200, 770, 212, 100);
            let original = extract(w, h, &p, w as f64, h as f64, None, false).unwrap();
            let r = extract(w, h, &p, w as f64, h as f64, None, true).unwrap();
            assert_eq!(r.bands[..original.bands.len()], original.bands);
            let additions: Vec<_> = r.bands[original.bands.len()..].iter().collect();
            if columns == 1 {
                assert!(
                    additions.is_empty(),
                    "sparse grid recovered as material: {additions:?}"
                );
            } else {
                assert!(
                    additions
                        .iter()
                        .any(|b| b.start.x <= 42. && b.end.x >= 730.)
                );
            }
        }
    }
    #[test]
    fn blocked_sums_match_exact_pixels_and_do_not_add_color_ink() {
        let (w, h) = (31, 23);
        let luma: Vec<_> = (0..w * h).map(|i| (i % 256) as u8).collect();
        let ink: Vec<_> = luma.iter().map(|v| *v < 100).collect();
        for vertical in [false, true] {
            let sums = CrossSums::new(&ink, &luma, w, h, vertical);
            let (major, minor) = if vertical { (h, w) } else { (w, h) };
            for along in 0..major {
                for lo in 0..minor {
                    for hi in lo + 1..=minor {
                        let mut expected = [0, 0];
                        for across in lo..hi {
                            let i = if vertical {
                                along * w + across
                            } else {
                                across * w + along
                            };
                            expected[0] += u32::from(ink[i]);
                            expected[1] += luma[i] as u32;
                        }
                        assert_eq!(sums.sum(along, lo, hi), expected);
                    }
                }
            }
        }
        let (w, h) = (400, 200);
        let mut p = canvas(w, h);
        for y in 50..100 {
            for x in 30..370 {
                p[(y * w + x) * 4..(y * w + x) * 4 + 3].copy_from_slice(&[147, 184, 139]);
            }
        }
        assert!(
            extract(w, h, &p, 400., 200., None, false)
                .unwrap()
                .primitives
                .is_empty()
        );
    }
    /// Local development inspection; never reads any document unless explicitly
    /// selected by its caller. Its JSON contains observations, not wall labels.
    #[test]
    #[ignore]
    fn development_measurements() {
        let source =
            std::env::var("OPT_RASTER_DEV_PDF").expect("Select a development PDF explicitly");
        let page: usize = std::env::var("OPT_RASTER_DEV_PAGE")
            .unwrap_or("1".into())
            .parse()
            .unwrap();
        let output =
            std::env::var("OPT_RASTER_DEV_JSON").expect("Select a local measurement output");
        let bytes = std::fs::read(source).unwrap();
        let now = std::time::Instant::now();
        let image = super::super::render::render(&bytes, page - 1, 3200).unwrap();
        let extracted = extract(
            image.width,
            image.height,
            &image.rgba,
            image.width as f64,
            image.height as f64,
            None,
            false,
        )
        .unwrap();
        let report = serde_json::json!({"width":image.width,"height":image.height,"primitives":extracted.primitives,"bands":extracted.bands,"threshold":extracted.threshold});
        std::fs::write(output, serde_json::to_vec_pretty(&report).unwrap()).unwrap();
        eprintln!(
            "{} faces, {} measured bands; {} ms",
            report["primitives"].as_array().unwrap().len(),
            report["bands"].as_array().unwrap().len(),
            now.elapsed().as_millis()
        );
    }
}
