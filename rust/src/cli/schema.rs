use std::{
    fs,
    io::{self, Write},
    path::Path,
};

// Embed the published contracts so a distributed CLI can serve them offline.
macro_rules! contract {
    ($file:literal) => {
        include_bytes!(concat!(
            "../../../docs/schemas/openplantrace.",
            $file,
            ".schema.json"
        ))
    };
}

pub(super) fn canonical_version(name: &str) -> String {
    let name = name.to_ascii_lowercase();
    let suffix = match name.as_str() {
        "scan" => "scan.v72",
        "scan-compact" | "compact-scan" | "scan.compact" => "scan.compact.v1",
        "object-review-dataset" | "object-review" => "object-review-dataset.v2",
        "object-correction-dataset" | "object-corrections" => "object-correction-dataset.v1",
        "benchmark-manifest" | "benchmark" => "benchmark-manifest.v1",
        "benchmark-result" | "benchmark-run" | "benchmark-output" => "benchmark-result.v1",
        "benchmark-comparison" | "benchmark-compare" => "benchmark-comparison.v1",
        "viewer-benchmark-review-session"
        | "benchmark-review-session"
        | "review-session"
        | "viewer-review-session" => "viewer-benchmark-review-session.v1",
        "batch-manifest" | "batch" => "batch-manifest.v1",
        "batch-result" | "batch-run" | "batch-summary" => "batch.v7",
        "batch-comparison" | "batch-compare" => "batch-comparison.v1",
        "layer-profile" | "layers" => "layer-profile.v1",
        "object-label-profile" | "object-labels" => "object-label-profile.v1",
        "kvemo-crops" | "visual-ai-crops" => "kvemo-crops.v2",
        "placement" | "placement-export" | "consumer-placement" => "placement.v12",
        "structure" | "structural-plan" | "canonical-structure" => "structure.v2",
        "wall-truth" | "wall-truth-dataset" => "wall-truth.v1",
        "wall-truth-evaluation" | "wall-truth-result" => "wall-truth-evaluation.v1",
        "visual-snapshot" | "overlay-snapshot" | "visual-qa" => "visual-snapshot.v4",
        _ => return name,
    };
    format!("openplantrace.{suffix}")
}

pub(super) fn published(name: &str) -> Option<&'static [u8]> {
    versioned(&canonical_version(name))
}

pub(super) fn versioned(version: &str) -> Option<&'static [u8]> {
    macro_rules! versions { ($($name:literal),*) => {match version.strip_prefix("openplantrace.")? { $($name=>Some(contract!($name)),)* _=>None }} }
    versions!(
        "batch-comparison.v1",
        "batch-manifest.v1",
        "batch.v5",
        "batch.v6",
        "batch.v7",
        "benchmark-comparison.v1",
        "benchmark-manifest.v1",
        "benchmark-result.v1",
        "decision-trace.v1",
        "enforcement-source-audit.v1",
        "hidden-evaluation-ledger.v1",
        "kvemo-crops.v1",
        "kvemo-crops.v2",
        "layer-profile.v1",
        "object-correction-dataset.v1",
        "object-label-profile.v1",
        "object-review-dataset.v1",
        "object-review-dataset.v2",
        "paired-benchmark-report.v1",
        "placement.v1",
        "placement.v2",
        "placement.v3",
        "placement.v4",
        "placement.v5",
        "placement.v6",
        "placement.v7",
        "placement.v8",
        "placement.v9",
        "placement.v10",
        "placement.v11",
        "placement.v12",
        "scan.compact.v1",
        "scan.v71",
        "scan.v72",
        "semantic-comparator-benchmark-report.v1",
        "semantic-output-diff.v1",
        "structure.v1",
        "structure.v2",
        "viewer-benchmark-review-session.v1",
        "visual-snapshot.v1",
        "visual-snapshot.v2",
        "visual-snapshot.v3",
        "visual-snapshot.v4",
        "wall-truth-evaluation.v1",
        "wall-truth.v1"
    )
}

pub fn run(args: Vec<String>) -> Result<(), String> {
    let mut name = None;
    let mut output = None;
    let mut args = args.iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--help" | "-h" => {
                println!(
                    "schema NAME [--json FILE | --out FILE]\nPrint the published OpenPlanTrace contract. Native scan outputs carry their own schemaVersion."
                );
                return Ok(());
            }
            "--json" | "--out" if output.is_none() => {
                output = Some(Path::new(args.next().ok_or("missing schema output path")?))
            }
            _ if !arg.starts_with('-') && name.is_none() => name = Some(arg),
            _ => return Err(format!("unexpected schema argument: {arg}")),
        }
    }
    let name = name.ok_or("supply a schema name")?;
    let bytes = published(name).ok_or_else(|| format!("unknown published schema: {name}"))?;
    if let Some(path) = output {
        if let Some(parent) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        }
        fs::write(path, bytes).map_err(|e| e.to_string())
    } else {
        io::stdout()
            .lock()
            .write_all(bytes)
            .map_err(|e| e.to_string())
    }
}
