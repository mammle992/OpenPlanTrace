namespace OpenPlanTrace;

public static class PlanBenchmarkEvaluator
{
    public static BenchmarkCaseResult Evaluate(
        BenchmarkFixture fixture,
        PlanScanResult result,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(result);

        var counts = BenchmarkCounts.From(result);
        var importReadiness = PlanImportReadiness.FromScanResult(result);
        var scanReviewQueue = ScanReviewQueueSummary.From(result);
        var assertions = new List<BenchmarkAssertionResult>
        {
            Pass("scan.completed", "scan succeeds", "scan succeeded", "Input was scanned successfully.")
        };

        AddCountAssertions(assertions, fixture.Expectations, counts);
        AddPerformanceAssertions(assertions, fixture.Expectations, result, duration);
        AddQualityAssertions(assertions, fixture.Expectations, result);
        AddScanReviewQueueAssertions(assertions, fixture.Expectations, scanReviewQueue);
        AddImportReadinessAssertions(assertions, fixture.Expectations, importReadiness);
        AddMeasurementAssertions(assertions, fixture.Expectations, result);
        AddDiagnosticAssertions(assertions, fixture.Expectations, result);
        AddRequiredRegionAssertions(assertions, fixture.Expectations, result);
        AddRequiredAnnotationAssertions(assertions, fixture.Expectations, result);
        AddRequiredGridAssertions(assertions, fixture.Expectations, result);
        AddRequiredOpeningAssertions(assertions, fixture.Expectations, result);
        AddRequiredObjectAssertions(assertions, fixture.Expectations, result);
        AddRoomLabelAssertions(assertions, fixture.Expectations, result);
        AddRequiredLayerAssertions(assertions, fixture.Expectations, result);
        AddCalibrationAssertion(assertions, fixture.Expectations, result);
        var metrics = AddDetectorMetricAssertions(assertions, fixture.Expectations, result);

        return new BenchmarkCaseResult(
            FixtureId(fixture),
            fixture.Name,
            fixture.SourcePath,
            assertions.All(assertion => assertion.Passed),
            true,
            duration.TotalMilliseconds,
            counts,
            assertions.ToArray(),
            null)
        {
            Properties = FixtureProperties(fixture),
            Metrics = metrics,
            QualityIssues = QualityIssueSummaries(result),
            DiagnosticIssues = DiagnosticIssueSummaries(result),
            Stages = StageSummaries(result),
            ImportReadiness = importReadiness,
            ScanReviewQueue = scanReviewQueue
        };
    }

    public static BenchmarkCaseResult FailedScan(
        BenchmarkFixture fixture,
        string errorMessage,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var assertions = new[]
        {
            Fail("scan.completed", "scan succeeds", "scan failed", errorMessage)
        };

        return new BenchmarkCaseResult(
            FixtureId(fixture),
            fixture.Name,
            fixture.SourcePath,
            false,
            false,
            duration.TotalMilliseconds,
            BenchmarkCounts.Empty,
            assertions,
            errorMessage)
        {
            Properties = FixtureProperties(fixture)
        };
    }

    public static BenchmarkCaseResult SkippedFixture(
        BenchmarkFixture fixture,
        string reason,
        TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        return new BenchmarkCaseResult(
            FixtureId(fixture),
            fixture.Name,
            fixture.SourcePath,
            false,
            false,
            duration.TotalMilliseconds,
            BenchmarkCounts.Empty,
            Array.Empty<BenchmarkAssertionResult>(),
            null)
        {
            Properties = FixtureProperties(fixture),
            Skipped = true,
            SkipReason = string.IsNullOrWhiteSpace(reason) ? "Fixture skipped." : reason
        };
    }

    private static IReadOnlyDictionary<string, string> FixtureProperties(BenchmarkFixture fixture) =>
        fixture.Properties ?? new Dictionary<string, string>();

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static IReadOnlyList<BenchmarkCaseIssueSummary> QualityIssueSummaries(PlanScanResult result) =>
        result.Quality.Issues
            .GroupBy(
                issue => new
                {
                    issue.Code,
                    issue.Severity,
                    issue.Message
                })
            .OrderByDescending(group => group.Key.Severity)
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var properties = group
                    .SelectMany(issue => issue.Properties)
                    .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToDictionary(pair => pair.Key, pair => pair.First().Value, StringComparer.OrdinalIgnoreCase);

                return new BenchmarkCaseIssueSummary(
                    Clean(group.Key.Code) ?? "quality.issue",
                    group.Key.Severity,
                    "quality",
                    DiagnosticScope.Document.ToString(),
                    group.Count(),
                    Clean(group.Key.Message) ?? string.Empty,
                    Array.Empty<int>(),
                    group.Max(issue => issue.Confidence.Value),
                    0,
                    Array.Empty<string>(),
                    properties);
            })
            .ToArray();

    private static IReadOnlyList<BenchmarkCaseIssueSummary> DiagnosticIssueSummaries(PlanScanResult result) =>
        result.Diagnostics.Messages
            .GroupBy(
                diagnostic => new
                {
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Stage,
                    diagnostic.Scope,
                    diagnostic.Message
                })
            .OrderByDescending(group => group.Key.Severity)
            .ThenByDescending(group => group.Count())
            .ThenBy(group => group.Key.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sourceIds = group
                    .SelectMany(diagnostic => diagnostic.SourcePrimitiveIds)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var properties = group
                    .SelectMany(diagnostic => diagnostic.Properties)
                    .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToDictionary(pair => pair.Key, pair => pair.First().Value, StringComparer.OrdinalIgnoreCase);

                return new BenchmarkCaseIssueSummary(
                    Clean(group.Key.Code) ?? "diagnostic",
                    group.Key.Severity,
                    Clean(group.Key.Stage) ?? string.Empty,
                    group.Key.Scope.ToString(),
                    group.Count(),
                    Clean(group.Key.Message) ?? string.Empty,
                    group
                        .Select(diagnostic => diagnostic.PageNumber)
                        .Where(page => page is not null)
                        .Select(page => page!.Value)
                        .Distinct()
                        .Order()
                        .Take(12)
                        .ToArray(),
                    MaxConfidence(group.Select(diagnostic => diagnostic.Confidence)),
                    sourceIds.Length,
                    sourceIds.Take(12).ToArray(),
                    properties);
            })
            .ToArray();

    private static IReadOnlyList<BenchmarkStageSummary> StageSummaries(PlanScanResult result) =>
        result.Diagnostics.StageReports
            .Select(stage => new BenchmarkStageSummary(
                stage.Stage,
                stage.Duration.TotalMilliseconds,
                stage.InputCount,
                stage.OutputCount,
                stage.DiagnosticCount,
                stage.InfoCount,
                stage.WarningCount,
                stage.ErrorCount))
            .ToArray();

    private static double? MaxConfidence(IEnumerable<Confidence?> values)
    {
        var concrete = values
            .Where(value => value is not null)
            .Select(value => value!.Value.Value)
            .ToArray();
        return concrete.Length == 0
            ? null
            : concrete.Max();
    }

    private static void AddCountAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        BenchmarkCounts counts)
    {
        AddMin(assertions, "pages.min", expectations.MinPages, counts.Pages);
        AddMin(assertions, "regions.min", expectations.MinRegions, counts.Regions);
        AddMin(assertions, "dimensions.min", expectations.MinDimensions, counts.Dimensions);
        AddMin(assertions, "annotations.min", expectations.MinAnnotations, counts.Annotations);
        AddMin(assertions, "annotation_references.min", expectations.MinAnnotationReferences, counts.AnnotationReferences);
        AddMin(assertions, "grid_axes.min", expectations.MinGridAxes, counts.GridAxes);
        AddMin(assertions, "grid_bay_spacings.min", expectations.MinGridBaySpacings, counts.GridBaySpacings);
        AddMin(assertions, "surface_patterns.min", expectations.MinSurfacePatterns, counts.SurfacePatterns);
        AddMin(assertions, "walls.min", expectations.MinWalls, counts.Walls);
        AddMin(assertions, "wall_nodes.min", expectations.MinWallNodes, counts.WallNodes);
        AddMin(assertions, "wall_edges.min", expectations.MinWallEdges, counts.WallEdges);
        AddMin(assertions, "rooms.min", expectations.MinRooms, counts.Rooms);
        AddMin(assertions, "room_adjacencies.min", expectations.MinRoomAdjacencies, counts.RoomAdjacencies);
        AddMin(assertions, "room_clusters.min", expectations.MinRoomClusters, counts.RoomClusters);
        AddMin(assertions, "openings.min", expectations.MinOpenings, counts.Openings);
        AddMin(assertions, "objects.min", expectations.MinObjects, counts.Objects);
        AddMin(assertions, "object_groups.min", expectations.MinObjectGroups, counts.ObjectGroups);
        AddMin(assertions, "object_aggregates.min", expectations.MinObjectAggregates, counts.ObjectAggregates);
        AddMin(assertions, "routing_items.min", expectations.MinRoutingItems, counts.RoutingItems);
        AddMin(assertions, "routing_suppressed_objects.min", expectations.MinRoutingSuppressedObjects, counts.RoutingSuppressedObjects);
        AddMax(assertions, "walls.max", expectations.MaxWalls, counts.Walls);
        AddMax(assertions, "rooms.max", expectations.MaxRooms, counts.Rooms);
        AddMax(assertions, "room_adjacencies.max", expectations.MaxRoomAdjacencies, counts.RoomAdjacencies);
        AddMax(assertions, "room_clusters.max", expectations.MaxRoomClusters, counts.RoomClusters);
        AddMax(assertions, "openings.max", expectations.MaxOpenings, counts.Openings);
        AddMax(assertions, "objects.max", expectations.MaxObjects, counts.Objects);
        AddMax(assertions, "object_groups.max", expectations.MaxObjectGroups, counts.ObjectGroups);
        AddMax(assertions, "object_aggregates.max", expectations.MaxObjectAggregates, counts.ObjectAggregates);
        AddMax(assertions, "routing_items.max", expectations.MaxRoutingItems, counts.RoutingItems);
        AddMax(assertions, "routing_suppressed_objects.max", expectations.MaxRoutingSuppressedObjects, counts.RoutingSuppressedObjects);
        AddMax(assertions, "dimensions.max", expectations.MaxDimensions, counts.Dimensions);
        AddMax(assertions, "annotations.max", expectations.MaxAnnotations, counts.Annotations);
        AddMax(assertions, "annotation_references.max", expectations.MaxAnnotationReferences, counts.AnnotationReferences);
        AddMax(assertions, "grid_axes.max", expectations.MaxGridAxes, counts.GridAxes);
        AddMax(assertions, "grid_bay_spacings.max", expectations.MaxGridBaySpacings, counts.GridBaySpacings);
        AddMax(assertions, "surface_patterns.max", expectations.MaxSurfacePatterns, counts.SurfacePatterns);
        AddMax(assertions, "diagnostic_warnings.max", expectations.MaxDiagnosticWarnings, counts.DiagnosticWarnings);
        AddMax(assertions, "diagnostic_errors.max", expectations.MaxDiagnosticErrors, counts.DiagnosticErrors);
    }

    private static void AddPerformanceAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result,
        TimeSpan duration)
    {
        AddMaxDuration(
            assertions,
            "duration.max",
            expectations.MaxDurationMilliseconds,
            duration.TotalMilliseconds,
            "scan duration");

        foreach (var stageExpectation in expectations.StageExpectations.Where(item => !string.IsNullOrWhiteSpace(item.Stage)))
        {
            var stageName = stageExpectation.Stage.Trim();
            var stage = result.Diagnostics.StageReports
                .FirstOrDefault(report => string.Equals(report.Stage, stageName, StringComparison.OrdinalIgnoreCase));

            if (stage is null)
            {
                assertions.Add(Fail(
                    $"stage.{stageName}.present",
                    "present",
                    "(missing)",
                    $"Stage '{stageName}' was not reported."));
                continue;
            }

            assertions.Add(Pass(
                $"stage.{stageName}.present",
                "present",
                "present",
                $"Stage '{stage.Stage}' was reported."));

            AddMaxDuration(
                assertions,
                $"stage.{stage.Stage}.duration.max",
                stageExpectation.MaxDurationMilliseconds,
                stage.Duration.TotalMilliseconds,
                $"stage {stage.Stage} duration");
            AddMax(assertions, $"stage.{stage.Stage}.diagnostics.max", stageExpectation.MaxDiagnostics, stage.DiagnosticCount);
            AddMax(assertions, $"stage.{stage.Stage}.warnings.max", stageExpectation.MaxWarnings, stage.WarningCount);
            AddMax(assertions, $"stage.{stage.Stage}.errors.max", stageExpectation.MaxErrors, stage.ErrorCount);
        }
    }

    private static void AddDiagnosticAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        var diagnosticCodes = result.Diagnostics.Messages
            .Select(message => message.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        foreach (var code in expectations.RequiredDiagnosticCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = diagnosticCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Pass($"diagnostic.required.{code}", code, code, $"Diagnostic code '{code}' was found.")
                    : Fail($"diagnostic.required.{code}", code, CodeSummary(diagnosticCodes), $"Diagnostic code '{code}' was not found."));
        }

        foreach (var code in expectations.ForbiddenDiagnosticCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = diagnosticCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Fail($"diagnostic.forbidden.{code}", "absent", code, $"Forbidden diagnostic code '{code}' was found.")
                    : Pass($"diagnostic.forbidden.{code}", "absent", CodeSummary(diagnosticCodes), $"Forbidden diagnostic code '{code}' was absent."));
        }
    }

    private static void AddQualityAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        AddMinQualityGrade(assertions, expectations.MinQualityGrade, result.Quality.Grade);
        AddMinRatio(
            assertions,
            "quality_confidence.min",
            expectations.MinQualityConfidence,
            result.Quality.OverallConfidence.Value,
            $"quality grade {result.Quality.Grade}");
        AddMax(assertions, "quality_issues.max", expectations.MaxQualityIssues, result.Quality.Issues.Count);
        AddMax(
            assertions,
            "quality_scan_risk_issues.max",
            expectations.MaxScanRiskIssues,
            result.Quality.Issues.Count(issue => issue.Code.StartsWith("quality.scan_risk.", StringComparison.OrdinalIgnoreCase)));
        AddQualityIssueCodeAssertions(assertions, expectations, result);
    }

    private static void AddScanReviewQueueAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        ScanReviewQueueSummary reviewQueue)
    {
        AddMax(
            assertions,
            "scan_review_queue_items.max",
            expectations.MaxScanReviewQueueItems,
            reviewQueue.Count);

        foreach (var pair in expectations.MaxScanReviewQueueKindCounts
                     .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                     .GroupBy(pair => pair.Key.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Select(group => new { Kind = group.Key, MaxCount = group.Min(pair => pair.Value) })
                     .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase))
        {
            var actual = CountKind(reviewQueue, pair.Kind);
            assertions.Add(
                actual <= pair.MaxCount
                    ? Pass(
                        $"scan_review_queue_kind.{pair.Kind}.max",
                        $"<= {pair.MaxCount}",
                        actual.ToString(),
                        $"scan_review_queue_kind.{pair.Kind}.max met maximum.")
                    : Fail(
                        $"scan_review_queue_kind.{pair.Kind}.max",
                        $"<= {pair.MaxCount}",
                        actual.ToString(),
                        $"scan_review_queue_kind.{pair.Kind}.max exceeded maximum."));
        }

        foreach (var kind in expectations.RequiredScanReviewQueueKinds
                     .Where(kind => !string.IsNullOrWhiteSpace(kind))
                     .Select(kind => kind.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = CountKind(reviewQueue, kind) > 0;
            assertions.Add(
                found
                    ? Pass($"scan_review_queue_kind.required.{kind}", kind, kind, $"Scan review queue kind '{kind}' was found.")
                    : Fail($"scan_review_queue_kind.required.{kind}", kind, ReviewKindSummary(reviewQueue), $"Scan review queue kind '{kind}' was not found."));
        }

        foreach (var kind in expectations.ForbiddenScanReviewQueueKinds
                     .Where(kind => !string.IsNullOrWhiteSpace(kind))
                     .Select(kind => kind.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = CountKind(reviewQueue, kind) > 0;
            assertions.Add(
                found
                    ? Fail($"scan_review_queue_kind.forbidden.{kind}", "absent", kind, $"Forbidden scan review queue kind '{kind}' was found.")
                    : Pass($"scan_review_queue_kind.forbidden.{kind}", "absent", ReviewKindSummary(reviewQueue), $"Forbidden scan review queue kind '{kind}' was absent."));
        }
    }

    private static void AddMeasurementAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        AddMin(
            assertions,
            "measurement_checked_count.min",
            expectations.MinMeasurementCheckedCount,
            result.MeasurementConsistency.CheckedCount);
        AddMin(
            assertions,
            "measurement_consistent_count.min",
            expectations.MinMeasurementConsistentCount,
            result.MeasurementConsistency.ConsistentCount);
        AddMax(
            assertions,
            "measurement_outlier_count.max",
            expectations.MaxMeasurementOutlierCount,
            result.MeasurementConsistency.OutlierCount);

        var outlierRatio = result.MeasurementConsistency.CheckedCount > 0
            ? result.MeasurementConsistency.OutlierCount / (double)result.MeasurementConsistency.CheckedCount
            : 0;
        AddMaxRatio(
            assertions,
            "measurement_outlier_ratio.max",
            expectations.MaxMeasurementOutlierRatio,
            outlierRatio,
            $"{result.MeasurementConsistency.OutlierCount}/{result.MeasurementConsistency.CheckedCount} checked dimensions were outliers");

        if (expectations.MaxMeasurementScaleSpreadRatio is not null)
        {
            if (result.MeasurementConsistency.DimensionScaleSpreadRatio is { } spread)
            {
                AddMaxRatio(
                    assertions,
                    "measurement_scale_spread_ratio.max",
                    expectations.MaxMeasurementScaleSpreadRatio,
                    spread,
                    "matched dimensions implied scale spread");
            }
            else
            {
                assertions.Add(Pass(
                    "measurement_scale_spread_ratio.max",
                    $"<= {FormatRatio(expectations.MaxMeasurementScaleSpreadRatio.Value)}",
                    "(none)",
                    "measurement_scale_spread_ratio.max met maximum because fewer than two matched dimensions had scale evidence."));
            }
        }
    }

    private static void AddImportReadinessAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanImportReadiness readiness)
    {
        AddMinImportReadinessGrade(assertions, expectations.MinImportReadinessGrade, readiness);
        AddMinRatio(
            assertions,
            "import_readiness_score.min",
            expectations.MinImportReadinessScore,
            readiness.Score,
            ImportReadinessSummary(readiness));
        AddRequiredBool(
            assertions,
            "import_geometry_ready.required",
            expectations.RequireGeometryImportReady,
            readiness.ReadyForGeometryImport,
            ImportReadinessSummary(readiness));
        AddRequiredBool(
            assertions,
            "import_metric_ready.required",
            expectations.RequireMetricImportReady,
            readiness.ReadyForMetricImport,
            ImportReadinessSummary(readiness));
        AddRequiredBool(
            assertions,
            "import_routing_ready.required",
            expectations.RequireRoutingImportReady,
            readiness.ReadyForRoutingImport,
            ImportReadinessSummary(readiness));

        if (expectations.AllowImportReview is false)
        {
            assertions.Add(
                readiness.RequiresReview
                    ? Fail(
                        "import_review.allowed",
                        "review not required",
                        "review required",
                        $"Import readiness requires review: {ImportIssueSummary(readiness)}.")
                    : Pass(
                        "import_review.allowed",
                        "review not required",
                        "review not required",
                        "Import readiness did not require review."));
        }
        else if (expectations.AllowImportReview is true)
        {
            assertions.Add(Pass(
                "import_review.allowed",
                "review allowed",
                readiness.RequiresReview ? "review required" : "review not required",
                "Import readiness review state is allowed by this fixture."));
        }

        AddImportIssueCodeAssertions(assertions, expectations, readiness);
    }

    private static void AddMinImportReadinessGrade(
        List<BenchmarkAssertionResult> assertions,
        PlanImportReadinessGrade? expected,
        PlanImportReadiness readiness)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            PlanImportReadiness.MeetsMinimumGrade(readiness.Grade, expected.Value)
                ? Pass(
                    "import_readiness_grade.min",
                    $">= {expected}",
                    readiness.Grade,
                    $"import_readiness_grade.min met minimum: {ImportReadinessSummary(readiness)}.")
                : Fail(
                    "import_readiness_grade.min",
                    $">= {expected}",
                    readiness.Grade,
                    $"import_readiness_grade.min was below minimum: {ImportReadinessSummary(readiness)}."));
    }

    private static void AddRequiredBool(
        List<BenchmarkAssertionResult> assertions,
        string name,
        bool? expected,
        bool actual,
        string detail)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual == expected.Value
                ? Pass(name, expected.Value.ToString(), actual.ToString(), $"{name} matched expectation: {detail}.")
                : Fail(name, expected.Value.ToString(), actual.ToString(), $"{name} did not match expectation: {detail}."));
    }

    private static void AddImportIssueCodeAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanImportReadiness readiness)
    {
        var issueCodes = readiness.BlockingIssueCodes
            .Concat(readiness.ReviewIssueCodes)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        foreach (var code in expectations.RequiredImportIssueCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = issueCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Pass($"import_issue.required.{code}", code, code, $"Import readiness issue code '{code}' was found.")
                    : Fail($"import_issue.required.{code}", code, CodeSummary(issueCodes), $"Import readiness issue code '{code}' was not found."));
        }

        foreach (var code in expectations.ForbiddenImportIssueCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = issueCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Fail($"import_issue.forbidden.{code}", "absent", code, $"Forbidden import readiness issue code '{code}' was found.")
                    : Pass($"import_issue.forbidden.{code}", "absent", CodeSummary(issueCodes), $"Forbidden import readiness issue code '{code}' was absent."));
        }
    }

    private static void AddMinQualityGrade(
        List<BenchmarkAssertionResult> assertions,
        PlanScanQualityGrade? expected,
        PlanScanQualityGrade actual)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            (int)actual >= (int)expected.Value
                ? Pass("quality_grade.min", $">= {expected}", actual.ToString(), "quality_grade.min met minimum.")
                : Fail("quality_grade.min", $">= {expected}", actual.ToString(), "quality_grade.min was below minimum."));
    }

    private static void AddQualityIssueCodeAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        var issueCodes = result.Quality.Issues
            .Select(issue => issue.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToArray();

        foreach (var code in expectations.RequiredQualityIssueCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = issueCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Pass($"quality_issue.required.{code}", code, code, $"Quality issue code '{code}' was found.")
                    : Fail($"quality_issue.required.{code}", code, CodeSummary(issueCodes), $"Quality issue code '{code}' was not found."));
        }

        foreach (var code in expectations.ForbiddenQualityIssueCodes
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Select(code => code.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = issueCodes.Any(actual => string.Equals(actual, code, StringComparison.OrdinalIgnoreCase));
            assertions.Add(
                found
                    ? Fail($"quality_issue.forbidden.{code}", "absent", code, $"Forbidden quality issue code '{code}' was found.")
                    : Pass($"quality_issue.forbidden.{code}", "absent", CodeSummary(issueCodes), $"Forbidden quality issue code '{code}' was absent."));
        }
    }

    private static void AddRequiredRegionAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var kind in expectations.RequiredRegionKinds.Distinct())
        {
            AddRequired(
                assertions,
                $"region.required.{kind}",
                kind.ToString(),
                result.SheetRegions.Any(region => region.Kind == kind),
                string.Join(", ", result.SheetRegions.Select(region => region.Kind).Distinct()));
        }
    }

    private static void AddRequiredAnnotationAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var kind in expectations.RequiredAnnotationKinds.Distinct())
        {
            AddRequired(
                assertions,
                $"annotation.required.{kind}",
                kind.ToString(),
                result.Annotations.Any(annotation => annotation.Kind == kind),
                string.Join(", ", result.Annotations.Select(annotation => annotation.Kind).Distinct()));
        }
    }

    private static void AddRequiredGridAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var label in expectations.RequiredGridLabels.Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddRequired(
                assertions,
                $"grid_label.required.{label}",
                label,
                result.GridAxes.Any(axis => string.Equals(axis.Label, label, StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", result.GridAxes.Select(axis => axis.Label).Where(item => !string.IsNullOrWhiteSpace(item))));
        }
    }

    private static void AddRequiredOpeningAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var type in expectations.RequiredOpeningTypes.Distinct())
        {
            AddRequired(
                assertions,
                $"opening_type.required.{type}",
                type.ToString(),
                result.Openings.Any(opening => opening.Type == type),
                string.Join(", ", result.Openings.Select(opening => opening.Type).Distinct()));
        }

        foreach (var operation in expectations.RequiredOpeningOperations.Distinct())
        {
            AddRequired(
                assertions,
                $"opening_operation.required.{operation}",
                operation.ToString(),
                result.Openings.Any(opening => opening.Operation == operation),
                string.Join(", ", result.Openings.Select(opening => opening.Operation).Distinct()));
        }
    }

    private static void AddRequiredObjectAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var category in expectations.RequiredObjectCategories.Distinct())
        {
            AddRequired(
                assertions,
                $"object_category.required.{category}",
                category.ToString(),
                result.ObjectCandidates.Any(candidate => candidate.Category == category),
                string.Join(", ", result.ObjectCandidates.Select(candidate => candidate.Category).Distinct()));
        }
    }

    private static void AddRoomLabelAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        var labels = result.Rooms
            .Select(room => room.Label)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();

        foreach (var label in expectations.RequiredRoomLabels.Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddRequired(
                assertions,
                $"room_label.required.{label}",
                label,
                labels.Any(actual => string.Equals(actual, label, StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", labels));
        }

        foreach (var label in expectations.ForbiddenRoomLabels.Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = labels.Any(actual => string.Equals(actual, label, StringComparison.OrdinalIgnoreCase));
            assertions.Add(found
                ? Fail($"room_label.forbidden.{label}", "absent", label, $"Forbidden room label '{label}' was found.")
                : Pass($"room_label.forbidden.{label}", "absent", string.Join(", ", labels), $"Forbidden room label '{label}' was absent."));
        }
    }

    private static void AddRequiredLayerAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        foreach (var category in expectations.RequiredLayerCategories.Distinct())
        {
            AddRequired(
                assertions,
                $"layer_category.required.{category}",
                category.ToString(),
                result.LayerAnalysis.Layers.Any(layer => layer.LikelyCategory == category),
                string.Join(", ", result.LayerAnalysis.Layers.Select(layer => layer.LikelyCategory).Distinct()));
        }
    }

    private static void AddCalibrationAssertion(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        if (expectations.RequiresReliableCalibration is not true)
        {
            return;
        }

        AddRequired(
            assertions,
            "calibration.required",
            "reliable calibration",
            result.Calibration.HasReliableMeasurementScale,
            result.Calibration.HasReliableMeasurementScale ? "reliable calibration" : "uncalibrated");
    }

    private static IReadOnlyList<BenchmarkDetectorMetrics> AddDetectorMetricAssertions(
        List<BenchmarkAssertionResult> assertions,
        BenchmarkExpectations expectations,
        PlanScanResult result)
    {
        var metrics = new List<BenchmarkDetectorMetrics>();

        AddDetectorMetric(
            assertions,
            metrics,
            "regions",
            expectations.RegionMetrics,
            result.SheetRegions.Select(region => new BenchmarkActualDetection(
                region.Id,
                region.PageNumber,
                region.Bounds,
                region.Label,
                null,
                RegionKind: region.Kind)));

        AddDetectorMetric(
            assertions,
            metrics,
            "dimensions",
            expectations.DimensionMetrics,
            result.Dimensions.Select(dimension => new BenchmarkActualDetection(
                dimension.Id,
                dimension.PageNumber,
                dimension.Bounds,
                null,
                dimension.Text,
                DimensionKind: dimension.Kind,
                DimensionOrientation: dimension.Orientation)));

        AddDetectorMetric(
            assertions,
            metrics,
            "annotations",
            expectations.AnnotationMetrics,
            result.Annotations.Select(annotation => new BenchmarkActualDetection(
                annotation.Id,
                annotation.PageNumber,
                annotation.Bounds,
                annotation.Label,
                null,
                AnnotationKind: annotation.Kind)));

        AddDetectorMetric(
            assertions,
            metrics,
            "annotation_references",
            expectations.AnnotationReferenceMetrics,
            result.Annotations.SelectMany(annotation =>
                annotation.Items.SelectMany(item =>
                    item.References.Select(reference => new BenchmarkActualDetection(
                        reference.Id,
                        item.PageNumber,
                        reference.Bounds,
                        annotation.Label,
                        reference.Text,
                        Marker: reference.Marker,
                        AnnotationKind: annotation.Kind)))));

        AddDetectorMetric(
            assertions,
            metrics,
            "grid_axes",
            expectations.GridAxisMetrics,
            result.GridAxes.Select(axis => new BenchmarkActualDetection(
                axis.Id,
                axis.PageNumber,
                axis.Bounds,
                axis.Label,
                null,
                GridAxisOrientation: axis.Orientation)));

        AddDetectorMetric(
            assertions,
            metrics,
            "walls",
            expectations.WallMetrics,
            result.Walls.Select(wall => new BenchmarkActualDetection(
                wall.Id,
                wall.PageNumber,
                wall.Bounds,
                null,
                null)));

        AddDetectorMetric(
            assertions,
            metrics,
            "rooms",
            expectations.RoomMetrics,
            result.Rooms.Select(room => new BenchmarkActualDetection(
                room.Id,
                room.PageNumber,
                room.Bounds,
                room.Label,
                null)));

        AddDetectorMetric(
            assertions,
            metrics,
            "openings",
            expectations.OpeningMetrics,
            result.Openings.Select(opening => new BenchmarkActualDetection(
                opening.Id,
                opening.PageNumber,
                opening.Bounds,
                null,
                null,
                OpeningType: opening.Type,
                OpeningOperation: opening.Operation)));

        AddDetectorMetric(
            assertions,
            metrics,
            "objects",
            expectations.ObjectMetrics,
            result.ObjectCandidates.Select(candidate => new BenchmarkActualDetection(
                candidate.Id,
                candidate.PageNumber,
                candidate.Bounds,
                candidate.Label,
                candidate.SymbolName,
                ObjectCategory: candidate.Category,
                ObjectKind: candidate.Kind,
                DetectedTags: SingleTag(candidate.DetectedTag))));

        AddDetectorMetric(
            assertions,
            metrics,
            "object_groups",
            expectations.ObjectGroupMetrics,
            result.ObjectGroups.Select(group => new BenchmarkActualDetection(
                group.Id,
                group.PageNumbers.Count == 1 ? group.PageNumbers[0] : null,
                group.RepresentativeBounds,
                group.Label,
                group.SymbolName,
                ObjectCategory: group.Category,
                ObjectKind: group.Kind,
                Count: group.Count,
                RequiresReview: group.RequiresReview,
                DetectedTags: group.DetectedTags)));

        AddDetectorMetric(
            assertions,
            metrics,
            "object_aggregates",
            expectations.ObjectAggregateMetrics,
            result.ObjectAggregates.Select(aggregate => new BenchmarkActualDetection(
                aggregate.Id,
                aggregate.PageNumber,
                aggregate.Bounds,
                aggregate.Label,
                null,
                ObjectCategory: aggregate.Category,
                ObjectKind: aggregate.Kind,
                Count: aggregate.ChildObjectCount,
                RequiresReview: aggregate.RequiresReview,
                RoutingInfluence: aggregate.RoutingInfluence,
                StructuralInfluence: aggregate.StructuralInfluence,
                RoomUseKind: aggregate.RoomUseEvidence,
                SuppressesChildObjects: aggregate.SuppressChildObjectsForRouting)));

        AddDetectorMetric(
            assertions,
            metrics,
            "routing_barriers",
            expectations.RoutingBarrierMetrics,
            result.RoutingLayer.Barriers.Select(barrier => new BenchmarkActualDetection(
                barrier.Id,
                barrier.PageNumber,
                barrier.Bounds,
                null,
                null,
                RoutingSourceKind: barrier.SourceKind)));

        AddDetectorMetric(
            assertions,
            metrics,
            "routing_passages",
            expectations.RoutingPassageMetrics,
            result.RoutingLayer.Passages.Select(passage => new BenchmarkActualDetection(
                passage.Id,
                passage.PageNumber,
                passage.Bounds,
                null,
                null,
                OpeningType: passage.Type,
                OpeningOperation: passage.Operation,
                RoutingSourceKind: passage.SourceKind)));

        AddDetectorMetric(
            assertions,
            metrics,
            "routing_obstacles",
            expectations.RoutingObstacleMetrics,
            result.RoutingLayer.Obstacles.Select(obstacle => new BenchmarkActualDetection(
                obstacle.Id,
                obstacle.PageNumber,
                obstacle.Bounds,
                obstacle.Label,
                obstacle.RoomLabel,
                ObjectCategory: obstacle.Category,
                ObjectKind: obstacle.ObjectKind,
                Count: obstacle.ChildObjectIds.Count,
                RoutingSourceKind: obstacle.SourceKind,
                RoutingObstacleKind: obstacle.ObstacleKind,
                RoutingInfluence: obstacle.RoutingInfluence,
                StructuralInfluence: obstacle.StructuralInfluence,
                SuppressesChildObjects: obstacle.SuppressesChildObjects)));

        AddDetectorMetric(
            assertions,
            metrics,
            "routing_room_use_hints",
            expectations.RoutingRoomUseHintMetrics,
            result.RoutingLayer.RoomUseHints.Select(hint => new BenchmarkActualDetection(
                hint.Id,
                hint.PageNumber,
                hint.Bounds,
                hint.RoomLabel,
                null,
                RoutingSourceKind: hint.SourceKind,
                RoomUseKind: hint.RoomUseKind)));

        AddDetectorMetric(
            assertions,
            metrics,
            "routing_suppressed_objects",
            expectations.RoutingSuppressedObjectMetrics,
            result.RoutingLayer.SuppressedObjects.Select(suppressed => new BenchmarkActualDetection(
                suppressed.Id,
                suppressed.PageNumber,
                suppressed.CandidateBounds,
                suppressed.CandidateLabel,
                suppressed.SuppressedByAggregateId,
                ObjectCategory: suppressed.CandidateCategory,
                ObjectKind: suppressed.CandidateKind,
                RoutingInfluence: suppressed.AggregateRoutingInfluence,
                StructuralInfluence: suppressed.AggregateStructuralInfluence,
                ObjectCandidateId: suppressed.ObjectCandidateId,
                SuppressedByAggregateId: suppressed.SuppressedByAggregateId,
                SuppressionReason: suppressed.Reason,
                SuppressionAction: suppressed.Action,
                ReplacementRoutingObstacleId: suppressed.ReplacementRoutingObstacleId,
                RoomUseHintId: suppressed.RoomUseHintId)));

        AddDetectorMetric(
            assertions,
            metrics,
            "layers",
            expectations.LayerMetrics,
            result.LayerAnalysis.Layers.Select(layer => new BenchmarkActualDetection(
                layer.Name,
                null,
                layer.Bounds,
                layer.Name,
                null,
                LayerCategory: layer.LikelyCategory)));

        return metrics;
    }

    private static void AddDetectorMetric(
        List<BenchmarkAssertionResult> assertions,
        List<BenchmarkDetectorMetrics> metrics,
        string detector,
        BenchmarkDetectorMetricExpectations expectations,
        IEnumerable<BenchmarkActualDetection> detections)
    {
        if (!expectations.HasExpectations)
        {
            return;
        }

        var metric = EvaluateDetectorMetric(detector, expectations, detections);
        metrics.Add(metric);
        AddMinRatio(
            assertions,
            $"{detector}.target_recall.min",
            expectations.MinRecall ?? (expectations.Targets.Count > 0 ? 1.0 : null),
            metric.Recall,
            $"{metric.MatchedCount}/{metric.ExpectedCount} expected {detector} targets matched");
        AddMinRatio(
            assertions,
            $"{detector}.target_precision.min",
            expectations.MinPrecision,
            metric.Precision,
            $"{metric.MatchedCount}/{metric.ScoredDetectionCount} precision-scored {detector} detection(s) matched targets"
            + (metric.PrecisionScoringEnabled ? " with complete truth/precision policy" : " as an informational spot-check value"));
    }

    private static BenchmarkDetectorMetrics EvaluateDetectorMetric(
        string detector,
        BenchmarkDetectorMetricExpectations expectations,
        IEnumerable<BenchmarkActualDetection> detections)
    {
        var targets = expectations.Targets.ToArray();
        var actual = detections.ToArray();
        var used = new bool[actual.Length];
        var matches = new List<BenchmarkTargetMatchResult>();

        for (var targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            var target = targets[targetIndex];
            var bestIndex = -1;
            var bestScore = double.MinValue;
            var bestEvidence = string.Empty;

            for (var actualIndex = 0; actualIndex < actual.Length; actualIndex++)
            {
                if (used[actualIndex])
                {
                    continue;
                }

                if (!TryScoreTargetMatch(target, actual[actualIndex], out var score, out var evidence))
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestIndex = actualIndex;
                    bestScore = score;
                    bestEvidence = evidence;
                }
            }

            if (bestIndex >= 0)
            {
                used[bestIndex] = true;
                matches.Add(new BenchmarkTargetMatchResult(
                    targetIndex,
                    TargetId(target, targetIndex),
                    true,
                    actual[bestIndex].Id,
                    bestScore,
                    bestEvidence));
            }
            else
            {
                matches.Add(new BenchmarkTargetMatchResult(
                    targetIndex,
                    TargetId(target, targetIndex),
                    false,
                    null,
                    0,
                    "no unused detection matched target criteria"));
            }
        }

        var matchedCount = matches.Count(match => match.Matched);
        var expectedCount = targets.Length;
        var detectedCount = actual.Length;
        var unmatched = actual
            .Select((detection, index) => (Detection: detection, Index: index))
            .Where(item => !used[item.Index])
            .ToArray();
        var reviewOnly = unmatched
            .Where(item => IsReviewOnlyDetection(item.Detection))
            .ToArray();
        var extraDetections = unmatched
            .Where(item => !IsReviewOnlyDetection(item.Detection))
            .ToArray();
        var scoredDetectionCount = matchedCount + extraDetections.Length;
        var recall = expectedCount == 0 ? 1.0 : matchedCount / (double)expectedCount;
        var precision = scoredDetectionCount == 0 ? expectedCount == 0 ? 1.0 : 0.0 : matchedCount / (double)scoredDetectionCount;
        var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);

        return new BenchmarkDetectorMetrics(
            detector,
            expectedCount,
            detectedCount,
            matchedCount,
            expectedCount - matchedCount,
            extraDetections.Length,
            recall,
            precision,
            f1,
            matches)
        {
            PrecisionScoringEnabled = expectations.PrecisionScoringEnabled,
            ScoredDetectionCount = scoredDetectionCount,
            ReviewOnlyDetectionCount = reviewOnly.Length,
            ExtraDetections = extraDetections
                .Select(item => DetectionSummary(item.Detection))
                .ToArray(),
            ReviewOnlyDetections = reviewOnly
                .Select(item => DetectionSummary(item.Detection))
                .ToArray()
        };
    }

    private static bool IsReviewOnlyDetection(BenchmarkActualDetection detection) =>
        detection.RequiresReview == true;

    private static BenchmarkDetectionSummary DetectionSummary(BenchmarkActualDetection detection) =>
        new(
            detection.Id,
            detection.PageNumber,
            detection.Bounds,
            detection.Label,
            detection.Text,
            detection.Marker,
            detection.ObjectCategory?.ToString(),
            detection.ObjectKind?.ToString(),
            detection.LayerCategory?.ToString(),
            detection.RoutingSourceKind?.ToString(),
            detection.RoutingObstacleKind?.ToString(),
            detection.RoutingInfluence?.ToString(),
            detection.StructuralInfluence?.ToString(),
            detection.RoomUseKind?.ToString(),
            detection.Count,
            detection.RequiresReview,
            detection.SuppressesChildObjects,
            CleanTags(detection.DetectedTags),
            DetectionEvidence(detection));

    private static string DetectionEvidence(BenchmarkActualDetection detection)
    {
        var evidence = new List<string>();
        if (detection.PageNumber is { } page)
        {
            evidence.Add($"page {page}");
        }

        AddEvidence(evidence, "label", detection.Label);
        AddEvidence(evidence, "text", detection.Text);
        AddEvidence(evidence, "marker", detection.Marker);
        AddEvidence(evidence, "category", detection.ObjectCategory?.ToString());
        AddEvidence(evidence, "kind", detection.ObjectKind?.ToString());
        AddEvidence(evidence, "layer", detection.LayerCategory?.ToString());
        AddEvidence(evidence, "routing source", detection.RoutingSourceKind?.ToString());
        AddEvidence(evidence, "routing obstacle", detection.RoutingObstacleKind?.ToString());
        AddEvidence(evidence, "routing influence", detection.RoutingInfluence?.ToString());
        AddEvidence(evidence, "structural influence", detection.StructuralInfluence?.ToString());
        AddEvidence(evidence, "room use", detection.RoomUseKind?.ToString());

        if (detection.Count is { } count)
        {
            evidence.Add($"count {count}");
        }

        if (detection.RequiresReview is { } requiresReview)
        {
            evidence.Add($"requires review {requiresReview}");
        }

        var tags = CleanTags(detection.DetectedTags);
        if (tags.Length > 0)
        {
            evidence.Add($"tags {string.Join(", ", tags)}");
        }

        return evidence.Count == 0
            ? detection.Id
            : string.Join(", ", evidence);
    }

    private static void AddEvidence(List<string> evidence, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            evidence.Add($"{name} '{value.Trim()}'");
        }
    }

    private static bool TryScoreTargetMatch(
        BenchmarkDetectionTarget target,
        BenchmarkActualDetection actual,
        out double score,
        out string evidence)
    {
        var evidenceItems = new List<string>();
        var criteriaCount = 0;
        score = 0;

        if (target.PageNumber is not null)
        {
            criteriaCount++;
            if (actual.PageNumber != target.PageNumber)
            {
                evidence = "page mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"page {target.PageNumber}");
        }

        if (!string.IsNullOrWhiteSpace(target.Label))
        {
            criteriaCount++;
            if (!StringMatches(actual.Label, target.Label))
            {
                evidence = "label mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"label '{target.Label}'");
        }

        if (!string.IsNullOrWhiteSpace(target.Text))
        {
            criteriaCount++;
            if (!TextMatches(actual.Text, target.Text))
            {
                evidence = "text mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"text '{target.Text}'");
        }

        if (!string.IsNullOrWhiteSpace(target.Marker))
        {
            criteriaCount++;
            if (!StringMatches(actual.Marker, target.Marker))
            {
                evidence = "marker mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"marker '{target.Marker}'");
        }

        if (target.MinCount is not null)
        {
            criteriaCount++;
            if (actual.Count is null)
            {
                evidence = "detection has no count";
                return false;
            }

            if (actual.Count.Value < target.MinCount.Value)
            {
                evidence = "count below minimum";
                return false;
            }

            score += 1;
            evidenceItems.Add($"count >= {target.MinCount}");
        }

        if (target.RequiresReview is not null)
        {
            criteriaCount++;
            if (actual.RequiresReview is null || actual.RequiresReview.Value != target.RequiresReview.Value)
            {
                evidence = "review flag mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"requires review {target.RequiresReview.Value}");
        }

        if (target.SuppressesChildObjects is not null)
        {
            criteriaCount++;
            if (actual.SuppressesChildObjects is null || actual.SuppressesChildObjects.Value != target.SuppressesChildObjects.Value)
            {
                evidence = "child-object suppression mismatch";
                return false;
            }

            score += 1;
            evidenceItems.Add($"suppresses child objects {target.SuppressesChildObjects.Value}");
        }

        if (!OptionalStringMatches(target.ObjectCandidateId, actual.ObjectCandidateId, evidenceItems, ref criteriaCount, ref score, "object candidate id", out evidence)
            || !OptionalStringMatches(target.SuppressedByAggregateId, actual.SuppressedByAggregateId, evidenceItems, ref criteriaCount, ref score, "suppressed by aggregate id", out evidence)
            || !OptionalStringMatches(target.ReplacementRoutingObstacleId, actual.ReplacementRoutingObstacleId, evidenceItems, ref criteriaCount, ref score, "replacement routing obstacle id", out evidence)
            || !OptionalStringMatches(target.RoomUseHintId, actual.RoomUseHintId, evidenceItems, ref criteriaCount, ref score, "room-use hint id", out evidence))
        {
            return false;
        }

        var requiredTags = CleanTags(target.DetectedTags);
        if (requiredTags.Length > 0)
        {
            criteriaCount++;
            var actualTags = CleanTags(actual.DetectedTags);
            var missingTags = requiredTags
                .Where(required => !actualTags.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missingTags.Length > 0)
            {
                evidence = $"detected tag mismatch: missing {string.Join(", ", missingTags)}";
                return false;
            }

            score += 1;
            evidenceItems.Add($"detected tags {string.Join(", ", requiredTags)}");
        }

        if (!EnumMatches(target.RegionKind, actual.RegionKind, out evidence, "region kind")
            || !EnumMatches(target.DimensionKind, actual.DimensionKind, out evidence, "dimension kind")
            || !EnumMatches(target.DimensionOrientation, actual.DimensionOrientation, out evidence, "dimension orientation")
            || !EnumMatches(target.AnnotationKind, actual.AnnotationKind, out evidence, "annotation kind")
            || !EnumMatches(target.GridAxisOrientation, actual.GridAxisOrientation, out evidence, "grid orientation")
            || !EnumMatches(target.OpeningType, actual.OpeningType, out evidence, "opening type")
            || !EnumMatches(target.OpeningOperation, actual.OpeningOperation, out evidence, "opening operation")
            || !EnumMatches(target.ObjectCategory, actual.ObjectCategory, out evidence, "object category")
            || !EnumMatches(target.ObjectKind, actual.ObjectKind, out evidence, "object kind")
            || !EnumMatches(target.LayerCategory, actual.LayerCategory, out evidence, "layer category")
            || !EnumMatches(target.RoutingSourceKind, actual.RoutingSourceKind, out evidence, "routing source kind")
            || !EnumMatches(target.RoutingObstacleKind, actual.RoutingObstacleKind, out evidence, "routing obstacle kind")
            || !EnumMatches(target.RoutingInfluence, actual.RoutingInfluence, out evidence, "routing influence")
            || !EnumMatches(target.StructuralInfluence, actual.StructuralInfluence, out evidence, "structural influence")
            || !EnumMatches(target.RoomUseKind, actual.RoomUseKind, out evidence, "room use kind")
            || !EnumMatches(target.SuppressionReason, actual.SuppressionReason, out evidence, "suppression reason")
            || !EnumMatches(target.SuppressionAction, actual.SuppressionAction, out evidence, "suppression action"))
        {
            return false;
        }

        AddEnumScore(target.RegionKind, evidenceItems, ref criteriaCount, ref score, "region kind");
        AddEnumScore(target.DimensionKind, evidenceItems, ref criteriaCount, ref score, "dimension kind");
        AddEnumScore(target.DimensionOrientation, evidenceItems, ref criteriaCount, ref score, "dimension orientation");
        AddEnumScore(target.AnnotationKind, evidenceItems, ref criteriaCount, ref score, "annotation kind");
        AddEnumScore(target.GridAxisOrientation, evidenceItems, ref criteriaCount, ref score, "grid orientation");
        AddEnumScore(target.OpeningType, evidenceItems, ref criteriaCount, ref score, "opening type");
        AddEnumScore(target.OpeningOperation, evidenceItems, ref criteriaCount, ref score, "opening operation");
        AddEnumScore(target.ObjectCategory, evidenceItems, ref criteriaCount, ref score, "object category");
        AddEnumScore(target.ObjectKind, evidenceItems, ref criteriaCount, ref score, "object kind");
        AddEnumScore(target.LayerCategory, evidenceItems, ref criteriaCount, ref score, "layer category");
        AddEnumScore(target.RoutingSourceKind, evidenceItems, ref criteriaCount, ref score, "routing source kind");
        AddEnumScore(target.RoutingObstacleKind, evidenceItems, ref criteriaCount, ref score, "routing obstacle kind");
        AddEnumScore(target.RoutingInfluence, evidenceItems, ref criteriaCount, ref score, "routing influence");
        AddEnumScore(target.StructuralInfluence, evidenceItems, ref criteriaCount, ref score, "structural influence");
        AddEnumScore(target.RoomUseKind, evidenceItems, ref criteriaCount, ref score, "room use kind");
        AddEnumScore(target.SuppressionReason, evidenceItems, ref criteriaCount, ref score, "suppression reason");
        AddEnumScore(target.SuppressionAction, evidenceItems, ref criteriaCount, ref score, "suppression action");

        if (target.Bounds is not null)
        {
            criteriaCount++;
            if (actual.Bounds is null)
            {
                evidence = "detection has no bounds";
                return false;
            }

            var iou = IntersectionOverUnion(target.Bounds.Value, actual.Bounds.Value);
            var minIou = target.MinIntersectionOverUnion ?? 0.25;
            var centerDistance = target.Bounds.Value.Center.DistanceTo(actual.Bounds.Value.Center);
            var centerMatches = target.MaxCenterDistance is not null && centerDistance <= target.MaxCenterDistance.Value;
            if (iou < minIou && !centerMatches)
            {
                evidence = $"bounds mismatch: IoU {FormatRatio(iou)}, center distance {centerDistance:0.###}";
                return false;
            }

            var centerScore = centerMatches && target.MaxCenterDistance is not null
                ? 1.0 - (centerDistance / Math.Max(1, target.MaxCenterDistance.Value))
                : 0;
            score += centerMatches ? Math.Max(iou, centerScore) : iou;
            evidenceItems.Add($"bounds IoU {FormatRatio(iou)}");
        }

        if (criteriaCount == 0)
        {
            score = 0.1;
            evidence = "target had no criteria";
            return true;
        }

        score /= criteriaCount;
        evidence = string.Join(", ", evidenceItems);
        return true;
    }

    private static void AddMin(
        List<BenchmarkAssertionResult> assertions,
        string name,
        int? expected,
        int actual)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual >= expected.Value
                ? Pass(name, $">= {expected}", actual.ToString(), $"{name} met minimum.")
                : Fail(name, $">= {expected}", actual.ToString(), $"{name} was below minimum."));
    }

    private static void AddMax(
        List<BenchmarkAssertionResult> assertions,
        string name,
        int? expected,
        int actual)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual <= expected.Value
                ? Pass(name, $"<= {expected}", actual.ToString(), $"{name} met maximum.")
                : Fail(name, $"<= {expected}", actual.ToString(), $"{name} exceeded maximum."));
    }

    private static void AddMaxDuration(
        List<BenchmarkAssertionResult> assertions,
        string name,
        double? expected,
        double actual,
        string label)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual <= expected.Value
                ? Pass(name, $"<= {expected.Value:0.###} ms", $"{actual:0.###} ms", $"{label} met maximum.")
                : Fail(name, $"<= {expected.Value:0.###} ms", $"{actual:0.###} ms", $"{label} exceeded maximum."));
    }

    private static void AddRequired(
        List<BenchmarkAssertionResult> assertions,
        string name,
        string expected,
        bool found,
        string actual)
    {
        assertions.Add(
            found
                ? Pass(name, expected, actual, $"{expected} was found.")
                : Fail(name, expected, string.IsNullOrWhiteSpace(actual) ? "(none)" : actual, $"{expected} was not found."));
    }

    private static void AddMinRatio(
        List<BenchmarkAssertionResult> assertions,
        string name,
        double? expected,
        double actual,
        string detail)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual + 0.0000001 >= expected.Value
                ? Pass(name, $">= {FormatRatio(expected.Value)}", FormatRatio(actual), $"{name} met minimum: {detail}.")
                : Fail(name, $">= {FormatRatio(expected.Value)}", FormatRatio(actual), $"{name} was below minimum: {detail}."));
    }

    private static void AddMaxRatio(
        List<BenchmarkAssertionResult> assertions,
        string name,
        double? expected,
        double actual,
        string detail)
    {
        if (expected is null)
        {
            return;
        }

        assertions.Add(
            actual <= expected.Value + 0.0000001
                ? Pass(name, $"<= {FormatRatio(expected.Value)}", FormatRatio(actual), $"{name} met maximum: {detail}.")
                : Fail(name, $"<= {FormatRatio(expected.Value)}", FormatRatio(actual), $"{name} exceeded maximum: {detail}."));
    }

    private static bool EnumMatches<T>(
        T? expected,
        T? actual,
        out string evidence,
        string name)
        where T : struct, Enum
    {
        if (expected is null)
        {
            evidence = string.Empty;
            return true;
        }

        if (actual is not null && expected.Value.Equals(actual.Value))
        {
            evidence = string.Empty;
            return true;
        }

        evidence = $"{name} mismatch";
        return false;
    }

    private static void AddEnumScore<T>(
        T? expected,
        ICollection<string> evidence,
        ref int criteriaCount,
        ref double score,
        string name)
        where T : struct, Enum
    {
        if (expected is null)
        {
            return;
        }

        criteriaCount++;
        score += 1;
        evidence.Add($"{name} {expected}");
    }

    private static bool OptionalStringMatches(
        string? expected,
        string? actual,
        ICollection<string> evidenceItems,
        ref int criteriaCount,
        ref double score,
        string name,
        out string evidence)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            evidence = string.Empty;
            return true;
        }

        criteriaCount++;
        if (!StringMatches(actual, expected))
        {
            evidence = $"{name} mismatch";
            return false;
        }

        score += 1;
        evidenceItems.Add($"{name} '{expected.Trim()}'");
        evidence = string.Empty;
        return true;
    }

    private static bool StringMatches(string? actual, string expected) =>
        !string.IsNullOrWhiteSpace(actual)
        && string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TextMatches(string? actual, string expected) =>
        !string.IsNullOrWhiteSpace(actual)
        && actual.Contains(expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SingleTag(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? Array.Empty<string>() : new[] { tag.Trim() };

    private static string[] CleanTags(IReadOnlyList<string>? tags) =>
        tags is null
            ? Array.Empty<string>()
            : tags
                .Select(tag => string.IsNullOrWhiteSpace(tag) ? null : tag.Trim())
                .Where(tag => tag is not null)
                .Select(tag => tag!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static double IntersectionOverUnion(PlanRect expected, PlanRect actual)
    {
        if (expected.IsEmpty || actual.IsEmpty)
        {
            return 0;
        }

        var overlap = expected.OverlapArea(actual);
        var union = expected.Area + actual.Area - overlap;
        return union <= 0 ? 0 : overlap / union;
    }

    private static string TargetId(BenchmarkDetectionTarget target, int index) =>
        string.IsNullOrWhiteSpace(target.Id) ? $"target-{index + 1}" : target.Id;

    private static string FormatRatio(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string CodeSummary(IReadOnlyList<string> codes) =>
        codes.Count == 0
            ? "(none)"
            : string.Join(", ", codes.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

    private static int CountKind(ScanReviewQueueSummary reviewQueue, string kind) =>
        reviewQueue.KindCounts.TryGetValue(kind, out var count) ? count : 0;

    private static string ReviewKindSummary(ScanReviewQueueSummary reviewQueue) =>
        reviewQueue.KindCounts.Count == 0
            ? "(none)"
            : string.Join(
                ", ",
                reviewQueue.KindCounts
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key}:{pair.Value}"));

    private static string ImportReadinessSummary(PlanImportReadiness readiness) =>
        $"grade {readiness.Grade}, score {FormatRatio(readiness.Score)}, geometry {Ready(readiness.ReadyForGeometryImport)}, metric {Ready(readiness.ReadyForMetricImport)}, routing {Ready(readiness.ReadyForRoutingImport)}, review {Ready(readiness.RequiresReview)}";

    private static string ImportIssueSummary(PlanImportReadiness readiness) =>
        CodeSummary(readiness.BlockingIssueCodes.Concat(readiness.ReviewIssueCodes).ToArray());

    private static string Ready(bool value) => value ? "ready" : "not ready";

    private static BenchmarkAssertionResult Pass(string name, string expected, string actual, string message) =>
        new(name, true, expected, actual, message);

    private static BenchmarkAssertionResult Fail(string name, string expected, string actual, string message) =>
        new(name, false, expected, actual, message);

    private static string FixtureId(BenchmarkFixture fixture) =>
        string.IsNullOrWhiteSpace(fixture.Id) ? fixture.SourcePath : fixture.Id;

    private sealed record BenchmarkActualDetection(
        string Id,
        int? PageNumber,
        PlanRect? Bounds,
        string? Label = null,
        string? Text = null,
        string? Marker = null,
        RegionKind? RegionKind = null,
        DimensionKind? DimensionKind = null,
        DimensionOrientation? DimensionOrientation = null,
        PlanAnnotationKind? AnnotationKind = null,
        GridAxisOrientation? GridAxisOrientation = null,
        OpeningType? OpeningType = null,
        OpeningOperation? OpeningOperation = null,
        ObjectCategory? ObjectCategory = null,
        ObjectCandidateKind? ObjectKind = null,
        LayerCategory? LayerCategory = null,
        RoutingSourceKind? RoutingSourceKind = null,
        RoutingObstacleKind? RoutingObstacleKind = null,
        ObjectRoutingInfluence? RoutingInfluence = null,
        ObjectStructuralInfluence? StructuralInfluence = null,
        RoomUseKind? RoomUseKind = null,
        int? Count = null,
        bool? RequiresReview = null,
        bool? SuppressesChildObjects = null,
        string? ObjectCandidateId = null,
        string? SuppressedByAggregateId = null,
        RoutingSuppressionReason? SuppressionReason = null,
        RoutingSuppressedObjectAction? SuppressionAction = null,
        string? ReplacementRoutingObstacleId = null,
        string? RoomUseHintId = null,
        IReadOnlyList<string>? DetectedTags = null);
}
