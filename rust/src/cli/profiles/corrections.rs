use super::*;
use std::collections::BTreeSet;

struct Fields<'a>(&'a serde_json::Map<String, Value>);
impl<'a> Fields<'a> {
    fn new(v: &'a Value) -> Result<Self, String> {
        Ok(Self(v.as_object().ok_or("expected an object")?))
    }
    fn get(&self, key: &str) -> &'a Value {
        self.0
            .get(key)
            .or_else(|| {
                self.0
                    .iter()
                    .find(|(k, _)| k.eq_ignore_ascii_case(key))
                    .map(|(_, v)| v)
            })
            .unwrap_or(&Value::Null)
    }
    fn text(&self, key: &str) -> Result<Option<&'a str>, String> {
        let v = self.get(key);
        if !v.is_null() && !v.is_string() {
            return Err(format!("{key} must be a string"));
        }
        Ok(label(v))
    }
    fn enumeration<'b>(&self, key: &str, names: &'b [&str]) -> Result<Option<&'b str>, String> {
        self.text(key)?
            .map(|s| canon(s, names).ok_or_else(|| format!("invalid {key}: {s}")))
            .transpose()
    }
    fn list(&self, key: &str) -> Result<Vec<&'a str>, String> {
        let v = self.get(key);
        if !v.is_null()
            && v.as_array().is_none_or(|items| {
                items
                    .iter()
                    .any(|item| !item.is_string() && !item.is_null())
            })
        {
            return Err(format!("{key} must be an array of strings"));
        }
        Ok(labels(v, false))
    }
}

/// Explicit-offset ISO review times are serialized using DateTimeOffset's round-trip format.
fn timestamp(s: &str) -> Result<String, String> {
    let invalid = || format!("invalid review timestamp: {s}");
    let number = |start: usize, end: usize| {
        s.get(start..end)
            .and_then(|p| p.bytes().all(|c| c.is_ascii_digit()).then_some(p))
            .and_then(|p| p.parse::<u32>().ok())
            .ok_or_else(invalid)
    };
    let bytes = s.as_bytes();
    if !s.is_ascii()
        || bytes.get(4) != Some(&b'-')
        || bytes.get(7) != Some(&b'-')
        || bytes.get(10) != Some(&b'T')
        || bytes.get(13) != Some(&b':')
    {
        return Err(invalid());
    }
    let (year, month, day, hour, minute) = (
        number(0, 4)?,
        number(5, 7)?,
        number(8, 10)?,
        number(11, 13)?,
        number(14, 16)?,
    );
    let leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    let max_day = match month {
        1 | 3 | 5 | 7 | 8 | 10 | 12 => 31,
        4 | 6 | 9 | 11 => 30,
        2 => 28 + u32::from(leap),
        _ => 0,
    };
    if year == 0 || day == 0 || day > max_day || hour > 23 || minute > 59 {
        return Err(invalid());
    }
    let mut pos = 16;
    let second = if bytes.get(pos) == Some(&b':') {
        pos += 3;
        number(17, 19)?
    } else {
        0
    };
    if second > 59 {
        return Err(invalid());
    }
    let mut fraction = String::new();
    if bytes.get(pos) == Some(&b'.') {
        let start = pos + 1;
        pos = start
            + bytes[start..]
                .iter()
                .take_while(|c| c.is_ascii_digit())
                .count();
        if pos == start || pos - start > 16 {
            return Err(invalid());
        }
        fraction.push_str(&s[start..pos.min(start + 7)]);
    }
    let zone = match &s[pos..] {
        "Z" => "+00:00".to_owned(),
        zone if matches!(zone.len(), 3 | 6) && matches!(bytes[pos], b'+' | b'-') => {
            let h = number(pos + 1, pos + 3)?;
            let m = if zone.len() == 6 && bytes[pos + 3] == b':' {
                number(pos + 4, pos + 6)?
            } else if zone.len() == 3 {
                0
            } else {
                return Err(invalid());
            };
            if h > 14 || m > 59 || h == 14 && m != 0 {
                return Err(invalid());
            }
            format!(
                "{}{h:02}:{m:02}",
                if h == 0 && m == 0 {
                    '+'
                } else {
                    bytes[pos] as char
                }
            )
        }
        _ => return Err(invalid()),
    };
    Ok(format!("{}:{second:02}.{fraction:0<7}{zone}", &s[..16]))
}

pub(super) fn profile(dataset: &Value, o: &Options) -> Result<Value, String> {
    let d = Fields::new(dataset)?;
    if let Some(schema) = d.text("schemaVersion")?
        && !schema.eq_ignore_ascii_case("openplantrace.object-correction-dataset.v1")
    {
        return Err(format!(
            "unsupported correction dataset schemaVersion: {schema}"
        ));
    }
    let actions = d
        .get("actions")
        .as_array()
        .ok_or("correction dataset requires an actions array")?;
    let mut rules = Vec::new();
    for (i, action) in actions.iter().enumerate() {
        if let Some(row) = action_rule(action, o)
            .map_err(|e| format!("object correction action #{}: {e}", i + 1))?
        {
            rules.push(row)
        }
    }
    rules.sort_by(|a, b| a.0.cmp(&b.0).then(a.1.cmp(b.1)));
    let default_name = format!(
        "{} object correction labels",
        d.text("documentId")?.unwrap_or("OpenPlanTrace")
    );
    Ok(
        json!({"schemaVersion":SCHEMA,"name":o.name.as_deref().unwrap_or(&default_name),"version":o.version.as_deref().or(d.text("version")?).unwrap_or("corrections"),"rules":rules.into_iter().map(|(_,_,v)|v).collect::<Vec<_>>()}),
    )
}
type SortedRule<'a> = (Option<&'a str>, &'a str, Value);
fn action_rule<'a>(action: &'a Value, o: &Options) -> Result<Option<SortedRule<'a>>, String> {
    let a = Fields::new(action)?;
    let id = a.text("actionId")?.ok_or("actionId is required")?;
    a.enumeration("targetKind", &["Group", "Candidate"])?
        .ok_or("targetKind is required")?;
    let decision = a
        .enumeration(
            "decision",
            &["Confirmed", "Corrected", "Ignored", "Unreviewed", "Unknown"],
        )?
        .ok_or("decision is required")?;
    let scope = a
        .enumeration(
            "applyScope",
            &[
                "TargetOnly",
                "MatchingSignature",
                "MatchingSymbolAndLayer",
                "MatchingDetectedTagPattern",
            ],
        )?
        .ok_or("applyScope is required")?;
    let category = a.enumeration("correctedCategory", CATEGORIES)?;
    let kind = a.enumeration("correctedKind", KINDS)?;
    let corrected_label = a.text("correctedLabel")?;
    let corrected_symbol = a.text("correctedSymbolName")?;
    let review = match a.get("requiresReview") {
        Value::Bool(v) => Some(*v),
        Value::Null => None,
        _ => return Err("requiresReview must be boolean".into()),
    };
    let confidence = match a.get("confidence") {
        Value::Null => None,
        v => Some(
            v.as_f64()
                .filter(|v| (0.0..=1.0).contains(v))
                .ok_or("confidence must be between 0 and 1")?,
        ),
    };
    let reviewed = matches!(decision, "Confirmed" | "Corrected");
    if reviewed
        && [category, kind, corrected_label, corrected_symbol]
            .into_iter()
            .all(|v| v.is_none())
        && review.is_none()
    {
        return Err("reviewed action has no corrected output".into());
    }
    let category = category.or(a.enumeration("originalCategory", CATEGORIES)?);
    let kind = kind.or(a.enumeration("originalKind", KINDS)?);
    let output_label = corrected_label.or(a.text("originalLabel")?);
    let symbol = corrected_symbol.or(a.text("originalSymbolName")?);
    let signature = a.text("signature")?;
    let tags = a.list("detectedTags")?;
    let layers = a.list("sourceLayers")?;
    let occurrences = a.list("candidateIds")?.len();
    a.list("sourcePrimitiveIds")?;
    let source_evidence = a.list("evidence")?;
    let reviewer = a.text("reviewer")?;
    let time = a.text("reviewedAt")?.map(timestamp).transpose()?;
    let mut pages = BTreeSet::new();
    let raw = a.get("pageNumbers");
    if !raw.is_null() {
        for page in raw
            .as_array()
            .ok_or("pageNumbers must be an integer array")?
        {
            let n = page
                .as_i64()
                .and_then(|v| i32::try_from(v).ok())
                .ok_or("invalid page number")?;
            if n > 0 {
                pages.insert(n);
            }
        }
    }
    if !reviewed
        || decision == "Confirmed" && o.corrected_only
        || decision == "Corrected" && o.confirmed_only
    {
        return Ok(None);
    }
    let pattern = tag_pattern(tags.iter().copied());
    let layer = layers.first().copied();
    let (sig, symbol_pattern, layer_pattern, detected_pattern) = match scope {
        "MatchingSignature" if signature.is_some() => (signature, None, None, None),
        "MatchingSymbolAndLayer"
            if symbol
                .into_iter()
                .chain(layer)
                .any(|s| s.chars().any(|c| c != '*')) =>
        {
            (None, symbol, layer, None)
        }
        "MatchingDetectedTagPattern" if pattern.is_some() => (None, None, None, pattern.as_deref()),
        _ => return Ok(None),
    };
    let mut evidence = vec![
        format!("Created from object correction action {id}."),
        format!("Human decision: {decision}."),
    ];
    if occurrences > 0 {
        evidence.push(format!(
            "Correction applies to {occurrences} reviewed occurrence{} in the source plan.",
            plural(occurrences)
        ))
    }
    if !pages.is_empty() {
        evidence.push(format!(
            "Reviewed occurrence pages: {}.",
            pages
                .iter()
                .map(i32::to_string)
                .collect::<Vec<_>>()
                .join(", ")
        ))
    }
    if !tags.is_empty() {
        evidence.push(format!("Reviewed occurrence tags: {}.", tags.join(", ")))
    }
    if let Some(pattern) = detected_pattern {
        evidence.push(format!(
            "Correction applies by detected tag pattern {pattern}."
        ))
    }
    evidence.extend(source_evidence.into_iter().map(str::to_owned));
    if let Some(reviewer) = reviewer {
        evidence.push(format!("Reviewer: {reviewer}."))
    }
    if let Some(time) = time {
        evidence.push(format!("Reviewed at {time}."))
    }
    Ok(Some((
        signature,
        id,
        rule(
            json!({"signature":sig,"symbolNamePattern":symbol_pattern,"layerPattern":layer_pattern,
        "detectedTagPattern":detected_pattern,"category":category,"kind":kind,"label":output_label,"symbolName":symbol,
        "requiresReview":review.unwrap_or(false),"confidence":confidence.map(rounded),"evidence":evidence}),
        ),
    )))
}
