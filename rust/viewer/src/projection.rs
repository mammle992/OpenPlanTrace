//! The viewer consumes the scanner's selected walls and measured opening intervals.
use openplantrace_rs::{
    export::coverage,
    model::{Point, Scan},
};
use serde_json::{Value, json};
use std::collections::{BTreeSet, HashMap};

fn bounds(points: &[Point]) -> Value {
    let Some(&first) = points.first() else {
        return Value::Null;
    };
    let (mut low, mut high) = (first, first);
    for p in points {
        low.x = low.x.min(p.x);
        low.y = low.y.min(p.y);
        high.x = high.x.max(p.x);
        high.y = high.y.max(p.y);
    }
    json!({"x":low.x,"y":low.y,"width":high.x-low.x,"height":high.y-low.y})
}

pub fn view(scan: &Scan, name: &str) -> Value {
    let mut predictions = HashMap::new();
    if let Some(model) = &scan.visual_classification {
        for entry in model.predictions.iter().filter(|p| p.accepted) {
            let Some(best) = entry.alternatives.first() else {
                continue;
            };
            let value = json!({"label":best.label,"category":best.category,"confidence":best.confidence,
                "modelName":model.model_name,"modelVersion":model.model_version,"modelSha256":model.model_sha256,
                "inferenceEngine":model.engine,"cropBounds":bounds(&[entry.crop_min,entry.crop_max]),"alternatives":entry.alternatives});
            for id in &entry.object_ids {
                predictions.insert((entry.page_number, id.as_str()), value.clone());
            }
        }
    }
    let mut labeled = HashMap::new();
    for application in scan
        .object_label_profile
        .iter()
        .flat_map(|profile| &profile.groups)
    {
        for member in &application.members {
            labeled.insert(
                (member.page_number, member.object_id.as_str()),
                (application, member),
            );
        }
    }
    let mut pages = vec![];
    let mut walls = vec![];
    let mut rooms = vec![];
    let mut openings = vec![];
    let mut objects = vec![];
    let mut nodes = vec![];
    let mut regions = vec![];
    let mut dimensions = vec![];
    let mut axes = vec![];
    let mut groups = vec![];
    let mut scales = vec![];
    let mut messages = scan
        .warnings
        .iter()
        .map(|w| {
            json!({
                "severity":"Warning","stage":"input","message":w
            })
        })
        .collect::<Vec<_>>();
    for p in &scan.pages {
        let n = p.page_number;
        messages.extend(p.warnings.iter().map(|w| {
            json!({
                "severity":"Warning","stage":"scan","pageNumber":n,"message":w
            })
        }));
        for d in &p.semantics.diagnostics {
            let label = match d.kind {
                openplantrace_rs::semantics::SemanticLimit::ConflictingDimensionLabels => {
                    "Conflicting dimension labels"
                }
                openplantrace_rs::semantics::SemanticLimit::DimensionGridGeometry => {
                    "Dimension and grid geometry limit reached"
                }
                openplantrace_rs::semantics::SemanticLimit::DimensionGridWork => {
                    "Dimension and grid processing limit reached"
                }
                openplantrace_rs::semantics::SemanticLimit::TextGroupingWork => {
                    "Text grouping limit reached"
                }
            };
            messages.push(
                json!({"severity":"Warning","stage":"semantics","pageNumber":n,
                "code":d.kind,"message":label,"properties":{"count":d.count}}),
            );
        }
        let mut source_layers = HashMap::new();
        for source in &p.primitives {
            let layer = source
                .layer
                .as_deref()
                .map(str::trim)
                .filter(|s| !s.is_empty())
                .unwrap_or("(unlayered)");
            source_layers.insert(source.id.as_str(), layer);
        }
        let names = |ids: &[String]| {
            ids.iter()
                .filter_map(|s| source_layers.get(s.as_str()).copied())
                .collect::<BTreeSet<_>>()
        };
        if let Some(mm) = p.units_to_millimeters {
            scales.push(json!({"scope":"Page","pageNumber":n,"millimetersPerDrawingUnit":mm,"source":p.scale_source,"sourceIds":p.calibration_source_ids}));
        }
        for (i, g) in p.object_groups.iter().enumerate() {
            let mut row = json!({"id":format!("p{n}:group:{}",i+1),"pageNumber":n,"pageNumbers":[n],"label":g.name,"objectIds":g.member_ids,"count":g.member_ids.len(),"requiresReview":g.requires_review});
            if let Some(signature) = &g.profile_signature {
                row["signature"] = json!(signature);
            }
            if let Some(id) = g.member_ids.first()
                && let Some(ai) = predictions.get(&(n, id.as_str()))
            {
                row["visualAi"] = ai.clone();
            }
            groups.push(row);
        }
        let mut page = json!({"number":n,"pageNumber":n,"width":p.width,"height":p.height,"bounds":{"x":0.,"y":0.,"width":p.width,"height":p.height},"unitsToMillimeters":p.units_to_millimeters});
        if matches!(
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
                | "openplantrace.rust.scan.v44"
                | "openplantrace.rust.scan.v45"
        ) {
            if let Value::Object(fields) = openplantrace_rs::export::placement_overlays(p) {
                page.as_object_mut().unwrap().extend(fields);
            }
        }
        if let Some(connections) = &p.exterior_connections {
            page["exteriorConnections"] = json!(connections);
        }
        pages.push(page);
        let decisions: HashMap<_, _> = p
            .placement_decisions
            .iter()
            .flatten()
            .map(|decision| (decision.wall_id.as_str(), decision))
            .collect();
        for w in &p.walls {
            let original = w;
            let decision = decisions.get(w.id.as_str()).copied();
            let measured = openplantrace_rs::placement::effective_wall(w, decision);
            let w = &*measured;
            let (_, spans) = coverage(w, &p.openings);
            let mut row = json!({"id":w.id,"pageNumber":n,"thickness":w.thickness,"requiresReview":w.requires_review,
                "kind":w.body,"centerLine":{"start":w.start,"end":w.end},"bounds":bounds(&[w.start,w.end]),
                "sourcePrimitiveIds":w.source_ids,"sourceLayers":names(&w.source_ids),"nativeSolidSpans":spans,
                "placementDecision":decision});
            if w.start != original.start || w.end != original.end {
                row["observedCenterLine"] = json!({"start":original.start,"end":original.end});
            }
            walls.push(row);
        }
        for r in &p.rooms {
            rooms.push(json!({"id":r.id,"pageNumber":n,"label":r.label,"area":r.area,"bounds":bounds(&r.polygon),
            "boundary":r.polygon,"wallIds":r.wall_ids,"requiresReview":r.requires_review}));
        }
        for o in &p.openings {
            let ready_hosts = p.placement_decisions.as_ref().map(|_| {
                o.host_wall_ids
                    .iter()
                    .filter(|id| {
                        decisions.get(id.as_str()).is_some_and(|d| {
                            d.status == openplantrace_rs::placement::WallStatus::Ready
                        })
                    })
                    .collect::<Vec<_>>()
            });
            let mut row = json!({"id":o.id,"pageNumber":n,"type":o.kind,"operation":o.operation,"hinge":o.hinge,
            "centerLine":{"start":o.start,"end":o.end},"bounds":bounds(&[o.start,o.end]),"hostWallId":o.host_wall_id,
                "hostWallIds":o.host_wall_ids,"placementHostWallIds":ready_hosts,"swingObservations":o.swing_observations,
            "sourcePrimitiveIds":o.source_ids,"sourceLayers":names(&o.source_ids),"requiresReview":o.requires_review});
            if let Some(proof) = &o.physical_jambs {
                row["physicalJambs"] = json!(proof);
            }
            openings.push(row);
        }
        for o in &p.objects {
            let footprint = o
                .footprint
                .as_ref()
                .map(|f| f.polygon.as_slice())
                .or_else(|| o.geometry.as_ref().map(|g| g.polygon.as_slice()))
                .unwrap_or(std::slice::from_ref(&o.at));
            let mut row = json!({"id":o.id,"pageNumber":n,"label":o.name,"category":o.category,"position":o.at,
            "bounds":bounds(footprint),"footprint":o.footprint,"roomId":o.room_id,"sourcePrimitiveIds":o.source_ids,"sourceLayers":names(&o.source_ids),"requiresReview":o.requires_review});
            if let Some(geometry) = &o.geometry {
                row["geometry"] = json!(geometry);
            }
            if let Some(ai) = predictions.get(&(n, o.id.as_str())) {
                row["visualAi"] = ai.clone();
            }
            if let Some((group, member)) = labeled.get(&(n, o.id.as_str())) {
                row["kind"] = json!(group.kind);
                row["confidence"] = json!(group.confidence);
                row["symbolName"] =
                    json!(group.symbol_name.as_ref().or(member.symbol_name.as_ref()));
                row["detectedTags"] =
                    json!(member.tags.iter().map(|t| &t.value).collect::<Vec<_>>());
                row["bounds"] = bounds(&[member.bounds.min, member.bounds.max]);
            }
            objects.push(row);
        }
        for node in &p.nodes {
            nodes.push(json!({"id":node.id,"pageNumber":n,"position":node.position,"wallIds":node.wall_ids,"requiresReview":node.requires_review}));
        }
        for region in &p.semantics.regions {
            regions.push(json!({"id":region.id,"pageNumber":n,"kind":region.kind,"bounds":bounds(&[region.bounds.min,region.bounds.max]),
            "sourcePrimitiveIds":region.source_ids,"sourceLayers":names(&region.source_ids),"requiresReview":region.requires_review}));
        }
        for d in &p.semantics.dimensions {
            let Some(line) = &d.line else { continue };
            dimensions.push(json!({"id":d.id,"pageNumber":n,"text":d.text,"orientation":d.orientation,"line":line,"bounds":bounds(&[line.start,line.end]),"valueMillimeters":d.measured_mm,"sourcePrimitiveIds":d.text_sources,"sourceLayers":names(&d.text_sources),"requiresReview":d.requires_review}));
        }
        for a in &p.semantics.grid_axes {
            axes.push(json!({"id":a.id,"pageNumber":n,"label":a.label,"orientation":a.orientation,"line":a.line,"bounds":bounds(&[a.line.start,a.line.end]),"sourcePrimitiveIds":a.line_sources,"sourceLayers":names(&a.line_sources),"requiresReview":a.requires_review}));
        }
    }
    let common = scan
        .pages
        .first()
        .and_then(|p| p.units_to_millimeters)
        .filter(|s| {
            scan.pages
                .iter()
                .all(|p| p.units_to_millimeters == Some(*s))
        });
    let calibration=(!scales.is_empty()).then(||json!({"hasReliableMeasurementScale":common.is_some(),"millimetersPerDrawingUnit":common,"scaleGroups":scales}));
    let sources = || {
        scan.pages
            .iter()
            .flat_map(|p| p.primitives.iter().map(move |v| (p.page_number, v)))
    };
    let mut source_ids = sources().map(|(_, p)| p.id.split(':').next());
    let inferred = source_ids
        .next()
        .flatten()
        .filter(|s| matches!(*s, "pdf" | "dxf") && source_ids.all(|id| id == Some(*s)))
        .unwrap_or("");
    let inferred = if scan.pages.iter().all(|p| p.pdf_input.is_some()) {
        "PDF"
    } else {
        inferred
    };
    let (format, rules) = scan
        .layer_profile
        .as_ref()
        .map_or((inferred, &[][..]), |p| {
            (p.source_format.as_str(), p.overrides.as_slice())
        });
    let layers:Vec<_>=openplantrace_rs::layers::analyze_with_profile(format,rules,sources())
        .into_iter().map(|layer| json!({"name":layer.name,"sourceFormat":layer.source_format,
            "entityCount":layer.primitive_count,"confidence":layer.confidence,"likelyCategory":layer.likely_category,
            "pageNumbers":layer.page_numbers,"overridePattern":layer.override_pattern})).collect();
    json!({"schemaVersion":"openplantrace.rust.viewer.v1","documentId":name,"engineVersion":scan.engine_version,"pages":pages,"walls":walls,
        "layers":layers,"objectGroups":groups,"calibration":calibration,
        "diagnostics":{"warningCount":messages.len(),"messages":messages},
        "rooms":rooms,"openings":openings,"objects":objects,"nodes":nodes,"regions":regions,"dimensions":dimensions,"gridAxes":axes,"warnings":scan.warnings})
}
