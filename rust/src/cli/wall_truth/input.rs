use openplantrace_rs::model::{ArcGeometry, Point};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{BTreeMap, HashSet};

pub(super) const TRUTH_SCHEMA: &str = "openplantrace.wall-truth.v1";
pub(super) const GATES: &[(&str, &str, f64, bool)] = &[
    ("minRecall", "recall", 0.90, true),
    (
        "minLengthWeightedRecall",
        "lengthWeightedRecall",
        0.94,
        true,
    ),
    ("minMajorWallRecall", "majorWallRecall", 0.98, true),
    ("minExteriorWallRecall", "exteriorWallRecall", 0.96, true),
    ("minWallTypeAccuracy", "wallTypeAccuracy", 0.90, true),
    ("maxMeanLineDistance", "meanLineDistance", 4.0, false),
    (
        "maxMeanEndpointDistance",
        "meanEndpointDistance",
        8.0,
        false,
    ),
    (
        "maxNotWallViolationCount",
        "notWallViolationCount",
        0.,
        false,
    ),
    ("minPrecision", "precision", 0.85, true),
];

// The reference JSON reader accepts case-insensitive property names.
fn field<'a>(v: &'a Value, key: &str) -> &'a Value {
    v.get(key)
        .or_else(|| {
            v.as_object()?
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case(key))
                .map(|(_, v)| v)
        })
        .unwrap_or(&Value::Null)
}
fn number(v: &Value, key: &str, default: Option<f64>) -> Result<f64, String> {
    let x = field(v, key);
    if x.is_null() {
        return default.ok_or_else(|| format!("missing {key}"));
    }
    x.as_f64()
        .filter(|v| v.is_finite())
        .ok_or_else(|| format!("expected finite {key}"))
}
fn string(v: &Value, key: &str) -> Result<Option<String>, String> {
    match field(v, key) {
        Value::Null => Ok(None),
        Value::String(s) => Ok(Some(s.clone())),
        _ => Err(format!("expected text for {key}")),
    }
}
fn list<'a>(v: &'a Value, key: &str, required: bool) -> Result<&'a [Value], String> {
    match field(v, key) {
        Value::Array(a) => Ok(a),
        Value::Null if !required => Ok(&[]),
        _ => Err(format!("expected {key} array")),
    }
}
fn texts(v: &Value, key: &str) -> Result<Vec<String>, String> {
    list(v, key, false)?
        .iter()
        .map(|x| {
            x.as_str()
                .map(str::to_owned)
                .ok_or_else(|| format!("invalid text in {key}"))
        })
        .collect()
}
fn choice(
    v: &Value,
    key: &str,
    choices: &[&'static str],
    default: usize,
) -> Result<&'static str, String> {
    let x = field(v, key);
    let n = if x.is_null() {
        Some(default)
    } else if let Some(s) = x.as_str() {
        choices
            .iter()
            .position(|w| w.eq_ignore_ascii_case(s))
            .or_else(|| s.parse().ok())
    } else {
        x.as_u64().and_then(|n| usize::try_from(n).ok())
    };
    n.and_then(|i| choices.get(i))
        .copied()
        .ok_or_else(|| format!("unknown {key}"))
}
fn page_number(v: &Value) -> Result<u32, String> {
    field(v, "pageNumber")
        .as_u64()
        .and_then(|n| u32::try_from(n).ok())
        .filter(|n| *n > 0)
        .ok_or_else(|| "expected a positive pageNumber".into())
}
fn id(v: &Value) -> Result<String, String> {
    string(v, "id")?
        .filter(|s| !s.trim().is_empty())
        .ok_or_else(|| "expected non-empty id".into())
}
fn point(v: &Value) -> Result<Point, String> {
    Ok(Point::new(number(v, "x", None)?, number(v, "y", None)?))
}
#[derive(Clone, Copy, Serialize, Deserialize)]
pub(super) struct Line {
    pub start: Point,
    pub end: Point,
}
impl Line {
    fn read(v: &Value) -> Result<Self, String> {
        Ok(Self {
            start: point(field(v, "start"))?,
            end: point(field(v, "end"))?,
        })
    }
    pub fn length(self) -> f64 {
        self.start.distance(self.end)
    }
    pub fn at(self, t: f64) -> Point {
        self.start + (self.end - self.start) * t
    }
}
pub(super) struct Entry {
    pub id: String,
    pub page: u32,
    pub is_wall: bool,
    pub line: Option<Line>,
    pub bounds: Option<[f64; 4]>,
    pub wall_type: Option<&'static str>,
    pub importance: &'static str,
    pub line_tol: f64,
    pub end_tol: f64,
    pub angle_tol: f64,
    pub overlap: f64,
    pub evidence: Vec<String>,
}
impl Entry {
    fn read(v: &Value) -> Result<Self, String> {
        let is_wall = choice(v, "labelKind", &["Wall", "NotWall"], 0)? == "Wall";
        let shape = field(v, "centerLine");
        let line = if shape.is_null() {
            None
        } else {
            Some(Line::read(shape)?)
        };
        let rect = field(v, "bounds");
        let bounds = if rect.is_null() {
            None
        } else {
            Some([
                number(rect, "x", None)?,
                number(rect, "y", None)?,
                number(rect, "width", None)?,
                number(rect, "height", None)?,
            ])
        };
        if (is_wall && line.is_none_or(|l| l.length() <= 0.))
            || (!is_wall && line.is_none() && bounds.is_none_or(|b| b[2] <= 0. || b[3] <= 0.))
        {
            return Err("wall truth entry requires finite labeled geometry".into());
        }
        let entry = Self {
            id: id(v)?,
            page: page_number(v)?,
            is_wall,
            line,
            bounds,
            wall_type: if field(v, "wallType").is_null() {
                None
            } else {
                Some(choice(
                    v,
                    "wallType",
                    &["Unknown", "Exterior", "Interior"],
                    0,
                )?)
            },
            importance: choice(
                v,
                "importance",
                &["Minor", "Standard", "Major", "Critical"],
                1,
            )?,
            line_tol: number(v, "maxLineDistance", Some(4.))?,
            end_tol: number(v, "maxEndpointDistance", Some(8.))?,
            angle_tol: number(v, "maxAngularDifferenceDegrees", Some(4.))?,
            overlap: number(v, "minLengthOverlapRatio", Some(0.75))?,
            evidence: texts(v, "evidence")?,
        };
        if entry.line_tol <= 0.
            || entry.end_tol <= 0.
            || entry.angle_tol <= 0.
            || entry.overlap <= 0.
            || entry.overlap > 1.
            || number(v, "thicknessDrawingUnits", Some(1.))? <= 0.
        {
            return Err(format!(
                "invalid wall truth geometry tolerance on {}",
                entry.id
            ));
        }
        Ok(entry)
    }
}
type Frames = BTreeMap<u32, Option<[f64; 2]>>;
fn frames(v: &Value, optional: bool, native: bool) -> Result<Frames, String> {
    let mut out = Frames::new();
    for p in list(v, "pages", !optional)? {
        let n = page_number(p)?;
        let frame = if native && field(p, "width").is_null() && field(p, "height").is_null() {
            None
        } else {
            let wh = [number(p, "width", None)?, number(p, "height", None)?];
            if wh.iter().any(|x| *x <= 0.) {
                return Err("page dimensions must be positive".into());
            }
            Some(wh)
        };
        if out.insert(n, frame).is_some() {
            return Err("duplicate page number".into());
        }
    }
    Ok(out)
}
pub(super) struct Dataset {
    pub name: Option<String>,
    pub version: Option<String>,
    pub document_id: Option<String>,
    pub complete: bool,
    pub entries: Vec<Entry>,
    pub frames: Frames,
    pub gates: Vec<f64>,
}
impl Dataset {
    pub fn read(v: &Value) -> Result<Self, String> {
        if !v.is_object() {
            return Err("expected a wall truth object".into());
        }
        if string(v, "schemaVersion")?.is_some_and(|s| !s.eq_ignore_ascii_case(TRUTH_SCHEMA)) {
            return Err("unsupported wall truth schemaVersion".into());
        }
        for (key, expected) in [
            ("coordinateSpace", "OpenPlanTracePageCoordinates"),
            ("coordinateUnit", "drawing-unit"),
            ("coordinateOrigin", "TopLeft"),
            ("coordinateYAxisDirection", "Down"),
        ] {
            if string(v, key)?.is_some_and(|s| s != expected) {
                return Err(format!("expected {key} = {expected}"));
            }
        }
        let frames = frames(v, true, false)?;
        let entries = list(v, "entries", false)?
            .iter()
            .map(Entry::read)
            .collect::<Result<Vec<_>, _>>()?;
        let mut ids = HashSet::new();
        for e in &entries {
            if !ids.insert(&e.id) || (!frames.is_empty() && !frames.contains_key(&e.page)) {
                return Err(format!("duplicate truth id or undeclared page on {}", e.id));
            }
        }
        let mut gates = Vec::new();
        let quality = field(v, "qualityGate");
        if !quality.is_null() && !quality.is_object() {
            return Err("expected a qualityGate object".into());
        }
        for &(key, _, default, min) in GATES {
            let x = number(quality, key, Some(default))?;
            if x < 0.
                || (min && x > 1.)
                || (key == "maxNotWallViolationCount" && (x.fract() != 0. || x > i32::MAX as f64))
            {
                return Err(format!("invalid quality gate {key}"));
            }
            gates.push(x);
        }
        let complete = match field(v, "completeTruthSet") {
            Value::Null => false,
            Value::Bool(b) => *b,
            _ => return Err("expected boolean completeTruthSet".into()),
        };
        Ok(Self {
            name: string(v, "name")?,
            version: string(v, "version")?,
            document_id: string(v, "documentId")?,
            complete,
            entries,
            frames,
            gates,
        })
    }
}
pub(super) struct Run {
    pub id: String,
    pub page: u32,
    pub line: Line,
    pub curve: Option<ArcGeometry>,
    pub length: f64,
    pub wall_type: String,
    pub confidence: Option<f64>,
    pub evidence: Vec<String>,
}
pub(super) struct Structure {
    pub schema: String,
    pub native: bool,
    pub document_id: Option<String>,
    pub frames: Frames,
    pub runs: Vec<Run>,
    pub solver: Option<[f64; 3]>,
}
impl Structure {
    pub fn read(v: &Value) -> Result<Self, String> {
        let schema = string(v, "schemaVersion")?.ok_or("missing structure schemaVersion")?;
        let native = match schema.as_str() {
            "openplantrace.structure.v2" => false,
            "openplantrace.rust.structure.v1" | "openplantrace.rust.structure.v2" => true,
            _ => return Err(format!("unsupported structure {schema}")),
        };
        let frames = frames(v, false, native)?;
        let mut nodes = BTreeMap::new();
        if !native {
            for node in list(v, "nodes", true)? {
                let n = page_number(node)?;
                if !frames.contains_key(&n) || number(node, "degree", None)? < 1. {
                    return Err("invalid wall endpoint node".into());
                }
                if nodes
                    .insert(id(node)?, (n, point(field(node, "position"))?))
                    .is_some()
                {
                    return Err("duplicate structure node id".into());
                }
            }
        }
        let mut runs = Vec::new();
        let mut ids = HashSet::new();
        let mut add = |v: &Value, page: u32| -> Result<(), String> {
            let id = id(v)?;
            let line = Line::read(field(v, "centerLine"))?;
            if !ids.insert(id.clone()) || !frames.contains_key(&page) {
                return Err(format!("duplicate or unpaged wall run {id}"));
            }
            let curve = if !native || field(v, "curve").is_null() {
                None
            } else {
                let c: ArcGeometry =
                    serde_json::from_value(field(v, "curve").clone()).map_err(|e| e.to_string())?;
                if c.radius <= 0. || c.sweep == 0. {
                    return Err(format!("invalid curved wall {id}"));
                }
                Some(c)
            };
            let actual = curve.map_or_else(|| line.length(), |c| c.radius * c.sweep.abs());
            let length = if native {
                actual
            } else {
                number(v, "drawingLength", None)?
            };
            if !length.is_finite()
                || length <= 0.
                || (length - actual).abs() > 0.01_f64.max(length * 0.001)
            {
                return Err(format!("invalid drawing length on {id}"));
            }
            if !native {
                let from = string(v, "fromNodeId")?.ok_or("missing fromNodeId")?;
                let to = string(v, "toNodeId")?.ok_or("missing toNodeId")?;
                if from == to {
                    return Err(format!("wall {id} references a single endpoint node"));
                }
                for (node, end) in [(from, line.start), (to, line.end)] {
                    if nodes
                        .get(&node)
                        .is_none_or(|(n, p)| *n != page || p.distance(end) > 0.01)
                    {
                        return Err(format!(
                            "wall {id} references a missing or displaced endpoint {node}"
                        ));
                    }
                }
            }
            let wall_type = string(v, "wallType")?
                .or_else(|| native.then(|| "Unknown".into()))
                .ok_or("missing wallType")?;
            let confidence = if native {
                None
            } else {
                Some(number(v, "confidence", None)?)
            };
            if confidence.is_some_and(|c| !(0.0..=1.0).contains(&c)) {
                return Err(format!("invalid confidence on {id}"));
            }
            runs.push(Run {
                id,
                page,
                line,
                curve,
                length,
                wall_type,
                confidence,
                evidence: texts(v, "evidence")?,
            });
            Ok(())
        };
        if native {
            for p in list(v, "pages", true)? {
                let n = page_number(p)?;
                for run in list(p, "wallRuns", true)? {
                    add(run, n)?;
                }
            }
        } else {
            for run in list(v, "wallRuns", true)? {
                add(run, page_number(run)?)?;
            }
        }
        let solver = if native {
            None
        } else {
            let s = field(v, "wallSolver");
            let m = field(s, "metrics");
            Some([
                number(s, "selectedScore", None)?,
                number(m, "endpointConnectivityRatio", None)?,
                number(m, "roomBoundaryClosureRatio", None)?,
            ])
        };
        Ok(Self {
            native,
            schema,
            document_id: string(field(v, "document"), "id")?,
            frames,
            runs,
            solver,
        })
    }
}
