//! Source-pixel crop inference. Deliberately never mutates scan or placement JSON.
mod bundle_eval;
use openplantrace_rs::visual::classifier::{Classifier, Options};
use openplantrace_symbol_data::{preprocess_rgb, preprocess_target_rgb};
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    error::Error,
    fs,
    path::{Path, PathBuf},
    time::Instant,
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    width: usize,
    height: usize,
    channels: usize,
    items: Vec<Item>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    image_path: String,
    #[serde(default)]
    offset: usize,
    #[serde(default)]
    label_index: Option<usize>,
    source_group: String,
    #[serde(default)]
    split: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Prediction {
    index: usize,
    source_group: String,
    truth: Option<usize>,
    predicted: usize,
    label: String,
    category: String,
    probabilities: Vec<f64>,
    calibrated_probabilities: Vec<f64>,
    accepted: bool,
    inference_ms: f64,
}

fn sha(bytes: &[u8]) -> String {
    Sha256::digest(bytes)
        .iter()
        .map(|x| format!("{x:02x}"))
        .collect()
}

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() || args.len() % 2 != 0 {
        return Err("Usage: --bundle MODEL.json --out NEW.json (--source-manifest SOURCES.json | --primary-predictions PRIMARY.json --refiner-predictions REFINER.json [--primary-manifest MANIFEST --refiner-manifest MANIFEST]); or --model MODEL.onnx --labels labels.txt --out NEW.json (--manifest MANIFEST [--split validation|test] | --image PREPARED.png | --source-image SOURCE_CROP.png) [--input-size 64|96 --preprocessing legacy-ink|target-centered-rgb-v1 --target-box x0,y0,x1,y1 --temperature 1 --confidence 0 --margin 0 --acceptance-enabled false --repeat 1]".into());
    }
    let mut opts = BTreeMap::new();
    for pair in args.chunks(2) {
        if ![
            "--model",
            "--labels",
            "--out",
            "--manifest",
            "--image",
            "--source-image",
            "--split",
            "--temperature",
            "--confidence",
            "--margin",
            "--repeat",
            "--acceptance-enabled",
            "--input-size",
            "--preprocessing",
            "--target-box",
            "--bundle",
            "--primary-predictions",
            "--refiner-predictions",
            "--primary-manifest",
            "--refiner-manifest",
            "--source-manifest",
        ]
        .contains(&pair[0].as_str())
            || opts.insert(pair[0].clone(), pair[1].clone()).is_some()
        {
            return Err(format!("Unknown or repeated option {}", pair[0]).into());
        }
    }
    let required = |name: &str| -> Result<&str> {
        opts.get(name)
            .map(String::as_str)
            .ok_or_else(|| format!("Missing {name}").into())
    };
    if opts.contains_key("--bundle") {
        return bundle_eval::run(&opts);
    }
    if opts.keys().any(|key| {
        matches!(
            key.as_str(),
            "--primary-predictions"
                | "--refiner-predictions"
                | "--primary-manifest"
                | "--refiner-manifest"
                | "--source-manifest"
        )
    }) {
        return Err("Composite prediction/source manifests require --bundle".into());
    }
    let model = PathBuf::from(required("--model")?);
    let labels = PathBuf::from(required("--labels")?);
    let output = PathBuf::from(required("--out")?);
    let side: usize = opts
        .get("--input-size")
        .map_or("64", String::as_str)
        .parse()?;
    let preprocessing = opts
        .get("--preprocessing")
        .map_or("legacy-ink", String::as_str);
    if ![64, 96].contains(&side)
        || !["legacy-ink", "target-centered-rgb-v1"].contains(&preprocessing)
    {
        return Err("Unsupported input size or preprocessing contract".into());
    }
    let target_box: Option<[f64; 4]> = opts
        .get("--target-box")
        .map(|s| -> Result<[f64; 4]> {
            let values = s
                .split(',')
                .map(str::parse::<f64>)
                .collect::<std::result::Result<Vec<_>, _>>()?;
            Ok(values
                .try_into()
                .map_err(|_| "Target box requires four numbers")?)
        })
        .transpose()?;
    let temperature: f64 = opts
        .get("--temperature")
        .map_or("1", String::as_str)
        .parse()?;
    let confidence: f64 = opts
        .get("--confidence")
        .map_or("0", String::as_str)
        .parse()?;
    let margin: f64 = opts.get("--margin").map_or("0", String::as_str).parse()?;
    let repeat: usize = opts.get("--repeat").map_or("1", String::as_str).parse()?;
    let acceptance_enabled: bool = opts
        .get("--acceptance-enabled")
        .map_or("false", String::as_str)
        .parse()?;
    if !temperature.is_finite()
        || temperature <= 0.0
        || !(0.0..=1.0).contains(&confidence)
        || !(0.0..=1.0).contains(&margin)
        || repeat == 0
        || repeat > 100
    {
        return Err("Invalid calibration or repetition options".into());
    }
    let manifest_path = opts.get("--manifest").map(PathBuf::from);
    let image_path = opts.get("--image").map(PathBuf::from);
    let source_path = opts.get("--source-image").map(PathBuf::from);
    if target_box.is_some() && source_path.is_none() {
        return Err("--target-box requires --source-image".into());
    }
    if usize::from(manifest_path.is_some())
        + usize::from(image_path.is_some())
        + usize::from(source_path.is_some())
        != 1
    {
        return Err("Supply exactly one --manifest, --image or --source-image".into());
    }
    let labels_text = fs::read_to_string(&labels)?;
    let names: Vec<_> = labels_text
        .lines()
        .map(str::trim)
        .filter(|x| !x.is_empty() && !x.starts_with('#'))
        .map(|x| x.split_once('|').map_or((x, "Unknown"), |pair| pair))
        .collect();
    let expected = [
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
    if names != expected {
        return Err("Label sidecar differs from the frozen nine-label order or categories".into());
    }
    let mut options = Options::new(model.clone());
    options.labels = Some(labels.clone());
    options.width = side;
    options.height = side;
    options.mean = [0.; 3];
    options.std = [1.; 3];
    options.top_k = 9;
    let loaded = Instant::now();
    let classifier = Classifier::load(options).map_err(|x| format!("Model load: {x}"))?;
    if classifier.class_count() != 9 {
        return Err("Expected exactly nine model output classes".into());
    }
    let load_ms = loaded.elapsed().as_secs_f64() * 1000.;
    let (items, directory, manifest_sha) = if let Some(path) = &manifest_path {
        let bytes = fs::read(path)?;
        let manifest: Manifest = serde_json::from_slice(&bytes)?;
        if (manifest.width, manifest.height, manifest.channels) != (side, side, 3) {
            return Err(format!(
                "Manifest must contain prepared {side}x{side} RGB crops matching --input-size"
            )
            .into());
        }
        (
            manifest.items,
            path.parent().unwrap_or(Path::new(".")).to_path_buf(),
            Some(sha(&bytes)),
        )
    } else {
        let path = fs::canonicalize(image_path.or_else(|| source_path.clone()).unwrap())?;
        (
            vec![Item {
                image_path: path.display().to_string(),
                offset: 0,
                label_index: None,
                source_group: "single-image".into(),
                split: "inference".into(),
            }],
            PathBuf::new(),
            None,
        )
    };
    let mut shards: BTreeMap<PathBuf, Vec<u8>> = BTreeMap::new();
    let mut predictions = Vec::new();
    let mut checksum = Vec::new();
    for (index, item) in items.into_iter().enumerate() {
        if opts
            .get("--split")
            .is_some_and(|split| split != &item.split)
        {
            continue;
        }
        if item.label_index.is_some_and(|x| x >= 9) || item.source_group.is_empty() {
            return Err("Invalid label or source group".into());
        }
        let path = directory.join(&item.image_path);
        let rgb = if path.extension().and_then(|s| s.to_str()) == Some("rgb") {
            if !shards.contains_key(&path) {
                shards.insert(path.clone(), fs::read(&path)?);
            }
            shards[&path]
                .get(
                    item.offset
                        ..item
                            .offset
                            .checked_add(side * side * 3)
                            .ok_or("Offset overflow")?,
                )
                .ok_or("Crop exceeds RGB shard")?
                .to_vec()
        } else {
            let rgba = image::open(&path)?.to_rgba8();
            if u64::from(rgba.width()) * u64::from(rgba.height()) > 16_777_216 {
                return Err("Input exceeds 16 million crop pixels".into());
            }
            let mut image = image::RgbImage::new(rgba.width(), rgba.height());
            for (dst, src) in image.pixels_mut().zip(rgba.pixels()) {
                for channel in 0..3 {
                    dst.0[channel] = ((u32::from(src.0[channel]) * u32::from(src.0[3])
                        + 255 * (255 - u32::from(src.0[3]))
                        + 127)
                        / 255) as u8;
                }
            }
            if source_path.is_some() {
                image = if preprocessing == "legacy-ink" {
                    if target_box.is_some() {
                        return Err("--target-box requires target-centered-rgb-v1".into());
                    }
                    preprocess_rgb(&image, side as u32, 0.15)
                } else {
                    let target =
                        target_box.unwrap_or([0., 0., image.width() as f64, image.height() as f64]);
                    preprocess_target_rgb(&image, side as u32, target, 0.15)?
                };
            }
            if image.dimensions() != (side as u32, side as u32) {
                return Err(format!("{} must already be prepared as {side}x{side} RGB; no inference-time stretching is allowed",path.display()).into());
            }
            image.into_raw()
        };
        checksum.extend_from_slice(&rgb);
        let started = Instant::now();
        let mut classes = Vec::new();
        for _ in 0..repeat {
            classes = classifier
                .predict_rgb(&rgb, side, side)
                .map_err(|x| format!("Prediction {index}: {x}"))?;
        }
        let inference_ms = started.elapsed().as_secs_f64() * 1000. / repeat as f64;
        if classes.len() != 9 {
            return Err("Unexpected class count".into());
        }
        let mut probabilities = vec![0.; 9];
        for class in &classes {
            probabilities[class.class_index] = f64::from(class.confidence);
        }
        // log(p)/T is equivalent to logits/T up to the cancelled additive constant.
        let logits: Vec<_> = probabilities
            .iter()
            .map(|p| p.max(1e-35).ln() / temperature)
            .collect();
        let max = logits.iter().copied().fold(f64::NEG_INFINITY, f64::max);
        let exp: Vec<_> = logits.iter().map(|x| (x - max).exp()).collect();
        let total: f64 = exp.iter().sum();
        let calibrated: Vec<_> = exp.iter().map(|x| x / total).collect();
        let predicted = classes[0].class_index;
        let accepted = acceptance_enabled
            && predicted != 0
            && calibrated[predicted] >= confidence
            && calibrated[predicted] - calibrated[classes[1].class_index] >= margin;
        predictions.push(Prediction {
            index,
            source_group: item.source_group,
            truth: item.label_index,
            predicted,
            label: names[predicted].0.into(),
            category: names[predicted].1.into(),
            probabilities,
            calibrated_probabilities: calibrated,
            accepted,
            inference_ms,
        });
    }
    if predictions.is_empty() {
        return Err("No input rows selected".into());
    }
    let shard_sha: BTreeMap<_, _> = shards
        .iter()
        .map(|(path, bytes)| (path.display().to_string(), sha(bytes)))
        .collect();
    let result = json!({"schema":"openplantrace.trained-crop-predictions.v1","modelSha256":sha(&fs::read(&model)?),"labelsSha256":sha(&fs::read(&labels)?),"manifestSha256":manifest_sha,"shardSha256":shard_sha,"selectedPixelsSha256":sha(&checksum),"engine":"tract-onnx native CPU","inputSize":side,"sourcePreprocessing":preprocessing,"sourceTargetBox":target_box,"preprocessing":if source_path.is_some(){format!("shared Rust {preprocessing}, aspect-preserving {side}px RGB; /255 NCHW")}else{format!("input already prepared {side}px RGB; /255 NCHW")},"scope":"crop classifier only; predictions are advisory and do not mutate walls, doorways or placement","calibration":{"temperature":temperature,"confidence":confidence,"margin":margin,"acceptanceEnabled":acceptance_enabled,"otherIsAlwaysRejected":true},"loadMs":load_ms,"repeat":repeat,"predictions":predictions});
    if output.exists() {
        return Err("Refusing to overwrite existing prediction evidence".into());
    }
    if let Some(parent) = output.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(&output, serde_json::to_vec_pretty(&result)?)?;
    println!(
        "Wrote {} crop predictions to {} (model load {:.1} ms)",
        predictions.len(),
        output.display(),
        load_ms
    );
    Ok(())
}
