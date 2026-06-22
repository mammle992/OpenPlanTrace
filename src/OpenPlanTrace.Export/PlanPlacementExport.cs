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
    PlacementWallGraphExport WallGraph,
    PlacementRoutingLayerExport RoutingLayer,
    IReadOnlyList<PlacementIssueExport> Issues)
{
    public const string CurrentSchemaVersion = "openplantrace.placement.v9";

    public static PlanPlacementExport From(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var primitiveSources = PrimitiveSourceExport.From(result.Document).ToArray();
        var sourceLookup = primitiveSources
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var wallReviewReasons = WallReviewReasonMerger.Merge(
            BuildWallReviewReasons(result.Diagnostics.Messages),
            WallPlacementContextGuards.BuildReviewReasons(result));
        var routingLayer = result.RoutingLayer;
        var rawWallTopologySpans = WallGraphTopologySpanBuilder
            .Build(result.WallGraph, result.Walls)
            .ToArray();
        var wallTopologySpans = WallTopologySpanVisibility
            .BuildCleanPlacementTopologySpans(result)
            .ToArray();
        var wallGraphTopologySpans = rawWallTopologySpans
            .Concat(wallTopologySpans)
            .ToArray();
        var wallTopologySpansByWallId = wallTopologySpans
            .GroupBy(span => span.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var openingsByWallId = BuildWallOpeningLookup(result.Openings);
        var wallGraphRepairCandidatesByWallId = BuildWallGraphRepairCandidateLookup(result.WallGraph.RepairCandidates);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
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
                wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment) ? assessment : null,
                wallTopologySpansByWallId.TryGetValue(wall.Id, out var spans) ? spans : Array.Empty<WallGraphTopologySpan>(),
                wallTopologySpans,
                openingsByWallId.TryGetValue(wall.Id, out var wallOpenings) ? wallOpenings : Array.Empty<OpeningCandidate>(),
                wallReviewReasons.TryGetValue(wall.Id, out var reasons) ? reasons : Array.Empty<string>(),
                wallGraphRepairCandidatesByWallId.TryGetValue(wall.Id, out var repairCandidates)
                    ? repairCandidates
                    : Array.Empty<WallGraphRepairCandidate>()))
            .ToArray();
        var placementWallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var roomBoundaryWallUseCounts = BuildRoomBoundaryWallUseCounts(result.Rooms);
        var rooms = result.Rooms
            .Select(room => PlacementRoomExport.From(
                room,
                result.Calibration,
                wallEvidenceAssessments,
                placementWallsById,
                roomBoundaryWallUseCounts))
            .ToArray();
        var openings = result.Openings
            .Select(opening => PlacementOpeningExport.From(opening, result.Calibration, sourceLookup, wallEvidenceAssessments))
            .ToArray();
        var objectAggregates = result.ObjectAggregates
            .Select(aggregate => PlacementObjectAggregateExport.From(aggregate, result.Calibration, sourceLookup))
            .ToArray();
        var wallGraphRepairCandidates = result.WallGraph.RepairCandidates
            .Select(candidate => PlacementWallGraphRepairCandidateExport.From(candidate, result.Calibration, sourceLookup))
            .ToArray();
        var placementWallGraph = PlacementWallGraphExport.From(
            result.WallGraph,
            wallGraphTopologySpans,
            result.Calibration,
            sourceLookup,
            wallComponentLookup,
            wallEvidenceAssessments);
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
            placementWallGraph,
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

    private static IReadOnlyDictionary<string, IReadOnlyList<WallGraphRepairCandidate>> BuildWallGraphRepairCandidateLookup(
        IReadOnlyList<WallGraphRepairCandidate> candidates)
    {
        var lookup = new Dictionary<string, List<WallGraphRepairCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var wallId in WallGraphRepairCandidateImpact.CoordinateImpactedWallIds(candidate).Distinct(StringComparer.Ordinal))
            {
                if (!lookup.TryGetValue(wallId, out var wallCandidates))
                {
                    wallCandidates = new List<WallGraphRepairCandidate>();
                    lookup[wallId] = wallCandidates;
                }

                wallCandidates.Add(candidate);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WallGraphRepairCandidate>)pair.Value
                .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<OpeningCandidate>> BuildWallOpeningLookup(
        IReadOnlyList<OpeningCandidate> openings)
    {
        var lookup = new Dictionary<string, List<OpeningCandidate>>(StringComparer.Ordinal);
        foreach (var opening in openings.Where(opening => opening.Placement is not null))
        {
            foreach (var wallId in OpeningWallIds(opening))
            {
                if (!lookup.TryGetValue(wallId, out var wallOpenings))
                {
                    wallOpenings = new List<OpeningCandidate>();
                    lookup[wallId] = wallOpenings;
                }

                wallOpenings.Add(opening);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<OpeningCandidate>)pair.Value
                .DistinctBy(opening => opening.Id, StringComparer.Ordinal)
                .OrderBy(opening => opening.Placement?.HostWallCenterParameter ?? 0)
                .ThenBy(opening => opening.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, int> BuildRoomBoundaryWallUseCounts(
        IReadOnlyList<RoomRegion> rooms) =>
        rooms
            .SelectMany(room => room.WallIds
                .Where(wallId => !string.IsNullOrWhiteSpace(wallId))
                .Distinct(StringComparer.Ordinal))
            .GroupBy(wallId => wallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static IEnumerable<string> OpeningWallIds(OpeningCandidate opening)
    {
        if (opening.Placement?.HostWallId is { Length: > 0 } hostWallId)
        {
            yield return hostWallId;
        }

        foreach (var wallId in opening.HostWallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }

        if (opening.Placement is not null)
        {
            foreach (var wallId in opening.Placement.AnchorWallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    yield return wallId;
                }
            }
        }
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
    int PlacementReadyWallCount,
    int PlacementOmittedWallCount,
    int WallTopologySpanCount,
    int SourceBackedFallbackWallCount,
    int SourceBackedFallbackTopologySpanCount,
    int WallSolidSpanCount,
    IReadOnlyDictionary<string, int> WallPlacementOmissionCounts,
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
        var placementReadyWallCount = CountPlacementReadyWalls(walls);
        var placementOmittedWallCount = walls.Count(wall => wall.PlacementOmission is not null);
        var wallTopologySpanCount = walls.Sum(wall => wall.TopologySpans.Count);
        var sourceBackedFallbackWallCount = CountSourceBackedFallbackWalls(walls);
        var sourceBackedFallbackTopologySpanCount = CountSourceBackedFallbackTopologySpans(walls);
        var wallSolidSpanCount = walls.Sum(wall => wall.SolidSpans.Count);
        var wallPlacementOmissionCounts = BuildWallPlacementOmissionCounts(walls);
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
            structuralWalls.Count(item => item.Reliability.RequiresReview)
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
            placementReadyWallCount,
            placementOmittedWallCount,
            wallTopologySpanCount,
            sourceBackedFallbackWallCount,
            sourceBackedFallbackTopologySpanCount,
            wallSolidSpanCount,
            wallPlacementOmissionCounts,
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
                issues,
                importCoordinateReadyEntityCount,
                importMetricReadyEntityCount,
                importReliabilityTrackedEntityCount),
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
            BuildEvidence(result, reliabilityTrackedEntityCount, coordinateReadyEntityCount, metricReadyEntityCount, reviewRequiredEntityCount, placementReadyWallCount, placementOmittedWallCount, sourceBackedFallbackTopologySpanCount, issues));
    }

    private static int CountRoutingItems(PlacementRoutingLayerExport routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count
        + routingLayer.SuppressedObjects.Count;

    private static double Ratio(int value, int total) =>
        total == 0 ? 1.0 : Math.Round(value / (double)total, 6);

    internal static int CountPlacementReadyWalls(IEnumerable<PlacementWallExport> walls) =>
        walls.Count(wall => wall.PlacementOmission is null && wall.Reliability.ReadyForCoordinatePlacement);

    internal static int CountSourceBackedFallbackWalls(IEnumerable<PlacementWallExport> walls) =>
        walls.Count(wall => wall.TopologySpans.Any(IsSourceBackedFallbackTopologySpan));

    internal static int CountSourceBackedFallbackTopologySpans(IEnumerable<PlacementWallExport> walls) =>
        walls.Sum(wall => wall.TopologySpans.Count(IsSourceBackedFallbackTopologySpan));

    internal static bool IsSourceBackedFallbackTopologySpan(PlacementWallTopologySpanExport span) =>
        span.Id.Contains(":source-backed-fallback:", StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, int> BuildWallPlacementOmissionCounts(
        IEnumerable<PlacementWallExport> walls) =>
        walls
            .Where(wall => wall.PlacementOmission is not null)
            .GroupBy(wall => wall.PlacementOmission!.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

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
        int placementReadyWallCount,
        int placementOmittedWallCount,
        int sourceBackedFallbackTopologySpanCount,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var evidence = new List<string>
        {
            $"placement summary covers {result.Document.Pages.Count} page(s)",
            $"coordinate-ready entities {coordinateReadyEntityCount}/{reliabilityTrackedEntityCount}",
            $"metric-ready entities {metricReadyEntityCount}/{reliabilityTrackedEntityCount}",
            $"placement-ready walls {placementReadyWallCount}/{result.Walls.Count}",
            $"placement-omitted walls {placementOmittedWallCount}/{result.Walls.Count}"
        };

        if (sourceBackedFallbackTopologySpanCount > 0)
        {
            evidence.Add($"source-backed fallback topology spans {sourceBackedFallbackTopologySpanCount}");
        }

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
        IReadOnlyList<PlacementIssueExport> issues,
        int? coordinateReadyEntityCount = null,
        int? metricReadyEntityCount = null,
        int? reliabilityTrackedEntityCount = null)
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
            issues
                .Where(ShouldIncludePlacementIssueForImportReadiness)
                .Select(ToReadinessIssue)
                .ToArray(),
            coordinateReadyEntityCount,
            metricReadyEntityCount,
            reliabilityTrackedEntityCount);

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

    private static PlanImportReadinessIssue ToReadinessIssue(PlacementIssueExport issue)
    {
        var severity = ShouldKeepPlacementIssueInformationalForReadiness(issue.Code)
            ? DiagnosticSeverity.Info
            : Enum.TryParse<DiagnosticSeverity>(issue.Severity, out var parsedSeverity)
                ? parsedSeverity
                : DiagnosticSeverity.Info;

        return new PlanImportReadinessIssue(ToImportReadinessIssueCode(issue.Code), severity);
    }

    private static string ToImportReadinessIssueCode(string code) =>
        string.Equals(code, "placement.review.wall_graph_endpoint_gap", StringComparison.Ordinal)
            ? "placement.wall_graph.endpoint_gaps.require_review"
            : string.Equals(code, "placement.review.surface_pattern_wall_overlap", StringComparison.Ordinal)
                ? "placement.wall_graph.surface_pattern_wall_overlaps.require_review"
            : string.Equals(code, "placement.review.wall_evidence_requires_review", StringComparison.Ordinal)
                ? "placement.wall_evidence.requires_review"
            : code;

    private static bool ShouldKeepPlacementIssueInformationalForReadiness(string code) =>
        string.Equals(code, "placement.review.dense_minor_routing_detail", StringComparison.Ordinal)
        || string.Equals(code, "placement.review.rejected_strong_wall_body", StringComparison.Ordinal);

    private static bool ShouldIncludePlacementIssueForImportReadiness(PlacementIssueExport issue) =>
        !string.Equals(issue.Code, "placement.review.dense_minor_routing_detail", StringComparison.Ordinal)
        && !string.Equals(issue.Code, "placement.review.rejected_strong_wall_body", StringComparison.Ordinal);
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
    int PlacementReadyWallCount,
    int PlacementOmittedWallCount,
    int WallTopologySpanCount,
    int SourceBackedFallbackWallCount,
    int SourceBackedFallbackTopologySpanCount,
    int WallSolidSpanCount,
    IReadOnlyDictionary<string, int> WallPlacementOmissionCounts,
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
        var pageStructuralWalls = pageWalls.Where(wall => !wall.ExcludedFromStructuralTopology).ToArray();
        var placementReadyWallCount = PlacementSummaryExport.CountPlacementReadyWalls(pageWalls);
        var placementOmittedWallCount = pageWalls.Count(wall => wall.PlacementOmission is not null);
        var wallTopologySpanCount = pageWalls.Sum(wall => wall.TopologySpans.Count);
        var sourceBackedFallbackWallCount = PlacementSummaryExport.CountSourceBackedFallbackWalls(pageWalls);
        var sourceBackedFallbackTopologySpanCount = PlacementSummaryExport.CountSourceBackedFallbackTopologySpans(pageWalls);
        var wallSolidSpanCount = pageWalls.Sum(wall => wall.SolidSpans.Count);
        var wallPlacementOmissionCounts = PlacementSummaryExport.BuildWallPlacementOmissionCounts(pageWalls);
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
            placementReadyWallCount,
            placementOmittedWallCount,
            wallTopologySpanCount,
            sourceBackedFallbackWallCount,
            sourceBackedFallbackTopologySpanCount,
            wallSolidSpanCount,
            wallPlacementOmissionCounts,
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
            pageStructuralWalls.Count(item => item.Reliability.RequiresReview)
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

public sealed record PlacementWallOmissionExport(
    string Code,
    string Category,
    string Message,
    string RecommendedAction,
    IReadOnlyList<string> LinkedWallIds,
    IReadOnlyList<string> RepairCandidateIds,
    IReadOnlyList<string> Evidence)
{
    private const double MinRepresentedByCleanTopologyOverlapRatio = 0.92;
    private const double MaxInteriorRepresentedByCleanTopologyAxisDistance = 6.0;
    private const double MaxExteriorRepresentedByCleanTopologyAxisDistance = 18.0;
    private const double MinDoorAdjacentCleanTopologyPieceLength = 20.0;

    public static PlacementWallOmissionExport? From(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan>? allCleanTopologySpans,
        IReadOnlyList<PlacementWallOpeningCutoutExport> openingCutouts,
        bool excludedFromStructuralTopology,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates,
        IReadOnlyList<string> reviewReasons)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(reliability);

        if (reliability.ReadyForCoordinatePlacement
            && topologySpans.Count > 0
            && !excludedFromStructuralTopology)
        {
            return null;
        }

        var repairCandidateIds = repairCandidates
            .Select(candidate => candidate.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var representedByCleanSpan = FindRepresentingCleanTopologySpan(
            wall,
            component,
            evidenceAssessment,
            reliability,
            topologySpans,
            allCleanTopologySpans ?? topologySpans,
            excludedFromStructuralTopology);
        var representedEvidence = representedByCleanSpan is null
            ? Array.Empty<string>()
            :
            [
                $"wall already represented by clean topology span from wall {representedByCleanSpan.WallId}; "
                + $"overlap {RepresentedByCleanTopologyOverlapRatio(wall, representedByCleanSpan):0.###}; "
                + $"axis distance {RepresentedByCleanTopologyAxisDistance(wall, representedByCleanSpan):0.###} drawing units"
            ];
        var suppressedOpeningTopologyEvidence = BuildSuppressedOpeningTopologyEvidence(
            wall,
            topologySpans,
            openingCutouts);
        var combinedEvidence = BuildEvidence(
            wall,
            component,
            evidenceAssessment,
            reliability,
            repairCandidates,
            reviewReasons,
            representedEvidence.Concat(suppressedOpeningTopologyEvidence).ToArray());
        var linkedWallIds = ExtractLinkedWallIds(wall.Id, combinedEvidence, repairCandidates);
        var classification = Classify(
            evidenceAssessment,
            component,
            wall,
            reliability,
            topologySpans,
            excludedFromStructuralTopology,
            repairCandidates,
            combinedEvidence);

        return new PlacementWallOmissionExport(
            classification.Code,
            classification.Category,
            classification.Message,
            classification.RecommendedAction,
            linkedWallIds,
            repairCandidateIds,
            combinedEvidence);
    }

    private static PlacementWallOmissionClassification Classify(
        WallEvidenceWallAssessment? evidenceAssessment,
        WallGraphComponent? component,
        WallSegment wall,
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        bool excludedFromStructuralTopology,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates,
        IReadOnlyList<string> evidence)
    {
        if (repairCandidates.Any(candidate => candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked))
        {
            return new PlacementWallOmissionClassification(
                "topology_import_blocked",
                "TopologyRepairBlocked",
                "Wall is omitted from clean placement topology because one or more wall graph repair candidates block import.",
                "Resolve or manually review the wall graph repair candidate before importing this wall as clean topology.");
        }

        if (ContainsEvidence(evidence, "duplicate wall-face")
            || ContainsEvidence(evidence, "already represented by stronger paired wall body"))
        {
            return new PlacementWallOmissionClassification(
                "duplicate_wall_face",
                "DuplicateWallFace",
                "Wall is omitted from clean placement topology because it appears to be a duplicate face of a stronger paired wall body.",
                "Use the linked stronger wall body for placement and keep this wall as review evidence only.");
        }

        if (ContainsEvidence(evidence, "already represented by clean topology span"))
        {
            return new PlacementWallOmissionClassification(
                "duplicate_clean_topology_span",
                "DuplicateCleanTopology",
                "Wall is omitted from clean placement topology because another clean wall span already represents the same run.",
                "Use the linked clean wall span for placement and keep this wall only as source/evidence context.");
        }

        if (evidenceAssessment?.RejectedAsNoise == true
            || evidenceAssessment?.Decision == WallEvidenceDecision.Reject)
        {
            return new PlacementWallOmissionClassification(
                "rejected_wall_evidence",
                "RejectedWallEvidence",
                "Wall is omitted from clean placement topology because wall evidence rejected it as noise or non-wall geometry.",
                "Do not import this wall unless a reviewer explicitly corrects the classification.");
        }

        if (component?.Kind == WallGraphComponentKind.ObjectLikeIsland)
        {
            return new PlacementWallOmissionClassification(
                "object_like_linework",
                "NonStructuralComponent",
                "Wall-like linework is omitted from clean placement topology because the graph component is object-like detail.",
                "Treat this as object/detail evidence, not a wall, unless a reviewer promotes it.");
        }

        if (component?.Kind == WallGraphComponentKind.IsolatedFragment)
        {
            return new PlacementWallOmissionClassification(
                "isolated_fragment",
                "NonStructuralComponent",
                "Wall-like linework is omitted from clean placement topology because it is an isolated fragment.",
                "Keep it for opening/object review, but do not import it as a structural wall without correction.");
        }

        if (ContainsEvidence(
            evidence,
            WallPlacementContextGuards.SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason))
        {
            return new PlacementWallOmissionClassification(
                "secondary_object_linework_without_room_boundary_support",
                "SecondaryObjectLineworkReview",
                "Wall is omitted from clean placement topology because it overlaps detected stair/object linework and is not used by any detected room boundary.",
                "Review the wall against the source PDF before importing it; it may be stair, fixture, or symbol linework rather than a true wall.");
        }

        if (ContainsEvidence(
            evidence,
            WallPlacementContextGuards.SecondaryStructuralOverSourcedDetailLineworkReason))
        {
            return new PlacementWallOmissionClassification(
                "secondary_over_sourced_detail_linework",
                "SecondaryObjectLineworkReview",
                "Wall is omitted from clean placement topology because a compact secondary wall component has excessive source/detail linework contamination despite room-boundary evidence.",
                "Review the wall against the source PDF before importing it; it may be stair, fixture, symbol, or detail linework that only looks like a wall.");
        }

        if (ContainsEvidence(
            evidence,
            WallPlacementContextGuards.FragmentMergedInteriorWithoutRoomBoundarySupportReason))
        {
            return new PlacementWallOmissionClassification(
                "fragmented_interior_without_room_boundary_support",
                "FragmentedInteriorReview",
                "Wall is omitted from clean placement topology because it is a suspicious fragment-merged interior line that is not used by any detected room boundary.",
                "Review the source PDF before importing it; this may be stitched door, fixture, stair, or furniture linework rather than a real partition.");
        }

        if (ContainsEvidence(
            evidence,
            WallPlacementContextGuards.SecondaryStructuralWithoutRoomBoundarySupportReason))
        {
            return new PlacementWallOmissionClassification(
                "secondary_without_room_boundary_support",
                "SecondaryStructuralReview",
                "Wall is omitted from clean placement topology because its secondary structural component is not used by any detected room boundary.",
                "Review the wall against the source PDF and promote it only if it is a real room or exterior boundary.");
        }

        if (excludedFromStructuralTopology)
        {
            return new PlacementWallOmissionClassification(
                "structural_topology_excluded",
                "NonStructuralComponent",
                "Wall is omitted from clean placement topology because the structural topology filter excluded it.",
                "Review the component evidence before using this wall for exact placement.");
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true
            || ContainsEvidence(evidence, "fragment geometry requires review"))
        {
            return new PlacementWallOmissionClassification(
                "fragment_geometry_review",
                "FragmentGeometryReview",
                "Wall is omitted from clean placement topology because healed or fragmented geometry needs review.",
                "Confirm the wall endpoints and thickness before importing exact coordinates.");
        }

        if (ContainsEvidence(evidence, "demoted from placement-ready")
            && ContainsEvidence(evidence, "severe fragmented-face evidence"))
        {
            return new PlacementWallOmissionClassification(
                "fragmented_pair_review_required",
                "FragmentedPairReview",
                "Wall is omitted from clean placement topology because a short paired-wall candidate was demoted after severe face fragmentation was detected.",
                "Review the source PDF linework before importing this wall; it may be stitched door, opening, fixture, or detail geometry rather than a wall.");
        }

        if (ContainsEvidence(evidence, WallPlacementReadinessEvaluator.WeakPromotedFragmentRoomBoundaryReason))
        {
            return new PlacementWallOmissionClassification(
                "weak_promoted_fragment_room_boundary_review_required",
                "WeakFragmentRoomBoundaryReview",
                "Wall is omitted from clean placement topology because a fragment-merged room boundary was promoted with no supported topology endpoint.",
                "Review the source PDF and require explicit/geometric room-boundary or endpoint support before importing this stitched fragment as exact wall geometry.");
        }

        if (ContainsEvidence(evidence, "unlayered fragment-merged wall candidate")
            && ContainsEvidence(evidence, "only one trusted structural endpoint"))
        {
            return new PlacementWallOmissionClassification(
                "one_endpoint_fragment_review_required",
                "FragmentEndpointReview",
                "Wall is omitted from clean placement topology because an unlayered fragment-merged candidate has only one trusted structural endpoint.",
                "Review the opposite endpoint against the source PDF before importing this wall; it may be a true wall return, furniture/detail linework, or a stitched partial wall.");
        }

        if (ContainsEvidence(evidence, "very short unlayered parallel-face candidate"))
        {
            return new PlacementWallOmissionClassification(
                "very_short_parallel_pair_review_required",
                "VeryShortParallelPairReview",
                "Wall is omitted from clean placement topology because a very short unlayered parallel-face candidate needs review before exact placement.",
                "Review this candidate as a possible short wall return, door/window frame, fixture edge, or stitched detail before importing it as wall geometry.");
        }

        var hasTopologySupportedFragmentedPairPromotion = ContainsEvidence(
            evidence,
            WallPlacementReadinessEvaluator.TopologySupportedFragmentedPairPromotionEvidence);
        if (!hasTopologySupportedFragmentedPairPromotion
            && ContainsEvidence(evidence, "unlayered parallel-face candidate")
            && (ContainsEvidence(evidence, "noisy fragmented face evidence")
                || ContainsEvidence(evidence, "weak/fragmented pair evidence")))
        {
            return new PlacementWallOmissionClassification(
                "fragmented_short_parallel_pair_review_required",
                "FragmentedShortParallelPairReview",
                "Wall is omitted from clean placement topology because a short unlayered parallel-face candidate has fragmented or weak paired-face evidence.",
                "Review the face fragments against the source PDF before importing this wall; it may be a real short return or stitched non-wall detail.");
        }

        if (!hasTopologySupportedFragmentedPairPromotion
            && ContainsEvidence(evidence, "unlayered parallel-face candidate"))
        {
            return new PlacementWallOmissionClassification(
                "short_parallel_pair_review_required",
                "ShortParallelPairReview",
                "Wall is omitted from clean placement topology because a short unlayered parallel-face candidate needs review before exact placement.",
                "Review the paired faces against the source PDF before importing this wall; it may be a real short return, door/window frame, fixture edge, or stitched detail linework.");
        }

        if (ContainsEvidence(evidence, "repeated short unlayered")
            && ContainsEvidence(evidence, "review as detail/object linework"))
        {
            return new PlacementWallOmissionClassification(
                "repeated_short_detail_review_required",
                "RepeatedDetailReview",
                "Wall is omitted from clean placement topology because repeated short unlayered linework looks more like detail/object geometry than a reliable wall.",
                "Keep the candidate for review evidence, but do not import it as a structural wall unless a reviewer promotes it.");
        }

        if (evidenceAssessment is not null
            && (!evidenceAssessment.PlacementReady
                || evidenceAssessment.RequiresReview
                || evidenceAssessment.Decision == WallEvidenceDecision.Review))
        {
            return new PlacementWallOmissionClassification(
                "wall_evidence_review_required",
                "WallEvidenceReview",
                "Wall is omitted from clean placement topology because wall evidence requires review before exact placement.",
                "Review the source linework and evidence before using this wall for exact placement.");
        }

        if (ContainsEvidence(evidence, "tiny door-adjacent placement topology piece"))
        {
            return new PlacementWallOmissionClassification(
                "tiny_door_adjacent_topology_suppressed",
                "OpeningSplitReview",
                "Wall is omitted from clean placement topology because only tiny door-adjacent wall leftovers remained after opening cutouts were applied.",
                "Keep the raw wall and opening cutout for QA, but do not import the tiny door-adjacent remainder as a structural wall unless reviewed.");
        }

        if (!hasTopologySupportedFragmentedPairPromotion
            && ContainsEvidence(evidence, "short high-density unknown-layer wall/detail candidate requires review"))
        {
            return new PlacementWallOmissionClassification(
                "short_dense_detail_review_required",
                "ShortDetailReview",
                "Wall is omitted from clean placement topology because short dense unknown-layer linework may be door, window, fixture, or detail geometry.",
                "Review the source PDF before importing this short wall; promote it only if it is a true wall return or partition.");
        }

        if (topologySpans.Count == 0)
        {
            return new PlacementWallOmissionClassification(
                "no_clean_topology_spans",
                "TopologyUnavailable",
                "Wall is omitted from clean placement topology because no clean wall graph span was available.",
                "Use the raw wall only for QA, or repair the wall graph before importing exact placement.");
        }

        if (reliability.RequiresReview)
        {
            return new PlacementWallOmissionClassification(
                "coordinate_review_required",
                "CoordinateReview",
                "Wall is omitted from clean placement topology because coordinate placement requires review.",
                "Resolve the review reasons before importing this wall as placement-ready geometry.");
        }

        return new PlacementWallOmissionClassification(
            "not_placement_ready",
            "PlacementNotReady",
            "Wall is omitted from clean placement topology because it is not placement-ready.",
            "Review wall evidence and graph topology before importing exact placement.");
    }

    private static IReadOnlyList<string> BuildEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates,
        IReadOnlyList<string> reviewReasons,
        IReadOnlyList<string>? extraEvidence = null)
    {
        var evidence = (extraEvidence ?? Array.Empty<string>())
            .Concat(reliability.Reasons)
            .Concat(reviewReasons)
            .Concat(evidenceAssessment?.Evidence ?? Array.Empty<string>())
            .Concat(wall.Evidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .Concat(repairCandidates.SelectMany(candidate => candidate.Evidence))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return evidence
            .Where(IsHighPriorityPlacementOmissionEvidence)
            .Concat(evidence.Where(item => !IsHighPriorityPlacementOmissionEvidence(item)))
            .Take(12)
            .ToArray();
    }

    private static bool IsHighPriorityPlacementOmissionEvidence(string evidence) =>
        evidence.Contains("demoted from placement-ready", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("severe fragmented-face evidence", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("already represented by clean topology span", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("unlayered fragment-merged wall candidate", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("unlayered parallel-face candidate", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("repeated short unlayered", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("short high-density unknown-layer wall/detail candidate", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("tiny door-adjacent placement topology piece", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> BuildSuppressedOpeningTopologyEvidence(
        WallSegment wall,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<PlacementWallOpeningCutoutExport> openingCutouts)
    {
        if (topologySpans.Count > 0
            || openingCutouts.Count == 0
            || wall.CenterLine.Length <= 0.001)
        {
            return Array.Empty<string>();
        }

        var cutouts = MergeOpeningCutoutsForSuppressionEvidence(openingCutouts);
        if (cutouts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var evidence = new List<string>();
        var cursor = 0.0;
        PlacementWallOpeningCutoutExport? previous = null;
        foreach (var cutout in cutouts)
        {
            AddSuppressedOpeningTopologyGapEvidence(
                evidence,
                wall,
                cursor,
                Math.Clamp(cutout.StartParameter, 0, 1),
                previous,
                cutout);
            cursor = Math.Max(cursor, Math.Clamp(cutout.EndParameter, 0, 1));
            previous = cutout;
        }

        AddSuppressedOpeningTopologyGapEvidence(
            evidence,
            wall,
            cursor,
            1.0,
            previous,
            nextCutout: null);

        return evidence
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddSuppressedOpeningTopologyGapEvidence(
        List<string> evidence,
        WallSegment wall,
        double startParameter,
        double endParameter,
        PlacementWallOpeningCutoutExport? previousCutout,
        PlacementWallOpeningCutoutExport? nextCutout)
    {
        var length = Math.Max(0, endParameter - startParameter) * wall.CenterLine.Length;
        if (length <= 0.001
            || length >= MinDoorAdjacentCleanTopologyPieceLength
            || (!IsDoorLikeCutout(previousCutout) && !IsDoorLikeCutout(nextCutout)))
        {
            return;
        }

        var previous = previousCutout?.OpeningId ?? "-";
        var next = nextCutout?.OpeningId ?? "-";
        evidence.Add(
            $"tiny door-adjacent placement topology piece suppressed; length {length:0.###} drawing units, "
            + $"threshold {MinDoorAdjacentCleanTopologyPieceLength:0.###}, "
            + $"wall parameters {startParameter:0.###}-{endParameter:0.###}, "
            + $"previous opening {previous}, next opening {next}");
    }

    private static IReadOnlyList<PlacementWallOpeningCutoutExport> MergeOpeningCutoutsForSuppressionEvidence(
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        var ordered = cutouts
            .Where(cutout => cutout.EndParameter > cutout.StartParameter)
            .OrderBy(cutout => cutout.StartParameter)
            .ThenBy(cutout => cutout.EndParameter)
            .ToArray();
        if (ordered.Length <= 1)
        {
            return ordered;
        }

        var merged = new List<PlacementWallOpeningCutoutExport>();
        var current = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var next = ordered[index];
            if (next.StartParameter > current.EndParameter + 0.001)
            {
                merged.Add(current);
                current = next;
                continue;
            }

            current = current with
            {
                EndParameter = Math.Max(current.EndParameter, next.EndParameter),
                OpeningId = IsDoorLikeCutout(current) ? current.OpeningId : next.OpeningId,
                Type = IsDoorLikeCutout(current) ? current.Type : next.Type,
                Operation = IsDoorLikeCutout(current) ? current.Operation : next.Operation,
                Evidence = current.Evidence
                    .Concat(next.Evidence)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        merged.Add(current);
        return merged;
    }

    private static bool IsDoorLikeCutout(PlacementWallOpeningCutoutExport? cutout)
    {
        if (cutout is null)
        {
            return false;
        }

        return string.Equals(cutout.Type, OpeningType.Door.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Type, OpeningType.GenericOpening.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.PassThrough.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.Hinged.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.DoubleSwing.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.Sliding.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.PocketSliding.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static WallGraphTopologySpan? FindRepresentingCleanTopologySpan(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan> allCleanTopologySpans,
        bool excludedFromStructuralTopology)
    {
        if (topologySpans.Count > 0
            || allCleanTopologySpans.Count == 0
            || excludedFromStructuralTopology
            || component?.Kind == WallGraphComponentKind.ObjectLikeIsland
            || evidenceAssessment?.Category == WallEvidenceCategory.ObjectOrFixtureDetail
            || evidenceAssessment?.RejectedAsNoise == true
            || evidenceAssessment?.Decision == WallEvidenceDecision.Reject
            || wall.CenterLine.Length <= 0.001
            || ResolveRepresentedTopologyOrientation(wall.CenterLine) == RepresentedTopologyOrientation.Unknown)
        {
            return null;
        }

        return allCleanTopologySpans
            .Where(span => span.PageNumber == wall.PageNumber)
            .Where(span => !string.Equals(span.WallId, wall.Id, StringComparison.Ordinal))
            .Where(span => ResolveRepresentedTopologyOrientation(span.CenterLine)
                == ResolveRepresentedTopologyOrientation(wall.CenterLine))
            .Select(span => new
            {
                Span = span,
                Overlap = RepresentedByCleanTopologyOverlapRatio(wall, span),
                AxisDistance = RepresentedByCleanTopologyAxisDistance(wall, span)
            })
            .Where(item => item.Overlap >= MinRepresentedByCleanTopologyOverlapRatio)
            .Where(item => item.AxisDistance <= RepresentedByCleanTopologyAxisDistanceTolerance(wall, item.Span))
            .OrderByDescending(item => item.Overlap)
            .ThenBy(item => item.AxisDistance)
            .ThenByDescending(item => item.Span.DrawingLength)
            .Select(item => item.Span)
            .FirstOrDefault();
    }

    private static double RepresentedByCleanTopologyOverlapRatio(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var orientation = ResolveRepresentedTopologyOrientation(wall.CenterLine);
        if (orientation == RepresentedTopologyOrientation.Unknown
            || orientation != ResolveRepresentedTopologyOrientation(span.CenterLine)
            || wall.CenterLine.Length <= 0.001)
        {
            return 0;
        }

        var overlap = Math.Min(RepresentedAxisMax(wall.CenterLine, orientation), RepresentedAxisMax(span.CenterLine, orientation))
            - Math.Max(RepresentedAxisMin(wall.CenterLine, orientation), RepresentedAxisMin(span.CenterLine, orientation));
        return Math.Clamp(overlap / wall.CenterLine.Length, 0, 1);
    }

    private static double RepresentedByCleanTopologyAxisDistance(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var orientation = ResolveRepresentedTopologyOrientation(wall.CenterLine);
        if (orientation == RepresentedTopologyOrientation.Unknown
            || orientation != ResolveRepresentedTopologyOrientation(span.CenterLine))
        {
            return double.PositiveInfinity;
        }

        return Math.Abs(RepresentedAxisCoordinate(wall.CenterLine, orientation) - RepresentedAxisCoordinate(span.CenterLine, orientation));
    }

    private static double RepresentedByCleanTopologyAxisDistanceTolerance(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var baseTolerance = wall.WallType == WallType.Exterior || span.SourceWall?.WallType == WallType.Exterior
            ? MaxExteriorRepresentedByCleanTopologyAxisDistance
            : MaxInteriorRepresentedByCleanTopologyAxisDistance;
        return Math.Max(baseTolerance, Math.Max(wall.Thickness, span.Thickness) + 1.0);
    }

    private static RepresentedTopologyOrientation ResolveRepresentedTopologyOrientation(PlanLineSegment line)
    {
        if (line.IsHorizontal(2))
        {
            return RepresentedTopologyOrientation.Horizontal;
        }

        return line.IsVertical(2)
            ? RepresentedTopologyOrientation.Vertical
            : RepresentedTopologyOrientation.Unknown;
    }

    private static double RepresentedAxisMin(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? Math.Min(line.Start.X, line.End.X)
            : Math.Min(line.Start.Y, line.End.Y);

    private static double RepresentedAxisMax(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? Math.Max(line.Start.X, line.End.X)
            : Math.Max(line.Start.Y, line.End.Y);

    private static double RepresentedAxisCoordinate(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? (line.Start.Y + line.End.Y) / 2.0
            : (line.Start.X + line.End.X) / 2.0;

    private static IReadOnlyList<string> ExtractLinkedWallIds(
        string wallId,
        IReadOnlyList<string> evidence,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        var linkedWallIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in repairCandidates)
        {
            foreach (var candidateWallId in candidate.WallIds)
            {
                AddWallId(candidateWallId, allowAnyRepairWallId: true);
            }
        }

        foreach (var text in evidence)
        {
            foreach (var token in text.Split(
                         new[] { ' ', '\t', '\r', '\n', ',', ';', '(', ')', '[', ']', '{', '}', '"', '\'' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                AddWallId(token.Trim('.', ',', ';', ':'), allowAnyRepairWallId: false);
            }
        }

        return linkedWallIds.ToArray();

        void AddWallId(string? candidateWallId, bool allowAnyRepairWallId)
        {
            if (string.IsNullOrWhiteSpace(candidateWallId))
            {
                return;
            }

            var trimmed = candidateWallId.Trim();
            if (string.Equals(trimmed, wallId, StringComparison.Ordinal))
            {
                return;
            }

            if (trimmed.Contains("wall-graph-repair", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "wall-face", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "wall-body", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "wall-evidence", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "wall-graph", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var looksLikeFreeTextWallId = LooksLikeWallIdentifier(trimmed);
            if (allowAnyRepairWallId || looksLikeFreeTextWallId)
            {
                linkedWallIds.Add(trimmed);
            }
        }
    }

    private static bool LooksLikeWallIdentifier(string value)
    {
        if (value.Contains(":wall:", StringComparison.OrdinalIgnoreCase)
            || value.Contains(":wall-evidence-recovered:", StringComparison.OrdinalIgnoreCase)
            || value.Contains(":wall-evidence-recovered-short:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (value.StartsWith("wall-", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("wall_", StringComparison.OrdinalIgnoreCase))
            && value.Any(char.IsDigit);
    }

    private static bool ContainsEvidence(IReadOnlyList<string> evidence, string text) =>
        evidence.Any(item => item.Contains(text, StringComparison.OrdinalIgnoreCase));

    private sealed record PlacementWallOmissionClassification(
        string Code,
        string Category,
        string Message,
        string RecommendedAction);

    private enum RepresentedTopologyOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }
}

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
    IReadOnlyList<PlacementWallTopologySpanExport> TopologySpans,
    IReadOnlyList<PlacementWallOpeningCutoutExport> OpeningCutouts,
    IReadOnlyList<PlacementWallSolidSpanExport> SolidSpans,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double DrawingLength,
    double? LengthMeters,
    double ThicknessDrawingUnits,
    double? ThicknessMillimeters,
    string DetectionKind,
    string WallType,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    string? MeasurementScaleGroupId,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    WallFragmentEvidenceExport? FragmentEvidence,
    WallEvidenceAssessmentExport? EvidenceAssessment,
    PlacementReliabilityExport Reliability,
    PlacementWallOmissionExport? PlacementOmission,
    IReadOnlyList<string> WallGraphRepairCandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallExport From(
        WallSegment wall,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        WallEvidenceWallAssessment? evidenceAssessment,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan> allCleanTopologySpans,
        IReadOnlyList<OpeningCandidate> openings,
        IReadOnlyList<string> reviewReasons,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        var scale = ResolveMillimetersPerDrawingUnit(calibration, wall.MeasurementScaleGroupId);
        var wallGraphRepairCandidateIds = repairCandidates
            .Select(candidate => candidate.Id)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var combinedReviewReasons = reviewReasons
            .Concat(repairCandidates.Where(candidate => candidate.RequiresReview).Select(WallGraphRepairReviewReason))
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var cutouts = openings
            .Select((opening, index) => PlacementWallOpeningCutoutExport.From(wall, opening, scale, index + 1))
            .Where(cutout => cutout is not null)
            .Select(cutout => cutout!)
            .DistinctBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
            .OrderBy(cutout => cutout.StartParameter)
            .ThenBy(cutout => cutout.EndParameter)
            .ThenBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
            .ToArray();
        var excludedFromStructuralTopology =
            WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment);
        var reliability = PlacementReliability.ForWall(wall, calibration, component, evidenceAssessment, combinedReviewReasons);
        var placementOmission = PlacementWallOmissionExport.From(
            wall,
            component,
            evidenceAssessment,
            reliability,
            topologySpans,
            allCleanTopologySpans,
            cutouts,
            excludedFromStructuralTopology,
            repairCandidates,
            combinedReviewReasons);
        var exportReliability = ApplyPlacementOmissionToReliability(reliability, placementOmission);
        var solidSpans = PlacementWallSolidSpanExport.From(
            wall,
            scale,
            cutouts,
            openings,
            exportReliability,
            placementOmission);

        return new PlacementWallExport(
            wall.Id,
            wall.PageNumber,
            LineExport.From(wall.CenterLine),
            ScaleLine(wall.CenterLine, scale),
            topologySpans
                .Select(span => PlacementWallTopologySpanExport.From(span, scale, sourceLookup))
                .ToArray(),
            cutouts,
            solidSpans,
            RectExport.From(wall.Bounds),
            ScaleRect(wall.Bounds, scale),
            wall.DrawingLength,
            wall.LengthMeters,
            wall.Thickness,
            wall.ThicknessMillimeters,
            wall.DetectionKind.ToString(),
            wall.WallType.ToString(),
            component?.Id,
            component?.Kind.ToString(),
            excludedFromStructuralTopology,
            wall.MeasurementScaleGroupId,
            scale,
            wall.Confidence.Value,
            wall.FragmentEvidence is null ? null : WallFragmentEvidenceExport.From(wall.FragmentEvidence),
            evidenceAssessment is null ? null : WallEvidenceAssessmentExport.From(evidenceAssessment),
            exportReliability,
            placementOmission,
            wallGraphRepairCandidateIds,
            wall.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(wall.SourcePrimitiveIds, sourceLookup),
            wall.Evidence);
    }

    private static PlacementReliabilityExport ApplyPlacementOmissionToReliability(
        PlacementReliabilityExport reliability,
        PlacementWallOmissionExport? placementOmission)
    {
        if (placementOmission is null)
        {
            return reliability;
        }

        var omissionReason = $"wall omitted from clean placement topology ({placementOmission.Code})";
        return reliability with
        {
            ReadyForCoordinatePlacement = false,
            ReadyForMetricPlacement = false,
            RequiresReview = true,
            Reasons = reliability.Reasons
                .Append(omissionReason)
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static string WallGraphRepairReviewReason(WallGraphRepairCandidate candidate)
    {
        var action = candidate.SuggestedAction switch
        {
            WallGraphRepairAction.TrimEndpointOverrun => "endpoint-overrun trim",
            WallGraphRepairAction.SnapEndpointToWall => "endpoint-to-wall snap",
            WallGraphRepairAction.SnapEndpointToEndpoint => "endpoint-to-endpoint snap",
            _ => candidate.SuggestedAction.ToString()
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"wall graph repair candidate {candidate.Id} requires review for {action} ({candidate.Kind}, {candidate.ImportImpact}, {candidate.GapDistance:0.###} drawing units)");
    }
}

public sealed record PlacementWallOpeningCutoutExport(
    string Id,
    string OpeningId,
    int PageNumber,
    string Type,
    string Operation,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    PointExport StartPoint,
    PointExport? StartPointMillimeters,
    PointExport EndPoint,
    PointExport? EndPointMillimeters,
    double StartParameter,
    double EndParameter,
    double CenterParameter,
    double StartOffsetDrawingUnits,
    double EndOffsetDrawingUnits,
    double CenterOffsetDrawingUnits,
    double LengthDrawingUnits,
    double? StartOffsetMillimeters,
    double? EndOffsetMillimeters,
    double? CenterOffsetMillimeters,
    double? LengthMillimeters,
    string? SourceHostWallId,
    IReadOnlyList<string> AnchorWallIds,
    double CrossWallOffsetDrawingUnits,
    double? CrossWallOffsetMillimeters,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallOpeningCutoutExport? From(
        WallSegment wall,
        OpeningCandidate opening,
        double? millimetersPerDrawingUnit,
        int sequence)
    {
        if (opening.Placement is null
            || !ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening)
            || !OpeningBelongsToWall(wall, opening))
        {
            return null;
        }

        var placement = opening.Placement;
        var wallLength = wall.CenterLine.Length;
        if (wallLength <= 0.001)
        {
            return null;
        }

        var (startParameter, endParameter) = OpeningParametersOnWall(wall, opening);
        var unclampedStart = Math.Min(startParameter, endParameter);
        var unclampedEnd = Math.Max(startParameter, endParameter);
        var clampedStart = Math.Clamp(unclampedStart, 0, 1);
        var clampedEnd = Math.Clamp(unclampedEnd, 0, 1);
        if (clampedEnd - clampedStart <= Math.Max(0.001, 0.5 / wallLength))
        {
            return null;
        }

        var startPoint = wall.CenterLine.PointAt(clampedStart);
        var endPoint = wall.CenterLine.PointAt(clampedEnd);
        var line = new PlanLineSegment(startPoint, endPoint);
        var startOffset = clampedStart * wallLength;
        var endOffset = clampedEnd * wallLength;
        var centerOffset = ((clampedStart + clampedEnd) / 2.0) * wallLength;
        var length = line.Length;

        return new PlacementWallOpeningCutoutExport(
            $"{wall.Id}:opening-cutout:{sequence}",
            opening.Id,
            wall.PageNumber,
            opening.Type.ToString(),
            opening.Operation.ToString(),
            LineExport.From(line),
            ScaleLine(line, millimetersPerDrawingUnit),
            PointExport.From(startPoint),
            ScalePoint(startPoint, millimetersPerDrawingUnit),
            PointExport.From(endPoint),
            ScalePoint(endPoint, millimetersPerDrawingUnit),
            clampedStart,
            clampedEnd,
            (clampedStart + clampedEnd) / 2.0,
            startOffset,
            endOffset,
            centerOffset,
            length,
            ScaleNullable(startOffset, millimetersPerDrawingUnit),
            ScaleNullable(endOffset, millimetersPerDrawingUnit),
            ScaleNullable(centerOffset, millimetersPerDrawingUnit),
            ScaleNullable(length, millimetersPerDrawingUnit),
            placement.HostWallId,
            placement.AnchorWallIds,
            placement.CrossWallOffsetDrawingUnits,
            ScaleNullable(placement.CrossWallOffsetDrawingUnits, millimetersPerDrawingUnit),
            opening.Confidence.Value,
            opening.Evidence
                .Concat(placement.Evidence)
                .Append("Opening exported as a wall cutout for downstream wall splitting.")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static bool OpeningBelongsToWall(WallSegment wall, OpeningCandidate opening)
    {
        var placement = opening.Placement;
        if (placement is null)
        {
            return false;
        }

        if (string.Equals(placement.HostWallId, wall.Id, StringComparison.Ordinal)
            || opening.HostWallIds.Contains(wall.Id, StringComparer.Ordinal)
            || placement.AnchorWallIds.Contains(wall.Id, StringComparer.Ordinal))
        {
            return true;
        }

        var maxDistance = Math.Max(wall.Thickness * 2.5, placement.DepthDrawingUnits * 2.5);
        maxDistance = Math.Max(maxDistance, 1.5);
        return wall.PageNumber == opening.PageNumber
            && wall.CenterLine.DistanceToPoint(placement.StartPoint) <= maxDistance
            && wall.CenterLine.DistanceToPoint(placement.EndPoint) <= maxDistance;
    }

    private static (double Start, double End) OpeningParametersOnWall(WallSegment wall, OpeningCandidate opening)
    {
        var placement = opening.Placement!;
        if (string.Equals(placement.HostWallId, wall.Id, StringComparison.Ordinal)
            && PlacementReferenceMatchesWall(wall, placement))
        {
            return (placement.HostWallStartParameter, placement.HostWallEndParameter);
        }

        return (
            wall.CenterLine.ProjectParameter(placement.StartPoint),
            wall.CenterLine.ProjectParameter(placement.EndPoint));
    }

    private static bool PlacementReferenceMatchesWall(WallSegment wall, OpeningPlacement placement)
    {
        var tolerance = Math.Max(1.5, wall.Thickness * 2.0);
        var sameDirection =
            wall.CenterLine.Start.DistanceTo(placement.ReferenceLine.Start) <= tolerance
            && wall.CenterLine.End.DistanceTo(placement.ReferenceLine.End) <= tolerance;
        var reversed =
            wall.CenterLine.Start.DistanceTo(placement.ReferenceLine.End) <= tolerance
            && wall.CenterLine.End.DistanceTo(placement.ReferenceLine.Start) <= tolerance;
        var lengthTolerance = Math.Max(tolerance, wall.CenterLine.Length * 0.05);

        return (sameDirection || reversed)
            && Math.Abs(wall.CenterLine.Length - placement.ReferenceLine.Length) <= lengthTolerance;
    }
}

public sealed record PlacementWallSolidSpanExport(
    string Id,
    int PageNumber,
    string WallId,
    int Sequence,
    bool ReadyForCoordinatePlacement,
    bool ReadyForMetricPlacement,
    bool RequiresReview,
    IReadOnlyList<string> ReviewReasons,
    string? PlacementOmissionCode,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    IReadOnlyList<PointExport> BodyPolygon,
    IReadOnlyList<PointExport>? BodyPolygonMillimeters,
    RectExport BodyBounds,
    RectExport? BodyBoundsMillimeters,
    VectorExport AlongVector,
    VectorExport NormalVector,
    double ThicknessDrawingUnits,
    double? ThicknessMillimeters,
    double StartParameter,
    double EndParameter,
    double CenterParameter,
    double StartOffsetDrawingUnits,
    double EndOffsetDrawingUnits,
    double CenterOffsetDrawingUnits,
    double DrawingLength,
    double? LengthMeters,
    IReadOnlyList<string> AdjacentOpeningIds,
    IReadOnlyList<string> Evidence)
{
    public static IReadOnlyList<PlacementWallSolidSpanExport> From(
        WallSegment wall,
        double? millimetersPerDrawingUnit,
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts,
        IReadOnlyList<OpeningCandidate> openings,
        PlacementReliabilityExport? reliability = null,
        PlacementWallOmissionExport? placementOmission = null)
    {
        var wallLength = wall.CenterLine.Length;
        if (wallLength <= 0.001)
        {
            return Array.Empty<PlacementWallSolidSpanExport>();
        }

        var normalizedCutouts = MergeCutouts(cutouts);
        var spans = new List<PlacementWallSolidSpanExport>();
        var cursor = 0.0;
        string? previousOpeningId = null;
        var sequence = 1;
        foreach (var cutout in normalizedCutouts)
        {
            var cutoutStart = Math.Clamp(cutout.StartParameter, 0, 1);
            var cutoutEnd = Math.Clamp(cutout.EndParameter, 0, 1);
            if (cutoutStart > cursor)
            {
                spans.Add(CreateSpan(
                wall,
                millimetersPerDrawingUnit,
                sequence++,
                cursor,
                cutoutStart,
                previousOpeningId,
                cutout.OpeningId,
                openings,
                reliability,
                placementOmission));
            }

            cursor = Math.Max(cursor, cutoutEnd);
            previousOpeningId = cutout.OpeningId;
        }

        if (cursor < 1.0)
        {
            spans.Add(CreateSpan(
                wall,
                millimetersPerDrawingUnit,
                sequence,
                cursor,
                1.0,
                previousOpeningId,
                nextOpeningId: null,
                openings,
                reliability,
                placementOmission));
        }

        return spans.ToArray();
    }

    private static PlacementWallSolidSpanExport CreateSpan(
        WallSegment wall,
        double? millimetersPerDrawingUnit,
        int sequence,
        double startParameter,
        double endParameter,
        string? previousOpeningId,
        string? nextOpeningId,
        IReadOnlyList<OpeningCandidate> openings,
        PlacementReliabilityExport? reliability,
        PlacementWallOmissionExport? placementOmission)
    {
        var wallLength = wall.CenterLine.Length;
        var startPoint = wall.CenterLine.PointAt(startParameter);
        var endPoint = wall.CenterLine.PointAt(endParameter);
        var bodyFootprint = WallBodyFootprintBuilder.Build(
            wall,
            startParameter,
            endParameter,
            $"{wall.Id}:solid-span:{sequence}:body",
            wall.Confidence,
            wall.Evidence);
        var line = bodyFootprint.CenterLine;
        var bodyPolygonMillimeters = ScalePoints(bodyFootprint.Polygon, millimetersPerDrawingUnit);
        var thicknessMillimeters = wall.ThicknessMillimeters
            ?? (millimetersPerDrawingUnit is > 0 ? wall.Thickness * millimetersPerDrawingUnit.Value : null);
        var startOffset = startParameter * wallLength;
        var endOffset = endParameter * wallLength;
        var centerOffset = ((startParameter + endParameter) / 2.0) * wallLength;
        var adjacentOpeningIds = new[] { previousOpeningId, nextOpeningId }
            .Concat(EndpointAdjacentOpeningIds(wall, startPoint, endPoint, openings))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new PlacementWallSolidSpanExport(
            $"{wall.Id}:solid-span:{sequence}",
            wall.PageNumber,
            wall.Id,
            sequence,
            reliability?.ReadyForCoordinatePlacement ?? true,
            reliability?.ReadyForMetricPlacement ?? (millimetersPerDrawingUnit is > 0),
            reliability?.RequiresReview ?? false,
            reliability?.Reasons ?? Array.Empty<string>(),
            placementOmission?.Code,
            LineExport.From(line),
            ScaleLine(line, millimetersPerDrawingUnit),
            bodyFootprint.Polygon.Select(PointExport.From).ToArray(),
            bodyPolygonMillimeters,
            RectExport.From(bodyFootprint.Bounds),
            ScaleRect(bodyFootprint.Bounds, millimetersPerDrawingUnit),
            VectorExport.From(bodyFootprint.AlongVector),
            VectorExport.From(bodyFootprint.NormalVector),
            wall.Thickness,
            thicknessMillimeters,
            startParameter,
            endParameter,
            (startParameter + endParameter) / 2.0,
            startOffset,
            endOffset,
            centerOffset,
            line.Length,
            millimetersPerDrawingUnit is > 0 ? line.Length * millimetersPerDrawingUnit.Value / 1000.0 : null,
            adjacentOpeningIds,
            adjacentOpeningIds.Length == 0
                ? [
                    "Wall has no anchored opening cutouts; full centerline is usable as one solid span.",
                    $"Solid span body polygon is a closed wall footprint ring from {bodyFootprint.GeometrySource}."
                ]
                : [
                    "Wall solid span was trimmed around anchored opening cutouts.",
                    $"Solid span body polygon is a closed wall footprint ring from {bodyFootprint.GeometrySource}."
                ]);
    }

    private static IReadOnlyList<PointExport>? ScalePoints(
        IReadOnlyList<PlanPoint> points,
        double? millimetersPerDrawingUnit) =>
        millimetersPerDrawingUnit is > 0
            ? points.Select(point => ScalePoint(point, millimetersPerDrawingUnit)!).ToArray()
            : null;

    private static IEnumerable<string> EndpointAdjacentOpeningIds(
        WallSegment wall,
        PlanPoint startPoint,
        PlanPoint endPoint,
        IReadOnlyList<OpeningCandidate> openings)
    {
        foreach (var opening in openings)
        {
            if (opening.Placement is null
                || !ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening))
            {
                continue;
            }

            var tolerance = Math.Max(1.5, Math.Max(wall.Thickness * 2.5, opening.Placement.DepthDrawingUnits * 2.5));
            if (OpeningEndpointTouches(opening.Placement, startPoint, tolerance)
                || OpeningEndpointTouches(opening.Placement, endPoint, tolerance))
            {
                yield return opening.Id;
            }
        }
    }

    private static bool OpeningEndpointTouches(
        OpeningPlacement placement,
        PlanPoint point,
        double tolerance) =>
        placement.StartPoint.DistanceTo(point) <= tolerance
        || placement.EndPoint.DistanceTo(point) <= tolerance;

    private static IReadOnlyList<PlacementWallOpeningCutoutExport> MergeCutouts(
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        if (cutouts.Count <= 1)
        {
            return cutouts;
        }

        var merged = new List<PlacementWallOpeningCutoutExport>();
        foreach (var cutout in cutouts.OrderBy(item => item.StartParameter).ThenBy(item => item.EndParameter))
        {
            if (merged.Count == 0
                || cutout.StartParameter > merged[^1].EndParameter + 0.001)
            {
                merged.Add(cutout);
                continue;
            }

            var previous = merged[^1];
            if (cutout.EndParameter > previous.EndParameter)
            {
                merged[^1] = previous with
                {
                    EndParameter = cutout.EndParameter,
                    CenterParameter = (previous.StartParameter + cutout.EndParameter) / 2.0,
                    EndOffsetDrawingUnits = cutout.EndOffsetDrawingUnits,
                    CenterOffsetDrawingUnits = (previous.StartOffsetDrawingUnits + cutout.EndOffsetDrawingUnits) / 2.0,
                    LengthDrawingUnits = cutout.EndOffsetDrawingUnits - previous.StartOffsetDrawingUnits,
                    EndOffsetMillimeters = cutout.EndOffsetMillimeters,
                    CenterOffsetMillimeters = cutout.CenterOffsetMillimeters,
                    LengthMillimeters = cutout.LengthMillimeters,
                    Evidence = previous.Evidence.Concat(cutout.Evidence).Distinct(StringComparer.Ordinal).ToArray()
                };
            }
        }

        return merged;
    }
}

public sealed record PlacementWallTopologySpanExport(
    string Id,
    string WallGraphEdgeId,
    int PageNumber,
    string WallId,
    string FromNodeId,
    string ToNodeId,
    LineExport CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    double DrawingLength,
    double? LengthMeters,
    double? SourceWallStartOffsetDrawingUnits,
    double? SourceWallEndOffsetDrawingUnits,
    double? SourceWallProjectedLengthDrawingUnits,
    double? SourceWallStartOffsetMillimeters,
    double? SourceWallEndOffsetMillimeters,
    double? SourceWallProjectedLengthMillimeters,
    double? SourceWallStartParameter,
    double? SourceWallEndParameter,
    double? SourceWallCenterParameter,
    double? SourceWallStartProjectionDistanceDrawingUnits,
    double? SourceWallEndProjectionDistanceDrawingUnits,
    double? SourceWallStartProjectionDistanceMillimeters,
    double? SourceWallEndProjectionDistanceMillimeters,
    double ThicknessDrawingUnits,
    double? ThicknessMillimeters,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> SourceWallGraphEdgeIds,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallTopologySpanExport From(
        WallGraphTopologySpan span,
        double? millimetersPerDrawingUnit,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var lengthMeters = millimetersPerDrawingUnit is > 0
            ? span.DrawingLength * millimetersPerDrawingUnit.Value / 1000.0
            : (double?)null;
        var thicknessMillimeters = millimetersPerDrawingUnit is > 0
            ? span.Thickness * millimetersPerDrawingUnit.Value
            : (double?)null;

        return new PlacementWallTopologySpanExport(
            span.Id,
            span.Id,
            span.PageNumber,
            span.WallId,
            span.FromNodeId,
            span.ToNodeId,
            LineExport.From(span.CenterLine),
            ScaleLine(span.CenterLine, millimetersPerDrawingUnit),
            RectExport.From(span.Bounds),
            ScaleRect(span.Bounds, millimetersPerDrawingUnit),
            span.DrawingLength,
            lengthMeters,
            span.SourceWallStartOffsetDrawingUnits,
            span.SourceWallEndOffsetDrawingUnits,
            span.SourceWallProjectedLengthDrawingUnits,
            ScaleNullable(span.SourceWallStartOffsetDrawingUnits, millimetersPerDrawingUnit),
            ScaleNullable(span.SourceWallEndOffsetDrawingUnits, millimetersPerDrawingUnit),
            ScaleNullable(span.SourceWallProjectedLengthDrawingUnits, millimetersPerDrawingUnit),
            span.SourceWallStartParameter,
            span.SourceWallEndParameter,
            span.SourceWallCenterParameter,
            span.SourceWallStartProjectionDistanceDrawingUnits,
            span.SourceWallEndProjectionDistanceDrawingUnits,
            ScaleNullable(span.SourceWallStartProjectionDistanceDrawingUnits, millimetersPerDrawingUnit),
            ScaleNullable(span.SourceWallEndProjectionDistanceDrawingUnits, millimetersPerDrawingUnit),
            span.Thickness,
            thicknessMillimeters,
            span.Confidence.Value,
            span.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(span.SourcePrimitiveIds, sourceLookup),
            span.SourceWallGraphEdgeIds,
            span.Evidence);
    }

    private static double? ScaleNullable(double? value, double? millimetersPerDrawingUnit) =>
        value is not null && millimetersPerDrawingUnit is > 0
            ? value.Value * millimetersPerDrawingUnit.Value
            : null;
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
    PlacementRoomBoundaryReliabilityExport BoundaryReliability,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> Evidence)
{
    public static PlacementRoomExport From(
        RoomRegion room,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments,
        IReadOnlyDictionary<string, PlacementWallExport> placementWallsById,
        IReadOnlyDictionary<string, int>? roomBoundaryWallUseCounts = null)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, room.MeasurementScaleGroupId);
        var boundaryReliability = PlacementReliability.ForRoomBoundary(
            room,
            wallEvidenceAssessments,
            placementWallsById,
            roomBoundaryWallUseCounts);
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
            boundaryReliability,
            PlacementReliability.ForRoom(room, calibration, boundaryReliability),
            room.Evidence);
    }
}

public sealed record PlacementRoomBoundaryReliabilityExport(
    int BoundaryWallCount,
    int AssessedWallCount,
    IReadOnlyList<string> ReadyWallIds,
    IReadOnlyList<string> ReviewWallIds,
    IReadOnlyList<string> RejectedWallIds,
    IReadOnlyList<string> NonBlockingDuplicateWallIds,
    IReadOnlyList<string> OpeningOnlyWallIds,
    IReadOnlyList<string> RoomSupportedFragmentWallIds,
    IReadOnlyList<string> UnassessedWallIds,
    IReadOnlyList<string> CoordinateBlockingWallIds,
    IReadOnlyList<string> Evidence);

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
    IReadOnlyList<OpeningRoomConnectionExport> ConnectedRoomLinks,
    IReadOnlyList<string> RoomAdjacencyIds,
    double Confidence,
    PlacementReliabilityExport Reliability,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementOpeningExport From(
        OpeningCandidate opening,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
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
            opening.Placement is null ? null : OpeningPlacementExport.From(opening.Placement, scale),
            opening.HingeSide.ToString(),
            opening.SwingSide.ToString(),
            opening.SwingDirection.ToString(),
            opening.HingePoint is null ? null : PointExport.From(opening.HingePoint.Value),
            opening.HingePoint is null ? null : ScalePoint(opening.HingePoint.Value, scale),
            opening.HostWallIds,
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.ConnectedRoomLinks.Select(OpeningRoomConnectionExport.From).ToArray(),
            opening.RoomAdjacencyIds,
            opening.Confidence.Value,
            PlacementReliability.ForOpening(opening, calibration, wallEvidenceAssessments),
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
    string Severity,
    string ImportImpact,
    string Applicability,
    string SourceNodeId,
    PointExport SourcePoint,
    PointExport? SourcePointMillimeters,
    PointExport TargetPoint,
    PointExport? TargetPointMillimeters,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistanceDrawingUnits,
    double? GapDistanceMillimeters,
    double SafeSnapDistanceDrawingUnits,
    double? SafeSnapDistanceMillimeters,
    double ReviewDistanceLimitDrawingUnits,
    double? ReviewDistanceLimitMillimeters,
    double ExcessDistanceBeyondSafeSnapDrawingUnits,
    double? ExcessDistanceBeyondSafeSnapMillimeters,
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
            candidate.Severity.ToString(),
            candidate.ImportImpact.ToString(),
            candidate.Applicability.ToString(),
            candidate.SourceNodeId,
            PointExport.From(candidate.SourcePoint),
            ScalePoint(candidate.SourcePoint, scale),
            PointExport.From(candidate.TargetPoint),
            ScalePoint(candidate.TargetPoint, scale),
            candidate.TargetNodeId,
            candidate.HostWallId,
            candidate.GapDistance,
            scale is > 0 ? candidate.GapDistance * scale.Value : null,
            candidate.SafeSnapDistance,
            scale is > 0 ? candidate.SafeSnapDistance * scale.Value : null,
            candidate.ReviewDistanceLimit,
            scale is > 0 ? candidate.ReviewDistanceLimit * scale.Value : null,
            candidate.ExcessDistanceBeyondSafeSnap,
            scale is > 0 ? candidate.ExcessDistanceBeyondSafeSnap * scale.Value : null,
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
        candidate.Applicability == WallGraphRepairApplicability.ManualCorrectionRecommended
            ? "Manually review and correct this wall graph issue before using topology for downstream placement."
            : candidate.SuggestedAction == WallGraphRepairAction.TrimEndpointOverrun
                ? "Review this endpoint-overrun trim candidate before shortening the wall placement geometry."
                : candidate.SuggestedAction == WallGraphRepairAction.SnapEndpointToWall
                    ? "Review this endpoint-to-wall snap candidate before repairing the wall graph topology."
                    : "Review this endpoint-to-endpoint snap candidate before repairing the wall graph topology.";
}

public sealed record PlacementWallGraphExport(
    PlacementWallGraphSummaryExport Summary,
    IReadOnlyList<PlacementWallGraphNodeExport> Nodes,
    IReadOnlyList<PlacementWallGraphEdgeExport> Edges,
    IReadOnlyList<PlacementWallGraphComponentExport> Components,
    IReadOnlyList<string> RepairCandidateIds,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallGraphExport From(
        WallGraph graph,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        var spansByEdgeId = BuildTopologySpanLookupByWallGraphEdgeId(topologySpans);
        var nodes = graph.Nodes
            .Select(node => PlacementWallGraphNodeExport.From(node, calibration))
            .ToArray();
        var edges = graph.Edges
            .Select(edge => PlacementWallGraphEdgeExport.From(
                edge,
                spansByEdgeId.TryGetValue(edge.Id, out var span) ? span : null,
                calibration,
                sourceLookup,
                wallComponentLookup,
                wallEvidenceAssessments))
            .ToArray();
        var components = graph.Components
            .Select(component => PlacementWallGraphComponentExport.From(component, calibration, sourceLookup))
            .ToArray();
        var repairCandidateIds = graph.RepairCandidates.Select(candidate => candidate.Id).ToArray();
        var summary = PlacementWallGraphSummaryExport.From(graph, edges);
        var evidence = new[]
        {
            $"placement wall graph exports {nodes.Length} node(s), {edges.Length} edge(s), and {components.Length} component(s)",
            $"repair candidate ids exported: {repairCandidateIds.Length}"
        };

        return new PlacementWallGraphExport(
            summary,
            nodes,
            edges,
            components,
            repairCandidateIds,
            evidence);
    }

    private static IReadOnlyDictionary<string, WallGraphTopologySpan> BuildTopologySpanLookupByWallGraphEdgeId(
        IReadOnlyList<WallGraphTopologySpan> topologySpans)
    {
        var spansByEdgeId = new Dictionary<string, WallGraphTopologySpan>(StringComparer.Ordinal);
        foreach (var span in topologySpans)
        {
            AddSpan(span.Id, span);
            foreach (var sourceEdgeId in span.SourceWallGraphEdgeIds)
            {
                AddSpan(sourceEdgeId, span);
            }
        }

        return spansByEdgeId;

        void AddSpan(string edgeId, WallGraphTopologySpan span)
        {
            if (string.IsNullOrWhiteSpace(edgeId))
            {
                return;
            }

            if (!spansByEdgeId.TryGetValue(edgeId, out var existing)
                || span.DrawingLength > existing.DrawingLength)
            {
                spansByEdgeId[edgeId] = span;
            }
        }
    }
}

public sealed record PlacementWallGraphSummaryExport(
    int NodeCount,
    int EdgeCount,
    int ComponentCount,
    int MainStructuralComponentCount,
    int SecondaryStructuralComponentCount,
    int ObjectLikeComponentCount,
    int IsolatedFragmentComponentCount,
    int StructuralEdgeCount,
    int ExcludedEdgeCount,
    int RepairCandidateCount,
    int HighSeverityRepairCandidateCount,
    int ReviewRepairCandidateCount,
    int BlockingRepairCandidateCount)
{
    public static PlacementWallGraphSummaryExport From(
        WallGraph graph,
        IReadOnlyList<PlacementWallGraphEdgeExport> edges) =>
        new(
            graph.Nodes.Count,
            graph.Edges.Count,
            graph.Components.Count,
            graph.Components.Count(component => component.Kind == WallGraphComponentKind.MainStructural),
            graph.Components.Count(component => component.Kind == WallGraphComponentKind.SecondaryStructural),
            graph.Components.Count(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland),
            graph.Components.Count(component => component.Kind == WallGraphComponentKind.IsolatedFragment),
            edges.Count(edge => !edge.ExcludedFromStructuralTopology),
            edges.Count(edge => edge.ExcludedFromStructuralTopology),
            graph.RepairCandidates.Count,
            graph.RepairCandidates.Count(candidate => candidate.Severity == WallGraphRepairSeverity.High),
            graph.RepairCandidates.Count(candidate => candidate.ImportImpact == WallGraphRepairImportImpact.TopologyReviewRequired),
            graph.RepairCandidates.Count(candidate => candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked));
}

public sealed record PlacementWallGraphNodeExport(
    string Id,
    int PageNumber,
    PointExport Position,
    PointExport? PositionMillimeters,
    string Kind,
    int Degree,
    IReadOnlyList<string> Directions,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallGraphNodeExport From(
        WallNode node,
        PlanCalibration calibration)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementWallGraphNodeExport(
            node.Id,
            node.PageNumber,
            PointExport.From(node.Position),
            ScalePoint(node.Position, scale),
            node.Kind.ToString(),
            node.Degree,
            node.Directions,
            node.Confidence.Value,
            node.Evidence);
    }
}

public sealed record PlacementWallGraphEdgeExport(
    string Id,
    int PageNumber,
    string FromNodeId,
    string ToNodeId,
    string WallId,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    LineExport? CenterLine,
    LineExport? CenterLineMillimeters,
    RectExport? Bounds,
    RectExport? BoundsMillimeters,
    double DrawingLength,
    double? LengthMeters,
    double ThicknessDrawingUnits,
    double? ThicknessMillimeters,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallGraphEdgeExport From(
        WallEdge edge,
        WallGraphTopologySpan? topologySpan,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        wallComponentLookup.TryGetValue(edge.WallId, out var component);
        wallEvidenceAssessments.TryGetValue(edge.WallId, out var evidenceAssessment);
        var scale = ResolveMillimetersPerDrawingUnit(calibration, topologySpan?.SourceWall?.MeasurementScaleGroupId);
        var drawingLength = topologySpan?.DrawingLength ?? 0;
        var thickness = topologySpan?.Thickness ?? 0;
        var excludedFromStructuralTopology =
            WallStructuralTrust.IsExcludedFromStructuralTopology(component, evidenceAssessment);
        return new PlacementWallGraphEdgeExport(
            edge.Id,
            edge.PageNumber,
            edge.FromNodeId,
            edge.ToNodeId,
            edge.WallId,
            component?.Id,
            component?.Kind.ToString(),
            excludedFromStructuralTopology,
            topologySpan is null ? null : LineExport.From(topologySpan.CenterLine),
            topologySpan is null ? null : ScaleLine(topologySpan.CenterLine, scale),
            topologySpan is null ? null : RectExport.From(topologySpan.Bounds),
            topologySpan is null ? null : ScaleRect(topologySpan.Bounds, scale),
            drawingLength,
            scale is > 0 && drawingLength > 0 ? drawingLength * scale.Value / 1000.0 : null,
            thickness,
            scale is > 0 && thickness > 0 ? thickness * scale.Value : null,
            scale,
            edge.Confidence.Value,
            topologySpan?.SourcePrimitiveIds ?? Array.Empty<string>(),
            topologySpan is null
                ? Array.Empty<string>()
                : ExportSourceHelpers.SourceLayers(topologySpan.SourcePrimitiveIds, sourceLookup),
            EdgeEvidence(topologySpan, evidenceAssessment));
    }

    private static IReadOnlyList<string> EdgeEvidence(
        WallGraphTopologySpan? topologySpan,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var evidence = new List<string>(topologySpan?.Evidence ?? Array.Empty<string>());
        if (WallStructuralTrust.IsRejectedNonStructural(evidenceAssessment))
        {
            evidence.Add($"wall graph edge excluded because wall evidence rejected as non-wall/noise ({evidenceAssessment!.Category})");
        }

        return evidence.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record PlacementWallGraphComponentExport(
    string Id,
    int PageNumber,
    string Kind,
    RectExport Bounds,
    RectExport? BoundsMillimeters,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    int WallCount,
    int NodeCount,
    int EdgeCount,
    double DrawingLength,
    double? LengthMeters,
    double Confidence,
    bool ExcludedFromStructuralTopology,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallGraphComponentExport From(
        WallGraphComponent component,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementWallGraphComponentExport(
            component.Id,
            component.PageNumber,
            component.Kind.ToString(),
            RectExport.From(component.Bounds),
            ScaleRect(component.Bounds, scale),
            component.WallIds,
            component.NodeIds,
            component.EdgeIds,
            component.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(component.SourcePrimitiveIds, sourceLookup),
            component.WallCount,
            component.NodeCount,
            component.EdgeCount,
            component.DrawingLength,
            scale is > 0 ? component.DrawingLength * scale.Value / 1000.0 : null,
            component.Confidence.Value,
            component.ExcludedFromStructuralTopology,
            component.Evidence);
    }
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
    IReadOnlyList<OpeningRoomConnectionExport> ConnectedRoomLinks,
    IReadOnlyList<string> RoomAdjacencyIds,
    OpeningPlacementExport? Placement,
    string PlacementStatus,
    bool ReadyForCoordinatePlacement,
    bool RequiresReview,
    IReadOnlyList<string> ReviewReasons,
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
            passage.ConnectedRoomLinks.Select(OpeningRoomConnectionExport.From).ToArray(),
            passage.RoomAdjacencyIds,
            passage.Placement is null ? null : OpeningPlacementExport.From(passage.Placement, scale),
            passage.Placement is null ? "Unanchored" : "Anchored",
            passage.ReadyForCoordinatePlacement,
            passage.RequiresReview,
            passage.ReviewReasons,
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
    private const string SurfacePatternWallOverlapDiagnosticCode = "wall_graph.surface_pattern_wall_overlap.review";
    private const string PdfRasterOcrRequiredIssueCode = "quality.pdf_raster_ocr_required";
    private const string RasterNoExtractedPrimitivesIssueCode = "quality.raster_no_extracted_primitives";
    private const string RasterLowExtractionConfidenceIssueCode = "quality.raster_low_extraction_confidence";
    private const string OpeningPlacementInconsistentIssueCode = "placement.opening.placement_inconsistent";

    public static IEnumerable<PlacementIssueExport> From(
        PlanScanResult result,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var wallsById = result.Walls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

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

        foreach (var entry in ScanReviewQueueSummary.QueuedWallEvidenceReviews(result.WallEvidenceMap)
                     .Select((assessment, index) => new { Assessment = assessment, Index = index }))
        {
            var assessment = entry.Assessment;
            var sourcePrimitiveIds = assessment.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            wallsById.TryGetValue(assessment.WallId, out var wall);
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, wall?.MeasurementScaleGroupId);
            var severity = ScanReviewQueueSummary.WallEvidenceReviewSeverity(assessment);
            var reviewReason = ScanReviewQueueSummary.WallEvidenceReviewReason(assessment);
            var score = assessment.ScoreBreakdown;

            yield return new PlacementIssueExport(
                "placement.review.wall_evidence_requires_review",
                severity.ToString(),
                "Wall Evidence V2 marked a wall candidate as requiring review before exact placement use.",
                assessment.PageNumber,
                new[] { assessment.PageNumber },
                assessment.WallId,
                RectExport.From(assessment.Bounds),
                ScaleRect(assessment.Bounds, scale),
                ClampRatio(assessment.Confidence.Value),
                "Review this wall candidate before importing it as coordinate-ready structural geometry.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(
                    assessment.Evidence
                        .Concat(score.PositiveEvidence.Select(evidence => $"positive: {evidence}"))
                        .Concat(score.NegativeEvidence.Select(evidence => $"negative: {evidence}"))
                        .Concat(new[] { reviewReason })),
                new Dictionary<string, string>
                {
                    ["detector"] = "wallEvidence",
                    ["wallId"] = assessment.WallId,
                    ["category"] = assessment.Category.ToString(),
                    ["decision"] = assessment.Decision.ToString(),
                    ["placementReady"] = assessment.PlacementReady.ToString(CultureInfo.InvariantCulture),
                    ["requiresReview"] = assessment.RequiresReview.ToString(CultureInfo.InvariantCulture),
                    ["rejectedAsNoise"] = assessment.RejectedAsNoise.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueLimit"] = ScanReviewQueueSummary.WallEvidenceReviewQueueLimit.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueReason"] = reviewReason,
                    ["reviewQueuePriorityScore"] = ScanReviewQueueSummary.WallEvidenceReviewPriorityScore(assessment).ToString("0.###", CultureInfo.InvariantCulture),
                    ["positiveScore"] = score.PositiveScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["negativeScore"] = score.NegativeScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["decisionScore"] = score.DecisionScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["pairSupportScore"] = score.PairSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["layerSupportScore"] = score.LayerSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["structuralSupportScore"] = score.StructuralSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["recoverySupportScore"] = score.RecoverySupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["noisePenalty"] = score.NoisePenalty.ToString("0.###", CultureInfo.InvariantCulture),
                    ["fragmentReviewPenalty"] = score.FragmentReviewPenalty.ToString("0.###", CultureInfo.InvariantCulture)
                });
        }

        var componentByWallId = BuildComponentLookup(result.WallGraph.Components);
        foreach (var entry in result.WallEvidenceMap.WallAssessments
                     .OrderByDescending(assessment => assessment.Confidence.Value)
                     .ThenBy(assessment => assessment.WallId, StringComparer.Ordinal)
                     .Select((assessment, index) => new { Assessment = assessment, Index = index }))
        {
            var assessment = entry.Assessment;
            if (!wallsById.TryGetValue(assessment.WallId, out var wall))
            {
                continue;
            }

            componentByWallId.TryGetValue(assessment.WallId, out var component);
            if (!IsRejectedStrongWallBody(assessment, wall, component))
            {
                continue;
            }

            var sourcePrimitiveIds = assessment.SourcePrimitiveIds
                .Concat(wall.SourcePrimitiveIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, wall.MeasurementScaleGroupId);
            var score = assessment.ScoreBreakdown;
            var componentEvidence = component?.Evidence ?? Array.Empty<string>();

            yield return new PlacementIssueExport(
                "placement.review.rejected_strong_wall_body",
                DiagnosticSeverity.Info.ToString(),
                "Wall Evidence V2 rejected a strong wall-body candidate as non-structural detail.",
                assessment.PageNumber,
                new[] { assessment.PageNumber },
                assessment.WallId,
                RectExport.From(assessment.Bounds),
                ScaleRect(assessment.Bounds, scale),
                ClampRatio(assessment.Confidence.Value),
                "Review this candidate against the source plan before deciding whether it is detail linework or a missed structural wall.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(
                    assessment.Evidence
                        .Concat(componentEvidence)
                        .Concat(score.PositiveEvidence.Select(evidence => $"positive: {evidence}"))
                        .Concat(score.NegativeEvidence.Select(evidence => $"negative: {evidence}"))),
                new Dictionary<string, string>
                {
                    ["detector"] = "wallEvidence",
                    ["wallId"] = assessment.WallId,
                    ["category"] = assessment.Category.ToString(),
                    ["decision"] = assessment.Decision.ToString(),
                    ["placementReady"] = assessment.PlacementReady.ToString(CultureInfo.InvariantCulture),
                    ["requiresReview"] = assessment.RequiresReview.ToString(CultureInfo.InvariantCulture),
                    ["rejectedAsNoise"] = assessment.RejectedAsNoise.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture),
                    ["componentId"] = component?.Id ?? string.Empty,
                    ["componentKind"] = component?.Kind.ToString() ?? string.Empty,
                    ["componentExcludedFromStructuralTopology"] = (component?.ExcludedFromStructuralTopology == true).ToString(CultureInfo.InvariantCulture),
                    ["positiveScore"] = score.PositiveScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["negativeScore"] = score.NegativeScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["decisionScore"] = score.DecisionScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["pairSupportScore"] = score.PairSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["structuralSupportScore"] = score.StructuralSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["noisePenalty"] = score.NoisePenalty.ToString("0.###", CultureInfo.InvariantCulture)
                });
        }

        foreach (var pattern in PlanRoutingLayerBuilder.DetectDenseMinorRoutingDetailPatterns(result)
                     .OrderBy(pattern => pattern.PageNumber)
                     .ThenBy(pattern => pattern.Id, StringComparer.Ordinal))
        {
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, scaleGroupId: null);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = nameof(PlanRoutingLayerBuilder),
                ["patternKind"] = "DenseMinorSecondaryRoutingDetail",
                ["hostWallId"] = pattern.HostWallId,
                ["hostWallComponentId"] = pattern.HostWallComponentId ?? string.Empty,
                ["hostWallComponentKind"] = pattern.HostWallComponentKind?.ToString() ?? string.Empty,
                ["minorJunctionCount"] = pattern.MinorJunctionCount.ToString(CultureInfo.InvariantCulture),
                ["minorDetailWallCount"] = pattern.MinorDetailWallCount.ToString(CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", pattern.WallIds),
                ["incidentWallIds"] = string.Join(",", pattern.IncidentWallIds)
            };

            yield return new PlacementIssueExport(
                "placement.review.dense_minor_routing_detail",
                DiagnosticSeverity.Info.ToString(),
                "Dense secondary detail linework was kept out of trusted routing barriers.",
                pattern.PageNumber,
                new[] { pattern.PageNumber },
                pattern.Id,
                RectExport.From(pattern.Bounds),
                ScaleRect(pattern.Bounds, scale),
                ClampRatio(pattern.Confidence.Value),
                "Treat this pattern as non-structural routing detail unless visual review confirms it is a real wall system.",
                pattern.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(pattern.SourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(pattern.Evidence),
                properties);
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedWallGraphGapDiagnostics(result.Diagnostics.Messages)
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
                : properties.TryGetValue("overrunDistance", out var overrunDistance)
                    ? new[] { $"overrun distance {overrunDistance} drawing units" }
                    : Array.Empty<string>();
            var wallEvidence = properties.TryGetValue("wallIds", out var wallIds) && !string.IsNullOrWhiteSpace(wallIds)
                ? new[] { $"candidate wall ids: {wallIds}" }
                : Array.Empty<string>();
            var isEndpointOverrun = string.Equals(diagnostic.Code, "wall_graph.endpoint_overrun.review", StringComparison.Ordinal);

            yield return new PlacementIssueExport(
                isEndpointOverrun
                    ? "placement.review.wall_graph_endpoint_overrun"
                    : "placement.review.wall_graph_endpoint_gap",
                diagnostic.Severity.ToString(),
                diagnostic.Message,
                diagnostic.PageNumber,
                pageNumbers,
                $"review:{(isEndpointOverrun ? "wall-graph-endpoint-overrun" : "wall-graph-gap")}:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Region is null ? null : ScaleRect(diagnostic.Region.Value, scale),
                ClampRatio(diagnostic.Confidence?.Value ?? 0.5),
                isEndpointOverrun
                    ? "Review or correct this possible endpoint-overrun trim before importing wall graph topology."
                    : "Review or correct this possible unsnapped wall junction before importing wall graph topology.",
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

        foreach (var opening in result.Openings
                     .Where(opening => opening.Placement is not null)
                     .Where(opening => !ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening)))
        {
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, opening.MeasurementScaleGroupId);
            var reasons = ScanReviewQueueSummary.OpeningReviewReasons(opening).ToArray();
            var sourcePrimitiveIds = opening.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var placement = opening.Placement!;
            yield return new PlacementIssueExport(
                OpeningPlacementInconsistentIssueCode,
                DiagnosticSeverity.Warning.ToString(),
                "Opening is anchored to a host wall, but its placement offsets or wall parameters are internally inconsistent.",
                opening.PageNumber,
                new[] { opening.PageNumber },
                opening.Id,
                RectExport.From(opening.Bounds),
                ScaleRect(opening.Bounds, scale),
                ClampRatio(opening.Confidence.Value),
                "Review or correct the anchored opening placement before using it to cut walls, connect rooms, or route through the opening.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(opening.Evidence.Concat(reasons).DefaultIfEmpty("Opening placement failed coordinate-readiness checks.")),
                new Dictionary<string, string>
                {
                    ["type"] = opening.Type.ToString(),
                    ["operation"] = opening.Operation.ToString(),
                    ["placementStatus"] = "Anchored",
                    ["hostWallId"] = placement.HostWallId ?? string.Empty,
                    ["hostWallIds"] = string.Join(",", opening.HostWallIds),
                    ["startOffsetDrawingUnits"] = placement.StartOffsetDrawingUnits.ToString("0.######", CultureInfo.InvariantCulture),
                    ["endOffsetDrawingUnits"] = placement.EndOffsetDrawingUnits.ToString("0.######", CultureInfo.InvariantCulture),
                    ["centerOffsetDrawingUnits"] = placement.CenterOffsetDrawingUnits.ToString("0.######", CultureInfo.InvariantCulture),
                    ["lengthDrawingUnits"] = placement.LengthDrawingUnits.ToString("0.######", CultureInfo.InvariantCulture),
                    ["hostWallStartParameter"] = placement.HostWallStartParameter.ToString("0.######", CultureInfo.InvariantCulture),
                    ["hostWallEndParameter"] = placement.HostWallEndParameter.ToString("0.######", CultureInfo.InvariantCulture),
                    ["hostWallCenterParameter"] = placement.HostWallCenterParameter.ToString("0.######", CultureInfo.InvariantCulture),
                    ["reasons"] = string.Join("; ", reasons)
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

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildComponentLookup(
        IReadOnlyList<WallGraphComponent> components)
    {
        var result = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
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

    private static bool IsRejectedStrongWallBody(
        WallEvidenceWallAssessment assessment,
        WallSegment wall,
        WallGraphComponent? component) =>
        (assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
        && component?.Kind == WallGraphComponentKind.ObjectLikeIsland
        && wall.DrawingLength >= 60
        && assessment.Category is WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.ObjectOrFixtureDetail
        && assessment.Evidence.Any(evidence =>
            evidence.Contains("strong double-edge wall body", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase));

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
        WallEvidenceWallAssessment? evidenceAssessment,
        IReadOnlyList<string> reviewReasons)
    {
        var readiness = WallPlacementReadinessEvaluator.Evaluate(
            wall,
            calibration,
            component,
            evidenceAssessment,
            reviewReasons);

        return Create(
            readiness.Confidence.Value,
            readiness.ReadyForCoordinatePlacement,
            readiness.ReadyForMetricPlacement,
            readiness.RequiresReview,
            readiness.Reasons);
    }

    public static PlacementReliabilityExport ForRoom(
        RoomRegion room,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        var boundaryReliability = ForRoomBoundary(
            room,
            wallEvidenceAssessments,
            placementWallsById: null);
        return ForRoom(room, calibration, boundaryReliability);
    }

    public static PlacementRoomBoundaryReliabilityExport ForRoomBoundary(
        RoomRegion room,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments,
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById,
        IReadOnlyDictionary<string, int>? roomBoundaryWallUseCounts = null)
    {
        var wallIds = room.WallIds
            .Where(wallId => !string.IsNullOrWhiteSpace(wallId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var readyWallIds = new List<string>();
        var reviewWallIds = new List<string>();
        var rejectedWallIds = new List<string>();
        var nonBlockingDuplicateWallIds = new List<string>();
        var openingOnlyWallIds = new List<string>();
        var roomSupportedFragmentWallIds = new List<string>();
        var unassessedWallIds = new List<string>();
        var assessedWallCount = 0;

        foreach (var wallId in wallIds)
        {
            PlacementWallExport? placementWall = null;
            placementWallsById?.TryGetValue(wallId, out placementWall);
            if (IsOpeningOnlyBoundaryWall(placementWall))
            {
                openingOnlyWallIds.Add(wallId);
                if (wallEvidenceAssessments.ContainsKey(wallId))
                {
                    assessedWallCount++;
                }

                continue;
            }

            if (!wallEvidenceAssessments.TryGetValue(wallId, out var assessment))
            {
                unassessedWallIds.Add(wallId);
                continue;
            }

            assessedWallCount++;
            if (assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            {
                rejectedWallIds.Add(wallId);
            }
            else if (IsNonBlockingDuplicateBoundaryWallEvidence(assessment))
            {
                nonBlockingDuplicateWallIds.Add(wallId);
            }
            else if (IsRoomSupportedFragmentBoundaryWall(
                wallId,
                assessment,
                placementWall,
                roomBoundaryWallUseCounts))
            {
                roomSupportedFragmentWallIds.Add(wallId);
            }
            else if (assessment.Decision == WallEvidenceDecision.Review
                || assessment.RequiresReview
                || !assessment.PlacementReady)
            {
                reviewWallIds.Add(wallId);
            }
            else
            {
                readyWallIds.Add(wallId);
            }
        }

        var coordinateBlockingWallIds = reviewWallIds
            .Concat(rejectedWallIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<string>
        {
            $"room boundary references {wallIds.Length} wall(s); {assessedWallCount} have wall evidence assessments"
        };
        if (coordinateBlockingWallIds.Length > 0)
        {
            evidence.Add($"coordinate-blocking room boundary wall evidence: {string.Join(",", coordinateBlockingWallIds)}");
        }

        if (nonBlockingDuplicateWallIds.Count > 0)
        {
            evidence.Add($"non-blocking duplicate room boundary wall evidence: {string.Join(",", nonBlockingDuplicateWallIds.Order(StringComparer.Ordinal))}");
        }

        if (openingOnlyWallIds.Count > 0)
        {
            evidence.Add($"non-blocking opening-only room boundary wall evidence: {string.Join(",", openingOnlyWallIds.Order(StringComparer.Ordinal))}");
        }

        if (roomSupportedFragmentWallIds.Count > 0)
        {
            evidence.Add($"non-blocking room-supported fragment boundary wall evidence: {string.Join(",", roomSupportedFragmentWallIds.Order(StringComparer.Ordinal))}");
        }

        if (unassessedWallIds.Count > 0)
        {
            evidence.Add($"room boundary wall evidence not assessed: {string.Join(",", unassessedWallIds.Order(StringComparer.Ordinal))}");
        }

        return new PlacementRoomBoundaryReliabilityExport(
            wallIds.Length,
            assessedWallCount,
            readyWallIds.Order(StringComparer.Ordinal).ToArray(),
            reviewWallIds.Order(StringComparer.Ordinal).ToArray(),
            rejectedWallIds.Order(StringComparer.Ordinal).ToArray(),
            nonBlockingDuplicateWallIds.Order(StringComparer.Ordinal).ToArray(),
            openingOnlyWallIds.Order(StringComparer.Ordinal).ToArray(),
            roomSupportedFragmentWallIds.Order(StringComparer.Ordinal).ToArray(),
            unassessedWallIds.Order(StringComparer.Ordinal).ToArray(),
            coordinateBlockingWallIds,
            evidence);
    }

    private static bool IsOpeningOnlyBoundaryWall(PlacementWallExport? wall)
    {
        if (wall is null
            || wall.OpeningCutouts.Count == 0
            || wall.TopologySpans.Count > 0
            || wall.SolidSpans.Count > 0
            || wall.DrawingLength <= 0.001)
        {
            return false;
        }

        return OpeningCutoutCoverageRatio(wall.OpeningCutouts) >= 0.98;
    }

    private static double OpeningCutoutCoverageRatio(IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        var intervals = cutouts
            .Select(cutout => (
                Start: Math.Clamp(cutout.StartParameter, 0, 1),
                End: Math.Clamp(cutout.EndParameter, 0, 1)))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ThenBy(interval => interval.End)
            .ToArray();
        if (intervals.Length == 0)
        {
            return 0;
        }

        var covered = 0.0;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        for (var index = 1; index < intervals.Length; index++)
        {
            var interval = intervals[index];
            if (interval.Start > currentEnd)
            {
                covered += currentEnd - currentStart;
                currentStart = interval.Start;
                currentEnd = interval.End;
                continue;
            }

            currentEnd = Math.Max(currentEnd, interval.End);
        }

        covered += currentEnd - currentStart;
        return Math.Clamp(covered, 0, 1);
    }

    private static bool IsRoomSupportedFragmentBoundaryWall(
        string wallId,
        WallEvidenceWallAssessment assessment,
        PlacementWallExport? wall,
        IReadOnlyDictionary<string, int>? roomBoundaryWallUseCounts)
    {
        const double maxLowRiskGapRatio = 0.09;
        const double minBoundaryLength = 40.0;

        if (wall is null
            || roomBoundaryWallUseCounts is null
            || !roomBoundaryWallUseCounts.TryGetValue(wallId, out var roomUseCount)
            || roomUseCount < 2
            || !string.Equals(wall.DetectionKind, nameof(WallDetectionKind.FragmentMerged), StringComparison.Ordinal)
            || !string.Equals(wall.WallType, nameof(WallType.Interior), StringComparison.Ordinal)
            || !string.Equals(wall.WallComponentKind, nameof(WallGraphComponentKind.IsolatedFragment), StringComparison.Ordinal)
            || wall.DrawingLength < minBoundaryLength
            || wall.FragmentEvidence is null
            || wall.FragmentEvidence.RequiresGeometryReview
            || wall.FragmentEvidence.GapRatio > maxLowRiskGapRatio
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

        return evidence.Any(item =>
            item.Contains("shared by room adjacency boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("both endpoints supported by structural context", StringComparison.OrdinalIgnoreCase)
            || item.Contains("room boundary", StringComparison.OrdinalIgnoreCase));
    }

    public static PlacementReliabilityExport ForRoom(
        RoomRegion room,
        PlanCalibration calibration,
        PlacementRoomBoundaryReliabilityExport boundaryReliability)
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

        var isSemanticRoomSeed = room.Evidence.Any(item => item.Contains("semantic room seed", StringComparison.OrdinalIgnoreCase));
        if (isSemanticRoomSeed)
        {
            reasons.Add("semantic room seed requires review before coordinate placement");
        }

        if (room.WallIds.Count == 0)
        {
            reasons.Add("room boundary has no linked wall evidence");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        if (boundaryReliability.ReviewWallIds.Count > 0)
        {
            reasons.Add($"room boundary uses review-required wall evidence: {string.Join(",", boundaryReliability.ReviewWallIds.Take(12))}");
        }

        if (boundaryReliability.RejectedWallIds.Count > 0)
        {
            reasons.Add($"room boundary uses rejected wall evidence: {string.Join(",", boundaryReliability.RejectedWallIds.Take(12))}");
        }

        var boundaryWallsAreCoordinateReady = !isSemanticRoomSeed
            && room.WallIds.Count > 0
            && boundaryReliability.CoordinateBlockingWallIds.Count == 0;

        return Create(
            room.Confidence.Value,
            room.Confidence.Value >= 0.5 && room.Boundary.Count >= 3 && boundaryWallsAreCoordinateReady,
            calibration.HasReliableMeasurementScale,
            reasons.Count > 0 || !boundaryWallsAreCoordinateReady,
            reasons);
    }

    private static bool IsNonBlockingDuplicateBoundaryWallEvidence(WallEvidenceWallAssessment assessment)
    {
        if (assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is WallEvidenceCategory.ObjectOrFixtureDetail or WallEvidenceCategory.SurfacePatternDetail)
        {
            return false;
        }

        var evidence = assessment.Evidence
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        var isExplicitDuplicate = evidence.Any(item =>
            item.Contains("duplicate wall-face", StringComparison.OrdinalIgnoreCase)
            || item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase));
        if (!isExplicitDuplicate)
        {
            return false;
        }

        return evidence.Any(item =>
            item.Contains("already represented by stronger", StringComparison.OrdinalIgnoreCase)
            && item.Contains("paired wall body", StringComparison.OrdinalIgnoreCase));
    }

    public static PlacementReliabilityExport ForOpening(
        OpeningCandidate opening,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        var placementReady = ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening);
        var reasons = ScanReviewQueueSummary.OpeningReviewReasons(opening).ToList();

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        var hostAssessments = OpeningWallIds(opening)
            .Select(wallId => wallEvidenceAssessments.TryGetValue(wallId, out var assessment) ? assessment : null)
            .OfType<WallEvidenceWallAssessment>()
            .ToArray();
        var reviewHostWallIds = hostAssessments
            .Where(assessment => !assessment.RejectedAsNoise)
            .Where(assessment => assessment.Decision == WallEvidenceDecision.Review
                || assessment.RequiresReview
                || !assessment.PlacementReady)
            .Select(assessment => assessment.WallId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var rejectedHostWallIds = hostAssessments
            .Where(assessment => assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            .Select(assessment => assessment.WallId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (reviewHostWallIds.Length > 0)
        {
            reasons.Add($"opening placement uses review-required wall evidence: {string.Join(",", reviewHostWallIds.Take(12))}");
        }

        if (rejectedHostWallIds.Length > 0)
        {
            reasons.Add($"opening placement uses rejected wall evidence: {string.Join(",", rejectedHostWallIds.Take(12))}");
        }

        var hostWallsAreCoordinateReady = reviewHostWallIds.Length == 0 && rejectedHostWallIds.Length == 0;

        return Create(
            opening.Confidence.Value,
            opening.Confidence.Value >= 0.5 && placementReady && hostWallsAreCoordinateReady,
            calibration.HasReliableMeasurementScale,
            !placementReady || opening.Operation == OpeningOperation.Unknown || !hostWallsAreCoordinateReady,
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

    private static IEnumerable<string> OpeningWallIds(OpeningCandidate opening)
    {
        foreach (var wallId in opening.HostWallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }

        if (opening.Placement is null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(opening.Placement.HostWallId))
        {
            yield return opening.Placement.HostWallId;
        }

        foreach (var wallId in opening.Placement.AnchorWallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }
    }
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

    public static double? ScaleNullable(double? value, double? millimetersPerDrawingUnit) =>
        value.HasValue && millimetersPerDrawingUnit is > 0
            ? value.Value * millimetersPerDrawingUnit.Value
            : null;

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
