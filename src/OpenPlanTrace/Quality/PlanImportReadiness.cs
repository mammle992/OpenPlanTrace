using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public enum PlanImportReadinessGrade
{
    Unknown = 0,
    Blocked,
    ReviewRequired,
    Usable,
    Strong
}

public sealed record PlanImportReadiness(
    string Grade,
    double Score,
    bool ReadyForGeometryImport,
    bool ReadyForMetricImport,
    bool ReadyForRoutingImport,
    bool RequiresReview,
    IReadOnlyList<string> BlockingIssueCodes,
    IReadOnlyList<string> ReviewIssueCodes,
    IReadOnlyList<string> RecommendedActions,
    IReadOnlyList<string> Evidence)
{
    private const string PdfRasterOcrRequiredIssueCode = "quality.pdf_raster_ocr_required";
    private const string RasterNoExtractedPrimitivesIssueCode = "quality.raster_no_extracted_primitives";
    private const string RasterLowExtractionConfidenceIssueCode = "quality.raster_low_extraction_confidence";
    private const string OpeningRoomSideLinksIncompleteIssueCode = "quality.scan_risk.opening_room_side_links_incomplete";
    private const string RoutingPassageRoomSideLinksIncompleteIssueCode = "quality.scan_risk.routing_passage_room_side_links_incomplete";
    private const string OpeningPlacementInconsistentIssueCode = "placement.opening.placement_inconsistent";
    private const string WallEvidenceRequiresReviewIssueCode = "placement.wall_evidence.requires_review";

    public static PlanImportReadiness Empty { get; } =
        new(
            "Blocked",
            0,
            false,
            false,
            false,
            true,
            new[] { "placement.import.no_pages" },
            Array.Empty<string>(),
            new[] { "Scan a supported plan before attempting downstream import." },
            new[] { "import readiness unavailable because no scan result was supplied" });

    [JsonIgnore]
    public PlanImportReadinessGrade ParsedGrade => ParseGrade(Grade);

    public static PlanImportReadinessGrade ParseGrade(string? grade) =>
        grade switch
        {
            nameof(PlanImportReadinessGrade.Strong) => PlanImportReadinessGrade.Strong,
            nameof(PlanImportReadinessGrade.Usable) => PlanImportReadinessGrade.Usable,
            nameof(PlanImportReadinessGrade.ReviewRequired) => PlanImportReadinessGrade.ReviewRequired,
            nameof(PlanImportReadinessGrade.Blocked) => PlanImportReadinessGrade.Blocked,
            _ => PlanImportReadinessGrade.Unknown
        };

    public static bool MeetsMinimumGrade(string? actualGrade, PlanImportReadinessGrade minimumGrade) =>
        (int)ParseGrade(actualGrade) >= (int)minimumGrade;

    public static PlanImportReadiness FromScanResult(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var structuralWalls = StructuralWallSelector.Select(result);
        var componentsByWallId = BuildComponentsByWallId(result.WallGraph);
        var reviewReasonsByWallId = WallPlacementContextGuards.BuildReviewReasons(result);
        var assessmentsByWallId = result.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var wallReady = 0;
        var wallReview = 0;
        foreach (var wall in structuralWalls)
        {
            componentsByWallId.TryGetValue(wall.Id, out var component);
            assessmentsByWallId.TryGetValue(wall.Id, out var evidenceAssessment);
            var reviewReasons = reviewReasonsByWallId.TryGetValue(wall.Id, out var wallReviewReasons)
                ? wallReviewReasons
                : Array.Empty<string>();
            var readiness = WallPlacementReadinessEvaluator.Evaluate(
                wall,
                result.Calibration,
                component,
                evidenceAssessment,
                reviewReasons);
            if (readiness.ReadyForCoordinatePlacement)
            {
                wallReady++;
            }

            if (readiness.RequiresReview)
            {
                wallReview++;
            }
        }

        var roomReady = result.Rooms.Count(room => room.Confidence.Value >= 0.5 && room.Boundary.Count >= 3);
        var roomReview = result.Rooms.Count(room => room.Confidence.Value < 0.5 || room.Boundary.Count < 3);
        var openingReady = result.Openings.Count(opening =>
            opening.Confidence.Value >= 0.5
            && ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening));
        var openingReview = result.Openings.Count(ScanReviewQueueSummary.NeedsOpeningReview);
        var aggregateReady = result.ObjectAggregates.Count(aggregate => aggregate.Confidence.Value >= 0.5);
        var coordinateReadyEntityCount = wallReady + roomReady + openingReady + aggregateReady;
        var reliabilityTrackedEntityCount =
            structuralWalls.Count + result.Rooms.Count + result.Openings.Count + result.ObjectAggregates.Count;
        var metricReadyEntityCount = result.Calibration.HasReliableMeasurementScale
            ? coordinateReadyEntityCount
            : 0;
        var reviewRequiredEntityCount = wallReview + roomReview + openingReview;

        return FromCounts(
            result,
            result.Document.Pages.Count,
            structuralWalls.Count,
            result.Rooms.Count,
            CountRoutingItems(result.RoutingLayer),
            Ratio(coordinateReadyEntityCount, reliabilityTrackedEntityCount),
            Ratio(metricReadyEntityCount, reliabilityTrackedEntityCount),
            reviewRequiredEntityCount,
            IssuesFromScanResult(result).ToArray(),
            coordinateReadyEntityCount,
            metricReadyEntityCount,
            reliabilityTrackedEntityCount);
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildComponentsByWallId(WallGraph graph)
    {
        var result = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in graph.Components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    result[wallId] = component;
                }
            }
        }

        return result;
    }

    public static PlanImportReadiness FromCounts(
        PlanScanResult result,
        int pageCount,
        int wallCount,
        int roomCount,
        int routingItemCount,
        double coordinateReadyRatio,
        double metricReadyRatio,
        int reviewRequiredEntityCount,
        IReadOnlyList<PlanImportReadinessIssue> issues,
        int? coordinateReadyEntityCount = null,
        int? metricReadyEntityCount = null,
        int? reliabilityTrackedEntityCount = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(issues);

        var blockingIssueCodes = new List<string>();
        if (pageCount == 0)
        {
            blockingIssueCodes.Add("placement.import.no_pages");
        }

        if (result.Diagnostics.HasErrors)
        {
            blockingIssueCodes.Add("placement.import.diagnostic_errors");
        }

        if (result.Quality.Grade is PlanScanQualityGrade.Unknown or PlanScanQualityGrade.Poor)
        {
            blockingIssueCodes.Add("placement.import.quality_blocked");
        }

        if (wallCount == 0)
        {
            blockingIssueCodes.Add("placement.import.no_walls");
        }

        if (roomCount == 0)
        {
            blockingIssueCodes.Add("placement.import.no_rooms");
        }

        if (coordinateReadyRatio < 0.85)
        {
            blockingIssueCodes.Add("placement.import.low_coordinate_ready_ratio");
        }

        if (!result.Calibration.HasReliableMeasurementScale)
        {
            blockingIssueCodes.Add("placement.import.metric_calibration_unavailable");
        }

        if (result.MeasurementConsistency.HasBlockingOutliers)
        {
            blockingIssueCodes.Add("placement.import.measurement_outliers");
        }

        if (metricReadyRatio < 0.85)
        {
            blockingIssueCodes.Add("placement.import.low_metric_ready_ratio");
        }

        if (routingItemCount == 0)
        {
            blockingIssueCodes.Add("placement.import.no_routing_items");
        }

        var readyForGeometryImport =
            pageCount > 0
            && wallCount > 0
            && roomCount > 0
            && coordinateReadyRatio >= 0.85
            && !result.Diagnostics.HasErrors
            && result.Quality.Grade is not (PlanScanQualityGrade.Unknown or PlanScanQualityGrade.Poor);
        var readyForMetricImport =
            readyForGeometryImport
            && result.Calibration.HasReliableMeasurementScale
            && !result.MeasurementConsistency.HasBlockingOutliers
            && metricReadyRatio >= 0.85;
        var readyForRoutingImport = readyForGeometryImport && routingItemCount > 0;
        var reviewIssueCodes = issues
            .Where(ShouldExposeImportReviewIssue)
            .Select(issue => issue.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        var requiresReview =
            result.Quality.RequiresReview
            || reviewRequiredEntityCount > 0
            || issues.Any(RequiresImportReview);
        var score = CalculateScore(result, coordinateReadyRatio, metricReadyRatio, wallCount, roomCount, routingItemCount, issues);
        var grade = !readyForGeometryImport
            ? "Blocked"
            : requiresReview || !readyForMetricImport || !readyForRoutingImport
                ? "ReviewRequired"
                : score >= 0.9
                    ? "Strong"
                    : "Usable";

        return new PlanImportReadiness(
            grade,
            score,
            readyForGeometryImport,
            readyForMetricImport,
            readyForRoutingImport,
            requiresReview,
            blockingIssueCodes.Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            reviewIssueCodes,
            BuildRecommendedActions(blockingIssueCodes, reviewIssueCodes, reviewRequiredEntityCount).ToArray(),
            BuildEvidence(
                grade,
                score,
                readyForGeometryImport,
                readyForMetricImport,
                readyForRoutingImport,
                coordinateReadyRatio,
                metricReadyRatio,
                reviewRequiredEntityCount,
                coordinateReadyEntityCount,
                metricReadyEntityCount,
                reliabilityTrackedEntityCount,
                QualitySummary(result.Quality),
                MeasurementOutlierSummary(result.MeasurementConsistency)).ToArray());
    }

    private static IEnumerable<PlanImportReadinessIssue> IssuesFromScanResult(PlanScanResult result)
    {
        foreach (var issue in result.Quality.Issues)
        {
            if (!ShouldIncludeQualityIssueForImport(issue.Code))
            {
                continue;
            }

            yield return new PlanImportReadinessIssue(issue.Code, issue.Severity);
        }

        if (result.Diagnostics.Messages.Any(message => string.Equals(
                message.Code,
                "wall_graph.endpoint_gap.review",
                StringComparison.Ordinal)))
        {
            yield return new PlanImportReadinessIssue(
                "placement.wall_graph.endpoint_gaps.require_review",
                DiagnosticSeverity.Warning);
        }

        if (result.Diagnostics.Messages.Any(message => string.Equals(
                message.Code,
                "wall_graph.surface_pattern_wall_overlap.review",
                StringComparison.Ordinal)))
        {
            yield return new PlanImportReadinessIssue(
                "placement.wall_graph.surface_pattern_wall_overlaps.require_review",
                DiagnosticSeverity.Warning);
        }

        if (ScanReviewQueueSummary.QueuedWallEvidenceReviews(result.WallEvidenceMap).Count > 0)
        {
            yield return new PlanImportReadinessIssue(
                WallEvidenceRequiresReviewIssueCode,
                DiagnosticSeverity.Warning);
        }

        if (!result.Calibration.HasReliableMeasurementScale)
        {
            yield return new PlanImportReadinessIssue(
                "placement.metric_coordinates.unavailable",
                DiagnosticSeverity.Warning);
        }

        if (result.MeasurementConsistency.HasOutliers)
        {
            yield return new PlanImportReadinessIssue(
                "placement.measurement_outliers.require_review",
                DiagnosticSeverity.Warning);
        }

        foreach (var opening in result.Openings.Where(opening => opening.Placement is null))
        {
            yield return new PlanImportReadinessIssue(
                "placement.opening.unanchored",
                DiagnosticSeverity.Info);
        }

        foreach (var opening in result.Openings.Where(opening =>
                     opening.Placement is not null
                     && !ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening)))
        {
            yield return new PlanImportReadinessIssue(
                OpeningPlacementInconsistentIssueCode,
                DiagnosticSeverity.Warning);
        }
    }

    private static int CountRoutingItems(PlanRoutingLayer routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count
        + routingLayer.SuppressedObjects.Count;

    private static double Ratio(int value, int total) =>
        total == 0 ? 1.0 : Math.Round(value / (double)total, 6);

    private static double CalculateScore(
        PlanScanResult result,
        double coordinateReadyRatio,
        double metricReadyRatio,
        int wallCount,
        int roomCount,
        int routingItemCount,
        IReadOnlyList<PlanImportReadinessIssue> issues)
    {
        var structureScore = (wallCount > 0 ? 0.5 : 0.0) + (roomCount > 0 ? 0.5 : 0.0);
        var routingScore = routingItemCount > 0 ? 1.0 : 0.0;
        var score =
            (coordinateReadyRatio * 0.30)
            + (metricReadyRatio * 0.20)
            + (Math.Clamp(result.Quality.OverallConfidence.Value, 0, 1) * 0.25)
            + (structureScore * 0.15)
            + (routingScore * 0.10);

        if (result.Diagnostics.HasErrors)
        {
            score *= 0.5;
        }

        if (issues.Any(issue => issue.Severity == DiagnosticSeverity.Error))
        {
            score *= 0.65;
        }

        return Math.Round(Math.Clamp(score, 0, 1), 6);
    }

    private static IEnumerable<string> BuildRecommendedActions(
        IReadOnlyList<string> blockingIssueCodes,
        IReadOnlyList<string> reviewIssueCodes,
        int reviewRequiredEntityCount)
    {
        if (reviewIssueCodes.Contains(PdfRasterOcrRequiredIssueCode, StringComparer.Ordinal))
        {
            yield return "Route image-only PDF pages through a registered raster/OCR adapter before trusting downstream placement geometry.";
        }

        if (reviewIssueCodes.Contains(RasterNoExtractedPrimitivesIssueCode, StringComparer.Ordinal))
        {
            yield return "Configure the raster extractor to emit text, linework, or polylines before importing geometry from this scanned source.";
        }

        if (reviewIssueCodes.Contains(RasterLowExtractionConfidenceIssueCode, StringComparer.Ordinal))
        {
            yield return "Review low-confidence raster/OCR evidence and source crops before trusting derived geometry or object placement.";
        }

        if (blockingIssueCodes.Contains("placement.import.no_walls", StringComparer.Ordinal)
            || blockingIssueCodes.Contains("placement.import.no_rooms", StringComparer.Ordinal)
            || blockingIssueCodes.Contains("placement.import.low_coordinate_ready_ratio", StringComparer.Ordinal))
        {
            yield return "Open the visual overlay and review wall/room geometry before importing placement coordinates.";
        }

        if (blockingIssueCodes.Contains("placement.import.metric_calibration_unavailable", StringComparer.Ordinal)
            || blockingIssueCodes.Contains("placement.import.measurement_outliers", StringComparer.Ordinal)
            || blockingIssueCodes.Contains("placement.import.low_metric_ready_ratio", StringComparer.Ordinal))
        {
            yield return "Review calibration and dimension evidence before using millimeter coordinates.";
        }

        if (reviewIssueCodes.Contains("placement.measurement_outliers.require_review", StringComparer.Ordinal)
            && !blockingIssueCodes.Contains("placement.import.measurement_outliers", StringComparer.Ordinal))
        {
            yield return "Review bounded dimension outliers, but metric coordinates can be imported with the selected calibration.";
        }

        if (reviewIssueCodes.Contains("placement.wall_graph.endpoint_gaps.require_review", StringComparer.Ordinal))
        {
            yield return "Review queued wall graph endpoint gaps before trusting topology-sensitive downstream workflows.";
        }

        if (reviewIssueCodes.Contains("placement.wall_graph.surface_pattern_wall_overlaps.require_review", StringComparer.Ordinal))
        {
            yield return "Review walls that overlap dense surface/detail patterns before using structural topology or room generation.";
        }

        if (reviewIssueCodes.Contains(WallEvidenceRequiresReviewIssueCode, StringComparer.Ordinal))
        {
            yield return "Review Wall Evidence V2 wall candidates before importing weak or recovered walls as exact structural geometry.";
        }

        if (reviewIssueCodes.Contains(OpeningRoomSideLinksIncompleteIssueCode, StringComparer.Ordinal)
            || reviewIssueCodes.Contains(RoutingPassageRoomSideLinksIncompleteIssueCode, StringComparer.Ordinal))
        {
            yield return "Review opening and routing-passage room-side links before using room adjacency, path routing, or exact door/window placement.";
        }

        if (reviewIssueCodes.Contains(OpeningPlacementInconsistentIssueCode, StringComparer.Ordinal))
        {
            yield return "Review anchored opening placement spans before using exact door/window coordinates, wall cuts, or routing passages.";
        }

        if (blockingIssueCodes.Contains("placement.import.no_routing_items", StringComparer.Ordinal))
        {
            yield return "Review wall, opening, and object aggregation output before using routing-layer data.";
        }

        if (reviewRequiredEntityCount > 0)
        {
            yield return "Inspect reliability reasons for low-confidence or review-required placement entities.";
        }

        if (reviewIssueCodes.Any(RequiresGenericIssueReviewAction))
        {
            yield return "Inspect placement issue codes and resolve warning-level scan-quality risks where they affect the target workflow.";
        }

        if (blockingIssueCodes.Count == 0 && reviewIssueCodes.Count == 0 && reviewRequiredEntityCount == 0)
        {
            yield return "Placement packet is ready for downstream import; keep benchmark coverage for this source family.";
        }
    }

    private static IEnumerable<string> BuildEvidence(
        string grade,
        double score,
        bool readyForGeometryImport,
        bool readyForMetricImport,
        bool readyForRoutingImport,
        double coordinateReadyRatio,
        double metricReadyRatio,
        int reviewRequiredEntityCount,
        int? coordinateReadyEntityCount,
        int? metricReadyEntityCount,
        int? reliabilityTrackedEntityCount,
        string qualitySummary,
        string measurementOutlierSummary)
    {
        yield return $"import readiness grade {grade} with score {score:0.###}";
        yield return $"geometry import ready: {readyForGeometryImport}";
        yield return $"metric import ready: {readyForMetricImport}";
        yield return $"routing import ready: {readyForRoutingImport}";
        yield return ReadinessRatioEvidence(
            "structural import coordinate readiness ratio",
            coordinateReadyRatio,
            coordinateReadyEntityCount,
            reliabilityTrackedEntityCount);
        yield return ReadinessRatioEvidence(
            "structural import metric readiness ratio",
            metricReadyRatio,
            metricReadyEntityCount,
            reliabilityTrackedEntityCount);
        yield return $"review-required wall/room/opening/evidence entities {reviewRequiredEntityCount}";
        yield return qualitySummary;
        yield return measurementOutlierSummary;
    }

    private static string ReadinessRatioEvidence(
        string label,
        double ratio,
        int? readyEntityCount,
        int? trackedEntityCount) =>
        readyEntityCount.HasValue && trackedEntityCount.HasValue
            ? $"{label} {ratio:0.###} ({readyEntityCount.Value}/{trackedEntityCount.Value} structural import entities)"
            : $"{label} {ratio:0.###}";

    private static string QualitySummary(PlanScanQualityReport quality) =>
        $"scan quality {quality.Grade} at {quality.OverallConfidence.Value:0.###} confidence";

    private static string MeasurementOutlierSummary(MeasurementConsistencyReport measurement)
    {
        if (!measurement.HasOutliers)
        {
            return "measurement consistency has no outliers";
        }

        var impact = measurement.HasBlockingOutliers
            ? "blocks metric import"
            : "requires review without blocking metric import";
        return $"measurement consistency has {measurement.OutlierCount}/{measurement.CheckedCount} outlier checks ({measurement.OutlierRatio:0.###}); {impact}";
    }

    private static bool RequiresImportReview(PlanImportReadinessIssue issue) =>
        issue.Severity != DiagnosticSeverity.Info
        && ShouldIncludeImportIssueCode(issue.Code);

    private static bool ShouldExposeImportReviewIssue(PlanImportReadinessIssue issue) =>
        issue.Severity != DiagnosticSeverity.Error
        && ShouldIncludeImportIssueCode(issue.Code);

    private static bool ShouldIncludeQualityIssueForImport(string code) =>
        ShouldIncludeImportIssueCode(code);

    private static bool ShouldIncludeImportIssueCode(string code) =>
        !string.Equals(code, "quality.diagnostic_warnings", StringComparison.Ordinal)
        && !string.Equals(code, "quality.object_groups_require_review", StringComparison.Ordinal)
        && !string.Equals(code, "quality.object_aggregates_require_review", StringComparison.Ordinal)
        && !string.Equals(code, "quality.measurement_outliers", StringComparison.Ordinal);

    private static bool RequiresGenericIssueReviewAction(string code) =>
        !string.Equals(code, "placement.measurement_outliers.require_review", StringComparison.Ordinal)
        && !string.Equals(code, "placement.wall_graph.endpoint_gaps.require_review", StringComparison.Ordinal)
        && !string.Equals(code, "placement.wall_graph.surface_pattern_wall_overlaps.require_review", StringComparison.Ordinal)
        && !string.Equals(code, WallEvidenceRequiresReviewIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, PdfRasterOcrRequiredIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, RasterNoExtractedPrimitivesIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, RasterLowExtractionConfidenceIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, OpeningRoomSideLinksIncompleteIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, RoutingPassageRoomSideLinksIncompleteIssueCode, StringComparison.Ordinal)
        && !string.Equals(code, OpeningPlacementInconsistentIssueCode, StringComparison.Ordinal);
}

public sealed record PlanImportReadinessIssue(
    string Code,
    DiagnosticSeverity Severity);
