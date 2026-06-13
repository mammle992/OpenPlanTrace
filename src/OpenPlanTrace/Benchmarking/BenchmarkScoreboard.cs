using System.Globalization;

namespace OpenPlanTrace;

public enum BenchmarkScoreGrade
{
    Unknown = 0,
    Blocked,
    NeedsWork,
    ReviewRequired,
    Usable,
    Strong
}

public enum BenchmarkFailureSeverity
{
    Info = 0,
    Warning,
    Critical
}

public sealed record BenchmarkScoreboard(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    BenchmarkScoreGrade Grade,
    double OverallScore,
    double ConsumerReadinessScore,
    bool ReadyForDownstreamUse,
    int CaseCount,
    int ScoredCaseCount,
    int SkippedCaseCount,
    int FailedScanCount,
    int FailedAssertionCount,
    int ExpectedTargetCount,
    int MatchedTargetCount,
    int MissedTargetCount,
    int ExtraDetectionCount,
    IReadOnlyList<BenchmarkCaseScore> Cases,
    IReadOnlyList<BenchmarkDetectorScore> Detectors,
    IReadOnlyList<BenchmarkFailureBucket> FailureBuckets,
    IReadOnlyList<string> RecommendedNextActions)
{
    public const string CurrentSchemaVersion = "openplantrace.benchmark-scoreboard.v1";
    private const int HeavyScanReviewQueueThreshold = 75;

    public static BenchmarkScoreboard Empty { get; } =
        new(
            CurrentSchemaVersion,
            DateTimeOffset.UnixEpoch,
            BenchmarkScoreGrade.Unknown,
            0,
            0,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<BenchmarkCaseScore>(),
            Array.Empty<BenchmarkDetectorScore>(),
            Array.Empty<BenchmarkFailureBucket>(),
            Array.Empty<string>());

    public static BenchmarkScoreboard FromCases(IEnumerable<BenchmarkCaseResult> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);

        var caseArray = cases.ToArray();
        var caseScores = caseArray.Select(CreateCaseScore).ToArray();
        var detectorScores = CreateDetectorScores(caseArray).ToArray();
        var failures = CreateFailureBuckets(caseArray, caseScores, detectorScores).ToArray();
        var scoredCases = caseScores.Where(item => !item.Skipped).ToArray();
        var overallScore = scoredCases.Length == 0
            ? 0
            : scoredCases.Average(item => item.OverallScore);
        var consumerReadinessScore = CalculateConsumerReadinessScore(scoredCases, detectorScores, overallScore);
        var failedAssertionCount = caseArray.Sum(item => item.FailedAssertionCount);
        var failedScanCount = caseArray.Count(item => !item.ScanSucceeded && !item.Skipped);
        var grade = GradeForScore(Math.Min(overallScore, consumerReadinessScore));

        if (failedScanCount > 0 || failures.Any(item => item.Severity == BenchmarkFailureSeverity.Critical))
        {
            grade = CapGrade(grade, BenchmarkScoreGrade.Blocked);
        }
        else if (failedAssertionCount > 0)
        {
            grade = CapGrade(grade, BenchmarkScoreGrade.ReviewRequired);
        }

        var ready =
            scoredCases.Length > 0
            && failedScanCount == 0
            && failedAssertionCount == 0
            && consumerReadinessScore >= 0.85
            && scoredCases.All(item =>
                item.ReadyForGeometryImport
                && item.ReadyForMetricImport
                && item.ReadyForRoutingImport)
            && detectorScores.All(item => item.Grade >= BenchmarkScoreGrade.Usable)
            && failures.All(item => item.Severity != BenchmarkFailureSeverity.Critical);

        return new BenchmarkScoreboard(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            grade,
            Round(overallScore),
            Round(consumerReadinessScore),
            ready,
            caseArray.Length,
            scoredCases.Length,
            caseArray.Count(item => item.Skipped),
            failedScanCount,
            failedAssertionCount,
            detectorScores.Sum(item => item.ExpectedCount),
            detectorScores.Sum(item => item.MatchedCount),
            detectorScores.Sum(item => item.MissedCount),
            detectorScores.Sum(item => item.ExtraCount),
            caseScores,
            detectorScores,
            failures,
            CreateRecommendedActions(failures, detectorScores, caseScores));
    }

    private static BenchmarkCaseScore CreateCaseScore(BenchmarkCaseResult item)
    {
        if (item.Skipped)
        {
            return new BenchmarkCaseScore(
                item.FixtureId,
                BenchmarkScoreGrade.Unknown,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                item.FailedAssertionCount,
                item.Counts.QualityGrade,
                item.Counts.QualityConfidence,
                true,
                true,
                new[] { item.SkipReason ?? "Fixture skipped." });
        }

        if (!item.ScanSucceeded)
        {
            return new BenchmarkCaseScore(
                item.FixtureId,
                BenchmarkScoreGrade.Blocked,
                0,
                0,
                0,
                0,
                AssertionReliability(item),
                0,
                0,
                0,
                false,
                false,
                false,
                0,
                0,
                0,
                0,
                item.FailedAssertionCount,
                item.Counts.QualityGrade,
                item.Counts.QualityConfidence,
                true,
                false,
                new[] { item.ErrorMessage ?? "Scan did not complete." });
        }

        var expected = item.Metrics.Sum(metric => metric.ExpectedCount);
        var matched = item.Metrics.Sum(metric => metric.MatchedCount);
        var missed = item.Metrics.Sum(metric => metric.MissedCount);
        var precisionMetrics = item.Metrics.Where(metric => metric.PrecisionScoringEnabled).ToArray();
        var precisionMatched = precisionMetrics.Sum(metric => metric.MatchedCount);
        var scoredDetected = precisionMetrics.Sum(metric => metric.ScoredDetectionCount);
        var extra = precisionMetrics.Sum(metric => metric.ExtraCount);
        var recall = expected == 0
            ? item.Passed ? 1.0 : 0.5
            : matched / (double)expected;
        var precision = precisionMetrics.Length == 0
            ? 1.0
            : scoredDetected == 0
                ? 0.0
                : precisionMatched / (double)scoredDetected;
        var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);
        var reliability = AssertionReliability(item);
        var quality = QualityScore(item.Counts);
        var measurement = MeasurementScore(item.Counts);
        var importReadiness = item.ImportReadiness;
        var importReadinessScore = Math.Clamp(importReadiness.Score, 0, 1);
        var score = item.Metrics.Count > 0
            ? (f1 * 0.35) + (recall * 0.18) + (precision * 0.12) + (reliability * 0.13) + (quality * 0.07) + (measurement * 0.05) + (importReadinessScore * 0.10)
            : (reliability * 0.45) + (quality * 0.20) + (measurement * 0.10) + (importReadinessScore * 0.25);

        if (!item.Passed)
        {
            score = Math.Min(score, 0.74);
        }

        var grade = GradeForScore(score);
        if (item.FailedAssertionCount > 0)
        {
            grade = CapGrade(grade, BenchmarkScoreGrade.ReviewRequired);
        }

        if (!importReadiness.ReadyForGeometryImport)
        {
            grade = CapGrade(grade, BenchmarkScoreGrade.Blocked);
        }

        var reasons = new List<string>();
        if (item.FailedAssertionCount > 0)
        {
            reasons.Add($"{item.FailedAssertionCount} failed assertion(s)");
        }

        if (missed > 0)
        {
            reasons.Add($"{missed} missed target(s)");
        }

        if (extra > 0)
        {
            reasons.Add($"{extra} unmatched extra detection(s)");
        }

        if (item.Counts.QualityRequiresReview)
        {
            reasons.Add($"scan quality requires review ({item.Counts.QualityGrade})");
        }

        if (!item.Counts.HasReliableCalibration)
        {
            reasons.Add("calibration is not reliable");
        }

        if (item.Counts.MeasurementOutlierCount > 0)
        {
            reasons.Add($"{item.Counts.MeasurementOutlierCount} measurement outlier(s)");
        }

        if (!importReadiness.ReadyForGeometryImport)
        {
            reasons.Add("geometry import is not ready");
        }
        else
        {
            if (!importReadiness.ReadyForMetricImport)
            {
                reasons.Add("metric import is not ready");
            }

            if (!importReadiness.ReadyForRoutingImport)
            {
                reasons.Add("routing import is not ready");
            }
        }

        if (importReadiness.RequiresReview)
        {
            reasons.Add($"import readiness requires review ({importReadiness.Grade})");
        }

        if (item.ScanReviewQueue.Count >= HeavyScanReviewQueueThreshold)
        {
            reasons.Add($"{item.ScanReviewQueue.Count} scanner review item(s)");
        }

        return new BenchmarkCaseScore(
            item.FixtureId,
            grade,
            Round(score),
            Round(f1),
            Round(recall),
            Round(precision),
            Round(reliability),
            Round(quality),
            Round(measurement),
            Round(importReadinessScore),
            importReadiness.ReadyForGeometryImport,
            importReadiness.ReadyForMetricImport,
            importReadiness.ReadyForRoutingImport,
            expected,
            matched,
            missed,
            extra,
            item.FailedAssertionCount,
            item.Counts.QualityGrade,
            item.Counts.QualityConfidence,
            item.Counts.QualityRequiresReview,
            false,
            reasons);
    }

    private static IEnumerable<BenchmarkDetectorScore> CreateDetectorScores(IReadOnlyList<BenchmarkCaseResult> cases)
    {
        foreach (var group in cases
                     .Where(item => !item.Skipped)
                     .SelectMany(item => item.Metrics)
                     .GroupBy(item => item.Detector, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            var metricItems = group.ToArray();
            var precisionMetrics = metricItems.Where(item => item.PrecisionScoringEnabled).ToArray();
            var expected = metricItems.Sum(item => item.ExpectedCount);
            var detected = metricItems.Sum(item => item.DetectedCount);
            var matched = metricItems.Sum(item => item.MatchedCount);
            var missed = metricItems.Sum(item => item.MissedCount);
            var scoredDetected = precisionMetrics.Sum(item => item.ScoredDetectionCount);
            var precisionMatched = precisionMetrics.Sum(item => item.MatchedCount);
            var extra = precisionMetrics.Sum(item => item.ExtraCount);
            var recall = expected == 0 ? 1.0 : matched / (double)expected;
            var precision = precisionMetrics.Length == 0
                ? 1.0
                : scoredDetected == 0
                    ? 0.0
                    : precisionMatched / (double)scoredDetected;
            var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);
            var grade = GradeForScore(f1);

            yield return new BenchmarkDetectorScore(
                group.Key,
                grade,
                Round(f1),
                expected,
                detected,
                matched,
                missed,
                extra,
                Round(recall),
                Round(precision),
                Round(f1),
                metricItems.Length,
                DetectorAction(group.Key, missed, extra, recall, precision));
        }
    }

    private static IEnumerable<BenchmarkFailureBucket> CreateFailureBuckets(
        IReadOnlyList<BenchmarkCaseResult> cases,
        IReadOnlyList<BenchmarkCaseScore> caseScores,
        IReadOnlyList<BenchmarkDetectorScore> detectorScores)
    {
        foreach (var item in cases.Where(item => item.Skipped))
        {
            yield return new BenchmarkFailureBucket(
                "case.skipped",
                BenchmarkFailureSeverity.Info,
                item.FixtureId,
                null,
                1,
                item.SkipReason ?? "Fixture was skipped.",
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        foreach (var item in cases.Where(item => !item.Skipped && !item.ScanSucceeded))
        {
            yield return new BenchmarkFailureBucket(
                "scan.failed",
                BenchmarkFailureSeverity.Critical,
                item.FixtureId,
                null,
                1,
                item.ErrorMessage ?? "Scan failed before benchmark assertions could be trusted.",
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        foreach (var item in cases.Where(item => item.FailedAssertionCount > 0 && !item.Skipped))
        {
            yield return new BenchmarkFailureBucket(
                "benchmark.assertions.failed",
                BenchmarkFailureSeverity.Critical,
                item.FixtureId,
                null,
                item.FailedAssertionCount,
                "Benchmark assertions failed; scanner behavior does not match the reviewed truth set.",
                item.Assertions
                    .Where(assertion => !assertion.Passed)
                    .Take(8)
                    .Select(assertion => $"{assertion.Name}: expected {assertion.Expected}, actual {assertion.Actual}")
                    .ToArray(),
                Array.Empty<string>());
        }

        foreach (var item in cases.Where(item => !item.Skipped && item.ScanSucceeded))
        {
            if (item.Counts.QualityRequiresReview || item.Counts.QualityGrade < PlanScanQualityGrade.Usable)
            {
                yield return new BenchmarkFailureBucket(
                    "scan_quality.requires_review",
                    BenchmarkFailureSeverity.Warning,
                    item.FixtureId,
                    null,
                    item.Counts.QualityIssues,
                    $"Scan quality is {item.Counts.QualityGrade} with confidence {Format(item.Counts.QualityConfidence)}.",
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            if (!item.Counts.HasReliableCalibration)
            {
                yield return new BenchmarkFailureBucket(
                    "calibration.unreliable",
                    BenchmarkFailureSeverity.Warning,
                    item.FixtureId,
                    null,
                    1,
                    "No reliable calibration was available for measured downstream placement.",
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            if (item.Counts.MeasurementOutlierCount > 0)
            {
                yield return new BenchmarkFailureBucket(
                    "measurement.outliers",
                    BenchmarkFailureSeverity.Warning,
                    item.FixtureId,
                    null,
                    item.Counts.MeasurementOutlierCount,
                    $"Matched dimension QA has {item.Counts.MeasurementOutlierCount} outlier(s) from {item.Counts.MeasurementCheckedCount} checked dimension(s).",
                    Array.Empty<string>(),
                    Array.Empty<string>());
            }

            if (item.ScanReviewQueue.Count >= HeavyScanReviewQueueThreshold)
            {
                yield return new BenchmarkFailureBucket(
                    "scan_review_queue.heavy",
                    BenchmarkFailureSeverity.Warning,
                    item.FixtureId,
                    null,
                    item.ScanReviewQueue.Count,
                    $"Scanner produced {item.ScanReviewQueue.Count} review queue item(s); visual QA workload is high before downstream trust.",
                    ScanReviewQueueEvidence(item.ScanReviewQueue).ToArray(),
                    item.ScanReviewQueue.KindCounts
                        .OrderByDescending(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(10)
                        .Select(pair => pair.Key)
                        .ToArray());
            }

            if (!item.ImportReadiness.ReadyForGeometryImport)
            {
                yield return new BenchmarkFailureBucket(
                    "import_readiness.geometry_blocked",
                    BenchmarkFailureSeverity.Critical,
                    item.FixtureId,
                    null,
                    Math.Max(1, item.ImportReadiness.BlockingIssueCodes.Count),
                    $"Geometry import is blocked with readiness grade {item.ImportReadiness.Grade}.",
                    item.ImportReadiness.Evidence.Take(8).ToArray(),
                    item.ImportReadiness.BlockingIssueCodes.Take(20).ToArray());
            }
            else if (!item.ImportReadiness.ReadyForMetricImport || !item.ImportReadiness.ReadyForRoutingImport)
            {
                var missing = new List<string>();
                if (!item.ImportReadiness.ReadyForMetricImport)
                {
                    missing.Add("metric");
                }

                if (!item.ImportReadiness.ReadyForRoutingImport)
                {
                    missing.Add("routing");
                }

                yield return new BenchmarkFailureBucket(
                    "import_readiness.partial",
                    BenchmarkFailureSeverity.Warning,
                    item.FixtureId,
                    null,
                    Math.Max(1, missing.Count),
                    $"{string.Join("/", missing)} import is not ready even though geometry import is available.",
                    item.ImportReadiness.Evidence.Take(8).ToArray(),
                    item.ImportReadiness.BlockingIssueCodes.Take(20).ToArray());
            }
            else if (item.ImportReadiness.RequiresReview)
            {
                yield return new BenchmarkFailureBucket(
                    "import_readiness.requires_review",
                    BenchmarkFailureSeverity.Info,
                    item.FixtureId,
                    null,
                    Math.Max(1, item.ImportReadiness.ReviewIssueCodes.Count),
                    $"Downstream import is ready but requires review ({item.ImportReadiness.Grade}).",
                    item.ImportReadiness.Evidence.Take(8).ToArray(),
                    item.ImportReadiness.ReviewIssueCodes.Take(20).ToArray());
            }

            foreach (var metric in item.Metrics)
            {
                if (metric.MissedCount > 0)
                {
                    yield return new BenchmarkFailureBucket(
                        $"detector.{metric.Detector}.missed_targets",
                        BenchmarkFailureSeverity.Critical,
                        item.FixtureId,
                        metric.Detector,
                        metric.MissedCount,
                        $"{metric.Detector} missed reviewed truth target(s).",
                        metric.Matches
                            .Where(match => !match.Matched)
                            .Take(8)
                            .Select(match => match.Evidence)
                            .ToArray(),
                        metric.Matches
                            .Where(match => !match.Matched)
                            .Take(20)
                            .Select(match => match.TargetId ?? $"target-{match.TargetIndex + 1}")
                            .ToArray());
                }

                if (metric.PrecisionScoringEnabled && metric.ExtraCount > 0)
                {
                    yield return new BenchmarkFailureBucket(
                        $"detector.{metric.Detector}.extra_detections",
                        metric.Precision < 0.70 ? BenchmarkFailureSeverity.Critical : BenchmarkFailureSeverity.Warning,
                        item.FixtureId,
                        metric.Detector,
                        metric.ExtraCount,
                        $"{metric.Detector} produced unmatched extra detection(s), a likely false-positive queue.",
                        new[]
                        {
                            $"precision {Format(metric.Precision)}, detected {metric.DetectedCount}, matched {metric.MatchedCount}"
                        },
                        Array.Empty<string>());
                }
                else if (!metric.PrecisionScoringEnabled && metric.ExtraCount > 0)
                {
                    yield return new BenchmarkFailureBucket(
                        $"detector.{metric.Detector}.unscored_extra_detections",
                        BenchmarkFailureSeverity.Info,
                        item.FixtureId,
                        metric.Detector,
                        metric.ExtraCount,
                        $"{metric.Detector} produced unmatched detection(s) outside precision scoring.",
                        new[]
                        {
                            $"spot-check metric, raw detected {metric.DetectedCount}, precision-scored {metric.ScoredDetectionCount}, unmatched {metric.ExtraCount}"
                        },
                        Array.Empty<string>());
                }

                if (metric.ReviewOnlyDetectionCount > 0)
                {
                    yield return new BenchmarkFailureBucket(
                        $"detector.{metric.Detector}.review_only_detections",
                        BenchmarkFailureSeverity.Info,
                        item.FixtureId,
                        metric.Detector,
                        metric.ReviewOnlyDetectionCount,
                        $"{metric.Detector} produced review-only detection(s) for labeling or training.",
                        new[]
                        {
                            $"review-only {metric.ReviewOnlyDetectionCount}, raw detected {metric.DetectedCount}, precision-scored {metric.ScoredDetectionCount}"
                        },
                        Array.Empty<string>());
                }
            }
        }

        foreach (var detector in detectorScores)
        {
            if (detector.ExpectedCount > 0 && detector.Recall < 0.80)
            {
                yield return new BenchmarkFailureBucket(
                    $"detector.{detector.Detector}.low_recall",
                    BenchmarkFailureSeverity.Critical,
                    null,
                    detector.Detector,
                    detector.MissedCount,
                    $"{detector.Detector} corpus recall is below 0.80.",
                    new[] { $"recall {Format(detector.Recall)}, matched {detector.MatchedCount}/{detector.ExpectedCount}" },
                    Array.Empty<string>());
            }

            if (detector.DetectedCount > 0 && detector.Precision < 0.80)
            {
                yield return new BenchmarkFailureBucket(
                    $"detector.{detector.Detector}.low_precision",
                    BenchmarkFailureSeverity.Warning,
                    null,
                    detector.Detector,
                    detector.ExtraCount,
                    $"{detector.Detector} corpus precision is below 0.80.",
                    new[] { $"precision {Format(detector.Precision)}, extra {detector.ExtraCount}" },
                    Array.Empty<string>());
            }
        }

        foreach (var item in caseScores.Where(item => !item.Skipped && item.Grade <= BenchmarkScoreGrade.NeedsWork))
        {
            yield return new BenchmarkFailureBucket(
                "case.score.low",
                BenchmarkFailureSeverity.Warning,
                item.FixtureId,
                null,
                1,
                $"Case readiness score is {Format(item.OverallScore)} ({item.Grade}).",
                item.BlockingReasons.Take(8).ToArray(),
                Array.Empty<string>());
        }
    }

    private static IReadOnlyList<string> CreateRecommendedActions(
        IReadOnlyList<BenchmarkFailureBucket> failures,
        IReadOnlyList<BenchmarkDetectorScore> detectors,
        IReadOnlyList<BenchmarkCaseScore> cases)
    {
        var actions = new List<string>();

        if (failures.Any(item => item.Code.Contains(".missed_targets", StringComparison.Ordinal)))
        {
            actions.Add("Open the viewer with the benchmark overlay and inspect missed truth targets first; add or tighten target bounds only after visual confirmation.");
        }

        if (failures.Any(item => item.Code.Contains(".extra_detections", StringComparison.Ordinal) || item.Code.Contains(".low_precision", StringComparison.Ordinal)))
        {
            actions.Add("Build a false-positive queue from extra detections, then tune detector filters with before/after screenshots for the same fixture.");
        }

        if (detectors.Any(item => item.Detector.Contains("object", StringComparison.OrdinalIgnoreCase) && item.Grade < BenchmarkScoreGrade.Usable))
        {
            actions.Add("Export object review/correction datasets for weak object detectors so repeated symbol groups can become deterministic label-profile rules.");
        }

        if (failures.Any(item => item.Code == "measurement.outliers" || item.Code == "calibration.unreliable"))
        {
            actions.Add("Review dimension and calibration evidence before trusting exact downstream placement coordinates.");
        }

        if (failures.Any(item => item.Code.StartsWith("import_readiness.", StringComparison.Ordinal)))
        {
            actions.Add("Inspect placement import readiness before downstream integration; resolve geometry blockers first, then metric and routing warnings.");
        }

        if (failures.Any(item => item.Code == "scan_quality.requires_review"))
        {
            actions.Add("Use the saved scan screenshots to classify quality issues as detector bugs, benchmark-truth gaps, or source-plan ambiguity.");
        }

        if (failures.Any(item => item.Code == "scan_review_queue.heavy"))
        {
            actions.Add("Triage the noisiest scan review queue kinds first, then turn recurring false-review patterns into deterministic filters or benchmark truth updates.");
        }

        if (cases.Any(item => item.Skipped))
        {
            actions.Add("Resolve skipped fixtures or keep them explicitly optional so local private PDFs do not hide missing coverage.");
        }

        if (actions.Count == 0)
        {
            actions.Add("No blocking truth-set failures were found; tighten benchmarks with more reviewed targets from real PDFs before treating the score as final.");
        }

        return actions;
    }

    private static double CalculateConsumerReadinessScore(
        IReadOnlyList<BenchmarkCaseScore> cases,
        IReadOnlyList<BenchmarkDetectorScore> detectors,
        double overallScore)
    {
        if (cases.Count == 0)
        {
            return 0;
        }

        var detectorScore = WeightedDetectorScore(detectors);
        if (detectorScore is null)
        {
            detectorScore = overallScore;
        }

        var calibrationScore = cases.Average(item => item.MeasurementScore);
        var importReadinessScore = cases.Average(item => item.ImportReadinessScore);
        return Round((overallScore * 0.40) + (detectorScore.Value * 0.25) + (calibrationScore * 0.15) + (importReadinessScore * 0.20));
    }

    private static double? WeightedDetectorScore(IReadOnlyList<BenchmarkDetectorScore> detectors)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["regions"] = 0.08,
            ["dimensions"] = 0.10,
            ["grid_axes"] = 0.06,
            ["walls"] = 0.22,
            ["rooms"] = 0.18,
            ["openings"] = 0.18,
            ["object_aggregates"] = 0.08,
            ["routing_barriers"] = 0.04,
            ["routing_passages"] = 0.03,
            ["routing_obstacles"] = 0.03,
            ["routing_room_use_hints"] = 0.03,
            ["routing_suppressed_objects"] = 0.04
        };

        var weighted = detectors
            .Select(item => (Score: item.F1, Weight: weights.TryGetValue(item.Detector, out var weight) ? weight : 0.04))
            .ToArray();
        var totalWeight = weighted.Sum(item => item.Weight);
        return totalWeight <= 0
            ? null
            : weighted.Sum(item => item.Score * item.Weight) / totalWeight;
    }

    private static double AssertionReliability(BenchmarkCaseResult item)
    {
        var total = item.Assertions.Count;
        return total == 0
            ? item.Passed ? 1.0 : 0.0
            : item.PassedAssertionCount / (double)total;
    }

    private static double QualityScore(BenchmarkCounts counts)
    {
        var gradeScore = counts.QualityGrade switch
        {
            PlanScanQualityGrade.Strong => 1.0,
            PlanScanQualityGrade.Usable => 0.86,
            PlanScanQualityGrade.ReviewRequired => 0.62,
            PlanScanQualityGrade.Poor => 0.35,
            _ => 0.20
        };
        var score = (Clamp01(counts.QualityConfidence) * 0.70) + (gradeScore * 0.30);
        if (counts.QualityRequiresReview)
        {
            score *= 0.90;
        }

        if (counts.QualityIssues > 0)
        {
            score *= Math.Max(0.70, 1.0 - (counts.QualityIssues * 0.03));
        }

        return Clamp01(score);
    }

    private static double MeasurementScore(BenchmarkCounts counts)
    {
        var score = counts.HasReliableCalibration ? 1.0 : 0.62;
        if (counts.MeasurementCheckedCount > 0)
        {
            var outlierRatio = counts.MeasurementOutlierCount / (double)counts.MeasurementCheckedCount;
            score *= Math.Max(0.25, 1.0 - outlierRatio);
        }
        else
        {
            score *= counts.HasReliableCalibration ? 0.92 : 0.75;
        }

        if (counts.MeasurementScaleSpreadRatio is { } spread and > 1)
        {
            score *= 1.0 / Math.Min(2.0, spread);
        }

        return Clamp01(score);
    }

    private static IEnumerable<string> ScanReviewQueueEvidence(ScanReviewQueueSummary summary)
    {
        yield return $"total review items {summary.Count}";

        foreach (var pair in summary.KindCounts
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            yield return $"{pair.Key}: {pair.Value}";
        }
    }

    private static string DetectorAction(string detector, int missed, int extra, double recall, double precision)
    {
        if (missed > 0 && extra > 0)
        {
            return $"Review {detector} misses and false positives in the viewer; tune matching and filtering together.";
        }

        if (missed > 0 || recall < 0.85)
        {
            return $"Prioritize missed {detector} targets from the reviewed truth set.";
        }

        if (extra > 0 || precision < 0.85)
        {
            return $"Build a false-positive queue for {detector} and tighten detector filters.";
        }

        return $"Keep {detector} covered with additional real-plan truth targets.";
    }

    private static BenchmarkScoreGrade GradeForScore(double score) =>
        score switch
        {
            >= 0.95 => BenchmarkScoreGrade.Strong,
            >= 0.85 => BenchmarkScoreGrade.Usable,
            >= 0.70 => BenchmarkScoreGrade.ReviewRequired,
            >= 0.50 => BenchmarkScoreGrade.NeedsWork,
            _ => BenchmarkScoreGrade.Blocked
        };

    private static BenchmarkScoreGrade CapGrade(BenchmarkScoreGrade grade, BenchmarkScoreGrade maximum) =>
        grade > maximum ? maximum : grade;

    private static double Clamp01(double value) =>
        Math.Clamp(value, 0, 1);

    private static double Round(double value) =>
        Math.Round(Clamp01(value), 4, MidpointRounding.AwayFromZero);

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed record BenchmarkCaseScore(
    string FixtureId,
    BenchmarkScoreGrade Grade,
    double OverallScore,
    double TargetF1,
    double TargetRecall,
    double TargetPrecision,
    double AssertionReliability,
    double ScanQualityScore,
    double MeasurementScore,
    double ImportReadinessScore,
    bool ReadyForGeometryImport,
    bool ReadyForMetricImport,
    bool ReadyForRoutingImport,
    int ExpectedTargetCount,
    int MatchedTargetCount,
    int MissedTargetCount,
    int ExtraDetectionCount,
    int FailedAssertionCount,
    PlanScanQualityGrade QualityGrade,
    double QualityConfidence,
    bool RequiresReview,
    bool Skipped,
    IReadOnlyList<string> BlockingReasons);

public sealed record BenchmarkDetectorScore(
    string Detector,
    BenchmarkScoreGrade Grade,
    double Score,
    int ExpectedCount,
    int DetectedCount,
    int MatchedCount,
    int MissedCount,
    int ExtraCount,
    double Recall,
    double Precision,
    double F1,
    int CaseCount,
    string RecommendedAction);

public sealed record BenchmarkFailureBucket(
    string Code,
    BenchmarkFailureSeverity Severity,
    string? FixtureId,
    string? Detector,
    int Count,
    string Message,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> TargetIds);
