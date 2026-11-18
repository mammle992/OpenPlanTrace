use serde_json::{Value, json};
use std::{
    fs,
    path::{Path, PathBuf},
    process::{Command, Output},
    sync::atomic::{AtomicUsize, Ordering},
};

struct Scratch(PathBuf);
impl Scratch {
    fn new() -> Self {
        static N: AtomicUsize = AtomicUsize::new(0);
        let dir = Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!(
            "profile-{}-{}",
            std::process::id(),
            N.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir(&dir).unwrap();
        Self(dir)
    }
    fn file(&self, name: &str, content: &str) -> PathBuf {
        let p = self.0.join(name);
        fs::write(&p, content).unwrap();
        p
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn read(path: &Path) -> Value {
    serde_json::from_slice(&fs::read(path).unwrap()).unwrap()
}

#[test]
fn authored_symbols_apply_reviewed_profiles_and_reject_unscoped_or_forged_outputs() {
    let scratch = Scratch::new();
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).parent().unwrap();
    let drawing = root.join("samples/golden/semantic-smoke.dxf");
    let profile = root.join("docs/object-label-profile.example.json");
    let corrections = root.join("docs/object-correction-dataset.example.json");
    let generated = scratch.0.join("generated.json");
    assert!(
        run(
            "corrections-to-profile",
            &corrections,
            &["--json", generated.to_str().unwrap()]
        )
        .status
        .success()
    );
    let scan = |input: &Path, profile: &Path| {
        let dir = Scratch::new();
        let out = dir.0.join("scan.json");
        let r = run(
            "scan",
            input,
            &[
                "--object-label-profile",
                profile.to_str().unwrap(),
                "--json",
                out.to_str().unwrap(),
            ],
        );
        assert!(r.status.success(), "{}", String::from_utf8_lossy(&r.stderr));
        read(&out)
    };
    let initial = scratch.0.join("initial.json");
    assert!(
        run("scan", &drawing, &["--json", initial.to_str().unwrap()])
            .status
            .success()
    );
    let observed = read(&initial);
    assert!(observed.get("objectLabelProfile").is_none());
    let orig = &observed["pages"][0]["objects"];
    let clean = scan(&drawing, &generated);
    assert_eq!(clean["pages"][0]["objects"], *orig);
    assert_eq!(clean["objectLabelProfile"]["groups"], json!([]));
    let valve = scan(&drawing, &profile);
    let objects = &valve["pages"][0]["objects"];
    assert_eq!(objects[0], orig[0]);
    for i in [1, 2] {
        assert_eq!(objects[i]["name"], "Isolation valve");
        assert_eq!(objects[i]["category"], "Equipment");
        assert_eq!(objects[i]["sourceIds"], orig[i]["sourceIds"]);
        assert_eq!(objects[i]["requiresReview"], false);
    }
    let group = &valve["objectLabelProfile"]["groups"][0];
    assert_eq!(group["ruleIndex"], 0);
    assert_eq!(group["symbolName"], "VALVE_ISO");
    assert_eq!(group["members"][0]["sourceName"], "ISO_TAG_71");
    assert_eq!(group["members"][0]["sourceCategory"], "GenericSymbol");
    let reviewed = scratch.file("scan.json", &valve.to_string());
    assert!(run("validate", &reviewed, &[]).status.success());
    for (pointer, bad) in [
        (
            "/objectLabelProfile/groups/0/members/0/sourceName",
            json!("made up"),
        ),
        ("/objectLabelProfile/groups/0/ruleIndex", json!(1)),
        ("/pages/0/objects/1/category", json!("Furniture")),
        ("/pages/0/objectGroups/0/memberIds", json!(["p1:object:1"])),
    ] {
        let mut forgery = valve.clone();
        *forgery.pointer_mut(pointer).unwrap() = bad;
        let bad = scratch.file("forgery.json", &forgery.to_string());
        assert!(!run("validate", &bad, &[]).status.success());
    }
    let bad = scratch.file(
        "unscoped.json",
        &json!({"rules":[{"layerPattern":"*","label":"all"}]}).to_string(),
    );
    assert!(
        !run(
            "scan",
            &drawing,
            &["--object-label-profile", bad.to_str().unwrap()]
        )
        .status
        .success()
    );
    let text = fs::read_to_string(&drawing).unwrap().replace("\r\n", "\n");
    let (body, end) = text.rsplit_once("0\nENDSEC").unwrap();
    let add = |layer: &str, x: i32, y: i32, t: &str| {
        format!("0\nTEXT\n8\n{layer}\n10\n{x}\n20\n{y}\n40\n5\n1\n{t}\n")
    };
    let data = format!(
        "{body}{}{}{}0\nENDSEC{end}",
        add("X-TAGS", 172, 337, "P-101"),
        add("X-TAGS", 342, 372, "P-102"),
        add("A-ROOM-NAME", 282, 367, "P-999")
    );
    let tagged = scratch.file("tagged.dxf", &data);
    let result = scan(&tagged, &generated);
    let groups = result["objectLabelProfile"]["groups"].as_array().unwrap();
    assert_eq!(groups.len(), 1, "{groups:?}");
    let group = &groups[0];
    assert_eq!(
        group["signature"],
        "symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols"
    );
    assert_eq!(group["label"], "Pump");
    assert_eq!(group["confidence"], 0.91);
    assert_eq!(group["members"].as_array().unwrap().len(), 2);
    let tags = group["members"]
        .as_array()
        .unwrap()
        .iter()
        .flat_map(|m| m["tags"].as_array().unwrap())
        .collect::<Vec<_>>();
    assert_eq!(tags.len(), 2);
    for t in tags {
        assert!(matches!(t["value"].as_str(), Some("P-101" | "P-102")));
        assert!(t["sourceId"].as_str().unwrap().ends_with(":TEXT"))
    }
    let manifest=scratch.file("batch.json",&json!({"inputs":["tagged.dxf"],"outputDirectory":"output","objectLabelProfiles":["generated.json"]}).to_string());
    let batch = Command::new(env!("CARGO_BIN_EXE_openplantrace-rs"))
        .args(["batch", "--manifest"])
        .arg(manifest)
        .output()
        .unwrap();
    assert!(
        batch.status.success(),
        "{}",
        String::from_utf8_lossy(&batch.stderr)
    );
    assert_eq!(
        read(&scratch.0.join("output/0001-tagged/scan.json")),
        result
    );
}

#[test]
fn explicit_layers_change_geometry_keep_source_names_and_replay_in_batch() {
    let scratch = Scratch::new();
    let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let input = scratch.0.join("plan.dxf");
    fs::copy(source, &input).unwrap();
    let profile = scratch.file(
        "layers.json",
        &json!({"SchemaVersion":"openplantrace.layer-profile.v1","Overrides":[
        {"Pattern":"A-WALL","Category":"Wall","SourceFormat":"pdf"},
        {"pattern":"m-*-eqpm","category":"electrical","sourceFormat":"dxf"},
        {"pattern":"A-WALL","category":"Wall","sourceFormat":"dxf"}]})
        .to_string(),
    );
    let out = scratch.0.join("scan");
    let result = run(
        "scan",
        &input,
        &[
            "--out",
            out.to_str().unwrap(),
            "--layer-profile",
            profile.to_str().unwrap(),
            "--layer-category",
            "A-WALL=Text",
        ],
    );
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let mut scan = read(&out.join("scan.json"));
    assert_eq!(scan["layerProfile"]["sourceFormat"], "DXF");
    let page = &scan["pages"][0];
    assert_eq!(page["walls"].as_array().unwrap().len(), 0);
    assert!(
        page["objects"]
            .as_array()
            .unwrap()
            .iter()
            .any(|o| o["category"] == "ElectricalDevice")
    );
    let strokes = page["primitives"]
        .as_array()
        .unwrap()
        .iter()
        .filter(|p| p["layer"] == "A-WALL")
        .collect::<Vec<_>>();
    assert_eq!(strokes.len(), 6);
    assert!(strokes.iter().all(|p| p["layerCategoryOverride"] == "Text"));
    assert!(
        run("validate", &out.join("scan.json"), &[])
            .status
            .success()
    );

    let manifest=scratch.file("manifest.json",&json!({"inputs":["plan.dxf"],"layerProfiles":["layers.json"],"outputDirectory":"batch","layerCategoryOverrides":[{"pattern":"A-WALL","category":"Text"}]}).to_string());
    let batch = Command::new(env!("CARGO_BIN_EXE_openplantrace-rs"))
        .arg("batch")
        .arg("--manifest")
        .arg(manifest)
        .output()
        .unwrap();
    assert!(
        batch.status.success(),
        "{}",
        String::from_utf8_lossy(&batch.stderr)
    );
    assert_eq!(read(&scratch.0.join("batch/0001-plan/scan.json")), scan);
    scan["pages"][0]["primitives"][0]["layerCategoryOverride"] = json!("Grid");
    let forged = scratch.file("forged.json", &scan.to_string());
    assert!(!run("validate", &forged, &[]).status.success());
    let invalid = scratch.0.join("bad-out");
    assert!(
        !run(
            "scan",
            &input,
            &[
                "--out",
                invalid.to_str().unwrap(),
                "--layer-category",
                "A=misspelled"
            ]
        )
        .status
        .success()
    );
    assert!(!invalid.exists());
}
fn run(command: &str, input: &Path, options: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_openplantrace-rs"))
        .arg(command)
        .arg(input)
        .args(options)
        .output()
        .unwrap()
}

#[test]
fn cross_page_rules_use_source_signatures_and_restore_raw_candidates_on_reapply() {
    use openplantrace_rs::{
        export, input,
        model::{Options, Shape},
        object_labels::{Profile, apply},
        pipeline,
    };
    let fixture =
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf");
    let mut document = input::load(&fixture).unwrap();
    let mut extra = document.pages[0].clone();
    extra.number = 2;
    // Same authored symbol, deliberately on a different layer on the second page.
    let moved = extra
        .primitives
        .iter_mut()
        .find(|p| matches!(&p.shape,Shape::Insert{name,..} if name=="ISO_TAG_71"))
        .unwrap();
    moved.layer = Some("Y-SYMBOLS".into());
    document.pages.push(extra);
    let mut scan = pipeline::scan_document(document, &Options::default(), None, false).unwrap();
    let original = serde_json::to_value(&scan).unwrap();
    let source_sig = "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols";
    let p=Profile::parse(json!({"rules":[
        {"signature":source_sig.to_uppercase(),"label":"wrong signature case"},
        {"signature":source_sig,"sourceFormat":"PDF","label":"wrong format"},
        {"symbolNamePattern":"iso*71","labelPattern":"ISO_*","layerPattern":"X-*","matchCategory":"genericsymbol","matchKind":"symbol","sourceFormat":"dXf","kind":"Fixture","label":"Selected","requiresReview":false},
        {"labelPattern":"Selected","label":"cascaded incorrectly"}
    ]})).unwrap();
    let other =
        Profile::parse(json!({"rules":[{"symbolNamePattern":"ISO_*","label":"Remaining"}]}))
            .unwrap();
    apply(&mut scan, "DXF", &[p.clone(), other]).unwrap();
    export::validate(&scan).unwrap();
    let groups = &scan.object_label_profile.as_ref().unwrap().groups;
    let first = groups.iter().find(|g| g.signature == source_sig).unwrap();
    assert_eq!(first.rule_index, 2);
    assert_eq!(first.members.len(), 3);
    assert_eq!(first.kind, "Fixture");
    assert_eq!(first.profile_index, 0);
    assert_eq!(
        groups
            .iter()
            .find(|g| g.label.as_deref() == Some("Remaining"))
            .unwrap()
            .members
            .len(),
        1
    );
    apply(&mut scan, "DXF", &[p]).unwrap();
    assert_eq!(scan.pages[1].objects[1].name, "ISO_TAG_71");
    apply(&mut scan, "DXF", &[]).unwrap();
    assert_eq!(serde_json::to_value(&scan).unwrap(), original);
}
fn success(result: Output) -> Value {
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    serde_json::from_slice(&result.stdout).unwrap()
}
fn action(id: &str, decision: &str, scope: &str) -> Value {
    json!({"actionId":id,"targetKind":"Group","decision":decision,"applyScope":scope,"correctedLabel":"Pump"})
}

#[test]
fn correction_scopes_require_review_and_source_selectors() {
    let scratch = Scratch::new();
    let mut confirmed = action("1", "confirmed", "MatchingSignature");
    confirmed["signature"] = json!("symbol:pump");
    confirmed["reviewedAt"] = json!("2026-11-10T10:05:00.123Z");
    confirmed["pageNumbers"] = json!([2, 1, 0, 2]);
    let mut tag = action("2", "Corrected", "MatchingDetectedTagPattern");
    tag["detectedTags"] = json!(["P-1", "P12"]);
    let mut mixed = tag.clone();
    mixed["detectedTags"] = json!(["P-1", "E-1"]);
    let mut layer = action("3", "corrected", "MatchingSymbolAndLayer");
    layer["sourceLayers"] = json!(["PLUMB", "other"]);
    let mut wide = layer.clone();
    wide["sourceLayers"] = json!(["*"]);
    let mut ignored = confirmed.clone();
    ignored["decision"] = json!("Ignored");
    let data=json!({"Actions":[confirmed,tag,mixed,layer,wide,ignored,action("4","Confirmed","TargetOnly"),action("5","Confirmed","MatchingSymbolAndLayer")]}).to_string();
    let input = scratch.file("corrections.json", &data);
    let profile = success(run(
        "object-corrections-to-profile",
        &input,
        &["--name", "Reviewed", "--compact-json"],
    ));
    assert_eq!(profile["name"], "Reviewed");
    assert_eq!(profile["rules"].as_array().unwrap().len(), 3);
    assert_eq!(profile["rules"][0]["detectedTagPattern"], "P*");
    assert_eq!(profile["rules"][1]["layerPattern"], "PLUMB");
    assert_eq!(profile["rules"][2]["requiresReview"], false);
    assert!(
        profile["rules"][2]["evidence"]
            .as_array()
            .unwrap()
            .contains(&json!("Reviewed at 2026-11-10T10:05:00.1230000+00:00."))
    );
    assert_eq!(
        success(run("corrections-to-profile", &input, &["--confirmed-only"]))["rules"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        run(
            "corrections-to-profile",
            &input,
            &["--confirmed-only", "--corrected-only"]
        )
        .status
        .code(),
        Some(2)
    );
    assert_eq!(
        run(
            "corrections-to-profile",
            &input,
            &["--json", input.to_str().unwrap()]
        )
        .status
        .code(),
        Some(2)
    );
    assert_eq!(fs::read_to_string(input).unwrap(), data);
}

#[test]
fn crop_drafts_filter_aggregate_and_fail_before_replacing_output() {
    let scratch = Scratch::new();
    let mut rows = vec![
        json!({"reviewKey":"geometry:fixture","sourceKindCounts":[{"value":"CadSymbol","count":4}],"pageNumber":2,"detectedTags":["F-1"],"category":"Fixture","label":"fixture"}),
        json!({"groupSignature":"geometry:fixture","sourceKind":"CadSymbol","pageNumber":1,"detectedTags":["F1"],"classification":{"label":"Fan","category":"Equipment","modelName":"kvemo","confidence":0.864}}),
        json!({"reviewKey":"weak","detectedTags":["P-11"],"label":"Pump"}),
        json!({"groupSignature":"P-*","detectedTags":["A-11"],"label":"Different signed group"}),
        json!({"reviewKey":"unscoped","detectedTags":["Z-1","A-2"]}),
        json!({"reviewKey":"geometry:unreviewed","suggestedTrainingUse":"Hard-negative-review"}),
    ];
    for row in &mut rows {
        row["schemaVersion"] = json!("openplantrace.kvemo-crops.v2")
    }
    let data = rows
        .iter()
        .map(Value::to_string)
        .collect::<Vec<_>>()
        .join("\n");
    let input = scratch.file("crops.jsonl", &data);
    let draft = success(run("kvemo-crops-to-profile", &input, &[]));
    let rules = draft["rules"].as_array().unwrap();
    assert_eq!(rules.len(), 3);
    assert!(rules.iter().all(|r| r["requiresReview"] == true
        && (r["signature"].is_string() || r["detectedTagPattern"].is_string())));
    assert_eq!(rules[0]["detectedTagPattern"], "F*");
    assert_eq!(rules[0]["label"], "Fan");
    assert_eq!(rules[0]["confidence"], 0.86);
    assert!(
        rules[0]["evidence"]
            .as_array()
            .unwrap()
            .contains(&json!("Source kinds: CadSymbol:5."))
    );
    assert_eq!(
        success(run(
            "kvemo-profile-template",
            &input,
            &["--include-hard-negatives"]
        ))["rules"]
            .as_array()
            .unwrap()
            .len(),
        4
    );
    assert_eq!(
        success(run(
            "kvemo-profile-template",
            &input,
            &["--classified-only"]
        ))["rules"]
            .as_array()
            .unwrap()
            .len(),
        1
    );
    assert_eq!(
        success(run("kvemo-profile-template", &input, &["--max-rules", "1"]))["rules"][0],
        rules[0]
    );
    let output = scratch.file("profile.json", "old output");
    fs::write(&input, format!("{data}\n{{bad json}}")).unwrap();
    let bad = run(
        "kvemo-profile-template",
        &input,
        &["--json", output.to_str().unwrap()],
    );
    assert_eq!(bad.status.code(), Some(1));
    assert!(String::from_utf8_lossy(&bad.stderr).contains("line 7"));
    assert_eq!(fs::read_to_string(&output).unwrap(), "old output");
    fs::write(&input, data).unwrap();
    assert!(
        run(
            "kvemo-profile-template",
            &input,
            &["--json", output.to_str().unwrap()]
        )
        .status
        .success()
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&fs::read(output).unwrap()).unwrap(),
        draft
    );
}
