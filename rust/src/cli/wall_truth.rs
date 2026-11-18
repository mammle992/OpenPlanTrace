//! Evaluate reviewed drawing-space wall geometry against native or reference structure exports.
mod input;
#[cfg(test)]
mod tests;

use input::{Dataset, Entry, GATES, Line, Run, Structure, TRUTH_SCHEMA};
use openplantrace_rs::model::on_segment;
use serde_json::{Value, json};
use std::{
    cmp::Ordering,
    fs,
    path::{Path, PathBuf},
    process::ExitCode,
    time::{SystemTime, UNIX_EPOCH},
};

const HELP: &str = "wall-truth-evaluate TRUTH.json STRUCTURE.json [--json REPORT.json] [--compact-json] [--no-fail-on-gate]";

fn round(x: f64) -> f64 {
    (x * 1_000_000.).round() / 1_000_000.
}
fn ratio(n: f64, d: f64) -> f64 {
    if d <= 0. { 1. } else { (n / d).clamp(0., 1.) }
}
fn ordinal(a: &str, b: &str) -> Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}
fn rank(s: &str) -> u8 {
    match s {
        "Critical" => 3,
        "Major" => 2,
        "Standard" => 1,
        _ => 0,
    }
}
fn display(x: f64) -> String {
    let formatted = format!("{x:.3}");
    formatted
        .trim_end_matches('0')
        .trim_end_matches('.')
        .to_owned()
}
fn evidence(original: &[String], extra: impl Into<String>) -> Vec<String> {
    let mut terms = original.to_vec();
    terms.push(extra.into());
    terms
}

#[derive(Clone, Copy)]
struct Difference {
    distance: f64,
    endpoint: f64,
    angle: f64,
    overlap: f64,
}
impl Difference {
    fn between(expected: Line, actual: Line) -> Self {
        let e = expected.end - expected.start;
        let a = actual.end - actual.start;
        let angle = (e.y.atan2(e.x) - a.y.atan2(a.x)).abs().to_degrees() % 180.;
        let em = expected.at(0.5);
        let am = actual.at(0.5);
        let x = (actual.start - expected.start).dot(e.unit());
        let y = (actual.end - expected.start).dot(e.unit());
        let len = e.norm();
        Self {
            distance: (em.distance(on_segment(em, actual.start, actual.end).0)
                + am.distance(on_segment(am, expected.start, expected.end).0))
                / 2.,
            endpoint: ((expected.start.distance(actual.start) + expected.end.distance(actual.end))
                .min(expected.start.distance(actual.end) + expected.end.distance(actual.start)))
                / 2.,
            angle: angle.min(180. - angle),
            overlap: if len > 0. && a.norm() > 0. {
                (x.max(y).min(len) - x.min(y).max(0.)).max(0.) / len
            } else {
                0.
            },
        }
    }
    fn fits(self, t: &Entry) -> bool {
        self.distance <= t.line_tol
            && self.endpoint <= t.end_tol
            && self.angle <= t.angle_tol
            && self.overlap >= t.overlap
    }
    fn score(self, t: &Entry, type_match: bool) -> f64 {
        (1. - (self.distance / t.line_tol).clamp(0., 1.)) * 0.30
            + (1. - (self.endpoint / t.end_tol).clamp(0., 1.)) * 0.25
            + (1. - (self.angle / t.angle_tol).clamp(0., 1.)) * 0.15
            + self.overlap * 0.25
            + if type_match { 0.05 } else { 0. }
    }
}

fn not_wall_overlap(truth: &Entry, run: &Run) -> f64 {
    if let Some(line) = truth.line {
        if run.curve.is_some() {
            return 0.;
        }
        let d = Difference::between(line, run.line);
        return if d.angle <= truth.angle_tol && d.distance <= truth.line_tol {
            d.overlap
        } else {
            0.
        };
    }
    let Some([x, y, w, h]) = truth.bounds else {
        return 0.;
    };
    let inside = (0..=24)
        .filter(|i| {
            let t = *i as f64 / 24.;
            let p = run.curve.map_or_else(|| run.line.at(t), |c| c.at(t));
            p.x >= x && p.x <= x + w && p.y >= y && p.y <= y + h
        })
        .count();
    inside as f64 / 25.
}
struct Found<'a> {
    truth: &'a Entry,
    run: &'a Run,
    d: Difference,
    score: f64,
    typed: bool,
}
impl Found<'_> {
    fn json(&self) -> Value {
        let Self {
            truth,
            run,
            d,
            score,
            typed,
        } = self;
        json!({"truthId":truth.id,"wallRunId":run.id,"pageNumber":truth.page,
            "truthWallType":truth.wall_type.unwrap_or("Unknown"),"detectedWallType":run.wall_type,"importance":truth.importance,
            "score":round(*score),"lineDistance":round(d.distance),"endpointDistance":round(d.endpoint),
            "angularDifferenceDegrees":round(d.angle),"lengthOverlapRatio":round(d.overlap),"wallTypeMatches":typed,
            "evidence":[format!("centerline distance {} drawing units", display(d.distance)),
                        format!("endpoint distance {} drawing units",display(d.endpoint)),
                        format!("angular difference {} degrees",display(d.angle)),
                        format!("length overlap ratio {}",display(d.overlap)),
                        if *typed {"wall type matches"} else {"wall type differs"}]})
    }
}

fn assertion(metric: &str, comparator: &str, expected: f64, actual: f64) -> Value {
    let (passed, requirement) = match comparator {
        "==" => ((actual - expected).abs() <= 0.000001, "must equal"),
        ">=" => (actual >= expected, "must be at least"),
        _ => (actual <= expected, "must not exceed"),
    };
    json!({"metric":metric,"comparator":comparator,"expected":expected,"actual":actual,"passed":passed,
           "message":format!("{metric} {} {requirement} {}",display(actual),display(expected))})
}
fn evaluate(dataset: &Dataset, structure: &Structure) -> Value {
    let mut walls: Vec<_> = dataset
        .entries
        .iter()
        .filter_map(|e| e.line.filter(|_| e.is_wall).map(|l| (e, l)))
        .collect();
    walls.sort_by(|(a, l), (b, r)| {
        rank(b.importance)
            .cmp(&rank(a.importance))
            .then_with(|| r.length().total_cmp(&l.length()))
            .then_with(|| ordinal(&a.id, &b.id))
    });
    let mut used = vec![false; structure.runs.len()];
    let mut matches = Vec::new();
    let mut misses = Vec::new();
    for &(truth, line) in &walls {
        let best = structure
            .runs
            .iter()
            .enumerate()
            .filter(|(i, r)| !used[*i] && r.page == truth.page && r.curve.is_none())
            .filter_map(|(i, run)| {
                let d = Difference::between(line, run.line);
                let typed = truth
                    .wall_type
                    .is_none_or(|t| t.eq_ignore_ascii_case(&run.wall_type));
                d.fits(truth).then(|| {
                    (
                        i,
                        Found {
                            truth,
                            run,
                            d,
                            typed,
                            score: d.score(truth, typed),
                        },
                    )
                })
            })
            .min_by(|(_, a), (_, b)| {
                b.score
                    .total_cmp(&a.score)
                    .then_with(|| a.d.distance.total_cmp(&b.d.distance))
                    .then_with(|| a.d.endpoint.total_cmp(&b.d.endpoint))
                    .then_with(|| ordinal(&a.run.id, &b.run.id))
            });
        if let Some((i, hit)) = best {
            used[i] = true;
            matches.push(hit);
        } else {
            misses.push(json!({"truthId":truth.id,"pageNumber":truth.page,"wallType":truth.wall_type.unwrap_or("Unknown"),
               "importance":truth.importance,"centerLine":line,"drawingLength":line.length(),
               "evidence":evidence(&truth.evidence,"no canonical wall run matched the reviewed centerline tolerances")}));
        }
    }
    let mut false_runs: Vec<_> = structure
        .runs
        .iter()
        .zip(&used)
        .filter(|(_, u)| dataset.complete && !**u)
        .map(|(r, _)| r)
        .collect();
    false_runs.sort_by(|a, b| {
        b.length
            .total_cmp(&a.length)
            .then_with(|| ordinal(&a.id, &b.id))
    });
    let false_positives:Vec<_>=false_runs.iter().map(|r|{
        let mut row=json!({"wallRunId":r.id,"pageNumber":r.page,"wallType":r.wall_type,"centerLine":r.line,"drawingLength":r.length,
            "evidence":evidence(&r.evidence[..r.evidence.len().min(10)],"canonical wall run did not match the complete reviewed truth set")});
        if let Some(c)=r.confidence{row["confidence"]=json!(c)}
        row
    }).collect();
    let mut violations = Vec::new();
    for t in dataset.entries.iter().filter(|e| !e.is_wall) {
        for r in structure.runs.iter().filter(|r| r.page == t.page) {
            let overlap = not_wall_overlap(t, r);
            if overlap >= t.overlap {
                violations.push((t, r, round(r.length * overlap), overlap));
            }
        }
    }
    violations.sort_by(|(a, p, l, _), (b, q, m, _)| {
        m.total_cmp(l)
            .then_with(|| ordinal(&a.id, &b.id))
            .then_with(|| ordinal(&p.id, &q.id))
    });
    let not_walls:Vec<_>=violations.iter().map(|(t,r,len,overlap)|json!({"truthId":t.id,"wallRunId":r.id,"pageNumber":t.page,
        "overlapRatio":round(*overlap),"drawingLength":len,"evidence":evidence(&t.evidence,format!("canonical wall run overlaps reviewed not-wall geometry at ratio {}",display(*overlap)))})).collect();
    let total: f64 = walls.iter().map(|(_, l)| l.length()).sum();
    let matched: f64 = matches.iter().map(|m| m.truth.line.unwrap().length()).sum();
    let predicted: f64 = structure.runs.iter().map(|r| r.length).sum();
    let fp_length: f64 = false_runs.iter().map(|r| r.length).sum();
    let n = matches.len() as f64;
    let recall = ratio(n, walls.len() as f64);
    let subset = |keep: fn(&Entry) -> bool| {
        ratio(
            matches.iter().filter(|f| keep(f.truth)).count() as f64,
            walls.iter().filter(|(t, _)| keep(t)).count() as f64,
        )
    };
    let mean = |f: fn(Difference) -> f64| {
        if matches.is_empty() {
            0.
        } else {
            round(matches.iter().map(|m| round(f(m.d))).sum::<f64>() / n)
        }
    };
    let mut metrics = json!({"truthWallCount":walls.len(),"predictedWallCount":structure.runs.len(),"matchedWallCount":matches.len(),"missedWallCount":misses.len(),
        "falsePositiveWallCount":false_runs.len(),"notWallViolationCount":violations.len(),"recall":round(recall),"lengthWeightedRecall":round(ratio(matched,total)),
        "majorWallRecall":round(subset(|t|rank(t.importance)>=2)),"criticalWallRecall":round(subset(|t|t.importance=="Critical")),
        "exteriorWallRecall":round(subset(|t|t.wall_type==Some("Exterior"))),"interiorWallRecall":round(subset(|t|t.wall_type==Some("Interior"))),
        "wallTypeAccuracy":if matches.is_empty(){0.}else{round(matches.iter().filter(|m|m.typed).count() as f64/n)},
        "meanLineDistance":mean(|d|d.distance),"meanEndpointDistance":mean(|d|d.endpoint),"meanAngularDifferenceDegrees":mean(|d|d.angle),"meanLengthOverlapRatio":mean(|d|d.overlap),
        "truthWallDrawingLength":round(total),"matchedTruthDrawingLength":round(matched),"predictedWallDrawingLength":round(predicted),
        "falsePositiveDrawingLength":round(fp_length),"notWallViolationDrawingLength":round(violations.iter().map(|v|v.2).sum())});
    if dataset.complete {
        let precision = ratio(n, structure.runs.len() as f64);
        metrics["precision"] = json!(round(precision));
        if precision + recall > 0. {
            metrics["f1Score"] = json!(round(2. * precision * recall / (precision + recall)));
        }
        metrics["lengthWeightedPrecision"] =
            json!(round(ratio((predicted - fp_length).max(0.), predicted)));
    }
    if let Some(s) = structure.solver {
        for (key, value) in [
            "solverObjectiveScore",
            "solverEndpointConnectivityRatio",
            "solverRoomBoundaryClosureRatio",
        ]
        .into_iter()
        .zip(s)
        {
            metrics[key] = json!(value);
        }
    }
    let identity = dataset
        .document_id
        .as_ref()
        .is_none_or(|id| id.trim().is_empty() || Some(id) == structure.document_id.as_ref());
    let frames = dataset.frames.iter().all(|(page, truth)| {
        let Some((t, s)) = truth
            .as_ref()
            .zip(structure.frames.get(page).and_then(Option::as_ref))
        else {
            return false;
        };
        t.iter()
            .zip(s)
            .all(|(a, b)| (a - b).abs() <= 0.01_f64.max(b * 0.000001))
    });
    let mut assertions = vec![
        assertion("documentIdentityMatch", "==", 1., f64::from(identity)),
        assertion("pageFrameMatch", "==", 1., f64::from(frames)),
    ];
    for (&(_, key, _, min), &expected) in GATES.iter().zip(&dataset.gates) {
        if key == "precision" && !dataset.complete {
            continue;
        }
        assertions.push(assertion(
            key,
            if min { ">=" } else { "<=" },
            expected,
            metrics[key].as_f64().unwrap(),
        ));
    }
    let passed = assertions.iter().all(|a| a["passed"] == true);
    let mut reasons = vec![
        format!(
            "evaluated {} reviewed wall(s) against {} canonical wall run(s)",
            walls.len(),
            structure.runs.len()
        ),
        format!(
            "matched {} reviewed wall(s); missed {}",
            matches.len(),
            misses.len()
        ),
        if dataset.complete {
            format!(
                "complete truth precision evaluated with {} unmatched canonical run(s)",
                false_runs.len()
            )
        } else {
            "truth set is incomplete; precision is intentionally not claimed".into()
        },
        format!(
            "not-wall geometry produced {} canonical violation(s)",
            violations.len()
        ),
        format!(
            "wall truth document identity {} canonical structure",
            if identity {
                "matches"
            } else {
                "does not match"
            }
        ),
        format!(
            "wall truth page frames {} canonical structure",
            if frames { "match" } else { "do not match" }
        ),
        format!("quality gate {}", if passed { "passed" } else { "failed" }),
    ];
    if structure.native {
        reasons.push("Native structure supplies physical wall runs. Solver metrics and unavailable per-run confidence are omitted. Curved runs contribute their arc lengths and cannot match a straight annotated centerline.".into());
    }
    let mut report = json!({"schemaVersion":if structure.native{"openplantrace.rust.wall-truth-evaluation.v1"}else{"openplantrace.wall-truth-evaluation.v1"},
        "evaluatedAt":utc(),"wallTruthSchemaVersion":TRUTH_SCHEMA,"structureSchemaVersion":structure.schema,"completeTruthSet":dataset.complete,
        "passed":passed,"metrics":metrics,"matches":matches.iter().map(Found::json).collect::<Vec<_>>(),"misses":misses,"falsePositives":false_positives,"notWallViolations":not_walls,
        "assertions":assertions,"evidence":reasons});
    for (key, field) in [
        ("datasetName", &dataset.name),
        ("datasetVersion", &dataset.version),
        ("documentId", &dataset.document_id),
    ] {
        if let Some(x) = field {
            report[key] = json!(x);
        }
    }
    report
}

// Gregorian civil date from elapsed UTC days since 1970, in 400-year cycles.
fn utc() -> String {
    let s = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let days = s / 86400 + 719468;
    let era = days / 146097;
    let day = days % 146097;
    let year_in_era = (day - day / 1460 + day / 36524 - day / 146096) / 365;
    let day_in_year = day - (365 * year_in_era + year_in_era / 4 - year_in_era / 100);
    let m = (5 * day_in_year + 2) / 153;
    let d = day_in_year - (153 * m + 2) / 5 + 1;
    let month = if m < 10 { m + 3 } else { m - 9 };
    let year = year_in_era + 400 * era + u64::from(month <= 2);
    format!(
        "{year:04}-{month:02}-{d:02}T{:02}:{:02}:{:02}Z",
        s / 3600 % 24,
        s / 60 % 60,
        s % 60
    )
}
pub fn validate(v: &Value) -> Result<(), String> {
    Dataset::read(v).map(|_| ())
}

fn read(p: &Path) -> Result<Value, String> {
    super::validate::read_json(p).map_err(|e| format!("{}: {e}", p.display()))
}
pub fn run(args: Vec<String>) -> Result<ExitCode, String> {
    if args
        .iter()
        .any(|s| matches!(s.as_str(), "-h" | "--help" | "/?"))
    {
        println!("{HELP}");
        return Ok(ExitCode::SUCCESS);
    }
    let mut inputs = Vec::new();
    let mut output = None;
    let mut compact = false;
    let mut fail = true;
    let mut args = args.into_iter();
    while let Some(arg) = args.next() {
        match arg.to_ascii_lowercase().as_str() {
            "--json" | "--out" => {
                output = Some(PathBuf::from(
                    args.next().ok_or("missing evaluation output path")?,
                ))
            }
            "--compact-json" => compact = true,
            "--no-fail-on-gate" => fail = false,
            _ if arg.starts_with("--") => {
                return Err(format!("unknown wall-truth-evaluate option {arg}"));
            }
            _ => inputs.push(PathBuf::from(arg)),
        }
    }
    let [truth, structure] = inputs.as_slice() else {
        return Err(HELP.into());
    };
    let report = evaluate(
        &Dataset::read(&read(truth)?)?,
        &Structure::read(&read(structure)?)?,
    );
    let passed = report["passed"] == true;
    if let Some(path) = output {
        if let Some(p) = path.parent().filter(|p| !p.as_os_str().is_empty()) {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        let bytes = if compact {
            serde_json::to_vec(&report)
        } else {
            serde_json::to_vec_pretty(&report)
        }
        .map_err(|e| e.to_string())?;
        fs::write(&path, bytes).map_err(|e| format!("{}: {e}", path.display()))?;
        println!(
            "Wall truth evaluation: {}\nResult: {}",
            path.display(),
            if passed { "PASS" } else { "FAIL" }
        );
    } else {
        super::print_json(&report, compact)?;
    }
    Ok(if fail && !passed {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    })
}
