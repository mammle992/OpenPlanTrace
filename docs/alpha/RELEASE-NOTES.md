# OpenPlanTrace Rust experimental alpha

Evaluation reference: `0.12.32-alpha.20`, native scan schema `v16`.

## Public build provenance — verified

The public binary was rebuilt from the same 112 frozen production files, with the same release configuration and features, using privacy-remapped compiler/dependency paths and linker debug-path metadata. Public scanner SHA256: `6dd19a8e61cfce657cb042793c6b41f9cbece582ca47b4d5a8d9f74f8cd89827`.

Five fresh paired rounds against the exact sealed reference passed on the same 12 inputs, plus five synthetic samples on both executables: **130 fresh scans, 325 byte-identical JSON artifact comparisons and 390 passing public-build native validations**. Synthetic Ready-wall counts are **[1,1,0,0,0]**. The executable bytes differ; all tested normal JSON exports match. The QA and runtime figures below remain the sealed reference evaluation's results. Rebuild-parity timing is recorded separately.

The public package includes the scanner, notices, documentation and generated synthetic PDFs. Optional trained models, their metadata and evaluator binaries are omitted and remain local. No learned inference was used for the reported scanner evaluation.

## Insulated-finish update

This alpha improves readiness for real insulated walls by checking the visibility of their narrow finishes. New insulated-finish placement needs complete batt material, opposed faces, closed finish contours and drawing-order evidence. Unsupported finish regions and uncertain gaps remain under review. Scanner and new selection work are implemented in Rust.

## Verified changes

- Existing placement controls improve **19/22 → 20/22**.
- All **176 previous Ready walls**, **32 clear areas**, **9 signed doorway controls**, **162 observed windows** and **71 Door records** are preserved.
- Five development spans and one independently source-approved validation span become Ready, producing **182 Ready walls** across the tested inputs.
- Previously supported material corners and contact/portal links remain intact. Source witness contours add no placed material.
- The new evidence rejects erased finish, unsafe text/image/stroke effects and erased remote contour caps while preserving legitimate insulated cores.

Independent testing used **five rounds, 120 fresh scanner processes and 60 pairs**. It includes **351 passing Rust tests** with **2 existing ignored tests**, **51 public PDF checks**, **23 rejected native corruption tests** and **360 source-aware artifact validations**. Full methods and the retained 15 checker flags are explained in `PUBLIC-QA.md`.

## Important limitations

This is an experimental alpha, not a complete house-tracing system. The 20/22 result is controlled placement coverage, not overall accuracy. Independent validation material samples improve only **1/34 → 2/34**; **32 samples still miss full coverage**. All 14 clear, 11 signed doorway and 10 window/panel samples remain free of placed wall material.

Complete exterior closure remains unfinished, no new nonwall contact is accepted, and raster coverage is unchanged by this update. All placements need review against the original drawing. The public package does not provide model-assisted automatic acceptance.

In the sealed reference evaluation, the median per-round sum of process wall times rises **16.4%**, and the insulated-wall target rises **0.462 → 1.324 seconds**. The finish-selection update improves verified coverage at a performance cost.

The evaluation drawings and identifying screenshots, paths and scan outputs are not part of these public documents. The sealed local report and its evidence remain unchanged.
