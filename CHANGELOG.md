# Changelog

All notable changes to OpenPlanTrace will be documented in this file.

OpenPlanTrace uses project versions in `A.BC.DEF` format. `A` is the release
generation, `BC` is the major update track, and `DEF` is the small update or bug
fix counter. Individual JSON contracts keep their own schema versions.

## [0.02.058] - 2026-06-16

### Fixed
- Placement opening reliability now checks Wall Evidence V2 assessments for
  host and anchor wall IDs, so doors/windows attached to review-required or
  rejected wall evidence are exported as requiring review before exact
  coordinate placement.
- Opening reliability reasons now name the suspicious host/anchor wall IDs,
  keeping downstream placement of cutouts, passages, and loop routes tied back
  to the wall evidence trust decision.

### Verified
- Focused placement export tests passed with `7` tests.
- Full solution test suite passed with `492` tests.

## [0.02.057] - 2026-06-16

### Fixed
- Placement room reliability now checks Wall Evidence V2 assessments for the
  room boundary wall IDs, so a room polygon built from review-required or
  rejected wall evidence is exported as requiring review instead of looking
  coordinate-ready.
- Room reliability reasons now name the review-required or rejected boundary
  wall IDs, giving downstream engines a direct trace from room geometry back to
  suspicious wall evidence.

### Verified
- Focused placement export tests passed with `6` tests.
- Full solution test suite passed with `491` tests.

## [0.02.056] - 2026-06-16

### Fixed
- Routing barriers now suppress unprotected review-required wall evidence, so
  weak wall-like geometry that Wall Evidence V2 already marked for review does
  not leak into placement/routing output as a trusted obstacle.
- Review-required walls are still kept as routing barriers when protected by
  stronger room or opening topology, preserving real walls that downstream
  topology already depends on.

### Verified
- Focused routing layer tests passed with `8` tests.
- Full solution test suite passed with `490` tests.

## [0.02.055] - 2026-06-16

### Fixed
- Wall graph endpoint-gap repair candidates now respect topology-preparation
  trust buckets: candidates involving review-required graph walls are
  suppressed instead of adding extra snap suggestions for geometry that already
  needs wall evidence review.
- Added `wall_graph.endpoint_gap.review_candidate_trust_gated` diagnostics so
  QA tools can see when endpoint repair suggestions were withheld because a
  review wall was involved.

### Verified
- Focused wall graph, structural topology, and export tests passed with `50`
  tests.
- Full solution test suite passed with `488` tests.

## [0.02.054] - 2026-06-16

### Fixed
- Automatic wall graph coordinate repair now uses only accepted or unassessed
  graph walls as snap/trim support, preventing review-required wall candidates
  from pulling trusted wall endpoints into suspicious geometry.
- Added `wall_graph.coordinate_repair.review_support_excluded` diagnostics so
  QA tools can see when review walls stayed visible in topology but were not
  allowed to drive automatic coordinate mutation.

### Verified
- Focused wall graph, structural topology, and export tests passed with `49`
  tests.
- Full solution test suite passed with `487` tests.

## [0.02.053] - 2026-06-16

### Fixed
- Short unlayered single-line wall candidates with only one structural endpoint
  support are now downgraded to `WeakSingleLine` review evidence instead of
  becoming placement-ready medium wall bodies.
- These ambiguous short candidates still remain graph input for QA/topology,
  but enter the review-required topology bucket so automatic coordinate repair
  cannot silently snap or trim them as trusted wall geometry.

### Verified
- Focused wall evidence, recovery, structural topology, and wall graph tests
  passed with `38` tests.
- Full solution test suite passed with `486` tests.

## [0.02.052] - 2026-06-16

### Added
- Full scan JSON now exports `wallTopologyPreparation`, including graph wall
  IDs split into accepted, review-required, unassessed, automatic coordinate
  repair, and rejected buckets.
- Rejected topology-preparation wall-like details now carry category, decision,
  source primitive IDs, source layers, and evidence in the scan export so
  downstream engines can inspect why noisy geometry stayed out of the graph.
- Bumped the scan schema to `openplantrace.scan.v65` and updated the bundled
  viewer routing-review sample to exercise the new topology preparation
  contract.

### Verified
- Focused export and schema contract tests passed with `64` tests.
- Full solution test suite passed with `485` tests.

## [0.02.051] - 2026-06-16

### Fixed
- Wall graph automatic coordinate repair now respects topology-preparation
  trust buckets: accepted and unassessed graph walls may still auto snap/trim,
  while review-required graph walls participate in topology without silently
  mutating their exported centerline coordinates.
- Added `wall_graph.coordinate_repair.trust_gated` diagnostics so QA tools can
  see when suspicious wall geometry was intentionally left raw for review.

### Verified
- Focused wall graph topology, structural topology filtering, scanner pipeline,
  and schema contract tests passed with `81` tests.
- Full solution test suite passed with `484` tests.

## [0.02.050] - 2026-06-16

### Added
- `WallTopologyPreparation` now separates graph-ready walls into accepted,
  review-required, and unassessed wall ID buckets while keeping rejected
  wall-like details outside graph construction.
- The wall topology preparation diagnostic now reports accepted/review/
  unassessed graph input counts and ID samples, giving future topology repair
  and QA tools a cleaner trust boundary before snapping, trimming, room solving,
  or opening detection.

### Verified
- Focused structural topology filtering, scanner pipeline, and schema contract
  tests passed with `61` tests.
- Full solution test suite passed with `483` tests.

## [0.02.049] - 2026-06-16

### Added
- Added a dedicated `wall-topology-preparation` pipeline stage and
  `WallTopologyPreparation` artifact between Wall Evidence V2 and wall graph
  construction.
- Wall graph construction now consumes an explicit graph-ready wall ID set,
  making rejected door/detail/surface/noise evidence a first-class topology
  input decision instead of local graph-stage filtering.

### Verified
- Focused structural topology filtering, scanner pipeline, and schema contract
  tests passed with `60` tests.
- Full solution test suite passed with `482` tests.

## [0.02.048] - 2026-06-16

### Fixed
- Wall graph construction now withholds Wall Evidence V2 rejected/non-wall
  candidates from topology input while keeping those wall-like details available
  in wall evidence and wall exports for QA review.
- Wall graph diagnostics now report rejected wall-evidence candidates excluded
  from graph input by category, making retained door/detail/noise linework
  easier to audit without polluting clean graph nodes and edges.

### Verified
- Focused structural topology filtering and wall graph topology tests passed
  with `26` tests.
- Focused structural topology, wall graph topology, opening semantics, room
  semantics, routing layer, wall placement readiness, object semantics, export,
  and schema contract tests passed with `153` tests.
- Full solution test suite passed with `482` tests.

## [0.02.047] - 2026-06-16

### Fixed
- Wall type refinement now forces Wall Evidence V2 rejected/non-wall
  candidates back to `Unknown`, preventing retained door/detail/noise wall-like
  geometry from being exported or visualized as exterior or interior walls.
- Wall type diagnostics now report how many rejected evidence walls were
  protected from architectural wall classification.

### Verified
- Focused structural topology filtering and wall pair reconstruction tests
  passed with `28` tests.
- Focused structural topology, wall reconstruction, routing layer, object
  semantics, export, schema contract, and wall placement readiness tests passed
  with `118` tests.
- Full solution test suite passed with `482` tests.

## [0.02.046] - 2026-06-16

### Fixed
- Structural topology filtering now excludes Wall Evidence V2 candidates marked
  as rejected/non-wall even when those wall-like details are retained in the
  wall list for QA/export review.
- Routing barrier generation now suppresses rejected wall-evidence candidates,
  preventing retained door/detail/noise linework from becoming downstream
  routing barriers.

### Verified
- Focused structural topology filtering tests passed with `7` tests.
- Focused structural topology, routing layer, opening semantics, room
  semantics, export, schema contract, and wall placement readiness tests passed
  with `116` tests.
- Full solution test suite passed with `482` tests.

## [0.02.045] - 2026-06-16

### Fixed
- Wall Evidence V2 segment, band, and assessment geometry now stays
  synchronized with wall graph endpoint normalization, so QA overlays and
  exports follow snapped or trimmed placement-ready wall coordinates instead of
  stale pre-normalized candidate geometry.

### Verified
- Focused wall graph topology tests passed with `19` tests.
- Focused wall graph topology, export, schema contract, wall placement
  readiness, opening semantics, and scanner pipeline tests passed with `108`
  tests.
- Full solution test suite passed with `481` tests.

## [0.02.044] - 2026-06-16

### Fixed
- Wall graph near-touch endpoint inference now normalizes safe snapped wall
  endpoints into the exported wall centerline, removing tiny connector edges
  from clean graph output and improving placement-ready coordinates.

### Verified
- Focused wall graph topology tests passed with `19` tests.
- Focused wall graph topology, export, schema contract, wall placement
  readiness, opening semantics, and scanner pipeline tests passed with `108`
  tests.
- Full solution test suite passed with `481` tests.

## [0.02.043] - 2026-06-16

### Added
- Wall Evidence V2 exports now include stable rejected wall ID sets for each
  rejection family, letting visual QA and downstream importers jump directly to
  door/opening, surface-pattern, dimension/annotation, or object/fixture wall
  candidates without parsing every rejected detail.

### Verified
- Focused export, schema contract, wall layer filtering, arc-door, wall
  evidence recovery, scanner pipeline, and object semantics tests passed with
  `124` tests.
- Full solution test suite passed with `481` tests.

## [0.02.042] - 2026-06-16

### Added
- Wall Evidence V2 exports now include stable rejected-wall reason counts for
  door/opening symbols, surface-pattern details, dimension/annotation linework,
  and object/fixture details so downstream QA can compare rejection causes
  without parsing every rejected wall-like item.

### Verified
- Focused export, schema contract, wall layer filtering, arc-door, wall
  evidence recovery, scanner pipeline, and object semantics tests passed with
  `124` tests.
- Full solution test suite passed with `481` tests.

## [0.02.041] - 2026-06-16

### Fixed
- Wall Evidence V2 now rejects high-scoring paired object, fixture, service,
  dimension, text, and grid layer linework before strong-wall acceptance unless
  the pair is wall-backed or structurally supported at both ends.

### Verified
- Focused wall layer filtering, arc-door, wall evidence recovery, export,
  schema contract, scanner pipeline, and object semantics tests passed with
  `124` tests.
- Full solution test suite passed with `481` tests.

## [0.02.040] - 2026-06-16

### Fixed
- Wall Evidence V2 now rejects high-scoring paired hatch/surface-pattern
  linework from `SurfacePattern` layers before strong-wall acceptance, reducing
  false placement walls from dense tile, hatch, and detail patterns.

### Verified
- Focused wall layer filtering, arc-door, wall evidence recovery, export,
  schema contract, scanner pipeline, and object semantics tests passed with
  `122` tests.
- Full solution test suite passed with `479` tests.

## [0.02.039] - 2026-06-16

### Fixed
- Wall Evidence V2 now rejects short paired door/window frame linework from
  door/window layers before it can be accepted as a strong parallel-face wall,
  reducing false placement walls from opening symbol details.

### Verified
- Focused arc-door, wall evidence recovery, wall layer filtering, export,
  schema contract, and scanner pipeline tests passed with `103` tests.
- Full solution test suite passed with `478` tests.

## [0.02.038] - 2026-06-16

### Changed
- Wall Evidence V2 now carries source, recovered, and total wall candidate
  counts through the scan model, diagnostics, and JSON export so downstream QA
  can compare raw candidates against accepted/review/rejected wall output.

### Verified
- Focused export, schema contract, wall layer filtering, and wall evidence
  recovery tests passed with `88` tests.
- Full solution test suite passed with `477` tests.

## [0.02.037] - 2026-06-16

### Changed
- Layer analysis now recognizes hatch and surface-pattern layers as
  `SurfacePattern`, giving hatches, tile patterns, fills, and poche/detail
  pattern linework explicit provenance instead of letting it look like walls.
- Wall detection, Wall Evidence V2, and short-wall recovery now suppress
  `SurfacePattern` layer linework before it can pollute placement-ready wall
  graph output.
- Object candidate generation now ignores surface-pattern linework, keeping
  hatch/detail fills out of both structural walls and object candidates.

### Verified
- Focused layer analysis, wall layer filtering, object semantics, schema
  contract, and layer profile tests passed with `94` tests.
- Full solution test suite passed with `477` tests.

## [0.02.036] - 2026-06-16

### Changed
- Layer analysis now recognizes furniture and architectural fixture layers, so
  furniture/fixture linework can be treated as object evidence instead of
  wall evidence.
- Wall detection and Wall Evidence V2 now exclude or reject furniture and
  fixture layer linework before topology, reducing false walls from sofas,
  tables, cabinetry, counters, toilets, sinks, and similar plan details.
- Object candidate generation now maps furniture and fixture layers to
  `Furniture` and `Fixture` object categories instead of generic symbols.

### Verified
- Focused layer analysis, wall layer filtering, object semantics, schema
  contract, and layer profile tests passed with `93` tests.
- Focused wall-pair reconstruction regression tests passed with `48` tests.
- Full solution test suite passed with `476` tests.

## [0.02.035] - 2026-06-16

### Changed
- Wall Evidence V2 now rejects equipment, electrical, HVAC, plumbing, and fire
  safety layer linework as `ObjectOrFixtureDetail` before wall graphing when it
  reaches refinement as a wall candidate, keeping service/equipment geometry out
  of placement-ready structural walls.
- Wall evidence rejection diagnostics now include an
  `objectFixtureRejectedCount` property for object/fixture/service false-wall
  suppression.

### Verified
- Focused wall layer filtering, wall evidence recovery, and object semantics
  tests passed with `41` tests.
- Full solution test suite passed with `475` tests.

## [0.02.034] - 2026-06-16

### Changed
- Wall Evidence V2 now rejects unlayered short linework that is strongly tied
  to a nearby swing arc when it has at most one distinct structural support
  wall, while preserving short wall candidates supported by two structural wall
  bodies.

### Verified
- Focused arc-door, wall evidence recovery, and wall layer filtering tests
  passed with `27` tests.
- Full solution test suite passed with `474` tests.

## [0.02.033] - 2026-06-16

### Changed
- Wall Evidence V2 now rejects short door/window-layer linework tied to a nearby
  swing arc even when the candidate touches surrounding wall geometry, reducing
  false wall placement from door symbol details before wall graphing.

### Verified
- Focused arc-door, wall evidence recovery, and wall layer filtering tests
  passed with `25` tests.
- Full solution test suite passed with `472` tests.

## [0.02.032] - 2026-06-16

### Added
- Wall Evidence V2 scan exports now include explicit decision counts and stable
  wall ID sets for accepted, review-decision, and rejected wall assessments, so
  downstream engines and visual QA can compare wall membership without deriving
  it from every assessment item.

### Verified
- Focused export and schema contract tests passed with `63` tests.
- Full solution test suite passed with `471` tests.

## [0.02.031] - 2026-06-16

### Changed
- Wall graph object-like component classification now feeds back into Wall
  Evidence V2, reclassifying affected wall assessments as
  `ObjectOrFixtureDetail` with reject decisions when compact wall-like islands
  are excluded from structural topology.
- Affected wall evidence segments and wall evidence text now carry the related
  object-like component ID, preserving source primitive provenance while making
  the non-wall reason explicit for scan JSON consumers.

### Verified
- Focused object semantics, scanner pipeline, wall graph topology, export, and
  schema contract tests passed with `108` tests.

## [0.02.030] - 2026-06-16

### Added
- Placement wall exports now include `wallGraphRepairCandidateIds`, linking each
  wall directly to related wall-graph snap or trim review candidates.

### Changed
- Wall placement reliability now includes wall-graph repair review reasons when
  a wall participates in endpoint snap or endpoint-overrun trim candidates, so
  downstream engines can see exact topology concerns on the wall item itself.

### Verified
- Focused wall graph topology, export, schema contract, and placement
  validation tests passed with `95` tests.
- Full solution test suite passed with `470` tests.

## [0.02.029] - 2026-06-16

### Changed
- Scan review queues and placement issues now treat endpoint-overrun trim
  reviews as first-class wall-graph topology review items, with trim-specific
  reason text, evidence, item IDs, and recommended actions instead of generic
  unsnapped-junction wording.

### Verified
- Focused export and scan review queue tests passed with `23` tests.
- Focused wall graph, export, schema, scan review queue, and scan quality tests
  passed with `104` tests.
- Full solution test suite passed with `470` tests.

## [0.02.028] - 2026-06-16

### Changed
- Public changelog QA notes now use neutral difficulty labels and local-only
  artifact paths instead of private plan/customer identifiers.

### Verified
- Repository search for known private plan/customer tokens returned no matches.
- `.gitignore` keeps local artifacts, drawing inputs, and screenshots out of
  normal commits.
- Full solution test suite passed with `469` tests.

## [0.02.027] - 2026-06-16

### Changed
- Reviewed long endpoint-overrun tails are now suppressed from clean wall graph
  edges while keeping the original wall geometry and endpoint-overrun repair
  candidate available for review/export.
- Endpoint-overrun review evidence now requires a perpendicular support wall
  endpoint at the junction for long-tail review, preventing legitimate crossing
  walls from being downgraded to overrun cleanup.

### Verified
- Focused wall graph topology tests passed with `19` tests.
- Focused wall graph, export, schema, and wall evidence recovery tests passed
  with `84` tests.
- Full solution test suite passed with `469` tests.

## [0.02.026] - 2026-06-16

### Added
- Wall evidence recovery now includes conservative short supported wall
  segments, allowing real bathroom/entry/interior wall stubs to be recovered
  when they are backed by wall-like layer evidence and nearby structural
  endpoint support.
- Wall evidence diagnostics now split recovered wall bands from recovered short
  wall segments while preserving the total recovered wall count for existing
  consumers.

### Verified
- Focused wall evidence recovery tests passed with `3` tests.
- Focused wall evidence, wall-pair reconstruction, arc door filtering, export,
  and schema contract tests passed with `88` tests.
- Full solution test suite passed with `469` tests.

## [0.02.025] - 2026-06-16

### Added
- Wall graph repair candidates now include `EndpointOverrun` review items for
  supported wall endpoint tails that look overextended but are too long to trim
  automatically.
- Endpoint overrun repair candidates export exact source endpoint, target
  junction point, tail distance, safe auto-trim distance, source primitive IDs,
  and a `TrimEndpointOverrun` suggested action in scan and placement JSON.

### Changed
- Placement repair guidance now distinguishes endpoint-overrun trim candidates
  from endpoint snap candidates.

### Verified
- Focused wall graph topology tests passed with `19` tests.
- Focused schema/export/placement validation tests passed with `76` tests.
- Full solution test suite passed with `468` tests.

## [0.02.024] - 2026-06-16

### Added
- Wall Evidence V2 assessments now include an explicit `Accept`, `Review`, or
  `Reject` decision plus a score breakdown with positive support, negative
  penalties, pair/layer/endpoint/recovery contributions, and evidence strings.
- Scan JSON now exports wall evidence decisions and score breakdowns for
  evidence segments, bands, detailed wall assessments, and rejected wall-like
  details under the new `openplantrace.scan.v64` contract.

### Changed
- The viewer routing sample was updated to the v64 scan schema with synthetic
  wall evidence scores so debug tools can exercise the richer contract.

### Verified
- Focused arc door-leaf filtering, export contract, and scan schema tests
  passed with `64` tests.
- Full solution test suite passed with `466` tests.

## [0.02.023] - 2026-06-16

### Added
- Scan JSON export now includes a top-level `wallEvidence` section with wall
  evidence segments, wall-band support, detailed wall assessments, and explicit
  rejected wall-like details.
- Rejected wall-like details now carry bounds, optional centerline, category,
  confidence, source primitive IDs, source layers, and evidence so downstream
  QA tools can inspect why door/detail/grid/furniture-like geometry was kept
  out of accepted placement walls.
- Added the `openplantrace.scan.v63` schema artifact and updated the embedded
  scan schema resource plus the viewer routing sample to the new contract.

### Verified
- Focused export contract tests passed with `18` tests.
- Scan schema contract tests passed with `44` tests.
- Full solution test suite passed with `466` tests.

## [0.02.022] - 2026-06-16

### Fixed
- Wall evidence refinement now rejects radial door-leaf linework that is tied to
  a swing arc even when the leaf endpoint touches a real wall, preventing normal
  door geometry from being protected as structural endpoint support.

### Verified
- Focused arc door-leaf wall filtering tests passed with `2` tests.
- Wall evidence/filtering/reconstruction/opening semantics tests passed with
  `56` tests.
- Full solution test suite passed with `465` tests.

## [0.02.021] - 2026-06-16

### Changed
- The viewer's `Walls` overlay now uses wall-truth colors for visual QA:
  exterior walls draw blue, interior walls draw green, and unclassified
  placement walls draw muted amber instead of the previous hard-to-read red.
- Wall hover titles now include the architectural wall type and wall evidence
  category, making screenshot review and local truth-reference comparison
  faster.
- The overlay legend now breaks displayed placement walls into exterior,
  interior, and unclassified counts while still reporting hidden/detail wall
  candidates separately.

### Fixed
- Wall type classification now recognizes local outer-boundary walls on
  concave/L-shaped floorplan envelopes instead of relying only on the global
  rectangular wall envelope.
- Room-adjacency refinement now preserves a wall already classified as exterior
  by envelope/local-boundary evidence, avoiding false interior labels on
  exterior walls that sit beside terrace/covered/outdoor regions.

### Verified
- Focused wall reconstruction tests passed with `21` tests.
- Full solution test suite passed with `464` tests.
- Local light-plan wall-truth scan stayed stable at `54` raw walls while
  wall type classification shifted from `14` exterior / `37` interior /
  `3` unknown to `26` exterior / `25` interior / `3` unknown.
- Walls-only and PDF-background QA screenshots were regenerated under
  `artifacts/local-wall-truth/light-plan-20260616/qa-screenshots`.
  Visual review confirms the exterior outline is much closer to the local
  blue/green truth references, especially garage bottom and right-wing top and
  bottom boundaries. Remaining issues are still visible around the garage/right
  transition and bathroom/entry door/detail linework.

## [0.02.020] - 2026-06-16

### Changed
- The viewer's primary `Walls` layer now draws only placement/import wall
  candidates, hiding object-like components, isolated fragments, and
  coordinate-blocked wall candidates from normal wall QA screenshots.
- Wall overlay counts and legend rows now report the displayed placement wall
  candidates separately from hidden wall/detail candidates, so visual review no
  longer mistakes raw candidate volume for trusted wall output.

### Verified
- Full solution test suite passed with `463` tests.
- Light PDF scan produced `80` raw walls, with `68` displayed placement wall
  candidates and `12` hidden wall/detail candidates in wall-only QA.
- Hard PDF scan produced `53` raw walls, with `31` displayed placement wall
  candidates and `22` hidden wall/detail candidates in wall-only QA.
- Wall-only and PDF-background wall QA screenshots were regenerated under
  `artifacts/wall-qa-structural-overlay-filter-20260616/qa-screenshots`.
  Visual review confirms the wall overlay is easier to inspect, but the light
  plan still shows false stair/detail wall lines and the hard plan still misses
  many true walls; the next scanner leap should target wall-body recovery and
  structural-vs-detail classification, not just display filtering.

## [0.02.019] - 2026-06-16

### Added
- Placement JSON walls now export `openingCutouts` and `solidSpans` so
  downstream engines can consume exact wall centerline pieces after anchored
  door/window openings are accounted for.
- Wall solid spans now carry `adjacentOpeningIds` when the scanner already
  split a wall at an opening, preserving the door/window relationship without
  inventing a continuous wall.
- Updated the placement v6 schema and opening placement tests to cover both
  split wall fragments beside an opening and a continuous host wall with a
  cutout subtracted from it.

### Fixed
- Opening cutout projection now falls back to jamb-point projection when an
  opening placement reference line is longer than the specific wall fragment
  that claims the opening, avoiding shifted cutouts on short host fragments.

### Verified
- Focused opening, placement export, and schema contract tests passed with
  `75` tests.
- Full solution test suite passed with `463` tests.
- Light PDF scan produced `80` walls, `7` rooms, `29` openings, `29` wall
  opening cutouts, `97` wall solid spans, and `39` solid spans adjacent to
  openings; `68` walls are coordinate-ready and `14` require review.
- Hard PDF scan produced `53` walls, `7` rooms, `33` openings, `32` wall
  opening cutouts, `83` wall solid spans, and `45` solid spans adjacent to
  openings; `31` walls are coordinate-ready and `22` require review.
- Wall-only and PDF-background wall QA screenshots were regenerated under
  `artifacts/wall-opening-placement-output-20260616/qa-screenshots`. Visual
  review confirms the placement JSON now carries opening-aware wall spans, but
  the detector still under-detects many hard-plan walls and still mistakes some
  furniture/stair/detail linework for walls on the light plan.

## [0.02.018] - 2026-06-15

### Changed
- Enabled deterministic wall-evidence recovery by default so unclaimed paired
  wall-face evidence can reconstruct missing wall bodies before graphing.
- Tightened recovered wall-band acceptance: non-wall-layer recovered pairs now
  require support at both endpoints, are deduplicated against already recovered
  centerlines, and are blocked inside excluded surface/detail patterns.
- Added dense-parallel recovery suppression so repeated hatch/detail bands do
  not re-enter the wall graph as recovered walls.

### Added
- Added focused wall-evidence recovery contract tests for supported wall-band
  recovery, duplicate recovered centerline collapse, unsupported pair rejection,
  surface/detail rejection, and dense parallel detail suppression.

### Verified
- Focused recovery, wall filtering, and door-leaf tests passed with `22` tests.
- Full solution test suite passed with `463` tests.
- Light PDF scan recovered `2` missing wall bands, producing `54` walls,
  `6` rooms, and `27` openings; `52` walls are placement-ready and `2` require
  review.
- Extreme PDF scan recovered `11` missing wall bands while rejecting `7`
  surface/detail false wall candidates, producing `53` walls, `7` rooms, and
  `33` openings; `46` walls are placement-ready and `7` require review.
- Wall-only and PDF-background wall QA screenshots were regenerated under
  `artifacts/wall-band-recovery-density-tuned-20260615`. Visual review shows
  better internal wall recovery than the prior `42`-wall scan and less hatch
  noise than the raw `102`-wall recovery experiment, but the hard plan still
  needs stronger opening-aware splitting and interior partition recovery.

## [0.02.017] - 2026-06-15

### Changed
- Enabled wall-evidence noise rejection by default so confirmed non-wall
  detail candidates are removed before wall graph construction instead of only
  being marked for review.
- Surface/detail pattern evidence now rejects unlayered single-line wall
  candidates that sit inside excluded dense grid/detail regions even when they
  do not share the exact surface-pattern source primitive IDs.
- Added core internals visibility for the test assembly so individual scanner
  stages can be covered with focused contract tests.

### Fixed
- Added a door-swing leaf regression guard so short door leaf/detail linework
  near swing arcs is filtered without removing nearby real short partitions.

### Verified
- Focused wall filtering, opening semantics, and wall graph topology tests
  passed with `51` tests.
- Full solution test suite passed with `461` tests.
- Light PDF scan stayed stable with `52` walls, `5` rooms, and `26` openings;
  wall evidence reports `50` placement-ready walls and `2` review walls.
- Extreme PDF scan removed `7` surface/detail false wall candidates before
  graphing, reducing walls from the prior `49` to `42`, with `35`
  placement-ready walls and `7` review walls.
- Wall-only and PDF-background wall QA screenshots were regenerated under
  `artifacts/surface-pattern-noise-rejection-20260615`. Visual review shows the
  dense surface/detail cleanup improved noise on the extreme plan, but true
  internal wall recovery remains the next major accuracy gap.

## [0.02.016] - 2026-06-15

### Fixed
- Updated the provided-PDF golden fixture test to use the new neutral
  `pdf-extreme` fixture ID after private fixture names were removed.

### Verified
- GitHub Actions failure was traced to the stale fixture ID assertion.

## [0.02.015] - 2026-06-15

### Changed
- Removed private project-specific PDF naming from public docs, tests, and
  provided benchmark examples.
- Standardized public provided-PDF fixture labels around neutral difficulty
  tiers: `light`, `medium`, `intermediate`, and `extreme`.

### Verified
- Repo search confirmed the private PDF/project name no longer appears in
  tracked files.

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
- Light PDF scan stayed stable with `52` walls, `5` rooms, and `26`
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
- Light PDF scan stayed stable with `52` walls, `5` rooms, and `26`
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
- Light PDF scan stayed stable with `52` walls, `5` rooms, and `26`
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
- Light PDF scan passed with `52` walls, `5` rooms, `26` openings,
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
- Light PDF scan passed with `53` walls, `5` rooms, `25` openings,
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
- Light PDF scan/export validation passed with `53` walls, `5` rooms,
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
- Light PDF scan/export validation passed with `53` walls,
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
- Light PDF scan/export validation passed with `53` walls and wall
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
- Light PDF scan/export validation passed with `53` walls and wall
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
- Light PDF scan/export validation passed with `53` walls after repeated
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
