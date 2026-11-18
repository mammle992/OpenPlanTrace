use openplantrace_rs::{export, model::Scan};
use serde_json::Value;
use std::{
    collections::HashSet,
    path::{Component, Path},
};

pub(crate) fn verify(v: &Value, bundles: Option<&Path>) -> Result<(), String> {
    let rows = v["items"].as_array().ok_or("missing batch items")?;
    let mut paths = HashSet::new();
    let mut names = HashSet::new();
    let mut successes = 0;
    for r in rows {
        let path = r["inputPath"]
            .as_str()
            .filter(|p| !p.trim().is_empty())
            .ok_or("missing batch input path")?;
        #[cfg(windows)]
        let path = path.replace('/', "\\").to_lowercase();
        if !paths.insert(path) {
            return Err("duplicate batch input path".into());
        }
        let attempts = r["attempts"].as_u64().ok_or("invalid attempt count")?;
        if !r["durationMilliseconds"]
            .as_f64()
            .is_some_and(|n| n.is_finite() && n >= 0.)
        {
            return Err("invalid elapsed time".into());
        }
        match r["status"].as_str() {
            Some("Scanned") => {
                let name = r["bundle"].as_str().ok_or("missing bundle name")?;
                let mut parts = Path::new(name).components();
                if !matches!(parts.next(), Some(Component::Normal(_)))
                    || parts.next().is_some()
                    || !names.insert(name)
                {
                    return Err("invalid or duplicate batch bundle name".into());
                }
                let counts = &r["counts"];
                if attempts == 0
                    || r["error"] != Value::Null
                    || !["pages", "rooms", "walls", "openings"]
                        .iter()
                        .all(|k| counts[k].is_u64())
                    || counts["pages"] == 0
                {
                    return Err(format!("invalid scan counts or outcome for {name}"));
                }
                if let Some(root) = bundles {
                    let root = root.canonicalize().map_err(|e| e.to_string())?;
                    let scan_path = root
                        .join(name)
                        .join("scan.json.gz")
                        .canonicalize()
                        .map_err(|e| format!("{name}: {e}"))?;
                    if !scan_path.starts_with(&root) {
                        return Err("bundle source is outside the batch directory".into());
                    }
                    let scan: Scan = super::read_json(&scan_path)?;
                    export::validate(&scan)?;
                    for (k, n) in [
                        ("pages", scan.pages.len()),
                        ("rooms", scan.pages.iter().map(|p| p.rooms.len()).sum()),
                        ("walls", scan.pages.iter().map(|p| p.walls.len()).sum()),
                        (
                            "openings",
                            scan.pages.iter().map(|p| p.openings.len()).sum(),
                        ),
                    ] {
                        if counts[k].as_u64() != Some(n as u64) {
                            return Err(format!("{name}: {k} differs from the scanned bundle"));
                        }
                    }
                }
                successes += 1;
            }
            Some(status @ ("Failed" | "Unsupported" | "Missing")) => {
                if !r["counts"].is_null()
                    || !r["bundle"].is_null()
                    || !r["error"].is_string()
                    || (status == "Failed") != (attempts > 0)
                {
                    return Err("invalid failed batch outcome".into());
                }
            }
            _ => return Err("unknown batch status".into()),
        }
    }
    if v["total"].as_u64() != Some(rows.len() as u64)
        || v["scanned"].as_u64() != Some(successes as u64)
        || v["passed"].as_bool() != Some(successes == rows.len())
    {
        return Err("batch totals disagree with the per-input outcomes".into());
    }
    Ok(())
}
