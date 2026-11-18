//! Source-pixel object review. Never changes scan interpretation or placement.
use super::{
    Class,
    classifier::{Classifier, Options},
};
use crate::{input::raster::render::Renderer, model::Scan};
use image::RgbImage;
use openplantrace_symbol_data::{preprocess_rgb, preprocess_target_rgb};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeSet,
    fs,
    path::{Component, Path, PathBuf},
    time::Instant,
};

const LABELS: [(&str, &str); 9] = [
    ("other", "Unknown"),
    ("door", "Structural"),
    ("window", "Structural"),
    ("stair", "Stair"),
    ("toilet", "PlumbingFixture"),
    ("sink", "PlumbingFixture"),
    ("shower", "PlumbingFixture"),
    ("bath", "PlumbingFixture"),
    ("furniture", "Furniture"),
];
pub fn sha256(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect()
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct InputConfig {
    pub width: usize,
    pub height: usize,
    pub channels: usize,
    #[serde(default = "layout")]
    pub layout: String,
    #[serde(default)]
    pub mean: [f32; 3],
    #[serde(default = "ones")]
    pub std: [f32; 3],
    #[serde(default = "legacy")]
    pub preprocessing: String,
    #[serde(default = "context")]
    pub context: f64,
}
fn legacy() -> String {
    "legacy-ink".into()
}
fn layout() -> String {
    "NCHW".into()
}
fn ones() -> [f32; 3] {
    [1.; 3]
}
fn context() -> f64 {
    0.15
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Calibration {
    pub temperature: f64,
    pub confidence: f64,
    #[serde(default)]
    pub margin: f64,
    #[serde(default, alias = "deploymentAccepted")]
    pub acceptance_enabled: bool,
}
#[derive(Clone, Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BundleConfig {
    pub schema: String,
    pub model: String,
    pub model_sha256: String,
    pub labels: String,
    pub labels_sha256: String,
    pub input: InputConfig,
    pub calibration: Calibration,
    #[serde(default)]
    pub recovery: Option<RecoveryConfig>,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct RecoveryConfig {
    pub bundle: String,
    pub bundle_sha256: String,
    pub min_raw_probability: f64,
    pub mode: String,
}
#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BundleMemberMetadata {
    pub model_sha256: String,
    pub bundle_sha256: String,
    pub labels_sha256: String,
    pub input: InputConfig,
}
#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BundlePrediction {
    pub raw_probabilities: Vec<f64>,
    pub calibrated_probabilities: Vec<f64>,
    pub alternatives: Vec<Class>,
    pub accepted_for_review: bool,
    pub recovered_from_other: bool,
}
pub struct SourcePrediction {
    pub primary_input: RgbImage,
    pub refiner_input: Option<RgbImage>,
    pub primary_raw_probabilities: Vec<f64>,
    pub refiner_raw_probabilities: Option<Vec<f64>>,
    pub prediction: BundlePrediction,
}
pub struct ModelBundle {
    pub config: BundleConfig,
    pub classifier: Classifier,
    pub config_sha256: String,
    refiner: Option<Box<ModelBundle>>,
}

fn asset(root: &Path, name: &str) -> Result<PathBuf, String> {
    let relative = Path::new(name);
    if relative.is_absolute()
        || relative
            .components()
            .any(|p| !matches!(p, Component::Normal(_)))
    {
        return Err("bundle asset must be a relative path without parent components".into());
    }
    let path = root
        .join(relative)
        .canonicalize()
        .map_err(|e| e.to_string())?;
    if !path.starts_with(root) {
        return Err("bundle asset resolves outside its directory".into());
    }
    Ok(path)
}
impl ModelBundle {
    pub fn load(path: &Path) -> Result<Self, String> {
        Self::load_inner(path, 0, None)
    }
    fn load_inner(path: &Path, depth: usize, expected_sha: Option<&str>) -> Result<Self, String> {
        let path = if path.is_dir() {
            path.join("model.json")
        } else {
            path.to_owned()
        };
        let path = path.canonicalize().map_err(|e| e.to_string())?;
        let root = path.parent().ok_or("model metadata has no parent")?;
        if fs::metadata(&path).map_err(|e| e.to_string())?.len() > 1 << 20 {
            return Err("model metadata exceeds 1 MiB".into());
        }
        let bytes = fs::read(&path).map_err(|e| e.to_string())?;
        let config_sha256 = sha256(&bytes);
        if expected_sha.is_some_and(|expected| expected != config_sha256) {
            return Err("recovery bundle metadata digest mismatch".into());
        }
        let config: BundleConfig =
            serde_json::from_slice(&bytes).map_err(|e| format!("model metadata: {e}"))?;
        validate_recovery(config.recovery.as_ref(), depth)?;
        if ![
            "openplantrace.trained-symbol-model.v1",
            "openplantrace.object-model-bundle.v1",
        ]
        .contains(&config.schema.as_str())
        {
            return Err("unsupported object-model bundle schema".into());
        }
        let i = &config.input;
        let c = &config.calibration;
        if ![64, 96].contains(&i.width)
            || i.height != i.width
            || i.channels != 3
            || i.layout != "NCHW"
            || i.mean != [0.; 3]
            || i.std != [1.; 3]
            || !["legacy-ink", "target-centered-rgb-v1"].contains(&i.preprocessing.as_str())
            || !i.context.is_finite()
            || !(0.0..=0.5).contains(&i.context)
            || !c.temperature.is_finite()
            || !(0.01..=100.).contains(&c.temperature)
            || !(0.0..=1.).contains(&c.confidence)
            || !(0.0..=1.).contains(&c.margin)
        {
            return Err("unsupported model input or calibration contract".into());
        }
        let model = asset(root, &config.model)?;
        let labels = asset(root, &config.labels)?;
        if fs::metadata(&model).map_err(|e| e.to_string())?.len() > 512 << 20 {
            return Err("model exceeds 512 MiB".into());
        }
        if fs::metadata(&labels).map_err(|e| e.to_string())?.len() > 64 << 10 {
            return Err("model labels exceed64KiB".into());
        }
        if sha256(&fs::read(&model).map_err(|e| e.to_string())?) != config.model_sha256
            || sha256(&fs::read(&labels).map_err(|e| e.to_string())?) != config.labels_sha256
        {
            return Err("bundle model or labels digest mismatch".into());
        }
        let text = fs::read_to_string(&labels).map_err(|e| e.to_string())?;
        let names: Vec<_> = text
            .lines()
            .map(str::trim)
            .filter(|x| !x.is_empty() && !x.starts_with('#'))
            .map(|x| x.split_once('|').unwrap_or((x, "")))
            .collect();
        if names != LABELS {
            return Err("bundle requires the fixed nine semantic labels/categories".into());
        }
        let mut options = Options::new(model);
        options.labels = Some(labels);
        options.width = i.width;
        options.height = i.height;
        options.mean = [0.; 3];
        options.std = [1.; 3];
        options.top_k = 9;
        let classifier = Classifier::load(options)?;
        if classifier.class_count() != 9 {
            return Err("object-model bundle must produce exactly nine classes".into());
        }
        let refiner = if let Some(recovery) = &config.recovery {
            let child = asset(root, &recovery.bundle)?;
            let child = if child.is_dir() {
                child.join("model.json")
            } else {
                child
            };
            let child = child.canonicalize().map_err(|e| e.to_string())?;
            if !child.starts_with(root) {
                return Err("recovery metadata resolves outside its parent bundle".into());
            }
            Some(Box::new(Self::load_inner(
                &child,
                depth + 1,
                Some(&recovery.bundle_sha256),
            )?))
        } else {
            None
        };
        Ok(Self {
            config,
            classifier,
            config_sha256,
            refiner,
        })
    }
    pub fn refiner_metadata(&self) -> Option<BundleMemberMetadata> {
        self.refiner.as_ref().map(|child| BundleMemberMetadata {
            model_sha256: child.config.model_sha256.clone(),
            bundle_sha256: child.config_sha256.clone(),
            labels_sha256: child.config.labels_sha256.clone(),
            input: child.config.input.clone(),
        })
    }
    pub fn prepare(&self, image: &RgbImage, target: [f64; 4]) -> Result<RgbImage, String> {
        let c = &self.config.input;
        match c.preprocessing.as_str() {
            "legacy-ink" => Ok(preprocess_rgb(image, c.width as u32, c.context as f32)),
            "target-centered-rgb-v1" => {
                preprocess_target_rgb(image, c.width as u32, target, c.context)
            }
            _ => Err("unsupported preprocessing".into()),
        }
    }
    /// Existing prepared-input callers retain their solo-model contract.
    /// Recovery requires the original RGB crop or both members' probability vectors.
    pub fn predict(&self, image: &RgbImage) -> Result<(Vec<Class>, Vec<f64>, bool), String> {
        if self.config.recovery.is_some() {
            return Err(
                "recovery bundle requires predict_source or both member probability vectors".into(),
            );
        }
        let raw = self.raw_probabilities(image)?;
        let prediction = self.predict_probabilities(&raw, None)?;
        Ok((
            prediction.alternatives,
            prediction.calibrated_probabilities,
            prediction.accepted_for_review,
        ))
    }
    pub fn raw_probabilities(&self, image: &RgbImage) -> Result<Vec<f64>, String> {
        let side = self.config.input.width;
        if image.dimensions() != (side as u32, side as u32) {
            return Err("bundle inference requires exactly prepared input dimensions".into());
        }
        let raw = self.classifier.predict_rgb(image.as_raw(), side, side)?;
        if raw.len() != 9 {
            return Err("object-model result must contain nine classes".into());
        }
        let mut seen = [false; 9];
        let mut probabilities = vec![0.; 9];
        for r in &raw {
            if r.class_index >= 9 || seen[r.class_index] {
                return Err("object-model result has repeated or invalid class indices".into());
            }
            seen[r.class_index] = true;
            probabilities[r.class_index] = f64::from(r.confidence);
        }
        validate_probabilities(&probabilities)?;
        Ok(probabilities)
    }
    pub fn predict_probabilities(
        &self,
        base: &[f64],
        refiner: Option<&[f64]>,
    ) -> Result<BundlePrediction, String> {
        route_probabilities(
            &self.config.calibration,
            self.config.recovery.as_ref(),
            base,
            refiner,
        )
    }
    pub fn predict_source(
        &self,
        image: &RgbImage,
        target: [f64; 4],
    ) -> Result<SourcePrediction, String> {
        let primary_input = self.prepare(image, target)?;
        let primary_raw_probabilities = self.raw_probabilities(&primary_input)?;
        let mut refiner_input = None;
        let mut refiner_raw_probabilities = None;
        // The primary raw rank is the only routing evidence. Child calibration
        // never participates, and non-Other primary results are never reconsidered.
        if ranked_indices(&primary_raw_probabilities)[0] == 0 {
            if let Some(child) = &self.refiner {
                let input = child.prepare(image, target)?;
                refiner_raw_probabilities = Some(child.raw_probabilities(&input)?);
                refiner_input = Some(input);
            }
        }
        let prediction = self.predict_probabilities(
            &primary_raw_probabilities,
            refiner_raw_probabilities.as_deref(),
        )?;
        Ok(SourcePrediction {
            primary_input,
            refiner_input,
            primary_raw_probabilities,
            refiner_raw_probabilities,
            prediction,
        })
    }
}

fn validate_recovery(recovery: Option<&RecoveryConfig>, depth: usize) -> Result<(), String> {
    if let Some(recovery) = recovery {
        if depth >= 1 {
            return Err("nested recovery bundles are limited to one child level".into());
        }
        if recovery.mode != "other-only"
            || !recovery.min_raw_probability.is_finite()
            || !(0.0..=1.0).contains(&recovery.min_raw_probability)
            || recovery.bundle_sha256.len() != 64
            || !recovery
                .bundle_sha256
                .bytes()
                .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
        {
            return Err("unsupported recovery routing contract".into());
        }
    }
    Ok(())
}
fn validate_probabilities(probabilities: &[f64]) -> Result<(), String> {
    if probabilities.len() != 9
        || probabilities
            .iter()
            .any(|p| !p.is_finite() || !(0.0..=1.0).contains(p))
        || (probabilities.iter().sum::<f64>() - 1.0).abs() > 1e-5
    {
        return Err(
            "object probabilities must contain nine finite values in0..1 summing to1 within1e-5"
                .into(),
        );
    }
    Ok(())
}
fn ranked_indices(probabilities: &[f64]) -> Vec<usize> {
    let mut indices: Vec<_> = (0..probabilities.len()).collect();
    indices.sort_by(|&a, &b| {
        probabilities[b]
            .partial_cmp(&probabilities[a])
            .unwrap()
            .then(a.cmp(&b))
    });
    indices
}
fn route_probabilities(
    calibration: &Calibration,
    recovery: Option<&RecoveryConfig>,
    base: &[f64],
    refiner: Option<&[f64]>,
) -> Result<BundlePrediction, String> {
    validate_probabilities(base)?;
    validate_recovery(recovery, 0)?;
    if let Some(probabilities) = refiner {
        validate_probabilities(probabilities)?;
        if recovery.is_none() {
            return Err("refiner probabilities require a recovery routing contract".into());
        }
    }
    if !calibration.temperature.is_finite()
        || !(0.01..=100.0).contains(&calibration.temperature)
        || !(0.0..=1.0).contains(&calibration.confidence)
        || !(0.0..=1.0).contains(&calibration.margin)
    {
        return Err("invalid parent calibration".into());
    }
    let base_rank = ranked_indices(base);
    if base_rank[0] == 0 && recovery.is_some() && refiner.is_none() {
        return Err("primary Other result requires recovery member probabilities".into());
    }
    let selected = if base_rank[0] == 0 {
        match (recovery, refiner) {
            (Some(policy), Some(probabilities)) => {
                let rank = ranked_indices(probabilities);
                (rank[0] != 0 && probabilities[rank[0]] >= policy.min_raw_probability)
                    .then_some(probabilities)
            }
            _ => None,
        }
    } else {
        None
    };
    let recovered_from_other = selected.is_some();
    let raw_probabilities = selected.unwrap_or(base).to_vec();
    let rank = ranked_indices(&raw_probabilities);
    let logits: Vec<_> = raw_probabilities
        .iter()
        .map(|p| p.max(1e-35).ln() / calibration.temperature)
        .collect();
    let max = logits.iter().copied().fold(f64::NEG_INFINITY, f64::max);
    let exps: Vec<_> = logits.iter().map(|x| (x - max).exp()).collect();
    let sum: f64 = exps.iter().sum();
    let calibrated_probabilities: Vec<_> = exps.iter().map(|x| x / sum).collect();
    // Raw rank is deterministic even if calibrated float32 display values tie.
    let alternatives = rank
        .iter()
        .map(|&i| Class {
            class_index: i,
            label: LABELS[i].0.into(),
            category: LABELS[i].1.into(),
            confidence: calibrated_probabilities[i] as f32,
        })
        .collect();
    let accepted_for_review = calibration.acceptance_enabled
        && rank[0] != 0
        && calibrated_probabilities[rank[0]] >= calibration.confidence
        && calibrated_probabilities[rank[0]] - calibrated_probabilities[rank[1]]
            >= calibration.margin;
    Ok(BundlePrediction {
        raw_probabilities,
        calibrated_probabilities,
        alternatives,
        accepted_for_review,
        recovered_from_other,
    })
}

/// All region coordinates refer to the displayed, rotated PDF crop box, normalized0..1.
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Region {
    pub id: String,
    pub page_number: u32,
    pub target: [f64; 4],
    /// Optional bounded source ROI, used to reproduce an explicitly reviewed crop.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub context: Option<[f64; 4]>,
    #[serde(default)]
    pub object_ids: Vec<String>,
    #[serde(default)]
    pub source_ids: Vec<String>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Regions {
    pub schema: String,
    pub source_sha256: String,
    pub regions: Vec<Region>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ReviewPrediction {
    pub region: Region,
    pub source_context: [f64; 4],
    pub prepared_crop: String,
    pub source_crop: String,
    pub source_pixels_sha256: String,
    pub source_target_box: [f64; 4],
    pub prepared_pixels_sha256: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refiner_prepared_crop: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refiner_prepared_pixels_sha256: Option<String>,
    pub refiner_evaluated: bool,
    pub recovered_from_other: bool,
    pub primary_raw_probabilities: Vec<f64>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refiner_raw_probabilities: Option<Vec<f64>>,
    pub raw_probabilities: Vec<f64>,
    pub accepted_for_review: bool,
    pub automatic_placement_allowed: bool,
    pub alternatives: Vec<Class>,
    pub inference_ms: f64,
    pub calibrated_probabilities: Vec<f64>,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SourceReport {
    pub schema: String,
    pub source_sha256: String,
    pub model_sha256: String,
    pub bundle_sha256: String,
    pub labels_sha256: String,
    pub proposal_origin: String,
    pub advisory_only: bool,
    pub preprocessing: InputConfig,
    pub calibration: Calibration,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub recovery: Option<RecoveryConfig>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub refiner: Option<BundleMemberMetadata>,
    pub source_render_max_dimension: u32,
    pub requested_region_count: usize,
    pub withheld_regions: Vec<WithheldRegion>,
    pub predictions: Vec<ReviewPrediction>,
    pub warnings: Vec<String>,
    pub elapsed_ms: f64,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WithheldRegion {
    pub id: String,
    pub page_number: u32,
    pub reason: String,
}

pub fn validate_regions(regions: &[Region]) -> Result<(), String> {
    if regions.len() > 500 {
        return Err("source review is limited to500 explicit regions".into());
    }
    let mut seen = BTreeSet::new();
    for r in regions {
        let [x0, y0, x1, y1] = r.target;
        if r.id.trim().is_empty()
            || !seen.insert((r.page_number, r.id.as_str()))
            || r.page_number == 0
            || r.target
                .iter()
                .any(|x| !x.is_finite() || !(0.0..=1.).contains(x))
            || x0 >= x1
            || y0 >= y1
        {
            return Err("invalid source-region target or repeated ID".into());
        }
        if let Some([a, b, c, d]) = r.context {
            if [a, b, c, d]
                .iter()
                .any(|v| !v.is_finite() || !(0.0..=1.).contains(v))
                || a > x0
                || b > y0
                || c < x1
                || d < y1
                || a >= c
                || b >= d
            {
                return Err("source context must contain its target inside the page".into());
            }
        }
    }
    Ok(())
}

pub fn scan_regions(scan: &Scan, limit: usize) -> Result<Vec<Region>, String> {
    let mut regions = Vec::new();
    for page in &scan.pages {
        for member in crate::object_labels::source_members(page)? {
            if member.category == "TextLabel" {
                continue;
            }
            if regions.len() >= limit {
                return Ok(regions);
            }
            let b = member.bounds;
            let target = [
                (b.min.x / page.width).max(0.),
                (b.min.y / page.height).max(0.),
                (b.max.x / page.width).min(1.),
                (b.max.y / page.height).min(1.),
            ];
            if target[2] <= target[0] || target[3] <= target[1] {
                continue;
            }
            regions.push(Region {
                id: member.object_id.clone(),
                page_number: page.page_number,
                target,
                context: None,
                object_ids: vec![member.object_id.clone()],
                source_ids: member.source_ids.clone(),
            });
        }
    }
    Ok(regions)
}

pub fn review_pdf(
    bytes: &[u8],
    bundle: &ModelBundle,
    regions: &[Region],
    origin: &str,
    out: &Path,
) -> Result<SourceReport, String> {
    validate_regions(regions)?;
    let started = Instant::now();
    let renderer = Renderer::new(bytes)?;
    fs::create_dir(out.join("object-review-crops")).map_err(|e| e.to_string())?;
    let mut report = SourceReport {
        schema: "openplantrace.source-object-review.v1".into(),
        source_sha256: sha256(bytes),
        model_sha256: bundle.config.model_sha256.clone(),
        bundle_sha256: bundle.config_sha256.clone(),
        proposal_origin: origin.into(),
        labels_sha256: bundle.config.labels_sha256.clone(),
        advisory_only: true,
        preprocessing: bundle.config.input.clone(),
        calibration: bundle.config.calibration.clone(),
        recovery: bundle.config.recovery.clone(),
        refiner: bundle.refiner_metadata(),
        source_render_max_dimension: 384,
        requested_region_count: regions.len(),
        withheld_regions: Vec::new(),
        predictions: Vec::new(),
        warnings: Vec::new(),
        elapsed_ms: 0.,
    };
    // Bounded page-local reuse amortizes resource decoding without retaining
    // an unbounded collection of glyphs for different region scales.
    let pages: BTreeSet<_> = regions.iter().map(|r| r.page_number).collect();
    for page in pages {
        let mut session = renderer.session();
        let (pw, ph) = renderer.page_dimensions((page - 1) as usize)?;
        for (page_region, (index, r)) in regions
            .iter()
            .enumerate()
            .filter(|(_, r)| r.page_number == page)
            .enumerate()
        {
            // Glyph/hint caches include each exact crop scale and have no
            // eviction in Hayro. Bound their lifetime without reparsing the PDF
            // or changing raster settings, input pixels or model contracts.
            if page_region > 0 && page_region % 4 == 0 {
                session = renderer.session();
            }
            let [x0, y0, x1, y1] = r.target;
            let pad = bundle.config.input.context * ((x1 - x0) * pw).max((y1 - y0) * ph);
            let area =
                r.context
                    .unwrap_or([x0 - pad / pw, y0 - pad / ph, x1 + pad / pw, y1 + pad / ph]);
            // A common native source capture avoids changing the primary input
            // resolution merely because an optional refiner is configured.
            let raster_side = 384;
            let rendered = match session.render_region((page - 1) as usize, area, raster_side) {
                Ok(v) => v,
                Err(e) => {
                    report
                        .warnings
                        .push(format!("{}: source crop withheld: {e}", r.id));
                    report.withheld_regions.push(WithheldRegion {
                        id: r.id.clone(),
                        page_number: r.page_number,
                        reason: e,
                    });
                    continue;
                }
            };
            let rgb: Vec<_> = rendered
                .rgba
                .chunks_exact(4)
                .flat_map(|p| [p[0], p[1], p[2]])
                .collect();
            let image = RgbImage::from_raw(rendered.width as u32, rendered.height as u32, rgb)
                .ok_or("invalid source RGB")?;
            // Match the renderer's one uniform scale, not two rounded bitmap dimensions.
            let scale = raster_side as f32
                / (((area[2] - area[0]) * pw).max((area[3] - area[1]) * ph) as f32);
            let sx = pw * f64::from(scale);
            let sy = ph * f64::from(scale);
            let target = [
                ((x0 - area[0]) * sx).clamp(0., rendered.width as f64),
                ((y0 - area[1]) * sy).clamp(0., rendered.height as f64),
                ((x1 - area[0]) * sx).clamp(0., rendered.width as f64),
                ((y1 - area[1]) * sy).clamp(0., rendered.height as f64),
            ];
            let when = Instant::now();
            let source_prediction = bundle.predict_source(&image, target)?;
            let inference_ms = when.elapsed().as_secs_f64() * 1000.;
            let relative = format!("object-review-crops/{index:04}.png");
            source_prediction
                .primary_input
                .save(out.join(&relative))
                .map_err(|e| e.to_string())?;
            let refiner_prepared_crop = if let Some(refiner) = &source_prediction.refiner_input {
                let relative = format!("object-review-crops/{index:04}-refiner.png");
                refiner
                    .save(out.join(&relative))
                    .map_err(|e| e.to_string())?;
                Some(relative)
            } else {
                None
            };
            let refiner_prepared_pixels_sha256 = source_prediction
                .refiner_input
                .as_ref()
                .map(|input| sha256(input.as_raw()));
            let source_crop = format!("object-review-crops/{index:04}-source.png");
            image
                .save(out.join(&source_crop))
                .map_err(|e| e.to_string())?;
            report.predictions.push(ReviewPrediction {
                region: r.clone(),
                source_context: area,
                prepared_crop: relative,
                source_crop,
                source_pixels_sha256: sha256(image.as_raw()),
                source_target_box: target,
                prepared_pixels_sha256: sha256(source_prediction.primary_input.as_raw()),
                refiner_prepared_crop,
                refiner_prepared_pixels_sha256,
                refiner_evaluated: source_prediction.refiner_input.is_some(),
                recovered_from_other: source_prediction.prediction.recovered_from_other,
                primary_raw_probabilities: source_prediction.primary_raw_probabilities,
                refiner_raw_probabilities: source_prediction.refiner_raw_probabilities,
                raw_probabilities: source_prediction.prediction.raw_probabilities,
                accepted_for_review: source_prediction.prediction.accepted_for_review,
                automatic_placement_allowed: false,
                alternatives: source_prediction.prediction.alternatives,
                calibrated_probabilities: source_prediction.prediction.calibrated_probabilities,
                inference_ms,
            });
        }
    }
    report.elapsed_ms = started.elapsed().as_secs_f64() * 1000.;
    Ok(report)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    fn policy() -> RecoveryConfig {
        RecoveryConfig {
            bundle: "refiner/model.json".into(),
            bundle_sha256: "0".repeat(64),
            min_raw_probability: 0.85,
            mode: "other-only".into(),
        }
    }
    fn calibration(temperature: f64) -> Calibration {
        Calibration {
            temperature,
            confidence: 0.0,
            margin: 0.0,
            acceptance_enabled: true,
        }
    }
    fn peaked(index: usize, probability: f64) -> Vec<f64> {
        let mut values = vec![(1.0 - probability) / 8.0; 9];
        values[index] = probability;
        values
    }
    #[test]
    fn recovery_keeps_every_primary_non_other_vector_unchanged() {
        let policy = policy();
        for index in 1..9 {
            let base = peaked(index, 0.8);
            let refiner = peaked((index % 8) + 1, 0.99);
            let solo = route_probabilities(&calibration(2.0), None, &base, None).unwrap();
            let result =
                route_probabilities(&calibration(2.0), Some(&policy), &base, Some(&refiner))
                    .unwrap();
            assert_eq!(result.raw_probabilities, base);
            assert_eq!(
                result.calibrated_probabilities,
                solo.calibrated_probabilities
            );
            assert_eq!(result.alternatives[0].class_index, index);
            assert_eq!(result.accepted_for_review, solo.accepted_for_review);
            assert!(!result.recovered_from_other);
            // A non-Other result does not require evaluating the refiner.
            assert!(route_probabilities(&calibration(2.0), Some(&policy), &base, None).is_ok());
        }
    }
    #[test]
    fn recovery_replaces_the_whole_vector_at_the_inclusive_raw_threshold() {
        let base = peaked(0, 0.9);
        let refiner = vec![0.05, 0.85, 0.1, 0., 0., 0., 0., 0., 0.];
        let result =
            route_probabilities(&calibration(1.0), Some(&policy()), &base, Some(&refiner)).unwrap();
        assert!(result.recovered_from_other);
        assert_eq!(result.raw_probabilities, refiner);
        assert_eq!(result.alternatives[0].class_index, 1);
        assert!(result.accepted_for_review);
        let below = peaked(1, 0.85 - 1e-12);
        let result =
            route_probabilities(&calibration(1.0), Some(&policy()), &base, Some(&below)).unwrap();
        assert!(!result.recovered_from_other);
        assert_eq!(result.raw_probabilities, base);
    }
    #[test]
    fn recovery_never_promotes_refiner_other_and_ties_choose_lowest_index() {
        let base = vec![0.5, 0.5, 0., 0., 0., 0., 0., 0., 0.];
        let child_other = peaked(0, 0.99);
        let result = route_probabilities(
            &calibration(1.0),
            Some(&policy()),
            &base,
            Some(&child_other),
        )
        .unwrap();
        assert!(!result.recovered_from_other);
        assert_eq!(result.alternatives[0].class_index, 0);
        assert!(!result.accepted_for_review);
        let child = peaked(2, 0.9);
        assert!(
            route_probabilities(&calibration(1.0), Some(&policy()), &base, Some(&child))
                .unwrap()
                .recovered_from_other
        );
        let non_other_tie = vec![0., 0.5, 0.5, 0., 0., 0., 0., 0., 0.];
        let result = route_probabilities(
            &calibration(1.0),
            Some(&policy()),
            &non_other_tie,
            Some(&child),
        )
        .unwrap();
        assert!(!result.recovered_from_other);
        assert_eq!(result.alternatives[0].class_index, 1);
    }
    #[test]
    fn recovery_applies_only_the_global_parent_temperature_once() {
        let temperature = 4.206890268939055;
        let child = vec![0.05, 0.85, 0.1, 0., 0., 0., 0., 0., 0.];
        let result = route_probabilities(
            &calibration(temperature),
            Some(&policy()),
            &peaked(0, 0.9),
            Some(&child),
        )
        .unwrap();
        let powers: Vec<_> = child
            .iter()
            .map(|p| p.max(1e-35).powf(1.0 / temperature))
            .collect();
        let sum: f64 = powers.iter().sum();
        for (actual, power) in result.calibrated_probabilities.iter().zip(powers) {
            assert!((actual - power / sum).abs() < 1e-14);
        }
        let mut disabled = calibration(temperature);
        disabled.acceptance_enabled = false;
        assert!(
            !route_probabilities(&disabled, Some(&policy()), &peaked(0, 0.9), Some(&child))
                .unwrap()
                .accepted_for_review
        );
    }
    #[test]
    fn recovery_rejects_invalid_vectors_missing_members_and_nested_policies() {
        let good = peaked(0, 0.9);
        for invalid in [
            vec![0.1; 8],
            vec![0.1; 9],
            vec![f64::NAN; 9],
            vec![f64::INFINITY; 9],
            vec![-0.1, 1.1, 0., 0., 0., 0., 0., 0., 0.],
        ] {
            assert!(route_probabilities(&calibration(1.0), None, &invalid, None).is_err());
            assert!(
                route_probabilities(&calibration(1.0), Some(&policy()), &good, Some(&invalid))
                    .is_err()
            );
        }
        assert!(route_probabilities(&calibration(1.0), Some(&policy()), &good, None).is_err());
        assert!(
            route_probabilities(&calibration(1.0), None, &good, Some(&peaked(1, 0.9))).is_err()
        );
        assert!(route_probabilities(&calibration(f64::NAN), None, &good, None).is_err());
        assert!(validate_recovery(Some(&policy()), 1).is_err());
        let mut bad = policy();
        bad.mode = "all-classes".into();
        assert!(validate_recovery(Some(&bad), 0).is_err());
        bad = policy();
        bad.min_raw_probability = f64::NAN;
        assert!(validate_recovery(Some(&bad), 0).is_err());
        bad = policy();
        bad.bundle_sha256 = "f".repeat(63);
        assert!(validate_recovery(Some(&bad), 0).is_err());
    }
    #[test]
    fn recovery_metadata_is_pinned_before_parsing_or_loading_child_weights() {
        let root = std::env::temp_dir().join(format!(
            "opt-recovery-metadata-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir(&root).unwrap();
        let path = root.join("model.json");
        fs::write(&path, b"not metadata").unwrap();
        assert!(
            ModelBundle::load_inner(&path, 1, Some(&"0".repeat(64)))
                .err()
                .unwrap()
                .contains("metadata digest mismatch")
        );
        let config = json!({"schema":"openplantrace.object-model-bundle.v1","model":"missing.onnx","modelSha256":"0".repeat(64),"labels":"missing.txt","labelsSha256":"0".repeat(64),"input":{"width":64,"height":64,"channels":3},"calibration":{"temperature":1.,"confidence":1.,"acceptanceEnabled":false},"recovery":{"bundle":"nested/model.json","bundleSha256":"0".repeat(64),"minRawProbability":0.85,"mode":"other-only"}});
        fs::write(&path, config.to_string()).unwrap();
        assert!(
            ModelBundle::load_inner(&path, 1, None)
                .err()
                .unwrap()
                .contains("one child level")
        );
    }
    #[test]
    fn explicit_context_and_unique_targets_are_enforced_before_rendering() {
        let r = Region {
            id: "source-target".into(),
            page_number: 1,
            target: [0.2, 0.3, 0.4, 0.5],
            context: Some([0.1, 0.2, 0.5, 0.6]),
            object_ids: vec![],
            source_ids: vec![],
        };
        assert!(validate_regions(&[r.clone()]).is_ok());
        assert!(validate_regions(&[r.clone(), r.clone()]).is_err());
        let mut bad = r.clone();
        bad.context = Some([0.3, 0.2, 0.5, 0.6]);
        assert!(validate_regions(&[bad]).is_err());
        let mut bad = r;
        bad.target[0] = f64::NAN;
        assert!(validate_regions(&[bad]).is_err());
    }
    #[test]
    fn bundle_pins_model_and_full_semantic_label_order() {
        let root = std::env::temp_dir().join(format!(
            "opt-source-model-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        fs::create_dir(&root).unwrap();
        fs::write(root.join("model.onnx"), b"deliberately-not-onnx").unwrap();
        let labels = LABELS
            .iter()
            .map(|(a, b)| format!("{a}|{b}\n"))
            .collect::<String>();
        fs::write(root.join("labels.txt"), &labels).unwrap();
        let mut config = json!({"schema":"openplantrace.object-model-bundle.v1","model":"model.onnx","modelSha256":"0".repeat(64),"labels":"labels.txt","labelsSha256":sha256(labels.as_bytes()),"input":{"width":96,"height":96,"channels":3,"preprocessing":"target-centered-rgb-v1"},"calibration":{"temperature":1.,"confidence":1.,"acceptanceEnabled":false}});
        fs::write(root.join("model.json"), config.to_string()).unwrap();
        assert!(
            ModelBundle::load(&root)
                .err()
                .unwrap()
                .contains("digest mismatch")
        );
        config["modelSha256"] = sha256(b"deliberately-not-onnx").into();
        let wrong = labels.replace("door|Structural", "wrong|Structural");
        fs::write(root.join("labels.txt"), &wrong).unwrap();
        config["labelsSha256"] = sha256(wrong.as_bytes()).into();
        fs::write(root.join("model.json"), config.to_string()).unwrap();
        assert!(
            ModelBundle::load(&root)
                .err()
                .unwrap()
                .contains("nine semantic")
        );
        assert!(asset(&root, "../outside.onnx").is_err());
    }
}
