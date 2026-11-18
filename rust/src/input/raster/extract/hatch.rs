//! Repeated zigzag material measured inside two finite image-derived faces.
//! Local contrast permits tinted ink; repetition and transverse phase supply
//! authority, rather than a looser global darkness threshold.
use crate::input::raster::RasterHatch;

struct Profiles {
    channels: [Vec<f64>; 3],
    thresholds: [f64; 3],
    contrast: f64,
}

/// Finish skins can be steady gray while the insulation varies along the wall.
/// Locate the longest measured varying core, without changing either source
/// face or the physical body. A small remote hatch cannot certify a wide strip.
#[allow(clippy::too_many_arguments)]
fn varying_core(
    luma: &[u8],
    width: usize,
    vertical: bool,
    lo: usize,
    hi: usize,
    cross_lo: usize,
    cross_hi: usize,
    region_mask: Option<&[bool]>,
) -> Option<(usize, usize)> {
    let mut best = (cross_lo, cross_lo);
    let mut begin = cross_lo;
    for across in cross_lo..cross_hi {
        let mut histogram = [0usize; 256];
        for along in lo..hi {
            let i = if vertical {
                along * width + across
            } else {
                across * width + along
            };
            let value = if region_mask.is_none_or(|m| m[i]) {
                luma[i] as usize
            } else {
                255
            };
            histogram[value] += 1;
        }
        let rank_lo = (hi - lo) / 10;
        let rank_hi = (hi - lo) * 9 / 10;
        let mut count = 0;
        let mut low = None;
        let mut high = 0;
        for (value, frequency) in histogram.iter().enumerate() {
            count += frequency;
            if low.is_none() && count > rank_lo {
                low = Some(value);
            }
            if count > rank_hi {
                high = value;
                break;
            }
        }
        if high - low.unwrap_or(high) >= 48 {
            if across + 1 - begin > best.1 - best.0 {
                best = (begin, across + 1);
            }
        } else {
            begin = across + 1;
        }
    }
    (best.1 - best.0 >= 7 && (best.1 - best.0) * 2 >= cross_hi - cross_lo).then_some(best)
}

#[allow(clippy::too_many_arguments)]
fn profiles(
    luma: &[u8],
    width: usize,
    height: usize,
    vertical: bool,
    lo: usize,
    hi: usize,
    cross_lo: usize,
    cross_hi: usize,
    region_mask: Option<&[bool]>,
) -> Option<Profiles> {
    let pixels = width.checked_mul(height)?;
    let (major, minor) = if vertical {
        (height, width)
    } else {
        (width, height)
    };
    if luma.len() != pixels
        || region_mask.is_some_and(|m| m.len() != pixels)
        || lo >= hi
        || hi > major
        || cross_lo >= cross_hi
        || cross_hi > minor
        || hi - lo < 24
        || cross_hi - cross_lo < 7
    {
        return None;
    }
    let (cross_lo, cross_hi) = varying_core(
        luma,
        width,
        vertical,
        lo,
        hi,
        cross_lo,
        cross_hi,
        region_mask,
    )?;
    let mut channels: [Vec<f64>; 3] = std::array::from_fn(|_| Vec::with_capacity(hi - lo));
    for (channel, fraction) in [0.2, 0.5, 0.8].into_iter().enumerate() {
        let at = cross_lo + ((cross_hi - cross_lo - 1) as f64 * fraction).round() as usize;
        let from = at.saturating_sub(1).max(cross_lo);
        let to = (at + 2).min(cross_hi);
        for along in lo..hi {
            let total: usize = (from..to)
                .map(|across| {
                    let i = if vertical {
                        along * width + across
                    } else {
                        across * width + along
                    };
                    if region_mask.is_none_or(|m| m[i]) {
                        luma[i] as usize
                    } else {
                        255
                    }
                })
                .sum();
            channels[channel].push(total as f64 / (to - from) as f64);
        }
    }
    let mut thresholds = [0.; 3];
    let mut contrast: f64 = 255.;
    for (i, channel) in channels.iter().enumerate() {
        let mut values = channel.clone();
        values.sort_unstable_by(f64::total_cmp);
        let low = values[values.len() / 10];
        let high = values[values.len() * 9 / 10];
        contrast = contrast.min(high - low);
        thresholds[i] = (low + high) * 0.5;
    }
    // Uniform gray surfaces cannot produce a repeated material witness.
    (contrast >= 32.).then_some(Profiles {
        channels,
        thresholds,
        contrast,
    })
}

fn correlation(a: &[f64], b: &[f64], shift: isize) -> f64 {
    let length = a.len().min(b.len());
    let offset = shift.unsigned_abs();
    if offset >= length || length - offset < 12 {
        return 0.;
    }
    let (a, b) = if shift >= 0 {
        (&a[..length - offset], &b[offset..length])
    } else {
        (&a[offset..length], &b[..length - offset])
    };
    let n = a.len() as f64;
    let ma = a.iter().sum::<f64>() / n;
    let mb = b.iter().sum::<f64>() / n;
    let (mut covariance, mut aa, mut bb) = (0., 0., 0.);
    for (&x, &y) in a.iter().zip(b) {
        let x = x - ma;
        let y = y - mb;
        covariance += x * y;
        aa += x * x;
        bb += y * y;
    }
    let denominator = (aa * bb).sqrt();
    if denominator <= 1e-9 {
        0.
    } else {
        (covariance / denominator).clamp(-1., 1.)
    }
}

#[allow(clippy::too_many_arguments)]
pub(super) fn measure(
    luma: &[u8],
    width: usize,
    height: usize,
    vertical: bool,
    lo: usize,
    hi: usize,
    cross_lo: usize,
    cross_hi: usize,
    face_ink_half_widths: [f64; 2],
    region_mask: Option<&[bool]>,
) -> Option<RasterHatch> {
    if face_ink_half_widths
        .iter()
        .any(|w| !w.is_finite() || *w < 0. || *w > 32.)
    {
        return None;
    }
    let p = profiles(
        luma,
        width,
        height,
        vertical,
        lo,
        hi,
        cross_lo,
        cross_hi,
        region_mask,
    )?;
    let center = &p.channels[1];
    let mut crossings = Vec::new();
    let mut i = 0;
    while i < center.len() {
        if center[i] >= p.thresholds[1] {
            i += 1;
            continue;
        }
        let mut end = i + 1;
        while end < center.len() && center[end] < p.thresholds[1] {
            end += 1;
        }
        crossings.push((i + end - 1) as f64 * 0.5);
        i = end;
    }
    if crossings.len() < 6 {
        return None;
    }
    let pitch: Vec<_> = crossings.windows(2).map(|w| w[1] - w[0]).collect();
    let mean = pitch.iter().sum::<f64>() / pitch.len() as f64;
    if mean < 2. || mean > (cross_hi - cross_lo) as f64 * 2.5 {
        return None;
    }
    let cv =
        (pitch.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / pitch.len() as f64).sqrt() / mean;
    // A long doorway cannot hide in the average statistics of a long wall.
    if cv > 0.35 || pitch.iter().any(|v| *v > mean * 2.25) {
        return None;
    }
    let covered = pitch
        .iter()
        .filter(|&&v| v >= mean * 0.35 && v <= mean * 1.65)
        .sum::<f64>()
        / center.len() as f64;
    if covered < 0.82 {
        return None;
    }
    let stationary = correlation(&p.channels[0], &p.channels[2], 0);
    let first = (mean * 0.65).ceil().max(1.) as usize;
    let last = ((mean * 1.35).floor() as usize).min(center.len() / 4);
    if first > last {
        return None;
    }
    let mut positive = (-1., 0usize);
    let mut negative = (-1., 0usize);
    for shift in first..=last {
        let c = correlation(&p.channels[0], &p.channels[2], shift as isize);
        if c > positive.0 {
            positive = (c, shift);
        }
        let c = correlation(&p.channels[0], &p.channels[2], -(shift as isize));
        if c > negative.0 {
            negative = (c, shift);
        }
    }
    let diagonal = positive.0.min(negative.0);
    // Treads and grid stripes are in phase across the strip. A zigzag has
    // regular center crossings and coherent opposite transverse phase shifts.
    if diagonal < 0.45
        || stationary > 0.25
        || diagonal - stationary < 0.30
        || positive.1.abs_diff(negative.1) as f64 > (mean * 0.25).max(1.)
    {
        return None;
    }
    Some(RasterHatch {
        crossing_count: crossings.len(),
        mean_pitch_pixels: mean,
        pitch_cv: cv,
        diagonal_support: diagonal,
        stationary_correlation: stationary,
        contrast: p.contrast,
        longitudinal_coverage: covered,
        face_ink_half_widths_pixels: face_ink_half_widths,
    })
}

/// Blank cross-sections split evidence before a witness is assigned. Neither
/// smoothing nor a context link is allowed to manufacture material in a gap.
#[allow(clippy::too_many_arguments)]
pub(super) fn segments(
    luma: &[u8],
    width: usize,
    height: usize,
    vertical: bool,
    lo: usize,
    hi: usize,
    cross_lo: usize,
    cross_hi: usize,
    face_ink_half_widths: [f64; 2],
    region_mask: Option<&[bool]>,
) -> Vec<(usize, usize, RasterHatch)> {
    let Some(p) = profiles(
        luma,
        width,
        height,
        vertical,
        lo,
        hi,
        cross_lo,
        cross_hi,
        region_mask,
    ) else {
        return vec![];
    };
    let blank: Vec<_> = (0..hi - lo)
        .map(|i| (0..3).all(|c| p.channels[c][i] >= p.thresholds[c]))
        .collect();
    let mut result = vec![];
    let mut begin = 0;
    let mut at = 0;
    let mut append = |start: usize, end: usize| {
        let mut start = start;
        let mut end = end;
        while start < end && blank[start] {
            start += 1;
        }
        while end > start && blank[end - 1] {
            end -= 1;
        }
        if let Some(witness) = measure(
            luma,
            width,
            height,
            vertical,
            lo + start,
            lo + end,
            cross_lo,
            cross_hi,
            face_ink_half_widths,
            region_mask,
        ) {
            result.push((lo + start, lo + end, witness));
        }
    };
    while at < blank.len() {
        if !blank[at] {
            at += 1;
            continue;
        }
        let mut end = at + 1;
        while end < blank.len() && blank[end] {
            end += 1;
        }
        if end - at >= 3 {
            append(begin, at);
            begin = end;
        }
        at = end;
    }
    append(begin, blank.len());
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    fn zigzag(width: usize, height: usize) -> Vec<u8> {
        let mut pixels = vec![245; width * height];
        for along in 0..width {
            let phase = (along % 8) as f64;
            let at = if phase <= 4. {
                phase / 4.
            } else {
                (8. - phase) / 4.
            } * (height - 1) as f64;
            for across in 0..height {
                if (across as f64 - at).abs() <= 1.5 {
                    pixels[across * width + along] = 90;
                }
            }
        }
        pixels
    }
    #[test]
    fn repeated_zigzag_has_shifted_phase_and_survives_local_brightness() {
        let (w, h) = (240, 13);
        let p = zigzag(w, h);
        let proof = measure(&p, w, h, false, 0, w, 0, h, [1., 2.], None).unwrap();
        assert!(proof.crossing_count > 30 && proof.pitch_cv < 0.15);
        assert!(proof.diagonal_support > 0.8 && proof.stationary_correlation < 0.);
        let lighter: Vec<_> = p.iter().map(|v| (*v as u16 + 40).min(255) as u8).collect();
        assert!(measure(&lighter, w, h, false, 0, w, 0, h, [1., 2.], None).is_some());
    }
    #[test]
    fn gray_slabs_treads_and_parallel_diagonals_do_not_supply_zigzag_authority() {
        let (w, h) = (240, 13);
        let uniform = vec![100; w * h];
        assert!(measure(&uniform, w, h, false, 0, w, 0, h, [0., 0.], None).is_none());
        let mut treads = vec![245; w * h];
        let mut diagonal = treads.clone();
        for y in 0..h {
            for x in 0..w {
                if x % 8 < 2 {
                    treads[y * w + x] = 90;
                }
                if (x + y / 2) % 8 < 2 {
                    diagonal[y * w + x] = 90;
                }
            }
        }
        assert!(measure(&treads, w, h, false, 0, w, 0, h, [0., 0.], None).is_none());
        assert!(measure(&diagonal, w, h, false, 0, w, 0, h, [0., 0.], None).is_none());
    }
    #[test]
    fn a_measured_varying_core_survives_steady_facing_but_cannot_claim_a_remote_strip() {
        let w = 240;
        let core = zigzag(w, 13);
        let mut facing = vec![150; w * 21];
        facing[..core.len()].copy_from_slice(&core);
        assert!(measure(&facing, w, 21, false, 0, w, 0, 21, [0.5, 1.], None).is_some());
        let mut overwide = vec![245; w * 31];
        overwide[..core.len()].copy_from_slice(&core);
        assert!(measure(&overwide, w, 31, false, 0, w, 0, 31, [0.5, 1.], None).is_none());
    }
    #[test]
    fn empty_apertures_split_spans_and_masked_hatching_cannot_witness_pixels() {
        let (w, h) = (240, 13);
        let mut p = zigzag(w, h);
        for y in 0..h {
            p[y * w + 104..y * w + 128].fill(255);
        }
        let spans = segments(&p, w, h, false, 0, w, 0, h, [0., 0.], None);
        assert!(spans.len() >= 2 && spans.iter().all(|&(a, b, _)| b <= 104 || a >= 128));
        let mask = vec![false; w * h];
        assert!(segments(&p, w, h, false, 0, w, 0, h, [0., 0.], Some(&mask)).is_empty());
    }
}
