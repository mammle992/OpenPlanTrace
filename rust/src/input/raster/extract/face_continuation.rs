//! Local raster face continuations are review observations separate from placement bands.
use super::{RasterBand, THRESHOLD, hatch, residual_hatch::Pixels};
use crate::{
    input::raster::{PdfInput, PdfRoute, RasterCoursePatch, RasterFaceContinuationReceipt},
    model::{Point, Primitive, Shape},
};
use std::collections::{BTreeMap, HashMap, HashSet};

const MAX_PATCH_SIDE: usize = 128;
const MAX_PATCH_PIXELS: usize = 4096;
const MAX_RECEIPTS: usize = 16;
const MAX_WORK: usize = 2_000_000;
const PIXEL_EPS: f64 = 1e-7;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Span {
    vertical: bool,
    along: [usize; 2],
    cross: [usize; 2],
}

#[derive(Clone, Copy)]
struct Footprint {
    vertical: bool,
    along: [f64; 2],
    cross: [f64; 2],
}

impl Footprint {
    fn from_band(band: &RasterBand, scale: [f64; 2]) -> Option<Self> {
        let a = [band.start.x / scale[0], band.start.y / scale[1]];
        let b = [band.end.x / scale[0], band.end.y / scale[1]];
        let (vertical, along, center) = if a[0] == b[0] && a[1] != b[1] {
            (true, [a[1], b[1]], a[0])
        } else if a[1] == b[1] && a[0] != b[0] {
            (false, [a[0], b[0]], a[1])
        } else {
            return None;
        };
        let half = band.width_pixels * 0.5;
        (along[0] < along[1] && center.is_finite() && half.is_finite() && half > 0.).then_some(
            Self {
                vertical,
                along,
                cross: [center - half, center + half],
            },
        )
    }

    fn native(self) -> Option<Span> {
        Some(Span {
            vertical: self.vertical,
            along: native_range(self.along)?,
            cross: native_range(self.cross)?,
        })
    }

    fn intersects(self, child: Span) -> bool {
        let [a, b, c, d] = child.bounds().map(|x| x as f64);
        let [x, y, u, v] = if self.vertical {
            [self.cross[0], self.along[0], self.cross[1], self.along[1]]
        } else {
            [self.along[0], self.cross[0], self.along[1], self.cross[1]]
        };
        x.max(a) + PIXEL_EPS < u.min(c) && y.max(b) + PIXEL_EPS < v.min(d)
    }
}

fn native_range(r: [f64; 2]) -> Option<[usize; 2]> {
    let px = |x: f64| {
        (x.is_finite()
            && (0. ..=u32::MAX as f64).contains(&x)
            && (x.round() - x).abs() <= PIXEL_EPS)
            .then_some(x.round() as usize)
    };
    let [a, b] = [px(r[0])?, px(r[1])?];
    (a < b).then_some([a, b])
}

impl Span {
    fn length(self) -> Option<usize> {
        self.along[1].checked_sub(self.along[0]).filter(|&n| n > 0)
    }

    fn bounds(self) -> [usize; 4] {
        let [a, b] = self.along;
        let [c, d] = self.cross;
        if self.vertical {
            [c, a, d, b]
        } else {
            [a, c, b, d]
        }
    }

    fn intersects(self, other: Self) -> bool {
        let [x, y, u, v] = self.bounds();
        let [a, b, c, d] = other.bounds();
        x.max(a) < u.min(c) && y.max(b) < v.min(d)
    }
}

/// Reconstructed from a pre-continuation native band's measured footprint.
#[derive(Clone, Copy)]
struct Anchor {
    span: Span,
    pitch: f64,
}

#[derive(Clone, Copy)]
enum End {
    Flanked {
        low: Anchor,
        high: Anchor,
    },
    /// The existing long face's exact native endpoint bounds a review segment.
    Terminal {
        anchor: Anchor,
        at_high: bool,
    },
}

#[derive(Clone, Copy)]
struct Candidate {
    child: Span,
    profiles: [usize; 2],
    retained_face_along: [usize; 2],
    original_pen_edges: [[usize; 2]; 2],
    retained_side: usize,
    end: End,
}

impl Candidate {
    fn context(self) -> Option<f64> {
        let covers =
            |outer: [usize; 2], inner: [usize; 2]| outer[0] <= inner[0] && outer[1] >= inner[1];
        let child = self.child;
        let length = child.length()?;
        let width = child.cross[1].checked_sub(child.cross[0])?;
        if !(8..=64).contains(&width)
            || length > width * 3
            || !covers(self.retained_face_along, self.profiles)
            || !covers(self.profiles, child.along)
            || self.retained_side > 1
        {
            return None;
        }
        let anchor_ok = |anchor: Anchor| {
            anchor.span.vertical == child.vertical
                && anchor.span.cross == child.cross
                && anchor.span.length().is_some()
                && anchor.pitch.is_finite()
                && (2. ..=16.).contains(&anchor.pitch)
        };
        match self.end {
            End::Flanked { low, high } => {
                let anchored = anchor_ok(low)
                    && anchor_ok(high)
                    && low.span.along[1] == child.along[0]
                    && high.span.along[0] == child.along[1]
                    && self.profiles[0] < child.along[0]
                    && child.along[1] < self.profiles[1]
                    && self.profiles[0] >= low.span.along[0]
                    && self.profiles[1] <= high.span.along[1]
                    && (low.pitch - high.pitch).abs() <= low.pitch.min(high.pitch) * 0.2;
                anchored.then_some((low.pitch + high.pitch) * 0.5)
            }
            End::Terminal { anchor, at_high } => {
                let far = usize::from(at_high);
                let near = 1 - far;
                let anchored = anchor_ok(anchor)
                    && length * 2 <= width * 3
                    && anchor.span.along[far] == child.along[near]
                    && child.along[far] == self.retained_face_along[far]
                    && self.profiles[far] == child.along[far]
                    && self.profiles[near] != child.along[near]
                    && covers(
                        anchor.span.along,
                        [
                            self.profiles[near].min(child.along[near]),
                            self.profiles[near].max(child.along[near]),
                        ],
                    );
                anchored.then_some(anchor.pitch)
            }
        }
    }

    fn patch_frame(self) -> Option<Span> {
        let [lower, upper] = self.child.cross;
        Some(Span {
            along: self.profiles,
            cross: [lower.checked_sub(1)?, upper.checked_add(1)?],
            ..self.child
        })
    }
}

/// Verify the exact candidate pixels. An opening cannot borrow child ink or hatch from its anchors.
fn verify(candidate: Candidate, patch: &RasterCoursePatch, work: &mut usize) -> Option<()> {
    let pitch = candidate.context()?;
    let child = candidate.child;
    if child.length()? as f64 <= pitch * 3.0 {
        return None;
    }
    let frame = candidate.patch_frame()?;
    let [x, y, u, v] = frame.bounds();
    let width = u.checked_sub(x)?;
    let height = v.checked_sub(y)?;
    let count = width.checked_mul(height)?;
    if width > MAX_PATCH_SIDE
        || height > MAX_PATCH_SIDE
        || count > MAX_PATCH_PIXELS
        || [x, y, u, v] != patch.bounds.map(|p| p as usize)
        || patch.luma.len() != count
        || patch.neutral_ink.len() != count
    {
        return None;
    }
    *work = work.checked_sub(count.saturating_mul(12))?;
    if patch
        .luma
        .iter()
        .zip(&patch.neutral_ink)
        .any(|(&luma, &ink)| ink && luma >= THRESHOLD)
    {
        return None;
    }
    let index = |t: usize, c: usize| {
        if child.vertical {
            (t - y) * width + c - x
        } else {
            (c - y) * width + t - x
        }
    };

    let [a, b] = candidate.original_pen_edges;
    let aw = a[1].checked_sub(a[0])?;
    let bw = b[1].checked_sub(b[0])?;
    if !(1..=5).contains(&aw)
        || !(1..=5).contains(&bw)
        || a[0] != child.cross[0]
        || b[1] != child.cross[1]
        || b[0].checked_sub(a[1])? < 9
    {
        return None;
    }
    let mut neutral = [0usize; 2];
    let mut outside_dark = 0;
    for t in child.along[0]..child.along[1] {
        for (side, [low, high]) in [a, b].into_iter().enumerate() {
            let indices = || (low..high).map(|c| index(t, c));
            let center = index(t, (low + high) / 2);
            let hits = indices().filter(|&i| patch.luma[i] < THRESHOLD).count();
            if patch.luma[center] >= THRESHOLD || hits * 2 < high - low {
                // A continuous dark pen center is stronger than its antialiased fringe.
                return None;
            }
            neutral[side] += usize::from(patch.neutral_ink[center]);
        }
        outside_dark += usize::from(
            [child.cross[0] - 1, child.cross[1]]
                .iter()
                .any(|&c| patch.luma[index(t, c)] < THRESHOLD),
        );
    }
    let length = child.length()?;
    if outside_dark * 10 > length
        || neutral[candidate.retained_side] * 100 < length * 95
        || neutral[1 - candidate.retained_side] * 2 < length
    {
        return None;
    }

    let interior = [a[1] + 1, b[0] - 1];
    let cross = interior.map(|c| c - frame.cross[0]);
    let contexts = hatch::segments(
        &patch.luma,
        width,
        height,
        child.vertical,
        0,
        frame.length()?,
        cross[0],
        cross[1],
        [aw as f64 * 0.5, bw as f64 * 0.5],
        None,
    );
    let [start, end] = child.along.map(|t| t - frame.along[0]);
    let mut supporting = contexts.into_iter().filter(|(a, b, h)| {
        *a <= start
            && *b >= end
            && (h.mean_pitch_pixels - pitch).abs() <= pitch * 0.25
            && h.diagonal_support >= 0.62
            && h.stationary_correlation <= -0.24
    });
    supporting.next()?;
    if supporting.next().is_some() {
        return None;
    }
    let center = (interior[0] + interior[1]) / 2;
    let profile = |t| {
        (center - 1..=center + 1)
            .map(|c| u16::from(patch.luma[index(t, c)]))
            .sum::<u16>()
    };
    let mut parent: Vec<_> = (frame.along[0]..frame.along[1]).map(profile).collect();
    parent.sort_unstable();
    let low = parent[parent.len() / 10];
    let high = parent[parent.len() * 9 / 10];
    if high - low < 96 {
        return None;
    }
    let limit = (low + high) / 2;
    let mut runs = Vec::new();
    let mut open = None;
    for t in child.along[0]..=child.along[1] {
        if t < child.along[1] && profile(t) < limit {
            open.get_or_insert(t);
        } else if let Some(a) = open.take() {
            runs.push([a, t]);
        }
    }
    let pause = |n: usize| n as f64 <= pitch * 1.75;
    let typical = |l: [usize; 2], r: [usize; 2]| {
        let step = ((r[0] + r[1]) - (l[0] + l[1])) as f64 * 0.5;
        (pitch * 0.5..=pitch * 1.6).contains(&step)
    };
    let minimum_runs = if matches!(candidate.end, End::Flanked { .. }) {
        5
    } else {
        3
    };
    if runs.len() < minimum_runs
        || !pause(runs[0][0] - child.along[0])
        || !pause(child.along[1] - runs.last()?[1])
        || !runs.windows(2).all(|pair| pause(pair[1][0] - pair[0][1]))
        || runs
            .windows(2)
            .filter(|pair| typical(pair[0], pair[1]))
            .count()
            + 2
            < runs.len()
    {
        return None;
    }
    Some(())
}

#[derive(Clone, Copy)]
struct Rail {
    along: [usize; 2],
    across: f64,
}

struct NativeFrame<'a> {
    bands: &'a [RasterBand],
    sources: HashMap<&'a str, &'a Primitive>,
    occupied: Vec<Footprint>,
    scale: [f64; 2],
    image: [usize; 2],
}

impl<'a> NativeFrame<'a> {
    fn new(
        bands: &'a [RasterBand],
        sources: &'a [Primitive],
        scale: [f64; 2],
        image: [usize; 2],
    ) -> Option<Self> {
        if scale.iter().any(|v| !v.is_finite() || *v <= 0.) {
            return None;
        }
        Some(Self {
            bands,
            sources: sources.iter().map(|s| (s.id.as_str(), s)).collect(),
            occupied: bands
                .iter()
                .map(|b| Footprint::from_band(b, scale))
                .collect::<Option<_>>()?,
            scale,
            image,
        })
    }

    fn anchor(&self, index: usize) -> Option<Anchor> {
        let band = self.bands.get(index)?;
        let hatch = band.hatch.as_ref()?;
        if hatch.crossing_count < 6
            || hatch.diagonal_support < 0.6
            || hatch.stationary_correlation > -0.1
            || hatch.longitudinal_coverage < 0.82
            || band.boundary_coverage.iter().any(|&c| c < 0.9)
        {
            return None;
        }
        Some(Anchor {
            span: self.occupied.get(index)?.native()?,
            pitch: hatch.mean_pitch_pixels,
        })
    }

    fn rail(&self, source: &str, vertical: bool) -> Option<Rail> {
        let p = self.sources.get(source)?;
        let Shape::Line { start, end } = p.shape else {
            return None;
        };
        let [sx, sy] = self.scale;
        let a = [start.x / sx, start.y / sy];
        let b = [end.x / sx, end.y / sy];
        let axis = usize::from(vertical);
        let normal = 1 - axis;
        if a[normal] != b[normal] {
            return None;
        }
        Some(Rail {
            along: native_range([a[axis].min(b[axis]), a[axis].max(b[axis])])?,
            across: a[normal],
        })
    }

    fn prepare(
        &self,
        stable: &str,
        indices: [Option<usize>; 2],
        work: &mut usize,
    ) -> Option<Candidate> {
        if !stable.starts_with("raster:face:") {
            return None;
        }
        let [left, right] = indices.map(|index| index.and_then(|i| self.anchor(i).map(|a| (i, a))));
        if [left.is_some(), right.is_some()] != indices.map(|v| v.is_some()) {
            return None;
        }
        let (basis, anchor, along, end) = match (left, right) {
            (Some((i, low)), Some((j, high))) if low.span.along[1] < high.span.along[0] => {
                let (basis, anchor) = if low.span.length()? > high.span.length()? {
                    (i, low)
                } else {
                    (j, high)
                };
                (
                    basis,
                    anchor,
                    [low.span.along[1], high.span.along[0]],
                    End::Flanked { low, high },
                )
            }
            (Some((i, a)), None) => {
                let rail = self.rail(stable, a.span.vertical)?;
                (
                    i,
                    a,
                    [a.span.along[1], rail.along[1]],
                    End::Terminal {
                        anchor: a,
                        at_high: true,
                    },
                )
            }
            (None, Some((i, a))) => {
                let rail = self.rail(stable, a.span.vertical)?;
                (
                    i,
                    a,
                    [rail.along[0], a.span.along[0]],
                    End::Terminal {
                        anchor: a,
                        at_high: false,
                    },
                )
            }
            _ => return None,
        };
        let child = Span {
            along,
            ..anchor.span
        };
        let rail = self.rail(stable, child.vertical)?;
        let primary = &self.bands[basis];
        let retained_side = primary.face_source_ids.iter().position(|id| id == stable)?;
        let remote = self.rail(&primary.face_source_ids[1 - retained_side], child.vertical)?;
        // A true missing face extent has a source pen ending at its existing anchor.
        let outside = if Some(basis) == indices[0] { 1 } else { 0 };
        let adjacent = 1 - outside;
        if remote.along[outside] != child.along[adjacent] {
            return None;
        }
        if indices
            .into_iter()
            .flatten()
            .any(|i| !self.bands[i].face_source_ids.iter().any(|id| id == stable))
        {
            return None;
        }
        let half = primary.hatch.as_ref()?.face_ink_half_widths_pixels;
        let centers = if retained_side == 0 {
            [rail.across, remote.across]
        } else {
            [remote.across, rail.across]
        };
        let original_pen_edges = [
            native_range([centers[0] - half[0], centers[0] + half[0]])?,
            native_range([centers[1] - half[1], centers[1] + half[1]])?,
        ];
        let profiles = match end {
            End::Flanked { low, high } => {
                let margin = (anchor.pitch * 2.).ceil().clamp(4., 16.) as usize;
                [
                    child.along[0].saturating_sub(margin).max(low.span.along[0]),
                    child.along[1]
                        .saturating_add(margin)
                        .min(high.span.along[1]),
                ]
            }
            End::Terminal { at_high, .. } => {
                let margin = (anchor.pitch * 7.).round().clamp(16., 48.) as usize;
                if at_high {
                    [
                        child.along[0]
                            .saturating_sub(margin)
                            .max(anchor.span.along[0]),
                        child.along[1],
                    ]
                } else {
                    [
                        child.along[0],
                        child.along[1]
                            .saturating_add(margin)
                            .min(anchor.span.along[1]),
                    ]
                }
            }
        };
        let candidate = Candidate {
            child,
            profiles,
            retained_face_along: rail.along,
            original_pen_edges,
            retained_side,
            end,
        };
        candidate.context()?;
        let [_, _, right, bottom] = candidate.patch_frame()?.bounds();
        if right > self.image[0] || bottom > self.image[1] {
            return None;
        }
        *work = work.checked_sub(self.occupied.len())?;
        if self.occupied.iter().any(|b| b.intersects(child)) {
            return None;
        }
        Some(candidate)
    }
}

fn capture(p: &Pixels<'_>, frame: Span) -> Option<RasterCoursePatch> {
    let [x, y, u, v] = frame.bounds();
    let [w, h] = [u.checked_sub(x)?, v.checked_sub(y)?];
    let n = w.checked_mul(h)?;
    if n == 0
        || w > MAX_PATCH_SIDE
        || h > MAX_PATCH_SIDE
        || n > MAX_PATCH_PIXELS
        || u > p.width
        || v > p.height
    {
        return None;
    }
    let mut luma = Vec::with_capacity(n);
    let mut neutral_ink = Vec::with_capacity(n);
    for row in y..v {
        for column in x..u {
            let at = row * p.width + column;
            let covered = p.mask.is_none_or(|m| m[at]);
            luma.push(if covered { p.luma[at] } else { 255 });
            neutral_ink.push(covered && p.ink[at]);
        }
    }
    Some(RasterCoursePatch {
        bounds: [x as u32, y as u32, u as u32, v as u32],
        luma,
        neutral_ink,
    })
}

pub(super) fn collect(
    p: &Pixels<'_>,
    bands: &[RasterBand],
    sources: &[Primitive],
) -> Vec<RasterFaceContinuationReceipt> {
    discover(p, bands, sources).unwrap_or_default()
}

fn discover(
    p: &Pixels<'_>,
    bands: &[RasterBand],
    sources: &[Primitive],
) -> Option<Vec<RasterFaceContinuationReceipt>> {
    let mut work = MAX_WORK.checked_sub(bands.len() + sources.len())?;
    let native = NativeFrame::new(bands, sources, p.scale, [p.width, p.height])?;
    let mut groups = BTreeMap::<(&str, bool, [usize; 2]), Vec<(usize, [usize; 2])>>::new();
    for (i, band) in bands.iter().enumerate() {
        if let Some(a) = native.anchor(i) {
            for id in &band.face_source_ids {
                groups
                    .entry((id, a.span.vertical, a.span.cross))
                    .or_default()
                    .push((i, a.span.along));
            }
        }
    }
    let mut found = Vec::new();
    for ((stable, _, _), mut anchors) in groups {
        anchors.sort_unstable_by_key(|&(i, span)| (span, i));
        let [first, last] = [anchors.first()?.0, anchors.last()?.0];
        let options = std::iter::once([None, Some(first)])
            .chain(anchors.windows(2).map(|w| [Some(w[0].0), Some(w[1].0)]))
            .chain(std::iter::once([Some(last), None]));
        for indices in options {
            work = work.checked_sub(8)?;
            let Some(candidate) = native.prepare(stable, indices, &mut work) else {
                continue;
            };
            let Some(patch) = candidate.patch_frame().and_then(|f| capture(p, f)) else {
                continue;
            };
            if verify(candidate, &patch, &mut work).is_some() {
                found.push((
                    candidate.child,
                    RasterFaceContinuationReceipt {
                        retained_face_source_id: stable.to_string(),
                        anchor_band_indices: indices,
                        patch,
                    },
                ));
                if found.len() > MAX_RECEIPTS {
                    return None;
                }
            }
        }
    }
    Some(
        found
            .iter()
            .enumerate()
            .filter(|(i, (span, _))| {
                found
                    .iter()
                    .enumerate()
                    .all(|(j, (other, _))| *i == j || !span.intersects(*other))
            })
            .map(|(_, (_, receipt))| receipt.clone())
            .collect(),
    )
}

pub(crate) struct VerifiedRasterFaceContinuation {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_face_continuations(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterFaceContinuation>, String> {
    let records = &info.raster_face_continuations;
    if records.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || records.len() > MAX_RECEIPTS
    {
        return Err("invalid measured raster face continuation inventory".into());
    }
    let image = [info.render_width, info.render_height];
    let scale = [page_width / image[0] as f64, page_height / image[1] as f64];
    verify_continuation_inventory(&info.bands, sources, scale, image, records)
}

pub(super) fn verify_continuation_inventory(
    bands: &[RasterBand],
    sources: &[Primitive],
    scale: [f64; 2],
    image: [usize; 2],
    records: &[RasterFaceContinuationReceipt],
) -> Result<Vec<VerifiedRasterFaceContinuation>, String> {
    if records.is_empty() {
        return Ok(vec![]);
    }
    if records.len() > MAX_RECEIPTS {
        return Err("invalid measured raster face continuation inventory".into());
    }
    let err =
        || "raster face continuation disagrees with its measured sources or pixels".to_string();
    let native = NativeFrame::new(bands, sources, scale, image).ok_or_else(err)?;
    let mut work = MAX_WORK;
    let mut used = HashSet::new();
    let mut accepted: Vec<Span> = Vec::new();
    let mut output = Vec::with_capacity(records.len());
    for record in records {
        let stable = record.retained_face_source_id.as_str();
        let indices = record.anchor_band_indices;
        if !used.insert((stable, indices)) {
            return Err(err());
        }
        let candidate = native.prepare(stable, indices, &mut work).ok_or_else(err)?;
        verify(candidate, &record.patch, &mut work).ok_or_else(err)?;
        let child = candidate.child;
        if accepted.iter().any(|other| child.intersects(*other)) {
            return Err(err());
        }
        accepted.push(child);
        let axis = usize::from(child.vertical);
        let cross = (child.cross[0] + child.cross[1]) as f64 * 0.5;
        let point = |t| {
            let at = t as f64 * scale[axis];
            let coord = cross * scale[1 - axis];
            if child.vertical {
                Point::new(coord, at)
            } else {
                Point::new(at, coord)
            }
        };
        let mut source_ids: Vec<_> = indices
            .into_iter()
            .flatten()
            .flat_map(|i| bands[i].face_source_ids.iter().cloned())
            .collect();
        source_ids.sort_unstable();
        source_ids.dedup();
        output.push(VerifiedRasterFaceContinuation {
            start: point(child.along[0]),
            end: point(child.along[1]),
            thickness: (child.cross[1] - child.cross[0]) as f64 * scale[1 - axis],
            source_ids,
        });
    }
    Ok(output)
}
