using System.Globalization;
using System.Text;

internal static class BatchScanMarkdownReport
{
    public static string Create(BatchScanRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var builder = new StringBuilder();
        builder.AppendLine("# OpenPlanTrace Batch Scan Report");
        builder.AppendLine();
        builder.AppendLine($"Generated: {run.GeneratedAt:O}");
        builder.AppendLine($"Status: {ReportStatus(run)}");
        builder.AppendLine($"Output directory: {Code(run.OutputDirectory ?? "-")}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Items: {run.ItemCount}");
        builder.AppendLine($"- Succeeded: {run.SucceededCount}");
        builder.AppendLine($"- Completed with diagnostic errors: {run.CompletedWithErrorsCount}");
        builder.AppendLine($"- Missing: {run.MissingCount}");
        builder.AppendLine($"- Unsupported: {run.UnsupportedCount}");
        builder.AppendLine($"- Failed: {run.FailedScanCount}");
        builder.AppendLine($"- Execution: parallel {run.MaxDegreeOfParallelism}, retries {run.RetryCount}");
        builder.AppendLine();
        builder.AppendLine("The JSON batch result remains the machine contract. This report is the human QA layer for corpus review, visual screenshot triage, and choosing the next scanner fixes.");
        builder.AppendLine();

        AppendCorpusTable(builder, run);
        AppendCorpusSignals(builder, run);
        AppendReviewPriorities(builder, run);
        AppendArtifactIndex(builder, run);
        AppendNextActions(builder, run);
        return builder.ToString();
    }

    private static void AppendCorpusTable(StringBuilder builder, BatchScanRunResult run)
    {
        builder.AppendLine("## Corpus Table");
        builder.AppendLine();
        builder.AppendLine("| Item | Status | Source | Quality | Geometry | Visual QA | Diagnostics | Artifacts |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        if (run.Items.Count == 0)
        {
            builder.AppendLine("| - | - | - | - | - | - | - | - |");
            builder.AppendLine();
            return;
        }

        foreach (var item in run.Items.OrderBy(item => item.ItemNumber))
        {
            builder.AppendLine(
                $"| {Cell(ItemLabel(item))} | {Cell(item.Status.ToString())} | {Cell(SourceSummary(item))} | {Cell(QualitySummary(item))} | {Cell(GeometrySummary(item))} | {Cell(VisualSummary(item))} | {Cell(DiagnosticSummary(item))} | {Cell(ArtifactSummary(item))} |");
        }

        builder.AppendLine();
    }

    private static void AppendCorpusSignals(StringBuilder builder, BatchScanRunResult run)
    {
        builder.AppendLine("## Corpus Signals");
        builder.AppendLine();

        if (run.Items.Count == 0)
        {
            builder.AppendLine("No corpus signals.");
            builder.AppendLine();
            return;
        }

        var totals = new
        {
            Walls = run.Items.Sum(item => item.Counts.Walls),
            Rooms = run.Items.Sum(item => item.Counts.Rooms),
            Openings = run.Items.Sum(item => item.Counts.Openings),
            Objects = run.Items.Sum(item => item.Counts.Objects),
            Aggregates = run.Items.Sum(item => item.Counts.ObjectAggregates),
            Routing = run.Items.Sum(item => item.Counts.RoutingItems),
            SurfacePatterns = run.Items.Sum(item => item.Counts.SurfacePatterns),
            Diagnostics = run.Items.Sum(item => item.Counts.Diagnostics),
            Warnings = run.Items.Sum(item => item.Counts.DiagnosticWarnings),
            Errors = run.Items.Sum(item => item.Counts.DiagnosticErrors),
            VisualIssues = run.Items.Sum(item => item.VisualSnapshot.IssueCount)
        };

        builder.AppendLine($"- Geometry totals: walls {totals.Walls}, rooms {totals.Rooms}, openings {totals.Openings}, objects {totals.Objects}, aggregates {totals.Aggregates}, routing {totals.Routing}, surface/detail patterns {totals.SurfacePatterns}");
        builder.AppendLine($"- Review burden: {run.Items.Count(item => item.Counts.RequiresReview)} review-required item(s), {totals.VisualIssues} visual issue(s), {totals.Diagnostics} diagnostic message(s), {totals.Warnings} warning(s), {totals.Errors} error(s)");
        builder.AppendLine($"- Source kinds: {FormatCounts(run.Items.Select(item => SourceSummary(item)))}");
        builder.AppendLine($"- Quality grades: {FormatCounts(run.Items.Select(item => item.Counts.QualityGrade))}");
        builder.AppendLine($"- Visual issue codes: {FormatCounts(run.Items.SelectMany(item => item.VisualSnapshot.IssueCodes))}");

        var slowest = run.Items
            .OrderByDescending(item => item.DurationMilliseconds)
            .Take(3)
            .Select(item => $"{ItemLabel(item)} {FormatMilliseconds(item.DurationMilliseconds)}");
        builder.AppendLine($"- Slowest scans: {FormatJoined(slowest)}");
        builder.AppendLine();
    }

    private static void AppendReviewPriorities(StringBuilder builder, BatchScanRunResult run)
    {
        builder.AppendLine("## Review Priorities");
        builder.AppendLine();

        var priorities = run.Items
            .Select(item => new { Item = item, Reasons = ReviewReasons(item) })
            .Where(item => item.Reasons.Count > 0)
            .OrderByDescending(item => ReviewWeight(item.Item))
            .ThenBy(item => ItemLabel(item.Item), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (priorities.Length == 0)
        {
            builder.AppendLine("No review priorities were raised by the batch summary.");
            builder.AppendLine();
            return;
        }

        foreach (var priority in priorities)
        {
            builder.AppendLine($"- {Text(ItemLabel(priority.Item))}: {string.Join("; ", priority.Reasons)}");
        }

        builder.AppendLine();
    }

    private static void AppendArtifactIndex(StringBuilder builder, BatchScanRunResult run)
    {
        builder.AppendLine("## Artifact Index");
        builder.AppendLine();

        if (run.Items.Count == 0)
        {
            builder.AppendLine("No artifacts.");
            builder.AppendLine();
            return;
        }

        foreach (var item in run.Items.OrderBy(item => item.ItemNumber))
        {
            builder.AppendLine($"### {Text(ItemLabel(item))}");
            builder.AppendLine();
            builder.AppendLine($"- Input: {Code(item.InputPath)}");
            AppendArtifact(builder, "Scan JSON", item.ScanJsonPath);
            AppendArtifact(builder, "Placement JSON", item.PlacementJsonPath);
            AppendArtifact(builder, "GeoJSON", item.GeoJsonPath);
            AppendArtifact(builder, "Visual snapshot", item.VisualSnapshotPath);
            AppendArtifact(builder, "SVG overlays", item.OverlayDirectory);
            if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            {
                builder.AppendLine($"- Error: {Text(item.ErrorMessage)}");
            }

            if (item.SourceCapability is not null)
            {
                builder.AppendLine($"- Source capability: {Text(item.SourceCapability.Key)} {Text(item.SourceCapability.Status.ToString())}");
                builder.AppendLine($"- Adapter: {Text(item.SourceCapability.AdapterRequirement)}");
                builder.AppendLine($"- Licensing: {Text(item.SourceCapability.LicenseNote)}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendNextActions(StringBuilder builder, BatchScanRunResult run)
    {
        builder.AppendLine("## Next Actions");
        builder.AppendLine();

        if (run.Items.Count == 0)
        {
            builder.AppendLine("- Add at least one PDF, DXF, or registered source format to the batch.");
            builder.AppendLine();
            return;
        }

        if (run.FailedCount > 0)
        {
            builder.AppendLine("- Fix missing/unsupported/failed inputs before trusting corpus-level scanner trends.");
        }

        if (run.Items.Any(item => item.VisualSnapshot.IssueCount > 0))
        {
            builder.AppendLine("- Open the SVG overlays and visual snapshots for items with visual QA issues before changing detector thresholds.");
        }

        if (run.Items.Any(item => item.Counts.DiagnosticWarnings > 0))
        {
            builder.AppendLine("- Review repeated diagnostic warning codes and decide whether they should become benchmark gates.");
        }

        if (run.Items.Any(item => item.Counts.RequiresReview))
        {
            builder.AppendLine("- Promote repeated review findings into benchmark targets or deterministic layer/object profiles.");
        }

        if (run.Items.Any(item => item.Counts.Walls == 0 || item.Counts.Rooms == 0))
        {
            builder.AppendLine("- Inspect low wall/room-count scans first; they usually reveal loader, scale, or wall-candidate failures.");
        }

        if (run.Items.All(item => item.Status == BatchScanItemStatus.Succeeded && !item.Counts.RequiresReview && item.VisualSnapshot.IssueCount == 0))
        {
            builder.AppendLine("- Use this run as a baseline and compare future scanner changes with `batch-compare`.");
        }

        builder.AppendLine();
    }

    private static string ReportStatus(BatchScanRunResult run)
    {
        if (!run.Passed)
        {
            return "BLOCKED";
        }

        return run.Items.Any(item =>
            item.Counts.RequiresReview
            || item.Counts.DiagnosticWarnings > 0
            || item.VisualSnapshot.IssueCount > 0)
            ? "REVIEW"
            : "PASS";
    }

    private static void AppendArtifact(StringBuilder builder, string label, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            builder.AppendLine($"- {label}: {Code(path)}");
        }
    }

    private static IReadOnlyList<string> ReviewReasons(BatchScanItemResult item)
    {
        var reasons = new List<string>();
        if (item.Status != BatchScanItemStatus.Succeeded)
        {
            reasons.Add($"status {item.Status}");
        }

        if (item.Counts.DiagnosticErrors > 0)
        {
            reasons.Add($"{item.Counts.DiagnosticErrors} diagnostic error(s)");
        }

        if (item.Counts.DiagnosticWarnings > 0)
        {
            reasons.Add($"{item.Counts.DiagnosticWarnings} diagnostic warning(s)");
        }

        if (item.Counts.RequiresReview)
        {
            reasons.Add($"quality review required ({item.Counts.QualityGrade} {FormatConfidence(item.Counts.QualityConfidence)})");
        }

        if (item.VisualSnapshot.ErrorIssueCount > 0)
        {
            reasons.Add($"{item.VisualSnapshot.ErrorIssueCount} visual error issue(s)");
        }

        if (item.VisualSnapshot.WarningIssueCount > 0)
        {
            reasons.Add($"{item.VisualSnapshot.WarningIssueCount} visual warning issue(s)");
        }

        if (item.VisualSnapshot.IssueCodes.Count > 0)
        {
            reasons.Add($"visual codes {string.Join(", ", item.VisualSnapshot.IssueCodes.Take(4))}");
        }

        if (item.Counts.Walls == 0 && item.Status is BatchScanItemStatus.Succeeded or BatchScanItemStatus.CompletedWithErrors)
        {
            reasons.Add("no walls detected");
        }

        if (item.Counts.Rooms == 0 && item.Counts.Walls > 0)
        {
            reasons.Add("walls detected but no rooms solved");
        }

        if (item.Counts.SurfacePatterns > 0)
        {
            reasons.Add($"{item.Counts.SurfacePatterns} surface/detail pattern(s) excluded from walls");
        }

        return reasons;
    }

    private static int ReviewWeight(BatchScanItemResult item)
    {
        var weight = 0;
        if (item.Status is BatchScanItemStatus.Failed or BatchScanItemStatus.Missing)
        {
            weight += 100;
        }

        if (item.Status == BatchScanItemStatus.Unsupported)
        {
            weight += 80;
        }

        weight += item.Counts.DiagnosticErrors * 10;
        weight += item.VisualSnapshot.ErrorIssueCount * 8;
        weight += item.VisualSnapshot.WarningIssueCount * 4;
        if (item.Counts.RequiresReview)
        {
            weight += 5;
        }

        if (item.Counts.Walls == 0)
        {
            weight += 5;
        }

        return weight;
    }

    private static string ItemLabel(BatchScanItemResult item) =>
        item.FileName ?? item.InputPath ?? $"item-{item.ItemNumber.ToString(CultureInfo.InvariantCulture)}";

    private static string SourceSummary(BatchScanItemResult item)
    {
        var value = item.SourceKind == item.EffectiveSourceKind
            ? item.SourceKind.ToString()
            : $"{item.SourceKind}->{item.EffectiveSourceKind}";
        if (item.SourceCapability is not null)
        {
            value += $" ({item.SourceCapability.Status})";
        }

        return value;
    }

    private static string QualitySummary(BatchScanItemResult item)
    {
        if (item.Counts.QualityGrade == "-")
        {
            return "-";
        }

        var review = item.Counts.RequiresReview ? ", review" : string.Empty;
        return $"{item.Counts.QualityGrade} {FormatConfidence(item.Counts.QualityConfidence)}{review}";
    }

    private static string GeometrySummary(BatchScanItemResult item) =>
        $"pages {item.Counts.Pages}, walls {item.Counts.Walls}, nodes {item.Counts.WallNodes}, rooms {item.Counts.Rooms}, openings {item.Counts.Openings}, objects {item.Counts.Objects}, aggregates {item.Counts.ObjectAggregates}, routing {item.Counts.RoutingItems}, surfaces {item.Counts.SurfacePatterns}";

    private static string VisualSummary(BatchScanItemResult item)
    {
        if (item.VisualSnapshot.SchemaVersion == "-")
        {
            return "-";
        }

        var codes = item.VisualSnapshot.IssueCodes.Count == 0
            ? "-"
            : string.Join(", ", item.VisualSnapshot.IssueCodes.Take(3));
        return $"{item.VisualSnapshot.DrawableItemCount} items, {item.VisualSnapshot.IssueCount} issues, coverage {FormatPercent(item.VisualSnapshot.MaxDetectionCoverage)}, codes {codes}";
    }

    private static string DiagnosticSummary(BatchScanItemResult item) =>
        $"{item.Counts.Diagnostics} total, {item.Counts.DiagnosticWarnings} warnings, {item.Counts.DiagnosticErrors} errors";

    private static string ArtifactSummary(BatchScanItemResult item)
    {
        var artifacts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.ScanJsonPath))
        {
            artifacts.Add("scan");
        }

        if (!string.IsNullOrWhiteSpace(item.VisualSnapshotPath))
        {
            artifacts.Add("visual");
        }

        if (!string.IsNullOrWhiteSpace(item.GeoJsonPath))
        {
            artifacts.Add("geojson");
        }

        if (!string.IsNullOrWhiteSpace(item.PlacementJsonPath))
        {
            artifacts.Add("placement");
        }

        if (!string.IsNullOrWhiteSpace(item.OverlayDirectory))
        {
            artifacts.Add("svg");
        }

        return artifacts.Count == 0 ? "-" : string.Join("+", artifacts);
    }

    private static string FormatConfidence(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatMilliseconds(double value) =>
        $"{value.ToString("0.##", CultureInfo.InvariantCulture)} ms";

    private static string FormatPercent(double value) =>
        value.ToString("P0", CultureInfo.InvariantCulture);

    private static string FormatCounts(IEnumerable<string> values)
    {
        var counts = values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "-")
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}");

        return FormatJoined(counts);
    }

    private static string FormatJoined(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? "-" : string.Join(", ", materialized);
    }

    private static string Code(string value) =>
        $"`{Text(value).Replace("`", "\\`", StringComparison.Ordinal)}`";

    private static string Cell(string value) =>
        Text(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Text(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
