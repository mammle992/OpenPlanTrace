//! Pixel-derived strips require material contrast and a building-scale connected network.
mod preview10;

use super::{WallDecision, WallReason, WallStatus, overlap};
use crate::{input::raster::RasterBand, model::*};
use std::collections::{HashMap, HashSet, VecDeque};

const PREFIX: &str = "pixel-band:";
pub(crate) fn is_raster(w: &Wall) -> bool {
    w.id.starts_with(PREFIX)
}

pub(crate) fn observe(page: &Page, walls: &mut Vec<Wall>, opts: &Options) {
    let Some(info) = &page.pdf_input else { return };
    let mut candidates: Vec<_> = info
        .bands
        .iter()
        .enumerate()
        .filter(|(_, b)| {
            b.thickness >= opts.min_wall_pair_separation
                && b.thickness <= opts.max_wall_pair_separation
                && b.start.distance(b.end) >= opts.min_wall_fragment_length
        })
        .collect();
    candidates.sort_by(|(i, a), (j, b)| {
        material(b)
            .cmp(&material(a))
            .then_with(|| hatch_material(b).cmp(&hatch_material(a)))
            .then_with(|| quality(b).total_cmp(&quality(a)))
            .then(i.cmp(j))
    });
    let mut owners: Vec<Wall> = vec![];
    for (index, b) in candidates {
        let mut sources = b.face_source_ids.to_vec();
        sources.sort();
        let w = Wall {
            id: format!("{PREFIX}{}:{}", page.number, index + 1),
            page: page.number,
            start: b.start,
            end: b.end,
            thickness: b.thickness,
            curve: None,
            body: BodyEvidence::ParallelFaces,
            source_ids: sources,
            requires_review: true,
        };
        // Keep the measured remainder when an adjacent interpretation shares
        // only an end. A tiny overlap must not erase an entire real wall.
        let axis = (w.end - w.start).unit();
        let length = w.start.distance(w.end);
        let eps = super::roundoff(&w);
        let mut cuts: Vec<_> = owners
            .iter()
            .filter(|other| {
                overlap::parallel_bodies_overlap(&w, other)
                    && other.thickness * 0.5 + eps
                        >= (other.start - w.start).cross(axis).abs() + w.thickness * 0.5
            })
            .map(|other| {
                let x = (other.start - w.start).dot(axis);
                let y = (other.end - w.start).dot(axis);
                (x.min(y).max(0.), x.max(y).min(length))
            })
            .filter(|(a, b)| b - a > eps)
            .collect();
        cuts.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.total_cmp(&b.1)));
        let mut from = 0.;
        let mut pieces = vec![];
        for (a, b) in cuts {
            if a > from + eps {
                pieces.push((from, a));
            }
            from = from.max(b);
        }
        if from < length - eps {
            pieces.push((from, length));
        }
        let count = pieces.len();
        let qualified_material = material(b);
        for (piece, (a, b)) in pieces.into_iter().enumerate() {
            // The minimum source-span test already passed on the complete
            // measured band. Ownership cuts must keep its short uncovered
            // terminal pieces too, just as an opening cut preserves a pier.
            if b - a <= eps {
                continue;
            }
            let mut part = w.clone();
            if count > 1 {
                part.id = format!("{}:part:{}", w.id, piece + 1);
            }
            part.start = if a <= eps {
                w.start
            } else {
                w.start + axis * a
            };
            part.end = if length - b <= eps {
                w.end
            } else {
                w.start + axis * b
            };
            // Partial-width conflicts remain explicit observations for review;
            // they cannot erase material that the dominant strip does not cover.
            if qualified_material
                && !owners
                    .iter()
                    .any(|other| overlap::parallel_bodies_overlap(&part, other))
            {
                owners.push(part.clone());
            }
            walls.push(part);
        }
    }
}

fn quality(b: &RasterBand) -> f64 {
    b.start.distance(b.end)
        * b.thickness
        * (255. - b.mean_luma).max(0.)
        * b.boundary_coverage[0].min(b.boundary_coverage[1])
}

fn band_key(start: Point, end: Point, thickness: f64) -> [u64; 5] {
    [
        start.x.to_bits(),
        start.y.to_bits(),
        end.x.to_bits(),
        end.y.to_bits(),
        thickness.to_bits(),
    ]
}
struct BandIndex<'a> {
    exact: HashMap<[u64; 5], Vec<&'a RasterBand>>,
    sources: HashMap<[String; 2], Vec<&'a RasterBand>>,
}
impl<'a> BandIndex<'a> {
    fn new(info: &'a crate::input::raster::PdfInput) -> Self {
        let mut index = HashMap::<_, Vec<_>>::new();
        let mut sources = HashMap::<_, Vec<_>>::new();
        for b in &info.bands {
            index
                .entry(band_key(b.start, b.end, b.thickness))
                .or_default()
                .push(b);
            let mut pair = b.face_source_ids.clone();
            pair.sort();
            sources.entry(pair).or_default().push(b);
        }
        Self {
            exact: index,
            sources,
        }
    }
    fn get(&self, w: &Wall) -> Option<&'a RasterBand> {
        let proof_order = |b: &&RasterBand| (material(b), hatch_material(b));
        if let Some(b) = self
            .exact
            .get(&band_key(w.start, w.end, w.thickness))
            .and_then(|v| {
                v.iter()
                    .copied()
                    .filter(|b| b.face_source_ids.iter().all(|id| w.source_ids.contains(id)))
                    .max_by_key(proof_order)
            })
        {
            return Some(b);
        }
        let pair: [String; 2] = w.source_ids.clone().try_into().ok()?;
        self.sources
            .get(&pair)?
            .iter()
            .copied()
            .filter(|b| {
                let axis = (b.end - b.start).unit();
                let length = b.start.distance(b.end);
                let eps = super::roundoff(w);
                let x = (w.start - b.start).dot(axis);
                let y = (w.end - b.start).dot(axis);
                (b.thickness - w.thickness).abs() <= eps
                    && (w.start - b.start).cross(axis).abs() <= eps
                    && (w.end - b.start).cross(axis).abs() <= eps
                    && x >= -eps
                    && y <= length + eps
                    && y > x
            })
            .max_by_key(proof_order)
    }
}

fn hatch_material(b: &RasterBand) -> bool {
    b.width_pixels >= 6.
        && b.boundary_coverage.iter().all(|v| *v >= 0.8)
        && b.hatch.as_ref().is_some_and(|h| {
            h.crossing_count >= 6
                && b.start.distance(b.end)
                    >= b.thickness
                        * (1. - h.face_ink_half_widths_pixels.iter().sum::<f64>() / b.width_pixels)
                        * 1.5
                && h.pitch_cv <= 0.35
                && h.mean_pitch_pixels >= 2.
                && h.mean_pitch_pixels <= b.width_pixels * 2.
                && h.diagonal_support >= 0.6
                && h.stationary_correlation <= -0.1
                && h.longitudinal_coverage >= 0.8
                && h.contrast >= 24.
        })
}

fn material(b: &RasterBand) -> bool {
    hatch_material(b)
        || b.width_pixels >= 3.
            && b.boundary_coverage.iter().all(|v| *v >= 0.8)
            && b.start.distance(b.end) >= b.thickness * 6.0
            && (b.dark_fraction >= 0.28 && b.mean_luma <= 220. || b.mean_luma <= 175.)
}

struct MeasuredContact {
    position: Point,
    axis_points: [Point; 2],
}

/// Exact contact of the already measured finite material rectangles. A point
/// and both axis connectors lie in their owning strips; no snapping or gap fill.
fn measured_contact(a: &Wall, b: &Wall) -> Option<MeasuredContact> {
    let bounds = |w: &Wall| {
        if w.start.y == w.end.y {
            Some((
                Point::new(w.start.x.min(w.end.x), w.start.y - w.thickness * 0.5),
                Point::new(w.start.x.max(w.end.x), w.start.y + w.thickness * 0.5),
            ))
        } else if w.start.x == w.end.x {
            Some((
                Point::new(w.start.x - w.thickness * 0.5, w.start.y.min(w.end.y)),
                Point::new(w.start.x + w.thickness * 0.5, w.start.y.max(w.end.y)),
            ))
        } else {
            None
        }
    };
    let (al, ah) = bounds(a)?;
    let (bl, bh) = bounds(b)?;
    let low = Point::new(al.x.max(bl.x), al.y.max(bl.y));
    let high = Point::new(ah.x.min(bh.x), ah.y.min(bh.y));
    if low.x > high.x || low.y > high.y {
        return None;
    }
    let center = (low + high) * 0.5;
    let project = |w: &Wall| {
        let q = on_segment(center, w.start, w.end).0;
        let eps = super::roundoff(w);
        if q.distance(w.start) <= eps {
            w.start
        } else if q.distance(w.end) <= eps {
            w.end
        } else {
            q
        }
    };
    Some(MeasuredContact {
        position: center,
        axis_points: [project(a), project(b)],
    })
}

/// Actual material contacts and near perpendicular joins establish context.
/// Separated collinear door flanks are excluded here and never receive material.
fn contact(a: &Wall, b: &Wall, pixel: f64) -> bool {
    if measured_contact(a, b).is_some() {
        return true;
    }
    let u = (a.end - a.start).unit();
    let v = (b.end - b.start).unit();
    if u.dot(v).abs() > 0.05 {
        return false;
    }
    let at = a.start + u * ((b.start - a.start).cross(v) / u.cross(v));
    let on = |w: &Wall, axis: Point, reach: f64| {
        let t = (at - w.start).dot(axis);
        let l = w.start.distance(w.end);
        t >= -reach && t <= l + reach
    };
    // A measured T or cross can lie inside both finite strips; requiring a
    // centerline endpoint here would disconnect a partition that meets a wall
    // at its outer face. This establishes context only, never new material.
    on(a, u, b.thickness * 0.5 + pixel * 1.5) && on(b, v, a.thickness * 0.5 + pixel * 1.5)
}

/// Context can pass across a measured empty aperture; placement solids never do.
fn related_flanks(a: &Wall, b: &Wall, pixel: f64, textured: bool) -> bool {
    let axis = (a.end - a.start).unit();
    let len = a.start.distance(a.end);
    if axis.dot((b.end - b.start).unit()).abs() < 0.999
        || (b.start - a.start).cross(axis).abs()
            > if textured {
                a.thickness.min(b.thickness) * 0.20 + pixel
            } else {
                pixel * 1.5
            }
        || (a.thickness - b.thickness).abs() > a.thickness.min(b.thickness) * 0.35 + pixel
    {
        return false;
    }
    let x = (b.start - a.start).dot(axis);
    let y = (b.end - a.start).dot(axis);
    let gap = if x.min(y) > len {
        x.min(y) - len
    } else if x.max(y) < 0. {
        -x.max(y)
    } else {
        0.
    };
    gap > 0. && gap <= a.thickness.min(b.thickness) * 10.
}

const MAX_CONTEXT_LINKS: usize = 8192;

/// Collect a bounded incidence list before constructing adjacency. A dense
/// grid can have quadratic contacts even with a modest number of source walls.
fn context_links(
    walls: &[Wall],
    eligible: &[usize],
    pixel: f64,
    textured: &HashSet<usize>,
) -> Option<Vec<(usize, usize)>> {
    let mut links = Vec::new();
    for (position, &i) in eligible.iter().enumerate() {
        for &j in &eligible[position + 1..] {
            if contact(&walls[i], &walls[j], pixel)
                || related_flanks(
                    &walls[i],
                    &walls[j],
                    pixel,
                    textured.contains(&i) && textured.contains(&j),
                )
            {
                if links.len() == MAX_CONTEXT_LINKS {
                    return None;
                }
                links.push((i, j));
            }
        }
    }
    Some(links)
}

pub(crate) fn select(page: &PageScan, out: &mut [WallDecision]) {
    if page
        .pdf_input
        .as_ref()
        .is_some_and(|p| p.analysis_revision == 0)
    {
        return preview10::select(page, out);
    }

    let Some(info) = &page.pdf_input else { return };
    if info.render_width == 0 {
        return;
    }
    let pixel = page.width / info.render_width as f64;
    let bands = BandIndex::new(info);
    let index: HashMap<_, _> = page
        .walls
        .iter()
        .enumerate()
        .map(|(i, w)| (w.id.as_str(), i))
        .collect();
    let protected: Vec<_> = out
        .iter()
        .filter(|d| d.status == WallStatus::Ready)
        .filter_map(|d| index.get(d.wall_id.as_str()).copied())
        .filter(|&i| !is_raster(&page.walls[i]))
        .collect();
    let mut eligible: Vec<_> = page
        .walls
        .iter()
        .enumerate()
        .filter(|(_, w)| {
            is_raster(w)
                && bands.get(w).is_some_and(material)
                && !protected
                    .iter()
                    .any(|&i| overlap::parallel_bodies_overlap(w, &page.walls[i]))
                && !page
                    .openings
                    .iter()
                    .any(|o| overlap::aperture_crosses(w, o))
        })
        .map(|(i, _)| i)
        .collect();
    if eligible.len() > 2048 {
        for d in out {
            if index
                .get(d.wall_id.as_str())
                .is_some_and(|&i| is_raster(&page.walls[i]))
            {
                d.status = WallStatus::Review;
                d.reason = WallReason::RasterContextBudgetExceeded;
            }
        }
        return;
    }
    // Resolve material conflicts by source evidence, retaining competing
    // observations under review rather than overlapping Ready barriers.
    eligible.sort_by(|&a, &b| {
        let aa = bands.get(&page.walls[a]).unwrap();
        let bb = bands.get(&page.walls[b]).unwrap();
        hatch_material(bb)
            .cmp(&hatch_material(aa))
            .then_with(|| quality(bb).total_cmp(&quality(aa)))
            .then(a.cmp(&b))
    });
    let mut chosen: Vec<usize> = vec![];
    for i in eligible {
        if !chosen
            .iter()
            .any(|&j| overlap::parallel_bodies_overlap(&page.walls[i], &page.walls[j]))
        {
            chosen.push(i);
        }
    }
    let eligible = chosen;
    let textured: HashSet<_> = eligible
        .iter()
        .copied()
        .filter(|&i| bands.get(&page.walls[i]).is_some_and(hatch_material))
        .collect();
    let Some(incidences) = context_links(&page.walls, &eligible, pixel, &textured) else {
        for d in out {
            if index
                .get(d.wall_id.as_str())
                .is_some_and(|&i| is_raster(&page.walls[i]))
            {
                d.status = WallStatus::Review;
                d.reason = WallReason::RasterContextBudgetExceeded;
            }
        }
        return;
    };
    let mut links = vec![vec![]; page.walls.len()];
    for (i, j) in incidences {
        links[i].push(j);
        links[j].push(i);
    }
    let mut seen = HashSet::new();
    let mut accepted = HashSet::new();
    let mut components = HashMap::new();
    for &i in &eligible {
        if !seen.insert(i) {
            continue;
        }
        let mut queue = VecDeque::from([i]);
        let mut component = vec![];
        while let Some(j) = queue.pop_front() {
            component.push(j);
            for &k in &links[j] {
                if seen.insert(k) {
                    queue.push_back(k);
                }
            }
        }
        let mut lo = Point::new(f64::INFINITY, f64::INFINITY);
        let mut hi = Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY);
        for &j in &component {
            for p in [page.walls[j].start, page.walls[j].end] {
                lo.x = lo.x.min(p.x);
                lo.y = lo.y.min(p.y);
                hi.x = hi.x.max(p.x);
                hi.y = hi.y.max(p.y);
            }
        }
        let extent = hi - lo;
        for &j in &component {
            components.insert(j, (i, component.len()));
        }
        let mut widths: Vec<_> = component.iter().map(|&j| page.walls[j].thickness).collect();
        widths.sort_by(f64::total_cmp);
        let width = widths[widths.len() / 2];
        let length: f64 = component
            .iter()
            .map(|&j| page.walls[j].start.distance(page.walls[j].end))
            .sum();
        // Architectural scale follows the measured material width, independent
        // of sheet margins or whether only one wing of a floor is visible.
        // A collinear set of piers still has no two-dimensional support.
        let physical_scale = extent.x.max(extent.y) >= 16. * width
            && extent.x.min(extent.y) >= 6. * width
            && extent.x * extent.y >= 150. * width * width
            && length >= 30. * width;
        let sheet_scale = extent.x.max(extent.y) >= page.width.max(page.height) * 0.20
            && extent.x * extent.y >= page.width * page.height * 0.025;
        let hatch_count = component
            .iter()
            .filter_map(|&j| bands.get(&page.walls[j]))
            .filter(|b| hatch_material(b))
            .map(|b| {
                let mut pair = b.face_source_ids.clone();
                pair.sort();
                pair
            })
            .collect::<HashSet<_>>()
            .len();
        let has_turn = component.iter().enumerate().any(|(k, &a)| {
            component[k + 1..].iter().any(|&b| {
                let x = &page.walls[a];
                let y = &page.walls[b];
                (x.end - x.start).unit().dot((y.end - y.start).unit()).abs() < 0.05
                    && contact(x, y, pixel)
            })
        });
        // Repeated insulation has specific material evidence even on short
        // window piers. Its assembly can contain large empty apertures, so
        // require a finite two-dimensional turn and repeated independent
        // measured strips, rather than demanding mostly uninterrupted solids.
        let insulated_assembly = hatch_count >= 2
            && has_turn
            && extent.x.max(extent.y) >= 12. * width
            && extent.x.min(extent.y) >= 3. * width
            && length >= 10. * width;
        if component.len() >= 3 && (physical_scale || sheet_scale || insulated_assembly) {
            accepted.extend(component);
        }
    }
    for d in out {
        let Some(&i) = index.get(d.wall_id.as_str()) else {
            continue;
        };
        if !is_raster(&page.walls[i]) {
            continue;
        }
        if let Some(&(group, count)) = components.get(&i) {
            d.component_id = format!("p{}:raster-component:{}", page.page_number, group + 1);
            d.component_wall_count = count;
        }
        // Raster strips do not inherit authored-vector placement rules.
        d.status = WallStatus::Review;
        d.reason = WallReason::RasterFacesNeedReview;
        if accepted.contains(&i) {
            d.status = WallStatus::Ready;
            d.reason = WallReason::ConnectedRasterMaterialFaces;
            d.support_source_ids = page.walls[i].source_ids.clone();
            d.support_wall_ids = links[i].iter().map(|&j| page.walls[j].id.clone()).collect();
        }
    }
}

pub(crate) fn validate_observations(page: &PageScan) -> Result<(), String> {
    if page
        .pdf_input
        .as_ref()
        .is_some_and(|p| p.analysis_revision == 0)
    {
        return preview10::validate_observations(page);
    }

    let Some(info) = page.pdf_input.as_ref() else {
        return if page.walls.iter().any(is_raster) {
            Err("missing raster band inventory".into())
        } else {
            Ok(())
        };
    };
    let bands = BandIndex::new(info);
    for w in page.walls.iter().filter(|w| is_raster(w)) {
        if w.body != BodyEvidence::ParallelFaces || w.curve.is_some() || bands.get(w).is_none() {
            return Err("pixel-derived wall disagrees with measured raster band".into());
        }
    }
    Ok(())
}

/// Connect measured exterior turns only when two opposite face corners are
/// visible in both participants. Proximity alone cannot fill the corner.
pub(crate) fn corner_joints(page: &PageScan) -> Vec<super::stroked::CornerJoint> {
    if page
        .pdf_input
        .as_ref()
        .is_some_and(|p| p.analysis_revision == 0)
    {
        return preview10::corner_joints(page);
    }

    let Some(info) = &page.pdf_input else {
        return vec![];
    };
    if info.render_width == 0 {
        return vec![];
    }
    let tolerance = page.width / info.render_width as f64 * 2.;
    let ready: HashSet<_> = page
        .placement_decisions
        .iter()
        .flatten()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.wall_id.as_str())
        .collect();
    let walls: Vec<_> = page
        .walls
        .iter()
        .filter(|w| is_raster(w) && ready.contains(w.id.as_str()))
        .collect();
    let sources: HashMap<_, _> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let has_end = |w: &Wall, p: Point| {
        w.source_ids.iter().any(|id| {
            sources.get(id.as_str()).is_some_and(|s| match s.shape {
                Shape::Line { start, end } => {
                    start.distance(p) <= tolerance || end.distance(p) <= tolerance
                }
                _ => false,
            })
        })
    };
    let mut out = vec![];
    for (position, &a) in walls.iter().enumerate() {
        for &b in &walls[position + 1..] {
            let u = (a.end - a.start).unit();
            let v = (b.end - b.start).unit();
            if let Some(proof) = measured_contact(a, b) {
                let center = proof.position;
                let na = Point::new(-u.y, u.x) * (a.thickness * 0.5);
                // This internal quadrilateral only carries the shared center;
                // the projection emits no polygon for a material contact.
                let nb = if u.dot(v).abs() < 0.001 {
                    Point::new(-v.y, v.x) * (b.thickness * 0.5)
                } else {
                    u * (b.thickness * 0.5)
                };
                let mut ids = a.source_ids.clone();
                ids.extend(b.source_ids.iter().cloned());
                ids.sort();
                ids.dedup();
                out.push(super::stroked::CornerJoint {
                    wall_ids: [a.id.clone(), b.id.clone()],
                    source_ids: ids,
                    polygon: [
                        center + na + nb,
                        center - na + nb,
                        center - na - nb,
                        center + na - nb,
                    ],
                    adds_material: false,
                    contact_points: Some(proof.axis_points),
                    ends: [
                        usize::from(center.distance(a.end) < center.distance(a.start)),
                        usize::from(center.distance(b.end) < center.distance(b.start)),
                    ],
                });
                continue;
            }
            if u.dot(v).abs() > 0.001 {
                continue;
            }
            let at = a.start + u * ((b.start - a.start).cross(v) / u.cross(v));
            let end = |w: &Wall, axis: Point, reach: f64| {
                let d = [w.start.distance(at), w.end.distance(at)];
                let e = usize::from(d[1] < d[0]);
                let outward = if e == 0 {
                    (w.start - at).dot(axis)
                } else {
                    (at - w.end).dot(axis)
                };
                (d[e] <= reach + tolerance).then_some((e, outward >= -tolerance))
            };
            let (Some((ea, outside_a)), Some((eb, outside_b))) =
                (end(a, u, b.thickness * 0.5), end(b, v, a.thickness * 0.5))
            else {
                continue;
            };
            let na = Point::new(-u.y, u.x) * (a.thickness * 0.5);
            let nb = Point::new(-v.y, v.x) * (b.thickness * 0.5);
            let polygon = [at + na + nb, at - na + nb, at - na - nb, at + na - nb];
            let contains = |w: &Wall, p: Point| {
                let axis = (w.end - w.start).unit();
                let q = p - w.start;
                let eps = super::roundoff(w);
                q.dot(axis) >= -eps
                    && q.dot(axis) <= w.start.distance(w.end) + eps
                    && q.cross(axis).abs() <= w.thickness * 0.5 + eps
            };
            let already_material = polygon.iter().all(|&p| contains(a, p) && contains(b, p));
            if !already_material
                && (!(outside_a && outside_b)
                    || ![[0, 2], [1, 3]].into_iter().any(|pair| {
                        pair.into_iter()
                            .all(|k| has_end(a, polygon[k]) && has_end(b, polygon[k]))
                    }))
            {
                continue;
            }
            // All corners lie within the adjoining source strips' terminal reach.
            // Any signed aperture intersecting the patch keeps it unfilled.
            let probe = Wall {
                id: String::new(),
                page: page.page_number,
                start: at - u * (b.thickness * 0.5),
                end: at + u * (b.thickness * 0.5),
                thickness: a.thickness,
                body: BodyEvidence::ParallelFaces,
                curve: None,
                source_ids: vec![],
                requires_review: true,
            };
            if page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(&probe, o))
            {
                continue;
            }
            let mut ids = a.source_ids.clone();
            ids.extend(b.source_ids.iter().cloned());
            ids.sort();
            ids.dedup();
            out.push(super::stroked::CornerJoint {
                wall_ids: [a.id.clone(), b.id.clone()],
                source_ids: ids,
                polygon,
                adds_material: !already_material,
                contact_points: None,
                ends: [ea, eb],
            });
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::raster::{PdfInput, PdfRoute};

    fn band(a: Point, b: Point, width: f64, source: &str) -> RasterBand {
        RasterBand {
            hatch: None,
            face_source_ids: [format!("raster:{source}:a"), format!("raster:{source}:b")],
            start: a,
            end: b,
            thickness: width,
            dark_fraction: 1.,
            mean_luma: 80.,
            boundary_coverage: [1., 1.],
            width_pixels: width,
        }
    }
    fn page(bands: Vec<RasterBand>) -> Page {
        Page {
            finish_history: None,
            number: 1,
            width: 240.,
            height: 180.,
            units_to_mm: None,
            primitives: vec![],
            material_tiles: vec![],
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            pdf_input: Some(PdfInput {
                analysis_revision: 1,
                route: PdfRoute::Raster,
                vector_primitive_count: 0,
                source_image_ids: vec![],
                image_coverage: 1.,
                render_width: 240,
                render_height: 180,
                threshold: 174,
                raster_source_ids: vec![],
                bands,
            }),
        }
    }

    #[test]
    fn shared_end_does_not_erase_the_rest_of_a_measured_wall() {
        let p = page(vec![
            band(Point::new(80., 40.), Point::new(220., 40.), 16., "long"),
            band(Point::new(20., 38.), Point::new(82., 38.), 10., "short"),
        ]);
        let mut walls = vec![];
        observe(&p, &mut walls, &Options::default());
        assert_eq!(walls.len(), 2);
        let remainder = walls
            .iter()
            .find(|w| w.source_ids[0].contains("short"))
            .unwrap();
        assert_eq!(remainder.start, Point::new(20., 38.));
        assert_eq!(remainder.end, Point::new(80., 38.));
        assert!(!overlap::parallel_bodies_overlap(&walls[0], &walls[1]));
        assert_eq!(
            p.pdf_input.as_ref().unwrap().bands[1].end,
            Point::new(82., 38.),
            "original measured extent remains available"
        );
    }

    #[test]
    fn overlap_subtraction_keeps_a_short_measured_terminal_piece() {
        let p = page(vec![
            band(Point::new(20., 40.), Point::new(140., 40.), 16., "wide"),
            band(Point::new(24., 40.), Point::new(142., 40.), 10., "core"),
        ]);
        let mut walls = vec![];
        observe(&p, &mut walls, &Options::default());
        let tail = walls
            .iter()
            .find(|w| w.source_ids[0].contains("core"))
            .unwrap();
        assert_eq!(tail.start, Point::new(140., 40.));
        assert_eq!(tail.end, Point::new(142., 40.));
        assert!(
            BandIndex::new(p.pdf_input.as_ref().unwrap())
                .get(tail)
                .is_some()
        );
    }

    #[test]
    fn partial_skin_overlap_stays_available_for_review() {
        let p = page(vec![
            band(Point::new(0., 40.), Point::new(140., 40.), 10., "dominant"),
            band(Point::new(100., 48.), Point::new(220., 48.), 10., "partial"),
        ]);
        let mut walls = vec![];
        observe(&p, &mut walls, &Options::default());
        assert_eq!(walls.len(), 2);
        let partial = walls
            .iter()
            .find(|w| w.source_ids[0].contains("partial"))
            .unwrap();
        assert_eq!(partial.start, Point::new(100., 48.));
        assert_eq!(partial.end, Point::new(220., 48.));
        assert!(overlap::parallel_bodies_overlap(&walls[0], &walls[1]));
    }

    #[test]
    fn dense_finite_contacts_stop_before_unbounded_adjacency() {
        fn grid(n: usize) -> Vec<Wall> {
            let mut walls = vec![];
            for i in 0..n {
                for vertical in [false, true] {
                    let coord = i as f64 * 2.;
                    let length = n as f64 * 2.;
                    let (start, end) = if vertical {
                        (Point::new(coord, 0.), Point::new(coord, length))
                    } else {
                        (Point::new(0., coord), Point::new(length, coord))
                    };
                    walls.push(Wall {
                        id: format!("pixel-band:{}", walls.len()),
                        page: 1,
                        start,
                        end,
                        thickness: 1.,
                        body: BodyEvidence::ParallelFaces,
                        curve: None,
                        source_ids: vec![],
                        requires_review: true,
                    });
                }
            }
            walls
        }
        let ordinary = grid(64);
        let ids: Vec<_> = (0..ordinary.len()).collect();
        assert_eq!(
            context_links(&ordinary, &ids, 0., &HashSet::new())
                .unwrap()
                .len(),
            64 * 64
        );
        let excessive = grid(91);
        let ids: Vec<_> = (0..excessive.len()).collect();
        assert!(context_links(&excessive, &ids, 0., &HashSet::new()).is_none());
    }

    #[test]
    fn finite_material_contact_connects_a_staggered_seam_but_not_an_aperture() {
        let p = page(vec![
            band(Point::new(20., 40.), Point::new(80., 40.), 10., "left"),
            band(Point::new(80., 42.), Point::new(160., 42.), 16., "right"),
        ]);
        let mut walls = vec![];
        observe(&p, &mut walls, &Options::default());
        let left = walls
            .iter()
            .find(|w| w.source_ids[0].contains("left"))
            .unwrap();
        let right = walls
            .iter()
            .find(|w| w.source_ids[0].contains("right"))
            .unwrap();
        let proof = measured_contact(left, right).unwrap();
        assert_eq!(proof.position.x, 80.);
        for (wall, point) in [left, right].into_iter().zip(proof.axis_points) {
            assert!(on_segment(point, wall.start, wall.end).0.distance(point) < 1e-10);
        }
        let mut across_door = right.clone();
        across_door.start.x = 82.;
        assert!(measured_contact(left, &across_door).is_none());
        let mut slanted = left.clone();
        slanted.end.y += 0.01;
        assert!(
            measured_contact(&slanted, right).is_none(),
            "an axial bounding box cannot prove contact of a slanted body"
        );
    }
}
