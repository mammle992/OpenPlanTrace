use super::*;
#[test]
fn one_to_one_target_matching_and_unknown_fields_fail_closed() {
    let rows =
        vec![json!({"id":"d","pageNumber":1,"openingType":"Door","openingOperation":"Hinged"})];
    let mut checks = Vec::new();
    let door = json!({"openingType":"Door","openingOperation":"Hinged"});
    let r = targets::evaluate(
        "openings",
        &json!({"targets":[door.clone(),door]}),
        &rows,
        &mut checks,
    )
    .unwrap();
    assert_eq!(r["matchedCount"], 1);
    assert_eq!(r["recall"], 0.5);
    assert_eq!(checks[0]["passed"], false);
    assert!(
        targets::evaluate(
            "openings",
            &json!({"targets":[{"isSpaceStation":true}]}),
            &[],
            &mut checks
        )
        .is_err()
    );
}
#[test]
fn measured_wall_target_handles_direction_and_offset() {
    let p = |x, y| json!({"x":x,"y":y});
    let line = json!({"start":p(0,0),"end":p(100,0)});
    let rows = vec![json!({"id":"w","centerLine":line})];
    let mut checks = Vec::new();
    let target = json!({"centerLine":{"start":p(100,1),"end":p(0,1)},"maxLineDistance":2.0});
    let r = targets::evaluate("walls", &json!({"targets":[target]}), &rows, &mut checks).unwrap();
    assert_eq!(r["matchedCount"], 1);
    let target = json!({"centerLine":{"start":p(100,9),"end":p(0,9)},"maxLineDistance":2.0});
    let r = targets::evaluate("walls", &json!({"targets":[target]}), &rows, &mut checks).unwrap();
    assert_eq!(r["matchedCount"], 0);
}
#[test]
fn golden_manifest_reports_legacy_only_gates_and_real_native_evidence() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/benchmark.json");
    let r = benchmark(&root, &ScanSettings::default()).unwrap();
    assert_eq!(r["passed"], false);
    let case = &r["cases"][0];
    let categories: Vec<_> = case["assertions"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|a| a["name"] == "requiredLayerCategories")
        .collect();
    assert_eq!(categories.len(), 5);
    assert!(categories.iter().all(|c| c["passed"] == true));
    assert_eq!(case["scanSucceeded"], true);
    assert!(
        case["assertions"]
            .as_array()
            .unwrap()
            .iter()
            .any(|a| a["name"] == "requiredRegionKinds"
                && a["expected"] == "MainFloorPlan"
                && a["passed"] == true)
    );
    let ms = case["metrics"].as_array().unwrap();
    for key in ["rooms", "openings", "object_groups"] {
        let m = ms.iter().find(|m| m["detector"] == key).unwrap();
        assert_eq!(m["matchedCount"], 1);
    }
    assert!(
        case["assertions"]
            .as_array()
            .unwrap()
            .iter()
            .any(|a| a["name"] == "stageExpectations" && a["passed"] == false)
    );
}

#[test]
fn removing_a_previously_tested_fixture_is_a_regression() {
    let c = json!({"fixtureId":"kept","passed":true,"durationMilliseconds":0,"skipped":false,"assertions":[]});
    let a = json!({"schemaVersion":RESULT,"cases":[c]});
    let b = json!({"schemaVersion":RESULT,"cases":[]});
    let opts = Args::parse("benchmark-compare", vec![]).unwrap();
    let delta = reports::compare(&a, &b, &opts).unwrap();
    assert_eq!(delta["regressionCount"], 1);
    assert_eq!(delta["removed"], json!(["kept"]));
}

#[test]
fn serialized_csharp_drafts_keep_null_defaults_and_target_provenance_inactive() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let scan = pipeline::scan_file(&root, &Options::default(), None).unwrap();
    let native = Inventory::new(&scan);
    let spec = json!({"maxAnnotations":null,"stageExpectations":[],"requiredGridLabels":[],
        "roomMetrics":{"hasExpectations":true,"precisionScoringEnabled":false,"minPrecision":null,"targets":[
            {"id":"room","label":"OFFICE","openingType":null,"confidence":0.8,"sourcePrimitiveIds":["legacy-id"],"evidence":["reviewed"]}]}});
    let (checks, metrics) = evaluate(&spec, &native, &root, 0.).unwrap();
    assert!(checks.iter().all(|c| c["passed"] == true));
    assert_eq!(metrics[0]["matchedCount"], 1);
    let (checks, _) = evaluate(&json!({"inventedConstraint":null}), &native, &root, 0.).unwrap();
    assert!(checks.iter().any(|c| c["passed"] == false));
}

#[test]
fn benchmark_profiles_use_the_same_thresholds_and_source_objects_on_every_fixture() {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap();
    let example = root.join("docs/object-label-profile.example.json");
    let layers = root.join("docs/layer-profile.example.json");
    let args = [
        "--min-wall-length",
        "24",
        "--min-wall-fragment",
        "4",
        "--min-opening-gap",
        "8",
        "--max-opening-gap",
        "60",
        "--wall-snap",
        "2.5",
        "--max-wall-candidates",
        "3200",
        "--layer-category",
        "A-WALL=Text",
        "--layer-category",
        "A-WALL=Wall",
        "--layer-profile",
        layers.to_str().unwrap(),
        "--object-label-profile",
        example.to_str().unwrap(),
    ];
    let parsed = Args::parse("benchmark", args.iter().map(|s| s.to_string()).collect()).unwrap();
    assert_eq!(parsed.scan.thresholds.snap_tolerance, 2.5);
    assert_eq!(parsed.scan.thresholds.max_wall_seeds, 3200);
    let fixture = json!({"id":"scope","sourcePath":"semantic-smoke.dxf","expectations":{"maxWalls":0,
        "objectMetrics":{"targets":[{"objectCategory":"Equipment","text":"Isolation valve"},{"objectCategory":"Equipment","text":"Isolation valve"}]},
        "objectGroupMetrics":{"targets":[{"text":"Isolation valve","minCount":2,"requiresReview":false}]}}});
    let dir = root.join("samples/golden");
    for id in ["first", "repeat"] {
        let mut f = fixture.clone();
        f["id"] = id.into();
        let case = scan_case(&f, &dir, &parsed.scan).unwrap();
        assert_eq!(case["passed"], true, "{case}");
        assert_eq!(case["counts"]["walls"], 0);
        assert_eq!(case["counts"]["objects"], 3);
    }
    assert_eq!(
        scan_case(&fixture, &dir, &ScanSettings::default()).unwrap()["passed"],
        false
    );
    assert!(
        Args::parse(
            "benchmark-draft",
            vec![
                "--object-label-profile".into(),
                example.display().to_string()
            ]
        )
        .is_err()
    );
    assert!(
        Args::parse(
            "benchmark",
            vec!["--max-wall-candidates".into(), "2.5".into()]
        )
        .is_err()
    );
    assert!(
        Args::parse(
            "benchmark",
            vec![
                "--min-opening-gap".into(),
                "78".into(),
                "--max-opening-gap".into(),
                "30".into()
            ]
        )
        .is_err()
    );
}
