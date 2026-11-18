use super::{Args, COMPARE, GROUPS, Inventory, MANIFEST, RESULT, read};
use openplantrace_rs::{export, model::Scan};
use serde_json::{Map, Value, json};
use std::{
    collections::{BTreeMap, HashSet},
    path::Path,
};

fn reference_records(scan: &Value) -> Result<BTreeMap<&'static str, Vec<Value>>, String> {
    let mut groups = BTreeMap::new();
    for (_, name) in GROUPS {
        let source = if *name == "layers" {
            &scan["layerAnalysis"]["layers"]
        } else {
            &scan[name]
        };
        let rows = source
            .as_array()
            .ok_or_else(|| format!("missing reference scan {name}"))?;
        let mut records = Vec::new();
        for r in rows {
            let mut record = json!({"id":r["id"],"pageNumber":r["pageNumber"],"bounds":r["bounds"],"label":r["label"]});
            for &(from, to) in match *name {
                "regions" => &[("kind", "regionKind")][..],
                "dimensions" => &[("orientation", "dimensionOrientation"), ("text", "text")],
                "annotations" => &[("kind", "annotationKind")],
                "gridAxes" => &[("orientation", "gridAxisOrientation")],
                "walls" => &[("wallType", "wallType"), ("centerLine", "centerLine")],
                "rooms" => &[],
                "openings" => &[("type", "openingType"), ("operation", "openingOperation")],
                "objects" => &[("category", "objectCategory"), ("symbolName", "text")],
                "layers" => &[
                    ("name", "id"),
                    ("name", "label"),
                    ("likelyCategory", "layerCategory"),
                ],
                "objectGroups" => &[
                    ("category", "objectCategory"),
                    ("symbolName", "text"),
                    ("count", "minCount"),
                    ("requiresReview", "requiresReview"),
                    ("representativeBounds", "bounds"),
                ],
                _ => unreachable!(),
            } {
                record[to] = r[from].clone();
            }
            if *name == "objectGroups" && r["pageNumbers"].as_array().is_some_and(|p| p.len() == 1)
            {
                record["pageNumber"] = r["pageNumbers"][0].clone();
            }
            if record["id"].as_str().is_none() {
                return Err(format!("missing reference {name} id"));
            }
            records.push(record);
        }
        groups.insert(*name, records);
    }
    Ok(groups)
}

pub(super) fn draft(path: &Path, args: &Args) -> Result<Value, String> {
    let scan = read(path)?;
    let scan = if matches!(
        scan["schemaVersion"].as_str(),
        Some(openplantrace_rs::compact::NATIVE | openplantrace_rs::compact::REFERENCE)
    ) {
        openplantrace_rs::compact::decode(&scan)?
    } else {
        scan
    };
    let schema = scan["schemaVersion"]
        .as_str()
        .ok_or("missing scan schemaVersion")?;
    let groups = if matches!(
        schema,
        "openplantrace.rust.scan.v1"
            | "openplantrace.rust.scan.v2"
            | "openplantrace.rust.scan.v3"
            | "openplantrace.rust.scan.v4"
            | "openplantrace.rust.scan.v5"
            | "openplantrace.rust.scan.v6"
            | "openplantrace.rust.scan.v7"
            | "openplantrace.rust.scan.v8"
            | "openplantrace.rust.scan.v9"
            | "openplantrace.rust.scan.v10"
            | "openplantrace.rust.scan.v11"
            | "openplantrace.rust.scan.v12"
            | "openplantrace.rust.scan.v13"
            | "openplantrace.rust.scan.v14"
            | "openplantrace.rust.scan.v15"
            | "openplantrace.rust.scan.v16"
    ) {
        let typed: Scan = serde_json::from_value(scan).map_err(|e| e.to_string())?;
        export::validate(&typed)?;
        Inventory::new(&typed).groups
    } else if schema == "openplantrace.scan.v72" {
        reference_records(&scan)?
    } else {
        return Err(format!("unsupported input scan schema {schema}"));
    };
    let cap = args
        .value("--max-targets-per-detector")
        .unwrap_or("8")
        .parse::<usize>()
        .map_err(|_| "invalid target limit")?;
    let recall = args.num("--target-recall", 1.)?;
    let precision = args
        .value("--target-precision")
        .map(|_| args.num("--target-precision", 1.))
        .transpose()?;
    if recall > 1. || precision.is_some_and(|n| n > 1.) {
        return Err("recall and precision must be in [0,1]".into());
    }
    let source = args
        .value("--source-path")
        .ok_or("benchmark-draft requires --source-path")?;
    let id = args
        .value("--fixture-id")
        .or_else(|| path.file_stem()?.to_str())
        .ok_or("missing fixture id")?;
    let mut expectations = Map::new();
    for &(metric, kind) in GROUPS {
        let group = &groups[kind];
        if group.is_empty() {
            continue;
        }
        let mut chars = kind.chars();
        let count_key = format!(
            "min{}{}",
            chars.next().unwrap().to_ascii_uppercase(),
            chars.as_str()
        );
        if super::declared("expectations", &count_key) {
            expectations.insert(count_key, json!(group.len()));
        }
        let mut targets = Vec::new();
        for r in group.iter().take(cap) {
            let mut target = r.as_object().unwrap().clone();
            target.retain(|_, v| !v.is_null());
            if args.flags.contains("--no-bounds") {
                target.remove("bounds");
            }
            // A point or an axis has a zero-area box. A finite center or centerline can still bind it.
            if let Some(b) = target.get("bounds")
                && (b["width"].as_f64() == Some(0.) || b["height"].as_f64() == Some(0.))
            {
                target.insert("maxCenterDistance".into(), json!(0.01));
            }
            targets.push(Value::Object(target));
        }
        if !targets.is_empty() {
            let mut m = json!({"targets":targets,"minRecall":recall,"completeTruthSet":false});
            if let Some(p) = precision {
                m["minPrecision"] = json!(p);
            }
            expectations.insert(metric.to_owned(), m);
        }
    }
    let mut fixture = json!({"id":id,"name":args.value("--fixture-name").unwrap_or(id),"sourcePath":source,
        "optional":args.flags.contains("--optional"),"properties":{"draftedFrom":"scan-json","draftReview":"Review observed detections before adopting them as ground truth."},"expectations":expectations});
    if let Some(s) = args.value("--skip-reason") {
        fixture["skipReason"] = json!(s);
    }
    Ok(
        json!({"schemaVersion":MANIFEST,"name":args.value("--name").unwrap_or("Native scanner benchmark draft"),"fixtures":[fixture]}),
    )
}

fn cases(report: &Value) -> Result<BTreeMap<&str, &Value>, String> {
    let native = match report["schemaVersion"].as_str() {
        Some(RESULT) => true,
        Some("openplantrace.benchmark-result.v1") => false,
        _ => return Err("unsupported benchmark result schemaVersion".into()),
    };
    let mut out = BTreeMap::new();
    for c in report["cases"]
        .as_array()
        .ok_or("missing benchmark cases")?
    {
        let id = c
            .get("fixtureId")
            .or_else(|| c.get("id"))
            .and_then(Value::as_str)
            .filter(|s| !s.is_empty())
            .ok_or("missing fixture id")?;
        if out.insert(id, c).is_some() {
            return Err("duplicate benchmark result fixture".into());
        }
        if !c["passed"].is_boolean()
            || !c["assertions"].is_array()
            || c["durationMilliseconds"].as_f64().is_none_or(|n| n < 0.)
        {
            return Err(format!("malformed result case {id}"));
        }
        if native && !c["skipped"].is_boolean() {
            return Err(format!("missing skip status on case {id}"));
        }
    }
    Ok(out)
}
fn failures(c: &Value) -> HashSet<String> {
    c["assertions"]
        .as_array()
        .into_iter()
        .flatten()
        .filter(|a| a["passed"] == false)
        .map(|a| format!("{} {}", a["name"], a["expected"]))
        .collect()
}
pub(super) fn compare(old: &Value, new: &Value, args: &Args) -> Result<Value, String> {
    // Cross-engine comparisons use different definitions of many checks. Compare report pairs within a single schema.
    if old["schemaVersion"] != new["schemaVersion"] {
        return Err("benchmark comparison requires two results with the same schemaVersion".into());
    }
    let old = cases(old)?;
    let new = cases(new)?;
    let mut rows = Vec::new();
    let mut added = Vec::new();
    let mut removed = Vec::new();
    let mut regressions = 0;
    let mut improvements = 0;
    for (&id, c) in &new {
        let Some(base) = old.get(id) else {
            added.push(id);
            continue;
        };
        let mut worse = Vec::new();
        let mut better = Vec::new();
        let a = failures(base);
        let b = failures(c);
        for k in b.difference(&a) {
            worse.push(format!("new failed assertion {k}"));
        }
        for k in a.difference(&b) {
            better.push(format!("resolved failed assertion {k}"));
        }
        if base["passed"] == true && c["passed"] == false {
            worse.push("fixture failed".into());
        }
        if base["passed"] == false && c["passed"] == true {
            better.push("fixture passed".into());
        }
        if base["skipped"] != true && c["skipped"] == true {
            worse.push("fixture newly skipped".into());
        }
        let prev = base["durationMilliseconds"].as_f64().unwrap();
        let now = c["durationMilliseconds"].as_f64().unwrap();
        if now > prev * args.num("--duration-ratio", 1.5)?
            && now - prev >= args.num("--duration-min-ms", 250.)?
        {
            worse.push("scan duration increased".into());
        }
        for x in c["metrics"].as_array().into_iter().flatten() {
            let Some(y) = base["metrics"]
                .as_array()
                .into_iter()
                .flatten()
                .find(|o| o["detector"] == x["detector"])
            else {
                continue;
            };
            for (key, opt) in [
                ("recall", "--detector-recall-drop"),
                ("precision", "--detector-precision-drop"),
                ("f1", "--detector-f1-drop"),
            ] {
                if key != "recall"
                    && (x["precisionScoringEnabled"] != true
                        || y["precisionScoringEnabled"] != true)
                {
                    continue;
                }
                if let (Some(a), Some(b)) = (y[key].as_f64(), x[key].as_f64())
                    && a - b > args.num(opt, 0.05)?
                {
                    worse.push(format!("{} {key} dropped", x["detector"]));
                }
            }
        }
        worse.sort();
        better.sort();
        regressions += worse.len();
        improvements += better.len();
        rows.push(json!({"fixtureId":id,"regressions":worse,"improvements":better,"baselineDurationMilliseconds":prev,"candidateDurationMilliseconds":now}));
    }
    for &id in old.keys() {
        if !new.contains_key(id) {
            removed.push(id);
            regressions += 1;
        }
    }
    Ok(
        json!({"schemaVersion":COMPARE,"matchedCaseCount":rows.len(),"addedCaseCount":added.len(),"removedCaseCount":removed.len(),
        "regressionCount":regressions,"improvementCount":improvements,"added":added,"removed":removed,"cases":rows}),
    )
}

pub(super) fn markdown(cmd: &str, data: &Value) -> String {
    let mut out = String::new();
    match cmd {
        "benchmark" => {
            out.push_str("# Benchmark results\n\n");
            for c in data["cases"].as_array().into_iter().flatten() {
                let id = c
                    .get("fixtureId")
                    .unwrap_or(&c["id"])
                    .as_str()
                    .unwrap_or("");
                let status = if c["skipped"] == true {
                    "skipped"
                } else if c["passed"] == true {
                    "passed"
                } else {
                    "failed"
                };
                out.push_str(&format!("## {id}\n\n{status}\n\n"));
                for a in c["assertions"]
                    .as_array()
                    .into_iter()
                    .flatten()
                    .filter(|a| a["passed"] != true)
                {
                    out.push_str(&format!(
                        "- {}. Expected {}. Observed {}.\n",
                        a["name"].as_str().unwrap_or(""),
                        a["expected"],
                        a["actual"]
                    ));
                }
                out.push('\n');
            }
        }
        "benchmark-draft" => {
            out.push_str("# Benchmark draft review\n\n");
            for f in data["fixtures"].as_array().into_iter().flatten() {
                out.push_str(&format!("## {}\n\n", f["id"].as_str().unwrap_or("")));
                for (metric, _) in GROUPS {
                    for target in f["expectations"][metric]["targets"]
                        .as_array()
                        .into_iter()
                        .flatten()
                    {
                        out.push_str(&format!("- {metric} {}\n", target));
                    }
                }
            }
        }
        _ => {
            out.push_str(&format!(
                "# Benchmark comparison\n\n{} matched cases, {} regressions, {} improvements.\n\n",
                data["matchedCaseCount"], data["regressionCount"], data["improvementCount"]
            ));
            for c in data["cases"].as_array().into_iter().flatten() {
                for msg in c["regressions"].as_array().into_iter().flatten() {
                    out.push_str(&format!(
                        "- {} — {}\n",
                        c["fixtureId"].as_str().unwrap_or(""),
                        msg.as_str().unwrap_or("")
                    ));
                }
            }
        }
    }
    out
}
