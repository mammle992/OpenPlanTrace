use openplantrace_rs::layers::LayerCategoryOverride;
use serde_json::Value;
use std::path::{Path, PathBuf};

#[derive(Default)]
pub(crate) struct LayerInputs {
    paths: Vec<PathBuf>,
    inline: Vec<LayerCategoryOverride>,
}
fn get<'a>(v: &'a Value, key: &str) -> &'a Value {
    v.get(key)
        .or_else(|| {
            v.as_object()?
                .iter()
                .find(|(k, _)| k.eq_ignore_ascii_case(key))
                .map(|(_, v)| v)
        })
        .unwrap_or(&Value::Null)
}
fn string<'a>(v: &'a Value, key: &str) -> Result<Option<&'a str>, String> {
    match get(v, key) {
        Value::Null => Ok(None),
        v => v
            .as_str()
            .map(Some)
            .ok_or_else(|| format!("{key} must be a string")),
    }
}
fn rule(v: &Value) -> Result<LayerCategoryOverride, String> {
    LayerCategoryOverride::from_parts(
        string(v, "pattern")?.ok_or("missing layer pattern")?,
        string(v, "category")?.ok_or("missing layer category")?,
        string(v, "sourceFormat")?,
    )
}
impl LayerInputs {
    pub(crate) fn path(&mut self, path: PathBuf) {
        self.paths.push(path)
    }
    pub(crate) fn inline(&mut self, value: &str) -> Result<(), String> {
        let (pattern, category) = value
            .rsplit_once('=')
            .ok_or("expected --layer-category PATTERN=CATEGORY")?;
        self.inline
            .push(LayerCategoryOverride::from_parts(pattern, category, None)?);
        Ok(())
    }
    pub(crate) fn manifest(
        &mut self,
        base: &Path,
        profiles: &[PathBuf],
        overrides: &[Value],
    ) -> Result<(), String> {
        self.paths.extend(profiles.iter().map(|p| base.join(p)));
        for value in overrides {
            self.inline.push(rule(value)?)
        }
        Ok(())
    }
    pub(crate) fn resolve(self) -> Result<Vec<LayerCategoryOverride>, String> {
        // The legacy scanner applies all inline rules before profile files. First match wins.
        let mut rules = self.inline;
        for path in self.paths {
            let file: Value = super::super::validate::read_json(&path)
                .map_err(|e| format!("{}: {e}", path.display()))?;
            if !file.is_object() {
                return Err("layer profile must be an object".into());
            }
            if let Some(s) = string(&file, "schemaVersion")?.and_then(super::cleaned)
                && !s.eq_ignore_ascii_case("openplantrace.layer-profile.v1")
            {
                return Err(format!("unsupported layer profile schemaVersion: {s}"));
            }
            match get(&file, "overrides") {
                Value::Null => (),
                Value::Array(items) => {
                    for value in items {
                        rules.push(rule(value)?)
                    }
                }
                _ => return Err("layer overrides must be an array".into()),
            }
        }
        Ok(rules)
    }
}
