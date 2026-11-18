//! Reconcile a published v72 scan's final pipeline inventory with its exported decisions.
use serde_json::Value;
use std::collections::{BTreeMap, HashSet};

fn array<'a>(parent: &'a Value, key: &str) -> &'a [Value] {
    parent[key].as_array().map(Vec::as_slice).unwrap_or(&[])
}

fn nonnegative_i32(value: &Value) -> Option<i32> {
    value.as_u64().and_then(|n| i32::try_from(n).ok())
}

fn expected_counts(root: &Value) -> BTreeMap<&'static str, i64> {
    let n = |parent: &Value, key: &str| array(parent, key).len() as i64;
    let graph = &root["wallGraph"];
    let routing = &root["routingLayer"];
    let calibration = &root["calibration"];
    let diagnostics = &root["diagnostics"];
    let stage_count = |stage: &str| {
        array(diagnostics, "messages")
            .iter()
            .filter(|message| message["stage"].as_str() == Some(stage))
            .count() as i64
    };
    let mut page_counts = array(root, "pages")
        .iter()
        .filter_map(|page| nonnegative_i32(&page["primitiveCount"]));
    // Only fall back when every page lacks a valid count. A real zero is an observation.
    let primitive_count = match page_counts.next() {
        Some(first) => i64::from(page_counts.fold(first, i32::wrapping_add)),
        None => n(root, "primitiveSources"),
    };
    let annotations = array(root, "annotations");
    let annotation_items: i64 = annotations.iter().map(|a| n(a, "items")).sum();
    let classified = ["objects", "objectGroups"]
        .into_iter()
        .flat_map(|key| array(root, key))
        .filter(|item| item["visualAi"].is_object())
        .count() as i64;
    BTreeMap::from([
        ("Document", i64::from(root["document"].is_object())),
        ("Pages", n(root, "pages")),
        ("Primitives", primitive_count),
        ("Layers", n(&root["layerAnalysis"], "layers")),
        ("SheetRegions", n(root, "regions")),
        ("TitleBlocks", n(root, "titleBlocks")),
        (
            "Calibration",
            n(calibration, "evidence") + n(calibration, "scaleGroups"),
        ),
        ("Dimensions", n(root, "dimensions")),
        ("Annotations", annotations.len() as i64 + annotation_items),
        ("GridAxes", n(root, "gridAxes")),
        ("GridBays", n(root, "gridBaySpacings")),
        (
            "MeasurementConsistency",
            n(&root["measurementConsistency"], "checks"),
        ),
        ("DimensionChains", stage_count("dimension-chains")),
        ("SurfacePatterns", n(root, "surfacePatterns")),
        ("Walls", n(root, "walls")),
        (
            "WallGraph",
            n(graph, "nodes")
                + n(graph, "edges")
                + n(graph, "components")
                + n(graph, "repairCandidates"),
        ),
        ("TopologySpans", n(graph, "edges")),
        ("Openings", n(root, "openings")),
        ("Rooms", n(root, "rooms")),
        (
            "RoomAdjacency",
            n(&root["roomAdjacencyGraph"], "edges") + n(&root["roomAdjacencyGraph"], "clusters"),
        ),
        ("ObjectCandidates", n(root, "objects")),
        ("ObjectGroups", n(root, "objectGroups")),
        ("ObjectAggregates", n(root, "objectAggregates")),
        ("RoutingBarriers", n(routing, "barriers")),
        ("RoutingPassages", n(routing, "passages")),
        ("RoutingObstacles", n(routing, "obstacles")),
        ("RoutingRoomUseHints", n(routing, "roomUseHints")),
        ("RoutingSuppressedObjects", n(routing, "suppressedObjects")),
        ("RoutingIgnoredObjects", n(routing, "ignoredObjects")),
        ("VisualAiClassifications", classified),
        ("LayerConsistency", stage_count("layer-consistency")),
        ("Diagnostics", n(diagnostics, "messages")),
    ])
}

// The v72 JSON Schema has already checked the object, booleans, string arrays and required keys.
// The deep pass adds the reference CLI's nonblank, Int32 and cross-record checks.
pub(super) fn verify(root: &Value) -> Result<(), String> {
    let diagnostics = &root["diagnostics"];
    let inventory = diagnostics["artifactInventory"]
        .as_array()
        .ok_or("Scan diagnostics requires an array artifactInventory.")?;
    let expected = expected_counts(root);
    let mut seen = HashSet::new();
    let mut errors = Vec::new();
    let mut present = 0;
    let mut empty = 0;
    let mut critical = 0;
    let mut missing_critical = 0;
    for (index, item) in inventory.iter().enumerate() {
        let prefix = format!("Scan diagnostics.artifactInventory[{index}]");
        for field in ["artifact", "importance", "readinessImpact"] {
            if item[field].as_str().is_none_or(|s| s.trim().is_empty()) {
                errors.push(format!("{prefix} requires a non-empty {field}."));
            }
        }
        let Some(count) = nonnegative_i32(&item["count"]) else {
            errors.push(format!("{prefix} count must be a non-negative integer."));
            continue;
        };
        let Some(artifact) = item["artifact"].as_str().filter(|s| !s.trim().is_empty()) else {
            continue;
        };
        if !seen.insert(artifact) {
            errors.push(format!("{prefix} duplicates artifact '{artifact}'."));
        }
        let is_present = item["isPresent"] == true;
        if is_present {
            present += 1;
        } else {
            empty += 1;
        }
        if item["isImportCritical"] == true {
            critical += 1;
            missing_critical += i32::from(!is_present);
        }
        if is_present != (count > 0) {
            errors.push(format!(
                "{prefix} isPresent must match whether count is greater than 0."
            ));
        }
        if let Some(expected) = expected.get(artifact)
            && *expected != i64::from(count)
        {
            errors.push(format!("{prefix} artifact '{artifact}' count should be {expected} based on exported scan content, got {count}."));
        }
    }
    for (artifact, expected) in expected {
        if expected > 0 && !seen.contains(artifact) {
            errors.push(format!("Scan diagnostics.artifactInventory is missing artifact '{artifact}' with expected count {expected}."));
        }
    }
    for (field, count) in [
        ("availableArtifactCount", present),
        ("emptyArtifactCount", empty),
        ("importCriticalArtifactCount", critical),
        ("missingImportCriticalArtifactCount", missing_critical),
    ] {
        match nonnegative_i32(&diagnostics[field]) {
            Some(actual) if actual == count => {}
            Some(_) => errors.push(format!(
                "Scan diagnostics {field} should be {count} based on artifactInventory."
            )),
            None => errors.push(format!(
                "Scan diagnostics {field} must be a non-negative integer."
            )),
        }
    }
    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors.join("\n"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn primitive_total_prefers_valid_per_page_counts_even_when_zero() {
        let mut root = json!({"pages":[{},{"primitiveCount":-1},{"primitiveCount":1.5},{"primitiveCount":2147483648i64}], "primitiveSources":[{},{}]});
        assert_eq!(expected_counts(&root)["Primitives"], 2);
        root["pages"][0]["primitiveCount"] = json!(0);
        assert_eq!(expected_counts(&root)["Primitives"], 0);
        root["pages"][1]["primitiveCount"] = json!(4);
        root["pages"][2]["primitiveCount"] = json!(5);
        assert_eq!(expected_counts(&root)["Primitives"], 9);
    }

    #[test]
    fn inventory_formulas_use_child_records_and_exact_stage_names() {
        let root = json!({
            "document":{}, "pages":[{"primitiveCount":2}], "layerAnalysis":{"layers":[{}]},
            "regions":[{}], "titleBlocks":[{}], "calibration":{"evidence":[{}],"scaleGroups":[{},{}]},
            "dimensions":[{}], "annotations":[{"items":[{},{}]},{}], "gridAxes":[{}],
            "gridBaySpacings":[{}], "measurementConsistency":{"checks":[{}]}, "surfacePatterns":[{}],
            "walls":[{}], "wallGraph":{"nodes":[{}],"edges":[{},{}],"components":[{}],"repairCandidates":[{}]},
            "openings":[{}],"rooms":[{}], "roomAdjacencyGraph":{"edges":[{}],"clusters":[{},{}]},
            "objects":[{"visualAi":{}},{"visualAi":null}],"objectGroups":[{"visualAi":{}},{}],
            "objectAggregates":[{}], "routingLayer":{"barriers":[{}],"passages":[{}],"obstacles":[{}],
                "roomUseHints":[{}],"suppressedObjects":[{}],"ignoredObjects":[{}]},
            "diagnostics":{"messages":[{"stage":"dimension-chains"},{"stage":"DIMENSION-CHAINS"},
                {"stage":"layer-consistency"},{"stage":"layer-consistency"},{"stage":"other"}]}
        });
        let counts = expected_counts(&root);
        assert_eq!(counts.len(), 32);
        for (kind, count) in counts {
            let expected = match kind {
                "Annotations" => 4,
                "Calibration" | "RoomAdjacency" => 3,
                "WallGraph" | "Diagnostics" => 5,
                "Primitives"
                | "TopologySpans"
                | "VisualAiClassifications"
                | "LayerConsistency"
                | "ObjectCandidates"
                | "ObjectGroups" => 2,
                _ => 1,
            };
            assert_eq!(count, expected, "{kind}");
        }
    }
}
