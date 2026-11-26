//! Shape and string dictionaries used by OpenPlanTrace compact scan files.
use crate::model::Scan;
use serde_json::{Map, Value, json};
use std::collections::{HashMap, HashSet};

pub const NATIVE: &str = "openplantrace.rust.scan.compact.v1";
pub const REFERENCE: &str = "openplantrace.scan.compact.v1";
const CODEC: &str = "shape-string-token-v1";

#[derive(Default)]
struct Encoder<'a> {
    ids: HashMap<&'a str, usize>,
    strings: Vec<&'a str>,
    shape_ids: HashMap<Vec<usize>, usize>,
    shapes: Vec<Vec<usize>>,
}
impl<'a> Encoder<'a> {
    fn string(&mut self, s: &'a str) -> usize {
        *self.ids.entry(s).or_insert_with(|| {
            self.strings.push(s);
            self.strings.len() - 1
        })
    }
    fn value(&mut self, v: &'a Value) -> Value {
        match v {
            Value::Object(fields) => {
                let keys: Vec<_> = fields.keys().map(|s| self.string(s)).collect();
                let id = *self.shape_ids.entry(keys.clone()).or_insert_with(|| {
                    self.shapes.push(keys);
                    self.shapes.len() - 1
                });
                let mut token = Vec::with_capacity(fields.len() + 2);
                token.extend([json!(0), json!(id)]);
                token.extend(fields.values().map(|x| self.value(x)));
                Value::Array(token)
            }
            Value::Array(seq) => {
                let mut token = Vec::with_capacity(seq.len() + 1);
                token.push(json!(1));
                token.extend(seq.iter().map(|x| self.value(x)));
                Value::Array(token)
            }
            Value::String(s) => json!([2, self.string(s)]),
            other => other.clone(),
        }
    }
}
pub fn encode(scan: &Scan) -> Result<Value, String> {
    let value = serde_json::to_value(scan).map_err(|e| e.to_string())?;
    let mut table = Encoder::default();
    let data = table.value(&value);
    Ok(
        json!({"schemaVersion":NATIVE,"sourceSchemaVersion":scan.schema_version,"encoding":CODEC,
        "dictionary":{"stringPrefixes":[],"strings":table.strings,"shapes":table.shapes},"data":data}),
    )
}

fn array<'a>(v: &'a Value, name: &str) -> Result<&'a [Value], String> {
    v.as_array()
        .map(Vec::as_slice)
        .ok_or_else(|| format!("missing or invalid compact {name}"))
}
fn index(v: &Value) -> Result<usize, String> {
    v.as_u64()
        .and_then(|n| usize::try_from(n).ok())
        .ok_or_else(|| "invalid compact dictionary index".into())
}

struct Decoder {
    strings: Vec<String>,
    shapes: Vec<Vec<usize>>,
    budget: usize,
}
impl Decoder {
    fn charge(&mut self, n: usize) -> Result<(), String> {
        self.budget = self
            .budget
            .checked_sub(n)
            .ok_or("expanded compact scan exceeds memory limit")?;
        Ok(())
    }
    fn new(dict: &Value) -> Result<Self, String> {
        let mut d = Self {
            strings: Vec::new(),
            shapes: Vec::new(),
            budget: 512 << 20,
        };
        let prefixes = array(&dict["stringPrefixes"], "prefixes")?
            .iter()
            .map(|v| v.as_str().ok_or("invalid compact prefix"))
            .collect::<Result<Vec<_>, _>>()?;
        for entry in array(&dict["strings"], "strings")? {
            let s = if let Some(s) = entry.as_str() {
                d.charge(s.len() + 24)?;
                s.to_owned()
            } else {
                let [tag, id, suffix] = array(entry, "prefix entry")? else {
                    return Err("invalid compact prefix entry".into());
                };
                if tag != "p" {
                    return Err("invalid compact prefix tag".into());
                }
                let prefix = prefixes
                    .get(index(id)?)
                    .ok_or("compact prefix index out of range")?;
                let suffix = suffix.as_str().ok_or("invalid compact suffix")?;
                d.charge(prefix.len() + suffix.len() + 24)?;
                format!("{prefix}{suffix}")
            };
            d.strings.push(s);
        }
        for entry in array(&dict["shapes"], "shapes")? {
            let keys = array(entry, "shape")?;
            d.charge(keys.len().saturating_mul(16) + 24)?;
            let mut names = HashSet::new();
            let mut shape = Vec::with_capacity(keys.len());
            for key in keys {
                let id = index(key)?;
                let name = d
                    .strings
                    .get(id)
                    .ok_or("compact property index out of range")?;
                if !names.insert(name.as_str()) {
                    return Err("duplicate property in compact shape".into());
                }
                shape.push(id);
            }
            d.shapes.push(shape);
        }
        Ok(d)
    }
    fn string(&mut self, id: usize) -> Result<String, String> {
        let size = self
            .strings
            .get(id)
            .ok_or("compact string index out of range")?
            .len();
        self.charge(size)?;
        Ok(self.strings[id].clone())
    }
    fn expand(&mut self, v: &Value) -> Result<Value, String> {
        self.charge(32)?;
        let token = match v {
            Value::Array(token) => token,
            Value::Object(_) => return Err("unencoded object in compact data".into()),
            Value::String(s) => {
                self.charge(s.len())?;
                return Ok(v.clone());
            }
            _ => return Ok(v.clone()),
        };
        match token.split_first() {
            Some((tag, [shape_id, values @ ..])) if tag.as_u64() == Some(0) => {
                let id = index(shape_id)?;
                let shape = self
                    .shapes
                    .get(id)
                    .ok_or("compact shape index out of range")?;
                if shape.len() != values.len() {
                    return Err("compact shape value count differs".into());
                }
                let mut out = Map::new();
                for (column, v) in values.iter().enumerate() {
                    let key = self.string(self.shapes[id][column])?;
                    out.insert(key, self.expand(v)?);
                }
                Ok(Value::Object(out))
            }
            Some((tag, values)) if tag.as_u64() == Some(1) => values
                .iter()
                .map(|v| self.expand(v))
                .collect::<Result<Vec<_>, _>>()
                .map(Value::Array),
            Some((tag, [id])) if tag.as_u64() == Some(2) => {
                self.string(index(id)?).map(Value::String)
            }
            _ => Err("unknown or malformed compact token".into()),
        }
    }
}
pub fn decode(compact: &Value) -> Result<Value, String> {
    let schema = compact["sourceSchemaVersion"]
        .as_str()
        .ok_or("missing compact source schema")?;
    let supported = match compact["schemaVersion"].as_str() {
        Some(REFERENCE) => schema == "openplantrace.scan.v72",
        Some(NATIVE) => matches!(
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
                | "openplantrace.rust.scan.v17"
                | "openplantrace.rust.scan.v18"
                | "openplantrace.rust.scan.v19"
                | "openplantrace.rust.scan.v20"
                | "openplantrace.rust.scan.v21"
                | "openplantrace.rust.scan.v22"
                | "openplantrace.rust.scan.v23"
                | "openplantrace.rust.scan.v24"
                | "openplantrace.rust.scan.v25"
                | "openplantrace.rust.scan.v26"
                | "openplantrace.rust.scan.v27"
                | "openplantrace.rust.scan.v28"
                | "openplantrace.rust.scan.v29"
                | "openplantrace.rust.scan.v30"
                | "openplantrace.rust.scan.v31"
                | "openplantrace.rust.scan.v32"
                | "openplantrace.rust.scan.v33"
                | "openplantrace.rust.scan.v34"
                | "openplantrace.rust.scan.v35"
                | "openplantrace.rust.scan.v36"
                | "openplantrace.rust.scan.v37"
                | "openplantrace.rust.scan.v38"
                | "openplantrace.rust.scan.v39"
                | "openplantrace.rust.scan.v40"
                | "openplantrace.rust.scan.v41"
                | "openplantrace.rust.scan.v42"
                | "openplantrace.rust.scan.v43"
        ),
        _ => false,
    };
    if !supported || compact["encoding"] != CODEC {
        return Err("unsupported compact encoding or source schema".into());
    }
    let mut decoder = Decoder::new(&compact["dictionary"])?;
    let v = decoder.expand(compact.get("data").ok_or("missing compact data")?)?;
    if v["schemaVersion"] != schema {
        return Err("compact source version disagrees with its data".into());
    }
    Ok(v)
}
