using System.Globalization;
using System.Text.Json;
using static OpenPlanTrace.Export.PlacementMetricTransform;

namespace OpenPlanTrace.Export;

public sealed record PlanPlacementJsonExportOptions
{
    public bool WriteIndented { get; init; } = true;
}

public static class PlanPlacementJsonExporter
{
    public static string Serialize(
        PlanScanResult result,
        PlanPlacementJsonExportOptions? options = null)
    {
        var export = PlanPlacementExport.From(result);
        return JsonSerializer.Serialize(export, CreateJsonOptions(options));
    }

    public static async ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanPlacementJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var export = PlanPlacementExport.From(result);
        await JsonSerializer.SerializeAsync(
                stream,
                export,
                CreateJsonOptions(options),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateJsonOptions(PlanPlacementJsonExportOptions? options)
    {
        options ??= new PlanPlacementJsonExportOptions();

        return new JsonSerializerOptions
        {
            WriteIndented = options.WriteIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}

public sealed record PlanPlacementExport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string ScanSchemaVersion,
    PlacementDocumentExport Document,
    CoordinateSystemExport CoordinateSystem,
    PlacementCalibrationExport Calibration,
    PlacementQualityGateExport QualityGate,
    PlacementSummaryExport Summary,
    IReadOnlyList<PlacementPageExport> Pages,
    IReadOnlyList<PlacementSurfacePatternExport> SurfacePatterns,
    IReadOnlyList<PlacementWallExport> Walls,
    IReadOnlyList<PlacementRoomExport> Rooms,
    IReadOnlyList<PlacementOpeningExport> Openings,
    IReadOnlyList<PlacementObjectAggregateExport> ObjectAggregates,
    IReadOnlyList<PlacementWallGraphRepairCandidateExport> WallGraphRepairCandidates,
    PlacementRoutingLayerExport RoutingLayer,
    IReadOnlyList<PlacementIssueExport> Issues)
{
    public const string CurrentSchemaVersion = "openplantrace.placement.v1";

    public static PlanPlacementExport From(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var primitiveSources = PrimitiveSourceExport.From(result.Document).ToArray();
        var sourceLookup = primitiveSources
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var wallReviewReasons = BuildWallReviewReasons(result.Diagnostics.Messages);
        var routingLayer = result.RoutingLayer;
        var pages = result.Document.Pages.Select(PlacementPageExport.From).ToArray();
        var surfacePatterns = result.SurfacePatterns
            .Select(pattern => PlacementSurfacePatternExport.From(pattern, result.Calibration, sourceLookup))
            .ToArray();
        var walls = result.Walls
            .Select(wall => PlacementWallExport.From(
                wall,
                result.Calibration,
                sourceLookup,
                wallComponentLookup,
                wallReviewReasons.TryGetValue(wall.Id, out var reasons) ? reasons : Array.Empty<string>()))
            .ToArray();
        var rooms = result.Rooms
            .Select(room => PlacementRoomExport.From(room, result.Calibration))
            .ToArray();
        var openings = result.Openings
            .Select(opening => PlacementOpeningExport.From(opening, result.Calibration, sourceLookup))
            .ToArray();
        var objectAggregates = result.ObjectAggregates
            .Select(aggregate => PlacementObjectAggregateExport.From(aggregate, result.Calibration, sourceLookup))
            .ToArray();
        var wallGraphRepairCandidates = result.WallGraph.RepairCandidates
            .Select(candidate => PlacementWallGraphRepairCandidateExport.From(candidate, result.Calibration, sourceLookup))
            .ToArray();
        var placementRoutingLayer = PlacementRoutingLayerExport.From(routingLayer, result.Calibration, sourceLookup);
        var issues = PlacementIssueExport.From(result, sourceLookup).ToArray();

        return new PlanPlacementExport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            PlanTraceExport.CurrentSchemaVersion,
            PlacementDocumentExport.From(result.Document),
            CoordinateSystemExport.From(result.Document.Pages, result.Calibration),
            PlacementCalibrationExport.From(result),
            PlacementQualityGateExport.From(result),
            PlacementSummaryExport.From(
                result,
                pages,
                surfacePatterns,
                walls,
                rooms,
                openings,
                objectAggregates,
                wallGraphRepairCandidates,
                placementRoutingLayer,
                issues),
            pages,
            surfacePatterns,
            walls,
            rooms,
            openings,
            objectAggregates,
            wallGraphRepairCandidates,
            placementRoutingLayer,
            issues);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildWallReviewReasons(
        IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(message => string.Equals(
                     message.Code,
                     "wall_graph.surface_pattern_wall_overlap.review",
                     StringComparison.Ordinal)))
        {
            if (!diagnostic.Properties.TryGetValue("wallId", out var wallId)
                || string.IsNullOrWhiteSpace(wallId))
            {
                continue;
            }

            if (!reasons.TryGetValue(wallId, out var wallReasons))
            {
                wallReasons = new List<string>();
                reasons[wallId] = wallReasons;
            }

            var surfacePatternId = diagnostic.Properties.TryGetValue("surfacePatternId", out var patternId)
                && !string.IsNullOrWhiteSpace(patternId)
                    ? patternId
                    : "unknown";
            var overlap = diagnostic.Properties.TryGetValue("wallOverlapRatio", out var ratio)
                && !string.IsNullOrWhiteSpace(ratio)
                    ? $" at wall overlap ratio {ratio}"
                    : string.Empty;
            wallReasons.Add($"wall overlaps non-structural surface/detail pattern {surfacePatternId}{overlap}");
        }

        return reasons.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
        IReadOnlyList<WallGraphComponent> components)
    {
        var lookup = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    lookup[wallId] = component;
                }
            }
        }

        return lookup;
    }
}

public sealed record PlacementSummaryExport(
    int PageCount,
    int MainFloorplanRegionCount,
    int SurfacePatternCount,
    int WallCount,
    int StructuralWallCount,
    int ExcludedWallCount,
    int RoomCount,
    int OpeningCount,
    int AnchoredOpeningCount,
    int UnanchoredOpeningCount,
    int ObjectAggregateCount,
    int WallGraphRepairCandidateCount,
    int SuppressedChildObjectCount,
    int RoutingBarrierCount,
    int RoutingPassageCount,
    int RoutingObstacleCount,
    int RoutingRoomUseHintCount,
    int RoutingSuppressedObjectCount,
    int RoutingItemCount,
    int TotalPlacementEntityCount,
    int ReliabilityTrackedEntityCount,
    int CoordinateReadyEntityCount,
    int MetricReadyEntityCount,
    int ReviewRequiredEntityCount,
    double CoordinateReadyRatio,
    double MetricReadyRatio,
    int IssueCount,
    int InfoIssueCount,
    int WarningIssueCount,
    int ErrorIssueCount,
    int SourcePrimitiveReferenceCount,
    int UniqueSourcePrimitiveReferenceCount,
    PlacementImportReadinessExport ImportReadiness,
    IReadOnlyList<PlacementPageSummaryExport> PageSummaries,
    IReadOnlyList<string> Evidence)
{
    public static PlacementSummaryExport From(
        PlanScanResult result,
        IReadOnlyList<PlacementPageExport> pages,
        IReadOnlyList<PlacementSurfacePatternExport> surfacePatterns,
        IReadOnlyList<PlacementWallExport> walls,
        IReadOnlyList<PlacementRoomExport> rooms,
        IReadOnlyList<PlacementOpeningExport> openings,
        IReadOnlyList<PlacementObjectAggregateExport> objectAggregates,
        IReadOnlyList<PlacementWallGraphRepairCandidateExport> wallGraphRepairCandidates,
        PlacementRoutingLayerExport routingLayer,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var routingItemCount = CountRoutingItems(routingLayer);
        var structuralWalls = walls
            .Where(wall => !wall.ExcludedFromStructuralTopology)
            .ToArray();
        var reliabilityTrackedEntityCount = walls.Count + rooms.Count + openings.Count + objectAggregates.Count;
        var coordinateReadyEntityCount =
            walls.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + rooms.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + openings.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + objectAggregates.Count(item => item.Reliability.ReadyForCoordinatePlacement);
        var metricReadyEntityCount =
            walls.Count(item => item.Reliability.ReadyForMetricPlacement)
            + rooms.Count(item => item.Reliability.ReadyForMetricPlacement)
            + openings.Count(item => item.Reliability.ReadyForMetricPlacement)
            + objectAggregates.Count(item => item.Reliability.ReadyForMetricPlacement);
        var reviewRequiredEntityCount =
            walls.Count(item => item.Reliability.RequiresReview)
            + rooms.Count(item => item.Reliability.RequiresReview)
            + openings.Count(item => item.Reliability.RequiresReview)
            + objectAggregates.Count(item => item.Reliability.RequiresReview);
        var importReliabilityTrackedEntityCount =
            structuralWalls.Length + rooms.Count + openings.Count + objectAggregates.Count;
        var importCoordinateReadyEntityCount =
            structuralWalls.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + rooms.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + openings.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + objectAggregates.Count(item => item.Reliability.ReadyForCoordinatePlacement);
        var importMetricReadyEntityCount =
            structuralWalls.Count(item => item.Reliability.ReadyForMetricPlacement)
            + rooms.Count(item => item.Reliability.ReadyForMetricPlacement)
            + openings.Count(item => item.Reliability.ReadyForMetricPlacement)
            + objectAggregates.Count(item => item.Reliability.ReadyForMetricPlacement);
        var importReviewRequiredEntityCount =
            structuralWalls.Count(item => item.Reliability.RequiresReview)
            + rooms.Count(item => item.Reliability.RequiresReview)
            + openings.Count(item => item.Reliability.RequiresReview);
        var mainFloorplanRegions = result.SheetRegions
            .Where(region => region.Kind == RegionKind.MainFloorPlan)
            .ToArray();
        var sourcePrimitiveIds = SourcePrimitiveIds(surfacePatterns, walls, openings, objectAggregates, wallGraphRepairCandidates, routingLayer).ToArray();
        var coordinateReadyRatio = Ratio(coordinateReadyEntityCount, reliabilityTrackedEntityCount);
        var metricReadyRatio = Ratio(metricReadyEntityCount, reliabilityTrackedEntityCount);

        return new PlacementSummaryExport(
            pages.Count,
            mainFloorplanRegions.Length,
            surfacePatterns.Count,
            walls.Count,
            walls.Count(wall => !wall.ExcludedFromStructuralTopology),
            walls.Count(wall => wall.ExcludedFromStructuralTopology),
            rooms.Count,
            openings.Count,
            openings.Count(opening => opening.Placement is not null),
            openings.Count(opening => opening.Placement is null),
            objectAggregates.Count,
            wallGraphRepairCandidates.Count,
            routingLayer.SuppressedObjectCandidateIds.Count,
            routingLayer.Barriers.Count,
            routingLayer.Passages.Count,
            routingLayer.Obstacles.Count,
            routingLayer.RoomUseHints.Count,
            routingLayer.SuppressedObjects.Count,
            routingItemCount,
            walls.Count + rooms.Count + openings.Count + objectAggregates.Count + routingItemCount,
            reliabilityTrackedEntityCount,
            coordinateReadyEntityCount,
            metricReadyEntityCount,
            reviewRequiredEntityCount,
            coordinateReadyRatio,
            metricReadyRatio,
            issues.Count,
            issues.Count(issue => string.Equals(issue.Severity, DiagnosticSeverity.Info.ToString(), StringComparison.Ordinal)),
            issues.Count(issue => string.Equals(issue.Severity, DiagnosticSeverity.Warning.ToString(), StringComparison.Ordinal)),
            issues.Count(issue => string.Equals(issue.Severity, DiagnosticSeverity.Error.ToString(), StringComparison.Ordinal)),
            sourcePrimitiveIds.Length,
            sourcePrimitiveIds.Distinct(StringComparer.Ordinal).Count(),
            PlacementImportReadinessExport.From(
                result,
                pages.Count,
                structuralWalls.Length,
                rooms.Count,
                routingItemCount,
                Ratio(importCoordinateReadyEntityCount, importReliabilityTrackedEntityCount),
                Ratio(importMetricReadyEntityCount, importReliabilityTrackedEntityCount),
                importReviewRequiredEntityCount,
                issues),
            pages.Select(page => PlacementPageSummaryExport.From(
                    page,
                    mainFloorplanRegions,
                    surfacePatterns,
                    walls,
                    rooms,
                    openings,
                    objectAggregates,
                    wallGraphRepairCandidates,
                    routingLayer,
                    issues))
                .ToArray(),
            BuildEvidence(result, reliabilityTrackedEntityCount, coordinateReadyEntityCount, metricReadyEntityCount, reviewRequiredEntityCount, issues));
    }

    private static int CountRoutingItems(PlacementRoutingLayerExport routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count
        + routingLayer.SuppressedObjects.Count;

    private static double Ratio(int value, int total) =>
        total == 0 ? 1.0 : Math.Round(value / (double)total, 6);

    private static IEnumerable<string> SourcePrimitiveIds(
        IReadOnlyList<PlacementSurfacePatternExport> surfacePatterns,
        IReadOnlyList<PlacementWallExport> walls,
        IReadOnlyList<PlacementOpeningExport> openings,
        IReadOnlyList<PlacementObjectAggregateExport> objectAggregates,
        IReadOnlyList<PlacementWallGraphRepairCandidateExport> wallGraphRepairCandidates,
        PlacementRoutingLayerExport routingLayer)
    {
        foreach (var id in surfacePatterns.SelectMany(item => item.SourcePrimitiveIds))
        {
            yield return id;
        }

        foreach (var id in walls.SelectMany(item => item.SourcePrimitiveIds))
        {
            yield return id;
        }

        foreach (var id in openings.SelectMany(item => item.SourcePrimitiveIds))
        {
            yield return id;
        }

        foreach (var id in objectAggregates.SelectMany(item => item.SourcePrimitiveIds))
        {
            yield return id;
        }

        foreach (var id in wallGraphRepairCandidates.SelectMany(item => item.SourcePrimitiveIds))
        {
            yield return id;
        }

        foreach (var id in routingLayer.Barriers.SelectMany(item => item.SourcePrimitiveIds)
                     .Concat(routingLayer.Passages.SelectMany(item => item.SourcePrimitiveIds))
                     .Concat(routingLayer.Obstacles.SelectMany(item => item.SourcePrimitiveIds))
                     .Concat(routingLayer.RoomUseHints.SelectMany(item => item.SourcePrimitiveIds))
                     .Concat(routingLayer.SuppressedObjects.SelectMany(item => item.SourcePrimitiveIds))
                     .Concat(routingLayer.IgnoredObjects.SelectMany(item => item.SourcePrimitiveIds)))
        {
            yield return id;
        }
    }

    private static IReadOnlyList<string> BuildEvidence(
        PlanScanResult result,
        int reliabilityTrackedEntityCount,
        int coordinateReadyEntityCount,
        int metricReadyEntityCount,
        int reviewRequiredEntityCount,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var evidence = new List<string>
        {
            $"placement summary covers {result.Document.Pages.Count} page(s)",
            $"coordinate-ready entities {coordinateReadyEntityCount}/{reliabilityTrackedEntityCount}",
            $"metric-ready entities {metricReadyEntityCount}/{reliabilityTrackedEntityCount}"
        };

        if (reviewRequiredEntityCount > 0)
        {
            evidence.Add($"{reviewRequiredEntityCount} reliability-tracked entity/entities require review");
        }

        if (result.SurfacePatterns.Count > 0)
        {
            evidence.Add($"{result.SurfacePatterns.Count} non-structural surface/detail pattern(s) exported separately from walls");
        }

        if (issues.Count > 0)
        {
            evidence.Add($"{issues.Count} placement issue(s) exported");
        }

        return evidence;
    }
}

public sealed record PlacementImportReadinessExport(
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
    public static PlacementImportReadinessExport From(
        PlanScanResult result,
        int pageCount,
        int wallCount,
        int roomCount,
        int routingItemCount,
        double coordinateReadyRatio,
        double metricReadyRatio,
        int reviewRequiredEntityCount,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var readiness = PlanImportReadiness.FromCounts(
            result,
            pageCount,
            wallCount,
            roomCount,
            routingItemCount,
            coordinateReadyRatio,
            metricReadyRatio,
            reviewRequiredEntityCount,
            issues.Select(ToReadinessIssue).ToArray());

        return From(readiness);
    }

    public static PlacementImportReadinessExport From(PlanImportReadiness readiness) =>
        new(
            readiness.Grade,
            readiness.Score,
            readiness.ReadyForGeometryImport,
            readiness.ReadyForMetricImport,
            readiness.ReadyForRoutingImport,
            readiness.RequiresReview,
            readiness.BlockingIssueCodes,
            readiness.ReviewIssueCodes,
            readiness.RecommendedActions,
            readiness.Evidence);

    private static PlanImportReadinessIssue ToReadinessIssue(PlacementIssueExport issue) =>
        new(
            ToImportReadinessIssueCode(issue.Code),
            Enum.TryParse<DiagnosticSeverity>(issue.Severity, out var severity)
                ? severity
                : DiagnosticSeverity.Info);

    private static string ToImportReadinessIssueCode(string code) =>
        string.Equals(code, "placement.review.wall_graph_endpoint_gap", StringComparison.Ordinal)
            ? "placement.wall_graph.endpoint_gaps.require_review"
            : string.Equals(code, "placement.review.surface_pattern_wall_overlap", StringComparison.Ordinal)
                ? "placement.wall_graph.surface_pattern_wall_overlaps.require_review"
            : code;
}

public sealed record PlacementPageSummaryExport(
    int PageNumber,
    RectExport PageBounds,
    RectExport? MainFloorplanBounds,
    RectExport? DetectionBounds,
    RectExport? DetectionBoundsMillimeters,
    int SurfacePatternCount,
    int WallCount,
    int StructuralWallCount,
    int ExcludedWallCount,
    int RoomCount,
    int OpeningCount,
    int AnchoredOpeningCount,
    int UnanchoredOpeningCount,
    int ObjectAggregateCount,
    int WallGraphRepairCandidateCount,
    int RoutingItemCount,
    int ReliabilityTrackedEntityCount,
    int CoordinateReadyEntityCount,
    int MetricReadyEntityCount,
    int ReviewRequiredEntityCount,
    int IssueCount)
{
    public static PlacementPageSummaryExport From(
        PlacementPageExport page,
        IReadOnlyList<SheetRegion> mainFloorplanRegions,
        IReadOnlyList<PlacementSurfacePatternExport> surfacePatterns,
        IReadOnlyList<PlacementWallExport> walls,
        IReadOnlyList<PlacementRoomExport> rooms,
        IReadOnlyList<PlacementOpeningExport> openings,
        IReadOnlyList<PlacementObjectAggregateExport> objectAggregates,
        IReadOnlyList<PlacementWallGraphRepairCandidateExport> wallGraphRepairCandidates,
        PlacementRoutingLayerExport routingLayer,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var pageSurfacePatterns = surfacePatterns.Where(pattern => pattern.PageNumber == page.PageNumber).ToArray();
        var pageWalls = walls.Where(wall => wall.PageNumber == page.PageNumber).ToArray();
        var pageRooms = rooms.Where(room => room.PageNumber == page.PageNumber).ToArray();
        var pageOpenings = openings.Where(opening => opening.PageNumber == page.PageNumber).ToArray();
        var pageObjectAggregates = objectAggregates.Where(aggregate => aggregate.PageNumber == page.PageNumber).ToArray();
        var pageWallGraphRepairCandidateCount = wallGraphRepairCandidates.Count(candidate => candidate.PageNumber == page.PageNumber);
        var routingItems = CountRoutingItemsForPage(routingLayer, page.PageNumber);
        var reliabilityTrackedEntityCount = pageWalls.Length + pageRooms.Length + pageOpenings.Length + pageObjectAggregates.Length;
        var coordinateReadyEntityCount =
            pageWalls.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageRooms.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageOpenings.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageObjectAggregates.Count(item => item.Reliability.ReadyForCoordinatePlacement);
        var metricReadyEntityCount =
            pageWalls.Count(item => item.Reliability.ReadyForMetricPlacement)
            + pageRooms.Count(item => item.Reliability.ReadyForMetricPlacement)
            + pageOpenings.Count(item => item.Reliability.ReadyForMetricPlacement)
            + pageObjectAggregates.Count(item => item.Reliability.ReadyForMetricPlacement);

        return new PlacementPageSummaryExport(
            page.PageNumber,
            page.Bounds,
            Union(mainFloorplanRegions
                .Where(region => region.PageNumber == page.PageNumber)
                .Select(region => region.Bounds)),
            Union(BoundsForPage(page.PageNumber, pageSurfacePatterns, pageWalls, pageRooms, pageOpenings, pageObjectAggregates, routingLayer, metric: false)),
            Union(BoundsForPage(page.PageNumber, pageSurfacePatterns, pageWalls, pageRooms, pageOpenings, pageObjectAggregates, routingLayer, metric: true)),
            pageSurfacePatterns.Length,
            pageWalls.Length,
            pageWalls.Count(wall => !wall.ExcludedFromStructuralTopology),
            pageWalls.Count(wall => wall.ExcludedFromStructuralTopology),
            pageRooms.Length,
            pageOpenings.Length,
            pageOpenings.Count(opening => opening.Placement is not null),
            pageOpenings.Count(opening => opening.Placement is null),
            pageObjectAggregates.Length,
            pageWallGraphRepairCandidateCount,
            routingItems,
            reliabilityTrackedEntityCount,
            coordinateReadyEntityCount,
            metricReadyEntityCount,
            pageWalls.Count(item => item.Reliability.RequiresReview)
            + pageRooms.Count(item => item.Reliability.RequiresReview)
            + pageOpenings.Count(item => item.Reliability.RequiresReview)
            + pageObjectAggregates.Count(item => item.Reliability.RequiresReview),
            issues.Count(issue => issue.PageNumber == page.PageNumber));
    }

    private static int CountRoutingItemsForPage(PlacementRoutingLayerExport routingLayer, int pageNumber) =>
        routingLayer.Barriers.Count(item => item.PageNumber == pageNumber)
        + routingLayer.Passages.Count(item => item.PageNumber == pageNumber)
        + routingLayer.Obstacles.Count(item => item.PageNumber == pageNumber)
        + routingLayer.RoomUseHints.Count(item => item.PageNumber == pageNumber)
        + routingLayer.SuppressedObjects.Count(item => item.PageNumber == pageNumber);

    private static IEnumerable<RectExport> BoundsForPage(
        int pageNumber,
        IReadOnlyList<PlacementSurfacePatternExport> surfacePatterns,
        IReadOnlyList<PlacementWallExport> walls,
        IReadOnlyList<PlacementRoomExport> rooms,
        IReadOnlyList<PlacementOpeningExport> openings,
        IReadOnlyList<PlacementObjectAggregateExport> objectAggregates,
        PlacementRoutingLayerExport routingLayer,
        bool metric)
    {
        foreach (var pattern in surfacePatterns.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? pattern.BoundsMillimeters! : pattern.Bounds;
        }

        foreach (var wall in walls.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? wall.BoundsMillimeters! : wall.Bounds;
        }

        foreach (var room in rooms.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? room.BoundsMillimeters! : room.Bounds;
        }

        foreach (var opening in openings.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? opening.BoundsMillimeters! : opening.Bounds;
        }

        foreach (var aggregate in objectAggregates.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? aggregate.BoundsMillimeters! : aggregate.Bounds;
        }

        foreach (var barrier in routingLayer.Barriers.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? barrier.BoundsMillimeters! : barrier.Bounds;
        }

        foreach (var passage in routingLayer.Passages.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? passage.BoundsMillimeters! : passage.Bounds;
        }

        foreach (var obstacle in routingLayer.Obstacles.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? obstacle.BoundsMillimeters! : obstacle.Bounds;
        }

        foreach (var hint in routingLayer.RoomUseHints.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? hint.BoundsMillimeters! : hint.Bounds;
        }

        foreach (var suppressed in routingLayer.SuppressedObjects.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? suppressed.CandidateBoundsMillimeters! : suppressed.CandidateBounds;
        }

        foreach (var ignored in routingLayer.IgnoredObjects.Where(item => item.PageNumber == pageNumber))
        {
            yield return metric ? ignored.CandidateBoundsMillimeters! : ignored.CandidateBounds;
        }
    }

    private static RectExport? Union(IEnumerable<PlanRect> rects)
    {
        var bounds = PlanRect.Union(rects);
        return bounds.IsEmpty ? null : RectExport.From(bounds);
    }

    private static RectExport? Union(IEnumerable<RectExport?> rects)
    {
        var bounds = PlanRect.Union(rects
            .Where(rect => rect is not null)
            .Select(rect => new PlanRect(rect!.X, rect.Y, rect.Width, rect.Height)));
        return bounds.IsEmpty ? null : RectExport.From(bounds);
    }
}

public sealed record PlacementDocumentExport(
    string Id,
    string? SourceName,
    string? SourcePath,
    string? SourceFormat,
    string? Loader,
    string? SourceKind,
    string? EffectiveSourceKind,
    string? ClipboardContentKind,
    string? FileExtension,
    string? ContentType,
    bool IsDwgDerived,
    string? DwgConversion,
    string? DwgConverter,
    string? DwgIntermediateFormat,
    string? DwgIntermediateLoader,
    string? RasterAdapter,
    string? RasterExtractor,
    string? RasterExtractorVersion,
    string? RasterModelName,
    string? RasterModelVersion,
    IReadOnlyDictionary<string, string> Properties)
{
    public static PlacementDocumentExport From(PlanDocument document) =>
        new(
            document.Id,
            document.Metadata.SourceName,
            document.Metadata.SourcePath,
            ReadProperty(document.Metadata.Properties, "format"),
            ReadProperty(document.Metadata.Properties, "loader"),
            ReadProperty(document.Metadata.Properties, "sourceKind") ?? InferSourceKind(ReadProperty(document.Metadata.Properties, "format")),
            ReadProperty(document.Metadata.Properties, "effectiveSourceKind") ?? InferSourceKind(ReadProperty(document.Metadata.Properties, "format")),
            ReadProperty(document.Metadata.Properties, "clipboardContentKind"),
            ReadProperty(document.Metadata.Properties, "fileExtension"),
            ReadProperty(document.Metadata.Properties, "contentType"),
            ComputeIsDwgDerived(document.Metadata.Properties),
            ReadProperty(document.Metadata.Properties, "dwg.conversion"),
            ReadProperty(document.Metadata.Properties, "dwg.converter"),
            ReadProperty(document.Metadata.Properties, "dwg.intermediateFormat"),
            ReadProperty(document.Metadata.Properties, "dwg.intermediateLoader"),
            ReadProperty(document.Metadata.Properties, "raster.adapter"),
            ReadProperty(document.Metadata.Properties, "raster.extractor") ?? ReadProperty(document.Metadata.Properties, "extractorName"),
            ReadProperty(document.Metadata.Properties, "raster.extractorVersion") ?? ReadProperty(document.Metadata.Properties, "extractorVersion"),
            ReadProperty(document.Metadata.Properties, "raster.modelName") ?? ReadProperty(document.Metadata.Properties, "modelName"),
            ReadProperty(document.Metadata.Properties, "raster.modelVersion") ?? ReadProperty(document.Metadata.Properties, "modelVersion"),
            document.Metadata.Properties
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static string? ReadProperty(
        IReadOnlyDictionary<string, string> properties,
        string key)
    {
        foreach (var pair in properties)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value.Trim();
            }
        }

        return null;
    }

    private static bool ComputeIsDwgDerived(IReadOnlyDictionary<string, string> properties) =>
        string.Equals(ReadProperty(properties, "format"), "dwg", StringComparison.OrdinalIgnoreCase)
        || ReadProperty(properties, "dwg.conversion") is not null
        || ReadProperty(properties, "dwg.converter") is not null;

    private static string? InferSourceKind(string? format) =>
        format?.Trim().ToLowerInvariant() switch
        {
            "pdf" => PlanSourceKind.Pdf.ToString(),
            "dwg" => PlanSourceKind.Dwg.ToString(),
            "dxf" => PlanSourceKind.Dxf.ToString(),
            "raster" => PlanSourceKind.RasterImage.ToString(),
            "raster-image" => PlanSourceKind.RasterImage.ToString(),
            "vector" => PlanSourceKind.VectorImage.ToString(),
            "vector-image" => PlanSourceKind.VectorImage.ToString(),
            "extracted-primitives" => PlanSourceKind.ExtractedPrimitives.ToString(),
            _ => null
        };
}

public sealed record PlacementPageExport(
    int PageNumber,
    double Width,
    double Height,
    RectExport Bounds)
{
    public static PlacementPageExport From(PlanPage page) =>
        new(page.Number, page.Size.Width, page.Size.Height, RectExport.From(new PlanRect(0, 0, page.Size.Width, page.Size.Height)));
}

public sealed record PlacementCalibrationExport(
    string DrawingUnit,
    string RealWorldUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    bool HasReliableMeasurementScale,
    string MetricCoordinateStatus,
    int EvidenceCount,
    int ScaleGroupCount,
    int MeasurementCheckedCount,
    int MeasurementConsistentCount,
    int MeasurementOutlierCount,
    double? MeasurementConfidence,
    IReadOnlyList<PlacementScaleGroupExport> ScaleGroups,
    IReadOnlyList<string> Evidence)
{
    public static PlacementCalibrationExport From(PlanScanResult result)
    {
        var calibration = result.Calibration;
        var consistency = result.MeasurementConsistency;
        return new PlacementCalibrationExport(
            calibration.DrawingUnit.ToString(),
            calibration.RealWorldUnit.ToString(),
            calibration.ScaleRatio,
            calibration.MillimetersPerDrawingUnit,
            calibration.HasReliableMeasurementScale,
            CreateMetricCoordinateStatus(result),
            calibration.Evidence.Count,
            calibration.ScaleGroups.Count,
            consistency.CheckedCount,
            consistency.ConsistentCount,
            consistency.OutlierCount,
            consistency.Confidence.Value,
            calibration.ScaleGroups.Select(PlacementScaleGroupExport.From).ToArray(),
            calibration.Evidence.Select(item => item.Description).ToArray());
    }

    private static string CreateMetricCoordinateStatus(PlanScanResult result)
    {
        if (!result.Calibration.HasReliableMeasurementScale)
        {
            return "Unavailable";
        }

        if (result.MeasurementConsistency.HasBlockingOutliers)
        {
            return "BlockedByOutlierReview";
        }

        return result.MeasurementConsistency.HasTolerableOutliers
            ? "AvailableWithOutlierReview"
            : "Available";
    }
}

public sealed record PlacementScaleGroupExport(
    string Id,
    int? PageNumber,
    string Scope,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    IReadOnlyList<string> SourceRegionIds)
{
    public static PlacementScaleGroupExport From(CalibrationScaleGroup group) =>
        new(
            group.Id,
            group.PageNumber,
            group.Scope.ToString(),
            group.MillimetersPerDrawingUnit,
            group.Confidence.Value,
            group.SourceRegionIds);
}

public sealed record PlacementQualityGateExport(
    string CoordinateTrust,
    string MetricTrust,
    bool ReadyForCoordinatePlacement,
    bool ReadyForMetricPlacement,
    string QualityGrade,
    double QualityConfidence,
    bool RequiresReview,
    bool HasReliableCalibration,
    int DiagnosticWarningCount,
    int DiagnosticErrorCount,
    IReadOnlyList<string> Evidence)
{
    public static PlacementQualityGateExport From(PlanScanResult result)
    {
        var hasCoordinateErrors = result.Diagnostics.HasErrors
            || result.Quality.Grade is PlanScanQualityGrade.Unknown or PlanScanQualityGrade.Poor;
        var readyForCoordinatePlacement = !hasCoordinateErrors && result.Quality.OverallConfidence.Value >= 0.5;
        var readyForMetricPlacement = readyForCoordinatePlacement
            && result.Calibration.HasReliableMeasurementScale
            && !result.MeasurementConsistency.HasBlockingOutliers;

        return new PlacementQualityGateExport(
            readyForCoordinatePlacement
                ? result.Quality.RequiresReview ? "UsableWithReview" : "Usable"
                : "Blocked",
            result.Calibration.HasReliableMeasurementScale
                ? result.MeasurementConsistency.HasBlockingOutliers
                    ? "CalibratedWithBlockingOutlierReview"
                    : result.MeasurementConsistency.HasTolerableOutliers
                        ? "CalibratedWithOutlierReview"
                        : "Calibrated"
                : "Uncalibrated",
            readyForCoordinatePlacement,
            readyForMetricPlacement,
            result.Quality.Grade.ToString(),
            result.Quality.OverallConfidence.Value,
            result.Quality.RequiresReview,
            result.Calibration.HasReliableMeasurementScale,
            result.Diagnostics.WarningCount,
            result.Diagnostics.ErrorCount,
            QualityEvidence(result, readyForCoordinatePlacement, readyForMetricPlacement));
    }

    private static IReadOnlyList<string> QualityEvidence(
        PlanScanResult result,
        bool readyForCoordinatePlacement,
        bool readyForMetricPlacement)
    {
        var evidence = new List<string>
        {
            $"Coordinate placement ready: {readyForCoordinatePlacement}.",
            $"Metric placement ready: {readyForMetricPlacement}.",
            $"Scan quality {result.Quality.Grade} with {result.Quality.OverallConfidence.Value:0.###} confidence."
        };

        if (!result.Calibration.HasReliableMeasurementScale)
        {
            evidence.Add("Metric coordinates are unavailable because calibration is not reliable.");
        }

        if (result.MeasurementConsistency.HasOutliers)
        {
            var impact = result.MeasurementConsistency.HasBlockingOutliers
                ? "block metric placement"
                : "require review but do not block metric placement";
            evidence.Add($"Metric coordinates {impact} because {result.MeasurementConsistency.OutlierCount}/{result.MeasurementConsistency.CheckedCount} dimension check(s) are outliers.");
        }

        if (result.Diagnostics.HasErrors)
        {
            evidence.Add("Scanner diagnostics contain errors.");
        }

        return evidence;
    }
}

public sealed record PlacementReliabilityExport(
    bool ReadyForCoordinatePlacement,
    bool ReadyForMetricPlacement,
    bool RequiresReview,
    double Confidence,
    IReadOnlyList<string> Reasons);

public sealed record PlacementSurfacePatternExport(
    string Id,
    int PageNumber,
    string Kind,
    string Orientation,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string? SourceRegionId,
    int LineCount,
    int HorizontalLineCount,
    int VerticalLineCount,
    int IntersectionCount,
    double? HorizontalMedianSpacing,
    double? VerticalMedianSpacing,
    double? MedianSpacing,
    bool ExcludedFromWallDetection,
    bool ExcludedFromStructuralTopology,
    double Confidence,
    bool RequiresReview,
    string RecommendedAction,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementSurfacePatternExport From(
        SurfacePatternCandidate pattern,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementSurfacePatternExport(
            pattern.Id,
            pattern.PageNumber,
            pattern.Kind.ToString(),
            pattern.Orientation.ToString(),
            RectExport.From(pattern.Bounds),
            ScaleRect(pattern.Bounds, scale),
            PointExport.From(pattern.Bounds.Center),
            ScalePoint(pattern.Bounds.Center, scale),
            scale,
            pattern.SourceRegionId,
            pattern.LineCount,
            pattern.HorizontalLineCount,
            pattern.VerticalLineCount,
            pattern.IntersectionCount,
            pattern.HorizontalMedianSpacing,
            pattern.VerticalMedianSpacing,
            pattern.MedianSpacing,
            pattern.ExcludedFromWallDetection,
            pattern.ExcludedFromStructuralTopology,
            pattern.Confidence.Value,
            pattern.RequiresReview,
            "Treat this region as non-structural detail unless a human explicitly promotes it.",
            pattern.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(pattern.SourcePrimitiveIds, sourceLookup),
            pattern.Evidence);
    }
}

public sealed record PlacementWallExport(
    string Id,
    int PageNumber,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double DrawingLength,
    double? LengthMeters,
    double ThicknessDrawingUnits,
    double? ThicknessMillimeters,
    string DetectionKind,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallExport From(
        WallSegment wall,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        IReadOnlyList<string> reviewReasons)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        var scale = ResolveMillimetersPerDrawingUnit(calibration, wall.MeasurementScaleGroupId);

        return new PlacementWallExport(
            wall.Id,
            wall.PageNumber,
            LineExport.From(wall.CenterLine),
            ScaleLine(wall.CenterLine, scale),
            RectExport.From(wall.Bounds),
            ScaleRect(wall.Bounds, scale),
            wall.DrawingLength,
            wall.LengthMeters,
            wall.Thickness,
            wall.ThicknessMillimeters,
            wall.DetectionKind.ToString(),
            component?.Id,
            component?.Kind.ToString(),
            component?.ExcludedFromStructuralTopology ?? false,
            wall.MeasurementScaleGroupId,
            scale,
            wall.Confidence.Value,
            PlacementReliability.ForWall(wall, calibration, component, reviewReasons),
            wall.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(wall.SourcePrimitiveIds, sourceLookup),
            wall.Evidence);
    }
}

public sealed record PlacementRoomExport(
    string Id,
    int PageNumber,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    IReadOnlyList<PointExport> Boundary,
    IReadOnlyList<PointExport>? BoundaryMillimeters,
    IReadOnlyList<string> WallIds,
    double DrawingArea,
    double? AreaSquareMeters,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    string? Label,
    string UseKind,
    double Confidence,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoomExport From(
        RoomRegion room,
        PlanCalibration calibration)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, room.MeasurementScaleGroupId);
        return new PlacementRoomExport(
            room.Id,
            room.PageNumber,
            RectExport.From(room.Bounds),
            ScaleRect(room.Bounds, scale),
            PointExport.From(room.Bounds.Center),
            ScalePoint(room.Bounds.Center, scale),
            room.Boundary.Select(PointExport.From).ToArray(),
            scale is > 0 ? room.Boundary.Select(point => ScalePoint(point, scale)!).ToArray() : null,
            room.WallIds,
            room.DrawingArea,
            room.AreaSquareMeters,
            room.MeasurementScaleGroupId,
            scale,
            room.Label,
            room.UseKind.ToString(),
            room.Confidence.Value,
            PlacementReliability.ForRoom(room, calibration),
            room.Evidence);
    }
}

public sealed record PlacementOpeningExport(
    string Id,
    int PageNumber,
    string Type,
    string Operation,
    string Orientation,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    string PlacementStatus,
    OpeningPlacementExport? Placement,
    string HingeSide,
    string SwingSide,
    string SwingDirection,
    PointExport? HingePoint,
    PointExport? HingePointMillimeters,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    double Confidence,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementOpeningExport From(
        OpeningCandidate opening,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, opening.MeasurementScaleGroupId);
        return new PlacementOpeningExport(
            opening.Id,
            opening.PageNumber,
            opening.Type.ToString(),
            opening.Operation.ToString(),
            opening.Orientation.ToString(),
            LineExport.From(opening.CenterLine),
            ScaleLine(opening.CenterLine, scale),
            RectExport.From(opening.Bounds),
            ScaleRect(opening.Bounds, scale),
            opening.DrawingWidth,
            opening.WidthMillimeters,
            opening.MeasurementScaleGroupId,
            scale,
            opening.Placement is null ? "Unanchored" : "Anchored",
            opening.Placement is null ? null : OpeningPlacementExport.From(opening.Placement),
            opening.HingeSide.ToString(),
            opening.SwingSide.ToString(),
            opening.SwingDirection.ToString(),
            opening.HingePoint is null ? null : PointExport.From(opening.HingePoint.Value),
            opening.HingePoint is null ? null : ScalePoint(opening.HingePoint.Value, scale),
            opening.HostWallIds,
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.Confidence.Value,
            PlacementReliability.ForOpening(opening, calibration),
            opening.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(opening.SourcePrimitiveIds, sourceLookup),
            opening.Evidence);
    }
}

public sealed record PlacementObjectAggregateExport(
    string Id,
    int PageNumber,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string Category,
    string Kind,
    string RoutingInfluence,
    string StructuralInfluence,
    bool SuppressChildObjectsForRouting,
    int ChildObjectCount,
    IReadOnlyList<string> ChildObjectIds,
    PlacementObjectAggregateCompositionExport Composition,
    IReadOnlyList<string> ObjectGroupIds,
    string? Label,
    string? RoomId,
    string? RoomLabel,
    bool RequiresReview,
    double Confidence,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementObjectAggregateExport From(
        ObjectAggregate aggregate,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementObjectAggregateExport(
            aggregate.Id,
            aggregate.PageNumber,
            RectExport.From(aggregate.Bounds),
            ScaleRect(aggregate.Bounds, scale),
            PointExport.From(aggregate.Bounds.Center),
            ScalePoint(aggregate.Bounds.Center, scale),
            scale,
            aggregate.Category.ToString(),
            aggregate.Kind.ToString(),
            aggregate.RoutingInfluence.ToString(),
            aggregate.StructuralInfluence.ToString(),
            aggregate.SuppressChildObjectsForRouting,
            aggregate.ChildObjectCount,
            aggregate.ChildObjectIds,
            PlacementObjectAggregateCompositionExport.From(aggregate.Composition, scale),
            aggregate.ObjectGroupIds,
            aggregate.Label,
            aggregate.RoomId,
            aggregate.RoomLabel,
            aggregate.RequiresReview,
            aggregate.Confidence.Value,
            PlacementReliability.ForObjectAggregate(aggregate, calibration),
            aggregate.SourcePrimitiveIds,
            aggregate.SourceLayers.Count > 0
                ? aggregate.SourceLayers
                : ExportSourceHelpers.SourceLayers(aggregate.SourcePrimitiveIds, sourceLookup),
            aggregate.Evidence);
    }
}

public sealed record PlacementObjectAggregateCompositionExport(
    IReadOnlyList<ObjectAggregateCompositionCountExport> CategoryCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> KindCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> SourceKindCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> SourceWallComponentKindCounts,
    IReadOnlyList<string> SourceWallComponentIds,
    IReadOnlyList<PlacementObjectAggregateChildObjectExport> Children)
{
    public static PlacementObjectAggregateCompositionExport From(
        ObjectAggregateComposition composition,
        double? millimetersPerDrawingUnit) =>
        new(
            composition.CategoryCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.KindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceKindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceWallComponentKindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceWallComponentIds,
            composition.Children.Select(child => PlacementObjectAggregateChildObjectExport.From(child, millimetersPerDrawingUnit)).ToArray());
}

public sealed record PlacementObjectAggregateChildObjectExport(
    string ObjectId,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    string Category,
    string Kind,
    string SourceKind,
    string? SourceWallComponentId,
    string? SourceWallComponentKind,
    string? Label,
    string? SymbolName,
    string? DetectedTag,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds)
{
    public static PlacementObjectAggregateChildObjectExport From(
        ObjectAggregateChildObject child,
        double? millimetersPerDrawingUnit) =>
        new(
            child.ObjectId,
            RectExport.From(child.Bounds),
            ScaleRect(child.Bounds, millimetersPerDrawingUnit),
            PointExport.From(child.Bounds.Center),
            ScalePoint(child.Bounds.Center, millimetersPerDrawingUnit),
            child.Category.ToString(),
            child.Kind.ToString(),
            child.SourceKind.ToString(),
            child.SourceWallComponentId,
            child.SourceWallComponentKind?.ToString(),
            child.Label,
            child.SymbolName,
            child.DetectedTag,
            child.Confidence.Value,
            child.SourcePrimitiveIds);
}

public sealed record PlacementWallGraphRepairCandidateExport(
    string Id,
    int PageNumber,
    string Kind,
    string SuggestedAction,
    string SourceNodeId,
    PointExport SourcePoint,
    PointExport? SourcePointMillimeters,
    PointExport TargetPoint,
    PointExport? TargetPointMillimeters,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistanceDrawingUnits,
    double? GapDistanceMillimeters,
    LineExport RepairLine,
    LineExport? RepairLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    double Confidence,
    bool RequiresReview,
    string RecommendedAction,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallGraphRepairCandidateExport From(
        WallGraphRepairCandidate candidate,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementWallGraphRepairCandidateExport(
            candidate.Id,
            candidate.PageNumber,
            candidate.Kind.ToString(),
            candidate.SuggestedAction.ToString(),
            candidate.SourceNodeId,
            PointExport.From(candidate.SourcePoint),
            ScalePoint(candidate.SourcePoint, scale),
            PointExport.From(candidate.TargetPoint),
            ScalePoint(candidate.TargetPoint, scale),
            candidate.TargetNodeId,
            candidate.HostWallId,
            candidate.GapDistance,
            scale is > 0 ? candidate.GapDistance * scale.Value : null,
            LineExport.From(candidate.RepairLine),
            ScaleLine(candidate.RepairLine, scale),
            RectExport.From(candidate.Bounds),
            ScaleRect(candidate.Bounds, scale),
            candidate.WallIds,
            candidate.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(candidate.SourcePrimitiveIds, sourceLookup),
            candidate.Confidence.Value,
            candidate.RequiresReview,
            RecommendedRepairAction(candidate),
            candidate.Evidence);
    }

    private static string RecommendedRepairAction(WallGraphRepairCandidate candidate) =>
        candidate.SuggestedAction == WallGraphRepairAction.SnapEndpointToWall
            ? "Review this endpoint-to-wall snap candidate before repairing the wall graph topology."
            : "Review this endpoint-to-endpoint snap candidate before repairing the wall graph topology.";
}

public sealed record PlacementRoutingLayerExport(
    IReadOnlyList<PlacementRoutingBarrierExport> Barriers,
    IReadOnlyList<PlacementRoutingPassageExport> Passages,
    IReadOnlyList<PlacementRoutingObstacleExport> Obstacles,
    IReadOnlyList<PlacementRoutingRoomUseHintExport> RoomUseHints,
    IReadOnlyList<PlacementRoutingSuppressedObjectExport> SuppressedObjects,
    IReadOnlyList<PlacementRoutingIgnoredObjectExport> IgnoredObjects,
    IReadOnlyList<string> SuppressedObjectCandidateIds,
    IReadOnlyList<string> IgnoredObjectCandidateIds,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingLayerExport From(
        PlanRoutingLayer routingLayer,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            routingLayer.Barriers.Select(barrier => PlacementRoutingBarrierExport.From(barrier, calibration, sourceLookup)).ToArray(),
            routingLayer.Passages.Select(passage => PlacementRoutingPassageExport.From(passage, calibration, sourceLookup)).ToArray(),
            routingLayer.Obstacles.Select(obstacle => PlacementRoutingObstacleExport.From(obstacle, calibration, sourceLookup)).ToArray(),
            routingLayer.RoomUseHints.Select(hint => PlacementRoutingRoomUseHintExport.From(hint, calibration, sourceLookup)).ToArray(),
            routingLayer.SuppressedObjects.Select(item => PlacementRoutingSuppressedObjectExport.From(item, calibration, sourceLookup)).ToArray(),
            routingLayer.IgnoredObjects.Select(item => PlacementRoutingIgnoredObjectExport.From(item, calibration, sourceLookup)).ToArray(),
            routingLayer.SuppressedObjectCandidateIds,
            routingLayer.IgnoredObjectCandidateIds,
            routingLayer.Evidence);
}

public sealed record PlacementRoutingBarrierExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double Thickness,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingBarrierExport From(
        RoutingBarrier barrier,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, barrier.MeasurementScaleGroupId);
        return new PlacementRoutingBarrierExport(
            barrier.Id,
            barrier.PageNumber,
            barrier.SourceId,
            barrier.SourceKind.ToString(),
            LineExport.From(barrier.CenterLine),
            ScaleLine(barrier.CenterLine, scale),
            RectExport.From(barrier.Bounds),
            ScaleRect(barrier.Bounds, scale),
            barrier.Thickness,
            barrier.DrawingLength,
            barrier.LengthMeters,
            barrier.ThicknessMillimeters,
            barrier.MeasurementScaleGroupId,
            scale,
            barrier.WallComponentId,
            barrier.WallComponentKind?.ToString(),
            barrier.ExcludedFromStructuralTopology,
            barrier.Confidence.Value,
            barrier.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(barrier.SourcePrimitiveIds, sourceLookup),
            barrier.Evidence);
    }
}

public sealed record PlacementRoutingPassageExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    string Type,
    string Operation,
    string Orientation,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    OpeningPlacementExport? Placement,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingPassageExport From(
        RoutingPassage passage,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, passage.MeasurementScaleGroupId);
        return new PlacementRoutingPassageExport(
            passage.Id,
            passage.PageNumber,
            passage.SourceId,
            passage.SourceKind.ToString(),
            passage.Type.ToString(),
            passage.Operation.ToString(),
            passage.Orientation.ToString(),
            LineExport.From(passage.CenterLine),
            ScaleLine(passage.CenterLine, scale),
            RectExport.From(passage.Bounds),
            ScaleRect(passage.Bounds, scale),
            passage.DrawingWidth,
            passage.WidthMillimeters,
            passage.MeasurementScaleGroupId,
            scale,
            passage.HostWallIds,
            passage.ConnectedRoomIds,
            passage.ConnectedRoomLabels,
            passage.Placement is null ? null : OpeningPlacementExport.From(passage.Placement),
            passage.Confidence.Value,
            passage.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(passage.SourcePrimitiveIds, sourceLookup),
            passage.Evidence);
    }
}

public sealed record PlacementRoutingObstacleExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    string ObstacleKind,
    string RoutingInfluence,
    string StructuralInfluence,
    string Category,
    string ObjectKind,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string? Label,
    string? RoomId,
    string? RoomLabel,
    bool SuppressesChildObjects,
    IReadOnlyList<string> ChildObjectIds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingObstacleExport From(
        RoutingObstacle obstacle,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementRoutingObstacleExport(
            obstacle.Id,
            obstacle.PageNumber,
            obstacle.SourceId,
            obstacle.SourceKind.ToString(),
            obstacle.ObstacleKind.ToString(),
            obstacle.RoutingInfluence.ToString(),
            obstacle.StructuralInfluence.ToString(),
            obstacle.Category.ToString(),
            obstacle.ObjectKind.ToString(),
            RectExport.From(obstacle.Bounds),
            ScaleRect(obstacle.Bounds, scale),
            PointExport.From(obstacle.Bounds.Center),
            ScalePoint(obstacle.Bounds.Center, scale),
            scale,
            obstacle.Label,
            obstacle.RoomId,
            obstacle.RoomLabel,
            obstacle.SuppressesChildObjects,
            obstacle.ChildObjectIds,
            obstacle.Confidence.Value,
            obstacle.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(obstacle.SourcePrimitiveIds, sourceLookup),
            obstacle.Evidence);
    }
}

public sealed record PlacementRoutingRoomUseHintExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    string RoomUseKind,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    PointExport Center,
    PointExport? CenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingRoomUseHintExport From(
        RoutingRoomUseHint hint,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementRoutingRoomUseHintExport(
            hint.Id,
            hint.PageNumber,
            hint.SourceId,
            hint.SourceKind.ToString(),
            hint.RoomUseKind.ToString(),
            RectExport.From(hint.Bounds),
            ScaleRect(hint.Bounds, scale),
            PointExport.From(hint.Bounds.Center),
            ScalePoint(hint.Bounds.Center, scale),
            scale,
            hint.RoomId,
            hint.RoomLabel,
            hint.Confidence.Value,
            hint.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(hint.SourcePrimitiveIds, sourceLookup),
            hint.Evidence);
    }
}

public sealed record PlacementRoutingSuppressedObjectExport(
    string Id,
    int PageNumber,
    string ObjectCandidateId,
    string SuppressedByAggregateId,
    string Reason,
    string Action,
    string? ReplacementRoutingObstacleId,
    string? RoomUseHintId,
    string AggregateRoutingInfluence,
    string AggregateStructuralInfluence,
    string CandidateCategory,
    string CandidateKind,
    RectExport CandidateBounds,
    RectExport? CandidateBoundsMillimeters,
    PointExport CandidateCenter,
    PointExport? CandidateCenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingSuppressedObjectExport From(
        RoutingSuppressedObject suppressed,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementRoutingSuppressedObjectExport(
            suppressed.Id,
            suppressed.PageNumber,
            suppressed.ObjectCandidateId,
            suppressed.SuppressedByAggregateId,
            suppressed.Reason.ToString(),
            suppressed.Action.ToString(),
            suppressed.ReplacementRoutingObstacleId,
            suppressed.RoomUseHintId,
            suppressed.AggregateRoutingInfluence.ToString(),
            suppressed.AggregateStructuralInfluence.ToString(),
            suppressed.CandidateCategory.ToString(),
            suppressed.CandidateKind.ToString(),
            RectExport.From(suppressed.CandidateBounds),
            ScaleRect(suppressed.CandidateBounds, scale),
            PointExport.From(suppressed.CandidateBounds.Center),
            ScalePoint(suppressed.CandidateBounds.Center, scale),
            scale,
            suppressed.CandidateLabel,
            suppressed.RoomId,
            suppressed.RoomLabel,
            suppressed.Confidence.Value,
            suppressed.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(suppressed.SourcePrimitiveIds, sourceLookup),
            suppressed.Evidence);
    }
}

public sealed record PlacementRoutingIgnoredObjectExport(
    string Id,
    int PageNumber,
    string ObjectCandidateId,
    string Reason,
    string RoutingInfluence,
    string StructuralInfluence,
    string CandidateCategory,
    string CandidateKind,
    string CandidateSourceKind,
    string? SourceWallComponentId,
    string? SourceWallComponentKind,
    RectExport CandidateBounds,
    RectExport? CandidateBoundsMillimeters,
    PointExport CandidateCenter,
    PointExport? CandidateCenterMillimeters,
    double? MillimetersPerDrawingUnit,
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    string? SuppressedObjectId,
    string? SuppressedByAggregateId,
    string? RoomUseHintId,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoutingIgnoredObjectExport From(
        RoutingIgnoredObject ignored,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementRoutingIgnoredObjectExport(
            ignored.Id,
            ignored.PageNumber,
            ignored.ObjectCandidateId,
            ignored.Reason.ToString(),
            ignored.RoutingInfluence.ToString(),
            ignored.StructuralInfluence.ToString(),
            ignored.CandidateCategory.ToString(),
            ignored.CandidateKind.ToString(),
            ignored.CandidateSourceKind.ToString(),
            ignored.SourceWallComponentId,
            ignored.SourceWallComponentKind?.ToString(),
            RectExport.From(ignored.CandidateBounds),
            ScaleRect(ignored.CandidateBounds, scale),
            PointExport.From(ignored.CandidateBounds.Center),
            ScalePoint(ignored.CandidateBounds.Center, scale),
            scale,
            ignored.CandidateLabel,
            ignored.RoomId,
            ignored.RoomLabel,
            ignored.SuppressedObjectId,
            ignored.SuppressedByAggregateId,
            ignored.RoomUseHintId,
            ignored.Confidence.Value,
            ignored.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(ignored.SourcePrimitiveIds, sourceLookup),
            ignored.Evidence);
    }
}

public sealed record PlacementIssueExport(
    string Code,
    string Severity,
    string Message,
    int? PageNumber,
    IReadOnlyList<int> PageNumbers,
    string? ItemId,
    RectExport? Bounds,
    RectExport? BoundsMillimeters,
    double? Confidence,
    string RecommendedAction,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence,
    IReadOnlyDictionary<string, string> Properties)
{
    private const string DenseWallPatternDiagnosticCode = "walls.dense_orthogonal_pattern_filtered";
    private const string WallGraphEndpointGapDiagnosticCode = "wall_graph.endpoint_gap.review";
    private const string SurfacePatternWallOverlapDiagnosticCode = "wall_graph.surface_pattern_wall_overlap.review";
    private const string PdfRasterOcrRequiredIssueCode = "quality.pdf_raster_ocr_required";
    private const string RasterNoExtractedPrimitivesIssueCode = "quality.raster_no_extracted_primitives";
    private const string RasterLowExtractionConfidenceIssueCode = "quality.raster_low_extraction_confidence";

    public static IEnumerable<PlacementIssueExport> From(
        PlanScanResult result,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        foreach (var issue in result.Quality.Issues)
        {
            yield return new PlacementIssueExport(
                issue.Code,
                issue.Severity.ToString(),
                issue.Message,
                null,
                Array.Empty<int>(),
                null,
                null,
                null,
                ClampRatio(issue.Confidence.Value),
                RecommendedActionForQualityIssue(issue.Code),
                Array.Empty<string>(),
                Array.Empty<string>(),
                BuildIssueEvidence(issue.Message),
                issue.Properties);
        }

        if (!result.Calibration.HasReliableMeasurementScale)
        {
            yield return new PlacementIssueExport(
                "placement.metric_coordinates.unavailable",
                DiagnosticSeverity.Warning.ToString(),
                "Metric coordinates are unavailable because the scan does not have a reliable measurement scale.",
                null,
                Array.Empty<int>(),
                null,
                null,
                null,
                ClampRatio(result.Calibration.Confidence.Value),
                "Provide a trusted drawing scale or dimension before consuming millimeter coordinates.",
                Array.Empty<string>(),
                Array.Empty<string>(),
                BuildIssueEvidence("Only page-local drawing coordinates are safe until calibration is reliable."),
                new Dictionary<string, string>
                {
                    ["calibrationConfidence"] = result.Calibration.Confidence.Value.ToString("0.###")
                });
        }

        if (result.MeasurementConsistency.HasOutliers)
        {
            var impact = result.MeasurementConsistency.HasBlockingOutliers
                ? "blocking"
                : "review";
            yield return new PlacementIssueExport(
                "placement.measurement_outliers.require_review",
                DiagnosticSeverity.Warning.ToString(),
                "Metric placement requires review because matched dimension checks contain outliers.",
                null,
                Array.Empty<int>(),
                null,
                null,
                null,
                ClampRatio(result.MeasurementConsistency.Confidence.Value),
                result.MeasurementConsistency.HasBlockingOutliers
                    ? "Resolve dimension/calibration outliers before importing metric coordinates."
                    : "Review bounded dimension outliers; selected calibration remains metric-import ready.",
                Array.Empty<string>(),
                Array.Empty<string>(),
                BuildIssueEvidence(
                    $"Checked {result.MeasurementConsistency.CheckedCount} dimension match(es); {result.MeasurementConsistency.OutlierCount} outlier(s) found.",
                    $"Metric import impact: {impact}."),
                new Dictionary<string, string>
                {
                    ["checkedCount"] = result.MeasurementConsistency.CheckedCount.ToString(),
                    ["outlierCount"] = result.MeasurementConsistency.OutlierCount.ToString(),
                    ["outlierRatio"] = result.MeasurementConsistency.OutlierRatio.ToString("0.###", CultureInfo.InvariantCulture),
                    ["metricImportImpact"] = impact
                });
        }

        foreach (var entry in result.Diagnostics.Messages
                     .Where(diagnostic => string.Equals(
                         diagnostic.Code,
                         DenseWallPatternDiagnosticCode,
                         StringComparison.Ordinal))
                     .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
                     .ThenBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var sourcePrimitiveIds = diagnostic.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["detector"] = diagnostic.Stage;
            properties["filteredLineCount"] = properties.TryGetValue("filteredLineCount", out var filteredLineCount)
                ? filteredLineCount
                : sourcePrimitiveIds.Length.ToString(CultureInfo.InvariantCulture);

            var patternEvidence = properties.TryGetValue("patterns", out var patterns) && !string.IsNullOrWhiteSpace(patterns)
                ? new[] { $"suppressed pattern summary: {patterns}" }
                : Array.Empty<string>();
            var countEvidence = new[]
            {
                $"suppressed {sourcePrimitiveIds.Length} source primitive(s) before wall reconstruction"
            };
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, scaleGroupId: null);

            yield return new PlacementIssueExport(
                "placement.review.suppressed_wall_pattern",
                diagnostic.Severity.ToString(),
                diagnostic.Message,
                diagnostic.PageNumber,
                pageNumbers,
                $"review:suppressed-wall-pattern:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Region is null ? null : ScaleRect(diagnostic.Region.Value, scale),
                ClampRatio(diagnostic.Confidence?.Value ?? 0.5),
                "Verify this dense/detail area visually before using the remaining wall graph as authoritative.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(new[] { diagnostic.Message }.Concat(patternEvidence).Concat(countEvidence)),
                properties);
        }

        foreach (var entry in result.Diagnostics.Messages
                     .Where(diagnostic => string.Equals(
                         diagnostic.Code,
                         WallGraphEndpointGapDiagnosticCode,
                         StringComparison.Ordinal))
                     .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
                     .ThenBy(diagnostic => diagnostic.Properties.TryGetValue("gapDistance", out var distance) ? distance : string.Empty, StringComparer.Ordinal)
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var sourcePrimitiveIds = diagnostic.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["detector"] = diagnostic.Stage;

            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, scaleGroupId: null);
            var gapEvidence = properties.TryGetValue("gapDistance", out var gapDistance)
                ? new[] { $"gap distance {gapDistance} drawing units" }
                : Array.Empty<string>();
            var wallEvidence = properties.TryGetValue("wallIds", out var wallIds) && !string.IsNullOrWhiteSpace(wallIds)
                ? new[] { $"candidate wall ids: {wallIds}" }
                : Array.Empty<string>();

            yield return new PlacementIssueExport(
                "placement.review.wall_graph_endpoint_gap",
                diagnostic.Severity.ToString(),
                diagnostic.Message,
                diagnostic.PageNumber,
                pageNumbers,
                $"review:wall-graph-gap:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Region is null ? null : ScaleRect(diagnostic.Region.Value, scale),
                ClampRatio(diagnostic.Confidence?.Value ?? 0.5),
                "Review or correct this possible unsnapped wall junction before importing wall graph topology.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(new[] { diagnostic.Message }.Concat(gapEvidence).Concat(wallEvidence)),
                properties);
        }

        foreach (var entry in result.Diagnostics.Messages
                     .Where(diagnostic => string.Equals(
                         diagnostic.Code,
                         SurfacePatternWallOverlapDiagnosticCode,
                         StringComparison.Ordinal))
                     .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
                     .ThenByDescending(diagnostic => ReadDiagnosticRatio(diagnostic, "wallOverlapRatio"))
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var sourcePrimitiveIds = diagnostic.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["detector"] = diagnostic.Stage;

            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, scaleGroupId: null);
            var wallEvidence = properties.TryGetValue("wallId", out var wallId) && !string.IsNullOrWhiteSpace(wallId)
                ? new[] { $"wall id: {wallId}" }
                : Array.Empty<string>();
            var patternEvidence = properties.TryGetValue("surfacePatternId", out var patternId) && !string.IsNullOrWhiteSpace(patternId)
                ? new[] { $"surface pattern id: {patternId}" }
                : Array.Empty<string>();
            var ratioEvidence = properties.TryGetValue("wallOverlapRatio", out var wallOverlapRatio) && !string.IsNullOrWhiteSpace(wallOverlapRatio)
                ? new[] { $"wall overlap ratio {wallOverlapRatio}" }
                : Array.Empty<string>();

            yield return new PlacementIssueExport(
                "placement.review.surface_pattern_wall_overlap",
                diagnostic.Severity.ToString(),
                diagnostic.Message,
                diagnostic.PageNumber,
                pageNumbers,
                $"review:surface-pattern-wall-overlap:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Region is null ? null : ScaleRect(diagnostic.Region.Value, scale),
                ClampRatio(diagnostic.Confidence?.Value ?? 0.5),
                "Review this wall/surface-pattern overlap before using the wall as structural topology.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(new[] { diagnostic.Message }.Concat(wallEvidence).Concat(patternEvidence).Concat(ratioEvidence)),
                properties);
        }

        foreach (var opening in result.Openings.Where(opening => opening.Placement is null))
        {
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, opening.MeasurementScaleGroupId);
            var sourcePrimitiveIds = opening.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            yield return new PlacementIssueExport(
                "placement.opening.unanchored",
                DiagnosticSeverity.Info.ToString(),
                "Opening has page coordinates but could not be anchored to a host-wall reference line.",
                opening.PageNumber,
                new[] { opening.PageNumber },
                opening.Id,
                RectExport.From(opening.Bounds),
                ScaleRect(opening.Bounds, scale),
                ClampRatio(opening.Confidence.Value),
                "Verify or correct host-wall anchoring before using this opening to cut walls or connect rooms.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(opening.Evidence.DefaultIfEmpty("Opening has no host-wall placement reference.")),
                new Dictionary<string, string>
                {
                    ["type"] = opening.Type.ToString(),
                    ["operation"] = opening.Operation.ToString()
                });
        }
    }

    private static IReadOnlyList<string> BuildIssueEvidence(params string[] evidence) =>
        BuildIssueEvidence((IEnumerable<string>)evidence);

    private static IReadOnlyList<string> BuildIssueEvidence(IEnumerable<string> evidence) =>
        evidence
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string RecommendedActionForQualityIssue(string code) =>
        code switch
        {
            PdfRasterOcrRequiredIssueCode =>
                "Register a real raster/OCR adapter and rescan image-only PDF pages before importing placement geometry.",
            RasterNoExtractedPrimitivesIssueCode =>
                "Configure the raster extractor to emit text, linework, or polylines before importing geometry from this scanned source.",
            RasterLowExtractionConfidenceIssueCode =>
                "Review low-confidence raster/OCR evidence and source crops before trusting derived geometry or object placement.",
            _ =>
                "Review scan quality issue before importing downstream placement geometry."
        };

    private static double ClampRatio(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(value, 0, 1)
            : 0;

    private static double ReadDiagnosticRatio(PlanDiagnostic diagnostic, string propertyName) =>
        diagnostic.Properties.TryGetValue(propertyName, out var text)
            && double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value)
            ? Math.Clamp(value, 0, 1)
            : 0;
}

internal static class PlacementReliability
{
    public static PlacementReliabilityExport ForWall(
        WallSegment wall,
        PlanCalibration calibration,
        WallGraphComponent? component,
        IReadOnlyList<string> reviewReasons)
    {
        var reasons = new List<string>();
        if (wall.Confidence.Value < 0.5)
        {
            reasons.Add("wall confidence below 0.5");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        reasons.AddRange(reviewReasons);

        return Create(
            wall.Confidence.Value,
            !reasons.Any(reason => reason.Contains("confidence", StringComparison.OrdinalIgnoreCase)),
            calibration.HasReliableMeasurementScale,
            reviewReasons.Count > 0,
            reasons);
    }

    public static PlacementReliabilityExport ForRoom(RoomRegion room, PlanCalibration calibration)
    {
        var reasons = new List<string>();
        if (room.Confidence.Value < 0.5)
        {
            reasons.Add("room confidence below 0.5");
        }

        if (room.Boundary.Count < 3)
        {
            reasons.Add("room boundary has fewer than 3 points");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        return Create(room.Confidence.Value, room.Confidence.Value >= 0.5 && room.Boundary.Count >= 3, calibration.HasReliableMeasurementScale, false, reasons);
    }

    public static PlacementReliabilityExport ForOpening(OpeningCandidate opening, PlanCalibration calibration)
    {
        var reasons = new List<string>();
        if (opening.Confidence.Value < 0.5)
        {
            reasons.Add("opening confidence below 0.5");
        }

        if (opening.Placement is null)
        {
            reasons.Add("opening is not anchored to a host-wall placement reference");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        if (opening.Operation == OpeningOperation.Unknown)
        {
            reasons.Add("opening operation unknown");
        }

        return Create(
            opening.Confidence.Value,
            opening.Confidence.Value >= 0.5 && opening.Placement is not null,
            calibration.HasReliableMeasurementScale,
            opening.Placement is null || opening.Operation == OpeningOperation.Unknown,
            reasons);
    }

    public static PlacementReliabilityExport ForObjectAggregate(
        ObjectAggregate aggregate,
        PlanCalibration calibration)
    {
        var reasons = new List<string>();
        if (aggregate.Confidence.Value < 0.5)
        {
            reasons.Add("object aggregate confidence below 0.5");
        }

        if (aggregate.RequiresReview)
        {
            reasons.Add("object aggregate requires review");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        return Create(
            aggregate.Confidence.Value,
            aggregate.Confidence.Value >= 0.5,
            calibration.HasReliableMeasurementScale,
            aggregate.RequiresReview,
            reasons);
    }

    private static PlacementReliabilityExport Create(
        double confidence,
        bool readyForCoordinatePlacement,
        bool readyForMetricPlacement,
        bool requiresReview,
        IReadOnlyList<string> reasons) =>
        new(
            readyForCoordinatePlacement,
            readyForCoordinatePlacement && readyForMetricPlacement,
            requiresReview || reasons.Count > 0,
            confidence,
            reasons);
}

internal static class PlacementMetricTransform
{
    public static double? ResolveMillimetersPerDrawingUnit(
        PlanCalibration calibration,
        string? scaleGroupId)
    {
        if (!string.IsNullOrWhiteSpace(scaleGroupId))
        {
            var group = calibration.ScaleGroups.FirstOrDefault(item =>
                string.Equals(item.Id, scaleGroupId, StringComparison.Ordinal));
            if (group?.MillimetersPerDrawingUnit is > 0)
            {
                return group.MillimetersPerDrawingUnit;
            }
        }

        return calibration.MillimetersPerDrawingUnit is > 0
            ? calibration.MillimetersPerDrawingUnit
            : null;
    }

    public static PointExport? ScalePoint(PlanPoint point, double? millimetersPerDrawingUnit) =>
        millimetersPerDrawingUnit is > 0
            ? new PointExport(point.X * millimetersPerDrawingUnit.Value, point.Y * millimetersPerDrawingUnit.Value)
            : null;

    public static LineExport? ScaleLine(PlanLineSegment line, double? millimetersPerDrawingUnit)
    {
        if (millimetersPerDrawingUnit is not > 0)
        {
            return null;
        }

        return new LineExport(
            ScalePoint(line.Start, millimetersPerDrawingUnit)!,
            ScalePoint(line.End, millimetersPerDrawingUnit)!);
    }

    public static RectExport? ScaleRect(PlanRect rect, double? millimetersPerDrawingUnit)
    {
        if (millimetersPerDrawingUnit is not > 0)
        {
            return null;
        }

        var scale = millimetersPerDrawingUnit.Value;
        return new RectExport(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }
}
