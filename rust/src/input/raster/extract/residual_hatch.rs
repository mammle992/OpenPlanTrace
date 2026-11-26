//! Recover independently measured hatch on uncovered intervals of an accepted face pair.
use super::{Face, RasterBand, RasterHatch, THRESHOLD, hatch, parallel_bands_overlap};
use crate::model::{Point, Primitive};

const MAX_WORK: usize = 12_000_000;
const NATIVE_EPS: f64 = 1e-8;

#[derive(Clone, Copy)]
pub(super) struct Seed {
    pub indices: [usize; 2],
    pub along: [usize; 2],
    pub interior: [usize; 2],
    pub min_length: usize,
}

pub(super) struct Pixels<'a> {
    pub width: usize,
    pub height: usize,
    pub luma: &'a [u8],
    pub ink: &'a [bool],
    pub mask: Option<&'a [bool]>,
    pub scale: [f64; 2],
}

impl Pixels<'_> {
    fn point(&self, vertical: bool, along: f64, across: f64) -> Point {
        let [sx, sy] = self.scale;
        if vertical {
            Point::new(across * sx, along * sy)
        } else {
            Point::new(along * sx, across * sy)
        }
    }

    fn index(&self, vertical: bool, along: usize, across: usize) -> usize {
        if vertical {
            along * self.width + across
        } else {
            across * self.width + along
        }
    }

    /// Shared by established extraction and optional residual recovery.
    pub(super) fn band(
        &self,
        seed: Seed,
        faces: &[Face],
        primitives: &[Primitive],
        witness: RasterHatch,
    ) -> RasterBand {
        let [i, j] = seed.indices;
        let [a, b] = [faces[i], faces[j]];
        let [first, end] = seed.along;
        let [cross_lo, cross_hi] = seed.interior;
        let mut dark = 0usize;
        let mut total = 0usize;
        for along in first..end {
            for across in cross_lo..cross_hi {
                let i = self.index(a.vertical, along, across);
                let value = if self.mask.is_none_or(|m| m[i]) {
                    self.luma[i]
                } else {
                    255
                };
                dark += usize::from(value < THRESHOLD);
                total += value as usize;
            }
        }
        let count = (end - first).saturating_mul(cross_hi - cross_lo) as f64;
        let left = a.coordinate - a.ink_half_width;
        let right = b.coordinate + b.ink_half_width;
        let boundary_coverage = [a.coordinate, b.coordinate].map(|coordinate| {
            let supported = (first..end)
                .filter(|&along| {
                    (-1..=1).any(|offset| {
                        let across = coordinate.floor() as isize + offset;
                        let limit = if a.vertical { self.width } else { self.height };
                        across >= 0
                            && (across as usize) < limit
                            && self.ink[self.index(a.vertical, along, across as usize)]
                    })
                })
                .count();
            supported as f64 / (end - first) as f64
        });
        RasterBand {
            hatch: Some(witness),
            face_source_ids: [primitives[i].id.clone(), primitives[j].id.clone()],
            start: self.point(a.vertical, first as f64, (left + right) * 0.5),
            end: self.point(a.vertical, end as f64, (left + right) * 0.5),
            thickness: (right - left) * self.scale[usize::from(!a.vertical)],
            dark_fraction: dark as f64 / count,
            mean_luma: total as f64 / count,
            boundary_coverage,
            width_pixels: right - left,
        }
    }
}

#[derive(Clone, Copy)]
struct Footprint {
    vertical: bool,
    along: [usize; 2],
    across: [f64; 2],
}

impl Footprint {
    fn from_band(band: &RasterBand, scale: [f64; 2]) -> Option<Self> {
        let vertical = band.start.x == band.end.x;
        let ([a, b], normal) = if vertical {
            ([band.start.y, band.end.y], band.start.x)
        } else {
            ([band.start.x, band.end.x], band.start.y)
        };
        let long_scale = scale[usize::from(vertical)];
        let [a, b] = [a / long_scale, b / long_scale];
        let native = |v: f64| {
            (v.is_finite() && v >= 0. && (v - v.round()).abs() <= NATIVE_EPS)
                .then_some(v.round() as usize)
        };
        let along = [native(a.min(b))?, native(a.max(b))?];
        let normal = normal / scale[usize::from(!vertical)];
        Some(Self {
            vertical,
            along,
            across: [
                normal - band.width_pixels * 0.5,
                normal + band.width_pixels * 0.5,
            ],
        })
    }

    fn collision(self, other: Self) -> Option<[usize; 2]> {
        if self.vertical != other.vertical
            || self.across[1].min(other.across[1])
                <= self.across[0].max(other.across[0]) + NATIVE_EPS
        {
            return None;
        }
        let lo = self.along[0].max(other.along[0]);
        let hi = self.along[1].min(other.along[1]);
        (lo < hi).then_some([lo, hi])
    }
}

fn uncovered(
    parent: Footprint,
    occupied: &[Footprint],
    min_length: usize,
    work: &mut usize,
) -> Option<Vec<[usize; 2]>> {
    *work = work.checked_sub(occupied.len())?;
    let mut clips: Vec<_> = occupied
        .iter()
        .filter_map(|&b| parent.collision(b))
        .collect();
    *work = work.checked_sub(
        clips
            .len()
            .saturating_mul(clips.len().max(1).ilog2() as usize + 1),
    )?;
    clips.sort_unstable();
    let mut start = parent.along[0];
    let mut residuals = Vec::new();
    for [lo, hi] in clips {
        if lo.saturating_sub(start) >= min_length {
            residuals.push([start, lo]);
        }
        start = start.max(hi);
    }
    if parent.along[1].saturating_sub(start) >= min_length {
        residuals.push([start, parent.along[1]]);
    }
    Some(residuals)
}

#[derive(Clone, Copy)]
struct SampleScope<'a> {
    pixels: &'a Pixels<'a>,
    faces: &'a [Face],
    primitives: &'a [Primitive],
    incumbents: &'a [RasterBand],
}

struct Candidate {
    shape: Footprint,
    seed: Seed,
    band: RasterBand,
}

impl SampleScope<'_> {
    fn measure(self, seed: Seed, shape: Footprint, work: &mut usize) -> Option<Vec<Candidate>> {
        let [a, b] = seed.indices.map(|i| self.faces[i]);
        let [lo, hi] = shape.along;
        let [cross_lo, cross_hi] = seed.interior;
        // Budget every adaptive profile and per-segment histogram, including
        // short residuals that cannot ultimately emit a band.
        let length = hi - lo;
        let profiles = 2 + length / 27;
        let cost = (length + 256usize.saturating_mul(profiles))
            .saturating_mul(cross_hi - cross_lo)
            .saturating_mul(4);
        *work = work.checked_sub(cost)?;
        let mut measured = Vec::new();
        let p = self.pixels;
        for (first, end, witness) in hatch::segments(
            p.luma,
            p.width,
            p.height,
            a.vertical,
            lo,
            hi,
            cross_lo,
            cross_hi,
            [a.ink_half_width, b.ink_half_width],
            p.mask,
        ) {
            if end.saturating_sub(first) < seed.min_length.max(24)
                || witness.crossing_count < 8
                || witness.stationary_correlation > -0.2
                || witness.diagonal_support - witness.stationary_correlation < 0.95
                || witness.longitudinal_coverage < 0.82
            {
                continue;
            }
            let seed = Seed {
                along: [first, end],
                ..seed
            };
            let band = p.band(seed, self.faces, self.primitives, witness);
            if band.boundary_coverage.iter().any(|&value| value < 0.95) {
                continue;
            }
            *work = work.checked_sub(self.incumbents.len())?;
            if self
                .incumbents
                .iter()
                .any(|old| parallel_bands_overlap(old, &band))
            {
                continue;
            }
            let shape = Footprint {
                along: seed.along,
                ..shape
            };
            measured.push(Candidate { shape, seed, band });
        }
        Some(measured)
    }

    fn center_hits(self, face: usize, along: [usize; 2], work: &mut usize) -> Option<usize> {
        let f = self.faces[face];
        let cross = f.coordinate.floor() as usize;
        *work = work.checked_sub(along[1] - along[0])?;
        Some(
            (along[0]..along[1])
                .filter(|&a| self.pixels.ink[self.pixels.index(f.vertical, a, cross)])
                .count(),
        )
    }
}

/// Two alternative widths can independently measure the same material on one
/// face. When one contains the other completely, its supported core uses the
/// narrower bounded body. The second face must still exist as its own pen.
fn nested_core(inner: &Candidate, outer: &Candidate) -> Option<usize> {
    let [lo, hi] = inner.shape.across;
    let [a, b] = outer.shape.across;
    if lo < a
        || hi > b
        || hi - lo >= b - a
        || inner.shape.along[0] < outer.shape.along[0]
        || inner.shape.along[1] > outer.shape.along[1]
    {
        return None;
    }
    let [p, q] = inner.seed.indices;
    let [r, s] = outer.seed.indices;
    if lo == a && p == r && q != s {
        Some(q)
    } else if hi == b && q == s && p != r {
        Some(p)
    } else {
        None
    }
}

fn recover(
    pixels: &Pixels<'_>,
    faces: &[Face],
    primitives: &[Primitive],
    incumbents: &[RasterBand],
    rejected: &[Seed],
    capacity: usize,
) -> Option<Vec<RasterBand>> {
    if capacity == 0 || rejected.is_empty() {
        return Some(vec![]);
    }
    let scope = SampleScope {
        pixels,
        faces,
        primitives,
        incumbents,
    };
    let mut work = MAX_WORK.checked_sub(incumbents.len())?;
    let occupied = incumbents
        .iter()
        .map(|b| Footprint::from_band(b, pixels.scale))
        .collect::<Option<Vec<_>>>()?;
    let mut candidates = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for &seed in rejected {
        let [a, b] = seed.indices.map(|i| faces[i]);
        let parent = Footprint {
            vertical: a.vertical,
            along: seed.along,
            across: [
                a.coordinate - a.ink_half_width,
                b.coordinate + b.ink_half_width,
            ],
        };
        for along in uncovered(parent, &occupied, seed.min_length.max(24), &mut work)? {
            let shape = Footprint { along, ..parent };
            candidates.extend(
                scope
                    .measure(seed, shape, &mut work)?
                    .into_iter()
                    .filter(|c| seen.insert((c.seed.indices, c.seed.along))),
            );
            if candidates.len() > capacity {
                return None;
            }
        }
    }

    // Only resolve a single two-way shared-core alternative. A candidate with
    // multiple overlapping interpretations stays in review.
    let count = candidates.len();
    work = work.checked_sub(count.saturating_pow(2))?;
    let mut links = vec![0usize; count];
    let mut cores = vec![None; count];
    for (i, a) in candidates.iter().enumerate() {
        for (j, b) in candidates.iter().enumerate().skip(i + 1) {
            if a.shape.collision(b.shape).is_none() {
                continue;
            }
            links[i] += 1;
            links[j] += 1;
            if let Some(pen) = nested_core(a, b) {
                cores[i] = Some((j, pen));
            } else if let Some(pen) = nested_core(b, a) {
                cores[j] = Some((i, pen));
            }
        }
    }
    let mut kept: Vec<_> = links.iter().map(|&n| n == 0).collect();
    let mut extra = Vec::new();
    for (i, core) in cores.into_iter().enumerate() {
        let Some((j, pen)) = core else { continue };
        if links[i] != 1 || links[j] != 1 {
            continue;
        }
        let [inner, outer] = [&candidates[i], &candidates[j]];
        let length = inner.shape.along[1] - inner.shape.along[0];
        if scope.center_hits(pen, inner.shape.along, &mut work)? * 100 < length * 95 {
            continue;
        }
        kept[i] = true;
        for along in uncovered(
            outer.shape,
            &[inner.shape],
            outer.seed.min_length.max(24),
            &mut work,
        )? {
            let length = along[1] - along[0];
            // A sustained pen outside the extracted inner face footprint may
            // still mark the same ambiguous facing. Re-enter a wider section
            // only where those original pixels do not continue the inner pen.
            if scope.center_hits(pen, along, &mut work)? * 5 > length {
                continue;
            }
            let shape = Footprint {
                along,
                ..outer.shape
            };
            extra.extend(
                scope
                    .measure(outer.seed, shape, &mut work)?
                    .into_iter()
                    .map(|c| c.band),
            );
        }
    }
    let mut result: Vec<_> = candidates
        .into_iter()
        .zip(kept)
        .filter_map(|(c, keep)| keep.then_some(c.band))
        .collect();
    if result.len() + extra.len() > capacity {
        return None;
    }
    result.append(&mut extra);
    Some(result)
}

pub(super) fn append(
    pixels: &Pixels<'_>,
    faces: &[Face],
    primitives: &[Primitive],
    incumbents: &mut Vec<RasterBand>,
    rejected: &[Seed],
    capacity: usize,
) {
    if let Some(residuals) = recover(pixels, faces, primitives, incumbents, rejected, capacity) {
        incumbents.extend(residuals);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_retained_bands_split_the_parent_at_measured_native_limits() {
        let parent = Footprint {
            vertical: false,
            along: [20, 200],
            across: [400., 425.],
        };
        let occupied = [
            Footprint {
                along: [100, 120],
                ..parent
            },
            Footprint {
                along: [123, 140],
                ..parent
            },
            Footprint {
                along: [25, 46],
                ..parent
            },
            Footprint {
                along: [40, 55],
                ..parent
            },
            Footprint {
                vertical: true,
                ..parent
            },
            Footprint {
                across: [425., 434.],
                ..parent
            },
        ];
        let mut work = 100;
        assert_eq!(
            uncovered(parent, &occupied, 30, &mut work),
            Some(vec![[55, 100], [140, 200]])
        );
        assert!(uncovered(parent, &occupied, 30, &mut 1).is_none());
    }
}
