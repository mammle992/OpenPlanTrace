//! A single page scale is safe only when independent finite measurements agree.
//! Leave mixed-scale pages unresolved rather than averaging different viewports.
use crate::{
    model::*,
    semantics::{DimensionEvidence, PageSemantics},
};
use std::collections::{BTreeMap, BTreeSet};
/// Relative tolerance for agreement between independent model scales.
pub(crate) fn scales_agree(a: f64, b: f64) -> bool {
    (a - b).abs() <= 0.03 * a.max(b)
}
#[cfg(test)]
mod tests;
pub fn dimensions(primitives: &[Primitive], s: &PageSemantics) -> Option<(f64, Vec<String>)> {
    if !s.diagnostics.is_empty() {
        return None;
    }
    let mut observations = BTreeMap::new();
    let mut reference: Option<(Point, Point, Vec<String>)> = None;
    let mut independent = false;
    for d in &s.dimensions {
        if d.evidence != DimensionEvidence::TwoFiniteWitnesses {
            continue;
        }
        if let (Some(line), Some(a), Some(b), Some(k)) = (
            &d.line_source,
            &d.start_witness,
            &d.end_witness,
            d.observed_mm_per_unit,
        ) {
            if a == b || !k.is_finite() || k <= 0. || d.text_sources.is_empty() {
                continue;
            }
            let Some(span) = &d.line else { continue };
            if let Some((p, q, text)) = &reference {
                let same = (p.distance(span.start) <= 1.5 && q.distance(span.end) <= 1.5)
                    || (p.distance(span.end) <= 1.5 && q.distance(span.start) <= 1.5);
                independent |= !same && text.iter().all(|s| !d.text_sources.contains(s));
            } else {
                reference = Some((span.start, span.end, d.text_sources.clone()));
            }
            let mut ids = d.text_sources.clone();
            ids.extend([line.clone(), a.clone(), b.clone()]);
            observations.insert(line, (k, ids));
        }
    }
    if observations.len() < 2 || !independent {
        return None;
    }
    let mut values = observations.values().map(|p| p.0).collect::<Vec<_>>();
    values.sort_by(f64::total_cmp);
    let first = values[0];
    let last = *values.last()?;
    if !scales_agree(first, last) {
        return None;
    }
    let mut chosen = if values.len() % 2 == 0 {
        (values[values.len() / 2 - 1] + values[values.len() / 2]) * 0.5
    } else {
        values[values.len() / 2]
    };
    let mut sources: BTreeSet<String> = observations.into_values().flat_map(|p| p.1).collect();
    let mut seen = None;
    for p in primitives {
        let Shape::Text { value, .. } = &p.shape else {
            continue;
        };
        let compact: String = value
            .to_uppercase()
            .chars()
            .filter(|c| !c.is_whitespace())
            .collect();
        let bare = compact
            .trim_start_matches("SCALE")
            .trim_start_matches("MÅLESTOKK")
            .trim_start_matches("SKALA")
            .trim_start_matches(':');
        if let Some(t) = bare.strip_prefix("1:") {
            if let Ok(ratio) = t.parse::<f64>() {
                let k = ratio * 25.4 / 72.;
                if !k.is_finite()
                    || k <= 0.
                    || !scales_agree(k, chosen)
                    || seen.is_some_and(|v: f64| (v - k).abs() > 1e-8)
                {
                    return None;
                }
                seen = Some(k);
                sources.insert(p.id.clone());
            }
        }
    }
    if let Some(k) = seen {
        chosen = k
    }
    Some((chosen, sources.into_iter().collect()))
}
pub fn project_measurements(s: &mut PageSemantics, k: f64) {
    for d in &mut s.dimensions {
        if let Some(len) = d.drawing_length {
            d.calibrated_length_mm = Some(len * k);
            if d.measured_mm > 0. {
                d.calibration_relative_error =
                    Some((len * k - d.measured_mm).abs() / d.measured_mm);
            }
        }
    }
    for b in &mut s.grid_bays {
        b.distance_mm = Some(b.drawing_distance * k);
    }
}
