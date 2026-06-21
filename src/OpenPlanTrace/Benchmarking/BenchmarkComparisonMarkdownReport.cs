using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenPlanTrace;

public static class BenchmarkComparisonMarkdownReport
{
    public static string Create(BenchmarkComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var builder = new StringBuilder();
        builder.AppendLine("# OpenPlanTrace Benchmark Comparison");
        builder.AppendLine();
        builder.AppendLine($"Generated: {comparison.GeneratedAt:O}");
        builder.AppendLine($"Baseline: {Text(comparison.BaselineName ?? "baseline")}");
        builder.AppendLine($"Candidate: {Text(comparison.CandidateName ?? "candidate")}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Status: {(comparison.Passed ? "PASS" : "REGRESSION")}");
        builder.AppendLine($"- Cases: {comparison.MatchedCaseCount} matched, {comparison.AddedCaseCount} added, {comparison.RemovedCaseCount} removed");
        builder.AppendLine($"- Signals: {comparison.RegressionCount} regressions, {comparison.ImprovementCount} improvements");
        builder.AppendLine();

        AppendSignals(builder, comparison);
        AppendWallPlacement(builder, comparison);
        AppendPipelineHealth(builder, comparison);
        AppendPipelinePlanIssues(builder, comparison);
        AppendArtifactPlans(builder, comparison);
        AppendRerunPlans(builder, comparison);
        AppendArtifactState(builder, comparison);
        AppendCases(builder, comparison);
        return builder.ToString();
    }

    private static void AppendSignals(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        builder.AppendLine("## Signals");
        builder.AppendLine();

        if (comparison.Signals.Count == 0)
        {
            builder.AppendLine("No regression or improvement signals.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Severity | Fixture | Code | Baseline | Candidate | Message |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var signal in comparison.Signals
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.FixtureId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(
                $"| {Cell(signal.Severity.ToString())} | {Cell(signal.FixtureId)} | `{Cell(signal.Code)}` | {Cell(signal.Baseline ?? "-")} | {Cell(signal.Candidate ?? "-")} | {Cell(signal.Message)} |");
        }

        builder.AppendLine();
    }

    private static void AppendWallPlacement(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var priorityNames = new HashSet<string>(
            [
                "wallPlacement.placementReadyWallCount",
                "wallPlacement.placementReviewWallCount",
                "wallPlacement.isolatedFragmentComponentCount",
                "wallPlacement.topologyImportBlockedRepairCandidateCount",
                "wallPlacement.highSeverityRepairCandidateCount",
                "wallPlacement.repairCandidateCount"
            ],
            StringComparer.Ordinal);
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("wallPlacement.", StringComparison.Ordinal)
                    && (priorityNames.Contains(delta.Name) || delta.Delta is not null and not 0))
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => priorityNames.Contains(item.Delta.Name))
            .ThenByDescending(item => Math.Abs(item.Delta.Delta ?? 0))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Wall Placement");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendPipelineHealth(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("pipelineHealth.", StringComparison.Ordinal)
                    && delta.Delta is not null and not 0)
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => Math.Abs(item.Delta.Delta!.Value))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Pipeline Health");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendPipelinePlanIssues(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("planIssue.", StringComparison.Ordinal)
                    && delta.Delta is not null and not 0)
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => Math.Abs(item.Delta.Delta!.Value))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Pipeline Plan Issues");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendRerunPlans(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("rerunPlan.", StringComparison.Ordinal)
                    && delta.Delta is not null and not 0)
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => Math.Abs(item.Delta.Delta!.Value))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Rerun Plans");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendArtifactPlans(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("artifactPlan.", StringComparison.Ordinal)
                    && delta.Delta is not null and not 0)
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => Math.Abs(item.Delta.Delta!.Value))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Artifact Plan Graph");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendArtifactState(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        var rows = comparison.Cases
            .SelectMany(item => item.CountDeltas
                .Where(delta => delta.Name.StartsWith("artifact.", StringComparison.Ordinal)
                    && delta.Name.EndsWith(".revision", StringComparison.Ordinal)
                    && delta.Delta is not null and not 0)
                .Select(delta => (Case: item, Delta: delta)))
            .OrderByDescending(item => Math.Abs(item.Delta.Delta!.Value))
            .ThenBy(item => item.Case.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Delta.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (rows.Length == 0)
        {
            return;
        }

        builder.AppendLine("## Artifact State");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Metric | Baseline | Candidate | Delta |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows)
        {
            builder.AppendLine(
                $"| {Cell(row.Case.FixtureId)} | `{Cell(row.Delta.Name)}` | {Cell(CountText(row.Delta.Baseline))} | {Cell(CountText(row.Delta.Candidate))} | {Cell(FormatDelta(row.Delta.Delta))} |");
        }

        builder.AppendLine();
    }

    private static void AppendCases(StringBuilder builder, BenchmarkComparisonResult comparison)
    {
        builder.AppendLine("## Cases");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Status | Pass | Quality | Confidence | Assertions | Duration | Key Count Deltas |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | --- | ---: | --- |");

        foreach (var item in comparison.Cases)
        {
            builder.AppendLine(
                $"| {Cell(item.FixtureId)} | {Cell(item.Status.ToString())} | {Cell(PassPair(item))} | {Cell(QualityPair(item))} | {Cell(ConfidencePair(item))} | {Cell(AssertionPair(item))} | {Cell(DurationPair(item))} | {Cell(KeyDeltas(item))} |");
        }

        builder.AppendLine();
    }

    private static string PassPair(BenchmarkCaseComparison item) =>
        $"{CasePassText(item.BaselinePassed, item.BaselineSkipped)} -> {CasePassText(item.CandidatePassed, item.CandidateSkipped)}";

    private static string QualityPair(BenchmarkCaseComparison item) =>
        $"{(item.BaselineSkipped ? "SKIP" : item.BaselineQualityGrade?.ToString() ?? "-")} -> {(item.CandidateSkipped ? "SKIP" : item.CandidateQualityGrade?.ToString() ?? "-")}";

    private static string ConfidencePair(BenchmarkCaseComparison item) =>
        $"{(item.BaselineSkipped ? "-" : FormatNullable(item.BaselineQualityConfidence))} -> {(item.CandidateSkipped ? "-" : FormatNullable(item.CandidateQualityConfidence))}";

    private static string AssertionPair(BenchmarkCaseComparison item) =>
        $"failed {item.BaselineFailedAssertionCount?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> {item.CandidateFailedAssertionCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    private static string DurationPair(BenchmarkCaseComparison item) =>
        $"{FormatMilliseconds(item.BaselineDurationMilliseconds)} -> {FormatMilliseconds(item.CandidateDurationMilliseconds)}";

    private static string KeyDeltas(BenchmarkCaseComparison item)
    {
        var keys = new[] { "walls", "rooms", "roomClusters", "openings", "annotations", "annotationReferences", "objects", "qualityIssues", "measurementChecked", "measurementOutliers", "scanReviewQueueItems" };
        var standard = item.CountDeltas
                .Where(delta => keys.Contains(delta.Name, StringComparer.Ordinal))
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");
        var wallPlacement = item.CountDeltas
            .Where(delta => delta.Name.StartsWith("wallPlacement.", StringComparison.OrdinalIgnoreCase) && delta.Delta is not null and not 0)
            .OrderByDescending(delta => Math.Abs(delta.Delta!.Value))
            .ThenBy(delta => delta.Name, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");
        var stageArtifacts = item.CountDeltas
            .Where(delta => delta.Name.StartsWith("stage.", StringComparison.OrdinalIgnoreCase) && delta.Delta is not null and not 0)
            .OrderByDescending(delta => Math.Abs(delta.Delta!.Value))
            .ThenBy(delta => delta.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");

        var finalArtifacts = item.CountDeltas
            .Where(delta => delta.Name.StartsWith("artifact.", StringComparison.OrdinalIgnoreCase)
                && !delta.Name.EndsWith(".revision", StringComparison.OrdinalIgnoreCase)
                && delta.Delta is not null and not 0)
            .OrderByDescending(delta => Math.Abs(delta.Delta!.Value))
            .ThenBy(delta => delta.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");

        var artifactPlans = item.CountDeltas
            .Where(delta => delta.Name.StartsWith("artifactPlan.", StringComparison.OrdinalIgnoreCase)
                && delta.Delta is not null and not 0)
            .OrderByDescending(delta => Math.Abs(delta.Delta!.Value))
            .ThenBy(delta => delta.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");

        var planIssues = item.CountDeltas
            .Where(delta => delta.Name.StartsWith("planIssue.", StringComparison.OrdinalIgnoreCase)
                && delta.Delta is not null and not 0)
            .OrderByDescending(delta => Math.Abs(delta.Delta!.Value))
            .ThenBy(delta => delta.Name, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}");

        return string.Join(", ", standard.Concat(wallPlacement).Concat(finalArtifacts).Concat(artifactPlans).Concat(planIssues).Concat(stageArtifacts));
    }

    private static string FormatDelta(int? delta) =>
        delta is null
            ? "-"
            : delta.Value >= 0
                ? $"+{delta.Value.ToString(CultureInfo.InvariantCulture)}"
                : delta.Value.ToString(CultureInfo.InvariantCulture);

    private static string CountText(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string FormatMilliseconds(double? value) =>
        value is null
            ? "-"
            : $"{Format(value.Value)} ms";

    private static string FormatNullable(double? value) =>
        value is null ? "-" : Format(value.Value);

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BoolText(bool? value) =>
        value is null ? "-" : value.Value ? "PASS" : "FAIL";

    private static string CasePassText(bool? value, bool skipped) =>
        skipped ? "SKIP" : BoolText(value);

    private static string Cell(string value) =>
        Text(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Text(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
