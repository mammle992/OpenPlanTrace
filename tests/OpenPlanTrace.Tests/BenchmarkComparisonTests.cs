namespace OpenPlanTrace.Tests;

public sealed class BenchmarkComparisonTests
{
    [Fact]
    public void Compare_DetectsRegressionSignalsAndCountDeltas()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "case-1",
                    passed: true,
                    failedAssertions: 0,
                    durationMilliseconds: 100,
                    walls: 10,
                    rooms: 2,
                    openings: 3,
                    objects: 4,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.92,
                    qualityIssues: 1,
                    diagnosticErrors: 0)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "case-1",
                    passed: false,
                    failedAssertions: 2,
                    durationMilliseconds: 340,
                    walls: 8,
                    rooms: 1,
                    openings: 3,
                    objects: 4,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityConfidence: 0.72,
                    qualityIssues: 3,
                    diagnosticErrors: 1)
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                QualityConfidenceRegressionThreshold = 0.05,
                DurationRegressionRatio = 2,
                DurationRegressionMinimumMilliseconds = 100
            });

        Assert.False(comparison.Passed);
        Assert.Equal(1, comparison.MatchedCaseCount);
        Assert.Equal(0, comparison.AddedCaseCount);
        Assert.Equal(0, comparison.RemovedCaseCount);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.failed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "assertions.failed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.confidence" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.issues" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "diagnostics.errors" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "duration" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var caseComparison = Assert.Single(comparison.Cases);
        Assert.Equal(BenchmarkComparisonCaseStatus.Matched, caseComparison.Status);
        Assert.Equal(-2, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "walls").Delta);
        Assert.Equal(-1, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "rooms").Delta);
        Assert.Equal(-1, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "roomClusters").Delta);
    }

    [Fact]
    public void Compare_DetectsMeasurementConsistencyRegressionSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 5,
                    measurementConsistent: 5,
                    measurementOutliers: 0,
                    measurementSelectedScale: 50,
                    measurementMedianScale: 50,
                    measurementSpread: 1.1,
                    measurementConfidence: 0.82)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 5,
                    measurementConsistent: 2,
                    measurementOutliers: 3,
                    measurementSelectedScale: 50,
                    measurementMedianScale: 76,
                    measurementSpread: 2.4,
                    measurementConfidence: 0.79)
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                MeasurementOutlierRatioRegressionThreshold = 0.1,
                MeasurementSpreadRegressionMinimumDelta = 0.25,
                MeasurementSpreadRegressionRatio = 1.25
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal => signal.Code == "measurement.outlier_ratio" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "measurement.spread" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        var caseComparison = Assert.Single(comparison.Cases);
        Assert.Equal(3, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "measurementOutliers").Delta);
        Assert.Equal(-3, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "measurementConsistent").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("measurement.spread", markdown);
        Assert.Contains("measurementOutliers +3", markdown);
    }

    [Fact]
    public void Compare_DetectsQualityReviewRequirementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "quality-review-added",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: false),
                Case(
                    "quality-review-cleared",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: true)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "quality-review-added",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: true),
                Case(
                    "quality-review-cleared",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: false)
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "quality-review-added"
            && signal.Code == "quality.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "quality-review-cleared"
            && signal.Code == "quality.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
    }

    [Fact]
    public void Compare_DetectsScanReviewQueueRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "review-growth",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.ObjectGroupReview, 5),
                        (ScanReviewQueueKinds.WallGraphGapReview, 3))),
                Case(
                    "review-shrink",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.OpeningReview, 12),
                        (ScanReviewQueueKinds.ObjectGroupReview, 8)))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "review-growth",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.ObjectGroupReview, 6),
                        (ScanReviewQueueKinds.SuppressedWallPatternReview, 5),
                        (ScanReviewQueueKinds.WallGraphGapReview, 13))),
                Case(
                    "review-shrink",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.OpeningReview, 2),
                        (ScanReviewQueueKinds.ObjectGroupReview, 3)))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScanReviewQueueItemRegressionMinimumDelta = 5,
                ScanReviewQueueItemRegressionRatio = 1.5,
                ScanReviewQueueKindRegressionMinimumDelta = 5,
                ScanReviewQueueKindRegressionRatio = 1.5
            });

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.items"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.kind.wallgraphgapreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.kind.suppressedwallpatternreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-shrink"
            && signal.Code == "scan_review_queue.items"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-shrink"
            && signal.Code == "scan_review_queue.kind.openingreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var growthCase = comparison.Cases.Single(item => item.FixtureId == "review-growth");
        Assert.Equal(16, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueueItems").Delta);
        Assert.Equal(10, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueue.WallGraphGapReview").Delta);
        Assert.Equal(5, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueue.SuppressedWallPatternReview").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("scan_review_queue.items", markdown);
        Assert.Contains("scanReviewQueueItems +16", markdown);
    }

    [Fact]
    public void Compare_DetectsImportReadinessRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "metric-loss",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 4,
                    measurementConsistent: 4,
                    measurementOutliers: 0),
                Case(
                    "geometry-recovery",
                    passed: true,
                    failedAssertions: 0,
                    walls: 0,
                    rooms: 0,
                    importReadiness: Readiness(walls: 0, rooms: 0, measurementOutliers: 0))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "metric-loss",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 4,
                    measurementConsistent: 2,
                    measurementOutliers: 2),
                Case(
                    "geometry-recovery",
                    passed: true,
                    failedAssertions: 0,
                    walls: 6,
                    rooms: 2,
                    importReadiness: Readiness(walls: 6, rooms: 2, measurementOutliers: 0))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ImportReadinessScoreRegressionThreshold = 0.01
            });

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.metric_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.score"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "geometry-recovery"
            && signal.Code == "import_readiness.geometry_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "geometry-recovery"
            && signal.Code == "import_readiness.routing_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("import_readiness.metric_ready", markdown);
        Assert.Contains("import_readiness.geometry_ready", markdown);
    }

    [Fact]
    public void Compare_DetectsDetectorMetricRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "tagged-industrial",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 4, detected: 4, matched: 4),
                        Metric("openings", expected: 2, detected: 5, matched: 1)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "tagged-industrial",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 4, detected: 5, matched: 2),
                        Metric("openings", expected: 2, detected: 2, matched: 2)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                DetectorRecallRegressionThreshold = 0.1,
                DetectorPrecisionRegressionThreshold = 0.1,
                DetectorF1RegressionThreshold = 0.1
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.recall"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression
            && signal.Baseline!.Contains("matched 4/4", StringComparison.Ordinal)
            && signal.Candidate!.Contains("matched 2/4", StringComparison.Ordinal));
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.precision"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.f1"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.recall"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.precision"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.f1"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("detector_metric.object_groups.recall", markdown);
        Assert.Contains("detector_metric.openings.precision", markdown);
    }

    [Fact]
    public void Compare_SkipsPrecisionSignalsForSpotCheckMetrics()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-spot-check",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 1, detected: 2, matched: 1, precisionScoring: false)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-spot-check",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 1, detected: 8, matched: 1, precisionScoring: false)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "detector_metric.object_groups.precision");
        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "detector_metric.object_groups.f1");
        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections");
    }

    [Fact]
    public void Compare_DetectsScoreboardReadinessRegressionSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.95,
                    metrics: new[]
                    {
                        Metric("walls", expected: 6, detected: 6, matched: 6),
                        Metric("rooms", expected: 4, detected: 4, matched: 4),
                        Metric("openings", expected: 3, detected: 3, matched: 3)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityConfidence: 0.78,
                    metrics: new[]
                    {
                        Metric("walls", expected: 6, detected: 12, matched: 3),
                        Metric("rooms", expected: 4, detected: 2, matched: 1),
                        Metric("openings", expected: 3, detected: 8, matched: 1)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScoreboardOverallRegressionThreshold = 0.01,
                ScoreboardConsumerReadinessRegressionThreshold = 0.01
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.downstream_ready" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.overall" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.consumer_readiness" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.missed_targets" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("scoreboard.consumer_readiness", markdown);
        Assert.Contains("scoreboard.missed_targets", markdown);
    }

    [Fact]
    public void Compare_DetectsScoreboardReadinessImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "object-heavy",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.ReviewRequired,
                    qualityConfidence: 0.62,
                    metrics: new[]
                    {
                        Metric("object_aggregates", expected: 4, detected: 8, matched: 1)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "object-heavy",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.94,
                    metrics: new[]
                    {
                        Metric("object_aggregates", expected: 4, detected: 4, matched: 4)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScoreboardOverallRegressionThreshold = 0.01,
                ScoreboardConsumerReadinessRegressionThreshold = 0.01
            });

        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.overall" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.consumer_readiness" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.missed_targets" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
    }

    [Fact]
    public void Compare_ReportsAddedRemovedAndRecoveredCases()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case("recovered", passed: false, failedAssertions: 1),
                Case("removed", passed: true, failedAssertions: 0)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case("recovered", passed: true, failedAssertions: 0),
                Case("added", passed: true, failedAssertions: 0)
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Equal(1, comparison.MatchedCaseCount);
        Assert.Equal(1, comparison.AddedCaseCount);
        Assert.Equal(1, comparison.RemovedCaseCount);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.recovered" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.added" && signal.Severity == BenchmarkComparisonSignalSeverity.Info);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.removed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Cases, item => item.FixtureId == "added" && item.Status == BenchmarkComparisonCaseStatus.Added);
        Assert.Contains(comparison.Cases, item => item.FixtureId == "removed" && item.Status == BenchmarkComparisonCaseStatus.Removed);
    }

    [Fact]
    public void Compare_TreatsSkippedOptionalCaseAsInformational()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[] { Case("optional-pdf", passed: true, failedAssertions: 0) });
        var skipped = PlanBenchmarkEvaluator.SkippedFixture(
            new BenchmarkFixture
            {
                Id = "optional-pdf",
                SourcePath = "%USERPROFILE%/Downloads/missing.pdf",
                Optional = true
            },
            "Optional PDF fixture was unavailable.",
            TimeSpan.FromMilliseconds(1));
        var candidate = BenchmarkRunResult.Create("candidate", new[] { skipped });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.True(comparison.Passed);
        Assert.Equal(0, comparison.RegressionCount);
        var signal = Assert.Single(comparison.Signals);
        Assert.Equal("case.skipped", signal.Code);
        Assert.Equal(BenchmarkComparisonSignalSeverity.Info, signal.Severity);
        var caseComparison = Assert.Single(comparison.Cases);
        Assert.False(caseComparison.BaselineSkipped);
        Assert.True(caseComparison.CandidateSkipped);
        Assert.Equal("Optional PDF fixture was unavailable.", caseComparison.CandidateSkipReason);
        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("PASS -> SKIP", markdown);
    }

    [Fact]
    public void MarkdownReport_IncludesRegressionSignalsAndCaseDeltas()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[] { Case("case-1", passed: true, failedAssertions: 0, walls: 10) });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[] { Case("case-1", passed: false, failedAssertions: 1, walls: 8) });
        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);

        Assert.Contains("# OpenPlanTrace Benchmark Comparison", markdown);
        Assert.Contains("Status: REGRESSION", markdown);
        Assert.Contains("case.failed", markdown);
        Assert.Contains("walls -2", markdown);
    }

    private static BenchmarkCaseResult Case(
        string fixtureId,
        bool passed,
        int failedAssertions,
        double durationMilliseconds = 100,
        int walls = 10,
        int rooms = 1,
        int openings = 1,
        int objects = 1,
        PlanScanQualityGrade qualityGrade = PlanScanQualityGrade.Usable,
        double qualityConfidence = 0.75,
        bool? qualityRequiresReview = null,
        int qualityIssues = 0,
        int diagnosticErrors = 0,
        int measurementChecked = 0,
        int measurementConsistent = 0,
        int measurementOutliers = 0,
        double? measurementSelectedScale = null,
        double? measurementMedianScale = null,
        double? measurementSpread = null,
        double measurementConfidence = 0,
        IReadOnlyList<BenchmarkDetectorMetrics>? metrics = null,
        PlanImportReadiness? importReadiness = null,
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
            durationMilliseconds,
            Counts(
                walls,
                rooms,
                openings,
                objects,
                qualityGrade,
                qualityConfidence,
                qualityRequiresReview,
                qualityIssues,
                diagnosticErrors,
                measurementChecked,
                measurementConsistent,
                measurementOutliers,
                measurementSelectedScale,
                measurementMedianScale,
                measurementSpread,
                measurementConfidence),
            assertions,
            passed ? null : "Synthetic failure.")
        {
            Metrics = metrics ?? Array.Empty<BenchmarkDetectorMetrics>(),
            ImportReadiness = importReadiness ?? Readiness(walls, rooms, measurementOutliers),
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
        int walls,
        int rooms,
        int measurementOutliers)
    {
        var geometryReady = walls > 0 && rooms > 0;
        var metricReady = geometryReady && measurementOutliers == 0;
        var routingReady = geometryReady;
        var score = geometryReady
            ? metricReady ? 0.96 : 0.78
            : 0.35;
        var grade = !geometryReady
            ? "Blocked"
            : metricReady
                ? "Strong"
                : "ReviewRequired";

        return new PlanImportReadiness(
            grade,
            score,
            geometryReady,
            metricReady,
            routingReady,
            !metricReady,
            metricReady
                ? Array.Empty<string>()
                : new[] { geometryReady ? "placement.import.measurement_outliers" : "placement.import.low_coordinate_ready_ratio" },
            metricReady
                ? Array.Empty<string>()
                : new[] { geometryReady ? "placement.measurement_outliers.require_review" : "placement.import.low_coordinate_ready_ratio" },
            new[] { metricReady ? "Synthetic import-ready fixture." : "Synthetic import readiness requires review." },
            new[] { $"synthetic import readiness {grade}" });
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
                $"target-{index + 1}",
                index < matched,
                index < matched ? $"detection-{index + 1}" : null,
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
            PrecisionScoringEnabled = precisionScoring
        };
    }

    private static BenchmarkCounts Counts(
        int walls,
        int rooms,
        int openings,
        int objects,
        PlanScanQualityGrade qualityGrade,
        double qualityConfidence,
        bool? qualityRequiresReview,
        int qualityIssues,
        int diagnosticErrors,
        int measurementChecked,
        int measurementConsistent,
        int measurementOutliers,
        double? measurementSelectedScale,
        double? measurementMedianScale,
        double? measurementSpread,
        double measurementConfidence) =>
        new(
            Pages: 1,
            Regions: 4,
            Dimensions: 0,
            Annotations: 0,
            AnnotationReferences: 0,
            GridAxes: 0,
            GridBaySpacings: 0,
            SurfacePatterns: 0,
            Walls: walls,
            WallNodes: walls * 2,
            WallEdges: walls,
            Rooms: rooms,
            RoomAdjacencies: 0,
            RoomClusters: rooms,
            Openings: openings,
            Objects: objects,
            ObjectGroups: 0,
            ObjectAggregates: 0,
            RoutingItems: 0,
            RoutingSuppressedObjects: 0,
            Diagnostics: diagnosticErrors,
            DiagnosticWarnings: 0,
            DiagnosticErrors: diagnosticErrors,
            QualityGrade: qualityGrade,
            QualityConfidence: qualityConfidence,
            QualityRequiresReview: qualityRequiresReview ?? qualityGrade < PlanScanQualityGrade.Usable,
            QualityIssues: qualityIssues,
            HasReliableCalibration: true,
            MeasurementCheckedCount: measurementChecked,
            MeasurementConsistentCount: measurementConsistent,
            MeasurementOutlierCount: measurementOutliers,
            MeasurementSelectedMillimetersPerDrawingUnit: measurementSelectedScale,
            MeasurementMedianMillimetersPerDrawingUnit: measurementMedianScale,
            MeasurementScaleSpreadRatio: measurementSpread,
            MeasurementConsistencyConfidence: measurementConfidence);
}
