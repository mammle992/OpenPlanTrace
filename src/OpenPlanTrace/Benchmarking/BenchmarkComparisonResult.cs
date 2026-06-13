using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public enum BenchmarkComparisonCaseStatus
{
    Matched = 0,
    Added,
    Removed
}

public enum BenchmarkComparisonSignalSeverity
{
    Info = 0,
    Improvement,
    Regression
}

public sealed record BenchmarkComparisonResult(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string? BaselineName,
    string? CandidateName,
    int BaselineCaseCount,
    int CandidateCaseCount,
    int MatchedCaseCount,
    int AddedCaseCount,
    int RemovedCaseCount,
    int RegressionCount,
    int ImprovementCount,
    IReadOnlyList<BenchmarkCaseComparison> Cases,
    IReadOnlyList<BenchmarkComparisonSignal> Signals)
{
    public const string CurrentSchemaVersion = "openplantrace.benchmark-comparison.v1";

    public bool Passed => RegressionCount == 0;

    public static BenchmarkComparisonResult Compare(
        BenchmarkRunResult baseline,
        BenchmarkRunResult candidate,
        BenchmarkComparisonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        options ??= new BenchmarkComparisonOptions();

        var baselineById = baseline.Cases.ToDictionary(item => item.FixtureId, StringComparer.OrdinalIgnoreCase);
        var candidateById = candidate.Cases.ToDictionary(item => item.FixtureId, StringComparer.OrdinalIgnoreCase);
        var allIds = baselineById.Keys
            .Concat(candidateById.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cases = new List<BenchmarkCaseComparison>();

        foreach (var id in allIds)
        {
            baselineById.TryGetValue(id, out var baselineCase);
            candidateById.TryGetValue(id, out var candidateCase);
            cases.Add(CompareCase(id, baselineCase, candidateCase, options));
        }

        var signals = cases.SelectMany(item => item.Signals).ToList();
        AddScoreboardSignals(baseline, candidate, options, signals);
        return new BenchmarkComparisonResult(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            baseline.Name,
            candidate.Name,
            baseline.CaseCount,
            candidate.CaseCount,
            cases.Count(item => item.Status == BenchmarkComparisonCaseStatus.Matched),
            cases.Count(item => item.Status == BenchmarkComparisonCaseStatus.Added),
            cases.Count(item => item.Status == BenchmarkComparisonCaseStatus.Removed),
            signals.Count(item => item.Severity == BenchmarkComparisonSignalSeverity.Regression),
            signals.Count(item => item.Severity == BenchmarkComparisonSignalSeverity.Improvement),
            cases,
            signals);
    }

    private static BenchmarkCaseComparison CompareCase(
        string fixtureId,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate,
        BenchmarkComparisonOptions options)
    {
        if (baseline is null)
        {
            var signals = new[]
            {
                new BenchmarkComparisonSignal(
                    fixtureId,
                    "case.added",
                    BenchmarkComparisonSignalSeverity.Info,
                    "Case exists only in the candidate benchmark run.",
                    null,
                    candidate?.FixtureId)
            };

            return new BenchmarkCaseComparison(
                fixtureId,
                BenchmarkComparisonCaseStatus.Added,
                null,
                candidate?.FixtureName,
                null,
                candidate?.Passed,
                null,
                candidate?.DurationMilliseconds,
                null,
                null,
                candidate?.Counts.QualityGrade,
                null,
                candidate?.Counts.QualityConfidence,
                null,
                candidate?.PassedAssertionCount,
                null,
                candidate?.FailedAssertionCount,
                CreateDeltas(null, candidate),
                signals)
            {
                CandidateSkipped = candidate?.Skipped ?? false,
                CandidateSkipReason = candidate?.SkipReason
            };
        }

        if (candidate is null)
        {
            var signals = new[]
            {
                new BenchmarkComparisonSignal(
                    fixtureId,
                    "case.removed",
                    BenchmarkComparisonSignalSeverity.Regression,
                    "Case was present in the baseline benchmark run but is missing from the candidate run.",
                    baseline.FixtureId,
                    null)
            };

            return new BenchmarkCaseComparison(
                fixtureId,
                BenchmarkComparisonCaseStatus.Removed,
                baseline.FixtureName,
                null,
                baseline.Passed,
                null,
                baseline.DurationMilliseconds,
                null,
                null,
                baseline.Counts.QualityGrade,
                null,
                baseline.Counts.QualityConfidence,
                null,
                baseline.PassedAssertionCount,
                null,
                baseline.FailedAssertionCount,
                null,
                CreateDeltas(baseline, null),
                signals)
            {
                BaselineSkipped = baseline.Skipped,
                BaselineSkipReason = baseline.SkipReason
            };
        }

        var caseSignals = new List<BenchmarkComparisonSignal>();
        if (baseline.Skipped || candidate.Skipped)
        {
            AddSkipSignals(fixtureId, baseline, candidate, caseSignals);
        }
        else
        {
            AddPassSignals(fixtureId, baseline, candidate, caseSignals);
            AddAssertionSignals(fixtureId, baseline, candidate, caseSignals);
            AddQualitySignals(fixtureId, baseline, candidate, options, caseSignals);
            AddScanReviewQueueSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddMeasurementSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddImportReadinessSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddDetectorMetricSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddDiagnosticSignals(fixtureId, baseline, candidate, caseSignals);
            AddDurationSignals(fixtureId, baseline, candidate, options, caseSignals);
        }

        var durationDelta = candidate.DurationMilliseconds - baseline.DurationMilliseconds;

        return new BenchmarkCaseComparison(
            fixtureId,
            BenchmarkComparisonCaseStatus.Matched,
            baseline.FixtureName,
            candidate.FixtureName,
            baseline.Passed,
            candidate.Passed,
            baseline.DurationMilliseconds,
            candidate.DurationMilliseconds,
            durationDelta,
            baseline.Counts.QualityGrade,
            candidate.Counts.QualityGrade,
            baseline.Counts.QualityConfidence,
            candidate.Counts.QualityConfidence,
            baseline.PassedAssertionCount,
            candidate.PassedAssertionCount,
            baseline.FailedAssertionCount,
            candidate.FailedAssertionCount,
            CreateDeltas(baseline, candidate),
            caseSignals)
        {
            BaselineSkipped = baseline.Skipped,
            CandidateSkipped = candidate.Skipped,
            BaselineSkipReason = baseline.SkipReason,
            CandidateSkipReason = candidate.SkipReason
        };
    }

    private static void AddSkipSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (!baseline.Skipped && candidate.Skipped)
        {
            signals.Add(new BenchmarkComparisonSignal(
                fixtureId,
                "case.skipped",
                BenchmarkComparisonSignalSeverity.Info,
                "Candidate case was skipped.",
                baseline.Passed ? "scanned/pass" : "scanned/fail",
                candidate.SkipReason ?? "skipped"));
        }
        else if (baseline.Skipped && !candidate.Skipped)
        {
            signals.Add(new BenchmarkComparisonSignal(
                fixtureId,
                "case.unskipped",
                BenchmarkComparisonSignalSeverity.Info,
                "Candidate case was available after being skipped in the baseline run.",
                baseline.SkipReason ?? "skipped",
                candidate.Passed ? "scanned/pass" : "scanned/fail"));
        }
        else
        {
            signals.Add(new BenchmarkComparisonSignal(
                fixtureId,
                "case.skipped",
                BenchmarkComparisonSignalSeverity.Info,
                "Both benchmark runs skipped this case.",
                baseline.SkipReason ?? "skipped",
                candidate.SkipReason ?? "skipped"));
        }
    }

    private static void AddPassSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline.Passed && !candidate.Passed)
        {
            signals.Add(Regression(fixtureId, "case.failed", "Candidate case failed after passing in the baseline run.", "passed", "failed"));
        }
        else if (!baseline.Passed && candidate.Passed)
        {
            signals.Add(Improvement(fixtureId, "case.recovered", "Candidate case passed after failing in the baseline run.", "failed", "passed"));
        }
    }

    private static void AddAssertionSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        AddIntegerSignal(
            fixtureId,
            "assertions.failed",
            baseline.FailedAssertionCount,
            candidate.FailedAssertionCount,
            "Candidate has more failed assertions.",
            "Candidate has fewer failed assertions.",
            signals);
    }

    private static void AddQualitySignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if ((int)candidate.Counts.QualityGrade < (int)baseline.Counts.QualityGrade)
        {
            signals.Add(Regression(
                fixtureId,
                "quality.grade",
                "Candidate scan quality grade dropped.",
                baseline.Counts.QualityGrade.ToString(),
                candidate.Counts.QualityGrade.ToString()));
        }
        else if ((int)candidate.Counts.QualityGrade > (int)baseline.Counts.QualityGrade)
        {
            signals.Add(Improvement(
                fixtureId,
                "quality.grade",
                "Candidate scan quality grade improved.",
                baseline.Counts.QualityGrade.ToString(),
                candidate.Counts.QualityGrade.ToString()));
        }

        if (!baseline.Counts.QualityRequiresReview && candidate.Counts.QualityRequiresReview)
        {
            signals.Add(Regression(
                fixtureId,
                "quality.review_required",
                "Candidate scan quality now requires review.",
                "review not required",
                "review required"));
        }
        else if (baseline.Counts.QualityRequiresReview && !candidate.Counts.QualityRequiresReview)
        {
            signals.Add(Improvement(
                fixtureId,
                "quality.review_required",
                "Candidate scan quality no longer requires review.",
                "review required",
                "review not required"));
        }

        var confidenceDelta = candidate.Counts.QualityConfidence - baseline.Counts.QualityConfidence;
        if (confidenceDelta <= -options.QualityConfidenceRegressionThreshold)
        {
            signals.Add(Regression(
                fixtureId,
                "quality.confidence",
                "Candidate scan quality confidence dropped beyond the configured threshold.",
                Format(baseline.Counts.QualityConfidence),
                Format(candidate.Counts.QualityConfidence)));
        }
        else if (confidenceDelta >= options.QualityConfidenceRegressionThreshold)
        {
            signals.Add(Improvement(
                fixtureId,
                "quality.confidence",
                "Candidate scan quality confidence improved beyond the configured threshold.",
                Format(baseline.Counts.QualityConfidence),
                Format(candidate.Counts.QualityConfidence)));
        }

        AddIntegerSignal(
            fixtureId,
            "quality.issues",
            baseline.Counts.QualityIssues,
            candidate.Counts.QualityIssues,
            "Candidate has more scan-quality issues.",
            "Candidate has fewer scan-quality issues.",
            signals);
    }

    private static void AddDiagnosticSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        AddIntegerSignal(
            fixtureId,
            "diagnostics.errors",
            baseline.Counts.DiagnosticErrors,
            candidate.Counts.DiagnosticErrors,
            "Candidate has more diagnostic errors.",
            "Candidate has fewer diagnostic errors.",
            signals);
    }

    private static void AddDetectorMetricSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineByDetector = baseline.Metrics.ToDictionary(metric => metric.Detector, StringComparer.OrdinalIgnoreCase);
        var candidateByDetector = candidate.Metrics.ToDictionary(metric => metric.Detector, StringComparer.OrdinalIgnoreCase);
        var detectorNames = baselineByDetector.Keys
            .Concat(candidateByDetector.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var detector in detectorNames)
        {
            baselineByDetector.TryGetValue(detector, out var baselineMetric);
            candidateByDetector.TryGetValue(detector, out var candidateMetric);
            var codeDetector = DetectorCode(detector);

            if (baselineMetric is null)
            {
                signals.Add(new BenchmarkComparisonSignal(
                    fixtureId,
                    $"detector_metric.{codeDetector}.added",
                    BenchmarkComparisonSignalSeverity.Info,
                    "Candidate benchmark run has a detector metric that was not present in the baseline run.",
                    null,
                    DetectorMetricSummary(candidateMetric!)));
                continue;
            }

            if (candidateMetric is null)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"detector_metric.{codeDetector}.removed",
                    "Candidate benchmark run is missing a detector metric that was present in the baseline run.",
                    DetectorMetricSummary(baselineMetric),
                    null));
                continue;
            }

            if (baselineMetric.ExpectedCount != candidateMetric.ExpectedCount)
            {
                signals.Add(new BenchmarkComparisonSignal(
                    fixtureId,
                    $"detector_metric.{codeDetector}.expected_count",
                    BenchmarkComparisonSignalSeverity.Info,
                    "Detector metric expected-target count changed between benchmark runs.",
                    baselineMetric.ExpectedCount.ToString(),
                    candidateMetric.ExpectedCount.ToString()));
            }

            AddDetectorRatioSignal(
                fixtureId,
                $"detector_metric.{codeDetector}.recall",
                baselineMetric.Recall,
                candidateMetric.Recall,
                options.DetectorRecallRegressionThreshold,
                "Candidate detector target recall dropped.",
                "Candidate detector target recall improved.",
                DetectorMetricSummary(baselineMetric),
                DetectorMetricSummary(candidateMetric),
                signals);

            if (baselineMetric.PrecisionScoringEnabled != candidateMetric.PrecisionScoringEnabled)
            {
                signals.Add(new BenchmarkComparisonSignal(
                    fixtureId,
                    $"detector_metric.{codeDetector}.precision_policy",
                    BenchmarkComparisonSignalSeverity.Info,
                    "Detector metric precision-scoring policy changed between benchmark runs.",
                    baselineMetric.PrecisionScoringEnabled ? "precision scored" : "spot-check only",
                    candidateMetric.PrecisionScoringEnabled ? "precision scored" : "spot-check only"));
            }

            if (baselineMetric.PrecisionScoringEnabled || candidateMetric.PrecisionScoringEnabled)
            {
                AddDetectorRatioSignal(
                    fixtureId,
                    $"detector_metric.{codeDetector}.precision",
                    baselineMetric.Precision,
                    candidateMetric.Precision,
                    options.DetectorPrecisionRegressionThreshold,
                    "Candidate detector target precision dropped.",
                    "Candidate detector target precision improved.",
                    DetectorMetricSummary(baselineMetric),
                    DetectorMetricSummary(candidateMetric),
                    signals);

                AddDetectorRatioSignal(
                    fixtureId,
                    $"detector_metric.{codeDetector}.f1",
                    baselineMetric.F1,
                    candidateMetric.F1,
                    options.DetectorF1RegressionThreshold,
                    "Candidate detector target F1 score dropped.",
                    "Candidate detector target F1 score improved.",
                    DetectorMetricSummary(baselineMetric),
                    DetectorMetricSummary(candidateMetric),
                    signals);
            }
        }
    }

    private static void AddDetectorRatioSignal(
        string fixtureId,
        string code,
        double baseline,
        double candidate,
        double threshold,
        string regressionMessage,
        string improvementMessage,
        string baselineSummary,
        string candidateSummary,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var delta = candidate - baseline;
        if (delta <= -threshold)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, baselineSummary, candidateSummary));
        }
        else if (delta >= threshold)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, baselineSummary, candidateSummary));
        }
    }

    private static void AddMeasurementSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineOutlierRatio = MeasurementOutlierRatio(baseline.Counts);
        var candidateOutlierRatio = MeasurementOutlierRatio(candidate.Counts);
        if (baselineOutlierRatio is not null && candidateOutlierRatio is not null)
        {
            var delta = candidateOutlierRatio.Value - baselineOutlierRatio.Value;
            if (delta >= options.MeasurementOutlierRatioRegressionThreshold)
            {
                signals.Add(Regression(
                    fixtureId,
                    "measurement.outlier_ratio",
                    "Candidate has a higher matched-dimension outlier ratio.",
                    FormatMeasurementOutlierRatio(baseline.Counts),
                    FormatMeasurementOutlierRatio(candidate.Counts)));
            }
            else if (-delta >= options.MeasurementOutlierRatioRegressionThreshold)
            {
                signals.Add(Improvement(
                    fixtureId,
                    "measurement.outlier_ratio",
                    "Candidate has a lower matched-dimension outlier ratio.",
                    FormatMeasurementOutlierRatio(baseline.Counts),
                    FormatMeasurementOutlierRatio(candidate.Counts)));
            }
        }

        var baselineSpread = baseline.Counts.MeasurementScaleSpreadRatio;
        var candidateSpread = candidate.Counts.MeasurementScaleSpreadRatio;
        if (baselineSpread is > 0 && candidateSpread is > 0)
        {
            var delta = candidateSpread.Value - baselineSpread.Value;
            var ratio = candidateSpread.Value / baselineSpread.Value;
            if (delta >= options.MeasurementSpreadRegressionMinimumDelta &&
                ratio >= options.MeasurementSpreadRegressionRatio)
            {
                signals.Add(Regression(
                    fixtureId,
                    "measurement.spread",
                    "Candidate matched dimensions have a wider implied-scale spread.",
                    FormatRatio(baselineSpread.Value),
                    FormatRatio(candidateSpread.Value)));
            }
            else if (-delta >= options.MeasurementSpreadRegressionMinimumDelta &&
                     ratio <= 1.0 / options.MeasurementSpreadRegressionRatio)
            {
                signals.Add(Improvement(
                    fixtureId,
                    "measurement.spread",
                    "Candidate matched dimensions have a tighter implied-scale spread.",
                    FormatRatio(baselineSpread.Value),
                    FormatRatio(candidateSpread.Value)));
            }
        }
    }

    private static void AddScanReviewQueueSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        AddReviewCountSignal(
            fixtureId,
            "scan_review_queue.items",
            baseline.ScanReviewQueue.Count,
            candidate.ScanReviewQueue.Count,
            options.ScanReviewQueueItemRegressionMinimumDelta,
            options.ScanReviewQueueItemRegressionRatio,
            "Candidate scanner produced substantially more scan review queue items.",
            "Candidate scanner produced substantially fewer scan review queue items.",
            ScanReviewQueueSummaryText(baseline.ScanReviewQueue),
            ScanReviewQueueSummaryText(candidate.ScanReviewQueue),
            signals);

        foreach (var kind in baseline.ScanReviewQueue.KindCounts.Keys
                     .Concat(candidate.ScanReviewQueue.KindCounts.Keys)
                     .Where(kind => !string.IsNullOrWhiteSpace(kind))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var baselineCount = ReviewKindCount(baseline.ScanReviewQueue, kind);
            var candidateCount = ReviewKindCount(candidate.ScanReviewQueue, kind);
            AddReviewCountSignal(
                fixtureId,
                $"scan_review_queue.kind.{DetectorCode(kind)}",
                baselineCount,
                candidateCount,
                options.ScanReviewQueueKindRegressionMinimumDelta,
                options.ScanReviewQueueKindRegressionRatio,
                $"Candidate scanner produced substantially more {kind} review items.",
                $"Candidate scanner produced substantially fewer {kind} review items.",
                ReviewKindSummary(kind, baselineCount),
                ReviewKindSummary(kind, candidateCount),
                signals);
        }
    }

    private static void AddReviewCountSignal(
        string fixtureId,
        string code,
        int baseline,
        int candidate,
        int minimumDelta,
        double ratio,
        string regressionMessage,
        string improvementMessage,
        string baselineSummary,
        string candidateSummary,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (IsMeaningfulIncrease(baseline, candidate, minimumDelta, ratio))
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, baselineSummary, candidateSummary));
        }
        else if (IsMeaningfulDecrease(baseline, candidate, minimumDelta, ratio))
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, baselineSummary, candidateSummary));
        }
    }

    private static bool IsMeaningfulIncrease(int baseline, int candidate, int minimumDelta, double ratio) =>
        candidate - baseline >= Math.Max(1, minimumDelta)
        && (baseline <= 0 || candidate / (double)baseline >= Math.Max(1.0, ratio));

    private static bool IsMeaningfulDecrease(int baseline, int candidate, int minimumDelta, double ratio) =>
        baseline - candidate >= Math.Max(1, minimumDelta)
        && (candidate <= 0 || baseline / (double)candidate >= Math.Max(1.0, ratio));

    private static void AddImportReadinessSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineReadiness = baseline.ImportReadiness;
        var candidateReadiness = candidate.ImportReadiness;
        var baselineRank = (int)PlanImportReadiness.ParseGrade(baselineReadiness.Grade);
        var candidateRank = (int)PlanImportReadiness.ParseGrade(candidateReadiness.Grade);

        if (candidateRank < baselineRank)
        {
            signals.Add(Regression(
                fixtureId,
                "import_readiness.grade",
                "Candidate import-readiness grade dropped.",
                ImportReadinessSummary(baselineReadiness),
                ImportReadinessSummary(candidateReadiness)));
        }
        else if (candidateRank > baselineRank)
        {
            signals.Add(Improvement(
                fixtureId,
                "import_readiness.grade",
                "Candidate import-readiness grade improved.",
                ImportReadinessSummary(baselineReadiness),
                ImportReadinessSummary(candidateReadiness)));
        }

        AddImportReadinessBoolSignal(
            fixtureId,
            "import_readiness.geometry_ready",
            baselineReadiness.ReadyForGeometryImport,
            candidateReadiness.ReadyForGeometryImport,
            "Candidate lost geometry import readiness.",
            "Candidate gained geometry import readiness.",
            signals);
        AddImportReadinessBoolSignal(
            fixtureId,
            "import_readiness.metric_ready",
            baselineReadiness.ReadyForMetricImport,
            candidateReadiness.ReadyForMetricImport,
            "Candidate lost metric import readiness.",
            "Candidate gained metric import readiness.",
            signals);
        AddImportReadinessBoolSignal(
            fixtureId,
            "import_readiness.routing_ready",
            baselineReadiness.ReadyForRoutingImport,
            candidateReadiness.ReadyForRoutingImport,
            "Candidate lost routing import readiness.",
            "Candidate gained routing import readiness.",
            signals);

        if (!baselineReadiness.RequiresReview && candidateReadiness.RequiresReview)
        {
            signals.Add(Regression(
                fixtureId,
                "import_readiness.review_required",
                "Candidate import readiness now requires review.",
                ImportReadinessSummary(baselineReadiness),
                ImportReadinessSummary(candidateReadiness)));
        }
        else if (baselineReadiness.RequiresReview && !candidateReadiness.RequiresReview)
        {
            signals.Add(Improvement(
                fixtureId,
                "import_readiness.review_required",
                "Candidate import readiness no longer requires review.",
                ImportReadinessSummary(baselineReadiness),
                ImportReadinessSummary(candidateReadiness)));
        }

        AddScoreSignal(
            fixtureId,
            "import_readiness.score",
            baselineReadiness.Score,
            candidateReadiness.Score,
            options.ImportReadinessScoreRegressionThreshold,
            "Candidate import-readiness score dropped.",
            "Candidate import-readiness score improved.",
            signals);
    }

    private static void AddImportReadinessBoolSignal(
        string fixtureId,
        string code,
        bool baseline,
        bool candidate,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline && !candidate)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, "ready", "not ready"));
        }
        else if (!baseline && candidate)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, "not ready", "ready"));
        }
    }

    private static void AddDurationSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline.DurationMilliseconds <= 0)
        {
            return;
        }

        var delta = candidate.DurationMilliseconds - baseline.DurationMilliseconds;
        var ratio = candidate.DurationMilliseconds / baseline.DurationMilliseconds;
        if (delta >= options.DurationRegressionMinimumMilliseconds && ratio >= options.DurationRegressionRatio)
        {
            signals.Add(Regression(
                fixtureId,
                "duration",
                "Candidate scan duration increased beyond the configured threshold.",
                FormatMilliseconds(baseline.DurationMilliseconds),
                FormatMilliseconds(candidate.DurationMilliseconds)));
        }
        else if (-delta >= options.DurationRegressionMinimumMilliseconds && ratio <= 1.0 / options.DurationRegressionRatio)
        {
            signals.Add(Improvement(
                fixtureId,
                "duration",
                "Candidate scan duration improved beyond the configured threshold.",
                FormatMilliseconds(baseline.DurationMilliseconds),
                FormatMilliseconds(candidate.DurationMilliseconds)));
        }
    }

    private static void AddScoreboardSignals(
        BenchmarkRunResult baseline,
        BenchmarkRunResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineScoreboard = ScoreboardFor(baseline);
        var candidateScoreboard = ScoreboardFor(candidate);
        const string suite = "suite";

        if (baselineScoreboard.ScoredCaseCount == 0 || candidateScoreboard.ScoredCaseCount == 0)
        {
            return;
        }

        if ((int)candidateScoreboard.Grade < (int)baselineScoreboard.Grade)
        {
            signals.Add(Regression(
                suite,
                "scoreboard.grade",
                "Candidate benchmark readiness grade dropped.",
                baselineScoreboard.Grade.ToString(),
                candidateScoreboard.Grade.ToString()));
        }
        else if ((int)candidateScoreboard.Grade > (int)baselineScoreboard.Grade)
        {
            signals.Add(Improvement(
                suite,
                "scoreboard.grade",
                "Candidate benchmark readiness grade improved.",
                baselineScoreboard.Grade.ToString(),
                candidateScoreboard.Grade.ToString()));
        }

        if (baselineScoreboard.ReadyForDownstreamUse && !candidateScoreboard.ReadyForDownstreamUse)
        {
            signals.Add(Regression(
                suite,
                "scoreboard.downstream_ready",
                "Candidate is no longer ready for downstream use.",
                "ready",
                "not ready"));
        }
        else if (!baselineScoreboard.ReadyForDownstreamUse && candidateScoreboard.ReadyForDownstreamUse)
        {
            signals.Add(Improvement(
                suite,
                "scoreboard.downstream_ready",
                "Candidate became ready for downstream use.",
                "not ready",
                "ready"));
        }

        AddScoreSignal(
            suite,
            "scoreboard.overall",
            baselineScoreboard.OverallScore,
            candidateScoreboard.OverallScore,
            options.ScoreboardOverallRegressionThreshold,
            "Candidate overall benchmark score dropped.",
            "Candidate overall benchmark score improved.",
            signals);
        AddScoreSignal(
            suite,
            "scoreboard.consumer_readiness",
            baselineScoreboard.ConsumerReadinessScore,
            candidateScoreboard.ConsumerReadinessScore,
            options.ScoreboardConsumerReadinessRegressionThreshold,
            "Candidate consumer-readiness score dropped.",
            "Candidate consumer-readiness score improved.",
            signals);
        AddIntegerSignal(
            suite,
            "scoreboard.missed_targets",
            baselineScoreboard.MissedTargetCount,
            candidateScoreboard.MissedTargetCount,
            "Candidate has more missed benchmark truth targets.",
            "Candidate has fewer missed benchmark truth targets.",
            signals);
        AddIntegerSignal(
            suite,
            "scoreboard.extra_detections",
            baselineScoreboard.ExtraDetectionCount,
            candidateScoreboard.ExtraDetectionCount,
            "Candidate has more unmatched extra detections against truth targets.",
            "Candidate has fewer unmatched extra detections against truth targets.",
            signals);
        AddIntegerSignal(
            suite,
            "scoreboard.failed_scans",
            baselineScoreboard.FailedScanCount,
            candidateScoreboard.FailedScanCount,
            "Candidate has more failed scans.",
            "Candidate has fewer failed scans.",
            signals);
    }

    private static void AddScoreSignal(
        string fixtureId,
        string code,
        double baseline,
        double candidate,
        double threshold,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var delta = candidate - baseline;
        if (delta <= -threshold)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, Format(baseline), Format(candidate)));
        }
        else if (delta >= threshold)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, Format(baseline), Format(candidate)));
        }
    }

    private static void AddIntegerSignal(
        string fixtureId,
        string code,
        int baseline,
        int candidate,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (candidate > baseline)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, baseline.ToString(), candidate.ToString()));
        }
        else if (candidate < baseline)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, baseline.ToString(), candidate.ToString()));
        }
    }

    private static IReadOnlyList<BenchmarkCountDelta> CreateDeltas(
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var deltas = new List<BenchmarkCountDelta>();
        Add(deltas, "pages", baseline?.Counts.Pages, candidate?.Counts.Pages);
        Add(deltas, "regions", baseline?.Counts.Regions, candidate?.Counts.Regions);
        Add(deltas, "dimensions", baseline?.Counts.Dimensions, candidate?.Counts.Dimensions);
        Add(deltas, "annotations", baseline?.Counts.Annotations, candidate?.Counts.Annotations);
        Add(deltas, "annotationReferences", baseline?.Counts.AnnotationReferences, candidate?.Counts.AnnotationReferences);
        Add(deltas, "gridAxes", baseline?.Counts.GridAxes, candidate?.Counts.GridAxes);
        Add(deltas, "gridBaySpacings", baseline?.Counts.GridBaySpacings, candidate?.Counts.GridBaySpacings);
        Add(deltas, "surfacePatterns", baseline?.Counts.SurfacePatterns, candidate?.Counts.SurfacePatterns);
        Add(deltas, "walls", baseline?.Counts.Walls, candidate?.Counts.Walls);
        Add(deltas, "wallNodes", baseline?.Counts.WallNodes, candidate?.Counts.WallNodes);
        Add(deltas, "wallEdges", baseline?.Counts.WallEdges, candidate?.Counts.WallEdges);
        Add(deltas, "rooms", baseline?.Counts.Rooms, candidate?.Counts.Rooms);
        Add(deltas, "roomAdjacencies", baseline?.Counts.RoomAdjacencies, candidate?.Counts.RoomAdjacencies);
        Add(deltas, "roomClusters", baseline?.Counts.RoomClusters, candidate?.Counts.RoomClusters);
        Add(deltas, "openings", baseline?.Counts.Openings, candidate?.Counts.Openings);
        Add(deltas, "objects", baseline?.Counts.Objects, candidate?.Counts.Objects);
        Add(deltas, "objectGroups", baseline?.Counts.ObjectGroups, candidate?.Counts.ObjectGroups);
        Add(deltas, "objectAggregates", baseline?.Counts.ObjectAggregates, candidate?.Counts.ObjectAggregates);
        Add(deltas, "routingItems", baseline?.Counts.RoutingItems, candidate?.Counts.RoutingItems);
        Add(deltas, "routingSuppressedObjects", baseline?.Counts.RoutingSuppressedObjects, candidate?.Counts.RoutingSuppressedObjects);
        Add(deltas, "diagnostics", baseline?.Counts.Diagnostics, candidate?.Counts.Diagnostics);
        Add(deltas, "diagnosticWarnings", baseline?.Counts.DiagnosticWarnings, candidate?.Counts.DiagnosticWarnings);
        Add(deltas, "diagnosticErrors", baseline?.Counts.DiagnosticErrors, candidate?.Counts.DiagnosticErrors);
        Add(deltas, "qualityIssues", baseline?.Counts.QualityIssues, candidate?.Counts.QualityIssues);
        Add(deltas, "measurementChecked", baseline?.Counts.MeasurementCheckedCount, candidate?.Counts.MeasurementCheckedCount);
        Add(deltas, "measurementConsistent", baseline?.Counts.MeasurementConsistentCount, candidate?.Counts.MeasurementConsistentCount);
        Add(deltas, "measurementOutliers", baseline?.Counts.MeasurementOutlierCount, candidate?.Counts.MeasurementOutlierCount);
        Add(deltas, "scanReviewQueueItems", baseline?.ScanReviewQueue.Count, candidate?.ScanReviewQueue.Count);
        AddReviewKindDeltas(deltas, baseline?.ScanReviewQueue, candidate?.ScanReviewQueue);
        Add(deltas, "passedAssertions", baseline?.PassedAssertionCount, candidate?.PassedAssertionCount);
        Add(deltas, "failedAssertions", baseline?.FailedAssertionCount, candidate?.FailedAssertionCount);
        return deltas;
    }

    private static void AddReviewKindDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        ScanReviewQueueSummary? baseline,
        ScanReviewQueueSummary? candidate)
    {
        var kindNames = (baseline?.KindCounts.Keys ?? Array.Empty<string>())
            .Concat(candidate?.KindCounts.Keys ?? Array.Empty<string>())
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in kindNames)
        {
            Add(
                deltas,
                $"scanReviewQueue.{kind}",
                baseline is null ? null : ReviewKindCount(baseline, kind),
                candidate is null ? null : ReviewKindCount(candidate, kind));
        }
    }

    private static void Add(
        ICollection<BenchmarkCountDelta> deltas,
        string name,
        int? baseline,
        int? candidate)
    {
        int? delta = baseline is null || candidate is null
            ? null
            : candidate.Value - baseline.Value;
        deltas.Add(new BenchmarkCountDelta(name, baseline, candidate, delta));
    }

    private static BenchmarkComparisonSignal Regression(
        string fixtureId,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(fixtureId, code, BenchmarkComparisonSignalSeverity.Regression, message, baseline, candidate);

    private static BenchmarkComparisonSignal Improvement(
        string fixtureId,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(fixtureId, code, BenchmarkComparisonSignalSeverity.Improvement, message, baseline, candidate);

    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatRatio(double value) =>
        $"{Format(value)}x";

    private static string FormatMeasurementOutlierRatio(BenchmarkCounts counts)
    {
        var ratio = MeasurementOutlierRatio(counts);
        return ratio is null
            ? "-"
            : $"{counts.MeasurementOutlierCount}/{counts.MeasurementCheckedCount} ({Format(ratio.Value * 100)}%)";
    }

    private static double? MeasurementOutlierRatio(BenchmarkCounts counts) =>
        counts.MeasurementCheckedCount > 0
            ? (double)counts.MeasurementOutlierCount / counts.MeasurementCheckedCount
            : null;

    private static string DetectorMetricSummary(BenchmarkDetectorMetrics metric) =>
        $"recall {Format(metric.Recall)}, precision {Format(metric.Precision)}, f1 {Format(metric.F1)}"
        + $", matched {metric.MatchedCount}/{metric.ExpectedCount}, detected {metric.DetectedCount}, missed {metric.MissedCount}, extra {metric.ExtraCount}"
        + $", precision scoring {(metric.PrecisionScoringEnabled ? "enabled" : "disabled")}";

    private static int ReviewKindCount(ScanReviewQueueSummary summary, string kind) =>
        summary.KindCounts.TryGetValue(kind, out var count) ? count : 0;

    private static string ReviewKindSummary(string kind, int count) =>
        $"{kind} {count}";

    private static string ScanReviewQueueSummaryText(ScanReviewQueueSummary summary)
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

    private static string ImportReadinessSummary(PlanImportReadiness readiness) =>
        $"{readiness.Grade}, score {Format(readiness.Score)}, geometry {Ready(readiness.ReadyForGeometryImport)}, metric {Ready(readiness.ReadyForMetricImport)}, routing {Ready(readiness.ReadyForRoutingImport)}, review {Ready(readiness.RequiresReview)}";

    private static string Ready(bool value) => value ? "ready" : "not ready";

    private static BenchmarkScoreboard ScoreboardFor(BenchmarkRunResult run) =>
        run.Scoreboard.SchemaVersion == BenchmarkScoreboard.CurrentSchemaVersion
        && run.Scoreboard.CaseCount == run.CaseCount
            ? run.Scoreboard
            : BenchmarkScoreboard.FromCases(run.Cases);

    private static string DetectorCode(string detector) =>
        CleanDetectorCode(detector) is { Length: > 0 } code ? code : "unknown";

    private static string CleanDetectorCode(string detector) =>
        string.IsNullOrWhiteSpace(detector)
            ? string.Empty
            : new string(detector.Trim().Select(character =>
                    char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_').ToArray())
                .Trim('_');

    private static string FormatMilliseconds(double value) =>
        $"{Format(value)} ms";
}

public sealed record BenchmarkCaseComparison(
    string FixtureId,
    BenchmarkComparisonCaseStatus Status,
    string? BaselineName,
    string? CandidateName,
    bool? BaselinePassed,
    bool? CandidatePassed,
    double? BaselineDurationMilliseconds,
    double? CandidateDurationMilliseconds,
    double? DurationDeltaMilliseconds,
    PlanScanQualityGrade? BaselineQualityGrade,
    PlanScanQualityGrade? CandidateQualityGrade,
    double? BaselineQualityConfidence,
    double? CandidateQualityConfidence,
    int? BaselinePassedAssertionCount,
    int? CandidatePassedAssertionCount,
    int? BaselineFailedAssertionCount,
    int? CandidateFailedAssertionCount,
    IReadOnlyList<BenchmarkCountDelta> CountDeltas,
    IReadOnlyList<BenchmarkComparisonSignal> Signals)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BaselineSkipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CandidateSkipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaselineSkipReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateSkipReason { get; init; }
}

public sealed record BenchmarkCountDelta(
    string Name,
    int? Baseline,
    int? Candidate,
    int? Delta);

public sealed record BenchmarkComparisonSignal(
    string FixtureId,
    string Code,
    BenchmarkComparisonSignalSeverity Severity,
    string Message,
    string? Baseline,
    string? Candidate);
