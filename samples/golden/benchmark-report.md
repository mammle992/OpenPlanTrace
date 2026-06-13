# OpenPlanTrace Benchmark Report

Generated: 2026-06-09T15:05:54.1676145+00:00
Suite: OpenPlanTrace golden smoke fixtures

## Summary

- Cases: 1 passed, 0 failed, 0 skipped / 1
- Assertions: 38 passed, 0 failed
- Total scan time: 783.663 ms

## Readiness Scoreboard

- Grade: Strong
- Overall score: 0.986
- Consumer readiness score: 0.993
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
| Warning | semantic-smoke-dxf | - | `scan_quality.requires_review` | 1 | Scan quality is Strong with confidence 0.881. |

### Next Actions

- Use the saved scan screenshots to classify quality issues as detector bugs, benchmark-truth gaps, or source-plan ambiguity.

## Cases

| Status | Fixture | Difficulty | Type | Quality | Confidence | Counts | Duration |
| --- | --- | --- | --- | --- | ---: | --- | ---: |
| PASS | semantic-smoke-dxf: Semantic smoke DXF | smoke | architectural | Strong | 0.881 | walls 6, rooms 1, clusters 1, openings 1, annotations 1, refs 0, objects 3, aggregates 0, routing 12, suppressed 0, measurement 1 checked/0 outliers | 783.663 ms |

## Failures

No failing benchmark assertions.

## Detector Metrics

| Fixture | Detector | Matched | Expected | Raw detected | Scored | Precision scored | Recall | Precision | Extra | Review-only |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |
| semantic-smoke-dxf | rooms | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |
| semantic-smoke-dxf | openings | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |
| semantic-smoke-dxf | object_groups | 1 | 1 | 1 | 1 | yes | 1 | 1 | 0 | 0 |

## Fixture Details

### semantic-smoke-dxf
- Source: `semantic-smoke.dxf`
- Status: PASS
- Assertions: 38 passed, 0 failed
- Quality: Strong, confidence 0.881, issues 1, review required True
- Calibration: reliable
- Measurement QA: checked 1, consistent 1, outliers 0, confidence 0.92, selected 1 mm/unit, median 1 mm/unit
- Geometry: regions 3, grid axes 1, walls 6, wall nodes 8, wall edges 6, rooms 1, room adjacencies 0, room clusters 1, openings 1
- Semantics: dimensions 1, annotations 1, annotation references 0, objects 3, object groups 1, object aggregates 0, routing items 12, routing suppressed objects 0
- Diagnostics: 11 total, 0 warnings, 0 errors
- Quality issue summary: quality.object_groups_require_review Info x1
- Diagnostic summary: annotations.detected Info x1, pages 1; dimensions.detected Info x1, pages 1; grid_axes.detected Info x1, pages 1; measurement_consistency.dimensions_consistent Info x1; object_groups.detected Info x1; rooms.clusters.detected Info x1, pages 1; rooms.use_semantics.detected Info x1, pages 1; rooms.wall_graph_cycles.detected Info x1, pages 1
- Slowest stages: calibration 92.542 ms (19 out, 0 diag); title-block-analysis 57.904 ms (16 out, 1 diag); sheet-regions 46.813 ms (12 out, 0 diag); rooms 42.807 ms (50 out, 2 diag); dimensions 32.015 ms (20 out, 1 diag)
- Properties: difficulty=smoke, planType=architectural, sourceFormat=dxf

