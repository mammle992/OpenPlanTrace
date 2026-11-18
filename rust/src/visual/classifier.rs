//! Single-image RGB classification using tract's native CPU ONNX implementation.
use super::{Class, Prediction, Report, crop};
use crate::{model::Scan, object_labels::CATEGORIES};
use sha2::{Digest, Sha256};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::Arc,
};
use tract_onnx::{pb, prelude::*};

pub struct Options {
    pub model: PathBuf,
    pub labels: Option<PathBuf>,
    pub input_name: Option<String>,
    pub output_name: Option<String>,
    pub name: Option<String>,
    pub version: Option<String>,
    pub width: usize,
    pub height: usize,
    pub channels_first: bool,
    pub top_k: usize,
    pub max_crops: usize,
    pub padding: f64,
    pub min_confidence: f32,
    pub mean: [f32; 3],
    pub std: [f32; 3],
}
impl Options {
    pub fn new(model: PathBuf) -> Self {
        Self {
            model,
            labels: None,
            input_name: None,
            output_name: None,
            name: None,
            version: None,
            width: 224,
            height: 224,
            channels_first: true,
            top_k: 5,
            max_crops: 200,
            padding: 18.,
            min_confidence: 0.35,
            mean: [0.485, 0.456, 0.406],
            std: [0.229, 0.224, 0.225],
        }
    }
    fn shape(&self) -> [usize; 4] {
        if self.channels_first {
            [1, 3, self.height, self.width]
        } else {
            [1, self.height, self.width, 3]
        }
    }
    fn validate(&self) -> Result<(), String> {
        if self.width == 0 || self.height == 0 || self.width.saturating_mul(self.height) > 4_194_304
        {
            return Err("invalid RGB input dimensions (maximum 4194304 pixels)".into());
        }
        if self.top_k == 0
            || self.top_k > 20
            || !self.padding.is_finite()
            || self.padding < 0.
            || !(0.0..=1.).contains(&self.min_confidence)
            || self.mean.iter().any(|n| !n.is_finite())
            || self.std.iter().any(|n| !n.is_finite() || *n == 0.)
        {
            return Err("invalid classifier configuration".into());
        }
        Ok(())
    }
}

fn label_category(s: &str) -> &'static str {
    let name = s.to_lowercase().replace(['_', '-'], " ");
    for (category, hints) in [
        ("Stair", &["stair", "trapp", "step"][..]),
        ("Elevator", &["elevator", "lift", "heis"]),
        ("Column", &["column", "pillar", "post"]),
        ("Shaft", &["shaft", "riser"]),
        (
            "PlumbingFixture",
            &[
                "toilet", "wc", "sink", "lav", "basin", "shower", "bath", "urinal",
            ],
        ),
        (
            "ElectricalDevice",
            &["electrical", "outlet", "socket", "switch", "panel", "mccb"],
        ),
        ("Lighting", &["light", "lamp", "fixture", "luminaire"]),
        (
            "HVACEquipment",
            &[
                "hvac", "ahu", "vav", "vent", "diffuser", "duct", "fan", "grille", "damper",
            ],
        ),
        (
            "FireSafety",
            &[
                "fire",
                "sprinkler",
                "alarm",
                "extinguisher",
                "hose",
                "hydrant",
                "smoke",
            ],
        ),
        (
            "Equipment",
            &[
                "equipment",
                "machine",
                "pump",
                "valve",
                "tank",
                "compressor",
                "motor",
                "process",
                "vessel",
            ],
        ),
        (
            "Vehicle",
            &[
                "vehicle",
                "car",
                "auto",
                "parking",
                "garage",
                "garasje",
                "bil",
                "parkering",
            ],
        ),
        (
            "Furniture",
            &[
                "desk",
                "chair",
                "table",
                "sofa",
                "bed",
                "cabinet",
                "furniture",
                "island",
            ],
        ),
        (
            "Structural",
            &["beam", "brace", "steel", "concrete", "structural"],
        ),
        (
            "Fixture",
            &["fixture", "casework", "kitchen", "counter", "appliance"],
        ),
    ] {
        if hints.iter().any(|h| name.contains(h)) {
            return category;
        }
    }
    "Unknown"
}
fn labels(path: Option<&Path>) -> Result<Vec<(String, String)>, String> {
    let Some(path) = path else { return Ok(vec![]) };
    let file = fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let mut result = Vec::new();
    for s in file
        .trim_start_matches('\u{feff}')
        .lines()
        .map(str::trim)
        .filter(|s| !s.is_empty() && !s.starts_with('#'))
    {
        let (label, cat) = s
            .split_once(['|', '\t'])
            .map_or((s, None), |(l, c)| (l.trim(), Some(c.trim())));
        if label.is_empty() {
            return Err("empty class label".into());
        }
        let category = cat
            .and_then(|c| {
                CATEGORIES
                    .iter()
                    .find(|k| k.eq_ignore_ascii_case(c))
                    .copied()
            })
            .filter(|c| *c != "Unknown")
            .unwrap_or_else(|| label_category(label));
        result.push((label.to_owned(), category.to_owned()));
    }
    Ok(result)
}
struct Engine {
    model: Arc<TypedRunnableModel>,
    input: String,
    output: String,
    sha: String,
    version: i64,
    classes: usize,
}
fn load(options: &Options) -> TractResult<Engine> {
    let path = &options.model;
    if fs::metadata(path)?.len() > 512 << 20 {
        return Err(error("model exceeds 512 MiB"));
    }
    let bytes = fs::read(path)?;
    let sha = Sha256::digest(&bytes)
        .iter()
        .map(|b| format!("{b:02x}"))
        .collect();
    let framework = tract_onnx::onnx();
    let proto = framework.proto_model_for_read(&mut bytes.as_slice())?;
    let graph = proto
        .graph
        .as_ref()
        .ok_or_else(|| error("missing ONNX graph"))?;
    let mut inputs = graph
        .input
        .iter()
        .filter(|input| !graph.initializer.iter().any(|t| t.name == input.name));
    let input = inputs
        .next()
        .ok_or_else(|| error("expected one RGB input"))?;
    if inputs.next().is_some()
        || options
            .input_name
            .as_deref()
            .is_some_and(|n| n != input.name)
    {
        return Err(error("expected exactly one named RGB input"));
    }
    let Some(pb::type_proto::Value::TensorType(t)) =
        input.r#type.as_ref().and_then(|t| t.value.as_ref())
    else {
        return Err(error("input must be a float32 tensor"));
    };
    let dims = &t
        .shape
        .as_ref()
        .ok_or_else(|| error("missing RGB input dimensions"))?
        .dim;
    let shape = options.shape();
    if t.elem_type!=1 || dims.len()!=4 || dims.iter().zip(shape).any(|(d,size)|
        matches!(d.value,Some(pb::tensor_shape_proto::dimension::Value::DimValue(v)) if v!=size as i64)){
        return Err(error(&format!("ONNX RGB input must accept float32 {shape:?}; check --kvemo-input-width, --kvemo-input-height and layout")))
    }
    let (index, output) = graph
        .output
        .iter()
        .enumerate()
        .find(|(_, o)| options.output_name.as_ref().is_none_or(|s| s == &o.name))
        .ok_or_else(|| error("missing named ONNX output"))?;
    let mut model = framework.model_for_proto_model(&proto)?;
    let outlet = model.output_outlets()?[index];
    model.select_output_outlets(&[outlet])?;
    model.set_input_fact(0, f32::fact(shape).into())?;
    let model = model.into_optimized()?;
    let fact = model.output_fact(0)?;
    let classes = match fact.shape.as_concrete() {
        Some([c]) | Some([1, c])
            if (1..=65536).contains(c) && fact.datum_type == f32::datum_type() =>
        {
            *c
        }
        _ => {
            return Err(error(
                "classifier output must be float32 [classes] or [1, classes], up to 65536 classes",
            ));
        }
    };
    Ok(Engine {
        input: input.name.clone(),
        output: output.name.clone(),
        sha,
        version: proto.model_version,
        classes,
        model: model.into_runnable()?,
    })
}
fn error(s: &str) -> TractError {
    TractError::msg(s.to_owned())
}

fn tensor(rgb: &[u8], width: usize, height: usize, options: &Options) -> TractResult<Tensor> {
    let pixels = options.width * options.height;
    if width == 0
        || height == 0
        || width.checked_mul(height).and_then(|n| n.checked_mul(3)) != Some(rgb.len())
    {
        return Err(error("incomplete crop RGB data"));
    }
    let mut values = vec![0.; 3 * pixels];
    let mut write_pixel = |index: usize, row: [f32; 3]| {
        for c in 0..3 {
            let i = if options.channels_first {
                c * pixels + index
            } else {
                index * 3 + c
            };
            values[i] = (row[c] / 255. - options.mean[c]) / options.std[c];
        }
    };
    if width == options.width && height == options.height {
        for (index, row) in rgb.chunks_exact(3).enumerate() {
            write_pixel(index, [row[0].into(), row[1].into(), row[2].into()])
        }
    } else {
        let axis = |pixel: usize, src: usize, dst: usize| {
            let p =
                ((pixel as f64 + 0.5) * src as f64 / dst as f64 - 0.5).clamp(0., (src - 1) as f64);
            let a = p.floor() as usize;
            (a, (a + 1).min(src - 1), p - a as f64)
        };
        let lerp = |a: f64, b: f64, t: f64| a + (b - a) * t;
        for y in 0..options.height {
            let (y0, y1, ty) = axis(y, height, options.height);
            for x in 0..options.width {
                let (x0, x1, tx) = axis(x, width, options.width);
                let color = std::array::from_fn(|c| {
                    let at = |x, y| f64::from(rgb[(y * width + x) * 3 + c]);
                    lerp(
                        lerp(at(x0, y0), at(x1, y0), tx),
                        lerp(at(x0, y1), at(x1, y1), tx),
                        ty,
                    ) as f32
                });
                write_pixel(y * options.width + x, color);
            }
        }
    }
    Tensor::from_shape(&options.shape(), &values)
}
impl Engine {
    fn predict(
        &self,
        rgb: &[u8],
        width: usize,
        height: usize,
        options: &Options,
        labels: &[(String, String)],
    ) -> TractResult<Vec<Class>> {
        let result = self
            .model
            .run(tvec!(tensor(rgb, width, height, options)?.into()))?;
        let view = result[0].try_as_plain_ram()?;
        let raw = view.as_slice::<f32>()?;
        if raw.len() != self.classes || raw.iter().any(|n| !n.is_finite()) {
            return Err(error("nonfinite or unexpected model output"));
        }
        let sum = raw.iter().sum::<f32>();
        let mut scores = raw.to_vec();
        if !(sum > 0.98 && sum < 1.02 && scores.iter().all(|x| (0.0..=1.).contains(x))) {
            let max = raw.iter().copied().fold(f32::NEG_INFINITY, f32::max);
            let exps: Vec<_> = raw
                .iter()
                .map(|v| (f64::from(*v) - f64::from(max)).exp())
                .collect();
            let denominator = exps.iter().sum::<f64>();
            for (s, e) in scores.iter_mut().zip(exps) {
                *s = (e / denominator) as f32
            }
        }
        let mut indices: Vec<_> = (0..scores.len()).collect();
        indices.sort_by(|&a, &b| scores[b].total_cmp(&scores[a]).then(a.cmp(&b)));
        Ok(indices
            .into_iter()
            .take(options.top_k)
            .map(|i| {
                let (label, category) = labels
                    .get(i)
                    .cloned()
                    .unwrap_or_else(|| (format!("class_{i}"), "Unknown".into()));
                Class {
                    class_index: i,
                    label,
                    category,
                    confidence: scores[i],
                }
            })
            .collect())
    }
}
pub struct Classifier {
    options: Options,
    engine: Engine,
    labels: Vec<(String, String)>,
}
impl Classifier {
    /// Fixed output dimension validated while loading this model.
    pub fn class_count(&self) -> usize {
        self.engine.classes
    }
    pub fn load(options: Options) -> Result<Self, String> {
        options.validate()?;
        let engine =
            load(&options).map_err(|e| format!("ONNX {}: {e:#}", options.model.display()))?;
        let labels = labels(options.labels.as_deref())?;
        Ok(Self {
            options,
            engine,
            labels,
        })
    }
    /// Evaluate interleaved source RGB, bilinearly resizing pixel centers to the model's input size.
    pub fn predict_rgb(
        &self,
        rgb: &[u8],
        width: usize,
        height: usize,
    ) -> Result<Vec<Class>, String> {
        self.engine
            .predict(rgb, width, height, &self.options, &self.labels)
            .map_err(|e| format!("{e:#}"))
    }
    pub fn classify(&self, scan: &Scan) -> Result<Report, String> {
        let options = &self.options;
        let engine = &self.engine;
        let labels = &self.labels;
        let mut result = Report {
            engine: "tract-onnx".into(),
            model_name: options
                .name
                .clone()
                .or_else(|| options.model.file_stem()?.to_str().map(str::to_owned))
                .ok_or("missing model name")?,
            model_version: options
                .version
                .clone()
                .or_else(|| (engine.version != 0).then(|| engine.version.to_string())),
            model_sha256: engine.sha.clone(),
            input_name: engine.input.clone(),
            output_name: engine.output.clone(),
            input_width: options.width,
            input_height: options.height,
            channels_first: options.channels_first,
            mean: options.mean,
            std: options.std,
            min_confidence: options.min_confidence,
            predictions: vec![],
        };
        let mut attempted = 0;
        for page in &scan.pages {
            if attempted >= options.max_crops {
                break;
            }
            for (object, ids, member) in crop::targets(page)? {
                if attempted >= options.max_crops {
                    break;
                }
                attempted += 1;
                let Some(crop) = crop::render(
                    page,
                    object,
                    &member,
                    options.width,
                    options.height,
                    options.padding,
                ) else {
                    continue;
                };
                let alternatives = engine
                    .predict(&crop.rgb, options.width, options.height, options, labels)
                    .map_err(|e| format!("ONNX inference on {}: {e:#}", object.id))?;
                result.predictions.push(Prediction {
                    page_number: page.page_number,
                    representative_object_id: object.id.clone(),
                    object_ids: ids,
                    source_ids: crop.sources,
                    crop_min: crop.area.min,
                    crop_max: crop.area.max,
                    accepted: alternatives[0].confidence >= options.min_confidence,
                    alternatives,
                });
            }
        }
        Ok(result)
    }
}
