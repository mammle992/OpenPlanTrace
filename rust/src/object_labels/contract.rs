use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const SCHEMA: &str = "openplantrace.object-label-profile.v1";
pub const CATEGORIES: &[&str] = &[
    "Unknown",
    "TextLabel",
    "GenericSymbol",
    "Fixture",
    "Furniture",
    "Stair",
    "Elevator",
    "Column",
    "Shaft",
    "PlumbingFixture",
    "ElectricalDevice",
    "Lighting",
    "HVACEquipment",
    "FireSafety",
    "Equipment",
    "Vehicle",
    "Structural",
];
pub const KINDS: &[&str] = &[
    "Unknown",
    "Fixture",
    "Furniture",
    "Vehicle",
    "Symbol",
    "Stair",
    "TextLabel",
];
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Rule {
    pub signature: Option<String>,
    pub symbol_name_pattern: Option<String>,
    pub label_pattern: Option<String>,
    pub layer_pattern: Option<String>,
    pub detected_tag_pattern: Option<String>,
    pub source_format: Option<String>,
    pub match_category: Option<String>,
    pub match_kind: Option<String>,
    pub category: Option<String>,
    pub kind: Option<String>,
    pub label: Option<String>,
    pub symbol_name: Option<String>,
    pub requires_review: Option<bool>,
    pub confidence: Option<f64>,
    pub evidence: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Profile {
    pub schema_version: String,
    pub name: Option<String>,
    pub version: Option<String>,
    pub rules: Vec<Rule>,
}

fn known_keys(v: &mut Value, keys: &[&str], reject_unknown: bool) -> Result<(), String> {
    let obj = v.as_object_mut().ok_or("expected an object")?;
    for (key, mut value) in std::mem::take(obj) {
        let Some(canonical) = keys.iter().find(|known| known.eq_ignore_ascii_case(&key)) else {
            if reject_unknown {
                return Err(format!("unsupported object label rule field {key}"));
            }
            continue;
        };
        if let Value::String(s) = &value {
            let s = s.trim();
            value = if s.is_empty() {
                Value::Null
            } else {
                Value::String(s.into())
            };
        }
        if obj.insert((*canonical).into(), value).is_some() {
            return Err(format!("duplicate object label field {canonical}"));
        }
    }
    Ok(())
}
impl Profile {
    pub fn parse(mut v: Value) -> Result<Self, String> {
        known_keys(
            &mut v,
            &["schemaVersion", "name", "version", "rules"],
            false,
        )?;
        if v["schemaVersion"].is_null() {
            v["schemaVersion"] = SCHEMA.into()
        }
        let schema = v["schemaVersion"]
            .as_str()
            .ok_or("invalid object label schemaVersion")?;
        if !schema.eq_ignore_ascii_case(SCHEMA) {
            return Err(format!("unsupported object label profile: {schema}"));
        }
        v["schemaVersion"] = SCHEMA.into();
        if v["rules"].is_null() {
            v["rules"] = Value::Array(vec![])
        }
        for r in v["rules"].as_array_mut().ok_or("rules must be an array")? {
            known_keys(
                r,
                &[
                    "signature",
                    "symbolNamePattern",
                    "labelPattern",
                    "layerPattern",
                    "detectedTagPattern",
                    "sourceFormat",
                    "matchCategory",
                    "matchKind",
                    "category",
                    "kind",
                    "label",
                    "symbolName",
                    "requiresReview",
                    "confidence",
                    "evidence",
                ],
                true,
            )?;
            if r["evidence"].is_null() {
                r["evidence"] = Value::Array(vec![])
            }
            let evidence = r["evidence"]
                .as_array_mut()
                .ok_or("evidence must be a string array")?;
            let mut cleaned = Vec::new();
            for text in evidence.drain(..) {
                if text.is_null() {
                    continue;
                }
                let s = text.as_str().ok_or("invalid profile evidence")?.trim();
                if !s.is_empty() {
                    cleaned.push(s.into())
                }
            }
            *evidence = cleaned;
            for (key, values) in [
                ("category", CATEGORIES),
                ("matchCategory", CATEGORIES),
                ("kind", KINDS),
                ("matchKind", KINDS),
            ] {
                if !r[key].is_null() {
                    let name = r[key].as_str().ok_or_else(|| format!("invalid {key}"))?;
                    r[key] = (*values
                        .iter()
                        .find(|v| v.eq_ignore_ascii_case(name))
                        .ok_or_else(|| format!("unknown {key}: {name}"))?)
                    .into();
                }
            }
        }
        let profile: Self = serde_json::from_value(v).map_err(|e| e.to_string())?;
        profile.check()?;
        Ok(profile)
    }
    pub(super) fn check(&self) -> Result<(), String> {
        if self.schema_version != SCHEMA {
            return Err("incorrect object profile version".into());
        }
        for (i, r) in self.rules.iter().enumerate() {
            let scoped = [
                &r.signature,
                &r.symbol_name_pattern,
                &r.label_pattern,
                &r.layer_pattern,
                &r.detected_tag_pattern,
                &r.source_format,
                &r.match_category,
                &r.match_kind,
            ]
            .iter()
            .any(|v| {
                v.as_deref()
                    .is_some_and(|s| !s.trim_matches('*').trim().is_empty())
            });
            let output = [&r.category, &r.kind, &r.label, &r.symbol_name]
                .iter()
                .any(|s| s.as_deref().is_some_and(|s| !s.trim().is_empty()))
                || r.confidence.is_some()
                || r.requires_review.is_some()
                || !r.evidence.is_empty();
            if !scoped
                || !output
                || r.confidence
                    .is_some_and(|v| !v.is_finite() || !(0.0..=1.).contains(&v))
            {
                return Err(format!(
                    "object rule {} needs a scoped selector, a label output and valid confidence",
                    i + 1
                ));
            }
            for (value, choices) in [
                (&r.category, CATEGORIES),
                (&r.match_category, CATEGORIES),
                (&r.kind, KINDS),
                (&r.match_kind, KINDS),
            ] {
                if value.as_deref().is_some_and(|s| !choices.contains(&s)) {
                    return Err(format!("invalid profile enum in rule {}", i + 1));
                }
            }
        }
        Ok(())
    }
}
