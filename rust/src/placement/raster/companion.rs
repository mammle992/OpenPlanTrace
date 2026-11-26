//! Place a finite raster partition when a previously qualified assembly and
//! independent terminal ink corroborate its measured material.
use super::{BandIndex, hatch_material, is_raster, material};
use crate::input::raster::{PdfRoute, RasterBand};
use crate::model::{PageScan, Point, Shape, Wall, on_segment};
use crate::placement::{WallDecision, WallReason, WallStatus, effective_wall, overlap};
use std::collections::{BTreeMap, HashMap};

fn dense(band: &RasterBand) -> bool {
    band.hatch.is_none()
        && material(band)
        && band.dark_fraction >= 0.55
        && band.mean_luma <= 175.
        && band.boundary_coverage.iter().all(|&v| v >= 0.95)
        && band.start.distance(band.end) >= 12. * band.thickness
}

fn opposed(a: &RasterBand, b: &RasterBand, pixel: f64) -> bool {
    let axis = (a.end - a.start).unit();
    let length = a.start.distance(a.end);
    let width = a.thickness.min(b.thickness);
    let ends = [b.start - a.start, b.end - a.start];
    let [t0, t1] = ends.map(|p| p.dot(axis));
    let distance = ends[0].cross(axis).abs();
    let end_tolerance = 2.5 * width + 2. * pixel;
    axis.dot((b.end - b.start).unit()).abs() >= 0.999
        && (5. * width..=40. * width).contains(&distance)
        && (a.thickness - b.thickness).abs() <= 0.25 * width + pixel
        && t0.min(t1).abs() <= end_tolerance
        && (t0.max(t1) - length).abs() <= end_tolerance
        && (a.dark_fraction - b.dark_fraction).abs() <= 0.12
        && (a.mean_luma - b.mean_luma).abs() <= 20.
}

fn has_cap(wall: &Wall, end: Point, line: (Point, Point), pixel: f64) -> bool {
    let axis = (wall.end - wall.start).unit();
    let (a, b) = (line.0 - end, line.1 - end);
    let (lo, hi) = (
        a.cross(axis).min(b.cross(axis)),
        a.cross(axis).max(b.cross(axis)),
    );
    a.dot(axis).abs() <= pixel
        && b.dot(axis).abs() <= pixel
        && lo <= -wall.thickness * 0.5 + pixel * 0.5
        && hi >= wall.thickness * 0.5 - pixel * 0.5
        && a.distance(b) <= 3. * wall.thickness
}

fn join_context(wall: &Wall, end: Point, cross: &Wall, pixel: f64) -> bool {
    let u = (wall.end - wall.start).unit();
    let v = (cross.end - cross.start).unit();
    let offset = end - cross.start;
    let across = offset.cross(v).abs();
    let along = offset.dot(v);
    u.dot(v).abs() <= 0.02
        && across <= cross.thickness * 0.5 + 4. * pixel
        && along >= -pixel
        && along <= cross.start.distance(cross.end) + pixel
}

pub(in crate::placement) fn select(page: &PageScan, decisions: &mut [WallDecision]) {
    if page.walls.len() > 2048 {
        return;
    }
    let Some(info) = page.pdf_input.as_ref().filter(|info| {
        info.route == PdfRoute::Raster && info.render_width > 0 && info.render_height > 0
    }) else {
        return;
    };
    let pixel =
        (page.width / info.render_width as f64).max(page.height / info.render_height as f64);
    let mut assemblies = BTreeMap::<&str, Vec<usize>>::new();
    let mut ready = Vec::new();
    for (i, d) in decisions.iter().enumerate() {
        if d.status != WallStatus::Ready {
            continue;
        }
        ready.push(effective_wall(&page.walls[i], Some(d)));
        if d.reason == WallReason::SourceBoundedRasterPartition {
            assemblies
                .entry(d.component_id.as_str())
                .or_default()
                .push(i);
        }
    }
    if assemblies.is_empty() {
        return;
    }
    let bands = BandIndex::new(info);
    let lines: HashMap<_, _> = page
        .primitives
        .iter()
        .filter_map(|p| match p.shape {
            Shape::Line { start, end } => Some((p.id.as_str(), (start, end))),
            _ => None,
        })
        .collect();
    let supporting_hatch: Vec<_> = page
        .walls
        .iter()
        .filter(|w| is_raster(w) && bands.get(w).is_some_and(hatch_material))
        .collect();
    let mut proposals = Vec::new();
    for group in assemblies.values() {
        let &[a, b, c] = group.as_slice() else {
            continue;
        };
        let Some((first, second, opposite)) =
            [(a, b, c), (a, c, b), (b, c, a)]
                .into_iter()
                .find(|&(a, b, c)| {
                    let [x, y, z] = [a, b, c].map(|i| &page.walls[i]);
                    let [Some(p), Some(q), Some(r)] = [x, y, z].map(|w| bands.get(w)) else {
                        return false;
                    };
                    super::partition_turn(x, y, z, [p, q, r], pixel).is_some()
                })
        else {
            continue;
        };
        let [a, b, reference] = [first, second, opposite].map(|i| &page.walls[i]);
        let reference_band = bands.get(reference).expect("confirmed assembly band");
        let rail_id = a.source_ids.iter().find(|id| b.source_ids.contains(id));
        let Some(&(rail_start, rail_end)) = rail_id.and_then(|id| lines.get(id.as_str())) else {
            continue;
        };
        let reference_axis = (reference.end - reference.start).unit();
        let rail_axis = (rail_end - rail_start).unit();
        if reference_axis.dot(rail_axis).abs() > 0.02 {
            continue;
        }
        for (i, wall) in page.walls.iter().enumerate() {
            if decisions[i].status != WallStatus::Review
                || decisions[i].reason != WallReason::RasterFacesNeedReview
                || !is_raster(wall)
            {
                continue;
            }
            let Some(band) = bands.get(wall).filter(|band| {
                band.start == wall.start
                    && band.end == wall.end
                    && dense(band)
                    && opposed(reference_band, band, pixel)
            }) else {
                continue;
            };
            let projection = |p| on_segment(p, band.start, band.end).0;
            let Some(rail_tip) = [rail_start, rail_end]
                .into_iter()
                .find(|&p| projection(p).distance(p) <= pixel)
            else {
                continue;
            };
            let near_tolerance = 2. * wall.thickness + pixel;
            let [near, far] = if rail_tip.distance(wall.start) < rail_tip.distance(wall.end) {
                [wall.start, wall.end]
            } else {
                [wall.end, wall.start]
            };
            if rail_tip.distance(near) > near_tolerance
                || ![a, b].into_iter().any(|f| {
                    f.start.distance(rail_tip).min(f.end.distance(rail_tip)) <= 2. * f.thickness
                })
            {
                continue;
            }
            let Some(&far_wall) = supporting_hatch
                .iter()
                .find(|w| w.id != a.id && w.id != b.id && join_context(wall, far, w, pixel))
            else {
                continue;
            };
            let Some(cap) = lines
                .iter()
                .filter(|(_, line)| has_cap(wall, near, **line, pixel))
                .map(|(&id, _)| id)
                .min()
            else {
                continue;
            };
            let mut sources = wall.source_ids.clone();
            sources.extend(far_wall.source_ids.iter().cloned());
            sources.push(rail_id.expect("confirmed shared rail").clone());
            sources.push(cap.to_owned());
            sources.sort();
            sources.dedup();
            proposals.push((i, [first, second, opposite], sources));
        }
    }
    proposals.sort_by_key(|(i, support, _)| (*i, *support));
    let mut proposals = proposals.into_iter().peekable();
    let mut eligible = Vec::new();
    while let Some((i, support, sources)) = proposals.next() {
        // Conflicting independent assemblies leave the interpretation uncertain.
        if proposals.peek().is_some_and(|next| next.0 == i) {
            while proposals.next_if(|next| next.0 == i).is_some() {}
            continue;
        }
        let wall = &page.walls[i];
        if ready
            .iter()
            .any(|other| overlap::parallel_bodies_overlap(wall, other))
            || page
                .openings
                .iter()
                .any(|o| overlap::aperture_crosses(wall, o))
        {
            continue;
        }
        eligible.push((i, support, sources));
    }
    // Two qualified interpretations of overlapping material do not establish
    // which finite body to place. Neither gets priority from input order.
    let mut ambiguous = vec![false; eligible.len()];
    for (position, &(i, _, _)) in eligible.iter().enumerate() {
        for (offset, &(j, _, _)) in eligible[position + 1..].iter().enumerate() {
            if overlap::parallel_bodies_overlap(&page.walls[i], &page.walls[j]) {
                ambiguous[position] = true;
                ambiguous[position + 1 + offset] = true;
            }
        }
    }
    for ((i, support, sources), ambiguous) in eligible.into_iter().zip(ambiguous) {
        if ambiguous {
            continue;
        }
        let decision = &mut decisions[i];
        decision.status = WallStatus::Ready;
        decision.reason = WallReason::SourceBoundedRasterCompanion;
        decision.support_source_ids = sources;
        decision.support_wall_ids = support.map(|k| page.walls[k].id.clone()).into();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::BodyEvidence;

    fn wall(start: (f64, f64), end: (f64, f64), thickness: f64) -> Wall {
        Wall {
            id: String::new(),
            page: 1,
            start: Point::new(start.0, start.1),
            end: Point::new(end.0, end.1),
            thickness,
            curve: None,
            body: BodyEvidence::ParallelFaces,
            source_ids: vec![],
            requires_review: true,
        }
    }

    #[test]
    fn support_near_a_partition_does_not_materialize_a_gap() {
        let partition = wall((40., 20.), (40., 90.), 4.);
        let corroborating_return = wall((40., 96.), (93., 96.), 6.);
        assert!(join_context(
            &partition,
            partition.end,
            &corroborating_return,
            1.
        ));
        assert!(super::super::measured_contact(&partition, &corroborating_return).is_none());
        assert!(has_cap(
            &partition,
            partition.start,
            (Point::new(38., 20.), Point::new(42., 20.)),
            1.
        ));
        assert!(!has_cap(
            &partition,
            partition.start,
            (Point::new(39.5, 20.), Point::new(42., 20.)),
            1.
        ));
        assert!(!has_cap(
            &partition,
            partition.start,
            (Point::new(38., 22.), Point::new(42., 22.)),
            1.
        ));
    }
}
