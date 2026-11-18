//! Run benchmark expectations against the same typed scan used by the native CLI.
mod inventory;
mod reports;
mod targets;
#[cfg(test)]
mod tests;
use inventory::{GROUPS, Inventory};
use openplantrace_rs::{
    layers::LayerCategoryOverride, model::Options, object_labels::Profile, pipeline,
};
use serde_json::{Map, Value, json};
use std::{
    collections::{BTreeMap, HashSet},
    fs,
    path::{Path, PathBuf},
    process::ExitCode,
    sync::OnceLock,
    time::Instant,
};

const MANIFEST: &str = "openplantrace.benchmark-manifest.v1";
const RESULT: &str = "openplantrace.rust.benchmark-result.v1";
const COMPARE: &str = "openplantrace.rust.benchmark-comparison.v1";
fn declared(family: &str, key: &str) -> bool {
    static MANIFEST_SCHEMA: OnceLock<Value> = OnceLock::new();
    let schema = MANIFEST_SCHEMA.get_or_init(|| {
        let bytes = super::schema::published("benchmark-manifest")
            .expect("built-in benchmark manifest schema");
        serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes))
            .expect("valid embedded benchmark schema")
    });
    schema["$defs"][family]["properties"].get(key).is_some()
}
const HELP: &str = "benchmark MANIFEST.json|DIRECTORY [--json REPORT.json] [--markdown REPORT.md] [--compact-json]
  [--min-wall-length N] [--min-wall-fragment N] [--wall-snap N] [--max-wall-candidates N] [--min-opening-gap N] [--max-opening-gap N]
  [--layer-profile FILE] [--layer-category PATTERN=CATEGORY] [--object-label-profile FILE]
benchmark-draft NATIVE_SCAN.json --source-path DRAWING [--json MANIFEST.json] [--review-markdown REVIEW.md]
benchmark-compare BASELINE.json CANDIDATE.json [--json RESULT.json] [--markdown REPORT.md] [--no-fail-on-regression]";
fn read(path: &Path) -> Result<Value, String> {
    super::validate::read_json(path).map_err(|e| format!("{}: {e}", path.display()))
}
fn write(path: &Path, data: &[u8]) -> Result<(), String> {
    if let Some(p) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        fs::create_dir_all(p).map_err(|e| e.to_string())?;
    }
    fs::write(path, data).map_err(|e| format!("{}: {e}", path.display()))
}
fn check(name: &str, expected: Value, actual: Value, pass: bool) -> Value {
    json!({"name":name,"passed":pass,"expected":expected,"actual":actual})
}
fn missing(name: &str, expected: &Value) -> Value {
    json!({"name":name,"expected":expected,"actual":null,"passed":false,"status":"Unsupported","message":"The native scanner does not expose this expectation."})
}
fn required(
    list: &Value,
    seen: &[String],
    key: &str,
    checks: &mut Vec<Value>,
) -> Result<(), String> {
    for wanted in list
        .as_array()
        .ok_or_else(|| format!("{key} requires an array"))?
    {
        let w = wanted.as_str().ok_or("expected string in required list")?;
        checks.push(check(
            key,
            wanted.clone(),
            json!(seen),
            seen.iter().any(|s| s.eq_ignore_ascii_case(w)),
        ));
    }
    Ok(())
}

fn evaluate(
    expect: &Value,
    inventory: &Inventory,
    path: &Path,
    elapsed: f64,
) -> Result<(Vec<Value>, Vec<Value>), String> {
    let mut checks = vec![check("scan.completed", true.into(), true.into(), true)];
    let mut metrics = Vec::new();
    let format = path
        .extension()
        .and_then(|x| x.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let pdf = format == "pdf";
    let source_kind = if pdf { "Pdf" } else { "Dxf" };
    let loader = if pdf {
        "lopdf"
    } else {
        "OpenPlanTrace Rust ASCII DXF"
    };
    let needs_ocr = pdf && inventory.image_only_pages > 0;
    let geometry = if needs_ocr {
        "mixed-pdf-vector-and-raster"
    } else if pdf {
        "pdf-vector-geometry"
    } else {
        "dxf-vector-geometry"
    };
    let status = if needs_ocr {
        "PdfRasterOcrRecommended"
    } else if inventory.has_vector_geometry {
        "VectorGeometryReady"
    } else {
        "NoObservedVectorGeometry"
    };
    let ingestion = if pdf { "pdf-vector" } else { "dxf-vector" };
    let source = json!({
        "requiredSourceFormat":format,"requiredSourceKind":source_kind,"requiredEffectiveSourceKind":source_kind,"requiredSourceLoader":loader,
        "requiredSourceIngestionPath":ingestion,"requiredSourceReadinessStatus":status,"requiredSourceGeometryBasis":geometry,
        "requireSourceVectorGeometryReady":inventory.has_vector_geometry,"requireSourceOcr":needs_ocr,
        "requireSourceExternalAdapter":false,"requireSourceLegalAdapterBacked":true,
        "requireDwgDerivedSource":false,"requireRasterDerivedSource":false
    });
    let evidence = [
        format!("format={format}"),
        format!("loader={loader}"),
        format!("sourceKind={source_kind}"),
        format!("ingestionPath={ingestion}"),
    ];
    for (key, value) in expect
        .as_object()
        .ok_or("benchmark expectations must be an object")?
    {
        // Reference drafts serialize unused optional properties as null or empty
        // collections. Only a declared, inactive expectation can be skipped.
        if declared("expectations", key)
            && (value.is_null()
                || value.as_array().is_some_and(Vec::is_empty)
                || value.as_object().is_some_and(Map::is_empty))
        {
            continue;
        }
        if key.ends_with("Metrics") && declared("expectations", key) && targets::inactive(value) {
            continue;
        }
        if let Some((_, kind)) = GROUPS.iter().find(|(k, _)| *k == key) {
            let detector = match *kind {
                "objectGroups" => "object_groups",
                "gridAxes" => "grid_axes",
                other => other,
            };
            match targets::evaluate(detector, value, &inventory.groups[kind], &mut checks) {
                Ok(metric) => metrics.push(metric),
                Err(message) => checks.push(json!({"name":key,"expected":value,"passed":false,
                    "actual":null,"status":"UnsupportedOrInvalid","message":message})),
            }
            continue;
        }
        if let Some((count, num)) = inventory.counts.iter().find(|(k, _)| {
            declared("expectations", key)
                && key.get(3..).is_some_and(|s| s.eq_ignore_ascii_case(k))
                && (key.starts_with("min") || key.starts_with("max"))
        }) {
            let n = value
                .as_u64()
                .ok_or_else(|| format!("{key} must be a nonnegative count"))?;
            let pass = if key.starts_with("min") {
                *num as u64 >= n
            } else {
                *num as u64 <= n
            };
            checks.push(check(
                &format!("{count}.{}", &key[..3]),
                value.clone(),
                json!(num),
                pass,
            ));
            continue;
        }
        let attrs = match key.as_str() {
            "requiredRegionKinds" => Some(("regions", "regionKind")),
            "requiredLayerCategories" => Some(("layers", "layerCategory")),
            "requiredAnnotationKinds" => Some(("annotations", "annotationKind")),
            "requiredGridLabels" => Some(("gridAxes", "label")),
            "requiredRoomLabels" => Some(("rooms", "label")),
            "requiredOpeningTypes" => Some(("openings", "openingType")),
            "requiredOpeningOperations" => Some(("openings", "openingOperation")),
            "requiredObjectCategories" => Some(("objects", "objectCategory")),
            _ => None,
        };
        if let Some((group, field)) = attrs {
            let mut seen: Vec<_> = inventory.groups[group]
                .iter()
                .filter_map(|r| r[field].as_str().map(str::to_owned))
                .collect();
            seen.sort();
            seen.dedup();
            required(value, &seen, key, &mut checks)?;
            continue;
        }
        match key.as_str() {
            "requiresReliableCalibration" => {
                let wanted = value.as_bool().ok_or("invalid calibration expectation")?;
                checks.push(check(
                    key,
                    value.clone(),
                    json!(inventory.calibrated),
                    !wanted || inventory.calibrated,
                ));
            }
            "maxDurationMilliseconds" => {
                let max = value
                    .as_f64()
                    .filter(|v| *v >= 0.)
                    .ok_or("invalid duration threshold")?;
                checks.push(check(key, value.clone(), json!(elapsed), elapsed <= max));
            }
            "requiredSourceEvidenceContains" | "forbiddenSourceEvidenceContains" => {
                for term in value
                    .as_array()
                    .ok_or("source evidence must be a list of strings")?
                {
                    let text = term
                        .as_str()
                        .ok_or("source evidence must be text")?
                        .to_lowercase();
                    let found = evidence.iter().any(|e| e.to_lowercase().contains(&text));
                    checks.push(check(
                        key,
                        term.clone(),
                        json!(evidence),
                        found == key.starts_with("required"),
                    ));
                }
            }
            _ if source.get(key).is_some() => {
                let actual = &source[key];
                let equal = match (value.as_str(), actual.as_str()) {
                    (Some(a), Some(b)) => a.trim().eq_ignore_ascii_case(b),
                    _ => value == actual,
                };
                checks.push(check(key, value.clone(), actual.clone(), equal));
            }
            _ => checks.push(missing(key, value)),
        }
    }
    Ok((checks, metrics))
}

fn fixture_id(f: &Value) -> Result<String, String> {
    Ok(f["id"]
        .as_str()
        .filter(|s| !s.trim().is_empty())
        .unwrap_or(
            f["sourcePath"]
                .as_str()
                .ok_or("fixture requires sourcePath")?,
        )
        .to_owned())
}
#[derive(Default)]
struct ScanSettings {
    thresholds: Options,
    layer_rules: Vec<LayerCategoryOverride>,
    object_profiles: Vec<Profile>,
}
fn scan_case(f: &Value, dir: &Path, settings: &ScanSettings) -> Result<Value, String> {
    let id = fixture_id(f)?;
    let s = f["sourcePath"]
        .as_str()
        .filter(|s| !s.trim().is_empty())
        .ok_or("empty fixture path")?;
    let path = dir.join(s);
    let optional = f.get("optional").map_or(Ok(false), |v| {
        v.as_bool().ok_or("expected boolean optional")
    })?;
    let start = Instant::now();
    if !path.is_file() && optional {
        return Ok(
            json!({"fixtureId":id,"fixtureName":f["name"],"sourcePath":s,"passed":true,"skipped":true,"scanSucceeded":false,"skipReason":f["skipReason"],"assertions":[],"metrics":[],"durationMilliseconds":0.}),
        );
    }
    let scan = match pipeline::scan_file_with_profiles(
        &path,
        &settings.thresholds,
        None,
        &settings.layer_rules,
        &settings.object_profiles,
    ) {
        Ok(scan) => scan,
        Err(e) => {
            return Ok(
                json!({"fixtureId":id,"fixtureName":f["name"],"sourcePath":s,"passed":false,
       "skipped":false,"scanSucceeded":false,"errorMessage":e,"assertions":[check("scan.completed",true.into(),false.into(),false)],"metrics":[],"durationMilliseconds":start.elapsed().as_secs_f64()*1000.}),
            );
        }
    };
    let elapsed = start.elapsed().as_secs_f64() * 1000.;
    let inventory = Inventory::new(&scan);
    let empty = json!({});
    let expect = f.get("expectations").unwrap_or(&empty);
    let (checks, metrics) = evaluate(expect, &inventory, &path, elapsed)?;
    let passed = checks.iter().all(|a| a["passed"] == true);
    Ok(
        json!({"fixtureId":id,"fixtureName":f["name"],"properties":f["properties"],"sourcePath":s,"passed":passed,"skipped":false,"scanSucceeded":true,
        "durationMilliseconds":elapsed,"counts":inventory.counts,"assertions":checks,"metrics":metrics}),
    )
}
fn benchmark(path: &Path, settings: &ScanSettings) -> Result<Value, String> {
    let path = if path.is_dir() {
        path.join("benchmark.json")
    } else {
        path.to_owned()
    };
    let m = read(&path)?;
    if m.get("schemaVersion").is_some_and(|v| v != MANIFEST) {
        return Err("unsupported benchmark manifest schemaVersion".into());
    }
    let fixtures = m["fixtures"]
        .as_array()
        .filter(|v| !v.is_empty())
        .ok_or("expected at least one fixture")?;
    let dir = path.parent().unwrap_or(Path::new("."));
    let mut ids = HashSet::new();
    let mut cases = Vec::new();
    for f in fixtures {
        if !ids.insert(fixture_id(f)?) {
            return Err("duplicate benchmark fixture id".into());
        }
        for k in f.as_object().ok_or("invalid fixture")?.keys() {
            if ![
                "id",
                "name",
                "sourcePath",
                "optional",
                "skipReason",
                "properties",
                "expectations",
            ]
            .contains(&k.as_str())
            {
                return Err(format!("unsupported fixture key {k}"));
            }
        }
        cases.push(scan_case(f, dir, settings)?);
    }
    let failed = cases.iter().filter(|c| c["passed"] != true).count();
    let skipped = cases.iter().filter(|c| c["skipped"] == true).count();
    let checks: Vec<_> = cases
        .iter()
        .flat_map(|c| c["assertions"].as_array().unwrap())
        .collect();
    let num_pass = checks.iter().filter(|c| c["passed"] == true).count();
    Ok(
        json!({"schemaVersion":RESULT,"name":m["name"],"passed":failed==0,"caseCount":cases.len(),"passedCaseCount":cases.len()-failed-skipped,
        "failedCaseCount":failed,"skippedCaseCount":skipped,"passedAssertionCount":num_pass,"failedAssertionCount":checks.len()-num_pass,"cases":cases}),
    )
}

struct Args {
    scan: ScanSettings,
    files: Vec<PathBuf>,
    values: BTreeMap<String, String>,
    flags: HashSet<String>,
}
impl Args {
    fn parse(command: &str, args: Vec<String>) -> Result<Self, String> {
        let mut result = Self {
            scan: ScanSettings::default(),
            files: Vec::new(),
            values: BTreeMap::new(),
            flags: HashSet::new(),
        };
        let mut args = args.into_iter().peekable();
        let mut thresholds = BTreeMap::new();
        let mut layers = super::profiles::layers::LayerInputs::default();
        let mut object_paths = Vec::new();
        let mut flags = vec!["--compact-json"];
        let mut keys = vec!["--json"];
        match command {
            "benchmark" => keys.extend([
                "--markdown",
                "--layer-profile",
                "--layer-category",
                "--object-label-profile",
            ]),
            "benchmark-draft" => {
                keys.extend([
                    "--source-path",
                    "--fixture-id",
                    "--fixture-name",
                    "--name",
                    "--review-markdown",
                    "--skip-reason",
                    "--max-targets-per-detector",
                    "--target-recall",
                    "--target-precision",
                ]);
                flags.extend(["--no-bounds", "--optional"]);
            }
            "benchmark-compare" => {
                keys.extend([
                    "--markdown",
                    "--duration-ratio",
                    "--duration-min-ms",
                    "--detector-recall-drop",
                    "--detector-precision-drop",
                    "--detector-f1-drop",
                ]);
                flags.push("--no-fail-on-regression");
            }
            _ => return Err("unknown benchmark operation".into()),
        }
        while let Some(arg) = args.next() {
            if flags.contains(&arg.as_str()) {
                result.flags.insert(arg);
            } else if keys.contains(&arg.as_str()) {
                let v = args
                    .next()
                    .ok_or_else(|| format!("missing value for {arg}"))?;
                match arg.as_str() {
                    "--layer-profile" => layers.path(v.into()),
                    "--layer-category" => layers.inline(&v)?,
                    "--object-label-profile" => object_paths.push(v.into()),
                    _ => {
                        result.values.insert(arg, v);
                    }
                }
            } else if command == "benchmark"
                && arg.starts_with('-')
                && super::batch::threshold_option(
                    &arg,
                    args.peek().map(String::as_str).unwrap_or(""),
                    &mut thresholds,
                )?
            {
                args.next();
            } else if arg.starts_with('-') {
                return Err(format!("unsupported {command} option {arg}"));
            } else {
                result.files.push(arg.into());
            }
        }
        if command == "benchmark" {
            result.scan = ScanSettings {
                thresholds: super::batch::thresholds(thresholds)?,
                layer_rules: layers.resolve()?,
                object_profiles: super::profiles::load_objects(&object_paths)?,
            };
        }
        Ok(result)
    }
    fn value(&self, key: &str) -> Option<&str> {
        self.values.get(key).map(String::as_str)
    }
    fn num(&self, key: &str, default: f64) -> Result<f64, String> {
        if let Some(s) = self.value(key) {
            return s
                .parse::<f64>()
                .ok()
                .filter(|x| x.is_finite() && *x >= 0.)
                .ok_or_else(|| format!("invalid {key}"));
        }
        Ok(default)
    }
}
pub fn run(command: &str, args: Vec<String>) -> Result<ExitCode, String> {
    if args.iter().any(|s| s == "--help" || s == "-h") {
        println!("{HELP}");
        return Ok(ExitCode::SUCCESS);
    }
    let args = Args::parse(command, args)?;
    let data = match (command, args.files.as_slice()) {
        ("benchmark", [p]) => benchmark(p, &args.scan)?,
        ("benchmark-draft", [p]) => reports::draft(p, &args)?,
        ("benchmark-compare", [a, b]) => reports::compare(&read(a)?, &read(b)?, &args)?,
        _ => return Err(HELP.into()),
    };
    if let Some(path) = args.value("--markdown").or(args.value("--review-markdown")) {
        write(
            Path::new(path),
            reports::markdown(command, &data).as_bytes(),
        )?;
    }
    let compact = args.flags.contains("--compact-json");
    if let Some(path) = args.value("--json") {
        let bytes = if compact {
            serde_json::to_vec(&data)
        } else {
            serde_json::to_vec_pretty(&data)
        }
        .map_err(|e| e.to_string())?;
        write(Path::new(path), &bytes)?;
        println!("Wrote {command} output to {path}");
    } else {
        super::print_json(&data, compact)?;
    }
    let failed = (command == "benchmark" && data["passed"] != true)
        || (command == "benchmark-compare"
            && !args.flags.contains("--no-fail-on-regression")
            && data["regressionCount"].as_u64().is_some_and(|n| n > 0));
    Ok(if failed {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    })
}
