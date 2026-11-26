//! A partly opened door needs an authored door tag as well as leaf and jamb geometry.
use crate::model::{LayerKind, Point, Primitive, Shape};

pub(super) fn is_short(sweep: f64) -> bool {
    (25.0..45.0).contains(&sweep.abs().to_degrees())
}

fn door_dimensions(text: &str) -> bool {
    let Some((width, height)) = text.split_once(['x', 'X', '×']) else {
        return false;
    };
    let (Ok(width), Ok(height)) = (width.trim().parse::<u32>(), height.trim().parse::<u32>())
    else {
        return false;
    };
    [1, 10, 100].into_iter().any(|unit| {
        (5 * unit..=24 * unit).contains(&width)
            && (18 * unit..=30 * unit).contains(&height)
            && width < height
    })
}

struct Tag<'a> {
    source: &'a Primitive,
    at: Point,
    height: f64,
    dimensions: bool,
}

pub(super) struct Signs<'a>(Vec<Tag<'a>>);

impl<'a> Signs<'a> {
    pub(super) fn new(sources: impl Iterator<Item = &'a Primitive>) -> Self {
        let mut tags = Vec::new();
        for p in sources {
            let Shape::Text { at, value, height } = &p.shape else {
                continue;
            };
            let text = value.trim();
            let dimensions = door_dimensions(text);
            let code = matches!(
                text.to_ascii_uppercase().as_str(),
                "D" | "ID" | "YD" | "BD" | "TD" | "SD" | "DOOR"
            );
            if p.layer.is_some() && *height > 0. && (dimensions || code) {
                tags.push(Tag {
                    source: p,
                    at: *at,
                    height: *height,
                    dimensions,
                });
            }
        }
        tags.sort_by(|a, b| {
            a.at.x
                .total_cmp(&b.at.x)
                .then(a.source.id.cmp(&b.source.id))
        });
        Self(tags)
    }

    pub(super) fn at(&self, arc: &Primitive, center: Point, tip: Point) -> Option<[&'a str; 2]> {
        if !arc.layer.as_deref().is_some_and(|layer| {
            arc.layer_kind() == LayerKind::Door
                || arc.layer_kind() == LayerKind::Wall
                || (arc.layer_category_override.is_none() && crate::layers::is_named_wall(layer))
        }) {
            return None;
        }
        let span = tip - center;
        let length = span.norm();
        if !length.is_finite() || length <= 0. {
            return None;
        }
        let axis = span.unit();
        let mid = center + span * 0.5;
        let start = self.0.partition_point(|tag| tag.at.x < mid.x - length);
        let mut code = None;
        let mut dimension = None;
        for tag in &self.0[start..] {
            if tag.at.x > mid.x + length {
                break;
            }
            let offset = tag.at - mid;
            if tag.source.layer != arc.layer
                || !(0.03 * length..=0.25 * length).contains(&tag.height)
                || offset.dot(axis).abs() > 0.44 * length
                || offset.cross(axis).abs() > 0.65 * length
            {
                continue;
            }
            let witness = if tag.dimensions {
                &mut dimension
            } else {
                &mut code
            };
            if witness.replace(tag).is_some() {
                return None;
            }
        }
        let (label, measure) = (code?, dimension?);
        ((label.height - measure.height).abs() <= 0.25 * label.height
            && label.at.distance(measure.at) <= 0.38 * length
            && (label.at - mid).cross(axis) * (measure.at - mid).cross(axis) >= 0.)
            .then_some([label.source.id.as_str(), measure.source.id.as_str()])
    }
}
