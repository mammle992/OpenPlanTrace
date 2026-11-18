#![cfg(feature = "onnx")]
use openplantrace_rs::{
    export,
    model::Options as ScanOptions,
    pipeline,
    visual::classifier::{Classifier, Options},
};
use std::path::{Path, PathBuf};

fn options(nhwc: bool) -> Options {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/kvemo");
    let file = if nhwc {
        "channel-means-nhwc.onnx"
    } else {
        "channel-means.onnx"
    };
    let mut o = Options::new(root.join(file));
    o.channels_first = !nhwc;
    o.labels = Some(root.join("labels.txt"));
    o.width = 8;
    o.height = 8;
    o.mean = [0.; 3];
    o.std = [1.; 3];
    o
}
fn drawing() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../samples/golden/semantic-smoke.dxf")
}

#[test]
fn source_rgb_runs_in_a_real_onnx_graph_in_both_layouts() {
    let mut scan = pipeline::scan_file(&drawing(), &ScanOptions::default(), None).unwrap();
    assert!(scan.visual_classification.is_none());
    // A single observed DXF INSERT anchor supplies one blue pixel on a white 8x8 crop.
    // The ONNX fixtures each ReduceMean over their two spatial axes with no trained weights.
    let logits = [58., 92., 170.].map(|v: f64| (63. + v / 255.) / 64.);
    let denominator = logits.map(f64::exp).iter().sum::<f64>();
    for nhwc in [false, true] {
        let report = Classifier::load(options(nhwc))
            .and_then(|c| c.classify(&scan))
            .unwrap();
        assert_eq!(report.predictions.len(), 2);
        assert_eq!(report.predictions[0].object_ids.len(), 2);
        for p in &report.predictions {
            assert!(!p.accepted);
            assert_eq!(p.alternatives[0].label, "blue");
            assert_eq!(p.alternatives[0].category, "HVACEquipment");
            for a in &p.alternatives {
                assert!(
                    (f64::from(a.confidence) - logits[a.class_index].exp() / denominator).abs()
                        < 1e-6
                )
            }
        }
        scan.visual_classification = Some(report);
        export::validate(&scan).unwrap();
    }
}
#[test]
fn wrong_shape_and_missing_output_fail_before_results() {
    let scan = pipeline::scan_file(&drawing(), &ScanOptions::default(), None).unwrap();
    let mut o = options(false);
    o.width = 9;
    assert!(
        Classifier::load(o)
            .and_then(|c| c.classify(&scan))
            .unwrap_err()
            .contains("RGB input")
    );
    let mut o = options(false);
    o.width = 8;
    o.output_name = Some("missing".into());
    assert!(
        Classifier::load(o)
            .and_then(|c| c.classify(&scan))
            .unwrap_err()
            .contains("missing named ONNX output")
    );
    let mut o = options(false);
    o.model = o.model.with_file_name("pixel-map.onnx");
    assert!(
        Classifier::load(o)
            .and_then(|c| c.classify(&scan))
            .unwrap_err()
            .contains("classifier output must be")
    );
}

#[test]
fn imported_graph_varies_with_rgb_input_and_preserves_probability_outputs() {
    let engine = Classifier::load(options(false)).unwrap();
    for (rgb, label) in [([255, 0, 0], "red"), ([0, 255, 0], "green")] {
        let prediction = engine.predict_rgb(&rgb, 1, 1).unwrap();
        assert_eq!(prediction[0].label, label);
        assert_eq!(prediction[0].confidence, 1.);
    }
    let predictions = engine.predict_rgb(&[255, 0, 0, 0, 0, 255], 2, 1).unwrap();
    for record in predictions {
        assert!((record.confidence - if record.label == "green" { 0. } else { 0.5 }).abs() < 1e-6);
    }
    assert!(engine.predict_rgb(&[255, 0], 1, 1).is_err());
}
