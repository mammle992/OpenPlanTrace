//! Evidence-only wrappers around the scanner's exact model-bundle implementation.
use super::{Result, sha};
use image::RgbImage;
use openplantrace_rs::visual::source_review::{BundlePrediction, ModelBundle};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs,
    path::{Path, PathBuf},
    time::Instant,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct NativePredictions {
    model_sha256: String,
    labels_sha256: String,
    #[serde(default)]
    manifest_sha256: Option<String>,
    predictions: Vec<NativeRow>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct NativeRow {
    #[serde(default, alias = "itemIndex")]
    index: Option<usize>,
    #[serde(default)]
    id: Option<String>,
    source_group: String,
    #[serde(default)]
    original_source_id: Option<String>,
    #[serde(default)]
    augmentation_id: Option<u64>,
    #[serde(default, alias = "expected")]
    truth: Option<usize>,
    probabilities: Vec<f64>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct JoinManifest {
    items: Vec<JoinItem>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct JoinItem {
    #[serde(default)]
    id: Option<String>,
    source_group: String,
    #[serde(default)]
    original_source_id: Option<String>,
    #[serde(default)]
    augmentation_id: Option<u64>,
    #[serde(default)]
    label_index: Option<usize>,
    #[serde(default)]
    split: Option<String>,
}
#[derive(Clone)]
struct Case {
    id: String,
    source_group: String,
    original_source_id: Option<String>,
    augmentation_id: Option<u64>,
    truth: Option<usize>,
    probabilities: Vec<f64>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SourceManifest {
    schema: String,
    items: Vec<SourceItem>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SourceItem {
    id: String,
    source_group: String,
    #[serde(default)]
    label_index: Option<usize>,
    image_path: String,
    #[serde(default)]
    image_sha256: Option<String>,
    target: [f64; 4],
}

fn required<'a>(opts: &'a BTreeMap<String, String>, name: &str) -> Result<&'a str> {
    opts.get(name)
        .map(String::as_str)
        .ok_or_else(|| format!("Missing {name}").into())
}

fn valid_identity(id: &str, source: &str, truth: Option<usize>) -> Result<()> {
    if id.trim().is_empty() || source.trim().is_empty() || truth.is_some_and(|x| x >= 9) {
        return Err(
            "Every case needs a nonempty ID/sourceGroup and an optional label in 0..9".into(),
        );
    }
    Ok(())
}

fn canonical_id(
    id: Option<&str>,
    original: Option<&str>,
    augmentation: Option<u64>,
    source: &str,
) -> Result<String> {
    if let Some(id) = id {
        if id.trim().is_empty() {
            return Err("Empty explicit case ID".into());
        }
        return Ok(id.into());
    }
    if let (Some(original), Some(augmentation)) = (original, augmentation) {
        if original.trim().is_empty() {
            return Err("Empty originalSourceId".into());
        }
        return Ok(serde_json::to_string(&(original, augmentation))?);
    }
    if original.is_some() != augmentation.is_some() {
        return Err(
            "A case needs both originalSourceId and augmentationId, or an explicit id".into(),
        );
    }
    // Legacy native envelopes can join by their exact unique sourceGroup only.
    // Repeated originals or differently formatted groups require explicit manifests.
    if source.trim().is_empty() {
        return Err("Missing case identity".into());
    }
    Ok(source.into())
}

fn validate_probabilities(p: &[f64]) -> Result<()> {
    if p.len() != 9
        || p.iter().any(|x| !x.is_finite() || !(0.0..=1.0).contains(x))
        || (p.iter().sum::<f64>() - 1.).abs() > 1e-5
    {
        return Err("Expected nine finite raw probabilities in 0..1 summing to one".into());
    }
    Ok(())
}

fn load_cases(
    path: &Path,
    manifest_path: Option<&Path>,
    model_sha: &str,
    labels_sha: &str,
) -> Result<(BTreeMap<String, Case>, String)> {
    let bytes = fs::read(path)?;
    let envelope: NativePredictions = serde_json::from_slice(&bytes)?;
    if envelope.model_sha256 != model_sha || envelope.labels_sha256 != labels_sha {
        return Err("Prediction model/labels hashes differ from the pinned bundle member".into());
    }
    let manifest = if let Some(path) = manifest_path {
        let bytes = fs::read(path)?;
        if envelope.manifest_sha256.as_deref() != Some(sha(&bytes).as_str()) {
            return Err("Prediction manifest hash differs from supplied identity manifest".into());
        }
        Some(serde_json::from_slice::<JoinManifest>(&bytes)?)
    } else {
        None
    };
    let mut result = BTreeMap::new();
    let mut seen_indices = BTreeSet::new();
    for row in envelope.predictions {
        validate_probabilities(&row.probabilities)?;
        let (id, original, augmentation, truth) = if let Some(manifest) = &manifest {
            let index = row
                .index
                .ok_or("Manifest-backed prediction lacks its own item index")?;
            if !seen_indices.insert(index) {
                return Err("Repeated prediction index inside one member file".into());
            }
            let item = manifest
                .items
                .get(index)
                .ok_or("Prediction index exceeds its own identity manifest")?;
            if row.source_group != item.source_group || row.truth != item.label_index {
                return Err(
                    "Prediction sourceGroup/label differs from its own manifest item".into(),
                );
            }
            if row
                .original_source_id
                .as_ref()
                .is_some_and(|x| Some(x) != item.original_source_id.as_ref())
                || row
                    .augmentation_id
                    .is_some_and(|x| Some(x) != item.augmentation_id)
                || row.id.as_ref().is_some_and(|x| Some(x) != item.id.as_ref())
            {
                return Err("Prediction identity metadata conflicts with its own manifest".into());
            }
            (
                canonical_id(
                    item.id.as_deref(),
                    item.original_source_id.as_deref(),
                    item.augmentation_id,
                    &item.source_group,
                )?,
                item.original_source_id.clone(),
                item.augmentation_id,
                item.label_index,
            )
        } else {
            (
                canonical_id(
                    row.id.as_deref(),
                    row.original_source_id.as_deref(),
                    row.augmentation_id,
                    &row.source_group,
                )?,
                row.original_source_id.clone(),
                row.augmentation_id,
                row.truth,
            )
        };
        valid_identity(&id, &row.source_group, truth)?;
        let case = Case {
            id: id.clone(),
            source_group: row.source_group,
            original_source_id: original,
            augmentation_id: augmentation,
            truth,
            probabilities: row.probabilities,
        };
        if result.insert(id, case).is_some() {
            return Err(
                "Repeated physical case identity; supply explicit original/augmentation IDs".into(),
            );
        }
    }
    if result.is_empty() {
        return Err("Empty prediction input".into());
    }
    if let Some(manifest) = &manifest {
        let splits: BTreeSet<_> = seen_indices
            .iter()
            .map(|&index| manifest.items[index].split.as_deref())
            .collect();
        if splits.len() != 1 {
            return Err(
                "Identity-manifest predictions must select exactly one complete split".into(),
            );
        }
        let split = *splits.first().unwrap();
        let expected: BTreeSet<_> = manifest
            .items
            .iter()
            .enumerate()
            .filter_map(|(i, item)| (item.split.as_deref() == split).then_some(i))
            .collect();
        if seen_indices != expected {
            return Err(
                "Prediction file omits cases from its selected identity-manifest split".into(),
            );
        }
    }
    Ok((result, sha(&bytes)))
}

fn prediction_json(prediction: &BundlePrediction) -> Value {
    let best = &prediction.alternatives[0];
    json!({"predicted":best.class_index,"label":best.label,"category":best.category,
        "probabilities":prediction.raw_probabilities,"calibratedProbabilities":prediction.calibrated_probabilities,
        "accepted":prediction.accepted_for_review,"recoveredFromOther":prediction.recovered_from_other,
        "alternatives":prediction.alternatives})
}

fn vector_predictions(bundle: &ModelBundle, opts: &BTreeMap<String, String>) -> Result<Value> {
    let primary = Path::new(required(opts, "--primary-predictions")?);
    let refiner = Path::new(required(opts, "--refiner-predictions")?);
    let child = bundle
        .refiner_metadata()
        .ok_or("Paired vector evaluation requires a pinned recovery member")?;
    let (base, base_sha) = load_cases(
        primary,
        opts.get("--primary-manifest").map(Path::new),
        &bundle.config.model_sha256,
        &bundle.config.labels_sha256,
    )?;
    let (refs, ref_sha) = load_cases(
        refiner,
        opts.get("--refiner-manifest").map(Path::new),
        &child.model_sha256,
        &child.labels_sha256,
    )?;
    if base.keys().ne(refs.keys()) {
        return Err(
            "Primary/refiner physical case IDs differ; provide their own identity manifests".into(),
        );
    }
    let mut predictions = Vec::new();
    for (id, b) in base {
        let r = &refs[&id];
        if b.truth != r.truth
            || b.original_source_id != r.original_source_id
            || b.augmentation_id != r.augmentation_id
        {
            return Err(
                "Aligned member cases disagree on ground truth or original/augmentation identity"
                    .into(),
            );
        }
        let began = Instant::now();
        let result = bundle.predict_probabilities(&b.probabilities, Some(&r.probabilities))?;
        let mut row = prediction_json(&result);
        row["id"] = json!(b.id);
        row["sourceGroup"] = json!(b.source_group);
        row["originalSourceId"] = json!(b.original_source_id);
        row["augmentationId"] = json!(b.augmentation_id);
        row["truth"] = json!(b.truth);
        row["primaryProbabilities"] = json!(b.probabilities);
        row["refinerProbabilities"] = json!(r.probabilities);
        row["policyMs"] = json!(began.elapsed().as_secs_f64() * 1000.);
        predictions.push(row);
    }
    Ok(
        json!({"mode":"precomputed-native-vectors","primaryPredictionsSha256":base_sha,"refinerPredictionsSha256":ref_sha,
        "primaryManifestSha256":opts.get("--primary-manifest").map(|p|fs::read(p).map(|b|sha(&b))).transpose()?,
        "refinerManifestSha256":opts.get("--refiner-manifest").map(|p|fs::read(p).map(|b|sha(&b))).transpose()?,
        "timingScope":"policy routing/calibration only; no model inference performed in this mode","predictions":predictions}),
    )
}

fn source_rgb(path: &Path) -> Result<(RgbImage, String)> {
    if path
        .extension()
        .and_then(|x| x.to_str())
        .is_none_or(|s| !s.eq_ignore_ascii_case("png"))
    {
        return Err("Source manifest accepts PNG only".into());
    }
    let bytes = fs::read(path)?;
    let rgba = image::load_from_memory_with_format(&bytes, image::ImageFormat::Png)?.to_rgba8();
    if u64::from(rgba.width()) * u64::from(rgba.height()) > 16_777_216 {
        return Err("Source crop exceeds 16 million pixels".into());
    }
    let mut rgb = RgbImage::new(rgba.width(), rgba.height());
    for (out, pixel) in rgb.pixels_mut().zip(rgba.pixels()) {
        for k in 0..3 {
            out.0[k] = ((u32::from(pixel.0[k]) * u32::from(pixel.0[3])
                + 255 * (255 - u32::from(pixel.0[3]))
                + 127)
                / 255) as u8;
        }
    }
    Ok((rgb, sha(&bytes)))
}

fn source_predictions(bundle: &ModelBundle, path: &Path) -> Result<Value> {
    let bytes = fs::read(path)?;
    let manifest: SourceManifest = serde_json::from_slice(&bytes)?;
    if manifest.schema != "openplantrace.native-object-crops.v1" || manifest.items.is_empty() {
        return Err("Unsupported or empty native source manifest".into());
    }
    let directory = path.parent().unwrap_or(Path::new("."));
    let mut ids = BTreeSet::new();
    // Validate every identity/target before opening any source pixels.
    for item in &manifest.items {
        valid_identity(&item.id, &item.source_group, item.label_index)?;
        if !ids.insert(item.id.clone()) {
            return Err("Repeated source case ID".into());
        }
        if item.target.iter().any(|v| !v.is_finite())
            || item.target[0] < 0.
            || item.target[1] < 0.
            || item.target[2] <= item.target[0]
            || item.target[3] <= item.target[1]
        {
            return Err("Invalid source target extent".into());
        }
    }
    let mut predictions = Vec::new();
    for item in manifest.items {
        let began = Instant::now();
        let source_path = directory.join(&item.image_path);
        let (image, image_sha) = source_rgb(&source_path)?;
        if item
            .image_sha256
            .as_ref()
            .is_some_and(|expected| expected != &image_sha)
        {
            return Err("Source image digest mismatch".into());
        }
        if item.target[2] > f64::from(image.width()) || item.target[3] > f64::from(image.height()) {
            return Err("Target exceeds original source crop bounds".into());
        }
        let decoded_ms = began.elapsed().as_secs_f64() * 1000.;
        let inference = Instant::now();
        let source = bundle.predict_source(&image, item.target)?;
        let inference_ms = inference.elapsed().as_secs_f64() * 1000.;
        let mut row = prediction_json(&source.prediction);
        row["id"] = json!(item.id);
        row["sourceGroup"] = json!(item.source_group);
        row["truth"] = json!(item.label_index);
        row["target"] = json!(item.target);
        row["imagePath"] = json!(source_path);
        row["imageSha256"] = json!(image_sha);
        row["sourceRgbSha256"] = json!(sha(image.as_raw()));
        row["sourceSize"] = json!([image.width(), image.height()]);
        row["primaryPreparedPixelsSha256"] = json!(sha(source.primary_input.as_raw()));
        row["primaryPreparedSize"] =
            json!([source.primary_input.width(), source.primary_input.height()]);
        row["refinerPreparedPixelsSha256"] =
            json!(source.refiner_input.as_ref().map(|x| sha(x.as_raw())));
        row["refinerPreparedSize"] = json!(
            source
                .refiner_input
                .as_ref()
                .map(|x| [x.width(), x.height()])
        );
        row["primaryProbabilities"] = json!(source.primary_raw_probabilities);
        row["refinerProbabilities"] = json!(source.refiner_raw_probabilities);
        row["decodeMs"] = json!(decoded_ms);
        row["inferenceMs"] = json!(inference_ms);
        predictions.push(row);
    }
    Ok(
        json!({"mode":"native-source-crops","sourceManifestSha256":sha(&bytes),"predictions":predictions,
        "timingScope":"inferenceMs includes shared Rust preprocessing and actual conditional primary/refiner ONNX inference; decodeMs is separate"}),
    )
}

pub fn run(opts: &BTreeMap<String, String>) -> Result<()> {
    if opts.keys().any(|key| {
        ![
            "--bundle",
            "--out",
            "--primary-predictions",
            "--refiner-predictions",
            "--primary-manifest",
            "--refiner-manifest",
            "--source-manifest",
        ]
        .contains(&key.as_str())
    }) {
        return Err("Bundle mode takes its model/preprocessing/calibration from pinned metadata; raw model options are not allowed".into());
    }
    let output = PathBuf::from(required(opts, "--out")?);
    if output.exists() {
        return Err("Refusing to overwrite existing prediction evidence".into());
    }
    let has_source = opts.contains_key("--source-manifest");
    let has_vectors =
        opts.contains_key("--primary-predictions") || opts.contains_key("--refiner-predictions");
    if has_source == has_vectors
        || (has_source
            && (opts.contains_key("--primary-manifest") || opts.contains_key("--refiner-manifest")))
    {
        return Err("Bundle mode requires either one --source-manifest or paired prediction files with optional identity manifests".into());
    }
    let began = Instant::now();
    let bundle = ModelBundle::load(Path::new(required(opts, "--bundle")?))?;
    let load_ms = began.elapsed().as_secs_f64() * 1000.;
    let mut result = if has_source {
        source_predictions(&bundle, Path::new(required(opts, "--source-manifest")?))?
    } else {
        vector_predictions(&bundle, opts)?
    };
    result["schema"] = json!("openplantrace.native-bundle-predictions.v1");
    result["bundleSha256"] = json!(bundle.config_sha256);
    result["modelSha256"] = json!(bundle.config.model_sha256);
    result["labelsSha256"] = json!(bundle.config.labels_sha256);
    result["primaryInput"] = json!(bundle.config.input);
    result["refiner"] = json!(bundle.refiner_metadata());
    result["recovery"] = json!(bundle.config.recovery);
    result["calibration"] = json!(bundle.config.calibration);
    result["loadMs"] = json!(load_ms);
    result["engine"] = json!(
        "same native Rust ModelBundle preprocessing/routing/calibration as scanner; tract-onnx CPU"
    );
    result["scope"] = json!(
        "Advisory crop classification only; no scan, object authority or wall placement mutation"
    );
    if let Some(parent) = output.parent() {
        fs::create_dir_all(parent)?;
    }
    // Recheck before writing; an independently created evidence file is preserved.
    if output.exists() {
        return Err("Refusing to overwrite existing prediction evidence".into());
    }
    fs::write(&output, serde_json::to_vec_pretty(&result)?)?;
    println!(
        "Wrote {} native bundle cases to {}",
        result["predictions"].as_array().unwrap().len(),
        output.display()
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture_directory() -> PathBuf {
        let unique = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let path = std::env::temp_dir().join(format!(
            "opt-native-bundle-test-{}-{unique}",
            std::process::id()
        ));
        fs::create_dir(&path).unwrap();
        path
    }

    #[test]
    fn each_members_own_manifest_resolves_different_indices_and_rejects_bad_evidence() {
        let directory = fixture_directory();
        let m = directory.join("manifest.json");
        let p = directory.join("predictions.json");
        let mut manifest = json!({"items":[
            {"sourceGroup":"unselected","labelIndex":0,"split":"train"},
            {"sourceGroup":"display:aug4","originalSourceId":"project/window","augmentationId":4,"labelIndex":2,"split":"validation"}
        ]});
        fs::write(&m, serde_json::to_vec(&manifest).unwrap()).unwrap();
        let mut envelope = json!({"modelSha256":"model","labelsSha256":"labels","manifestSha256":sha(&fs::read(&m).unwrap()),
            "predictions":[{"index":1,"sourceGroup":"display:aug4","truth":2,"probabilities":[0,0,1,0,0,0,0,0,0]}]});
        fs::write(&p, serde_json::to_vec(&envelope).unwrap()).unwrap();
        let (cases, _) = load_cases(&p, Some(&m), "model", "labels").unwrap();
        let id = canonical_id(None, Some("project/window"), Some(4), "another display").unwrap();
        assert_eq!(cases[&id].truth, Some(2));
        assert!(load_cases(&p, Some(&m), "wrong model", "labels").is_err());
        envelope["predictions"][0]["truth"] = json!(1);
        fs::write(&p, serde_json::to_vec(&envelope).unwrap()).unwrap();
        assert!(load_cases(&p, Some(&m), "model", "labels").is_err());
        // Matching truncation in both members must not hide a missing denominator.
        manifest["items"][0]["split"] = json!("validation");
        fs::write(&m, serde_json::to_vec(&manifest).unwrap()).unwrap();
        envelope["predictions"][0]["truth"] = json!(2);
        envelope["manifestSha256"] = json!(sha(&fs::read(&m).unwrap()));
        fs::write(&p, serde_json::to_vec(&envelope).unwrap()).unwrap();
        assert!(load_cases(&p, Some(&m), "model", "labels").is_err());
        envelope["predictions"][0]["truth"] = json!(2);
        envelope["manifestSha256"] = json!("changed metadata");
        fs::write(&p, serde_json::to_vec(&envelope).unwrap()).unwrap();
        assert!(load_cases(&p, Some(&m), "model", "labels").is_err());
        fs::remove_file(p).unwrap();
        fs::remove_file(m).unwrap();
        fs::remove_dir(directory).unwrap();
    }

    #[test]
    fn png_alpha_is_composited_on_white_before_source_preparation() {
        let directory = fixture_directory();
        let path = directory.join("alpha.png");
        let image = image::RgbaImage::from_raw(2, 1, vec![0, 128, 255, 128, 0, 0, 0, 0]).unwrap();
        image.save(&path).unwrap();
        let (rgb, hash) = source_rgb(&path).unwrap();
        assert_eq!(rgb.as_raw(), &[127, 191, 255, 255, 255, 255]);
        assert_eq!(hash, sha(&fs::read(&path).unwrap()));
        fs::remove_file(path).unwrap();
        fs::remove_dir(directory).unwrap();
    }
    #[test]
    fn physical_case_identity_never_uses_cross_member_index() {
        assert_eq!(
            canonical_id(None, Some("project/object"), Some(4), "displayA").unwrap(),
            canonical_id(None, Some("project/object"), Some(4), "displayB").unwrap()
        );
        assert_ne!(
            canonical_id(None, Some("project/object"), Some(0), "display").unwrap(),
            canonical_id(None, Some("project/object"), Some(4), "display").unwrap()
        );
        assert!(canonical_id(None, Some("original"), None, "display").is_err());
    }
    #[test]
    fn source_manifest_rejects_silent_transform_requests() {
        let json = r#"{"schema":"openplantrace.native-object-crops.v1","items":[{"id":"x","sourceGroup":"p","imagePath":"x.png","target":[0,0,10,10],"augmentationId":4}]}"#;
        assert!(serde_json::from_str::<SourceManifest>(json).is_err());
    }
    #[test]
    fn probability_guard_rejects_bad_heads_nan_and_mass() {
        assert!(validate_probabilities(&[0.1; 10]).is_err());
        assert!(validate_probabilities(&[f64::NAN; 9]).is_err());
        assert!(validate_probabilities(&[0.1; 9]).is_err());
        assert!(validate_probabilities(&[1., 0., 0., 0., 0., 0., 0., 0., 0.]).is_ok());
    }
}
