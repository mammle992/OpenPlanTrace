mod cli;
use openplantrace_rs::pipeline;
use std::{env, path::PathBuf, process::ExitCode};
const HELP: &str = "OpenPlanTrace Rust
  scan INPUT [--out NEW_DIRECTORY] [--json FILE] [--structure FILE] [--placement FILE]
       [--connections FILE] [--connections-svg FILE]
       [--geojson FILE --geo-transform a,b,c,d,e,f] [--local-features FILE] [--svg FILE] [--svg-dir DIRECTORY] [--page N]
       [--units-to-mm N] [--layer-profile FILE] [--layer-category PATTERN=CATEGORY] [--object-label-profile FILE] [--compact-json] [--no-svg]
       [--compact-scan FILE] [--compact-scan-gzip FILE.gz]
  batch FILE_OR_DIRECTORY... --out-dir NEW_DIRECTORY [--recursive] [--parallel N] [--retries N]
  batch --manifest MANIFEST.json [--out-dir NEW_DIRECTORY]
  batch-compare BASELINE.json CANDIDATE.json [--json COMPARISON.json]
  inspect INPUT [--json] [--compact-json] [--text-samples [N]]
  object-review INPUT.pdf --bundle MODEL_DIRECTORY --regions REGIONS.json --out NEW_DIRECTORY
  scan INPUT.pdf --out NEW_DIRECTORY --object-model-bundle MODEL_DIRECTORY (advisory source-pixel review)
  formats [--json] [--compact-json]
  schema NAME [--json PATH]
  wall-truth-evaluate TRUTH.json STRUCTURE.json [--json RESULT.json] [--no-fail-on-gate]
  benchmark MANIFEST.json [--json RESULT.json]
  benchmark-draft SCAN.json --source-path INPUT [--json MANIFEST.json]
  benchmark-compare BASELINE.json CANDIDATE.json [--json COMPARISON.json]
  scan INPUT --kvemo-crop-dir DIRECTORY [--kvemo-input-width N --kvemo-input-height N] [--kvemo-max-crops N --kvemo-crop-padding N]
       [--kvemo-model MODEL.onnx --kvemo-labels LABELS.txt] [--kvemo-top-k N --kvemo-min-confidence N]
       [--kvemo-input-name NAME --kvemo-output-name NAME] [--kvemo-channels-last] [--kvemo-mean R,G,B --kvemo-std R,G,B]
       [--kvemo-model-name NAME --kvemo-model-version VERSION] (model requires the onnx feature)
  kvemo-report CROPS.jsonl [--json REPORT.json]
  kvemo-profile-template CROPS.jsonl [--json PROFILE.json]
  corrections-to-profile CORRECTIONS.json [--json PROFILE.json]
  validate BUNDLE/scan.json[.gz]
  expand-compact SCAN.json[.gz] [--json NEW_FILE]
  --version

PDF pages automatically select vector, raster or mixed scanning. ASCII DXF is also supported.\nA geo transform maps drawing coordinates to WGS84.
Use 'batch --help' for manifest and output options. 'schema' retrieves published reference contracts.";
fn run(cmd: String, mut args: impl Iterator<Item = String>) -> Result<(), String> {
    match cmd.as_str() {
        "--help" | "-h" | "help" => {
            println!("{HELP}");
            Ok(())
        }
        "--version" | "-V" => {
            println!("openplantrace-rs {}", env!("CARGO_PKG_VERSION"));
            Ok(())
        }
        "batch" => cli::batch::run(args.collect()),
        "batch-compare" => cli::batch_compare::run(args.collect()),
        "kvemo-report" => cli::kvemo::run(args.collect()),
        "object-review" => cli::object_review::run(args.collect()),
        "inspect" => cli::inspect(args.collect()),
        "formats" => cli::formats(args.collect()),
        "schema" => cli::schema::run(args.collect()),
        "validate" => cli::validate::run(args.collect()),
        "expand-compact" => cli::validate::expand(args.collect()),
        "scan" => {
            let input = PathBuf::from(args.next().ok_or("supply a PDF or DXF path")?);
            if input == std::path::Path::new("--help") || input == std::path::Path::new("-h") {
                println!("{HELP}");
                return Ok(());
            }
            let mut out = None;
            let mut scale = None;
            let mut geo = None;
            let mut outputs = cli::outputs::Outputs::default();
            let mut thresholds = std::collections::BTreeMap::new();
            let mut overlays = true;
            let mut layers = cli::profiles::layers::LayerInputs::default();
            let mut visual = cli::visual::Arguments::default();
            let mut object_profiles = Vec::new();
            let mut object_model = None;
            while let Some(opt) = args.next() {
                if visual.take(&opt, &mut args)? {
                    continue;
                }
                if opt == "--compact-json" {
                    outputs.compact = true;
                    continue;
                }
                if opt == "--no-svg" {
                    overlays = false;
                    continue;
                }
                let val = args
                    .next()
                    .ok_or_else(|| format!("missing value for {opt}"))?;
                match opt.as_str() {
                    "--out" | "--out-dir" if out.is_none() => out = Some(PathBuf::from(val)),
                    "--units-to-mm" if scale.is_none() => {
                        scale = Some(val.parse::<f64>().map_err(|_| "invalid --units-to-mm")?)
                    }
                    "--geo-transform" if geo.is_none() => geo = Some(cli::transform(&val)?),
                    "--layer-profile" => layers.path(val.into()),
                    "--object-label-profile" => object_profiles.push(val.into()),
                    "--object-model-bundle" if object_model.is_none() => {
                        object_model = Some(PathBuf::from(val))
                    }
                    "--layer-category" => layers.inline(&val)?,
                    flag if outputs.option(flag, &val)? => {}
                    flag if cli::batch::threshold_option(flag, &val, &mut thresholds)? => {}
                    _ => return Err(format!("unknown or repeated option: {opt}")),
                }
            }
            if out.as_deref().is_some_and(std::path::Path::exists) {
                return Err("output already exists; use a fresh directory".into());
            }
            let visual = visual.prepare()?;
            if object_model.is_some() && out.is_none() {
                return Err("--object-model-bundle requires --out for its review sidecar".into());
            }
            let object_review = object_model
                .as_deref()
                .map(|p| cli::object_review::ScannerReview::load(p, &input))
                .transpose()?;
            let mut scan = pipeline::scan_file_with_profiles(
                &input,
                &cli::batch::thresholds(thresholds)?,
                scale,
                &layers.resolve()?,
                &cli::profiles::load_objects(&object_profiles)?,
            )?;
            visual.apply(&mut scan)?;
            outputs.write_with_extra(&scan, out.as_deref(), geo, overlays, |stage| {
                if let Some(review) = &object_review {
                    review.write(
                        &scan,
                        stage.ok_or("object review requires an output directory")?,
                    )?;
                }
                visual.staged(out.as_deref(), stage, |where_to| {
                    if let Some((stage, dest)) = where_to {
                        openplantrace_rs::visual::dataset::write(
                            &scan,
                            &input,
                            stage,
                            dest,
                            &visual.crop,
                        )?;
                    }
                    Ok(())
                })
            })?;
            println!(
                "Scanned {} page(s): {} selected walls, {} openings, {} reviewable rooms.",
                scan.pages.len(),
                scan.pages.iter().map(|p| p.walls.len()).sum::<usize>(),
                scan.pages.iter().map(|p| p.openings.len()).sum::<usize>(),
                scan.pages.iter().map(|p| p.rooms.len()).sum::<usize>()
            );
            Ok(())
        }
        _ => Err(format!("unknown command: {cmd}\n{HELP}")),
    }
}
fn main() -> ExitCode {
    let mut args = env::args().skip(1);
    let cmd = args.next().unwrap_or_else(|| "--help".to_owned());
    let native_command = match cmd.as_str() {
        "corrections-to-profile"
        | "object-corrections-to-profile"
        | "kvemo-profile-template"
        | "kvemo-crops-to-profile" => cli::profiles::run(&cmd, args.collect()),
        "wall-truth-evaluate" | "wall-truth-eval" => cli::wall_truth::run(args.collect()),
        "benchmark" | "benchmark-draft" | "benchmark-compare" => {
            cli::benchmark::run(&cmd, args.collect())
        }
        _ => {
            return run(cmd, args).map_or_else(
                |err| {
                    eprintln!("openplantrace-rs: {err}");
                    ExitCode::FAILURE
                },
                |()| ExitCode::SUCCESS,
            );
        }
    };
    native_command.unwrap_or_else(|e| {
        eprintln!("openplantrace-rs: {e}");
        ExitCode::from(2)
    })
}
