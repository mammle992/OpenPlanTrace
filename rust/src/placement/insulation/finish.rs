//! A narrow facing may lie between the batt and an independently drawn wall face.
use super::{MaterialTile, Sources};
use crate::model::{PageScan, Point, Primitive, Shape, Wall};

fn facing<'a>(
    w: &Wall,
    tile: &MaterialTile,
    lines: impl Iterator<Item = &'a Primitive>,
) -> Option<Vec<String>> {
    let length = w.start.distance(w.end);
    let axis = (w.end - w.start).unit();
    let normal = Point::new(-axis.y, axis.x);
    let half = w.thickness / 2.;
    let eps = crate::placement::roundoff(w).max(w.thickness * 1e-5);
    let local = |p: Point| Point::new((p - w.start).dot(axis), (p - w.start).dot(normal));

    let outline = tile.outline.map(local);
    let low = outline.iter().map(|p| p.y).fold(f64::INFINITY, f64::min);
    let high = outline
        .iter()
        .map(|p| p.y)
        .fold(f64::NEG_INFINITY, f64::max);
    // All four corners must be present. A partial segment or a diagonally
    // clipped end cannot witness the entire finite barrier.
    for x in [0., length] {
        for y in [low, high] {
            if !outline.iter().any(|p| p.distance(Point::new(x, y)) <= eps) {
                return None;
            }
        }
    }
    let (inner, outer) = if (high - half).abs() <= eps {
        (low, -half)
    } else if (low + half).abs() <= eps {
        (high, half)
    } else {
        return None;
    };
    let depth = (outer - inner) * outer.signum();
    if depth <= eps || depth >= high - low {
        return None;
    }

    let point = |x, y| w.start + axis * x + normal * y;
    let seams = [
        (point(0., inner), point(length, inner)),
        (point(0., outer), point(0., inner)),
        (point(length, outer), point(length, inner)),
    ];
    let mut ids = [None; 3];
    for p in lines {
        let Shape::Line { start, end } = p.shape else {
            continue;
        };
        for (i, (a, b)) in seams.iter().enumerate() {
            if (start.distance(*a) <= eps && end.distance(*b) <= eps)
                || (start.distance(*b) <= eps && end.distance(*a) <= eps)
            {
                ids[i].get_or_insert(p.id.as_str());
            }
        }
        if let [Some(seam), Some(start_cap), Some(end_cap)] = ids {
            return Some(vec![
                seam.to_owned(),
                start_cap.to_owned(),
                end_cap.to_owned(),
            ]);
        }
    }
    None
}

pub(super) fn proof(
    w: &Wall,
    tile: &MaterialTile,
    page: &PageScan,
    sources: &Sources<'_>,
) -> Option<Vec<String>> {
    // Only original strokes on the same structural layer can close the facing.
    let wall_source = w
        .source_ids
        .iter()
        .filter_map(|id| sources.get(id.as_str()))
        .find(|s| s.exclusive_wall && s.primitive.layer == tile.layer)?;
    facing(
        w,
        tile,
        page.primitives.iter().filter(|p| {
            p.layer == tile.layer
                && p.layer_category_override == wall_source.primitive.layer_category_override
        }),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::BodyEvidence;

    #[test]
    fn facing_must_be_closed_by_three_independent_finite_strokes() {
        let point = |u, v| Point::new(150. + 0.6 * u - 0.8 * v, 200. + 0.8 * u + 0.6 * v);
        let w = Wall {
            id: "w".into(),
            page: 1,
            start: point(0., 0.),
            end: point(80., 0.),
            thickness: 10.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![],
            requires_review: true,
        };
        let mut tile = MaterialTile {
            id: "tile".into(),
            layer: Some("A-WALL".into()),
            outline: [
                point(0., -4.),
                point(80., -4.),
                point(80., 5.),
                point(0., 5.),
            ],
            origin: point(0., -4.),
            repeat: Point::new(6., 8.),
            across: Point::new(-7.2, 5.4),
            strokes: vec![],
        };
        let stroke = |id: &str, a, b| Primitive {
            id: id.into(),
            layer: tile.layer.clone(),
            layer_category_override: None,
            stroke_width: None,
            shape: Shape::Line { start: a, end: b },
        };
        let mut ink = [
            stroke("inner", point(80., -4.), point(0., -4.)),
            stroke("left", point(0., -4.), point(0., -5.)),
            stroke("right", point(80., -5.), point(80., -4.)),
        ];
        assert_eq!(
            facing(&w, &tile, ink.iter()).unwrap(),
            ["inner", "left", "right"]
        );
        assert!(facing(&w, &tile, ink[..2].iter()).is_none());
        ink[2].shape = Shape::Line {
            start: point(80., -5.),
            end: point(80., 5.),
        };
        assert!(facing(&w, &tile, ink.iter()).is_none()); // A whole-wall jamb is not a finish return.
        ink[2].shape = Shape::Line {
            start: point(80., -5.),
            end: point(80., -4.),
        };
        tile.outline[1] = point(78., -4.);
        assert!(facing(&w, &tile, ink.iter()).is_none());
    }
}
