//! A finite raster jamb post must have independently sampled stock outside a proven door.
use super::{
    RasterBand, THRESHOLD,
    hinged_doors::{Samples, verified_hinged_doors},
    residual_hatch::Pixels,
};
use crate::{
    input::raster::{PdfInput, PdfRoute, RasterHingedDoorReceipt, RasterJambStockReceipt},
    model::{Point, Primitive, Shape},
};

const MAX_WORK: usize = 2_000_000;
const MAX_POSTS: usize = 32;
const MAX_PATCH_SAMPLES: usize = 4_096;
const NATIVE_EPS: f64 = 1e-7;

fn charge(work: &mut usize, cost: usize) -> Option<()> {
    let ok = *work >= cost;
    *work = work.saturating_sub(cost);
    ok.then_some(())
}

fn native(value: f64) -> Option<i32> {
    (value.is_finite()
        && (0. ..=32_768.).contains(&value)
        && (value - value.round()).abs() < NATIVE_EPS)
        .then_some(value.round() as i32)
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

#[derive(Clone, Copy)]
pub(super) struct BandFootprint {
    pub(super) vertical: bool,
    pub(super) along: [f64; 2],
    pub(super) cross: [f64; 2],
}

impl BandFootprint {
    pub(super) fn read(b: &RasterBand, scale: [f64; 2]) -> Option<Self> {
        let a = [b.start.x / scale[0], b.start.y / scale[1]];
        let c = [b.end.x / scale[0], b.end.y / scale[1]];
        let vertical = a[0] == c[0];
        let axis = usize::from(vertical);
        let transverse = 1 - axis;
        if a[transverse] != c[transverse] || a[axis] >= c[axis] || b.width_pixels <= 0. {
            return None;
        }
        let h = b.width_pixels * 0.5;
        Some(Self {
            vertical,
            along: [a[axis], c[axis]],
            cross: [a[transverse] - h, a[transverse] + h],
        })
    }

    fn intersects(self, b: [i32; 4]) -> bool {
        let [x, y, u, v] = if self.vertical {
            [self.cross[0], self.along[0], self.cross[1], self.along[1]]
        } else {
            [self.along[0], self.cross[0], self.along[1], self.cross[1]]
        };
        x.max(b[0] as f64) + NATIVE_EPS < u.min(b[2] as f64)
            && y.max(b[1] as f64) + NATIVE_EPS < v.min(b[3] as f64)
    }
}

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    along: [i32; 2],
    corridor: [i32; 2],
    return_at: i32,
    return_ink: [i32; 2],
    return_high: bool,
    at_high_end: bool,
    pitch: i32,
}

impl Frame {
    fn new(
        door: &RasterHingedDoorReceipt,
        returns: &RasterBand,
        footprint: BandFootprint,
        at_high_end: bool,
    ) -> Option<Self> {
        let corridor = range(door.cross)?;
        let rough = range(door.rough_jambs)?;
        let width = corridor[1] - corridor[0];
        let proof = returns.hatch.as_ref()?;
        if door.vertical == footprint.vertical
            || proof.crossing_count < 6
            || proof.diagonal_support < 0.6
            || proof.stationary_correlation > -0.15
            || proof.longitudinal_coverage < 0.82
            || proof.mean_pitch_pixels < 2.
            || proof.mean_pitch_pixels > 8.
        {
            return None;
        }
        let near = usize::from(!at_high_end);
        let far_cut = native(footprint.cross[near])?;
        let near_cut = rough[usize::from(at_high_end)];
        let along = if at_high_end {
            [near_cut, far_cut]
        } else {
            [far_cut, near_cut]
        };
        let length = along[1] - along[0];
        if !(10..=32).contains(&length) || length * 4 > 5 * width {
            return None;
        }
        let first = native(footprint.along[0])?;
        let last = native(footprint.along[1])?;
        let limit = (width / 3).max(3).min(8);
        let high = (first - corridor[1]).abs() <= limit && last > corridor[1] + width;
        let low = (last - corridor[0]).abs() <= limit && first < corridor[0] - width;
        let (return_high, return_at) = match (low, high) {
            (true, false) => (false, last - 2),
            (false, true) => (true, first + 1),
            _ => return None,
        };
        let paint = proof.face_ink_half_widths_pixels[near];
        if !paint.is_finite() || !(0.5..=3.).contains(&paint) {
            return None;
        }
        let pen = native(2. * paint)?;
        let return_ink = if at_high_end {
            [far_cut, far_cut + pen]
        } else {
            [far_cut - pen, far_cut]
        };
        Some(Frame {
            vertical: door.vertical,
            along,
            corridor,
            return_at,
            return_ink,
            return_high,
            at_high_end,
            pitch: proof.mean_pitch_pixels.round() as i32,
        })
    }

    fn patch(self) -> Option<[i32; 4]> {
        let axis = [
            self.along[0].min(self.return_ink[0]) - 1,
            self.along[1].max(self.return_ink[1]) + 1,
        ];
        let cross = [
            self.corridor[0].min(self.return_at) - 2,
            self.corridor[1].max(self.return_at) + 2,
        ];
        let r = bounds(self.vertical, axis, cross);
        (r[0] >= 0 && r[1] >= 0 && ((r[2] - r[0]) * (r[3] - r[1])) as usize <= MAX_PATCH_SAMPLES)
            .then_some(r)
    }
}

fn clear_after_cap(p: Samples<'_>, frame: Frame) -> Option<[i32; 2]> {
    let width = frame.corridor[1] - frame.corridor[0];
    let depth = frame.along[1] - frame.along[0];
    let direction = if frame.at_high_end { 1 } else { -1 };
    let cap = if frame.at_high_end {
        frame.along[0]
    } else {
        frame.along[1] - 1
    };
    let mut cap_rows = 0;
    while cap_rows <= depth / 2 {
        let along = cap + direction * cap_rows;
        let mut ink = 0;
        for c in frame.corridor[0]..frame.corridor[1] {
            ink += i32::from(p.at(frame.vertical, along, c)?.0 < THRESHOLD);
        }
        if ink * 10 < width * 9 {
            break;
        }
        cap_rows += 1;
    }
    if !(2..=6).contains(&cap_rows) || depth - cap_rows < 8 {
        return None;
    }
    Some(if frame.at_high_end {
        [frame.along[0] + cap_rows, frame.along[1]]
    } else {
        [frame.along[0], frame.along[1] - cap_rows]
    })
}

#[derive(Clone, Copy)]
struct Rail {
    cross: [i32; 2],
    neutral: i32,
}

fn rails(p: Samples<'_>, f: Frame, a: [i32; 2]) -> Option<Vec<Rail>> {
    let n = a[1] - a[0];
    let mut result = Vec::new();
    let mut open: Option<Rail> = None;
    for c in f.corridor[0]..=f.corridor[1] {
        let mut dark = 0;
        let mut neutral = 0;
        if c < f.corridor[1] {
            for y in a[0]..a[1] {
                let (l, g) = p.at(f.vertical, y, c)?;
                dark += i32::from(l < THRESHOLD);
                neutral += i32::from(g);
            }
        }
        if dark * 100 >= 82 * n {
            if let Some(rail) = &mut open {
                rail.cross[1] = c + 1;
                rail.neutral = rail.neutral.max(neutral);
            } else {
                open = Some(Rail {
                    cross: [c, c + 1],
                    neutral,
                });
            }
        } else if let Some(rail) = open.take() {
            // A wide opaque interior is not a pair of independently observed pens.
            result.push(rail);
        }
    }
    Some(result)
}

fn profile(p: Samples<'_>, f: Frame, a: [i32; 2], center: i32) -> Option<Vec<f64>> {
    (a[0]..a[1])
        .map(|y| {
            let mut sum = 0.;
            for c in center - 1..=center + 1 {
                sum += f64::from(p.at(f.vertical, y, c)?.0);
            }
            Some(sum / 3.)
        })
        .collect()
}

fn correlate(a: &[f64], b: &[f64], shift: i32) -> Option<f64> {
    let skipped = shift.unsigned_abs() as usize;
    let size = a.len().checked_sub(skipped)?;
    if size < 8 {
        return None;
    }
    let (a, b) = if shift > 0 {
        (&a[skipped..], &b[..size])
    } else {
        (&a[..size], &b[skipped..])
    };
    let mean = |p: &[f64]| p.iter().sum::<f64>() / p.len() as f64;
    let (ma, mb) = (mean(a), mean(b));
    let (mut aa, mut bb, mut ab) = (0., 0., 0.);
    for (u, v) in a.iter().zip(b) {
        let (x, y) = (*u - ma, *v - mb);
        aa += x * x;
        bb += y * y;
        ab += x * y;
    }
    (aa > 0. && bb > 0.).then_some(ab / (aa * bb).sqrt())
}

fn tinted_phase(p: Samples<'_>, f: Frame, a: [i32; 2], interior: [i32; 2]) -> Option<()> {
    // At least two distinct pairs must carry the return's pitch and opposite phase.
    let [lo, hi] = interior;
    let n = hi - lo;
    let mut first_pair: Option<[i32; 2]> = None;
    for c in lo + 1..=lo + n / 3 {
        let left = profile(p, f, a, c)?;
        if left.iter().fold(f64::NEG_INFINITY, |a, b| a.max(*b))
            - left.iter().fold(f64::INFINITY, |a, b| a.min(*b))
            < 38.
        {
            continue;
        }
        for d in hi - 1 - n / 3..hi - 1 {
            if d - c < n / 3 {
                continue;
            }
            let right = profile(p, f, a, d)?;
            if right.iter().fold(f64::NEG_INFINITY, |a, b| a.max(*b))
                - right.iter().fold(f64::INFINITY, |a, b| a.min(*b))
                < 38.
            {
                continue;
            }
            let strong = correlate(&left, &right, 0).is_some_and(|v| v <= -0.25)
                && correlate(&left, &right, f.pitch).is_some_and(|v| v >= 0.62)
                && correlate(&left, &right, -f.pitch).is_some_and(|v| v >= 0.62);
            if strong {
                if first_pair.is_some_and(|[a, b]| a != c && b != d) {
                    return Some(());
                }
                first_pair.get_or_insert([c, d]);
            }
        }
    }
    None
}

fn stock_for_pair(p: Samples<'_>, f: Frame, a: [i32; 2], rails: [Rail; 2]) -> Option<[i32; 2]> {
    let [left, right] = rails;
    if rails.iter().any(|r| r.cross[1] - r.cross[0] > 5) {
        return None;
    }
    let cross = [left.cross[0], right.cross[1]];
    let interior = [left.cross[1], right.cross[0]];
    let width = interior[1] - interior[0];
    let size = cross[1] - cross[0];
    let n = a[1] - a[0];
    if width < 8 || size > 28 || n * 3 < size || n > 2 * size {
        return None;
    }
    let mut dark = 0;
    let mut neutral = 0;
    let mut outside = [0, 0];
    for y in a[0]..a[1] {
        let mut row_dark = 0;
        for c in interior[0]..interior[1] {
            let (v, ink) = p.at(f.vertical, y, c)?;
            row_dark += i32::from(v < THRESHOLD);
            neutral += i32::from(ink);
        }
        if row_dark == 0 {
            return None;
        }
        dark += row_dark;
        outside[0] += i32::from(p.at(f.vertical, y, cross[0] - 1)?.0 < THRESHOLD);
        outside[1] += i32::from(p.at(f.vertical, y, cross[1])?.0 < THRESHOLD);
    }
    if dark * 4 < n * width || outside.iter().any(|&g| g * 4 > n) {
        return None;
    }
    let neutral_pens = left.neutral.min(right.neutral) * 5 >= n * 4;
    if neutral_pens {
        if neutral * 5 < n * width {
            return None;
        }
    } else {
        if left.neutral.max(right.neutral) * 2 < n {
            return None;
        }
        tinted_phase(p, f, a, interior)?;
    }
    let continuation = if f.return_high {
        cross[1] - 1
    } else {
        cross[0]
    };
    if (continuation - f.return_at).abs() > size / 2 {
        return None;
    }
    let span = continuation.min(f.return_at)..=continuation.max(f.return_at);
    if !(f.return_ink[0]..f.return_ink[1]).any(|a| {
        span.clone()
            .all(|c| p.at(f.vertical, a, c).is_some_and(|(v, _)| v < THRESHOLD))
    }) {
        return None;
    }
    Some(cross)
}

fn proof(p: Samples<'_>, f: Frame, work: &mut usize) -> Option<[i32; 2]> {
    let [x, y, u, v] = f.patch()?;
    charge(work, ((u - x) * (v - y) * 16) as usize)?;
    let a = clear_after_cap(p, f)?;
    let all = rails(p, f, a)?;
    let mut result = None;
    for both in all.windows(2) {
        if let Some(cross) = stock_for_pair(p, f, a, [both[0], both[1]]) {
            if result.replace(cross).is_some() {
                return None;
            }
        }
    }
    result
}

fn compatible(samples: Samples<'_>, frame: [i32; 4], door: &RasterHingedDoorReceipt) -> Option<()> {
    let q = door
        .patch
        .bounds
        .map(|v| i32::try_from(v).ok())
        .into_iter()
        .collect::<Option<Vec<_>>>()?;
    let [x, y, u, v] = <[i32; 4]>::try_from(q).ok()?;
    let old = Samples::from_patch(&door.patch, [x, y, u, v])?;
    let [a, b, c, d] = [
        frame[0].max(x),
        frame[1].max(y),
        frame[2].min(u),
        frame[3].min(v),
    ];
    if a >= c || b >= d {
        return None;
    }
    for yy in b..d {
        for xx in a..c {
            if old.xy(xx, yy)? != samples.xy(xx, yy)? {
                return None;
            }
        }
    }
    Some(())
}

fn intersects(a: [i32; 4], b: [i32; 4]) -> bool {
    a[0].max(b[0]) < a[2].min(b[2]) && a[1].max(b[1]) < a[3].min(b[3])
}

fn unoccupied(
    body: [i32; 4],
    measured: &[Option<BandFootprint>],
    doors: &[RasterHingedDoorReceipt],
) -> bool {
    !measured.iter().flatten().any(|b| b.intersects(body))
        && doors.iter().all(|d| {
            range(d.rough_jambs)
                .zip(range(d.cross))
                .is_some_and(|(a, c)| !intersects(body, bounds(d.vertical, a, c)))
        })
}

pub(super) fn collect(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    bands: &[RasterBand],
) -> Vec<RasterJambStockReceipt> {
    collect_bounded(pixels, doors, bands).unwrap_or_default()
}

fn collect_bounded(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    bands: &[RasterBand],
) -> Option<Vec<RasterJambStockReceipt>> {
    if doors.is_empty() {
        return Some(vec![]);
    }
    let measurements: Vec<_> = bands
        .iter()
        .map(|b| BandFootprint::read(b, pixels.scale))
        .collect();
    let p = Samples::from_pixels(pixels);
    let mut work = MAX_WORK;
    let mut receipts = Vec::new();
    for (door_index, door) in doors.iter().enumerate() {
        for at_high_end in [false, true] {
            let mut chosen: Option<RasterJambStockReceipt> = None;
            let mut ambiguous = false;
            for (return_band_index, (band, measured)) in bands.iter().zip(&measurements).enumerate()
            {
                charge(&mut work, 10)?;
                let Some(f) = measured.and_then(|r| Frame::new(door, band, r, at_high_end)) else {
                    continue;
                };
                let Some(cross) = proof(p, f, &mut work) else {
                    if work == 0 {
                        return None;
                    }
                    continue;
                };
                charge(&mut work, measurements.len())?;
                let body = bounds(f.vertical, f.along, cross);
                if !unoccupied(body, &measurements, doors) {
                    continue;
                }
                let along = f.along.map(|v| v as u32);
                let cross = cross.map(|v| v as u32);
                if let Some(current) = &chosen {
                    if current.along != along || current.cross != cross {
                        ambiguous = true;
                    }
                    continue;
                }
                let patch = p.capture(f.patch()?)?;
                chosen = Some(RasterJambStockReceipt {
                    door_index,
                    at_high_end,
                    return_band_index,
                    along,
                    cross,
                    patch,
                });
            }
            if !ambiguous && let Some(receipt) = chosen {
                receipts.push(receipt);
            }
        }
    }
    if receipts.len() > MAX_POSTS {
        return None;
    }
    let bodies: Vec<_> = receipts
        .iter()
        .map(|r| {
            bounds(
                doors[r.door_index].vertical,
                r.along.map(|v| v as i32),
                r.cross.map(|v| v as i32),
            )
        })
        .collect();
    Some(
        receipts
            .into_iter()
            .enumerate()
            .filter_map(|(i, r)| {
                bodies
                    .iter()
                    .enumerate()
                    .all(|(j, b)| i == j || !intersects(bodies[i], *b))
                    .then_some(r)
            })
            .collect(),
    )
}

pub(crate) struct VerifiedRasterJambStock {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub threshold: [Point; 2],
    pub at_high_end: bool,
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_jamb_stocks(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterJambStock>, String> {
    let records = &info.raster_jamb_stocks;
    if records.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || records.len() > MAX_POSTS
        || info.render_width == 0
        || info.render_height == 0
    {
        return Err("invalid measured raster jamb stock inventory".into());
    }
    let error = || "raster jamb stock disagrees with its door, return or source pixels".to_string();
    let doors = verified_hinged_doors(info, sources, page_width, page_height)?;
    let scale = [
        page_width / info.render_width as f64,
        page_height / info.render_height as f64,
    ];
    let measurements: Vec<_> = info
        .bands
        .iter()
        .map(|b| BandFootprint::read(b, scale))
        .collect();
    let mut result = Vec::with_capacity(records.len());
    let mut past: Option<(usize, bool)> = None;
    let mut occupied = Vec::new();
    let mut work = MAX_WORK;
    for record in records {
        let key = (record.door_index, record.at_high_end);
        if past.is_some_and(|k| k >= key) {
            return Err(error());
        }
        past = Some(key);
        let door = info
            .raster_hinged_doors
            .get(record.door_index)
            .ok_or_else(error)?;
        let aperture = doors.get(record.door_index).ok_or_else(error)?;
        let band = info.bands.get(record.return_band_index).ok_or_else(error)?;
        let foot = measurements
            .get(record.return_band_index)
            .and_then(|r| *r)
            .ok_or_else(error)?;
        let frame = Frame::new(door, band, foot, record.at_high_end).ok_or_else(error)?;
        let patch = frame.patch().ok_or_else(error)?;
        let [x, y, u, v] = patch;
        if u as usize > info.render_width || v as usize > info.render_height {
            return Err(error());
        }
        let p = Samples::from_patch(&record.patch, patch).ok_or_else(error)?;
        compatible(p, patch, door).ok_or_else(error)?;
        let cross = proof(p, frame, &mut work).ok_or_else(error)?;
        let body = bounds(frame.vertical, frame.along, cross);
        if range(record.along) != Some(frame.along)
            || range(record.cross) != Some(cross)
            || charge(&mut work, measurements.len()).is_none()
            || !unoccupied(body, &measurements, &info.raster_hinged_doors)
            || occupied.iter().any(|&b| intersects(body, b))
        {
            return Err(error());
        }
        occupied.push(body);
        let center = (cross[0] + cross[1]) as f64 * 0.5;
        let xy = |a: f64, c: f64| {
            if frame.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let lo = Point::new(x as f64 * scale[0], y as f64 * scale[1]);
        let hi = Point::new(u as f64 * scale[0], v as f64 * scale[1]);
        let mut source_ids: Vec<_> = sources
            .iter()
            .filter_map(|s| {
                let Shape::Image { min, max } = s.shape else {
                    return None;
                };
                (info.source_image_ids.binary_search(&s.id).is_ok()
                    && min.x.max(lo.x) < max.x.min(hi.x)
                    && min.y.max(lo.y) < max.y.min(hi.y))
                .then_some(s.id.clone())
            })
            .collect();
        source_ids.sort_unstable();
        source_ids.dedup();
        if source_ids.is_empty() {
            return Err(error());
        }
        result.push(VerifiedRasterJambStock {
            start: xy(frame.along[0] as f64, center),
            end: xy(frame.along[1] as f64, center),
            thickness: (cross[1] - cross[0]) as f64 * scale[usize::from(!frame.vertical)],
            threshold: [aperture.start, aperture.end],
            at_high_end: record.at_high_end,
            source_ids,
        });
    }
    Ok(result)
}
