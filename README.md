# OpenPlanTrace

OpenPlanTrace is a standalone .NET floorplan scanning engine for applications that need to analyze architectural PDFs, DXF files, or extracted floorplan drawings.

The project intentionally does not contain downstream application UI code. It exposes a library boundary that other applications can consume later.

**Project links:** [Changelog](CHANGELOG.md) | [Contributing](CONTRIBUTING.md) | [Security](SECURITY.md) | [License](LICENSE)

## Versioning and Changelog

OpenPlanTrace uses project versions in `A.BC.DEF` format, for example `0.02.001`.

- `A` is the release generation. `0` means the project is still in alpha.
- `BC` is the major update track for noticeable scanner, architecture, or workflow improvements.
- `DEF` is the small update counter for bug fixes, focused improvements, and documentation changes.

When `DEF` reaches `030`, the next major work cycle moves to the next `BC` track
and resets the small counter to `000`, for example `0.03.030` -> `0.04.000`.
The `0.xx.000` entries should be reserved for bigger consolidated updates rather
than tiny one-line fixes.

Every bug fix, feature update, and meaningful project change should be recorded in `CHANGELOG.md`. Keep entries short: use one-line bullets such as `- Improved wall snapping accuracy.` or `- Small improvement to visual QA screenshots.` Avoid long paragraphs and keep test/verification details out of the changelog. These project versions are separate from OpenPlanTrace's JSON contract schema versions such as `openplantrace.scan.v62`.

## Current Scope

The first engine version works from normalized floorplan drawing primitives:

- sheet regions
- main floorplan area
- title blocks, notes, dimensions, and key-plan candidate regions
- structured title-block metadata fields for project name, sheet number, sheet title, revision, issue date, scale, drawn/checked by, and discipline
- parsed dimension annotations with metric/imperial values, matched orthogonal/aligned dimension lines, conservative unlayered/PDF matching that requires witness/extension support outside explicit dimension contexts, calibration-guided expected-length scoring and implausible-match rejection when reliable scale evidence is already available, duplicate PDF dimension text suppression, witness/extension-line evidence, source IDs, evidence, drawing-unit scale hints, and chained-dimension sum QA diagnostics
- native DXF linear dimension entities normalized into dimension text/line primitives when they expose reliable text and geometry
- structured annotation blocks for general notes, keynotes, legends, schedules, revision tables, callouts, and text blocks with item markers, deterministic keynote/callout plan-marker references, source IDs, evidence, and confidence
- measurement consistency checks that compare matched dimension annotations against the selected calibration and flag scale conflicts plus high-spread dimension matches that imply unreliable or mixed scale evidence
- wall candidate detection followed by a dedicated wall evidence refinement stage, including centerline fallback, compact object-linework and door/detail symbol prefiltering that still works on weakly classified unlayered PDF content, conservative dimension-like fragment-run rejection, orthogonal and non-orthogonal duplicate/fragmented collinear wall cleanup, gap-healed fragment runs, parallel-line wall-pair reconstruction with structured face-pair evidence, wall evidence segments/bands, exported per-wall evidence assessments, placement-readiness assessment, opt-in guarded missing wall-band recovery/noise rejection, and explicit rejected-noise diagnostics
- layer-aware wall filtering, source-format-aware layer consistency checks, weak-layer ambiguity handling, and user-configurable layer category overrides so strong dimension, grid, door/window, text, equipment, and MEP layer hints are not promoted into wall segments while weak unlayered hints do not protect compact detail/object linework from filtering
- first-class non-structural surface/detail pattern candidates for dense orthogonal grids and dense parallel bands, such as terrace, hatch, overroof, or tile-like linework, with page bounds, source primitive IDs/layers, spacing/intersection evidence, confidence, review flags, and explicit exclusion from wall detection and structural topology
- structural grid/axis detection from strong grid layers and conservative length-gated unlayered/PDF endpoint-label inference with cached layer lookups, including horizontal/vertical axes, nearby labels, bubble evidence, adjacent grid bay spacings, calibrated bay distances when scale is known, source IDs, evidence, and diagnostics
- wall graph nodes and edges with endpoint, inline, corner, T-junction, crossing, and junction topology classifications, including conservative near-touch endpoint recovery for small PDF/CAD wall gaps and supported endpoint-overrun trimming where perpendicular junction evidence proves the intended wall end
- room candidates from wall-graph faces and fallback rectangular coverage, including boundary polygons, matched labels with deterministic filtering for dimension/tag/glazing fragments, deterministic room-use hints, evidence, and calibrated areas
- room adjacency graph edges and connected room clusters from shared room boundaries, including shared walls, structured opening-to-room links, directional topology, confidence, evidence, and diagnostics
- door, window, hinged-door, double-swing-door, sliding-door, pocket-sliding-door, generic wall-gap openings, swing-arc hinged/double-swing door candidates on continuous/imperfect walls, low-confidence paired-tick opening candidates, host-wall placement offsets/vectors, and connected-room evidence for openings on room boundaries
- object candidates with symbol/layer category hints, deterministic industrial tag-code classification for nearby equipment codes such as `P-101`, `TK-201`, or compact tags like `P14`, explicit detected tag/source metadata, spatial-indexed nearby text/tag assisted classification, spatial-indexed composite loose-linework object islands, promoted topology-excluded object-like wall components, room assignment, and nearby text/tag evidence
- deterministic object candidate groups for repeated CAD symbols, repeated composite linework symbols, nearby tag context, deterministic industrial/equipment tag classification, aggregated detected tags, and generic/unknown symbol review, with optional schema-versioned object label profiles for persisted user-confirmed symbol labels
- compound object aggregates that combine nearby child object candidates into one physical/semantic object, retaining child IDs/source IDs while exporting child composition counts, per-child bounds/source-kind/category summaries, routing influence, structural influence, room-use evidence, review flags, confidence, coordinates, child-suppression records, and evidence so downstream layout engines can avoid counting internal symbol parts as separate obstacles
- optional local Visual AI object classification through Kvemo, `OpenPlanTrace.Ai`, and ONNX Runtime, requiring a user-supplied model and labels file, with exported confidence, alternatives, model name/version, inference engine, crop bounds, crop source ID, and evidence; Kvemo can also export clean PNG object/group crops plus `kvemo-crops.jsonl` with visual fingerprints and similarity keys for review/training data collection without fabricating labels
- CAD/PDF layer summaries with likely categories, ranked category alternatives, evidence, and ambiguity diagnostics
- DXF block inserts that preserve the original symbol, expand reusable block geometry into normalized child primitives, and expose visible insert attributes as text primitives with inherited layers and provenance
- scale/unit calibration evidence from PDF scale text, labeled scale bars, DXF drawing units, dimension hints, grouped scale contexts with source region IDs/bounds, scope-aware default selection that does not promote key-plan/notes scale groups to whole-sheet measurements, spatial-indexed dense-page matching, measurement scale provenance, and mixed-scale diagnostics
- dimension-versus-calibration QA diagnostics for consistent dimensions, text-localized dense PDF dimension-line pools, same-line dimension text conflicts, scale outliers, high dimension-scale spread, chained dimension sum conflicts, mixed-scale sheets, and not-to-scale notes
- schema-versioned JSON export with a portable page coordinate-system contract, source document provenance, title-block, dimension, annotation item references, surface patterns, grid-axis, grid-bay spacing, deterministic room-use, room-adjacency, typed room-cluster semantic evidence, routing-layer barriers/passages/obstacles/room-use hints/suppressed objects/ignored objects, graph-junction-aware routing barriers with minor junction compression, suppression of unused isolated wall fragments and dense secondary detail patterns from trusted routing barriers, source primitive IDs, and source layers
- page-coordinate GeoJSON-style feature export for downstream QA/mapping tools, including pages, regions, surface patterns, walls, wall nodes, rooms, room adjacencies, room clusters, openings, grid axes, grid bay spacings, dimensions, annotations, annotation references, objects, object groups, object aggregates, and routing-layer features
- measured wall lengths, room areas, grid bay spacings, and opening widths when calibration is reliable, including the scale group used for each measurement when it can be assigned deterministically
- deterministic scan quality/readiness report with overall confidence, grade, detector-level confidence summaries, review-required flags, evidence-backed issues including high dimension-scale spread, and a scan-level review queue for measurement outliers, ranked/capped wall-gap and symbol-group triage, object aggregates, and openings
- professional scan-risk audit that flags sheet/title-block contamination, geometry outside the main floorplan, poorly covered fragmented wall graphs, weak source provenance, object-noise dominance, opening/room topology mismatch, and weak room-boundary evidence
- wall detection kind, structured wall-pair face evidence, dominant wall-pair separation profiling, layer evidence, orthogonal/non-orthogonal duplicate and fragment-merged wall evidence, gap-healing/filtering diagnostics, and wall-node topology evidence in exported/reviewed outputs
- placement wall reliability reasons from a reusable core `WallPlacementReadinessEvaluator`, covering object-like wall components, isolated wall graph fragments, topology-excluded detail linework, fragment-merged geometry that still needs review, surface/detail overlaps, wall-evidence review/not-placement-ready states, low-confidence walls, and missing metric scale, so downstream engines can retain exact coordinates without treating every exported wall as equally trusted structural geometry
- import-readiness-aware placement quality gates, so compact placement JSON can keep useful page coordinates and diagnostics while clearly blocking exact coordinate or metric placement when too much wall/room/opening/object geometry still requires review
- grid axis labels, adjacent bay spacing distances, source layers, and evidence in exported/reviewed outputs
- room boundary, room-label source IDs, room-use kind, and room evidence in exported/reviewed outputs
- room link counts, shared boundaries, shared wall IDs, connected-opening IDs, north/south/east/west directional topology, deterministic room-cluster kind hints, and adjacency evidence in exported/reviewed outputs
- opening orientation, operation, host walls, placement reference lines, start/end/center offsets in drawing units, calibrated millimeter offsets when scale is reliable, along/normal vectors, connected room IDs/labels, structured connected-room links, room adjacency IDs, centerline width, hinge/swing metadata when geometry supports it, pocket-door evidence when layer/track geometry supports it, swing-arc hinged/double-swing door diagnostics, paired-tick candidate diagnostics, and evidence
- object category, symbol name, room assignment, explicit detected tag/source metadata, nearby text/tag evidence including recognized industrial tag codes, deterministic group/review metadata, compound object aggregate metadata, routing/structural influence policy, and evidence for CAD symbols/blocks, compact geometry, composite loose-linework islands, and topology-excluded object-like wall components
- schema-versioned object correction datasets that capture human-reviewed symbol/object decisions and can be converted into deterministic label-profile rules
- confidence scores and structured diagnostics with stage, scope, page, region, source primitive IDs, details, and severity counts

This repository does not fake AI output. Kvemo is the working name for OpenPlanTrace's optional local Visual AI layer. It only classifies when a real model-backed classifier is configured, and model output is kept as evidence-bearing classifier output on top of the deterministic scan. PDF parsing and rendering are represented by interfaces so concrete extractors can be added without changing the scanner contract.

## Input Strategy

Applications can consume OpenPlanTrace through one engine facade while source-specific loaders handle the input format:

- `PlanSourceKind.Pdf` for architectural PDF sheets
- `PlanSourceKind.Dwg` for native CAD drawings
- `PlanSourceKind.Dxf` for open CAD exchange drawings
- `PlanSourceKind.RasterImage` and `PlanSourceKind.VectorImage` for rendered or exported drawings
- `PlanSourceKind.Clipboard` for future clipboard ingestion

Clipboard input is modeled as a wrapper around an effective content kind. For example, clipboard PDF bytes can use `PlanSourceDescriptor.FromClipboard(PlanSourceKind.Pdf)`, then route to the same PDF loader used for files. Hosts that only know the clipboard item name or MIME type can use `PlanSourceDescriptor.FromClipboardContent(...)`, which infers PDF, DXF, DWG, raster image, or SVG/vector content from MIME type first and filename extension second. Clipboard DWG/DXF payloads route through the same legal loader/capability checks as file inputs; clipboard support is not a separate parser.

DWG support should live in an optional adapter package because native DWG access usually depends on a licensed SDK or a GPL-compatible bridge. The MIT core should stay format-agnostic and consume only normalized `PlanDocument` primitives. `OpenPlanTrace.Dxf` includes a `DwgToDxfPlanDocumentLoader` boundary that accepts an `IDwgToDxfConverter` implementation supplied by the host application or a separate adapter package, then delegates the converted DXF stream to the real DXF loader. It also includes `ExternalDwgToDxfConverter`, an explicit external-process bridge for hosts that already have a licensed converter installed. OpenPlanTrace does not ship a DWG converter and the MIT CLI does not register DWG support by default.

The core exposes source capability metadata through `PlanSourceCapabilityCatalog` and `PlanDocumentLoaderRegistry.GetCapabilities()`. This lets tools report whether a format is registered, known-but-not-registered, optional-adapter-required, planned, or a wrapper without pretending unsupported parsers exist. Scan JSON also includes `document.sourceReadiness`, a compact source-audit block that reports the ingestion path, whether vector geometry is directly usable, whether DWG/raster input required an external adapter, whether OCR is still needed, and the evidence behind that assessment. Raster/OCR support has a typed adapter boundary through `IRasterPlanPrimitiveExtractor`, `RasterPlanDocumentLoader`, `RasterExtractionResult`, and `RasterPlanDocumentBuilder`, but the current MIT CLI does not ship OCR/vectorization models or invent raster detections.

## Architecture

```mermaid
flowchart LR
  A["PDF or Drawing Source"] --> B["IPlanDocumentLoader"]
  B --> C["PlanDocument primitives"]
  C --> D["OpenPlanTraceScanner"]
  D --> E["Layer and sheet region stages"]
  E --> L["Calibration stage"]
  L --> F["Wall detection stage"]
  F --> W["Wall evidence refinement stage"]
  W --> P["Wall topology preparation stage"]
  P --> G["Wall graph stage"]
  G --> H["Opening stage"]
  H --> I["Room stage"]
  I --> R["Room adjacency stage"]
  R --> J["Object candidate stage"]
  J --> O["Object grouping and aggregation"]
  O --> V["Kvemo optional Visual AI"]
  V --> K["PlanScanResult"]
```

## Coordinate Model

Inputs should be normalized into page-space units where each `PlanPage` has a rectangular page size. Geometric tolerances in `ScannerOptions` must use the same unit system as the primitives. Current heuristics assume page origin is top-left for title-block band detection.

The scanner now emits a `PlanCalibration` result. It records the chosen drawing-to-real-world conversion, grouped scale contexts, confidence, source region IDs, evidence bounds, and all evidence used to choose it. PDF scale text such as `SCALE: 1:100` can produce a page-point to millimeter conversion. Labeled scale bars such as a line with `0` and `5 m` endpoint labels can calibrate directly from drawing geometry. DXF `$INSUNITS` metadata can produce model-space measurements. Dimension text can provide a lower-confidence conversion when it is matched to nearby dimension geometry. Walls, rooms, openings, and grid bay spacings expose `measurementScaleGroupId` when OpenPlanTrace can tie the measurement to a specific scale group by source region, spatial match, or a single applicable scale. If multiple scale groups appear on one page, OpenPlanTrace emits diagnostics instead of pretending one scale applies everywhere; ambiguous measurement provenance remains unset and `measurement_scale.unassigned_detections` identifies the affected detections for review. If no reliable evidence exists, measured outputs remain null instead of being invented.

Opening exports include a `placement` object for downstream layout engines. It projects each door/window/opening onto a host-wall reference line and provides ordered start/end/center offsets in page drawing units, calibrated millimeter offsets when scale is unambiguous, opening length, host-wall parameters, along/normal vectors, cross-wall offset, confidence, and evidence. If a detected opening projects opposite the reference-line direction, the exported placement packet normalizes start/end offsets, host-wall parameters, jamb lines, and footprint corner order so downstream consumers do not have to repair reversed spans. Split-wall gaps use both adjacent wall fragments to derive a continuous reference line, while continuous-wall swing/tick openings use the actual host wall centerline.

The scanner still executes a fixed stage chain, but each stage now declares its display name, kind, required/optional input artifacts, output artifacts, and capabilities. Wall geometry is intentionally split into raw `WallCandidates`, a `WallEvidence` refinement artifact, explicit `WallTopologyPreparation`, and final `Walls`; this lets OpenPlanTrace score, recover, reject, and diagnose wall evidence before topology consumes a graph-ready wall ID set split into accepted, review-required, and unassessed inputs. Wall coordinate trust is also evaluated in the core engine through `WallPlacementReadinessEvaluator`, so JSON export, visual QA, CLI validation, and future host applications can share the same rule for whether a wall-looking segment is safe exact placement geometry. Scan diagnostics export an execution plan with per-stage dependency readiness, missing artifacts, available-before artifacts, hard/preferred dependency levels, and a runtime artifact ledger showing per-stage input snapshots, output snapshots, and changed artifact counts. Benchmark manifests can gate those artifact counts and deltas per stage, and benchmark-draft generation seeds conservative gates from reviewed scan JSON. Routing is now represented as its own pipeline stage with first-class barrier, passage, obstacle, room-use hint, suppressed-object, and ignored-object artifacts, so downstream routing noise can be benchmarked directly instead of inferred only from final counts. That makes the current linear pipeline auditable and gives future detector plugins, partial reruns, and parallel stage groups a stable contract to build on.

## Repository Layout

- `src/OpenPlanTrace`: library source
- `src/OpenPlanTrace.Pdf`: PdfPig-based PDF extraction adapter with vector/text extraction and embedded-image metadata for scanned/raster-PDF routing
- `src/OpenPlanTrace.Dxf`: IxMilia.Dxf-based DXF extraction adapter with deterministic block insert, visible insert-attribute, linear dimension entity expansion, and optional DWG-to-DXF bridge contracts
- `src/OpenPlanTrace.Ai`: optional local AI adapters, currently ONNX Runtime visual object classification
- `src/OpenPlanTrace.Export`: JSON, GeoJSON, and SVG export tools
- `tools/OpenPlanTrace.Cli`: command-line scanner
- `tools/OpenPlanTrace.Viewer`: local browser viewer for PDF scan overlays
- `tests/OpenPlanTrace.Tests`: behavior tests for the first pipeline

Public output examples are available in `docs/examples`:

- `openplantrace.scan.example.json`: schema-versioned OpenPlanTrace scan JSON
- `openplantrace.geojson.example.json`: GeoJSON-compatible page-coordinate
  `FeatureCollection`

OpenPlanTrace GeoJSON uses the standard GeoJSON `FeatureCollection` / `Feature`
shape, with geometry and properties that normal GeoJSON tooling can parse. The
export also includes allowed foreign members such as `schemaVersion` and
`coordinateSpace` to make the OpenPlanTrace contract explicit. It is not WGS84
map/GPS GeoJSON: coordinates are OpenPlanTrace page drawing units, not
longitude/latitude, unless a host application later transforms them into a real
map coordinate reference system.

## Repository Hygiene

Generated scan outputs are intentionally ignored by Git. If local smoke tests
or visual QA runs make the working folder huge, clean them with:

```powershell
.\tools\clean-local-outputs.ps1
```

The repo keeps the current scan schema contract in `docs/schemas` instead of
every generated alpha scan-schema snapshot, because old scan schemas are large
and quickly dominate GitHub's line count. Older scan schemas can be recovered
from Git history or tagged releases when needed.

## CLI

Run a scan and export JSON plus page overlays:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --out-dir .\scan-output
```

For alignment QA, render source PDF pages to images named `page-1.png`,
`page-2.png`, and so on, then pass `--svg-background-dir .\backgrounds`. The
generated SVG overlays embed those page images behind the scan geometry so wall
body footprints and clean wall spans can be inspected against the actual PDF
strokes. Use `--svg-background-opacity 0.5` to `1.0` to tune readability. This
does not change scan JSON, placement JSON, or GeoJSON; it is a visual-review
aid for screenshots and benchmark triage. Add `--svg-background-embed` when the
SVG should contain the page image as a data URI, which makes headless raster
screenshots portable even when the renderer cannot resolve external image
paths.

`--out-dir` writes `scan.json`, `scan.geojson`, `placement.json`, `overlays/page-N.svg`, and `visual-snapshot.json`. CLI SVG overlays now default to the `placement-review` profile, which draws placement-ready clean wall graph topology as merged source-wall runs in a separate `wall-topology-spans` layer for placement QA while keeping review-only, excluded, object-like, isolated-fragment, and short dangling non-placement spans out of that clean layer; pass `--svg-profile wall-qa-focus` for cropped, source-backed walls-only screenshots around clean wall topology, `--svg-profile wall-qa` for full-page clean walls-only accuracy screenshots, `--svg-profile wall-qa-review` for full-page screenshots that show clean spans plus amber non-placement/review spans, `--svg-profile structural-review` for the broader structural context, or `--svg-profile full` when you want every debug layer in the SVG. The full scan export now includes `document.sourceReadiness`, so consumers can see whether the source came from PDF vector extraction, DXF vector extraction, DWG-to-DXF conversion, raster extraction, or pre-extracted primitives before reading detector arrays. The placement export is the compact downstream-consumption packet: it preserves source document provenance (`sourceFormat`, loader, raw/effective source kind, DWG/raster adapter facts, and metadata properties), a fast import summary with counts/readiness ratios/per-page bounds, an `importReadiness` gate for geometry/metric/routing import, page-local coordinates, calibrated millimeter coordinates when scale is reliable, quality/metric trust gates, surface patterns, walls, rooms, openings with anchor offsets, object aggregates with child-composition summaries and per-child placement bounds, routing items, and placement issues without requiring consumers to parse the full scan result. Wall records keep the raw detected centerline for audit, expose `evidenceAssessment` with category/confidence/placement-ready/review/rejected-noise flags, and expose `topologySpans` as merged clean placement runs projected back onto trusted orthogonal source-wall axes; when paired wall-face evidence exists, placement spans and solid-span centerlines use the midpoint between the faces while raw wall and wall-graph coordinates remain available for audit. Downstream placement engines should prefer those span coordinates, or the stricter routing barriers, when they need snapped wall segments instead of long source primitives. The placement `wallGraph.edges` section now collapses raw fragment edges into clean topology-span edges for downstream use while preserving every original raw wall graph edge id in `sourceWallGraphEdgeIds` for audit/debug traceability. `surfacePatterns` identify dense terrace, hatch, overroof, tile-like, or similar non-structural linework that was excluded from wall detection and structural topology; each item keeps bounds, millimeter bounds when calibration is reliable, center coordinates, spacing/intersection counts, source primitive IDs/layers, confidence, evidence, and a recommended action for downstream engines. Scan JSON and visual snapshots also emit exact `SurfacePatternReview` queue items for each reviewable surface pattern, plus capped `SurfacePatternWallOverlapReview` items when a non-excluded wall still overlaps or shares source evidence with a non-structural surface pattern. Those overlaps now also surface as `quality.scan_risk.surface_pattern_wall_overlap`, `placement.wall_graph.surface_pattern_wall_overlaps.require_review`, compact placement issues named `placement.review.surface_pattern_wall_overlap`, and per-wall `reliability.reasons` entries on the affected wall records. Review tools can jump directly to the relevant pattern/wall bounds instead of interpreting broad wall-filter diagnostics. Placement issues include page numbers, page-coordinate bounds, optional millimeter bounds, confidence, recommended action, source primitive IDs/layers, evidence, and properties so suppressed dense detail patterns, surface/wall overlaps, or unanchored openings can be reviewed and mapped precisely by another engine. The viewer accepts scan/placement JSON and exposes a dedicated placement-issues overlay plus an Advanced-tab issue audit; selecting an issue shows exact page, normalized, and real-world coordinates together with source evidence and recommended action. Walls whose placement reliability or scan evidence assessment requires review or blocks coordinate placement are styled separately, counted in the legend, and listed in an Advanced-tab wall reliability audit so downstream users can distinguish safe walls from topology/placement risks. The viewer exposes placement-ready merged clean wall topology runs as a default-on layer and non-placement wall spans as an optional debug layer, and its placement wall and wall-body footprint layers intentionally draw only walls that have clean topology spans, so visual QA does not accidentally treat raw detected centerlines as placement-ready geometry. Scan-quality wall summaries, scan-risk audits including object-noise and surface-pattern overlap warnings, no-wall/no-room gates, and import-readiness ratios in both the core `PlanImportReadiness.FromScanResult(...)` API and compact placement export use structural wall entities while still exporting excluded/object-like wall components with explicit `excludedFromStructuralTopology` metadata, so downstream consumers can retain full evidence without treating intentionally non-structural linework as failed geometry. The visual snapshot is a compact QA artifact for repeatable review: it records the OpenPlanTrace page coordinate system, page bounds, SVG overlay paths, detector layer counts, per-layer bounds, confidence summaries, coverage ratio, primitive counts, scan review-queue counts/kinds/severities, compact per-page review items, and visual-review issues such as empty overlays, missing main-floorplan regions, out-of-page detections, high wall-node density, missing object aggregation, or queued scan-review work. The top-level wall-gap, surface/wall overlap, and object-group review queues are intentionally ranked and capped so they stay first-pass triage lists; the full diagnostics, `objectGroups`, review datasets, correction datasets, and placement/object exports still retain the complete candidate set.

Placement topology spans include `sourceWallGraphEdgeIds`, which lets downstream importers trace a compact merged clean wall run back to every raw wall graph edge that produced it. Placement wall graph edges may also collapse near-contiguous collinear clean spans across adjacent source wall IDs, keeping the long placement-ready run while preserving the original raw edge IDs for audit. Recovered or source-backed clean topology spans that do not originate from a raw wall graph edge are still exported as compact placement wall graph edges, so downstream consumers can rely on `wallGraph.edges` for placement-ready topology.

The placement quality gate is stricter than raw export success. A scan can
export coordinates, walls, rooms, openings, object aggregates, and review
issues while still reporting `qualityGate.readyForCoordinatePlacement = false`
when import-readiness ratios show that too many entities are omitted or
review-only. Treat that as the engine saying, "use this for QA and review, but
do not auto-place it as exact production geometry yet."

Dense local detail cleanup demotes short, weakly sourced wall candidates inside
stair/detail-like clusters unless room-boundary, room-adjacency, endpoint, or
exterior-shell evidence supports them. This reduces random clean wall spans in
dense middle-plan details, but graph-level wall splitting and wall recovery are
still the next major accuracy target for professional import quality.

Dimension-like single-line cleanup also demotes non-orthogonal, short, or
fragmented weak-layer wall candidates when no room-boundary, room-adjacency, or
exterior-shell evidence supports them. This keeps door swings, cabinets,
dimension fragments, stair details, and furniture strokes out of clean
placement spans while preserving explicitly supported wall boundaries for
review and import.

The viewer also includes an off-by-default `Raw detected walls` layer for audit-only comparison against the clean placement spans. Keep this layer off for wall-QA screenshots unless the goal is to inspect detector mistakes directly.

The `wall-qa`, `wall-qa-review`, and `wall-qa-focus` SVG profiles add a faint `sourceContext` linework layer when no PDF background image is embedded, so clean-wall screenshots remain visually comparable to the source plan without treating that context as detected output. For very large PDF primitive streams, source context is spatially prioritized around the visible wall/topology review area before the cap is applied, so the reference linework comes from the same plan neighborhood as the wall overlay instead of from unrelated first-in-file-order primitives. Use `wall-qa` or `wall-qa-focus` for wall correctness screenshots; use `wall-qa-review` when diagnosing missing walls because it separates clean placement spans from faint dashed amber review-only spans. Amber review spans are diagnostic omissions, not coordinates a downstream placement engine should import.

Opening-linked one-endpoint wall/detail fragments are suppressed from the
`wall-qa-review` amber span layer and counted as hidden suppressed detail, while
remaining available as placement issues and import-readiness review codes.

Compact orthogonal paired-wall returns can be retained as secondary structural placement context when strong wall-body evidence, pair-score evidence, and endpoint support distinguish them from disconnected object/detail linework. This helps recover small L-shaped wall returns while keeping low-score paired details, dense stair/detail clusters, and object-like components out of clean placement topology.

Endpoint-to-wall repair candidates are exported for QA without automatically punishing the host wall. A high-severity endpoint snap can still block the source/endpoint-side wall from coordinate placement, but the host wall remains eligible for clean topology when its own wall evidence, component classification, and placement spans are trustworthy.

Placement issues also include informational `placement.review.rejected_strong_wall_body` entries when a long object-like wall candidate was rejected even though it still has strong paired wall-body evidence. These entries are review beacons for possible missed structural walls; they do not promote the candidate, mark it placement-ready, or block import readiness by themselves.

The placement contract is schema-versioned as `openplantrace.placement.v11`. Its schema explicitly defines source document provenance, the `summary` block for import gating, the nested `importReadiness` block with grade, score, readiness booleans, blocking/review issue codes, recommended actions, and evidence, and the nested opening-placement anchor object, including host wall IDs, anchor wall IDs, reference lines, start/end/center offsets, length, host-wall parameters, along/normal vectors, confidence, and evidence. Placement v2 carries wall-graph repair assessment fields (`severity`, `importImpact`, `applicability`, safe-snap distance, review limit, and excess distance) in drawing units and millimeters so downstream importers can distinguish reviewable snaps from topology blockers. Placement v3 adds a compact `wallGraph` packet with graph summary counts, node positions, edge centerlines, component membership, exclusion state, metric transforms when calibration allows, and repair-candidate ID links so downstream consumers can import topology without reconstructing it from wall arrays. Placement v4 adds routing-passage placement readiness fields (`placementStatus`, `readyForCoordinatePlacement`, `requiresReview`, and `reviewReasons`) so downstream routing engines can reject unsafe door/window passage coordinates without parsing evidence text. Placement v5 adds `wallType` on exported walls (`Exterior`, `Interior`, or `Unknown`) so downstream consumers can separate envelope walls from partitions without parsing evidence text. Placement v6 adds structured wall fragment evidence, wall evidence assessment export, and review/placement-ready reliability gating so consumers can avoid trusting heavily healed, weak, rejected, or review-required wall geometry as exact placement input. Placement v7 adds closed wall-body footprint polygons, footprint bounds, along/normal vectors, and span thickness to each solid wall span so downstream placement engines can use wall rectangles directly instead of reconstructing thickness from a centerline. Placement v8 adds structured wall `placementOmission` reasons so downstream consumers can skip duplicate faces, blocked topology repairs, object-like linework, isolated fragments, and review-only wall evidence without parsing free-text diagnostics. Walls with any `placementOmission` are also marked not coordinate-ready in their reliability block, even if their raw wall evidence looked strong. Placement v9 adds document/page summary counts for placement-ready walls, omitted walls, clean topology spans, solid wall spans, and omission-code totals so importers can judge wall output readiness without scanning every wall entity first. Placement v10 adds `openingDominatedWallIds` to room boundary reliability so trusted room boundaries almost entirely consumed by anchored doors/openings do not block room placement while their tiny wall remnants stay omitted from exact wall geometry, and separates represented duplicate/context walls, suppressed noise/detail/opening-consumed walls, and true placement-review walls in summary and page-summary blocks. Placement v11 adds `wallSets`, a compact ID index for exact wall import, review-only wall IDs, represented/context walls, suppressed wall noise, omitted walls by code, and reliability-tracked walls. Its omission-code enum covers topology blockers, duplicate faces, rejected evidence, opening-consumed wall remnants, object/detail linework, secondary walls without room support, secondary stair/object linework, fragmented-pair review, thin exterior face-pair review, missing clean spans, and generic coordinate-review gates. Export the JSON Schema with `openplantrace schema placement --out .\placement.schema.json`, and validate a generated artifact with `openplantrace validate .\scan-output\placement.json --kind placement`. Validation checks that summary counts agree with the emitted arrays and that metric/routing readiness cannot be true unless geometry import is ready. Add `--deep` to validate downstream-placement semantics such as page references, positive/page-contained bounds, wall centerline lengths, anchored opening host-wall references, placement offset consistency, metric coordinate consistency, routing references, and metric-readiness calibration gates.

Opening-linked one-ended wall fragments use `opening_detail_fragment_review_required`
when a wall-looking candidate has weak endpoint support and is also referenced by
a detected door/window/opening candidate, so importers can separate likely
opening detail linework from possible true wall returns.
Those omissions also emit `placement.review.opening_detail_fragment` placement
issues and the import-readiness code
`placement.wall_opening.opening_detail_fragments_require_review`, giving
downstream importers exact wall bounds, source IDs/layers, confidence, evidence,
and a review action before treating the fragment as usable wall geometry.

Room placement exports include `boundaryReliability` buckets so consumers can distinguish clean ready boundary walls from review/rejected blockers, non-blocking duplicate or opening-only evidence, room-supported fragment evidence, and `placementOmittedWallIds` where wall evidence exists but exact clean placement geometry is not safe to import. Rooms with coordinate-blocking boundary walls also emit `placement.review.room_boundary_blocker` placement issues and the import-readiness code `placement.room_boundary.blockers_require_review`, giving downstream importers exact room bounds, wall IDs, source IDs, evidence, and recommended actions for room polygon review.

Thin exterior face-pair omissions also emit `placement.review.thin_exterior_face_pair` placement issues and the import-readiness code `placement.wall_exterior.thin_face_pairs_require_review`, giving downstream importers exact wall bounds, thickness, source IDs/layers, confidence, evidence, and a review action instead of silently treating covered-entry, railing, trim, glazing, or similar thin detail bands as coordinate-ready exterior walls.

The `fragmented_interior_without_room_boundary_support` omission code marks suspicious unpaired, fragment-merged interior linework that is not used by any detected room boundary. It stays available as review evidence, but downstream importers should not use it as exact partition geometry until a reviewer or stronger room/wall detector promotes it.

Topology-supported fragmented paired walls are also guarded: if a short promoted wall has excessive face fragmentation, OpenPlanTrace keeps it as `fragmented_short_parallel_pair_review_required` review evidence instead of exporting it as exact clean placement geometry. These omissions also emit `placement.review.fragmented_short_parallel_pair` placement issues and the import-readiness code `placement.wall_pairs.fragmented_short_pairs_require_review`, including wall bounds, source IDs/layers, confidence, thickness, evidence, and a reviewer action.

The snapshot contract is schema-versioned as `openplantrace.visual-snapshot.v4`. Export its JSON Schema with `openplantrace schema visual-snapshot --out .\visual-snapshot.schema.json`, and validate a generated artifact with `openplantrace validate .\scan-output\visual-snapshot.json --kind visual-snapshot`. Visual snapshots include page-space detector bounds, normalized page-relative layer bounds, normalized layer-density scores, wall-placement ready/review/suppressed/represented counts, residual endpoint-on-host-wall counts after placement graph cleanup, prioritized wall omission reasons, capped omitted-wall examples with page-space bounds and centerlines, high-density visual warnings, and high review-wall ratio warnings so screenshots and overlays can be compared across page sizes without losing exact drawing-unit coordinates. The viewer can load `visual-snapshot.json` directly, render page-coordinate layer bounds, and expose density, issue, review-queue, wall QA, schema, and coordinate metadata in the General and Advanced tabs for repeatable visual QA.

When a visual snapshot includes `wallPlacement`, the viewer summarizes placement-ready walls, review walls, suppressed/noise walls, represented duplicate/context walls, and top omission reasons without requiring the original scan JSON.

Placement-review SVGs and visual snapshots also expose wall graph repair candidates as a separate `wall-graph-repairs` / `wallGraphRepairs` QA layer. These are not accepted walls; they are suggested endpoint snap or trim actions with source/target coordinates, severity, import impact, and evidence so screenshots can show known topology gaps before a downstream engine trusts wall placement.

Write a standalone page-coordinate GeoJSON feature collection:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --geojson .\scan.geojson
```

OpenPlanTrace GeoJSON is a GeoJSON-compatible page-coordinate `FeatureCollection`. It uses standard `FeatureCollection`, `Feature`, `geometry`, and `properties` members, and adds allowed foreign members such as `schemaVersion=openplantrace.geojson.v1` and `coordinateSpace=OpenPlanTracePageCoordinates`. Coordinates are OpenPlanTrace page drawing units, not WGS84 longitude/latitude. GIS/map software may parse the file, but consumers must not treat it as Earth-map coordinates unless a host application transforms the page coordinate frame into a real coordinate reference system. Feature properties include page number, detector type, confidence, labels, source primitive IDs, source layers, wall evidence category/readiness flags, surface-pattern exclusion policy, routing influence, room-use hints, child object suppression, and measurements when available. The full scan JSON also includes a top-level `coordinateSystem` block with page bounds, axes, coordinate order, normalized transforms, calibration linkage, and `routingLayer` data for downstream software.

Compound objects are exported in two layers: raw child detections remain in `objects` for review/training, while `objectAggregates` describe the physical or semantic object they form. Each aggregate includes a `composition` block with category/kind/source-kind counts, wall-component provenance when applicable, and per-child object bounds/source/category/confidence summaries. The routing layer then makes the downstream action explicit through `suppressedObjects`: each suppressed child object names the aggregate that replaces it, the reason, the recommended action (`UseAggregateObstacle`, `UseAggregateRoomUseHint`, or `IgnoreForRouting`), replacement obstacle or room-use hint IDs when applicable, candidate bounds, source IDs/layers, confidence, and evidence. The companion `ignoredObjects` list explains every object candidate omitted from routing obstacles, including unreviewed generic symbols, room-use-only objects, and aggregate-suppressed children with page bounds, source provenance, reason, influence, and links to suppression or room-use records. A car made of many symbols can therefore mark a room as parking evidence without becoming many separate loop-generation blockers.

Inspect loader output without running the scanner:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- inspect .\sample.pdf
```

For very dense vector PDFs, the scanner localizes dimension-line candidates around parseable dimension text and emits `dimensions.text_candidate_pool.pruned`; it still caps other expensive searches and emits diagnostics such as `walls.candidate_limit_applied` instead of hanging. When a reliable scale is available before dimension matching, dimension-line scoring uses the expected drawing length as a soft signal and emits evidence for the expected-length difference. Duplicate PDF dimension text on the same matched line is suppressed with `dimensions.duplicates_suppressed` while retaining combined source IDs, and conflicting text on the same matched line is suppressed with `dimensions.same_line_conflicts_suppressed`. Grid-axis endpoint-label lookup uses a spatial label index so large unlayered PDF line sets do not repeatedly scan every text label. Use stage tracing when profiling a difficult file:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --json .\scan.json --trace-stages
```

Export Kvemo training/review crops without a model:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --out-dir .\scan-output --kvemo-crop-dir .\scan-output\kvemo-crops --kvemo-max-crops 200
```

This writes standard PNG crops plus `kvemo-crops.jsonl`. The manifest records page coordinate-frame metadata, page width/height, object bounds, crop bounds, object-to-crop area ratio, review keys, group signatures, visual similarity keys, deterministic image fingerprints, source primitive IDs, source candidate kind, source-kind distributions for grouped crops, promoted wall-component IDs/kind distributions, source layer/entity/block evidence, deterministic confidence, deterministic category/kind/label hints, nearby text, review priority, review reasons, suggested training use, and later model predictions when a model is configured. Its documented entry schema is `docs/schemas/openplantrace.kvemo-crops.v2.schema.json`. Kvemo uses adaptive padding so tiny symbols get tighter object-focused crops while larger candidates can keep useful context. Text boxes are excluded from crop images by default because nearby text is already passed as structured evidence; add `--kvemo-include-text-bounds` when you intentionally want text rectangles in the crop image.

Summarize a crop corpus after a scan:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- kvemo-report .\scan-output\kvemo-crops\kvemo-crops.jsonl
```

The report counts crop-only versus model-classified entries, review priorities, suggested training-use buckets, detection kinds, categories, object source kinds, promoted wall-component source kinds, top visual similarity keys, top review reasons, invalid JSONL entries, source primitive totals, and average object-to-crop area ratio. The schema can also be printed or written with `schema kvemo-crops`.

Draft a deterministic object-label profile from reviewed Kvemo crop groups:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- kvemo-profile-template .\scan-output\kvemo-crops\kvemo-crops.jsonl --json .\scan-output\kvemo-object-label-profile.json
```

The generated profile is intentionally conservative. It reuses Kvemo `reviewKey`/`groupSignature` selectors, detected tag patterns, source layers, crop counts, review priorities, and real model evidence if present, but it keeps generated rules as draft review rules. Edit the rule labels/categories, set confirmed rules to `"requiresReview": false`, and reuse the file with `--object-label-profile`.

Run local Kvemo object classification with a real ONNX model:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.pdf --out-dir .\scan-output --kvemo-model .\models\plan-symbols.onnx --kvemo-labels .\models\plan-symbols.labels.txt --kvemo-crop-dir .\scan-output\kvemo-crops --kvemo-model-name PlanSymbols --kvemo-model-version 0.1
```

Kvemo classifies deterministic object candidates and repeated object groups after geometry/layer/text analysis. It does not replace deterministic scanning. It writes `visualAi` blocks on exported objects/groups with label, category, confidence, alternatives, model metadata, inference engine, page number, crop bounds, crop source ID, and evidence. If no model is supplied, no AI labels are invented; crop export still works as data collection. If Kvemo is requested without a classifier or crop sink, the scan emits `visual_ai.classifier_missing`.

Download or train these files before using Visual AI:

- an ONNX image-classification model trained or fine-tuned for floorplan/object crops
- a labels file in the same output order as the model classes
- license text for the model weights/dataset that allows your intended use

Labels are one per line. Add `|ObjectCategory` to pin the category instead of relying on label keyword mapping:

```text
process pump|Equipment
valve|Equipment
sofa|Furniture
sink|PlumbingFixture
electrical panel|ElectricalDevice
unknown symbol|Unknown
```

The default model input is RGB `224x224` in NCHW layout with ImageNet normalization. Use `--kvemo-input-width`, `--kvemo-input-height`, `--kvemo-channels-last`, `--kvemo-mean`, and `--kvemo-std` when your model expects different preprocessing. For a model that expects plain `0..1` RGB values, pass `--kvemo-mean 0,0,0 --kvemo-std 1,1,1`. The older `--visual-ai-*` option names remain accepted as aliases.

Tune wall-fragment cleanup for heavily fragmented vector exports:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\sample.dxf --out-dir .\scan-output --min-wall-fragment 4 --max-wall-fragment-gap 6
```

Override layer classification when a CAD/PDF uses project-specific layer names:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\industrial.dxf --out-dir .\scan-output --layer-category XREF-*-LINEWORK=Wall --layer-category EQUIP-LINE=Equipment
```

Reuse a layer profile across a whole plan set:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch .\plans --out-dir .\batch-output --recursive --layer-profile .\docs\layer-profile.example.json
```

Layer profiles are schema-versioned JSON files:

```json
{
  "schemaVersion": "openplantrace.layer-profile.v1",
  "name": "Industrial CAD layers",
  "version": "1.0",
  "overrides": [
    { "pattern": "A-WALL-*", "category": "Wall", "sourceFormat": "dxf" },
    { "pattern": "E-EQP-*", "category": "Electrical" }
  ]
}
```

Multiple `--layer-profile` values can be supplied. Inline `--layer-category` overrides are applied before profile overrides, so a one-off command can correct a broad profile without editing the file.

Layer categories are deterministic and case-insensitive: `Unknown`, `Wall`, `Door`, `Window`, `Room`, `Dimension`, `Text`, `Grid`, `Structural`, `Equipment`, `Electrical`, `HVAC`, `Plumbing`, and `FireSafety`.

Export the layer-profile JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema layer-profile --json .\openplantrace.layer-profile.schema.json
```

Persist user-confirmed object and symbol labels across scans:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch .\plans --out-dir .\batch-output --recursive --object-label-profile .\docs\object-label-profile.example.json
```

Generate an editable object-label profile draft from detected object groups:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\industrial.dxf --out-dir .\scan-output --object-label-template .\scan-output\object-label-profile.json
```

Export a deterministic object/symbol review queue with grouped candidates, source IDs, source layers, room assignment, nearby text, padded review crop bounds, evidence, and suggested label-profile selectors:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\industrial.dxf --out-dir .\scan-output --object-review-dataset .\scan-output\object-review-dataset.json
```

Write an editable correction dataset for human review decisions:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- scan .\industrial.dxf --out-dir .\scan-output --object-correction-template .\scan-output\object-corrections.json
```

Convert reviewed corrections into a reusable deterministic object-label profile:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- corrections-to-profile .\scan-output\object-corrections.json --json .\scan-output\object-label-profile.json
```

Correction datasets are schema-versioned JSON artifacts that record local user decisions for object groups or candidates. Draft actions start with `"decision": "Unreviewed"` and do not become reusable knowledge until a user changes the decision to `"Confirmed"` or `"Corrected"` and supplies corrected label/category outputs. Actions include reviewed occurrence page numbers, candidate IDs, detected tags, and optional padded crop bounds so the user can see how many same-plan symbols a group correction affects and retain the visual review region before converting it. Confirmed/corrected group actions can be converted into deterministic `ObjectLabelProfile` rules with `corrections-to-profile`, so one reviewed symbol family can be applied to many matching future occurrences without AI or hidden inference; generated profile rules preserve occurrence-count and page evidence. Set `applyScope` to `MatchingSignature`, `MatchingSymbolAndLayer`, or `MatchingDetectedTagPattern` to control whether the generated rule matches the exact symbol family, the symbol/layer pair, or a common detected tag prefix such as `P-*` for separated tags or `P*` for compact tags. The example lives at `docs/object-correction-dataset.example.json`.

Object label profiles are deterministic, schema-versioned JSON files. They do not run AI and do not infer labels beyond explicit selectors:

```json
{
  "schemaVersion": "openplantrace.object-label-profile.v1",
  "name": "Industrial symbol labels",
  "version": "1.0",
  "rules": [
    {
      "signature": "symbol:iso_tag_71|category:GenericSymbol|kind:Symbol|layers:x-symbols",
      "category": "Equipment",
      "label": "Isolation valve",
      "symbolName": "VALVE_ISO",
      "requiresReview": false,
      "confidence": 0.91,
      "evidence": ["User confirmed this repeated symbol family."]
    },
    {
      "symbolNamePattern": "PUMP_*",
      "layerPattern": "I-EQUIP*",
      "category": "Equipment",
      "label": "Pump",
      "requiresReview": false
    }
  ]
}
```

Rules can match exact object-group signatures exported in `objectGroups[].signature`, symbol names, object labels, detected tag patterns such as `P-*`, `P*`, or `HV-*`, source layers, source formats, current category, or current kind. When a rule matches, OpenPlanTrace applies the label/category/review flag to the group and its member object candidates, and records profile evidence in exported JSON and diagnostics.

The generated `--object-label-template` file uses the same schema. It writes one draft rule per detected object group with the group signature, detected tag prefix pattern when the group has a clear common tag prefix, current category/kind, review flag, confidence, and evidence. Kvemo crop manifests can also become draft profile rules with `kvemo-profile-template`, so visual crop review can feed the same deterministic label-profile loop. Edit the draft labels/categories, set confirmed groups to `"requiresReview": false`, then reuse the file with `--object-label-profile`.

Export the object-label-profile JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema object-label-profile --json .\openplantrace.object-label-profile.schema.json
```

Supported CLI input formats today:

- PDF through `OpenPlanTrace.Pdf`
- DXF through `OpenPlanTrace.Dxf`

List formats:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- formats
```

For machine-readable capability metadata, including registered loaders, planned adapters, and licensing notes:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- formats --json
```

Validate schema-versioned OpenPlanTrace artifacts before using or sharing them:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\docs\layer-profile.example.json
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\docs\object-label-profile.example.json --json .\validation.json
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\docs\object-correction-dataset.example.json --kind object-correction-dataset
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\docs\batch-manifest.example.json --kind batch-manifest
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-output\batch.json --kind batch-result
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-output\batch.json --kind batch-result --deep
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-output\batch.json --kind batch --deep
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-comparison.json --kind batch-comparison
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-comparison.json --kind batch-comparison --deep
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\samples\golden\benchmark.json --kind benchmark-manifest
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\samples\golden\benchmark-output.json --kind benchmark-result
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\docs\viewer-benchmark-review-session.example.json --kind viewer-benchmark-review-session
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\scan-output\placement.json --kind placement
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\scan-output\placement.json --kind placement --deep
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\scan.geojson --kind geojson
```

The validator uses OpenPlanTrace's current schema versions and parser contracts for scan JSON, object review datasets, object correction datasets, benchmark manifests, benchmark results, benchmark comparisons, viewer benchmark review sessions, batch manifests, batch results, batch comparisons, layer profiles, object-label profiles, placement exports, visual snapshots, and GeoJSON feature exports. It does not require a third-party schema validator dependency. The short `--kind batch` alias auto-detects batch manifests versus generated batch results from the artifact schema. Add `--deep` when validating a placement export to check coordinate frame transforms, page references, geometry bounds, metric wall/room/opening/object/routing coordinates, wall length consistency, anchored-opening references, opening offset math, routing-layer source references, suppressed-object replacement links, surface-pattern wall-overlap issue references, affected-wall reliability reasons, and metric calibration gates for downstream consumers. Add `--deep` when validating a batch result to also validate the referenced per-input `scan.json`, `visual-snapshot.json`, optional `placement.json`, optional `scan.geojson`, overlay directory, and SVG links from each visual snapshot. Add `--deep` when validating a batch comparison to validate the baseline/candidate scan JSON, visual snapshot, optional placement export, optional GeoJSON, overlay directory, scan-vs-visual page counts, visual issue counts, and SVG links referenced by each comparison item.

Export the current scan-result JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema scan --json .\openplantrace.scan.schema.json
```

The scan schema is versioned with the exported `schemaVersion` field. The current artifact lives at `docs/schemas/openplantrace.scan.v71.schema.json` and is embedded in `OpenPlanTrace.Export` through `PlanTraceJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`. The schema pins stable top-level fields and evidence-bearing detector shapes, while allowing additional properties so future detectors can add evidence without breaking older consumers. Scan v50 adds per-stage contract audit telemetry so consumers can see whether a pipeline stage changed only the artifact kinds it declared in its write contract. Scan v51 adds explicit wall-graph repair assessment fields so unsnapped endpoint gaps can be imported, reviewed, or blocked by policy instead of being treated as generic diagnostics. Scan v52 adds a final diagnostics artifact inventory so downstream tools can see which source, geometry, topology, semantic, routing, and quality artifacts are available after the run. Scan v54 adds execution-wave scheduling reasons, direct downstream stage hints, and downstream-read artifact lists for future partial reruns and safe parallel stage scheduling. Scan v55 adds deterministic artifact rerun-impact maps so corrections to walls, rooms, objects, source primitives, or other artifacts can invalidate only the downstream stages and artifacts that depend on them. Scan v56 adds ready-made rerun plans for common correction workflows, including source primitive, wall geometry, wall topology, opening, room, object, calibration, and routing corrections. Scan v57 adds deterministic artifact snapshot state keys and revisions to stage artifact snapshots and final artifact inventory for audit, cache, comparison, and future partial-rerun workflows. Scan v58 adds machine-readable routing-passage placement readiness fields so route consumers can filter unsafe exact door/window passage coordinates. Scan v59 adds per-stage `outputReadiness` summaries so consumers can see declared outputs with data, empty declared outputs, changed/unchanged declared outputs, undeclared changed artifacts, and evidence without reverse-engineering artifact deltas. Scan v61 adds deterministic `wallType` classification on wall records so consuming software can distinguish exterior walls, interior partitions, and unknown walls without text parsing. Scan v62 adds structured `fragmentEvidence` on walls to expose fragment count, healed gaps, duplicate collapse, and review-required geometry risk without parsing evidence text. Scan v70 keeps raw wall graph spans in `wallGraph` while exposing wall-level `topologySpans` as the same clean placement-ready spans used by placement JSON and wall-QA overlays, so the viewer does not redraw review-only graph fragments as trusted wall output. Scan v71 adds wall-level `placementStatus` and `representedByWallIds` so duplicate raw wall candidates already covered by clean topology are provenance context instead of unresolved review noise.

The exported `quality` block summarizes deterministic scan quality. It reports an overall confidence score, grade (`Poor`, `ReviewRequired`, `Usable`, or `Strong`), detector-level confidence summaries, review-required counts, and evidence-backed quality issues. Semantic object-group and object-aggregate review backlogs remain visible as informational quality issues and review/correction datasets, but they do not alone mark the whole scan as requiring quality review; geometry, metric, raster/OCR, calibration, and professional scan-risk warnings still can. The top-level `importReadiness` block then translates the same deterministic scan evidence into downstream geometry, metric, and routing import readiness with grade, score, blocking/review issue codes, recommended actions, and evidence. Import-readiness issue codes are workflow-specific: semantic object labeling backlogs are not repeated there, while topology risks such as queued wall graph endpoint gaps are reported as `placement.wall_graph.endpoint_gaps.require_review`, and walls overlapping dense non-structural surface/detail patterns are reported as `placement.wall_graph.surface_pattern_wall_overlaps.require_review`. Metric import uses a bounded-outlier policy: a small minority of dimension conflicts can require review without blocking millimeter coordinates, while higher outlier ratios or wide scale spread still block metric import. Scan v40 exposes this policy directly in `measurementConsistency` through `outlierRatio`, `hasBlockingOutliers`, `hasTolerableOutliers`, `metricImportImpact`, and the threshold values used for the decision. Scan v41 adds top-level `reviewQueue` items with priority, severity, page/bounds, source IDs/layers, evidence, recommended action, and structured properties so a viewer or downstream QA tool can review scan risks without first running a benchmark. Wall graph endpoint-gap review items identify possible unsnapped junctions that are outside safe auto-snap tolerance but close enough to affect topology; they include coordinates, source IDs/layers, gap distance, wall IDs, a `repairCandidateId`, and a suggested repair action without inventing a connection. Scan exports preserve typed `wallGraph.repairCandidates` with source/target points, repair line, suggested action, confidence, and evidence. Placement exports mirror these as top-level `wallGraphRepairCandidates` with drawing-unit and millimeter coordinates when calibration is available, so downstream tools can review or apply a snap deliberately instead of parsing diagnostics. Scan v42 adds `sourceKind`, `sourceWallComponentId`, and `sourceWallComponentKind` on object candidates so promoted false-wall/object-island candidates can be linked back to wall graph components without parsing evidence text. Scan v43 adds top-level `surfacePatterns` for dense non-structural detail linework that was excluded from walls/topology while preserving coordinates, source provenance, confidence, and evidence. It is not AI judgment.

Export the object/symbol review dataset JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema object-review-dataset --json .\openplantrace.object-review-dataset.schema.json
```

The object review dataset schema lives at `docs/schemas/openplantrace.object-review-dataset.v2.schema.json` and is embedded in the core `OpenPlanTrace` package through `ObjectReviewDatasetJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`. Version 2 carries object source-kind and wall-component provenance into human review records, so training and correction workflows can distinguish CAD symbols, text-derived objects, composite linework, and promoted object-like wall islands.

Export the object correction dataset JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema object-correction-dataset --json .\openplantrace.object-correction-dataset.schema.json
```

The object correction dataset schema lives at `docs/schemas/openplantrace.object-correction-dataset.v1.schema.json` and is embedded in the core `OpenPlanTrace` package through `ObjectCorrectionDatasetJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`.

Export the benchmark manifest JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema benchmark-manifest --json .\openplantrace.benchmark-manifest.schema.json
```

The benchmark manifest schema lives at `docs/schemas/openplantrace.benchmark-manifest.v1.schema.json` and is embedded in the core `OpenPlanTrace` package through `BenchmarkManifestJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`.

Export the benchmark result JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema benchmark-result --json .\openplantrace.benchmark-result.schema.json
```

Benchmark result JSON is schema-versioned as `openplantrace.benchmark-result.v1`. The schema lives at `docs/schemas/openplantrace.benchmark-result.v1.schema.json` and is embedded in the core `OpenPlanTrace` package through `BenchmarkRunResultJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`. Result artifacts include per-fixture counts, assertions, detector target metrics, unmatched extra-detection queues, quality issue summaries, diagnostic code summaries, stage timings, per-stage artifact input/output/change ledgers, and the readiness scoreboard so downstream tools can validate benchmark evidence before trusting it.

Export the viewer benchmark review-session JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema viewer-benchmark-review-session --json .\openplantrace.viewer-benchmark-review-session.schema.json
```

The viewer benchmark review-session schema lives at `docs/schemas/openplantrace.viewer-benchmark-review-session.v1.schema.json` and is embedded in the core `OpenPlanTrace` package through `ViewerBenchmarkReviewSessionJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`. Review-session JSON is a merge artifact for restoring target decisions, bounds edits, deleted targets, added targets, active filters, scan snapshot details, and review issues against the matching benchmark manifest. The example lives at `docs/viewer-benchmark-review-session.example.json`.

Export the batch manifest JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema batch-manifest --json .\openplantrace.batch-manifest.schema.json
```

The batch manifest schema lives at `docs/schemas/openplantrace.batch-manifest.v1.schema.json` and is embedded in the core `OpenPlanTrace` package through `BatchScanManifestJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`.

Export the batch result JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema batch-result --json .\openplantrace.batch-result.schema.json
```

Batch result JSON is schema-versioned as `openplantrace.batch.v7`. The schema lives at `docs/schemas/openplantrace.batch.v7.schema.json` and is embedded in the CLI package through `BatchScanRunResultJsonSchema.ReadCurrent()` / `WriteCurrentAsync(...)`. Result artifacts include per-input output paths, downstream `placement.json` paths, status, source format/capability metadata, scan counts, scan quality, visual-snapshot issue summaries, wall-placement QA summaries, and import-readiness summaries with coordinate/metric ready ratios.

Export the batch comparison JSON Schema contract:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- schema batch-comparison --json .\openplantrace.batch-comparison.schema.json
```

Compare two batch runs after changing scanner code or options:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch-compare .\batch-baseline\batch.json .\batch-candidate\batch.json --json .\batch-comparison.json --markdown .\batch-comparison.md
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- validate .\batch-comparison.json --kind batch-comparison --deep
```

Batch comparison JSON is schema-versioned as `openplantrace.batch-comparison.v1`. It compares batch result artifacts without rescanning PDFs/DXFs and reports matched/added/removed inputs, status changes, count deltas, quality-confidence deltas, diagnostic-error deltas, visual-snapshot issue deltas, duration changes, and regression/improvement/info signals. Each item also carries baseline/candidate `scanJsonPath`, `visualSnapshotPath`, optional `placementJsonPath`, optional `geoJsonPath`, and `overlayDirectory` links when the original batch result had them, so a count drift can be traced straight back to visual and downstream-placement evidence. Deep validation checks those evidence links before the report is trusted. Use it as the fast regression loop before deeper benchmark truth-set scoring.

The layer-profile, object-label-profile, and object-correction-dataset schemas live at `docs/schemas/openplantrace.layer-profile.v1.schema.json`, `docs/schemas/openplantrace.object-label-profile.v1.schema.json`, and `docs/schemas/openplantrace.object-correction-dataset.v1.schema.json`, and are embedded in the core `OpenPlanTrace` package through `LayerCategoryProfileJsonSchema`, `ObjectLabelProfileJsonSchema`, and `ObjectCorrectionDatasetJsonSchema`.

Run a batch scan over files or a directory:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch .\plans --out-dir .\batch-output --recursive --markdown .\batch-output\batch-report.md
```

Batch scanning writes one folder per input with `scan.json`, `placement.json`, `visual-snapshot.json`, and page SVG overlays, plus a `batch.json` summary. Add `--markdown` to write a human-readable corpus QA report with per-file status, source kind, import-readiness grade/score/coordinate ratios, quality/review flags, geometry counts, visual issue counts, diagnostic counts, review priorities, and artifact paths for `scan.json`, `placement.json`, `scan.geojson`, visual snapshots, and SVG overlays. Add `--geojson` to write `scan.geojson` beside each per-file `scan.json`. The summary includes title-block, dimension, annotation, grid-axis, grid-bay spacing, room-adjacency, room-cluster, object, object-group, object-aggregate, routing-layer, diagnostic, scan-quality, visual-snapshot counts/issues, and import-readiness counts alongside geometry counts, and preserves the per-file placement path for downstream import checks. SVG and GeoJSON outputs include annotation marker references when keynote/callout items can be matched to plan-side markers. Add `--parallel 4` to scan up to four supported inputs at once, and `--retries 1` to retry failed scan/load attempts once. Missing and unsupported inputs are not retried. Scan summaries also report measurement QA outliers when matched dimensions disagree with the selected calibration. PDF and DXF files are scanned by the current CLI. Existing DWG, raster image, and SVG-like inputs are reported in the batch summary as unsupported unless a real adapter is registered, and unsupported/failed items include a `sourceCapability` block with status, adapter requirements, and licensing notes. OpenPlanTrace does not fake native DWG or raster parsing.

Kvemo crop harvesting also works in batch mode. Use a shared crop directory to collect object/group crops and one append-only `kvemo-crops.jsonl` manifest across a plan set:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch .\plans --out-dir .\batch-output --recursive --kvemo-crop-dir .\batch-output\kvemo-crops --kvemo-max-crops 200
```

This is the intended way to build local review/training corpora from the provided easy, medium, and hard benchmark PDFs without inventing labels. Each crop manifest entry includes `reviewPriority`, `reviewReasons`, and `suggestedTrainingUse` so later review tooling can prioritize true symbol-labeling candidates, model-audit candidates, and hard-negative linework false-positive review. Add `--kvemo-model` and `--kvemo-labels` to the same batch command only after a real licensed ONNX model is available.

For repeatable plan-set runs, use a schema-versioned batch manifest:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- batch --manifest .\docs\batch-manifest.example.json
```

Minimal batch manifest:

```json
{
  "schemaVersion": "openplantrace.batch-manifest.v1",
  "name": "Project A floor plans",
  "outputDirectory": "batch-output/project-a",
  "summaryJsonPath": "batch-output/project-a/batch.json",
  "inputs": [
    "plans",
    "site/level-01.pdf"
  ],
  "recursive": true,
  "writeSvg": true,
  "writeGeoJson": true,
  "maxDegreeOfParallelism": 4,
  "retryCount": 1,
  "layerProfiles": ["layers.json"],
  "objectLabelProfiles": ["object-labels.json"],
  "layerCategoryOverrides": [
    { "pattern": "A-WALL-*", "category": "Wall", "sourceFormat": "dxf" }
  ],
  "scannerOptions": {
    "minWallLength": 24,
    "maxWallCandidateSeedsPerPage": 15000,
    "wallSnapTolerance": 3,
    "objectNearbyTextSearchRadius": 90,
    "maxNearbyTextPerObject": 5
  }
}
```

Manifest input, output, layer-profile, and object-label-profile paths are resolved relative to the manifest file unless they are absolute. CLI flags can still override output paths and scanner thresholds for a one-off run; inline `--layer-category` overrides keep priority over manifest/profile overrides.

DWG integration pattern for host applications:

```csharp
var registry = new PlanDocumentLoaderRegistry(new IPlanDocumentLoader[]
{
    new PdfPigPlanDocumentLoader(),
    new IxMiliaDxfPlanDocumentLoader(),
    new DwgToDxfPlanDocumentLoader(myLicensedDwgConverter)
});
```

`myLicensedDwgConverter` must implement `IDwgToDxfConverter` using a real DWG-capable toolchain. Metadata from the bridge records `format=dwg`, `sourceKind`, `effectiveSourceKind`, `dwg.conversion=dwg-to-dxf`, the converter name, and the intermediate DXF loader so downstream consumers can audit the path. The downstream `placement.json` document block mirrors those fields so an importer can reject unknown or unapproved DWG paths without loading the full scan JSON.

For a separately installed command-line converter, configure the external-process bridge explicitly:

```csharp
var dwgConverter = new ExternalDwgToDxfConverter(
    new ExternalDwgToDxfConverterOptions
    {
        ConverterName = "MyLicensedDwgTool",
        ExecutablePath = @"C:\Tools\DwgConverter\converter.exe",
        Arguments = new[] { "--input", "{input}", "--output", "{output}" },
        Timeout = TimeSpan.FromMinutes(2),
        Properties = new Dictionary<string, string>
        {
            ["licenseBoundary"] = "host-installed"
        }
    });

var loader = new DwgToDxfPlanDocumentLoader(dwgConverter);
```

The external bridge writes the incoming DWG stream to a temporary file, runs the configured process without shell command-line concatenation, requires a DXF output file, and records `dwg.converter.executionMode=external-process`, executable name, exit code, duration, timeout, output byte count, and any host-supplied properties. Keep the external converter's license and deployment separate from the MIT core.

Raster/OCR integration pattern for host applications:

```csharp
var registry = new PlanDocumentLoaderRegistry(new IPlanDocumentLoader[]
{
    new PdfPigPlanDocumentLoader(),
    new IxMiliaDxfPlanDocumentLoader(),
    new RasterPlanDocumentLoader(myRasterExtractor)
});
```

`myRasterExtractor` must implement `IRasterPlanPrimitiveExtractor` using a real OCR/vectorization pipeline. It should emit `RasterTextEvidence`, `RasterLineEvidence`, and `RasterPolylineEvidence` with confidence, source IDs, page DPI, extractor/model names, and any source-image identifiers. Clipboard images should use `PlanSourceDescriptor.FromClipboard(PlanSourceKind.RasterImage, "clipboard.png", "image/png")`; they route through the same registered raster loader by effective content kind. Metadata from the loader records `format=raster`, `loader`, `raster.adapter`, extractor/model provenance, evidence counts, DPI/source-image summaries, and extraction confidence statistics. Raster scans emit `raster.extraction.summary` diagnostics; empty extractor output emits `raster.extraction.no_primitives`, and mostly low-confidence evidence emits `raster.extraction.low_confidence`. These diagnostics do not invent detections; they tell the host that its real extractor needs review or improvement. Import-readiness and placement-export issues now carry specific recommended actions for empty raster output and low-confidence raster/OCR evidence, so a consuming app can route scans to OCR review instead of treating every failure as generic missing geometry.

PDF files that contain embedded page images now retain PDF image metadata such as `pdf.imageCount`, `pdf.imagePageCount`, `pdf.imageOnlyPageCount`, `pdf.imagePages`, `pdf.imageOnlyPages`, sample-pixel counts, and maximum image-page coverage. If a PDF page has embedded images but no extracted text/vector primitives, the scanner emits `pdf.raster_image_only_pages` and the scan-quality report includes `quality.pdf_raster_ocr_required`. This does not OCR the page; it tells the host to route the page through a real raster/OCR adapter. The placement packet exposes the same image-only PDF issue with an OCR-specific recommended action for visual QA and downstream import gates.

Run a benchmark manifest against one or more fixture files:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark .\fixtures\benchmark.json --json .\benchmark-output.json
```

Benchmark JSON outputs are schema-versioned `openplantrace.benchmark-result.v1` artifacts. They include per-case quality issue summaries, diagnostic code summaries, stage timings with pipeline kind/read/write/capability/dependency-level and artifact-change telemetry, deterministic geometry/metric/routing import readiness, detector `extraDetections` queues for unmatched precision-scored false positives, `reviewOnlyDetections` queues for unknown/generic detections that should feed labeling or training instead of downstream placement, and a top-level `reviewQueue` that flattens all reviewable detections with fixture, detector, queue kind, evidence, and recommended action. The detector metric keeps raw `detectedCount`, production-scored `scoredDetectionCount`, and `precisionScoringEnabled`, so a partial/spot-check target list can measure recall without pretending every other detection is a false positive. Set `completeTruthSet: true` or `minPrecision` only after the target list is exhaustive for that detector. They can be validated with `openplantrace validate .\benchmark-output.json --kind benchmark-result` before a comparison run, viewer import, or downstream quality gate.

Run the included golden smoke fixture:

```powershell
New-Item -ItemType Directory -Force .\artifacts | Out-Null
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark .\samples\golden\benchmark.json --json .\artifacts\golden-benchmark-output.local.json --markdown .\artifacts\golden-benchmark-report.local.md
```

Run the local provided-PDF fixture template when those PDFs are available in Downloads:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark .\samples\provided-pdfs\benchmark.example.json --json .\samples\provided-pdfs\benchmark-output.local.json --markdown .\samples\provided-pdfs\benchmark-report.local.md
```

The provided-PDF fixtures are marked `optional`, so missing local PDFs are reported as skipped cases instead of failed benchmarks. Fixture `sourcePath` values support environment variables such as `%OPENPLANTRACE_PDF_CORPUS%`; present optional files are scanned normally and must satisfy their expectations. Public fixture examples should use neutral difficulty names such as `light`, `medium`, `intermediate`, and `extreme`, not private project or customer names.

Draft a measured benchmark manifest from an exported scan JSON:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark-draft .\scan-output\scan.json --source-path "%OPENPLANTRACE_PDF_CORPUS%\light.pdf" --fixture-id pdf-light --json .\samples\provided-pdfs\pdf-light.draft.json --review-markdown .\samples\provided-pdfs\pdf-light.draft-review.md
```

`benchmark-draft` reads schema-versioned scan JSON and writes a normal benchmark manifest with measured count floors, quality/diagnostic gates, source-readiness/provenance gates, import-readiness gates when the scan JSON includes readiness, scan-review-queue workload gates, measurement QA gates, and capped detector targets for regions, dimensions, annotations, annotation references, grid axes, walls, rooms, openings, objects, object groups, object aggregates, routing barriers, routing passages, routing obstacles, routing room-use hints, routing suppressed child objects, and layers. Generated object/object-group targets preserve detected industrial tag criteria when the scan export has them. Generated aggregate/routing targets preserve downstream semantics such as child-object suppression, suppressed child candidate IDs, aggregate IDs, suppression reason/action, routing influence, structural influence, obstacle kind, source kind, replacement obstacle IDs, room-use hint IDs, and room-use evidence, so a compound car/equipment symbol can be regression-tested as one semantic thing instead of many accidental blockers. All generated targets include review provenance when available: confidence, source primitive IDs, source layers, and human-readable evidence. Add `--review-markdown` to write a target audit report that highlights missing bounds, missing provenance, low-confidence targets, drafted source-readiness/provenance gates, and any drafted import-readiness/review-workload gates. The generated manifest is a starting point, not ground truth: review the visual output, remove false-positive targets, adjust source paths/properties, and then run it with `benchmark`. Use `--max-targets-per-detector`, `--target-recall`, `--target-precision`, `--no-bounds`, and `--optional` to tune the draft for local/private corpora.

Compare two benchmark result files after a scanner change:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Cli\OpenPlanTrace.Cli.csproj -- benchmark-compare .\baseline-benchmark-output.json .\candidate-benchmark-output.json --json .\benchmark-comparison.json --markdown .\benchmark-comparison.md
```

`benchmark-compare` returns a non-zero exit code when regression signals are found. Regression signals include removed cases, newly failed cases or assertions, benchmark readiness-grade drops, downstream-readiness loss, geometry/metric/routing import-readiness losses, import-readiness score drops, overall/consumer-readiness score drops, more missed truth targets, more unmatched extra detections, detector recall/precision/F1 drops for reviewed targets, scan-quality grade/confidence drops, new quality-review requirements, new quality issues, scan-review-queue workload increases by total count or kind, measurement outlier-ratio increases, wider matched-dimension scale spread, diagnostic error increases, large duration increases, and large per-stage artifact growth such as wall graph, topology span, wall, surface-pattern, room-adjacency, object-candidate, object-group, or object-aggregate explosions. Count deltas for walls, rooms, room clusters, openings, annotations, annotation references, objects, scan review queue items, measurement checks/outliers, diagnostics, assertions, and noise-sensitive per-stage artifact after/delta counts are included in JSON/Markdown for review, but ordinary raw count movement is not automatically treated as wrong because detector improvements can legitimately change counts.

Minimal benchmark manifest:

```json
{
  "schemaVersion": "openplantrace.benchmark-manifest.v1",
  "name": "Smoke fixtures",
  "fixtures": [
    {
      "id": "case-1",
      "sourcePath": "sample.dxf",
      "properties": {
        "difficulty": "smoke",
        "planType": "architectural",
        "sourceFormat": "dxf"
      },
      "expectations": {
        "minWalls": 4,
        "minRooms": 2,
        "minRoomAdjacencies": 1,
        "minRoomClusters": 1,
        "minDimensions": 1,
        "minAnnotations": 1,
        "minAnnotationReferences": 1,
        "minGridAxes": 1,
        "minObjectGroups": 1,
        "maxDurationMilliseconds": 2500,
        "minQualityGrade": "Usable",
        "minQualityConfidence": 0.65,
        "maxQualityIssues": 4,
        "maxScanRiskIssues": 0,
        "minMeasurementCheckedCount": 1,
        "maxMeasurementOutlierRatio": 0.25,
        "maxMeasurementScaleSpreadRatio": 2.0,
        "requiredGridLabels": ["A"],
        "requiredOpeningTypes": ["Door"],
        "requiredAnnotationKinds": ["GeneralNotes"],
        "requiredObjectCategories": ["HVACEquipment"],
        "requiredDiagnosticCodes": ["grid_axes.detected"],
        "forbiddenDiagnosticCodes": ["scanner.internal_error"],
        "forbiddenQualityIssueCodes": ["quality.scan_risk.sheet_contamination"],
        "maxDiagnosticErrors": 0,
        "stageExpectations": [
          {
            "stage": "grid-axes",
            "maxDurationMilliseconds": 500,
            "maxErrors": 0
          }
        ],
        "roomMetrics": {
          "minRecall": 1.0,
          "targets": [
            {
              "id": "office-room",
              "pageNumber": 1,
              "label": "OFFICE",
              "bounds": { "x": 100, "y": 100, "width": 300, "height": 200 },
              "minIntersectionOverUnion": 0.5
            }
          ]
        },
        "dimensionMetrics": {
          "minRecall": 1.0,
          "targets": [
            {
              "id": "overall-width",
              "text": "4000 mm",
              "dimensionKind": "Linear",
              "dimensionOrientation": "Horizontal"
            }
          ]
        },
        "annotationReferenceMetrics": {
          "minRecall": 1.0,
          "targets": [
            {
              "id": "keynote-marker-1",
              "pageNumber": 1,
              "marker": "1",
              "text": "1",
              "annotationKind": "Keynotes"
            }
          ]
        },
        "openingMetrics": {
          "minRecall": 1.0,
          "targets": [
            {
              "id": "main-door",
              "openingType": "Door",
              "openingOperation": "Hinged"
            }
          ]
        },
        "objectGroupMetrics": {
          "minRecall": 1.0,
          "targets": [
            {
              "id": "unknown-symbol-family",
              "text": "ISO_TAG_71",
              "objectCategory": "GenericSymbol",
              "detectedTags": ["P-101", "P-102"],
              "minCount": 2,
              "requiresReview": true
            }
          ]
        }
      }
    }
  ]
}
```

Fixture `sourcePath` values are resolved relative to the manifest file unless they are absolute, and environment variables are expanded before resolution. A benchmark fails honestly when a required input file is missing, when a scan fails, or when expected detector signals are not present. Mark local/private corpus entries with `optional: true` when a missing source should produce an explicit skipped case instead of a failure. Fixture `properties` are optional tags, such as difficulty, plan type, source format, and benchmark-draft provenance, and are copied into benchmark JSON and Markdown reports. Detector metric blocks such as `roomMetrics`, `openingMetrics`, `objectMetrics`, `objectGroupMetrics`, `objectAggregateMetrics`, `routingBarrierMetrics`, `routingPassageMetrics`, `routingObstacleMetrics`, `routingRoomUseHintMetrics`, `routingSuppressedObjectMetrics`, `wallMetrics`, `annotationMetrics`, `annotationReferenceMetrics`, `gridAxisMetrics`, `dimensionMetrics`, `regionMetrics`, and `layerMetrics` can define expected targets with optional page, bounds, labels/text, marker, min count, review flag, detector-specific enum fields such as annotation kind, dimension kind/orientation, grid orientation, opening type/operation, object category/kind, detected object/group tags, layer category, routing source kind, routing obstacle kind, routing influence, structural influence, room-use kind, child-object suppression, child object ID, suppressing aggregate ID, suppression reason/action, replacement routing obstacle ID, and room-use hint ID, plus optional confidence/source/evidence provenance for target review. `completeTruthSet: true` means the target list is exhaustive for that detector and unmatched scored detections are false positives; omit it for spot-check target lists where extra detections are evidence to review, not readiness blockers. The evaluator reports matched, missed, extra, precision, recall, and F1 values in benchmark JSON, and benchmark comparison turns detector recall drops plus precision/F1 drops for precision-scored metrics into explicit regression signals. Benchmark counts include surface patterns, object aggregates, routing items, routing suppressed objects, scan-review queue summaries, and measurement QA fields for checked dimensions, consistent dimensions, outliers, selected scale, median implied scale, spread ratio, and measurement confidence. Source-readiness gates can assert source format, loader, raw/effective source kind, ingestion path, readiness status, geometry basis, vector/DWG/raster/OCR/adapter facts, legal-adapter backing, and required/forbidden source evidence so a PDF, DXF, DWG-derived, raster-derived, or pre-extracted fixture cannot silently route through the wrong loader. Scan-review queue gates can cap exact review kinds such as `SurfacePatternReview`, `SurfacePatternWallOverlapReview`, `SuppressedWallPatternReview`, `WallGraphGapReview`, `ObjectGroupReview`, `ObjectAggregateReview`, `OpeningReview`, and `MeasurementOutlier`. Benchmarks can also set `minSurfacePatterns`, `maxSurfacePatterns`, `minObjectAggregates`, `maxObjectAggregates`, `minRoutingItems`, `maxRoutingItems`, `minRoutingSuppressedObjects`, `maxRoutingSuppressedObjects`, `minQualityGrade`, `minQualityConfidence`, `maxQualityIssues`, `maxScanRiskIssues`, `maxScanReviewQueueItems`, `maxScanReviewQueueKindCounts`, `requiredScanReviewQueueKinds`, `forbiddenScanReviewQueueKinds`, `minImportReadinessGrade`, `minImportReadinessScore`, `requireGeometryImportReady`, `requireMetricImportReady`, `requireRoutingImportReady`, `allowImportReview`, `requiredImportIssueCodes`, `forbiddenImportIssueCodes`, `minMeasurementCheckedCount`, `minMeasurementConsistentCount`, `maxMeasurementOutlierCount`, `maxMeasurementOutlierRatio`, `maxMeasurementScaleSpreadRatio`, `requiredRoomLabels`, `forbiddenRoomLabels`, `requiredQualityIssueCodes`, `forbiddenQualityIssueCodes`, `maxDurationMilliseconds`, `requiredDiagnosticCodes`, `forbiddenDiagnosticCodes`, and `stageExpectations` with `artifactExpectations` for downstream routing semantics, downstream import usability, scan-quality, scan-review workload, source provenance/readiness, measurement QA, professional scan-risk, local performance, diagnostics, and pipeline-artifact regression checks. Treat duration limits as machine-local smoke gates rather than public performance guarantees. Use `benchmark-draft --review-markdown draft-review.md` to seed and audit a manifest from real scan output, `benchmark --markdown report.md` to emit a readable corpus report next to the machine JSON, and `benchmark-compare` to compare two schema-versioned benchmark result files and produce a schema-versioned comparison JSON plus a Markdown regression report.

Benchmark runs also emit a `scoreboard` block in result JSON and a "Readiness Scoreboard" section in Markdown. The scoreboard grades the whole corpus, computes an overall score and consumer-readiness score, aggregates detector precision/recall/F1, counts matched/missed targets and precision-scored extra detections, records failed assertions/scans, and produces failure buckets such as missed targets, scored extra detections, unscored spot-check extras, review-only detections, low detector precision/recall, calibration risk, measurement outliers, heavy scan-review queues, skipped fixtures, scan-quality review warnings, and import-readiness blockers for geometry, metric, or routing consumers. The result-level `reviewQueue` is the machine-readable to-do list for that loop: scored extras should be fixed or promoted to truth targets, spot-check extras should be reviewed before enabling precision scoring, and review-only detections should be labeled, ignored, or converted through the object correction workflow. Per-case `scanReviewQueue` summaries are the scanner's own review workload; heavy workloads stay non-blocking but become scoreboard warning buckets with top review kinds so repeated review noise can become deterministic filters or benchmark truth updates. `benchmark-compare` promotes scoreboard grade, downstream-readiness, import-readiness grade/score/geometry/metric/routing changes, missed-target, scored extra-detection, scan-review workload, and failed-scan changes into explicit regression or improvement signals. This is the intended professional improvement loop: generate or review truth targets in the viewer, run `benchmark`, inspect the worst failure buckets, fix the detector or benchmark truth, save before/after screenshots, then use `benchmark-compare` to prove whether the candidate scanner improved or regressed.

The `samples/golden` folder contains a small hand-authored DXF smoke fixture that exercises the real DXF loader, room detection, hinged opening semantics, grid/dimension/annotation extraction, calibration, HVAC object classification, repeated unknown-symbol grouping, routing-layer generation, and benchmark metrics, including stage artifact gates for wall graph topology and routing barriers/passages/obstacles/hints. The `samples/provided-pdfs` folder contains an optional local manifest for the easy/medium/hard PDFs provided during development. The hard industrial fixture now forbids the old high dimension-scale spread diagnostic/quality issue, fragmented wall-graph scan-risk issue, wall-pair thickness-variance warning, dense dimension candidate-limit warning, and known non-room label fragments after calibrated expected-length rejection, text-localized dimension pools, same-line dimension conflict suppression, near-touch wall-graph junction recovery, shared horizontal/vertical wall-pair separation profiling, and stricter room-label filtering removed those regressions. The provided PDF entries also cap noise-sensitive stage artifact deltas for walls, surface patterns, wall graph topology, object candidates, object groups, object aggregates, routing barriers, routing passages, routing obstacles, routing room-use hints, routing suppressed/ignored objects, and industrial room adjacency so real-plan regressions fail when overlays or downstream routing output get messy again. These PDF entries start with conservative expectations; use scan JSON plus `benchmark-draft` to seed measured detector targets, then tighten them after visual review.

## Roadmap

The long-range implementation roadmap is tracked in [docs/ROADMAP.md](docs/ROADMAP.md).

## Licensing

OpenPlanTrace is MIT licensed. Third-party package notices are tracked in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Visual PDF Viewer

Run the local viewer:

```powershell
dotnet run --project .\tools\OpenPlanTrace.Viewer\OpenPlanTrace.Viewer.csproj --urls http://localhost:5077
```

Then open:

```text
http://localhost:5077
```

Drop a PDF into the viewer to scan it and render detected regions, surface patterns, dimension annotations, structured annotation/text blocks, grid axes, grid bay spacings, walls, wall graph components, wall nodes, room candidates with deterministic use hints, room adjacency links, room clusters, openings, object candidates, repeated object groups, compound object aggregates, optional routing-layer barriers/passages/obstacles/room-use hints/suppressed child objects/ignored objects, title-block fields, source-layer summaries, calibration evidence, measurement scale-group provenance, measurement consistency checks with clickable outlier/spread review rows, quality issues, professional scan-risk issues, and diagnostics. The default overlay starts with the trusted routing layer instead of every raw wall candidate, while the raw `Walls` layer remains available for auditing noisy detections. Use the `WALL QA` preset in the Overlay Layers panel when judging wall accuracy from screenshots; it enables only trusted clean wall spans, while blocked/review/non-placement wall spans stay diagnostic-only unless explicitly toggled. The default overlay also includes a separate `Surface patterns` layer for dense repeated surface/detail patterns that were intentionally kept out of wall reconstruction, while the full `Suppressed details` and `Scan review queue` layers remain available for diagnostic boxes and other review items. The Advanced tab groups scan review workload by kind and includes a surface-pattern audit list with exact pattern IDs, orientation, line/intersection counts, spacing, page bounds, and structural exclusion flags. Clicking a surface-pattern item shows its kind, orientation, line/intersection counts, spacing evidence, source primitive IDs/layers, page bounds, confidence, and structural exclusion flags. The Object Groups panel surfaces repeated CAD symbols and generic/unknown review groups so one family can be selected, inspected, exported, turned into an editable object-label profile template, written as an object review dataset with padded crop bounds, or written as an editable object correction dataset without rescanning the source file. Unreviewed generic/unknown symbols remain review candidates and are exported in routing ignored IDs plus detailed `ignoredObjects` records instead of becoming loop-blocking routing obstacles until a deterministic label, profile rule, correction, or real model-backed classification promotes them. Viewer-generated review and correction exports retain schema-current review crop bounds and occurrence page numbers so corrections can be traced back to the reviewed symbol evidence. Edit the downloaded correction decisions, then run `corrections-to-profile` to produce a reusable deterministic object-label profile. You can also drop exported `scan.json`, `placement.json`, or `visual-snapshot.json` artifacts from the CLI or batch runner to review detections, downstream placement packets, or visual QA metadata without rescanning the source file. Drop a Kvemo `kvemo-crops.jsonl` manifest to inspect crop groups visually, filter by review priority, training use, source kind, and promoted wall-component source kind, then use `LABELS` to download a draft object-label-profile from the manifest's review keys and group signatures. After loading a benchmark manifest draft, the viewer can overlay detector targets, filter target overlays by detector/status/page/review issue, audit missing bounds, low confidence, and missing provenance, draw or type manual benchmark target boxes for missed detections, add selected detections as new benchmark targets for objects, object groups, object aggregates, routing barriers, routing passages, routing obstacles, routing room-use hints, and routing-suppressed child objects with their semantic criteria intact, delete bad targets, correct target page/bounds, mark targets as accepted/rejected/needs-review, and download a reviewed benchmark manifest plus separate review-session JSON and Markdown report artifacts. Drop the review-session JSON again after loading the same benchmark manifest to restore decisions, bounds edits, deleted targets, added manual targets, and active filters. Drop a benchmark result JSON (`openplantrace.benchmark-result.v1`) by itself to inspect its readiness scoreboard and flattened review queue, or drop it on top of an already loaded PDF/scan to overlay scored extras, spot-check extras, and review-only detections with distinct queue colors, coordinates, evidence, source layers, and recommended actions.

Standing scanner-test rule: serious PDF scanner changes must be checked visually in the viewer, not only through unit tests or benchmark counts. For real-plan changes, save or attach before/after viewer screenshots next to the scan JSON/benchmark report, then inspect the screenshots directly and use visible failures to choose fixes: geometry that does not snap together, wrong main floorplan region, title block included as plan geometry, dimensions or furniture mistaken for walls, missing rooms/openings, noisy wall graphs, and overconfident diagnostics. Screenshots are part of the development evidence for OpenPlanTrace.

Dimension annotations export compact review bounds around the parsed text and matched dimension line while retaining witness/extension lines in source IDs and evidence. This keeps viewer and benchmark overlays from blanketing large room areas just because a valid witness line is long. Content-aware main-floorplan cropping also treats long orthogonal lines with nearby bubbled grid endpoint labels as structural anchors, so dense unlayered PDF noise does not crop labeled grid axes out of the plan area. Wall detection filters compact unknown-layer object-like linework clusters before wall merging so obvious table/furniture-style islands can remain available for object review instead of being consumed as wall geometry. It also filters small door/detail symbol clusters and very short many-fragment door/detail wall candidates before they become placement wall output. It also filters dense repeated orthogonal/parallel surface patterns, such as tight terrace, hatch, tile, or overroof grids, before wall reconstruction and exports them as first-class `surfacePatterns` with source-ID diagnostics plus `SuppressedWallPatternReview` queue items for review. A stricter default dimension-like fragment filter removes only high-gap fragmented runs with weak wall provenance and no credible parallel wall-face support, preserving longer review walls that may still be needed for room topology. The wall graph now exports connected component summaries with bounds, wall/node/edge IDs, source primitive IDs, source layers, confidence, and evidence, classifying each component as main structural, secondary structural, object-like island, or isolated fragment for review. By default, `ExcludeObjectLikeWallComponentsFromStructuralTopology` keeps object-like component walls in wall/component exports but marks them as excluded from room/opening topology solving; wall JSON, GeoJSON, SVG, and the viewer expose `wallComponentId`, `wallComponentKind`, and `excludedFromStructuralTopology` so consumers can separate structural wall evidence from object-like linework. Dense fragment-run wall filtering is intentionally opt-in (`FilterDenseFragmentLineworkFromWalls`) because real-plan visual review showed that applying it globally can remove true fragmented wall/room structure on hard industrial sheets.

To compare two exported scan JSON files, place them under the viewer's static root or serve them from reachable URLs and open:

```text
http://localhost:5077?baseline=/old-scan.json&candidate=/new-scan.json
```

The viewer keeps the candidate scan as the primary overlay, adds a Compare panel with count, quality, and diagnostic deltas, and paints candidate-only geometry green and baseline-only geometry dashed red. The older `?scan=/scan.json` URL shape still opens a single scan artifact.

For local QA, absolute JSON/PDF file paths can be loaded through the viewer query string without copying artifacts under `wwwroot`:

```text
http://localhost:5077?pdf=C:\plans\plan.pdf&scan=C:\runs\scan.json
```

To load a scan with a benchmark result review queue in one URL, place both artifacts under the viewer's static root or serve them from reachable URLs and open:

```text
http://localhost:5077?scan=/samples/routing-review-scan.json&result=/benchmark-result.json&layers=regions,walls,rooms,openings,objectAggregates,benchmarkTargets
```

To inspect a scan together with a benchmark manifest draft, add `benchmark`, `manifest`, or `draft`:

```text
http://localhost:5077?scan=/samples/annotation-reference-scan.json&benchmark=/samples/annotation-reference-benchmark-draft.json
```

To restore a saved review session in one URL, add `session`:

```text
http://localhost:5077?scan=/scan.json&benchmark=/benchmark.draft.json&session=/benchmark-review-session.json
```

The static sample `tools/OpenPlanTrace.Viewer/wwwroot/samples/annotation-reference-scan.json` exercises the v31 annotation-reference overlay. The paired `tools/OpenPlanTrace.Viewer/wwwroot/samples/annotation-reference-benchmark-draft.json` sample overlays benchmark targets for the same notes, regions, and annotation references. The sample `tools/OpenPlanTrace.Viewer/wwwroot/samples/measurement-qa-scan.json` exercises the Scale & QA measurement-consistency review rows with one consistent dimension and one outlier. The sample `tools/OpenPlanTrace.Viewer/wwwroot/samples/routing-review-scan.json` plus `routing-review-benchmark-draft.json` exercises compound object aggregates, child-object routing suppression records, routing barriers/passages/obstacles, and room-use hints; with the viewer running, open `http://localhost:5077?scan=/samples/routing-review-scan.json&benchmark=/samples/routing-review-benchmark-draft.json&layers=regions,walls,rooms,openings,objects,objectAggregates,routingLayer,benchmarkTargets`. With the viewer running, open the URL above to inspect keynote/callout marker boxes, dashed reference links, benchmark target boxes, source-layer filtering, click-through evidence, target filtering, selected-detection target authoring, manual target box authoring, bounds editing, reviewed benchmark export, review-session JSON export/reload, and Markdown review report export. Rejected targets are removed from the downloaded reviewed manifest; accepted and needs-review targets keep an `xReview` note that the evaluator ignores safely. Added targets are copied from selected real detections with their bounds, confidence, source layers, source primitive IDs, and evidence, or from manual reviewer boxes with explicit manual evidence and no invented source primitive IDs. Bounds edits update the exported target `pageNumber`/`bounds` and add `xReview.boundsEdited`. Review sessions are schema-versioned merge artifacts, not benchmarks themselves: load the benchmark manifest first, then the session, or use the combined `?benchmark=...&session=...` URL. Validate saved sessions with `openplantrace validate <session.json> --kind viewer-benchmark-review-session`.

The viewer uses the real `OpenPlanTrace.Pdf` adapter for PDF text/vector extraction. Scan JSON review renders the overlay on a blank page grid when the original PDF background is not available. It includes source-layer summaries, layer filtering, title-block metadata, dimension overlays, annotation overlays with matched keynote/callout marker references, grid-axis overlays, grid-bay overlays, wall component overlays, room-link overlays, wall-repair candidate overlays, benchmark target overlays, benchmark target filters, benchmark draft review summaries, selected-detection benchmark target authoring, manual benchmark target box authoring, benchmark target bounds editing, reviewed benchmark manifest export, benchmark review-session JSON export/reload, benchmark Markdown report export, calibration details, measurement-QA outlier ratio/spread summaries, clickable dimension-check evidence rows, source-readiness details, pipeline execution-plan dependency levels, execution waves, parallel-candidate hints, per-stage read/write/capability summaries, per-stage artifact input/output/change ledgers, structured diagnostic summaries, scan-result comparison, scan JSON download, layer JSON download, title-block JSON download, dimension JSON download, grid-axis JSON download, grid-bay JSON download, annotation JSON download, object-group JSON download, object-label profile template download, object review dataset download, calibration JSON download, and SVG overlay download. It does not invent detections for raster-only scanned PDFs; those need a real raster/OCR adapter that produces normalized raster evidence.

## Build

Install the .NET 8 SDK, then run:

```powershell
dotnet test .\OpenPlanTrace.sln
```
