use serde_json::Value;
use std::{
    collections::HashSet,
    fs,
    io::Read,
    path::{Component, Path},
};

fn ids<'a>(v: &'a Value, key: &str) -> Result<HashSet<&'a str>, String> {
    let values = v[key].as_array().ok_or_else(|| format!("missing {key}"))?;
    let mut set = HashSet::new();
    for entry in values {
        let id = entry
            .as_str()
            .filter(|s| !s.is_empty())
            .ok_or_else(|| format!("invalid {key}"))?;
        if !set.insert(id) {
            return Err(format!("duplicate id in {key}"));
        }
    }
    if set.is_empty() {
        return Err(format!("empty {key}"));
    }
    Ok(set)
}
fn number(v: &Value, key: &str) -> Result<f64, String> {
    v[key]
        .as_f64()
        .filter(|n| n.is_finite())
        .ok_or_else(|| format!("invalid {key}"))
}
pub(crate) fn verify(v: &Value, image_dir: Option<&Path>) -> Result<(), String> {
    let objects = ids(v, "objectIds")?;
    let sources = ids(v, "sourcePrimitiveIds")?;
    let id = v["detectionId"].as_str().ok_or("missing detectionId")?;
    if !objects.contains(id)
        || v["sourceEvidence"]["primitiveCount"].as_u64() != Some(sources.len() as u64)
        || !v["pageNumber"].as_u64().is_some_and(|n| n > 0)
        || v["imageColorSpace"] != "RGB"
        || v["coordinateSpace"] != "page"
    {
        return Err("invalid crop source metadata".into());
    }
    let dims = |k: &str| {
        v[k].as_u64()
            .and_then(|n| u32::try_from(n).ok())
            .filter(|n| *n > 0)
            .ok_or("invalid crop image dimensions")
    };
    let (width, height) = (dims("imageWidth")?, dims("imageHeight")?);
    let crop = &v["cropBounds"];
    for (offset, extent, page) in [("x", "width", "pageWidth"), ("y", "height", "pageHeight")] {
        let (at, len, available) = (
            number(crop, offset)?,
            number(crop, extent)?,
            number(v, page)?,
        );
        if at < 0.
            || len <= 0.
            || at + len > available + 1e-9 * available.max(1.)
            || number(&v["bounds"], extent)? < 0.
        {
            return Err("crop bounds or source bounds disagree with page".into());
        }
        number(&v["bounds"], offset)?;
    }
    let name = v["imageFileName"]
        .as_str()
        .ok_or("missing crop image name")?;
    let mut components = Path::new(name).components();
    if !matches!(components.next(), Some(Component::Normal(_)))
        || components.next().is_some()
        || !name.to_ascii_lowercase().ends_with(".png")
    {
        return Err("invalid crop image filename".into());
    }
    if let Some(c) = v.get("classification").filter(|c| !c.is_null()) {
        if !c["accepted"].is_boolean()
            || !c["label"].is_string()
            || !(0.0..=1.).contains(&number(c, "confidence")?)
            || !c["modelSha256"]
                .as_str()
                .is_some_and(|s| s.len() == 64 && s.bytes().all(|c| c.is_ascii_hexdigit()))
        {
            return Err("invalid crop model prediction".into());
        }
    }
    if let Some(dir) = image_dir {
        let dir = dir.canonicalize().map_err(|e| e.to_string())?;
        // A batch also publishes a manifest above its per-input crop directories.
        let recorded = Path::new(v["imagePath"].as_str().ok_or("missing image path")?);
        if recorded.file_name() != Some(std::ffi::OsStr::new(name)) {
            return Err("imagePath and imageFileName disagree".into());
        }
        let nested = recorded
            .parent()
            .and_then(Path::file_name)
            .map(|folder| dir.join(folder).join(name));
        let local = dir.join(name);
        let path = nested
            .filter(|p| p.is_file())
            .unwrap_or(local)
            .canonicalize()
            .map_err(|e| format!("{name}: {e}"))?;
        if !path.starts_with(&dir) {
            return Err("image outside crop directory".into());
        }
        let mut header = [0; 29];
        fs::File::open(path)
            .and_then(|mut f| f.read_exact(&mut header))
            .map_err(|e| e.to_string())?;
        if header[..16] != *b"\x89PNG\r\n\x1a\n\0\0\0\rIHDR"
            || header[16..20] != width.to_be_bytes()
            || header[20..24] != height.to_be_bytes()
            || header[24..26] != [8, 2]
        {
            return Err("PNG header disagrees with crop dimensions or RGB format".into());
        }
    }
    Ok(())
}
