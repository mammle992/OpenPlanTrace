use serde_json::Value;

fn array(v: &Value) -> Result<&[Value], String> {
    v.as_array()
        .map(Vec::as_slice)
        .ok_or_else(|| "expected a coordinate array".into())
}

fn position(v: &Value, geographic: bool) -> Result<(), String> {
    let p = array(v)?;
    if p.len() < 2 || !p.iter().all(|c| c.as_f64().is_some_and(f64::is_finite)) {
        return Err("position requires at least two finite numbers".into());
    }
    if geographic
        && (!(-180.0..=180.0).contains(&p[0].as_f64().unwrap())
            || !(-90.0..=90.0).contains(&p[1].as_f64().unwrap()))
    {
        return Err("coordinate outside the WGS84 longitude/latitude range".into());
    }
    Ok(())
}

fn line(v: &Value, geographic: bool, ring: bool) -> Result<(), String> {
    let points = array(v)?;
    if points.len() < if ring { 4 } else { 2 } {
        return Err("too few points for the line or polygon ring".into());
    }
    for p in points {
        position(p, geographic)?;
    }
    if ring
        && !array(&points[0])?
            .iter()
            .map(Value::as_f64)
            .eq(array(points.last().unwrap())?.iter().map(Value::as_f64))
    {
        return Err("open polygon ring".into());
    }
    Ok(())
}

fn polygon(v: &Value, geographic: bool) -> Result<(), String> {
    let rings = array(v)?;
    if rings.is_empty() {
        return Err("missing exterior ring".into());
    }
    rings.iter().try_for_each(|r| line(r, geographic, true))
}

fn geometry(g: &Value, geographic: bool) -> Result<(), String> {
    let c = &g["coordinates"];
    match g["type"].as_str() {
        Some("Point") => position(c, geographic),
        Some("LineString") => line(c, geographic, false),
        Some("Polygon") => polygon(c, geographic),
        Some("MultiPoint") => array(c)?.iter().try_for_each(|p| position(p, geographic)),
        Some("MultiLineString") => array(c)?
            .iter()
            .try_for_each(|p| line(p, geographic, false)),
        Some("MultiPolygon") => array(c)?.iter().try_for_each(|p| polygon(p, geographic)),
        Some("GeometryCollection") => array(&g["geometries"])?
            .iter()
            .try_for_each(|p| geometry(p, geographic)),
        _ => Err("unknown geometry type".into()),
    }
}

pub(super) fn verify(v: &Value) -> Result<String, String> {
    if v["type"] != "FeatureCollection" {
        return Err("expected a FeatureCollection".into());
    }
    let geographic = match (v.get("schemaVersion"), v.get("coordinateSpace")) {
        (None, None) => true,
        (Some(version), Some(space))
            if version == "openplantrace.geojson.v1" && space == "OpenPlanTracePageCoordinates" =>
        {
            false
        }
        _ => return Err("unrecognized coordinate space or spatial export version".into()),
    };
    for (i, f) in v["features"]
        .as_array()
        .ok_or("missing features array")?
        .iter()
        .enumerate()
    {
        if f["type"] != "Feature"
            || !f
                .get("properties")
                .is_some_and(|p| p.is_null() || p.is_object())
        {
            return Err(format!("feature {} requires type and properties", i + 1));
        }
        let g = f
            .get("geometry")
            .ok_or_else(|| format!("feature {} is missing geometry", i + 1))?;
        if !g.is_null() {
            geometry(g, geographic).map_err(|e| format!("feature {}: {e}", i + 1))?;
        }
    }
    Ok(if geographic {
        "GeoJSON FeatureCollection (WGS84)"
    } else {
        "openplantrace.geojson.v1 (page coordinates)"
    }
    .into())
}
