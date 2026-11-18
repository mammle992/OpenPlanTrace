//! Verify that an apparent filled wall is the painted floor of a labeled space.
//! Printed areas on other disjoint fills corroborate the interpretation without calibrating the page.
use crate::{calibration::scales_agree, model::*, room_labels::RoomAnnotation};
use std::collections::{BTreeSet, HashMap};

pub(crate) struct MeasuredFill {
    pub annotation: usize,
    pub witnesses: Vec<String>,
}
pub(crate) type PaintedFloors = HashMap<String, MeasuredFill>;

#[derive(Clone, Copy)]
struct Bounds {
    min: Point,
    max: Point,
}
impl Bounds {
    fn separate(&self, other: &Self) -> bool {
        self.max.x <= other.min.x
            || other.max.x <= self.min.x
            || self.max.y <= other.min.y
            || other.max.y <= self.min.y
    }
}
struct Fill {
    source: usize,
    label: usize,
    bounds: Bounds,
    implied_scale: f64,
}
fn spend(remaining: &mut usize, n: usize) -> Result<(), String> {
    *remaining = remaining
        .checked_sub(n)
        .ok_or("painted room area comparison budget exceeded")?;
    Ok(())
}
pub(crate) fn measured_floors(
    primitives: &[Primitive],
    areas: &[RoomAnnotation],
    scale: Option<f64>,
) -> Result<PaintedFloors, String> {
    if areas.len() < 3 {
        return Ok(PaintedFloors::new());
    }
    let mut ordered: Vec<_> = (0..areas.len()).collect();
    ordered.sort_by(|&a, &b| areas[a].anchor.x.total_cmp(&areas[b].anchor.x));
    let mut remaining = 16_000_000;
    let mut fills = Vec::new();
    let mut per_label = vec![0; areas.len()];
    for (source, p) in primitives.iter().enumerate() {
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
        else {
            continue;
        };
        if points.len() < 3 || !matches!(p.layer_kind(), LayerKind::Unknown | LayerKind::Room) {
            continue;
        }
        spend(&mut remaining, points.len())?;
        let origin = points[0];
        let mut bounds = Bounds {
            min: origin,
            max: origin,
        };
        let mut area = 0.;
        for (&a, &b) in points.iter().zip(points.iter().cycle().skip(1)) {
            bounds.min.x = bounds.min.x.min(a.x);
            bounds.min.y = bounds.min.y.min(a.y);
            bounds.max.x = bounds.max.x.max(a.x);
            bounds.max.y = bounds.max.y.max(a.y);
            area += (a - origin).cross(b - origin);
        }
        let area = area.abs() * 0.5;
        if area <= 0. {
            continue;
        }
        let lo = ordered.partition_point(|&i| areas[i].anchor.x <= bounds.min.x);
        let hi = ordered.partition_point(|&i| areas[i].anchor.x < bounds.max.x);
        if lo >= hi {
            continue;
        }
        spend(&mut remaining, hi - lo)?;
        let mut labels = Vec::new();
        for &i in &ordered[lo..hi] {
            let at = areas[i].anchor;
            if at.y <= bounds.min.y || at.y >= bounds.max.y {
                continue;
            }
            spend(&mut remaining, points.len())?;
            if inside(at, points) {
                labels.push(i);
                if labels.len() > 1 {
                    break;
                }
            }
        }
        let [label] = labels.as_slice() else { continue };
        let implied = (areas[*label].area_square_meters * 1_000_000. / area).sqrt();
        if !implied.is_finite() || scale.is_some_and(|s| !scales_agree(s, implied)) {
            continue;
        }
        per_label[*label] += 1;
        fills.push(Fill {
            source,
            label: *label,
            bounds,
            implied_scale: implied,
        });
    }
    // A nested shape or overprint with the same annotation is insufficiently specific.
    fills.retain(|f| per_label[f.label] == 1);
    let mut floors = PaintedFloors::new();
    for paint in &fills {
        let mut proof = BTreeSet::new();
        let mut peers = Vec::new();
        spend(&mut remaining, fills.len())?;
        for f in &fills {
            if paint.bounds.separate(&f.bounds)
                && scales_agree(paint.implied_scale, f.implied_scale)
            {
                peers.push(f);
            }
        }
        'proof: for (i, a) in peers.iter().enumerate() {
            for b in &peers[..i] {
                spend(&mut remaining, 1)?;
                if !a.bounds.separate(&b.bounds) || !scales_agree(a.implied_scale, b.implied_scale)
                {
                    continue;
                }
                let measured = [paint, *a, *b];
                if measured.iter().all(|f| {
                    areas[f.label].area_square_meters == areas[paint.label].area_square_meters
                }) {
                    continue;
                }
                for f in measured {
                    proof.insert(primitives[f.source].id.clone());
                    proof.extend(areas[f.label].area_source_ids.iter().cloned());
                }
                break 'proof;
            }
        }
        if !proof.is_empty() {
            floors.insert(
                primitives[paint.source].id.clone(),
                MeasuredFill {
                    annotation: paint.label,
                    witnesses: proof.into_iter().collect(),
                },
            );
        }
    }
    Ok(floors)
}
pub(crate) fn separate(floors: &PaintedFloors, walls: &mut Vec<Wall>) -> Vec<WithheldWall> {
    let mut withheld = Vec::new();
    walls.retain(|wall| {
        if wall.body == BodyEvidence::FilledBody
            && let [source] = wall.source_ids.as_slice()
            && let Some(witnesses) = floors.get(source)
        {
            withheld.push(WithheldWall {
                furniture_outline: None,
                wall: wall.clone(),
                reason: WallSelectionReason::PrintedRoomArea,
                witness_source_ids: witnesses.witnesses.clone(),
            });
            return false;
        }
        true
    });
    withheld
}
