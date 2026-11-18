use openplantrace_rs::{export, input::DrawingFormat, model::Options, pipeline};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    fs,
    io::{BufReader, Write},
    path::{Path, PathBuf},
    sync::atomic::{AtomicUsize, Ordering},
    thread,
    time::Instant,
};

const HELP: &str =
    "batch INPUT... --out-dir NEW_DIRECTORY [--recursive] [--parallel N] [--retries N]
batch --manifest PATH [additional inputs and options]
  --no-svg                 Omit optional SVG overlays
  --units-to-mm N          Known scale for every input in this batch
  --geo-transform A,B,C,D,E,F   Known WGS84 transform for all drawings
  --json PATH              Additional copy of the native batch.json summary
  --markdown PATH          Human-readable per-input results
  --compact-json           Write a compact summary
  --kvemo-model MODEL.onnx  Use one model session across drawings (onnx feature)
  --kvemo-crop-dir DIRECTORY   Write native RGB PNGs and a batch-wide crop manifest
  Batch accepts the same Kvemo labels, tensor layout, normalization and crop options as scan.
  --layer-profile PATH     Load layer category rules (repeatable)
  --object-label-profile PATH   Apply reviewed labels to source object groups (repeatable)
  --layer-category PATTERN=CATEGORY   Inline layer rule (repeatable)
Batch supports PDF and ASCII DXF files. Manifest-relative inputs and outputDirectory
are resolved against the manifest. Explicit CLI options take precedence.";

#[derive(Default, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields, default)]
struct Manifest {
    schema_version: Option<String>,
    name: Option<String>,
    inputs: Vec<PathBuf>,
    output_directory: Option<PathBuf>,
    summary_json_path: Option<PathBuf>,
    recursive: bool,
    write_svg: Option<bool>,
    write_geo_json: Option<bool>,
    max_degree_of_parallelism: Option<usize>,
    retry_count: Option<u32>,
    scanner_options: Option<BTreeMap<String, Value>>,
    layer_profiles: Vec<PathBuf>,
    object_label_profiles: Vec<PathBuf>,
    layer_category_overrides: Vec<Value>,
}
#[derive(Default)]
struct Args {
    visual: super::visual::Arguments,
    object_profiles: Vec<PathBuf>,
    layers: super::profiles::layers::LayerInputs,
    inputs: Vec<PathBuf>,
    out: Option<PathBuf>,
    manifest: Option<PathBuf>,
    json: Option<PathBuf>,
    md: Option<PathBuf>,
    recursive: bool,
    no_svg: bool,
    geojson: bool,
    compact: bool,
    parallel: Option<usize>,
    retries: Option<u32>,
    scale: Option<f64>,
    transform: Option<[f64; 6]>,
    thresholds: BTreeMap<String, Value>,
}

impl Args {
    fn parse(args: Vec<String>) -> Result<Self, String> {
        let mut options = Self::default();
        let mut args = args.into_iter();
        while let Some(flag) = args.next() {
            if options.visual.take(&flag, &mut args)? {
                continue;
            }
            let mut next = || {
                args.next()
                    .ok_or_else(|| format!("missing value for {flag}"))
            };
            match flag.as_str() {
                "--recursive" => options.recursive = true,
                "--no-svg" => options.no_svg = true,
                "--geojson" => options.geojson = true,
                "--compact-json" => options.compact = true,
                "--layer-profile" => options.layers.path(next()?.into()),
                "--object-label-profile" => options.object_profiles.push(next()?.into()),
                "--layer-category" => options.layers.inline(&next()?)?,
                "--out-dir" | "--out" => options.out = Some(next()?.into()),
                "--manifest" => options.manifest = Some(next()?.into()),
                "--json" => options.json = Some(next()?.into()),
                "--markdown" => options.md = Some(next()?.into()),
                "--parallel" => {
                    options.parallel = Some(next()?.parse().map_err(|_| "invalid --parallel")?)
                }
                "--retries" => {
                    options.retries = Some(next()?.parse().map_err(|_| "invalid --retries")?)
                }
                "--units-to-mm" => {
                    options.scale = Some(next()?.parse().map_err(|_| "invalid --units-to-mm")?)
                }
                "--geo-transform" => options.transform = Some(super::transform(&next()?)?),
                key if threshold_name(key).is_some() => {
                    threshold_option(key, &next()?, &mut options.thresholds)?;
                }
                _ if !flag.starts_with('-') => options.inputs.push(flag.into()),
                _ => return Err(format!("unknown batch option: {flag}")),
            }
        }
        Ok(options)
    }
}

fn collect_files(root: &Path, recursive: bool, files: &mut Vec<PathBuf>) -> Result<(), String> {
    if !root.is_dir() {
        files.push(root.to_owned());
        return Ok(());
    }
    let mut dirs = vec![root.to_path_buf()];
    while let Some(dir) = dirs.pop() {
        for entry in fs::read_dir(&dir).map_err(|e| format!("{}: {e}", dir.display()))? {
            let entry = entry.map_err(|e| e.to_string())?;
            let ty = entry.file_type().map_err(|e| e.to_string())?;
            let path = entry.path();
            if recursive && ty.is_dir() && !ty.is_symlink() {
                #[cfg(windows)]
                {
                    use std::os::windows::fs::MetadataExt;
                    if entry
                        .metadata()
                        .map_err(|e| e.to_string())?
                        .file_attributes()
                        & 0x400
                        != 0
                    {
                        continue;
                    }
                }
                dirs.push(path);
            } else if ty.is_file()
                && path
                    .extension()
                    .and_then(|e| e.to_str())
                    .is_some_and(|ext| {
                        super::FILE_FORMATS.iter().any(|(_, extensions, _)| {
                            extensions.iter().any(|s| ext.eq_ignore_ascii_case(s))
                        })
                    })
            {
                files.push(path);
            }
        }
    }
    Ok(())
}

fn threshold_name(flag: &str) -> Option<&'static str> {
    Some(match flag {
        "--min-wall-length" => "minWallLength",
        "--min-wall-fragment" => "minWallFragmentLength",
        "--max-wall-candidates" => "maxWallCandidateSeedsPerPage",
        "--wall-snap" => "wallSnapTolerance",
        "--min-opening-gap" => "minOpeningGap",
        "--max-opening-gap" => "maxOpeningGap",
        _ => return None,
    })
}
pub(crate) fn threshold_option(
    flag: &str,
    value: &str,
    map: &mut BTreeMap<String, Value>,
) -> Result<bool, String> {
    let Some(key) = threshold_name(flag) else {
        return Ok(false);
    };
    map.insert(
        key.to_owned(),
        serde_json::from_str(value).map_err(|_| format!("invalid {flag}"))?,
    );
    Ok(true)
}
pub(crate) fn thresholds(map: BTreeMap<String, Value>) -> Result<Options, String> {
    let mut opts = Options::default();
    for (key, v) in map {
        if v.is_null() {
            continue;
        }
        let value = v
            .as_f64()
            .filter(|n| n.is_finite() && *n > 0.)
            .ok_or_else(|| format!("invalid scanner option {key}"))?;
        match key.as_str() {
            "minWallLength" => opts.min_wall_length = value,
            "minWallFragmentLength" => opts.min_wall_fragment_length = value,
            "wallSnapTolerance" => opts.snap_tolerance = value,
            "minOpeningGap" => opts.min_opening_gap = value,
            "maxOpeningGap" => opts.max_opening_gap = value,
            "maxWallCandidateSeedsPerPage" => {
                opts.max_wall_seeds = v
                    .as_u64()
                    .and_then(|n| usize::try_from(n).ok())
                    .ok_or("invalid wall candidate count")?
            }
            _ => return Err(format!("unsupported batch scanner option: {key}")),
        }
    }
    if opts.min_opening_gap > opts.max_opening_gap {
        return Err("minOpeningGap exceeds maxOpeningGap".into());
    }
    Ok(opts)
}

struct Settings {
    visual: super::visual::Runner,
    object_profiles: Vec<openplantrace_rs::object_labels::Profile>,
    layers: Vec<openplantrace_rs::layers::LayerCategoryOverride>,
    options: Options,
    scale: Option<f64>,
    transform: Option<[f64; 6]>,
    overlays: bool,
    retries: u32,
}
fn item(
    index: usize,
    input: &Path,
    stage: &Path,
    settings: &Settings,
    crop_root: Option<(&Path, &Path)>,
) -> Value {
    let now = Instant::now();
    let stem = input
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("drawing");
    let safe: String = stem
        .chars()
        .map(|c| if c.is_whitespace() { '-' } else { c })
        .collect();
    let folder = format!("{:04}-{safe}", index + 1);
    let mut attempts = 0;
    let mut counts = Value::Null;
    let (status, message) = if !input.is_file() {
        ("Missing", Some("input file missing".to_owned()))
    } else if DrawingFormat::of(input).is_none() {
        ("Unsupported", Some("unsupported input format".to_owned()))
    } else {
        loop {
            attempts += 1;
            let result=pipeline::scan_file_with_profiles(input,&settings.options,settings.scale,&settings.layers,&settings.object_profiles).and_then(|mut scan|{
                settings.visual.apply(&mut scan)?;
                export::write_bundle_with_extra(&scan,&stage.join(&folder),settings.transform,settings.overlays,|_|{
                    if let Some((draft,root))=crop_root {
                        export::publish_directory(&draft.join(&folder),|temp|
                            openplantrace_rs::visual::dataset::write(&scan,input,temp,&root.join(&folder),&settings.visual.crop))?;
                    }Ok(())
                })?;
                Ok(json!({"pages":scan.pages.len(),"walls":scan.pages.iter().map(|p|p.walls.len()).sum::<usize>(),
                    "rooms":scan.pages.iter().map(|p|p.rooms.len()).sum::<usize>(),"openings":scan.pages.iter().map(|p|p.openings.len()).sum::<usize>()}))
            });
            match result {
                Ok(c) => {
                    counts = c;
                    break ("Scanned", None);
                }
                Err(e) if attempts > u64::from(settings.retries) => break ("Failed", Some(e)),
                Err(_) => {}
            }
        }
    };
    json!({"inputPath":input,"status":status,"attempts":attempts,"bundle":(status=="Scanned").then_some(folder),
        "counts":counts,"error":message,"durationMilliseconds":now.elapsed().as_secs_f64()*1000.})
}

pub(super) fn write_report(path: &Path, bytes: &[u8]) -> Result<(), String> {
    if let Some(dir) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
        fs::create_dir_all(dir).map_err(|e| e.to_string())?;
    }
    let mut file = fs::File::create_new(path).map_err(|e| format!("{}: {e}", path.display()))?;
    if let Err(e) = file.write_all(bytes).and_then(|_| file.flush()) {
        drop(file);
        let _ = fs::remove_file(path);
        return Err(format!("{}: {e}", path.display()));
    }
    Ok(())
}

pub fn run(args: Vec<String>) -> Result<(), String> {
    if args.iter().any(|x| x == "--help" || x == "-h") {
        println!("{HELP}");
        return Ok(());
    }
    let mut args = Args::parse(args)?;
    let mut manifest = Manifest::default();
    if let Some(path) = &args.manifest {
        let file = fs::File::open(path).map_err(|e| e.to_string())?;
        manifest = serde_json::from_reader(BufReader::new(file))
            .map_err(|e| format!("invalid batch manifest: {e}"))?;
        if manifest
            .schema_version
            .as_deref()
            .is_some_and(|v| v != "openplantrace.batch-manifest.v1")
        {
            return Err("unsupported batch manifest schemaVersion".into());
        }
        let base = path.parent().unwrap_or(Path::new("."));
        args.object_profiles
            .extend(manifest.object_label_profiles.iter().map(|p| base.join(p)));
        args.layers.manifest(
            base,
            &manifest.layer_profiles,
            &manifest.layer_category_overrides,
        )?;
        args.inputs
            .extend(manifest.inputs.iter().map(|p| base.join(p)));
        args.out = args
            .out
            .or_else(|| manifest.output_directory.map(|p| base.join(p)));
        args.json = args
            .json
            .or_else(|| manifest.summary_json_path.map(|p| base.join(p)));
    }
    let mut limits = manifest.scanner_options.unwrap_or_default();
    limits.extend(args.thresholds);
    let parallel = args
        .parallel
        .or(manifest.max_degree_of_parallelism)
        .unwrap_or(1);
    if parallel == 0 {
        return Err("--parallel must be positive".into());
    }
    if args.scale.is_some_and(|s| !s.is_finite() || s <= 0.) {
        return Err("invalid --units-to-mm".into());
    }
    if (args.geojson || manifest.write_geo_json == Some(true)) && args.transform.is_none() {
        return Err("geographic export requires a known --geo-transform".into());
    }
    let settings = Settings {
        visual: args.visual.prepare()?,
        object_profiles: super::profiles::load_objects(&args.object_profiles)?,
        layers: args.layers.resolve()?,
        options: thresholds(limits)?,
        scale: args.scale,
        transform: args.transform,
        overlays: !args.no_svg && manifest.write_svg != Some(false),
        retries: args.retries.or(manifest.retry_count).unwrap_or(0),
    };
    let out = args
        .out
        .ok_or("batch requires --out-dir or manifest outputDirectory")?;
    let mut unique = BTreeMap::new();
    let mut found = Vec::new();
    for path in args.inputs {
        collect_files(&path, args.recursive || manifest.recursive, &mut found)?;
    }
    for path in found {
        let absolute = std::path::absolute(&path).map_err(|e| e.to_string())?;
        let key = fs::canonicalize(&absolute).unwrap_or_else(|_| absolute.clone());
        #[cfg(windows)]
        let key = key.as_os_str().to_ascii_lowercase();
        unique.entry(key).or_insert(absolute);
    }
    let files: Vec<_> = unique.into_values().collect();
    if files.is_empty() {
        return Err("no batch inputs found".into());
    }
    let output = std::path::absolute(&out).map_err(|e| e.to_string())?;
    let default_report = output.join("batch.json");
    let json_path = args
        .json
        .map(std::path::absolute)
        .transpose()
        .map_err(|e| e.to_string())?
        .filter(|p| *p != default_report);
    let md = args
        .md
        .map(std::path::absolute)
        .transpose()
        .map_err(|e| e.to_string())?;
    if json_path.is_some() && json_path == md {
        return Err("JSON and Markdown report paths coincide".into());
    }
    if md.as_ref() == Some(&default_report) {
        return Err("Markdown would overwrite batch.json".into());
    }
    for path in json_path.iter().chain(md.iter()) {
        if path.exists() {
            return Err(format!("report already exists: {}", path.display()));
        }
    }
    let results = export::publish_directory(&out, |stage| {
        settings.visual.staged(Some(&out),Some(stage),|crop_root| {
        let next = AtomicUsize::new(0);
        let mut rows = thread::scope(|scope| {
            let mut workers = Vec::new();
            for _ in 0..parallel
                .min(files.len())
                .min(thread::available_parallelism().map_or(1, usize::from))
            {
                workers.push(
                    thread::Builder::new()
                        .spawn_scoped(scope, || {
                            let mut rows = Vec::new();
                            loop {
                                let i = next.fetch_add(1, Ordering::Relaxed);
                                let Some(path) = files.get(i) else { break };
                                rows.push((i, item(i, path, stage, &settings,crop_root)));
                            }
                            rows
                        })
                        .map_err(|e| e.to_string())?,
                );
            }
            let mut rows = Vec::with_capacity(files.len());
            for worker in workers {
                rows.extend(worker.join().map_err(|_| "batch worker failed")?);
            }
            Ok::<_, String>(rows)
        })?;
        rows.sort_unstable_by_key(|r| r.0);
        let items: Vec<_> = rows.into_iter().map(|r| r.1).collect();
        if let Some((stage,_))=crop_root {
            let mut manifest=std::io::BufWriter::new(fs::File::create_new(stage.join("kvemo-crops.jsonl")).map_err(|e|e.to_string())?);
            for folder in items.iter().filter_map(|row|row["bundle"].as_str()){
                let mut file=fs::File::open(stage.join(folder).join("kvemo-crops.jsonl")).map_err(|e|e.to_string())?;
                std::io::copy(&mut file,&mut manifest).map_err(|e|e.to_string())?;
            }
            manifest.flush().map_err(|e|e.to_string())?;
        }
        let passed = items.iter().filter(|v| v["status"] == "Scanned").count();
        let report = json!({"schemaVersion":"openplantrace.rust.batch.v1","name":manifest.name,"outputDirectory":output,"total":items.len(),
            "scanned":passed,"passed":passed==items.len(),"items":items});
        let bytes = if args.compact {
            serde_json::to_vec(&report)
        } else {
            serde_json::to_vec_pretty(&report)
        }
        .map_err(|e| e.to_string())?;
        write_report(&stage.join("batch.json"), &bytes)?;
        let mut external = Vec::new();
        let mut reports = Vec::new();
        if let Some(path) = json_path {
            reports.push((path, bytes));
        }
        if let Some(path) = md {
            let mut text = format!(
                "# Batch results\n\n{passed}/{} drawings scanned.\n\n",
                files.len()
            );
            for v in report["items"].as_array().unwrap() {
                text.push_str(&format!(
                    "- {}  {}\n",
                    v["status"].as_str().unwrap(),
                    v["inputPath"]
                        .as_str()
                        .unwrap_or("?")
                        .replace(['\r', '\n'], " ")
                ));
            }
            reports.push((path, text.into_bytes()));
        }
        for (path, data) in reports {
            if let Ok(relative) = path.strip_prefix(&output)
                && relative
                    .components()
                    .all(|c| matches!(c, std::path::Component::Normal(_)))
            {
                write_report(&stage.join(relative), &data)?;
            } else {
                external.push((path, data));
            }
        }
        Ok((passed, external))
    })
    })?;
    let (passed, external) = results;
    for (path, data) in external {
        write_report(&path, &data)?;
    }
    println!(
        "{passed}/{} drawings scanned. {}",
        files.len(),
        default_report.display()
    );
    if passed < files.len() {
        return Err("batch contains unsuccessful inputs; see batch.json".into());
    }
    Ok(())
}
