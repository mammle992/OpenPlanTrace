//! Pure projections of detector decisions. No wall selection or missing-door recovery here.
use crate::model::*;
use serde::Serialize;
use serde_json::{Value, json};
use std::{
    collections::{HashMap, HashSet},
    fs,
    io::{BufWriter, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};
mod connection_view;
mod placement;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Interval<'a> {
    pub opening_id: &'a str,
    pub start: f64,
    pub end: f64,
}
#[derive(Serialize)]
pub struct Span {
    pub start: Point,
    pub end: Point,
    pub curve: Option<ArcGeometry>,
}
pub fn coverage<'a>(wall: &Wall, opens: &'a [Opening]) -> (Vec<Interval<'a>>, Vec<Span>) {
    wall_coverage(wall, opens.iter())
}

fn wall_coverage<'a>(
    wall: &Wall,
    opens: impl IntoIterator<Item = &'a Opening>,
) -> (Vec<Interval<'a>>, Vec<Span>) {
    if wall.curve.is_some() {
        return (
            vec![],
            vec![Span {
                start: wall.start,
                end: wall.end,
                curve: wall.curve,
            }],
        );
    }
    let v = wall.end - wall.start;
    let l = v.dot(v);
    let mut intervals = Vec::new();
    if l <= 0. {
        return (intervals, vec![]);
    }
    for o in opens {
        if let Some((start, end)) = o.interval_on(wall) {
            intervals.push(Interval {
                opening_id: &o.id,
                start,
                end,
            });
        }
    }
    intervals.sort_by(|a, b| {
        a.start
            .total_cmp(&b.start)
            .then(a.opening_id.cmp(b.opening_id))
    });
    let mut spans = Vec::new();
    let mut at = 0_f64;
    for iv in &intervals {
        if iv.start > at {
            spans.push(Span {
                start: wall.start + v * at,
                end: wall.start + v * iv.start,
                curve: None,
            });
        }
        at = at.max(iv.end);
    }
    if at < 1. {
        spans.push(Span {
            start: wall.start + v * at,
            end: wall.end,
            curve: None,
        })
    }
    (intervals, spans)
}
pub fn structure(scan: &Scan) -> Value {
    let pages:Vec<_>=scan.pages.iter().map(|p|{
        let extents:HashMap<_,_>=p.placement_decisions.iter().flatten().filter_map(|d|d.placement_extent.as_ref().map(|e|(d.wall_id.as_str(),e))).collect();
        let runs:Vec<_>=p.walls.iter().map(|w|{let (iv,s)=coverage(w,&p.openings);
            let mut row=json!({"id":w.id,"sourceIds":w.source_ids,"centerLine":{"start":w.start,"end":w.end},"thickness":w.thickness,"thicknessKnown":w.thickness>0.,"curve":w.curve,"body":w.body,"requiresReview":w.requires_review,"openingIds":p.openings.iter().filter(|o|o.host_wall_ids.contains(&w.id)).map(|o|&o.id).collect::<Vec<_>>(),"openingIntervals":iv,"solidSegments":s});
            if let Some(extent)=extents.get(w.id.as_str()){row["placementExtent"]=json!(extent);}
            row
        }).collect();
        let mut page=json!({"pageNumber":p.page_number,"width":p.width,"height":p.height,"wallRuns":runs,"nodes":p.nodes,"edges":p.edges,"rooms":p.rooms,"unitsToMillimeters":p.units_to_millimeters});
        let painted:Vec<_>=p.room_annotations.iter().filter(|a|a.floor_footprint.is_some()).collect();
        if !painted.is_empty() {page["paintedFloorAreas"]=json!(painted);}
        if let Some(connections)=&p.exterior_connections {page["exteriorConnections"]=json!(connections);}
        page
    }).collect();
    let (schema, selection) = if scan.schema_version == "openplantrace.rust.scan.v1" {
        ("openplantrace.rust.structure.v1", "ObservedPhysicalWalls")
    } else {
        ("openplantrace.rust.structure.v2", "SelectedPhysicalWalls")
    };
    json!({"schemaVersion":schema,"selection":selection,"pages":pages})
}
pub fn placement(scan: &Scan) -> Value {
    let with_enclosures = matches!(
        scan.schema_version.as_str(),
        "openplantrace.rust.scan.v8"
            | "openplantrace.rust.scan.v9"
            | "openplantrace.rust.scan.v10"
            | "openplantrace.rust.scan.v11"
            | "openplantrace.rust.scan.v12"
            | "openplantrace.rust.scan.v13"
            | "openplantrace.rust.scan.v14"
            | "openplantrace.rust.scan.v15"
            | "openplantrace.rust.scan.v16"
    );
    let (version, pages): (_, Vec<_>) = if matches!(
        scan.schema_version.as_str(),
        "openplantrace.rust.scan.v3"
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
    ) {
        (
            if with_enclosures {
                "openplantrace.rust.placement.v3"
            } else {
                "openplantrace.rust.placement.v2"
            },
            scan.pages
                .iter()
                .map(|p| placement::page(p, with_enclosures))
                .collect(),
        )
    } else {
        (
            "openplantrace.rust.placement.v1",
            scan.pages.iter().map(placement::legacy_page).collect(),
        )
    };
    let mut result = json!({"schemaVersion":version,"pages":pages});
    if let Some(labels) = &scan.object_label_profile {
        result["objectLabelProfile"] = json!(labels);
    }
    result
}

pub fn placement_overlays(page: &PageScan) -> Value {
    placement::overlays(page)
}
/// Project the recorded, source-validated boundary connection pass.
pub fn connections(scan: &Scan) -> Value {
    let pages: Vec<_> = scan
        .pages
        .iter()
        .filter_map(|p| p.exterior_connections.as_ref())
        .collect();
    json!({"schemaVersion":"openplantrace.rust.connections.v1", "sourceScanSchemaVersion":scan.schema_version, "pages":pages})
}
/// Registered SVG view of supported graph connections and unresolved ends.
pub fn connections_svg(page: &PageScan) -> String {
    connection_view::svg(page)
}
pub fn valid_transform(t: &[f64; 6]) -> bool {
    let determinant = t[0] * t[4] - t[1] * t[3];
    t.iter().all(|x| x.is_finite()) && determinant.is_finite() && determinant != 0.
}
fn coords(p: Point, t: Option<[f64; 6]>) -> Result<[f64; 2], String> {
    if let Some([a, b, c, d, e, f]) = t {
        let lon = a * p.x + b * p.y + c;
        let lat = d * p.x + e * p.y + f;
        if !(-180.0..=180.0).contains(&lon) || !(-90.0..=90.0).contains(&lat) {
            return Err("transformed coordinate outside WGS84 range".into());
        }
        Ok([lon, lat])
    } else {
        Ok([p.x, p.y])
    }
}
fn spatial_polygon(points: &[Point], t: Option<[f64; 6]>) -> Result<Option<Value>, String> {
    let mut ring = points
        .iter()
        .map(|&q| coords(q, t))
        .collect::<Result<Vec<_>, _>>()?;
    if t.is_some() {
        let area = ring
            .iter()
            .zip(ring.iter().cycle().skip(1))
            .map(|(p, q)| p[0] * q[1] - q[0] * p[1])
            .sum::<f64>();
        if area < 0. {
            ring.reverse();
        }
    }
    let Some(&first) = ring.first() else {
        return Ok(None);
    };
    ring.push(first);
    Ok(Some(json!({"type":"Polygon","coordinates":[ring]})))
}
pub fn spatial(scan: &Scan, t: Option<[f64; 6]>) -> Result<Value, String> {
    if t.as_ref().is_some_and(|a| !valid_transform(a)) {
        return Err("geographic transform must be finite and nonsingular".into());
    }
    let mut fs = Vec::new();
    for p in &scan.pages {
        let view = crate::placement::effective_page(p);
        let p = &*view;
        let mut feature = |id: &str, kind: &str, geom: Value| {
            fs.push(json!({"type":"Feature","id":id,"properties":{"kind":kind,"pageNumber":p.page_number},"geometry":geom}))
        };
        for w in &p.walls {
            for (i, s) in coverage(w, &p.openings).1.iter().enumerate() {
                let v: Vec<_> = if let Some(c) = s.curve {
                    let n = (c.sweep.abs() / 0.05).ceil().clamp(2., 1024.) as usize;
                    (0..=n)
                        .map(|j| coords(c.at(j as f64 / n as f64), t))
                        .collect::<Result<_, _>>()?
                } else {
                    vec![coords(s.start, t)?, coords(s.end, t)?]
                };
                feature(
                    &format!("{}:solid:{}", w.id, i + 1),
                    "wall",
                    json!({"type":"LineString","coordinates":v}),
                );
            }
        }
        for o in &p.openings {
            feature(
                &o.id,
                if o.kind == OpeningKind::Door {
                    "door"
                } else {
                    "window"
                },
                json!({"type":"LineString","coordinates":[coords(o.start,t)?,coords(o.end,t)?]}),
            )
        }
        for r in &p.rooms {
            if let Some(polygon) = spatial_polygon(&r.polygon, t)? {
                feature(&r.id, "room", polygon);
            }
        }
        for o in &p.objects {
            let geometry = if let Some(foot) = &o.footprint {
                spatial_polygon(&foot.polygon, t)?.ok_or("empty furniture footprint")?
            } else if let Some(measured) = &o.geometry {
                spatial_polygon(&measured.polygon, t)?.ok_or("empty object footprint")?
            } else {
                json!({"type":"Point","coordinates":coords(o.at,t)?})
            };
            feature(&o.id, "object", geometry);
        }
    }
    if t.is_some() {
        Ok(json!({"type":"FeatureCollection","features":fs}))
    } else {
        Ok(
            json!({"schemaVersion":"openplantrace.rust.local-features.v1","coordinateSpace":"page-local drawing units, not WGS84","features":fs}),
        )
    }
}
fn esc(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('"', "&quot;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}
pub fn svg(page: &PageScan, with_openings: bool) -> String {
    use std::fmt::Write as _;
    let view = crate::placement::effective_page(page);
    let page = &*view;
    let mut s = format!(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {} {}\"><title>OpenPlanTrace Rust — {}</title><g fill=\"none\" stroke=\"#235b84\" stroke-linecap=\"butt\">\n",
        page.width,
        page.height,
        if with_openings {
            "walls and openings"
        } else {
            "walls only"
        }
    );
    for w in &page.walls {
        for span in coverage(w, &page.openings).1 {
            if let Some(c) = span.curve {
                // Two finite SVG arc commands also represent a complete circle (equal endpoints).
                let m = c.at(0.5);
                let sweep = usize::from(c.sweep > 0.);
                let _ = writeln!(
                    s,
                    "<path data-wall=\"{}\" stroke-width=\"{}\" d=\"M {} {} A {r} {r} 0 0 {sweep} {} {} A {r} {r} 0 0 {sweep} {} {}\"/>",
                    esc(&w.id),
                    if w.thickness == 0. { 0.6 } else { w.thickness },
                    span.start.x,
                    span.start.y,
                    m.x,
                    m.y,
                    span.end.x,
                    span.end.y,
                    r = c.radius
                );
            } else {
                let _ = writeln!(
                    s,
                    "<line data-wall=\"{}\" stroke-width=\"{}\" x1=\"{}\" y1=\"{}\" x2=\"{}\" y2=\"{}\"/>",
                    esc(&w.id),
                    if w.thickness == 0. { 0.6 } else { w.thickness },
                    span.start.x,
                    span.start.y,
                    span.end.x,
                    span.end.y
                );
            }
        }
    }
    s.push_str("</g>");
    if with_openings {
        s.push_str("<g fill=\"none\" stroke-width=\"1.5\">");
        for o in &page.openings {
            let _ = write!(
                s,
                "<line data-opening=\"{}\" stroke=\"{}\" x1=\"{}\" y1=\"{}\" x2=\"{}\" y2=\"{}\"/>",
                esc(&o.id),
                if o.kind == OpeningKind::Door {
                    "#be3d00"
                } else {
                    "#009269"
                },
                o.start.x,
                o.start.y,
                o.end.x,
                o.end.y
            );
        }
        s.push_str("</g>");
    }
    s.push_str("</svg>\n");
    s
}
pub fn validate(s: &Scan) -> Result<(), String> {
    if let Some(visual) = &s.visual_classification {
        visual.validate(s)?;
    }
    crate::pipeline::validate_layer_profile(s)?;
    crate::object_labels::validate(s)?;
    let selected = match s.schema_version.as_str() {
        "openplantrace.rust.scan.v1" => false,
        "openplantrace.rust.scan.v2"
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
        | "openplantrace.rust.scan.v16" => true,
        _ => return Err("unsupported native scan schema".into()),
    };
    if s.pages.is_empty() {
        return Err("unsupported or empty native scan".into());
    }
    let mut pages = HashSet::new();
    for p in &s.pages {
        if p.page_number == 0
            || !pages.insert(p.page_number)
            || !p.width.is_finite()
            || p.width <= 0.
            || !p.height.is_finite()
            || p.height <= 0.
            || p.units_to_millimeters
                .is_some_and(|v| !v.is_finite() || v <= 0.)
        {
            return Err("invalid page identity, dimensions or calibration".into());
        }
        if !p.material_tiles.is_empty()
            && !matches!(
                s.schema_version.as_str(),
                "openplantrace.rust.scan.v5"
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
            )
        {
            return Err("material tile evidence requires scan v5 or newer".into());
        }
        let uses_paint = matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v6"
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
        );
        if uses_paint && p.placement_paint_ids.is_none() {
            return Err("missing placement paint inventory".into());
        }
        if !uses_paint
            && (p.placement_paint_ids.is_some()
                || p.walls.iter().any(crate::placement::jamb::is_recovered))
        {
            return Err(
                "placement paint and recovered jamb evidence require scan v6 or newer".into(),
            );
        }
        if !matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v8"
                | "openplantrace.rust.scan.v9"
                | "openplantrace.rust.scan.v10"
                | "openplantrace.rust.scan.v11"
                | "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        ) && p.walls.iter().any(crate::placement::stroked::is_recovered)
        {
            return Err("recovered stroke-pair walls require scan v8 or newer".into());
        }
        if !matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v7"
                | "openplantrace.rust.scan.v8"
                | "openplantrace.rust.scan.v9"
                | "openplantrace.rust.scan.v10"
                | "openplantrace.rust.scan.v11"
                | "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        ) && p
            .primitives
            .iter()
            .any(|primitive| primitive.stroke_width.is_some())
        {
            return Err("source stroke widths require scan v7 or newer".into());
        }
        if let Some(info) = &p.pdf_input {
            let revision = match s.schema_version.as_str() {
                "openplantrace.rust.scan.v9" => 0,
                "openplantrace.rust.scan.v10"
                | "openplantrace.rust.scan.v11"
                | "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16" => 1,
                _ => return Err("automatic PDF metadata requires scan v9 or newer".into()),
            };
            if info.analysis_revision != revision {
                return Err("raster evidence revision disagrees with scan schema".into());
            }
            crate::input::raster::validate(info, &p.primitives, p.width, p.height)?;
        } else if p.primitives.iter().any(|p| p.id.starts_with("raster:"))
            || p.walls.iter().any(crate::placement::raster::is_raster)
        {
            return Err("missing raster source provenance".into());
        }
        crate::pipeline::validate_sources(
            p.page_number,
            &p.primitives,
            &p.material_tiles,
            p.placement_paint_ids.as_deref(),
        )?;
        let primitives_by_id: HashMap<_, _> =
            p.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
        let physical = matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        );
        if !matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v15" | "openplantrace.rust.scan.v16"
        ) && p.placement_decisions.iter().flatten().any(|d| {
            matches!(
                d.reason,
                crate::placement::WallReason::SourceBoundedPaintedJunctionSpan
            )
        }) {
            return Err("source-bounded painted junction spans require scan v15 or newer".into());
        }
        if s.schema_version == "openplantrace.rust.scan.v16" {
            crate::finish_visibility::validate(p)?;
        } else if p.finish_visibility.is_some()
            || p.placement_decisions.iter().flatten().any(|d| {
                matches!(d.reason, crate::placement::WallReason::SourceBoundedVisibleInsulationAssembly
                    | crate::placement::WallReason::InsulationFinishVisibilityMissing)
            })
        {
            return Err("finish visibility evidence requires scan v16 or newer".into());
        }
        if physical {
            crate::pipeline::validate_polygon_sources(
                &p.primitives,
                p.placement_polygon_paint_ids
                    .as_deref()
                    .ok_or("missing polygon paint inventory")?,
            )?;
            crate::openings::validate_physical_jambs(p)?;
        } else if p.placement_polygon_paint_ids.is_some()
            || p.openings.iter().any(|o| o.physical_jambs.is_some())
            || p.placement_decisions.iter().flatten().any(|d| {
                d.placement_extent.is_some()
                    || d.reason == crate::placement::WallReason::SourceBoundedDoorJambFlanks
            })
        {
            return Err(
                "physical jamb and bounded placement evidence require scan v14 or newer".into(),
            );
        }
        let observed = crate::placement::observation_page(p);
        let w: HashMap<_, _> = p.walls.iter().map(|w| (&w.id, w)).collect();
        let o: HashMap<_, _> = p.openings.iter().map(|o| (&o.id, o)).collect();
        let rm: HashSet<_> = p.rooms.iter().map(|r| &r.id).collect();
        if w.len() != p.walls.len() || o.len() != p.openings.len() || rm.len() != p.rooms.len() {
            return Err("duplicate wall, room or opening".into());
        }
        if selected != p.withheld_walls.is_some() {
            return Err("wall selection record does not match scan schema".into());
        }
        let withheld = p.withheld_walls.as_deref().unwrap_or_default();
        let mut withheld_ids = HashSet::new();
        let mut furniture_work = 32_000_000;
        let mut furnished = HashMap::new();
        let leaf_sources: HashMap<_, _> = p
            .openings
            .iter()
            .flat_map(|o| &o.swing_observations)
            .map(|s| (&s.arc_source_id, &s.open_leaf_source_ids))
            .collect();
        for decision in withheld {
            if let Some(outline) = &decision.furniture_outline {
                crate::objects::checked_outline(
                    outline,
                    decision,
                    &primitives_by_id,
                    &mut furniture_work,
                )?;
                furnished.insert(decision.wall.id.as_str(), decision);
            }
            let witnesses = &decision.witness_source_ids;
            let unique: HashSet<_> = witnesses.iter().collect();
            if !withheld_ids.insert(&decision.wall.id)
                || w.contains_key(&decision.wall.id)
                || !decision.wall.requires_review
                || witnesses.is_empty()
                || !witnesses
                    .iter()
                    .all(|id| primitives_by_id.contains_key(id.as_str()))
                || unique.len() != witnesses.len()
            {
                return Err(format!(
                    "invalid withheld wall decision {}",
                    decision.wall.id
                ));
            }
            if decision.reason == WallSelectionReason::DoorLeaf
                && !witnesses
                    .iter()
                    .filter_map(|id| leaf_sources.get(id))
                    .any(|sources| {
                        sources
                            .iter()
                            .any(|id| unique.contains(id) && decision.wall.source_ids.contains(id))
                    })
            {
                return Err(
                    "withheld door leaf has no shared source with an accepted swing".into(),
                );
            }
            if decision.reason == WallSelectionReason::PrintedRoomArea {
                let written_areas = p
                    .room_annotations
                    .iter()
                    .filter(|a| {
                        !a.area_source_ids.is_empty()
                            && a.area_source_ids.iter().all(|id| unique.contains(id))
                    })
                    .take(3)
                    .count();
                let paint = witnesses
                    .iter()
                    .filter(|id| {
                        matches!(
                            primitives_by_id[id.as_str()].shape,
                            Shape::Polyline {
                                filled: true,
                                closed: true,
                                ..
                            }
                        )
                    })
                    .count();
                if decision.wall.body != BodyEvidence::FilledBody
                    || !decision
                        .wall
                        .source_ids
                        .iter()
                        .all(|id| unique.contains(id))
                    || written_areas != 3
                    || paint < 3
                {
                    return Err(
                        "painted space lacks independent room area and fill witnesses".into(),
                    );
                }
            }
        }
        for v in p.walls.iter().chain(withheld.iter().map(|d| &d.wall)) {
            if v.id.is_empty()
                || !v.start.finite()
                || !v.end.finite()
                || v.thickness < 0.
                || (v.thickness == 0. && v.body != BodyEvidence::ExplicitCenterline)
                || !v.thickness.is_finite()
                || v.page != p.page_number
                || v.source_ids.is_empty()
                || !v
                    .source_ids
                    .iter()
                    .all(|id| primitives_by_id.contains_key(id.as_str()))
            {
                return Err(format!("invalid wall {}", v.id));
            }
            if let Some(c) = v.curve {
                if !c.center.finite()
                    || !c.radius.is_finite()
                    || c.radius <= 0.
                    || !c.sweep.is_finite()
                    || !c.start_angle.is_finite()
                    || c.sweep.abs() <= 0.
                    || c.sweep.abs() > std::f64::consts::TAU + 1e-6
                    || c.at(0.).distance(v.start) > 1e-5
                    || c.at(1.).distance(v.end) > 1e-5
                {
                    return Err("invalid retained analytic curve".into());
                }
            } else if v.start == v.end {
                return Err("degenerate wall".into());
            }
        }
        for v in &p.openings {
            if v.page != p.page_number
                || (selected && !crate::openings::validate_swing_sources(v, &primitives_by_id))
                || !v.start.finite()
                || !v.end.finite()
                || v.start == v.end
                || !v.host_wall_ids.contains(&v.host_wall_id)
                || !v.host_wall_ids.iter().all(|id| w.contains_key(id))
                || v.source_ids.is_empty()
                || !v
                    .source_ids
                    .iter()
                    .all(|id| primitives_by_id.contains_key(id.as_str()))
            {
                return Err(format!("invalid opening {}", v.id));
            }
            if selected && v.evidence == OpeningEvidence::FiniteJambSwing {
                let host = w[&v.host_wall_id];
                if host.curve.is_some()
                    || [v.start, v.end]
                        .iter()
                        .any(|&p| on_segment(p, host.start, host.end).0.distance(p) > 1e-5)
                {
                    return Err("hosted door cut does not lie on its finite wall".into());
                }
            }
        }
        let mut node_ids = HashSet::new();
        for n in &p.nodes {
            if n.id.is_empty()
                || !node_ids.insert(&n.id)
                || !n.position.finite()
                || !n.wall_ids.iter().all(|id| w.contains_key(id))
            {
                return Err("invalid graph node".into());
            }
        }
        for e in &p.edges {
            if e.start_node >= p.nodes.len()
                || e.end_node >= p.nodes.len()
                || e.start_node == e.end_node
                || !e.wall_ids.iter().all(|id| w.contains_key(id))
                || !e.opening_ids.iter().all(|id| {
                    o.get(id).is_some_and(|opening| {
                        !selected
                            || opening.evidence == OpeningEvidence::WallGapSwing
                            || opening.host_wall_ids.iter().any(|h| e.wall_ids.contains(h))
                    })
                })
                || (e.wall_ids.is_empty() && e.opening_ids.is_empty())
            {
                return Err("invalid graph incidence".into());
            }
        }
        let mut incidences = vec![HashSet::new(); p.nodes.len()];
        for e in &p.edges {
            for end in [e.start_node, e.end_node] {
                incidences[end].extend(&e.wall_ids)
            }
        }
        for (n, links) in p.nodes.iter().zip(incidences) {
            if n.wall_ids.iter().collect::<HashSet<_>>() != links
                || links.iter().any(|key| w[*key].requires_review) && !n.requires_review
            {
                return Err("inconsistent node wall incidence or final review flag".into());
            }
        }
        let mut perimeter_work = 32_000_000;
        let painted_floors = if p.rooms.iter().any(|r| {
            matches!(
                r.boundary_evidence,
                Some(RoomBoundaryEvidence::MeasuredFloorFill { .. })
            )
        }) || p
            .room_annotations
            .iter()
            .any(|a| a.floor_footprint.is_some())
        {
            let printed = crate::room_labels::observe(&p.primitives)?;
            let supplied_scale = p.units_to_millimeters.filter(|_| {
                matches!(
                    p.scale_source.as_deref(),
                    Some("InputModelUnits" | "ExplicitOverride")
                )
            });
            let floors =
                crate::room_fill::measured_floors(&p.primitives, &printed.records, supplied_scale)?;
            for (i, annotation) in p.room_annotations.iter().enumerate() {
                if let Some(foot) = &annotation.floor_footprint {
                    let source = primitives_by_id
                        .get(foot.source_id.as_str())
                        .ok_or("missing floor footprint source")?;
                    if !printed.records.get(i).is_some_and(|a| {
                        a.anchor == annotation.anchor
                            && a.area_source_ids == annotation.area_source_ids
                            && a.area_square_meters == annotation.area_square_meters
                    }) {
                        return Err("floor footprint annotation disagrees with source text".into());
                    }
                    crate::topology::perimeters::validate_observation(
                        foot,
                        source,
                        i,
                        &floors,
                        &mut perimeter_work,
                    )?;
                }
            }
            floors
        } else {
            crate::room_fill::PaintedFloors::new()
        };
        let materials =
            crate::topology::perimeters::Materials::new(&p.primitives, &painted_floors, &p.walls);
        for r in &p.rooms {
            if let Some(proof) = &r.boundary_evidence {
                let id = match proof {
                    RoomBoundaryEvidence::MeasuredFloorFill { source_id, .. }
                    | RoomBoundaryEvidence::RoomLayerPerimeter { source_id } => source_id,
                };
                let source = primitives_by_id
                    .get(id.as_str())
                    .ok_or("missing authored room boundary source")?;
                crate::topology::perimeters::validate(
                    r,
                    source,
                    &painted_floors,
                    &p.walls,
                    &observed.openings,
                    &materials,
                    &mut perimeter_work,
                )?;
            }
            if r.polygon.len() < 3
                || !r.area.is_finite()
                || r.area <= 0.
                || !r.polygon.iter().all(|v| v.finite())
                || !r.wall_ids.iter().all(|id| w.contains_key(id))
                || r.label_source_id
                    .as_ref()
                    .is_some_and(|id| !primitives_by_id.contains_key(id.as_str()))
            {
                return Err("invalid room".into());
            }
            let a = r.polygon[0];
            let observed = (1..r.polygon.len() - 1)
                .map(|i| (r.polygon[i] - a).cross(r.polygon[i + 1] - a))
                .sum::<f64>()
                * 0.5;
            if (observed - r.area).abs() > 1e-8 * r.area.max(1.) {
                return Err("room area disagrees with boundary".into());
            }
        }
        crate::objects::validate_geometry(
            &observed,
            matches!(
                s.schema_version.as_str(),
                "openplantrace.rust.scan.v11"
                    | "openplantrace.rust.scan.v12"
                    | "openplantrace.rust.scan.v13"
                    | "openplantrace.rust.scan.v14"
                    | "openplantrace.rust.scan.v15"
                    | "openplantrace.rust.scan.v16"
            ),
        )?;
        for v in &p.objects {
            if let Some(foot) = &v.footprint {
                let d = furnished
                    .remove(foot.selection_id.as_str())
                    .ok_or("missing or duplicate furniture selection")?;
                let (kind, name) =
                    crate::objects::furniture_kind(d.reason).ok_or("invalid furniture evidence")?;
                let at = foot
                    .polygon
                    .iter()
                    .copied()
                    .fold(Point::default(), |a, b| a + b)
                    * 0.25;
                if d.furniture_outline.as_ref() != Some(&foot.polygon)
                    || (v.profile_signature.is_none()
                        && (v.category != kind || v.name != name || !v.requires_review))
                    || v.source_ids != d.witness_source_ids
                    || v.at.distance(at) > 1e-8
                {
                    return Err("object differs from its furnished outline".into());
                }
            } else if matches!(v.category.as_str(), "FurnishedSurface" | "Cabinet") {
                return Err("identified furnishing lacks a finite footprint".into());
            }
            if !v.at.finite()
                || !v
                    .source_ids
                    .iter()
                    .all(|id| primitives_by_id.contains_key(id.as_str()))
                || v.room_id.as_ref().is_some_and(|id| !rm.contains(id))
            {
                return Err("invalid object reference".into());
            }
        }
        let objects: HashSet<_> = p.objects.iter().map(|v| &v.id).collect();
        if objects.len() != p.objects.len() {
            return Err("duplicate object".into());
        }
        for g in &p.object_groups {
            if g.member_ids.is_empty()
                || (g.member_ids.len() < 2 && g.profile_signature.is_none())
                || !g.member_ids.iter().all(|id| objects.contains(id))
            {
                return Err("invalid object group".into());
            }
        }
        for c in &p.room_connections {
            if !o.contains_key(&c.opening_id) || !c.room_ids.iter().all(|id| rm.contains(id)) {
                return Err("invalid room adjacency".into());
            }
        }
        for r in &p.room_annotations {
            if !r.anchor.finite()
                || r.area_square_meters <= 0.
                || !r.area_square_meters.is_finite()
                || r.area_source_ids.is_empty()
                || !r
                    .area_source_ids
                    .iter()
                    .all(|id| primitives_by_id.contains_key(id.as_str()))
                || r.label_source_id
                    .as_ref()
                    .is_some_and(|id| !primitives_by_id.contains_key(id.as_str()))
                || r.room_id.as_ref().is_some_and(|id| !rm.contains(id))
            {
                return Err("invalid room annotation".into());
            }
        }
        if p.units_to_millimeters.is_some() != p.scale_source.is_some()
            || !p
                .calibration_source_ids
                .iter()
                .all(|id| primitives_by_id.contains_key(id.as_str()))
            || (p.scale_source.as_deref() == Some("AgreeingFiniteDimensions")
                && p.calibration_source_ids.is_empty())
        {
            return Err("invalid calibration evidence".into());
        }
        crate::integrity::check(p)?;
        match s.schema_version.as_str() {
            "openplantrace.rust.scan.v3" => crate::placement::validate_preview4(p)?,
            "openplantrace.rust.scan.v4" => crate::placement::validate_preview5(p)?,
            "openplantrace.rust.scan.v5" => crate::placement::validate_preview6(p)?,
            "openplantrace.rust.scan.v6" => crate::placement::validate_preview7(p)?,
            "openplantrace.rust.scan.v7" => crate::placement::validate_preview8(p)?,
            "openplantrace.rust.scan.v8"
            | "openplantrace.rust.scan.v9"
            | "openplantrace.rust.scan.v10"
            | "openplantrace.rust.scan.v11"
            | "openplantrace.rust.scan.v12"
            | "openplantrace.rust.scan.v13"
            | "openplantrace.rust.scan.v14"
            | "openplantrace.rust.scan.v15"
            | "openplantrace.rust.scan.v16" => {
                if s.schema_version == "openplantrace.rust.scan.v16" {
                    crate::placement::validate(p, true)?;
                } else if s.schema_version == "openplantrace.rust.scan.v15" {
                    crate::placement::validate_preview19(p, true)?;
                } else if s.schema_version == "openplantrace.rust.scan.v14" {
                    crate::placement::validate_preview18(p, true)?;
                } else if s.schema_version == "openplantrace.rust.scan.v13" {
                    crate::placement::validate_preview17(p, true)?;
                } else {
                    crate::placement::validate_preview16(p, true)?;
                }
                let saved = p
                    .exterior_enclosures
                    .as_ref()
                    .ok_or("missing exterior enclosure inventory")?;
                if *saved != crate::placement::enclosures::build(p) {
                    return Err(format!(
                        "exterior enclosure inventory disagrees with source evidence on page {}",
                        p.page_number
                    ));
                }
            }
            _ => crate::placement::validate(p, false)?,
        }
        if !matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v8"
                | "openplantrace.rust.scan.v9"
                | "openplantrace.rust.scan.v10"
                | "openplantrace.rust.scan.v11"
                | "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        ) && p.exterior_enclosures.is_some()
        {
            return Err("exterior enclosure inventory requires scan v8 or newer".into());
        }
        if matches!(
            s.schema_version.as_str(),
            "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        ) {
            let saved = p
                .exterior_connections
                .as_ref()
                .ok_or("missing exterior connection pass")?;
            let measured = crate::placement::connections::build(p)?;
            if *saved != measured {
                return Err(format!(
                    "exterior connection pass disagrees with source evidence on page {}",
                    p.page_number
                ));
            }
        } else if p.exterior_connections.is_some() {
            return Err("exterior connection pass requires scan v12 or newer".into());
        }
        match p.scale_source.as_deref() {
            Some("AgreeingFiniteDimensions") => {
                let (k, source) = crate::calibration::dimensions(&p.primitives, &p.semantics)
                    .ok_or("page calibration has no independent corroboration")?;
                if source != p.calibration_source_ids
                    || (k - p.units_to_millimeters.unwrap_or(0.)).abs() > k * 1e-10
                {
                    return Err("page scale disagrees with bound dimension observations".into());
                }
            }
            Some("InputModelUnits" | "ExplicitOverride") | None => {}
            Some(_) => return Err("unknown calibration source".into()),
        }
    }
    Ok(())
}
fn json_file(path: PathBuf, v: &impl Serialize, pretty: bool) -> Result<(), String> {
    let mut f = BufWriter::new(fs::File::create(path).map_err(|e| e.to_string())?);
    if pretty {
        serde_json::to_writer_pretty(&mut f, v)
    } else {
        serde_json::to_writer(&mut f, v)
    }
    .map_err(|e| e.to_string())?;
    f.flush().map_err(|e| e.to_string())
}
pub fn write_bundle(scan: &Scan, out: &Path, t: Option<[f64; 6]>) -> Result<(), String> {
    write_bundle_with_overlays(scan, out, t, true)
}

/// Stage a new scan or batch beside its destination and publish it in one rename.
pub fn publish_directory<T>(
    out: &Path,
    write: impl FnOnce(&Path) -> Result<T, String>,
) -> Result<T, String> {
    use std::sync::atomic::{AtomicU64, Ordering};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    if out.exists() {
        return Err("output already exists; use a fresh directory".into());
    }
    let parent = out
        .parent()
        .filter(|p| !p.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    fs::create_dir_all(parent).map_err(|e| format!("creating {}: {e}", parent.display()))?;
    let temp = parent.join(format!(
        ".openplantrace-{}-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos()
    ));
    fs::create_dir(&temp).map_err(|e| format!("creating {}: {e}", temp.display()))?;
    let result = write(&temp).and_then(|value| {
        if out
            .try_exists()
            .map_err(|e| format!("checking {}: {e}", out.display()))?
        {
            return Err(format!("output already exists: {}", out.display()));
        }
        fs::rename(&temp, out)
            .map_err(|e| format!("publishing {} to {}: {e}", temp.display(), out.display()))?;
        Ok(value)
    });
    if result.is_err() {
        let _ = fs::remove_dir_all(&temp);
    }
    result
}

pub fn write_bundle_with_overlays(
    scan: &Scan,
    out: &Path,
    t: Option<[f64; 6]>,
    overlays: bool,
) -> Result<(), String> {
    write_bundle_with_extra(scan, out, t, overlays, |_| Ok(()))
}

/// The callback can add user-selected artifacts to the same unpublished directory.
pub fn write_bundle_with_extra(
    scan: &Scan,
    out: &Path,
    t: Option<[f64; 6]>,
    overlays: bool,
    extra: impl FnOnce(&Path) -> Result<(), String>,
) -> Result<(), String> {
    validate(scan)?;
    publish_directory(out, |temp| {
        json_file(temp.join("scan.json"), scan, false)?;
        let mut gz = flate2::write::GzEncoder::new(
            BufWriter::new(fs::File::create(temp.join("scan.json.gz")).map_err(|e| e.to_string())?),
            flate2::Compression::fast(),
        );
        // Compress the bytes already serialized for this bundle in blocks. Feeding
        // the encoder one JSON token at a time dominates large drawing exports.
        std::io::copy(
            &mut fs::File::open(temp.join("scan.json")).map_err(|e| e.to_string())?,
            &mut gz,
        )
        .map_err(|e| e.to_string())?;
        gz.finish()
            .map_err(|e| e.to_string())?
            .flush()
            .map_err(|e| e.to_string())?;
        json_file(temp.join("structure.json"), &structure(scan), false)?;
        json_file(temp.join("placement.json"), &placement(scan), false)?;
        if matches!(
            scan.schema_version.as_str(),
            "openplantrace.rust.scan.v12"
                | "openplantrace.rust.scan.v13"
                | "openplantrace.rust.scan.v14"
                | "openplantrace.rust.scan.v15"
                | "openplantrace.rust.scan.v16"
        ) {
            json_file(temp.join("connections.json"), &connections(scan), false)?;
        }
        json_file(temp.join("plan.local.json"), &spatial(scan, None)?, false)?;
        if let Some(t) = t {
            json_file(temp.join("plan.geojson"), &spatial(scan, Some(t))?, false)?;
        }
        if !overlays {
            return extra(temp);
        }
        for (i, p) in scan.pages.iter().enumerate() {
            let prefix = if i == 0 {
                String::new()
            } else {
                format!("page-{}-", p.page_number)
            };
            for (name, open) in [("walls", false), ("openings", true)] {
                fs::write(temp.join(format!("{prefix}{name}.svg")), svg(p, open))
                    .map_err(|e| e.to_string())?;
            }
            if p.exterior_connections.is_some() {
                fs::write(
                    temp.join(format!("{prefix}connections.svg")),
                    connections_svg(p),
                )
                .map_err(|e| e.to_string())?;
            }
        }
        extra(temp)
    })
}
