//! Opposed short raster returns can be credible without building-scale
//! connectivity. Their witnesses establish wall role, never additional solid.
use super::{BandIndex, corroborated_hatch, hatch_material, is_raster};
use crate::input::raster::{PdfRoute, RasterBand};
use crate::model::{PageScan, Point, Shape, Wall};
use crate::placement::{WallDecision, WallReason, WallStatus, effective_wall, overlap};
use std::collections::HashMap;

#[derive(Clone, Copy, Default)]
struct Terminal<'a> {
    cap: Option<&'a str>,
    rails: [Option<&'a str>; 2],
    anchored_rails: [Option<(&'a str, usize)>; 2],
    faces_end_here: bool,
}

struct Candidate<'a> {
    index: usize,
    band: &'a RasterBand,
    vertical: bool,
    cross: f64,
    along: [f64; 2],
    terminals: [Terminal<'a>; 2],
}

#[derive(Clone, Copy)]
struct Proof<'a> {
    anchor: usize,
    sources: [&'a str; 3],
}

fn extent(start: Point, end: Point, vertical: bool) -> [f64; 2] {
    let [a, b] = if vertical {
        [start.y, end.y]
    } else {
        [start.x, end.x]
    };
    [a.min(b), a.max(b)]
}

impl<'a> Candidate<'a> {
    fn new(index: usize, band: &'a RasterBand) -> Self {
        let vertical = band.start.x == band.end.x;
        Self {
            index,
            band,
            vertical,
            cross: if vertical { band.start.x } else { band.start.y },
            along: extent(band.start, band.end, vertical),
            terminals: [Terminal::default(); 2],
        }
    }

    fn witness(
        &mut self,
        id: &'a str,
        start: Point,
        end: Point,
        owners: &[usize],
        walls: &[Wall],
        pixel: f64,
    ) {
        let [a, b] = extent(start, end, self.vertical);
        if b - a > pixel * 0.25 {
            return;
        }
        let [lo, hi] = extent(start, end, !self.vertical);
        let width = self.band.thickness;
        let [edge_lo, edge_hi] = [self.cross - width * 0.5, self.cross + width * 0.5];
        for (terminal, &at) in self.terminals.iter_mut().zip(&self.along) {
            let offset = (a - at).abs().max((b - at).abs());
            if offset <= 1.5 * pixel
                && hi - lo <= 3. * width
                && lo <= self.cross
                && hi >= self.cross
                && hi.min(edge_hi) - lo.max(edge_lo) >= 0.75 * width
            {
                terminal.cap.get_or_insert(id);
            }
            if offset > 5. * pixel || hi - lo < 2. * width {
                continue;
            }
            for (side, tip, edge) in [(0, hi, edge_lo), (1, lo, edge_hi)] {
                if (tip - edge).abs() > 5. * pixel {
                    continue;
                }
                terminal.rails[side].get_or_insert(id);
                let corner = if self.vertical {
                    Point::new(edge, at)
                } else {
                    Point::new(at, edge)
                };
                let distance = |i: usize| {
                    let wall = &walls[i];
                    corner.distance(wall.start).min(corner.distance(wall.end))
                };
                if let Some(anchor) = owners
                    .iter()
                    .copied()
                    .filter(|&i| distance(i) <= width)
                    .min_by(|&a, &b| {
                        distance(a)
                            .total_cmp(&distance(b))
                            .then(walls[a].id.cmp(&walls[b].id))
                    })
                {
                    terminal.anchored_rails[side].get_or_insert((id, anchor));
                }
            }
        }
    }
}

fn proof<'a>(a: &Candidate<'a>, b: &Candidate<'a>, pixel: f64) -> Option<Proof<'a>> {
    let width = a.band.thickness.min(b.band.thickness);
    let max_width = a.band.thickness.max(b.band.thickness);
    let [length_a, length_b] = [a.along[1] - a.along[0], b.along[1] - b.along[0]];
    let overlap = a.along[1].min(b.along[1]) - a.along[0].max(b.along[0]);
    if a.vertical != b.vertical
        || max_width > 1.4 * width
        || !(6. * max_width..=32. * max_width).contains(&(b.cross - a.cross))
        || overlap < 0.85 * length_a.max(length_b)
    {
        return None;
    }
    let (ha, hb) = (a.band.hatch.as_ref()?, b.band.hatch.as_ref()?);
    if (ha.mean_pitch_pixels - hb.mean_pitch_pixels).abs()
        > 0.2 * ha.mean_pitch_pixels.min(hb.mean_pitch_pixels)
        || (a.band.mean_luma - b.band.mean_luma).abs() > 20.
    {
        return None;
    }
    // The open-side jambs are aligned more tightly than the shoulder ends.
    // Either axis direction is supported, independent of sheet orientation.
    for (near, far) in [(0, 1), (1, 0)] {
        if (a.along[near] - b.along[near]).abs() > 3. * pixel
            || (a.along[far] - b.along[far]).abs() > 0.5 * width
            || !a.terminals[near].faces_end_here
            || !b.terminals[near].faces_end_here
        {
            continue;
        }
        let x = a.terminals[far];
        let y = b.terminals[far];
        if let (Some(rail), Some((anchored, anchor)), Some(cap)) =
            (x.rails[0], y.anchored_rails[1], a.terminals[near].cap)
        {
            return Some(Proof {
                anchor,
                sources: [rail, anchored, cap],
            });
        }
        if let (Some(rail), Some((anchored, anchor)), Some(cap)) =
            (y.rails[1], x.anchored_rails[0], b.terminals[near].cap)
        {
            return Some(Proof {
                anchor,
                sources: [rail, anchored, cap],
            });
        }
    }
    None
}

pub(in crate::placement) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    let Some(info) = page.pdf_input.as_ref().filter(|p| {
        p.route == PdfRoute::Raster
            && p.analysis_revision == 1
            && p.render_width > 0
            && p.render_height > 0
    }) else {
        return;
    };
    if page.walls.len() > 2048 {
        return;
    }
    let pixel =
        (page.width / info.render_width as f64).max(page.height / info.render_height as f64);
    let bands = BandIndex::new(info);
    let mut anchors: HashMap<_, Vec<_>> = HashMap::new();
    let mut ready = vec![];
    for (i, d) in decisions
        .iter()
        .enumerate()
        .filter(|(_, d)| d.status == WallStatus::Ready)
    {
        let wall = effective_wall(&page.walls[i], Some(d));
        if d.reason == WallReason::ConnectedRasterMaterialFaces
            && is_raster(&wall)
            && wall.source_ids.len() == 2
            && bands
                .get(&wall)
                .is_some_and(|b| hatch_material(b) && corroborated_hatch(b))
        {
            for id in &page.walls[i].source_ids {
                anchors.entry(id.as_str()).or_default().push(i);
            }
        }
        ready.push(wall);
    }
    if anchors.is_empty() {
        return;
    }
    let mut candidates: Vec<_> = decisions
        .iter()
        .enumerate()
        .filter_map(|(i, d)| {
            let wall = &page.walls[i];
            if d.status != WallStatus::Review
                || d.reason != WallReason::RasterFacesNeedReview
                || !is_raster(wall)
            {
                return None;
            }
            let band = bands.get(wall)?;
            let length = band.start.distance(band.end);
            if band.start != wall.start
                || band.end != wall.end
                || !hatch_material(band)
                || !corroborated_hatch(band)
                || !(2.5 * band.thickness..=8. * band.thickness).contains(&length)
                || ready
                    .iter()
                    .any(|other| overlap::parallel_bodies_overlap(wall, other))
                || page
                    .openings
                    .iter()
                    .any(|o| overlap::aperture_crosses(wall, o))
            {
                return None;
            }
            Some(Candidate::new(i, band))
        })
        .collect();
    if candidates.len() < 2 || candidates.len().saturating_mul(page.primitives.len()) > 8_000_000 {
        return;
    }
    let mut lines: Vec<_> = page
        .primitives
        .iter()
        .filter_map(|p| match p.shape {
            Shape::Line { start, end } if p.id.starts_with("raster:face:") => {
                Some((p.id.as_str(), start, end))
            }
            _ => None,
        })
        .collect();
    lines.sort_unstable_by_key(|&(id, _, _)| id);
    let by_id: HashMap<_, _> = lines
        .iter()
        .map(|&(id, start, end)| (id, (start, end)))
        .collect();
    for candidate in &mut candidates {
        // The prospective aperture side must end on both recorded longitudinal
        // faces. A band clipped from strokes continuing into glazing is weaker
        // evidence than a band bounded by the actual jamb.
        let face_extents = candidate.band.face_source_ids.each_ref().map(|id| {
            by_id
                .get(id.as_str())
                .map(|&(start, end)| extent(start, end, candidate.vertical))
        });
        for (i, terminal) in candidate.terminals.iter_mut().enumerate() {
            terminal.faces_end_here = face_extents.iter().all(|end| {
                end.is_some_and(|end| (end[i] - candidate.along[i]).abs() <= 1.5 * pixel)
            });
        }
        for &(id, start, end) in &lines {
            let owners = anchors.get(id).map(Vec::as_slice).unwrap_or_default();
            candidate.witness(id, start, end, owners, &page.walls, pixel);
        }
    }
    candidates.sort_by(|a, b| a.cross.total_cmp(&b.cross));
    let mut counts = vec![0; page.walls.len()];
    let mut pairs = vec![];
    for (slot, a) in candidates.iter().enumerate() {
        for b in &candidates[slot + 1..] {
            if let Some(proof) = proof(a, b, pixel) {
                if counts[a.index] == 0 && counts[b.index] == 0 {
                    pairs.push(([a.index, b.index], proof));
                }
                counts[a.index] += 1;
                counts[b.index] += 1;
            }
        }
    }
    pairs.retain(|(walls, _)| walls.iter().all(|&i| counts[i] == 1));
    // Retain both sets for Review when distinct eligible pairs would install
    // overlapping alternatives. A previous Ready strip always has priority.
    let mut conflicted = vec![false; pairs.len()];
    for (i, ([a, b], _)) in pairs.iter().enumerate() {
        for (offset, ([c, d], _)) in pairs[i + 1..].iter().enumerate() {
            if [a, b].into_iter().any(|&x| {
                [c, d]
                    .into_iter()
                    .any(|&y| overlap::parallel_bodies_overlap(&page.walls[x], &page.walls[y]))
            }) {
                conflicted[i] = true;
                conflicted[i + 1 + offset] = true;
            }
        }
    }
    for (([a, b], proof), conflicted) in pairs.into_iter().zip(conflicted) {
        if conflicted {
            continue;
        }
        let mut sources: Vec<_> = [a, b]
            .into_iter()
            .flat_map(|i| page.walls[i].source_ids.iter().cloned())
            .chain(proof.sources.map(str::to_owned))
            .collect();
        sources.sort();
        sources.dedup();
        for (position, peer) in [(a, b), (b, a)] {
            let d = &mut decisions[position];
            d.status = WallStatus::Ready;
            d.reason = WallReason::SourceBoundedRasterOffsetPair;
            d.support_source_ids.clone_from(&sources);
            d.support_wall_ids = vec![
                page.walls[peer].id.clone(),
                page.walls[proof.anchor].id.clone(),
            ];
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::raster::RasterHatch;
    use crate::model::BodyEvidence;

    #[test]
    fn mirrored_returns_need_both_jamb_ends_and_an_independent_exterior_rail() {
        for vertical in [false, true] {
            let point = |cross, along| {
                if vertical {
                    Point::new(cross, along)
                } else {
                    Point::new(along, cross)
                }
            };
            let band = |cross, along| RasterBand {
                hatch: Some(RasterHatch {
                    crossing_count: 16,
                    mean_pitch_pixels: 4.,
                    pitch_cv: 0.18,
                    diagonal_support: 0.8,
                    stationary_correlation: -0.65,
                    contrast: 80.,
                    longitudinal_coverage: 0.94,
                    face_ink_half_widths_pixels: [1., 1.],
                }),
                face_source_ids: ["p".into(), "q".into()],
                start: point(cross, along),
                end: point(cross, along + 32.),
                thickness: 8.,
                dark_fraction: 0.52,
                mean_luma: 165.,
                boundary_coverage: [1., 1.],
                width_pixels: 8.,
            };
            let walls = [Wall {
                id: "anchored-return".into(),
                page: 1,
                start: point(106., 47.),
                end: point(132., 47.),
                thickness: 6.,
                curve: None,
                body: BodyEvidence::ParallelFaces,
                source_ids: vec![],
                requires_review: false,
            }];
            let [left, right] = [band(20., 10.), band(100., 11.)];
            let mut a = Candidate::new(1, &left);
            let mut b = Candidate::new(2, &right);
            a.witness("cap", point(16., 10.), point(24., 10.), &[], &walls, 1.);
            a.witness(
                "left rail",
                point(-8., 42.),
                point(16., 42.),
                &[],
                &walls,
                1.,
            );
            b.witness(
                "right rail",
                point(104., 43.),
                point(132., 43.),
                &[],
                &walls,
                1.,
            );
            a.terminals[0].faces_end_here = true;
            b.terminals[0].faces_end_here = true;
            assert!(proof(&a, &b, 1.).is_none());
            b.witness(
                "right rail",
                point(104., 43.),
                point(132., 43.),
                &[0],
                &walls,
                1.,
            );
            assert_eq!(proof(&a, &b, 1.).unwrap().anchor, 0);
            b.terminals[0].faces_end_here = false;
            assert!(proof(&a, &b, 1.).is_none());
        }
    }
}
