using System.Globalization;
using System.Text;

internal enum BatchScanComparisonItemStatus
{
    Matched = 0,
    Added,
    Removed
}

internal enum BatchScanComparisonSignalSeverity
{
    Info = 0,
    Improvement,
    Regression
}

internal sealed record BatchScanComparisonResult(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string? BaselineOutputDirectory,
    string? CandidateOutputDirectory,
    int BaselineItemCount,
    int CandidateItemCount,
    int MatchedItemCount,
    int AddedItemCount,
    int RemovedItemCount,
    int StatusChangeCount,
    int RegressionCount,
    int ImprovementCount,
    int InfoCount,
    int DiagnosticErrorDelta,
    int DiagnosticWarningDelta,
    int VisualIssueDelta,
    int VisualErrorIssueDelta,
    double QualityConfidenceAverageDelta,
    double TotalDurationDeltaMilliseconds,
    IReadOnlyList<BatchScanItemComparison> Items,
    IReadOnlyList<BatchScanComparisonSignal> Signals)
{
    public const string CurrentSchemaVersion = "openplantrace.batch-comparison.v1";

    public bool Passed => RegressionCount == 0;

    public static BatchScanComparisonResult Compare(
        BatchScanRunResult baseline,
        BatchScanRunResult candidate,
        BatchScanComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        options ??= new BatchScanComparisonOptions();

        var baselineByKey = IndexItems(baseline.Items);
        var candidateByKey = IndexItems(candidate.Items);
        var keys = baselineByKey.Keys
            .Concat(candidateByKey.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<BatchScanItemComparison>();
        foreach (var key in keys)
        {
            baselineByKey.TryGetValue(key, out var baselineItem);
            candidateByKey.TryGetValue(key, out var candidateItem);
            items.Add(CompareItem(key, baselineItem, candidateItem, options));
        }

        var signals = items.SelectMany(item => item.Signals).ToArray();
        return new BatchScanComparisonResult(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            baseline.OutputDirectory,
            candidate.OutputDirectory,
            baseline.ItemCount,
            candidate.ItemCount,
            items.Count(item => item.Status == BatchScanComparisonItemStatus.Matched),
            items.Count(item => item.Status == BatchScanComparisonItemStatus.Added),
            items.Count(item => item.Status == BatchScanComparisonItemStatus.Removed),
            items.Count(item => item.Status == BatchScanComparisonItemStatus.Matched
                && item.BaselineStatus is not null
                && item.CandidateStatus is not null
                && item.BaselineStatus != item.CandidateStatus),
            signals.Count(signal => signal.Severity == BatchScanComparisonSignalSeverity.Regression),
            signals.Count(signal => signal.Severity == BatchScanComparisonSignalSeverity.Improvement),
            signals.Count(signal => signal.Severity == BatchScanComparisonSignalSeverity.Info),
            candidate.Items.Sum(item => item.Counts.DiagnosticErrors) - baseline.Items.Sum(item => item.Counts.DiagnosticErrors),
            candidate.Items.Sum(item => item.Counts.DiagnosticWarnings) - baseline.Items.Sum(item => item.Counts.DiagnosticWarnings),
            candidate.Items.Sum(item => item.VisualSnapshot.IssueCount) - baseline.Items.Sum(item => item.VisualSnapshot.IssueCount),
            candidate.Items.Sum(item => item.VisualSnapshot.ErrorIssueCount) - baseline.Items.Sum(item => item.VisualSnapshot.ErrorIssueCount),
            AverageQuality(candidate.Items) - AverageQuality(baseline.Items),
            candidate.Items.Sum(item => item.DurationMilliseconds) - baseline.Items.Sum(item => item.DurationMilliseconds),
            items,
            signals);
    }

    private static IReadOnlyDictionary<string, BatchScanItemResult> IndexItems(IReadOnlyList<BatchScanItemResult> items)
    {
        var fileNameCounts = items
            .Select(item => NormalizeKey(item.FileName))
            .Where(key => key is not null)
            .Select(key => key!)
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var indexed = new Dictionary<string, BatchScanItemResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var fileNameKey = NormalizeKey(item.FileName);
            var key = fileNameKey is not null
                      && fileNameCounts.TryGetValue(fileNameKey, out var fileNameCount)
                      && fileNameCount == 1
                ? fileNameKey
                : NormalizePathKey(item.InputPath) ?? fileNameKey ?? item.ItemNumber.ToString(CultureInfo.InvariantCulture);

            if (indexed.ContainsKey(key))
            {
                key = $"{key}#{item.ItemNumber.ToString(CultureInfo.InvariantCulture)}";
            }

            indexed[key] = item;
        }

        return indexed;
    }

    private static BatchScanItemComparison CompareItem(
        string key,
        BatchScanItemResult? baseline,
        BatchScanItemResult? candidate,
        BatchScanComparisonOptions options)
    {
        var displayKey = DisplayKey(key, baseline, candidate);
        if (baseline is null)
        {
            var signals = new[]
            {
                new BatchScanComparisonSignal(
                    displayKey,
                    "item.added",
                    BatchScanComparisonSignalSeverity.Info,
                    "Item exists only in the candidate batch run.",
                    null,
                    candidate?.FileName ?? candidate?.InputPath)
            };

            return new BatchScanItemComparison(
                displayKey,
                BatchScanComparisonItemStatus.Added,
                null,
                candidate?.InputPath,
                null,
                candidate?.FileName,
                null,
                candidate?.ScanJsonPath,
                null,
                candidate?.VisualSnapshotPath,
                null,
                candidate?.GeoJsonPath,
                null,
                candidate?.PlacementJsonPath,
                null,
                candidate?.OverlayDirectory,
                null,
                candidate?.Status,
                null,
                candidate?.DurationMilliseconds,
                null,
                null,
                candidate?.Counts.QualityGrade,
                null,
                candidate?.Counts.QualityConfidence,
                null,
                candidate?.Counts.DiagnosticErrors,
                null,
                candidate?.VisualSnapshot.IssueCount,
                null,
                candidate?.VisualSnapshot.ErrorIssueCount,
                Array.Empty<BatchScanMetricDelta>(),
                candidate?.VisualSnapshot.IssueCodes ?? Array.Empty<string>(),
                Array.Empty<string>(),
                signals);
        }

        if (candidate is null)
        {
            var signals = new[]
            {
                new BatchScanComparisonSignal(
                    displayKey,
                    "item.removed",
                    BatchScanComparisonSignalSeverity.Regression,
                    "Item was present in the baseline batch run but is missing from the candidate run.",
                    baseline.FileName ?? baseline.InputPath,
                    null)
            };

            return new BatchScanItemComparison(
                displayKey,
                BatchScanComparisonItemStatus.Removed,
                baseline.InputPath,
                null,
                baseline.FileName,
                null,
                baseline.ScanJsonPath,
                null,
                baseline.VisualSnapshotPath,
                null,
                baseline.GeoJsonPath,
                null,
                baseline.PlacementJsonPath,
                null,
                baseline.OverlayDirectory,
                null,
                baseline.Status,
                null,
                baseline.DurationMilliseconds,
                null,
                null,
                baseline.Counts.QualityGrade,
                null,
                baseline.Counts.QualityConfidence,
                null,
                baseline.Counts.DiagnosticErrors,
                null,
                baseline.VisualSnapshot.IssueCount,
                null,
                baseline.VisualSnapshot.ErrorIssueCount,
                null,
                Array.Empty<BatchScanMetricDelta>(),
                Array.Empty<string>(),
                baseline.VisualSnapshot.IssueCodes,
                signals);
        }

        var itemSignals = new List<BatchScanComparisonSignal>();
        AddStatusSignals(displayKey, baseline, candidate, itemSignals);
        AddQualitySignals(displayKey, baseline, candidate, options, itemSignals);
        AddDiagnosticSignals(displayKey, baseline, candidate, options, itemSignals);
        AddVisualSignals(displayKey, baseline, candidate, options, itemSignals);
        AddDurationSignals(displayKey, baseline, candidate, options, itemSignals);
        AddZeroedDetectorSignals(displayKey, baseline, candidate, itemSignals);
        AddCountDriftSignals(displayKey, baseline, candidate, itemSignals);

        var baselineIssueCodes = baseline.VisualSnapshot.IssueCodes ?? Array.Empty<string>();
        var candidateIssueCodes = candidate.VisualSnapshot.IssueCodes ?? Array.Empty<string>();
        var addedIssueCodes = candidateIssueCodes
            .Except(baselineIssueCodes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var removedIssueCodes = baselineIssueCodes
            .Except(candidateIssueCodes, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new BatchScanItemComparison(
            displayKey,
            BatchScanComparisonItemStatus.Matched,
            baseline.InputPath,
            candidate.InputPath,
            baseline.FileName,
            candidate.FileName,
            baseline.ScanJsonPath,
            candidate.ScanJsonPath,
            baseline.VisualSnapshotPath,
            candidate.VisualSnapshotPath,
            baseline.GeoJsonPath,
            candidate.GeoJsonPath,
            baseline.PlacementJsonPath,
            candidate.PlacementJsonPath,
            baseline.OverlayDirectory,
            candidate.OverlayDirectory,
            baseline.Status,
            candidate.Status,
            baseline.DurationMilliseconds,
            candidate.DurationMilliseconds,
            candidate.DurationMilliseconds - baseline.DurationMilliseconds,
            baseline.Counts.QualityGrade,
            candidate.Counts.QualityGrade,
            baseline.Counts.QualityConfidence,
            candidate.Counts.QualityConfidence,
            baseline.Counts.DiagnosticErrors,
            candidate.Counts.DiagnosticErrors,
            baseline.VisualSnapshot.IssueCount,
            candidate.VisualSnapshot.IssueCount,
            baseline.VisualSnapshot.ErrorIssueCount,
            candidate.VisualSnapshot.ErrorIssueCount,
            CreateMetricDeltas(baseline, candidate),
            addedIssueCodes,
            removedIssueCodes,
            itemSignals);
    }

    private static string DisplayKey(
        string key,
        BatchScanItemResult? baseline,
        BatchScanItemResult? candidate) =>
        candidate?.FileName
        ?? baseline?.FileName
        ?? candidate?.InputPath
        ?? baseline?.InputPath
        ?? key;

    private static void AddStatusSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        ICollection<BatchScanComparisonSignal> signals)
    {
        if (baseline.Status == candidate.Status)
        {
            return;
        }

        var baselineRank = StatusRank(baseline.Status);
        var candidateRank = StatusRank(candidate.Status);
        if (candidateRank < baselineRank)
        {
            signals.Add(Regression(
                key,
                "status.regressed",
                "Candidate scan status is worse than the baseline.",
                baseline.Status.ToString(),
                candidate.Status.ToString()));
        }
        else if (candidateRank > baselineRank)
        {
            signals.Add(Improvement(
                key,
                "status.improved",
                "Candidate scan status is better than the baseline.",
                baseline.Status.ToString(),
                candidate.Status.ToString()));
        }
        else
        {
            signals.Add(Info(
                key,
                "status.changed",
                "Candidate scan status changed.",
                baseline.Status.ToString(),
                candidate.Status.ToString()));
        }
    }

    private static void AddQualitySignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        BatchScanComparisonOptions options,
        ICollection<BatchScanComparisonSignal> signals)
    {
        var confidenceDelta = candidate.Counts.QualityConfidence - baseline.Counts.QualityConfidence;
        if (confidenceDelta <= -options.QualityConfidenceRegressionThreshold)
        {
            signals.Add(Regression(
                key,
                "quality.confidence_drop",
                "Candidate quality confidence dropped beyond the configured threshold.",
                FormatRatio(baseline.Counts.QualityConfidence),
                FormatRatio(candidate.Counts.QualityConfidence)));
        }
        else if (confidenceDelta >= options.QualityConfidenceRegressionThreshold)
        {
            signals.Add(Improvement(
                key,
                "quality.confidence_improved",
                "Candidate quality confidence improved beyond the configured threshold.",
                FormatRatio(baseline.Counts.QualityConfidence),
                FormatRatio(candidate.Counts.QualityConfidence)));
        }

        if (!baseline.Counts.RequiresReview && candidate.Counts.RequiresReview)
        {
            signals.Add(Regression(
                key,
                "quality.requires_review",
                "Candidate now requires review after the baseline did not.",
                "false",
                "true"));
        }
        else if (baseline.Counts.RequiresReview && !candidate.Counts.RequiresReview)
        {
            signals.Add(Improvement(
                key,
                "quality.review_cleared",
                "Candidate no longer requires review.",
                "true",
                "false"));
        }
    }

    private static void AddDiagnosticSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        BatchScanComparisonOptions options,
        ICollection<BatchScanComparisonSignal> signals)
    {
        var errorDelta = candidate.Counts.DiagnosticErrors - baseline.Counts.DiagnosticErrors;
        if (errorDelta >= options.DiagnosticErrorIncreaseThreshold)
        {
            signals.Add(Regression(
                key,
                "diagnostics.errors_increased",
                "Candidate has more diagnostic errors.",
                baseline.Counts.DiagnosticErrors.ToString(CultureInfo.InvariantCulture),
                candidate.Counts.DiagnosticErrors.ToString(CultureInfo.InvariantCulture)));
        }
        else if (-errorDelta >= options.DiagnosticErrorIncreaseThreshold)
        {
            signals.Add(Improvement(
                key,
                "diagnostics.errors_reduced",
                "Candidate has fewer diagnostic errors.",
                baseline.Counts.DiagnosticErrors.ToString(CultureInfo.InvariantCulture),
                candidate.Counts.DiagnosticErrors.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AddVisualSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        BatchScanComparisonOptions options,
        ICollection<BatchScanComparisonSignal> signals)
    {
        var issueDelta = candidate.VisualSnapshot.IssueCount - baseline.VisualSnapshot.IssueCount;
        if (issueDelta >= options.VisualIssueIncreaseThreshold)
        {
            signals.Add(Regression(
                key,
                "visual.issues_increased",
                "Candidate visual snapshot has more review issues.",
                baseline.VisualSnapshot.IssueCount.ToString(CultureInfo.InvariantCulture),
                candidate.VisualSnapshot.IssueCount.ToString(CultureInfo.InvariantCulture)));
        }
        else if (-issueDelta >= options.VisualIssueIncreaseThreshold)
        {
            signals.Add(Improvement(
                key,
                "visual.issues_reduced",
                "Candidate visual snapshot has fewer review issues.",
                baseline.VisualSnapshot.IssueCount.ToString(CultureInfo.InvariantCulture),
                candidate.VisualSnapshot.IssueCount.ToString(CultureInfo.InvariantCulture)));
        }

        if (candidate.VisualSnapshot.ErrorIssueCount > baseline.VisualSnapshot.ErrorIssueCount)
        {
            signals.Add(Regression(
                key,
                "visual.error_issues_increased",
                "Candidate visual snapshot has more error-severity visual issues.",
                baseline.VisualSnapshot.ErrorIssueCount.ToString(CultureInfo.InvariantCulture),
                candidate.VisualSnapshot.ErrorIssueCount.ToString(CultureInfo.InvariantCulture)));
        }
        else if (candidate.VisualSnapshot.ErrorIssueCount < baseline.VisualSnapshot.ErrorIssueCount)
        {
            signals.Add(Improvement(
                key,
                "visual.error_issues_reduced",
                "Candidate visual snapshot has fewer error-severity visual issues.",
                baseline.VisualSnapshot.ErrorIssueCount.ToString(CultureInfo.InvariantCulture),
                candidate.VisualSnapshot.ErrorIssueCount.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static void AddDurationSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        BatchScanComparisonOptions options,
        ICollection<BatchScanComparisonSignal> signals)
    {
        var delta = candidate.DurationMilliseconds - baseline.DurationMilliseconds;
        if (baseline.DurationMilliseconds > 0
            && delta >= options.DurationRegressionMinimumMilliseconds
            && candidate.DurationMilliseconds / baseline.DurationMilliseconds >= options.DurationRegressionRatio)
        {
            signals.Add(Regression(
                key,
                "duration.regressed",
                "Candidate scan duration increased beyond the configured threshold.",
                FormatMilliseconds(baseline.DurationMilliseconds),
                FormatMilliseconds(candidate.DurationMilliseconds)));
        }
        else if (candidate.DurationMilliseconds > 0
                 && -delta >= options.DurationRegressionMinimumMilliseconds
                 && baseline.DurationMilliseconds / candidate.DurationMilliseconds >= options.DurationRegressionRatio)
        {
            signals.Add(Improvement(
                key,
                "duration.improved",
                "Candidate scan duration decreased beyond the configured threshold.",
                FormatMilliseconds(baseline.DurationMilliseconds),
                FormatMilliseconds(candidate.DurationMilliseconds)));
        }
    }

    private static void AddZeroedDetectorSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        ICollection<BatchScanComparisonSignal> signals)
    {
        AddZeroedDetectorSignal(key, "pages", baseline.Counts.Pages, candidate.Counts.Pages, signals);
        AddZeroedDetectorSignal(key, "walls", baseline.Counts.Walls, candidate.Counts.Walls, signals);
        AddZeroedDetectorSignal(key, "rooms", baseline.Counts.Rooms, candidate.Counts.Rooms, signals);
        AddZeroedDetectorSignal(key, "openings", baseline.Counts.Openings, candidate.Counts.Openings, signals);
    }

    private static void AddZeroedDetectorSignal(
        string key,
        string detector,
        int baselineCount,
        int candidateCount,
        ICollection<BatchScanComparisonSignal> signals)
    {
        if (baselineCount > 0 && candidateCount == 0)
        {
            signals.Add(Regression(
                key,
                $"counts.{detector}_zeroed",
                $"Candidate detector count for {detector} dropped to zero.",
                baselineCount.ToString(CultureInfo.InvariantCulture),
                "0"));
        }
    }

    private static void AddCountDriftSignals(
        string key,
        BatchScanItemResult baseline,
        BatchScanItemResult candidate,
        ICollection<BatchScanComparisonSignal> signals)
    {
        AddCountDriftSignal(key, "walls", baseline.Counts.Walls, candidate.Counts.Walls, signals);
        AddCountDriftSignal(key, "rooms", baseline.Counts.Rooms, candidate.Counts.Rooms, signals);
        AddCountDriftSignal(key, "openings", baseline.Counts.Openings, candidate.Counts.Openings, signals);
        AddCountDriftSignal(key, "objects", baseline.Counts.Objects, candidate.Counts.Objects, signals);
        AddCountDriftSignal(key, "surfacePatterns", baseline.Counts.SurfacePatterns, candidate.Counts.SurfacePatterns, signals);
        AddCountDriftSignal(key, "objectAggregates", baseline.Counts.ObjectAggregates, candidate.Counts.ObjectAggregates, signals);
        AddCountDriftSignal(key, "visualDrawableItems", baseline.VisualSnapshot.DrawableItemCount, candidate.VisualSnapshot.DrawableItemCount, signals);
    }

    private static void AddCountDriftSignal(
        string key,
        string metric,
        int baselineCount,
        int candidateCount,
        ICollection<BatchScanComparisonSignal> signals)
    {
        var absoluteDelta = Math.Abs(candidateCount - baselineCount);
        var denominator = Math.Max(baselineCount, 1);
        var ratio = absoluteDelta / (double)denominator;
        if (absoluteDelta < 5 || ratio < 0.25)
        {
            return;
        }

        signals.Add(Info(
            key,
            $"counts.{metric}_changed",
            $"Candidate {metric} count changed significantly; review visually before treating this as better or worse.",
            baselineCount.ToString(CultureInfo.InvariantCulture),
            candidateCount.ToString(CultureInfo.InvariantCulture)));
    }

    private static IReadOnlyList<BatchScanMetricDelta> CreateMetricDeltas(
        BatchScanItemResult baseline,
        BatchScanItemResult candidate)
    {
        var deltas = new List<BatchScanMetricDelta>
        {
            CountDelta("pages", baseline.Counts.Pages, candidate.Counts.Pages),
            CountDelta("regions", baseline.Counts.Regions, candidate.Counts.Regions),
            CountDelta("titleBlocks", baseline.Counts.TitleBlocks, candidate.Counts.TitleBlocks),
            CountDelta("dimensions", baseline.Counts.Dimensions, candidate.Counts.Dimensions),
            CountDelta("annotations", baseline.Counts.Annotations, candidate.Counts.Annotations),
            CountDelta("gridAxes", baseline.Counts.GridAxes, candidate.Counts.GridAxes),
            CountDelta("gridBaySpacings", baseline.Counts.GridBaySpacings, candidate.Counts.GridBaySpacings),
            CountDelta("surfacePatterns", baseline.Counts.SurfacePatterns, candidate.Counts.SurfacePatterns),
            CountDelta("walls", baseline.Counts.Walls, candidate.Counts.Walls),
            CountDelta("wallNodes", baseline.Counts.WallNodes, candidate.Counts.WallNodes),
            CountDelta("wallEdges", baseline.Counts.WallEdges, candidate.Counts.WallEdges),
            CountDelta("rooms", baseline.Counts.Rooms, candidate.Counts.Rooms),
            CountDelta("roomAdjacencies", baseline.Counts.RoomAdjacencies, candidate.Counts.RoomAdjacencies),
            CountDelta("roomClusters", baseline.Counts.RoomClusters, candidate.Counts.RoomClusters),
            CountDelta("openings", baseline.Counts.Openings, candidate.Counts.Openings),
            CountDelta("objects", baseline.Counts.Objects, candidate.Counts.Objects),
            CountDelta("objectGroups", baseline.Counts.ObjectGroups, candidate.Counts.ObjectGroups),
            CountDelta("objectAggregates", baseline.Counts.ObjectAggregates, candidate.Counts.ObjectAggregates),
            CountDelta("routingItems", baseline.Counts.RoutingItems, candidate.Counts.RoutingItems),
            CountDelta("diagnostics", baseline.Counts.Diagnostics, candidate.Counts.Diagnostics),
            CountDelta("diagnosticWarnings", baseline.Counts.DiagnosticWarnings, candidate.Counts.DiagnosticWarnings),
            CountDelta("diagnosticErrors", baseline.Counts.DiagnosticErrors, candidate.Counts.DiagnosticErrors),
            NumberDelta("qualityConfidence", baseline.Counts.QualityConfidence, candidate.Counts.QualityConfidence, "ratio"),
            CountDelta("visualLayers", baseline.VisualSnapshot.LayerCount, candidate.VisualSnapshot.LayerCount),
            CountDelta("visualDrawableItems", baseline.VisualSnapshot.DrawableItemCount, candidate.VisualSnapshot.DrawableItemCount),
            CountDelta("visualIssues", baseline.VisualSnapshot.IssueCount, candidate.VisualSnapshot.IssueCount),
            CountDelta("visualWarningIssues", baseline.VisualSnapshot.WarningIssueCount, candidate.VisualSnapshot.WarningIssueCount),
            CountDelta("visualErrorIssues", baseline.VisualSnapshot.ErrorIssueCount, candidate.VisualSnapshot.ErrorIssueCount),
            NumberDelta("visualMaxDetectionCoverage", baseline.VisualSnapshot.MaxDetectionCoverage, candidate.VisualSnapshot.MaxDetectionCoverage, "ratio"),
            NumberDelta("durationMilliseconds", baseline.DurationMilliseconds, candidate.DurationMilliseconds, "ms")
        };

        return deltas;
    }

    private static BatchScanMetricDelta CountDelta(string name, int baseline, int candidate) =>
        NumberDelta(name, baseline, candidate, "count");

    private static BatchScanMetricDelta NumberDelta(string name, double baseline, double candidate, string unit) =>
        new(name, baseline, candidate, candidate - baseline, unit);

    private static int StatusRank(BatchScanItemStatus status) =>
        status switch
        {
            BatchScanItemStatus.Succeeded => 4,
            BatchScanItemStatus.CompletedWithErrors => 3,
            BatchScanItemStatus.Failed => 1,
            BatchScanItemStatus.Missing or BatchScanItemStatus.Unsupported => 0,
            _ => 0
        };

    private static double AverageQuality(IReadOnlyList<BatchScanItemResult> items) =>
        items.Count == 0 ? 0 : items.Average(item => item.Counts.QualityConfidence);

    private static string? NormalizeKey(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToUpperInvariant();

    private static string? NormalizePathKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).Trim().ToUpperInvariant();
        }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            return path.Trim().ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return path.Trim().ToUpperInvariant();
        }
        catch (NotSupportedException)
        {
            return path.Trim().ToUpperInvariant();
        }
    }

    private static BatchScanComparisonSignal Info(
        string key,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(key, code, BatchScanComparisonSignalSeverity.Info, message, baseline, candidate);

    private static BatchScanComparisonSignal Improvement(
        string key,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(key, code, BatchScanComparisonSignalSeverity.Improvement, message, baseline, candidate);

    private static BatchScanComparisonSignal Regression(
        string key,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(key, code, BatchScanComparisonSignalSeverity.Regression, message, baseline, candidate);

    private static string FormatRatio(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatMilliseconds(double value) =>
        $"{value.ToString("0.##", CultureInfo.InvariantCulture)} ms";
}

internal sealed record BatchScanItemComparison(
    string Key,
    BatchScanComparisonItemStatus Status,
    string? BaselineInputPath,
    string? CandidateInputPath,
    string? BaselineFileName,
    string? CandidateFileName,
    string? BaselineScanJsonPath,
    string? CandidateScanJsonPath,
    string? BaselineVisualSnapshotPath,
    string? CandidateVisualSnapshotPath,
    string? BaselineGeoJsonPath,
    string? CandidateGeoJsonPath,
    string? BaselinePlacementJsonPath,
    string? CandidatePlacementJsonPath,
    string? BaselineOverlayDirectory,
    string? CandidateOverlayDirectory,
    BatchScanItemStatus? BaselineStatus,
    BatchScanItemStatus? CandidateStatus,
    double? BaselineDurationMilliseconds,
    double? CandidateDurationMilliseconds,
    double? DurationDeltaMilliseconds,
    string? BaselineQualityGrade,
    string? CandidateQualityGrade,
    double? BaselineQualityConfidence,
    double? CandidateQualityConfidence,
    int? BaselineDiagnosticErrors,
    int? CandidateDiagnosticErrors,
    int? BaselineVisualIssueCount,
    int? CandidateVisualIssueCount,
    int? BaselineVisualErrorIssueCount,
    int? CandidateVisualErrorIssueCount,
    IReadOnlyList<BatchScanMetricDelta> Deltas,
    IReadOnlyList<string> AddedVisualIssueCodes,
    IReadOnlyList<string> RemovedVisualIssueCodes,
    IReadOnlyList<BatchScanComparisonSignal> Signals);

internal sealed record BatchScanMetricDelta(
    string Name,
    double? Baseline,
    double? Candidate,
    double? Delta,
    string Unit);

internal sealed record BatchScanComparisonSignal(
    string Key,
    string Code,
    BatchScanComparisonSignalSeverity Severity,
    string Message,
    string? Baseline,
    string? Candidate);

internal sealed class BatchScanComparisonOptions
{
    public double QualityConfidenceRegressionThreshold { get; set; } = 0.05;

    public double DurationRegressionRatio { get; set; } = 1.5;

    public double DurationRegressionMinimumMilliseconds { get; set; } = 250;

    public int DiagnosticErrorIncreaseThreshold { get; set; } = 1;

    public int VisualIssueIncreaseThreshold { get; set; } = 1;
}

internal sealed class BatchCompareArguments
{
    public string? BaselinePath { get; set; }

    public string? CandidatePath { get; set; }

    public string? JsonPath { get; set; }

    public string? MarkdownPath { get; set; }

    public bool PrettyJson { get; set; } = true;

    public bool NoFailOnRegression { get; set; }

    public double QualityConfidenceDropThreshold { get; set; } = 0.05;

    public double DurationRegressionRatio { get; set; } = 1.5;

    public double DurationRegressionMinimumMilliseconds { get; set; } = 250;

    public int DiagnosticErrorIncreaseThreshold { get; set; } = 1;

    public int VisualIssueIncreaseThreshold { get; set; } = 1;

    public static BatchCompareArguments Parse(string[] args)
    {
        var parsed = new BatchCompareArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--markdown":
                    parsed.MarkdownPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--no-fail-on-regression":
                    parsed.NoFailOnRegression = true;
                    break;
                case "--quality-confidence-drop":
                    parsed.QualityConfidenceDropThreshold = ReadDouble(args, ref index, arg);
                    break;
                case "--duration-ratio":
                    parsed.DurationRegressionRatio = ReadDouble(args, ref index, arg);
                    break;
                case "--duration-min-ms":
                    parsed.DurationRegressionMinimumMilliseconds = ReadDouble(args, ref index, arg);
                    break;
                case "--diagnostic-error-increase":
                    parsed.DiagnosticErrorIncreaseThreshold = ReadInt(args, ref index, arg);
                    break;
                case "--visual-issue-increase":
                    parsed.VisualIssueIncreaseThreshold = ReadInt(args, ref index, arg);
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.BaselinePath is null)
                    {
                        parsed.BaselinePath = arg;
                    }
                    else if (parsed.CandidatePath is null)
                    {
                        parsed.CandidatePath = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }
}

internal static class BatchScanComparisonMarkdownReport
{
    public static string Create(BatchScanComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var builder = new StringBuilder();
        builder.AppendLine("# OpenPlanTrace Batch Comparison");
        builder.AppendLine();
        builder.AppendLine($"Generated: {comparison.GeneratedAt:O}");
        builder.AppendLine($"Baseline: {Text(comparison.BaselineOutputDirectory ?? "baseline")}");
        builder.AppendLine($"Candidate: {Text(comparison.CandidateOutputDirectory ?? "candidate")}");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- Status: {(comparison.Passed ? "PASS" : "REGRESSION")}");
        builder.AppendLine($"- Items: {comparison.MatchedItemCount} matched, {comparison.AddedItemCount} added, {comparison.RemovedItemCount} removed");
        builder.AppendLine($"- Signals: {comparison.RegressionCount} regressions, {comparison.ImprovementCount} improvements, {comparison.InfoCount} info");
        builder.AppendLine($"- Diagnostic errors delta: {FormatDelta(comparison.DiagnosticErrorDelta)}");
        builder.AppendLine($"- Visual issues delta: {FormatDelta(comparison.VisualIssueDelta)}");
        builder.AppendLine($"- Average quality-confidence delta: {FormatNumber(comparison.QualityConfidenceAverageDelta)}");
        builder.AppendLine($"- Total duration delta: {FormatMilliseconds(comparison.TotalDurationDeltaMilliseconds)}");
        builder.AppendLine();

        AppendSignals(builder, comparison);
        AppendItems(builder, comparison);
        return builder.ToString();
    }

    private static void AppendSignals(StringBuilder builder, BatchScanComparisonResult comparison)
    {
        builder.AppendLine("## Signals");
        builder.AppendLine();

        if (comparison.Signals.Count == 0)
        {
            builder.AppendLine("No regression or improvement signals.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("| Severity | Item | Code | Baseline | Candidate | Message |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var signal in comparison.Signals
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Code, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine(
                $"| {Cell(signal.Severity.ToString())} | {Cell(signal.Key)} | `{Cell(signal.Code)}` | {Cell(signal.Baseline ?? "-")} | {Cell(signal.Candidate ?? "-")} | {Cell(signal.Message)} |");
        }

        builder.AppendLine();
    }

    private static void AppendItems(StringBuilder builder, BatchScanComparisonResult comparison)
    {
        builder.AppendLine("## Items");
        builder.AppendLine();
        builder.AppendLine("| Item | Match | Scan Status | Quality | Diagnostics | Visual Issues | Duration | Evidence | Key Deltas |");
        builder.AppendLine("| --- | --- | --- | --- | ---: | ---: | ---: | --- | --- |");

        foreach (var item in comparison.Items)
        {
            builder.AppendLine(
                $"| {Cell(ItemName(item))} | {Cell(item.Status.ToString())} | {Cell(StatusPair(item))} | {Cell(QualityPair(item))} | {Cell(IntPair(item.BaselineDiagnosticErrors, item.CandidateDiagnosticErrors))} | {Cell(IntPair(item.BaselineVisualIssueCount, item.CandidateVisualIssueCount))} | {Cell(DurationPair(item))} | {Cell(EvidencePair(item))} | {Cell(KeyDeltas(item))} |");
        }

        builder.AppendLine();
    }

    private static string ItemName(BatchScanItemComparison item) =>
        item.CandidateFileName
        ?? item.BaselineFileName
        ?? item.CandidateInputPath
        ?? item.BaselineInputPath
        ?? item.Key;

    private static string StatusPair(BatchScanItemComparison item) =>
        $"{item.BaselineStatus?.ToString() ?? "-"} -> {item.CandidateStatus?.ToString() ?? "-"}";

    private static string QualityPair(BatchScanItemComparison item) =>
        $"{item.BaselineQualityGrade ?? "-"} {FormatNullable(item.BaselineQualityConfidence)} -> {item.CandidateQualityGrade ?? "-"} {FormatNullable(item.CandidateQualityConfidence)}";

    private static string IntPair(int? baseline, int? candidate) =>
        $"{baseline?.ToString(CultureInfo.InvariantCulture) ?? "-"} -> {candidate?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    private static string DurationPair(BatchScanItemComparison item) =>
        $"{FormatNullableMilliseconds(item.BaselineDurationMilliseconds)} -> {FormatNullableMilliseconds(item.CandidateDurationMilliseconds)}";

    private static string EvidencePair(BatchScanItemComparison item) =>
        $"{EvidenceSummary(item.BaselineScanJsonPath, item.BaselineVisualSnapshotPath, item.BaselineGeoJsonPath, item.BaselinePlacementJsonPath, item.BaselineOverlayDirectory)} -> {EvidenceSummary(item.CandidateScanJsonPath, item.CandidateVisualSnapshotPath, item.CandidateGeoJsonPath, item.CandidatePlacementJsonPath, item.CandidateOverlayDirectory)}";

    private static string EvidenceSummary(
        string? scanJsonPath,
        string? visualSnapshotPath,
        string? geoJsonPath,
        string? placementJsonPath,
        string? overlayDirectory)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(scanJsonPath))
        {
            parts.Add("scan");
        }

        if (!string.IsNullOrWhiteSpace(visualSnapshotPath))
        {
            parts.Add("visual");
        }

        if (!string.IsNullOrWhiteSpace(geoJsonPath))
        {
            parts.Add("geojson");
        }

        if (!string.IsNullOrWhiteSpace(placementJsonPath))
        {
            parts.Add("placement");
        }

        if (!string.IsNullOrWhiteSpace(overlayDirectory))
        {
            parts.Add("svg");
        }

        return parts.Count == 0 ? "-" : string.Join("+", parts);
    }

    private static string KeyDeltas(BatchScanItemComparison item)
    {
        var keys = new[] { "walls", "rooms", "openings", "objects", "objectAggregates", "diagnosticErrors", "visualIssues", "qualityConfidence" };
        var selected = item.Deltas
            .Where(delta => keys.Contains(delta.Name, StringComparer.Ordinal))
            .Where(delta => delta.Delta is not null && Math.Abs(delta.Delta.Value) > 0.000001)
            .Select(delta => $"{delta.Name} {FormatDelta(delta.Delta)}")
            .ToArray();

        return selected.Length == 0 ? "-" : string.Join(", ", selected);
    }

    private static string FormatDelta(int value) =>
        value >= 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
            : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatDelta(double? value)
    {
        if (value is null)
        {
            return "-";
        }

        return value.Value >= 0
            ? $"+{FormatNumber(value.Value)}"
            : FormatNumber(value.Value);
    }

    private static string FormatNullable(double? value) =>
        value is null ? "-" : FormatNumber(value.Value);

    private static string FormatNullableMilliseconds(double? value) =>
        value is null ? "-" : FormatMilliseconds(value.Value);

    private static string FormatMilliseconds(double value) =>
        $"{FormatNumber(value)} ms";

    private static string FormatNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Cell(string value) =>
        Text(value).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Text(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
