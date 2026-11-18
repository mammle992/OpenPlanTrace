//! Frozen preview 10 raster placement authority for saved scan.v9 replay.
#![allow(dead_code)]
//! Pixel-derived strips require material contrast and a building-scale connected network.
use crate::placement::{WallDecision, WallReason, WallStatus, overlap};
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
            .then_with(|| quality(b).total_cmp(&quality(a)))
            .then(i.cmp(j))
    });
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
        // Alternative raster widths compete locally. Vector observations retain
        // independent placement authority.
        if walls
            .iter()
            .any(|other| is_raster(other) && overlap::parallel_bodies_overlap(&w, other))
        {
            continue;
        }
        walls.push(w);
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
struct BandIndex<'a>(HashMap<[u64; 5], Vec<&'a RasterBand>>);
impl<'a> BandIndex<'a> {
    fn new(info: &'a crate::input::raster::PdfInput) -> Self {
        let mut index = HashMap::<_, Vec<_>>::new();
        for b in &info.bands {
            index
                .entry(band_key(b.start, b.end, b.thickness))
                .or_default()
                .push(b);
        }
        Self(index)
    }
    fn get(&self, w: &Wall) -> Option<&'a RasterBand> {
        self.0
            .get(&band_key(w.start, w.end, w.thickness))?
            .iter()
            .copied()
            .find(|b| b.face_source_ids.iter().all(|id| w.source_ids.contains(id)))
    }
}

fn material(b: &RasterBand) -> bool {
    b.width_pixels >= 3.
        && b.boundary_coverage.iter().all(|v| *v >= 0.8)
        && b.start.distance(b.end) >= b.thickness * 6.0
        && (b.dark_fraction >= 0.28 && b.mean_luma <= 220. || b.mean_luma <= 175.)
}

/// A contact is a finite L or T made by material strips. Near collinear door flanks are excluded.
fn contact(a: &Wall, b: &Wall, pixel: f64) -> bool {
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
fn related_flanks(a: &Wall, b: &Wall, pixel: f64) -> bool {
    let axis = (a.end - a.start).unit();
    let len = a.start.distance(a.end);
    if axis.dot((b.end - b.start).unit()).abs() < 0.999
        || (b.start - a.start).cross(axis).abs() > pixel * 1.5
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

pub(crate) fn select(page: &PageScan, out: &mut [WallDecision]) {
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
    let eligible: Vec<_> = page
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
                d.reason = WallReason::RasterFacesNeedReview;
            }
        }
        return;
    }
    let mut links = vec![vec![]; page.walls.len()];
    for (position, &i) in eligible.iter().enumerate() {
        for &j in &eligible[position + 1..] {
            if contact(&page.walls[i], &page.walls[j], pixel)
                || related_flanks(&page.walls[i], &page.walls[j], pixel)
            {
                links[i].push(j);
                links[j].push(i);
            }
        }
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
        if component.len() >= 3 && (physical_scale || sheet_scale) {
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
pub(crate) fn corner_joints(page: &PageScan) -> Vec<crate::placement::stroked::CornerJoint> {
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
                let eps = crate::placement::roundoff(w);
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
            out.push(crate::placement::stroked::CornerJoint {
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
