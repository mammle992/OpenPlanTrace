using System.Globalization;
using System.Text;

namespace OpenPlanTrace;

public static class BenchmarkMarkdownReport
{
    public static string Create(BenchmarkRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var builder = new StringBuilder();
        builder.AppendLine("# OpenPlanTrace Benchmark Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {run.GeneratedAt:O}");
        builder.AppendLine($"Suite: {Text(run.Name ?? "OpenPlanTrace")}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Cases: {run.PassedCaseCount} passed, {run.FailedCaseCount} failed, {run.SkippedCaseCount} skipped / {run.CaseCount}");
        builder.AppendLine($"- Assertions: {run.PassedAssertionCount} passed, {run.FailedAssertionCount} failed");
        builder.AppendLine($"- Total scan time: {FormatMilliseconds(run.Cases.Sum(item => item.DurationMilliseconds))}");
        builder.AppendLine();

        AppendScoreboard(builder, run);
        AppendCaseTable(builder, run);
        AppendFailureSummary(builder, run);
        AppendDetectorMetrics(builder, run);
        AppendReviewQueue(builder, run);
        AppendCaseDetails(builder, run);

        return builder.ToString();
    }

    private static void AppendScoreboard(StringBuilder builder, BenchmarkRunResult run)
    {
        var scoreboard = run.Scoreboard;
        if (scoreboard.SchemaVersion != BenchmarkScoreboard.CurrentSchemaVersion
            || scoreboard.CaseCount != run.CaseCount)
        {
            scoreboard = BenchmarkScoreboard.FromCases(run.Cases);
        }

        builder.AppendLine("## Readiness Scoreboard");
        builder.AppendLine();
        builder.AppendLine($"- Grade: {scoreboard.Grade}");
        builder.AppendLine($"- Overall score: {Format(scoreboard.OverallScore)}");
        builder.AppendLine($"- Consumer readiness score: {Format(scoreboard.ConsumerReadinessScore)}");
        builder.AppendLine($"- Ready for downstream use: {(scoreboard.ReadyForDownstreamUse ? "yes" : "no")}");
        builder.AppendLine($"- Truth targets: {scoreboard.MatchedTargetCount}/{scoreboard.ExpectedTargetCount} matched, {scoreboard.MissedTargetCount} missed, {scoreboard.ExtraDetectionCount} extra");
        builder.AppendLine($"- Failed assertions: {scoreboard.FailedAssertionCount}; failed scans: {scoreboard.FailedScanCount}; skipped cases: {scoreboard.SkippedCaseCount}");
        builder.AppendLine();

        if (scoreboard.Detectors.Count > 0)
        {
            builder.AppendLine("### Detector Grades");
            builder.AppendLine();
            builder.AppendLine("| Detector | Grade | F1 | Recall | Precision | Matched | Expected | Detected | Extra | Action |");
            builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (var detector in scoreboard.Detectors
                         .OrderBy(item => item.Grade)
                         .ThenBy(item => item.F1)
                         .ThenBy(item => item.Detector, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine(
                    $"| {Cell(detector.Detector)} | {Cell(detector.Grade.ToString())} | {Format(detector.F1)} | {Format(detector.Recall)} | {Format(detector.Precision)} | {detector.MatchedCount} | {detector.ExpectedCount} | {detector.DetectedCount} | {detector.ExtraCount} | {Cell(detector.RecommendedAction)} |");
            }

            builder.AppendLine();
        }

        if (scoreboard.FailureBuckets.Count > 0)
        {
            builder.AppendLine("### Failure Buckets");
            builder.AppendLine();
            builder.AppendLine("| Severity | Fixture | Detector | Code | Count | Message |");
            builder.AppendLine("| --- | --- | --- | --- | ---: | --- |");
            foreach (var bucket in scoreboard.FailureBuckets
                         .OrderByDescending(item => item.Severity)
                         .ThenBy(item => item.FixtureId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Detector ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .Take(18))
            {
                builder.AppendLine(
                    $"| {Cell(bucket.Severity.ToString())} | {Cell(bucket.FixtureId ?? "-")} | {Cell(bucket.Detector ?? "-")} | `{Cell(bucket.Code)}` | {bucket.Count} | {Cell(bucket.Message)} |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("### Next Actions");
        builder.AppendLine();
        foreach (var action in scoreboard.RecommendedNextActions)
        {
            builder.AppendLine($"- {Text(action)}");
        }

        builder.AppendLine();
    }

    private static void AppendCaseTable(StringBuilder builder, BenchmarkRunResult run)
    {
        builder.AppendLine("## Cases");
        builder.AppendLine();
        builder.AppendLine("| Status | Fixture | Difficulty | Type | Quality | Import | Confidence | Counts | Duration |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | ---: | --- | ---: |");

        foreach (var item in run.Cases)
        {
            var status = item.Skipped ? "SKIP" : item.Passed ? "PASS" : "FAIL";
            var name = string.IsNullOrWhiteSpace(item.FixtureName)
                ? item.FixtureId
                : $"{item.FixtureId}: {item.FixtureName}";
            var difficulty = Property(item, "difficulty");
            var type = Property(item, "planType", "type", "sourceFormat");
            var counts = item.Skipped
                ? "skipped"
                : $"surface patterns {item.Counts.SurfacePatterns}, walls {item.Counts.Walls}, rooms {item.Counts.Rooms}, clusters {item.Counts.RoomClusters}, openings {item.Counts.Openings}, annotations {item.Counts.Annotations}, refs {item.Counts.AnnotationReferences}, objects {item.Counts.Objects}, aggregates {item.Counts.ObjectAggregates}, routing {item.Counts.RoutingItems}, suppressed {item.Counts.RoutingSuppressedObjects}, measurement {FormatMeasurementCompact(item.Counts)}";
            var import = item.Skipped
                ? "-"
                : $"{item.ImportReadiness.Grade} G:{Ready(item.ImportReadiness.ReadyForGeometryImport)} M:{Ready(item.ImportReadiness.ReadyForMetricImport)} R:{Ready(item.ImportReadiness.ReadyForRoutingImport)}";
            builder.AppendLine(
                $"| {status} | {Cell(name)} | {Cell(difficulty)} | {Cell(type)} | {Cell(item.Skipped ? "-" : item.Counts.QualityGrade.ToString())} | {Cell(import)} | {Cell(item.Skipped ? "-" : item.Counts.QualityConfidence.ToString("0.###", CultureInfo.InvariantCulture))} | {Cell(counts)} | {Cell(FormatMilliseconds(item.DurationMilliseconds))} |");
        }

        builder.AppendLine();
    }

    private static void AppendFailureSummary(StringBuilder builder, BenchmarkRunResult run)
    {
        var failedCases = run.Cases.Where(item => !item.Passed && !item.Skipped).ToArray();
        if (failedCases.Length == 0)
        {
            builder.AppendLine("## Failures");
            builder.AppendLine();
            builder.AppendLine("No failing benchmark assertions.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("## Failures");
        builder.AppendLine();

        foreach (var item in failedCases)
        {
            builder.AppendLine($"### {Text(item.FixtureId)}");
            if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            {
                builder.AppendLine($"- Scan error: {Text(item.ErrorMessage)}");
            }

            foreach (var failure in item.Assertions.Where(assertion => !assertion.Passed))
            {
                builder.AppendLine($"- `{failure.Name}` expected {Text(failure.Expected)}, actual {Text(failure.Actual)}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendDetectorMetrics(StringBuilder builder, BenchmarkRunResult run)
    {
        var metrics = run.Cases.SelectMany(item => item.Metrics.Select(metric => (Case: item, Metric: metric))).ToArray();
        if (metrics.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Detector Metrics");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Detector | Matched | Expected | Raw detected | Scored | Precision scored | Recall | Precision | Extra | Review-only |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |");

        foreach (var item in metrics)
        {
            builder.AppendLine(
                $"| {Cell(item.Case.FixtureId)} | {Cell(item.Metric.Detector)} | {item.Metric.MatchedCount} | {item.Metric.ExpectedCount} | {item.Metric.DetectedCount} | {item.Metric.ScoredDetectionCount} | {Cell(item.Metric.PrecisionScoringEnabled ? "yes" : "no")} | {item.Metric.Recall.ToString("0.###", CultureInfo.InvariantCulture)} | {item.Metric.Precision.ToString("0.###", CultureInfo.InvariantCulture)} | {item.Metric.ExtraCount} | {item.Metric.ReviewOnlyDetectionCount} |");
        }

        builder.AppendLine();

        var extras = metrics
            .Where(item => item.Metric.ExtraDetections.Count > 0)
            .ToArray();
        if (extras.Length > 0)
        {
            builder.AppendLine("### Extra Detection Queue");
            builder.AppendLine();
            builder.AppendLine("| Fixture | Detector | Detection | Page | Bounds | Evidence |");
            builder.AppendLine("| --- | --- | --- | ---: | --- | --- |");
            foreach (var item in extras)
            {
                foreach (var extra in item.Metric.ExtraDetections.Take(12))
                {
                    builder.AppendLine(
                        $"| {Cell(item.Case.FixtureId)} | {Cell(item.Metric.Detector)} | `{Cell(extra.DetectionId)}` | {Cell(extra.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "-")} | {Cell(FormatBounds(extra.Bounds))} | {Cell(extra.Evidence)} |");
                }
            }

            builder.AppendLine();
        }

        var reviewOnly = metrics
            .Where(item => item.Metric.ReviewOnlyDetections.Count > 0)
            .ToArray();
        if (reviewOnly.Length == 0)
        {
            return;
        }

        builder.AppendLine("### Review-Only Detection Queue");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Detector | Detection | Page | Bounds | Evidence |");
        builder.AppendLine("| --- | --- | --- | ---: | --- | --- |");
        foreach (var item in reviewOnly)
        {
            foreach (var detection in item.Metric.ReviewOnlyDetections.Take(12))
            {
                builder.AppendLine(
                    $"| {Cell(item.Case.FixtureId)} | {Cell(item.Metric.Detector)} | `{Cell(detection.DetectionId)}` | {Cell(detection.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "-")} | {Cell(FormatBounds(detection.Bounds))} | {Cell(detection.Evidence)} |");
            }
        }

        builder.AppendLine();
    }

    private static void AppendReviewQueue(StringBuilder builder, BenchmarkRunResult run)
    {
        if (run.ReviewQueue.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Review Queue");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Detector | Kind | Detection | Page | Bounds | Action | Evidence |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | --- | --- | --- |");
        foreach (var item in run.ReviewQueue.Take(24))
        {
            builder.AppendLine(
                $"| {Cell(item.FixtureId)} | {Cell(item.Detector)} | {Cell(item.Kind.ToString())} | `{Cell(item.Detection.DetectionId)}` | {Cell(item.Detection.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "-")} | {Cell(FormatBounds(item.Detection.Bounds))} | {Cell(item.RecommendedAction)} | {Cell(item.Detection.Evidence)} |");
        }

        if (run.ReviewQueue.Count > 24)
        {
            builder.AppendLine();
            builder.AppendLine($"Showing first 24 of {run.ReviewQueue.Count} review item(s).");
        }

        builder.AppendLine();
    }

    private static void AppendCaseDetails(StringBuilder builder, BenchmarkRunResult run)
    {
        builder.AppendLine("## Fixture Details");
        builder.AppendLine();

        foreach (var item in run.Cases)
        {
            builder.AppendLine($"### {Text(item.FixtureId)}");
            builder.AppendLine($"- Source: `{Text(item.SourcePath)}`");
            builder.AppendLine($"- Status: {(item.Skipped ? "SKIP" : item.Passed ? "PASS" : "FAIL")}");
            if (item.Skipped)
            {
                builder.AppendLine($"- Skip reason: {Text(item.SkipReason ?? "Fixture skipped.")}");
                if (item.Properties.Count > 0)
                {
                    builder.AppendLine($"- Properties: {Text(string.Join(", ", item.Properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")))}");
                }

                builder.AppendLine();
                continue;
            }

            builder.AppendLine($"- Assertions: {item.PassedAssertionCount} passed, {item.FailedAssertionCount} failed");
            builder.AppendLine($"- Quality: {item.Counts.QualityGrade}, confidence {item.Counts.QualityConfidence.ToString("0.###", CultureInfo.InvariantCulture)}, issues {item.Counts.QualityIssues}, review required {item.Counts.QualityRequiresReview}");
            builder.AppendLine($"- Import readiness: {item.ImportReadiness.Grade}, score {Format(item.ImportReadiness.Score)}, geometry {Ready(item.ImportReadiness.ReadyForGeometryImport)}, metric {Ready(item.ImportReadiness.ReadyForMetricImport)}, routing {Ready(item.ImportReadiness.ReadyForRoutingImport)}, review required {item.ImportReadiness.RequiresReview}");
            builder.AppendLine($"- Calibration: {(item.Counts.HasReliableCalibration ? "reliable" : "missing")}");
            builder.AppendLine($"- Measurement QA: {FormatMeasurementDetails(item.Counts)}");
            builder.AppendLine($"- Scan review queue: {FormatScanReviewQueue(item.ScanReviewQueue)}");
            builder.AppendLine($"- Geometry: regions {item.Counts.Regions}, grid axes {item.Counts.GridAxes}, grid bay spacings {item.Counts.GridBaySpacings}, surface patterns {item.Counts.SurfacePatterns}, walls {item.Counts.Walls}, wall nodes {item.Counts.WallNodes}, wall edges {item.Counts.WallEdges}, rooms {item.Counts.Rooms}, room adjacencies {item.Counts.RoomAdjacencies}, room clusters {item.Counts.RoomClusters}, openings {item.Counts.Openings}");
            builder.AppendLine($"- Semantics: dimensions {item.Counts.Dimensions}, annotations {item.Counts.Annotations}, annotation references {item.Counts.AnnotationReferences}, objects {item.Counts.Objects}, object groups {item.Counts.ObjectGroups}, object aggregates {item.Counts.ObjectAggregates}, routing items {item.Counts.RoutingItems}, routing suppressed objects {item.Counts.RoutingSuppressedObjects}");
            builder.AppendLine($"- Diagnostics: {item.Counts.Diagnostics} total, {item.Counts.DiagnosticWarnings} warnings, {item.Counts.DiagnosticErrors} errors");
            if (item.QualityIssues.Count > 0)
            {
                builder.AppendLine($"- Quality issue summary: {Text(string.Join("; ", item.QualityIssues.Take(5).Select(FormatIssueSummary)))}");
            }

            if (item.DiagnosticIssues.Count > 0)
            {
                builder.AppendLine($"- Diagnostic summary: {Text(string.Join("; ", item.DiagnosticIssues.Take(8).Select(FormatIssueSummary)))}");
            }

            if (item.Stages.Count > 0)
            {
                builder.AppendLine($"- Slowest stages: {Text(string.Join("; ", item.Stages.OrderByDescending(stage => stage.DurationMilliseconds).Take(5).Select(FormatStageSummary)))}");
            }

            if (item.Properties.Count > 0)
            {
                builder.AppendLine($"- Properties: {Text(string.Join(", ", item.Properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}")))}");
            }

            builder.AppendLine();
        }
    }

    private static string Property(BenchmarkCaseResult item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (item.Properties.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "-";
    }

    private static string FormatMilliseconds(double value) =>
        $"{value.ToString("0.###", CultureInfo.InvariantCulture)} ms";

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Ready(bool value) => value ? "yes" : "no";

    private static string FormatMeasurementCompact(BenchmarkCounts counts)
    {
        if (counts.MeasurementCheckedCount == 0)
        {
            return "none";
        }

        return $"{counts.MeasurementCheckedCount} checked/{counts.MeasurementOutlierCount} outliers";
    }

    private static string FormatMeasurementDetails(BenchmarkCounts counts)
    {
        if (counts.MeasurementCheckedCount == 0)
        {
            return "no matched dimension checks";
        }

        var values = new List<string>
        {
            $"checked {counts.MeasurementCheckedCount}",
            $"consistent {counts.MeasurementConsistentCount}",
            $"outliers {counts.MeasurementOutlierCount}",
            $"confidence {counts.MeasurementConsistencyConfidence.ToString("0.###", CultureInfo.InvariantCulture)}"
        };

        if (counts.MeasurementSelectedMillimetersPerDrawingUnit is { } selected)
        {
            values.Add($"selected {selected.ToString("0.###", CultureInfo.InvariantCulture)} mm/unit");
        }

        if (counts.MeasurementMedianMillimetersPerDrawingUnit is { } median)
        {
            values.Add($"median {median.ToString("0.###", CultureInfo.InvariantCulture)} mm/unit");
        }

        if (counts.MeasurementScaleSpreadRatio is { } spread)
        {
            values.Add($"spread {spread.ToString("0.###", CultureInfo.InvariantCulture)}x");
        }

        return string.Join(", ", values);
    }

    private static string FormatScanReviewQueue(ScanReviewQueueSummary summary)
    {
        if (summary.Count == 0)
        {
            return "0 items";
        }

        var kinds = string.Join(
            ", ",
            summary.KindCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(pair => $"{pair.Key} {pair.Value}"));

        return $"{summary.Count} items ({kinds})";
    }

    private static string FormatBounds(PlanRect? bounds) =>
        bounds is null
            ? "-"
            : $"{bounds.Value.X.ToString("0.###", CultureInfo.InvariantCulture)},{bounds.Value.Y.ToString("0.###", CultureInfo.InvariantCulture)} {bounds.Value.Width.ToString("0.###", CultureInfo.InvariantCulture)}x{bounds.Value.Height.ToString("0.###", CultureInfo.InvariantCulture)}";

    private static string FormatIssueSummary(BenchmarkCaseIssueSummary issue)
    {
        var pages = issue.PageNumbers.Count == 0
            ? string.Empty
            : $", pages {string.Join("/", issue.PageNumbers)}";
        return $"{issue.Code} {issue.Severity} x{issue.Count}{pages}";
    }

    private static string FormatStageSummary(BenchmarkStageSummary stage) =>
        $"{stage.Stage} {FormatMilliseconds(stage.DurationMilliseconds)} ({stage.OutputCount} out, {stage.DiagnosticCount} diag)";

    private static string Cell(string value) =>
        Text(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Text(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
