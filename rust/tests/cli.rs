use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};
fn cli() -> Command {
    Command::new(env!("CARGO_BIN_EXE_openplantrace-rs"))
}
fn source() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf")
}
fn temp(tag: &str) -> PathBuf {
    Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!(
        "{tag}-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ))
}
#[test]
fn complete_bundle_validates_both_encodings_and_refuses_replacement() {
    let out = temp("bundle");
    let first = cli()
        .arg("scan")
        .arg(source())
        .arg("--out")
        .arg(&out)
        .output()
        .unwrap();
    assert!(
        first.status.success(),
        "{}",
        String::from_utf8_lossy(&first.stderr)
    );
    let raw = fs::read(out.join("scan.json")).unwrap();
    let mut decoded = Vec::new();
    flate2::read::MultiGzDecoder::new(&fs::read(out.join("scan.json.gz")).unwrap()[..])
        .read_to_end(&mut decoded)
        .unwrap();
    assert_eq!(raw, decoded);
    for file in [
        "scan.json",
        "scan.json.gz",
        "structure.json",
        "placement.json",
        "connections.json",
        "plan.local.json",
    ] {
        let r = cli().arg("validate").arg(out.join(file)).output().unwrap();
        assert!(r.status.success(), "{}", String::from_utf8_lossy(&r.stderr))
    }
    assert!(
        !cli()
            .arg("scan")
            .arg(source())
            .arg("--out")
            .arg(&out)
            .output()
            .unwrap()
            .status
            .success()
    );
    assert_eq!(raw, fs::read(out.join("scan.json")).unwrap());
    let structure = out.join("structure.json");
    let mut modified: serde_json::Value =
        serde_json::from_slice(&fs::read(&structure).unwrap()).unwrap();
    modified["pages"][0]["wallRuns"][0]["thickness"] = serde_json::json!(-1);
    fs::write(&structure, serde_json::to_vec(&modified).unwrap()).unwrap();
    assert!(
        !cli()
            .arg("validate")
            .arg(structure)
            .output()
            .unwrap()
            .status
            .success()
    );
    assert!(out.join("plan.local.json").is_file());
    assert!(!out.join("plan.geojson").exists());
    for file in fs::read_dir(&out).unwrap() {
        fs::remove_file(file.unwrap().path()).unwrap()
    }
    fs::remove_dir(out).unwrap();
}
#[test]
fn invalid_geographic_transform_does_not_publish_a_partial_bundle() {
    let out = temp("rejected");
    let r = cli()
        .arg("scan")
        .arg(source())
        .arg("--out")
        .arg(&out)
        .args(["--geo-transform", "1,0,900,0,1,900"])
        .output()
        .unwrap();
    assert!(!r.status.success());
    assert!(!out.exists());
}

#[test]
fn explicit_connection_exports_can_name_their_standard_bundle_files() {
    let out = temp("connection-alias");
    let result = cli()
        .arg("scan")
        .arg(source())
        .arg("--out")
        .arg(&out)
        .arg("--connections")
        .arg(out.join("connections.json"))
        .arg("--connections-svg")
        .arg(out.join("connections.svg"))
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    assert!(out.join("connections.svg").is_file());
    assert!(
        cli()
            .arg("validate")
            .arg(out.join("connections.json"))
            .status()
            .unwrap()
            .success()
    );
    fs::remove_dir_all(out).unwrap();
}

#[test]
fn parallel_batch_preserves_each_input_and_reports_failed_retries() {
    let root = temp("batch");
    for folder in ["a", "b", "bad"] {
        let dir = root.join(folder);
        fs::create_dir_all(&dir).unwrap();
        if folder == "bad" {
            fs::write(dir.join("drawing.dxf"), b"invalid drawing").unwrap();
        } else {
            fs::copy(source(), dir.join("drawing.dxf")).unwrap();
        }
    }
    let bundle = root.join("output");
    let result = cli()
        .arg("batch")
        .arg(&root)
        .args([
            "--recursive",
            "--parallel",
            "2",
            "--retries",
            "1",
            "--no-svg",
            "--geo-transform",
            "0.001,0,0,0,0.001,0",
            "--out",
        ])
        .arg(&bundle)
        .output()
        .unwrap();
    assert!(!result.status.success());
    let summary: serde_json::Value =
        serde_json::from_slice(&fs::read(bundle.join("batch.json")).unwrap()).unwrap();
    assert_eq!(summary["total"], 3);
    assert_eq!(summary["scanned"], 2);
    assert!(
        cli()
            .arg("validate")
            .arg(bundle.join("batch.json"))
            .args(["--kind", "batch-result", "--deep"])
            .status()
            .unwrap()
            .success()
    );
    let items = summary["items"].as_array().unwrap();
    let names: std::collections::HashSet<_> =
        items.iter().filter_map(|i| i["bundle"].as_str()).collect();
    assert_eq!(names.len(), 2);
    for name in names {
        let dir = bundle.join(name);
        assert!(dir.join("plan.geojson").is_file());
        assert!(
            cli()
                .arg("validate")
                .arg(dir.join("plan.geojson"))
                .status()
                .unwrap()
                .success()
        );
        assert!(!dir.join("walls.svg").exists());
    }
    assert_eq!(
        items.iter().find(|i| i["bundle"].is_null()).unwrap()["attempts"],
        2
    );
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn geojson_validation_respects_declared_coordinate_space() {
    let path = temp("coordinates").with_extension("json");
    let mut collection = serde_json::json!({"type":"FeatureCollection","features":[{"type":"Feature","properties":{},"geometry":{"type":"Point","coordinates":[500.,600.]}}]});
    fs::write(&path, serde_json::to_vec(&collection).unwrap()).unwrap();
    assert!(!cli().arg("validate").arg(&path).status().unwrap().success());
    collection["schemaVersion"] = serde_json::json!("openplantrace.geojson.v1");
    collection["coordinateSpace"] = serde_json::json!("OpenPlanTracePageCoordinates");
    fs::write(&path, serde_json::to_vec(&collection).unwrap()).unwrap();
    assert!(cli().arg("validate").arg(&path).status().unwrap().success());
    fs::remove_file(path).unwrap();
}

#[test]
fn individual_exports_match_the_bundle_and_use_the_selected_page() {
    let root = temp("selected");
    let out = root.join("bundle");
    let scan = root.join("saved-scan.json");
    let walls = out.join("custom/saved-structure.json");
    let svgdir = out.join("overlays");
    let connections = out.join("custom/saved-connections.json");
    let connection_svg = out.join("custom/saved-connections.svg");
    let r = cli()
        .arg("scan")
        .arg(source())
        .arg("--out")
        .arg(&out)
        .arg("--json")
        .arg(&scan)
        .arg("--structure")
        .arg(&walls)
        .arg("--connections")
        .arg(&connections)
        .arg("--connections-svg")
        .arg(&connection_svg)
        .arg("--svg-dir")
        .arg(&svgdir)
        .args(["--page", "1", "--compact-json", "--min-wall-length", "24"])
        .output()
        .unwrap();
    assert!(r.status.success(), "{}", String::from_utf8_lossy(&r.stderr));
    assert_eq!(
        fs::read(&scan).unwrap(),
        fs::read(out.join("scan.json")).unwrap()
    );
    assert_eq!(
        fs::read(&walls).unwrap(),
        fs::read(out.join("structure.json")).unwrap()
    );
    assert_eq!(
        fs::read(&connections).unwrap(),
        fs::read(out.join("connections.json")).unwrap()
    );
    assert_eq!(
        fs::read(&connection_svg).unwrap(),
        fs::read(out.join("connections.svg")).unwrap()
    );
    assert!(
        cli()
            .arg("validate")
            .arg(&connections)
            .arg("--source-scan")
            .arg(&scan)
            .status()
            .unwrap()
            .success()
    );
    let mut forged: serde_json::Value =
        serde_json::from_slice(&fs::read(&connections).unwrap()).unwrap();
    forged["pages"][0]["requiresReview"] = serde_json::json!(false);
    let forged_path = root.join("forged-connections.json");
    fs::write(&forged_path, serde_json::to_vec(&forged).unwrap()).unwrap();
    assert!(
        !cli()
            .arg("validate")
            .arg(&forged_path)
            .arg("--source-scan")
            .arg(&scan)
            .status()
            .unwrap()
            .success()
    );
    assert_eq!(
        fs::read(svgdir.join("page-1.svg")).unwrap(),
        fs::read(out.join("openings.svg")).unwrap()
    );
    assert!(
        cli()
            .arg("validate")
            .arg(&walls)
            .arg("--source-scan")
            .arg(&scan)
            .status()
            .unwrap()
            .success()
    );
    let fresh = root.join("invalid");
    assert!(
        !cli()
            .arg("scan")
            .arg(source())
            .arg("--svg")
            .arg(&fresh)
            .args(["--page", "2"])
            .status()
            .unwrap()
            .success()
    );
    assert!(!fresh.exists());
    fs::remove_dir_all(root).unwrap();
}

#[test]
fn compact_output_round_trips_and_rejects_invalid_dictionary_references() {
    let root = temp("dictionary");
    let raw = root.join("scan.json");
    let compact = root.join("compact.json");
    let gzip = root.join("compact.json.gz");
    let output = cli()
        .arg("scan")
        .arg(source())
        .arg("--json")
        .arg(&raw)
        .arg("--compact-scan")
        .arg(&compact)
        .arg("--compact-scan-gzip")
        .arg(&gzip)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let expected: serde_json::Value = serde_json::from_slice(&fs::read(&raw).unwrap()).unwrap();
    let mut document: serde_json::Value =
        serde_json::from_slice(&fs::read(&compact).unwrap()).unwrap();
    assert_eq!(
        openplantrace_rs::compact::decode(&document).unwrap(),
        expected
    );
    assert!(
        cli()
            .arg("validate")
            .arg(&gzip)
            .args(["--kind", "scan-compact"])
            .status()
            .unwrap()
            .success()
    );
    document["data"] = serde_json::json!([2, 100000000]);
    assert!(openplantrace_rs::compact::decode(&document).is_err());
    fs::remove_dir_all(root).unwrap();
}

#[cfg(feature = "published-contracts")]
#[test]
fn published_schema_rejects_wrong_artifact_kind_and_invalid_fields() {
    let original = Path::new(env!("CARGO_MANIFEST_DIR")).join("../docs/layer-profile.example.json");
    assert!(
        cli()
            .arg("validate")
            .arg(&original)
            .args(["--kind", "layer-profile"])
            .status()
            .unwrap()
            .success()
    );
    assert!(
        !cli()
            .arg("validate")
            .arg(&original)
            .args(["--kind", "structure"])
            .status()
            .unwrap()
            .success()
    );
    let mut invalid: serde_json::Value =
        serde_json::from_slice(&fs::read(&original).unwrap()).unwrap();
    invalid["overrides"][0]["pattern"] = serde_json::Value::Null;
    let path = temp("invalid-layer").with_extension("json");
    fs::write(&path, serde_json::to_vec(&invalid).unwrap()).unwrap();
    assert!(!cli().arg("validate").arg(&path).status().unwrap().success());
    fs::remove_file(path).unwrap();
}

#[cfg(feature = "published-contracts")]
#[test]
fn published_structure_aliases_apply_the_same_contract_as_auto_detection() {
    let path = temp("incomplete-structure").with_extension("json");
    fs::write(&path, r#"{"schemaVersion":"openplantrace.structure.v2"}"#).unwrap();
    let inferred = cli().arg("validate").arg(&path).output().unwrap();
    assert!(!inferred.status.success());
    assert!(String::from_utf8_lossy(&inferred.stderr).contains("required"));
    for alias in ["structure", "canonical-structure", "structural-plan"] {
        let explicit = cli()
            .arg("validate")
            .arg(&path)
            .args(["--kind", alias])
            .output()
            .unwrap();
        assert!(!explicit.status.success());
        assert_eq!(explicit.stderr, inferred.stderr);
    }
    fs::remove_file(path).unwrap();
}

#[test]
fn placement_separates_door_hosts_from_ready_barriers_and_checks_edited_exports() {
    use serde_json::{Value, json};
    use std::collections::BTreeSet;
    let dir = temp("wall-placement");
    assert!(
        cli()
            .arg("scan")
            .arg(source())
            .arg("--out")
            .arg(&dir)
            .status()
            .unwrap()
            .success()
    );
    let path = dir.join("placement.json");
    let read = |path: &Path| -> Value { serde_json::from_slice(&fs::read(path).unwrap()).unwrap() };
    let original = read(&path);
    assert_eq!(original["schemaVersion"], "openplantrace.rust.placement.v3");
    let page = &original["pages"][0];
    let ids = |v: &Value| -> BTreeSet<String> {
        v.as_array()
            .unwrap()
            .iter()
            .map(|id| id.as_str().unwrap().to_owned())
            .collect()
    };
    let ready = ids(&page["wallSets"]["readyWallIds"]);
    let flanks = ids(&page["wallSets"]["evidenceOnlyWallIds"]);
    assert_eq!((ready.len(), flanks.len()), (4, 2));
    assert_eq!(page["walls"].as_array().unwrap().len(), 6);
    let barriers: BTreeSet<_> = page["barriers"]
        .as_array()
        .unwrap()
        .iter()
        .map(|b| b["wallId"].as_str().unwrap().to_owned())
        .collect();
    assert_eq!(barriers, ready);
    let door = &page["openings"][0];
    assert_eq!(ids(&door["hostWallIds"]), flanks);
    assert_eq!(door["placementHostWallIds"], json!([]));
    assert!(!door["swingObservations"].as_array().unwrap().is_empty());
    let graph = &page["wallGraph"];
    assert_eq!(graph["nodes"].as_array().unwrap().len(), 4);
    assert_eq!(graph["edges"].as_array().unwrap().len(), 4);
    for n in graph["nodes"].as_array().unwrap() {
        assert_eq!(n["degree"], 2);
        assert!(ids(&n["wallIds"]).is_subset(&ready));
    }

    assert!(
        cli()
            .arg("validate")
            .arg(&path)
            .arg("--deep")
            .status()
            .unwrap()
            .success()
    );
    for (pointer, altered) in [
        ("/pages/0/wallGraph/nodes/0/degree", json!(9)),
        (
            "/pages/0/openings/0/placementHostWallIds",
            door["hostWallIds"].clone(),
        ),
        ("/pages/0/wallSets/readyWallIds", json!([])),
    ] {
        let mut edited = original.clone();
        *edited.pointer_mut(pointer).unwrap() = altered;
        fs::write(&path, serde_json::to_vec(&edited).unwrap()).unwrap();
        let check = cli().arg("validate").arg(&path).output().unwrap();
        assert!(!check.status.success(), "{pointer}");
        assert!(String::from_utf8_lossy(&check.stderr).contains("differs from the source scan"));
    }

    let scan_path = dir.join("scan.json");
    let mut scan = read(&scan_path);
    let mut forged = scan.clone();
    let row = forged["pages"][0]["placementDecisions"]
        .as_array_mut()
        .unwrap()
        .iter_mut()
        .find(|d| d["status"] == "EvidenceOnly")
        .unwrap();
    row["status"] = json!("Ready");
    fs::write(&scan_path, serde_json::to_vec(&forged).unwrap()).unwrap();
    assert!(
        !cli()
            .arg("validate")
            .arg(&scan_path)
            .output()
            .unwrap()
            .status
            .success()
    );
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementDecisions");
    fs::write(&scan_path, serde_json::to_vec(&scan).unwrap()).unwrap();
    assert!(
        !cli()
            .arg("validate")
            .arg(&scan_path)
            .output()
            .unwrap()
            .status
            .success()
    );

    // Previous Rust scans carried reviewed physical walls and no placement authority.
    scan["schemaVersion"] = json!("openplantrace.rust.scan.v2");
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("exteriorEnclosures");
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("exteriorConnections");
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementPaintIds");
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("placementPolygonPaintIds");
    scan["pages"][0]
        .as_object_mut()
        .unwrap()
        .remove("finishVisibility");
    scan["engineVersion"] = json!("0.12.26-alpha.3");
    fs::write(&scan_path, serde_json::to_vec(&scan).unwrap()).unwrap();
    let previous = openplantrace_rs::export::placement(&serde_json::from_value(scan).unwrap());
    assert_eq!(previous["schemaVersion"], "openplantrace.rust.placement.v1");
    assert_eq!(
        previous["pages"][0]["barriers"].as_array().unwrap().len(),
        6
    );
    assert!(previous["pages"][0].get("wallSets").is_none());
    fs::write(&path, serde_json::to_vec(&previous).unwrap()).unwrap();
    assert!(cli().arg("validate").arg(&path).status().unwrap().success());
    fs::remove_dir_all(dir).unwrap();
}
