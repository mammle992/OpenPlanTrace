use super::*;
use crate::model::{LayerKind, Object, PageScan, Point, Primitive, Shape};
use std::collections::{BTreeSet, HashMap, HashSet};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Extent {
    pub min: Point,
    pub max: Point,
}
impl Extent {
    fn point(p: Point) -> Self {
        Self { min: p, max: p }
    }
    fn add(&mut self, p: Point) {
        self.min.x = self.min.x.min(p.x);
        self.min.y = self.min.y.min(p.y);
        self.max.x = self.max.x.max(p.x);
        self.max.y = self.max.y.max(p.y)
    }
    fn distance(&self, p: Point) -> f64 {
        let x = p.x.clamp(self.min.x, self.max.x);
        let y = p.y.clamp(self.min.y, self.max.y);
        p.distance(Point::new(x, y))
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Tag {
    pub value: String,
    pub source_id: String,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Member {
    pub page_number: u32,
    pub object_id: String,
    pub source_ids: Vec<String>,
    pub bounds: Extent,
    pub source_name: String,
    pub source_category: String,
    pub source_requires_review: bool,
    pub category: String,
    pub kind: String,
    pub label: Option<String>,
    pub symbol_name: Option<String>,
    pub source_layers: Vec<String>,
    pub tags: Vec<Tag>,
}
fn canonical(s: &str) -> String {
    s.trim().to_lowercase().replace(['\\', '/', '.', ' '], "-")
}
pub(super) fn kind(category: &str) -> &str {
    match category {
        "Fixture" | "PlumbingFixture" => "Fixture",
        "Furniture" => "Furniture",
        "Vehicle" => "Vehicle",
        "Stair" => "Stair",
        "Unknown" => "Unknown",
        _ => "Symbol",
    }
}
impl Member {
    pub fn signature(&self) -> String {
        let layers: BTreeSet<_> = self.source_layers.iter().map(|s| canonical(s)).collect();
        let layer = if layers.is_empty() {
            "unlayered".into()
        } else {
            layers.into_iter().collect::<Vec<_>>().join("+")
        };
        let key = if let Some(s) = &self.symbol_name {
            format!("symbol:{}", canonical(s))
        } else if let Some(s) = &self.label {
            format!("label:{}", canonical(s))
        } else {
            let bucket = |d: f64| ((d / 5.).round_ties_even() * 5.).max(1.) as i64;
            format!(
                "geometry:{}x{}",
                bucket(self.bounds.max.x - self.bounds.min.x),
                bucket(self.bounds.max.y - self.bounds.min.y)
            )
        };
        format!(
            "{key}|category:{}|kind:{}|layers:{layer}",
            self.category, self.kind
        )
    }
}
fn source(
    o: &Object,
    page: &PageScan,
    by_id: &HashMap<&str, &Primitive>,
    furniture: &HashMap<&str, (&str, &str)>,
) -> Result<Member, String> {
    let mut bounds: Option<Extent> = None;
    let mut layers = BTreeSet::new();
    for id in &o.source_ids {
        let p = *by_id
            .get(id.as_str())
            .ok_or("object lacks source primitive")?;
        if let Some(s) = p.layer.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
            layers.insert(s.to_owned());
        }
        if let Some(b) = crate::semantics::bounds(&p.shape) {
            let extent = bounds.get_or_insert_with(|| Extent::point(b.min));
            extent.add(b.min);
            extent.add(b.max)
        }
    }
    let mut bounds = bounds.ok_or("object profile needs measured source geometry")?;
    let (raw_category, name, symbol) = if let Some(measured) = &o.geometry {
        bounds = Extent::point(
            measured
                .polygon
                .first()
                .copied()
                .ok_or("empty object geometry")?,
        );
        for &p in &measured.polygon {
            bounds.add(p);
        }
        let (category, name) = measured.evidence.category_and_name();
        (category, name, None)
    } else if let Some(f) = &o.footprint {
        let (category, name) = *furniture
            .get(f.selection_id.as_str())
            .ok_or("object profile furniture lacks selection")?;
        bounds = Extent::point(f.polygon[0]);
        for &p in &f.polygon {
            bounds.add(p)
        }
        (category, name, None)
    } else if let [id] = o.source_ids.as_slice() {
        let p = by_id[id.as_str()];
        match &p.shape {
            Shape::Insert { name, at } if *at == o.at => (
                crate::objects::source_category(p),
                name.as_str(),
                Some(name.clone()),
            ),
            Shape::Polyline { closed: true, .. } if p.layer_kind() == LayerKind::Object => (
                crate::objects::source_category(p),
                p.layer.as_deref().unwrap_or("").trim(),
                None,
            ),
            _ => return Err(format!("object {} has no native candidate source", o.id)),
        }
    } else if o.source_ids.len() > 1 {
        ("GenericSymbol", "Unclassified vector symbol", None)
    } else {
        return Err("object has no candidate sources".into());
    };
    let category = if CATEGORIES.contains(&raw_category) {
        raw_category
    } else {
        "Unknown"
    };
    Ok(Member {
        page_number: page.page_number,
        object_id: o.id.clone(),
        source_ids: o.source_ids.clone(),
        bounds,
        source_name: name.into(),
        source_category: raw_category.into(),
        source_requires_review: true,
        category: category.into(),
        kind: kind(category).into(),
        label: symbol.clone(),
        symbol_name: symbol,
        source_layers: layers.into_iter().collect(),
        tags: vec![],
    })
}
fn tag_category(prefix: &str) -> Option<&'static str> {
    match prefix {
        "P" | "PU" | "V" | "HV" | "XV" | "CV" | "SV" | "PSV" | "TK" | "T" | "VES" | "HX" | "HE"
        | "PMP" | "CMP" => Some("Equipment"),
        "AHU" | "VAV" | "FCU" | "RTU" | "EF" | "SF" | "RF" | "FAN" => Some("HVACEquipment"),
        "DB" | "MCC" | "MSB" | "LP" | "PP" => Some("ElectricalDevice"),
        "FA" | "FACP" | "SP" => Some("FireSafety"),
        _ => None,
    }
}
fn industrial_tag(text: &str) -> Option<String> {
    let t = text.trim().to_ascii_uppercase().replace('_', "-");
    let prefix_end = t.bytes().take_while(u8::is_ascii_alphabetic).count();
    let (prefix, tail) = t.split_at(prefix_end);
    tag_category(prefix)?;
    let (suffix, separated) = tail.strip_prefix('-').map_or((tail, false), |v| (v, true));
    let digits = suffix.bytes().filter(u8::is_ascii_digit).count();
    (digits > 0
        && (separated
            || (suffix.as_bytes().first().is_some_and(u8::is_ascii_digit)
                && (prefix.len() > 1 || digits >= 2)))
        && (1..=8).contains(&suffix.len())
        && suffix.bytes().all(|c| c.is_ascii_alphanumeric()))
    .then_some(t)
}

pub(super) fn page_members(page: &PageScan, work: &mut usize) -> Result<Vec<Member>, String> {
    if page.objects.is_empty() {
        return Ok(vec![]);
    }
    let by_id = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let furniture = page
        .withheld_walls
        .iter()
        .flatten()
        .filter_map(|w| crate::objects::furniture_kind(w.reason).map(|k| (w.wall.id.as_str(), k)))
        .collect();
    let mut members = page
        .objects
        .iter()
        .map(|o| source(o, page, &by_id, &furniture))
        .collect::<Result<Vec<_>, _>>()?;
    let inserts: HashMap<_, _> = members
        .iter()
        .enumerate()
        .filter(|(_, m)| m.symbol_name.is_some())
        .map(|(i, m)| (m.source_ids[0].clone(), i))
        .collect();
    for m in &mut members {
        if let Some(tag) = m.symbol_name.as_deref().and_then(industrial_tag) {
            m.tags.push(Tag {
                value: tag,
                source_id: m.source_ids[0].clone(),
            })
        }
    }
    let room_text: HashSet<_> = page
        .room_annotations
        .iter()
        .filter_map(|a| a.label_source_id.as_deref())
        .chain(
            page.rooms
                .iter()
                .filter_map(|r| r.label_source_id.as_deref()),
        )
        .collect();
    let mut free_tags = vec![];
    for p in &page.primitives {
        let mut owner = None;
        let mut id = p.id.as_str();
        // DXF expansion parents carry the exact INSERT id preceding their array row/column.
        while let Some((parent, part)) = id.rsplit_once(":r") {
            let is_child = part
                .split_once(":entity:")
                .and_then(|(rc, _)| rc.split_once('c'))
                .is_some_and(|(r, c)| {
                    !r.is_empty()
                        && !c.is_empty()
                        && r.bytes().chain(c.bytes()).all(|b| b.is_ascii_digit())
                });
            if !is_child {
                break;
            }
            if let Some(&i) = inserts.get(parent) {
                owner.get_or_insert(i);
                if !matches!(p.shape, Shape::Text { .. })
                    && let Some(b) = crate::semantics::bounds(&p.shape)
                {
                    members[i].bounds.add(b.min);
                    members[i].bounds.add(b.max);
                }
            }
            id = parent;
        }
        if let Shape::Text { at, value, height } = &p.shape
            && !room_text.contains(p.id.as_str())
            && !p
                .layer_name()
                .split(|c: char| !c.is_ascii_alphanumeric())
                .any(|word| {
                    ["NOTE", "NOTES", "TITLE", "SCHEDULE"]
                        .iter()
                        .any(|s| word.eq_ignore_ascii_case(s))
                })
            && !matches!(
                p.layer_kind(),
                LayerKind::Room | LayerKind::Grid | LayerKind::Dimension
            )
            && let Some(value) = industrial_tag(value)
        {
            let tag = Tag {
                value,
                source_id: p.id.clone(),
            };
            if let Some(i) = owner {
                members[i].tags.push(tag)
            } else {
                free_tags.push((*at, *height, tag))
            }
        }
    }
    for (at, height, tag) in free_tags {
        charge(work, members.len())?;
        let mut best = None;
        let mut best_distance = f64::INFINITY;
        let mut tied = false;
        for (i, m) in members.iter().enumerate() {
            let distance = m.bounds.distance(at);
            let limit = (height * 3.)
                .max(m.bounds.min.distance(m.bounds.max) * 0.5)
                .min(90.);
            if distance > limit {
                continue;
            }
            if distance + 1e-8 < best_distance {
                best = Some(i);
                best_distance = distance;
                tied = false
            } else if (distance - best_distance).abs() <= 1e-8 {
                tied = true
            }
        }
        if !tied && let Some(i) = best {
            members[i].tags.push(tag)
        }
    }
    for m in &mut members {
        if m.category != "GenericSymbol" {
            continue;
        }
        let mut categories = m.tags.iter().filter_map(|tag| {
            let end = tag
                .value
                .bytes()
                .take_while(u8::is_ascii_alphabetic)
                .count();
            tag_category(&tag.value[..end])
        });
        // A proved family on a generic symbol participates in the published
        // group signature. Preserve the detector's pre-tag category separately.
        if let Some(c) = categories.next()
            && categories.all(|other| other == c)
        {
            m.category = c.into();
            m.kind = kind(c).into();
        }
    }
    Ok(members)
}
