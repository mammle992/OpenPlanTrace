//! Bounded original-pixel evidence for an orthogonal hinged opening.
use super::{Face, THRESHOLD, residual_hatch::Pixels};
use crate::{
    input::raster::{PdfInput, PdfRoute, RasterCoursePatch, RasterHingedDoorReceipt},
    model::{Point, Primitive, Shape},
};
use std::collections::HashSet;

const MAX_PATCH_SIDE: i32 = 224;
const MAX_PATCH_PIXELS: usize = 32_768;
const MAX_RECEIPTS: usize = 16;
const MAX_WORK: usize = 9_000_000;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
struct Mark {
    vertical: bool,
    rough: [i32; 2],
    cross: [i32; 2],
    leaf: [i32; 2],
    hinge_at_high: bool,
    opens_high: bool,
}

impl Mark {
    fn from_receipt(r: &RasterHingedDoorReceipt) -> Option<Self> {
        let convert =
            |[a, b]: [u32; 2]| (a <= 32_768 && b <= 32_768).then_some([a as i32, b as i32]);
        Some(Self {
            vertical: r.vertical,
            rough: convert(r.rough_jambs)?,
            cross: convert(r.cross)?,
            leaf: convert(r.leaf)?,
            hinge_at_high: r.hinge_at_high,
            opens_high: r.opens_high,
        })
    }

    fn receipt(self, patch: RasterCoursePatch) -> RasterHingedDoorReceipt {
        RasterHingedDoorReceipt {
            vertical: self.vertical,
            rough_jambs: self.rough.map(|x| x as u32),
            cross: self.cross.map(|x| x as u32),
            leaf: self.leaf.map(|x| x as u32),
            hinge_at_high: self.hinge_at_high,
            opens_high: self.opens_high,
            patch,
        }
    }

    fn spans(self) -> Option<(i32, i32)> {
        let length = self.rough[1].checked_sub(self.rough[0])?;
        let width = self.cross[1].checked_sub(self.cross[0])?;
        let axis = self.leaf[0];
        let near = (self.rough[usize::from(self.hinge_at_high)] - axis).abs();
        let open = if self.opens_high {
            self.leaf[1] - self.cross[1]
        } else {
            self.cross[0] - self.leaf[1]
        };
        ((28..=162).contains(&length)
            && (8..=49).contains(&width)
            && width * 5 <= length * 3
            && axis > self.rough[0]
            && axis < self.rough[1]
            && (2..=16).contains(&near)
            && near * 5 <= length
            && near <= (width / 2).max(4)
            && open * 5 >= length * 3
            && open * 5 <= length * 6)
            .then_some((length, width))
    }

    fn bounds(self) -> Option<[i32; 4]> {
        let (_, width) = self.spans()?;
        let outside = (width / 2).clamp(6, 15) + 2;
        let along = [
            self.rough[0].checked_sub(outside)?,
            self.rough[1].checked_add(outside)?,
        ];
        let cross = [
            self.cross[0].min(self.leaf[1]).checked_sub(8)?,
            self.cross[1].max(self.leaf[1]).checked_add(9)?,
        ];
        let bounds = rect(self.vertical, along, cross);
        let [x, y, u, v] = bounds;
        (x >= 0
            && y >= 0
            && u - x <= MAX_PATCH_SIDE
            && v - y <= MAX_PATCH_SIDE
            && ((u - x) * (v - y)) as usize <= MAX_PATCH_PIXELS)
            .then_some(bounds)
    }

    fn overlaps(self, b: Self) -> bool {
        let [x, y, u, v] = rect(self.vertical, self.rough, self.cross);
        let [a, b, c, d] = rect(b.vertical, b.rough, b.cross);
        x.max(a) < u.min(c) && y.max(b) < v.min(d)
    }

    fn same_opening(self, other: Self) -> bool {
        self.vertical == other.vertical
            && self.hinge_at_high == other.hinge_at_high
            && self.opens_high == other.opens_high
            && self
                .rough
                .into_iter()
                .zip(other.rough)
                .all(|(a, b)| (a - b).abs() <= 2)
    }
}

fn rect(vertical: bool, a: [i32; 2], c: [i32; 2]) -> [i32; 4] {
    if vertical {
        [c[0], a[0], c[1], a[1]]
    } else {
        [a[0], c[0], a[1], c[1]]
    }
}

#[derive(Clone, Copy)]
pub(super) struct Samples<'a> {
    bounds: [i32; 4],
    luma: &'a [u8],
    ink: &'a [bool],
    mask: Option<&'a [bool]>,
}

impl<'a> Samples<'a> {
    pub(super) fn from_pixels(p: &'a Pixels<'_>) -> Self {
        Self {
            bounds: [0, 0, p.width as i32, p.height as i32],
            luma: p.luma,
            ink: p.ink,
            mask: p.mask,
        }
    }

    pub(super) fn from_patch(p: &'a RasterCoursePatch, bounds: [i32; 4]) -> Option<Self> {
        let [x, y, u, v] = bounds;
        let count = ((u - x) * (v - y)) as usize;
        if bounds.map(|v| v as u32) != p.bounds
            || p.luma.len() != count
            || p.neutral_ink.len() != count
            || p.luma
                .iter()
                .zip(&p.neutral_ink)
                .any(|(&l, &n)| n && l >= THRESHOLD)
        {
            return None;
        }
        Some(Self {
            bounds,
            luma: &p.luma,
            ink: &p.neutral_ink,
            mask: None,
        })
    }

    pub(super) fn xy(self, x: i32, y: i32) -> Option<(u8, bool)> {
        let [a, b, u, v] = self.bounds;
        if x < a || y < b || x >= u || y >= v {
            return None;
        }
        let i = ((y - b) * (u - a) + (x - a)) as usize;
        let covered = self.mask.is_none_or(|m| m[i]);
        Some(if covered {
            (self.luma[i], self.ink[i])
        } else {
            (255, false)
        })
    }

    pub(super) fn at(self, vertical: bool, a: i32, c: i32) -> Option<(u8, bool)> {
        if vertical {
            self.xy(c, a)
        } else {
            self.xy(a, c)
        }
    }

    pub(super) fn capture(self, bounds: [i32; 4]) -> Option<RasterCoursePatch> {
        let [x, y, u, v] = bounds;
        let count = ((u - x) * (v - y)) as usize;
        let mut luma = Vec::with_capacity(count);
        let mut neutral_ink = Vec::with_capacity(count);
        for yy in y..v {
            for xx in x..u {
                let (l, n) = self.xy(xx, yy)?;
                luma.push(l);
                neutral_ink.push(n);
            }
        }
        Some(RasterCoursePatch {
            bounds: bounds.map(|x| x as u32),
            luma,
            neutral_ink,
        })
    }
}

#[derive(Clone, Copy)]
struct Cap {
    edge: i32,
    cross: [i32; 2],
}

fn charge(work: &mut usize, cost: usize) -> Option<()> {
    let enough = *work >= cost;
    *work = work.saturating_sub(cost);
    enough.then_some(())
}

fn row(p: Samples<'_>, v: bool, a: i32, cross: [i32; 2]) -> Option<(i32, i32)> {
    let [mut dark, mut ink] = [0, 0];
    for c in cross[0]..cross[1] {
        let (l, n) = p.at(v, a, c)?;
        dark += i32::from(l < THRESHOLD);
        ink += i32::from(n);
    }
    Some((dark, ink))
}

fn cap_layout(
    p: Samples<'_>,
    v: bool,
    cross: [i32; 2],
    cap_row: i32,
    direction: i32,
) -> Option<i32> {
    let size = cross[1] - cross[0];
    let [(dark, ink), (dark2, ink2)] = [
        row(p, v, cap_row, cross)?,
        row(p, v, cap_row + direction, cross)?,
    ];
    let (away, _) = row(p, v, cap_row - direction, cross)?;
    if dark * 10 < size * 9
        || dark2 * 10 < size * 9
        || (ink + ink2) * 100 < size * 44
        || away * 5 > size * 3
    {
        return None;
    }
    Some(ink + ink2)
}

fn wall_behind_cap(p: Samples<'_>, m: Mark, high: bool, width: i32) -> Option<()> {
    let d = if high { 1 } else { -1 };
    let cap = m.rough[usize::from(high)] - i32::from(!high);
    let depth = (width / 2).clamp(6, 15);
    let mut luma = 0;
    let mut ink = 0;
    let mut supported = 0;
    for s in 2..depth {
        let (dark, n) = row(p, m.vertical, cap + s * d, m.cross)?;
        luma += dark;
        ink += n;
        supported += i32::from(dark * 4 >= width);
    }
    let area = (depth - 2) * width;
    (luma * 3 >= area && ink * 20 >= area && supported * 4 >= (depth - 2) * 3).then_some(())
}

fn clear_throat(p: Samples<'_>, m: Mark, length: i32, width: i32) -> Option<()> {
    let margin = (length / 5).max(4);
    let mut dark = 0;
    let mut neutral = 0;
    let mut blocked = 0;
    for a in m.rough[0] + margin..m.rough[1] - margin {
        let (d, n) = row(p, m.vertical, a, m.cross)?;
        dark += d;
        neutral += n;
        blocked += i32::from(n * 5 >= width * 2);
    }
    let rows = length - 2 * margin;
    let area = rows * width;
    (dark * 3 <= area && neutral * 10 <= area && blocked * 10 <= rows).then_some(())
}

fn leaf_line(p: Samples<'_>, m: Mark, hinge: i32) -> Option<()> {
    let [axis, tip] = m.leaf;
    let direction = if m.opens_high { -1 } else { 1 };
    let count = (hinge - tip).abs();
    let mut dark = 0;
    let mut ink = 0;
    let mut flank = 0;
    let mut quiet_run = 0;
    for i in 0..count {
        let c = tip + i * direction;
        let (l, n) = p.at(m.vertical, axis, c)?;
        dark += i32::from(l < THRESHOLD);
        ink += i32::from(n);
        quiet_run = if l < THRESHOLD { 0 } else { quiet_run + 1 };
        if quiet_run > 1 {
            return None;
        }
        if i > 4 && i < count - 4 {
            let (x, _) = p.at(m.vertical, axis - 2, c)?;
            let (y, _) = p.at(m.vertical, axis + 2, c)?;
            flank += i32::from(x < THRESHOLD || y < THRESHOLD);
        }
    }
    let (tip_value, tip_ink) = p.at(m.vertical, axis, tip)?;
    let tip_exterior = p.at(m.vertical, axis, tip - direction * 2)?.0;
    (dark * 100 >= count * 94
        && ink * 2 >= count
        && flank * 5 < count
        && tip_value < THRESHOLD
        && tip_ink
        && tip_exterior >= THRESHOLD)
        .then_some(())
}

fn arc_score(p: Samples<'_>, m: Mark, hinge: i32, radius: i32) -> Option<i32> {
    let axial = if m.hinge_at_high { -1. } else { 1. };
    let swing = if m.opens_high { 1. } else { -1. };
    let mut passed = 0;
    let mut dark = 0;
    let mut score = 0;
    let mut missing = 0;
    let mut longest = 0;
    for degrees in [8f64, 15., 22., 30., 38., 45., 52., 60., 68., 75., 82.] {
        let (s, c) = degrees.to_radians().sin_cos();
        let at = |r: i32| {
            p.at(
                m.vertical,
                (m.leaf[0] as f64 + axial * s * r as f64).round() as i32,
                (hinge as f64 + swing * c * r as f64).round() as i32,
            )
            .map(|s| i32::from(s.0))
        };
        let mut best = (i32::MAX, 0, 0);
        for offset in -2i32..=2 {
            let value = at(radius + offset)?;
            let quality = value + 5 * offset.abs();
            if quality < best.0 {
                best = (quality, offset, value);
            }
        }
        let before = at(radius + best.1 - 3)?;
        let after = at(radius + best.1 + 3)?;
        let contrast = (before + after) / 2 - best.2;
        if best.2 <= 214 && contrast >= 22 {
            passed += 1;
            dark += i32::from(best.2 < i32::from(THRESHOLD));
            score += contrast.min(110) - best.1.abs() * 5;
            missing = 0;
        } else {
            missing += 1;
            longest = longest.max(missing);
        }
    }
    (passed >= 9 && dark >= 3 && longest <= 1).then_some(score + passed * 80)
}

#[derive(Clone, Copy)]
struct Verified {
    hinge: i32,
    score: i32,
}

fn verify(p: Samples<'_>, m: Mark, work: &mut usize) -> Option<Verified> {
    let (length, width) = m.spans()?;
    charge(work, (length * width * 2) as usize)?;
    let cap_support = cap_layout(p, m.vertical, m.cross, m.rough[0] - 1, -1)?
        + cap_layout(p, m.vertical, m.cross, m.rough[1], 1)?;
    wall_behind_cap(p, m, false, width)?;
    wall_behind_cap(p, m, true, width)?;
    clear_throat(p, m, length, width)?;
    let mut best: Option<Verified> = None;
    let mut competing = Vec::new();
    for hinge in m.cross[0] + 2..m.cross[1] - 2 {
        let radius = (hinge - m.leaf[1]).abs();
        let latch = m.leaf[0] + if m.hinge_at_high { -radius } else { radius };
        let far = m.rough[usize::from(!m.hinge_at_high)];
        if radius * 20 < length * 15
            || radius * 20 > length * 21
            || (latch - far).abs() * 5 > length
            || (latch - far).abs() > (width / 2).max(4)
        {
            continue;
        }
        charge(work, radius as usize + 12 * 9)?;
        if leaf_line(p, m, hinge).is_none() {
            continue;
        }
        if let Some(score) = arc_score(p, m, hinge, radius) {
            let result = Verified {
                hinge,
                score: score + cap_support,
            };
            if best.is_none_or(|x| result.score > x.score) {
                best = Some(result);
            }
            competing.push(result);
        }
    }
    let best = best?;
    (!competing
        .iter()
        .any(|r| (r.hinge - best.hinge).abs() > 3 && (best.score - r.score) < 55))
    .then_some(best)
}

fn caps(
    p: Samples<'_>,
    v: bool,
    along: [i32; 2],
    cross: [i32; 2],
    way: i32,
    work: &mut usize,
) -> Option<Vec<Cap>> {
    let mut result = Vec::new();
    charge(
        work,
        ((along[1] - along[0]).max(0) * (cross[1] - cross[0]).max(0) * 3) as usize,
    )?;
    for a in along[0]..along[1] {
        let mut first = None;
        for c in cross[0]..=cross[1] {
            let dark = c < cross[1] && p.at(v, a, c).is_some_and(|s| s.0 < THRESHOLD);
            if dark {
                first.get_or_insert(c);
            } else if let Some(lo) = first.take() {
                if (8..=49).contains(&(c - lo)) && cap_layout(p, v, [lo, c], a, way).is_some() {
                    result.push(Cap {
                        edge: a + i32::from(way < 0),
                        cross: [lo, c],
                    });
                }
            }
        }
    }
    Some(result)
}

fn trace(p: Samples<'_>, v: bool, a: i32, tip: i32, direction: i32) -> Option<i32> {
    if !p.at(v, a, tip)?.1 || p.at(v, a, tip - direction * 2)?.0 < THRESHOLD {
        return None;
    }
    let mut run = 0;
    let mut quiet = 0;
    for i in 0..173 {
        if p.at(v, a, tip + direction * i)
            .is_none_or(|s| s.0 >= THRESHOLD)
        {
            quiet += 1;
            if quiet >= 2 {
                break;
            }
        } else {
            quiet = 0;
            run = i + 1;
        }
    }
    (32..=169).contains(&run).then_some(run)
}

fn proposals(
    samples: Samples<'_>,
    faces: &[Face],
    work: &mut usize,
) -> Option<Vec<(Mark, Verified)>> {
    let mut projections = Vec::new();
    let mut seen = HashSet::new();
    for face in faces {
        let seed_length = face.end - face.start;
        if !((24. ..=142.).contains(&seed_length) && face.ink_half_width <= 1.5) {
            continue;
        }
        let axis = face.coordinate.floor() as i32;
        let vertical = !face.vertical;
        for opens_high in [false, true] {
            let tip = if opens_high {
                face.end.floor() as i32 - 1
            } else {
                face.start.ceil() as i32
            };
            let direction = if opens_high { -1 } else { 1 };
            charge(work, 173)?;
            let Some(run) = trace(samples, vertical, axis, tip, direction) else {
                continue;
            };
            if (run as f64) < seed_length - 3. {
                continue;
            }
            let [c, d] = [
                tip + direction * (run * 58 / 100),
                tip + direction * (run * 125 / 100),
            ];
            let cross = [c.min(d), c.max(d) + 1];
            let near_range = (run / 6).clamp(5, 16);
            for way in [-1, 1] {
                let [a, b] = [axis + way * 2, axis + way * near_range];
                let near = caps(
                    samples,
                    vertical,
                    [a.min(b), a.max(b) + 1],
                    cross,
                    way,
                    work,
                )?;
                if near.is_empty() {
                    continue;
                }
                let [a, b] = [
                    axis - way * (run * 72 / 100),
                    axis - way * (run * 116 / 100),
                ];
                let far = caps(
                    samples,
                    vertical,
                    [a.min(b), a.max(b) + 1],
                    cross,
                    -way,
                    work,
                )?;
                for n in &near {
                    for f in &far {
                        charge(work, 20)?;
                        let width = (n.cross[1] - n.cross[0]).max(f.cross[1] - f.cross[0]);
                        let caps_cross = [n.cross[0].max(f.cross[0]), n.cross[1].min(f.cross[1])];
                        if (caps_cross[1] - caps_cross[0]) * 4 < width * 3 {
                            continue;
                        }
                        let m = Mark {
                            vertical,
                            rough: [n.edge.min(f.edge), n.edge.max(f.edge)],
                            cross: caps_cross,
                            leaf: [axis, tip],
                            hinge_at_high: way > 0,
                            opens_high,
                        };
                        if m.bounds().is_none() || !seen.insert(m) {
                            continue;
                        }
                        if let Some(proof) = verify(samples, m, work) {
                            projections.push((m, proof));
                            if projections.len() > MAX_RECEIPTS * 8 {
                                return None;
                            }
                        } else if *work == 0 {
                            return None;
                        }
                    }
                }
            }
        }
    }
    Some(projections)
}

pub(super) fn collect(pixels: &Pixels<'_>, faces: &[Face]) -> Vec<RasterHingedDoorReceipt> {
    let samples = Samples::from_pixels(pixels);
    let mut work = MAX_WORK;
    let Some(mut proposals) = proposals(samples, faces, &mut work) else {
        return vec![];
    };
    proposals.retain(|(mark, _)| {
        mark.bounds().is_some_and(|[x, y, u, v]| {
            samples.xy(x, y).is_some() && samples.xy(u - 1, v - 1).is_some()
        })
    });
    let ambiguous: Vec<_> = proposals
        .iter()
        .map(|(m, _)| {
            proposals
                .iter()
                .any(|(n, _)| m.overlaps(*n) && !m.same_opening(*n))
        })
        .collect();
    let mut ordered: Vec<_> = proposals
        .into_iter()
        .zip(ambiguous)
        .filter_map(|(p, a)| (!a).then_some(p))
        .collect();
    ordered.sort_unstable_by(|(a, x), (b, y)| y.score.cmp(&x.score).then(a.cmp(b)));
    let mut kept = Vec::<Mark>::new();
    for (m, _) in ordered {
        if kept.iter().any(|x| m.overlaps(*x)) {
            continue;
        }
        kept.push(m);
        if kept.len() > MAX_RECEIPTS {
            return vec![];
        }
    }
    kept.sort_unstable();
    kept.into_iter()
        .filter_map(|m| {
            let patch = samples.capture(m.bounds()?)?;
            Some(m.receipt(patch))
        })
        .collect()
}

pub(crate) struct VerifiedRasterHingedDoor {
    pub start: Point,
    pub end: Point,
    pub hinge: Point,
    pub leaf_tip: Point,
    pub wall_thickness: f64,
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_hinged_doors(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterHingedDoor>, String> {
    let records = &info.raster_hinged_doors;
    if records.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || records.len() > MAX_RECEIPTS
        || info.render_width == 0
        || info.render_height == 0
        || !page_width.is_finite()
        || !page_height.is_finite()
        || page_width <= 0.
        || page_height <= 0.
    {
        return Err("invalid hinged raster opening inventory".into());
    }
    let scale = [
        page_width / info.render_width as f64,
        page_height / info.render_height as f64,
    ];
    let error =
        || "hinged raster opening disagrees with measured jambs, leaf or swing pixels".to_string();
    let mut output = Vec::with_capacity(records.len());
    let mut previous = Vec::<Mark>::new();
    let mut work = MAX_WORK;
    for record in records {
        let mark = Mark::from_receipt(record).ok_or_else(error)?;
        let bounds = mark.bounds().ok_or_else(error)?;
        let [x, y, u, v] = bounds;
        if u as usize > info.render_width
            || v as usize > info.render_height
            || previous.last().is_some_and(|m| *m >= mark)
            || previous.iter().any(|&m| m.overlaps(mark))
        {
            return Err(error());
        }
        let crop = Samples::from_patch(&record.patch, bounds).ok_or_else(error)?;
        let proof = verify(crop, mark, &mut work).ok_or_else(error)?;
        previous.push(mark);
        let axis = usize::from(mark.vertical);
        let point = |a: f64, c: f64| {
            if mark.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let midpoint = (mark.cross[0] + mark.cross[1]) as f64 * 0.5;
        let (a, b) = (
            Point::new(x as f64 * scale[0], y as f64 * scale[1]),
            Point::new(u as f64 * scale[0], v as f64 * scale[1]),
        );
        let mut source_ids: Vec<_> = sources
            .iter()
            .filter_map(|p| {
                let Shape::Image { min, max } = p.shape else {
                    return None;
                };
                (info.source_image_ids.binary_search(&p.id).is_ok()
                    && min.x.max(a.x) < max.x.min(b.x)
                    && min.y.max(a.y) < max.y.min(b.y))
                .then_some(p.id.clone())
            })
            .collect();
        source_ids.sort_unstable();
        source_ids.dedup();
        if source_ids.is_empty() {
            return Err(error());
        }
        output.push(VerifiedRasterHingedDoor {
            start: point(mark.rough[0] as f64, midpoint),
            end: point(mark.rough[1] as f64, midpoint),
            hinge: point(mark.leaf[0] as f64 + 0.5, proof.hinge as f64 + 0.5),
            leaf_tip: point(mark.leaf[0] as f64 + 0.5, mark.leaf[1] as f64 + 0.5),
            wall_thickness: (mark.cross[1] - mark.cross[0]) as f64 * scale[1 - axis],
            source_ids,
        });
    }
    Ok(output)
}
