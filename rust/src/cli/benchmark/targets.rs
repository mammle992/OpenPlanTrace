use super::check;
use openplantrace_rs::model::Point;
use serde_json::{Value, json};

const TEXT_FIELDS: &[&str] = &[
    "label",
    "text",
    "regionKind",
    "dimensionOrientation",
    "annotationKind",
    "gridAxisOrientation",
    "wallType",
    "openingType",
    "openingOperation",
    "objectCategory",
    "layerCategory",
];
const LINE_TOLERANCES: &[&str] = &[
    "maxLineDistance",
    "maxEndpointDistance",
    "maxAngularDifferenceDegrees",
    "minLengthOverlapRatio",
];
const PROVENANCE: &[&str] = &[
    "confidence",
    "sourcePrimitiveIds",
    "sourceLayers",
    "evidence",
];
pub(super) fn inactive(spec: &Value) -> bool {
    spec.as_object().is_some_and(|m| {
        m.iter().all(|(k, v)| match k.as_str() {
            "targets" => v.is_null() || v.as_array().is_some_and(Vec::is_empty),
            "minPrecision" | "minRecall" => v.is_null(),
            "completeTruthSet" => v.is_null() || v == false,
            "hasExpectations" | "precisionScoringEnabled" => v.is_boolean(),
            _ => false,
        })
    })
}
fn point(v: &Value) -> Result<Point, String> {
    serde_json::from_value(v.clone()).map_err(|e| format!("invalid target point: {e}"))
}
fn line(v: &Value) -> Result<[Point; 2], String> {
    Ok([point(&v["start"])?, point(&v["end"])?])
}
fn rect(v: &Value) -> Result<[f64; 4], String> {
    let mut r = [0.; 4];
    for (i, k) in ["x", "y", "width", "height"].iter().enumerate() {
        r[i] = v[k]
            .as_f64()
            .ok_or_else(|| format!("missing numeric bounds.{k}"))?;
    }
    if r[2] < 0. || r[3] < 0. {
        return Err("negative target bounds".into());
    }
    Ok(r)
}
fn limit(v: &Value, key: &str, fallback: f64) -> f64 {
    v[key].as_f64().unwrap_or(fallback)
}
fn validate(target: &Value) -> Result<(), String> {
    for (key, value) in target
        .as_object()
        .ok_or("expected benchmark target object")?
    {
        if value.is_null() && super::declared("detectionTarget", key) {
            continue;
        }
        if PROVENANCE.contains(&key.as_str()) {
            continue;
        }
        if key == "detectedTags" && value.as_array().is_some_and(Vec::is_empty) {
            continue;
        }
        if TEXT_FIELDS.contains(&key.as_str()) || key == "id" {
            if !value.is_string() {
                return Err(format!("invalid target.{key}"));
            }
            continue;
        }
        if LINE_TOLERANCES.contains(&key.as_str())
            || matches!(
                key.as_str(),
                "minIntersectionOverUnion" | "maxCenterDistance"
            )
        {
            let n = value
                .as_f64()
                .filter(|n| *n >= 0.)
                .ok_or_else(|| format!("invalid target.{key}"))?;
            let is_line = LINE_TOLERANCES.contains(&key.as_str());
            if target[if is_line { "centerLine" } else { "bounds" }].is_null() {
                return Err(format!("target.{key} requires its geometry"));
            }
            if matches!(
                key.as_str(),
                "minLengthOverlapRatio" | "minIntersectionOverUnion"
            ) && n > 1.
            {
                return Err(format!("invalid target.{key}"));
            }
            continue;
        }
        match key.as_str() {
            "bounds" => {
                rect(value)?;
            }
            "centerLine" => {
                line(value)?;
            }
            "pageNumber" if value.as_u64().is_some_and(|n| n > 0) => {}
            "minCount" if value.as_u64().is_some() => {}
            "requiresReview" if value.is_boolean() => {}
            _ => return Err(format!("unsupported or invalid target criterion '{key}'")),
        }
    }
    Ok(())
}
fn line_score(t: &Value, actual: &Value) -> Option<f64> {
    let [p, q] = line(&t["centerLine"]).ok()?;
    let [a, b] = line(actual).ok()?;
    let e = q - p;
    let d = b - a;
    let el = e.norm();
    let dl = d.norm();
    if el <= 0.001 || dl <= 0.001 {
        return None;
    }
    let angle = if el * dl <= 0.001 {
        90.
    } else {
        (e.dot(d).abs() / (el * dl)).min(1.).acos().to_degrees()
    };
    let lateral = [
        (a - p).cross(e).abs() / el,
        (b - p).cross(e).abs() / el,
        (p - a).cross(d).abs() / dl,
        (q - a).cross(d).abs() / dl,
    ]
    .into_iter()
    .fold(0.0, f64::max);
    let endpoint = p
        .distance(a)
        .max(q.distance(b))
        .min(p.distance(b).max(q.distance(a)));
    let x = (a - p).dot(e.unit());
    let y = (b - p).dot(e.unit());
    let overlap = (x.max(y).min(el) - x.min(y).max(0.)).max(0.) / el;
    if overlap + 1e-6 < limit(t, "minLengthOverlapRatio", 0.80) {
        return None;
    }
    let mut score = overlap;
    for (v, max) in [
        (angle, limit(t, "maxAngularDifferenceDegrees", 5.).min(90.)),
        (lateral, limit(t, "maxLineDistance", 3.)),
        (endpoint, limit(t, "maxEndpointDistance", 8.)),
    ] {
        if v > max + 1e-6 {
            return None;
        }
        score += if max <= 1e-6 {
            if v <= 1e-6 { 1. } else { 0. }
        } else {
            (1. - v / max).clamp(0., 1.)
        };
    }
    Some(score / 4.)
}
fn box_score(t: &Value, actual: &Value) -> Option<f64> {
    let a = rect(&t["bounds"]).ok()?;
    let b = rect(actual).ok()?;
    let w = ((a[0] + a[2]).min(b[0] + b[2]) - a[0].max(b[0])).max(0.);
    let h = ((a[1] + a[3]).min(b[1] + b[3]) - a[1].max(b[1])).max(0.);
    let union = a[2] * a[3] + b[2] * b[3] - w * h;
    let iou = if union > 0. { w * h / union } else { 0. };
    let dist =
        ((a[0] + a[2] / 2.) - (b[0] + b[2] / 2.)).hypot((a[1] + a[3] / 2.) - (b[1] + b[3] / 2.));
    let threshold = t["maxCenterDistance"].as_f64();
    if threshold.is_some_and(|max| dist <= max) {
        return Some(iou.max(1. - dist / threshold.unwrap().max(1.)));
    }
    (iou >= limit(t, "minIntersectionOverUnion", 0.25)).then_some(iou)
}
fn score(target: &Value, record: &Value) -> Option<f64> {
    let mut n = 0.;
    let mut total = 0.;
    for (key, v) in target.as_object()? {
        let k = key.as_str();
        if v.is_null()
            || PROVENANCE.contains(&k)
            || (k == "detectedTags" && v.as_array().is_some_and(Vec::is_empty))
        {
            continue;
        }
        if k == "id"
            || LINE_TOLERANCES.contains(&k)
            || matches!(k, "minIntersectionOverUnion" | "maxCenterDistance")
        {
            continue;
        }
        n += 1.;
        total += match k {
            "centerLine" => line_score(target, &record[k])?,
            "bounds" => box_score(target, &record[k])?,
            "text"
                if record[k]
                    .as_str()?
                    .to_lowercase()
                    .contains(&v.as_str()?.trim().to_lowercase()) =>
            {
                1.
            }
            "minCount" if record[k].as_u64()? >= v.as_u64()? => 1.,
            _ if TEXT_FIELDS.contains(&k)
                && record[k]
                    .as_str()?
                    .trim()
                    .eq_ignore_ascii_case(v.as_str()?.trim()) =>
            {
                1.
            }
            _ if record[k] == *v => 1.,
            _ => return None,
        };
    }
    Some(if n == 0. { 0.1 } else { total / n })
}
pub(super) fn evaluate(
    kind: &str,
    spec: &Value,
    actual: &[Value],
    checks: &mut Vec<Value>,
) -> Result<Value, String> {
    for (k, v) in spec.as_object().ok_or("invalid detector metrics")? {
        if v.is_null() && super::declared("metricExpectations", k) {
            continue;
        }
        if !(match k.as_str() {
            "hasExpectations" | "precisionScoringEnabled" => v.is_boolean(),
            "targets" => v.is_array(),
            "minRecall" | "minPrecision" => v.as_f64().is_some_and(|n| (0.0..=1.0).contains(&n)),
            "completeTruthSet" => v.is_boolean(),
            _ => false,
        }) {
            return Err(format!("unsupported or invalid {kind} metric {k}"));
        }
    }
    let targets = spec["targets"].as_array().map(Vec::as_slice).unwrap_or(&[]);
    let mut used = vec![false; actual.len()];
    let mut matches = Vec::new();
    for (i, t) in targets.iter().enumerate() {
        validate(t)?;
        let mut best = None;
        for (j, a) in actual.iter().enumerate().filter(|(j, _)| !used[*j]) {
            if let Some(s) = score(t, a)
                && best.is_none_or(|(_, x)| s > x)
            {
                best = Some((j, s));
            }
        }
        if let Some((j, _)) = best {
            used[j] = true;
        }
        matches.push(json!({"targetIndex":i,"targetId":t["id"].as_str().map(str::to_owned).unwrap_or_else(||format!("target-{}",i+1)),
            "matched":best.is_some(),"detectionId":best.map(|(j,_)|&actual[j]["id"]),"score":best.map_or(0.,|(_,s)|s)}));
    }
    let hit = used.iter().filter(|b| **b).count();
    let review = actual
        .iter()
        .zip(&used)
        .filter(|(a, b)| !**b && a["requiresReview"] == true)
        .count();
    let scored = actual.len() - review;
    let recall = if targets.is_empty() {
        1.
    } else {
        hit as f64 / targets.len() as f64
    };
    let precision = if scored == 0 {
        f64::from(targets.is_empty())
    } else {
        hit as f64 / scored as f64
    };
    if let Some(floor) = spec["minRecall"]
        .as_f64()
        .or_else(|| (!targets.is_empty()).then_some(1.))
    {
        checks.push(check(
            &format!("{kind}.target_recall.min"),
            json!(floor),
            json!(recall),
            recall + 1e-9 >= floor,
        ));
    }
    if let Some(floor) = spec["minPrecision"].as_f64() {
        checks.push(check(
            &format!("{kind}.target_precision.min"),
            json!(floor),
            json!(precision),
            precision + 1e-9 >= floor,
        ));
    }
    Ok(
        json!({"detector":kind,"expectedCount":targets.len(),"detectedCount":actual.len(),"matchedCount":hit,
        "missedCount":targets.len()-hit,"extraCount":scored-hit,"scoredDetectionCount":scored,"reviewOnlyDetectionCount":review,
        "recall":recall,"precision":precision,"precisionScoringEnabled":spec["completeTruthSet"]==true||spec["minPrecision"].is_number(),
        "f1":if precision+recall==0.{0.}else{2.*precision*recall/(precision+recall)},"matches":matches}),
    )
}
