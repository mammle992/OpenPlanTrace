using System.Globalization;
using System.Text;

namespace OpenPlanTrace;

public static class BenchmarkManifestDraftMarkdownReport
{
    private const double LowConfidenceThreshold = 0.5;
    private const int DetailedTargetLimitPerDetector = 12;

    public static string Create(BenchmarkManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var builder = new StringBuilder();
        builder.AppendLine("# Benchmark Draft Review");
        builder.AppendLine();
        builder.AppendLine($"- Schema: `{Clean(manifest.SchemaVersion) ?? BenchmarkManifest.CurrentSchemaVersion}`");
        builder.AppendLine($"- Manifest: {MarkdownValue(manifest.Name)}");
        builder.AppendLine($"- Fixtures: {(manifest.Fixtures ?? Array.Empty<BenchmarkFixture>()).Count}");
        builder.AppendLine();
        builder.AppendLine("This report reviews generated benchmark targets before they are treated as ground truth. Use it to remove false positives, tune thresholds, and confirm that each target has enough provenance to justify becoming a regression expectation.");
        builder.AppendLine();
        AppendReviewChecklist(builder);
        AppendFixtureSummary(builder, manifest.Fixtures ?? Array.Empty<BenchmarkFixture>());

        foreach (var fixture in manifest.Fixtures ?? Array.Empty<BenchmarkFixture>())
        {
            AppendFixtureDetails(builder, fixture);
        }

        return builder.ToString();
    }

    private static void AppendReviewChecklist(StringBuilder builder)
    {
        builder.AppendLine("## Review Checklist");
        builder.AppendLine();
        builder.AppendLine("- Open the source scan JSON in the viewer and visually verify each detector family.");
        builder.AppendLine("- Delete targets that are clear false positives or sheet/title-block contamination.");
        builder.AppendLine("- Prefer targets with page, bounds, semantic criteria, confidence, source IDs/layers, and evidence.");
        builder.AppendLine("- Keep precision gates unset until extra detections have been reviewed as actual false positives.");
        builder.AppendLine("- Tighten quality and count gates only after the source plan has been visually checked.");
        builder.AppendLine();
    }

    private static void AppendFixtureSummary(
        StringBuilder builder,
        IReadOnlyList<BenchmarkFixture> fixtures)
    {
        builder.AppendLine("## Fixture Summary");
        builder.AppendLine();
        if (fixtures.Count == 0)
        {
            builder.AppendLine("No fixtures in this draft.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Fixture | Source | Targets | Missing Bounds | Missing Provenance | Low Confidence |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: |");

        foreach (var fixture in fixtures)
        {
            var summary = TargetSummary.From(fixture);
            builder.AppendLine(
                $"| {MarkdownValue(FixtureLabel(fixture))} | {MarkdownValue(fixture.SourcePath)} | {summary.TotalTargets} | {summary.MissingBounds} | {summary.MissingProvenance} | {summary.LowConfidence} |");
        }

        builder.AppendLine();
    }

    private static void AppendFixtureDetails(StringBuilder builder, BenchmarkFixture fixture)
    {
        builder.AppendLine($"## Fixture: {MarkdownValue(FixtureLabel(fixture))}");
        builder.AppendLine();
        builder.AppendLine($"- Source: `{Clean(fixture.SourcePath) ?? "(missing)"}`");
        builder.AppendLine($"- Optional: {BoolValue(fixture.Optional)}");
        if (!string.IsNullOrWhiteSpace(fixture.SkipReason))
        {
            builder.AppendLine($"- Skip reason: {MarkdownValue(fixture.SkipReason)}");
        }

        AppendProperties(builder, fixture.Properties);
        AppendExpectationSummary(builder, fixture.Expectations);
        AppendDetectorSummary(builder, fixture.Expectations);
        AppendTargetDetails(builder, fixture.Expectations);
    }

    private static void AppendProperties(
        StringBuilder builder,
        IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
        {
            return;
        }

        builder.AppendLine("- Properties:");
        foreach (var property in properties.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  - `{property.Key}`: {MarkdownValue(property.Value)}");
        }
    }

    private static void AppendExpectationSummary(
        StringBuilder builder,
        BenchmarkExpectations expectations)
    {
        builder.AppendLine();
        builder.AppendLine("### Gates");
        builder.AppendLine();

        var countGates = new[]
            {
                ("pages", expectations.MinPages),
                ("regions", expectations.MinRegions),
                ("dimensions", expectations.MinDimensions),
                ("annotations", expectations.MinAnnotations),
                ("annotation references", expectations.MinAnnotationReferences),
                ("grid axes", expectations.MinGridAxes),
                ("grid bay spacings", expectations.MinGridBaySpacings),
                ("surface patterns", expectations.MinSurfacePatterns),
                ("walls", expectations.MinWalls),
                ("wall nodes", expectations.MinWallNodes),
                ("wall edges", expectations.MinWallEdges),
                ("rooms", expectations.MinRooms),
                ("room adjacencies", expectations.MinRoomAdjacencies),
                ("room clusters", expectations.MinRoomClusters),
                ("openings", expectations.MinOpenings),
                ("objects", expectations.MinObjects),
                ("object groups", expectations.MinObjectGroups),
                ("object aggregates", expectations.MinObjectAggregates),
                ("routing items", expectations.MinRoutingItems),
                ("routing suppressed objects", expectations.MinRoutingSuppressedObjects)
            }
            .Where(item => item.Item2 is not null)
            .Select(item => $"{item.Item1} >= {item.Item2}")
            .ToArray();

        builder.AppendLine(countGates.Length == 0
            ? "- Count gates: none"
            : $"- Count gates: {string.Join(", ", countGates)}");
        builder.AppendLine($"- Quality grade: {MarkdownValue(expectations.MinQualityGrade?.ToString())}");
        builder.AppendLine($"- Quality confidence: {RatioValue(expectations.MinQualityConfidence)}");
        builder.AppendLine($"- Max diagnostic warnings: {MarkdownValue(expectations.MaxDiagnosticWarnings?.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"- Max diagnostic errors: {MarkdownValue(expectations.MaxDiagnosticErrors?.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"- Max quality issues: {MarkdownValue(expectations.MaxQualityIssues?.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"- Max scan-risk issues: {MarkdownValue(expectations.MaxScanRiskIssues?.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"- Max scan review queue items: {MarkdownValue(expectations.MaxScanReviewQueueItems?.ToString(CultureInfo.InvariantCulture))}");
        builder.AppendLine($"- Max scan review queue kinds: {DictionaryValue(expectations.MaxScanReviewQueueKindCounts)}");
        builder.AppendLine($"- Import readiness grade: {MarkdownValue(expectations.MinImportReadinessGrade?.ToString())}");
        builder.AppendLine($"- Import readiness score: {RatioValue(expectations.MinImportReadinessScore)}");
        builder.AppendLine($"- Require geometry import ready: {BoolGateValue(expectations.RequireGeometryImportReady)}");
        builder.AppendLine($"- Require metric import ready: {BoolGateValue(expectations.RequireMetricImportReady)}");
        builder.AppendLine($"- Require routing import ready: {BoolGateValue(expectations.RequireRoutingImportReady)}");
        builder.AppendLine($"- Allow import review: {BoolGateValue(expectations.AllowImportReview)}");
        builder.AppendLine($"- Required import issues: {ListValue(expectations.RequiredImportIssueCodes)}");
        builder.AppendLine($"- Forbidden import issues: {ListValue(expectations.ForbiddenImportIssueCodes)}");
    }

    private static void AppendDetectorSummary(StringBuilder builder, BenchmarkExpectations expectations)
    {
        var detectors = DetectorMetrics(expectations)
            .Select(item => DetectorTargetSummary.From(item.Name, item.Metrics))
            .Where(summary => summary.Targets > 0)
            .ToArray();

        builder.AppendLine();
        builder.AppendLine("### Detector Targets");
        builder.AppendLine();
        if (detectors.Length == 0)
        {
            builder.AppendLine("No detector targets were drafted.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Detector | Targets | Recall | Precision | With Bounds | With Provenance | Low Confidence |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var summary in detectors)
        {
            builder.AppendLine(
                $"| {summary.Name} | {summary.Targets} | {RatioValue(summary.MinRecall)} | {RatioValue(summary.MinPrecision)} | {summary.WithBounds} | {summary.WithProvenance} | {summary.LowConfidence} |");
        }

        builder.AppendLine();
    }

    private static void AppendTargetDetails(StringBuilder builder, BenchmarkExpectations expectations)
    {
        builder.AppendLine("### Target Details");
        builder.AppendLine();

        var wroteAny = false;
        foreach (var detector in DetectorMetrics(expectations))
        {
            if (detector.Metrics.Targets.Count == 0)
            {
                continue;
            }

            wroteAny = true;
            builder.AppendLine($"#### {detector.Name}");
            builder.AppendLine();
            foreach (var target in detector.Metrics.Targets.Take(DetailedTargetLimitPerDetector))
            {
                builder.AppendLine($"- `{Clean(target.Id) ?? "(unnamed)"}`: {TargetCriteria(target)}");
                builder.AppendLine($"  - Confidence: {RatioValue(target.Confidence)}");
                builder.AppendLine($"  - Source layers: {ListValue(target.SourceLayers)}");
                builder.AppendLine($"  - Source IDs: {ListValue(target.SourcePrimitiveIds, 6)}");
                builder.AppendLine($"  - Evidence: {ListValue(target.Evidence, 3)}");
            }

            var omitted = detector.Metrics.Targets.Count - DetailedTargetLimitPerDetector;
            if (omitted > 0)
            {
                builder.AppendLine($"- ... {omitted} more target(s) omitted from this review section.");
            }

            builder.AppendLine();
        }

        if (!wroteAny)
        {
            builder.AppendLine("No target details available.");
            builder.AppendLine();
        }
    }

    private static IEnumerable<(string Name, BenchmarkDetectorMetricExpectations Metrics)> DetectorMetrics(
        BenchmarkExpectations expectations)
    {
        yield return ("regions", expectations.RegionMetrics);
        yield return ("dimensions", expectations.DimensionMetrics);
        yield return ("annotations", expectations.AnnotationMetrics);
        yield return ("annotation references", expectations.AnnotationReferenceMetrics);
        yield return ("grid axes", expectations.GridAxisMetrics);
        yield return ("walls", expectations.WallMetrics);
        yield return ("rooms", expectations.RoomMetrics);
        yield return ("openings", expectations.OpeningMetrics);
        yield return ("objects", expectations.ObjectMetrics);
        yield return ("object groups", expectations.ObjectGroupMetrics);
        yield return ("object aggregates", expectations.ObjectAggregateMetrics);
        yield return ("routing barriers", expectations.RoutingBarrierMetrics);
        yield return ("routing passages", expectations.RoutingPassageMetrics);
        yield return ("routing obstacles", expectations.RoutingObstacleMetrics);
        yield return ("routing room-use hints", expectations.RoutingRoomUseHintMetrics);
        yield return ("routing suppressed objects", expectations.RoutingSuppressedObjectMetrics);
        yield return ("layers", expectations.LayerMetrics);
    }

    private static string TargetCriteria(BenchmarkDetectionTarget target)
    {
        var parts = new List<string>();
        if (target.PageNumber is not null)
        {
            parts.Add($"page {target.PageNumber}");
        }

        if (target.Bounds is not null)
        {
            var bounds = target.Bounds.Value;
            parts.Add($"bounds ({bounds.X:0.###}, {bounds.Y:0.###}, {bounds.Width:0.###}, {bounds.Height:0.###})");
        }

        AddPart(parts, "label", target.Label);
        AddPart(parts, "text", target.Text);
        AddPart(parts, "marker", target.Marker);
        AddPart(parts, "region", target.RegionKind?.ToString());
        AddPart(parts, "dimension", target.DimensionKind?.ToString());
        AddPart(parts, "orientation", target.DimensionOrientation?.ToString() ?? target.GridAxisOrientation?.ToString());
        AddPart(parts, "annotation", target.AnnotationKind?.ToString());
        AddPart(parts, "opening", target.OpeningType?.ToString());
        AddPart(parts, "operation", target.OpeningOperation?.ToString());
        AddPart(parts, "object", target.ObjectCategory?.ToString());
        AddPart(parts, "object kind", target.ObjectKind?.ToString());
        AddPart(parts, "layer", target.LayerCategory?.ToString());
        AddPart(parts, "routing source", target.RoutingSourceKind?.ToString());
        AddPart(parts, "routing obstacle", target.RoutingObstacleKind?.ToString());
        AddPart(parts, "routing influence", target.RoutingInfluence?.ToString());
        AddPart(parts, "structural influence", target.StructuralInfluence?.ToString());
        AddPart(parts, "room use", target.RoomUseKind?.ToString());
        AddPart(parts, "child object", target.ObjectCandidateId);
        AddPart(parts, "suppressed by", target.SuppressedByAggregateId);
        AddPart(parts, "suppression reason", target.SuppressionReason?.ToString());
        AddPart(parts, "suppression action", target.SuppressionAction?.ToString());
        AddPart(parts, "replacement obstacle", target.ReplacementRoutingObstacleId);
        AddPart(parts, "room-use hint", target.RoomUseHintId);
        if (target.DetectedTags is { Count: > 0 })
        {
            parts.Add($"detected tags {ListValue(target.DetectedTags)}");
        }

        if (target.MinCount is not null)
        {
            parts.Add($"count >= {target.MinCount}");
        }

        if (target.RequiresReview is not null)
        {
            parts.Add($"requires review {BoolValue(target.RequiresReview.Value)}");
        }

        if (target.SuppressesChildObjects is not null)
        {
            parts.Add($"suppresses child objects {BoolValue(target.SuppressesChildObjects.Value)}");
        }

        return parts.Count == 0 ? "no criteria" : string.Join("; ", parts);
    }

    private static void AddPart(List<string> parts, string label, string? value)
    {
        value = Clean(value);
        if (value is not null)
        {
            parts.Add($"{label} `{EscapeMarkdown(value)}`");
        }
    }

    private static string FixtureLabel(BenchmarkFixture fixture) =>
        Clean(fixture.Name) ?? Clean(fixture.Id) ?? Clean(fixture.SourcePath) ?? "(unnamed fixture)";

    private static string MarkdownValue(string? value) =>
        Clean(value) is { } clean ? EscapeMarkdown(clean) : "-";

    private static string RatioValue(double? value) =>
        value is null ? "-" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string BoolValue(bool value) => value ? "true" : "false";

    private static string BoolGateValue(bool? value) => value is null ? "-" : BoolValue(value.Value);

    private static string ListValue(IReadOnlyList<string>? values, int limit = 5)
    {
        if (values is null || values.Count == 0)
        {
            return "-";
        }

        var clean = values
            .Select(Clean)
            .Where(value => value is not null)
            .Select(value => value!)
            .Take(limit)
            .Select(EscapeMarkdown)
            .ToArray();
        if (clean.Length == 0)
        {
            return "-";
        }

        var suffix = values.Count > limit ? $" (+{values.Count - limit} more)" : string.Empty;
        return string.Join(", ", clean) + suffix;
    }

    private static string DictionaryValue(IReadOnlyDictionary<string, int>? values, int limit = 5)
    {
        if (values is null || values.Count == 0)
        {
            return "-";
        }

        var clean = values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(pair => $"{EscapeMarkdown(pair.Key.Trim())} <= {pair.Value.ToString(CultureInfo.InvariantCulture)}")
            .ToArray();
        if (clean.Length == 0)
        {
            return "-";
        }

        var suffix = values.Count > limit ? $" (+{values.Count - limit} more)" : string.Empty;
        return string.Join(", ", clean) + suffix;
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record TargetSummary(
        int TotalTargets,
        int MissingBounds,
        int MissingProvenance,
        int LowConfidence)
    {
        public static TargetSummary From(BenchmarkFixture fixture)
        {
            var targets = DetectorMetrics(fixture.Expectations)
                .SelectMany(item => item.Metrics.Targets)
                .ToArray();

            return new TargetSummary(
                targets.Length,
                targets.Count(target => target.Bounds is null),
                targets.Count(target => !HasProvenance(target)),
                targets.Count(IsLowConfidence));
        }
    }

    private sealed record DetectorTargetSummary(
        string Name,
        int Targets,
        double? MinRecall,
        double? MinPrecision,
        int WithBounds,
        int WithProvenance,
        int LowConfidence)
    {
        public static DetectorTargetSummary From(
            string name,
            BenchmarkDetectorMetricExpectations metrics) =>
            new(
                name,
                metrics.Targets.Count,
                metrics.MinRecall,
                metrics.MinPrecision,
                metrics.Targets.Count(target => target.Bounds is not null),
                metrics.Targets.Count(HasProvenance),
                metrics.Targets.Count(IsLowConfidence));
    }

    private static bool HasProvenance(BenchmarkDetectionTarget target) =>
        target.Confidence is not null
        || target.SourcePrimitiveIds is { Count: > 0 }
        || target.SourceLayers is { Count: > 0 }
        || target.Evidence is { Count: > 0 };

    private static bool IsLowConfidence(BenchmarkDetectionTarget target) =>
        target.Confidence is not null && target.Confidence.Value < LowConfidenceThreshold;
}
