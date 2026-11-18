//! Aggregate existing crop records, including invalid lines, without loading the corpus into memory.
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    fs::File,
    io::{BufRead, BufReader, Read},
    path::Path,
};

#[derive(Default)]
struct Counts(BTreeMap<String, (String, u64)>);
impl Counts {
    fn add(&mut self, label: &str, n: u64) {
        let label = label.trim();
        self.0
            .entry(label.to_lowercase())
            .or_insert_with(|| (label.to_owned(), 0))
            .1 += n;
    }
    fn provenance(&mut self, record: &Value, array: &str, fallback: &str, empty: &str) {
        let mut any = false;
        for group in record[array].as_array().into_iter().flatten() {
            if let (Some(label), Some(n)) = (
                label(&group["value"]),
                group["count"]
                    .as_u64()
                    .filter(|v| *v > 0 && *v <= i32::MAX as u64),
            ) {
                self.add(label, n);
                any = true;
            }
        }
        if !any {
            self.add(label(&record[fallback]).unwrap_or(empty), 1);
        }
    }
    fn ranked(&self, limit: usize) -> Vec<Value> {
        let mut rows: Vec<_> = self.0.iter().collect();
        rows.sort_by(|(ka, (_, a)), (kb, (_, b))| b.cmp(a).then(ka.cmp(kb)));
        rows.into_iter()
            .take(limit)
            .map(|(_, (v, n))| json!({"value":v,"count":n}))
            .collect()
    }
}
fn label(v: &Value) -> Option<&str> {
    v.as_str().map(str::trim).filter(|s| !s.is_empty())
}
fn report(path: &Path) -> Result<Value, String> {
    let file = File::open(path).map_err(|e| e.to_string())?;
    let mut reader = BufReader::new(file);
    let mut invalid = Vec::new();
    let (mut entries, mut classified, mut total, mut ratio, mut first) =
        (0u64, 0u64, 0i64, 0., None);
    let (mut native_rows, mut accepted, mut below_threshold) = (0, 0, 0);
    let mut fields: BTreeMap<&str, Counts> = BTreeMap::new();
    let keys = [
        ("byReviewPriority", "reviewPriority"),
        ("bySuggestedTrainingUse", "suggestedTrainingUse"),
        ("byDetectionKind", "detectionKind"),
        ("byCategory", "category"),
    ];
    let (mut sources, mut wall_kinds, mut similarity, mut reasons) = (
        Counts::default(),
        Counts::default(),
        Counts::default(),
        Counts::default(),
    );
    let mut text = String::new();
    for number in 1.. {
        text.clear();
        // Bound individual records, independently of the number of source drawings.
        const MAX_LINE: u64 = 16 << 20;
        let length = (&mut reader)
            .take(MAX_LINE + 1)
            .read_line(&mut text)
            .map_err(|e| e.to_string())? as u64;
        if length == 0 {
            break;
        }
        if length > MAX_LINE {
            return Err(format!("crop record on line {number} exceeds 16 MiB"));
        }
        let line = text.trim_start_matches('\u{feff}').trim();
        if line.is_empty() {
            continue;
        }
        let rec: Value = match serde_json::from_str(line) {
            Ok(v) => v,
            Err(e) => {
                invalid.push(json!({"lineNumber":number,"message":e.to_string()}));
                continue;
            }
        };
        if rec["schemaVersion"] != "openplantrace.kvemo-crops.v2"
            && rec["schemaVersion"] != openplantrace_rs::visual::dataset::SCHEMA
        {
            invalid.push(json!({"lineNumber":number,"message":format!("Unexpected schemaVersion '{}'.",label(&rec["schemaVersion"]).unwrap_or("(missing)"))}));
            continue;
        }
        entries += 1;
        if rec["schemaVersion"] == openplantrace_rs::visual::dataset::SCHEMA {
            native_rows += 1;
            match rec["classification"]["accepted"].as_bool() {
                Some(true) => accepted += 1,
                Some(false) => below_threshold += 1,
                None => (),
            }
        }
        for (out, key) in keys {
            fields
                .entry(out)
                .or_default()
                .add(label(&rec[key]).unwrap_or("(missing)"), 1);
        }
        sources.provenance(&rec, "sourceKindCounts", "sourceKind", "(missing)");
        wall_kinds.provenance(
            &rec,
            "sourceWallComponentKindCounts",
            "sourceWallComponentKind",
            "(none)",
        );
        if let Some(key) = label(&rec["visualSimilarityKey"]) {
            similarity.add(key, 1);
        }
        for reason in rec["reviewReasons"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|v| v.as_str())
        {
            reasons.add(
                if reason.trim().is_empty() {
                    "(missing)"
                } else {
                    reason
                },
                1,
            );
        }
        first.get_or_insert_with(|| label(&rec["imagePath"]).unwrap_or("(missing)").to_owned());
        classified += u64::from(!rec["classification"].is_null());
        total += rec["sourceEvidence"]["primitiveCount"]
            .as_i64()
            .and_then(|n| i32::try_from(n).ok())
            .unwrap_or(0) as i64;
        ratio += rec["objectToCropAreaRatio"].as_f64().unwrap_or(0.);
    }
    let mean = if entries == 0 {
        0.
    } else {
        (ratio / entries as f64 * 1e6).round_ties_even() / 1e6
    };
    let mut result = json!({"manifestPath":std::path::absolute(path).map_err(|e|e.to_string())?,"entryCount":entries,"cropOnlyEntryCount":entries-classified,
        "classifiedEntryCount":classified,"invalidEntryCount":invalid.len(),"sourcePrimitiveTotal":total,"averageObjectToCropAreaRatio":mean,"firstImagePath":first,
        "bySourceKind":sources.ranked(usize::MAX),"bySourceWallComponentKind":wall_kinds.ranked(usize::MAX),"topVisualSimilarityKeys":similarity.ranked(8),
        "topReviewReasons":reasons.ranked(8),"invalidEntries":invalid});
    for (out, _) in keys {
        result[out] = json!(fields.entry(out).or_default().ranked(usize::MAX));
    }
    if native_rows > 0 {
        result["acceptedPredictionCount"] = json!(accepted);
        result["belowThresholdPredictionCount"] = json!(below_threshold);
    }
    Ok(result)
}
pub fn run(args: Vec<String>) -> Result<(), String> {
    let (mut file, mut out, mut compact) = (None, None, false);
    let mut args = args.iter();
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--help" | "-h" => {
                println!("kvemo-report MANIFEST.jsonl [--json REPORT] [--compact-json]");
                return Ok(());
            }
            "--json" => out = Some(Path::new(args.next().ok_or("missing report path")?)),
            "--compact-json" => compact = true,
            _ if !arg.starts_with('-') && file.is_none() => file = Some(Path::new(arg)),
            _ => return Err(format!("unexpected crop report argument {arg}")),
        }
    }
    let summary = report(file.ok_or("supply a crop manifest")?)?;
    if let Some(path) = out {
        let bytes = if compact {
            serde_json::to_vec(&summary)
        } else {
            serde_json::to_vec_pretty(&summary)
        }
        .map_err(|e| e.to_string())?;
        super::batch::write_report(path, &bytes)?;
    } else {
        super::print_json(&summary, compact)?;
    }
    Ok(())
}
