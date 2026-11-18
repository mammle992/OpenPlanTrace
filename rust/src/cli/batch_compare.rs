use serde::Deserialize;
use serde_json::{Value, json};
use std::{
    collections::{BTreeMap, BTreeSet},
    path::Path,
};

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Batch {
    items: Vec<Item>,
}
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    input_path: String,
    status: String,
    duration_milliseconds: f64,
    counts: Option<BTreeMap<String, u64>>,
}
fn index(path: &Path) -> Result<BTreeMap<String, Item>, String> {
    let mut document: Value = super::validate::read_json(path)?;
    let native = match document["schemaVersion"].as_str() {
        Some("openplantrace.rust.batch.v1") => {
            super::validate::batch::verify(&document, None)?;
            true
        }
        Some("openplantrace.batch.v5" | "openplantrace.batch.v6" | "openplantrace.batch.v7") => {
            super::validate::check_reference_value(&document, "batch-result")?;
            for item in document["items"]
                .as_array_mut()
                .ok_or("missing batch items")?
            {
                // Reference batches include confidence and review flags alongside the common counts.
                let mut comparable = serde_json::Map::new();
                for k in ["pages", "walls", "rooms", "openings"] {
                    comparable.insert(k.to_owned(), item["counts"][k].clone());
                }
                item["counts"] = Value::Object(comparable);
            }
            false
        }
        _ => {
            return Err(format!(
                "unsupported batch result: {}",
                document["schemaVersion"]
            ));
        }
    };
    let batch: Batch = serde_json::from_value(document).map_err(|e| e.to_string())?;
    let mut values = BTreeMap::new();
    for row in batch.items {
        if (native && matches!(row.status.as_str(), "Succeeded" | "CompletedWithErrors"))
            || !matches!(
                row.status.as_str(),
                "Scanned"
                    | "Succeeded"
                    | "CompletedWithErrors"
                    | "Missing"
                    | "Unsupported"
                    | "Failed"
            )
            || row.input_path.trim().is_empty()
            || !row.duration_milliseconds.is_finite()
            || row.duration_milliseconds < 0.
            || (succeeded(&row)
                && !row.counts.as_ref().is_some_and(|c| {
                    ["pages", "walls", "rooms", "openings"]
                        .iter()
                        .all(|k| c.contains_key(*k))
                }))
        {
            return Err("invalid batch item".into());
        }
        #[cfg(windows)]
        let key = row.input_path.replace('/', "\\").to_lowercase();
        #[cfg(not(windows))]
        let key = row.input_path.clone();
        if values.insert(key, row).is_some() {
            return Err("duplicate input path in batch".into());
        }
    }
    Ok(values)
}

fn succeeded(item: &Item) -> bool {
    matches!(item.status.as_str(), "Scanned" | "Succeeded")
}

pub fn run(args: Vec<String>) -> Result<(), String> {
    let mut paths = Vec::new();
    let (mut out, mut markdown, mut ignore, mut compact) = (None, None, false, false);
    let (mut ratio, mut min_ms) = (1.5f64, 250f64);
    let mut args = args.iter();
    while let Some(a) = args.next() {
        match a.as_str() {
            "--help" | "-h" => {
                println!(
                    "batch-compare BASELINE.json CANDIDATE.json [--json FILE] [--markdown FILE] [--duration-ratio N] [--duration-min-ms N] [--no-fail-on-regression]"
                );
                return Ok(());
            }
            "--json" => out = Some(Path::new(args.next().ok_or("missing --json path")?)),
            "--markdown" => {
                markdown = Some(Path::new(args.next().ok_or("missing --markdown path")?))
            }
            "--duration-ratio" => {
                ratio = args
                    .next()
                    .ok_or("missing ratio")?
                    .parse()
                    .map_err(|_| "invalid ratio")?
            }
            "--duration-min-ms" => {
                min_ms = args
                    .next()
                    .ok_or("missing duration")?
                    .parse()
                    .map_err(|_| "invalid duration")?
            }
            "--compact-json" => compact = true,
            "--no-fail-on-regression" => ignore = true,
            _ if !a.starts_with('-') && paths.len() < 2 => paths.push(Path::new(a)),
            _ => return Err(format!("unexpected argument: {a}")),
        }
    }
    let [baseline, candidate] = paths.as_slice() else {
        return Err("supply baseline and candidate batch summaries".into());
    };
    if !ratio.is_finite() || ratio < 1. || !min_ms.is_finite() || min_ms < 0. {
        return Err("invalid timing thresholds".into());
    }
    let old = index(baseline)?;
    let new = index(candidate)?;
    let keys: BTreeSet<_> = old.keys().chain(new.keys()).collect();
    let (mut rows, mut regressions) = (Vec::new(), 0);
    for k in keys {
        let (a, b) = (old.get(k), new.get(k));
        let mut signals = Vec::new();
        let mut diffs = BTreeMap::new();
        match (a, b) {
            (Some(old), Some(new)) => {
                if succeeded(old) && !succeeded(new) {
                    signals.push("scan-failed".to_owned());
                }
                if let (Some(x), Some(y)) = (&old.counts, &new.counts) {
                    for (field, prev) in x {
                        if let Some(now) = y.get(field) {
                            if now != prev {
                                diffs.insert(field, json!({"baseline":prev,"candidate":now}));
                            }
                            if *now == 0 && *prev > 0 {
                                signals.push(format!("{field}-dropped-to-zero"));
                            }
                        }
                    }
                }
                if succeeded(old)
                    && succeeded(new)
                    && new.duration_milliseconds - old.duration_milliseconds >= min_ms
                    && new.duration_milliseconds > old.duration_milliseconds * ratio
                {
                    signals.push("duration-increased".into());
                }
            }
            (Some(_), None) => signals.push("input-removed".into()),
            (None, Some(item)) if !succeeded(item) => signals.push("added-input-failed".into()),
            _ => {}
        }
        regressions += signals.len();
        rows.push(json!({"inputPath":a.or(b).map(|i|&i.input_path),"baselineStatus":a.map(|v|&v.status),"candidateStatus":b.map(|v|&v.status),
            "baselineMilliseconds":a.map(|v|v.duration_milliseconds),"candidateMilliseconds":b.map(|v|v.duration_milliseconds),"countChanges":diffs,"regressions":signals}));
    }
    let result = json!({"schemaVersion":"openplantrace.rust.batch-comparison.v1","comparedMetrics":["input status","page count","selected wall count","room count","opening count","elapsed time"],"passed":regressions==0,"regressionCount":regressions,"items":rows});
    if let Some(path) = out {
        let bytes = if compact {
            serde_json::to_vec(&result)
        } else {
            serde_json::to_vec_pretty(&result)
        }
        .map_err(|e| e.to_string())?;
        super::batch::write_report(path, &bytes)?;
    } else {
        super::print_json(&result, compact)?;
    }
    if let Some(path) = markdown {
        let mut note = format!(
            "# Batch comparison\n\n{regressions} regression signals across {} inputs.\n",
            rows.len()
        );
        for item in &rows {
            if !item["regressions"].as_array().is_none_or(Vec::is_empty) {
                note.push_str(&format!(
                    "\n- {} — {}\n",
                    item["inputPath"].as_str().unwrap_or(""),
                    item["regressions"]
                ));
            }
        }
        super::batch::write_report(path, note.as_bytes())?;
    }
    if regressions > 0 && !ignore {
        Err("batch regression gate failed".into())
    } else {
        Ok(())
    }
}
