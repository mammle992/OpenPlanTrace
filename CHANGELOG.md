# Changelog

All notable changes to OpenPlanTrace will be documented in this file.

OpenPlanTrace uses project versions in `A.BC.DEF` format. `A` is the release
generation, `BC` is the major update track, and `DEF` is the small update or bug
fix counter. Individual JSON contracts keep their own schema versions.

## [0.02.014] - 2026-06-15

### Added
- Added a reusable core `WallPlacementReadinessEvaluator` and
  `WallPlacementReadiness` model so wall coordinate trust is no longer owned by
  JSON export code.
- Added direct readiness tests for safe structural walls, object-like topology
  components, and fragment-merged geometry that still needs exact-placement
  review.

### Changed
- Placement export now delegates wall reliability to the core readiness
  evaluator while preserving the existing placement JSON contract.
- Fragment-merged wall geometry marked as requiring review is now blocked from
  coordinate and metric placement instead of only being flagged with a review
  reason.
- README architecture notes now document the shared wall placement-readiness
  boundary for future host integrations.

### Verified
- Focused wall readiness, structural-topology, and export tests passed with
  `26` tests.
- Full solution test suite passed with `459` tests.
- Easy Blyverket PDF scan stayed stable with `52` walls, `5` rooms, and `26`
  openings; `47` walls are coordinate-ready and `5` are blocked/review-required.
- Hard industrial PDF scan stayed stable with `49` walls, `10` rooms, and `42`
  openings; `27` walls are coordinate-ready and `22` are blocked/review-required.
- Wall-only QA screenshots were regenerated under
  `artifacts/wall-readiness-evaluator-20260615`. Visual review confirms the
  architecture now separates risky wall-looking geometry from placement-ready
  walls, but the detector still needs a larger accuracy pass for missed short
  partitions, door/detail line contamination, and over-long snapped spans.

## [0.02.013] - 2026-06-15

### Added
- Exported per-wall `evidenceAssessment` in scan JSON and placement JSON with
  category, confidence, placement-ready, review, rejected-noise, source ID, and
  evidence fields.
- Added wall evidence assessment properties to GeoJSON wall features so mapping
  and QA tools can filter trusted, review-required, and rejected wall geometry.
- Added export tests proving placement-ready evidence is emitted and weak wall
  evidence blocks coordinate placement until reviewed.

### Changed
- Placement reliability now uses the wall evidence assessment when deciding
  whether each wall is coordinate-ready or review-required.
- The viewer now draws wall `topologySpans` before raw centerlines, so visual QA
  prefers snapped wall-graph placement geometry while remaining compatible with
  older scan JSON.
- Scan-mode viewer styling now falls back to `evidenceAssessment` when compact
  placement reliability data is not loaded.

### Verified
- Focused export, wall-pair, and schema tests passed with `81` tests.
- Full solution test suite passed with `456` tests.
- Viewer JavaScript parse check passed with bundled Node.js.
- Easy Blyverket PDF scan stayed stable with `52` walls, `5` rooms, and `26`
  openings; all `52` walls exported evidence assessments.
- Hard industrial PDF scan stayed stable with `49` walls, `10` rooms, and `42`
  openings; all `49` walls exported evidence assessments.
- Wall-only PDF-background QA screenshots were regenerated under
  `artifacts/wall-evidence-export-20260615` using topology spans. Visual review
  confirms the analyzer is clearer and contract trust is now visible, but wall
  accuracy still needs a larger detector pass for missed partitions, false
  furniture/detail walls, and bad snapped spans in dense middle areas.

## [0.02.012] - 2026-06-15

### Added
- Added a first-class wall evidence map model with wall evidence segments,
  double-edge wall bands, per-wall evidence assessments, placement-readiness
  flags, review flags, and rejected-noise counts.
- Added a dedicated `wall-evidence` pipeline stage between wall detection and
  wall graphing. Raw wall detection now produces `WallCandidates`, while the
  evidence stage is the single owner of final `Walls`.
- Added opt-in guarded missing-wall-band recovery from unclaimed parallel source
  primitives, plus evidence diagnostics for recovered and rejected wall
  candidates.
- Added opt-in wall-evidence noise rejection while keeping default evidence
  assessment non-destructive for topology safety.

### Changed
- Moved the scanner architecture closer to a professional evidence pipeline:
  wall detection collects candidates, wall evidence scores/refines them, and
  topology consumes the refined wall output.
- Kept wall evidence assessment non-destructive by default after real-plan QA
  showed that deleting suspected evidence walls can remove topology-critical
  fragments before the recovery policy is fully benchmarked.

### Verified
- Targeted wall-pair, wall-layer filtering, pipeline, and schema contract tests
  passed with `91` tests.
- Full solution test suite passed with `455` tests.
- Easy Blyverket PDF scan stayed stable with `52` walls, `5` rooms, and `26`
  openings while adding the new wall evidence artifact.
- Hard industrial PDF scan stayed stable with `49` walls, `10` rooms, and `42`
  openings after making recovery/noise deletion opt-in.
- Wall-only PDF-background QA screenshots were regenerated under
  `artifacts/wall-evidence-refinement-20260615`. Visual review confirms the
  new architecture is safer, but wall geometry is still not professional-grade:
  furniture/detail lines can still be promoted as walls, hard-plan partitions
  are still missed, and some helper/dimension-like red wall candidates remain.

## [0.02.011] - 2026-06-15

### Added
- Added a default door/detail symbol linework filter before wall seeding so
  small weak-layer door leaf/detail clusters are kept out of wall detection.
- Added a post-merge cleanup for very short, many-fragment, dimension-like
  door/detail wall candidates that survive initial seed filtering.
- Added regression tests for pre-seed door symbol filtering and post-merge
  short fragment-detail wall removal.

### Verified
- Full solution test suite passed with `455` tests.
- Easy Blyverket PDF scan passed with `52` walls, `5` rooms, `26` openings,
  `11` pre-seed door/detail primitives filtered, and `1` short post-merge
  door/detail wall removed.
- Hard industrial PDF scan stayed stable with `49` walls, `10` rooms, and
  `42` openings.
- Wall-only PDF-background QA screenshots were regenerated under
  `artifacts/door-detail-wall-filter-20260615-final`. Visual review confirms a
  small safe reduction in bathroom/entry noise, while the remaining long red
  fragment-review/helper lines still need a larger placement-wall versus
  topology-helper wall split.

## [0.02.010] - 2026-06-15

### Added
- Added a conservative default wall prefilter for dimension-like fragmented
  line runs with high healed-gap ratios, weak wall provenance, and no credible
  parallel wall-face support.
- Added focused tests proving dimension-like fragment noise is removed while
  paired fragmented wall faces are preserved for wall-body reconstruction.

### Changed
- Documented the new narrow dimension-fragment filter and kept broad dense
  fragment-run filtering opt-in because the hard PDF still needs some long
  fragment-review walls for room topology.

### Verified
- Focused wall filtering, wall-pair, and structural-topology tests passed with
  `42` tests.
- Full solution test suite passed with `453` tests.
- Easy Blyverket PDF scan passed with `53` walls, `5` rooms, `25` openings,
  and `2` conservative dimension-fragment runs filtered before wall
  reconstruction.
- Hard industrial PDF scan passed with `49` walls, `10` rooms, `42` openings,
  and no default dimension-fragment deletions, avoiding the earlier room-count
  regression from over-aggressive filtering.
- Wall-only and PDF-background QA screenshots were regenerated under
  `artifacts/dimension-fragment-filter-20260615-130720`. Visual review confirms
  topology is preserved, but the remaining red fragment-review walls are still
  visibly overextended/misaligned and need a later wall-body/topology correction
  pass instead of broader deletion.

## [0.02.009] - 2026-06-15

### Added
- Added structured `fragmentEvidence` to scan and placement wall exports so
  downstream consumers can read fragment count, healed gap length, duplicate
  collapse, gap ratio, and geometry-review status without parsing evidence
  strings.
- Added fragment-merged geometry review diagnostics for unlayered/weak wall
  candidates whose long centerlines were created by heavy fragment healing.

### Changed
- Bumped scan JSON to `openplantrace.scan.v62` and placement JSON to
  `openplantrace.placement.v6`, including updated embedded schemas, README
  notes, schema contract tests, and viewer routing sample compatibility.
- Suppressed fragment-review wall candidates from routing barrier output while
  keeping them in scan/placement wall arrays with coordinates, confidence,
  source IDs, and review-required reliability. This prevents downstream
  placement engines from using risky fragment-healed walls as exact barriers
  before the geometry is corrected.

### Verified
- Focused wall/export/schema tests passed with `80` tests.
- Focused topology/wall regression tests passed with `26` tests after routing
  suppression was added.
- Full solution test suite passed with `451` tests.
- Easy Blyverket PDF scan/export validation passed with `53` walls, `5` rooms,
  `25` openings, `2` fragment-review walls, and `0` fragment-review walls
  remaining as routing barriers.
- Hard industrial PDF scan/export validation passed with `49` walls,
  `10` rooms, `42` openings, `11` fragment-review walls, and `0`
  fragment-review walls remaining as routing barriers.
- Wall-only and PDF-background QA screenshots were regenerated for both easy
  and hard PDFs under `artifacts/wall-fragment-evidence-20260615-124033`.
  Visual review shows the risky overextended wall candidates are now explicit
  review geometry, but wall-body alignment, exterior/interior classification,
  and long false centerline removal remain major accuracy targets.

## [0.02.008] - 2026-06-15

### Changed
- Made weak isolated wall-fragment exclusion the default for structural
  topology, while preserving isolated fragments that sit near explicit
  door/window/opening evidence or surface-pattern review zones.
- Demoted topology-excluded wall components and object-like wall islands to
  `Unknown` wall type with explicit evidence instead of letting them masquerade
  as exterior or interior structure.
- Added routing diagnostics for non-structural wall components that remain in
  wall exports but are excluded from routing barriers.
- Updated the viewer wall layer to hide topology-excluded/object-like wall
  components while keeping legitimate isolated opening fragments visible.

### Verified
- Focused structural-topology regression tests passed with `5` tests,
  including a new door-fragment keeper case.
- Full solution test suite passed with `449` tests.
- Easy Blyverket PDF scan/export validation passed with `53` walls,
  `25` openings, `5` rooms, and no topology-excluded walls.
- Hard industrial PDF scan/export validation passed with `49` walls,
  `42` openings, `10` rooms, and `8` topology-excluded wall components hidden
  from the structural wall layer.
- Wall-only and PDF-background QA screenshots were regenerated for both easy
  and hard PDFs. The hard terrace/detail grid pollution is reduced, while
  wall-body continuity, exterior/interior classification, and long centerline
  over-extension remain the next accuracy targets.

## [0.02.007] - 2026-06-15

### Added
- Added a post-room `wall-type-refinement` pipeline stage that uses explicit
  room-adjacency shared-wall evidence and room-side sampling to refine exported
  `Exterior`, `Interior`, and `Unknown` wall classifications after topology and
  room solving are available.
- Added diagnostics for refined wall types, including changed wall counts,
  evidence-updated counts, room-referenced walls, and one-sided/two-sided
  room evidence counts.

### Fixed
- Kept long exterior walls from being flipped to interior merely because
  multiple rooms reference different spans of the same outside wall; interior
  override now requires explicit shared-boundary topology.
- Fixed a viewer JavaScript duplicate declaration that prevented the visualizer
  from loading, and bumped the viewer script cache key so screenshot QA uses
  the fixed bundle.

### Verified
- Focused wall-pair and scanner-pipeline tests passed with `28` tests.
- Full solution test suite passed with `448` tests.
- Easy Blyverket PDF scan/export validation passed with `53` walls and wall
  type counts of `10` exterior, `42` interior, and `1` unknown.
- Hard industrial PDF scan/export validation passed with `49` walls and wall
  type counts of `19` exterior, `29` interior, and `1` unknown.
- Wall-only PDF-background screenshots and no-background wall extraction
  screenshots were regenerated and visually reviewed. Wall type output improved,
  while endpoint trimming/over-extension remains the next major geometry issue.

## [0.02.006] - 2026-06-15

### Added
- Added deterministic wall type output (`Exterior`, `Interior`, or `Unknown`)
  on wall records so downstream engines can separate envelope walls from
  partitions without parsing evidence text.
- Bumped scan JSON to `openplantrace.scan.v61` and placement JSON to
  `openplantrace.placement.v5`, with `wallType` exposed in scan, placement,
  and GeoJSON wall exports.

### Verified
- Focused wall, export, and schema contract tests passed with `79` tests.
- Full solution test suite passed with `448` tests.
- Easy Blyverket PDF scan/export validation passed with `53` walls and wall
  type counts of `4` exterior, `48` interior, and `1` unknown.
- Hard industrial PDF scan/export validation passed with `49` walls and wall
  type counts of `3` exterior, `45` interior, and `1` unknown.
- Wall-only PDF-background screenshots were regenerated and visually reviewed;
  geometry was unchanged from the previous wall cleanup pass.

## [0.02.005] - 2026-06-15

### Changed
- Added a wall-body support filter that removes weak unpaired wall-like linework
  when a page already has enough reconstructed double-line wall bodies, reducing
  furniture/detail lines being exported as walls.
- Kept supported single-line interior partitions when their endpoints connect
  back into reconstructed wall bodies, so centerline partition evidence is not
  discarded blindly.
- Expanded wall-body cleanup to reject connected mixed-orientation symbol
  clusters and dense repeated paired wall-body bands that behave like
  roof/terrace/detail patterns instead of structural walls.
- Widened the repeated short-slot detail filter for near-threshold architectural
  detail spacing and made unsupported wall-body cleanup iterative, so rejected
  detail slots can no longer keep neighboring detail rails alive as walls.

### Verified
- Focused wall-pair reconstruction tests passed with connected symbol,
  thin paired surface-band, short repeated detail-slot, and rejected-slot support
  chain regressions.
- Full solution test suite passed with `448` tests.
- Easy Blyverket PDF scan/export validation passed with `53` walls after repeated
  garage slot/rail detail cleanup, down from `62` earlier in this pass, with
  wall-only PDF-background review screenshots.
- Hard industrial PDF scan/export validation passed with `49` walls after dense
  paired roof/terrace band and support-chain detail suppression, down from `66`
  earlier in this pass, with wall-only PDF-background review screenshots.

## [0.02.004] - 2026-06-15

### Fixed
- Changed the default SVG and viewer wall overlays to draw raw detected wall
  centerlines instead of wall-graph topology spans, so review-required snapped
  topology does not make walls appear bent or shifted in the visualizer.
- Updated visual snapshot wall-layer bounds to match the raw wall overlay.

### Verified
- Focused export tests passed.
- Full solution test suite passed with `442` tests.
- Hard industrial PDF scan/export validation passed for scan, placement, and
  visual snapshot artifacts.

## [0.02.003] - 2026-06-15

### Changed
- Added a prominent README project-link row so `CHANGELOG.md` is visible from
  the GitHub repository landing page.

### Verified
- `git diff --check` reported no whitespace errors.

## [0.02.002] - 2026-06-15

### Changed
- Tightened `.gitignore` so private/user-provided drawing inputs such as PDF,
  DWG, DXF, RVT, IFC, and Navisworks files stay local by default, while public
  golden/docs fixtures can still be added intentionally.

### Verified
- Confirmed the existing tracked public DXF fixture remains tracked.
- Cleaned ignored local viewer logs and generated scan/build output before
  publishing.

## [0.02.001] - 2026-06-15

### Changed
- Documented the project versioning and changelog rules in `README.md`.
- Aligned existing changelog headings with the `A.BC.DEF` version format.

### Verified
- `git diff --check` reported no whitespace errors.

## [0.02.000] - 2026-06-14

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

## [0.01.000] - 2026-06-13

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
