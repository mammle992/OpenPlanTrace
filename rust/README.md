# OpenPlanTrace Rust 0.12.60-alpha.48

Experimental floor-plan scanning for vector, raster and mixed PDF and ASCII DXF. Native scan schema **v43** keeps observations separate from Ready wall material and source-supported connections. Structure v2 and placement v3 remain derived projections.

```sh
cargo test --locked --jobs 2 --manifest-path rust/Cargo.toml --features onnx
cargo run --release --locked --manifest-path rust/Cargo.toml --features onnx -- scan samples/self-test/01-insulated-wall.pdf --out scan-output
cargo run --release --locked --manifest-path rust/Cargo.toml -- validate scan-output/placement.json --source-scan scan-output/scan.json
```

Rust 1.99.1 is the tested compiler. Use a new output directory for each run. Coordinates remain drawing units unless a supported scale or explicit units are available. Read warnings and review each Ready span against the original, especially furniture, duplicate strips, physical doorways, windows and exterior gaps.

The optional local viewer can be built with:

```sh
cargo run --release --locked --manifest-path rust/viewer/Cargo.toml -- --root . --port 5077
```

Open the local address printed by the viewer. It uses the existing JavaScript/CSS browser interface; new scanner implementation is Rust. PDF browser preview can fetch PDF.js. The production scan path itself does not depend on Python, Poppler, PDFium, OCR services or trained models.

The `onnx` feature builds optional advisory-model commands. Trained weights, training metadata and the evaluator executable are omitted from the public Windows package. No learned inference was used for the reported wall-readiness results.

See the [public QA report](../docs/alpha/PUBLIC-QA.md), [usage](../docs/alpha/USAGE.md), [synthetic samples](../samples/self-test/README.md) and [build provenance](../docs/alpha/BUILD.md). Native validation checks provenance and consistency; original-source review remains necessary. Full house closure and general wall recall remain incomplete.
