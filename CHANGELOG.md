# Changelog

All notable changes to OpenPlanTrace will be documented in this file.

OpenPlanTrace uses project-level semantic versions while the individual JSON
contracts keep their own schema versions.

## [0.2.0] - 2026-06-14

### Added
- Added artifact-based scanner pipeline metadata, including stage contracts,
  artifact plans, execution waves, rerun impact summaries, and dependency
  readiness diagnostics.
- Added routing-layer output for barriers, passages, obstacles, room-use hints,
  suppressed objects, and ignored routing objects.
- Added wall graph topology span export for downstream wall placement and
  endpoint repair analysis.
- Added placement JSON schema versions through `openplantrace.placement.v4`.
- Added scan JSON schema versions through `openplantrace.scan.v60`.
- Added visual snapshot schema `openplantrace.visual-snapshot.v3`.
- Added source-readiness metadata for PDF, DXF, DWG-derived, clipboard, and
  raster-derived ingestion paths.
- Added benchmark pipeline health scoring, artifact expectations, plan issue
  reporting, and benchmark comparison signals.
- Added viewer controls and diagnostics for advanced overlay review, pipeline
  data, coordinate inspection, and routing/object review.

### Changed
- Improved opening, door, window, and routing passage placement output with
  exact drawing-space and metric coordinates, including start/end points,
  reference lines, footprint bounds, footprint corners, jamb lines, host wall
  offsets, direction vectors, and confidence evidence.
- Improved wall graph repair diagnostics with severity, import impact,
  applicability, safe snap distance, review distance, and excess gap data.
- Improved object aggregation and routing suppression so furniture, vehicles,
  repeated symbols, and dense detail groups can be represented without
  polluting downstream routing.
- Improved dense/detail and surface-pattern handling so non-wall grids and
  decorative/detail linework can be isolated from structural topology.
- Expanded placement exports, scan exports, GeoJSON debug properties, CLI
  validation, and viewer samples around downstream import readiness.
- Expanded golden benchmark outputs and provided-PDF benchmark manifests for
  easy, medium, and hard sample-plan workflows.

### Fixed
- Fixed a documented schema mismatch where `openplantrace.scan.v35` metadata
  did not match its `schemaVersion` constant.
- Added schema consistency tests so documented schema metadata and constants
  cannot drift silently.
- Tightened placement consistency checks for openings and routing passages
  before marking them coordinate-ready.
- Reduced noisy import-readiness behavior for review-only dense minor routing
  details.

### Verified
- Full solution test suite passed with `442` tests.
- Schema contract tests passed with `44` tests.
- `git diff --check` reported no whitespace errors.

## [0.1.0] - 2026-06-13

### Added
- Initial public OpenPlanTrace repository and solution structure.
- Standalone .NET library projects for core scanning, PDF loading, DXF loading,
  AI integration hooks, exports, CLI tooling, and tests.
- First deterministic scanner pipeline for architectural floorplan PDFs.
- Core models for pages, primitives, sheet regions, title blocks, dimensions,
  annotations, grids, walls, wall graphs, rooms, openings, objects, diagnostics,
  confidence, calibration, and exports.
- Initial JSON, SVG, GeoJSON, placement, benchmark, and viewer support.
- MIT licensing, third-party notices, README, roadmap, samples, and baseline
  test coverage.
