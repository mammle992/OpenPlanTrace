//! Compact disconnected nested outlines are symbol proposals, never semantic fixture labels.
use crate::model::*;
use std::collections::{HashMap, HashSet};

pub(super) struct Candidate {
    pub polygon: Vec<Point>,
    pub source_ids: Vec<String>,
    pub outer_source_id: String,
    pub inner_source_ids: Vec<String>,
    pub basin_witness: Option<(String, String)>,
}
struct Contour<'a> {
    id: &'a str,
    polygon: Vec<Point>,
    lo: Point,
    hi: Point,
    area: f64,
    rounded: bool,
    extra_sources: Vec<String>,
}
fn area(points: &[Point]) -> f64 {
    points
        .iter()
        .zip(points.iter().cycle().skip(1))
        .map(|(a, b)| a.cross(*b))
        .sum::<f64>()
        .abs()
        * 0.5
}

fn convex(points: &[Point]) -> bool {
    let mut sign = 0.;
    let mut turn = 0.;
    for i in 0..points.len() {
        let a = points[(i + 1) % points.len()] - points[i];
        let b = points[(i + 2) % points.len()] - points[(i + 1) % points.len()];
        if a.norm() < 1e-10 || b.norm() < 1e-10 {
            continue;
        }
        let cross = a.unit().cross(b.unit());
        if cross.abs() < 1e-7 {
            if a.dot(b) < 0. {
                return false;
            }
            continue;
        }
        if sign != 0. && cross * sign < 0. {
            return false;
        }
        sign = cross;
        turn += cross.atan2(a.unit().dot(b.unit()));
    }
    sign != 0. && (turn.abs() - std::f64::consts::TAU).abs() < 1e-5
}
fn material_overlap(polygon: &[Point], w: &Wall) -> bool {
    if w.curve.is_some() || w.thickness <= 0. {
        return false;
    }
    let u = (w.end - w.start).unit();
    let n = Point::new(-u.y, u.x);
    let d = n * (w.thickness * 0.5);
    let body = [w.start + d, w.end + d, w.end - d, w.start - d];
    let axes = polygon
        .iter()
        .zip(polygon.iter().cycle().skip(1))
        .map(|(&a, &b)| {
            let d = (b - a).unit();
            Point::new(-d.y, d.x)
        })
        .chain([u, n]);
    axes.into_iter().all(|axis| {
        let range = |points: &[Point]| {
            points
                .iter()
                .map(|p| p.dot(axis))
                .fold((f64::INFINITY, f64::NEG_INFINITY), |(lo, hi), x| {
                    (lo.min(x), hi.max(x))
                })
        };
        let (a, b) = range(polygon);
        let (c, d) = range(&body);
        b.min(d) - a.max(c) > 1e-8
    })
}

fn basin_witness(
    page: &Page,
    outer: &Contour<'_>,
    inner: &Contour<'_>,
    work: &mut usize,
) -> Result<Option<(String, String)>, String> {
    let outer_size = outer.hi - outer.lo;
    let straight_back = outer
        .polygon
        .iter()
        .zip(outer.polygon.iter().cycle().skip(1))
        .any(|(a, b)| a.distance(*b) >= outer_size.x.min(outer_size.y) * 0.5);
    if !outer.rounded || !straight_back {
        return Ok(None);
    }
    let size = inner.hi - inner.lo;
    let small = size.x.min(size.y);
    *work = work
        .checked_sub(page.primitives.len())
        .ok_or("basin source comparison limit exceeded")?;
    for drain in &page.primitives {
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &drain.shape
        else {
            continue;
        };
        if !(8..=1024).contains(&points.len()) {
            continue;
        }
        *work = work
            .checked_sub(points.len() + inner.polygon.len())
            .ok_or("basin drain work limit exceeded")?;
        let mut lo = points[0];
        let mut hi = lo;
        for p in points {
            lo.x = lo.x.min(p.x);
            lo.y = lo.y.min(p.y);
            hi.x = hi.x.max(p.x);
            hi.y = hi.y.max(p.y);
        }
        let d = hi - lo;
        let center = (lo + hi) * 0.5;
        if d.x.min(d.y) < small * 0.02
            || d.x.max(d.y) > small * 0.25
            || d.x.min(d.y) < d.x.max(d.y) * 0.85
            || !inside(center, &inner.polygon)
            || center.distance((inner.lo + inner.hi) * 0.5) > small * 0.25
        {
            continue;
        }
        let radius = (d.x + d.y) * 0.25;
        if points
            .iter()
            .any(|p| (p.distance(center) - radius).abs() > radius * 0.15)
        {
            continue;
        }
        *work = work
            .checked_sub(page.primitives.len() * (outer.polygon.len() + inner.polygon.len() + 1))
            .ok_or("basin tap work limit exceeded")?;
        for tap in &page.primitives {
            let Shape::Line { start, end } = tap.shape else {
                continue;
            };
            let v = end - start;
            let length = v.norm();
            let mid = (start + end) * 0.5;
            if length < small * 0.08
                || length > small * 0.5
                || !inside(mid, &outer.polygon)
                || inside(mid, &inner.polygon)
                || (center - start).cross(v.unit()).abs() > small * 0.12
            {
                continue;
            }
            let rim_distance = |p: Point| {
                outer
                    .polygon
                    .iter()
                    .zip(outer.polygon.iter().cycle().skip(1))
                    .map(|(&a, &b)| on_segment(p, a, b).0.distance(p))
                    .fold(f64::INFINITY, f64::min)
            };
            if rim_distance(start).min(rim_distance(end)) > small * 0.15 {
                continue;
            }
            return Ok(Some((drain.id.clone(), tap.id.clone())));
        }
    }
    Ok(None)
}
fn outline(p: &Primitive) -> Option<Contour<'_>> {
    let (mut polygon, rounded) = match &p.shape {
        Shape::Polyline {
            points,
            closed: true,
            filled: false,
        } if (3..=1024).contains(&points.len()) => (points.clone(), points.len() >= 8),
        Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } if (sweep.abs() - std::f64::consts::TAU).abs() < 1e-5 => (
            (0..32)
                .map(|i| {
                    let a = start_angle + sweep * i as f64 / 32.;
                    *center + Point::new(a.cos(), a.sin()) * *radius
                })
                .collect(),
            true,
        ),
        _ => return None,
    };
    if polygon.first() == polygon.last() {
        polygon.pop();
    }
    if polygon.len() < 3 {
        return None;
    }
    let mut lo = polygon[0];
    let mut hi = lo;
    for p in &polygon {
        lo.x = lo.x.min(p.x);
        lo.y = lo.y.min(p.y);
        hi.x = hi.x.max(p.x);
        hi.y = hi.y.max(p.y);
    }
    let a = area(&polygon);
    (a > 0. && (hi.x - lo.x) > 0. && (hi.y - lo.y) > 0. && convex(&polygon)).then_some(Contour {
        id: &p.id,
        polygon,
        lo,
        hi,
        area: a,
        rounded,
        extra_sources: vec![],
    })
}

/// CAD often paints each edge/arc of one bowl as a different source path.
/// Reassemble only degree-two finite chains with coincident source endpoints.
fn chains(page: &Page) -> Result<Vec<Contour<'_>>, String> {
    let mut pens: Vec<_> = page
        .primitives
        .iter()
        .filter_map(|p| p.stroke_width)
        .collect();
    pens.sort_by(f64::total_cmp);
    let eps = pens
        .get(pens.len() / 2)
        .copied()
        .map_or(page.width.max(page.height) * 1e-7, |p| p * 0.05)
        .max(1e-7);
    let key = |p: Point| ((p.x / eps).round() as i64, (p.y / eps).round() as i64);
    let mut paths = vec![];
    let mut nodes: HashMap<usize, Vec<usize>> = HashMap::new();
    let mut buckets: HashMap<_, Vec<usize>> = HashMap::new();
    let mut positions: Vec<Point> = vec![];
    let mut ends = vec![];
    let mut endpoint_work = 4_000_000usize;
    let mut points = 0usize;
    for p in &page.primitives {
        if !matches!(
            p.layer_kind(),
            LayerKind::Unknown | LayerKind::Object | LayerKind::Wall
        ) {
            continue;
        }
        let path = match &p.shape {
            Shape::Line { start, end } => vec![*start, *end],
            Shape::Polyline {
                points,
                closed: false,
                filled: false,
            } if points.len() <= 1024 => points.clone(),
            Shape::Arc {
                center,
                radius,
                start_angle,
                sweep,
            } if sweep.abs() < std::f64::consts::TAU - 1e-5 => {
                let n = (sweep.abs() * 8.).ceil().max(3.) as usize;
                (0..=n)
                    .map(|i| {
                        let a = start_angle + sweep * i as f64 / n as f64;
                        *center + Point::new(a.cos(), a.sin()) * *radius
                    })
                    .collect()
            }
            _ => continue,
        };
        if path.len() < 2 || path[0].distance(*path.last().unwrap()) <= eps {
            continue;
        }
        points += path.len();
        if paths.len() >= 60_000 || points > 1_000_000 {
            return Err("object outline chain inventory limit exceeded".into());
        }
        let i = paths.len();
        let mut endpoints = [0; 2];
        for (side, p) in [path[0], *path.last().unwrap()].into_iter().enumerate() {
            let k = key(p);
            let mut found = None;
            for dx in -1..=1 {
                for dy in -1..=1 {
                    if let Some(ids) = buckets.get(&(k.0 + dx, k.1 + dy)) {
                        endpoint_work = endpoint_work
                            .checked_sub(ids.len())
                            .ok_or("object endpoint matching limit exceeded")?;
                        for &id in ids {
                            if positions[id].distance(p) <= eps {
                                found = Some(id);
                                break;
                            }
                        }
                    }
                }
            }
            let id = found.unwrap_or_else(|| {
                let id = positions.len();
                positions.push(p);
                buckets.entry(k).or_default().push(id);
                id
            });
            endpoints[side] = id;
            nodes.entry(id).or_default().push(i);
        }
        ends.push(endpoints);
        paths.push((p.id.as_str(), path));
    }
    let mut visited = vec![false; paths.len()];
    let mut out = vec![];
    for seed in 0..paths.len() {
        if visited[seed] {
            continue;
        }
        let first = ends[seed][0];
        let mut current = first;
        let mut edge = seed;
        let mut polygon = vec![];
        let mut ids = vec![];
        let mut closed = false;
        for _ in 0..256 {
            if visited[edge] {
                break;
            }
            visited[edge] = true;
            let (id, path) = &paths[edge];
            ids.push(*id);
            if ends[edge][0] == current {
                polygon.extend(path[..path.len() - 1].iter().copied());
                current = ends[edge][1];
            } else {
                polygon.extend(path[1..].iter().rev().copied());
                current = ends[edge][0];
            }
            if current == first {
                closed = true;
                break;
            }
            let Some(adjacent) = nodes.get(&current) else {
                break;
            };
            if adjacent.len() != 2 {
                break;
            }
            edge = if adjacent[0] == edge {
                adjacent[1]
            } else {
                adjacent[0]
            };
        }
        if !closed || polygon.len() < 4 || polygon.len() > 1024 {
            continue;
        }
        let mut lo = polygon[0];
        let mut hi = lo;
        for p in &polygon {
            lo.x = lo.x.min(p.x);
            lo.y = lo.y.min(p.y);
            hi.x = hi.x.max(p.x);
            hi.y = hi.y.max(p.y);
        }
        let a = area(&polygon);
        if a <= 0. || !convex(&polygon) {
            continue;
        }
        ids.sort();
        ids.dedup();
        let id = ids[0];
        let rounded = (0..polygon.len())
            .filter(|&i| {
                let a = (polygon[i] - polygon[(i + polygon.len() - 1) % polygon.len()]).unit();
                let b = (polygon[(i + 1) % polygon.len()] - polygon[i]).unit();
                a.cross(b).abs() > 0.01
            })
            .count()
            >= 6;
        out.push(Contour {
            id,
            polygon,
            lo,
            hi,
            area: a,
            rounded,
            extra_sources: ids.into_iter().skip(1).map(str::to_owned).collect(),
        });
    }
    Ok(out)
}

pub(super) fn detect(
    page: &Page,
    walls: &[Wall],
    openings: &[Opening],
) -> Result<Vec<Candidate>, String> {
    let claimed: HashSet<_> = walls
        .iter()
        .flat_map(|w| &w.source_ids)
        .chain(openings.iter().flat_map(|o| &o.source_ids))
        .map(String::as_str)
        .collect();
    let mut text_heights: Vec<_> = page
        .primitives
        .iter()
        .filter_map(|p| match &p.shape {
            Shape::Text { height, .. } if *height > 0. => Some(*height),
            _ => None,
        })
        .collect();
    text_heights.sort_by(f64::total_cmp);
    let minimum = text_heights
        .get(text_heights.len() / 2)
        .copied()
        .unwrap_or(page.width.min(page.height) * 0.004)
        * 1.5;
    let max_area = page.width
        * page.height
        * if page.primitives.len() < 64 && walls.is_empty() {
            0.9
        } else {
            0.02
        };
    let mut contours = vec![];
    for p in &page.primitives {
        if claimed.contains(p.id.as_str())
            || !matches!(p.layer_kind(), LayerKind::Unknown | LayerKind::Object)
        {
            continue;
        }
        if let Some(c) = outline(p) {
            let d = c.hi - c.lo;
            if d.x.min(d.y) < minimum || d.x.max(d.y) > d.x.min(d.y) * 5. || c.area > max_area {
                continue;
            }
            if contours.len() >= 4096 {
                return Err("object contour inventory limit exceeded".into());
            }
            contours.push(c);
        }
    }
    for c in chains(page)? {
        let d = c.hi - c.lo;
        if d.x.min(d.y) < minimum || d.x.max(d.y) > d.x.min(d.y) * 5. || c.area > max_area {
            continue;
        }
        if openings.iter().any(|o| {
            o.source_ids
                .iter()
                .any(|id| id == c.id || c.extra_sources.contains(id))
        }) {
            continue;
        }
        if contours.len() >= 4096 {
            return Err("object contour inventory limit exceeded".into());
        }
        contours.push(c);
    }
    // Only direct children belong to a proposal. Largest enclosing contours
    // cannot swallow a row of separate furniture or an entire room.
    let mut work = 8_000_000usize;
    let mut out = vec![];
    for outer in &contours {
        let mut children = vec![];
        for inner in &contours {
            work = work
                .checked_sub(1)
                .ok_or("object contour comparison limit exceeded")?;
            if outer.id == inner.id
                || inner.area >= outer.area * 0.85
                || inner.area < outer.area * 0.08
                || inner.lo.x <= outer.lo.x
                || inner.lo.y <= outer.lo.y
                || inner.hi.x >= outer.hi.x
                || inner.hi.y >= outer.hi.y
            {
                continue;
            }
            work = work
                .checked_sub(inner.polygon.len() * outer.polygon.len())
                .ok_or("object contour containment limit exceeded")?;
            if inner.polygon.iter().all(|&p| inside(p, &outer.polygon)) {
                children.push(inner);
            }
        }
        if children.is_empty()
            || children.len() > 4
            || !(outer.rounded || children.iter().any(|c| c.rounded))
        {
            continue;
        }
        // Disjoint internal parts may establish one glyph. Nested subparts
        // remain with their nearest outer outline instead of duplicate objects.
        let containment_size = children.iter().map(|c| c.polygon.len()).sum::<usize>();
        work = work
            .checked_sub(containment_size * containment_size + page.primitives.len() + walls.len())
            .ok_or("object proposal work limit exceeded")?;
        let direct: Vec<_> = children
            .iter()
            .copied()
            .filter(|inner| {
                !children.iter().any(|middle| {
                    middle.area > inner.area
                        && inner.polygon.iter().all(|&p| inside(p, &middle.polygon))
                })
            })
            .collect();
        if direct.is_empty() {
            continue;
        }
        let has_text = page.primitives.iter().any(|p| match &p.shape {
            Shape::Text { at, height, value } if !value.trim().is_empty() => {
                let lo = Point::new(at.x - height * 0.25, at.y - height);
                let hi = Point::new(
                    at.x + height * value.chars().take(256).count() as f64,
                    at.y + height * 0.35,
                );
                lo.x < outer.hi.x && hi.x > outer.lo.x && lo.y < outer.hi.y && hi.y > outer.lo.y
            }
            _ => false,
        });
        if has_text {
            continue;
        }
        let nearby: Vec<_> = walls
            .iter()
            .filter(|w| {
                let pad = w.thickness * 0.5;
                w.start.x.min(w.end.x) - pad < outer.hi.x
                    && w.start.x.max(w.end.x) + pad > outer.lo.x
                    && w.start.y.min(w.end.y) - pad < outer.hi.y
                    && w.start.y.max(w.end.y) + pad > outer.lo.y
            })
            .collect();
        work = work
            .checked_sub(nearby.len() * outer.polygon.len() * outer.polygon.len())
            .ok_or("object material-overlap work limit exceeded")?;
        if nearby.iter().any(|w| material_overlap(&outer.polygon, w)) {
            continue;
        }
        let mut inner_source_ids: Vec<_> = direct
            .iter()
            .flat_map(|c| std::iter::once(c.id.to_owned()).chain(c.extra_sources.clone()))
            .collect();
        inner_source_ids.sort();
        inner_source_ids.dedup();
        let mut source_ids = inner_source_ids.clone();
        source_ids.push(outer.id.to_owned());
        source_ids.extend(outer.extra_sources.iter().cloned());
        let basin_witness = if direct.len() == 1 {
            basin_witness(page, outer, direct[0], &mut work)?
        } else {
            None
        };
        if let Some((drain, tap)) = &basin_witness {
            source_ids.extend([drain.clone(), tap.clone()]);
        }
        source_ids.sort();
        source_ids.dedup();
        if out.len() >= 512 {
            return Err("nested symbol candidate limit exceeded".into());
        }
        out.push(Candidate {
            polygon: outer.polygon.clone(),
            source_ids,
            outer_source_id: outer.id.to_owned(),
            inner_source_ids,
            basin_witness,
        });
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn page() -> Page {
        let polygon = |id: &str, lo: Point, hi: Point| Primitive {
            id: id.into(),
            layer: None,
            stroke_width: Some(0.2),
            layer_category_override: None,
            shape: Shape::Polyline {
                points: vec![lo, Point::new(hi.x, lo.y), hi, Point::new(lo.x, hi.y)],
                closed: true,
                filled: false,
            },
        };
        Page {
            finish_history: None,
            number: 1,
            width: 400.,
            height: 300.,
            units_to_mm: None,
            pdf_input: None,
            material_tiles: vec![],
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            primitives: vec![
                polygon("rim", Point::new(20., 20.), Point::new(70., 55.)),
                Primitive {
                    id: "bowl".into(),
                    layer: None,
                    stroke_width: Some(0.2),
                    layer_category_override: None,
                    shape: Shape::Arc {
                        center: Point::new(45., 37.5),
                        radius: 12.,
                        start_angle: 0.,
                        sweep: std::f64::consts::TAU,
                    },
                },
            ],
        }
    }
    #[test]
    fn disconnected_bowl_and_rim_have_one_measured_generic_proposal() {
        let p = page();
        let c = detect(&p, &[], &[]).unwrap();
        assert_eq!(c.len(), 1);
        assert_eq!(c[0].source_ids, ["bowl", "rim"]);
        assert_eq!(c[0].polygon.len(), 4);
    }
    #[test]
    fn plain_footprints_and_labelled_or_claimed_frames_are_not_symbol_anatomy() {
        let mut p = page();
        p.primitives.pop();
        assert!(detect(&p, &[], &[]).unwrap().is_empty());
        let mut p = page();
        p.primitives.push(Primitive {
            id: "label".into(),
            layer: None,
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Text {
                at: Point::new(30., 40.),
                height: 4.,
                value: "A".into(),
            },
        });
        assert!(detect(&p, &[], &[]).unwrap().is_empty());
        let p = page();
        let wall = Wall {
            id: "w".into(),
            page: 1,
            start: Point::new(20., 20.),
            end: Point::new(70., 20.),
            thickness: 2.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec!["rim".into()],
            requires_review: true,
        };
        assert!(detect(&p, &[wall], &[]).unwrap().is_empty());
    }

    #[test]
    fn a_basin_needs_its_own_filled_drain_and_separate_tap() {
        let mut p = page();
        let mut rim = vec![Point::new(20., 20.), Point::new(70., 20.)];
        rim.extend((0..=16).map(|i| {
            let a = std::f64::consts::PI * i as f64 / 16.;
            Point::new(45., 30.) + Point::new(a.cos(), a.sin()) * 25.
        }));
        p.primitives[0].shape = Shape::Polyline {
            points: rim,
            closed: true,
            filled: false,
        };
        let center = Point::new(45., 37.5);
        let points = (0..32)
            .map(|i| {
                let a = std::f64::consts::TAU * i as f64 / 32.;
                center + Point::new(a.cos(), a.sin()) * 1.5
            })
            .collect();
        p.primitives.push(Primitive {
            id: "drain".into(),
            layer: None,
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Polyline {
                points,
                closed: true,
                filled: true,
            },
        });
        assert!(detect(&p, &[], &[]).unwrap()[0].basin_witness.is_none());
        p.primitives.push(Primitive {
            id: "tap".into(),
            layer: None,
            stroke_width: Some(0.2),
            layer_category_override: None,
            shape: Shape::Line {
                start: Point::new(45., 22.),
                end: Point::new(45., 25.),
            },
        });
        assert_eq!(
            detect(&p, &[], &[]).unwrap()[0].basin_witness,
            Some(("drain".into(), "tap".into()))
        );
        p.primitives[0].shape = Shape::Arc {
            center,
            radius: 20.,
            start_angle: 0.,
            sweep: std::f64::consts::TAU,
        };
        assert!(
            detect(&p, &[], &[]).unwrap()[0].basin_witness.is_none(),
            "a circular gauge is not basin anatomy"
        );
    }

    #[test]
    fn crossing_and_retracing_outlines_are_not_polygon_footprints() {
        assert!(!convex(&[
            Point::new(0., 0.),
            Point::new(20., 20.),
            Point::new(0., 20.),
            Point::new(15., 0.)
        ]));
        assert!(!convex(&[
            Point::new(0., 0.),
            Point::new(10., 0.),
            Point::new(5., 0.),
            Point::new(10., 0.),
            Point::new(10., 10.),
            Point::new(0., 10.)
        ]));
    }
}
