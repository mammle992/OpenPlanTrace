# Changelog

All notable changes to OpenPlanTrace will be documented in this file.

OpenPlanTrace uses project versions in `A.BC.DEF` format. `A` is the release
generation, `BC` is the major update track, and `DEF` is the small update or bug
fix counter. Individual JSON contracts keep their own schema versions.

## [0.02.140] - 2026-06-21

### Improved
- Room-grid extraction now uses median coordinate clustering instead of averaging
  each snapped coordinate cluster. This makes room boundaries less likely to be
  pulled away from the dominant wall axis by one noisy snapped graph node.
- Room graph edges now fall back to projecting drifted graph-node spans onto the
  source wall centerline when the node-to-node edge is no longer axis-aligned,
  preserving room closure without promoting review-only wall evidence into
  placement-ready output.

### Verified
- Added regression coverage for a closed room whose merged graph node drifts off
  one wall corner while the source wall centerlines remain correct.
- Focused room/wall graph/recovery tests passed with `89` tests.
- Rebuilt the solution successfully, and the full test suite passed with `593`
  tests.
- Rescanned the ignored local medium-difficulty PDF fixture and rendered a
  wall-QA screenshot with the source background. Placement-ready wall count
  stayed at `39`, omitted/review wall count stayed at `85`, and room candidates
  increased from `9` to `13`. The central recovered-wall room is back, but the
  scan now exposes extra narrow/sliver room faces that should be filtered next.

## [0.02.139] - 2026-06-21

### Improved
- Scan JSON is now `openplantrace.scan.v70`. Inline wall
  `evidenceAssessment` objects now include the same score breakdown used by the
  top-level wall evidence map, so downstream consumers can inspect why a wall
  was accepted, reviewed, or rejected without joining against a second table.
- Recovered wall evidence now compares recovered wall bands against the full
  recovered candidate set, not just original wall candidates. Adjacent recovered
  bands that duplicate a stronger recovered wall body are kept as review-only
  topology evidence instead of being exported as coordinate-ready duplicate
  walls.
- Public scan examples and viewer smoke samples were regenerated or aligned to
  the current scan schema version.

### Verified
- Added regression coverage for inline wall evidence score export, scan schema
  v70 contract metadata, and adjacent recovered wall duplicate review handling.
- Focused recovery/export/schema/documentation tests passed with `115` tests.
- Full test suite passed with `592` tests using cached/offline restore mode
  (`RestoreIgnoreFailedSources=true`) because `api.nuget.org` was unavailable.
- Rescanned the ignored local medium-difficulty PDF fixture and rendered a
  wall-QA screenshot with the source background. The duplicate recovered
  vertical wall in the middle is no longer placement-ready noise (`39`
  placement-ready walls, `85` omitted/review walls). Room closure dropped from
  `10` to `9`, so room reconstruction around recovered/review-only walls is the
  next accuracy target.

## [0.02.138] - 2026-06-21

### Improved
- Wall graph classification now promotes only strongly evidenced single paired
  wall bodies that are anchored to the main structural component into secondary
  structural wall context. This recovers real wall returns that sit just beyond
  the automatic snap tolerance without reopening the door to short door/detail
  fragments.
- Placement context guards now recognize those explicitly anchored single
  paired-wall bodies as trusted secondary structural support, so they can be
  exported as coordinate-ready when their wall evidence is strong.

### Verified
- Added regression coverage for anchored single paired-wall promotion and its
  placement review guard behavior.
- Focused wall graph/readiness/filter tests passed with `62` tests; focused
  viewer/recovery tests passed with `20` tests.
- Full test suite passed with `591` tests using cached/offline restore mode
  (`RestoreIgnoreFailedSources=true`) because `api.nuget.org` was unreachable.
- Rescanned the ignored local medium-difficulty PDF fixture and rendered a
  wall-QA screenshot with the source background. Placement-ready walls improved
  from `38` to `40`, review/omitted walls dropped from `86` to `84`, secondary
  structural wall components increased from `7` to `9`, and the prior random
  connector strokes did not reappear.

## [0.02.137] - 2026-06-21

### Fixed
- Viewer wall-QA drawing now filters sub-threshold clean topology crumbs even
  when only one span remains. This prevents tiny graph scraps around
  doors/openings from reappearing as random trusted wall strokes in scan-JSON
  screenshots.
- Recovered wall evidence no longer gets suppressed as a duplicate when the
  alleged stronger wall only overlaps a short portion of the recovered span.
  Duplicate suppression now requires a comparable-length representative wall,
  so long recovered wall bodies can become placement-ready instead of being
  hidden by a small local overlap.

### Verified
- Added regression coverage for viewer micro-span filtering and long recovered
  wall duplicate handling.
- Focused viewer/recovery tests passed with `20` tests.
- Rescanned the ignored local medium-difficulty PDF fixture with wall-QA output
  and an embedded source-page background. Placement-ready walls increased from
  `35` to `38`, omitted/review walls dropped from `89` to `86`, wall graph
  edges increased from `187` to `202`, and the middle recovered vertical wall
  now appears in the wall-only alignment screenshot without the reported random
  short connector strokes.

## [0.02.136] - 2026-06-21

### Fixed
- Viewer clean-wall drawing now honors top-level wall placement readiness flags,
  not only nested evidence assessments. Walls exported as coordinate-blocked or
  review-required no longer leak into the trusted clean-wall layer.

### Added
- Viewer Overlay Layers panel now includes one-click `DEFAULT` and `WALL QA`
  presets. `WALL QA` enables only clean wall spans for repeatable wall-accuracy
  screenshots.

### Verified
- Added viewer script contract coverage for the wall-QA preset and top-level
  wall-readiness gates.
- Focused viewer contract tests passed with `4` tests.
- Full test suite passed with `588` tests.
- Scanned an ignored local medium-difficulty PDF fixture, rendered the page
  background locally, generated an embedded-background `wall-qa` SVG, and
  rendered it headlessly for visual inspection. The trusted wall screenshot no
  longer shows the jagged non-placement wall noise from the reported layer mix,
  while the QA panel still reports `35` placement-ready walls and `89`
  omitted/review walls for ongoing accuracy work.

## [0.02.135] - 2026-06-21

### Added
- Added a dedicated SVG `wall-qa` profile for source-backed wall-accuracy
  screenshots. It renders only clean placement-grade wall topology spans while
  hiding wall body footprints, repair rays, rooms, openings, dimensions,
  objects, and other debug layers.

### Improved
- SVG QA legends now state when wall body footprints or wall graph repair
  layers are hidden, so a walls-only screenshot does not imply hidden layers are
  visible.

### Verified
- Added export coverage for `wall-qa` profile aliases and layer visibility.
- Focused export/viewer tests passed with `54` tests.
- Full test suite passed with `586` tests.
- Scanned an ignored local medium-difficulty PDF fixture with embedded source
  background using `--svg-profile wall-qa` and rendered the result for visual
  inspection. The walls-only overlay is clearer for wall alignment review while
  still reporting `35` placement-ready walls and `89` omitted/review walls in
  the QA panel.

## [0.02.134] - 2026-06-21

### Fixed
- Viewer-side wall repair blocking now mirrors placement-export semantics.
  High-severity endpoint-to-wall repairs block the endpoint/source wall that
  needs repair, but no longer mark the clean host wall as coordinate-blocked
  just because it is the snap target.

### Verified
- Added viewer script contract coverage for endpoint-to-wall host-wall repair
  impact semantics.
- Focused viewer/export tests passed with `50` tests.
- Full test suite passed with `582` tests.
- Scanned an ignored local medium-difficulty PDF fixture with an embedded
  source-page background for visual QA. The placement-review overlay shows `35`
  placement-ready walls out of `124` wall candidates, confirming the clean wall
  layer is readable while the remaining professional gap is reducing
  review/omitted wall-candidate noise.

## [0.02.133] - 2026-06-21

### Fixed
- Endpoint-to-wall repair candidates no longer block the host wall's clean
  placement geometry. High-severity endpoint snaps still block the
  source/endpoint-side wall that needs repair, while the host wall remains
  eligible for coordinate placement when its own evidence and topology are
  trustworthy.
- Placement SVG summaries now use the same repair-impact semantics as placement
  JSON, so visual QA counts match exported placement readiness.

### Verified
- Added export regression coverage proving a topology-blocked endpoint-to-wall
  repair candidate blocks the endpoint/source wall but not the clean host wall.
- Focused export/readiness tests passed with `55` tests.
- Full test suite passed with `582` tests.
- Rescanned the ignored local medium PDF fixture with embedded source
  background. Clean placement improved from `34` to `35` placement-ready walls,
  placement-omitted walls dropped from `90` to `89`, topology-blocked omissions
  dropped from `2` to `1`, and clean topology spans increased from `34` to `35`.
- Rendered and inspected the fresh source-backed placement-review screenshot.
  The overlay legend now reports `35` placement-ready walls and `35` visible
  topology spans, matching placement JSON while the source-side endpoint repair
  remains visible as a blocker.

## [0.02.132] - 2026-06-21

### Improved
- Wall graph classification now keeps compact orthogonal paired-wall returns as
  secondary structural context when they have strong wall-body evidence,
  endpoint support, and at least one strong pair score. The placement guard also
  allows these compact supported returns without room-boundary support, while
  low-score paired details, dense detail clusters, object-like islands, and
  topology-blocked companions stay out of clean placement output.

### Verified
- Added regression coverage for compact supported paired returns in both wall
  graph classification and placement readiness/export behavior.
- Focused wall-graph tests passed with `48` tests.
- Focused scan-quality/export tests passed with `71` tests.
- Full test suite passed with `581` tests.
- Rescanned the ignored local medium PDF fixture with embedded source
  background. Clean placement improved from `33` to `34` placement-ready walls;
  structural walls improved from `88` to `90`; rejected-wall-evidence omissions
  dropped from `36` to `34`; openings increased from `31` to `32`; object
  aggregates increased from `5` to `7`.
- Rendered and inspected a fresh source-backed placement-review screenshot. The
  recovered compact return appears aligned in the middle/right interior area;
  the companion wall still stays omitted behind a topology repair blocker, so
  the next accuracy work should address safe endpoint repair rather than simply
  promoting it.

## [0.02.131] - 2026-06-21

### Added
- Placement JSON now emits informational `placement.review.rejected_strong_wall_body`
  issues for long object-like wall candidates that Wall Evidence V2 rejected
  despite strong paired wall-body evidence. These candidates remain omitted and
  non-placement-ready, but review tools can jump directly to possible missed
  structural wall bodies instead of hunting through all rejected evidence.

### Verified
- Added export regression coverage proving the issue is emitted with component
  and evidence metadata, while the wall remains `rejected_wall_evidence` and
  the issue does not enter import-readiness blocking or review codes.
- Focused export tests passed with `47` tests.
- Full test suite passed with `579` tests.
- Rescanned the ignored local medium PDF fixture with embedded source
  background. Detector totals stayed stable at `124` walls, `10` rooms, and
  `31` openings, with `33` placement-ready walls and `91` omitted/review walls.
- The scan produced `5` informational rejected-strong-wall-body review issues
  and did not add this issue type to import-readiness review or blocking codes.
- Rendered and inspected the source-backed placement-review screenshot; clean
  wall placement geometry stayed unchanged while the new review issue queue
  exposes suspicious rejected wall bodies for follow-up.

## [0.02.130] - 2026-06-19

### Added
- CLI scans now support `--svg-background-embed`, which stores alignment-QA
  background page images as SVG data URIs. This makes placement-review overlays
  portable for headless screenshot rendering, benchmark review, and visual
  wall-alignment triage without manually patching SVG image paths.

### Verified
- Added parser and render-option regression coverage for embedded SVG
  background images.
- Focused export/CLI tests passed with `46` tests.
- Full test suite passed with `578` tests.
- Rescanned the ignored local medium PDF fixture using
  `--svg-background-embed`; detector totals stayed stable at `124` walls, `10`
  rooms, and `31` openings, with `33` placement-ready walls and `91`
  omitted/review walls.
- Rendered the generated SVG directly with a headless rasterizer and confirmed
  the source PDF page appears behind the placement-review wall overlay without
  manual data-URI editing.

## [0.02.129] - 2026-06-19

### Improved
- Wall Evidence V2 room-adjacency refinement can now promote a short structural
  return when one room boundary, two supported topology endpoints, and a strong
  parallel-face pair prove it is a real wall body. Duplicate recovered wall
  bodies, door/opening evidence, surface/detail patterns, object details, and
  explicit non-wall evidence still stay review-only.
- Secondary structural components without room-boundary support remain blocked
  by default, but long, thin chains of accepted high-confidence parallel-face
  wall bodies are now allowed through. This recovers real stair-side/exterior
  wall runs in plans where room detection misses the adjacent room boundary,
  without accepting ordinary secondary fragments.

### Verified
- Added regression coverage for short structural return promotion and trusted
  paired secondary wall-body chain import.
- Focused wall refinement, scan-quality, and export tests passed with `77`
  tests.
- Full test suite passed with `577` tests.
- Rescanned the ignored local medium PDF fixture and rendered source-backed
  placement-review screenshots. Detector totals stayed stable at `124` walls,
  `10` rooms, and `31` openings; clean placement output improved to `33`
  placement-ready walls and `91` omitted/review walls.
- Visual review confirmed the newly accepted stair-side wall chain lands on the
  source grey wall body, while duplicate recovered wall evidence and unrelated
  stair/detail linework remain omitted from the clean placement layer.

## [0.02.128] - 2026-06-19

### Fixed
- Room-adjacency wall refinement no longer promotes recovered duplicate wall
  bodies into placement-ready geometry when Wall Evidence V2 already said the
  wall is represented by a stronger nearby paired wall and must stay
  review-only. This prevents recovered duplicate fragments from reappearing as
  random clean wall lines in placement-review overlays and downstream placement
  JSON.

### Verified
- Added regression coverage proving room-confirmed adjacency cannot override a
  recovered duplicate wall-body blocker.
- Focused wall refinement, wall recovery, export, and scan-quality tests passed
  with `89` tests.
- Full test suite passed with `575` tests.
- Rescanned the ignored private medium PDF fixture. Detector totals stayed
  stable at `124` walls, `10` rooms, and `31` openings; clean placement output
  changed from `31` to `30` placement-ready walls and from `93` to `94`
  omitted/review walls.
- Rendered and inspected the ignored private walls-only overlay screenshot. The
  recovered duplicate wall `page:1:wall-evidence-recovered:001` is now omitted
  with `wall_evidence_review_required` and no longer appears as a clean topology
  span or wall-body footprint.

## [0.02.127] - 2026-06-19

### Improved
- Placement JSON now gives unsupported secondary structural walls a dedicated
  omission code, `secondary_without_room_boundary_support`, instead of only
  reporting a generic coordinate-review omission. This makes downstream import
  and viewer QA more explainable when strong-looking secondary wall pairs are
  withheld because no detected room boundary uses them.
- Placement-review SVG legends now show up to five wall omission buckets, so
  smaller but important review categories such as `secondary no room` are
  visible during screenshot review.

### Verified
- Added export regression coverage for the new secondary structural omission
  code, category, evidence, and summary count.
- Focused scan-quality/export tests passed with `67` tests.
- Broader wall/export/benchmark/schema test slice passed with `202` tests.
- Full test suite passed with `574` tests.
- Rescanned the ignored private medium PDF fixture. Detector totals stayed
  stable at `124` walls, `10` rooms, and `31` openings; clean placement output
  stayed at `31` placement-ready walls, `93` omitted/review walls, and `31`
  clean topology spans.
- Rendered and inspected the ignored private walls-only overlay screenshot. The
  side legend now exposes `omit: secondary no room 4`, matching the placement
  JSON omission summary.

## [0.02.126] - 2026-06-19

### Fixed
- Import readiness now uses the wall placement-readiness evaluator instead of
  confidence-only wall counts. Review-only wall evidence and topology/component
  blockers now correctly lower coordinate/metric readiness and can block
  geometry import instead of claiming the scan is placement-safe too early.
- Secondary structural wall components now need room-boundary support before
  they can become coordinate-placement-ready once rooms have been detected. This
  keeps strong-looking but isolated secondary wall pairs in review output rather
  than clean placement JSON or placement-review SVGs.

### Verified
- Added regression coverage for review-required wall evidence blocking geometry
  import and for secondary structural components without room-boundary support.
- Focused scan-quality/export tests passed with `66` tests.
- Broader wall/export/benchmark/schema test slice passed with `201` tests.
- Full test suite passed with `573` tests.
- Rescanned the ignored private medium PDF fixture. Detector totals stayed
  stable at `124` walls, `10` rooms, and `31` openings; placement-ready walls
  dropped from `33` to `31`, removing the isolated secondary vertical pair from
  clean placement output while preserving it as review evidence.
- Rendered and inspected the ignored private walls-only overlay screenshot. The
  long stray secondary vertical pair is no longer shown in clean placement
  output; remaining gaps are still visible accuracy work for the next cycle.

## [0.02.125] - 2026-06-19

### Fixed
- Wall Evidence V2 now downgrades unpaired single-line exterior boundaries near
  outdoor/covered-entry labels to review-only surface/detail evidence. This
  blocks thin covered-entry or terrace outline linework from becoming exact
  coordinate-ready structural placement unless stronger structural support is
  present.

### Verified
- Added regression coverage for a single-line `Overbygd inngang` boundary that
  must remain review-only while a real paired exterior wall stays accepted.
- Focused wall evidence, wall type, and wall graph tests passed with `68`
  tests.
- Broader wall/export/benchmark/room test slice passed with `191` tests.
- Full test suite passed with `572` tests.
- Rescanned the private medium PDF fixture. Output stayed stable at `124`
  walls, `10` rooms, `31` openings, `33` placement-ready walls, `108` routing
  items, and `60` diagnostics.
- Rendered and inspected the ignored private walls-only overlay screenshot.
  The previous room-confirmed recovered wall remains visible and no new clutter
  was introduced.

## [0.02.124] - 2026-06-19

### Added
- Wall type refinement can now promote a review-only medium/recovered wall body
  to coordinate-ready placement only when later room topology confirms it as a
  real boundary. The promotion requires structural component evidence plus
  shared room adjacency, two room references, or rooms detected on both sides.

### Fixed
- The medium private PDF fixture now recovers one missing central interior wall
  without re-promoting the loose fragments that caused random walls-only
  visualizer linework.
- Pipeline health stays clean while still recording the new room-confirmed wall
  evidence path.

### Verified
- Added regression coverage for room-confirmed wall evidence promotion and for
  one-sided room references staying review-only.
- Focused wall type and wall graph tests passed with `54` tests.
- Broader wall/export/benchmark/room test slice passed with `177` tests.
- Full test suite passed with `571` tests.
- Rescanned the private medium PDF fixture. Detector totals stayed stable at
  `124` walls, `10` rooms, and `31` openings; placement-ready walls increased
  from `32` to `33` by promoting only
  `page:1:wall-evidence-recovered:001`.
- Rendered and inspected the ignored private walls-only overlay screenshot.
  The recovered wall is visible, while earlier random diagonal/zigzag fragments
  remain omitted/review-only.

## [0.02.123] - 2026-06-19

### Fixed
- Wall graph evidence promotion is now stricter for exact placement output.
  Review-only medium/detail fragments that explicitly say exact placement is
  blocked until review are no longer promoted to placement-ready just because
  they sit inside a structural graph component.
- One-endpoint fragment and weak short paired-wall candidates are retained for
  topology/review, but kept out of trusted placement walls. This reduces the
  branchy random wall fragments seen in walls-only visualizer screenshots.

### Verified
- Added regression coverage for main structural medium wall promotion respecting
  explicit placement-blocking evidence.
- Updated long secondary fragment coverage so recovered fragments remain useful
  to topology while staying review-only for coordinate placement.
- Focused wall graph/topology/readiness tests passed with `58` tests.
- Full test suite passed with `567` tests.
- Rescanned the private medium PDF fixture. Detector totals stayed stable at
  `124` walls, `10` rooms, and `31` openings, while trusted placement-wall
  visibility dropped from `43` to `37` items. Five previously promoted review
  fragments are now kept review-only.
- Captured and inspected source-underlay and overlay-only walls-only viewer
  screenshots in the ignored private fixture output folder.

## [0.02.122] - 2026-06-19

### Added
- Scan JSON is now `openplantrace.scan.v69`.
- Added `Outdoor` room-use semantics for terrace, balcony, veranda, patio,
  covered-entry, carport, and similar outside/transition spaces.

### Fixed
- Wall type refinement now preserves or restores exterior classification when
  room adjacency or two-sided room evidence includes an outdoor/terrace space.
  This prevents outdoor covered areas from making true exterior boundaries look
  like interior walls.

### Verified
- Added regression coverage for outdoor room semantics and outdoor-adjacent wall
  type refinement.
- Focused room, wall type, schema, and export tests passed with `117` tests.
- Full test suite passed with `567` tests after the later accuracy update in
  this working tree.

## [0.02.121] - 2026-06-19

### Fixed
- Wall type refinement now lets strong room topology override an earlier
  exterior envelope/local-boundary guess. If a wall is shared by room adjacency
  or has detected rooms on both sides, it is refined to `Interior` instead of
  staying falsely marked as exterior.

### Verified
- Added regression coverage for exterior-guessed walls being corrected to
  interior from room adjacency and two-sided room evidence.
- Focused wall type, structural filtering, and wall evidence recovery tests
  passed with `24` tests.
- Full test suite passed with `564` tests.
- Rescanned the private medium PDF fixture.
  Overall detector counts remained stable at `124` walls, `218` graph nodes,
  `186` graph edges, `10` rooms, `31` openings, and `62` diagnostics.
- The medium fixture wall type distribution changed from `25` exterior,
  `54` interior, `45` unknown to `22` exterior, `57` interior, `45` unknown.
  Three walls were corrected from exterior to interior; placement readiness
  stayed stable at `37` coordinate-ready walls and `87` review walls.
- Captured and inspected a source-underlay, placement-walls-only viewer
  screenshot in the ignored private fixture output folder.
  The viewer reached `Ready`, page `1 / 1`, only `Placement walls` was enabled,
  and the PDF source underlay was visible.

## [0.02.120] - 2026-06-19

### Added
- Scan JSON is now `openplantrace.scan.v68`.
- Full scan wall and wall graph edge exports now include explicit
  `readyForCoordinatePlacement`, `requiresReview`, and `reviewReasons` fields.
  This gives downstream engines a direct import gate instead of forcing them to
  infer readiness from nested wall evidence, graph component kind, topology
  spans, and diagnostics.
- Added the documented `openplantrace.scan.v68` schema artifact and updated the
  compact scan schema to reference the new source scan schema version.
- Retired the superseded `openplantrace.scan.v67` schema artifact from the
  public tree; Git history remains the archive for old alpha scan contracts.

### Verified
- Added regression coverage for scan JSON marking review-only isolated wall
  geometry as not coordinate-ready.
- Focused export and schema tests passed with `89` tests.
- Full test suite passed with `562` tests.
- Rescanned the private medium PDF fixture.
  Output remained stable at `124` walls, `218` graph nodes, `186` graph edges,
  `10` rooms, `31` openings, and `62` diagnostics.
- Readiness audit on the scan JSON reported `37` coordinate-ready walls,
  `87` review walls, `73` coordinate-ready wall graph edges, `113` review wall
  graph edges, and `0` missing readiness flag sets.
- Captured and inspected a source-underlay, placement-walls-only viewer
  screenshot in the ignored private fixture output folder.
  The viewer reached `Ready`, only `Placement walls` was enabled, and rendered
  `0` visible off-axis placement wall lines.

## [0.02.119] - 2026-06-18

### Fixed
- Scan JSON topology spans and wall graph edge placement lines now project back
  onto the source wall axis before export. Averaged wall graph nodes can still
  exist for diagnostics, but exported wall placement coordinates no longer bend
  into tiny diagonal connector lines when a junction node is slightly off-axis.
- Trusted endpoint repair now uses the perpendicular wall-axis intersection at
  near corners and trims tiny approved endpoint tails, reducing noisy fragments
  where real wall ends should meet cleanly.
- Tiny trusted endpoint gaps just over the safe snap tolerance can now snap even
  near opening evidence, while larger opening-sized gaps remain review-only.

### Verified
- Added regression coverage for full scan JSON exporting off-axis graph spans
  back onto the orthogonal source wall axis.
- Focused export and wall graph tests passed with `68` tests.
- Full test suite passed with `561` tests.
- Rescanned the new private medium PDF fixture. The scan produced `124` walls,
  `218` graph nodes, `186` graph edges, `10` rooms, `31` openings, and `62`
  diagnostics.
- Exported off-axis wall graph/topology lines on that fixture dropped from `19`
  to `10`; the remaining off-axis graph lines are excluded `ObjectLikeIsland`
  diagnostics, not visible placement walls.
- Captured and inspected a source-underlay, placement-walls-only viewer
  screenshot in the ignored private fixture output folder.
  The viewer reached `Ready`, only `Placement walls` was enabled, and rendered
  `0` visible off-axis placement wall lines.

## [0.02.118] - 2026-06-18

### Fixed
- Short, isolated wall graph fragments with no topology-supported endpoints no
  longer stay marked as placement-ready just because their earlier wall evidence
  assessment accepted them. They are now kept as review-only medium wall
  evidence with a diagnostic instead of being allowed to masquerade as exact
  wall placement data.

### Verified
- Added regression coverage for an accepted short isolated wall candidate being
  reclassified to review-only before placement export.
- Focused wall graph tests passed with `22` tests.
- Full test suite passed with `557` tests.
- Rescanned the private medium PDF fixture. Evidence-ready wall assessments
  dropped from `42` to `39`; the three affected walls now report
  `placementReady = false` and remain available in review diagnostics.
- Captured and inspected a source-underlay, placement-walls-only viewer
  screenshot:
  `real-pdf-output/private-medium-fixture-v137/viewer-source-underlay-placement-walls-only-v137-ready.png`.
  The viewer reached `Ready`, page `1 / 1`, and rendered `41` visible wall
  overlay items. The isolated random wall candidates are reduced, while the next
  major accuracy work remains interior wall continuity and wall-face pairing.

## [0.02.117] - 2026-06-18

### Fixed
- Wall graph endpoint repair now preserves the original wall axis when it
  normalizes snapped or trimmed endpoints. A horizontal or vertical wall can be
  shortened, extended, or snapped into a better junction without being bent into
  a diagonal placement wall.

### Verified
- Added regression coverage for axis-preserving wall endpoint normalization.
- Focused wall graph tests passed with `21` tests.
- Full test suite passed with `556` tests.
- Rescanned the private medium PDF fixture. Placement-ready walls stayed at
  `37`, omitted walls stayed at `87`, and placement-ready off-axis walls
  dropped from `2` to `0`. Solid spans updated from `141` to `143`, openings
  from `29` to `31`, and routing items from `107` to `110`.
- Captured and inspected a source-underlay wall-only viewer screenshot for the
  private medium fixture. The previous random sloped placement wall is gone;
  remaining visible accuracy work is now mostly false-positive suppression
  around furniture, casework, and detail lines rather than endpoint bending.

## [0.02.116] - 2026-06-18

### Added
- Viewer QA now distinguishes source-aligned review from overlay-only review.
  When a PDF underlay is present the visualizer shows a `PDF source underlay`
  badge; when only scan/placement/snapshot JSON is loaded it shows
  `Overlay-only: source PDF not loaded`. This makes wall-only screenshots much
  harder to misread as source-aligned evidence.
- The viewer now supports local PDF + JSON multi-file loads in one drop/open
  operation. Placement JSON, scan JSON, or visual snapshot JSON can be paired
  with the source PDF so wall accuracy can be inspected directly against the
  drawing instead of on a blank page.

### Verified
- JavaScript syntax check passed with the bundled Node runtime.
- Focused wall-graph regression tests passed with `3` tests.
- Solution build passed after NuGet restore was allowed for verification.
- Full test suite passed with `555` tests using `--no-restore`.
- Viewer local file endpoints served the private medium PDF and latest
  placement JSON successfully.
- Captured and inspected a controlled headless viewer screenshot with the
  source PDF underlay:
  `real-pdf-output/private-medium-fixture-v135/viewer-source-underlay-walls-cdp.png`.
  The viewer reached `Ready`, page `1 / 1`, and showed the `PDF source
  underlay` badge.
- Captured and inspected the JSON-only control screenshot:
  `real-pdf-output/private-medium-fixture-v135/viewer-overlay-only-warning.png`.
  It shows the overlay-only warning badge on the blank-grid render.

### Notes
- The source-aligned medium screenshot makes the next scanner accuracy target
  clearer: several top/right exterior walls and some interior walls align, but
  the left stair/entrance side, several interior spans, and some
  furniture/detail-vs-wall decisions still need substantial wall-face recovery
  work.

## [0.02.115] - 2026-06-18

### Fixed
- Compact paired-wall component recovery now has an explicit pair-score
  guardrail. Strong compact wall clusters below the structural recovery
  threshold stay object-like/review-only instead of being promoted into
  secondary structural topology. This blocks borderline recoveries that can add
  apparent walls while creating high-severity topology import blockers.

### Verified
- Added regression coverage proving compact strong paired-wall clusters with a
  `0.67` pair score remain object-like, while higher-confidence compact strong
  paired-wall clusters can still be retained as secondary structural context.
- Focused component/object guardrail tests passed with `6` tests.
- Full test suite passed with `555` tests.
- Medium private PDF smoke scan confirmed the unsafe borderline v134 recovery
  was rejected again in v135: placement-ready walls stayed at the safer `37`,
  omitted walls `87`, topology spans `37`, solid wall spans `141`, wall-graph
  repair candidates `2`, openings `29`, and routing items `107`. This avoids
  the v134 topology-import-blocked candidate while preserving the safe v133
  compact structural recovery.
- Light private PDF smoke scan stayed stable: placement-ready walls `17`,
  omitted walls `39`, topology spans `17`, solid spans `75`, wall-graph repair
  candidates `2`, openings `24`, and routing items `78`.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v135/placement.json` and
  `real-pdf-output/private-light-fixture-v135/placement.json`.
- Rendered and inspected wall-only QA screenshots:
  `real-pdf-output/private-medium-fixture-v135/viewer-placement-walls-only.png`
  and
  `real-pdf-output/private-light-fixture-v135/viewer-placement-walls-only.png`.
  The medium visual keeps the safe recovered vertical structural cluster but
  avoids the questionable extra L-shaped recovery that produced a topology
  blocker; broad missing wall recovery remains the next accuracy target.

## [0.02.114] - 2026-06-18

### Fixed
- Compact disconnected wall graph components now have a guarded structural
  escape hatch before they are demoted as object-like linework. A compact
  component near the main structural graph is retained as secondary structural
  context only when it is orthogonal, has no diagonal detail lines, contains at
  least two high-confidence placement-ready `StrongWallBody` paired-wall
  segments, has enough total wall length, and has no hard-risk, weak-pair,
  duplicate, or opening-like evidence. Medium/review-only paired-wall clusters
  still become object/fixture detail, and existing table/car/stair detail
  protections stay in place.

### Verified
- Added wall-graph regression coverage for compact strong paired-wall cluster
  recovery and compact medium paired-wall cluster rejection.
- Focused component/object guardrail tests passed with `5` tests.
- Full test suite passed with `554` tests.
- Medium private PDF smoke scan recovered one compact structural component
  (`page:1:wall-component:5`) from object-like/excluded to secondary structural.
  Placement-ready walls rose from `35` to `37`, omitted walls dropped from `89`
  to `87`, rejected wall evidence dropped from `40` to `38`, topology spans rose
  from `35` to `37`, solid wall spans rose from `140` to `141`, openings rose
  from `27` to `29`, and routing items rose from `105` to `107`; wall-graph
  repair candidates stayed at `2`.
- Light private PDF smoke scan stayed stable: placement-ready walls `17`,
  omitted walls `39`, topology spans `17`, solid spans `75`, openings `24`, and
  routing items `78`.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v133/placement.json` and
  `real-pdf-output/private-light-fixture-v133/placement.json`.
- Rendered and inspected wall-only QA screenshots:
  `real-pdf-output/private-medium-fixture-v133/viewer-placement-walls-only.png`
  and
  `real-pdf-output/private-light-fixture-v133/viewer-placement-walls-only.png`.
  The medium visual now shows the extra recovered vertical structural cluster
  without changing the light fixture output, but broad missing wall recovery and
  better source-aligned visual comparison remain open accuracy work.

## [0.02.113] - 2026-06-18

### Fixed
- Trusted endpoint-to-wall snapping now has a stricter paired-wall evidence
  path. Strong placement-ready paired wall bodies, and high-pair-score medium
  paired wall bodies that are otherwise eligible for one-endpoint structural
  promotion, can use the wider paired endpoint tolerance when snapping to a
  trusted perpendicular host wall. Weak/fragmented, duplicate, hard-risk,
  opening-like, review-blocked, or too-short candidates remain blocked. This
  reduces floating wall-return fragments without opening the door to door-leaf
  or detail-line noise.

### Verified
- Added wall-graph regression coverage for extended paired-wall endpoint
  snapping and weak medium-pair rejection.
- Focused wall-graph snap tests passed with `4` tests.
- Full test suite passed with `552` tests.
- Medium private PDF smoke scan completed with no placement-ready wall-count
  inflation: placement-ready walls stayed at `35`, omitted walls stayed at
  `89`, topology spans stayed at `35`, solid wall spans changed from `141` to
  `140`, wall-graph repair candidates dropped from `4` to `2`, trusted endpoint
  snap diagnostics rose from `9` to `15`, and normalized snapped endpoint gaps
  rose from `5` to `6`.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v132/placement.json`.
- Light private PDF smoke scan completed and deep placement validation passed
  for `real-pdf-output/private-light-fixture-v132/placement.json`.
- Rendered and inspected wall-only QA screenshots:
  `real-pdf-output/private-medium-fixture-v132/viewer-placement-walls-only.png`
  and
  `real-pdf-output/private-light-fixture-v132/viewer-placement-walls-only.png`.
  The paired snap cleanup reduces some floating repair work, but both images
  still show missing real wall structure and remaining review markers; the next
  accuracy pass should focus on recovering complete exterior/interior wall
  bodies from source geometry instead of only repairing endpoints.

## [0.02.112] - 2026-06-18

### Fixed
- Main-structural medium paired-wall evidence can now be promoted when it has
  one topology-supported endpoint, a high parsed pair score, interior wall type,
  enough wall length, and no weak/fragmented, duplicate, or hard-risk evidence.
  This recovers short real partition returns that previously stayed review-only
  while keeping weaker one-endpoint candidates blocked.

### Verified
- Added wall-graph regression coverage for high-score one-endpoint medium
  paired-wall promotion and weak-score one-endpoint rejection.
- Focused wall-graph promotion tests passed with `4` tests.
- Full test suite passed with `550` tests.
- Medium PDF smoke scan completed with the private medium PDF fixture.
  Placement-ready wall output rose from `34` to `35`, review-only wall evidence
  dropped from `4` to `3`, `no_clean_topology_spans` stayed at `0`, and
  rejected/isolated wall-noise counts stayed unchanged.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v131/placement.json`.
- Rendered and inspected a wall-only QA screenshot:
  `real-pdf-output/private-medium-fixture-v131/viewer-placement-walls-only.png`.
  The recovered short wall is visible without adding random wall noise, but the
  overlay remains too sparse overall; the next wall pass should recover broader
  missing real structure.

## [0.02.111] - 2026-06-18

### Fixed
- Clean placement wall topology now keeps trusted short dangling wall returns
  when they come from placement-ready paired-wall evidence inside a structural
  component. Short single-line/off-axis connectors are still filtered out, so
  this recovers real wall returns without reopening the random-line noise path.
- Clean wall-run merging now keeps tiny contiguous fragments long enough to
  merge them into a trusted placement run, then applies the minimum-length gate
  to the merged run. This prevents the tail of a real fragmented wall from being
  discarded before it can reconnect to the rest of the wall.

### Verified
- Added placement export regression coverage for trusted short paired-wall
  returns, promoted medium paired-wall returns, short connector rejection, and
  tiny contiguous fragment merging.
- Focused placement export tests passed with `5` tests.
- Full test suite passed with `548` tests.
- Medium PDF smoke scan completed with
  `private medium PDF fixture`. Placement-ready wall
  output rose from `32` to `34`, `no_clean_topology_spans` omissions dropped
  from `2` to `0`, and rejected/isolated wall-noise counts stayed unchanged.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v130/placement.json`.
- Rendered and inspected a wall-only QA screenshot:
  `real-pdf-output/private-medium-fixture-v130/viewer-placement-walls-only.png`.
  The recovered output is cleaner, but still too sparse; the next accuracy pass
  should focus on recovering missing real wall structure, not merely suppressing
  noise.

## [0.02.110] - 2026-06-18

### Changed
- Placement-ready wall topology spans and solid wall span centerlines now use the
  midpoint between detected paired wall faces when reliable pair evidence exists,
  instead of always reusing the raw source wall centerline. This keeps raw wall
  provenance intact while improving downstream placement alignment for thick
  walls whose detected source line is biased toward one face.

### Verified
- Added placement export regression coverage for pair-centered topology spans and
  solid spans.
- Focused placement export tests passed with `3` tests.
- Export, placement validation, and viewer script contract tests passed with
  `55` tests.
- Full test suite passed with `545` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Placement-ready wall
  counts stayed stable at `32` topology spans and `141` solid spans while
  pair-evidence placement spans now carry non-zero source projection distances
  when centered between detected faces.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v129/placement.json`.
- Rendered and inspected a wall-only visualizer screenshot:
  `real-pdf-output/private-medium-fixture-v129/viewer-placement-walls-only.png`.
  The overlay remains clean and stable; remaining visual issues are still
  missing/fragmented interior spans and broader source wall-body recognition.

## [0.02.109] - 2026-06-18

### Fixed
- Long interior fragment-merged wall components that sit near the main wall body
  can now be recovered as secondary structural walls instead of staying isolated
  review-only fragments. This helps recover real bathroom/entry partition walls
  while short detail-sized fragments remain blocked from placement output.

### Verified
- Added wall-graph regression coverage for long interior fragment recovery and
  short interior fragment rejection.
- Focused graph recovery tests passed with `2` tests.
- Related wall graph/topology/evidence suites passed with `53` tests.
- Full test suite passed with `545` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Placement-ready wall
  output rose from `29` to `32` walls after promoting
  `page:1:wall:134`, `page:1:wall:149`, and `page:1:wall:155` as long interior
  fragment walls.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v128/placement.json`.
- Rendered and inspected a wall-only viewer screenshot:
  `real-pdf-output/private-medium-fixture-v128/viewer-placement-walls-only.png`.
  The previous random-line overlay remains gone, and the new promoted interior
  spans are visible, but visual QA still shows remaining wall centering/alignment
  drift and fragmented/missing interior spans for the next accuracy pass.

## [0.02.108] - 2026-06-18

### Changed
- The visualizer's main wall overlay is now explicitly a `Placement walls`
  layer and draws only clean placement-ready topology spans, instead of falling
  back to raw wall evidence lines that made wall-only screenshots look random.
- Wall overlay styling no longer marks every placement wall as dashed just
  because metric scale is unavailable. Dashed walls are now reserved for actual
  geometry/evidence review in page-coordinate placement.

### Fixed
- Long unlayered fragment-merged wall candidates now need trusted structural
  support before they can become exact placement geometry. Unsupported candidates
  are kept for topology/review, and object-like fragment clusters can be rejected
  before they pollute clean placement output.

### Verified
- Added wall-evidence regression coverage for unsupported and supported
  unlayered fragment-merged wall candidates.
- Focused wall-evidence tests passed with `13` tests.
- Full test suite passed with `543` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Placement-ready wall
  spans dropped from `35` to `29`, wall graph edges dropped from `199` to `188`,
  accepted wall evidence dropped from `41` to `36`, and rejected wall-like noise
  increased from `35` to `40`.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v127/placement.json`.
- Rendered and inspected wall-only visualizer screenshots:
  `real-pdf-output/private-medium-fixture-v127/viewer-placement-walls-only.png`
  and
  `real-pdf-output/private-medium-fixture-v127/viewer-placement-walls-on-pdf.png`.
  The random raw-line mess is gone and the overlay is easier to read, but visual
  QA still shows missing/fragmented interior walls and some exterior centering
  drift that need the next accuracy pass.

## [0.02.107] - 2026-06-18

### Fixed
- Recovered unknown paired wall bodies that strongly overlap a nearby known
  structural paired wall are now marked review-only duplicate geometry before
  exact placement. This prevents a duplicate recovered wall face/body from
  becoming a clean downstream wall span.

### Verified
- Added wall-evidence regression coverage for recovered duplicate paired bodies.
- Focused wall-evidence tests passed with `11` tests.
- Full test suite passed with `541` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Clean placement spans
  dropped from `38` to `35`, raw wall graph edges dropped from `215` to `199`,
  and clean placement spans now include `0` unknown wall-type spans.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v125/placement.json`.
- Rendered and inspected the wall-only viewer screenshot
  `real-pdf-output/private-medium-fixture-v125/viewer-placement-clean-spans-only.png`.
  The previous odd unclassified vertical span is gone, while remaining wall
  coverage gaps are still visible and need later accuracy work.

## [0.02.106] - 2026-06-18

### Added
- Scan and placement wall topology spans now expose structured
  `sourceWallGraphEdgeIds`, so compact merged clean wall runs can be traced back
  to every raw wall graph edge that produced them without parsing free-text
  evidence.

### Changed
- README placement guidance now documents the raw-edge provenance field for
  downstream importers that consume compact wall geometry.

### Verified
- Added schema/export contract coverage for single-edge, projected, and
  multi-edge merged clean wall topology spans.
- Focused schema/export tests passed with `84` tests.
- Full test suite passed with `540` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The placement packet
  reports `38` clean topology spans and preserves `215` raw wall graph edges;
  `18` clean spans merge more than one raw edge, with up to `6` raw edge IDs on
  a single compact span.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v124/placement.json`.
- Rendered and inspected the wall-only viewer screenshot
  `real-pdf-output/private-medium-fixture-v124/viewer-placement-clean-spans-only.png`.
  The clean wall layer is much less noisy than raw graph output, but visual QA
  still shows incomplete wall coverage and one odd vertical non-blue/green span
  needing future accuracy work. Source-PDF background rendering was unavailable
  in headless verification because local PDF rendering tools are not installed
  and Edge's native headless PDF capture returned a blank page.

## [0.02.105] - 2026-06-18

### Changed
- Compact placement wall `topologySpans` now use the same merged clean
  source-wall runs as placement-review SVG overlays, instead of exporting many
  smaller raw graph-edge fragments as downstream wall geometry.
- Placement exports still preserve raw wall graph edge coordinates and evidence
  under `wallGraph.edges`, so consumers get cleaner import spans without losing
  audit/debug provenance.
- README placement-export guidance now states that wall `topologySpans` are
  merged clean placement runs and that `wallGraph.edges` is the edge-level audit
  source.

### Verified
- Updated placement export regression coverage for merged downstream wall runs
  and raw graph edge retention.
- Focused placement/export tests passed with `5` tests.
- Full test suite passed with `540` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The compact
  `placement.json` now reports `38` wall topology spans, matching the
  placement-review SVG clean layer, while retaining `215` raw wall graph edges.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v123/placement.json`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v123/placement-review-page-1.png`.

## [0.02.104] - 2026-06-18

### Changed
- The viewer now merges normalized scan-mode clean wall spans into longer
  source-wall runs before drawing the clean wall-span layer.
- Walls-only scan review screenshots are less fragmented while raw scan JSON
  still keeps the original wall graph evidence for audit/debug work.

### Verified
- Extended viewer contract coverage for clean wall-span run merging.
- Focused viewer contract tests passed with `2` tests.
- Full test suite passed with `540` tests.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v122/viewer-scan-merged-clean-wall-spans.png`
  from the medium PDF scan JSON. The clean wall-span layer dropped from `86`
  drawn spans to `43` merged visual runs.
- The local viewer was started hidden at `127.0.0.1:5077` for QA and stopped
  after the screenshot.

## [0.02.103] - 2026-06-18

### Fixed
- The interactive viewer now normalizes scan-JSON wall topology spans before
  drawing them, so live PDF scans and full scan JSON no longer show raw off-axis
  graph fragments as clean wall geometry.
- Short off-axis scan-mode connector spans are suppressed in the viewer clean
  wall-span layer, matching the placement-export cleanup path without removing
  raw audit evidence from scan JSON.

### Verified
- Added viewer contract coverage for scan-mode wall-span normalization.
- Focused viewer/export tests passed with `4` tests.
- Full test suite passed with `540` tests.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v122/viewer-scan-clean-wall-spans.png`
  from `scan.json` with only the clean wall-span layer enabled; the previous
  off-axis spaghetti pattern is no longer visible in the viewer path.
- The in-app browser runtime was unavailable in this sandbox, so visual QA used
  hidden headless Microsoft Edge against the local viewer at `127.0.0.1:5077`.

## [0.02.102] - 2026-06-18

### Fixed
- Placement wall topology spans are now projected back onto their trusted
  orthogonal source wall axis when graph endpoints drift off-axis, removing
  crooked connector chains from placement JSON and the viewer walls-only layer.
- Ultra-short off-axis connector spans are now dropped from clean placement
  topology instead of being exported as real wall geometry.

### Verified
- Added regression coverage for off-axis wall graph span projection and short
  connector suppression in placement exports.
- Focused placement export tests passed with `3` tests.
- Full test suite passed with `539` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Exported placement
  topology spans dropped from `118` to `81`, with `0` remaining off-axis spans
  above one drawing unit.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v122/placement.json`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v122/placement-review-page-1.png`;
  the previous random loose wall-chain pattern is gone from the walls-only
  visual review.

## [0.02.101] - 2026-06-18

### Changed
- Placement-review SVG overlays now show wall import-readiness directly in the
  right QA rail: placement-ready wall count, omitted/review wall count, and the
  top wall omission causes.
- The SVG legend now mirrors the placement v9 wall-readiness contract so visual
  screenshots immediately reveal whether noisy wall-like geometry is clean
  import geometry or review-only evidence.

### Verified
- Added regression coverage for placement-review legend rows that expose
  placement-ready and omitted/review wall counts.
- Focused placement-review SVG tests passed with `3` tests.
- Focused export/schema/CLI placement validation tests passed with `96` tests.
- Full test suite passed with `537` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The placement-review
  SVG now reports `39` placement-ready walls, `85` omitted/review walls, and top
  omission categories: rejected evidence, isolated fragments, and duplicate
  faces.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v121/placement.json`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v121/placement-review-page-1.png`.

## [0.02.100] - 2026-06-18

### Changed
- Placement JSON now uses `openplantrace.placement.v9` and reports
  document/page wall-readiness summary fields: placement-ready wall count,
  placement-omitted wall count, clean wall topology span count, solid wall span
  count, and omission-code totals.
- Placement validation now checks those summary fields against the actual wall
  array, so import-readiness numbers cannot drift away from the emitted wall
  geometry and omission evidence.

### Verified
- Added regression coverage for placement-ready/omitted wall summary fields,
  page summary mirrors, placement omission code totals, and the placement v9
  schema artifact.
- Focused export/schema/CLI placement validation tests passed with `96` tests.
- Full test suite passed with `537` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The placement summary
  reported `124` walls, `39` placement-ready walls, `85` omitted/review walls,
  `118` clean topology spans, and `141` solid wall spans.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v120/placement.json`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v120/placement-review-page-1.png`;
  visual geometry is intentionally unchanged from the prior pass, while
  readiness totals are now available directly in placement JSON.

## [0.02.099] - 2026-06-18

### Changed
- Placement JSON now uses `openplantrace.placement.v8` and exports structured
  wall `placementOmission` objects for walls that are not clean
  coordinate-placement topology. Omission codes distinguish topology-import
  blockers, duplicate wall faces, rejected evidence, object-like linework,
  isolated fragments, fragment-geometry review, wall-evidence review, and
  missing clean topology spans.
- Wall omission records include recommended actions, linked wall IDs, repair
  candidate IDs, and evidence so downstream importers can skip noisy wall-like
  linework without parsing free-text diagnostics.

### Verified
- Added regression coverage for placement-ready walls with no omission,
  review-required wall evidence omissions, topology-import-blocked repair
  omissions, and the placement v8 schema artifact.
- Focused export/schema tests passed with `82` tests.
- Full test suite passed with `537` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The scan exported
  `124` walls: `39` placement-ready walls with no omission and `85`
  review/omitted walls with structured omission codes.
- Deep placement validation passed for
  `real-pdf-output/private-medium-fixture-v119/placement.json`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v119/placement-review-page-1.png`.
  The view now separates placement-ready spans from blocked/review repair
  evidence; detector-level wall recovery and remaining missing/false walls are
  still the next accuracy target.

## [0.02.098] - 2026-06-18

### Changed
- Placement-review wall body footprints now reuse placement solid spans with
  anchored opening cutouts. Door/window cutouts no longer render as a single
  uninterrupted wall body in visual QA or snapshot counts.
- Wall evidence refinement now has a late graph-aware promotion pass for trusted
  `MediumWallBody` candidates inside a main structural component. The pass only
  promotes paired wall bodies with two supported graph endpoints and blocks
  duplicates, hard-risk evidence, and topology-import-blocking repair
  candidates.

### Verified
- Added regression coverage for cutout-aware SVG wall body footprints, trusted
  main-structural medium wall promotion, and duplicate/review wall promotion
  blocking.
- Focused wall graph/export tests passed with `4` tests.
- Full test suite passed with `537` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The repair-aware
  promotion safely moved `page:1:wall:105` into placement-ready output while
  leaving the risky `page:1:wall:111` endpoint-repair wall review-only.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v115/placement-review-background-side-rail.png`
  and
  `real-pdf-output/private-medium-fixture-v115-walls-only/placement-review-side-rail.png`.
  The walls-only view gained one aligned interior wall and kept the risky
  endpoint-repair wall hidden; larger missing-wall recovery remains the next
  accuracy target.

## [0.02.097] - 2026-06-18

### Changed
- Placement-review SVG overlays now reserve a right-side QA rail for legends and
  diagnostics instead of drawing those panels over plan geometry. Source PDF
  backgrounds stay constrained to the real page area so exported page
  coordinates remain visually honest.
- Clean placement topology spans now regularize nearly-collinear horizontal and
  vertical runs onto a shared centerline. This reduces tiny coordinate wobbles
  that made walls look like random kinked line chains in wall-only screenshots.
- Placement JSON now uses graph-faithful but coordinate-regularized placement
  spans for wall import geometry while keeping raw wall graph edge geometry
  available for diagnostics.

### Verified
- Added regression coverage for off-page SVG QA panels, page-bound background
  images, regularized placement-review spans, and placement JSON clean-span
  export.
- Focused export and placement contract tests passed with `7` tests.
- Private medium PDF fixture smoke scan completed outside the repo. The scan
  regularized `55` placement spans across `20` walls while preserving raw graph
  diagnostics.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v112/placement-review-background-side-rail.png`
  and `real-pdf-output/private-medium-fixture-v112-walls-only/placement-review-side-rail.png`;
  the previous long random kink chain is reduced, but missing wall recovery and
  false door/detail wall fragments still need detector-level work.

## [0.02.096] - 2026-06-18

### Changed
- Wall type refinement no longer flips a wall from `Interior` to `Exterior`
  solely because room detection found a room on only one side. One-sided room
  evidence now preserves prior interior envelope/topology classification, which
  reduces false exterior walls when room detection misses the opposite side of a
  real interior partition.

### Verified
- Added regression coverage proving one-sided room evidence does not override a
  prior interior wall-envelope classification.
- Focused wall-type refinement test passed.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Wall geometry counts
  stayed stable, while wall typing improved from `40` exterior / `39` interior
  to `25` exterior / `54` interior.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v110/placement-review-background-page-fit.png`;
  the middle/right interior partitions now render green instead of false
  exterior blue while the outer shell remains blue.

## [0.02.095] - 2026-06-18

### Changed
- The viewer now treats scan-only `TopologyImportBlocked` wall graph repair
  candidates as coordinate-blocking evidence, even when a raw scan JSON has not
  been converted to placement JSON yet. Clean structural wall layers no longer
  draw those blocked wall IDs as placement-ready lines.
- Viewer cache busting was bumped so the browser reloads the corrected script.

### Verified
- Added a viewer script contract test that guards the raw-scan fallback for
  `TopologyImportBlocked` repair candidates.
- Viewer JavaScript syntax check passed with the bundled Node.js runtime.
- Focused export, wall-graph, wall-placement, arc-door filtering, and viewer
  contract tests passed with `78` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The high-severity repair
  involving `page:1:wall:111` and `page:1:wall:54` remains visible as QA, while
  clean topology spans stay at `37`, wall body footprints stay at `37`, and
  hidden non-placement topology spans stay at `99`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v109/placement-review-page-fit.png`;
  the previous random-line failure is now reduced to a small repair marker
  instead of a long clean wall.

## [0.02.094] - 2026-06-18

### Changed
- Placement-review SVG overlays now hide wall topology spans and wall body
  footprints for walls involved in `TopologyImportBlocked` repair candidates.
  The repair candidate itself remains visible as a QA layer, but blocked walls no
  longer appear as clean placement-ready geometry.
- Visual snapshots now reflect those blocked spans as hidden non-placement
  topology, keeping screenshot QA aligned with placement JSON reliability.

### Verified
- Added placement-review renderer coverage proving import-blocked walls are
  omitted from clean topology spans and wall body footprints while the repair QA
  layer remains visible.
- Focused export, wall-graph, and placement-readiness tests passed with `67`
  tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. Clean visible topology
  spans dropped from `38` to `37`, wall body footprints dropped from `38` to
  `37`, and hidden non-placement topology spans rose from `98` to `99`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v108/placement-review-page-fit.png`;
  the blocked repair remains visible while the affected wall is removed from
  clean placement-review geometry.

## [0.02.093] - 2026-06-18

### Changed
- Placement reliability now treats `TopologyImportBlocked` wall graph repair
  candidates as coordinate-placement blockers for the involved walls. The wall
  remains exported with its source geometry and repair candidate IDs, but
  downstream engines should no longer treat it as exact-placement ready.
- Wall graph repair review reasons in placement JSON now include the repair
  candidate import impact (`TopologyReviewRequired` or `TopologyImportBlocked`)
  so consumers can distinguish review warnings from blocking topology defects.

### Verified
- Added readiness and placement JSON coverage for import-blocking wall graph
  repair candidates.
- Focused export, wall-graph, and wall-placement tests passed with `67` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The blocking repair
  involving `page:1:wall:54` now makes that wall coordinate-not-ready, dropping
  coordinate-ready walls from `40` to `39` and total coordinate-ready placement
  entities from `64` to `63`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v107/placement-review-page-fit.png`;
  the visual wall overlay remains stable while the JSON reliability contract is
  stricter.

## [0.02.092] - 2026-06-18

### Changed
- Short unlayered parallel-face wall candidates are now demoted to review-only
  when they have only one structurally supported endpoint and weak or fragmented
  pair evidence. This catches door/stair/detail fragments that previously looked
  like clean interior walls in placement-review output.
- The wall evidence message now records the endpoint-support reason, pair score,
  and face-fragment count so future screenshot QA can explain why a candidate was
  blocked from exact placement.

### Verified
- Added regression coverage for a weak short unlayered parallel pair supported
  by only one structural endpoint.
- Focused wall evidence tests passed with `46` tests.
- Full test suite passed with `528` tests.
- Private medium PDF smoke scan completed with
  `private medium PDF fixture`. The false accepted wall
  `page:1:wall:9` is now review-only instead of placement-ready, clean
  placement-ready walls dropped from `45` to `43`, and rendered screenshots were
  inspected at
  `real-pdf-output/private-medium-fixture-v106/placement-review-page-fit.png`.
  The worst random short pair in the left/stair area is removed from the clean
  placement overlay, while other questionable fragment-merged walls remain as
  candidates for the next accuracy pass.

## [0.02.091] - 2026-06-18

### Changed
- SVG overlays can now embed source page background images for alignment QA.
  The direct `scan` command accepts `--svg-background`, `--svg-background-dir`,
  and `--svg-background-opacity`, with per-page directories using
  `page-N.png`, `page-N.jpg`, `page-N.jpeg`, or `page-N.webp`.
- The README now documents the background-image workflow for PDF-over-overlay
  screenshots. The scan, placement, and GeoJSON contracts remain unchanged; the
  background is only a visual review aid.

### Verified
- Added renderer coverage for SVG background image embedding and parser
  coverage for the new scan CLI flags.
- Focused export tests passed with `33` tests.
- Full test suite passed with `527` tests.
- Private medium PDF smoke scan completed with source page background embedded
  in the placement-review SVG. Rendered and inspected
  `real-pdf-output/private-medium-fixture-v104/placement-review-with-background.png`.
  The background view confirms the current exterior wall alignment is mostly
  useful, while several interior/detail regions still need stricter
  source-aware wall promotion before they are professional-grade.

## [0.02.090] - 2026-06-18

### Changed
- Placement-review SVGs and the viewer now expose a separate wall body
  footprint layer. The layer draws translucent wall-body polygons from detected
  paired wall faces when possible and falls back to centerline plus thickness
  when only a line wall is available.
- Visual snapshots now report `wallBodyFootprints` as their own overlay layer,
  so screenshot QA and JSON QA can compare the same wall-body geometry.
- Wall evidence now marks single-line or fragment-merged duplicate wall-face
  lines as review-only when they are already represented by a stronger paired
  wall body. This reduces doubled/offset wall faces in clean placement output
  without deleting the underlying evidence.

### Verified
- Added regression coverage proving placement-review SVGs and visual snapshots
  expose wall body footprints alongside clean topology spans.
- Focused wall evidence/export tests passed with `45` tests.
- Viewer JavaScript syntax check passed with the bundled Node.js runtime.
- Full test suite passed with `525` tests.
- Private medium PDF smoke scan completed. Clean placement-review wall spans
  dropped from `48` to `39`, wall body footprints dropped from `48` to `39`,
  hidden non-placement topology spans rose from `86` to `95`, wall graph edges
  dropped from `240` to `215`, and routing items dropped from `144` to `117`.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v103/placement-review-svg.png`. The
  duplicate-face cleanup is visible, but the center cluster still needs a
  PDF-background comparison pass before further aggressive accuracy tuning.

## [0.02.089] - 2026-06-18

### Changed
- Placement `solidSpans` now prefer detected paired wall-face evidence when
  building `bodyPolygon` geometry. Single-line or otherwise unpaired walls still
  fall back to the centerline-plus-thickness rectangle.
- Solid-span evidence now states whether the body polygon came from detected
  paired wall faces or from the fallback centerline/thickness path.

### Verified
- Added a regression proving asymmetric paired wall-face evidence controls the
  exported solid-span body polygon instead of the fallback centerline rectangle.
- Focused export, opening semantics, and schema-contract tests passed with `90`
  tests.
- Full test suite passed with `525` tests.
- Private medium PDF smoke scan completed; placement JSON validation passed as
  `openplantrace.placement.v7`. All `141` solid spans had closed body polygons;
  `55` used detected paired wall-face evidence and `86` used the fallback path.
- Scan JSON, compact scan JSON, GeoJSON, and placement JSON validation all
  passed after regeneration.
- Rendered and inspected
  `real-pdf-output/private-medium-fixture-v101/placement-review-svg.png`. The
  visible wall-line overlay is unchanged by this data-contract improvement, so a
  future viewer/SVG wall-body footprint layer is needed to visually inspect the
  new polygons directly.

## [0.02.088] - 2026-06-18

### Changed
- Placement JSON is now `openplantrace.placement.v7`.
- Wall `solidSpans` now export closed wall-body footprint polygons, footprint
  bounds, along/normal vectors, drawing-unit thickness, and metric thickness
  when calibration is available. Downstream engines can now use wall rectangles
  directly instead of reconstructing wall bodies from centerlines.
- Documented placement v7 in the README and added the v7 JSON Schema artifact.

### Verified
- Added export/schema/opening regressions for closed solid-span body polygons,
  footprint bounds, wall direction vectors, and metric-scaled footprint data.
- Focused export, opening semantics, and schema-contract tests passed with `89`
  tests.
- Full test suite passed with `524` tests.
- Private medium PDF smoke scan completed; all `141` exported wall solid spans
  include closed body polygons. Scan JSON, placement JSON, compact scan JSON, and
  GeoJSON validation all passed. The PDF has unreliable calibration, so metric
  body polygons correctly remain null.
- Rendered and inspected the placement-review SVG screenshot at
  `real-pdf-output/private-medium-fixture-v100/placement-review-svg.png`.
  The clean wall layer is less noisy than the earlier random-line failure, but
  still shows suspicious short stubs/disconnected segments that need the next
  wall-body/envelope reconstruction pass.

## [0.02.087] - 2026-06-18

### Changed
- Wall Evidence V2 now keeps short unlayered/unknown recovered wall segments
  review-only instead of promoting them directly to coordinate-ready wall
  placement. Wall-layer-backed short recovery still remains eligible for
  placement.
- Short unknown fragment-merged wall candidates now require review before exact
  placement, which reduces false walls made from healed door, furniture, or
  detail fragments.
- Repeated short unlayered linework groups are now treated as object/detail
  review candidates instead of placement-ready walls. This targets visual soup
  such as repeated furniture, stair, shelf, or window/detail strokes riding
  inside the main structural wall graph.
- Placement-review SVG visibility now follows the stricter placement-readiness
  contract and keeps isolated wall graph fragments out of the clean wall layer.

### Verified
- Added regressions for review-only short unlayered recovered walls, review-only
  short unknown fragment-merged walls, repeated short unlayered detail groups,
  isolated-fragment placement blocking, and clean SVG topology visibility.
- Focused wall evidence/export/readiness tests passed with `44` tests.
- Full test suite passed with `524` tests.
- Private medium PDF smoke scan completed with `48` visible clean wall topology
  spans, `86` hidden non-placement topology spans, and `3` wall graph repair
  candidates after the fix. The wall-only screenshot is materially cleaner:
  repeated bottom furniture/detail tails and several isolated recovered fragments
  no longer appear in the clean placement layer.
- Private medium scan JSON, compact scan JSON, GeoJSON, and placement JSON all
  passed CLI validation after regeneration.

## [0.02.086] - 2026-06-17

### Changed
- Wall Evidence V2 now has a narrow continuity-supported acceptance path for
  short unlayered paired wall bodies. Candidates that would normally be
  review-only because they are short can become placement-ready when they have
  structural support and a same-axis collinear continuation from a stronger wall
  run. Explicit door/window, surface-pattern, object/fixture, dimension, and
  outdoor-boundary filters still run before this promotion.
- Added `wall_evidence.continuity_supported_pairs_promoted` diagnostics so
  benchmark logs can explain which short paired wall chunks were trusted due to
  collinear structural continuity.

### Verified
- Added a regression proving a short exterior paired wall chunk aligned with a
  stronger shell continuation is accepted instead of left review-only.
- Focused wall-evidence recovery tests passed with `7` tests.
- Private medium PDF smoke scan completed with `70` visible clean wall topology
  spans, `131` hidden non-placement topology spans, and `4` wall graph repair
  candidates. The pass promoted `3` short paired wall candidates at the wall
  evidence stage; graph component filtering still excluded object-like geometry
  after evidence refinement.
- Private medium scan JSON, compact scan JSON, GeoJSON, and placement JSON all
  passed CLI validation after regeneration.

## [0.02.085] - 2026-06-17

### Changed
- Placement-review wall overlays now recover trusted isolated wall fragments
  when wall evidence marks them as placement-ready `StrongWallBody` or
  `RecoveredWallBody`. Weak, review-only, object-like, and rejected fragments
  still stay out of the clean wall view, which lets real short wall chunks come
  back without reopening dense detail noise.
- Wall graph classification now demotes compact dense stair/detail-like
  components from structural topology, so repeated stair treads and similar
  detail linework do not masquerade as placement-ready walls.
- The wall graph stage now performs a conservative trusted endpoint-to-wall snap
  for low-risk gaps between strong host walls and accepted wall endpoints,
  reducing manual repair candidates while keeping larger gaps in the review
  layer.
- Wall graph main-region fallback now handles direct scan contexts without a
  detected main floorplan region instead of accidentally using a zero-size
  default bounds value.

### Verified
- Added focused regressions for dense stair/detail component demotion, trusted
  endpoint-to-wall auto-snapping, and trusted isolated wall visibility.
- Focused export and wall-graph topology tests passed with `58` tests.
- Private medium PDF smoke scan completed with `69` visible clean wall topology
  spans, `132` hidden non-placement topology spans, and `3` wall graph repair
  candidates after the fix. The previously reported random stair/detail cluster
  is gone from the wall-only screenshot, but full wall-body continuity still
  needs a larger reconstruction pass.

## [0.02.084] - 2026-06-17

### Added
- Placement-review SVG overlays now include a separate `wall-graph-repairs`
  layer for endpoint gap and overrun repair candidates. The layer draws the
  suggested repair line plus source/target markers and keeps severity/import
  impact in the title evidence, so screenshots show where topology is known to
  be broken without pretending the clean wall geometry has already been fixed.
- Visual snapshots now include a `wallGraphRepairs` layer with per-page counts,
  bounds, confidence summaries, and severity breakdowns for repeatable visual
  QA and benchmark review.

### Verified
- Added an export regression proving placement-review SVGs and visual snapshots
  expose high-severity wall graph repair candidates as a separate QA layer.
- Export-focused tests passed with `30` tests.
- Private medium residential PDF smoke scan completed with `87` merged visible
  clean wall runs, `50` hidden non-placement topology spans, and `5` wall graph
  repair candidates, including `1` blocking candidate.
- Private medium scan JSON, deep placement JSON, and visual snapshot validation
  all passed after regenerating the output.
- Captured a headless placement-review screenshot showing orange repair markers
  at the scanner's known topology near-miss points. The next accuracy step is to
  convert low-risk repair candidates into safer deterministic graph repairs.

## [0.02.083] - 2026-06-17

### Changed
- Placement-review SVG overlays and visual snapshots now merge visible clean
  wall topology fragments back into longer source-wall runs before drawing or
  counting the default `wallTopologySpans` layer. Raw graph spans remain in the
  scan/placement evidence for diagnostics, but the wall-only QA view is less
  fragmented and closer to downstream placement geometry.
- The clean wall-span filter now keeps review-only, topology-excluded,
  object-like, isolated-fragment, and short dangling non-placement spans out of
  the default placement-review layer while preserving them in the hidden
  `wallTopologyReviewSpans` layer for debug review.
- Opening placement exports now normalize reversed host-wall projections so
  start/end offsets, host-wall parameters, jamb lines, and footprint corner
  order stay internally consistent even when the reference wall line runs in
  the opposite direction from the detected opening symbol.

### Verified
- Updated export regressions so a dense host wall split into five graph edges is
  shown as one clean placement run while repeated short detail teeth remain
  hidden from the clean layer.
- Export-focused tests passed with `29` tests, and the full solution test suite
  passed with `516` tests.
- Private easy residential PDF smoke scan completed with `38` merged visible
  clean wall runs and `22` hidden non-placement topology spans, down from `116`
  visible topology fragments before this merge pass.
- Private medium residential PDF smoke scan completed with `87` merged visible
  clean wall runs and `50` hidden non-placement topology spans, down from `289`
  visible topology fragments before this merge pass.
- The regenerated private medium residential PDF placement export now passes
  deep placement validation after fixing reversed opening/passage offset order.
- Captured headless placement-review screenshots for both PDFs. Visual QA shows
  a cleaner wall-only overlay, but the engine still misses some real wall runs
  and keeps some non-wall linework, so the next major accuracy step remains true
  wall-body/envelope reconstruction rather than more display filtering.

## [0.02.082] - 2026-06-17

### Changed
- Placement-review SVG overlays now draw only placement-ready structural wall
  topology spans in the clean `wall-topology-spans` layer. Review-only or
  topology-excluded spans, plus object-like or isolated-fragment component
  spans, remain available in full/debug output but no longer masquerade as clean
  downstream wall geometry.
- Visual snapshots now split hidden non-placement wall topology spans into a
  separate `wallTopologyReviewSpans` layer count so QA tools can see how much
  geometry was withheld from the clean placement overlay.
- The browser viewer now keeps `Clean wall spans` default-on for placement-ready
  geometry and adds an optional `Non-placement wall spans` debug layer for
  suspicious or coordinate-blocked wall evidence.

### Verified
- Added export regressions proving placement-review SVGs omit review-only wall
  topology spans while visual snapshots still count them as hidden review spans.
- Export-focused tests passed with `27` tests, and the full solution test suite
  passed with `514` tests.
- Private easy residential PDF smoke scan completed with `128` visible
  placement-ready topology spans and `10` hidden review topology spans before
  the isolated-fragment cleanup, then `126` visible placement-ready topology
  spans and `12` hidden non-placement topology spans after the cleanup. Scan
  JSON, deep placement JSON, and visual snapshot validation all passed.
- Captured a headless rendered placement-review SVG screenshot for visual QA.
  The clean layer is less polluted, but the screenshot still shows broken or
  missing exterior runs and several small hanging center spans, so the next
  accuracy pass should focus on stronger wall-body reconstruction and exterior
  continuity.

## [0.02.081] - 2026-06-17

### Changed
- Wall graph coordinate repair now has a narrow trusted-review support path:
  high-confidence interior `MediumWallBody` review walls with strong paired-wall
  band evidence may split/snap topology while still remaining review-required in
  exported evidence. This helps recover real interior wall junctions from
  unlayered or dimension-ish source linework without accepting weak/detail
  walls as placement-ready geometry.
- Review-gate diagnostics now separate trusted review support from still-gated
  review walls with `wall_graph.coordinate_repair.trusted_review_support` and
  trusted/excluded counts on related wall graph diagnostics.

### Verified
- Added a wall graph regression proving a high-confidence interior review wall
  can participate in coordinate repair while remaining review-only in topology
  preparation.
- Focused wall graph topology tests passed with `26` tests and the full solution
  test suite passed with `514` tests.
- Private easy residential PDF smoke scan improved from `133` to `138` clean
  graph edges, `5` to `6` rooms, and reduced review-gated endpoint candidates
  from `117` to `54` by trusting only `2` interior review walls for coordinate
  repair. Placement-review screenshot was captured for wall-only visual QA.
- Private medium residential PDF smoke scan completed with `5` trusted review
  support walls out of `28` review walls, and scan/placement JSON validation
  passed. Placement-review screenshot was captured for wall-only visual QA.

## [0.02.080] - 2026-06-17

### Changed
- Wall graph normalization now has a paired endpoint-to-wall repair path for
  repeated parallel wall-face endpoints just beyond the normal single-endpoint
  snap distance. This closes supported structural wall returns without raising
  the global snap tolerance that would risk turning door swings into walls.
- Paired endpoint snapping is vetoed near local door/window/opening evidence,
  and emits `wall_graph.endpoint_gap.paired_support_snapped` diagnostics with
  the safe single-endpoint tolerance, paired tolerance, and support separation.

### Verified
- Added wall graph regressions proving paired wall-face endpoint gaps snap when
  two supported faces meet the same host wall, while nearby opening evidence
  prevents the same geometry from auto-snapping.
- Focused wall graph topology tests passed with `25` tests.
- Private easy residential PDF smoke scan completed with repair candidates
  reduced from `7` to `4`, clean graph edges increased from `127` to `133`, and
  the placement-review screenshot captured for wall-only visual QA.

## [0.02.079] - 2026-06-17

### Changed
- SVG overlays now expose clean wall graph topology spans as a first-class
  `wall-topology-spans` layer, separate from raw wall evidence. The new
  `placement-review` SVG profile draws only the cleaned topology spans so
  downstream placement geometry can be reviewed without raw wall overrun noise.
- The CLI now uses `placement-review` as the default SVG profile for scan and
  batch outputs. `structural-review` and `full` remain available when raw
  evidence or all diagnostic layers are needed.
- The browser visualizer now has a default-on `Clean wall spans` layer beside
  the raw `Walls` layer, making it easier to compare evidence geometry against
  placement-ready graph spans.

### Verified
- Added export regressions proving `placement-review` emits cleaned split wall
  topology spans, hides the raw wall layer, and records the new layer in visual
  snapshot metadata.
- Export-focused tests passed with `27` tests, and focused wall topology/export
  tests passed with `50` tests.
- Full solution test suite passed with `511` tests.
- Private easy residential PDF smoke scan completed with `placement-review`
  SVG output, and scan JSON, placement JSON, GeoJSON, and visual snapshot JSON
  all passed CLI validation.
- Captured a headless rendered `placement-review` SVG screenshot for visual QA;
  the clean span overlay is much easier to inspect, while still showing that
  topology remains incomplete in several building areas.

## [0.02.078] - 2026-06-17

### Changed
- Wall Evidence V2 now downgrades clean, thin, unlayered outdoor/covered-area
  boundary pairs near labels such as `overbygd`, `covered`, `terrace`, or
  `patio` to review-only evidence when they only have weak structural support.
  Fragmented outdoor boundary detail is still rejected outright, while thicker
  supported exterior walls can still become placement-ready.

### Verified
- Added a regression test proving a clean covered-entry boundary is review-only
  before it can be accepted as a strong wall body, while a real thick exterior
  wall remains accepted.
- Focused wall-evidence recovery tests passed with `6` tests.
- Full solution test suite passed with `509` tests.
- Private easy residential PDF smoke scan completed; scan counts stayed stable
  against `0.02.077`, confirming the new rule did not disturb that benchmark.
- Scan JSON, placement JSON, and GeoJSON all passed CLI validation.
- Captured a walls-only rendered SVG/PNG fallback for visual review when the
  in-app browser was blocked by the managed sandbox.

## [0.02.077] - 2026-06-17

### Changed
- Routing export now blocks non-trusted wall evidence from protecting routing
  barriers. Review-required, rejected, or otherwise non-placement-ready walls
  can still appear in scan diagnostics, but they no longer keep uncertain
  geometry alive as trusted downstream routing output.

### Verified
- Added a routing-layer regression test proving review-required wall evidence is
  suppressed even when room topology references it.
- Focused routing tests passed with `8` tests.
- Full solution test suite passed with `508` tests.
- Private easy residential PDF smoke scan completed with `11` non-trusted wall
  evidence IDs blocked from routing protection and one additional isolated
  routing fragment suppressed compared to `0.02.076`.
- Scan JSON, placement JSON, and GeoJSON all passed CLI validation.
- Captured readable walls-only visualizer screenshots for review.

## [0.02.076] - 2026-06-17

### Changed
- Wall Evidence V2 now downgrades short unlayered parallel-face wall candidates
  with fewer than two distinct structural supports to review-only geometry
  instead of marking them placement-ready. This keeps uncertain wall-like detail
  available for diagnostics/topology while blocking automatic exact placement.

### Verified
- Added a regression test for short weakly supported unlayered parallel-face
  candidates; focused wall filtering tests passed with `35` tests.
- Full solution test suite passed with `508` tests.
- Private easy residential PDF smoke scan completed with `4` short paired walls
  moved from placement-ready to review-only, reducing accepted wall evidence
  from `53` to `49`, graph edges from `134` to `127`, and rooms from `5` to
  `4`.
- Scan JSON passed validation and placement JSON passed deep validation.
- Captured a walls-only visualizer screenshot for review.

## [0.02.075] - 2026-06-17

### Fixed
- Wall Evidence V2 now rejects short unlayered parallel door/window frame
  linework that is strongly tied to a nearby swing arc before it can be accepted
  as a strong double-line wall. This targets real-plan middle-area noise where
  door frame/detail pairs were visually promoted into wall geometry.

### Verified
- Added a regression test for unlayered paired door-frame linework near a swing
  arc; focused arc-door wall filtering tests passed with `9` tests.
- Full solution test suite passed with `507` tests.
- Private easy residential PDF smoke scan completed, scan JSON passed
  validation, and placement JSON passed deep validation.

## [0.02.074] - 2026-06-17

### Documentation
- Clarified in `README.md` that OpenPlanTrace GeoJSON is a
  GeoJSON-compatible page-coordinate `FeatureCollection`, not WGS84/GPS map
  GeoJSON, and that `schemaVersion`/`coordinateSpace` are allowed foreign
  members for the OpenPlanTrace contract.

## [0.02.073] - 2026-06-17

### Added
- Added public sanitized output examples under `docs/examples`: one full
  OpenPlanTrace scan JSON artifact and one page-coordinate GeoJSON feature
  collection generated from the public `semantic-smoke.dxf` fixture.
- Added `docs/examples/README.md` with regeneration instructions for public
  output examples.
- Added documentation-example tests that keep those examples tied to the
  current scan and GeoJSON schema contracts.

### Changed
- Pruned historical generated `openplantrace.scan.v*.schema.json` alpha
  snapshots from the working repository so the current scan contract remains
  documented without letting old generated schemas dominate the project line
  count.
- Documented repository hygiene rules for generated scan outputs and schema
  artifact retention.

### Tooling
- Added `tools/clean-local-outputs.ps1` to safely remove ignored local scan,
  benchmark, coverage, and test-output folders after heavy QA sessions.

### Verified
- Local ignored QA outputs were cleaned, reclaiming about `3.97 GB`.
- Repository text-file line count dropped from about `356k` to about `167k`
  after pruning obsolete generated scan-schema snapshots.
- Schema contract tests passed with `45` tests.
- Full solution test suite passed with `504` tests.
- Public scan and GeoJSON examples both passed CLI validation.
- Documentation example tests passed with `2` tests.

## [0.02.072] - 2026-06-17

### Added
- Added `openplantrace.scan.compact.v1`, a lossless compact scan export that
  dictionary-encodes repeated strings and shape-encodes repeated JSON object
  patterns while preserving the normal `openplantrace.scan.v67` scan tree for
  expansion and validation.
- Added CLI outputs `--compact-scan <path>` and `--compact-scan-gzip <path>`;
  `scan --out-dir` now writes `scan.compact.json` and
  `scan.compact.json.gz` beside the full forensic `scan.json`.
- Added `openplantrace schema scan-compact` and `openplantrace validate`
  support for compact scan artifacts.

### Verified
- Focused export and schema tests passed with `70` tests.
- Full solution test suite passed with `504` tests.
- Private easy residential PDF smoke scan emitted valid full and compact scan
  artifacts; minified full scan was `7.38 MB`, compact scan was `2.58 MB`, and
  gzipped compact scan was `306 KB`.
- Gzipped compact scan roundtripped back to JSON and passed
  `openplantrace validate` as `openplantrace.scan.compact.v1`.

## [0.02.071] - 2026-06-17

### Fixed
- Wall Evidence V2 now rejects thin, fragmented exterior candidates near
  covered/outdoor area labels before strong paired-wall acceptance, preventing
  covered-entry, terrace, canopy, porch, balcony, and similar boundary/detail
  linework from being exported as structural exterior wall placement geometry.

### Verified
- Focused wall evidence refinement tests passed with `19` tests.
- Full solution test suite passed with `502` tests.
- Private easy residential PDF smoke scan emitted `openplantrace.scan.v67`,
  passed validation, and rejected the covered-entry exterior boundary candidates
  before placement export.
- Private easy residential PDF visual QA loaded with Walls-only enabled and no
  longer drew exterior wall overlay around the covered-entry strip.

## [0.02.070] - 2026-06-17

### Improved
- Made visualizer wall overlays easier to inspect by drawing a subtle white
  backing stroke behind each placement wall and increasing exterior/interior
  wall stroke weights.
- Matched the wall legend and exported SVG styling to the stronger visualizer
  wall colors so screenshots, review sessions, and overlay exports read the
  same way.
- Added no-cache static-file headers and bumped viewer asset versions so local
  QA reloads pick up visualizer changes immediately.

### Verified
- Viewer JavaScript syntax check passed with the bundled Node runtime.
- Viewer Release build passed.
- Full solution test suite passed with `501` tests.
- Private easy residential PDF visual QA loaded with Walls-only enabled and
  drew matching colored wall lines plus wall backing strokes for every visible
  wall span.

## [0.02.069] - 2026-06-17

### Fixed
- Wall Evidence V2 short-wall recovery now suppresses repeated unlayered
  short supported linework with nearly identical spans, preventing closet,
  shelf, fixture, and cabinet slot details from being promoted into recovered
  placement walls.

### Improved
- Added a `wall_evidence.short_repeated_slots_suppressed` diagnostic so QA can
  see when short repeated details were held back from wall recovery, including
  source primitive counts and samples.

### Verified
- Focused wall evidence, wall layer, and door/detail filtering tests passed
  with `37` tests.
- Full solution test suite passed with `501` tests.
- Private easy residential PDF smoke scan emitted `openplantrace.scan.v67`,
  passed validation, and reduced repeated recovered-short wall noise from the
  wall graph.
- Private hard PDF smoke scan emitted `openplantrace.scan.v67` and passed scan
  validation.

## [0.02.068] - 2026-06-17

### Fixed
- Short unlayered single-line candidates now need support from two distinct
  structural walls before Wall Evidence V2 promotes them to placement-ready
  medium wall bodies.
- Detail-like short lines whose endpoints are only near the same structural
  wall are kept as review-only weak linework instead of being treated as
  trusted wall placement geometry.

### Improved
- Wall Evidence V2 now records an explicit reason when a short unlayered
  candidate has endpoint support but only from one distinct structural wall.

### Verified
- Focused wall evidence/layer/recovery tests passed with `36` tests.
- Full solution test suite passed with `500` tests.
- Private provided PDF smoke scan emitted `openplantrace.scan.v67`, passed scan
  validation, preserved the cleaner `125` node / `161` edge wall graph, and
  preserved populated metric fields on `161 / 161` wall graph edges.

## [0.02.067] - 2026-06-17

### Improved
- Wall graph topology now trust-gates junction splitting: review-required graph
  walls remain visible for QA, but no longer split accepted/unassessed wall
  graph geometry before they are confirmed as structural.
- Added a `wall_graph.junctions.review_trust_gated` diagnostic so scans explain
  when uncertain walls were prevented from creating topology junctions.

### Fixed
- Reduced noisy wall graph nodes/edges caused by weak wall-like candidates,
  such as door/detail lines, touching trusted wall geometry.

### Verified
- Focused wall topology and structural filtering tests passed with `31` tests.
- Full solution test suite passed with `499` tests.
- Private provided PDF smoke scan emitted `openplantrace.scan.v67`, passed scan
  validation, suppressed `15` review-wall junction pairs, and produced a
  cleaner wall graph with `125` nodes / `161` edges while preserving populated
  metric fields on `161 / 161` wall graph edges.

## [0.02.066] - 2026-06-17

### Added
- Scan JSON schema `openplantrace.scan.v67` now exports metric-ready wall
  graph edge geometry: `lineMillimeters`, `boundsMillimeters`, `lengthMeters`,
  `thicknessDrawingUnits`, `thicknessMillimeters`, `measurementScaleGroupId`,
  and `millimetersPerDrawingUnit`.

### Improved
- Scan-level wall graph edges now carry the same downstream placement scale
  context as placement graph edges, making the main scan JSON more useful for
  consumers that need precise edge coordinates without reading a second
  placement artifact.

### Verified
- Focused schema/export tests passed with `68` tests.
- Full solution test suite passed with `498` tests.
- Private provided PDF smoke scan emitted `openplantrace.scan.v67`, passed scan
  validation, and confirmed `184 / 184` wall graph edges included populated
  metric coordinate fields.

## [0.02.065] - 2026-06-17

### Added
- Scan JSON schema `openplantrace.scan.v66` now exports wall graph edge
  structural trust fields: `wallComponentId`, `wallComponentKind`, and
  `excludedFromStructuralTopology`.

### Fixed
- Scan-level wall graph edges now use the same shared `WallStructuralTrust`
  rule as walls, placement exports, GeoJSON, and SVG overlays, so rejected
  Wall Evidence V2 wall-like details are marked as non-structural directly on
  graph edges.
- Wall graph edge evidence now records when an edge was excluded because its
  source wall evidence was rejected as non-wall/noise, preserving provenance
  for downstream consumers.

### Verified
- Focused schema/export tests passed with `68` tests.
- Full solution test suite passed with `498` tests.
- Private provided PDF smoke scan emitted `openplantrace.scan.v66`, passed scan
  validation, and confirmed `184 / 184` wall graph edges included the new trust
  fields.

## [0.02.064] - 2026-06-16

### Fixed
- Added a shared core `WallStructuralTrust` helper so quality/readiness,
  placement exports, scan exports, GeoJSON, and SVG overlays use one
  deterministic rule for rejected Wall Evidence V2 structural exclusion.
- Placement wall-graph edge exports now apply rejected wall evidence to
  `excludedFromStructuralTopology`, excluded-edge summary counts, and edge
  evidence, preventing retained non-wall evidence edges from being counted as
  structural downstream.

### Verified
- Focused export tests passed with `24` tests.
- Focused scan quality, structural-topology, wall placement readiness, and
  routing layer tests passed with `40` tests.
- Full solution test suite passed with `498` tests.

## [0.02.063] - 2026-06-16

### Fixed
- SVG wall overlays now use the shared Wall Evidence V2 structural-exclusion
  rule, so rejected wall-like details render with excluded-wall styling,
  reduced opacity, accurate hover text, and correct legend counts instead of
  looking like normal structural wall output.

### Verified
- Focused export/SVG tests passed with `24` tests.
- Focused scan quality, structural-topology, and wall placement readiness tests
  passed with `32` tests.
- Full solution test suite passed with `498` tests.

## [0.02.062] - 2026-06-16

### Fixed
- Scan JSON, placement JSON, and GeoJSON now share one Wall Evidence V2
  structural-exclusion rule, so rejected wall-like details are consistently
  marked as excluded from structural topology across every downstream contract.

### Verified
- Focused export tests passed with `23` tests.
- Focused scan quality, structural-topology, and wall placement readiness tests
  passed with `32` tests.
- Full solution test suite passed with `497` tests.

## [0.02.061] - 2026-06-16

### Fixed
- Shared structural wall selection now excludes Wall Evidence V2 candidates
  marked as rejected/non-wall, so rejected door/detail/object geometry cannot
  inflate quality detector counts or import-readiness ratios.
- Placement wall exports now mark rejected wall evidence as excluded from
  structural topology while still exporting the rejected wall-like detail, its
  source IDs, reliability reasons, and evidence for QA review.

### Verified
- Focused scan quality/import readiness tests passed with `21` tests.
- Focused export, structural-topology, and wall placement readiness tests
  passed with `33` tests.
- Full solution test suite passed with `496` tests.

## [0.02.060] - 2026-06-16

### Added
- Scan-level import readiness now treats Wall Evidence V2 review-required wall
  assessments as downstream review issues via
  `placement.wall_evidence.requires_review`.
- Placement import readiness now maps
  `placement.review.wall_evidence_requires_review` into the same normalized
  wall-evidence issue code, keeping scan JSON, placement JSON, and benchmark
  gates aligned.

### Verified
- Focused scan quality/import readiness tests passed with `19` tests.
- Focused export tests passed with `22` tests.
- Full solution test suite passed with `494` tests.

## [0.02.059] - 2026-06-16

### Added
- Wall Evidence V2 review-required wall candidates are now first-class scan
  review queue items with ranked priority, source primitive IDs/layers, score
  breakdown values, bounds, evidence, and recommended actions.
- Placement export now emits `placement.review.wall_evidence_requires_review`
  issues for review-required wall evidence, giving downstream engines a direct
  stoplight before importing uncertain walls as coordinate-ready structural
  geometry.

### Verified
- Focused scan review queue tests passed with `6` tests.
- Focused export tests passed with `22` tests.
- Full solution test suite passed with `493` tests.

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
