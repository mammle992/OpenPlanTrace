//! Source-layer classification from original drawing entities, independent of wall selection.
use crate::model::{Primitive, Shape};
use crate::semantics::{Bounds, bounds};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};

macro_rules! categories {
    ($($name:ident),+) => {
        #[allow(clippy::upper_case_acronyms)]
        #[derive(Serialize, Deserialize, Clone, Copy, Debug, PartialEq, Eq)]
        pub enum LayerCategory { $($name),+ }
        impl LayerCategory {
            const ALL: &[Self] = &[$(Self::$name),+];
            pub fn as_str(self) -> &'static str { match self { $(Self::$name => stringify!($name)),+ } }
        }
    };
}
categories!(
    Unknown,
    Wall,
    Door,
    Window,
    Room,
    Dimension,
    Text,
    Grid,
    Structural,
    Equipment,
    Electrical,
    HVAC,
    Plumbing,
    FireSafety,
    Furniture,
    Fixture,
    SurfacePattern
);

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct LayerCategoryOverride {
    pub pattern: String,
    pub category: LayerCategory,
    pub source_format: Option<String>,
}
impl LayerCategoryOverride {
    pub fn from_parts(
        pattern: &str,
        category: &str,
        source_format: Option<&str>,
    ) -> Result<Self, String> {
        let pattern = pattern.trim();
        if pattern.is_empty() {
            return Err("layer override requires a nonempty pattern".into());
        }
        let category = LayerCategory::ALL
            .iter()
            .copied()
            .find(|c| c.as_str().eq_ignore_ascii_case(category.trim()))
            .ok_or_else(|| format!("invalid layer category '{category}'"))?;
        Ok(Self {
            pattern: pattern.into(),
            category,
            source_format: source_format
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .map(str::to_owned),
        })
    }
    fn matches(&self, name: &str, fmt: &str) -> bool {
        if self.pattern.trim().is_empty()
            || self
                .source_format
                .as_ref()
                .is_some_and(|s| !s.eq_ignore_ascii_case(fmt))
        {
            return false;
        }
        matches_pattern(name, &self.pattern)
    }
}
pub(crate) fn matches_pattern(name: &str, pattern: &str) -> bool {
    let name = name.to_lowercase();
    let pattern = pattern.to_lowercase();
    if !pattern.contains('*') {
        return name == pattern;
    }
    let mut rest = name.as_str();
    let mut parts = pattern.split('*').filter(|s| !s.is_empty()).peekable();
    if !pattern.starts_with('*') && parts.peek().is_some_and(|p| !name.starts_with(p)) {
        return false;
    }
    for part in parts {
        let Some((_, tail)) = rest.split_once(part) else {
            return false;
        };
        rest = tail;
    }
    pattern.ends_with('*') || name.ends_with(pattern.rsplit('*').next().unwrap_or_default())
}
pub fn first_override<'a>(
    rules: &'a [LayerCategoryOverride],
    layer_name: &str,
    fmt: &str,
) -> Option<&'a LayerCategoryOverride> {
    rules.iter().find(|rule| rule.matches(layer_name, fmt))
}

// This ordering is the published LayerCategory enum order and breaks equal-score ties.
const HINTS: &[(&str, &[&str])] = &[
    ("Wall", &["wall", "vegg", "mur", "partition"]),
    ("Door", &["door", "dor", "deur"]),
    ("Window", &["window", "vindu", "glass"]),
    ("Room", &["room", "space", "rom", "area"]),
    ("Dimension", &["dim", "maal", "measure"]),
    ("Text", &["text", "txt", "anno", "note"]),
    ("Grid", &["grid", "axis", "axes", "akse", "module"]),
    (
        "Structural",
        &[
            "struct", "beam", "column", "col", "baering", "steel", "concrete",
        ],
    ),
    (
        "Equipment",
        &["equip", "utstyr", "machine", "maskin", "process"],
    ),
    (
        "Electrical",
        &["elec", "power", "kraft", "lighting", "lys", "el-"],
    ),
    ("HVAC", &["hvac", "vent", "duct", "kanal", "air", "luft"]),
    (
        "Plumbing",
        &["plumb", "vvs", "pipe", "ror", "water", "drain"],
    ),
    (
        "FireSafety",
        &["fire", "brann", "sprinkler", "alarm", "escape", "evac"],
    ),
    (
        "Furniture",
        &["furn", "chair", "table", "sofa", "desk", "bed", "cabinet"],
    ),
    (
        "Fixture",
        &[
            "fixture",
            "casework",
            "kitchen",
            "counter",
            "appliance",
            "toilet",
            "wc",
            "sink",
            "shower",
            "bath",
        ],
    ),
    (
        "SurfacePattern",
        &["hatch", "pattern", "tile", "fill", "poche"],
    ),
];

/// An authored layer name alone can support placement only when its vocabulary
/// identifies a single role. Geometry-based layer guesses serve inspection.
pub(crate) fn is_named_wall(name: &str) -> bool {
    let lower = name.to_lowercase();
    let mut names = HINTS
        .iter()
        .filter(|(_, words)| words.iter().any(|word| lower.contains(word)));
    names
        .next()
        .is_some_and(|(category, _)| *category == "Wall")
        && names.next().is_none()
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LayerSummary {
    pub name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub source_format: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub override_pattern: Option<String>,
    pub likely_category: &'static str,
    pub confidence: f64,
    pub primitive_count: usize,
    pub page_numbers: BTreeSet<u32>,
    pub bounds: Option<Bounds>,
}

#[derive(Default)]
struct Observations {
    count: usize,
    texts: usize,
    linework: usize,
    arcs: usize,
    inserts: usize,
    dimension_texts: usize,
    dimensions: usize,
    length: f64,
    pages: BTreeSet<u32>,
    bounds: Option<Bounds>,
}
impl Observations {
    fn add(&mut self, page: u32, shape: &Shape) {
        self.pages.insert(page);
        self.count += 1;
        if let Some(extent) = bounds(shape) {
            if let Some(total) = &mut self.bounds {
                total.min.x = total.min.x.min(extent.min.x);
                total.min.y = total.min.y.min(extent.min.y);
                total.max.x = total.max.x.max(extent.max.x);
                total.max.y = total.max.y.max(extent.max.y);
            } else {
                self.bounds = Some(extent);
            }
        }
        match shape {
            Shape::Line { start, end } => {
                self.linework += 1;
                self.length += start.distance(*end);
            }
            Shape::Polyline { points, closed, .. } => {
                self.linework += 1;
                self.length += points.windows(2).map(|p| p[0].distance(p[1])).sum::<f64>();
                if *closed && points.len() > 1 {
                    self.length += points[0].distance(points[points.len() - 1]);
                }
            }
            Shape::Arc { radius, sweep, .. } => {
                self.linework += 1;
                self.arcs += 1;
                self.length += radius * sweep.abs();
            }
            Shape::Text { value, .. } => {
                self.texts += 1;
                let lower = value.to_ascii_lowercase();
                if value.chars().any(char::is_numeric)
                    && (value.contains(['\'', '"', 'm', 'x', 'X', ':'])
                        || lower.contains("mm")
                        || lower.contains("cm"))
                {
                    self.dimension_texts += 1;
                }
            }
            Shape::Insert { .. } => self.inserts += 1,
            Shape::Dimension { .. } => self.dimensions += 1,
            Shape::Image { .. } => {}
        }
    }
    fn classify(&self, name: &str) -> (&'static str, f64) {
        let normalized = name.to_lowercase().replace(['\\', '/', '.', ' '], "-");
        let mut top = ("Unknown", 0u16, false);
        for &(category, hints) in HINTS {
            let whole_token = normalized
                .split(|c: char| !c.is_alphanumeric())
                .any(|token| hints.contains(&token));
            let name_score = if hints.iter().any(|hint| normalized.contains(hint)) {
                72
            } else {
                0
            };
            let geometry_score = match category {
                "Text" if self.texts * 100 >= self.count * 65 => 28,
                "Wall" if self.linework * 100 >= self.count * 75 && self.length > 80. => 18,
                "Door" if self.arcs > 0 => 18,
                "Equipment" if self.inserts > 0 => 16,
                // A native DXF DIMENSION is direct evidence even without rendered dimension text.
                "Dimension" if self.dimensions > 0 => 72,
                "Dimension" => self.dimension_texts.min(3) as u16 * 12,
                _ => 0,
            };
            let score = name_score + geometry_score;
            // A-CHAIR matches both "chair" and the embedded HVAC hint "air".
            // On equal scores prefer an authored token, then the published category order.
            if (score, whole_token) > (top.1, top.2) {
                top = (category, score, whole_token);
            }
        }
        if top.1 < 24 {
            ("Unknown", 0.35)
        } else {
            (top.0, f64::from(top.1.min(95)) / 100.)
        }
    }
}

/// All pages in a loaded drawing share a single source format.
pub fn analyze<'a>(entities: impl IntoIterator<Item = (u32, &'a Primitive)>) -> Vec<LayerSummary> {
    analyze_with_profile("", &[], entities)
}
pub fn analyze_with_profile<'a>(
    source_format: &str,
    rules: &[LayerCategoryOverride],
    entities: impl IntoIterator<Item = (u32, &'a Primitive)>,
) -> Vec<LayerSummary> {
    let mut layers: BTreeMap<String, (&str, Observations)> = BTreeMap::new();
    for (page, entity) in entities {
        let name = entity
            .layer
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or("(unlayered)");
        let (_, observations) = layers
            .entry(name.to_lowercase())
            .or_insert_with(|| (name, Observations::default()));
        observations.add(page, &entity.shape);
    }
    layers
        .into_values()
        .map(|(name, o)| {
            let rule = first_override(rules, name, source_format);
            let (category, confidence) =
                rule.map_or_else(|| o.classify(name), |r| (r.category.as_str(), 0.90));
            LayerSummary {
                name: name.to_owned(),
                source_format: (!source_format.is_empty()).then(|| source_format.to_owned()),
                override_pattern: rule.map(|r| r.pattern.clone()),
                likely_category: category,
                confidence,
                primitive_count: o.count,
                page_numbers: o.pages,
                bounds: o.bounds,
            }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::Point;
    fn primitive(layer: Option<&str>, shape: Shape) -> Primitive {
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: "s".into(),
            layer: layer.map(str::to_owned),
            shape,
        }
    }
    #[test]
    fn classifies_authored_categories_and_preserves_weak_and_ambiguous_evidence() {
        let line = Shape::Line {
            start: Point::default(),
            end: Point::new(120., 30.),
        };
        let layers = [
            "M-VENTILATION",
            "A-HATCH",
            "A-CHAIR",
            "A-FIXTURE",
            "A-DOOR-WINDOW",
            "A-WALL",
            "P-WATER",
            "X-STEEL",
            "E-LIGHTING",
            "F-SPRINKLER",
            "UNSPECIFIED",
        ];
        let expected = [
            "HVAC",
            "SurfacePattern",
            "Furniture",
            "Fixture",
            "Door",
            "Wall",
            "Plumbing",
            "Structural",
            "Electrical",
            "FireSafety",
            "Unknown",
        ];
        for (name, category) in layers.into_iter().zip(expected) {
            let p = primitive(Some(name), line.clone());
            let result = analyze([(1, &p)]);
            assert_eq!(result[0].likely_category, category, "{name}");
        }
    }
    #[test]
    fn combines_case_variants_across_pages_and_uses_entity_composition() {
        let txt = |layer, value: &str| {
            primitive(
                layer,
                Shape::Text {
                    at: Point::new(1., 2.),
                    height: 1.,
                    value: value.into(),
                },
            )
        };
        let a = txt(Some(" X99 "), "250 mm");
        let b = txt(Some("x99"), "1:50");
        let c = txt(Some("x99"), "3 x 2");
        let d = txt(None, "NOTES");
        let found = analyze([(1, &a), (2, &b), (2, &c), (3, &d)]);
        assert_eq!(found.len(), 2);
        let named = &found[1];
        assert_eq!(named.name, "X99");
        assert_eq!(named.likely_category, "Dimension");
        assert_eq!(named.primitive_count, 3);
        assert_eq!(named.page_numbers, BTreeSet::from([1, 2]));
        assert_eq!(found[0].name, "(unlayered)");
        assert_eq!(found[0].likely_category, "Text");
    }
    #[test]
    fn first_matching_override_respects_format_and_wildcard_anchors() {
        let first =
            LayerCategoryOverride::from_parts(" XREF*-PlAn ", "wAlL", Some(" pdf ")).unwrap();
        let second = LayerCategoryOverride::from_parts("*Plan", "Grid", None).unwrap();
        let rules = [first, second];
        let p = primitive(
            Some("XRef-1-plan"),
            Shape::Line {
                start: Point::default(),
                end: Point::new(5., 0.),
            },
        );
        let pdf = analyze_with_profile("PDF", &rules, [(1, &p)]);
        assert_eq!(pdf[0].likely_category, "Wall");
        assert_eq!(pdf[0].confidence, 0.9);
        assert_eq!(pdf[0].override_pattern.as_deref(), Some("XREF*-PlAn"));
        let dxf = analyze_with_profile("dxf", &rules, [(1, &p)]);
        assert_eq!(dxf[0].likely_category, "Grid");
        assert!(first_override(&rules, "Xref-1-plan-other", "pdf").is_none());
        assert_eq!(
            first_override(&rules, "abcXref-1-Plan", "pdf")
                .unwrap()
                .category,
            LayerCategory::Grid
        );
        assert!(LayerCategoryOverride::from_parts(" * ", "invented", None).is_err());
        assert!(LayerCategoryOverride::from_parts(" ", "Wall", None).is_err());
    }
}
