//! Printed area/label observations are useful even without a closed room.
//! Keep unbounded annotations separate from finite room polygons.
use crate::model::*;
use serde::{Deserialize, Serialize};
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RoomAnnotation {
    /// Measured painted surface. This observation can also belong to an outdoor space.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub floor_footprint: Option<FloorFootprint>,
    pub label: Option<String>,
    pub label_source_id: Option<String>,
    pub area_square_meters: f64,
    pub area_source_ids: Vec<String>,
    pub anchor: Point,
    pub room_id: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FloorFootprint {
    pub source_id: String,
    pub witness_source_ids: Vec<String>,
    pub polygon: Vec<Point>,
}
fn number(s: &str) -> Option<f64> {
    let v = s.trim().replace(',', ".").parse::<f64>().ok()?;
    (v > 0. && v.is_finite()).then_some(v)
}
fn unit(s: &str) -> bool {
    matches!(s.trim().to_lowercase().as_str(), "m2" | "m²" | "m^2" | "㎡")
}
fn area(s: &str) -> Option<f64> {
    let text = s.trim().to_lowercase();
    ["m²", "m^2", "m2", "㎡"]
        .iter()
        .find_map(|unit| text.strip_suffix(unit).and_then(number))
}
fn plausible(s: &str) -> bool {
    let s = s.trim();
    s.len() >= 2
        && s.len() <= 64
        && s.split_whitespace().count() <= 5
        && s.chars().any(char::is_alphabetic)
        && s.chars()
            .all(|c| c.is_alphanumeric() || " -/_.".contains(c))
        && !unit(s)
        && !s.to_lowercase().split_whitespace().any(|w| {
            matches!(
                w,
                "area" | "total" | "gross" | "bra" | "bta" | "areal" | "scale"
            )
        })
}
pub(crate) struct PrintedAreas {
    pub records: Vec<RoomAnnotation>,
    label_positions: Vec<Option<Point>>,
    remaining_work: usize,
}
pub(crate) fn observe(primitives: &[Primitive]) -> Result<PrintedAreas, String> {
    let mut texts: Vec<_> = primitives
        .iter()
        .filter_map(|p| {
            if let Shape::Text { at, value, height } = &p.shape {
                matches!(p.layer_kind(), LayerKind::Unknown | LayerKind::Room).then_some((
                    *at,
                    value.as_str(),
                    *height,
                    p.id.as_str(),
                ))
            } else {
                None
            }
        })
        .collect();
    texts.sort_by(|a, b| {
        a.0.x
            .total_cmp(&b.0.x)
            .then(a.0.y.total_cmp(&b.0.y))
            .then(a.3.cmp(b.3))
    });
    let mut out = Vec::new();
    let mut label_positions = Vec::new();
    let mut work = 0usize;
    for &(at, text, h, id) in &texts {
        let mut src = vec![id.to_owned()];
        let h = h.abs().clamp(1., 30.);
        let seek = 2.5 * h;
        let lo = texts.partition_point(|t| t.0.x < at.x - 6. * h);
        let hi = texts.partition_point(|t| t.0.x <= at.x + 6. * h);
        let nearby = &texts[lo..hi];
        if area(text).is_none() && !unit(text) {
            continue;
        }
        work = work.saturating_add(nearby.len() * 2);
        if work > 20_000_000 {
            return Err("room annotation work budget exceeded".into());
        }
        let value = if let Some(v) = area(text) {
            v
        } else if unit(text) {
            let found: Vec<_> = nearby
                .iter()
                .filter(|t| t.0.x < at.x && (t.0.y - at.y).abs() <= h * 0.4)
                .filter_map(|t| number(t.1).map(|v| (v, t.3)))
                .collect();
            if let [(v, source)] = found.as_slice() {
                src.push((*source).into());
                *v
            } else {
                continue;
            }
        } else {
            continue;
        };
        let mut labels: Vec<_> = nearby
            .iter()
            .filter(|t| {
                plausible(t.1)
                    && t.3 != id
                    && (t.0.y - at.y).abs() <= seek
                    && t.0.distance(at) <= 4. * h
            })
            .collect();
        labels.sort_by(|a, b| {
            a.0.distance(at)
                .total_cmp(&b.0.distance(at))
                .then(a.3.cmp(b.3))
        });
        let label = labels.first().copied().filter(|l| {
            labels
                .get(1)
                .is_none_or(|n| n.0.distance(at) > l.0.distance(at) + h * 0.5)
        });
        label_positions.push(label.map(|t| t.0));
        out.push(RoomAnnotation {
            floor_footprint: None,
            label: label.map(|t| t.1.to_owned()),
            label_source_id: label.map(|t| t.3.to_owned()),
            area_square_meters: value,
            area_source_ids: src,
            anchor: at,
            room_id: None,
        });
    }
    Ok(PrintedAreas {
        records: out,
        label_positions,
        remaining_work: 20_000_000 - work,
    })
}
impl PrintedAreas {
    pub(crate) fn bind(self, rooms: &mut [Room]) -> Result<Vec<RoomAnnotation>, String> {
        let mut out = self.records;
        let vertices: usize = rooms.iter().map(|r| r.polygon.len()).sum();
        if vertices.saturating_mul(out.len()) > self.remaining_work {
            return Err("room annotation work budget exceeded".into());
        }
        for (area, label) in out.iter_mut().zip(self.label_positions) {
            let mut hits = rooms.iter().filter(|r| {
                inside(area.anchor, &r.polygon) && label.is_none_or(|at| inside(at, &r.polygon))
            });
            area.room_id = hits
                .next()
                .map(|r| r.id.clone())
                .filter(|_| hits.next().is_none());
        }
        for room in rooms.iter_mut().filter(|r| r.label.is_none()) {
            let mut labels = out
                .iter()
                .filter(|a| a.room_id.as_ref() == Some(&room.id) && a.label.is_some());
            if let Some(a) = labels.next() {
                if labels.next().is_none() {
                    room.label.clone_from(&a.label);
                    room.label_source_id.clone_from(&a.label_source_id);
                }
            }
        }
        Ok(out)
    }
}
pub fn associate(page: &Page, rooms: &mut [Room]) -> Result<Vec<RoomAnnotation>, String> {
    observe(&page.primitives)?.bind(rooms)
}
