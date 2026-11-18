use super::*;
use std::collections::{BTreeMap, BTreeSet, HashMap};

#[derive(Default)]
struct Counts(BTreeMap<String, (String, u64)>);
impl Counts {
    fn add(&mut self, name: &str, n: u64) {
        self.0
            .entry(name.to_lowercase())
            .or_insert_with(|| (name.to_owned(), 0))
            .1 += n;
    }
    fn ranked(&self) -> Vec<&(String, u64)> {
        let mut items = self.0.values().collect::<Vec<_>>();
        items.sort_by(|a, b| {
            b.1.cmp(&a.1)
                .then_with(|| a.0.to_lowercase().cmp(&b.0.to_lowercase()))
        });
        items
    }
    fn best(&self) -> Option<&str> {
        self.ranked().first().map(|(s, _)| s.as_str())
    }
    fn display(&self) -> String {
        self.ranked()
            .iter()
            .map(|(s, n)| format!("{s}:{n}"))
            .collect::<Vec<_>>()
            .join(", ")
    }
    fn provenance(&mut self, rows: &Value, fallback: Option<&str>) {
        let mut present = false;
        for row in rows.as_array().into_iter().flatten() {
            if let (Some(s), Some(n)) = (
                label(&row["value"]),
                row["count"]
                    .as_u64()
                    .filter(|v| *v > 0 && *v <= i32::MAX as u64),
            ) {
                self.add(s, n);
                present = true;
            }
        }
        if !present && let Some(f) = fallback {
            self.add(f, 1)
        }
    }
}
#[derive(Default)]
struct Distinct {
    values: Vec<String>,
    seen: HashSet<String>,
}
impl Distinct {
    fn extend(&mut self, values: &Value) {
        for value in values.as_array().into_iter().flatten().filter_map(label) {
            if self.seen.insert(value.to_lowercase()) {
                self.values.push(value.to_owned())
            }
        }
    }
    fn append_evidence(
        &self,
        evidence: &mut Vec<String>,
        heading: &str,
        limit: usize,
        sorted: bool,
    ) {
        if self.values.is_empty() {
            return;
        }
        let mut values = self.values.iter().map(String::as_str).collect::<Vec<_>>();
        if sorted {
            values.sort_by_cached_key(|v| v.to_lowercase())
        }
        let ellipsis = if values.len() > limit { ", ..." } else { "" };
        values.truncate(limit);
        evidence.push(format!("{heading}: {}{ellipsis}.", values.join(", ")));
    }
}
fn field<'a>(v: &Value, key: &str, names: &'a [&str]) -> &'a str {
    label(&v[key])
        .and_then(|s| canon(s, names))
        .unwrap_or("Unknown")
}
fn confidence(v: &Value) -> f64 {
    v.as_f64().unwrap_or(0.).clamp(0., 1.)
}
fn signature_key(s: &str) -> bool {
    let s = s.to_lowercase();
    s.contains("|kind:")
        || s.contains("|layers:")
        || s.starts_with("symbol:")
        || s.starts_with("geometry:")
}
fn selector(v: &Value) -> Option<(String, bool)> {
    if let Some(s) =
        label(&v["groupSignature"]).or_else(|| label(&v["reviewKey"]).filter(|s| signature_key(s)))
    {
        Some((s.to_owned(), true))
    } else {
        tag_pattern(labels(&v["detectedTags"], true)).map(|s| (s, false))
    }
}
struct Classification {
    label: String,
    category: String,
    confidence: f64,
    model: String,
    version: String,
}
#[derive(Default)]
struct Group {
    entries: usize,
    primitive_count: usize,
    pages: BTreeSet<i32>,
    tags: Distinct,
    layers: Distinct,
    blocks: Distinct,
    priorities: Counts,
    uses: Counts,
    sources: Counts,
    wall_kinds: Counts,
    categories: Counts,
    kinds: Counts,
    labels: Counts,
    symbols: Counts,
    total_confidence: f64,
    confidence_count: usize,
    best: Option<Classification>,
}
impl Group {
    fn add(&mut self, v: &Value) {
        self.entries += 1;
        self.primitive_count += labels(&v["sourcePrimitiveIds"], true).len();
        if let Some(page) = v["pageNumber"]
            .as_i64()
            .and_then(|n| i32::try_from(n).ok())
            .filter(|n| *n > 0)
        {
            self.pages.insert(page);
        }
        self.tags.extend(&v["detectedTags"]);
        self.layers.extend(&v["sourceEvidence"]["layers"]);
        self.blocks.extend(&v["sourceEvidence"]["blockNames"]);
        self.priorities
            .add(label(&v["reviewPriority"]).unwrap_or("Medium"), 1);
        self.uses.add(
            label(&v["suggestedTrainingUse"]).unwrap_or("classification-training-candidate"),
            1,
        );
        self.sources.provenance(
            &v["sourceKindCounts"],
            Some(field(v, "sourceKind", SOURCES)),
        );
        self.wall_kinds.provenance(
            &v["sourceWallComponentKindCounts"],
            label(&v["sourceWallComponentKind"]),
        );
        let category = field(v, "category", CATEGORIES);
        if category != "Unknown" {
            self.categories.add(category, 1)
        }
        let kind = field(v, "candidateKind", KINDS);
        if kind != "Unknown" {
            self.kinds.add(kind, 1)
        }
        if let Some(label) = label(&v["label"]) {
            self.labels.add(label, 1)
        }
        if let Some(symbol) = label(&v["symbolName"]) {
            self.symbols.add(symbol, 1)
        }
        if v["schemaVersion"] != openplantrace_rs::visual::dataset::SCHEMA
            || v["deterministicConfidence"].is_number()
        {
            self.total_confidence += confidence(&v["deterministicConfidence"]);
            self.confidence_count += 1;
        }
        let c = &v["classification"];
        let score = confidence(&c["confidence"]);
        if c.is_object()
            && (v["schemaVersion"] != openplantrace_rs::visual::dataset::SCHEMA
                || c["accepted"] == true)
            && self
                .best
                .as_ref()
                .is_none_or(|best| score > best.confidence)
        {
            self.best = Some(Classification {
                label: label(&c["label"]).unwrap_or("unknown").into(),
                category: field(c, "category", CATEGORIES).into(),
                confidence: score,
                model: label(&c["modelName"]).unwrap_or("unknown-model").into(),
                version: label(&c["modelVersion"])
                    .unwrap_or("unknown-version")
                    .into(),
            });
        }
    }
    fn rule(&self, key: &str, signature: bool) -> Value {
        let mut evidence=vec![
            format!("Drafted from Kvemo crop manifest review key '{key}'."),
            format!("{} crop{} share this selector.",self.entries,plural(self.entries)),
            format!("Training use: {}.",self.uses.display()),
            format!("Review priority: {}.",self.priorities.display()),
            "Edit this rule's label/category/requiresReview fields before using it as confirmed knowledge.".into()
        ];
        if !self.pages.is_empty() {
            evidence.push(format!(
                "Pages {}.",
                self.pages
                    .iter()
                    .map(i32::to_string)
                    .collect::<Vec<_>>()
                    .join(", ")
            ))
        }
        if self.primitive_count > 0 {
            evidence.push(format!(
                "{} source primitive{} referenced by crops.",
                self.primitive_count,
                plural(self.primitive_count)
            ))
        }
        if !self.sources.0.is_empty() {
            evidence.push(format!("Source kinds: {}.", self.sources.display()))
        }
        if !self.wall_kinds.0.is_empty() {
            evidence.push(format!(
                "Wall component source kinds: {}.",
                self.wall_kinds.display()
            ))
        }
        self.tags
            .append_evidence(&mut evidence, "Detected tags", 12, false);
        self.layers
            .append_evidence(&mut evidence, "Source layers", 8, true);
        self.blocks
            .append_evidence(&mut evidence, "Block names", 8, true);
        if let Some(c) = &self.best {
            let n = (c.confidence * 1000.).round() / 1000.;
            evidence.push(format!(
                "Kvemo model candidate: {} ({n}) using {} {}.",
                c.label, c.model, c.version
            ));
        }
        let tag = tag_pattern(self.tags.values.iter().map(String::as_str));
        rule(
            json!({"signature":signature.then_some(key),"detectedTagPattern":tag,
            "category":self.best.as_ref().map(|c|c.category.as_str()).or_else(||self.categories.best()).unwrap_or("Unknown"),
            "kind":self.kinds.best().unwrap_or("Unknown"),"label":self.best.as_ref().map(|c|c.label.as_str()).or_else(||self.labels.best()),
            "symbolName":self.symbols.best(),"requiresReview":true,
            "confidence":self.best.as_ref().map(|c|rounded(c.confidence)).or_else(||(self.confidence_count>0).then(||rounded(self.total_confidence/self.confidence_count as f64))),
            "evidence":evidence}),
        )
    }
}

type DraftResult = Result<(Value, usize), Vec<String>>;
pub(super) fn profile(o: &Options) -> Result<DraftResult, String> {
    let mut keys = HashMap::new();
    // Group order follows the first occurrence, including ties on case-insensitive rule names.
    let mut groups = Vec::<(String, bool, Group)>::new();
    let mut errors = Vec::new();
    let mut skipped = 0;
    crop_lines(&o.input, |number, record| {
        let record = record.and_then(|v| {
            if v["classification"].is_null() || v["classification"].is_object() {
                Ok(v)
            } else {
                Err("classification must be an object".into())
            }
        });
        let v = match record {
            Ok(v) => v,
            Err(err) => {
                if errors.len() < 10 {
                    errors.push(format!("line {number}: {err}"))
                }
                return;
            }
        };
        let classified = !v["classification"].is_null()
            && (v["schemaVersion"] != openplantrace_rs::visual::dataset::SCHEMA
                || v["classification"]["accepted"] == true);
        let negative = label(&v["suggestedTrainingUse"])
            .is_some_and(|u| u.eq_ignore_ascii_case("hard-negative-review"));
        if (classified && o.crop_only)
            || (!classified && o.classified_only)
            || (negative && !o.negatives)
        {
            skipped += 1;
            return;
        }
        let Some((key, is_signature)) = selector(&v) else {
            skipped += 1;
            return;
        };
        // A literal signature can spell the same text as a tag glob. Combining their
        // evidence can erase the tag selector and turn a draft into a global rule.
        let i = *keys.entry((key.clone(), is_signature)).or_insert_with(|| {
            groups.push((key, is_signature, Group::default()));
            groups.len() - 1
        });
        groups[i].2.add(&v);
    })?;
    if !errors.is_empty() {
        return Ok(Err(errors));
    }
    let mut rules = groups
        .into_iter()
        .map(|(key, sig, group)| group.rule(&key, sig))
        .collect::<Vec<_>>();
    rules.sort_by_cached_key(|r| {
        (
            std::cmp::Reverse(r["evidence"].as_array().unwrap().len()),
            label(&r["signature"])
                .or_else(|| label(&r["detectedTagPattern"]))
                .unwrap_or("")
                .to_lowercase(),
        )
    });
    if let Some(cap) = o.max_rules {
        rules.truncate(cap)
    }
    let default_name = format!(
        "{} object label draft",
        o.input
            .file_stem()
            .and_then(|s| s.to_str())
            .and_then(cleaned)
            .unwrap_or("Kvemo crops")
    );
    Ok(Ok((
        json!({"schemaVersion":SCHEMA,"name":o.name.as_deref().unwrap_or(&default_name),"version":o.version.as_deref().unwrap_or("draft"),"rules":rules}),
        skipped,
    )))
}
