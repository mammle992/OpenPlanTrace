//! Predictions from an explicitly supplied model, linked to the original deterministic objects.
use crate::model::{Point, Scan};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[cfg(feature = "onnx")]
pub mod classifier;
mod crop;
pub mod dataset;
#[cfg(feature = "onnx")]
pub mod source_proposals;
#[cfg(feature = "onnx")]
pub mod source_review;

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Class {
    pub class_index: usize,
    pub label: String,
    pub category: String,
    pub confidence: f32,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Prediction {
    pub page_number: u32,
    pub representative_object_id: String,
    pub object_ids: Vec<String>,
    pub source_ids: Vec<String>,
    pub crop_min: Point,
    pub crop_max: Point,
    pub accepted: bool,
    pub alternatives: Vec<Class>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Report {
    pub engine: String,
    pub model_name: String,
    pub model_version: Option<String>,
    pub model_sha256: String,
    pub input_name: String,
    pub output_name: String,
    pub input_width: usize,
    pub input_height: usize,
    pub channels_first: bool,
    pub mean: [f32; 3],
    pub std: [f32; 3],
    pub min_confidence: f32,
    pub predictions: Vec<Prediction>,
}
impl Report {
    pub(crate) fn validate(&self, scan: &Scan) -> Result<(), String> {
        if self.engine != "tract-onnx"
            || self.model_name.trim().is_empty()
            || self.model_sha256.len() != 64
            || !self.model_sha256.bytes().all(|b| b.is_ascii_hexdigit())
            || self.input_name.is_empty()
            || self.output_name.is_empty()
            || self.input_width == 0
            || self.input_height == 0
            || self.input_width.saturating_mul(self.input_height) > 4_194_304
            || !(0.0..=1.).contains(&self.min_confidence)
            || self.mean.iter().any(|n| !n.is_finite())
            || self.std.iter().any(|n| !n.is_finite() || *n == 0.)
        {
            return Err("invalid visual classifier metadata".into());
        }
        let mut assigned = HashSet::new();
        let mut source_index = HashMap::new();
        for record in &self.predictions {
            let page = scan
                .pages
                .iter()
                .find(|p| p.page_number == record.page_number)
                .ok_or("invalid visual classification page")?;
            let min = record.crop_min;
            let max = record.crop_max;
            if !min.finite()
                || !max.finite()
                || min.x < 0.
                || min.y < 0.
                || max.x > page.width
                || max.y > page.height
                || min.x >= max.x
                || min.y >= max.y
                || record.source_ids.is_empty()
                || record.alternatives.is_empty()
                || record.alternatives.len() > 20
                || !record.object_ids.contains(&record.representative_object_id)
                || record.object_ids.iter().any(|id| {
                    !assigned.insert((record.page_number, id))
                        || !page.objects.iter().any(|o| &o.id == id)
                })
            {
                return Err("invalid visual prediction target or crop".into());
            }
            let mut classes = HashSet::new();
            if record.alternatives.iter().any(|c| {
                c.label.trim().is_empty()
                    || !(0.0..=1.).contains(&c.confidence)
                    || !classes.insert(c.class_index)
            }) || record
                .alternatives
                .windows(2)
                .any(|w| w[0].confidence < w[1].confidence)
                || record.accepted != (record.alternatives[0].confidence >= self.min_confidence)
            {
                return Err("invalid visual class probability or acceptance threshold".into());
            }
            let target = page
                .objects
                .iter()
                .find(|o| o.id == record.representative_object_id)
                .unwrap();
            let source_ids = source_index.entry(page.page_number).or_insert_with(|| {
                page.primitives
                    .iter()
                    .map(|p| p.id.as_str())
                    .collect::<HashSet<_>>()
            });
            if !target
                .source_ids
                .iter()
                .all(|s| record.source_ids.contains(s))
                || record
                    .source_ids
                    .iter()
                    .any(|s| !source_ids.contains(s.as_str()))
            {
                return Err("invalid visual crop source evidence".into());
            }
        }
        Ok(())
    }
}
