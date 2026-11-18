# Third-Party Notices

OpenPlanTrace is licensed under the BSD 2-Clause License. See [LICENSE](LICENSE).

This file summarizes the third-party packages currently referenced by the repository. It is not legal advice; review the upstream licenses before redistribution.

## Runtime Dependencies

| Package | Version | Used By | License | Project |
|---|---:|---|---|---|
| PdfPig | 0.1.14 | `OpenPlanTrace.Pdf` | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| IxMilia.Dxf | 0.8.4 | `OpenPlanTrace.Dxf` | MIT | https://github.com/ixmilia/dxf |
| Microsoft.ML.OnnxRuntime | 1.26.0 | `OpenPlanTrace.Ai` | MIT | https://github.com/microsoft/onnxruntime |
| System.Text.Encoding.CodePages | 5.0.0 | transitive through `IxMilia.Dxf` | MIT | https://github.com/dotnet/runtime |
| Microsoft.NETCore.Platforms | 5.0.0 | transitive through `IxMilia.Dxf` | MIT | https://github.com/dotnet/runtime |

## Rust dependencies

The direct dependencies of the Rust executables are listed below. The Cargo lockfiles in `rust`, `rust/viewer` and `rust/qa/replay` record transitive versions. Each crate carries its upstream license.

| Rust dependency | Locked version | License | Used by |
|---|---|---|---|
| lopdf | 0.47.0 | MIT | PDF input |
| serde, serde_json | 1.0.229, 1.0.154 | MIT or Apache-2.0 | JSON contracts |
| flate2 | 1.1.11 | MIT or Apache-2.0 | Gzip import and export |
| jsonschema | 0.61.1 | MIT | Published JSON Schema validation |
| axum, tokio, tokio-util | 0.8.9, 1.54.1, 0.7.20 | MIT | Local viewer HTTP server |
| sha2 | 0.11.1 | MIT or Apache-2.0 | Binary, source and output fingerprints |
| tract-onnx | 0.23.10 | MIT or Apache-2.0 | Optional ONNX model inference |

The browser viewer loads PDF.js 4.10.38 (Apache-2.0) to render source PDFs.

The Rust replay runner ports the polygon fill used by Pillow so that previously registered wall and clear-area measurements can be compared pixel for pixel. [Pillow copyright and permission notice](rust/qa/replay/src/walls/LICENSE-Pillow.txt)

## .NET test and development dependencies

| Package | Version | License | Project |
|---|---:|---|---|
| Microsoft.NET.Test.Sdk | 17.10.0 | MIT | https://github.com/microsoft/vstest |
| Microsoft.CodeAnalysis.CSharp | 4.11.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.CodeAnalysis.Common | 4.11.0 | MIT | https://github.com/dotnet/roslyn |
| Microsoft.CodeAnalysis.Analyzers | 3.3.4 | MIT | https://github.com/dotnet/roslyn-analyzers |
| Microsoft.CodeCoverage | 17.10.0 | MIT | https://github.com/microsoft/vstest |
| xunit | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.runner.visualstudio | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.assert | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.core | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.extensibility.core | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.extensibility.execution | 2.8.1 | Apache-2.0 | https://xunit.net |
| xunit.abstractions | 2.0.3 | Apache-2.0 | https://xunit.net |
| xunit.analyzers | 1.14.0 | Apache-2.0 | https://xunit.net |
| Newtonsoft.Json | 13.0.1 | MIT | https://www.newtonsoft.com/json |
| System.Collections.Immutable | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Reflection.Metadata | 1.6.0 / 8.0.0 | MIT | https://github.com/dotnet/runtime |

## DWG Status

OpenPlanTrace does not currently include native DWG parsing and does not bundle any proprietary DWG SDK.

DWG support should remain optional and explicit:

- Autodesk RealDWG requires a commercial/proprietary SDK license.
- Open Design Alliance Drawings SDK requires a commercial/proprietary SDK license.
- GNU LibreDWG is GPL-family software and would need careful license isolation if used.

`OpenPlanTrace.Dxf.ExternalDwgToDxfConverter` is only an external-process bridge. It does not include a DWG parser, does not grant a DWG SDK license, and should only be configured by a host application that has installed and licensed a real converter separately.

Do not claim DWG support unless a real DWG adapter or configured converter is present and tested.

## Kvemo / Visual AI Model Status

Kvemo is OpenPlanTrace's working name for the optional local Visual AI layer. OpenPlanTrace includes an optional ONNX Runtime adapter and crop-export tooling, but does not bundle AI model weights, training data, or labels.

Any model configured through `--kvemo-model` / `--visual-ai-model` is supplied by the host/user. Review the model, dataset, and label-file licenses before redistribution or commercial use. Do not claim a specific object class is supported unless a real trained model and matching labels file are present and tested.
