use serde_json::Value;
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    process::Command,
    time::{SystemTime, UNIX_EPOCH},
};

fn workspace() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).to_owned()
}
fn temp() -> PathBuf {
    Path::new(env!("CARGO_TARGET_TMPDIR")).join(format!(
        "openplantrace-visual-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ))
}
fn png_rgb(path: &Path) -> Vec<u8> {
    let raw = fs::read(path).unwrap();
    assert_eq!(&raw[..8], b"\x89PNG\r\n\x1a\n");
    let mut offset = 8;
    let mut encoded = Vec::new();
    while offset < raw.len() {
        let n = u32::from_be_bytes(raw[offset..offset + 4].try_into().unwrap()) as usize;
        if &raw[offset + 4..offset + 8] == b"IDAT" {
            encoded.extend_from_slice(&raw[offset + 8..offset + 8 + n])
        }
        offset += 12 + n;
    }
    let mut rows = Vec::new();
    flate2::read::ZlibDecoder::new(encoded.as_slice())
        .read_to_end(&mut rows)
        .unwrap();
    rows.chunks_exact(25)
        .flat_map(|row| &row[1..])
        .copied()
        .collect()
}
#[test]
fn scan_and_batch_export_source_rgb_with_no_invented_model_scores() {
    let temp = temp();
    let exe = env!("CARGO_BIN_EXE_openplantrace-rs");
    let input = workspace().join("../samples/golden/semantic-smoke.dxf");
    for action in ["scan", "batch"] {
        let out = temp.join(action);
        let crops = out.join("source-crops");
        let status = Command::new(exe)
            .arg(action)
            .arg(&input)
            .arg("--out")
            .arg(&out)
            .arg("--visual-ai-crop-dir")
            .arg(&crops)
            .args(["--kvemo-input-width", "8", "--kvemo-input-height", "8"])
            .status()
            .unwrap();
        assert!(status.success());
        let manifest = fs::read_to_string(crops.join("kvemo-crops.jsonl")).unwrap();
        let entries: Vec<Value> = manifest
            .lines()
            .map(|s| serde_json::from_str(s).unwrap())
            .collect();
        assert_eq!(entries.len(), 2);
        for entry in entries {
            assert_eq!(
                entry["schemaVersion"],
                openplantrace_rs::visual::dataset::SCHEMA
            );
            assert!(entry["classification"].is_null());
            assert!(!entry["groupSignature"].as_str().unwrap().is_empty());
            let rgb = png_rgb(Path::new(entry["imagePath"].as_str().unwrap()));
            assert_eq!(rgb.len(), 192);
            assert_eq!(
                rgb.chunks_exact(3).filter(|c| *c == [58, 92, 170]).count(),
                1
            );
            assert_eq!(
                rgb.chunks_exact(3)
                    .filter(|c| *c == [255, 255, 255])
                    .count(),
                63
            );
        }
    }
    fs::remove_dir_all(temp).unwrap();
}
#[cfg(feature = "onnx")]
#[test]
fn optional_engine_operates_through_the_public_batch_cli_and_rejects_bad_shapes_transactionally() {
    let temp = temp();
    let exe = env!("CARGO_BIN_EXE_openplantrace-rs");
    let input = workspace().join("../samples/golden/semantic-smoke.dxf");
    let model = workspace().join("tests/fixtures/kvemo/channel-means.onnx");
    let labels = model.with_file_name("labels.txt");
    let second = temp.join("second.dxf");
    fs::create_dir_all(&temp).unwrap();
    fs::copy(&input, &second).unwrap();
    let out = temp.join("batch");
    let crops = temp.join("golden-crops");
    let cmd = Command::new(exe)
        .arg("batch")
        .arg(&input)
        .arg(&second)
        .arg("--out")
        .arg(&out)
        .arg("--kvemo-crop-dir")
        .arg(&crops)
        .arg("--visual-ai-model")
        .arg(&model)
        .arg("--kvemo-labels")
        .arg(&labels)
        .args([
            "--kvemo-input-height",
            "8",
            "--kvemo-input-width",
            "8",
            "--parallel",
            "2",
            "--kvemo-mean",
            "0,0,0",
            "--kvemo-std",
            "1,1,1",
            "--kvemo-min-confidence",
            "0.3",
        ])
        .output()
        .unwrap();
    assert!(
        cmd.status.success(),
        "{}",
        String::from_utf8_lossy(&cmd.stderr)
    );
    let data: Value = serde_json::from_slice(&fs::read(out.join("batch.json")).unwrap()).unwrap();
    assert_eq!(data["scanned"], 2);
    let combined = fs::read_to_string(crops.join("kvemo-crops.jsonl")).unwrap();
    assert_eq!(combined.lines().count(), 4);
    for line in combined.lines() {
        let entry: Value = serde_json::from_str(line).unwrap();
        assert_eq!(entry["classification"]["label"], "blue");
        assert_eq!(entry["classification"]["accepted"], true)
    }
    let rejected = temp.join("bad");
    let external = temp.join("bad-crops");
    let cmd = Command::new(exe)
        .arg("scan")
        .arg(input)
        .arg("--out")
        .arg(&rejected)
        .arg("--kvemo-crop-dir")
        .arg(&external)
        .arg("--kvemo-model")
        .arg(model)
        .args(["--kvemo-input-width", "9", "--kvemo-input-height", "8"])
        .output()
        .unwrap();
    assert!(!cmd.status.success());
    assert!(String::from_utf8_lossy(&cmd.stderr).contains("RGB input"));
    assert!(!rejected.exists() && !external.exists());
    fs::remove_dir_all(temp).unwrap();
}
