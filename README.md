# OpenPlanTrace Rust alpha

**Experimental prerelease 0.12.60-alpha.48. Check placement against the original drawing.**

OpenPlanTrace reads vector, raster and mixed PDFs and ASCII DXF. It exports observed geometry, ready wall spans, doors, windows and supported connections. Production PDF scanning runs in Rust. The earlier scanner is available on the [legacy C# branch](https://github.com/mammle992/OpenPlanTrace/tree/codex/legacy-csharp-before-rust).

## Try the Windows build

Extract the [Windows x64 ZIP](https://github.com/mammle992/OpenPlanTrace/releases/download/v0.12.60-alpha.48/OpenPlanTrace-Rust-0.12.60-alpha.48-Windows-x64.zip) from the [prerelease](https://github.com/mammle992/OpenPlanTrace/releases/tag/v0.12.60-alpha.48). From the extracted Windows-x64 folder in PowerShell:

~~~powershell
.\openplantrace-rs.exe --version
.\openplantrace-rs.exe scan .\samples\self-test\01-insulated-wall.pdf --out .\first-scan
.\openplantrace-rs.exe validate .\first-scan\scan.json --deep
.\openplantrace-rs.exe validate .\first-scan\placement.json --source-scan .\first-scan\scan.json --deep
~~~

Use a fresh output directory. The five bundled synthetic PDF examples exercise specific vector paint and wall visibility cases. See [usage](docs/alpha/USAGE.md) and [sample provenance](samples/self-test/README.md).

## Placement and QA

Alpha 48 adds source-supported exterior material and a raster wall return. It verifies the exterior role of a previously connected complex vector perimeter. Uncertain joints and incomplete walls still require review. The [public QA report](docs/alpha/PUBLIC-QA.md) covers the tested controls, runtime and limits.

## Build from source

With Rust 1.99.1:

~~~sh
cargo test --locked --manifest-path rust/Cargo.toml --features onnx
cargo build --release --locked --manifest-path rust/Cargo.toml --features onnx
cargo check --locked --manifest-path rust/viewer/Cargo.toml
~~~

See [Rust usage](rust/README.md) and [Windows build provenance](docs/alpha/BUILD.md). The shipped scanner includes optional advisory ONNX support. Trained model bundles and client drawings are excluded.

## License

OpenPlanTrace and the authored synthetic test drawings use the [BSD 2-Clause License](LICENSE). Dependencies and embedded assets retain their own notices.
