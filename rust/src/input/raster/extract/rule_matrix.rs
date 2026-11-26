//! In a ruled form, narrow strokes extend across alternating blank cells.
//! A recovered pair on its rim can bound text without representing material.

use super::{CrossSums, Face};

fn charge(work: &mut usize, units: usize) -> bool {
    match work.checked_sub(units) {
        Some(left) => {
            *work = left;
            true
        }
        None => {
            *work = 0;
            false
        }
    }
}

fn near(faces: &[Face], lo: f64, hi: f64) -> &[Face] {
    let start = faces.partition_point(|f| f.coordinate < lo);
    let end = faces.partition_point(|f| f.coordinate <= hi);
    &faces[start..end]
}

fn repeated_row(
    faces: &[Face],
    pair: &[Face; 2],
    row: f64,
    tolerance: f64,
    left: f64,
    right: f64,
) -> bool {
    let mut clips: Vec<_> = near(faces, row - tolerance, row + tolerance)
        .iter()
        .chain(
            pair.iter()
                .filter(|f| (f.coordinate - row).abs() <= tolerance),
        )
        .filter_map(|f| {
            let lo = f.start.max(left);
            let hi = f.end.min(right);
            (hi > lo).then_some((lo, hi))
        })
        .collect();
    clips.sort_unstable_by(|a, b| a.0.total_cmp(&b.0));
    let mut length = 0.;
    let mut end = left;
    for (lo, hi) in clips {
        let start = lo.max(end);
        if hi > start {
            length += hi - start;
            end = hi;
        }
    }
    length >= (right - left) * 0.85
}

fn mostly_blank(
    dividers: &[f64],
    rows: &[f64; 6],
    inset: f64,
    [along_limit, across_limit]: [usize; 2],
    sums: &CrossSums<'_>,
    work: &mut usize,
) -> bool {
    let mut blank = 0;
    let mut cells = 0;
    for axes in dividers.windows(2) {
        let lo = (axes[0] + inset).ceil() as usize;
        let hi = ((axes[1] - inset).floor() as usize).min(along_limit);
        if hi <= lo {
            return false;
        }
        // Neighbor cells establish the role of the rule pattern without
        // reusing the text-bearing cell that produced the false hypothesis.
        for r in rows[1..].windows(2) {
            let a = (r[0].min(r[1]) + inset).ceil() as usize;
            let b = ((r[0].max(r[1]) - inset).floor() as usize).min(across_limit);
            if b <= a || !charge(work, hi - lo) {
                return false;
            }
            let pixels = (hi - lo) * (b - a);
            let ink: usize = (lo..hi).map(|x| sums.sum(x, a, b)[0] as usize).sum();
            cells += 1;
            blank += usize::from(ink * 200 < pixels * 7);
        }
    }
    blank * 5 >= cells * 4
}

pub(super) fn contains(
    original: &[Face],
    pair: [Face; 2],
    [first, last]: [usize; 2],
    [width, height]: [usize; 2],
    sums: &CrossSums<'_>,
    work: &mut usize,
) -> bool {
    let [a, b] = pair;
    let thickness = b.coordinate - a.coordinate;
    let tolerance = (thickness * 0.12).clamp(1.25, 4.);
    let (h, v) = original.split_at(original.partition_point(|f| !f.vertical));
    let (parallel, perpendicular, limits) = if a.vertical {
        (v, h, [height, width])
    } else {
        (h, v, [width, height])
    };
    let cross = near(perpendicular, a.start.min(b.start), a.end.max(b.end));
    if !charge(work, cross.len().saturating_mul(2).max(1)) {
        return false;
    }
    for (outer, inner, sign) in [(a, b, 1.), (b, a, -1.)] {
        let far = outer.coordinate + sign * 4.8 * thickness;
        let mut dividers = Vec::new();
        for f in cross {
            let termination = if sign > 0. { f.start } else { f.end };
            if (termination - outer.coordinate).abs() <= tolerance
                && f.start - tolerance <= far
                && f.end + tolerance >= far
                && dividers
                    .last()
                    .is_none_or(|at| f.coordinate - at >= 0.55 * thickness)
            {
                dividers.push(f.coordinate);
            }
        }
        let (Some(&left), Some(&right)) = (dividers.first(), dividers.last()) else {
            continue;
        };
        if dividers.len() < 4
            || left > first as f64
            || right < last as f64
            || right - left < (8. * thickness).max(3. * (last - first) as f64)
        {
            continue;
        }
        let pitch_window = [0.8, 1.2].map(|k| inner.coordinate + k * sign * thickness);
        for f in near(
            parallel,
            pitch_window[0].min(pitch_window[1]),
            pitch_window[0].max(pitch_window[1]),
        ) {
            if !charge(work, parallel.len().max(1)) {
                return false;
            }
            if f.end.min(right) - f.start.max(left) <= thickness {
                continue;
            }
            let step = f.coordinate - inner.coordinate;
            let rows = [
                outer.coordinate,
                inner.coordinate,
                f.coordinate,
                inner.coordinate + 2. * step,
                inner.coordinate + 3. * step,
                inner.coordinate + 4. * step,
            ];
            if rows
                .iter()
                .all(|&at| repeated_row(parallel, &pair, at, tolerance, left, right))
                && mostly_blank(&dividers, &rows, 2. * tolerance, limits, sums, work)
            {
                return true;
            }
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_a_supported_blank_rule_matrix_vetoes_a_recovered_pair() {
        let size = 280;
        for vertical in [false, true] {
            let face = |vertical, coordinate, start, end| Face {
                vertical,
                coordinate,
                start,
                end,
                ink_half_width: 1.,
            };
            let pair = [20., 40.].map(|at| face(vertical, at, 24., 252.));
            let mut original: Vec<_> = [40., 60., 80., 100., 120.]
                .map(|at| face(vertical, at, 24., 252.))
                .to_vec();
            original.extend([24., 65., 132., 191., 252.].map(|at| face(!vertical, at, 20., 130.)));
            original.sort_by(|a, b| {
                a.vertical
                    .cmp(&b.vertical)
                    .then(a.coordinate.total_cmp(&b.coordinate))
            });
            let mut ink = vec![false; size * size];
            let mut luma = vec![255; size * size];
            let index = |along: usize, across: usize| {
                if vertical {
                    along * size + across
                } else {
                    across * size + along
                }
            };
            for at in [20, 40, 60, 80, 100, 120] {
                for x in 24..252 {
                    ink[index(x, at)] = true;
                    luma[index(x, at)] = 10;
                }
            }
            for at in [24, 65, 132, 191, 252] {
                for y in 20..130 {
                    ink[index(at, y)] = true;
                    luma[index(at, y)] = 10;
                }
            }
            for x in 80..110 {
                for y in 25..35 {
                    ink[index(x, y)] = true;
                    luma[index(x, y)] = 10;
                }
            }
            let check = |faces: &[Face], ink: &[bool], luma: &[u8], work: &mut usize| {
                let sums = CrossSums::new(ink, luma, size, size, vertical);
                contains(faces, pair, [80, 110], [size, size], &sums, work)
            };
            assert!(check(&original, &ink, &luma, &mut 250_000));
            assert!(!check(&original, &ink, &luma, &mut 0));
            let isolated_jamb: Vec<_> = original
                .iter()
                .copied()
                .filter(|f| f.vertical == vertical && f.coordinate < 100.)
                .collect();
            assert!(!check(&isolated_jamb, &ink, &luma, &mut 250_000));
            // A filled intersecting grid is different source evidence.
            for x in 25..252 {
                for y in 42..120 {
                    ink[index(x, y)] = true;
                    luma[index(x, y)] = 10;
                }
            }
            assert!(!check(&original, &ink, &luma, &mut 250_000));
        }
    }
}
