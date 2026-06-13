namespace OpenPlanTrace.Tests;

public sealed class BenchmarkScoreboardTests
{
    [Fact]
    public void Create_AttachesProfessionalReadinessScoreboard()
    {
        var run = BenchmarkRunResult.Create(
            "truth loop",
            new[]
            {
                Case(
                    "easy-plan",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.94,
                    metrics: new[]
                    {
                        Metric("walls", expected: 4, detected: 4, matched: 4),
                        Metric("rooms", expected: 2, detected: 2, matched: 2),
                        Metric("openings", expected: 1, detected: 1, matched: 1)
                    })
            });

        Assert.Equal(BenchmarkScoreboard.CurrentSchemaVersion, run.Scoreboard.SchemaVersion);
        Assert.Equal(BenchmarkScoreGrade.Strong, run.Scoreboard.Grade);
        Assert.True(run.Scoreboard.ReadyForDownstreamUse);
        Assert.True(run.Scoreboard.ConsumerReadinessScore >= 0.95);
        Assert.Equal(7, run.Scoreboard.ExpectedTargetCount);
        Assert.Equal(7, run.Scoreboard.MatchedTargetCount);
        Assert.Equal(0, run.Scoreboard.MissedTargetCount);
        Assert.Equal(0, run.Scoreboard.ExtraDetectionCount);
        Assert.Contains(run.Scoreboard.Detectors, detector => detector.Detector == "walls" && detector.Grade == BenchmarkScoreGrade.Strong);
    }

    [Fact]
    public void Create_BucketsMissedTargetsExtrasAndQualityFailures()
    {
        var run = BenchmarkRunResult.Create(
            "truth loop",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: false,
                    failedAssertions: 2,
                    qualityGrade: PlanScanQualityGrade.ReviewRequired,
                    qualityConfidence: 0.68,
                    qualityRequiresReview: true,
                    qualityIssues: 4,
                    hasReliableCalibration: false,
                    measurementChecked: 5,
                    measurementOutliers: 2,
                    metrics: new[]
                    {
                        Metric("walls", expected: 4, detected: 6, matched: 2),
                        Metric("openings", expected: 3, detected: 7, matched: 1),
                        Metric("object_aggregates", expected: 2, detected: 5, matched: 1)
                    })
            });

        Assert.False(run.Scoreboard.ReadyForDownstreamUse);
        Assert.Equal(BenchmarkScoreGrade.Blocked, run.Scoreboard.Grade);
        Assert.Equal(5, run.Scoreboard.MissedTargetCount);
        Assert.Equal(14, run.Scoreboard.ExtraDetectionCount);
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "benchmark.assertions.failed" && bucket.Severity == BenchmarkFailureSeverity.Critical);
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "detector.walls.missed_targets");
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "detector.openings.extra_detections");
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "calibration.unreliable");
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "measurement.outliers");
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "import_readiness.partial");
        Assert.Contains(run.Scoreboard.RecommendedNextActions, action => action.Contains("false-positive queue", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(run.Scoreboard.RecommendedNextActions, action => action.Contains("object review/correction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Create_TreatsSpotCheckExtrasAsInformational()
    {
        var run = BenchmarkRunResult.Create(
            "spot-check loop",
            new[]
            {
                Case(
                    "industrial-spot-check",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.9,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 1, detected: 5, matched: 1, precisionScoring: false)
                    })
            });

        Assert.True(run.Scoreboard.ReadyForDownstreamUse);
        Assert.Equal(0, run.Scoreboard.ExtraDetectionCount);
        Assert.DoesNotContain(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "detector.object_groups.extra_detections");
        Assert.DoesNotContain(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "detector.object_groups.low_precision");
        Assert.Contains(run.Scoreboard.FailureBuckets, bucket =>
            bucket.Code == "detector.object_groups.unscored_extra_detections"
            && bucket.Severity == BenchmarkFailureSeverity.Info
            && bucket.Count == 4);
        Assert.Contains(run.ReviewQueue, item =>
            item.Kind == BenchmarkReviewQueueKind.SpotCheckExtra
            && item.Detector == "object_groups"
            && !item.PrecisionScoringEnabled);
        Assert.Contains(run.Scoreboard.Detectors, detector =>
            detector.Detector == "object_groups"
            && detector.Grade == BenchmarkScoreGrade.Strong
            && detector.ExtraCount == 0);
    }

    [Fact]
    public void Create_BucketsHeavyScanReviewQueueWithoutBlockingReadiness()
    {
        var run = BenchmarkRunResult.Create(
            "review workload loop",
            new[]
            {
                Case(
                    "hard-plan",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.94,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.WallGraphGapReview, 42),
                        (ScanReviewQueueKinds.ObjectGroupReview, 28),
                        (ScanReviewQueueKinds.ObjectAggregateReview, 8)),
                    metrics: new[]
                    {
                        Metric("walls", expected: 4, detected: 4, matched: 4),
                        Metric("rooms", expected: 2, detected: 2, matched: 2)
                    })
            });

        Assert.True(run.Scoreboard.ReadyForDownstreamUse);
        var bucket = Assert.Single(run.Scoreboard.FailureBuckets, bucket => bucket.Code == "scan_review_queue.heavy");
        Assert.Equal(BenchmarkFailureSeverity.Warning, bucket.Severity);
        Assert.Equal(78, bucket.Count);
        Assert.Contains("WallGraphGapReview: 42", bucket.Evidence);
        Assert.Contains("WallGraphGapReview", bucket.TargetIds);
        Assert.Contains(run.Scoreboard.Cases, item =>
            item.FixtureId == "hard-plan"
            && item.BlockingReasons.Any(reason => reason.Contains("scanner review item", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(run.Scoreboard.RecommendedNextActions, action =>
            action.Contains("scan review queue", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarkdownReport_IncludesReadinessScoreboard()
    {
        var run = BenchmarkRunResult.Create(
            "report truth loop",
            new[]
            {
                Case(
                    "case-1",
                    passed: false,
                    failedAssertions: 1,
                    metrics: new[] { Metric("rooms", expected: 2, detected: 1, matched: 1) })
            });

        var markdown = BenchmarkMarkdownReport.Create(run);

        Assert.Contains("## Readiness Scoreboard", markdown);
        Assert.Contains("Consumer readiness score", markdown);
        Assert.Contains("Import readiness", markdown);
        Assert.Contains("### Detector Grades", markdown);
        Assert.Contains("detector.rooms.missed_targets", markdown);
        Assert.Contains("### Next Actions", markdown);
    }

    private static BenchmarkCaseResult Case(
        string fixtureId,
        bool passed,
        int failedAssertions,
        PlanScanQualityGrade qualityGrade = PlanScanQualityGrade.Usable,
        double qualityConfidence = 0.84,
        bool qualityRequiresReview = false,
        int qualityIssues = 0,
        bool hasReliableCalibration = true,
        int measurementChecked = 2,
        int measurementOutliers = 0,
        IReadOnlyList<BenchmarkDetectorMetrics>? metrics = null,
        ScanReviewQueueSummary? scanReviewQueue = null)
    {
        var assertions = new List<BenchmarkAssertionResult>
        {
            new("scan.completed", true, "scan succeeds", "scan succeeded", "Input was scanned successfully.")
        };
        for (var index = 0; index < failedAssertions; index++)
        {
            assertions.Add(new BenchmarkAssertionResult(
                $"failure.{index + 1}",
                false,
                "expected",
                "actual",
                "Synthetic benchmark failure."));
        }

        return new BenchmarkCaseResult(
            fixtureId,
            fixtureId,
            $"{fixtureId}.pdf",
            passed,
            true,
            100,
            Counts(
                qualityGrade,
                qualityConfidence,
                qualityRequiresReview,
                qualityIssues,
                hasReliableCalibration,
                measurementChecked,
                measurementOutliers),
            assertions,
            passed ? null : "Synthetic failure.")
        {
            Metrics = metrics ?? Array.Empty<BenchmarkDetectorMetrics>(),
            ImportReadiness = Readiness(hasReliableCalibration, measurementOutliers),
            ScanReviewQueue = scanReviewQueue ?? ScanReviewQueueSummary.Empty
        };
    }

    private static ScanReviewQueueSummary ReviewQueue(params (string Kind, int Count)[] counts)
    {
        var kindCounts = counts
            .Where(item => item.Count > 0 && !string.IsNullOrWhiteSpace(item.Kind))
            .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);
        var total = kindCounts.Values.Sum();
        return new ScanReviewQueueSummary(
            total,
            kindCounts,
            total == 0
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [DiagnosticSeverity.Info.ToString()] = total
                });
    }

    private static PlanImportReadiness Readiness(
        bool hasReliableCalibration = true,
        int measurementOutliers = 0)
    {
        var metricReady = hasReliableCalibration && measurementOutliers == 0;
        var grade = metricReady ? "Strong" : "ReviewRequired";
        return new PlanImportReadiness(
            grade,
            metricReady ? 0.98 : 0.78,
            ReadyForGeometryImport: true,
            ReadyForMetricImport: metricReady,
            ReadyForRoutingImport: true,
            RequiresReview: !metricReady,
            BlockingIssueCodes: metricReady
                ? Array.Empty<string>()
                : new[] { hasReliableCalibration ? "placement.import.measurement_outliers" : "placement.import.metric_calibration_unavailable" },
            ReviewIssueCodes: metricReady
                ? Array.Empty<string>()
                : new[] { hasReliableCalibration ? "placement.measurement_outliers.require_review" : "placement.metric_coordinates.unavailable" },
            RecommendedActions: new[] { metricReady ? "Synthetic import-ready fixture." : "Synthetic import readiness requires metric review." },
            Evidence: new[] { $"synthetic import readiness {grade}" });
    }

    private static BenchmarkDetectorMetrics Metric(
        string detector,
        int expected,
        int detected,
        int matched,
        bool precisionScoring = true)
    {
        var missed = Math.Max(0, expected - matched);
        var extra = Math.Max(0, detected - matched);
        var recall = expected == 0 ? 1.0 : matched / (double)expected;
        var precision = detected == 0 ? expected == 0 ? 1.0 : 0.0 : matched / (double)detected;
        var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);
        var matches = Enumerable.Range(0, expected)
            .Select(index => new BenchmarkTargetMatchResult(
                index,
                $"{detector}-target-{index + 1}",
                index < matched,
                index < matched ? $"{detector}-detection-{index + 1}" : null,
                index < matched ? 1 : 0,
                index < matched ? "synthetic match" : "synthetic miss"))
            .ToArray();

        return new BenchmarkDetectorMetrics(
            detector,
            expected,
            detected,
            matched,
            missed,
            extra,
            recall,
            precision,
            f1,
            matches)
        {
            PrecisionScoringEnabled = precisionScoring,
            ExtraDetections = Enumerable.Range(0, extra)
                .Select(index => new BenchmarkDetectionSummary(
                    $"{detector}-extra-{index + 1}",
                    1,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    Array.Empty<string>(),
                    "synthetic extra detection"))
                .ToArray()
        };
    }

    private static BenchmarkCounts Counts(
        PlanScanQualityGrade qualityGrade,
        double qualityConfidence,
        bool qualityRequiresReview,
        int qualityIssues,
        bool hasReliableCalibration,
        int measurementChecked,
        int measurementOutliers) =>
        new(
            Pages: 1,
            Regions: 4,
            Dimensions: 2,
            Annotations: 1,
            AnnotationReferences: 0,
            GridAxes: 2,
            GridBaySpacings: 1,
            SurfacePatterns: 0,
            Walls: 8,
            WallNodes: 12,
            WallEdges: 8,
            Rooms: 2,
            RoomAdjacencies: 1,
            RoomClusters: 1,
            Openings: 1,
            Objects: 4,
            ObjectGroups: 1,
            ObjectAggregates: 1,
            RoutingItems: 5,
            RoutingSuppressedObjects: 2,
            Diagnostics: 0,
            DiagnosticWarnings: 0,
            DiagnosticErrors: 0,
            QualityGrade: qualityGrade,
            QualityConfidence: qualityConfidence,
            QualityRequiresReview: qualityRequiresReview,
            QualityIssues: qualityIssues,
            HasReliableCalibration: hasReliableCalibration,
            MeasurementCheckedCount: measurementChecked,
            MeasurementConsistentCount: Math.Max(0, measurementChecked - measurementOutliers),
            MeasurementOutlierCount: measurementOutliers,
            MeasurementSelectedMillimetersPerDrawingUnit: 50,
            MeasurementMedianMillimetersPerDrawingUnit: 50,
            MeasurementScaleSpreadRatio: 1.05,
            MeasurementConsistencyConfidence: 0.9);
}
