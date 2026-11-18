//! Apply explicit review profiles to existing object candidates. Every selector is
//! evaluated against the original sources, before any label output is published.
mod contract;
mod source;
use crate::{
    layers::matches_pattern,
    model::{ObjectGroup, Scan},
};
pub use contract::{CATEGORIES, KINDS, Profile, Rule, SCHEMA};
use serde::{Deserialize, Serialize};
pub use source::{Extent, Member, Tag};
use std::collections::BTreeMap;

/// Source bounds, labels, and tags used by classification and crop review.
pub(crate) fn source_members(page: &crate::model::PageScan) -> Result<Vec<Member>, String> {
    source::page_members(page, &mut 32_000_000)
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Application {
    pub source_format: String,
    pub profiles: Vec<Profile>,
    pub groups: Vec<LabeledGroup>,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LabeledGroup {
    pub signature: String,
    pub profile_index: usize,
    pub rule_index: usize,
    pub members: Vec<Member>,
    pub category: String,
    pub kind: String,
    pub label: Option<String>,
    pub symbol_name: Option<String>,
    pub requires_review: bool,
    pub confidence: Option<f64>,
    pub evidence: Vec<String>,
}
struct Edit {
    name: String,
    category: String,
    review: bool,
    signature: Option<String>,
}
struct Plan {
    groups: Vec<LabeledGroup>,
    edits: Vec<Vec<Edit>>,
    page_groups: Vec<Vec<ObjectGroup>>,
}
fn charge(work: &mut usize, count: usize) -> Result<(), String> {
    *work = work
        .checked_sub(count)
        .ok_or("object label comparison limit exceeded")?;
    Ok(())
}
fn matches(rule: &Rule, sig: &str, fmt: &str, members: &[&Member]) -> bool {
    if rule.signature.as_ref().is_some_and(|v| v != sig)
        || rule
            .source_format
            .as_ref()
            .is_some_and(|v| !v.eq_ignore_ascii_case(fmt))
    {
        return false;
    }
    rule.match_category
        .as_ref()
        .is_none_or(|v| members.iter().any(|m| m.category == *v))
        && rule
            .match_kind
            .as_ref()
            .is_none_or(|v| members.iter().any(|m| m.kind == *v))
        && rule.symbol_name_pattern.as_ref().is_none_or(|p| {
            members.iter().any(|m| {
                m.symbol_name
                    .as_deref()
                    .is_some_and(|v| matches_pattern(v, p))
            })
        })
        && rule.label_pattern.as_ref().is_none_or(|p| {
            members
                .iter()
                .any(|m| m.label.as_deref().is_some_and(|v| matches_pattern(v, p)))
        })
        && rule.layer_pattern.as_ref().is_none_or(|p| {
            members
                .iter()
                .any(|m| m.source_layers.iter().any(|s| matches_pattern(s, p)))
        })
        && rule.detected_tag_pattern.as_ref().is_none_or(|p| {
            members
                .iter()
                .any(|m| m.tags.iter().any(|t| matches_pattern(&t.value, p)))
        })
}
fn compute(scan: &Scan, format: &str, profiles: &[Profile]) -> Result<Plan, String> {
    if !matches!(format, "PDF" | "DXF") {
        return Err("invalid object label source format".into());
    }
    for p in profiles {
        p.check()?
    }
    let mut work = 32_000_000;
    let members = scan
        .pages
        .iter()
        .map(|p| source::page_members(p, &mut work))
        .collect::<Result<Vec<_>, _>>()?;
    let mut edits = members
        .iter()
        .map(|page| {
            page.iter()
                .map(|m| Edit {
                    name: m.source_name.clone(),
                    category: m.source_category.clone(),
                    review: true,
                    signature: None,
                })
                .collect::<Vec<_>>()
        })
        .collect::<Vec<_>>();
    let mut inventory = BTreeMap::<String, Vec<(usize, usize)>>::new();
    let mut by_page = Vec::new();
    for (page, members) in members.iter().enumerate() {
        by_page.push(Vec::new());
        for (i, m) in members.iter().enumerate() {
            inventory.entry(m.signature()).or_default().push((page, i))
        }
    }
    let mut groups = Vec::new();
    for (signature, mut indices) in inventory {
        indices.sort_by(|(pa, a), (pb, b)| {
            let a = &members[*pa][*a];
            let b = &members[*pb][*b];
            a.page_number
                .cmp(&b.page_number)
                .then(a.bounds.min.y.total_cmp(&b.bounds.min.y))
                .then(a.bounds.min.x.total_cmp(&b.bounds.min.x))
        });
        let group: Vec<_> = indices.iter().map(|&(pg, i)| &members[pg][i]).collect();
        let mut selected = None;
        'profiles: for (pi, profile) in profiles.iter().enumerate() {
            for (ri, r) in profile.rules.iter().enumerate() {
                charge(&mut work, group.len())?;
                if matches(r, &signature, format, &group) {
                    selected = Some((pi, ri, r));
                    break 'profiles;
                }
            }
        }
        let Some((profile_index, rule_index, r)) = selected else {
            continue;
        };
        let first = group[0];
        let category = r
            .category
            .as_ref()
            .unwrap_or(if first.category == "Unknown" {
                &first.source_category
            } else {
                &first.category
            })
            .clone();
        let kind = r.kind.clone().unwrap_or_else(|| {
            r.category
                .as_deref()
                .map(source::kind)
                .unwrap_or(&first.kind)
                .into()
        });
        let label = r
            .label
            .clone()
            .or_else(|| group.iter().find_map(|m| m.label.clone()));
        let symbol_name = r
            .symbol_name
            .clone()
            .or_else(|| group.iter().find_map(|m| m.symbol_name.clone()));
        let requires_review = indices
            .iter()
            .any(|&(pg, i)| scan.pages[pg].objects[i].geometry.is_some())
            || r.requires_review.unwrap_or_else(|| {
                matches!(
                    category.as_str(),
                    "GenericSymbol" | "Unknown" | "FurnishedSurface" | "Cabinet"
                )
            });
        let mut local = BTreeMap::<usize, Vec<String>>::new();
        for &(pg, i) in &indices {
            let m = &members[pg][i];
            edits[pg][i] = Edit {
                name: label
                    .as_ref()
                    .or(symbol_name.as_ref())
                    .unwrap_or(&m.source_name)
                    .clone(),
                category: category.clone(),
                review: requires_review,
                signature: Some(signature.clone()),
            };
            local.entry(pg).or_default().push(m.object_id.clone());
        }
        for (pg, ids) in local {
            by_page[pg].push(ObjectGroup {
                profile_signature: Some(signature.clone()),
                name: label
                    .as_ref()
                    .or(symbol_name.as_ref())
                    .unwrap_or(&first.source_name)
                    .clone(),
                member_ids: ids,
                requires_review,
            });
        }
        groups.push(LabeledGroup {
            signature,
            profile_index,
            rule_index,
            members: group.into_iter().cloned().collect(),
            category,
            kind,
            label,
            symbol_name,
            requires_review,
            confidence: r.confidence,
            evidence: r.evidence.clone(),
        });
    }
    for (pg, page) in members.iter().enumerate() {
        // Keep the native repeated-block groups for candidates untouched by any rule.
        let mut repeats = BTreeMap::<String, Vec<String>>::new();
        for (m, e) in page.iter().zip(&edits[pg]) {
            if e.signature.is_none()
                && let Some(s) = &m.symbol_name
            {
                repeats
                    .entry(s.clone())
                    .or_default()
                    .push(m.object_id.clone())
            }
        }
        by_page[pg].extend(repeats.into_iter().filter(|(_, ids)| ids.len() >= 2).map(
            |(name, member_ids)| ObjectGroup {
                name,
                member_ids,
                requires_review: true,
                profile_signature: None,
            },
        ));
    }
    Ok(Plan {
        groups,
        edits,
        page_groups: by_page,
    })
}
/// All fallible work completes before changing the scan, including on reapplication.
pub fn apply(scan: &mut Scan, format: &str, profiles: &[Profile]) -> Result<(), String> {
    if profiles.is_empty() && scan.object_label_profile.is_none() {
        return Ok(());
    }
    let plan = compute(scan, format, profiles)?;
    for ((page, edits), groups) in scan.pages.iter_mut().zip(plan.edits).zip(plan.page_groups) {
        for (object, e) in page.objects.iter_mut().zip(edits) {
            object.name = e.name;
            object.category = e.category;
            object.requires_review = e.review;
            object.profile_signature = e.signature;
        }
        page.object_groups = groups;
    }
    scan.object_label_profile = (!profiles.is_empty()).then(|| Application {
        source_format: format.into(),
        profiles: profiles.to_vec(),
        groups: plan.groups,
    });
    Ok(())
}
pub(crate) fn validate(scan: &Scan) -> Result<(), String> {
    let Some(a) = &scan.object_label_profile else {
        if scan.pages.iter().any(|p| {
            p.objects.iter().any(|o| o.profile_signature.is_some())
                || p.object_groups
                    .iter()
                    .any(|g| g.profile_signature.is_some())
        }) {
            return Err("object label has no source profile".into());
        }
        return Ok(());
    };
    if a.profiles.is_empty()
        || scan
            .layer_profile
            .as_ref()
            .is_some_and(|p| p.source_format != a.source_format)
    {
        return Err("invalid object label profile origin".into());
    }
    if scan.pages.iter().flat_map(|p| &p.primitives).any(|p| {
        p.id.starts_with("dxf:") && a.source_format != "DXF"
            || p.id.starts_with("pdf:") && a.source_format != "PDF"
    }) {
        return Err("object label source format disagrees with source primitives".into());
    }
    let plan = compute(scan, &a.source_format, &a.profiles)?;
    if a.groups != plan.groups {
        return Err("object profile group evidence differs from source".into());
    }
    for ((p, edits), groups) in scan.pages.iter().zip(plan.edits).zip(plan.page_groups) {
        if p.object_groups != groups {
            return Err("profile group membership differs from drawing".into());
        }
        for (o, e) in p.objects.iter().zip(edits) {
            if o.name != e.name
                || o.category != e.category
                || o.requires_review != e.review
                || o.profile_signature != e.signature
            {
                return Err(format!(
                    "profile label differs from source rule for {}",
                    o.id
                ));
            }
        }
    }
    Ok(())
}
