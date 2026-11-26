# Experimental alpha usage

## Public build status

The Windows alpha 48 package ships with a scanner, dependency notices and five generated synthetic PDFs. Verify the downloadable archive with SHA256SUMS.txt. The executable checksum is in [build provenance](BUILD-PROVENANCE.json). See [distribution parity](PUBLIC-PARITY-SUMMARY.json) for the checks against the sealed reference build and [public QA](PUBLIC-QA.md) for the original evaluation.

The reported wall-scanning checks used no learned inference. Optional trained models, model metadata and evaluator binaries stay outside the public package.

## Commands

From the Windows release directory, inspect the available commands:

```powershell
.\openplantrace-rs.exe --help
.\openplantrace-rs.exe --version
```

Scan a PDF or DXF into a new output directory:

```powershell
.\openplantrace-rs.exe scan ".\input.pdf" --out ".\scan-output"
```

The scanner writes native observations and derived structure, placement, connection and local-plan JSON. Validate native observations and a placement projection with:

```powershell
.\openplantrace-rs.exe validate ".\scan-output\scan.json" --deep
.\openplantrace-rs.exe validate ".\scan-output\placement.json" --source-scan ".\scan-output\scan.json" --deep
```

Deep validation checks document consistency and source evidence. Compare Ready wall material with the original drawing, checking furniture, physical doorways, windows, duplicate strips and exterior continuity. Inspect the review marks on uncertain joints and incomplete boundaries.

See `PUBLIC-QA.md` for the evaluation scope, runtime and remaining misses. Use an anonymous minimal reproduction when reporting a scanning problem.
