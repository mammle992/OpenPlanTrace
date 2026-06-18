# OpenPlanTrace Benchmark Report

Generated: 2026-06-14T18:38:29.5244683+00:00
Suite: OpenPlanTrace golden smoke fixtures

## Summary

- Cases: 1 passed, 0 failed, 0 skipped / 1
- Assertions: 117 passed, 0 failed
- Total scan time: 728.892 ms

## Readiness Scoreboard

- Grade: Strong
- Overall score: 0.976
- Consumer readiness score: 0.965
- Pipeline health score: 0.79
- Ready for downstream use: yes
- Truth targets: 3/3 matched, 0 missed, 0 extra
- Failed assertions: 0; failed scans: 0; skipped cases: 0

### Detector Grades

| Detector | Grade | F1 | Recall | Precision | Matched | Expected | Detected | Extra | Action |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| object_groups | Strong | 1 | 1 | 1 | 1 | 1 | 1 | 0 | Keep object_groups covered with additional real-plan truth targets. |
| openings | Strong | 1 | 1 | 1 | 1 | 1 | 1 | 0 | Keep openings covered with additional real-plan truth targets. |
| rooms | Strong | 1 | 1 | 1 | 1 | 1 | 1 | 0 | Keep rooms covered with additional real-plan truth targets. |

### Failure Buckets

| Severity | Fixture | Detector | Code | Count | Message |
| --- | --- | --- | --- | ---: | --- |
| Warning | semantic-smoke-dxf | - | `pipeline_health.empty_required_runtime_reads` | 4 | Pipeline stages had required runtime inputs with no data for this source. |
| Info | semantic-smoke-dxf | - | `pipeline_health.empty_optional_runtime_reads` | 1 | Pipeline stages had optional runtime inputs with no data for this source. |
| Info | semantic-smoke-dxf | - | `pipeline_health.empty_declared_outputs` | 13 | Pipeline stages declared outputs that stayed empty; verify whether this is valid for the source type. |

### Next Actions

- Inspect pipeline health evidence for empty reads/outputs; make source-specific branches optional or feed the missing artifacts intentionally.

## Cases

| Status | Fixture | Difficulty | Type | Quality | Import | Confidence | Counts | Duration |
| --- | --- | --- | --- | --- | --- | ---: | --- | ---: |
| PASS | semantic-smoke-dxf: Semantic smoke DXF | smoke | architectural | Strong | Strong G:yes M:yes R:yes | 0.881 | surface patterns 0, walls 6, rooms 1, clusters 1, openings 1, annotations 1, refs 0, objects 3, aggregates 0, routing 10, suppressed 0, measurement 1 checked/0 outliers | 728.892 ms |

## Failures

No failing benchmark assertions.

## Detector Metrics

| Fixture | Detector | Matched | Expected | Raw detected | Scored | Precision scored | Recall | Precision | Extra | Review-only |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |
| semantic-smoke-dxf | rooms | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |
| semantic-smoke-dxf | openings | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |
| semantic-smoke-dxf | object_groups | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |

## Pipeline Health

| Fixture | Dependency | Runtime reads | Contract | Plan issues | Empty reads | Undeclared | Empty outputs | Review stages |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| semantic-smoke-dxf | yes 24/24 | req no, opt no | yes | 0 | req 4, opt 1 | 0 | 13 | dimension-chains, grid-bays, layer-analysis, layer-consistency, object-aggregates, pdf-image-analysis, raster-extraction, routing-layer |

## Artifact Plan Graph

| Fixture | Artifact | Role | Producers | Consumers | Producer waves | Consumer waves | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| semantic-smoke-dxf | Diagnostics | SourceTerminal | layer-analysis, raster-extraction, pdf-image-analysis, sheet-regions, title-block-analysis (+19 more) | none | 1-11 | - | artifact is available from source ingestion producer stages: layer-analysis, raster-extraction, pdf-image-analysis, sheet-regions, title-block-analysis, calibration, dimensions, annotations, grid-axes, grid-bays, measurement-consistency, dimension-chains, walls, wall-graph, openings, rooms, room-adjacency, measurement-scale-provenance, object-candidates, object-groups, object-aggregates, routing-layer, visual-ai, layer-consistency |
| semantic-smoke-dxf | DimensionChains | ProducedTerminal | dimension-chains | none | 5 | - | artifact depends on scanner stage output producer stages: dimension-chains |
| semantic-smoke-dxf | GridBays | ProducedTerminal | grid-bays | none | 5 | - | artifact depends on scanner stage output producer stages: grid-bays |
| semantic-smoke-dxf | LayerConsistency | ProducedTerminal | layer-consistency | none | 11 | - | artifact depends on scanner stage output producer stages: layer-consistency |
| semantic-smoke-dxf | MeasurementConsistency | ProducedTerminal | measurement-consistency | none | 5 | - | artifact depends on scanner stage output producer stages: measurement-consistency |
| semantic-smoke-dxf | RoomAdjacency | ProducedTerminal | room-adjacency | none | 8 | - | artifact depends on scanner stage output producer stages: room-adjacency |
| semantic-smoke-dxf | RoutingBarriers | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | RoutingIgnoredObjects | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | RoutingObstacles | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | RoutingPassages | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | RoutingRoomUseHints | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | RoutingSuppressedObjects | ProducedTerminal | routing-layer | none | 11 | - | artifact depends on scanner stage output producer stages: routing-layer |
| semantic-smoke-dxf | SurfacePatterns | ProducedTerminal | walls | none | 4 | - | artifact depends on scanner stage output producer stages: walls |
| semantic-smoke-dxf | TopologySpans | ProducedTerminal | wall-graph | none | 5 | - | artifact depends on scanner stage output producer stages: wall-graph |
| semantic-smoke-dxf | VisualAiClassifications | ProducedTerminal | visual-ai | none | 10 | - | artifact depends on scanner stage output producer stages: visual-ai |
| semantic-smoke-dxf | Annotations | ProducedAndConsumed | annotations | openings, rooms, object-candidates | 3 | 6-8 | artifact depends on scanner stage output producer stages: annotations |
| semantic-smoke-dxf | Calibration | ProducedAndConsumed | calibration | dimensions, grid-bays, measurement-consistency, dimension-chains, walls (+4 more) | 3 | 4-11 | artifact depends on scanner stage output producer stages: calibration |
| semantic-smoke-dxf | Dimensions | ProducedAndConsumed | dimensions | grid-bays, measurement-consistency, dimension-chains, walls, measurement-scale-provenance | 4 | 5 | artifact depends on scanner stage output producer stages: dimensions |
| semantic-smoke-dxf | Document | SourceInput | none | layer-analysis, raster-extraction, pdf-image-analysis, calibration | - | 1-3 | artifact is available from source ingestion producer stages: none |
| semantic-smoke-dxf | GridAxes | ProducedAndConsumed | grid-axes | grid-bays, dimension-chains, walls | 3 | 5-4 | artifact depends on scanner stage output producer stages: grid-axes |
| semantic-smoke-dxf | Layers | ProducedAndConsumed | layer-analysis | sheet-regions, grid-axes, walls, layer-consistency | 1 | 2-11 | artifact depends on scanner stage output producer stages: layer-analysis |
| semantic-smoke-dxf | ObjectAggregates | ProducedAndConsumed | object-aggregates | routing-layer, layer-consistency | 10 | 11 | artifact depends on scanner stage output producer stages: object-aggregates |
| semantic-smoke-dxf | ObjectCandidates | ProducedAndConsumed | object-candidates | object-groups, object-aggregates, routing-layer, visual-ai, layer-consistency | 8 | 9-11 | artifact depends on scanner stage output producer stages: object-candidates |
| semantic-smoke-dxf | ObjectGroups | ProducedAndConsumed | object-groups | object-aggregates, visual-ai | 9 | 10 | artifact depends on scanner stage output producer stages: object-groups |
| semantic-smoke-dxf | Openings | ProducedAndConsumed | openings | rooms, room-adjacency, routing-layer | 6 | 7-11 | artifact depends on scanner stage output producer stages: openings |
| semantic-smoke-dxf | Pages | SourceInput | none | layer-analysis, raster-extraction, pdf-image-analysis | - | 1 | artifact is available from source ingestion producer stages: none |
| semantic-smoke-dxf | PdfImages | SourceInput | pdf-image-analysis | pdf-image-analysis | 1 | 1 | artifact is available from source ingestion producer stages: pdf-image-analysis |
| semantic-smoke-dxf | Primitives | SourceInput | none | layer-analysis, sheet-regions, title-block-analysis, calibration, dimensions (+5 more) | - | 1-8 | artifact is available from source ingestion producer stages: none |
| semantic-smoke-dxf | RasterImages | SourceInput | raster-extraction | raster-extraction, visual-ai | 1 | 1-10 | artifact is available from source ingestion producer stages: raster-extraction |
| semantic-smoke-dxf | Rooms | ProducedAndConsumed | rooms | room-adjacency, object-candidates, object-aggregates, routing-layer | 7 | 8-11 | artifact depends on scanner stage output producer stages: rooms |
| semantic-smoke-dxf | SheetRegions | ProducedAndConsumed | sheet-regions | title-block-analysis, calibration, dimensions, annotations, grid-axes (+3 more) | 2 | 3-7 | artifact depends on scanner stage output producer stages: sheet-regions |
| semantic-smoke-dxf | TitleBlocks | ProducedAndConsumed | title-block-analysis | calibration, measurement-scale-provenance | 3 | 3-5 | artifact depends on scanner stage output producer stages: title-block-analysis |
| semantic-smoke-dxf | WallGraph | ProducedAndConsumed | wall-graph | openings, rooms, room-adjacency, object-candidates, routing-layer | 5 | 6-11 | artifact depends on scanner stage output producer stages: wall-graph |
| semantic-smoke-dxf | Walls | ProducedAndConsumed | walls | wall-graph, object-candidates, routing-layer, layer-consistency | 4 | 5-11 | artifact depends on scanner stage output producer stages: walls |

## Execution Waves

| Fixture | Wave | Mode | Readiness | Stages | Writes | Downstream | Reasons |
| --- | ---: | --- | --- | --- | --- | --- | --- |
| semantic-smoke-dxf | 1 | Parallel | Ready | layer-analysis, raster-extraction, pdf-image-analysis | Diagnostics, Layers, PdfImages, RasterImages | sheet-regions, grid-axes, walls, visual-ai, layer-consistency | Wave has multiple stages, no write conflicts, and no intra-wave dependencies. Wave output feeds 5 later stage(s): sheet-regions, grid-axes, walls, visual-ai, layer-consistency. |
| semantic-smoke-dxf | 2 | Sequential | SingleStage | sheet-regions | Diagnostics, SheetRegions | title-block-analysis, calibration, dimensions, annotations, grid-axes, walls | Wave contains a single stage. Wave output feeds 8 later stage(s): title-block-analysis, calibration, dimensions, annotations, grid-axes, walls, openings, rooms. |
| semantic-smoke-dxf | 3 | Sequential | IntraWaveDependency | title-block-analysis, calibration, annotations, grid-axes | Annotations, Calibration, Diagnostics, GridAxes, TitleBlocks | dimensions, grid-bays, measurement-consistency, dimension-chains, walls, openings | Wave contains stages that read artifacts written by another stage in the same wave. Wave output feeds 10 later stage(s): dimensions, grid-bays, measurement-consistency, dimension-chains, walls, openings, rooms, measurement-scale-provenance. |
| semantic-smoke-dxf | 4 | Sequential | IntraWaveDependency | dimensions, walls | Diagnostics, Dimensions, SurfacePatterns, Walls | grid-bays, measurement-consistency, dimension-chains, wall-graph, measurement-scale-provenance, object-candidates | Wave contains stages that read artifacts written by another stage in the same wave. Wave output feeds 8 later stage(s): grid-bays, measurement-consistency, dimension-chains, wall-graph, measurement-scale-provenance, object-candidates, routing-layer, layer-consistency. |
| semantic-smoke-dxf | 5 | Parallel | Ready | grid-bays, measurement-consistency, dimension-chains, wall-graph, measurement-scale-provenance | Diagnostics, DimensionChains, GridBays, MeasurementConsistency, TopologySpans, WallGraph | openings, rooms, room-adjacency, object-candidates, routing-layer | Wave has multiple stages, no write conflicts, and no intra-wave dependencies. Wave output feeds 5 later stage(s): openings, rooms, room-adjacency, object-candidates, routing-layer. |
| semantic-smoke-dxf | 6 | Sequential | SingleStage | openings | Diagnostics, Openings | rooms, room-adjacency, routing-layer | Wave contains a single stage. Wave output feeds 3 later stage(s): rooms, room-adjacency, routing-layer. |
| semantic-smoke-dxf | 7 | Sequential | SingleStage | rooms | Diagnostics, Rooms | room-adjacency, object-candidates, object-aggregates, routing-layer | Wave contains a single stage. Wave output feeds 4 later stage(s): room-adjacency, object-candidates, object-aggregates, routing-layer. |
| semantic-smoke-dxf | 8 | Parallel | Ready | room-adjacency, object-candidates | Diagnostics, ObjectCandidates, RoomAdjacency | object-groups, object-aggregates, routing-layer, visual-ai, layer-consistency | Wave has multiple stages, no write conflicts, and no intra-wave dependencies. Wave output feeds 5 later stage(s): object-groups, object-aggregates, routing-layer, visual-ai, layer-consistency. |
| semantic-smoke-dxf | 9 | Sequential | SingleStage | object-groups | Diagnostics, ObjectGroups | object-aggregates, visual-ai | Wave contains a single stage. Wave output feeds 2 later stage(s): object-aggregates, visual-ai. |
| semantic-smoke-dxf | 10 | Parallel | Ready | object-aggregates, visual-ai | Diagnostics, ObjectAggregates, VisualAiClassifications | routing-layer, layer-consistency | Wave has multiple stages, no write conflicts, and no intra-wave dependencies. Wave output feeds 2 later stage(s): routing-layer, layer-consistency. |
| semantic-smoke-dxf | 11 | Parallel | Ready | routing-layer, layer-consistency | Diagnostics, LayerConsistency, RoutingBarriers, RoutingIgnoredObjects, RoutingObstacles, RoutingPassages | none | Wave has multiple stages, no write conflicts, and no intra-wave dependencies. |

## Rerun Impact

| Fixture | Artifact | Scope | Direct consumers | Affected stages | Affected artifacts | First wave | Evidence |
| --- | --- | --- | --- | ---: | --- | ---: | --- |
| semantic-smoke-dxf | Document | SourceArtifact | layer-analysis, raster-extraction, pdf-image-analysis, calibration | 24 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | 1 | artifact is provided by source ingestion producer stages: none |
| semantic-smoke-dxf | Pages | SourceArtifact | layer-analysis, raster-extraction, pdf-image-analysis | 24 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | 1 | artifact is provided by source ingestion producer stages: none |
| semantic-smoke-dxf | Primitives | SourceArtifact | layer-analysis, sheet-regions, title-block-analysis, calibration, dimensions, annotations | 22 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | 1 | artifact is provided by source ingestion producer stages: none |
| semantic-smoke-dxf | Layers | DerivedArtifact | sheet-regions, grid-axes, walls, layer-consistency | 21 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | 2 | artifact is produced by scanner stages producer stages: layer-analysis |
| semantic-smoke-dxf | SheetRegions | DerivedArtifact | title-block-analysis, calibration, dimensions, annotations, grid-axes, walls | 20 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | 3 | artifact is produced by scanner stages producer stages: sheet-regions |
| semantic-smoke-dxf | TitleBlocks | DerivedArtifact | calibration, measurement-scale-provenance | 17 | Calibration, DimensionChains, Dimensions, GridBays, LayerConsistency, MeasurementConsistency | 3 | artifact is produced by scanner stages producer stages: title-block-analysis |
| semantic-smoke-dxf | Calibration | DerivedArtifact | dimensions, grid-bays, measurement-consistency, dimension-chains, walls, openings | 16 | DimensionChains, Dimensions, GridBays, LayerConsistency, MeasurementConsistency, ObjectAggregates | 4 | artifact is produced by scanner stages producer stages: calibration |
| semantic-smoke-dxf | Dimensions | DerivedArtifact | grid-bays, measurement-consistency, dimension-chains, walls, measurement-scale-provenance | 15 | DimensionChains, GridBays, LayerConsistency, MeasurementConsistency, ObjectAggregates, ObjectCandidates | 4 | artifact is produced by scanner stages producer stages: dimensions |
| semantic-smoke-dxf | GridAxes | DerivedArtifact | grid-bays, dimension-chains, walls | 13 | DimensionChains, GridBays, LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups | 4 | artifact is produced by scanner stages producer stages: grid-axes |
| semantic-smoke-dxf | Walls | DerivedArtifact | wall-graph, object-candidates, routing-layer, layer-consistency | 10 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, Openings, RoomAdjacency | 5 | artifact is produced by scanner stages producer stages: walls |
| semantic-smoke-dxf | Annotations | DerivedArtifact | openings, rooms, object-candidates | 9 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, Openings, RoomAdjacency | 6 | artifact is produced by scanner stages producer stages: annotations |
| semantic-smoke-dxf | WallGraph | DerivedArtifact | openings, rooms, room-adjacency, object-candidates, routing-layer | 9 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, Openings, RoomAdjacency | 6 | artifact is produced by scanner stages producer stages: wall-graph |
| semantic-smoke-dxf | Openings | DerivedArtifact | rooms, room-adjacency, routing-layer | 8 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, RoomAdjacency, Rooms | 7 | artifact is produced by scanner stages producer stages: openings |
| semantic-smoke-dxf | Rooms | DerivedArtifact | room-adjacency, object-candidates, object-aggregates, routing-layer | 7 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, RoomAdjacency, RoutingBarriers | 8 | artifact is produced by scanner stages producer stages: rooms |
| semantic-smoke-dxf | ObjectCandidates | DerivedArtifact | object-groups, object-aggregates, routing-layer, visual-ai, layer-consistency | 5 | LayerConsistency, ObjectAggregates, ObjectGroups, RoutingBarriers, RoutingIgnoredObjects, RoutingObstacles | 9 | artifact is produced by scanner stages producer stages: object-candidates |
| semantic-smoke-dxf | ObjectGroups | DerivedArtifact | object-aggregates, visual-ai | 4 | LayerConsistency, ObjectAggregates, RoutingBarriers, RoutingIgnoredObjects, RoutingObstacles, RoutingPassages | 10 | artifact is produced by scanner stages producer stages: object-groups |
| semantic-smoke-dxf | ObjectAggregates | DerivedArtifact | routing-layer, layer-consistency | 2 | LayerConsistency, RoutingBarriers, RoutingIgnoredObjects, RoutingObstacles, RoutingPassages, RoutingRoomUseHints | 11 | artifact is produced by scanner stages producer stages: object-aggregates |
| semantic-smoke-dxf | RasterImages | SourceArtifact | raster-extraction, visual-ai | 2 | RasterImages, VisualAiClassifications | 1 | artifact is provided by source ingestion producer stages: raster-extraction |
| semantic-smoke-dxf | PdfImages | SourceArtifact | pdf-image-analysis | 1 | PdfImages | 1 | artifact is provided by source ingestion producer stages: pdf-image-analysis |

## Rerun Plans

| Fixture | Plan | Changed artifacts | Mode | Rerun stages | Waves | Affected artifacts | Evidence |
| --- | --- | --- | --- | ---: | --- | --- | --- |
| semantic-smoke-dxf | source-primitives | Primitives | WaveOrderedWithParallelCandidates | 22 | 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 | Annotations, Calibration, DimensionChains, Dimensions, GridAxes, GridBays | changed artifacts: Primitives changed source artifacts: Primitives |
| semantic-smoke-dxf | calibration | Calibration | WaveOrderedWithParallelCandidates | 16 | 4, 5, 6, 7, 8, 9, 10, 11 | DimensionChains, Dimensions, GridBays, LayerConsistency, MeasurementConsistency, ObjectAggregates | changed artifacts: Calibration changed source artifacts: none |
| semantic-smoke-dxf | wall-geometry | Walls | WaveOrderedWithParallelCandidates | 10 | 5, 6, 7, 8, 9, 10, 11 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, Openings, RoomAdjacency | changed artifacts: Walls changed source artifacts: none |
| semantic-smoke-dxf | wall-topology | TopologySpans, WallGraph | WaveOrderedWithParallelCandidates | 9 | 6, 7, 8, 9, 10, 11 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, Openings, RoomAdjacency | changed artifacts: TopologySpans, WallGraph changed source artifacts: none |
| semantic-smoke-dxf | openings | Openings | WaveOrderedWithParallelCandidates | 8 | 7, 8, 9, 10, 11 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, RoomAdjacency, Rooms | changed artifacts: Openings changed source artifacts: none |
| semantic-smoke-dxf | rooms | RoomAdjacency, Rooms | WaveOrderedWithParallelCandidates | 7 | 8, 9, 10, 11 | LayerConsistency, ObjectAggregates, ObjectCandidates, ObjectGroups, RoomAdjacency, RoutingBarriers | changed artifacts: RoomAdjacency, Rooms changed source artifacts: none |
| semantic-smoke-dxf | objects | ObjectAggregates, ObjectCandidates, ObjectGroups | WaveOrderedWithParallelCandidates | 5 | 9, 10, 11 | LayerConsistency, ObjectAggregates, ObjectGroups, RoutingBarriers, RoutingIgnoredObjects, RoutingObstacles | changed artifacts: ObjectAggregates, ObjectCandidates, ObjectGroups changed source artifacts: none |

## Fixture Details

### semantic-smoke-dxf
- Source: `samples/golden/semantic-smoke.dxf`
- Status: PASS
- Assertions: 117 passed, 0 failed
- Quality: Strong, confidence 0.881, issues 1, review required False
- Import readiness: Strong, score 0.97, geometry yes, metric yes, routing yes, review required False
- Pipeline health: dependency yes, runtime required reads no, contract yes, empty declared outputs 13
- Calibration: reliable
- Measurement QA: checked 1, consistent 1, outliers 0, confidence 0.92, selected 1 mm/unit, median 1 mm/unit
- Scan review queue: 1 items (ObjectGroupReview 1)
- Geometry: regions 3, grid axes 1, grid bay spacings 0, surface patterns 0, walls 6, wall nodes 8, wall edges 6, rooms 1, room adjacencies 0, room clusters 1, openings 1
- Semantics: dimensions 1, annotations 1, annotation references 0, objects 3, object groups 1, object aggregates 0, routing items 10, routing suppressed objects 0
- Final artifact inventory: Primitives 24, WallGraph 17, Diagnostics 12, Layers 9, Calibration 6, RoutingBarriers 6, TopologySpans 6, Walls 6, Annotations 3, ObjectCandidates 3 (+15 more)
- Diagnostics: 12 total, 0 warnings, 0 errors
- Quality issue summary: quality.object_groups_require_review Info x1
- Diagnostic summary: annotations.detected Info x1, pages 1; dimensions.detected Info x1, pages 1; grid_axes.detected Info x1, pages 1; measurement_consistency.dimensions_consistent Info x1; object_groups.detected Info x1; rooms.clusters.detected Info x1, pages 1; rooms.use_semantics.detected Info x1, pages 1; rooms.wall_graph_cycles.detected Info x1, pages 1
- Slowest stages: calibration 84.949 ms (Measurement, L3/pref L4, ready, 19 out, 0 diag, changes Calibration +6); wall-graph 46.74 ms (Topology, L5/pref L7, ready, 48 out, 1 diag, changes WallGraph +17/TopologySpans +6/Diagnostics +1); title-block-analysis 44.204 ms (Layout, L3/pref L3, ready, 16 out, 1 diag, changes Diagnostics +1/TitleBlocks +1); sheet-regions 43.921 ms (Layout, L2/pref L2, ready, 12 out, 0 diag, changes SheetRegions +3, telemetry empty declared outputs Diagnostics); rooms 40.571 ms (Topology, L7/pref L9, ready, 50 out, 2 diag, changes Diagnostics +2/Rooms +1)
- Properties: difficulty=smoke, planType=architectural, sourceFormat=dxf

