pub(crate) mod batch;
pub(crate) mod crops;
#[cfg(feature = "published-contracts")]
mod published_scan;
mod spatial;
use openplantrace_rs::{export, model::Scan};
use serde_json::{Value, json};
use std::{
    fs,
    io::{BufRead, BufReader, Read},
    path::Path,
};

const LIMIT: u64 = 512 * 1024 * 1024;
fn reader(path: &Path) -> Result<BufReader<std::io::Take<Box<dyn Read>>>, String> {
    let f = fs::File::open(path).map_err(|e| e.to_string())?;
    if f.metadata().map_err(|e| e.to_string())?.len() > LIMIT {
        return Err("input exceeds 512 MiB".into());
    }
    let source: Box<dyn Read> = if path
        .extension()
        .is_some_and(|v| v.eq_ignore_ascii_case("gz"))
    {
        Box::new(flate2::read::MultiGzDecoder::new(f))
    } else {
        Box::new(f)
    };
    let mut reader = BufReader::new(source.take(LIMIT + 1));
    if reader
        .fill_buf()
        .map_err(|e| e.to_string())?
        .starts_with(&[0xEF, 0xBB, 0xBF])
    {
        reader.consume(3);
    }
    Ok(reader)
}

pub(super) fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<T, String> {
    let mut r = reader(path)?;
    let object = serde_json::from_reader(&mut r).map_err(|e| e.to_string())?;
    if r.get_ref().limit() == 0 {
        return Err("decompressed input exceeds 512 MiB".into());
    }
    Ok(object)
}

pub fn expand(args: Vec<String>) -> Result<(), String> {
    let (mut input, mut output) = (None, None);
    let mut args = args.iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "-h" | "--help" => {
                println!("expand-compact SCAN.json[.gz] [--json NEW_FILE]");
                return Ok(());
            }
            "--json" if output.is_none() => {
                output = Some(Path::new(args.next().ok_or("missing output path")?))
            }
            _ if !arg.starts_with('-') && input.is_none() => input = Some(Path::new(arg)),
            _ => return Err(format!("unexpected compact scan argument: {arg}")),
        }
    }
    let value: Value = read_json(input.ok_or("supply a compact scan")?)?;
    let expanded = openplantrace_rs::compact::decode(&value)?;
    if let Some(path) = output {
        super::batch::write_report(
            path,
            &serde_json::to_vec(&expanded).map_err(|e| e.to_string())?,
        )
    } else {
        super::print_json(&expanded, true)
    }
}

#[cfg(feature = "published-contracts")]
fn contract(bytes: &[u8]) -> Result<jsonschema::Validator, String> {
    let schema: Value =
        serde_json::from_slice(bytes.strip_prefix(&[239, 187, 191]).unwrap_or(bytes))
            .map_err(|e| e.to_string())?;
    jsonschema::validator_for(&schema).map_err(|e| format!("invalid built-in schema: {e}"))
}

#[cfg(feature = "published-contracts")]
type Validators = std::collections::HashMap<String, jsonschema::Validator>;
#[cfg(not(feature = "published-contracts"))]
type Validators = ();
#[cfg(feature = "published-contracts")]
fn reference(
    value: &Value,
    kind: &str,
    deep: bool,
    validators: &mut Validators,
) -> Result<(), String> {
    let version = value["schemaVersion"]
        .as_str()
        .ok_or("missing schemaVersion")?;
    let bytes = super::schema::versioned(version)
        .ok_or_else(|| format!("unsupported schemaVersion: {version}"))?;
    if kind != "auto" {
        let named = super::schema::canonical_version(kind);
        if super::schema::versioned(&named).is_none() {
            return Err(format!("unknown artifact kind: {kind}"));
        }
        if version.rsplit_once(".v").map(|(family, _)| family)
            != named.rsplit_once(".v").map(|(family, _)| family)
        {
            return Err(format!("kind {kind} does not match {version}"));
        }
    }
    let compiled = match validators.entry(version.to_owned()) {
        std::collections::hash_map::Entry::Occupied(v) => v.into_mut(),
        std::collections::hash_map::Entry::Vacant(v) => v.insert(contract(bytes)?),
    };
    if let Err(e) = compiled.validate(value) {
        return Err(format!("{}: {e}", e.instance_path()));
    }
    if version == "openplantrace.wall-truth.v1" {
        super::wall_truth::validate(value)?;
    } else if deep && version == "openplantrace.scan.v72" {
        published_scan::verify(value)?;
    } else if deep {
        return Err(format!(
            "deep semantic validation is unavailable for {version}"
        ));
    }
    Ok(())
}

#[cfg(not(feature = "published-contracts"))]
fn reference(_: &Value, _: &str, _: bool, _: &mut Validators) -> Result<(), String> {
    Err("this build omits published-contract validation".into())
}

pub(super) fn check_reference_value(value: &Value, kind: &str) -> Result<(), String> {
    reference(value, kind, false, &mut Validators::default())
}

fn check(
    path: &Path,
    kind: &str,
    deep: bool,
    source_scan: Option<&Path>,
) -> Result<String, String> {
    // The common native path stays streaming and retains all domain validation.
    if source_scan.is_none() && matches!(kind, "auto" | "scan" | "native-scan") {
        if let Ok(scan) = read_json::<Scan>(path) {
            if scan.schema_version.starts_with("openplantrace.rust.scan.") {
                export::validate(&scan)?;
                return Ok(scan.schema_version);
            }
        }
        if kind == "native-scan" {
            return Err("invalid native scan".into());
        }
    }
    let mut validators = Validators::default();
    if path
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("jsonl"))
    {
        if !matches!(kind, "auto" | "kvemo-crops") {
            return Err("JSON Lines requires kvemo-crops kind".into());
        }
        let mut lines = reader(path)?;
        let mut line = String::new();
        let mut count = 0usize;
        loop {
            line.clear();
            if lines.read_line(&mut line).map_err(|e| e.to_string())? == 0 {
                break;
            }
            if line.trim().is_empty() {
                continue;
            }
            count += 1;
            let v: Value =
                serde_json::from_str(&line).map_err(|e| format!("entry {count}: {e}"))?;
            (if v["schemaVersion"] == openplantrace_rs::visual::dataset::SCHEMA {
                crops::verify(
                    &v,
                    deep.then_some(
                        path.parent()
                            .filter(|p| !p.as_os_str().is_empty())
                            .unwrap_or(Path::new(".")),
                    ),
                )
            } else {
                reference(&v, "kvemo-crops", deep, &mut validators)
            })
            .map_err(|e| format!("entry {count}: {e}"))?;
        }
        if lines.get_ref().limit() == 0 {
            return Err("decompressed input exceeds 512 MiB".into());
        }
        if count == 0 {
            return Err("empty crop manifest".into());
        }
        return Ok(format!("{count} crop entries"));
    }
    let v: Value = read_json(path)?;
    let schema = v["schemaVersion"].as_str().unwrap_or("");
    if schema == "openplantrace.rust.batch.v1" {
        if !matches!(
            kind,
            "auto" | "batch-result" | "batch-summary" | "batch-run"
        ) {
            return Err(format!("kind {kind} does not match {schema}"));
        }
        batch::verify(
            &v,
            deep.then_some(
                path.parent()
                    .filter(|p| !p.as_os_str().is_empty())
                    .unwrap_or(Path::new(".")),
            ),
        )?;
        return Ok(schema.to_owned());
    }
    if matches!(
        schema,
        openplantrace_rs::compact::NATIVE | openplantrace_rs::compact::REFERENCE
    ) {
        if !matches!(kind, "auto" | "scan-compact" | "compact-scan") {
            return Err(format!("kind {kind} does not match {schema}"));
        }
        let expanded = openplantrace_rs::compact::decode(&v)?;
        if schema == openplantrace_rs::compact::REFERENCE {
            reference(&v, "scan-compact", false, &mut validators)?;
            reference(&expanded, "scan", false, &mut validators)?;
        } else {
            let scan: Scan = serde_json::from_value(expanded).map_err(|e| e.to_string())?;
            export::validate(&scan)?;
        }
        return Ok(schema.to_owned());
    }
    if kind == "geojson" || (kind == "auto" && v["type"] == "FeatureCollection") {
        return spatial::verify(&v);
    }
    let projection = match schema {
        "openplantrace.rust.structure.v1" | "openplantrace.rust.structure.v2" => Some("structure"),
        "openplantrace.rust.placement.v1"
        | "openplantrace.rust.placement.v2"
        | "openplantrace.rust.placement.v3" => Some("placement"),
        "openplantrace.rust.local-features.v1" => Some("local-features"),
        "openplantrace.rust.connections.v1" => Some("connections"),
        _ => None,
    };
    if let Some(which) = projection {
        if kind != "auto" && kind != which {
            return Err(format!("kind {kind} does not match {schema}"));
        }
        let parent = path.parent().unwrap_or(Path::new("."));
        let raw = parent.join("scan.json");
        let source = if raw.is_file() {
            raw
        } else {
            parent.join("scan.json.gz")
        };
        let scan: Scan = read_json(source_scan.unwrap_or(&source)).map_err(|e| {
            format!("native projection validation requires the bundle's source scan: {e}")
        })?;
        export::validate(&scan)?;
        let mut expected = match which {
            "structure" => export::structure(&scan),
            "placement" => export::placement(&scan),
            "connections" => export::connections(&scan),
            _ => export::spatial(&scan, None)?,
        };
        // The first two releases omitted the optional page extents from their structure export.
        if which == "structure"
            && matches!(
                scan.engine_version.as_str(),
                "0.12.24-alpha.1" | "0.12.25-alpha.2"
            )
        {
            if let (Some(stored), Some(pages)) =
                (v["pages"].as_array(), expected["pages"].as_array_mut())
            {
                for (old, page) in stored.iter().zip(pages) {
                    if old.get("width").is_none() && old.get("height").is_none() {
                        if let Some(obj) = page.as_object_mut() {
                            obj.remove("width");
                            obj.remove("height");
                        }
                    }
                }
            }
        }
        if expected != v {
            return Err(format!("{which} differs from the source scan"));
        }
        return Ok(schema.to_owned());
    }
    reference(&v, kind, deep, &mut validators)?;
    Ok(v["schemaVersion"].as_str().unwrap_or("").to_owned())
}

pub fn run(args: Vec<String>) -> Result<(), String> {
    let (mut input, mut kind, mut output, mut deep, mut compact) =
        (None, "auto", None, false, false);
    let mut args = args.iter();
    let mut source_scan = None;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--kind" => kind = args.next().ok_or("missing artifact kind")?,
            "--deep" => deep = true,
            "--source-scan" => {
                source_scan = Some(Path::new(args.next().ok_or("missing source scan path")?))
            }
            "--compact-json" => compact = true,
            "--json" => output = Some(Path::new(args.next().ok_or("missing report path")?)),
            "-h" | "--help" => {
                println!(
                    "validate ARTIFACT [--kind KIND] [--source-scan SCAN.json] [--deep] [--json REPORT] [--compact-json]"
                );
                return Ok(());
            }
            _ if input.is_none() && !arg.starts_with('-') => input = Some(Path::new(arg)),
            _ => return Err(format!("unexpected validation argument: {arg}")),
        }
    }
    let input = input.ok_or("supply an artifact to validate")?;
    let result = check(input, kind, deep, source_scan);
    if let Some(output) = output {
        let report = json!({"schemaVersion":"openplantrace.rust.validation.v1","path":input,"valid":result.is_ok(),"artifactVersion":result.as_ref().ok(),"error":result.as_ref().err()});
        let bytes = if compact {
            serde_json::to_vec(&report)
        } else {
            serde_json::to_vec_pretty(&report)
        }
        .map_err(|e| e.to_string())?;
        super::batch::write_report(output, &bytes)?;
    }
    let version = result?;
    println!("Valid: {version}");
    Ok(())
}
