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
            AddWallPlacementSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddDetectorMetricSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddDiagnosticSignals(fixtureId, baseline, candidate, caseSignals);
            AddDurationSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddFinalArtifactInventorySignals(fixtureId, baseline, candidate, options, caseSignals);
            AddArtifactPlanSignals(fixtureId, baseline, candidate, caseSignals);
            AddPipelineHealthSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddPipelinePlanIssueSignals(fixtureId, baseline, candidate, caseSignals);
            AddRerunPlanSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddStageArtifactSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddStageRuntimeReadinessSignals(fixtureId, baseline, candidate, options, caseSignals);
            AddStageContractSignals(fixtureId, baseline, candidate, options, caseSignals);
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

    private static void AddWallPlacementSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineSummary = baseline.WallPlacement;
        var candidateSummary = candidate.WallPlacement;
        var baselineText = WallPlacementSummaryText(baselineSummary);
        var candidateText = WallPlacementSummaryText(candidateSummary);

        AddDecreaseIsRegressionSignal(
            fixtureId,
            "wall_placement.ready_walls",
            baselineSummary.PlacementReadyWallCount,
            candidateSummary.PlacementReadyWallCount,
            options.WallPlacementReadyWallRegressionMinimumDelta,
            "Candidate has fewer placement-ready walls.",
            "Candidate has more placement-ready walls.",
            baselineText,
            candidateText,
            signals);
        AddIncreaseIsRegressionSignal(
            fixtureId,
            "wall_placement.review_walls",
            baselineSummary.PlacementReviewWallCount,
            candidateSummary.PlacementReviewWallCount,
            options.WallPlacementReviewWallRegressionMinimumDelta,
            "Candidate moved more walls into placement review.",
            "Candidate moved fewer walls into placement review.",
            baselineText,
            candidateText,
            signals);
        AddIncreaseIsRegressionSignal(
            fixtureId,
            "wall_placement.isolated_fragments",
            baselineSummary.IsolatedFragmentComponentCount,
            candidateSummary.IsolatedFragmentComponentCount,
            options.WallPlacementFragmentRegressionMinimumDelta,
            "Candidate produced more isolated wall-fragment components.",
            "Candidate produced fewer isolated wall-fragment components.",
            baselineText,
            candidateText,
            signals);
        AddIncreaseIsRegressionSignal(
            fixtureId,
            "wall_placement.topology_blocked_repairs",
            baselineSummary.TopologyImportBlockedRepairCandidateCount,
            candidateSummary.TopologyImportBlockedRepairCandidateCount,
            options.WallPlacementRepairRegressionMinimumDelta,
            "Candidate produced more topology-blocking wall repair candidates.",
            "Candidate produced fewer topology-blocking wall repair candidates.",
            baselineText,
            candidateText,
            signals);
        AddIncreaseIsRegressionSignal(
            fixtureId,
            "wall_placement.high_severity_repairs",
            baselineSummary.HighSeverityRepairCandidateCount,
            candidateSummary.HighSeverityRepairCandidateCount,
            options.WallPlacementRepairRegressionMinimumDelta,
            "Candidate produced more high-severity wall repair candidates.",
            "Candidate produced fewer high-severity wall repair candidates.",
            baselineText,
            candidateText,
            signals);
    }

    private static void AddDecreaseIsRegressionSignal(
        string fixtureId,
        string code,
        int baseline,
        int candidate,
        int minimumDelta,
        string regressionMessage,
        string improvementMessage,
        string baselineSummary,
        string candidateSummary,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var threshold = Math.Max(1, minimumDelta);
        if (baseline - candidate >= threshold)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, baselineSummary, candidateSummary));
        }
        else if (candidate - baseline >= threshold)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, baselineSummary, candidateSummary));
        }
    }

    private static void AddIncreaseIsRegressionSignal(
        string fixtureId,
        string code,
        int baseline,
        int candidate,
        int minimumDelta,
        string regressionMessage,
        string improvementMessage,
        string baselineSummary,
        string candidateSummary,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var threshold = Math.Max(1, minimumDelta);
        if (candidate - baseline >= threshold)
        {
            signals.Add(Regression(fixtureId, code, regressionMessage, baselineSummary, candidateSummary));
        }
        else if (baseline - candidate >= threshold)
        {
            signals.Add(Improvement(fixtureId, code, improvementMessage, baselineSummary, candidateSummary));
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

    private static void AddStageArtifactSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineArtifacts = StageArtifacts(baseline);
        var candidateArtifacts = StageArtifacts(candidate);
        var keys = baselineArtifacts.Keys
            .Concat(candidateArtifacts.Keys)
            .Where(key => IsNoiseSensitiveStageArtifact(key.Artifact))
            .Distinct()
            .OrderBy(key => key.Stage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Artifact.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            baselineArtifacts.TryGetValue(key, out var baselineArtifact);
            candidateArtifacts.TryGetValue(key, out var candidateArtifact);
            AddStageArtifactGrowthSignal(
                fixtureId,
                key,
                "after",
                baselineArtifact?.AfterCount,
                candidateArtifact?.AfterCount,
                options,
                signals);
            AddStageArtifactGrowthSignal(
                fixtureId,
                key,
                "delta",
                baselineArtifact?.Delta,
                candidateArtifact?.Delta,
                options,
                signals);
        }
    }

    private static void AddFinalArtifactInventorySignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineArtifacts = FinalArtifactStates(baseline);
        var candidateArtifacts = FinalArtifactStates(candidate);
        var artifacts = baselineArtifacts.Keys
            .Concat(candidateArtifacts.Keys)
            .Where(IsComparedFinalArtifact)
            .Distinct()
            .OrderBy(artifact => artifact.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var artifact in artifacts)
        {
            baselineArtifacts.TryGetValue(artifact, out var baselineCount);
            candidateArtifacts.TryGetValue(artifact, out var candidateCount);
            AddFinalArtifactInventorySignal(
                fixtureId,
                artifact,
                baselineCount?.Count ?? 0,
                candidateCount?.Count ?? 0,
                options,
                signals);
            AddFinalArtifactStateSignal(
                fixtureId,
                artifact,
                baselineCount,
                candidateCount,
                signals);
        }
    }

    private static void AddFinalArtifactInventorySignal(
        string fixtureId,
        PlanArtifactKind artifact,
        int baseline,
        int candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var delta = candidate - baseline;
        var ratio = baseline > 0
            ? candidate / (double)baseline
            : candidate > 0
                ? double.PositiveInfinity
                : 1.0;
        var codeSuffix = CleanDetectorCode(artifact.ToString());
        var baselineText = FinalArtifactText(artifact, baseline);
        var candidateText = FinalArtifactText(artifact, candidate);

        if (IsCriticalFinalArtifact(artifact))
        {
            if (baseline > 0 && candidate == 0)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_inventory.missing.{codeSuffix}",
                    $"Candidate final artifact inventory lost required downstream artifact {artifact}.",
                    baselineText,
                    candidateText));
                return;
            }

            if (baseline == 0 && candidate > 0)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_inventory.present.{codeSuffix}",
                    $"Candidate final artifact inventory gained downstream artifact {artifact}.",
                    baselineText,
                    candidateText));
                return;
            }

            if (-delta >= options.FinalArtifactRegressionMinimumDelta
                && baseline > 0
                && ratio <= 1.0 / options.FinalArtifactRegressionRatio)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_inventory.shrink.{codeSuffix}",
                    $"Candidate final artifact inventory for {artifact} shrank beyond the configured downstream-data threshold.",
                    baselineText,
                    candidateText));
            }
        }

        if (IsExplosionSensitiveFinalArtifact(artifact)
            && delta >= options.FinalArtifactRegressionMinimumDelta
            && (baseline == 0 || ratio >= options.FinalArtifactRegressionRatio))
        {
            signals.Add(Regression(
                fixtureId,
                $"artifact_inventory.growth.{codeSuffix}",
                $"Candidate final artifact inventory for {artifact} grew beyond the configured noise threshold.",
                baselineText,
                candidateText));
        }
        else if (IsNoiseReducibleFinalArtifact(artifact)
                 && -delta >= options.FinalArtifactRegressionMinimumDelta
                 && baseline > 0
                 && ratio <= 1.0 / options.FinalArtifactRegressionRatio)
        {
            signals.Add(Improvement(
                fixtureId,
                $"artifact_inventory.shrink.{codeSuffix}",
                $"Candidate final artifact inventory for {artifact} shrank beyond the configured noise threshold.",
                baselineText,
                candidateText));
        }
    }

    private static void AddFinalArtifactStateSignal(
        string fixtureId,
        PlanArtifactKind artifact,
        ArtifactStateMetric? baseline,
        ArtifactStateMetric? candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline is null || candidate is null)
        {
            return;
        }

        if (baseline.Count != candidate.Count)
        {
            return;
        }

        if (string.Equals(baseline.StateKey, candidate.StateKey, StringComparison.Ordinal)
            && baseline.Revision == candidate.Revision)
        {
            return;
        }

        signals.Add(Info(
            fixtureId,
            $"artifact_inventory.state_key.{CleanDetectorCode(artifact.ToString())}",
            $"Candidate final artifact inventory state changed for {artifact} while the count stayed the same.",
            FinalArtifactStateText(artifact, baseline),
            FinalArtifactStateText(artifact, candidate)));
    }

    private static void AddArtifactPlanSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselinePlans = ArtifactPlans(baseline);
        var candidatePlans = ArtifactPlans(candidate);
        var artifacts = baselinePlans.Keys
            .Concat(candidatePlans.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var artifact in artifacts)
        {
            baselinePlans.TryGetValue(artifact, out var baselinePlan);
            candidatePlans.TryGetValue(artifact, out var candidatePlan);
            var codeSuffix = CleanDetectorCode(artifact);
            if (baselinePlan is null)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_plan.added.{codeSuffix}",
                    "Candidate exposes a new artifact dependency plan.",
                    null,
                    ArtifactPlanText(candidatePlan)));
                continue;
            }

            if (candidatePlan is null)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_plan.removed.{codeSuffix}",
                    "Candidate is missing an artifact dependency plan that existed in the baseline.",
                    ArtifactPlanText(baselinePlan),
                    null));
                continue;
            }

            var important = IsImportantArtifactPlan(baselinePlan) || IsImportantArtifactPlan(candidatePlan);
            if (baselinePlan.IsProducedByStage && !candidatePlan.IsProducedByStage)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_plan.producer_lost.{codeSuffix}",
                    "Candidate artifact plan lost its stage producer.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }
            else if (!baselinePlan.IsProducedByStage && candidatePlan.IsProducedByStage)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_plan.producer_gained.{codeSuffix}",
                    "Candidate artifact plan gained a stage producer.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }

            if (important && baselinePlan.RequiredConsumerStages.Count > candidatePlan.RequiredConsumerStages.Count)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_plan.required_consumers_lost.{codeSuffix}",
                    "Candidate artifact plan lost required downstream consumers.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }
            else if (important && baselinePlan.RequiredConsumerStages.Count < candidatePlan.RequiredConsumerStages.Count)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_plan.required_consumers_gained.{codeSuffix}",
                    "Candidate artifact plan gained required downstream consumers.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }

            if (important && !baselinePlan.IsTerminalArtifact && candidatePlan.IsTerminalArtifact)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_plan.terminal.{codeSuffix}",
                    "Candidate made an important artifact terminal in the pipeline graph.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }
            else if (important && baselinePlan.IsTerminalArtifact && !candidatePlan.IsTerminalArtifact)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_plan.no_longer_terminal.{codeSuffix}",
                    "Candidate connects a previously terminal artifact to downstream consumers.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }

            if (!baselinePlan.HasMultipleProducers && candidatePlan.HasMultipleProducers)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"artifact_plan.multiple_producers.{codeSuffix}",
                    "Candidate introduced multiple producers for one artifact, which weakens ownership clarity.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }
            else if (baselinePlan.HasMultipleProducers && !candidatePlan.HasMultipleProducers)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"artifact_plan.single_producer.{codeSuffix}",
                    "Candidate resolved multiple producers for one artifact.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }

            if (!string.Equals(baselinePlan.DependencyRole, candidatePlan.DependencyRole, StringComparison.Ordinal))
            {
                signals.Add(Info(
                    fixtureId,
                    $"artifact_plan.role.{codeSuffix}",
                    "Candidate artifact dependency role changed.",
                    ArtifactPlanText(baselinePlan),
                    ArtifactPlanText(candidatePlan)));
            }
        }
    }

    private static void AddStageArtifactGrowthSignal(
        string fixtureId,
        StageArtifactKey key,
        string metric,
        int? baseline,
        int? candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline is null || candidate is null || baseline.Value < 0 || candidate.Value < 0)
        {
            return;
        }

        var delta = candidate.Value - baseline.Value;
        var ratio = baseline.Value > 0
            ? candidate.Value / (double)baseline.Value
            : candidate.Value > 0
                ? double.PositiveInfinity
                : 1.0;
        var code = $"stage_artifact.{metric}.{CleanDetectorCode(key.Stage)}.{CleanDetectorCode(key.Artifact.ToString())}";
        var label = $"{key.Stage}/{key.Artifact} {metric}";

        if (delta >= options.StageArtifactRegressionMinimumDelta
            && (baseline.Value == 0 || ratio >= options.StageArtifactRegressionRatio))
        {
            signals.Add(Regression(
                fixtureId,
                code,
                $"Candidate stage artifact {label} grew beyond the configured noise threshold.",
                StageArtifactMetricText(key, metric, baseline.Value),
                StageArtifactMetricText(key, metric, candidate.Value)));
        }
        else if (-delta >= options.StageArtifactRegressionMinimumDelta
                 && baseline.Value > 0
                 && ratio <= 1.0 / options.StageArtifactRegressionRatio)
        {
            signals.Add(Improvement(
                fixtureId,
                code,
                $"Candidate stage artifact {label} shrank beyond the configured noise threshold.",
                StageArtifactMetricText(key, metric, baseline.Value),
                StageArtifactMetricText(key, metric, candidate.Value)));
        }
    }

    private static void AddStageRuntimeReadinessSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineReadiness = StageRuntimeReadiness(baseline);
        var candidateReadiness = StageRuntimeReadiness(candidate);
        var stages = baselineReadiness.Keys
            .Concat(candidateReadiness.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var stage in stages)
        {
            baselineReadiness.TryGetValue(stage, out var baselineStage);
            candidateReadiness.TryGetValue(stage, out var candidateStage);
            AddStageRuntimeReadinessSignal(
                fixtureId,
                stage,
                "empty_required_reads",
                baselineStage?.EmptyRequiredReadCount,
                candidateStage?.EmptyRequiredReadCount,
                options.StageRuntimeEmptyRequiredReadRegressionMinimumDelta,
                "Candidate stage has more empty required runtime reads.",
                "Candidate stage has fewer empty required runtime reads.",
                signals);
            AddStageRuntimeReadinessSignal(
                fixtureId,
                stage,
                "empty_optional_reads",
                baselineStage?.EmptyOptionalReadCount,
                candidateStage?.EmptyOptionalReadCount,
                options.StageRuntimeEmptyOptionalReadRegressionMinimumDelta,
                "Candidate stage has more empty optional runtime reads.",
                "Candidate stage has fewer empty optional runtime reads.",
                signals);
        }
    }

    private static void AddPipelineHealthSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineHealth = PipelineHealth(baseline);
        var candidateHealth = PipelineHealth(candidate);
        if (baselineHealth is null || candidateHealth is null)
        {
            return;
        }

        AddPipelineHealthSignal(
            fixtureId,
            "not_dependency_ready_stages",
            baselineHealth.NotDependencyReadyStageCount,
            candidateHealth.NotDependencyReadyStageCount,
            1,
            "Candidate has more dependency-not-ready pipeline stages.",
            "Candidate has fewer dependency-not-ready pipeline stages.",
            signals);
        AddPipelineHealthSignal(
            fixtureId,
            "empty_required_runtime_reads",
            baselineHealth.EmptyRequiredReadCount,
            candidateHealth.EmptyRequiredReadCount,
            options.StageRuntimeEmptyRequiredReadRegressionMinimumDelta,
            "Candidate has more empty required runtime reads across the pipeline.",
            "Candidate has fewer empty required runtime reads across the pipeline.",
            signals);
        AddPipelineHealthSignal(
            fixtureId,
            "empty_optional_runtime_reads",
            baselineHealth.EmptyOptionalReadCount,
            candidateHealth.EmptyOptionalReadCount,
            options.StageRuntimeEmptyOptionalReadRegressionMinimumDelta,
            "Candidate has more empty optional runtime reads across the pipeline.",
            "Candidate has fewer empty optional runtime reads across the pipeline.",
            signals);
        AddPipelineHealthSignal(
            fixtureId,
            "contract_violation_stages",
            baselineHealth.ContractViolationStageCount,
            candidateHealth.ContractViolationStageCount,
            1,
            "Candidate has more stages changing undeclared artifacts.",
            "Candidate has fewer stages changing undeclared artifacts.",
            signals);
        AddPipelineHealthSignal(
            fixtureId,
            "undeclared_changed_artifacts",
            baselineHealth.UndeclaredChangedArtifactCount,
            candidateHealth.UndeclaredChangedArtifactCount,
            options.StageContractUndeclaredChangeRegressionMinimumDelta,
            "Candidate has more undeclared changed artifacts across the pipeline.",
            "Candidate has fewer undeclared changed artifacts across the pipeline.",
            signals);
        AddPipelineHealthSignal(
            fixtureId,
            "empty_declared_outputs",
            baselineHealth.EmptyDeclaredOutputCount,
            candidateHealth.EmptyDeclaredOutputCount,
            options.StageContractEmptyDeclaredOutputRegressionMinimumDelta,
            "Candidate has more empty declared stage outputs across the pipeline.",
            "Candidate has fewer empty declared stage outputs across the pipeline.",
            signals);
    }

    private static void AddPipelineHealthSignal(
        string fixtureId,
        string metric,
        int baseline,
        int candidate,
        int threshold,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var effectiveThreshold = Math.Max(1, threshold);
        var delta = candidate - baseline;
        var code = $"pipeline_health.{metric}";
        if (delta >= effectiveThreshold)
        {
            signals.Add(Regression(
                fixtureId,
                code,
                regressionMessage,
                PipelineHealthText(metric, baseline),
                PipelineHealthText(metric, candidate)));
        }
        else if (-delta >= effectiveThreshold)
        {
            signals.Add(Improvement(
                fixtureId,
                code,
                improvementMessage,
                PipelineHealthText(metric, baseline),
                PipelineHealthText(metric, candidate)));
        }
    }

    private static void AddPipelinePlanIssueSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineIssues = PipelinePlanIssues(baseline);
        var candidateIssues = PipelinePlanIssues(candidate);
        var keys = baselineIssues.Keys
            .Concat(candidateIssues.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var key in keys)
        {
            baselineIssues.TryGetValue(key, out var baselineIssue);
            candidateIssues.TryGetValue(key, out var candidateIssue);
            var codeSuffix = CleanDetectorCode(key);
            if (baselineIssue is null)
            {
                signals.Add(new BenchmarkComparisonSignal(
                    fixtureId,
                    $"pipeline_plan_issue.added.{codeSuffix}",
                    PlanIssueSignalSeverity(candidateIssue!.Severity, added: true),
                    "Candidate introduced a new pipeline plan issue.",
                    null,
                    PipelinePlanIssueText(candidateIssue)));
                continue;
            }

            if (candidateIssue is null)
            {
                signals.Add(new BenchmarkComparisonSignal(
                    fixtureId,
                    $"pipeline_plan_issue.removed.{codeSuffix}",
                    PlanIssueSignalSeverity(baselineIssue.Severity, added: false),
                    "Candidate removed a pipeline plan issue.",
                    PipelinePlanIssueText(baselineIssue),
                    null));
                continue;
            }

            var baselineSeverity = SeverityRank(baselineIssue.Severity);
            var candidateSeverity = SeverityRank(candidateIssue.Severity);
            if (candidateSeverity > baselineSeverity)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"pipeline_plan_issue.severity.{codeSuffix}",
                    "Candidate pipeline plan issue became more severe.",
                    PipelinePlanIssueText(baselineIssue),
                    PipelinePlanIssueText(candidateIssue)));
            }
            else if (candidateSeverity < baselineSeverity)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"pipeline_plan_issue.severity.{codeSuffix}",
                    "Candidate pipeline plan issue became less severe.",
                    PipelinePlanIssueText(baselineIssue),
                    PipelinePlanIssueText(candidateIssue)));
            }
            else if (!string.Equals(baselineIssue.Message, candidateIssue.Message, StringComparison.Ordinal))
            {
                signals.Add(Info(
                    fixtureId,
                    $"pipeline_plan_issue.message.{codeSuffix}",
                    "Candidate pipeline plan issue message changed.",
                    PipelinePlanIssueText(baselineIssue),
                    PipelinePlanIssueText(candidateIssue)));
            }
        }
    }

    private static void AddRerunPlanSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselinePlans = RerunPlans(baseline);
        var candidatePlans = RerunPlans(candidate);
        var planIds = baselinePlans.Keys
            .Concat(candidatePlans.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var planId in planIds)
        {
            baselinePlans.TryGetValue(planId, out var baselinePlan);
            candidatePlans.TryGetValue(planId, out var candidatePlan);
            var codeSuffix = CleanDetectorCode(planId);
            if (baselinePlan is null)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"rerun_plan.added.{codeSuffix}",
                    "Candidate exposes an additional rerun plan for partial rescans.",
                    null,
                    RerunPlanText(candidatePlan)));
                continue;
            }

            if (candidatePlan is null)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"rerun_plan.removed.{codeSuffix}",
                    "Candidate is missing a rerun plan that existed in the baseline.",
                    RerunPlanText(baselinePlan),
                    null));
                continue;
            }

            AddRerunPlanCountSignal(
                fixtureId,
                planId,
                "rerun_stages",
                baselinePlan.RerunStageCount,
                candidatePlan.RerunStageCount,
                options.RerunPlanScopeRegressionMinimumDelta,
                "Candidate rerun plan touches more stages.",
                "Candidate rerun plan touches fewer stages.",
                signals);
            AddRerunPlanCountSignal(
                fixtureId,
                planId,
                "affected_artifacts",
                baselinePlan.AffectedArtifactCount,
                candidatePlan.AffectedArtifactCount,
                options.RerunPlanScopeRegressionMinimumDelta,
                "Candidate rerun plan affects more artifact kinds.",
                "Candidate rerun plan affects fewer artifact kinds.",
                signals);
            AddRerunPlanCountSignal(
                fixtureId,
                planId,
                "wave_span",
                RerunPlanWaveSpan(baselinePlan),
                RerunPlanWaveSpan(candidatePlan),
                options.RerunPlanScopeRegressionMinimumDelta,
                "Candidate rerun plan spans more execution waves.",
                "Candidate rerun plan spans fewer execution waves.",
                signals);

            var baselineHasWork = baselinePlan.HasWork ? 1 : 0;
            var candidateHasWork = candidatePlan.HasWork ? 1 : 0;
            if (candidateHasWork > baselineHasWork)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"rerun_plan.has_work.{codeSuffix}",
                    "Candidate rerun plan now requires work where the baseline had no rerun work.",
                    RerunPlanText(baselinePlan),
                    RerunPlanText(candidatePlan)));
            }
            else if (candidateHasWork < baselineHasWork)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"rerun_plan.has_work.{codeSuffix}",
                    "Candidate rerun plan no longer requires work.",
                    RerunPlanText(baselinePlan),
                    RerunPlanText(candidatePlan)));
            }

            var baselineModeRank = RerunPlanModeRank(baselinePlan.RecommendedExecutionMode);
            var candidateModeRank = RerunPlanModeRank(candidatePlan.RecommendedExecutionMode);
            if (candidateModeRank < baselineModeRank)
            {
                signals.Add(Regression(
                    fixtureId,
                    $"rerun_plan.execution_mode.{codeSuffix}",
                    "Candidate rerun plan lost parallel execution readiness.",
                    RerunPlanText(baselinePlan),
                    RerunPlanText(candidatePlan)));
            }
            else if (candidateModeRank > baselineModeRank)
            {
                signals.Add(Improvement(
                    fixtureId,
                    $"rerun_plan.execution_mode.{codeSuffix}",
                    "Candidate rerun plan gained parallel execution readiness.",
                    RerunPlanText(baselinePlan),
                    RerunPlanText(candidatePlan)));
            }
        }
    }

    private static void AddRerunPlanCountSignal(
        string fixtureId,
        string planId,
        string metric,
        int baseline,
        int candidate,
        int threshold,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var effectiveThreshold = Math.Max(1, threshold);
        var delta = candidate - baseline;
        var code = $"rerun_plan.{metric}.{CleanDetectorCode(planId)}";
        if (delta >= effectiveThreshold)
        {
            signals.Add(Regression(
                fixtureId,
                code,
                regressionMessage,
                RerunPlanMetricText(planId, metric, baseline),
                RerunPlanMetricText(planId, metric, candidate)));
        }
        else if (-delta >= effectiveThreshold)
        {
            signals.Add(Improvement(
                fixtureId,
                code,
                improvementMessage,
                RerunPlanMetricText(planId, metric, baseline),
                RerunPlanMetricText(planId, metric, candidate)));
        }
    }

    private static void AddStageRuntimeReadinessSignal(
        string fixtureId,
        string stage,
        string metric,
        int? baseline,
        int? candidate,
        int threshold,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline is null || candidate is null)
        {
            return;
        }

        var effectiveThreshold = Math.Max(1, threshold);
        var delta = candidate.Value - baseline.Value;
        var code = $"stage_runtime_readiness.{metric}.{CleanDetectorCode(stage)}";
        if (delta >= effectiveThreshold)
        {
            signals.Add(Regression(
                fixtureId,
                code,
                regressionMessage,
                StageRuntimeReadinessText(stage, metric, baseline.Value),
                StageRuntimeReadinessText(stage, metric, candidate.Value)));
        }
        else if (-delta >= effectiveThreshold)
        {
            signals.Add(Improvement(
                fixtureId,
                code,
                improvementMessage,
                StageRuntimeReadinessText(stage, metric, baseline.Value),
                StageRuntimeReadinessText(stage, metric, candidate.Value)));
        }
    }

    private static void AddStageContractSignals(
        string fixtureId,
        BenchmarkCaseResult baseline,
        BenchmarkCaseResult candidate,
        BenchmarkComparisonOptions options,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        var baselineContracts = StageContracts(baseline);
        var candidateContracts = StageContracts(candidate);
        var stages = baselineContracts.Keys
            .Concat(candidateContracts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var stage in stages)
        {
            baselineContracts.TryGetValue(stage, out var baselineStage);
            candidateContracts.TryGetValue(stage, out var candidateStage);
            if (baselineStage is not null && candidateStage is not null)
            {
                if (baselineStage.WritesOnlyDeclaredArtifacts && !candidateStage.WritesOnlyDeclaredArtifacts)
                {
                    signals.Add(Regression(
                        fixtureId,
                        $"stage_contract.writes_only_declared.{CleanDetectorCode(stage)}",
                        "Candidate stage changed artifacts outside its declared writes.",
                        StageContractText(stage, "writes only declared", 1),
                        StageContractText(stage, "writes only declared", 0)));
                }
                else if (!baselineStage.WritesOnlyDeclaredArtifacts && candidateStage.WritesOnlyDeclaredArtifacts)
                {
                    signals.Add(Improvement(
                        fixtureId,
                        $"stage_contract.writes_only_declared.{CleanDetectorCode(stage)}",
                        "Candidate stage no longer changes artifacts outside its declared writes.",
                        StageContractText(stage, "writes only declared", 0),
                        StageContractText(stage, "writes only declared", 1)));
                }
            }

            AddStageContractCountSignal(
                fixtureId,
                stage,
                "undeclared_changed_artifacts",
                baselineStage?.UndeclaredChangedArtifactCount,
                candidateStage?.UndeclaredChangedArtifactCount,
                options.StageContractUndeclaredChangeRegressionMinimumDelta,
                "Candidate stage has more undeclared changed artifacts.",
                "Candidate stage has fewer undeclared changed artifacts.",
                signals);
            AddStageContractCountSignal(
                fixtureId,
                stage,
                "empty_declared_outputs",
                baselineStage?.EmptyDeclaredOutputCount,
                candidateStage?.EmptyDeclaredOutputCount,
                options.StageContractEmptyDeclaredOutputRegressionMinimumDelta,
                "Candidate stage has more empty declared outputs.",
                "Candidate stage has fewer empty declared outputs.",
                signals);
        }
    }

    private static void AddStageContractCountSignal(
        string fixtureId,
        string stage,
        string metric,
        int? baseline,
        int? candidate,
        int threshold,
        string regressionMessage,
        string improvementMessage,
        ICollection<BenchmarkComparisonSignal> signals)
    {
        if (baseline is null || candidate is null)
        {
            return;
        }

        var effectiveThreshold = Math.Max(1, threshold);
        var delta = candidate.Value - baseline.Value;
        var code = $"stage_contract.{metric}.{CleanDetectorCode(stage)}";
        if (delta >= effectiveThreshold)
        {
            signals.Add(Regression(
                fixtureId,
                code,
                regressionMessage,
                StageContractText(stage, metric, baseline.Value),
                StageContractText(stage, metric, candidate.Value)));
        }
        else if (-delta >= effectiveThreshold)
        {
            signals.Add(Improvement(
                fixtureId,
                code,
                improvementMessage,
                StageContractText(stage, metric, baseline.Value),
                StageContractText(stage, metric, candidate.Value)));
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
        AddWallPlacementDeltas(deltas, baseline?.WallPlacement, candidate?.WallPlacement);
        AddFinalArtifactDeltas(deltas, baseline, candidate);
        AddArtifactPlanDeltas(deltas, baseline, candidate);
        AddPipelineHealthDeltas(deltas, baseline, candidate);
        AddPipelinePlanIssueDeltas(deltas, baseline, candidate);
        AddRerunPlanDeltas(deltas, baseline, candidate);
        AddStageArtifactDeltas(deltas, baseline, candidate);
        AddStageRuntimeReadinessDeltas(deltas, baseline, candidate);
        AddStageContractDeltas(deltas, baseline, candidate);
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

    private static void AddWallPlacementDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkWallPlacementSummary? baseline,
        BenchmarkWallPlacementSummary? candidate)
    {
        Add(deltas, "wallPlacement.totalWallCount", baseline?.TotalWallCount, candidate?.TotalWallCount);
        Add(deltas, "wallPlacement.placementReadyWallCount", baseline?.PlacementReadyWallCount, candidate?.PlacementReadyWallCount);
        Add(deltas, "wallPlacement.placementReviewWallCount", baseline?.PlacementReviewWallCount, candidate?.PlacementReviewWallCount);
        Add(deltas, "wallPlacement.rejectedNoiseWallCount", baseline?.RejectedNoiseWallCount, candidate?.RejectedNoiseWallCount);
        Add(deltas, "wallPlacement.acceptedWallCount", baseline?.AcceptedWallCount, candidate?.AcceptedWallCount);
        Add(deltas, "wallPlacement.reviewDecisionWallCount", baseline?.ReviewDecisionWallCount, candidate?.ReviewDecisionWallCount);
        Add(deltas, "wallPlacement.rejectedWallCount", baseline?.RejectedWallCount, candidate?.RejectedWallCount);
        Add(deltas, "wallPlacement.structuralComponentCount", baseline?.StructuralComponentCount, candidate?.StructuralComponentCount);
        Add(deltas, "wallPlacement.mainStructuralComponentCount", baseline?.MainStructuralComponentCount, candidate?.MainStructuralComponentCount);
        Add(deltas, "wallPlacement.secondaryStructuralComponentCount", baseline?.SecondaryStructuralComponentCount, candidate?.SecondaryStructuralComponentCount);
        Add(deltas, "wallPlacement.objectLikeComponentCount", baseline?.ObjectLikeComponentCount, candidate?.ObjectLikeComponentCount);
        Add(deltas, "wallPlacement.isolatedFragmentComponentCount", baseline?.IsolatedFragmentComponentCount, candidate?.IsolatedFragmentComponentCount);
        Add(deltas, "wallPlacement.topologyEdgeCount", baseline?.TopologyEdgeCount, candidate?.TopologyEdgeCount);
        Add(deltas, "wallPlacement.repairCandidateCount", baseline?.RepairCandidateCount, candidate?.RepairCandidateCount);
        Add(deltas, "wallPlacement.topologyImportBlockedRepairCandidateCount", baseline?.TopologyImportBlockedRepairCandidateCount, candidate?.TopologyImportBlockedRepairCandidateCount);
        Add(deltas, "wallPlacement.endpointGapRepairCandidateCount", baseline?.EndpointGapRepairCandidateCount, candidate?.EndpointGapRepairCandidateCount);
        Add(deltas, "wallPlacement.endpointOverrunRepairCandidateCount", baseline?.EndpointOverrunRepairCandidateCount, candidate?.EndpointOverrunRepairCandidateCount);
        Add(deltas, "wallPlacement.highSeverityRepairCandidateCount", baseline?.HighSeverityRepairCandidateCount, candidate?.HighSeverityRepairCandidateCount);
    }

    private static void AddFinalArtifactDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineArtifacts = FinalArtifactStates(baseline);
        var candidateArtifacts = FinalArtifactStates(candidate);
        var artifacts = baselineArtifacts.Keys
            .Concat(candidateArtifacts.Keys)
            .Where(IsComparedFinalArtifact)
            .Distinct()
            .OrderBy(artifact => artifact.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var artifact in artifacts)
        {
            Add(
                deltas,
                $"artifact.{artifact}.count",
                baseline is null ? null : FinalArtifactCount(baselineArtifacts, artifact),
                candidate is null ? null : FinalArtifactCount(candidateArtifacts, artifact));
            Add(
                deltas,
                $"artifact.{artifact}.revision",
                baseline is null ? null : FinalArtifactRevision(baselineArtifacts, artifact),
                candidate is null ? null : FinalArtifactRevision(candidateArtifacts, artifact));
        }
    }

    private static void AddArtifactPlanDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineTotals = ArtifactPlanTotals(baseline);
        var candidateTotals = ArtifactPlanTotals(candidate);
        Add(deltas, "artifactPlan.planCount", baselineTotals?.PlanCount, candidateTotals?.PlanCount);
        Add(deltas, "artifactPlan.sourceCount", baselineTotals?.SourceCount, candidateTotals?.SourceCount);
        Add(deltas, "artifactPlan.producedCount", baselineTotals?.ProducedCount, candidateTotals?.ProducedCount);
        Add(deltas, "artifactPlan.consumedCount", baselineTotals?.ConsumedCount, candidateTotals?.ConsumedCount);
        Add(deltas, "artifactPlan.terminalCount", baselineTotals?.TerminalCount, candidateTotals?.TerminalCount);
        Add(deltas, "artifactPlan.multiProducerCount", baselineTotals?.MultiProducerCount, candidateTotals?.MultiProducerCount);
        Add(deltas, "artifactPlan.requiredConsumerEdges", baselineTotals?.RequiredConsumerEdgeCount, candidateTotals?.RequiredConsumerEdgeCount);

        var baselinePlans = ArtifactPlans(baseline);
        var candidatePlans = ArtifactPlans(candidate);
        var artifacts = baselinePlans.Keys
            .Concat(candidatePlans.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var artifact in artifacts)
        {
            baselinePlans.TryGetValue(artifact, out var baselinePlan);
            candidatePlans.TryGetValue(artifact, out var candidatePlan);
            Add(deltas, $"artifactPlan.{artifact}.producerCount", baselinePlan?.ProducerCount, candidatePlan?.ProducerCount);
            Add(deltas, $"artifactPlan.{artifact}.consumerCount", baselinePlan?.ConsumerCount, candidatePlan?.ConsumerCount);
            Add(deltas, $"artifactPlan.{artifact}.requiredConsumerCount", baselinePlan?.RequiredConsumerStages.Count, candidatePlan?.RequiredConsumerStages.Count);
            Add(deltas, $"artifactPlan.{artifact}.terminal", baselinePlan is null ? null : baselinePlan.IsTerminalArtifact ? 1 : 0, candidatePlan is null ? null : candidatePlan.IsTerminalArtifact ? 1 : 0);
            Add(deltas, $"artifactPlan.{artifact}.multipleProducers", baselinePlan is null ? null : baselinePlan.HasMultipleProducers ? 1 : 0, candidatePlan is null ? null : candidatePlan.HasMultipleProducers ? 1 : 0);
            Add(deltas, $"artifactPlan.{artifact}.roleRank", baselinePlan is null ? null : ArtifactPlanRoleRank(baselinePlan), candidatePlan is null ? null : ArtifactPlanRoleRank(candidatePlan));
        }
    }

    private static void AddPipelineHealthDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineHealth = PipelineHealth(baseline);
        var candidateHealth = PipelineHealth(candidate);
        Add(deltas, "pipelineHealth.notDependencyReadyStages", baselineHealth?.NotDependencyReadyStageCount, candidateHealth?.NotDependencyReadyStageCount);
        Add(deltas, "pipelineHealth.emptyRequiredRuntimeReads", baselineHealth?.EmptyRequiredReadCount, candidateHealth?.EmptyRequiredReadCount);
        Add(deltas, "pipelineHealth.emptyOptionalRuntimeReads", baselineHealth?.EmptyOptionalReadCount, candidateHealth?.EmptyOptionalReadCount);
        Add(deltas, "pipelineHealth.contractViolationStages", baselineHealth?.ContractViolationStageCount, candidateHealth?.ContractViolationStageCount);
        Add(deltas, "pipelineHealth.undeclaredChangedArtifacts", baselineHealth?.UndeclaredChangedArtifactCount, candidateHealth?.UndeclaredChangedArtifactCount);
        Add(deltas, "pipelineHealth.emptyDeclaredOutputs", baselineHealth?.EmptyDeclaredOutputCount, candidateHealth?.EmptyDeclaredOutputCount);
    }

    private static void AddPipelinePlanIssueDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineTotals = PipelinePlanIssueTotals(baseline);
        var candidateTotals = PipelinePlanIssueTotals(candidate);
        Add(deltas, "planIssue.totalCount", baselineTotals?.TotalCount, candidateTotals?.TotalCount);
        Add(deltas, "planIssue.infoCount", baselineTotals?.InfoCount, candidateTotals?.InfoCount);
        Add(deltas, "planIssue.warningCount", baselineTotals?.WarningCount, candidateTotals?.WarningCount);
        Add(deltas, "planIssue.errorCount", baselineTotals?.ErrorCount, candidateTotals?.ErrorCount);

        var baselineByCode = PipelinePlanIssueCodeCounts(baseline);
        var candidateByCode = PipelinePlanIssueCodeCounts(candidate);
        var codes = baselineByCode.Keys
            .Concat(candidateByCode.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var code in codes)
        {
            Add(
                deltas,
                $"planIssue.{CleanDetectorCode(code)}.count",
                baselineByCode.TryGetValue(code, out var baselineCount) ? baselineCount : 0,
                candidateByCode.TryGetValue(code, out var candidateCount) ? candidateCount : 0);
        }
    }

    private static void AddRerunPlanDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineTotals = RerunPlanTotals(baseline);
        var candidateTotals = RerunPlanTotals(candidate);
        Add(deltas, "rerunPlan.planCount", baselineTotals?.PlanCount, candidateTotals?.PlanCount);
        Add(deltas, "rerunPlan.workPlanCount", baselineTotals?.WorkPlanCount, candidateTotals?.WorkPlanCount);
        Add(deltas, "rerunPlan.totalRerunStages", baselineTotals?.TotalRerunStageCount, candidateTotals?.TotalRerunStageCount);
        Add(deltas, "rerunPlan.totalAffectedArtifacts", baselineTotals?.TotalAffectedArtifactCount, candidateTotals?.TotalAffectedArtifactCount);
        Add(deltas, "rerunPlan.parallelCandidatePlans", baselineTotals?.ParallelCandidatePlanCount, candidateTotals?.ParallelCandidatePlanCount);
        Add(deltas, "rerunPlan.sequentialWorkPlans", baselineTotals?.SequentialWorkPlanCount, candidateTotals?.SequentialWorkPlanCount);

        var baselinePlans = RerunPlans(baseline);
        var candidatePlans = RerunPlans(candidate);
        var planIds = baselinePlans.Keys
            .Concat(candidatePlans.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var planId in planIds)
        {
            baselinePlans.TryGetValue(planId, out var baselinePlan);
            candidatePlans.TryGetValue(planId, out var candidatePlan);
            Add(deltas, $"rerunPlan.{planId}.hasWork", baselinePlan is null ? null : baselinePlan.HasWork ? 1 : 0, candidatePlan is null ? null : candidatePlan.HasWork ? 1 : 0);
            Add(deltas, $"rerunPlan.{planId}.rerunStages", baselinePlan?.RerunStageCount, candidatePlan?.RerunStageCount);
            Add(deltas, $"rerunPlan.{planId}.affectedArtifacts", baselinePlan?.AffectedArtifactCount, candidatePlan?.AffectedArtifactCount);
            Add(deltas, $"rerunPlan.{planId}.waveSpan", baselinePlan is null ? null : RerunPlanWaveSpan(baselinePlan), candidatePlan is null ? null : RerunPlanWaveSpan(candidatePlan));
            Add(deltas, $"rerunPlan.{planId}.modeRank", baselinePlan is null ? null : RerunPlanModeRank(baselinePlan.RecommendedExecutionMode), candidatePlan is null ? null : RerunPlanModeRank(candidatePlan.RecommendedExecutionMode));
        }
    }

    private static void AddStageArtifactDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineArtifacts = StageArtifacts(baseline);
        var candidateArtifacts = StageArtifacts(candidate);
        var keys = baselineArtifacts.Keys
            .Concat(candidateArtifacts.Keys)
            .Where(key => IsNoiseSensitiveStageArtifact(key.Artifact))
            .Distinct()
            .OrderBy(key => key.Stage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Artifact.ToString(), StringComparer.Ordinal)
            .ToArray();

        foreach (var key in keys)
        {
            baselineArtifacts.TryGetValue(key, out var baselineArtifact);
            candidateArtifacts.TryGetValue(key, out var candidateArtifact);
            Add(
                deltas,
                $"stage.{key.Stage}.{key.Artifact}.after",
                baselineArtifact?.AfterCount,
                candidateArtifact?.AfterCount);
            Add(
                deltas,
                $"stage.{key.Stage}.{key.Artifact}.delta",
                baselineArtifact?.Delta,
                candidateArtifact?.Delta);
        }
    }

    private static void AddStageRuntimeReadinessDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineReadiness = StageRuntimeReadiness(baseline);
        var candidateReadiness = StageRuntimeReadiness(candidate);
        var stages = baselineReadiness.Keys
            .Concat(candidateReadiness.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var stage in stages)
        {
            baselineReadiness.TryGetValue(stage, out var baselineStage);
            candidateReadiness.TryGetValue(stage, out var candidateStage);
            Add(
                deltas,
                $"stage.{stage}.runtimeReadiness.emptyRequiredReads",
                baselineStage?.EmptyRequiredReadCount,
                candidateStage?.EmptyRequiredReadCount);
            Add(
                deltas,
                $"stage.{stage}.runtimeReadiness.emptyOptionalReads",
                baselineStage?.EmptyOptionalReadCount,
                candidateStage?.EmptyOptionalReadCount);
        }
    }

    private static void AddStageContractDeltas(
        ICollection<BenchmarkCountDelta> deltas,
        BenchmarkCaseResult? baseline,
        BenchmarkCaseResult? candidate)
    {
        var baselineContracts = StageContracts(baseline);
        var candidateContracts = StageContracts(candidate);
        var stages = baselineContracts.Keys
            .Concat(candidateContracts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var stage in stages)
        {
            baselineContracts.TryGetValue(stage, out var baselineStage);
            candidateContracts.TryGetValue(stage, out var candidateStage);
            Add(
                deltas,
                $"stage.{stage}.contract.writesOnlyDeclaredArtifacts",
                baselineStage is null ? null : baselineStage.WritesOnlyDeclaredArtifacts ? 1 : 0,
                candidateStage is null ? null : candidateStage.WritesOnlyDeclaredArtifacts ? 1 : 0);
            Add(
                deltas,
                $"stage.{stage}.contract.undeclaredChangedArtifacts",
                baselineStage?.UndeclaredChangedArtifactCount,
                candidateStage?.UndeclaredChangedArtifactCount);
            Add(
                deltas,
                $"stage.{stage}.contract.emptyDeclaredOutputs",
                baselineStage?.EmptyDeclaredOutputCount,
                candidateStage?.EmptyDeclaredOutputCount);
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

    private static IReadOnlyDictionary<StageArtifactKey, StageArtifactMetric> StageArtifacts(BenchmarkCaseResult? result)
    {
        if (result is null || result.Stages.Count == 0)
        {
            return new Dictionary<StageArtifactKey, StageArtifactMetric>();
        }

        var values = new Dictionary<StageArtifactKey, StageArtifactMetric>();
        foreach (var stage in result.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Stage))
            {
                continue;
            }

            var artifacts = stage.InputArtifacts
                .Select(item => item.Artifact)
                .Concat(stage.OutputArtifacts.Select(item => item.Artifact))
                .Concat(stage.ChangedArtifacts.Select(item => item.Artifact))
                .Concat(stage.ArtifactDeltas.Select(item => item.Artifact))
                .Where(artifact => artifact != PlanArtifactKind.Unknown)
                .Distinct()
                .ToArray();

            foreach (var artifact in artifacts)
            {
                var key = new StageArtifactKey(stage.Stage, artifact);
                var lifecycle = stage.ArtifactDeltas.FirstOrDefault(item => item.Artifact == artifact);
                var change = stage.ChangedArtifacts.FirstOrDefault(item => item.Artifact == artifact);
                var input = stage.InputArtifacts.FirstOrDefault(item => item.Artifact == artifact);
                var output = stage.OutputArtifacts.FirstOrDefault(item => item.Artifact == artifact);
                var before = lifecycle?.BeforeCount ?? change?.BeforeCount ?? input?.Count ?? output?.Count;
                var after = lifecycle?.AfterCount ?? change?.AfterCount ?? output?.Count ?? input?.Count;
                var delta = lifecycle?.Delta ?? change?.Delta;
                if (delta is null && before is not null && after is not null)
                {
                    delta = after.Value - before.Value;
                }

                values[key] = new StageArtifactMetric(before, after, delta);
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, StageRuntimeReadinessMetric> StageRuntimeReadiness(BenchmarkCaseResult? result)
    {
        if (result is null || result.Stages.Count == 0)
        {
            return new Dictionary<string, StageRuntimeReadinessMetric>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, StageRuntimeReadinessMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in result.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Stage))
            {
                continue;
            }

            var readiness = stage.RuntimeReadiness ?? PipelineStageRuntimeReadiness.Empty;
            values[stage.Stage] = new StageRuntimeReadinessMetric(
                readiness.EmptyRequiredReads.Count,
                readiness.EmptyOptionalReads.Count);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, BenchmarkRerunPlanSummary> RerunPlans(BenchmarkCaseResult? result)
    {
        if (result is null || result.RerunPlans.Count == 0)
        {
            return new Dictionary<string, BenchmarkRerunPlanSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return result.RerunPlans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.PlanId))
            .GroupBy(plan => plan.PlanId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(plan => plan.HasWork)
                    .ThenByDescending(plan => plan.RerunStageCount)
                    .ThenByDescending(plan => plan.AffectedArtifactCount)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, BenchmarkArtifactPlanSummary> ArtifactPlans(BenchmarkCaseResult? result)
    {
        if (result is null || result.ArtifactPlans.Count == 0)
        {
            return new Dictionary<string, BenchmarkArtifactPlanSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return result.ArtifactPlans
            .Where(plan => !string.IsNullOrWhiteSpace(plan.Artifact))
            .GroupBy(plan => plan.Artifact, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(plan => plan.IsProducedByStage)
                    .ThenByDescending(plan => plan.IsConsumedByStage)
                    .ThenByDescending(plan => plan.ConsumerCount)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static ArtifactPlanTotalsMetric? ArtifactPlanTotals(BenchmarkCaseResult? result)
    {
        if (result is null)
        {
            return null;
        }

        var plans = result.ArtifactPlans;
        return new ArtifactPlanTotalsMetric(
            plans.Count,
            plans.Count(plan => plan.IsSourceArtifact),
            plans.Count(plan => plan.IsProducedByStage),
            plans.Count(plan => plan.IsConsumedByStage),
            plans.Count(plan => plan.IsTerminalArtifact),
            plans.Count(plan => plan.HasMultipleProducers),
            plans.Sum(plan => plan.RequiredConsumerStages.Count));
    }

    private static IReadOnlyDictionary<string, BenchmarkPipelinePlanIssueSummary> PipelinePlanIssues(BenchmarkCaseResult? result)
    {
        if (result is null || result.PlanIssues.Count == 0)
        {
            return new Dictionary<string, BenchmarkPipelinePlanIssueSummary>(StringComparer.OrdinalIgnoreCase);
        }

        return result.PlanIssues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.Code))
            .GroupBy(PipelinePlanIssueKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(issue => SeverityRank(issue.Severity))
                    .ThenBy(issue => issue.Stage, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(issue => issue.Message, StringComparer.OrdinalIgnoreCase)
                    .First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, int> PipelinePlanIssueCodeCounts(BenchmarkCaseResult? result)
    {
        if (result is null || result.PlanIssues.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return result.PlanIssues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.Code))
            .GroupBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static PipelinePlanIssueTotalsMetric? PipelinePlanIssueTotals(BenchmarkCaseResult? result)
    {
        if (result is null)
        {
            return null;
        }

        return new PipelinePlanIssueTotalsMetric(
            result.PlanIssues.Count,
            result.PlanIssues.Count(issue => SeverityRank(issue.Severity) == (int)DiagnosticSeverity.Info),
            result.PlanIssues.Count(issue => SeverityRank(issue.Severity) == (int)DiagnosticSeverity.Warning),
            result.PlanIssues.Count(issue => SeverityRank(issue.Severity) >= (int)DiagnosticSeverity.Error));
    }

    private static string PipelinePlanIssueKey(BenchmarkPipelinePlanIssueSummary issue)
    {
        var artifacts = issue.Artifacts.Count == 0
            ? "-"
            : string.Join("/", issue.Artifacts.Order(StringComparer.OrdinalIgnoreCase));
        return $"{issue.Code}|{issue.Stage}|{artifacts}";
    }

    private static RerunPlanTotalsMetric? RerunPlanTotals(BenchmarkCaseResult? result)
    {
        if (result is null)
        {
            return null;
        }

        var plans = result.RerunPlans;
        return new RerunPlanTotalsMetric(
            plans.Count,
            plans.Count(plan => plan.HasWork),
            plans.Where(plan => plan.HasWork).Sum(plan => Math.Max(0, plan.RerunStageCount)),
            plans.Where(plan => plan.HasWork).Sum(plan => Math.Max(0, plan.AffectedArtifactCount)),
            plans.Count(plan => plan.HasWork && RerunPlanModeRank(plan.RecommendedExecutionMode) >= 2),
            plans.Count(plan => plan.HasWork && RerunPlanModeRank(plan.RecommendedExecutionMode) == 1));
    }

    private static int RerunPlanWaveSpan(BenchmarkRerunPlanSummary plan)
    {
        if (!plan.HasWork)
        {
            return 0;
        }

        if (plan.RerunWaves.Count > 0)
        {
            return plan.RerunWaves.Distinct().Count();
        }

        return plan.FirstRerunWave >= 0 && plan.LastRerunWave >= plan.FirstRerunWave
            ? plan.LastRerunWave - plan.FirstRerunWave + 1
            : 0;
    }

    private static int RerunPlanModeRank(string? mode) =>
        mode switch
        {
            "WaveOrderedWithParallelCandidates" => 2,
            "Parallel" => 2,
            "WaveOrderedSequential" => 1,
            "Sequential" => 1,
            _ => 0
        };

    private static int ArtifactPlanRoleRank(BenchmarkArtifactPlanSummary plan) =>
        plan.DependencyRole switch
        {
            "SourceInput" => 4,
            "ProducedAndConsumed" => 3,
            "SourceTerminal" => 2,
            "ProducedTerminal" => 1,
            "UnproducedRead" => 0,
            _ => plan.IsConsumedByStage ? 2 : plan.IsProducedByStage ? 1 : 0
        };

    private static PipelineHealthMetric? PipelineHealth(BenchmarkCaseResult? result)
    {
        if (result is null)
        {
            return null;
        }

        if (result.PipelineHealth.StageCount > 0 || result.Stages.Count == 0)
        {
            return new PipelineHealthMetric(
                result.PipelineHealth.NotDependencyReadyStageCount,
                result.PipelineHealth.EmptyRequiredRuntimeReadCount,
                result.PipelineHealth.EmptyOptionalRuntimeReadCount,
                result.PipelineHealth.ContractViolationStageCount,
                result.PipelineHealth.UndeclaredChangedArtifactCount,
                result.PipelineHealth.EmptyDeclaredOutputCount);
        }

        var stages = result.Stages;
        return new PipelineHealthMetric(
            stages.Count(stage => !stage.IsDependencyReady),
            stages.Sum(stage => (stage.RuntimeReadiness ?? PipelineStageRuntimeReadiness.Empty).EmptyRequiredReads.Count),
            stages.Sum(stage => (stage.RuntimeReadiness ?? PipelineStageRuntimeReadiness.Empty).EmptyOptionalReads.Count),
            stages.Count(stage => !(stage.Contract ?? PipelineStageContract.Empty).WritesOnlyDeclaredArtifacts),
            stages.Sum(stage => (stage.Contract ?? PipelineStageContract.Empty).UndeclaredChangedArtifacts.Count),
            stages.Sum(EmptyDeclaredOutputCount));
    }

    private static IReadOnlyDictionary<string, StageContractMetric> StageContracts(BenchmarkCaseResult? result)
    {
        if (result is null || result.Stages.Count == 0)
        {
            return new Dictionary<string, StageContractMetric>(StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, StageContractMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in result.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Stage))
            {
                continue;
            }

            var contract = stage.Contract ?? PipelineStageContract.Empty;
            values[stage.Stage] = new StageContractMetric(
                contract.WritesOnlyDeclaredArtifacts,
                contract.UndeclaredChangedArtifacts.Count,
                EmptyDeclaredOutputCount(stage));
        }

        return values;
    }

    private static int EmptyDeclaredOutputCount(BenchmarkStageSummary stage)
    {
        var readiness = stage.OutputReadiness ?? PipelineStageOutputReadiness.Empty;
        return readiness.IsAvailable
            ? readiness.EmptyDeclaredOutputs.Count
            : stage.ArtifactDeltas.Count(delta => delta.IsEmptyDeclaredOutput);
    }

    private static IReadOnlyDictionary<PlanArtifactKind, ArtifactStateMetric> FinalArtifactStates(BenchmarkCaseResult? result)
    {
        if (result is null || result.ArtifactInventory.Count == 0)
        {
            return new Dictionary<PlanArtifactKind, ArtifactStateMetric>();
        }

        return result.ArtifactInventory
            .Where(item => item.Artifact != PlanArtifactKind.Unknown)
            .GroupBy(item => item.Artifact)
            .ToDictionary(group => group.Key, ArtifactStateMetric.From);
    }

    private static int FinalArtifactCount(
        IReadOnlyDictionary<PlanArtifactKind, ArtifactStateMetric> artifacts,
        PlanArtifactKind artifact) =>
        artifacts.TryGetValue(artifact, out var metric) ? metric.Count : 0;

    private static int FinalArtifactRevision(
        IReadOnlyDictionary<PlanArtifactKind, ArtifactStateMetric> artifacts,
        PlanArtifactKind artifact) =>
        artifacts.TryGetValue(artifact, out var metric) ? metric.Revision : 0;

    private static bool IsComparedFinalArtifact(PlanArtifactKind artifact) =>
        IsCriticalFinalArtifact(artifact)
        || IsExplosionSensitiveFinalArtifact(artifact)
        || IsNoiseReducibleFinalArtifact(artifact);

    private static bool IsCriticalFinalArtifact(PlanArtifactKind artifact) =>
        artifact is PlanArtifactKind.Walls
            or PlanArtifactKind.WallGraph
            or PlanArtifactKind.TopologySpans
            or PlanArtifactKind.Rooms
            or PlanArtifactKind.Openings
            or PlanArtifactKind.RoutingBarriers
            or PlanArtifactKind.RoutingPassages;

    private static bool IsExplosionSensitiveFinalArtifact(PlanArtifactKind artifact) =>
        artifact is PlanArtifactKind.Walls
            or PlanArtifactKind.WallGraph
            or PlanArtifactKind.TopologySpans
            or PlanArtifactKind.SurfacePatterns
            or PlanArtifactKind.RoomAdjacency
            or PlanArtifactKind.ObjectCandidates
            or PlanArtifactKind.ObjectGroups
            or PlanArtifactKind.ObjectAggregates
            or PlanArtifactKind.RoutingBarriers
            or PlanArtifactKind.RoutingPassages
            or PlanArtifactKind.RoutingObstacles
            or PlanArtifactKind.RoutingRoomUseHints
            or PlanArtifactKind.RoutingSuppressedObjects
            or PlanArtifactKind.RoutingIgnoredObjects;

    private static bool IsNoiseReducibleFinalArtifact(PlanArtifactKind artifact) =>
        artifact is PlanArtifactKind.SurfacePatterns
            or PlanArtifactKind.ObjectCandidates
            or PlanArtifactKind.ObjectGroups
            or PlanArtifactKind.RoutingObstacles
            or PlanArtifactKind.RoutingRoomUseHints
            or PlanArtifactKind.RoutingSuppressedObjects
            or PlanArtifactKind.RoutingIgnoredObjects;

    private static bool IsNoiseSensitiveStageArtifact(PlanArtifactKind artifact) =>
        artifact is PlanArtifactKind.Walls
            or PlanArtifactKind.WallGraph
            or PlanArtifactKind.TopologySpans
            or PlanArtifactKind.SurfacePatterns
            or PlanArtifactKind.RoomAdjacency
            or PlanArtifactKind.ObjectCandidates
            or PlanArtifactKind.ObjectGroups
            or PlanArtifactKind.ObjectAggregates
            or PlanArtifactKind.RoutingBarriers
            or PlanArtifactKind.RoutingPassages
            or PlanArtifactKind.RoutingObstacles
            or PlanArtifactKind.RoutingRoomUseHints
            or PlanArtifactKind.RoutingSuppressedObjects
            or PlanArtifactKind.RoutingIgnoredObjects;

    private static bool IsImportantArtifactPlan(BenchmarkArtifactPlanSummary plan) =>
        Enum.TryParse<PlanArtifactKind>(plan.Artifact, ignoreCase: true, out var artifact)
            ? IsCriticalFinalArtifact(artifact)
            : plan.HasRequiredConsumers || plan.IsProducedByStage;

    private static BenchmarkComparisonSignalSeverity PlanIssueSignalSeverity(string severity, bool added)
    {
        var rank = SeverityRank(severity);
        if (rank < (int)DiagnosticSeverity.Warning)
        {
            return BenchmarkComparisonSignalSeverity.Info;
        }

        return added
            ? BenchmarkComparisonSignalSeverity.Regression
            : BenchmarkComparisonSignalSeverity.Improvement;
    }

    private static int SeverityRank(string severity) =>
        Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: true, out var parsed)
            ? (int)parsed
            : 0;

    private static string StageArtifactMetricText(StageArtifactKey key, string metric, int value) =>
        $"{key.Stage}/{key.Artifact} {metric} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string StageRuntimeReadinessText(string stage, string metric, int value) =>
        $"{stage} {metric.Replace('_', ' ')} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string StageContractText(string stage, string metric, int value) =>
        $"{stage} {metric.Replace('_', ' ')} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string PipelineHealthText(string metric, int value) =>
        $"pipeline {metric.Replace('_', ' ')} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string PipelinePlanIssueText(BenchmarkPipelinePlanIssueSummary issue)
    {
        var artifacts = issue.Artifacts.Count == 0
            ? "-"
            : string.Join("/", issue.Artifacts);
        return $"{issue.Code} {issue.Severity}, stage {issue.Stage}, artifacts {artifacts}";
    }

    private static string RerunPlanMetricText(string planId, string metric, int value) =>
        $"{planId} {metric.Replace('_', ' ')} {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string RerunPlanText(BenchmarkRerunPlanSummary? plan) =>
        plan is null
            ? "-"
            : $"{plan.PlanId} {plan.RecommendedExecutionMode}, stages {plan.RerunStageCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, artifacts {plan.AffectedArtifactCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, waves {RerunPlanWaveSpan(plan).ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string ArtifactPlanText(BenchmarkArtifactPlanSummary? plan) =>
        plan is null
            ? "-"
            : $"{plan.Artifact} {plan.DependencyRole}, producers {plan.ProducerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, consumers {plan.ConsumerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}, required {plan.RequiredConsumerStages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}, terminal {YesNo(plan.IsTerminalArtifact)}, multi-producer {YesNo(plan.HasMultipleProducers)}";

    private static string FinalArtifactText(PlanArtifactKind artifact, int count) =>
        $"{artifact} {count.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string FinalArtifactStateText(PlanArtifactKind artifact, ArtifactStateMetric metric) =>
        $"{artifact} count {metric.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}, revision {metric.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}, state {metric.StateKey}";

    private static string YesNo(bool value) => value ? "yes" : "no";

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

    private static BenchmarkComparisonSignal Info(
        string fixtureId,
        string code,
        string message,
        string? baseline,
        string? candidate) =>
        new(fixtureId, code, BenchmarkComparisonSignalSeverity.Info, message, baseline, candidate);

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

    private static string WallPlacementSummaryText(BenchmarkWallPlacementSummary summary) =>
        $"ready {summary.PlacementReadyWallCount}, review {summary.PlacementReviewWallCount}, rejected {summary.RejectedNoiseWallCount}, structural {summary.StructuralComponentCount}, isolated {summary.IsolatedFragmentComponentCount}, repairs {summary.RepairCandidateCount}, blocked repairs {summary.TopologyImportBlockedRepairCandidateCount}";

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

    private sealed record StageArtifactKey(string Stage, PlanArtifactKind Artifact);

    private sealed record StageArtifactMetric(int? BeforeCount, int? AfterCount, int? Delta);

    private sealed record StageRuntimeReadinessMetric(int EmptyRequiredReadCount, int EmptyOptionalReadCount);

    private sealed record PipelineHealthMetric(
        int NotDependencyReadyStageCount,
        int EmptyRequiredReadCount,
        int EmptyOptionalReadCount,
        int ContractViolationStageCount,
        int UndeclaredChangedArtifactCount,
        int EmptyDeclaredOutputCount);

    private sealed record ArtifactStateMetric(
        int Count,
        string StateKey,
        int Revision)
    {
        public static ArtifactStateMetric From(IGrouping<PlanArtifactKind, PipelineArtifactSnapshot> group)
        {
            var snapshots = group
                .OrderBy(item => item.StateKey, StringComparer.Ordinal)
                .ThenBy(item => item.Revision)
                .ToArray();
            var count = snapshots.Sum(item => Math.Max(0, item.Count));
            var stateKey = snapshots.Length == 1
                ? snapshots[0].StateKey
                : string.Join("|", snapshots.Select(item => item.StateKey));
            var revision = snapshots.Length == 1
                ? snapshots[0].Revision
                : snapshots
                    .Select(item => item.Revision)
                    .Aggregate(17, (hash, revisionValue) => unchecked((hash * 31) + revisionValue));

            return new ArtifactStateMetric(count, stateKey, revision < 0 ? -revision : revision);
        }
    }

    private sealed record RerunPlanTotalsMetric(
        int PlanCount,
        int WorkPlanCount,
        int TotalRerunStageCount,
        int TotalAffectedArtifactCount,
        int ParallelCandidatePlanCount,
        int SequentialWorkPlanCount);

    private sealed record ArtifactPlanTotalsMetric(
        int PlanCount,
        int SourceCount,
        int ProducedCount,
        int ConsumedCount,
        int TerminalCount,
        int MultiProducerCount,
        int RequiredConsumerEdgeCount);

    private sealed record PipelinePlanIssueTotalsMetric(
        int TotalCount,
        int InfoCount,
        int WarningCount,
        int ErrorCount);

    private sealed record StageContractMetric(
        bool WritesOnlyDeclaredArtifacts,
        int UndeclaredChangedArtifactCount,
        int EmptyDeclaredOutputCount);
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
