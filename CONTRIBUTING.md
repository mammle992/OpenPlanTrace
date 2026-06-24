# Contributing to OpenPlanTrace

OpenPlanTrace is a standalone .NET scanning engine for architectural floorplans. Contributions should keep the engine deterministic, explainable, and independent from downstream applications.

## Ground Rules

- Keep the core library free of downstream application dependencies.
- Do not commit proprietary PDFs, DWGs, screenshots, customer drawings, local benchmark outputs, model weights, secrets, or generated build artifacts.
- Do not claim DWG or AI support unless a real, licensed adapter or model is configured and tested.
- Prefer evidence-bearing outputs: source IDs, bounds, confidence, diagnostics, and review actions should travel with each detection.
- Keep new behavior covered by focused tests and, when possible, benchmark fixtures.

## Local Build

```powershell
dotnet restore .\OpenPlanTrace.sln
dotnet build .\OpenPlanTrace.sln --configuration Release
dotnet test .\OpenPlanTrace.sln --configuration Release
```

## Useful Checks

Run the golden benchmark:

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark .\samples\golden\benchmark.json --json .\artifacts\golden-benchmark-output.local.json --markdown .\artifacts\golden-benchmark-report.local.md
```

Run a local PDF scan without committing outputs:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --out-dir .\scan-output
```

Use `artifacts/`, `real-pdf-output/`, or `*.local.*` paths for private visual QA and real-plan benchmark outputs.

## Changelog Style

- Use short one-line bullets such as `- Improved wall snapping accuracy.` or `- Small improvement to visual QA screenshots.`
- Avoid long paragraphs in changelog entries.
- Keep test and verification details in commit notes or PR notes, not the changelog.
- When the small update counter reaches `030`, move the next major work cycle to the next `0.xx.000` version.

## Pull Request Checklist

- Build passes.
- Tests pass.
- New or changed schemas are versioned and documented.
- Public samples do not contain machine-specific paths.
- Third-party dependencies or model requirements are documented in `THIRD-PARTY-NOTICES.md`.
