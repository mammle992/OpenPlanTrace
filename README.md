# OpenPlanTrace: experimental Rust alpha

**Version 0.12.32-alpha.20. Human review is required before using wall placement.**

Rust is the primary implementation on `main`. The previous C# version is preserved on the [legacy C# branch](https://github.com/mammle992/OpenPlanTrace/tree/codex/legacy-csharp-before-rust). The Rust release remains experimental.

OpenPlanTrace reads vector, raster and mixed PDFs and ASCII DXF drawings, then exports observed geometry, wall-readiness decisions, physical openings and supported connections. Production PDF scanning runs in Rust and does not require Poppler, PDFium or Python.

This experimental release qualifies insulated wall finishes using their actual source contours and visibility through PDF paint. Earlier background paint can be distinguished from a later erased finish, while remote contour closures remain protected. Supporting contour edges do not become wall material.

## Try the Windows build

Download the [Windows x64 ZIP](https://github.com/mammle992/OpenPlanTrace/releases/download/v0.12.32-alpha.20/OpenPlanTrace-Rust-0.12.32-alpha.20-Windows-x64.zip) from the [experimental release](https://github.com/mammle992/OpenPlanTrace/releases/tag/v0.12.32-alpha.20) and extract it. In PowerShell:

```powershell
.\openplantrace-rs.exe --version
.\openplantrace-rs.exe scan .\samples\self-test\01-insulated-wall.pdf --out .\first-scan
.\openplantrace-rs.exe validate .\first-scan\scan.json
.\openplantrace-rs.exe validate .\first-scan\placement.json --source-scan .\first-scan\scan.json
```

Choose a fresh output directory for each scan. See [usage](docs/alpha/USAGE.md) and the [synthetic self-test pack](samples/self-test/README.md).

## Measured results and limits

- Development placement controls improved from **19/22 to 20/22**, adding five source-reviewed spans. All **176 prior Ready placements**, **32 clear areas**, **9 signed doorway controls**, observed windows and existing supported links were preserved.
- One validation project gained a verified partition. Fully covered material samples there improved from **1/34 to 2/34**; **32 sampled material areas still lack full coverage**. These are separate samples, not overall accuracy percentages.
- Five paired runs compared JSON and source-aligned images. The source passed **351 Rust tests**, with two existing tests ignored; the private evaluation also used **51 public adversarial cases**, **23 native corruptions** and **360 native validations**.
- Reference process runtime increased **16.4%**. The intermediate plan's median rose from **0.462 to 1.324 seconds**. These reference measurements are not a new timing study of the privacy-remapped distribution.
- Complete house closure, unattended placement, complete wall recall and reliable recognition of every object are not established. Unsupported joins remain review items.

Read the [public QA report](docs/alpha/PUBLIC-QA.md) for the evaluation scope, checker corrections, remaining misses and distribution provenance. Private plans, images, screenshots, scan bundles and raw QA captures are excluded. The public synthetic PDFs exercise narrow vector-paint cases and do not reproduce the private-plan results.

The Windows package contains the scanner and dependency notices. Optional trained model bundles, training metadata and the model evaluator remain outside this distribution; the reported wall-scanning evaluation used no learned inference.

## Build from source

Install Rust **1.99.1**. From this source snapshot:

```sh
cargo test --locked --jobs 2 --manifest-path rust/Cargo.toml --features onnx
cargo build --release --locked --jobs 2 --manifest-path rust/Cargo.toml --features onnx
cargo check --locked --manifest-path rust/viewer/Cargo.toml
```

The optional local viewer embeds the existing browser interface; the server and scanner are Rust. Browser PDF preview may load PDF.js. See [Rust usage](rust/README.md) and [privacy-preserving builds](docs/alpha/BUILD.md).

## Licence

OpenPlanTrace and the authored synthetic test drawings use [BSD-2-Clause](LICENSE). Retain the copyright notice, licence and disclaimer. Runtime dependencies and embedded assets retain their respective licences and notices. Client drawings are not supplied or licensed by this release.
