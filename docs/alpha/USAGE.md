# Experimental alpha usage

## Public build status

The privacy-remapped public scanner is verified against the sealed reference, using the same 112 frozen production files and release configuration. Its SHA256 is `6dd19a8e61cfce657cb042793c6b41f9cbece582ca47b4d5a8d9f74f8cd89827`.

Five paired cohort rounds and five synthetic samples passed: 130 fresh scans, 325 byte-identical comparisons of normal JSON artifacts, and 390 passing public-build native validations. Synthetic Ready-wall counts are [1,1,0,0,0]. Executable bytes differ because of privacy path remapping; tested JSON exports match. The original reference evaluation timings remain separate from rebuild-parity measurements.

The public package contains the scanner, notices, documentation and generated synthetic PDFs. Optional trained models, model metadata and evaluator binaries are not included. The reported QA evaluation used no learned inference.

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
.\openplantrace-rs.exe validate ".\scan-output\scan.json"
.\openplantrace-rs.exe validate ".\scan-output\placement.json" --source-scan ".\scan-output\scan.json"
```

Schema validation checks document consistency and source evidence. It does not independently establish that every proposed wall is correct. Compare Ready wall material with the original drawing, checking furniture, physical doorways, windows, duplicate strips and exterior continuity. Source contour witnesses are supporting evidence; they do not add wall material. Flagged gaps require review, and a closed house outline is not guaranteed.

See `PUBLIC-QA.md` for the measured results, limited validation coverage, performance cost and independent-checker corrections. Use an anonymous minimal reproduction when reporting a scanning problem.
