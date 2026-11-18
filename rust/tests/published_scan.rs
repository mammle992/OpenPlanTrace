#![cfg(feature = "published-contracts")]

use serde_json::{Value, json};
use std::{fs, path::Path, process::Command};

fn validate(path: &Path, deep: bool) -> (bool, String) {
    let mut cmd = Command::new(env!("CARGO_BIN_EXE_openplantrace-rs"));
    cmd.arg("validate").arg(path).args(["--kind", "scan"]);
    if deep {
        cmd.arg("--deep");
    }
    let output = cmd.output().unwrap();
    (
        output.status.success(),
        String::from_utf8_lossy(&output.stderr).into_owned(),
    )
}

#[test]
fn published_v72_deep_validation_reconciles_rows_and_preserves_extension_artifacts() {
    let example = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../docs/examples/openplantrace.scan.example.json");
    assert_eq!(validate(&example, true), (true, String::new()));
    let original: Value = serde_json::from_slice(&fs::read(example).unwrap()).unwrap();
    let path = Path::new(env!("CARGO_TARGET_TMPDIR"))
        .join(format!("inventory-{}.json", std::process::id()));
    let mut scan = original.clone();
    let diagnostics = &mut scan["diagnostics"];
    let rows = diagnostics["artifactInventory"].as_array_mut().unwrap();
    let wall = rows
        .iter_mut()
        .find(|row| row["artifact"] == "Walls")
        .unwrap();
    let actual = wall["count"].as_i64().unwrap();
    wall["count"] = json!(actual + 1);
    fs::write(&path, serde_json::to_vec(&scan).unwrap()).unwrap();
    assert!(validate(&path, false).0);
    let (valid, error) = validate(&path, true);
    assert!(
        !valid && error.contains(&format!("artifact 'Walls' count should be {actual}")),
        "{error}"
    );

    let mut scan = original;
    let diagnostics = &mut scan["diagnostics"];
    let rows = diagnostics["artifactInventory"].as_array_mut().unwrap();
    let zero = rows.iter_mut().find(|row| row["count"] == 0).unwrap();
    zero["artifact"] = json!("VendorReviewObservations");
    fs::write(&path, serde_json::to_vec(&scan).unwrap()).unwrap();
    assert!(validate(&path, true).0);
    let rows = scan["diagnostics"]["artifactInventory"]
        .as_array_mut()
        .unwrap();
    rows.push(
        rows.iter()
            .find(|row| row["artifact"] == "VendorReviewObservations")
            .unwrap()
            .clone(),
    );
    fs::write(&path, serde_json::to_vec(&scan).unwrap()).unwrap();
    let (valid, error) = validate(&path, true);
    assert!(
        !valid && error.contains("duplicates artifact 'VendorReviewObservations'"),
        "{error}"
    );
    fs::remove_file(path).unwrap();
}
