//! Proof for a transverse measured course seating both a jamb and a partition.
use super::super::{hinged_doors::Samples, residual_hatch::Pixels};
use super::{
    BandFootprint, RasterBand, THRESHOLD, bounds, hatch, hatch_quality, native, overlaps,
    protected, range, section::same_overlap, verified_jamb_stocks,
};
use crate::input::raster::{
    PdfInput, PdfRoute, RasterCoursePatch, RasterHingedDoorReceipt, RasterJambReturnReceipt,
    RasterJambStockReceipt,
};
use crate::model::{Point, Primitive};

const MAX_RECEIPTS: usize = 16;
const MAX_SAMPLES: usize = 4_096;
const MAX_WORK: usize = 2_000_000;

#[derive(Clone, Copy)]
struct Frame {
    vertical: bool,
    along: [i32; 2],
    cross: [i32; 2],
    near: usize,
    jamb_side: usize,
    jamb_cross: [i32; 2],
    incoming_tip: i32,
    incoming_axis: f64,
    return_pen: i32,
    pitch: f64,
}

impl Frame {
    fn new(
        jamb: &RasterJambStockReceipt,
        door: &RasterHingedDoorReceipt,
        return_band: &RasterBand,
        returned: BandFootprint,
        incoming_band: &RasterBand,
        incoming: BandFootprint,
    ) -> Option<Self> {
        let witness = return_band.hatch.as_ref().filter(|h| hatch_quality(h))?;
        if incoming.vertical != door.vertical
            || returned.vertical == door.vertical
            || return_band.boundary_coverage.iter().any(|&n| n < 0.98)
            || incoming_band.boundary_coverage.iter().any(|&n| n < 0.95)
            || incoming_band.hatch.is_some()
        {
            return None;
        }
        let [a, b] = returned.along.map(native);
        let along = [a?, b?];
        let [c, d] = returned.cross.map(native);
        let cross = [c?, d?];
        let jamb_cross = range(jamb.cross)?;
        let jamb_along = range(jamb.along)?;
        let jamb_side = usize::from(!jamb.at_high_end);
        let opposite = 1 - jamb_side;
        if cross[jamb_side] != jamb_along[opposite] {
            return None;
        }
        let (near, gap) = if along[0] > jamb_cross[1] {
            (0, along[0] - jamb_cross[1])
        } else if along[1] < jamb_cross[0] {
            (1, jamb_cross[0] - along[1])
        } else {
            return None;
        };
        let length = along[1] - along[0];
        let width = cross[1] - cross[0];
        let jamb_width = jamb_cross[1] - jamb_cross[0];
        let incoming_tip = native(incoming.along[jamb_side])?;
        let insertion = (incoming_tip - cross[opposite]).abs();
        let incoming_width = incoming.cross[1] - incoming.cross[0];
        let incoming_axis = (incoming.cross[0] + incoming.cross[1]) * 0.5;
        let supported_incoming = incoming.cross[0] >= f64::from(jamb_cross[0])
            && incoming.cross[1] <= f64::from(jamb_cross[1])
            && incoming.along[1] - incoming.along[0] >= f64::from(width * 3);
        let return_pen = native(2. * witness.face_ink_half_widths_pixels[jamb_side])?;
        if !(60..=256).contains(&length)
            || !(12..=40).contains(&width)
            || gap > jamb_width / 3
            || f64::from(gap) > 2. * witness.mean_pitch_pixels
            || !(2..=4).contains(&return_pen)
            || incoming_tip <= cross[0]
            || incoming_tip >= cross[1]
            || insertion < 2
            || insertion * 5 > width * 2
            || width - insertion - return_pen < 7
            || !(5. ..=32.).contains(&incoming_width)
            || !supported_incoming
            || (incoming.along[opposite] - f64::from(cross[opposite])).abs() < f64::from(width * 2)
            || (jamb_side == 1 && incoming.along[0] >= f64::from(cross[0]))
            || (jamb_side == 0 && incoming.along[1] <= f64::from(cross[1]))
        {
            return None;
        }
        Some(Self {
            vertical: returned.vertical,
            along,
            cross,
            near,
            jamb_side,
            jamb_cross,
            incoming_tip,
            incoming_axis,
            return_pen,
            pitch: witness.mean_pitch_pixels,
        })
    }

    fn footprint(self) -> [i32; 4] {
        bounds(self.vertical, self.along, self.cross)
    }

    fn section(self, a: [i32; 2]) -> Option<[i32; 4]> {
        let box_ = bounds(self.vertical, a, [self.cross[0] - 1, self.cross[1] + 1]);
        valid_bounds(box_).then_some(box_)
    }

    fn patches(self) -> Option<[[i32; 4]; 2]> {
        let [a, z] = self.along;
        let mid = a + (z - a) / 2;
        // Two independent phase measurements cover the whole finite body.
        Some([self.section([a, mid])?, self.section([mid, z])?])
    }

    fn jamb_step(self) -> i32 {
        if self.jamb_side == 1 { 1 } else { -1 }
    }

    fn upper_row(self) -> i32 {
        self.incoming_tip - i32::from(self.jamb_side == 1)
    }

    fn lower_row(self) -> i32 {
        self.cross[self.jamb_side] - i32::from(self.jamb_side == 1)
    }

    fn jamb_column(self) -> i32 {
        self.jamb_cross[1 - self.near] - i32::from(self.near == 0)
    }

    fn shoulder(self) -> Option<[i32; 4]> {
        let cap = self.along[self.near];
        let [j, i] = [self.jamb_column(), self.incoming_axis.floor() as i32];
        let a = [cap.min(j).min(i) - 1, cap.max(j).max(i) + 2];
        let b = [self.cross[0] - 2, self.cross[1] + 2];
        let box_ = bounds(self.vertical, a, b);
        valid_bounds(box_).then_some(box_)
    }

    fn prove_course(self, p: &RasterCoursePatch, expected: [i32; 4]) -> Option<()> {
        let s = Samples::from_patch(p, expected)?;
        let [x, y, u, v] = expected;
        let (a, z, origin) = if self.vertical { (y, v, x) } else { (x, u, y) };
        let [lo, hi] = self.cross;
        let (slab, pen, inner) = if self.jamb_side == 1 {
            (
                lo..self.incoming_tip,
                hi - self.return_pen..hi,
                [self.incoming_tip, hi - self.return_pen],
            )
        } else {
            (
                self.incoming_tip..hi,
                lo..lo + self.return_pen,
                [lo + self.return_pen, self.incoming_tip],
            )
        };
        let at = |a, c| s.at(self.vertical, a, c);
        if slab
            .chain(pen)
            .any(|c| (a..z).any(|t| at(t, c).is_none_or(|(_, n)| !n)))
            || [lo - 1, hi].into_iter().any(|c| {
                (a..z)
                    .filter(|&t| at(t, c).is_none_or(|(l, _)| l < THRESHOLD))
                    .count()
                    * 10
                    > (z - a) as usize
            })
        {
            return None;
        }
        let measured = hatch::measure(
            &p.luma,
            (u - x) as usize,
            (v - y) as usize,
            self.vertical,
            0,
            (z - a) as usize,
            (inner[0] - origin) as usize,
            (inner[1] - origin) as usize,
            [0.5, 0.5],
            None,
        )?;
        (hatch_quality(&measured)
            && (measured.mean_pitch_pixels - self.pitch).abs() <= self.pitch * 0.20)
            .then_some(())
    }

    fn prove_shoulders(
        self,
        p: &RasterCoursePatch,
        jamb: &RasterCoursePatch,
        body: &RasterCoursePatch,
    ) -> Option<()> {
        let s = Samples::from_patch(p, self.shoulder()?)?;
        if !same_overlap(p, jamb) || !same_overlap(p, body) {
            return None;
        }
        let at = |a, c| s.at(self.vertical, a, c);
        let cap = self.along[self.near];
        let inside = cap - i32::from(self.near == 1);
        let head = self.incoming_axis.floor() as i32;
        let high_row = self.upper_row();
        for a in head.min(inside)..=head.max(inside) {
            // Verify two opaque lanes from inside the incoming partition to
            // inside the separate wall cap, including the native setback.
            for c in [high_row, high_row - self.jamb_step()] {
                if !at(a, c)?.1 {
                    return None;
                }
            }
        }

        let low_row = self.lower_row();
        let last = self.jamb_column();
        for a in last.min(inside)..=last.max(inside) {
            // The primary rail is opaque gray ink at every station. Its
            // adjoining row may land exactly at the dark/light threshold.
            if !at(a, low_row)?.1 || at(a, low_row - self.jamb_step())?.0 > THRESHOLD {
                return None;
            }
        }
        // The rail actually turns into the source-authenticated jamb face.
        for c in [low_row + self.jamb_step(), low_row + 2 * self.jamb_step()] {
            if at(last, c)?.0 >= THRESHOLD {
                return None;
            }
        }
        Some(())
    }

    fn validated(self, r: &RasterJambReturnReceipt, jamb: &RasterJambStockReceipt) -> Option<()> {
        let expected = self.patches()?;
        for (patch, bounds) in r.course_patches.iter().zip(expected) {
            self.prove_course(patch, bounds)?;
        }
        self.prove_shoulders(&r.shoulder_patch, &jamb.patch, &r.course_patches[self.near])
    }
}

fn valid_bounds([x, y, u, v]: [i32; 4]) -> bool {
    x >= 0
        && y >= 0
        && (1..=128).contains(&(u - x))
        && (1..=128).contains(&(v - y))
        && ((u - x) * (v - y)) as usize <= MAX_SAMPLES
}

fn capture(
    frame: Frame,
    s: Samples<'_>,
    jamb_stock_index: usize,
    incoming_band_index: usize,
) -> Option<RasterJambReturnReceipt> {
    let [a, b] = frame.patches()?;
    Some(RasterJambReturnReceipt {
        jamb_stock_index,
        incoming_band_index,
        course_patches: [s.capture(a)?, s.capture(b)?],
        shoulder_patch: s.capture(frame.shoulder()?)?,
    })
}

pub(in crate::input::raster::extract) fn collect_jamb_returns(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
) -> Vec<RasterJambReturnReceipt> {
    collect(pixels, doors, jambs, bands).unwrap_or_default()
}

fn collect(
    pixels: &Pixels<'_>,
    doors: &[RasterHingedDoorReceipt],
    jambs: &[RasterJambStockReceipt],
    bands: &[RasterBand],
) -> Option<Vec<RasterJambReturnReceipt>> {
    if jambs.is_empty() {
        return Some(vec![]);
    }
    let measured: Vec<_> = bands
        .iter()
        .map(|b| BandFootprint::read(b, pixels.scale))
        .collect();
    let band = |i: usize| bands.get(i).zip(measured.get(i).copied().flatten());
    let samples = Samples::from_pixels(pixels);
    let mut work = MAX_WORK;
    let mut result = Vec::new();
    for (i, jamb) in jambs.iter().enumerate() {
        let door = doors.get(jamb.door_index)?;
        let (b, ret) = band(jamb.return_band_index)?;
        let mut choice = None;
        let mut ambiguous = false;
        work = work.checked_sub(bands.len())?;
        for j in 0..bands.len() {
            let Some((n, inside)) = band(j) else { continue };
            let Some(f) = Frame::new(jamb, door, b, ret, n, inside) else {
                continue;
            };
            if protected(f.footprint(), doors) || protected(f.shoulder()?, doors) {
                continue;
            }
            let [x, y, u, v] = f.footprint();
            work = work.checked_sub((16 * (u - x) * (v - y)) as usize)?;
            let Some(receipt) = capture(f, samples, i, j) else {
                continue;
            };
            if f.validated(&receipt, jamb).is_none() {
                continue;
            }
            if choice.is_some() {
                ambiguous = true;
                break;
            }
            choice = Some((f, receipt));
        }
        if !ambiguous && let Some(x) = choice {
            result.push(x);
        }
    }
    if result.len() > MAX_RECEIPTS {
        return None;
    }
    Some(
        result
            .iter()
            .enumerate()
            .filter(|&(i, (x, _))| {
                result
                    .iter()
                    .enumerate()
                    .all(|(j, (y, _))| i == j || !overlaps(x.footprint(), y.footprint()))
            })
            .map(|(_, (_, r))| r.clone())
            .collect(),
    )
}

pub(crate) struct VerifiedRasterJambReturn {
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub raw_face_ids: [String; 2],
    pub incoming_start: Point,
    pub incoming_end: Point,
    pub incoming_thickness: f64,
    pub incoming_face_ids: [String; 2],
    pub jamb_start: Point,
    pub jamb_end: Point,
    pub jamb_thickness: f64,
    pub incoming_path: Vec<Point>,
    pub jamb_path: Vec<Point>,
    pub source_ids: Vec<String>,
}

pub(crate) fn verified_jamb_returns(
    info: &PdfInput,
    sources: &[Primitive],
    page_width: f64,
    page_height: f64,
) -> Result<Vec<VerifiedRasterJambReturn>, String> {
    let rows = &info.raster_jamb_returns;
    if rows.is_empty() {
        return Ok(vec![]);
    }
    if info.route != PdfRoute::Raster
        || info.analysis_revision != 1
        || info.threshold != THRESHOLD
        || info.render_width == 0
        || info.render_height == 0
        || rows.len() > MAX_RECEIPTS
        || rows
            .windows(2)
            .any(|w| w[0].jamb_stock_index >= w[1].jamb_stock_index)
    {
        return Err("invalid raster jamb return inventory".into());
    }
    let jamb_proofs = verified_jamb_stocks(info, sources, page_width, page_height)?;
    let scale = [
        page_width / info.render_width as f64,
        page_height / info.render_height as f64,
    ];
    let error = || {
        "raster jamb return disagrees with its independent stock or measured shoulders".to_string()
    };
    let mut used = vec![];
    let mut output = Vec::with_capacity(rows.len());
    for record in rows {
        let j = info
            .raster_jamb_stocks
            .get(record.jamb_stock_index)
            .ok_or_else(error)?;
        let jamb = jamb_proofs.get(record.jamb_stock_index).ok_or_else(error)?;
        let door = info
            .raster_hinged_doors
            .get(j.door_index)
            .ok_or_else(error)?;
        let b = info.bands.get(j.return_band_index).ok_or_else(error)?;
        let n = info
            .bands
            .get(record.incoming_band_index)
            .ok_or_else(error)?;
        let f = Frame::new(
            j,
            door,
            b,
            BandFootprint::read(b, scale).ok_or_else(error)?,
            n,
            BandFootprint::read(n, scale).ok_or_else(error)?,
        )
        .ok_or_else(error)?;
        let in_page = |[_, _, u, v]: [i32; 4]| {
            u as usize <= info.render_width && v as usize <= info.render_height
        };
        let boxes = f.patches().ok_or_else(error)?;
        let shoulder = f.shoulder().ok_or_else(error)?;
        if !boxes.into_iter().all(in_page)
            || !in_page(shoulder)
            || protected(f.footprint(), &info.raster_hinged_doors)
            || protected(shoulder, &info.raster_hinged_doors)
            || used.iter().any(|&x| overlaps(f.footprint(), x))
        {
            return Err(error());
        }
        f.validated(record, j).ok_or_else(error)?;
        used.push(f.footprint());

        let xy = |a: f64, c: f64| {
            if f.vertical {
                Point::new(c * scale[0], a * scale[1])
            } else {
                Point::new(a * scale[0], c * scale[1])
            }
        };
        let cap = f64::from(f.along[f.near]);
        let upper = f64::from(f.upper_row()) + 0.5;
        let lower = f64::from(f.lower_row()) + 0.5;
        let stock_station = f64::from(f.cross[f.jamb_side]);
        let turn = f64::from(f.jamb_column()) + 0.5;
        let cap_axis = [b.start, b.end][f.near];
        let jamb_end = if j.at_high_end { jamb.end } else { jamb.start };
        let incoming_path = vec![
            [n.start, n.end][f.jamb_side],
            xy(f.incoming_axis, upper),
            xy(cap, upper),
            cap_axis,
        ];
        let jamb_path = vec![
            jamb_end,
            xy(turn, stock_station),
            xy(turn, lower),
            xy(cap, lower),
            cap_axis,
        ];
        let mut source_ids = jamb.source_ids.clone();
        source_ids.extend(b.face_source_ids.iter().chain(&n.face_source_ids).cloned());
        source_ids.sort_unstable();
        source_ids.dedup();
        output.push(VerifiedRasterJambReturn {
            start: b.start,
            end: b.end,
            thickness: b.thickness,
            raw_face_ids: b.face_source_ids.clone(),
            incoming_start: n.start,
            incoming_end: n.end,
            incoming_thickness: n.thickness,
            incoming_face_ids: n.face_source_ids.clone(),
            jamb_start: jamb.start,
            jamb_end: jamb.end,
            jamb_thickness: jamb.thickness,
            incoming_path,
            jamb_path,
            source_ids,
        });
    }
    Ok(output)
}
