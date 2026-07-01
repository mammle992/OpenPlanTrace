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
        return Serialize(export, options);
    }

    public static string Serialize(
        PlanPlacementExport export,
        PlanPlacementJsonExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(export);

        return JsonSerializer.Serialize(export, CreateJsonOptions(options));
    }

    public static async ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanPlacementJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var export = PlanPlacementExport.From(result);
        await WriteAsync(
                export,
                stream,
                options,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask WriteAsync(
        PlanPlacementExport export,
        Stream stream,
        PlanPlacementJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);

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
    PlacementWallSetsExport WallSets,
    IReadOnlyList<PlacementRoomExport> Rooms,
    IReadOnlyList<PlacementOpeningExport> Openings,
    IReadOnlyList<PlacementObjectAggregateExport> ObjectAggregates,
    IReadOnlyList<PlacementWallGraphRepairCandidateExport> WallGraphRepairCandidates,
    PlacementWallGraphExport WallGraph,
    PlacementRoutingLayerExport RoutingLayer,
    IReadOnlyList<PlacementIssueExport> Issues)
{
    public const string CurrentSchemaVersion = "openplantrace.placement.v12";

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
        var wallTopologySpans = WallTopologySpanVisibility
            .BuildCleanPlacementTopologySpans(result)
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
            .Select(wall =>
            {
                var spans = wallTopologySpansByWallId.TryGetValue(wall.Id, out var foundSpans)
                    ? foundSpans
                    : Array.Empty<WallGraphTopologySpan>();
                var repairCandidates = wallGraphRepairCandidatesByWallId.TryGetValue(wall.Id, out var foundRepairCandidates)
                    ? foundRepairCandidates
                    : Array.Empty<WallGraphRepairCandidate>();
                wallComponentLookup.TryGetValue(wall.Id, out var wallComponent);
                wallEvidenceAssessments.TryGetValue(wall.Id, out var wallAssessment);
                var effectiveRepairCandidates = FilterWallRepairCandidatesForCleanTrustedTopology(
                    wall,
                    wallComponent,
                    wallAssessment,
                    spans,
                    repairCandidates);
                var reviewReasons = wallReviewReasons.TryGetValue(wall.Id, out var foundReviewReasons)
                    ? foundReviewReasons
                    : Array.Empty<string>();
                var effectiveReviewReasons = FilterWallReviewReasonsForSourceBackedFallback(spans, reviewReasons);

                return PlacementWallExport.From(
                    wall,
                    result.Calibration,
                    sourceLookup,
                    wallComponentLookup,
                    wallAssessment,
                    spans,
                    wallTopologySpans,
                    openingsByWallId.TryGetValue(wall.Id, out var wallOpenings) ? wallOpenings : Array.Empty<OpeningCandidate>(),
                    effectiveReviewReasons,
                    effectiveRepairCandidates);
            })
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
            wallTopologySpans,
            result.Calibration,
            sourceLookup,
            wallComponentLookup,
            wallEvidenceAssessments,
            placementWallsById);
        var placementRoutingLayer = PlacementRoutingLayerExport.From(routingLayer, result.Calibration, sourceLookup);
        var issues = PlacementIssueExport.From(result, sourceLookup, rooms, placementWallsById).ToArray();
        var wallSets = PlacementWallSetsExport.From(walls);

        var summary = PlacementSummaryExport.From(
            result,
            pages,
            surfacePatterns,
            walls,
            rooms,
            openings,
            objectAggregates,
            wallGraphRepairCandidates,
            placementRoutingLayer,
            issues);

        return new PlanPlacementExport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            PlanTraceExport.CurrentSchemaVersion,
            PlacementDocumentExport.From(result.Document),
            CoordinateSystemExport.From(result.Document.Pages, result.Calibration),
            PlacementCalibrationExport.From(result),
            PlacementQualityGateExport.From(result, summary),
            summary,
            pages,
            surfacePatterns,
            walls,
            wallSets,
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

    private static IReadOnlyList<WallGraphRepairCandidate> FilterWallRepairCandidatesForCleanTrustedTopology(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        if (repairCandidates.Count == 0
            || topologySpans.Count == 0)
        {
            return repairCandidates;
        }

        var hasSourceBackedFallback = topologySpans.Any(WallTopologySpanVisibility.IsSourceBackedFallbackTopologySpan);
        var hasTrustedExteriorShellRepairTopology =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(wall, component, assessment)
            || WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(wall, component, assessment);
        if (!hasSourceBackedFallback && !hasTrustedExteriorShellRepairTopology)
        {
            return repairCandidates;
        }

        return repairCandidates
            .Where(candidate => candidate.ImportImpact != WallGraphRepairImportImpact.TopologyImportBlocked)
            .ToArray();
    }

    private static IReadOnlyList<string> FilterWallReviewReasonsForSourceBackedFallback(
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<string> reviewReasons)
    {
        if (reviewReasons.Count == 0
            || !topologySpans.Any(WallTopologySpanVisibility.IsSourceBackedFallbackTopologySpan))
        {
            return reviewReasons;
        }

        return reviewReasons
            .Where(reason => !IsTopologyImportBlockedWallGraphRepairReason(reason))
            .ToArray();
    }

    private static bool IsTopologyImportBlockedWallGraphRepairReason(string reason) =>
        reason.Contains("wall graph repair candidate", StringComparison.OrdinalIgnoreCase)
        && reason.Contains(nameof(WallGraphRepairImportImpact.TopologyImportBlocked), StringComparison.OrdinalIgnoreCase);

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
    int RepresentedWallCount,
    int PlacementSuppressedWallCount,
    int PlacementReviewWallCount,
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
        var structuralWalls = walls.Where(IsReliabilityTrackedWall).ToArray();
        var placementReadyWallCount = CountPlacementReadyWalls(walls);
        var placementOmittedWallCount = walls.Count(wall => wall.PlacementOmission is not null);
        var representedWallCount = CountRepresentedWalls(walls);
        var placementSuppressedWallCount = CountPlacementSuppressedWalls(walls);
        var placementReviewWallCount = CountPlacementReviewWalls(walls);
        var wallTopologySpanCount = walls.Sum(wall => wall.TopologySpans.Count);
        var sourceBackedFallbackWallCount = CountSourceBackedFallbackWalls(walls);
        var sourceBackedFallbackTopologySpanCount = CountSourceBackedFallbackTopologySpans(walls);
        var wallSolidSpanCount = walls.Sum(wall => wall.SolidSpans.Count);
        var wallPlacementOmissionCounts = BuildWallPlacementOmissionCounts(walls);
        var reliabilityTrackedEntityCount = structuralWalls.Length + rooms.Count + openings.Count + objectAggregates.Count;
        var coordinateReadyEntityCount =
            structuralWalls.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + rooms.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + openings.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + objectAggregates.Count(item => item.Reliability.ReadyForCoordinatePlacement);
        var metricReadyEntityCount =
            structuralWalls.Count(item => item.Reliability.ReadyForMetricPlacement)
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
            structuralWalls.Length,
            walls.Count - structuralWalls.Length,
            placementReadyWallCount,
            placementOmittedWallCount,
            representedWallCount,
            placementSuppressedWallCount,
            placementReviewWallCount,
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
            BuildEvidence(result, reliabilityTrackedEntityCount, coordinateReadyEntityCount, metricReadyEntityCount, reviewRequiredEntityCount, placementReadyWallCount, placementOmittedWallCount, representedWallCount, placementSuppressedWallCount, placementReviewWallCount, sourceBackedFallbackTopologySpanCount, issues));
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
        walls.Count(IsPlacementReadyWall);

    internal static bool IsPlacementReadyWall(PlacementWallExport wall) =>
        wall.PlacementOmission is null && wall.Reliability.ReadyForCoordinatePlacement;

    internal static int CountRepresentedWalls(IEnumerable<PlacementWallExport> walls) =>
        walls.Count(IsRepresentedWall);

    internal static bool IsRepresentedWall(PlacementWallExport wall) =>
        wall.PlacementOmission?.Code is "duplicate_clean_topology_span" or "duplicate_wall_face";

    internal static int CountPlacementSuppressedWalls(IEnumerable<PlacementWallExport> walls) =>
        walls.Count(IsPlacementSuppressedWall);

    internal static int CountPlacementReviewWalls(IEnumerable<PlacementWallExport> walls) =>
        walls.Count(IsPlacementReviewWall);

    internal static bool IsPlacementReviewWall(PlacementWallExport wall) =>
        wall.PlacementOmission is not null
        && !IsRepresentedWall(wall)
        && !IsPlacementSuppressedWall(wall);

    internal static bool IsPlacementSuppressedWall(PlacementWallExport wall) =>
        wall.PlacementOmission?.Code is
            "rejected_wall_evidence"
            or "object_like_linework"
            or "structural_topology_excluded"
            or "opening_consumed_wall_remainder"
            or "opening_linked_isolated_fragment_suppressed"
            or "repeated_short_detail_review_required"
            or "tiny_door_adjacent_topology_suppressed";

    internal static bool IsReliabilityTrackedWall(PlacementWallExport wall)
    {
        if (wall.ExcludedFromStructuralTopology)
        {
            return false;
        }

        return wall.PlacementOmission?.Code is not (
            "duplicate_clean_topology_span"
            or "duplicate_wall_face"
            or "rejected_wall_evidence"
            or "object_like_linework"
            or "isolated_fragment"
            or "opening_consumed_wall_remainder"
            or "opening_linked_isolated_fragment_suppressed"
            or "repeated_short_detail_review_required"
            or "structural_topology_excluded"
            or "tiny_door_adjacent_topology_suppressed");
    }

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
        int representedWallCount,
        int placementSuppressedWallCount,
        int placementReviewWallCount,
        int sourceBackedFallbackTopologySpanCount,
        IReadOnlyList<PlacementIssueExport> issues)
    {
        var evidence = new List<string>
        {
            $"placement summary covers {result.Document.Pages.Count} page(s)",
            $"coordinate-ready entities {coordinateReadyEntityCount}/{reliabilityTrackedEntityCount}",
            $"metric-ready entities {metricReadyEntityCount}/{reliabilityTrackedEntityCount}",
            $"placement-ready walls {placementReadyWallCount}/{result.Walls.Count}",
            $"placement-omitted walls {placementOmittedWallCount}/{result.Walls.Count}",
            $"placement-review walls {placementReviewWallCount}/{result.Walls.Count}",
            $"placement-suppressed walls {placementSuppressedWallCount}/{result.Walls.Count}",
            $"represented duplicate/context walls {representedWallCount}/{result.Walls.Count}"
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

public sealed record PlacementWallSetsExport(
    IReadOnlyList<string> PlacementReadyWallIds,
    IReadOnlyList<string> PlacementReviewWallIds,
    IReadOnlyList<string> RepresentedWallIds,
    IReadOnlyList<string> PlacementSuppressedWallIds,
    IReadOnlyList<string> PlacementOmittedWallIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PlacementOmittedWallIdsByCode,
    IReadOnlyList<string> ReliabilityTrackedWallIds,
    IReadOnlyList<string> Evidence)
{
    public static PlacementWallSetsExport From(IReadOnlyList<PlacementWallExport> walls)
    {
        ArgumentNullException.ThrowIfNull(walls);

        var placementReadyWallIds = Ids(walls.Where(PlacementSummaryExport.IsPlacementReadyWall));
        var placementReviewWallIds = Ids(walls.Where(PlacementSummaryExport.IsPlacementReviewWall));
        var representedWallIds = Ids(walls.Where(PlacementSummaryExport.IsRepresentedWall));
        var placementSuppressedWallIds = Ids(walls.Where(PlacementSummaryExport.IsPlacementSuppressedWall));
        var placementOmittedWallIds = Ids(walls.Where(wall => wall.PlacementOmission is not null));
        var reliabilityTrackedWallIds = Ids(walls.Where(PlacementSummaryExport.IsReliabilityTrackedWall));
        var omittedByCode = walls
            .Where(wall => wall.PlacementOmission is not null)
            .GroupBy(wall => wall.PlacementOmission!.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)Ids(group),
                StringComparer.Ordinal);

        return new PlacementWallSetsExport(
            placementReadyWallIds,
            placementReviewWallIds,
            representedWallIds,
            placementSuppressedWallIds,
            placementOmittedWallIds,
            omittedByCode,
            reliabilityTrackedWallIds,
            new[]
            {
                $"placement-ready wall ids {placementReadyWallIds.Length}",
                $"placement-review wall ids {placementReviewWallIds.Length}",
                $"represented duplicate/context wall ids {representedWallIds.Length}",
                $"suppressed noise/detail/opening wall ids {placementSuppressedWallIds.Length}",
                "Use placementReadyWallIds for exact wall import; use placementReviewWallIds only after review."
            });
    }

    private static string[] Ids(IEnumerable<PlacementWallExport> walls) =>
        walls
            .Select(wall => wall.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
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
            : string.Equals(code, "placement.review.room_boundary_blocker", StringComparison.Ordinal)
                ? "placement.room_boundary.blockers_require_review"
            : string.Equals(code, "placement.review.thin_exterior_face_pair", StringComparison.Ordinal)
                ? "placement.wall_exterior.thin_face_pairs_require_review"
            : string.Equals(code, "placement.review.fragmented_short_parallel_pair", StringComparison.Ordinal)
                ? "placement.wall_pairs.fragmented_short_pairs_require_review"
            : string.Equals(code, "placement.review.covered_area_boundary", StringComparison.Ordinal)
                ? "placement.wall_exterior.covered_area_boundaries_require_review"
            : string.Equals(code, "placement.review.opening_detail_fragment", StringComparison.Ordinal)
                ? "placement.wall_opening.opening_detail_fragments_require_review"
            : string.Equals(code, "placement.review.one_endpoint_fragment", StringComparison.Ordinal)
                ? "placement.wall_fragment.one_endpoint_fragments_require_review"
            : string.Equals(code, "placement.review.fragment_geometry", StringComparison.Ordinal)
                ? "placement.wall_fragment.geometry_requires_review"
            : string.Equals(code, "placement.review.secondary_structural_wall_without_room_boundary", StringComparison.Ordinal)
                ? "placement.wall_secondary.room_boundary_support_requires_review"
            : code;

    private static bool ShouldKeepPlacementIssueInformationalForReadiness(string code) =>
        string.Equals(code, "placement.review.dense_minor_routing_detail", StringComparison.Ordinal)
        || string.Equals(code, "placement.review.rejected_strong_wall_body", StringComparison.Ordinal)
        || string.Equals(code, "placement.info.wall_graph_endpoint_gap_nonblocking", StringComparison.Ordinal);

    private static bool ShouldIncludePlacementIssueForImportReadiness(PlacementIssueExport issue) =>
        !string.Equals(issue.Code, "placement.review.dense_minor_routing_detail", StringComparison.Ordinal)
        && !string.Equals(issue.Code, "placement.review.rejected_strong_wall_body", StringComparison.Ordinal)
        && !string.Equals(issue.Code, "placement.info.wall_graph_endpoint_gap_nonblocking", StringComparison.Ordinal);
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
    int RepresentedWallCount,
    int PlacementSuppressedWallCount,
    int PlacementReviewWallCount,
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
        var pageStructuralWalls = pageWalls.Where(PlacementSummaryExport.IsReliabilityTrackedWall).ToArray();
        var placementReadyWallCount = PlacementSummaryExport.CountPlacementReadyWalls(pageWalls);
        var placementOmittedWallCount = pageWalls.Count(wall => wall.PlacementOmission is not null);
        var representedWallCount = PlacementSummaryExport.CountRepresentedWalls(pageWalls);
        var placementSuppressedWallCount = PlacementSummaryExport.CountPlacementSuppressedWalls(pageWalls);
        var placementReviewWallCount = PlacementSummaryExport.CountPlacementReviewWalls(pageWalls);
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
        var reliabilityTrackedEntityCount = pageStructuralWalls.Length + pageRooms.Length + pageOpenings.Length + pageObjectAggregates.Length;
        var coordinateReadyEntityCount =
            pageStructuralWalls.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageRooms.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageOpenings.Count(item => item.Reliability.ReadyForCoordinatePlacement)
            + pageObjectAggregates.Count(item => item.Reliability.ReadyForCoordinatePlacement);
        var metricReadyEntityCount =
            pageStructuralWalls.Count(item => item.Reliability.ReadyForMetricPlacement)
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
            pageStructuralWalls.Length,
            pageWalls.Length - pageStructuralWalls.Length,
            placementReadyWallCount,
            placementOmittedWallCount,
            representedWallCount,
            placementSuppressedWallCount,
            placementReviewWallCount,
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
    public static PlacementQualityGateExport From(
        PlanScanResult result,
        PlacementSummaryExport summary)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(summary);

        var hasCoordinateErrors = result.Diagnostics.HasErrors
            || result.Quality.Grade is PlanScanQualityGrade.Unknown or PlanScanQualityGrade.Poor;
        var baselineCoordinateReady = !hasCoordinateErrors && result.Quality.OverallConfidence.Value >= 0.5;
        var readyForCoordinatePlacement = baselineCoordinateReady && summary.ImportReadiness.ReadyForGeometryImport;
        var readyForMetricPlacement = readyForCoordinatePlacement
            && result.Calibration.HasReliableMeasurementScale
            && !result.MeasurementConsistency.HasBlockingOutliers
            && summary.ImportReadiness.ReadyForMetricImport;

        return new PlacementQualityGateExport(
            readyForCoordinatePlacement
                ? result.Quality.RequiresReview || summary.ImportReadiness.RequiresReview
                    ? "UsableWithReview"
                    : "Usable"
                : baselineCoordinateReady
                    ? "BlockedByImportReadiness"
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
            QualityEvidence(result, summary, readyForCoordinatePlacement, readyForMetricPlacement));
    }

    private static IReadOnlyList<string> QualityEvidence(
        PlanScanResult result,
        PlacementSummaryExport summary,
        bool readyForCoordinatePlacement,
        bool readyForMetricPlacement)
    {
        var evidence = new List<string>
        {
            $"Coordinate placement ready: {readyForCoordinatePlacement}.",
            $"Metric placement ready: {readyForMetricPlacement}.",
            $"Scan quality {result.Quality.Grade} with {result.Quality.OverallConfidence.Value:0.###} confidence.",
            $"Import readiness {summary.ImportReadiness.Grade} with score {summary.ImportReadiness.Score:0.###}.",
            $"Coordinate-ready import entities {summary.CoordinateReadyEntityCount}/{summary.ReliabilityTrackedEntityCount} ({summary.CoordinateReadyRatio:0.###}).",
            $"Metric-ready import entities {summary.MetricReadyEntityCount}/{summary.ReliabilityTrackedEntityCount} ({summary.MetricReadyRatio:0.###}).",
            $"Placement-ready walls {summary.PlacementReadyWallCount}/{summary.WallCount}; review walls {summary.PlacementReviewWallCount}/{summary.WallCount}; suppressed walls {summary.PlacementSuppressedWallCount}/{summary.WallCount}; represented duplicate/context walls {summary.RepresentedWallCount}/{summary.WallCount}."
        };

        if (!summary.ImportReadiness.ReadyForGeometryImport)
        {
            evidence.Add("Coordinate placement is blocked because geometry import readiness is not satisfied.");
        }

        if (!summary.ImportReadiness.ReadyForMetricImport)
        {
            evidence.Add("Metric placement is blocked because metric import readiness is not satisfied.");
        }

        foreach (var code in summary.ImportReadiness.BlockingIssueCodes)
        {
            evidence.Add($"Import readiness blocking issue: {code}.");
        }

        foreach (var action in summary.ImportReadiness.RecommendedActions)
        {
            evidence.Add($"Recommended action: {action}");
        }

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
    private const double MinDoorAdjacentCleanTopologyPieceLength = 20.0;
    private const double MaxOpeningLinkedIsolatedFragmentSuppressionLengthDrawingUnits = 140.0;
    private const double MinUnsafeCleanTopologyProjectionDriftDrawingUnits = 12.0;
    private const double MaxUnsafeCleanTopologyProjectionDriftThicknessRatio = 3.0;
    private const double MinUnsafeCleanTopologyLengthOverrunRatio = 0.10;

    public static PlacementWallOmissionExport? From(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan>? allCleanTopologySpans,
        IReadOnlyList<PlacementWallOpeningCutoutExport> openingCutouts,
        IReadOnlyList<OpeningCandidate> openings,
        bool excludedFromStructuralTopology,
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates,
        IReadOnlyList<string> reviewReasons)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(reliability);

        var unsafeCleanTopologyProjectionEvidence = BuildUnsafeCleanTopologyProjectionEvidence(wall, topologySpans);
        if (unsafeCleanTopologyProjectionEvidence.Count > 0
            && IsTrustedRoomBoundaryOpeningSplitProjectionDrift(
                wall,
                component,
                evidenceAssessment,
                topologySpans))
        {
            unsafeCleanTopologyProjectionEvidence = unsafeCleanTopologyProjectionEvidence
                .Where(item => !item.Contains("clean placement projection drift requires review", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        if (reliability.ReadyForCoordinatePlacement
            && topologySpans.Count > 0
            && !excludedFromStructuralTopology
            && unsafeCleanTopologyProjectionEvidence.Count == 0)
        {
            return null;
        }

        var repairCandidateIds = repairCandidates
            .Select(candidate => candidate.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var representedByCleanSpan = WallCleanTopologyRepresentationMatcher.FindBest(
            wall,
            component,
            evidenceAssessment,
            reliability.ReadyForCoordinatePlacement,
            topologySpans,
            allCleanTopologySpans ?? topologySpans,
            excludedFromStructuralTopology);
        var representedEvidence = representedByCleanSpan is null
            ? Array.Empty<string>()
            :
            [
                $"wall already represented by clean topology span from wall {representedByCleanSpan.Span.WallId}; "
                + "overlap "
                + representedByCleanSpan.OverlapRatio.ToString("0.###", CultureInfo.InvariantCulture)
                + "; axis distance "
                + representedByCleanSpan.AxisDistance.ToString("0.###", CultureInfo.InvariantCulture)
                + " drawing units"
            ];
        var suppressedOpeningTopologyEvidence = BuildSuppressedOpeningTopologyEvidence(
            wall,
            topologySpans,
            openingCutouts);
        var openingLinkedWallEvidence = BuildOpeningLinkedWallEvidence(wall, openings);
        var combinedEvidence = BuildEvidence(
            wall,
            component,
            evidenceAssessment,
            reliability,
            repairCandidates,
            reviewReasons,
            representedEvidence
                .Concat(suppressedOpeningTopologyEvidence)
                .Concat(openingLinkedWallEvidence)
                .Concat(unsafeCleanTopologyProjectionEvidence)
                .ToArray());
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
        var trustedRecoveredRoomBoundaryObjectLikeWall =
            WallPlacementReadinessEvaluator.IsTrustedRecoveredRoomBoundaryObjectLikeWall(
                wall,
                component,
                evidenceAssessment);
        var trustedObjectLikeLongCleanFragmentInterior =
            WallPlacementContextGuards.IsTrustedObjectLikeLongCleanFragmentInteriorWallBody(
                wall,
                component,
                evidenceAssessment);
        var trustedOpeningLinkedFilledInteriorWallBody =
            WallPlacementReadinessEvaluator.IsTrustedOpeningLinkedFilledInteriorWallBody(
                wall,
                component,
                evidenceAssessment);

        if (repairCandidates.Any(candidate => candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked))
        {
            return new PlacementWallOmissionClassification(
                "topology_import_blocked",
                "TopologyRepairBlocked",
                "Wall is omitted from clean placement topology because one or more wall graph repair candidates block import.",
                "Resolve or manually review the wall graph repair candidate before importing this wall as clean topology.");
        }

        var hasCleanTopologyRepresentation = ContainsEvidence(evidence, "already represented by clean topology span");
        if (ContainsEvidence(evidence, "duplicate wall-face")
            || ContainsEvidence(evidence, "already represented by stronger paired wall body")
            || (!hasCleanTopologyRepresentation && ContainsDuplicateWallBodyRepresentationEvidence(evidence)))
        {
            return new PlacementWallOmissionClassification(
                "duplicate_wall_face",
                "DuplicateWallFace",
                "Wall is omitted from clean placement topology because it appears to be a duplicate face of a stronger paired wall body.",
                "Use the linked stronger wall body for placement and keep this wall as review evidence only.");
        }

        if (hasCleanTopologyRepresentation)
        {
            return new PlacementWallOmissionClassification(
                "duplicate_clean_topology_span",
                "DuplicateCleanTopology",
                "Wall is omitted from clean placement topology because another clean wall span already represents the same run.",
                "Use the linked clean wall span for placement and keep this wall only as source/evidence context.");
        }

        if (ContainsEvidence(evidence, "opening cutouts fully consume wall placement run"))
        {
            return new PlacementWallOmissionClassification(
                "opening_consumed_wall_remainder",
                "OpeningConsumedWallRemainder",
                "Wall-like linework is omitted from clean placement topology because anchored opening cutouts consume the entire wall run.",
                "Use the anchored opening as placement evidence and do not import the consumed wall remainder as structural geometry.");
        }

        if (ContainsEvidence(evidence, "outdoor covered-area boundary")
            || ContainsEvidence(evidence, "unpaired outdoor covered-area boundary"))
        {
            return new PlacementWallOmissionClassification(
                "covered_area_boundary_review_required",
                "CoveredAreaBoundaryReview",
                "Wall-like linework is omitted from clean placement topology because it is near a covered-entry, terrace, canopy, or outdoor label and lacks trusted exterior shell support.",
                "Review the source PDF before importing this as an exterior wall; it may be covered-entry boundary, canopy, railing, glazing, or detail linework.");
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

        if (component?.Kind == WallGraphComponentKind.ObjectLikeIsland
            && !trustedRecoveredRoomBoundaryObjectLikeWall
            && !trustedObjectLikeLongCleanFragmentInterior)
        {
            return new PlacementWallOmissionClassification(
                "object_like_linework",
                "NonStructuralComponent",
                "Wall-like linework is omitted from clean placement topology because the graph component is object-like detail.",
                "Treat this as object/detail evidence, not a wall, unless a reviewer promotes it.");
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

        if (excludedFromStructuralTopology
            && !trustedRecoveredRoomBoundaryObjectLikeWall
            && !trustedObjectLikeLongCleanFragmentInterior
            && !trustedOpeningLinkedFilledInteriorWallBody)
        {
            return new PlacementWallOmissionClassification(
                "structural_topology_excluded",
                "NonStructuralComponent",
                "Wall is omitted from clean placement topology because the structural topology filter excluded it.",
                "Review the component evidence before using this wall for exact placement.");
        }

        if (ContainsEvidence(evidence, "tiny door-adjacent placement topology piece"))
        {
            return new PlacementWallOmissionClassification(
                "tiny_door_adjacent_topology_suppressed",
                "OpeningSplitReview",
                "Wall is omitted from clean placement topology because only tiny door-adjacent wall leftovers remained after opening cutouts were applied.",
                "Keep the raw wall and opening cutout for QA, but do not import the tiny door-adjacent remainder as a structural wall unless reviewed.");
        }

        if (!trustedOpeningLinkedFilledInteriorWallBody
            && IsSuppressedOpeningLinkedIsolatedFragment(component, wall, topologySpans, evidence))
        {
            return new PlacementWallOmissionClassification(
                "opening_linked_isolated_fragment_suppressed",
                "OpeningLinkedIsolatedFragment",
                "Wall-like linework is omitted from clean placement topology because it is a short isolated fragment linked only to detected opening evidence.",
                "Use the opening anchor and surrounding clean walls for placement; keep this fragment as source QA evidence unless a reviewer promotes it.");
        }

        if (component?.Kind == WallGraphComponentKind.IsolatedFragment
            && !trustedOpeningLinkedFilledInteriorWallBody)
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
            WallPlacementContextGuards.MainStructuralInteriorWithoutSemanticSupportReason))
        {
            return new PlacementWallOmissionClassification(
                "main_structural_semantic_support_review_required",
                "SemanticWallSupportReview",
                "Wall is omitted from clean placement topology because a risky main-structural interior candidate is not confirmed by room-boundary evidence.",
                "Review the wall against the source PDF before importing exact coordinates; promote it only when room, layer, or benchmark evidence confirms it is a real partition.");
        }

        if (ContainsEvidence(evidence, "unlayered fragment-merged wall candidate")
            && ContainsEvidence(evidence, "only one trusted structural endpoint"))
        {
            if (ContainsEvidence(evidence, "opening-linked wall fragment"))
            {
                return new PlacementWallOmissionClassification(
                    "opening_detail_fragment_review_required",
                    "OpeningDetailReview",
                    "Wall-like fragment is omitted from clean placement topology because it has only one trusted structural endpoint and is linked to a detected opening candidate.",
                    "Treat this as opening/window/door detail evidence unless review confirms it is a true wall return.");
            }

            return new PlacementWallOmissionClassification(
                "one_endpoint_fragment_review_required",
                "FragmentEndpointReview",
                "Wall is omitted from clean placement topology because an unlayered fragment-merged candidate has only one trusted structural endpoint.",
                "Review the opposite endpoint against the source PDF before importing this wall; it may be a true wall return, furniture/detail linework, or a stitched partial wall.");
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

        if (ContainsEvidence(evidence, "clean placement projection drift requires review")
            || ContainsEvidence(evidence, "clean placement source-length overrun requires review"))
        {
            return new PlacementWallOmissionClassification(
                "fragment_geometry_review",
                "FragmentGeometryReview",
                "Wall is omitted from clean placement topology because projected clean geometry drifted too far from its source wall evidence.",
                "Review the source PDF and wall graph before importing exact coordinates; this may be an over-extended or snapped-out wall span.");
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

        if (ContainsEvidence(evidence, WallPlacementReadinessEvaluator.ThinExteriorFacePairWithoutShellSupportReason))
        {
            return new PlacementWallOmissionClassification(
                "thin_exterior_face_pair_review_required",
                "ThinExteriorFacePairReview",
                "Wall is omitted from clean placement topology because a thin exterior parallel-face candidate lacks trusted exterior shell or layer support.",
                "Review the source PDF before importing this as an exterior wall; it may be covered-entry, railing, trim, glazing, or detail linework.");
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
        var hasNoisyTopologySupportedFragmentedPair = ContainsEvidence(
            evidence,
            WallPlacementReadinessEvaluator.NoisyTopologySupportedFragmentedPairReason);
        if ((!hasTopologySupportedFragmentedPairPromotion || hasNoisyTopologySupportedFragmentedPair)
            && ContainsEvidence(evidence, "unlayered parallel-face candidate")
            && (ContainsEvidence(evidence, "noisy fragmented face evidence")
                || ContainsEvidence(evidence, "weak/fragmented pair evidence")
                || hasNoisyTopologySupportedFragmentedPair))
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
        || evidence.Contains(WallPlacementReadinessEvaluator.ThinExteriorFacePairWithoutShellSupportReason, StringComparison.OrdinalIgnoreCase)
        || evidence.Contains(WallPlacementContextGuards.MainStructuralInteriorWithoutSemanticSupportReason, StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("unlayered parallel-face candidate", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("repeated short unlayered", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("short high-density unknown-layer wall/detail candidate", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("opening-linked wall fragment", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("tiny door-adjacent placement topology piece", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("unpaired outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase);

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
        AddOpeningConsumedWallEvidence(evidence, wall, cutouts);
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

    private static void AddOpeningConsumedWallEvidence(
        List<string> evidence,
        WallSegment wall,
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        var mergedCoverageLength = cutouts.Sum(cutout =>
            Math.Max(0, Math.Clamp(cutout.EndParameter, 0, 1) - Math.Clamp(cutout.StartParameter, 0, 1)));
        if (mergedCoverageLength < 0.985)
        {
            return;
        }

        evidence.Add(
            "opening cutouts fully consume wall placement run; "
            + $"coverage {mergedCoverageLength:0.###}, "
            + $"wall length {wall.CenterLine.Length:0.###} drawing units, "
            + $"opening ids {string.Join(", ", cutouts.Select(cutout => cutout.OpeningId).Distinct(StringComparer.Ordinal))}");
    }

    private static IReadOnlyList<string> BuildOpeningLinkedWallEvidence(
        WallSegment wall,
        IReadOnlyList<OpeningCandidate> openings)
    {
        if (openings.Count == 0)
        {
            return Array.Empty<string>();
        }

        var linkedOpenings = openings
            .Where(opening => OpeningCandidateReferencesWall(opening, wall.Id))
            .OrderBy(opening => opening.Id, StringComparer.Ordinal)
            .Take(4)
            .Select(opening => $"{opening.Id} ({opening.Type}/{opening.Operation}/{opening.Orientation})")
            .ToArray();
        if (linkedOpenings.Length == 0)
        {
            return Array.Empty<string>();
        }

        var suffix = openings.Count > linkedOpenings.Length
            ? $"; {openings.Count - linkedOpenings.Length} more opening candidate(s) linked"
            : string.Empty;
        return
        [
            $"opening-linked wall fragment: wall is referenced by opening candidate(s) {string.Join(", ", linkedOpenings)}{suffix}"
        ];
    }

    private static bool OpeningCandidateReferencesWall(OpeningCandidate opening, string wallId)
    {
        if (string.IsNullOrWhiteSpace(wallId))
        {
            return false;
        }

        return string.Equals(opening.Placement?.HostWallId, wallId, StringComparison.Ordinal)
            || opening.HostWallIds.Contains(wallId, StringComparer.Ordinal)
            || (opening.Placement?.AnchorWallIds.Contains(wallId, StringComparer.Ordinal) ?? false);
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

    private static bool ContainsDuplicateWallBodyRepresentationEvidence(IEnumerable<string> evidence)
    {
        var evidenceItems = evidence.ToArray();
        var isExplicitDuplicate = evidenceItems.Any(item =>
            item.Contains("duplicate wall-face", StringComparison.OrdinalIgnoreCase)
            || item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase));
        if (!isExplicitDuplicate)
        {
            return false;
        }

        return evidenceItems.Any(item =>
            item.Contains("already represented by stronger", StringComparison.OrdinalIgnoreCase)
            && item.Contains("paired wall body", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSuppressedOpeningLinkedIsolatedFragment(
        WallGraphComponent? component,
        WallSegment wall,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<string> evidence) =>
        component?.Kind == WallGraphComponentKind.IsolatedFragment
        && topologySpans.Count == 0
        && wall.DrawingLength <= MaxOpeningLinkedIsolatedFragmentSuppressionLengthDrawingUnits
        && ContainsEvidence(evidence, "opening-linked wall fragment")
        && !ContainsEvidence(evidence, "explicit room boundary support")
        && !ContainsEvidence(evidence, "geometric room boundary support")
        && !ContainsEvidence(evidence, "detected room evidence on both sides")
        && !ContainsEvidence(evidence, "room-confirmed");

    private static IReadOnlyList<string> BuildUnsafeCleanTopologyProjectionEvidence(
        WallSegment wall,
        IReadOnlyList<WallGraphTopologySpan> topologySpans)
    {
        if (topologySpans.Count == 0)
        {
            return Array.Empty<string>();
        }

        var evidence = new List<string>();
        foreach (var span in topologySpans)
        {
            if (IsTrustedSourceBackedFallbackSpan(span)
                || IsBodyAxisRecenteredCleanPlacementSpan(span))
            {
                continue;
            }

            var limit = CleanTopologyProjectionDriftLimit(wall, span);
            var maxProjectionDistance = MaxNullable(
                span.SourceWallStartProjectionDistanceDrawingUnits,
                span.SourceWallEndProjectionDistanceDrawingUnits);
            if (maxProjectionDistance is > 0
                && maxProjectionDistance.Value > limit)
            {
                evidence.Add(
                    "clean placement projection drift requires review: "
                    + $"span {span.Id} endpoint projection {Format(maxProjectionDistance.Value)} drawing units "
                    + $"exceeds {Format(limit)} drawing unit limit");
            }

            if (span.SourceWallProjectedLengthDrawingUnits is not { } sourceProjectedLength
                || sourceProjectedLength <= 0.001)
            {
                continue;
            }

            var overrun = Math.Max(0, span.DrawingLength - sourceProjectedLength);
            var overrunRatio = overrun / sourceProjectedLength;
            if (overrun > limit && overrunRatio >= MinUnsafeCleanTopologyLengthOverrunRatio)
            {
                evidence.Add(
                    "clean placement source-length overrun requires review: "
                    + $"span {span.Id} length {Format(span.DrawingLength)} drawing units exceeds source projection "
                    + $"{Format(sourceProjectedLength)} by {Format(overrun)} drawing units");
            }
        }

        return evidence.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool IsTrustedSourceBackedFallbackSpan(WallGraphTopologySpan span) =>
        WallTopologySpanVisibility.IsSourceBackedFallbackTopologySpan(span)
        && span.Evidence.Any(item =>
            item.Contains("source-backed clean placement fallback", StringComparison.OrdinalIgnoreCase));

    private static bool IsBodyAxisRecenteredCleanPlacementSpan(WallGraphTopologySpan span) =>
        ContainsEvidence(span.Evidence, "clean placement body-axis recenter");

    private static bool IsTrustedRoomBoundaryOpeningSplitProjectionDrift(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        IReadOnlyList<WallGraphTopologySpan> topologySpans)
    {
        if (component is null
            || evidenceAssessment is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is not (WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural)
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < 0.80
            || pair.OverlapRatio < 0.90
            || pair.FaceSeparation < 1.5
            || pair.FaceSeparation > 24.0
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > 72
            || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 96
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RequiresReview
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || evidenceAssessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(component.Evidence)
            .Concat(topologySpans.SelectMany(span => span.Evidence))
            .ToArray();
        if (!ContainsEvidence(evidence, "split around anchored door/opening cutouts")
            || (!ContainsEvidence(evidence, "explicit room boundary support")
                && !ContainsEvidence(evidence, "geometric room boundary support")
                && !ContainsEvidence(evidence, "shared by room adjacency boundary")))
        {
            return false;
        }

        return !ContainsEvidence(evidence, "surface pattern")
            && !ContainsEvidence(evidence, "object/fixture")
            && !ContainsEvidence(evidence, "object-like")
            && !ContainsEvidence(evidence, "repeated short detail")
            && !ContainsEvidence(evidence, "review as detail/object")
            && !ContainsEvidence(evidence, "outdoor covered-area boundary")
            && !ContainsEvidence(evidence, "unpaired outdoor covered-area boundary")
            && !ContainsEvidence(evidence, "covered-area boundary")
            && !ContainsEvidence(evidence, "covered entry")
            && !ContainsEvidence(evidence, "covered-entry")
            && !ContainsEvidence(evidence, "overbygd")
            && !ContainsEvidence(evidence, "terrace")
            && !ContainsEvidence(evidence, "railing")
            && !ContainsEvidence(evidence, "stair-like linework");
    }

    private static double CleanTopologyProjectionDriftLimit(WallSegment wall, WallGraphTopologySpan span)
    {
        var thickness = Math.Max(Math.Max(wall.Thickness, span.Thickness), 1.0);
        return Math.Max(
            MinUnsafeCleanTopologyProjectionDriftDrawingUnits,
            thickness * MaxUnsafeCleanTopologyProjectionDriftThicknessRatio);
    }

    private static double? MaxNullable(double? first, double? second)
    {
        if (first is null)
        {
            return second;
        }

        if (second is null)
        {
            return first;
        }

        return Math.Max(first.Value, second.Value);
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record PlacementWallOmissionClassification(
        string Code,
        string Category,
        string Message,
        string RecommendedAction);
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
            WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                wall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(
                wall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                wall,
                component,
                evidenceAssessment)
            && !WallPlacementContextGuards.IsTrustedObjectLikeLongCleanFragmentInteriorWallBody(
                wall,
                component,
                evidenceAssessment);
        var reliability = PlacementReliability.ForWall(wall, calibration, component, evidenceAssessment, combinedReviewReasons);
        reliability = ApplySourceBackedFallbackToReliability(reliability, topologySpans, calibration);
        var placementOmission = PlacementWallOmissionExport.From(
            wall,
            component,
            evidenceAssessment,
            reliability,
            topologySpans,
            allCleanTopologySpans,
            cutouts,
            openings,
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

    private static PlacementReliabilityExport ApplySourceBackedFallbackToReliability(
        PlacementReliabilityExport reliability,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        PlanCalibration calibration)
    {
        if (!topologySpans.Any(WallTopologySpanVisibility.IsSourceBackedFallbackTopologySpan))
        {
            return reliability;
        }

        var filteredReasons = reliability.Reasons
            .Where(reason => !reason.Contains("wall fragment geometry requires review before exact placement", StringComparison.OrdinalIgnoreCase))
            .Where(reason => !reason.Contains("no clean wall graph topology span", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return reliability with
        {
            ReadyForCoordinatePlacement = true,
            ReadyForMetricPlacement = calibration.HasReliableMeasurementScale,
            RequiresReview = filteredReasons.Length > 0,
            Reasons = filteredReasons
                .Append("coordinate placement backed by source-backed fallback topology span")
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
    IReadOnlyList<string> OpeningDominatedWallIds,
    IReadOnlyList<string> RoomSupportedFragmentWallIds,
    IReadOnlyList<string> PlacementOmittedWallIds,
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
    IReadOnlyList<PlacementWallGraphResidualEndpointOnHostCandidateExport> ResidualEndpointOnHostCandidates,
    IReadOnlyList<string> Evidence)
{
    private const double MaxCoincidentPlacementNodeDistanceDrawingUnits = 1.0;
    private const double MaxInlinePlacementGraphMergeAxisDistanceDrawingUnits = 1.5;
    private const double MaxInlinePlacementGraphMergeAxisDistanceByThicknessDrawingUnits = 4.0;
    private const double MaxExteriorShellInlinePlacementGraphMergeAxisDistanceDrawingUnits = 8.0;
    private const double MaxOverlappingStructuralPlacementGraphMergeAxisDistanceDrawingUnits = 4.0;
    private const double MaxInlinePlacementGraphMergeGapDrawingUnits = 12.0;
    private const double MaxInlinePlacementGraphStructuralMergeGapDrawingUnits = 36.0;
    private const double MaxInlinePlacementGraphSameWallGapDrawingUnits = 48.0;
    private const double MaxInlinePlacementGraphOpeningCutoutGapDrawingUnits = 96.0;
    private const double MaxInlinePlacementGraphExteriorShellMergeGapDrawingUnits = 144.0;
    private const double MaxInlinePlacementGraphMergeThicknessDeltaDrawingUnits = 4.0;
    private const double MinOverlappingStructuralPlacementGraphMergeLengthDrawingUnits = 12.0;
    private const double MinOverlappingStructuralPlacementGraphMergeOverlapRatio = 0.2;
    private const double MaxContainedPlacementGraphEdgeAxisDistanceDrawingUnits = 1.5;
    private const double MaxNearContainedPlacementGraphEdgeAxisDistanceDrawingUnits = 4.0;
    private const double MaxExteriorShellContainedPlacementGraphEdgeAxisDistanceDrawingUnits = 8.0;
    private const double MinContainedPlacementGraphEdgeOverlapRatio = 0.985;
    private const double MinNearContainedPlacementGraphEdgeOverlapRatio = 0.95;
    private const double MaxNearContainedPlacementGraphEdgeOverhangDrawingUnits = 4.0;
    private const double MinStackedPlacementGraphDuplicateOverlapRatio = 0.80;
    private const double MinStackedPlacementGraphDuplicateHostLengthRatio = 1.25;
    private const double MaxStackedPlacementGraphDuplicateOverhangDrawingUnits = 14.0;
    private const double MinPlacementEndpointOnWallSplitFraction = 0.025;
    private const double MinPlacementEndpointOnWallSplitDistanceDrawingUnits = 2.0;
    private const double MinPlacementEndpointOnWallCandidateLengthDrawingUnits = 6.0;
    private const double MaxTinyEndpointOnHostStubLengthDrawingUnits = 6.0;
    private const double MaxRedundantEndpointOnHostFragmentLengthDrawingUnits = 24.0;
    private const double MaxOpeningLikeRedundantEndpointOnHostFragmentLengthDrawingUnits = 36.0;
    private const double MinRedundantEndpointOnHostFragmentHostLengthRatio = 3.0;
    private const double MaxTinyOpeningBridgeOnHostLengthDrawingUnits = 16.0;
    private const double MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits = 2.5;
    private const double MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits = 1.0;
    private const double MaxPlacementNodeCoordinateAlignmentDistanceDrawingUnits = 2.5;
    private const double MaxPlacementGraphAxisRegularizationDistanceDrawingUnits = 2.5;
    private const double MaxPlacementSharedNodeHostSnapDistanceDrawingUnits = 3.5;
    private const double MaxExteriorShellPlacementEndpointOnWallAbsorptionDistanceDrawingUnits = 4.5;
    private const double MaxExteriorShellPlacementSharedNodeHostSnapDistanceDrawingUnits = 5.0;
    private const double MaxTrustedExteriorCornerEndpointPairSnapDistanceDrawingUnits = 8.0;
    private const double MinTrustedExteriorCornerEndpointPairSnapLengthDrawingUnits = 120.0;
    private const double MinSameAxisResidualEndpointHostLengthRatio = 1.25;
    private const double MinSameAxisResidualEndpointHostOverlapRatio = 0.8;
    private const double MinDominantPlacementGraphMergeAxisLengthDrawingUnits = 24.0;
    private const double MinDominantPlacementGraphMergeAxisCoverageRatio = 0.6;
    private const double MinTrustedExteriorDominantPlacementGraphMergeAxisCoverageRatio = 0.67;
    private const double MinDominantPlacementGraphMergeAxisLengthRatio = 2.0;
    private const double MinDominantPlacementGraphMergeAxisBandDistanceDrawingUnits = 0.75;
    private const double MaxDominantPlacementGraphMergeAxisBandDistanceDrawingUnits = 2.5;
    private const double MaxTrustedExteriorDominantPlacementGraphMergeAxisBandDistanceDrawingUnits = 4.0;

    public static PlacementWallGraphExport From(
        WallGraph graph,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments,
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById = null)
    {
        var spansByEdgeId = BuildTopologySpanLookupByWallGraphEdgeId(topologySpans);
        var rawEdges = graph.Edges
            .Select(edge => PlacementWallGraphEdgeExport.From(
                edge,
                spansByEdgeId.TryGetValue(edge.Id, out var span) ? span : null,
                calibration,
                sourceLookup,
                wallComponentLookup,
                wallEvidenceAssessments))
            .ToArray();
        var representedSpanIds = rawEdges
            .Where(edge => edge.CenterLine is not null)
            .Select(edge => edge.Id)
            .ToHashSet(StringComparer.Ordinal);
        var appendedTopologyEdges = topologySpans
            .Where(span => !representedSpanIds.Contains(span.Id))
            .Select(span => PlacementWallGraphEdgeExport.From(
                span,
                calibration,
                sourceLookup,
                wallComponentLookup,
                wallEvidenceAssessments))
            .ToArray();
        var edges = CollapseCleanPlacementGraphEdges(rawEdges.Concat(appendedTopologyEdges).ToArray());
        edges = SuppressContainedPlacementGraphEdges(edges, out var preInlineSuppressedContainedEdgeCount);
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var suppressedRawEdgeCount = edges.Count(edge => edge.CenterLine is null);
        edges = edges
            .Where(edge => edge.CenterLine is not null)
            .ToArray();
        edges = SuppressNonImportablePlacementGraphEdges(edges, placementWallsById, out var suppressedNonImportableEdgeCount);
        edges = SuppressContainedPlacementGraphEdges(edges, out var suppressedContainedEdgeCount);
        var initialAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = initialAxisRegularization.Edges;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var preNormalizationNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = preNormalizationNodeCoordinateAlignment.Edges;
        var nodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = nodeNormalization.Edges;
        var endpointAbsorption = AbsorbPlacementGraphEndpointNodesOnHostEdges(edges);
        edges = endpointAbsorption.Edges;
        var postAbsorptionEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        edges = SuppressContainedPlacementGraphEdges(edges, out var postAbsorptionSuppressedContainedEdgeCount);
        var postAbsorptionPreNormalizationNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postAbsorptionPreNormalizationNodeCoordinateAlignment.Edges;
        var postAbsorptionNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postAbsorptionNodeNormalization.Edges;
        var sharedNodeHostSnap = SnapPlacementGraphSharedNodesOntoHostEdges(edges);
        edges = sharedNodeHostSnap.Edges;
        var postSharedNodeHostSnapAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = postSharedNodeHostSnapAxisRegularization.Edges;
        var postSharedNodeHostSnapNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postSharedNodeHostSnapNormalization.Edges;
        var finalAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = finalAxisRegularization.Edges;
        var postNormalizationNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postNormalizationNodeCoordinateAlignment.Edges;
        var finalCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        edges = SuppressContainedPlacementGraphEdges(edges, out var finalSuppressedContainedEdgeCount);
        var finalPostCompactionEdgeCount = edges.Length;
        var finalNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = finalNodeNormalization.Edges;
        var finalTinyStubSuppression = SuppressTinyEndpointOnHostPlacementGraphStubs(edges);
        edges = finalTinyStubSuppression.Edges;
        var finalEndpointAbsorption = AbsorbCoincidentPlacementGraphEndpointNodesOnHostEdges(edges);
        edges = finalEndpointAbsorption.Edges;
        var postFinalEndpointAbsorptionEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postFinalEndpointAbsorptionCompactedEdgeCount = Math.Max(0, postFinalEndpointAbsorptionEdgeCount - edges.Length);
        var finalResidualEndpointSnap = SnapResidualPlacementGraphEndpointsOntoHostEdges(edges);
        edges = finalResidualEndpointSnap.Edges;
        var finalEndpointPairSnap = SnapNearbyPlacementGraphEndpointPairs(edges);
        edges = finalEndpointPairSnap.Edges;
        var postResidualSnapAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = postResidualSnapAxisRegularization.Edges;
        var postResidualSnapCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postResidualSnapCollapsedEdgeCount = Math.Max(0, postResidualSnapCompactionPreEdgeCount - edges.Length);
        edges = SuppressContainedPlacementGraphEdges(edges, out var postResidualSnapSuppressedContainedEdgeCount);
        var postFinalEndpointAbsorptionNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postFinalEndpointAbsorptionNodeCoordinateAlignment.Edges;
        var postFinalEndpointAbsorptionNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postFinalEndpointAbsorptionNodeNormalization.Edges;
        var postPairResidualEndpointSnap = SnapResidualPlacementGraphEndpointsOntoHostEdges(edges);
        edges = postPairResidualEndpointSnap.Edges;
        var postPairResidualSnapAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = postPairResidualSnapAxisRegularization.Edges;
        var postPairResidualSnapCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postPairResidualSnapCollapsedEdgeCount = Math.Max(0, postPairResidualSnapCompactionPreEdgeCount - edges.Length);
        edges = SuppressContainedPlacementGraphEdges(edges, out var postPairResidualSnapSuppressedContainedEdgeCount);
        var postPairResidualNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postPairResidualNodeCoordinateAlignment.Edges;
        var postPairResidualNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postPairResidualNodeNormalization.Edges;
        var redundantEndpointOnHostFragmentSuppression = SuppressRedundantEndpointOnHostPlacementGraphFragments(edges);
        edges = redundantEndpointOnHostFragmentSuppression.Edges;
        var postRedundantEndpointOnHostFragmentCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postRedundantEndpointOnHostFragmentCollapsedEdgeCount =
            Math.Max(0, postRedundantEndpointOnHostFragmentCompactionPreEdgeCount - edges.Length);
        edges = SuppressContainedPlacementGraphEdges(
            edges,
            out var postRedundantEndpointOnHostFragmentSuppressedContainedEdgeCount);
        var postRedundantEndpointOnHostFragmentNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postRedundantEndpointOnHostFragmentNodeCoordinateAlignment.Edges;
        var postRedundantEndpointOnHostFragmentNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postRedundantEndpointOnHostFragmentNodeNormalization.Edges;
        var postRedundantResidualEndpointSnap = SnapResidualPlacementGraphEndpointsOntoHostEdges(edges);
        edges = postRedundantResidualEndpointSnap.Edges;
        var postRedundantResidualAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = postRedundantResidualAxisRegularization.Edges;
        var postRedundantResidualCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postRedundantResidualCollapsedEdgeCount =
            Math.Max(0, postRedundantResidualCompactionPreEdgeCount - edges.Length);
        edges = SuppressContainedPlacementGraphEdges(
            edges,
            out var postRedundantResidualSuppressedContainedEdgeCount);
        var postRedundantResidualNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postRedundantResidualNodeCoordinateAlignment.Edges;
        var postRedundantResidualNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postRedundantResidualNodeNormalization.Edges;
        var finalTinyOpeningBridgeSuppression = SuppressTinyOpeningBridgeEdgesOnHostWalls(edges);
        edges = finalTinyOpeningBridgeSuppression.Edges;
        var postBridgeResidualEndpointSnap = SnapResidualPlacementGraphEndpointsOntoHostEdges(edges);
        edges = postBridgeResidualEndpointSnap.Edges;
        var postBridgeResidualAxisRegularization = RegularizeAxisAlignedPlacementGraphEdges(edges);
        edges = postBridgeResidualAxisRegularization.Edges;
        var postBridgeResidualCompactionPreEdgeCount = edges.Length;
        edges = CollapseInlineCollinearPlacementGraphEdges(edges);
        var postBridgeResidualCollapsedEdgeCount =
            Math.Max(0, postBridgeResidualCompactionPreEdgeCount - edges.Length);
        edges = SuppressContainedPlacementGraphEdges(
            edges,
            out var postBridgeResidualSuppressedContainedEdgeCount);
        var postBridgeResidualNodeCoordinateAlignment = AlignPlacementGraphEdgesToCanonicalNodePositions(edges);
        edges = postBridgeResidualNodeCoordinateAlignment.Edges;
        var postBridgeResidualNodeNormalization = NormalizePlacementGraphNodeReferencesFromGeometry(edges);
        edges = postBridgeResidualNodeNormalization.Edges;
        var residualEndpointOnHostSummary = SummarizeResidualPlacementGraphEndpointOnHostEdges(edges);
        var finalCompactedEdgeCount = Math.Max(0, finalCompactionPreEdgeCount - finalPostCompactionEdgeCount);
        var alignedEndpointCount = preNormalizationNodeCoordinateAlignment.AlignedEndpointCount
            + postAbsorptionPreNormalizationNodeCoordinateAlignment.AlignedEndpointCount
            + postNormalizationNodeCoordinateAlignment.AlignedEndpointCount
            + postFinalEndpointAbsorptionNodeCoordinateAlignment.AlignedEndpointCount
            + postPairResidualNodeCoordinateAlignment.AlignedEndpointCount
            + postRedundantEndpointOnHostFragmentNodeCoordinateAlignment.AlignedEndpointCount
            + postRedundantResidualNodeCoordinateAlignment.AlignedEndpointCount
            + postBridgeResidualNodeCoordinateAlignment.AlignedEndpointCount;
        var alignedNodeCount = preNormalizationNodeCoordinateAlignment.AlignedNodeCount
            + postAbsorptionPreNormalizationNodeCoordinateAlignment.AlignedNodeCount
            + postNormalizationNodeCoordinateAlignment.AlignedNodeCount
            + postFinalEndpointAbsorptionNodeCoordinateAlignment.AlignedNodeCount
            + postPairResidualNodeCoordinateAlignment.AlignedNodeCount
            + postRedundantEndpointOnHostFragmentNodeCoordinateAlignment.AlignedNodeCount
            + postRedundantResidualNodeCoordinateAlignment.AlignedNodeCount
            + postBridgeResidualNodeCoordinateAlignment.AlignedNodeCount;
        var regularizedAxisEdgeCount = initialAxisRegularization.RegularizedEdgeCount
            + postSharedNodeHostSnapAxisRegularization.RegularizedEdgeCount
            + finalAxisRegularization.RegularizedEdgeCount
            + postResidualSnapAxisRegularization.RegularizedEdgeCount
            + postPairResidualSnapAxisRegularization.RegularizedEdgeCount
            + postRedundantResidualAxisRegularization.RegularizedEdgeCount
            + postBridgeResidualAxisRegularization.RegularizedEdgeCount;
        var postAbsorptionCompactedEdgeCount = Math.Max(0, postAbsorptionEdgeCount - edges.Length);
        var nodes = BuildPlacementWallGraphNodes(graph, edges, calibration);
        var components = graph.Components
            .Select(component => PlacementWallGraphComponentExport.From(component, calibration, sourceLookup, edges))
            .ToArray();
        var repairCandidateIds = graph.RepairCandidates.Select(candidate => candidate.Id).ToArray();
        var summary = PlacementWallGraphSummaryExport.From(graph, nodes, edges);
        var residualEndpointOnHostCandidates = residualEndpointOnHostSummary.Candidates;
        var evidence = new[]
        {
            $"placement wall graph exports {nodes.Count} cleaned node(s), {edges.Length} edge(s), and {components.Length} component(s)",
            $"placement wall graph collapsed {Math.Max(0, rawEdges.Length + appendedTopologyEdges.Length - suppressedRawEdgeCount - edges.Length)} raw edge fragment(s) into clean topology edges",
            $"placement wall graph appended {appendedTopologyEdges.Length} recovered/source-backed clean topology edge(s)",
            $"placement wall graph suppressed {suppressedRawEdgeCount} raw non-placement edge(s) without clean geometry",
            $"placement wall graph suppressed {suppressedNonImportableEdgeCount} non-importable edge(s) blocked by wall placement omission/exclusion gates",
            $"placement wall graph suppressed {preInlineSuppressedContainedEdgeCount} pre-inline stacked/contained duplicate edge(s)",
            $"placement wall graph suppressed {suppressedContainedEdgeCount} contained duplicate clean edge(s)",
            $"placement wall graph split {nodeNormalization.SplitNodeReferenceCount} reused node reference(s) whose clean endpoint coordinates no longer coincide",
            $"placement wall graph absorbed {endpointAbsorption.AbsorbedEndpointCount} endpoint node(s) onto host wall edge(s) and split {endpointAbsorption.SplitHostEdgeCount} host edge(s) at true junctions",
            $"placement wall graph compacted {postAbsorptionCompactedEdgeCount} post-junction wall fragment(s) back into long straight run(s)",
            $"placement wall graph suppressed {postAbsorptionSuppressedContainedEdgeCount} post-junction contained duplicate edge(s)",
            $"placement wall graph split {postAbsorptionNodeNormalization.SplitNodeReferenceCount} post-junction reused node reference(s) after long-run compaction",
            $"placement wall graph snapped {sharedNodeHostSnap.SnappedNodeCount} shared node(s), {sharedNodeHostSnap.SnappedEndpointCount} endpoint coordinate(s), and split {sharedNodeHostSnap.SplitHostEdgeCount} host edge(s) at shared junctions",
            $"placement wall graph split {postSharedNodeHostSnapNormalization.SplitNodeReferenceCount} reused node reference(s) after shared-node host snapping",
            $"placement wall graph regularized {regularizedAxisEdgeCount} near-axis edge(s) onto canonical straight axes",
            $"placement wall graph aligned {alignedEndpointCount} endpoint coordinate(s) across {alignedNodeCount} canonical node(s)",
            $"placement wall graph final-compacted {finalCompactedEdgeCount} aligned wall fragment(s) and suppressed {finalSuppressedContainedEdgeCount} final contained duplicate edge(s)",
            $"placement wall graph split {finalNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after final long-run compaction",
            $"placement wall graph suppressed {finalTinyStubSuppression.SuppressedStubCount} tiny endpoint-on-host stub edge(s) before final endpoint cleanup",
            $"placement wall graph final-cleaned {finalEndpointAbsorption.AbsorbedEndpointCount} coincident endpoint node(s) already lying on host wall edge(s) and split {finalEndpointAbsorption.SplitHostEdgeCount} host edge(s)",
            $"placement wall graph rejoined {postFinalEndpointAbsorptionCompactedEdgeCount} final host wall fragment(s) after endpoint cleanup to keep long walls compact",
            $"placement wall graph snapped {finalResidualEndpointSnap.SnappedEndpointCount} small residual endpoint(s) onto host wall runs without splitting long host edges",
            $"placement wall graph snapped {finalEndpointPairSnap.SnappedEndpointCount} nearby endpoint coordinate(s) across {finalEndpointPairSnap.SnappedPairCount} structural endpoint pair(s)",
            $"placement wall graph post-snap compacted {postResidualSnapCollapsedEdgeCount} aligned wall fragment(s) and suppressed {postResidualSnapSuppressedContainedEdgeCount} contained duplicate edge(s)",
            $"placement wall graph split {postFinalEndpointAbsorptionNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after final endpoint-on-wall cleanup",
            $"placement wall graph post-pair snapped {postPairResidualEndpointSnap.SnappedEndpointCount} residual endpoint(s) onto host wall runs",
            $"placement wall graph post-pair compacted {postPairResidualSnapCollapsedEdgeCount} aligned wall fragment(s) and suppressed {postPairResidualSnapSuppressedContainedEdgeCount} contained duplicate edge(s)",
            $"placement wall graph split {postPairResidualNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after post-pair residual cleanup",
            $"placement wall graph suppressed {redundantEndpointOnHostFragmentSuppression.SuppressedFragmentCount} redundant endpoint-on-host fragment(s) after residual cleanup",
            $"placement wall graph post-redundant cleanup compacted {postRedundantEndpointOnHostFragmentCollapsedEdgeCount} aligned wall fragment(s) and suppressed {postRedundantEndpointOnHostFragmentSuppressedContainedEdgeCount} contained duplicate edge(s)",
            $"placement wall graph split {postRedundantEndpointOnHostFragmentNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after redundant endpoint-on-host cleanup",
            $"placement wall graph post-redundant snapped {postRedundantResidualEndpointSnap.SnappedEndpointCount} residual endpoint(s) onto host wall runs",
            $"placement wall graph post-redundant residual compacted {postRedundantResidualCollapsedEdgeCount} aligned wall fragment(s) and suppressed {postRedundantResidualSuppressedContainedEdgeCount} contained duplicate edge(s)",
            $"placement wall graph split {postRedundantResidualNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after final residual cleanup",
            $"placement wall graph suppressed {finalTinyOpeningBridgeSuppression.SuppressedBridgeCount} tiny opening bridge edge(s) whose endpoints already land on host wall runs",
            $"placement wall graph post-bridge snapped {postBridgeResidualEndpointSnap.SnappedEndpointCount} residual endpoint(s) onto host wall runs",
            $"placement wall graph post-bridge compacted {postBridgeResidualCollapsedEdgeCount} aligned wall fragment(s) and suppressed {postBridgeResidualSuppressedContainedEdgeCount} contained duplicate edge(s)",
            $"placement wall graph split {postBridgeResidualNodeNormalization.SplitNodeReferenceCount} reused node reference(s) after post-bridge residual cleanup",
            "placement wall graph residual endpoint-on-host-wall candidates after cleanup: "
            + $"{residualEndpointOnHostSummary.CandidateEndpointCount} total, "
            + $"{residualEndpointOnHostSummary.CoincidentCandidateEndpointCount} coincident, "
            + $"{residualEndpointOnHostSummary.SameAxisCandidateEndpointCount} same-axis, "
            + $"{residualEndpointOnHostSummary.PerpendicularCandidateEndpointCount} perpendicular, "
            + $"max distance {residualEndpointOnHostSummary.MaxDistance:0.###} drawing units",
            $"placement wall graph suppressed {Math.Max(0, graph.Nodes.Count - nodes.Count)} raw node(s) after clean topology endpoint normalization",
            $"repair candidate ids exported: {repairCandidateIds.Length}"
        };

        return new PlacementWallGraphExport(
            summary,
            nodes,
            edges,
            components,
            repairCandidateIds,
            residualEndpointOnHostCandidates,
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

    private static PlacementWallGraphEdgeExport[] CollapseCleanPlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return edges.ToArray();
        }

        return edges
            .GroupBy(edge => edge.CenterLine is null ? $"raw:{edge.Id}" : $"clean:{edge.Id}", StringComparer.Ordinal)
            .Select(group => group.Count() == 1 || group.First().CenterLine is null
                ? group.First()
                : MergeCleanPlacementGraphEdges(group.ToArray()))
            .OrderBy(edge => edge.PageNumber)
            .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
            .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static PlacementWallGraphEdgeExport MergeCleanPlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        var primary = edges
            .OrderBy(edge => edge.Id.Length)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .First();
        var sourceEdgeIds = edges
            .SelectMany(edge => edge.SourceWallGraphEdgeIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidence = edges
            .SelectMany(edge => edge.Evidence)
            .Append($"placement wall graph edge collapsed from {edges.Count} source wall graph edge fragment(s)")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return primary with
        {
            Confidence = edges.Min(edge => edge.Confidence),
            SourcePrimitiveIds = edges
                .SelectMany(edge => edge.SourcePrimitiveIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceLayers = edges
                .SelectMany(edge => edge.SourceLayers)
                .Where(layer => !string.IsNullOrWhiteSpace(layer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceWallGraphEdgeIds = sourceEdgeIds,
            Evidence = evidence
        };
    }

    private static PlacementWallGraphEdgeExport[] CollapseInlineCollinearPlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return edges.ToArray();
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return edges.ToArray();
        }

        var collapsedIndexes = new HashSet<int>();
        var mergedEdges = new List<PlacementWallGraphEdgeExport>();
        var nodeIncidentCounts = BuildPlacementGraphMergeNodeIncidentCounts(spans);
        foreach (var group in spans.GroupBy(span => span.GroupKey))
        {
            var clusters = BuildPlacementGraphMergeClusters(group, nodeIncidentCounts);
            foreach (var cluster in clusters.Where(cluster => cluster.Count > 1))
            {
                mergedEdges.Add(MergeInlineCollinearPlacementGraphEdges(cluster));
                foreach (var span in cluster)
                {
                    collapsedIndexes.Add(span.Index);
                }
            }
        }

        if (mergedEdges.Count == 0)
        {
            return edges.ToArray();
        }

        return edges
            .Where((_, index) => !collapsedIndexes.Contains(index))
            .Concat(mergedEdges)
            .OrderBy(edge => edge.PageNumber)
            .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
            .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static PlacementWallGraphEdgeExport[] SuppressNonImportablePlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById,
        out int suppressedCount)
    {
        suppressedCount = 0;
        if (edges.Count == 0 || placementWallsById is null || placementWallsById.Count == 0)
        {
            return edges.ToArray();
        }

        var kept = new List<PlacementWallGraphEdgeExport>(edges.Count);
        foreach (var edge in edges)
        {
            if (ShouldSuppressNonImportablePlacementGraphEdge(edge, placementWallsById))
            {
                suppressedCount++;
                continue;
            }

            kept.Add(edge);
        }

        return kept
            .OrderBy(edge => edge.PageNumber)
            .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
            .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ShouldSuppressNonImportablePlacementGraphEdge(
        PlacementWallGraphEdgeExport edge,
        IReadOnlyDictionary<string, PlacementWallExport> placementWallsById)
    {
        if (string.IsNullOrWhiteSpace(edge.WallId)
            || !placementWallsById.TryGetValue(edge.WallId, out var wall))
        {
            return false;
        }

        if (PlacementSummaryExport.IsPlacementReadyWall(wall))
        {
            return false;
        }

        if (wall.ExcludedFromStructuralTopology)
        {
            return true;
        }

        return wall.PlacementOmission?.Code is
            "covered_area_boundary_review_required"
            or "duplicate_clean_topology_span"
            or "duplicate_wall_face"
            or "isolated_fragment"
            or "object_like_linework"
            or "opening_linked_isolated_fragment_suppressed"
            or "rejected_wall_evidence"
            or "secondary_object_linework_without_room_boundary_support"
            or "secondary_over_sourced_detail_linework"
            or "structural_topology_excluded"
            or "tiny_door_adjacent_topology_suppressed";
    }

    private static PlacementWallGraphEdgeExport[] SuppressContainedPlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        out int suppressedCount)
    {
        suppressedCount = 0;
        if (edges.Count <= 1)
        {
            return edges.ToArray();
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return edges.ToArray();
        }

        var axisAlignedIndexes = spans
            .Select(span => span.Index)
            .ToHashSet();
        var kept = new List<PlacementGraphMergeSpan>();
        foreach (var span in spans
            .OrderByDescending(span => span.Length)
            .ThenBy(span => span.Axis)
            .ThenBy(span => span.Start)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal))
        {
            var representativeIndex = FindContainedPlacementGraphEdgeRepresentative(span, kept, out var overlapRatio, out var axisDistance);
            if (representativeIndex >= 0)
            {
                kept[representativeIndex] = AddContainedPlacementGraphEdgeEvidence(
                    kept[representativeIndex],
                    span,
                    overlapRatio,
                    axisDistance);
                suppressedCount++;
                continue;
            }

            kept.Add(span);
        }

        if (suppressedCount == 0)
        {
            return edges.ToArray();
        }

        return edges
            .Where((_, index) => !axisAlignedIndexes.Contains(index))
            .Concat(kept.Select(span => span.Edge))
            .OrderBy(edge => edge.PageNumber)
            .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
            .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
            .ThenBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static int FindContainedPlacementGraphEdgeRepresentative(
        PlacementGraphMergeSpan candidate,
        IReadOnlyList<PlacementGraphMergeSpan> kept,
        out double overlapRatio,
        out double axisDistance)
    {
        overlapRatio = 0;
        axisDistance = 0;
        var bestIndex = -1;
        var bestScore = double.PositiveInfinity;
        for (var index = 0; index < kept.Count; index++)
        {
            var representative = kept[index];
            if (representative.Edge.PageNumber != candidate.Edge.PageNumber
                || representative.Orientation != candidate.Orientation)
            {
                continue;
            }

            var candidateAxisDistance = Math.Abs(candidate.Axis - representative.Axis);
            if (candidateAxisDistance > ContainedPlacementGraphEdgeAxisTolerance(candidate, representative))
            {
                continue;
            }

            var overlap = Math.Min(candidate.End, representative.End) - Math.Max(candidate.Start, representative.Start);
            if (overlap <= 0.001)
            {
                continue;
            }

            var candidateOverlapRatio = overlap / Math.Max(candidate.Length, 0.001);
            if (!IsContainedPlacementGraphOverlapAcceptable(
                    candidate,
                    representative,
                    candidateOverlapRatio))
            {
                continue;
            }

            var score = candidateAxisDistance + Math.Max(0, candidate.End - representative.End) + Math.Max(0, representative.Start - candidate.Start);
            if (score < bestScore)
            {
                bestScore = score;
                bestIndex = index;
                overlapRatio = candidateOverlapRatio;
                axisDistance = candidateAxisDistance;
            }
        }

        return bestIndex;
    }

    private static double ContainedPlacementGraphEdgeAxisTolerance(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan representative)
    {
        var thicknessTolerance = Math.Max(
            candidate.Edge.ThicknessDrawingUnits,
            representative.Edge.ThicknessDrawingUnits);
        if (IsTrustedExteriorShellPlacementGraphMergeContinuation(candidate.Edge)
            && IsTrustedExteriorShellPlacementGraphMergeContinuation(representative.Edge))
        {
            return Math.Clamp(
                thicknessTolerance * 0.75,
                MaxNearContainedPlacementGraphEdgeAxisDistanceDrawingUnits,
                MaxExteriorShellContainedPlacementGraphEdgeAxisDistanceDrawingUnits);
        }

        if (!StructuralPlacementGraphKindsMatch(candidate.Edge, representative.Edge))
        {
            return MaxContainedPlacementGraphEdgeAxisDistanceDrawingUnits;
        }

        return Math.Clamp(
            thicknessTolerance,
            MaxContainedPlacementGraphEdgeAxisDistanceDrawingUnits,
            MaxNearContainedPlacementGraphEdgeAxisDistanceDrawingUnits);
    }

    private static bool IsContainedPlacementGraphOverlapAcceptable(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan representative,
        double candidateOverlapRatio)
    {
        if (candidateOverlapRatio >= MinContainedPlacementGraphEdgeOverlapRatio)
        {
            return true;
        }

        if (candidateOverlapRatio >= MinNearContainedPlacementGraphEdgeOverlapRatio
            && StructuralPlacementGraphKindsMatch(candidate.Edge, representative.Edge))
        {
            var nearContainedOverhang = Math.Max(0, representative.Start - candidate.Start)
                + Math.Max(0, candidate.End - representative.End);
            return nearContainedOverhang <= NearContainedPlacementGraphEdgeOverhangTolerance(candidate, representative);
        }

        return IsStackedPlacementGraphDuplicateOverlapAcceptable(
            candidate,
            representative,
            candidateOverlapRatio);
    }

    private static bool IsStackedPlacementGraphDuplicateOverlapAcceptable(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan representative,
        double candidateOverlapRatio)
    {
        if (candidateOverlapRatio < MinStackedPlacementGraphDuplicateOverlapRatio
            || representative.Length < Math.Max(
                MinDominantPlacementGraphMergeAxisLengthDrawingUnits,
                candidate.Length * MinStackedPlacementGraphDuplicateHostLengthRatio)
            || HasPlacementGraphDetailOrSurfaceEvidence(representative.Edge))
        {
            return false;
        }

        var overhang = Math.Max(0, representative.Start - candidate.Start)
            + Math.Max(0, candidate.End - representative.End);
        if (overhang > StackedPlacementGraphDuplicateOverhangTolerance(candidate, representative))
        {
            return false;
        }

        if (!IsStructuralPlacementGraphMergeContinuation(representative.Edge)
            && !representative.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence))
        {
            return false;
        }

        if (HasStrongPlacementGraphBoundaryEvidence(candidate.Edge)
            && !StructuralPlacementGraphKindsMatch(candidate.Edge, representative.Edge)
            && !IsSameTrustedExteriorPlacementGraphDuplicate(candidate.Edge, representative.Edge))
        {
            return false;
        }

        return string.Equals(candidate.Edge.WallId, representative.Edge.WallId, StringComparison.Ordinal)
            || StructuralPlacementGraphKindsMatch(candidate.Edge, representative.Edge)
            || IsSameTrustedExteriorPlacementGraphDuplicate(candidate.Edge, representative.Edge)
            || HasPlacementGraphDetailOrSurfaceEvidence(candidate.Edge)
            || candidate.Edge.Evidence.Any(item =>
                item.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || item.Contains("residual", StringComparison.OrdinalIgnoreCase)
                || item.Contains("stacked", StringComparison.OrdinalIgnoreCase)
                || item.Contains("fragment", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSameTrustedExteriorPlacementGraphDuplicate(
        PlacementWallGraphEdgeExport candidate,
        PlacementWallGraphEdgeExport representative) =>
        IsTrustedExteriorShellPlacementGraphMergeContinuation(candidate)
        && IsTrustedExteriorShellPlacementGraphMergeContinuation(representative);

    private static double StackedPlacementGraphDuplicateOverhangTolerance(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan representative)
    {
        var thicknessTolerance = Math.Max(
            candidate.Edge.ThicknessDrawingUnits,
            representative.Edge.ThicknessDrawingUnits) * 2.0;
        return Math.Clamp(
            thicknessTolerance,
            MaxNearContainedPlacementGraphEdgeOverhangDrawingUnits,
            MaxStackedPlacementGraphDuplicateOverhangDrawingUnits);
    }

    private static double NearContainedPlacementGraphEdgeOverhangTolerance(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan representative)
    {
        var thicknessTolerance = Math.Max(
            candidate.Edge.ThicknessDrawingUnits,
            representative.Edge.ThicknessDrawingUnits) / 2.0;
        return Math.Clamp(
            thicknessTolerance,
            MaxContainedPlacementGraphEdgeAxisDistanceDrawingUnits,
            MaxNearContainedPlacementGraphEdgeOverhangDrawingUnits);
    }

    private static bool StructuralPlacementGraphKindsMatch(
        PlacementWallGraphEdgeExport first,
        PlacementWallGraphEdgeExport second) =>
        IsStructuralPlacementGraphMergeContinuation(first)
        && IsStructuralPlacementGraphMergeContinuation(second)
        && string.Equals(
            PlacementGraphMergeComponentKindGroup(first),
            PlacementGraphMergeComponentKindGroup(second),
            StringComparison.Ordinal);

    private static PlacementGraphMergeSpan AddContainedPlacementGraphEdgeEvidence(
        PlacementGraphMergeSpan representative,
        PlacementGraphMergeSpan contained,
        double overlapRatio,
        double axisDistance)
    {
        var edge = representative.Edge;
        var containedEdge = contained.Edge;
        var evidence = edge.Evidence
            .Concat(containedEdge.Evidence)
            .Append(
                "placement wall graph contained-edge suppression: "
                + $"suppressed {containedEdge.Id} into {edge.Id}; overlap {overlapRatio:0.###}, "
                + $"axis distance {axisDistance:0.###} drawing units")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return representative with
        {
            Edge = edge with
            {
                Confidence = Math.Min(edge.Confidence, containedEdge.Confidence),
                SourcePrimitiveIds = edge.SourcePrimitiveIds
                    .Concat(containedEdge.SourcePrimitiveIds)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                SourceLayers = edge.SourceLayers
                    .Concat(containedEdge.SourceLayers)
                    .Where(layer => !string.IsNullOrWhiteSpace(layer))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                SourceWallGraphEdgeIds = edge.SourceWallGraphEdgeIds
                    .Concat(containedEdge.SourceWallGraphEdgeIds)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Evidence = evidence
            }
        };
    }

    private static IReadOnlyDictionary<string, int> BuildPlacementGraphMergeNodeIncidentCounts(
        IReadOnlyList<PlacementGraphMergeSpan> spans)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var nodeId in spans
            .SelectMany(span => new[] { span.StartNodeId, span.EndNodeId })
            .Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            counts[nodeId] = counts.GetValueOrDefault(nodeId) + 1;
        }

        return counts;
    }

    private static IReadOnlyList<IReadOnlyList<PlacementGraphMergeSpan>> BuildPlacementGraphMergeClusters(
        IEnumerable<PlacementGraphMergeSpan> spans,
        IReadOnlyDictionary<string, int> nodeIncidentCounts)
    {
        var clusters = new List<List<PlacementGraphMergeSpan>>();
        foreach (var span in spans
            .OrderBy(span => span.Axis)
            .ThenBy(span => span.Start)
            .ThenBy(span => span.End)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal))
        {
            var cluster = clusters.LastOrDefault(candidate => CanMergeIntoPlacementGraphCluster(candidate, span, nodeIncidentCounts));
            if (cluster is null)
            {
                clusters.Add([span]);
                continue;
            }

            cluster.Add(span);
        }

        return clusters;
    }

    private static bool CanMergeIntoPlacementGraphCluster(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        IReadOnlyDictionary<string, int> nodeIncidentCounts)
    {
        if (TouchesProtectedPlacementGraphJunction(cluster, span, nodeIncidentCounts)
            && !IsSameSourceWallPlacementGraphRun(cluster, span)
            && !IsTrustedExteriorShellPlacementGraphRun(cluster, span)
            && !IsTrustedStructuralInlineProtectedJunctionMergeRun(cluster, span, nodeIncidentCounts))
        {
            return false;
        }

        var axis = WeightedAxis(cluster);
        var axisDistance = Math.Abs(span.Axis - axis);
        if (axisDistance > PlacementGraphMergeAxisTolerance(cluster, span)
            && !CanUseOverlappingStructuralPlacementGraphMergeAxisTolerance(cluster, span, axisDistance)
            && !CanUseShortOverlapStructuralPlacementGraphContinuationAxisTolerance(cluster, span, axisDistance))
        {
            return false;
        }

        var minThickness = cluster.Min(item => item.Edge.ThicknessDrawingUnits);
        var maxThickness = cluster.Max(item => item.Edge.ThicknessDrawingUnits);
        minThickness = Math.Min(minThickness, span.Edge.ThicknessDrawingUnits);
        maxThickness = Math.Max(maxThickness, span.Edge.ThicknessDrawingUnits);
        if (maxThickness - minThickness > MaxInlinePlacementGraphMergeThicknessDeltaDrawingUnits)
        {
            return false;
        }

        var clusterStart = cluster.Min(item => item.Start);
        var clusterEnd = cluster.Max(item => item.End);
        var maxGap = PlacementGraphMergeGapTolerance(cluster, span);
        return span.Start <= clusterEnd + maxGap
            && span.End >= clusterStart - maxGap;
    }

    private static bool CanUseOverlappingStructuralPlacementGraphMergeAxisTolerance(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        double axisDistance)
    {
        if (cluster.Count == 0
            || !cluster.All(item => IsStructuralPlacementGraphMergeContinuation(item.Edge))
            || !IsStructuralPlacementGraphMergeContinuation(span.Edge)
            || HasPlacementGraphDetailOrSurfaceEvidence(span.Edge)
            || cluster.Any(item => HasPlacementGraphDetailOrSurfaceEvidence(item.Edge)))
        {
            return false;
        }

        var maxThickness = cluster
            .Select(item => item.Edge.ThicknessDrawingUnits)
            .Append(span.Edge.ThicknessDrawingUnits)
            .DefaultIfEmpty(0)
            .Max();
        var tolerance = Math.Clamp(
            maxThickness * 0.65,
            MaxInlinePlacementGraphMergeAxisDistanceDrawingUnits,
            MaxOverlappingStructuralPlacementGraphMergeAxisDistanceDrawingUnits);
        if (axisDistance > tolerance)
        {
            return false;
        }

        var clusterStart = cluster.Min(item => item.Start);
        var clusterEnd = cluster.Max(item => item.End);
        var clusterLength = Math.Max(0, clusterEnd - clusterStart);
        var overlap = Math.Min(span.End, clusterEnd) - Math.Max(span.Start, clusterStart);
        if (overlap < MinOverlappingStructuralPlacementGraphMergeLengthDrawingUnits)
        {
            return false;
        }

        var overlapRatio = overlap / Math.Max(Math.Min(span.Length, clusterLength), 0.001);
        return overlapRatio >= MinOverlappingStructuralPlacementGraphMergeOverlapRatio;
    }

    private static bool CanUseShortOverlapStructuralPlacementGraphContinuationAxisTolerance(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        double axisDistance)
    {
        if (cluster.Count == 0
            || !cluster.All(item => IsStructuralPlacementGraphMergeContinuation(item.Edge))
            || !IsStructuralPlacementGraphMergeContinuation(span.Edge)
            || HasPlacementGraphDetailOrSurfaceEvidence(span.Edge)
            || cluster.Any(item => HasPlacementGraphDetailOrSurfaceEvidence(item.Edge)))
        {
            return false;
        }

        var maxThickness = cluster
            .Select(item => item.Edge.ThicknessDrawingUnits)
            .Append(span.Edge.ThicknessDrawingUnits)
            .DefaultIfEmpty(0)
            .Max();
        var tolerance = Math.Clamp(
            maxThickness * 0.60,
            MaxInlinePlacementGraphMergeAxisDistanceDrawingUnits,
            MaxOverlappingStructuralPlacementGraphMergeAxisDistanceDrawingUnits);
        if (axisDistance > tolerance)
        {
            return false;
        }

        var clusterStart = cluster.Min(item => item.Start);
        var clusterEnd = cluster.Max(item => item.End);
        var clusterLength = Math.Max(0, clusterEnd - clusterStart);
        var shorterLength = Math.Min(span.Length, clusterLength);
        if (shorterLength < MinOverlappingStructuralPlacementGraphMergeLengthDrawingUnits)
        {
            return false;
        }

        var overlap = Math.Min(span.End, clusterEnd) - Math.Max(span.Start, clusterStart);
        if (overlap <= MaxCoincidentPlacementNodeDistanceDrawingUnits)
        {
            return false;
        }

        var maxShortOverlap = Math.Max(
            MinOverlappingStructuralPlacementGraphMergeLengthDrawingUnits,
            maxThickness * 2.0);
        return overlap <= maxShortOverlap
            && overlap / Math.Max(shorterLength, 0.001) <= 0.25;
    }

    private static bool TouchesProtectedPlacementGraphJunction(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        IReadOnlyDictionary<string, int> nodeIncidentCounts) =>
        cluster.Any(existing =>
            IsProtectedPlacementGraphMergeNode(existing.EndNodeId, span.StartNodeId, nodeIncidentCounts)
            || IsProtectedPlacementGraphMergeNode(existing.StartNodeId, span.EndNodeId, nodeIncidentCounts)
            || IsProtectedPlacementGraphMergeNode(existing.StartNodeId, span.StartNodeId, nodeIncidentCounts)
            || IsProtectedPlacementGraphMergeNode(existing.EndNodeId, span.EndNodeId, nodeIncidentCounts));

    private static bool IsProtectedPlacementGraphMergeNode(
        string firstNodeId,
        string secondNodeId,
        IReadOnlyDictionary<string, int> nodeIncidentCounts) =>
        !string.IsNullOrWhiteSpace(firstNodeId)
        && string.Equals(firstNodeId, secondNodeId, StringComparison.Ordinal)
        && nodeIncidentCounts.GetValueOrDefault(firstNodeId) > 2;

    private static double PlacementGraphMergeAxisTolerance(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span)
    {
        var maxThickness = cluster
            .Select(item => item.Edge.ThicknessDrawingUnits)
            .Append(span.Edge.ThicknessDrawingUnits)
            .DefaultIfEmpty(0)
            .Max();
        if (cluster.All(item => IsTrustedExteriorShellPlacementGraphMergeContinuation(item.Edge))
            && IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
        {
            return Math.Clamp(
                maxThickness * 0.75,
                MaxInlinePlacementGraphMergeAxisDistanceByThicknessDrawingUnits,
                MaxExteriorShellInlinePlacementGraphMergeAxisDistanceDrawingUnits);
        }

        return Math.Clamp(
            maxThickness / 2.0,
            MaxInlinePlacementGraphMergeAxisDistanceDrawingUnits,
            MaxInlinePlacementGraphMergeAxisDistanceByThicknessDrawingUnits);
    }

    private static double PlacementGraphMergeGapTolerance(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span)
    {
        if (IsOpeningCutoutSplitPlacementGraphRun(cluster, span))
        {
            return MaxInlinePlacementGraphOpeningCutoutGapDrawingUnits;
        }

        if (IsSameSourceWallPlacementGraphRun(cluster, span))
        {
            return cluster.All(item => IsTrustedExteriorShellPlacementGraphMergeContinuation(item.Edge))
                && IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge)
                    ? MaxInlinePlacementGraphExteriorShellMergeGapDrawingUnits
                    : MaxInlinePlacementGraphSameWallGapDrawingUnits;
        }

        if (cluster.All(item => IsTrustedExteriorShellPlacementGraphMergeContinuation(item.Edge))
            && IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
        {
            return MaxInlinePlacementGraphExteriorShellMergeGapDrawingUnits;
        }

        if (cluster.All(item => IsStructuralPlacementGraphMergeContinuation(item.Edge))
            && IsStructuralPlacementGraphMergeContinuation(span.Edge))
        {
            var maxThickness = cluster
                .Select(item => item.Edge.ThicknessDrawingUnits)
                .Append(span.Edge.ThicknessDrawingUnits)
                .DefaultIfEmpty(0)
                .Max();
            return Math.Clamp(
                maxThickness * 9.0,
                MaxInlinePlacementGraphMergeGapDrawingUnits,
                MaxInlinePlacementGraphStructuralMergeGapDrawingUnits);
        }

        return MaxInlinePlacementGraphMergeGapDrawingUnits;
    }

    private static bool IsSameSourceWallPlacementGraphRun(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span) =>
        !string.IsNullOrWhiteSpace(span.Edge.WallId)
        && cluster.All(item => string.Equals(item.Edge.WallId, span.Edge.WallId, StringComparison.Ordinal));

    private static bool IsOpeningCutoutSplitPlacementGraphRun(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span) =>
        IsSameSourceWallPlacementGraphRun(cluster, span)
        && cluster.All(item => IsOpeningCutoutSplitPlacementGraphEdge(item.Edge))
        && IsOpeningCutoutSplitPlacementGraphEdge(span.Edge);

    private static bool IsOpeningCutoutSplitPlacementGraphRun(
        IReadOnlyList<PlacementGraphMergeSpan> spans) =>
        spans.Count > 1
        && spans.All(span => IsOpeningCutoutSplitPlacementGraphEdge(span.Edge))
        && spans
            .Select(span => span.Edge.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;

    private static bool IsOpeningCutoutSplitPlacementGraphEdge(PlacementWallGraphEdgeExport edge) =>
        edge.Evidence.Any(item =>
            item.Contains("split around anchored door/opening cutouts", StringComparison.OrdinalIgnoreCase));

    private static bool IsTrustedExteriorShellPlacementGraphRun(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span) =>
        cluster.All(item => IsTrustedExteriorShellPlacementGraphMergeContinuation(item.Edge))
        && IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge);

    private static bool IsTrustedStructuralInlineProtectedJunctionMergeRun(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        IReadOnlyDictionary<string, int> nodeIncidentCounts)
    {
        if (cluster.Count == 0
            || !cluster.All(item => IsStructuralPlacementGraphMergeContinuation(item.Edge))
            || !IsStructuralPlacementGraphMergeContinuation(span.Edge)
            || HasPlacementGraphDetailOrSurfaceEvidence(span.Edge)
            || cluster.Any(item => HasPlacementGraphDetailOrSurfaceEvidence(item.Edge)))
        {
            return false;
        }

        var protectedSharedNodeIds = cluster
            .SelectMany(item => new[] { item.StartNodeId, item.EndNodeId })
            .Where(nodeId => IsProtectedPlacementGraphMergeNode(nodeId, span.StartNodeId, nodeIncidentCounts)
                || IsProtectedPlacementGraphMergeNode(nodeId, span.EndNodeId, nodeIncidentCounts))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (protectedSharedNodeIds.Length == 0)
        {
            return false;
        }

        return protectedSharedNodeIds.All(nodeId =>
            IsInlineContinuationThroughProtectedPlacementGraphNode(cluster, span, nodeId));
    }

    private static bool IsInlineContinuationThroughProtectedPlacementGraphNode(
        IReadOnlyList<PlacementGraphMergeSpan> cluster,
        PlacementGraphMergeSpan span,
        string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        var clusterTouchesNodeFromLeft = cluster.Any(item => string.Equals(item.EndNodeId, nodeId, StringComparison.Ordinal));
        var clusterTouchesNodeFromRight = cluster.Any(item => string.Equals(item.StartNodeId, nodeId, StringComparison.Ordinal));
        var spanTouchesNodeFromRight = string.Equals(span.StartNodeId, nodeId, StringComparison.Ordinal);
        var spanTouchesNodeFromLeft = string.Equals(span.EndNodeId, nodeId, StringComparison.Ordinal);

        return (clusterTouchesNodeFromLeft && spanTouchesNodeFromRight)
            || (clusterTouchesNodeFromRight && spanTouchesNodeFromLeft);
    }

    private static bool IsTrustedExteriorShellPlacementGraphMergeContinuation(PlacementWallGraphEdgeExport edge) =>
        !edge.ExcludedFromStructuralTopology
        && edge.Evidence.Any(IsExteriorPlacementGraphEvidence);

    private static PlacementWallGraphEdgeExport MergeInlineCollinearPlacementGraphEdges(
        IReadOnlyList<PlacementGraphMergeSpan> spans)
    {
        var ordered = spans
            .OrderBy(span => span.Start)
            .ThenBy(span => span.End)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal)
            .ToArray();
        var primary = ordered
            .OrderByDescending(span => span.Length)
            .ThenBy(span => span.Edge.Id.Length)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal)
            .First()
            .Edge;
        var orientation = ordered[0].Orientation;
        var start = ordered.Min(span => span.Start);
        var end = ordered.Max(span => span.End);
        var axisDecision = PlacementGraphMergeAxis(ordered);
        var axis = axisDecision.Axis;
        var line = orientation == PlacementGraphEdgeOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, axis), new PlanPoint(end, axis))
            : new PlanLineSegment(new PlanPoint(axis, start), new PlanPoint(axis, end));
        var thickness = ordered.Max(span => span.Edge.ThicknessDrawingUnits);
        var bounds = BoundsForMergedPlacementGraphRun(line, orientation, thickness);
        var scale = primary.MillimetersPerDrawingUnit;
        var sourceEdgeIds = ordered
            .SelectMany(span => span.Edge.SourceWallGraphEdgeIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceWallIds = ordered
            .Select(span => span.Edge.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var mergedId = PlacementGraphMergedInlineRunId(primary.Id, ordered);
        var evidence = ordered
            .SelectMany(span => span.Edge.Evidence)
            .Append($"placement wall graph inline run merged {ordered.Length} collinear edge(s) and removed {ordered.Length - 1} inline node(s)")
            .Concat(!string.Equals(mergedId, primary.Id, StringComparison.Ordinal)
                ? new[] { $"placement wall graph inline run rejoined split host pieces as {mergedId}" }
                : Array.Empty<string>())
            .Concat(sourceWallIds.Length > 1
                ? new[] { $"placement wall graph inline run preserves {sourceWallIds.Length} source wall id(s) through sourceWallGraphEdgeIds" }
                : Array.Empty<string>())
            .Concat(ordered.Any(span => IsTrustedSourceBackedIsolatedPlacementContinuation(span.Edge))
                ? new[] { "placement wall graph inline run absorbed source-backed isolated continuation into structural run" }
                : Array.Empty<string>())
            .Concat(IsOpeningCutoutSplitPlacementGraphRun(ordered)
                ? new[]
                {
                    "placement wall graph inline run bridged anchored opening cutout gap; opening exports preserve the actual cutout"
                }
                : Array.Empty<string>())
            .Concat(axisDecision.Evidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return primary with
        {
            Id = mergedId,
            FromNodeId = ordered.First().StartNodeId,
            ToNodeId = ordered.OrderByDescending(span => span.End).First().EndNodeId,
            CenterLine = LineExport.From(line),
            CenterLineMillimeters = ScaleLine(line, scale),
            Bounds = RectExport.From(bounds),
            BoundsMillimeters = ScaleRect(bounds, scale),
            DrawingLength = line.Length,
            LengthMeters = scale is > 0 ? line.Length * scale.Value / 1000.0 : null,
            ThicknessDrawingUnits = thickness,
            ThicknessMillimeters = scale is > 0 && thickness > 0 ? thickness * scale.Value : null,
            Confidence = ordered.Min(span => span.Edge.Confidence),
            SourcePrimitiveIds = ordered
                .SelectMany(span => span.Edge.SourcePrimitiveIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceLayers = ordered
                .SelectMany(span => span.Edge.SourceLayers)
                .Where(layer => !string.IsNullOrWhiteSpace(layer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceWallGraphEdgeIds = sourceEdgeIds,
            Evidence = evidence
        };
    }

    private static string PlacementGraphMergedInlineRunId(
        string primaryId,
        IReadOnlyList<PlacementGraphMergeSpan> ordered)
    {
        if (ordered.Count <= 1
            || ordered.Any(span => !span.Edge.Id.Contains(":junction-piece:", StringComparison.Ordinal)))
        {
            return primaryId;
        }

        var baseIds = ordered
            .Select(span => PlacementGraphJunctionPieceBaseId(span.Edge.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return baseIds.Length == 1
            ? $"{baseIds[0]}:rejoined"
            : primaryId;
    }

    private static string PlacementGraphJunctionPieceBaseId(string edgeId)
    {
        var marker = edgeId.IndexOf(":junction-piece:", StringComparison.Ordinal);
        return marker > 0 ? edgeId[..marker] : edgeId;
    }

    private static PlacementGraphMergeAxisDecision PlacementGraphMergeAxis(
        IReadOnlyList<PlacementGraphMergeSpan> ordered)
    {
        if (ordered.Count == 0)
        {
            return new PlacementGraphMergeAxisDecision(0, Array.Empty<string>());
        }

        var weightedAxis = WeightedAxis(ordered);
        if (ordered.Count == 1)
        {
            return new PlacementGraphMergeAxisDecision(weightedAxis, Array.Empty<string>());
        }

        var totalLength = ordered.Sum(span => Math.Max(span.Length, 0.001));
        var dominantBand = FindDominantPlacementGraphMergeAxisBand(ordered, weightedAxis);
        var coverageRatio = dominantBand.Length / totalLength;
        var secondBandLength = ordered
            .Where(span => !dominantBand.MemberIndexes.Contains(span.Index))
            .Select(span => span.Length)
            .DefaultIfEmpty(0)
            .Max();
        var lengthRatio = secondBandLength <= 0.001 ? double.PositiveInfinity : dominantBand.Length / secondBandLength;
        var requiredCoverageRatio = dominantBand.Members.Any(span => IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
            && ordered.Any(span => !dominantBand.MemberIndexes.Contains(span.Index)
                && IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
                ? MinTrustedExteriorDominantPlacementGraphMergeAxisCoverageRatio
                : MinDominantPlacementGraphMergeAxisCoverageRatio;

        if (dominantBand.Length < MinDominantPlacementGraphMergeAxisLengthDrawingUnits
            || (coverageRatio < requiredCoverageRatio
                && lengthRatio < MinDominantPlacementGraphMergeAxisLengthRatio))
        {
            return new PlacementGraphMergeAxisDecision(weightedAxis, Array.Empty<string>());
        }

        var maxAxisDrift = ordered.Max(span => Math.Abs(span.Axis - dominantBand.Axis));
        if (maxAxisDrift <= 0.001)
        {
            return new PlacementGraphMergeAxisDecision(weightedAxis, Array.Empty<string>());
        }

        return new PlacementGraphMergeAxisDecision(
            dominantBand.Axis,
            [
                "placement wall graph inline run snapped merged axis to dominant host span "
                + $"{dominantBand.Leader.Edge.Id}; band coverage {coverageRatio:0.###}; "
                + $"band length ratio {lengthRatio:0.###}; max axis drift {maxAxisDrift:0.###} drawing units"
            ]);
    }

    private static PlacementGraphMergeAxisBand FindDominantPlacementGraphMergeAxisBand(
        IReadOnlyList<PlacementGraphMergeSpan> spans,
        double weightedAxis)
    {
        var tolerance = PlacementGraphDominantAxisBandTolerance(spans);
        return spans
            .Select(seed => CreatePlacementGraphMergeAxisBand(spans, seed.Axis, tolerance))
            .GroupBy(band => string.Join("|", band.MemberIndexes.OrderBy(index => index)), StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(band => Math.Abs(band.Axis - weightedAxis))
                .ThenBy(band => band.Leader.Edge.Id, StringComparer.Ordinal)
                .First())
            .OrderByDescending(band => band.Length)
            .ThenBy(band => Math.Abs(band.Axis - weightedAxis))
            .ThenByDescending(band => band.Leader.Length)
            .ThenBy(band => band.Leader.Edge.Id, StringComparer.Ordinal)
            .First();
    }

    private static PlacementGraphMergeAxisBand CreatePlacementGraphMergeAxisBand(
        IReadOnlyList<PlacementGraphMergeSpan> spans,
        double seedAxis,
        double tolerance)
    {
        var members = spans
            .Where(span => Math.Abs(span.Axis - seedAxis) <= tolerance)
            .OrderBy(span => span.Axis)
            .ThenBy(span => span.Start)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal)
            .ToArray();
        var axis = WeightedAxis(members);
        var refinedMembers = spans
            .Where(span => Math.Abs(span.Axis - axis) <= tolerance)
            .OrderBy(span => span.Axis)
            .ThenBy(span => span.Start)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal)
            .ToArray();
        if (refinedMembers.Length > 0)
        {
            members = refinedMembers;
            axis = WeightedAxis(members);
        }

        var leader = members
            .OrderByDescending(span => span.Length)
            .ThenBy(span => Math.Abs(span.Axis - axis))
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal)
            .First();
        axis = PreferDominantPlacementGraphLeaderAxis(members, leader, axis);
        var memberIndexes = members
            .Select(span => span.Index)
            .ToHashSet();

        return new PlacementGraphMergeAxisBand(
            axis,
            members.Sum(span => Math.Max(span.Length, 0.001)),
            leader,
            members,
            memberIndexes);
    }

    private static double PreferDominantPlacementGraphLeaderAxis(
        IReadOnlyList<PlacementGraphMergeSpan> members,
        PlacementGraphMergeSpan leader,
        double weightedAxis)
    {
        if (members.Count <= 1
            || leader.Length < MinDominantPlacementGraphMergeAxisLengthDrawingUnits
            || !IsStructuralPlacementGraphMergeContinuation(leader.Edge)
            || HasPlacementGraphDetailOrSurfaceEvidence(leader.Edge))
        {
            return weightedAxis;
        }

        var totalLength = members.Sum(span => Math.Max(span.Length, 0.001));
        var leaderCoverage = leader.Length / Math.Max(totalLength, 0.001);
        var nextLength = members
            .Where(span => span.Index != leader.Index)
            .Select(span => span.Length)
            .DefaultIfEmpty(0)
            .Max();
        var leaderLengthRatio = nextLength <= 0.001 ? double.PositiveInfinity : leader.Length / nextLength;
        if (leaderCoverage < 0.55 || leaderLengthRatio < 1.5)
        {
            return weightedAxis;
        }

        return leader.Axis;
    }

    private static double PlacementGraphDominantAxisBandTolerance(IReadOnlyList<PlacementGraphMergeSpan> spans)
    {
        var maxThickness = spans
            .Select(span => span.Edge.ThicknessDrawingUnits)
            .DefaultIfEmpty(0)
            .Max();
        var maxTolerance = spans.Any(span => IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
            ? MaxTrustedExteriorDominantPlacementGraphMergeAxisBandDistanceDrawingUnits
            : MaxDominantPlacementGraphMergeAxisBandDistanceDrawingUnits;
        return Math.Clamp(
            maxThickness / 3.0,
            MinDominantPlacementGraphMergeAxisBandDistanceDrawingUnits,
            maxTolerance);
    }

    private static PlacementGraphMergeSpan? TryCreatePlacementGraphMergeSpan(
        int index,
        PlacementWallGraphEdgeExport edge)
    {
        if (edge.CenterLine is null
            || (edge.ExcludedFromStructuralTopology
                && !IsTrustedSourceBackedIsolatedPlacementContinuation(edge))
            || edge.DrawingLength <= 0)
        {
            return null;
        }

        var start = ToPlanPoint(edge.CenterLine.Start);
        var end = ToPlanPoint(edge.CenterLine.End);
        var dx = Math.Abs(end.X - start.X);
        var dy = Math.Abs(end.Y - start.Y);
        if (dx <= double.Epsilon && dy <= double.Epsilon)
        {
            return null;
        }

        var axisTolerance = PlacementGraphInlineAxisTolerance(edge);
        if (dy <= axisTolerance && dx >= dy)
        {
            var startIsFirst = start.X <= end.X;
            var componentGroupId = PlacementGraphMergeComponentGroupId(edge);
            return new PlacementGraphMergeSpan(
                index,
                edge,
                new PlacementGraphMergeGroupKey(
                    edge.PageNumber,
                    PlacementGraphEdgeOrientation.Horizontal,
                    componentGroupId,
                    PlacementGraphMergeComponentKindGroup(edge)),
                PlacementGraphEdgeOrientation.Horizontal,
                (start.Y + end.Y) / 2.0,
                Math.Min(start.X, end.X),
                Math.Max(start.X, end.X),
                startIsFirst ? edge.FromNodeId : edge.ToNodeId,
                startIsFirst ? edge.ToNodeId : edge.FromNodeId);
        }

        if (dx <= axisTolerance && dy > dx)
        {
            var startIsFirst = start.Y <= end.Y;
            var componentGroupId = PlacementGraphMergeComponentGroupId(edge);
            return new PlacementGraphMergeSpan(
                index,
                edge,
                new PlacementGraphMergeGroupKey(
                    edge.PageNumber,
                    PlacementGraphEdgeOrientation.Vertical,
                    componentGroupId,
                    PlacementGraphMergeComponentKindGroup(edge)),
                PlacementGraphEdgeOrientation.Vertical,
                (start.X + end.X) / 2.0,
                Math.Min(start.Y, end.Y),
                Math.Max(start.Y, end.Y),
                startIsFirst ? edge.FromNodeId : edge.ToNodeId,
                startIsFirst ? edge.ToNodeId : edge.FromNodeId);
        }

        return null;
    }

    private static double PlacementGraphInlineAxisTolerance(PlacementWallGraphEdgeExport edge) =>
        Math.Clamp(
            edge.ThicknessDrawingUnits / 2.0,
            MaxInlinePlacementGraphMergeAxisDistanceDrawingUnits,
            IsTrustedExteriorShellPlacementGraphMergeContinuation(edge)
                ? MaxExteriorShellInlinePlacementGraphMergeAxisDistanceDrawingUnits
                : MaxInlinePlacementGraphMergeAxisDistanceByThicknessDrawingUnits);

    private static string PlacementGraphMergeComponentGroupId(PlacementWallGraphEdgeExport edge) =>
        IsStructuralPlacementGraphMergeContinuation(edge)
            ? string.Empty
            : edge.WallComponentId ?? edge.WallId ?? string.Empty;

    private static string PlacementGraphMergeComponentKindGroup(PlacementWallGraphEdgeExport edge) =>
        IsStructuralPlacementGraphMergeContinuation(edge)
            ? "StructuralContinuation"
            : edge.WallComponentKind ?? string.Empty;

    private static bool IsStructuralPlacementGraphMergeContinuation(PlacementWallGraphEdgeExport edge) =>
        IsStructuralPlacementGraphComponentKind(edge.WallComponentKind)
        || IsTrustedExteriorShellPlacementGraphMergeContinuation(edge)
        || IsTrustedSourceBackedIsolatedPlacementContinuation(edge);

    private static bool IsTrustedSourceBackedIsolatedPlacementContinuation(PlacementWallGraphEdgeExport edge)
    {
        var componentKindAllowsFallbackMerge =
            string.IsNullOrWhiteSpace(edge.WallComponentKind)
            || string.Equals(edge.WallComponentKind, nameof(WallGraphComponentKind.IsolatedFragment), StringComparison.Ordinal);
        if (!componentKindAllowsFallbackMerge
            || !edge.Evidence.Any(item => item.Contains("source-backed clean placement fallback", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!edge.Evidence.Any(item =>
            item.Contains("global exterior-shell repair confirmed", StringComparison.OrdinalIgnoreCase)
            || item.Contains("exterior shell repair promoted", StringComparison.OrdinalIgnoreCase)
            || item.Contains("source-backed exterior shell closure", StringComparison.OrdinalIgnoreCase)
            || item.Contains("trusted two-sided fragment-merged room boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("clean promoted fragment wall-body evidence is placement-ready", StringComparison.OrdinalIgnoreCase)
            || item.Contains("shared indoor room-boundary inference is placement-ready", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !edge.Evidence.Any(item =>
            item.Contains("object-like", StringComparison.OrdinalIgnoreCase)
            || item.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || item.Contains("stair-like linework", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasPlacementGraphDetailOrSurfaceEvidence(PlacementWallGraphEdgeExport edge) =>
        edge.Evidence.Any(IsPlacementGraphDetailOrSurfaceEvidence);

    private static bool IsPlacementGraphDetailOrSurfaceEvidence(string evidence)
    {
        if (evidence.Contains("shared by room adjacency that includes outdoor/terrace room evidence", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (evidence.Contains("room evidence on both sides includes outdoor/terrace space", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("outdoor/terrace room evidence", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return evidence.Contains("object-like", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("review as detail/object", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("stair-like linework", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStructuralPlacementGraphComponentKind(string? componentKind) =>
        string.Equals(componentKind, nameof(WallGraphComponentKind.MainStructural), StringComparison.Ordinal)
        || string.Equals(componentKind, nameof(WallGraphComponentKind.SecondaryStructural), StringComparison.Ordinal);

    private static PlacementGraphAxisRegularizationResult RegularizeAxisAlignedPlacementGraphEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count == 0)
        {
            return new PlacementGraphAxisRegularizationResult(edges.ToArray(), 0);
        }

        var regularizedCount = 0;
        var regularized = edges
            .Select((edge, index) =>
            {
                if (edge.CenterLine is null
                    || TryCreatePlacementGraphMergeSpan(index, edge) is not { } span)
                {
                    return edge;
                }

                var originalLine = new PlanLineSegment(
                    ToPlanPoint(edge.CenterLine.Start),
                    ToPlanPoint(edge.CenterLine.End));
                var start = originalLine.Start;
                var end = originalLine.End;
                var regularizedLine = span.Orientation == PlacementGraphEdgeOrientation.Horizontal
                    ? new PlanLineSegment(
                        new PlanPoint(start.X, span.Axis),
                        new PlanPoint(end.X, span.Axis))
                    : new PlanLineSegment(
                        new PlanPoint(span.Axis, start.Y),
                        new PlanPoint(span.Axis, end.Y));
                if (regularizedLine.Length <= 0.001)
                {
                    return edge;
                }

                var startMove = originalLine.Start.DistanceTo(regularizedLine.Start);
                var endMove = originalLine.End.DistanceTo(regularizedLine.End);
                var maxMove = Math.Max(startMove, endMove);
                if (maxMove <= 0.001
                    || maxMove > MaxPlacementGraphAxisRegularizationDistanceDrawingUnits)
                {
                    return edge;
                }

                regularizedCount++;
                var bounds = BoundsForPlacementGraphLine(regularizedLine, edge.ThicknessDrawingUnits);
                var scale = edge.MillimetersPerDrawingUnit;
                var evidence = edge.Evidence
                    .Append(
                        "placement wall graph axis regularization: "
                        + $"straightened near-{span.Orientation.ToString().ToLowerInvariant()} edge; "
                        + $"max endpoint offset {maxMove:0.###} drawing units")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return edge with
                {
                    CenterLine = LineExport.From(regularizedLine),
                    CenterLineMillimeters = ScaleLine(regularizedLine, scale),
                    Bounds = RectExport.From(bounds),
                    BoundsMillimeters = ScaleRect(bounds, scale),
                    DrawingLength = regularizedLine.Length,
                    LengthMeters = scale is > 0 ? regularizedLine.Length * scale.Value / 1000.0 : null,
                    Evidence = evidence
                };
            })
            .ToArray();

        return new PlacementGraphAxisRegularizationResult(regularized, regularizedCount);
    }

    private static double WeightedAxis(IReadOnlyList<PlacementGraphMergeSpan> spans)
    {
        var totalLength = spans.Sum(span => Math.Max(span.Length, 0.001));
        return spans.Sum(span => span.Axis * Math.Max(span.Length, 0.001)) / totalLength;
    }

    private static double WeightedAxis(IEnumerable<PlacementGraphMergeSpan> spans)
    {
        var items = spans.ToArray();
        if (items.Length == 0)
        {
            return 0;
        }

        return WeightedAxis(items);
    }

    private static PlanRect BoundsForMergedPlacementGraphRun(
        PlanLineSegment line,
        PlacementGraphEdgeOrientation orientation,
        double thickness)
    {
        var width = Math.Max(thickness, 0.001);
        return orientation == PlacementGraphEdgeOrientation.Horizontal
            ? new PlanRect(
                Math.Min(line.Start.X, line.End.X),
                line.Start.Y - width / 2.0,
                Math.Abs(line.End.X - line.Start.X),
                width)
            : new PlanRect(
                line.Start.X - width / 2.0,
                Math.Min(line.Start.Y, line.End.Y),
                width,
                Math.Abs(line.End.Y - line.Start.Y));
    }

    private static PlacementGraphNodeReferenceNormalizationResult NormalizePlacementGraphNodeReferencesFromGeometry(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count == 0)
        {
            return new PlacementGraphNodeReferenceNormalizationResult(edges.ToArray(), 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphNodeReferenceNormalizationResult(edges.ToArray(), 0);
        }

        var clusters = BuildPlacementGraphEndpointClusters(observations);
        if (clusters.Count == 0)
        {
            return new PlacementGraphNodeReferenceNormalizationResult(edges.ToArray(), 0);
        }

        var originalClusterUse = BuildPlacementGraphNodeOriginalClusterUse(clusters);
        var canonicalByCluster = BuildPlacementGraphCanonicalNodeIds(clusters, originalClusterUse);
        var nodeIdByEndpoint = new Dictionary<PlacementGraphEndpointKey, string>();
        var splitCount = 0;

        for (var clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
        {
            var canonicalId = canonicalByCluster[clusterIndex];
            foreach (var observation in clusters[clusterIndex])
            {
                nodeIdByEndpoint[new PlacementGraphEndpointKey(observation.EdgeIndex, observation.IsStart)] = canonicalId;
                if (!string.Equals(observation.OriginalNodeId, canonicalId, StringComparison.Ordinal)
                    && originalClusterUse.TryGetValue(observation.OriginalNodeId, out var usedClusterIndexes)
                    && usedClusterIndexes.Count > 1)
                {
                    splitCount++;
                }
            }
        }

        var normalized = edges
            .Select((edge, index) =>
            {
                var from = nodeIdByEndpoint.TryGetValue(new PlacementGraphEndpointKey(index, IsStart: true), out var fromId)
                    ? fromId
                    : edge.FromNodeId;
                var to = nodeIdByEndpoint.TryGetValue(new PlacementGraphEndpointKey(index, IsStart: false), out var toId)
                    ? toId
                    : edge.ToNodeId;
                return edge with
                {
                    FromNodeId = from,
                    ToNodeId = to
                };
            })
            .ToArray();

        return new PlacementGraphNodeReferenceNormalizationResult(normalized, splitCount);
    }

    private static PlacementGraphNodeCoordinateAlignmentResult AlignPlacementGraphEdgesToCanonicalNodePositions(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count == 0)
        {
            return new PlacementGraphNodeCoordinateAlignmentResult(edges.ToArray(), 0, 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphNodeCoordinateAlignmentResult(edges.ToArray(), 0, 0);
        }

        var spansByIndex = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToDictionary(span => span.Index);
        if (spansByIndex.Count == 0)
        {
            return new PlacementGraphNodeCoordinateAlignmentResult(edges.ToArray(), 0, 0);
        }

        var targetByNodeId = new Dictionary<string, PlanPoint>(StringComparer.Ordinal);
        foreach (var group in observations
                     .Where(observation => !string.IsNullOrWhiteSpace(observation.OriginalNodeId))
                     .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal))
        {
            var nodeObservations = group.ToArray();
            if (nodeObservations.Length <= 1)
            {
                continue;
            }

            if (TryCreateCanonicalPlacementNodePoint(nodeObservations, spansByIndex, out var point))
            {
                targetByNodeId[group.Key] = point;
            }
        }

        if (targetByNodeId.Count == 0)
        {
            return new PlacementGraphNodeCoordinateAlignmentResult(edges.ToArray(), 0, 0);
        }

        var alignedEndpointCount = 0;
        var alignedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        var alignedEdges = edges
            .Select((edge, _) =>
            {
                if (edge.CenterLine is null)
                {
                    return edge;
                }

                var start = ToPlanPoint(edge.CenterLine.Start);
                var end = ToPlanPoint(edge.CenterLine.End);
                var snappedEndpointNames = new List<string>(capacity: 2);
                if (targetByNodeId.TryGetValue(edge.FromNodeId, out var startTarget)
                    && start.DistanceTo(startTarget) <= MaxPlacementNodeCoordinateAlignmentDistanceDrawingUnits
                    && start.DistanceTo(startTarget) > 0.001)
                {
                    start = startTarget;
                    alignedEndpointCount++;
                    alignedNodeIds.Add(edge.FromNodeId);
                    snappedEndpointNames.Add("start");
                }

                if (targetByNodeId.TryGetValue(edge.ToNodeId, out var endTarget)
                    && end.DistanceTo(endTarget) <= MaxPlacementNodeCoordinateAlignmentDistanceDrawingUnits
                    && end.DistanceTo(endTarget) > 0.001)
                {
                    end = endTarget;
                    alignedEndpointCount++;
                    alignedNodeIds.Add(edge.ToNodeId);
                    snappedEndpointNames.Add("end");
                }

                if (snappedEndpointNames.Count == 0)
                {
                    return edge;
                }

                var line = new PlanLineSegment(start, end);
                if (line.Length <= 0.001)
                {
                    return edge;
                }

                var bounds = BoundsForPlacementGraphLine(line, edge.ThicknessDrawingUnits);
                var scale = edge.MillimetersPerDrawingUnit;
                var evidence = edge.Evidence
                    .Append(
                        "placement wall graph node-coordinate alignment: "
                        + $"snapped {string.Join("+", snappedEndpointNames)} endpoint coordinate(s) to canonical shared node position")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return edge with
                {
                    CenterLine = LineExport.From(line),
                    CenterLineMillimeters = ScaleLine(line, scale),
                    Bounds = RectExport.From(bounds),
                    BoundsMillimeters = ScaleRect(bounds, scale),
                    DrawingLength = line.Length,
                    LengthMeters = scale is > 0 ? line.Length * scale.Value / 1000.0 : null,
                    Evidence = evidence
                };
            })
            .ToArray();

        return new PlacementGraphNodeCoordinateAlignmentResult(
            alignedEdges,
            alignedEndpointCount,
            alignedNodeIds.Count);
    }

    private static bool TryCreateCanonicalPlacementNodePoint(
        IReadOnlyList<PlacementGraphEndpointObservation> observations,
        IReadOnlyDictionary<int, PlacementGraphMergeSpan> spansByIndex,
        out PlanPoint point)
    {
        point = new PlanPoint(0, 0);
        var spanObservations = observations
            .Select(observation => spansByIndex.TryGetValue(observation.EdgeIndex, out var span)
                ? new PlacementGraphEndpointSpanObservation(observation, span)
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        if (spanObservations.Length != observations.Count)
        {
            return false;
        }

        var horizontal = spanObservations
            .Where(item => item.Span.Orientation == PlacementGraphEdgeOrientation.Horizontal)
            .ToArray();
        var vertical = spanObservations
            .Where(item => item.Span.Orientation == PlacementGraphEdgeOrientation.Vertical)
            .ToArray();
        if (horizontal.Length == 0 && vertical.Length == 0)
        {
            return false;
        }

        var x = vertical.Length > 0
            ? WeightedAxis(vertical.Select(item => item.Span))
            : observations.Average(item => item.Position.X);
        var y = horizontal.Length > 0
            ? WeightedAxis(horizontal.Select(item => item.Span))
            : observations.Average(item => item.Position.Y);
        var candidate = new PlanPoint(x, y);
        if (observations.Any(observation =>
                observation.Position.DistanceTo(candidate) > MaxPlacementNodeCoordinateAlignmentDistanceDrawingUnits))
        {
            return false;
        }

        point = candidate;
        return true;
    }

    private static PlacementGraphNodeOnEdgeAbsorptionResult AbsorbPlacementGraphEndpointNodesOnHostEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges) =>
        AbsorbPlacementGraphEndpointNodesOnHostEdgesCore(
            edges,
            requireSingleUseEndpointNode: true,
            maxDistanceOverride: null,
            minHostSplitFraction: MinPlacementEndpointOnWallSplitFraction,
            evidencePrefix: "placement wall graph endpoint-on-wall absorption",
            splitPointEvidence: "host split uses absorbed node");

    private static PlacementGraphNodeOnEdgeAbsorptionResult AbsorbCoincidentPlacementGraphEndpointNodesOnHostEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges) =>
        AbsorbPlacementGraphEndpointNodesOnHostEdgesCore(
            edges,
            requireSingleUseEndpointNode: false,
            maxDistanceOverride: MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits,
            minHostSplitFraction: 0,
            evidencePrefix: "placement wall graph final endpoint-on-wall cleanup",
            splitPointEvidence: "host split uses coincident endpoint node");

    private static PlacementGraphTinyEndpointOnHostStubSuppressionResult SuppressTinyEndpointOnHostPlacementGraphStubs(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphTinyEndpointOnHostStubSuppressionResult(edges.ToArray(), 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphTinyEndpointOnHostStubSuppressionResult(edges.ToArray(), 0);
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphTinyEndpointOnHostStubSuppressionResult(edges.ToArray(), 0);
        }

        var observationsByEdgeIndex = observations
            .GroupBy(observation => observation.EdgeIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var endpointUseCount = observations
            .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var suppressedIndexes = new HashSet<int>();
        foreach (var candidateSpan in spans)
        {
            if (!CanSuppressTinyEndpointOnHostStub(candidateSpan)
                || !observationsByEdgeIndex.TryGetValue(candidateSpan.Index, out var candidateEndpoints))
            {
                continue;
            }

            if (candidateEndpoints.Any(endpoint => IsTinyEndpointStubAnchoredOnHostWall(
                    candidateSpan,
                    endpoint,
                    spans,
                    endpointUseCount)))
            {
                suppressedIndexes.Add(candidateSpan.Index);
            }
        }

        if (suppressedIndexes.Count == 0)
        {
            return new PlacementGraphTinyEndpointOnHostStubSuppressionResult(edges.ToArray(), 0);
        }

        return new PlacementGraphTinyEndpointOnHostStubSuppressionResult(
            edges
                .Where((_, index) => !suppressedIndexes.Contains(index))
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            suppressedIndexes.Count);
    }

    private static bool CanSuppressTinyEndpointOnHostStub(PlacementGraphMergeSpan candidateSpan)
    {
        if (candidateSpan.Length <= 0
            || candidateSpan.Length > MaxTinyEndpointOnHostStubLengthDrawingUnits
            || candidateSpan.Edge.ExcludedFromStructuralTopology)
        {
            return false;
        }

        if (IsTrustedExteriorShellPlacementGraphMergeContinuation(candidateSpan.Edge)
            || candidateSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence))
        {
            return false;
        }

        return true;
    }

    private static bool IsTinyEndpointStubAnchoredOnHostWall(
        PlacementGraphMergeSpan candidateSpan,
        PlacementGraphEndpointObservation endpoint,
        IReadOnlyList<PlacementGraphMergeSpan> hostSpans,
        IReadOnlyDictionary<string, int> endpointUseCount)
    {
        if (endpointUseCount.GetValueOrDefault(endpoint.OriginalNodeId) > 1)
        {
            return false;
        }

        foreach (var hostSpan in hostSpans)
        {
            if (hostSpan.Index == candidateSpan.Index
                || hostSpan.Edge.PageNumber != candidateSpan.Edge.PageNumber
                || hostSpan.Edge.CenterLine is null
                || hostSpan.Edge.ExcludedFromStructuralTopology
                || hostSpan.Length < Math.Max(MinPlacementEndpointOnWallSplitDistanceDrawingUnits * 2.0, candidateSpan.Length * 3.0))
            {
                continue;
            }

            var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                ? endpoint.Position.X
                : endpoint.Position.Y;
            if (coordinate <= hostSpan.Start + MinPlacementEndpointOnWallSplitDistanceDrawingUnits
                || coordinate >= hostSpan.End - MinPlacementEndpointOnWallSplitDistanceDrawingUnits)
            {
                continue;
            }

            var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
            if (endpoint.Position.DistanceTo(hostPoint) <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits)
            {
                return true;
            }
        }

        return false;
    }

    private static PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult SuppressRedundantEndpointOnHostPlacementGraphFragments(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(edges.ToArray(), 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(edges.ToArray(), 0);
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(edges.ToArray(), 0);
        }

        var observationsByEdgeIndex = observations
            .GroupBy(observation => observation.EdgeIndex)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var endpointUseCount = observations
            .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var suppressedIndexes = new HashSet<int>();
        var suppressionsByHostIndex = new Dictionary<int, List<PlacementGraphRedundantEndpointOnHostFragmentSuppression>>();

        foreach (var candidateSpan in spans
            .OrderBy(span => span.Length)
            .ThenBy(span => span.Edge.Id, StringComparer.Ordinal))
        {
            if (suppressedIndexes.Contains(candidateSpan.Index)
                || !observationsByEdgeIndex.TryGetValue(candidateSpan.Index, out var candidateEndpoints)
                || TryCreateRedundantEndpointOnHostFragmentSuppression(
                    candidateSpan,
                    candidateEndpoints,
                    spans,
                    endpointUseCount,
                    suppressedIndexes) is not { } suppression)
            {
                continue;
            }

            suppressedIndexes.Add(candidateSpan.Index);
            if (!suppressionsByHostIndex.TryGetValue(suppression.HostSpan.Index, out var hostSuppressions))
            {
                hostSuppressions = new List<PlacementGraphRedundantEndpointOnHostFragmentSuppression>();
                suppressionsByHostIndex[suppression.HostSpan.Index] = hostSuppressions;
            }

            hostSuppressions.Add(suppression);
        }

        if (suppressedIndexes.Count == 0)
        {
            return new PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(edges.ToArray(), 0);
        }

        return new PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(
            edges
                .Select((edge, index) => suppressionsByHostIndex.TryGetValue(index, out var hostSuppressions)
                    ? AddRedundantEndpointOnHostFragmentSuppressionEvidence(edge, hostSuppressions)
                    : edge)
                .Where((_, index) => !suppressedIndexes.Contains(index))
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            suppressedIndexes.Count);
    }

    private static PlacementGraphRedundantEndpointOnHostFragmentSuppression? TryCreateRedundantEndpointOnHostFragmentSuppression(
        PlacementGraphMergeSpan candidateSpan,
        IReadOnlyList<PlacementGraphEndpointObservation> candidateEndpoints,
        IReadOnlyList<PlacementGraphMergeSpan> hostSpans,
        IReadOnlyDictionary<string, int> endpointUseCount,
        IReadOnlySet<int> suppressedIndexes)
    {
        if (!CanSuppressRedundantEndpointOnHostFragment(candidateSpan))
        {
            return null;
        }

        PlacementGraphRedundantEndpointOnHostFragmentSuppression? best = null;
        foreach (var endpoint in candidateEndpoints)
        {
            var oppositeEndpoint = candidateEndpoints.FirstOrDefault(item => item.IsStart != endpoint.IsStart);
            if (oppositeEndpoint is null
                || endpointUseCount.GetValueOrDefault(oppositeEndpoint.OriginalNodeId) > 1)
            {
                continue;
            }

            foreach (var hostSpan in hostSpans)
            {
                if (!CanHostRedundantEndpointOnHostFragment(candidateSpan, hostSpan, suppressedIndexes))
                {
                    continue;
                }

                var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                    ? endpoint.Position.X
                    : endpoint.Position.Y;
                if (coordinate <= hostSpan.Start + MinPlacementEndpointOnWallSplitDistanceDrawingUnits
                    || coordinate >= hostSpan.End - MinPlacementEndpointOnWallSplitDistanceDrawingUnits)
                {
                    continue;
                }

                var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
                var distance = endpoint.Position.DistanceTo(hostPoint);
                if (distance > RedundantEndpointOnHostFragmentTolerance(candidateSpan, hostSpan))
                {
                    continue;
                }

                var candidate = new PlacementGraphRedundantEndpointOnHostFragmentSuppression(
                    candidateSpan,
                    hostSpan,
                    endpoint,
                    distance);
                if (best is null
                    || candidate.Distance < best.Distance
                    || (Math.Abs(candidate.Distance - best.Distance) <= 0.001
                        && candidate.HostSpan.Length > best.HostSpan.Length))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool CanSuppressRedundantEndpointOnHostFragment(PlacementGraphMergeSpan candidateSpan)
    {
        var openingLikeOrphanFragment = IsOpeningLikeOrphanEndpointOnHostFragment(candidateSpan.Edge);
        if (candidateSpan.Length <= 0
            || candidateSpan.Length > (openingLikeOrphanFragment
                ? MaxOpeningLikeRedundantEndpointOnHostFragmentLengthDrawingUnits
                : MaxRedundantEndpointOnHostFragmentLengthDrawingUnits)
            || candidateSpan.Edge.ExcludedFromStructuralTopology
            || IsTrustedExteriorShellPlacementGraphMergeContinuation(candidateSpan.Edge)
            || candidateSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || HasStrongPlacementGraphBoundaryEvidence(candidateSpan.Edge))
        {
            return false;
        }

        return openingLikeOrphanFragment
            || HasPlacementGraphDetailOrSurfaceEvidence(candidateSpan.Edge);
    }

    private static bool IsOpeningLikeOrphanEndpointOnHostFragment(PlacementWallGraphEdgeExport edge) =>
        edge.Id.Contains("opening-piece", StringComparison.OrdinalIgnoreCase)
        || edge.Evidence.Any(item =>
                item.Contains("opening", StringComparison.OrdinalIgnoreCase)
                || item.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase)
                || item.Contains("isolated fragment", StringComparison.OrdinalIgnoreCase)
                || item.Contains("one endpoint", StringComparison.OrdinalIgnoreCase)
                || item.Contains("dangling", StringComparison.OrdinalIgnoreCase));

    private static bool HasStrongPlacementGraphBoundaryEvidence(PlacementWallGraphEdgeExport edge) =>
        edge.Evidence.Any(item =>
            item.Contains("detected room evidence on both sides", StringComparison.OrdinalIgnoreCase)
            || item.Contains("shared by room adjacency", StringComparison.OrdinalIgnoreCase)
            || item.Contains("accepted paired wall body evidence", StringComparison.OrdinalIgnoreCase)
            || item.Contains("trusted two-sided", StringComparison.OrdinalIgnoreCase)
            || item.Contains("source-backed", StringComparison.OrdinalIgnoreCase));

    private static bool CanHostRedundantEndpointOnHostFragment(
        PlacementGraphMergeSpan candidateSpan,
        PlacementGraphMergeSpan hostSpan,
        IReadOnlySet<int> suppressedIndexes)
    {
        if (hostSpan.Index == candidateSpan.Index
            || suppressedIndexes.Contains(hostSpan.Index)
            || hostSpan.Edge.PageNumber != candidateSpan.Edge.PageNumber
            || hostSpan.Edge.CenterLine is null
            || hostSpan.Edge.ExcludedFromStructuralTopology
            || hostSpan.Length < Math.Max(
                MinDominantPlacementGraphMergeAxisLengthDrawingUnits,
                candidateSpan.Length * MinRedundantEndpointOnHostFragmentHostLengthRatio)
            || HasPlacementGraphDetailOrSurfaceEvidence(hostSpan.Edge))
        {
            return false;
        }

        return IsStructuralPlacementGraphMergeContinuation(hostSpan.Edge)
            || hostSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence);
    }

    private static double RedundantEndpointOnHostFragmentTolerance(
        PlacementGraphMergeSpan candidateSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        var maxThickness = Math.Max(
            candidateSpan.Edge.ThicknessDrawingUnits,
            hostSpan.Edge.ThicknessDrawingUnits);
        return Math.Clamp(
            maxThickness / 2.0,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits);
    }

    private static PlacementWallGraphEdgeExport AddRedundantEndpointOnHostFragmentSuppressionEvidence(
        PlacementWallGraphEdgeExport hostEdge,
        IReadOnlyList<PlacementGraphRedundantEndpointOnHostFragmentSuppression> suppressions)
    {
        var ordered = suppressions
            .OrderBy(suppression => suppression.Distance)
            .ThenBy(suppression => suppression.CandidateSpan.Edge.Id, StringComparer.Ordinal)
            .ToArray();
        var evidence = hostEdge.Evidence
            .Concat(ordered.Select(suppression =>
                "placement wall graph redundant endpoint-on-host cleanup: "
                + $"suppressed {suppression.CandidateSpan.Edge.Id} into host {hostEdge.Id}; "
                + $"endpoint offset {suppression.Distance:0.###} drawing units"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return hostEdge with
        {
            Confidence = Math.Min(
                hostEdge.Confidence,
                ordered.Select(suppression => suppression.CandidateSpan.Edge.Confidence).DefaultIfEmpty(hostEdge.Confidence).Min()),
            SourcePrimitiveIds = hostEdge.SourcePrimitiveIds
                .Concat(ordered.SelectMany(suppression => suppression.CandidateSpan.Edge.SourcePrimitiveIds))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceLayers = hostEdge.SourceLayers
                .Concat(ordered.SelectMany(suppression => suppression.CandidateSpan.Edge.SourceLayers))
                .Where(layer => !string.IsNullOrWhiteSpace(layer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceWallGraphEdgeIds = hostEdge.SourceWallGraphEdgeIds
                .Concat(ordered.SelectMany(suppression => suppression.CandidateSpan.Edge.SourceWallGraphEdgeIds))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence
        };
    }

    private static PlacementGraphTinyOpeningBridgeSuppressionResult SuppressTinyOpeningBridgeEdgesOnHostWalls(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphTinyOpeningBridgeSuppressionResult(edges.ToArray(), 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphTinyOpeningBridgeSuppressionResult(edges.ToArray(), 0);
        }

        var nodeObservations = observations
            .Where(observation => !string.IsNullOrWhiteSpace(observation.OriginalNodeId))
            .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(observation => observation.Position).ToList(),
                StringComparer.Ordinal);
        var nodeUseCount = observations
            .Where(observation => !string.IsNullOrWhiteSpace(observation.OriginalNodeId))
            .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var attachmentsByNodeId = BuildPlacementGraphInteriorNodeAttachments(edges, nodeObservations);
        if (attachmentsByNodeId.Count == 0)
        {
            return new PlacementGraphTinyOpeningBridgeSuppressionResult(edges.ToArray(), 0);
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphTinyOpeningBridgeSuppressionResult(edges.ToArray(), 0);
        }

        var edgeIndexById = edges
            .Select((edge, index) => new { edge.Id, Index = index })
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.Ordinal);
        var suppressedIndexes = new HashSet<int>();
        var suppressionsByHostEdgeIndex = new Dictionary<int, List<PlacementGraphTinyOpeningBridgeSuppression>>();
        foreach (var span in spans)
        {
            if (!CanSuppressTinyOpeningBridgeEdgeOnHostWalls(
                    span,
                    nodeUseCount,
                    attachmentsByNodeId,
                    out var hostAttachments))
            {
                continue;
            }

            suppressedIndexes.Add(span.Index);
            foreach (var hostEdgeId in hostAttachments
                         .Select(attachment => attachment.HostEdgeId)
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.Ordinal))
            {
                if (!edgeIndexById.TryGetValue(hostEdgeId, out var hostEdgeIndex)
                    || hostEdgeIndex == span.Index)
                {
                    continue;
                }

                if (!suppressionsByHostEdgeIndex.TryGetValue(hostEdgeIndex, out var hostSuppressions))
                {
                    hostSuppressions = new List<PlacementGraphTinyOpeningBridgeSuppression>();
                    suppressionsByHostEdgeIndex[hostEdgeIndex] = hostSuppressions;
                }

                hostSuppressions.Add(new PlacementGraphTinyOpeningBridgeSuppression(
                    span.Edge,
                    hostAttachments.Where(attachment => string.Equals(attachment.HostEdgeId, hostEdgeId, StringComparison.Ordinal)).ToArray()));
            }
        }

        if (suppressedIndexes.Count == 0)
        {
            return new PlacementGraphTinyOpeningBridgeSuppressionResult(edges.ToArray(), 0);
        }

        return new PlacementGraphTinyOpeningBridgeSuppressionResult(
            edges
                .Select((edge, index) => suppressionsByHostEdgeIndex.TryGetValue(index, out var suppressions)
                    ? AddTinyOpeningBridgeSuppressionEvidence(edge, suppressions)
                    : edge)
                .Where((_, index) => !suppressedIndexes.Contains(index))
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            suppressedIndexes.Count);
    }

    private static bool CanSuppressTinyOpeningBridgeEdgeOnHostWalls(
        PlacementGraphMergeSpan span,
        IReadOnlyDictionary<string, int> nodeUseCount,
        IReadOnlyDictionary<string, IReadOnlyList<PlacementGraphInteriorNodeAttachment>> attachmentsByNodeId,
        out IReadOnlyList<PlacementGraphInteriorNodeAttachment> hostAttachments)
    {
        hostAttachments = Array.Empty<PlacementGraphInteriorNodeAttachment>();
        if (span.Length <= 0
            || span.Length > MaxTinyOpeningBridgeOnHostLengthDrawingUnits
            || span.Edge.ExcludedFromStructuralTopology
            || IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge)
            || span.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || !HasTinyOpeningBridgeEvidence(span.Edge)
            || nodeUseCount.GetValueOrDefault(span.StartNodeId) != 1
            || nodeUseCount.GetValueOrDefault(span.EndNodeId) != 1)
        {
            return false;
        }

        if (!attachmentsByNodeId.TryGetValue(span.StartNodeId, out var startAttachments)
            || !attachmentsByNodeId.TryGetValue(span.EndNodeId, out var endAttachments)
            || startAttachments.Count == 0
            || endAttachments.Count == 0)
        {
            return false;
        }

        var attachments = startAttachments
            .Concat(endAttachments)
            .Where(attachment => attachment.Distance <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits)
            .ToArray();
        if (attachments.Length < 2)
        {
            return false;
        }

        hostAttachments = attachments;
        return true;
    }

    private static bool HasTinyOpeningBridgeEvidence(PlacementWallGraphEdgeExport edge) =>
        edge.Id.Contains("opening-piece", StringComparison.OrdinalIgnoreCase)
        || edge.Evidence.Any(item =>
            item.Contains("split around anchored door/opening cutouts", StringComparison.OrdinalIgnoreCase)
            || item.Contains("anchored opening cutout", StringComparison.OrdinalIgnoreCase)
            || item.Contains("opening-piece", StringComparison.OrdinalIgnoreCase));

    private static PlacementWallGraphEdgeExport AddTinyOpeningBridgeSuppressionEvidence(
        PlacementWallGraphEdgeExport hostEdge,
        IReadOnlyList<PlacementGraphTinyOpeningBridgeSuppression> suppressions)
    {
        var ordered = suppressions
            .OrderBy(suppression => suppression.BridgeEdge.DrawingLength)
            .ThenBy(suppression => suppression.BridgeEdge.Id, StringComparer.Ordinal)
            .ToArray();
        var evidence = hostEdge.Evidence
            .Concat(ordered.Select(suppression =>
            {
                var maxOffset = suppression.Attachments
                    .Select(attachment => attachment.Distance)
                    .DefaultIfEmpty(0)
                    .Max();
                return "placement wall graph tiny opening bridge cleanup: "
                    + $"suppressed {suppression.BridgeEdge.Id} into host {hostEdge.Id}; "
                    + $"bridge length {suppression.BridgeEdge.DrawingLength:0.###}, "
                    + $"max endpoint-host offset {maxOffset:0.###} drawing units";
            }))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return hostEdge with
        {
            Confidence = Math.Min(
                hostEdge.Confidence,
                ordered.Select(suppression => suppression.BridgeEdge.Confidence).DefaultIfEmpty(hostEdge.Confidence).Min()),
            SourcePrimitiveIds = hostEdge.SourcePrimitiveIds
                .Concat(ordered.SelectMany(suppression => suppression.BridgeEdge.SourcePrimitiveIds))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceLayers = hostEdge.SourceLayers
                .Concat(ordered.SelectMany(suppression => suppression.BridgeEdge.SourceLayers))
                .Where(layer => !string.IsNullOrWhiteSpace(layer))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SourceWallGraphEdgeIds = hostEdge.SourceWallGraphEdgeIds
                .Concat(ordered.SelectMany(suppression => suppression.BridgeEdge.SourceWallGraphEdgeIds))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence
        };
    }

    private static PlacementGraphEndpointOnHostResidualSummary SummarizeResidualPlacementGraphEndpointOnHostEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return PlacementGraphEndpointOnHostResidualSummary.Empty;
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return PlacementGraphEndpointOnHostResidualSummary.Empty;
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return PlacementGraphEndpointOnHostResidualSummary.Empty;
        }

        var spansByIndex = spans.ToDictionary(span => span.Index);
        var residuals = new List<PlacementGraphEndpointOnHostResidual>();
        foreach (var observation in observations)
        {
            if (!spansByIndex.TryGetValue(observation.EdgeIndex, out var endpointSpan))
            {
                continue;
            }

            PlacementGraphEndpointOnHostResidual? best = null;
            foreach (var hostSpan in spans)
            {
                if (hostSpan.Index == endpointSpan.Index
                    || hostSpan.Edge.PageNumber != endpointSpan.Edge.PageNumber
                    || string.Equals(hostSpan.Edge.WallId, endpointSpan.Edge.WallId, StringComparison.Ordinal)
                    || hostSpan.Length <= MinPlacementEndpointOnWallSplitDistanceDrawingUnits * 2.0)
                {
                    continue;
                }

                var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                    ? observation.Position.X
                    : observation.Position.Y;
                if (coordinate <= hostSpan.Start + MinPlacementEndpointOnWallSplitDistanceDrawingUnits
                    || coordinate >= hostSpan.End - MinPlacementEndpointOnWallSplitDistanceDrawingUnits)
                {
                    continue;
                }

                var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
                var distance = observation.Position.DistanceTo(hostPoint);
                if (distance <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits)
                {
                    continue;
                }

                if (distance > MaxPlacementSharedNodeHostSnapDistanceDrawingUnits)
                {
                    continue;
                }

                var candidate = new PlacementGraphEndpointOnHostResidual(
                    observation,
                    endpointSpan,
                    hostSpan,
                    coordinate,
                    hostPoint,
                    distance);
                if (best is null
                    || candidate.Distance < best.Distance
                    || (Math.Abs(candidate.Distance - best.Distance) <= 0.001
                        && candidate.HostSpan.Length > best.HostSpan.Length))
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                residuals.Add(best);
            }
        }

        if (residuals.Count == 0)
        {
            return PlacementGraphEndpointOnHostResidualSummary.Empty;
        }

        var exportedCandidates = residuals
            .Select(ToResidualEndpointOnHostCandidateExport)
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.Endpoint.Y)
            .ThenBy(candidate => candidate.Endpoint.X)
            .ThenBy(candidate => candidate.EndpointEdgeId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.HostEdgeId, StringComparer.Ordinal)
            .ToArray();

        return new PlacementGraphEndpointOnHostResidualSummary(
            residuals.Count,
            residuals.Count(residual => residual.Distance <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits),
            residuals.Count(residual => residual.EndpointSpan.Orientation == residual.HostSpan.Orientation),
            residuals.Count(residual => residual.EndpointSpan.Orientation != residual.HostSpan.Orientation),
            residuals.Max(residual => residual.Distance),
            exportedCandidates);
    }

    private static PlacementGraphResidualEndpointSnapResult SnapResidualPlacementGraphEndpointsOntoHostEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphResidualEndpointSnapResult(edges.ToArray(), 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphResidualEndpointSnapResult(edges.ToArray(), 0);
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphResidualEndpointSnapResult(edges.ToArray(), 0);
        }

        var spansByIndex = spans.ToDictionary(span => span.Index);
        var snaps = observations
            .Select(observation => TryCreateResidualPlacementGraphEndpointSnap(observation, spans, spansByIndex))
            .Where(snap => snap is not null)
            .Select(snap => snap!)
            .GroupBy(snap => new PlacementGraphEndpointKey(snap.Endpoint.EdgeIndex, snap.Endpoint.IsStart))
            .Select(group => group
                .OrderBy(snap => snap.Distance)
                .ThenByDescending(snap => snap.HostSpan.Length)
                .ThenBy(snap => snap.HostSpan.Edge.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(snap => snap.Endpoint.EdgeIndex)
            .ThenBy(snap => snap.Endpoint.IsStart ? 0 : 1)
            .ToArray();
        if (snaps.Length == 0)
        {
            return new PlacementGraphResidualEndpointSnapResult(edges.ToArray(), 0);
        }

        var snappedEdges = edges.ToArray();
        foreach (var snap in snaps)
        {
            snappedEdges[snap.Endpoint.EdgeIndex] = SnapPlacementGraphEndpointOntoHostWall(
                snappedEdges[snap.Endpoint.EdgeIndex],
                snap.Endpoint.IsStart,
                snap.HostPoint,
                snap.HostSpan.Edge.Id,
                snap.Distance,
                "placement wall graph final residual endpoint snap");
        }

        return new PlacementGraphResidualEndpointSnapResult(
            snappedEdges
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            snaps.Length);
    }

    private static PlacementGraphEndpointOnWallAbsorption? TryCreateResidualPlacementGraphEndpointSnap(
        PlacementGraphEndpointObservation endpoint,
        IReadOnlyList<PlacementGraphMergeSpan> hostSpans,
        IReadOnlyDictionary<int, PlacementGraphMergeSpan> spansByIndex)
    {
        if (!spansByIndex.TryGetValue(endpoint.EdgeIndex, out var endpointSpan)
            || endpointSpan.Length < MinPlacementEndpointOnWallCandidateLengthDrawingUnits)
        {
            return null;
        }

        PlacementGraphEndpointOnWallAbsorption? best = null;
        foreach (var hostSpan in hostSpans)
        {
            if (!CanSnapResidualPlacementGraphEndpointOntoHostSpan(endpointSpan, hostSpan))
            {
                continue;
            }

            var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                ? endpoint.Position.X
                : endpoint.Position.Y;
            if (coordinate <= hostSpan.Start + MinPlacementEndpointOnWallSplitDistanceDrawingUnits
                || coordinate >= hostSpan.End - MinPlacementEndpointOnWallSplitDistanceDrawingUnits)
            {
                continue;
            }

            var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
            var distance = endpoint.Position.DistanceTo(hostPoint);
            var maxDistance = PlacementResidualEndpointOnHostSnapTolerance(endpointSpan, hostSpan);
            if (distance <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits
                || distance > maxDistance)
            {
                continue;
            }

            var candidate = new PlacementGraphEndpointOnWallAbsorption(
                endpoint,
                hostSpan,
                coordinate,
                hostPoint,
                distance);
            if (best is null
                || candidate.Distance < best.Distance
                || (Math.Abs(candidate.Distance - best.Distance) <= 0.001
                    && candidate.HostSpan.Length > best.HostSpan.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static PlacementGraphEndpointPairSnapResult SnapNearbyPlacementGraphEndpointPairs(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphEndpointPairSnapResult(edges.ToArray(), 0, 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count <= 1)
        {
            return new PlacementGraphEndpointPairSnapResult(edges.ToArray(), 0, 0);
        }

        var spansByIndex = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToDictionary(span => span.Index);
        if (spansByIndex.Count <= 1)
        {
            return new PlacementGraphEndpointPairSnapResult(edges.ToArray(), 0, 0);
        }

        var candidates = new List<PlacementGraphEndpointPairSnap>();
        for (var firstIndex = 0; firstIndex < observations.Count; firstIndex++)
        {
            var first = observations[firstIndex];
            if (!spansByIndex.TryGetValue(first.EdgeIndex, out var firstSpan))
            {
                continue;
            }

            for (var secondIndex = firstIndex + 1; secondIndex < observations.Count; secondIndex++)
            {
                var second = observations[secondIndex];
                if (!spansByIndex.TryGetValue(second.EdgeIndex, out var secondSpan)
                    || !CanSnapNearbyPlacementGraphEndpointPair(firstSpan, secondSpan))
                {
                    continue;
                }

                var point = PlacementGraphEndpointPairSnapPoint(firstSpan, secondSpan);
                var firstDistance = first.Position.DistanceTo(point);
                var secondDistance = second.Position.DistanceTo(point);
                var tolerance = PlacementEndpointToEndpointSnapTolerance(firstSpan, secondSpan);
                if (firstDistance > tolerance
                    || secondDistance > tolerance
                    || firstDistance + secondDistance <= 0.001)
                {
                    continue;
                }

                candidates.Add(new PlacementGraphEndpointPairSnap(
                    first,
                    second,
                    point,
                    firstDistance,
                    secondDistance));
            }
        }

        if (candidates.Count == 0)
        {
            return new PlacementGraphEndpointPairSnapResult(edges.ToArray(), 0, 0);
        }

        var usedEndpoints = new HashSet<PlacementGraphEndpointKey>();
        var selected = new List<PlacementGraphEndpointPairSnap>();
        foreach (var candidate in candidates
            .OrderBy(candidate => candidate.FirstDistance + candidate.SecondDistance)
            .ThenBy(candidate => Math.Max(candidate.FirstDistance, candidate.SecondDistance))
            .ThenBy(candidate => candidate.First.EdgeIndex)
            .ThenBy(candidate => candidate.Second.EdgeIndex))
        {
            var firstKey = new PlacementGraphEndpointKey(candidate.First.EdgeIndex, candidate.First.IsStart);
            var secondKey = new PlacementGraphEndpointKey(candidate.Second.EdgeIndex, candidate.Second.IsStart);
            if (usedEndpoints.Contains(firstKey) || usedEndpoints.Contains(secondKey))
            {
                continue;
            }

            usedEndpoints.Add(firstKey);
            usedEndpoints.Add(secondKey);
            selected.Add(candidate);
        }

        if (selected.Count == 0)
        {
            return new PlacementGraphEndpointPairSnapResult(edges.ToArray(), 0, 0);
        }

        var snappedEdges = edges.ToArray();
        foreach (var snap in selected)
        {
            snappedEdges[snap.First.EdgeIndex] = SnapPlacementGraphEndpointToPoint(
                snappedEdges[snap.First.EdgeIndex],
                snap.First.IsStart,
                snap.Point,
                "placement wall graph endpoint-pair snap",
                $"snapped endpoint to nearby structural endpoint pair at axis intersection; offset {snap.FirstDistance:0.###} drawing units");
            snappedEdges[snap.Second.EdgeIndex] = SnapPlacementGraphEndpointToPoint(
                snappedEdges[snap.Second.EdgeIndex],
                snap.Second.IsStart,
                snap.Point,
                "placement wall graph endpoint-pair snap",
                $"snapped endpoint to nearby structural endpoint pair at axis intersection; offset {snap.SecondDistance:0.###} drawing units");
        }

        return new PlacementGraphEndpointPairSnapResult(
            snappedEdges
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            selected.Count,
            selected.Count * 2);
    }

    private static bool CanSnapNearbyPlacementGraphEndpointPair(
        PlacementGraphMergeSpan first,
        PlacementGraphMergeSpan second)
    {
        if (first.Index == second.Index
            || first.Edge.PageNumber != second.Edge.PageNumber
            || first.Edge.CenterLine is null
            || second.Edge.CenterLine is null
            || first.Edge.ExcludedFromStructuralTopology
            || second.Edge.ExcludedFromStructuralTopology
            || first.Orientation == second.Orientation)
        {
            return false;
        }

        return IsStructuralPlacementGraphComponentKind(first.Edge.WallComponentKind)
            || IsStructuralPlacementGraphComponentKind(second.Edge.WallComponentKind)
            || first.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || second.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence);
    }

    private static PlanPoint PlacementGraphEndpointPairSnapPoint(
        PlacementGraphMergeSpan first,
        PlacementGraphMergeSpan second) =>
        first.Orientation == PlacementGraphEdgeOrientation.Horizontal
            ? new PlanPoint(second.Axis, first.Axis)
            : new PlanPoint(first.Axis, second.Axis);

    private static double PlacementEndpointToEndpointSnapTolerance(
        PlacementGraphMergeSpan first,
        PlacementGraphMergeSpan second)
    {
        var maxThickness = Math.Max(first.Edge.ThicknessDrawingUnits, second.Edge.ThicknessDrawingUnits);
        if (CanUseTrustedExteriorCornerEndpointPairSnap(first, second))
        {
            return Math.Clamp(
                maxThickness * 1.5,
                MaxExteriorShellPlacementEndpointOnWallAbsorptionDistanceDrawingUnits,
                MaxTrustedExteriorCornerEndpointPairSnapDistanceDrawingUnits);
        }

        var maxTolerance = IsTrustedExteriorShellPlacementGraphMergeContinuation(first.Edge)
            || IsTrustedExteriorShellPlacementGraphMergeContinuation(second.Edge)
                ? MaxExteriorShellPlacementEndpointOnWallAbsorptionDistanceDrawingUnits
                : MaxPlacementNodeCoordinateAlignmentDistanceDrawingUnits;
        return Math.Clamp(
            maxThickness / 2.0,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            maxTolerance);
    }

    private static bool CanUseTrustedExteriorCornerEndpointPairSnap(
        PlacementGraphMergeSpan first,
        PlacementGraphMergeSpan second) =>
        first.Length >= MinTrustedExteriorCornerEndpointPairSnapLengthDrawingUnits
        && second.Length >= MinTrustedExteriorCornerEndpointPairSnapLengthDrawingUnits
        && IsTrustedExteriorShellPlacementGraphMergeContinuation(first.Edge)
        && IsTrustedExteriorShellPlacementGraphMergeContinuation(second.Edge)
        && !HasPlacementGraphDetailOrSurfaceEvidence(first.Edge)
        && !HasPlacementGraphDetailOrSurfaceEvidence(second.Edge);

    private static PlacementWallGraphResidualEndpointOnHostCandidateExport ToResidualEndpointOnHostCandidateExport(
        PlacementGraphEndpointOnHostResidual residual)
    {
        var relationship = residual.EndpointSpan.Orientation == residual.HostSpan.Orientation
            ? "SameAxis"
            : "Perpendicular";
        var endpointRole = residual.Endpoint.IsStart ? "Start" : "End";
        var scale = residual.EndpointSpan.Edge.MillimetersPerDrawingUnit
            ?? residual.HostSpan.Edge.MillimetersPerDrawingUnit;
        var repairLine = new PlanLineSegment(residual.Endpoint.Position, residual.HostPoint);
        var severity = residual.Distance <= MaxFinalPlacementEndpointOnWallAbsorptionDistanceDrawingUnits
            || relationship == "SameAxis"
                ? "Warning"
                : "Info";
        var evidence = new[]
        {
            "placement wall graph residual endpoint-on-host-wall candidate after cleanup",
            $"endpoint {endpointRole.ToLowerInvariant()} of edge {residual.EndpointSpan.Edge.Id} is {residual.Distance:0.###} drawing units from host edge {residual.HostSpan.Edge.Id}",
            $"relationship: {relationship}",
            "review before snapping because the endpoint may be a real near-miss, source extraction gap, wall return, opening edge, or non-wall detail"
        };

        return new PlacementWallGraphResidualEndpointOnHostCandidateExport(
            $"residual-endpoint-on-host:{residual.EndpointSpan.Edge.Id}:{endpointRole.ToLowerInvariant()}:{residual.HostSpan.Edge.Id}",
            residual.Endpoint.PageNumber,
            residual.EndpointSpan.Edge.Id,
            residual.HostSpan.Edge.Id,
            residual.EndpointSpan.Edge.WallId,
            residual.HostSpan.Edge.WallId,
            residual.Endpoint.OriginalNodeId,
            endpointRole,
            relationship,
            PointExport.From(residual.Endpoint.Position),
            PointExport.From(residual.HostPoint),
            ScalePoint(residual.Endpoint.Position, scale),
            ScalePoint(residual.HostPoint, scale),
            PlanOverlaySnapshot.Round(residual.Distance),
            ScaleNullable(residual.Distance, scale),
            LineExport.From(repairLine),
            ScaleLine(repairLine, scale),
            severity,
            "Review this residual endpoint before applying an endpoint-to-host-wall snap or changing wall topology.",
            evidence);
    }

    private static PlacementGraphNodeOnEdgeAbsorptionResult AbsorbPlacementGraphEndpointNodesOnHostEdgesCore(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        bool requireSingleUseEndpointNode,
        double? maxDistanceOverride,
        double minHostSplitFraction,
        string evidencePrefix,
        string splitPointEvidence)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphNodeOnEdgeAbsorptionResult(edges.ToArray(), 0, 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphNodeOnEdgeAbsorptionResult(edges.ToArray(), 0, 0);
        }

        var endpointUseCount = observations
            .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphNodeOnEdgeAbsorptionResult(edges.ToArray(), 0, 0);
        }

        var spansByIndex = spans.ToDictionary(span => span.Index);
        var absorptions = observations
            .Where(observation => !requireSingleUseEndpointNode
                || endpointUseCount.GetValueOrDefault(observation.OriginalNodeId) == 1)
            .Select(observation => TryCreatePlacementGraphEndpointOnWallAbsorption(
                observation,
                edges,
                spans,
                spansByIndex,
                maxDistanceOverride,
                minHostSplitFraction))
            .Where(absorption => absorption is not null)
            .Select(absorption => absorption!)
            .GroupBy(absorption => new PlacementGraphEndpointKey(absorption.Endpoint.EdgeIndex, absorption.Endpoint.IsStart))
            .Select(group => group
                .OrderBy(absorption => absorption.Distance)
                .ThenByDescending(absorption => absorption.HostSpan.Length)
                .ThenBy(absorption => absorption.HostSpan.Edge.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(absorption => absorption.Endpoint.EdgeIndex)
            .ThenBy(absorption => absorption.Endpoint.IsStart ? 0 : 1)
            .ToArray();

        if (absorptions.Length == 0)
        {
            return new PlacementGraphNodeOnEdgeAbsorptionResult(edges.ToArray(), 0, 0);
        }

        var snappedEdges = edges.ToArray();
        var splitPointsByHostEdgeIndex = new Dictionary<int, List<PlacementGraphHostSplitPoint>>();
        foreach (var absorption in absorptions)
        {
            snappedEdges[absorption.Endpoint.EdgeIndex] = SnapPlacementGraphEndpointOntoHostWall(
                snappedEdges[absorption.Endpoint.EdgeIndex],
                absorption.Endpoint.IsStart,
                absorption.HostPoint,
                absorption.HostSpan.Edge.Id,
                absorption.Distance,
                evidencePrefix);

            if (!splitPointsByHostEdgeIndex.TryGetValue(absorption.HostSpan.Index, out var splitPoints))
            {
                splitPoints = new List<PlacementGraphHostSplitPoint>();
                splitPointsByHostEdgeIndex[absorption.HostSpan.Index] = splitPoints;
            }

            splitPoints.Add(new PlacementGraphHostSplitPoint(
                absorption.Endpoint.OriginalNodeId,
                absorption.HostCoordinate,
                absorption.HostPoint,
                absorption.Endpoint.EdgeIndex,
                absorption.Distance));
        }

        var resultEdges = new List<PlacementWallGraphEdgeExport>(snappedEdges.Length + absorptions.Length);
        var splitHostEdgeCount = 0;
        for (var index = 0; index < snappedEdges.Length; index++)
        {
            if (splitPointsByHostEdgeIndex.TryGetValue(index, out var splitPoints)
                && TryCreatePlacementGraphMergeSpan(index, snappedEdges[index]) is { } hostSpan)
            {
                var splitEdges = SplitPlacementGraphHostEdgeAtAbsorbedJunctions(
                    snappedEdges[index],
                    hostSpan,
                    splitPoints,
                    evidencePrefix,
                    splitPointEvidence,
                    minHostSplitFraction);
                if (splitEdges.Length > 1)
                {
                    resultEdges.AddRange(splitEdges);
                    splitHostEdgeCount++;
                    continue;
                }
            }

            resultEdges.Add(snappedEdges[index]);
        }

        return new PlacementGraphNodeOnEdgeAbsorptionResult(
            resultEdges
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            absorptions.Length,
            splitHostEdgeCount);
    }

    private static PlacementGraphEndpointOnWallAbsorption? TryCreatePlacementGraphEndpointOnWallAbsorption(
        PlacementGraphEndpointObservation endpoint,
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        IReadOnlyList<PlacementGraphMergeSpan> hostSpans,
        IReadOnlyDictionary<int, PlacementGraphMergeSpan> spansByIndex,
        double? maxDistanceOverride = null,
        double minHostSplitFraction = MinPlacementEndpointOnWallSplitFraction)
    {
        if (!spansByIndex.TryGetValue(endpoint.EdgeIndex, out var endpointSpan)
            || endpointSpan.Length < MinPlacementEndpointOnWallCandidateLengthDrawingUnits)
        {
            return null;
        }

        PlacementGraphEndpointOnWallAbsorption? best = null;
        foreach (var hostSpan in hostSpans)
        {
            if (!CanAbsorbPlacementGraphEndpointOntoHostSpan(endpointSpan, hostSpan))
            {
                continue;
            }

            var splitCoordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                ? endpoint.Position.X
                : endpoint.Position.Y;
            var minHostMargin = Math.Max(
                MinPlacementEndpointOnWallSplitDistanceDrawingUnits,
                hostSpan.Length * minHostSplitFraction);
            if (splitCoordinate <= hostSpan.Start + minHostMargin
                || splitCoordinate >= hostSpan.End - minHostMargin)
            {
                continue;
            }

            var hostPoint = PointOnPlacementGraphSpan(hostSpan, splitCoordinate);
            var distance = endpoint.Position.DistanceTo(hostPoint);
            var maxDistance = maxDistanceOverride
                ?? PlacementEndpointOnWallAbsorptionTolerance(endpointSpan.Edge, hostSpan.Edge);
            if (distance > maxDistance)
            {
                continue;
            }

            var candidate = new PlacementGraphEndpointOnWallAbsorption(
                endpoint,
                hostSpan,
                splitCoordinate,
                hostPoint,
                distance);
            if (best is null
                || candidate.Distance < best.Distance
                || (Math.Abs(candidate.Distance - best.Distance) <= 0.001
                    && candidate.HostSpan.Length > best.HostSpan.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static PlacementGraphSharedNodeHostSnapResult SnapPlacementGraphSharedNodesOntoHostEdges(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        if (edges.Count <= 1)
        {
            return new PlacementGraphSharedNodeHostSnapResult(edges.ToArray(), 0, 0, 0);
        }

        var observations = BuildPlacementGraphEndpointObservations(edges);
        if (observations.Count == 0)
        {
            return new PlacementGraphSharedNodeHostSnapResult(edges.ToArray(), 0, 0, 0);
        }

        var spans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .ToArray();
        if (spans.Length <= 1)
        {
            return new PlacementGraphSharedNodeHostSnapResult(edges.ToArray(), 0, 0, 0);
        }

        var spansByIndex = spans.ToDictionary(span => span.Index);
        var snaps = new Dictionary<string, PlacementGraphSharedNodeHostSnap>(StringComparer.Ordinal);
        foreach (var group in observations
                     .Where(observation => !string.IsNullOrWhiteSpace(observation.OriginalNodeId))
                     .GroupBy(observation => observation.OriginalNodeId, StringComparer.Ordinal))
        {
            var nodeObservations = group.ToArray();
            if (nodeObservations.Length <= 1)
            {
                continue;
            }

            var incidentSpans = nodeObservations
                .Select(observation => spansByIndex.TryGetValue(observation.EdgeIndex, out var span) ? span : null)
                .Where(span => span is not null)
                .Select(span => span!)
                .DistinctBy(span => span.Index)
                .ToArray();
            if (incidentSpans.Length == 0)
            {
                continue;
            }

            var point = AveragePoint(nodeObservations.Select(observation => observation.Position).ToArray());
            PlacementGraphSharedNodeHostSnap? best = null;
            foreach (var hostSpan in spans)
            {
                if (!CanSnapSharedPlacementNodeOntoHostSpan(incidentSpans, hostSpan))
                {
                    continue;
                }

                var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                    ? point.X
                    : point.Y;
                var minHostMargin = Math.Max(
                    MinPlacementEndpointOnWallSplitDistanceDrawingUnits,
                    hostSpan.Length * MinPlacementEndpointOnWallSplitFraction);
                if (coordinate <= hostSpan.Start + minHostMargin
                    || coordinate >= hostSpan.End - minHostMargin)
                {
                    continue;
                }

                var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
                var distance = point.DistanceTo(hostPoint);
                if (distance > PlacementSharedNodeHostSnapTolerance(incidentSpans, hostSpan))
                {
                    continue;
                }

                var candidate = new PlacementGraphSharedNodeHostSnap(group.Key, hostSpan, hostPoint, distance);
                if (best is null
                    || candidate.Distance < best.Distance
                    || (Math.Abs(candidate.Distance - best.Distance) <= 0.001
                        && candidate.HostSpan.Length > best.HostSpan.Length))
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                snaps[group.Key] = best;
            }
        }

        if (snaps.Count == 0)
        {
            return new PlacementGraphSharedNodeHostSnapResult(edges.ToArray(), 0, 0, 0);
        }

        var splitPointsByHostEdgeIndex = snaps.Values
            .GroupBy(snap => snap.HostSpan.Index)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(snap =>
                    {
                        var coordinate = snap.HostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                            ? snap.Point.X
                            : snap.Point.Y;
                        return new PlacementGraphHostSplitPoint(
                            snap.NodeId,
                            coordinate,
                            snap.Point,
                            EndpointEdgeIndex: -1,
                            snap.Distance);
                    })
                    .ToList());

        var snappedEndpointCount = 0;
        var snappedEdges = edges
            .Select(edge =>
            {
                if (edge.CenterLine is null)
                {
                    return edge;
                }

                var start = ToPlanPoint(edge.CenterLine.Start);
                var end = ToPlanPoint(edge.CenterLine.End);
                var snappedEndpointNames = new List<string>(capacity: 2);
                var hostEdgeIds = new List<string>(capacity: 2);
                var offsets = new List<double>(capacity: 2);

                if (snaps.TryGetValue(edge.FromNodeId, out var startSnap)
                    && start.DistanceTo(startSnap.Point) > 0.001)
                {
                    start = startSnap.Point;
                    snappedEndpointCount++;
                    snappedEndpointNames.Add("start");
                    hostEdgeIds.Add(startSnap.HostSpan.Edge.Id);
                    offsets.Add(startSnap.Distance);
                }

                if (snaps.TryGetValue(edge.ToNodeId, out var endSnap)
                    && end.DistanceTo(endSnap.Point) > 0.001)
                {
                    end = endSnap.Point;
                    snappedEndpointCount++;
                    snappedEndpointNames.Add("end");
                    hostEdgeIds.Add(endSnap.HostSpan.Edge.Id);
                    offsets.Add(endSnap.Distance);
                }

                if (snappedEndpointNames.Count == 0)
                {
                    return edge;
                }

                var line = new PlanLineSegment(start, end);
                if (line.Length <= 0.001)
                {
                    return edge;
                }

                var maxOffset = offsets.Count == 0 ? 0 : offsets.Max();
                var bounds = BoundsForPlacementGraphLine(line, edge.ThicknessDrawingUnits);
                var scale = edge.MillimetersPerDrawingUnit;
                var evidence = edge.Evidence
                    .Append(
                        "placement wall graph shared-node host snap: "
                        + $"snapped {string.Join("+", snappedEndpointNames)} endpoint coordinate(s) "
                        + $"onto host edge(s) {string.Join(",", hostEdgeIds.Distinct(StringComparer.Ordinal))}; "
                        + $"max offset {maxOffset:0.###} drawing units")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return edge with
                {
                    CenterLine = LineExport.From(line),
                    CenterLineMillimeters = ScaleLine(line, scale),
                    Bounds = RectExport.From(bounds),
                    BoundsMillimeters = ScaleRect(bounds, scale),
                    DrawingLength = line.Length,
                    LengthMeters = scale is > 0 ? line.Length * scale.Value / 1000.0 : null,
                    Evidence = evidence
                };
            })
            .ToArray();

        var resultEdges = new List<PlacementWallGraphEdgeExport>(snappedEdges.Length + snaps.Count);
        var splitHostEdgeCount = 0;
        for (var index = 0; index < snappedEdges.Length; index++)
        {
            if (splitPointsByHostEdgeIndex.TryGetValue(index, out var splitPoints)
                && TryCreatePlacementGraphMergeSpan(index, snappedEdges[index]) is { } hostSpan)
            {
                var splitEdges = SplitPlacementGraphHostEdgeAtAbsorbedJunctions(
                    snappedEdges[index],
                    hostSpan,
                    splitPoints,
                    "placement wall graph shared-node host snap",
                    "host split uses shared junction node");
                if (splitEdges.Length > 1)
                {
                    resultEdges.AddRange(splitEdges);
                    splitHostEdgeCount++;
                    continue;
                }
            }

            resultEdges.Add(snappedEdges[index]);
        }

        return new PlacementGraphSharedNodeHostSnapResult(
            resultEdges
                .OrderBy(edge => edge.PageNumber)
                .ThenBy(edge => edge.Bounds?.Y ?? double.MaxValue)
                .ThenBy(edge => edge.Bounds?.X ?? double.MaxValue)
                .ThenBy(edge => edge.Id, StringComparer.Ordinal)
                .ToArray(),
            snaps.Count,
            snappedEndpointCount,
            splitHostEdgeCount);
    }

    private static bool CanSnapSharedPlacementNodeOntoHostSpan(
        IReadOnlyList<PlacementGraphMergeSpan> incidentSpans,
        PlacementGraphMergeSpan hostSpan)
    {
        if (hostSpan.Edge.CenterLine is null
            || hostSpan.Edge.ExcludedFromStructuralTopology
            || hostSpan.Length <= MinPlacementEndpointOnWallSplitDistanceDrawingUnits * 2.0
            || incidentSpans.Any(span => span.Index == hostSpan.Index))
        {
            return false;
        }

        if (!incidentSpans.Any(span => span.Orientation != hostSpan.Orientation))
        {
            return false;
        }

        return IsStructuralPlacementGraphComponentKind(hostSpan.Edge.WallComponentKind)
            || hostSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || incidentSpans.Any(span =>
                IsStructuralPlacementGraphComponentKind(span.Edge.WallComponentKind)
                || span.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence));
    }

    private static double PlacementSharedNodeHostSnapTolerance(
        IReadOnlyList<PlacementGraphMergeSpan> incidentSpans,
        PlacementGraphMergeSpan hostSpan)
    {
        var maxThickness = incidentSpans
            .Select(span => span.Edge.ThicknessDrawingUnits)
            .Append(hostSpan.Edge.ThicknessDrawingUnits)
            .DefaultIfEmpty(0)
            .Max();
        var maxTolerance = IsTrustedExteriorShellPlacementGraphMergeContinuation(hostSpan.Edge)
            || incidentSpans.Any(span => IsTrustedExteriorShellPlacementGraphMergeContinuation(span.Edge))
                ? MaxExteriorShellPlacementSharedNodeHostSnapDistanceDrawingUnits
                : MaxPlacementSharedNodeHostSnapDistanceDrawingUnits;
        return Math.Clamp(
            maxThickness / 2.0,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            maxTolerance);
    }

    private static bool CanAbsorbPlacementGraphEndpointOntoHostSpan(
        PlacementGraphMergeSpan endpointSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        if (endpointSpan.Index == hostSpan.Index
            || endpointSpan.Edge.PageNumber != hostSpan.Edge.PageNumber
            || endpointSpan.Edge.CenterLine is null
            || hostSpan.Edge.CenterLine is null
            || endpointSpan.Edge.ExcludedFromStructuralTopology
            || hostSpan.Edge.ExcludedFromStructuralTopology
            || endpointSpan.Orientation == hostSpan.Orientation
            || hostSpan.Length <= MinPlacementEndpointOnWallSplitDistanceDrawingUnits * 2.0)
        {
            return false;
        }

        return IsStructuralPlacementGraphComponentKind(endpointSpan.Edge.WallComponentKind)
            || IsStructuralPlacementGraphComponentKind(hostSpan.Edge.WallComponentKind)
            || endpointSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || hostSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence);
    }

    private static bool CanSnapResidualPlacementGraphEndpointOntoHostSpan(
        PlacementGraphMergeSpan endpointSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        if (endpointSpan.Orientation != hostSpan.Orientation)
        {
            return CanAbsorbPlacementGraphEndpointOntoHostSpan(endpointSpan, hostSpan);
        }

        if (endpointSpan.Index == hostSpan.Index
            || endpointSpan.Edge.PageNumber != hostSpan.Edge.PageNumber
            || string.Equals(endpointSpan.Edge.WallId, hostSpan.Edge.WallId, StringComparison.Ordinal)
            || endpointSpan.Edge.CenterLine is null
            || hostSpan.Edge.CenterLine is null
            || endpointSpan.Edge.ExcludedFromStructuralTopology
            || hostSpan.Edge.ExcludedFromStructuralTopology
            || hostSpan.Length <= endpointSpan.Length * MinSameAxisResidualEndpointHostLengthRatio)
        {
            return false;
        }

        if (!IsStructuralPlacementGraphMergeContinuation(hostSpan.Edge)
            && !IsStructuralPlacementGraphMergeContinuation(endpointSpan.Edge))
        {
            return false;
        }

        return PlacementGraphSpanProjectionOverlapRatio(endpointSpan, hostSpan)
            >= MinSameAxisResidualEndpointHostOverlapRatio;
    }

    private static double PlacementGraphSpanProjectionOverlapRatio(
        PlacementGraphMergeSpan candidate,
        PlacementGraphMergeSpan host)
    {
        if (candidate.Orientation != host.Orientation || candidate.Length <= 0.001)
        {
            return 0;
        }

        var overlap = Math.Min(candidate.End, host.End) - Math.Max(candidate.Start, host.Start);
        return Math.Max(0, overlap) / candidate.Length;
    }

    private static bool IsExteriorPlacementGraphEvidence(string evidence) =>
        evidence.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("exterior shell", StringComparison.OrdinalIgnoreCase);

    private static double PlacementEndpointOnWallAbsorptionTolerance(
        PlacementWallGraphEdgeExport endpointEdge,
        PlacementWallGraphEdgeExport hostEdge)
    {
        var maxThickness = Math.Max(endpointEdge.ThicknessDrawingUnits, hostEdge.ThicknessDrawingUnits);
        var maxTolerance = IsTrustedExteriorShellPlacementGraphMergeContinuation(endpointEdge)
            || IsTrustedExteriorShellPlacementGraphMergeContinuation(hostEdge)
                ? MaxExteriorShellPlacementEndpointOnWallAbsorptionDistanceDrawingUnits
                : MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits;
        return Math.Clamp(
            maxThickness / 4.0,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            maxTolerance);
    }

    private static double PlacementResidualEndpointOnHostSnapTolerance(
        PlacementGraphMergeSpan endpointSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        if (endpointSpan.Orientation != hostSpan.Orientation)
        {
            if (!CanUseExtendedPerpendicularResidualEndpointSnapTolerance(endpointSpan, hostSpan))
            {
                return MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits;
            }

            var perpendicularMaxThickness = Math.Max(
                endpointSpan.Edge.ThicknessDrawingUnits,
                hostSpan.Edge.ThicknessDrawingUnits);
            return Math.Clamp(
                perpendicularMaxThickness,
                MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits,
                MaxPlacementSharedNodeHostSnapDistanceDrawingUnits);
        }

        var maxThickness = Math.Max(
            endpointSpan.Edge.ThicknessDrawingUnits,
            hostSpan.Edge.ThicknessDrawingUnits);
        var maxTolerance = IsTrustedExteriorShellPlacementGraphMergeContinuation(endpointSpan.Edge)
            || IsTrustedExteriorShellPlacementGraphMergeContinuation(hostSpan.Edge)
                ? MaxExteriorShellPlacementSharedNodeHostSnapDistanceDrawingUnits
                : MaxPlacementSharedNodeHostSnapDistanceDrawingUnits;
        return Math.Clamp(
            maxThickness * 0.75,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            maxTolerance);
    }

    private static bool CanUseExtendedPerpendicularResidualEndpointSnapTolerance(
        PlacementGraphMergeSpan endpointSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        if (CanUseTrustedSourceBackedExteriorResidualEndpointSnap(endpointSpan, hostSpan))
        {
            return true;
        }

        if (!IsStructuralPlacementGraphComponentKind(endpointSpan.Edge.WallComponentKind))
        {
            return false;
        }

        if (!IsStructuralPlacementGraphComponentKind(hostSpan.Edge.WallComponentKind)
            && !IsTrustedSourceBackedPlacementGraphHost(hostSpan.Edge)
            && !IsTrustedExteriorShellPlacementGraphMergeContinuation(hostSpan.Edge))
        {
            return false;
        }

        return !endpointSpan.Edge.Evidence.Concat(hostSpan.Edge.Evidence)
            .Any(IsPlacementGraphDetailOrSurfaceEvidence);
    }

    private static bool CanUseTrustedSourceBackedExteriorResidualEndpointSnap(
        PlacementGraphMergeSpan endpointSpan,
        PlacementGraphMergeSpan hostSpan)
    {
        if (endpointSpan.Orientation == hostSpan.Orientation
            || endpointSpan.Length < MinDominantPlacementGraphMergeAxisLengthDrawingUnits
            || hostSpan.Length < MinDominantPlacementGraphMergeAxisLengthDrawingUnits)
        {
            return false;
        }

        if (!IsTrustedSourceBackedPlacementGraphHost(endpointSpan.Edge)
            || !IsTrustedSourceBackedPlacementGraphHost(hostSpan.Edge)
            || !endpointSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence)
            || !hostSpan.Edge.Evidence.Any(IsExteriorPlacementGraphEvidence))
        {
            return false;
        }

        return !endpointSpan.Edge.Evidence.Concat(hostSpan.Edge.Evidence)
            .Any(IsPlacementGraphDetailOrSurfaceEvidence);
    }

    private static bool IsTrustedSourceBackedPlacementGraphHost(PlacementWallGraphEdgeExport edge) =>
        edge.Evidence.Any(item => item.Contains("source-backed clean placement fallback", StringComparison.OrdinalIgnoreCase))
        && edge.Evidence.Any(item =>
            item.Contains("paired wall-face evidence is placement-ready", StringComparison.OrdinalIgnoreCase)
            || item.Contains("source-backed fallback pair score", StringComparison.OrdinalIgnoreCase)
            || item.Contains("source-backed exterior shell closure", StringComparison.OrdinalIgnoreCase)
            || item.Contains("exterior shell repair promoted", StringComparison.OrdinalIgnoreCase)
            || item.Contains("trusted two-sided fragment-merged room boundary", StringComparison.OrdinalIgnoreCase));

    private static PlanPoint PointOnPlacementGraphSpan(PlacementGraphMergeSpan span, double coordinate) =>
        span.Orientation == PlacementGraphEdgeOrientation.Horizontal
            ? new PlanPoint(coordinate, span.Axis)
            : new PlanPoint(span.Axis, coordinate);

    private static PlacementWallGraphEdgeExport SnapPlacementGraphEndpointOntoHostWall(
        PlacementWallGraphEdgeExport edge,
        bool isStart,
        PlanPoint snappedPoint,
        string hostEdgeId,
        double distance,
        string evidencePrefix = "placement wall graph endpoint-on-wall absorption") =>
        SnapPlacementGraphEndpointToPoint(
            edge,
            isStart,
            snappedPoint,
            evidencePrefix,
            $"snapped {(isStart ? "start" : "end")} endpoint onto host edge {hostEdgeId}; "
            + $"offset {distance:0.###} drawing units");

    private static PlacementWallGraphEdgeExport SnapPlacementGraphEndpointToPoint(
        PlacementWallGraphEdgeExport edge,
        bool isStart,
        PlanPoint snappedPoint,
        string evidencePrefix,
        string evidenceDetail)
    {
        if (edge.CenterLine is null)
        {
            return edge;
        }

        var originalLine = new PlanLineSegment(ToPlanPoint(edge.CenterLine.Start), ToPlanPoint(edge.CenterLine.End));
        var line = isStart
            ? new PlanLineSegment(snappedPoint, originalLine.End)
            : new PlanLineSegment(originalLine.Start, snappedPoint);
        var bounds = BoundsForPlacementGraphLine(line, edge.ThicknessDrawingUnits);
        var scale = edge.MillimetersPerDrawingUnit;
        var evidence = edge.Evidence
            .Append($"{evidencePrefix}: {evidenceDetail}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return edge with
        {
            CenterLine = LineExport.From(line),
            CenterLineMillimeters = ScaleLine(line, scale),
            Bounds = RectExport.From(bounds),
            BoundsMillimeters = ScaleRect(bounds, scale),
            DrawingLength = line.Length,
            LengthMeters = scale is > 0 ? line.Length * scale.Value / 1000.0 : null,
            Evidence = evidence
        };
    }

    private static PlacementWallGraphEdgeExport[] SplitPlacementGraphHostEdgeAtAbsorbedJunctions(
        PlacementWallGraphEdgeExport edge,
        PlacementGraphMergeSpan hostSpan,
        IReadOnlyList<PlacementGraphHostSplitPoint> splitPoints,
        string evidencePrefix = "placement wall graph endpoint-on-wall absorption",
        string splitPointEvidence = "host split uses absorbed node",
        double minHostSplitFraction = MinPlacementEndpointOnWallSplitFraction)
    {
        var distinctSplitPoints = DistinctPlacementGraphHostSplitPoints(hostSpan, splitPoints, minHostSplitFraction);
        if (distinctSplitPoints.Count == 0)
        {
            return [edge];
        }

        var pieces = new List<PlacementWallGraphEdgeExport>();
        var startCoordinate = hostSpan.Start;
        var startNodeId = hostSpan.StartNodeId;
        var pieceIndex = 1;
        foreach (var splitPoint in distinctSplitPoints)
        {
            AddPiece(startCoordinate, splitPoint.Coordinate, startNodeId, splitPoint.NodeId, splitPoint);
            startCoordinate = splitPoint.Coordinate;
            startNodeId = splitPoint.NodeId;
        }

        AddPiece(startCoordinate, hostSpan.End, startNodeId, hostSpan.EndNodeId, null);
        return pieces.ToArray();

        void AddPiece(
            double fromCoordinate,
            double toCoordinate,
            string fromNodeId,
            string toNodeId,
            PlacementGraphHostSplitPoint? splitPoint)
        {
            if (toCoordinate - fromCoordinate <= 0.001)
            {
                return;
            }

            var line = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                ? new PlanLineSegment(
                    new PlanPoint(fromCoordinate, hostSpan.Axis),
                    new PlanPoint(toCoordinate, hostSpan.Axis))
                : new PlanLineSegment(
                    new PlanPoint(hostSpan.Axis, fromCoordinate),
                    new PlanPoint(hostSpan.Axis, toCoordinate));
            var bounds = BoundsForMergedPlacementGraphRun(line, hostSpan.Orientation, edge.ThicknessDrawingUnits);
            var scale = edge.MillimetersPerDrawingUnit;
            var evidence = edge.Evidence
                .Append(
                    $"{evidencePrefix}: split host edge {edge.Id} into junction piece {pieceIndex} of {distinctSplitPoints.Count + 1}")
                .Concat(splitPoint is null
                    ? Array.Empty<string>()
                    : new[]
                    {
                        splitPoint.EndpointEdgeIndex >= 0
                            ? $"{evidencePrefix}: {splitPointEvidence} {splitPoint.NodeId} from edge index {splitPoint.EndpointEdgeIndex}"
                            : $"{evidencePrefix}: {splitPointEvidence} {splitPoint.NodeId}"
                    })
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            pieces.Add(edge with
            {
                Id = $"{edge.Id}:junction-piece:{pieceIndex}",
                FromNodeId = fromNodeId,
                ToNodeId = toNodeId,
                CenterLine = LineExport.From(line),
                CenterLineMillimeters = ScaleLine(line, scale),
                Bounds = RectExport.From(bounds),
                BoundsMillimeters = ScaleRect(bounds, scale),
                DrawingLength = line.Length,
                LengthMeters = scale is > 0 ? line.Length * scale.Value / 1000.0 : null,
                Evidence = evidence
            });
            pieceIndex++;
        }
    }

    private static IReadOnlyList<PlacementGraphHostSplitPoint> DistinctPlacementGraphHostSplitPoints(
        PlacementGraphMergeSpan hostSpan,
        IReadOnlyList<PlacementGraphHostSplitPoint> splitPoints,
        double minHostSplitFraction = MinPlacementEndpointOnWallSplitFraction)
    {
        var minHostMargin = Math.Max(
            MinPlacementEndpointOnWallSplitDistanceDrawingUnits,
            hostSpan.Length * minHostSplitFraction);
        var distinct = new List<PlacementGraphHostSplitPoint>();
        foreach (var splitPoint in splitPoints
            .Where(splitPoint => splitPoint.Coordinate > hostSpan.Start + minHostMargin
                && splitPoint.Coordinate < hostSpan.End - minHostMargin)
            .OrderBy(splitPoint => splitPoint.Coordinate)
            .ThenBy(splitPoint => splitPoint.NodeId, StringComparer.Ordinal))
        {
            if (distinct.Count > 0
                && Math.Abs(splitPoint.Coordinate - distinct[^1].Coordinate) <= MaxCoincidentPlacementNodeDistanceDrawingUnits)
            {
                continue;
            }

            distinct.Add(splitPoint);
        }

        return distinct;
    }

    private static PlanRect BoundsForPlacementGraphLine(PlanLineSegment line, double thickness)
    {
        var padding = Math.Max(thickness, 0.001) / 2.0;
        return line.Bounds.Inflate(padding);
    }

    private static IReadOnlyList<PlacementGraphEndpointObservation> BuildPlacementGraphEndpointObservations(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges)
    {
        var observations = new List<PlacementGraphEndpointObservation>();
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            if (edge.CenterLine is null)
            {
                continue;
            }

            observations.Add(new PlacementGraphEndpointObservation(
                index,
                IsStart: true,
                edge.FromNodeId,
                edge.PageNumber,
                ToPlanPoint(edge.CenterLine.Start)));
            observations.Add(new PlacementGraphEndpointObservation(
                index,
                IsStart: false,
                edge.ToNodeId,
                edge.PageNumber,
                ToPlanPoint(edge.CenterLine.End)));
        }

        return observations;
    }

    private static IReadOnlyList<IReadOnlyList<PlacementGraphEndpointObservation>> BuildPlacementGraphEndpointClusters(
        IReadOnlyList<PlacementGraphEndpointObservation> observations)
    {
        var clusters = new List<List<PlacementGraphEndpointObservation>>();
        foreach (var observation in observations
            .OrderBy(item => item.PageNumber)
            .ThenBy(item => item.Position.Y)
            .ThenBy(item => item.Position.X)
            .ThenBy(item => item.OriginalNodeId, StringComparer.Ordinal)
            .ThenBy(item => item.EdgeIndex)
            .ThenBy(item => item.IsStart ? 0 : 1))
        {
            var cluster = clusters.FirstOrDefault(candidate =>
                candidate[0].PageNumber == observation.PageNumber
                && ClusterCentroid(candidate).DistanceTo(observation.Position) <= MaxCoincidentPlacementNodeDistanceDrawingUnits);
            if (cluster is null)
            {
                clusters.Add([observation]);
                continue;
            }

            cluster.Add(observation);
        }

        return clusters;
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<int>> BuildPlacementGraphNodeOriginalClusterUse(
        IReadOnlyList<IReadOnlyList<PlacementGraphEndpointObservation>> clusters)
    {
        var usage = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        for (var index = 0; index < clusters.Count; index++)
        {
            foreach (var nodeId in clusters[index]
                .Select(item => item.OriginalNodeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal))
            {
                if (!usage.TryGetValue(nodeId, out var indexes))
                {
                    indexes = new HashSet<int>();
                    usage[nodeId] = indexes;
                }

                indexes.Add(index);
            }
        }

        return usage.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<int>)item.Value,
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> BuildPlacementGraphCanonicalNodeIds(
        IReadOnlyList<IReadOnlyList<PlacementGraphEndpointObservation>> clusters,
        IReadOnlyDictionary<string, IReadOnlySet<int>> originalClusterUse)
    {
        var preferredReusableClusterByNodeId = PreferredReusableClusterByNodeId(clusters, originalClusterUse);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var canonical = new string[clusters.Count];
        for (var index = 0; index < clusters.Count; index++)
        {
            var cluster = clusters[index];
            var stableSingleUse = cluster
                .GroupBy(item => item.OriginalNodeId, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                    && originalClusterUse.TryGetValue(group.Key!, out var usedClusters)
                    && usedClusters.Count == 1
                    && !used.Contains(group.Key!))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key!.Length)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key!)
                .FirstOrDefault();
            if (stableSingleUse is not null)
            {
                canonical[index] = stableSingleUse;
                used.Add(stableSingleUse);
                continue;
            }

            var reusable = cluster
                .GroupBy(item => item.OriginalNodeId, StringComparer.Ordinal)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key)
                    && preferredReusableClusterByNodeId.TryGetValue(group.Key!, out var preferredIndex)
                    && preferredIndex == index
                    && !used.Contains(group.Key!))
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key!.Length)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key!)
                .FirstOrDefault();
            if (reusable is not null)
            {
                canonical[index] = reusable;
                used.Add(reusable);
                continue;
            }

            var derived = DerivedPlacementNodeId(cluster, index);
            while (!used.Add(derived))
            {
                derived = $"{derived}:dup";
            }

            canonical[index] = derived;
        }

        return canonical;
    }

    private static IReadOnlyDictionary<string, int> PreferredReusableClusterByNodeId(
        IReadOnlyList<IReadOnlyList<PlacementGraphEndpointObservation>> clusters,
        IReadOnlyDictionary<string, IReadOnlySet<int>> originalClusterUse)
    {
        var preferred = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in originalClusterUse.Where(item => item.Value.Count > 1))
        {
            preferred[item.Key] = item.Value
                .OrderByDescending(index => clusters[index].Count(observation =>
                    string.Equals(observation.OriginalNodeId, item.Key, StringComparison.Ordinal)))
                .ThenBy(index => index)
                .First();
        }

        return preferred;
    }

    private static string DerivedPlacementNodeId(
        IReadOnlyList<PlacementGraphEndpointObservation> cluster,
        int clusterIndex)
    {
        var pageNumber = cluster.Count == 0 ? 1 : cluster[0].PageNumber;
        return string.Create(CultureInfo.InvariantCulture, $"page:{pageNumber}:placement-node:{clusterIndex + 1:0000}");
    }

    private static PlanPoint ClusterCentroid(IReadOnlyList<PlacementGraphEndpointObservation> cluster) =>
        new(cluster.Average(item => item.Position.X), cluster.Average(item => item.Position.Y));

    private static IReadOnlyList<PlacementWallGraphNodeExport> BuildPlacementWallGraphNodes(
        WallGraph graph,
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        PlanCalibration calibration)
    {
        var rawNodesById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var observations = new Dictionary<string, List<PlanPoint>>(StringComparer.Ordinal);
        var pageNumbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var directionLookup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var cleanObservationCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (edge.CenterLine is null)
            {
                AddRawNode(edge.FromNodeId);
                AddRawNode(edge.ToNodeId);
                continue;
            }

            var start = ToPlanPoint(edge.CenterLine.Start);
            var end = ToPlanPoint(edge.CenterLine.End);
            AddObservation(edge.FromNodeId, edge.PageNumber, start, isCleanObservation: true);
            AddObservation(edge.ToNodeId, edge.PageNumber, end, isCleanObservation: true);
            AddDirection(edge.FromNodeId, start, end);
            AddDirection(edge.ToNodeId, end, start);
        }

        var interiorAttachmentsByNodeId = BuildPlacementGraphInteriorNodeAttachments(edges, observations);

        return observations
            .Select(item =>
            {
                rawNodesById.TryGetValue(item.Key, out var rawNode);
                var position = AveragePoint(item.Value);
                var interiorAttachments = interiorAttachmentsByNodeId.TryGetValue(item.Key, out var foundInteriorAttachments)
                    ? foundInteriorAttachments
                    : Array.Empty<PlacementGraphInteriorNodeAttachment>();
                var directions = directionLookup.TryGetValue(item.Key, out var foundDirections)
                    ? foundDirections
                        .Concat(interiorAttachments.SelectMany(attachment => attachment.Directions))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(DirectionSortOrder)
                        .ToArray()
                    : rawNode?.Directions ?? Array.Empty<string>();
                var degree = edges.Count(edge => edge.FromNodeId == item.Key || edge.ToNodeId == item.Key)
                    + interiorAttachments.Sum(attachment => attachment.Directions.Count);
                var evidence = (rawNode?.Evidence ?? Array.Empty<string>())
                    .Append("placement wall graph node normalized from cleaned topology endpoint(s)")
                    .Append($"clean topology endpoint observations: {cleanObservationCounts.GetValueOrDefault(item.Key)}")
                    .Concat(interiorAttachments.Select(attachment =>
                        "placement wall graph node attached to host wall edge "
                        + $"{attachment.HostEdgeId} without splitting long wall run; "
                        + $"offset {attachment.Distance:0.###} drawing units"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                return PlacementWallGraphNodeExport.From(
                    item.Key,
                    pageNumbers[item.Key],
                    position,
                    ClassifyPlacementNode(degree, directions),
                    degree,
                    directions,
                    rawNode?.Confidence ?? (degree > 1 ? Confidence.High : Confidence.Medium),
                    evidence,
                    calibration);
            })
            .OrderBy(node => node.PageNumber)
            .ThenBy(node => node.Position.Y)
            .ThenBy(node => node.Position.X)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();

        void AddRawNode(string nodeId)
        {
            if (!rawNodesById.TryGetValue(nodeId, out var rawNode))
            {
                return;
            }

            AddObservation(nodeId, rawNode.PageNumber, rawNode.Position, isCleanObservation: false);
            foreach (var direction in rawNode.Directions)
            {
                AddDirectionName(nodeId, direction);
            }
        }

        void AddObservation(string nodeId, int pageNumber, PlanPoint position, bool isCleanObservation)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return;
            }

            if (!observations.TryGetValue(nodeId, out var points))
            {
                points = new List<PlanPoint>();
                observations[nodeId] = points;
                pageNumbers[nodeId] = pageNumber;
            }

            points.Add(position);
            if (isCleanObservation)
            {
                cleanObservationCounts[nodeId] = cleanObservationCounts.TryGetValue(nodeId, out var count)
                    ? count + 1
                    : 1;
            }
        }

        void AddDirection(string nodeId, PlanPoint from, PlanPoint to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var direction = Math.Abs(dx) <= double.Epsilon && Math.Abs(dy) <= double.Epsilon
                ? "Other"
                : Math.Abs(dx) >= Math.Abs(dy)
                    ? dx >= 0 ? "East" : "West"
                    : dy >= 0 ? "South" : "North";
            AddDirectionName(nodeId, direction);
        }

        void AddDirectionName(string nodeId, string direction)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(direction))
            {
                return;
            }

            if (!directionLookup.TryGetValue(nodeId, out var directions))
            {
                directions = new List<string>();
                directionLookup[nodeId] = directions;
            }

            directions.Add(direction);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<PlacementGraphInteriorNodeAttachment>> BuildPlacementGraphInteriorNodeAttachments(
        IReadOnlyList<PlacementWallGraphEdgeExport> edges,
        IReadOnlyDictionary<string, List<PlanPoint>> observations)
    {
        if (edges.Count <= 1 || observations.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<PlacementGraphInteriorNodeAttachment>>(StringComparer.Ordinal);
        }

        var hostSpans = edges
            .Select((edge, index) => TryCreatePlacementGraphMergeSpan(index, edge))
            .Where(span => span is not null)
            .Select(span => span!)
            .OrderByDescending(span => span.Length)
            .ToArray();
        if (hostSpans.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<PlacementGraphInteriorNodeAttachment>>(StringComparer.Ordinal);
        }

        var attachmentsByNodeId = new Dictionary<string, List<PlacementGraphInteriorNodeAttachment>>(StringComparer.Ordinal);
        foreach (var item in observations)
        {
            var nodeId = item.Key;
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            var point = AveragePoint(item.Value);
            foreach (var hostSpan in hostSpans)
            {
                if (string.Equals(hostSpan.StartNodeId, nodeId, StringComparison.Ordinal)
                    || string.Equals(hostSpan.EndNodeId, nodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                var coordinate = hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                    ? point.X
                    : point.Y;
                var minHostMargin = MinPlacementEndpointOnWallSplitDistanceDrawingUnits;
                if (coordinate <= hostSpan.Start + minHostMargin
                    || coordinate >= hostSpan.End - minHostMargin)
                {
                    continue;
                }

                var hostPoint = PointOnPlacementGraphSpan(hostSpan, coordinate);
                var distance = point.DistanceTo(hostPoint);
                if (distance > PlacementInteriorNodeHostAttachmentTolerance(hostSpan.Edge))
                {
                    continue;
                }

                if (!attachmentsByNodeId.TryGetValue(nodeId, out var attachments))
                {
                    attachments = new List<PlacementGraphInteriorNodeAttachment>();
                    attachmentsByNodeId[nodeId] = attachments;
                }

                if (attachments.Any(attachment => string.Equals(attachment.HostEdgeId, hostSpan.Edge.Id, StringComparison.Ordinal)))
                {
                    continue;
                }

                attachments.Add(new PlacementGraphInteriorNodeAttachment(
                    hostSpan.Edge.Id,
                    hostSpan.Orientation == PlacementGraphEdgeOrientation.Horizontal
                        ? new[] { "East", "West" }
                        : new[] { "North", "South" },
                    distance));
            }
        }

        return attachmentsByNodeId.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<PlacementGraphInteriorNodeAttachment>)item.Value
                .OrderBy(attachment => attachment.Distance)
                .ThenBy(attachment => attachment.HostEdgeId, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static double PlacementInteriorNodeHostAttachmentTolerance(PlacementWallGraphEdgeExport hostEdge) =>
        Math.Clamp(
            hostEdge.ThicknessDrawingUnits / 4.0,
            MaxCoincidentPlacementNodeDistanceDrawingUnits,
            MaxPlacementEndpointOnWallAbsorptionDistanceDrawingUnits);

    private static WallNodeKind ClassifyPlacementNode(int degree, IReadOnlyList<string> directions)
    {
        var set = directions.ToHashSet(StringComparer.Ordinal);
        var hasEastWest = set.Contains("East") && set.Contains("West");
        var hasNorthSouth = set.Contains("North") && set.Contains("South");
        var hasHorizontal = set.Contains("East") || set.Contains("West");
        var hasVertical = set.Contains("North") || set.Contains("South");
        return degree switch
        {
            <= 1 => WallNodeKind.Endpoint,
            2 when hasEastWest || hasNorthSouth => WallNodeKind.Inline,
            2 when hasHorizontal && hasVertical => WallNodeKind.Corner,
            3 when (hasEastWest || hasNorthSouth) && hasHorizontal && hasVertical => WallNodeKind.TJunction,
            >= 4 when hasEastWest && hasNorthSouth => WallNodeKind.Crossing,
            _ => WallNodeKind.Junction
        };
    }

    private static int DirectionSortOrder(string direction) =>
        direction switch
        {
            "North" => 0,
            "East" => 1,
            "South" => 2,
            "West" => 3,
            _ => 4
        };

    private static PlanPoint AveragePoint(IReadOnlyList<PlanPoint> points) =>
        new(points.Average(point => point.X), points.Average(point => point.Y));

    private static PlanPoint ToPlanPoint(PointExport point) => new(point.X, point.Y);

    private enum PlacementGraphEdgeOrientation
    {
        Horizontal,
        Vertical
    }

    private sealed record PlacementGraphMergeGroupKey(
        int PageNumber,
        PlacementGraphEdgeOrientation Orientation,
        string WallComponentId,
        string WallComponentKind);

    private sealed record PlacementGraphMergeSpan(
        int Index,
        PlacementWallGraphEdgeExport Edge,
        PlacementGraphMergeGroupKey GroupKey,
        PlacementGraphEdgeOrientation Orientation,
        double Axis,
        double Start,
        double End,
        string StartNodeId,
        string EndNodeId)
    {
        public double Length => Math.Max(0, End - Start);
    }

    private sealed record PlacementGraphNodeReferenceNormalizationResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SplitNodeReferenceCount);

    private sealed record PlacementGraphNodeCoordinateAlignmentResult(
        PlacementWallGraphEdgeExport[] Edges,
        int AlignedEndpointCount,
        int AlignedNodeCount);

    private sealed record PlacementGraphAxisRegularizationResult(
        PlacementWallGraphEdgeExport[] Edges,
        int RegularizedEdgeCount);

    private sealed record PlacementGraphNodeOnEdgeAbsorptionResult(
        PlacementWallGraphEdgeExport[] Edges,
        int AbsorbedEndpointCount,
        int SplitHostEdgeCount);

    private sealed record PlacementGraphTinyEndpointOnHostStubSuppressionResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SuppressedStubCount);

    private sealed record PlacementGraphRedundantEndpointOnHostFragmentSuppressionResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SuppressedFragmentCount);

    private sealed record PlacementGraphRedundantEndpointOnHostFragmentSuppression(
        PlacementGraphMergeSpan CandidateSpan,
        PlacementGraphMergeSpan HostSpan,
        PlacementGraphEndpointObservation Endpoint,
        double Distance);

    private sealed record PlacementGraphTinyOpeningBridgeSuppressionResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SuppressedBridgeCount);

    private sealed record PlacementGraphTinyOpeningBridgeSuppression(
        PlacementWallGraphEdgeExport BridgeEdge,
        IReadOnlyList<PlacementGraphInteriorNodeAttachment> Attachments);

    private sealed record PlacementGraphResidualEndpointSnapResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SnappedEndpointCount);

    private sealed record PlacementGraphEndpointPairSnapResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SnappedPairCount,
        int SnappedEndpointCount);

    private sealed record PlacementGraphEndpointOnHostResidualSummary(
        int CandidateEndpointCount,
        int CoincidentCandidateEndpointCount,
        int SameAxisCandidateEndpointCount,
        int PerpendicularCandidateEndpointCount,
        double MaxDistance,
        IReadOnlyList<PlacementWallGraphResidualEndpointOnHostCandidateExport> Candidates)
    {
        public static PlacementGraphEndpointOnHostResidualSummary Empty { get; } = new(
            0,
            0,
            0,
            0,
            0,
            Array.Empty<PlacementWallGraphResidualEndpointOnHostCandidateExport>());
    }

    private sealed record PlacementGraphSharedNodeHostSnapResult(
        PlacementWallGraphEdgeExport[] Edges,
        int SnappedNodeCount,
        int SnappedEndpointCount,
        int SplitHostEdgeCount);

    private sealed record PlacementGraphMergeAxisDecision(
        double Axis,
        IReadOnlyList<string> Evidence);

    private sealed record PlacementGraphMergeAxisBand(
        double Axis,
        double Length,
        PlacementGraphMergeSpan Leader,
        IReadOnlyList<PlacementGraphMergeSpan> Members,
        IReadOnlySet<int> MemberIndexes);

    private sealed record PlacementGraphEndpointOnWallAbsorption(
        PlacementGraphEndpointObservation Endpoint,
        PlacementGraphMergeSpan HostSpan,
        double HostCoordinate,
        PlanPoint HostPoint,
        double Distance);

    private sealed record PlacementGraphSharedNodeHostSnap(
        string NodeId,
        PlacementGraphMergeSpan HostSpan,
        PlanPoint Point,
        double Distance);

    private sealed record PlacementGraphEndpointOnHostResidual(
        PlacementGraphEndpointObservation Endpoint,
        PlacementGraphMergeSpan EndpointSpan,
        PlacementGraphMergeSpan HostSpan,
        double HostCoordinate,
        PlanPoint HostPoint,
        double Distance);

    private sealed record PlacementGraphEndpointPairSnap(
        PlacementGraphEndpointObservation First,
        PlacementGraphEndpointObservation Second,
        PlanPoint Point,
        double FirstDistance,
        double SecondDistance);

    private sealed record PlacementGraphHostSplitPoint(
        string NodeId,
        double Coordinate,
        PlanPoint Point,
        int EndpointEdgeIndex,
        double Distance);

    private sealed record PlacementGraphInteriorNodeAttachment(
        string HostEdgeId,
        IReadOnlyList<string> Directions,
        double Distance);

    private sealed record PlacementGraphEndpointSpanObservation(
        PlacementGraphEndpointObservation Observation,
        PlacementGraphMergeSpan Span);

    private sealed record PlacementGraphEndpointObservation(
        int EdgeIndex,
        bool IsStart,
        string OriginalNodeId,
        int PageNumber,
        PlanPoint Position);

    private sealed record PlacementGraphEndpointKey(
        int EdgeIndex,
        bool IsStart);
}

public sealed record PlacementWallGraphResidualEndpointOnHostCandidateExport(
    string Id,
    int PageNumber,
    string EndpointEdgeId,
    string HostEdgeId,
    string EndpointWallId,
    string HostWallId,
    string EndpointNodeId,
    string EndpointRole,
    string Relationship,
    PointExport Endpoint,
    PointExport HostPoint,
    PointExport? EndpointMillimeters,
    PointExport? HostPointMillimeters,
    double DistanceDrawingUnits,
    double? DistanceMillimeters,
    LineExport RepairLine,
    LineExport? RepairLineMillimeters,
    string Severity,
    string RecommendedAction,
    IReadOnlyList<string> Evidence);

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
        IReadOnlyList<PlacementWallGraphNodeExport> nodes,
        IReadOnlyList<PlacementWallGraphEdgeExport> edges) =>
        new(
            nodes.Count,
            edges.Count,
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
        => From(
            node.Id,
            node.PageNumber,
            node.Position,
            node.Kind,
            node.Degree,
            node.Directions,
            node.Confidence,
            node.Evidence,
            calibration);

    public static PlacementWallGraphNodeExport From(
        string id,
        int pageNumber,
        PlanPoint position,
        WallNodeKind kind,
        int degree,
        IReadOnlyList<string> directions,
        Confidence confidence,
        IReadOnlyList<string> evidence,
        PlanCalibration calibration)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        return new PlacementWallGraphNodeExport(
            id,
            pageNumber,
            PointExport.From(position),
            ScalePoint(position, scale),
            kind.ToString(),
            degree,
            directions,
            confidence.Value,
            evidence);
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
    IReadOnlyList<string> SourceWallGraphEdgeIds,
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
            WallStructuralTrust.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                topologySpan?.SourceWall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(
                topologySpan?.SourceWall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                topologySpan?.SourceWall,
                component,
                evidenceAssessment)
            && !(topologySpan?.SourceWall is { } edgeSourceWall
                && WallPlacementContextGuards.IsTrustedObjectLikeLongCleanFragmentInteriorWallBody(
                    edgeSourceWall,
                    component,
                    evidenceAssessment));
        return new PlacementWallGraphEdgeExport(
            topologySpan?.Id ?? edge.Id,
            edge.PageNumber,
            topologySpan?.FromNodeId ?? edge.FromNodeId,
            topologySpan?.ToNodeId ?? edge.ToNodeId,
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
            topologySpan?.SourceWallGraphEdgeIds ?? new[] { edge.Id },
            EdgeEvidence(topologySpan, evidenceAssessment));
    }

    public static PlacementWallGraphEdgeExport From(
        WallGraphTopologySpan topologySpan,
        PlanCalibration calibration,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        wallComponentLookup.TryGetValue(topologySpan.WallId, out var component);
        wallEvidenceAssessments.TryGetValue(topologySpan.WallId, out var evidenceAssessment);
        var scale = ResolveMillimetersPerDrawingUnit(calibration, topologySpan.SourceWall?.MeasurementScaleGroupId);
        var drawingLength = topologySpan.DrawingLength;
        var thickness = topologySpan.Thickness;
        var excludedFromStructuralTopology =
            WallStructuralTrust.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                topologySpan.SourceWall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(
                topologySpan.SourceWall,
                component,
                evidenceAssessment)
            && !WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                topologySpan.SourceWall,
                component,
                evidenceAssessment)
            && !(topologySpan.SourceWall is { } spanSourceWall
                && WallPlacementContextGuards.IsTrustedObjectLikeLongCleanFragmentInteriorWallBody(
                    spanSourceWall,
                    component,
                    evidenceAssessment));
        return new PlacementWallGraphEdgeExport(
            topologySpan.Id,
            topologySpan.PageNumber,
            topologySpan.FromNodeId,
            topologySpan.ToNodeId,
            topologySpan.WallId,
            component?.Id,
            component?.Kind.ToString(),
            excludedFromStructuralTopology,
            LineExport.From(topologySpan.CenterLine),
            ScaleLine(topologySpan.CenterLine, scale),
            RectExport.From(topologySpan.Bounds),
            ScaleRect(topologySpan.Bounds, scale),
            drawingLength,
            scale is > 0 && drawingLength > 0 ? drawingLength * scale.Value / 1000.0 : null,
            thickness,
            scale is > 0 && thickness > 0 ? thickness * scale.Value : null,
            scale,
            topologySpan.Confidence.Value,
            topologySpan.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(topologySpan.SourcePrimitiveIds, sourceLookup),
            topologySpan.SourceWallGraphEdgeIds,
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
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyList<PlacementWallGraphEdgeExport>? placementEdges = null)
    {
        var scale = ResolveMillimetersPerDrawingUnit(calibration, scaleGroupId: null);
        var usePlacementEdges = placementEdges is not null;
        var componentEdges = (placementEdges ?? Array.Empty<PlacementWallGraphEdgeExport>())
            .Where(edge => string.Equals(edge.WallComponentId, component.Id, StringComparison.Ordinal))
            .ToArray();
        var edgeIds = usePlacementEdges
            ? componentEdges.Select(edge => edge.Id).Distinct(StringComparer.Ordinal).ToArray()
            : component.EdgeIds;
        var nodeIds = usePlacementEdges
            ? componentEdges
                .SelectMany(edge => new[] { edge.FromNodeId, edge.ToNodeId })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : component.NodeIds;
        return new PlacementWallGraphComponentExport(
            component.Id,
            component.PageNumber,
            component.Kind.ToString(),
            RectExport.From(component.Bounds),
            ScaleRect(component.Bounds, scale),
            component.WallIds,
            nodeIds,
            edgeIds,
            component.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(component.SourcePrimitiveIds, sourceLookup),
            component.WallCount,
            nodeIds.Count,
            edgeIds.Count,
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
    private static readonly string[] SpecificPlacementReviewOmissionCodes =
    [
        "thin_exterior_face_pair_review_required",
        "covered_area_boundary_review_required",
        "opening_detail_fragment_review_required",
        "one_endpoint_fragment_review_required",
        "fragmented_short_parallel_pair_review_required",
        "fragment_geometry_review",
        "secondary_without_room_boundary_support"
    ];

    public static IEnumerable<PlacementIssueExport> From(
        PlanScanResult result,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyList<PlacementRoomExport>? placementRooms = null,
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById = null)
    {
        var wallsById = result.Walls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var wallIdsWithDedicatedReviewIssues = BuildDedicatedWallReviewIssueIdSet(placementWallsById);
        AddSurfacePatternWallOverlapReviewWallIds(wallIdsWithDedicatedReviewIssues, result.Diagnostics.Messages);

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

        foreach (var room in (placementRooms ?? Array.Empty<PlacementRoomExport>())
                     .Where(room => room.BoundaryReliability.CoordinateBlockingWallIds.Count > 0)
                     .OrderBy(room => room.PageNumber)
                     .ThenBy(room => room.Id, StringComparer.Ordinal))
        {
            var scale = room.MillimetersPerDrawingUnit;
            var blockerWallIds = room.BoundaryReliability.CoordinateBlockingWallIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var blockerWalls = blockerWallIds
                .Select(id => placementWallsById is not null && placementWallsById.TryGetValue(id, out var wall)
                    ? wall
                    : null)
                .OfType<PlacementWallExport>()
                .ToArray();
            var sourcePrimitiveIds = blockerWalls
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementRoomBoundaryReliability",
                ["roomId"] = room.Id,
                ["roomLabel"] = room.Label ?? string.Empty,
                ["roomUseKind"] = room.UseKind,
                ["coordinateBlockingWallIds"] = string.Join(",", blockerWallIds),
                ["reviewWallIds"] = string.Join(",", room.BoundaryReliability.ReviewWallIds),
                ["rejectedWallIds"] = string.Join(",", room.BoundaryReliability.RejectedWallIds),
                ["placementOmittedWallIds"] = string.Join(",", room.BoundaryReliability.PlacementOmittedWallIds),
                ["readyWallIds"] = string.Join(",", room.BoundaryReliability.ReadyWallIds),
                ["nonBlockingDuplicateWallIds"] = string.Join(",", room.BoundaryReliability.NonBlockingDuplicateWallIds),
                ["openingOnlyWallIds"] = string.Join(",", room.BoundaryReliability.OpeningOnlyWallIds),
                ["openingDominatedWallIds"] = string.Join(",", room.BoundaryReliability.OpeningDominatedWallIds),
                ["roomSupportedFragmentWallIds"] = string.Join(",", room.BoundaryReliability.RoomSupportedFragmentWallIds),
                ["boundaryWallCount"] = room.BoundaryReliability.BoundaryWallCount.ToString(CultureInfo.InvariantCulture),
                ["assessedWallCount"] = room.BoundaryReliability.AssessedWallCount.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.room_boundary_blocker",
                DiagnosticSeverity.Warning.ToString(),
                "Room boundary references wall geometry that is not safe for coordinate placement.",
                room.PageNumber,
                new[] { room.PageNumber },
                room.Id,
                room.Bounds,
                ScaleRect(ToPlanRect(room.Bounds), scale),
                ClampRatio(room.Confidence),
                "Review or correct the listed boundary wall IDs before importing this room polygon as exact placement geometry.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(
                    room.BoundaryReliability.Evidence
                        .Concat(room.Reliability.Reasons)
                        .Concat(blockerWalls.SelectMany(wall => wall.Evidence.Take(3)))
                        .DefaultIfEmpty("Room boundary has coordinate-blocking wall evidence.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "thin_exterior_face_pair_review_required",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["thicknessDrawingUnits"] = wall.ThicknessDrawingUnits.ToString("0.###", CultureInfo.InvariantCulture),
                ["thicknessMillimeters"] = wall.ThicknessMillimeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.thin_exterior_face_pair",
                DiagnosticSeverity.Warning.ToString(),
                "Thin exterior parallel-face wall candidate requires review before coordinate placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the source PDF before importing this candidate as an exterior wall.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("Thin exterior face-pair wall candidate is not coordinate-ready.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "covered_area_boundary_review_required",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.covered_area_boundary",
                DiagnosticSeverity.Warning.ToString(),
                "Covered/outdoor boundary wall-like candidate requires review before exterior wall placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the source PDF before importing this candidate as exterior wall geometry.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("Covered/outdoor boundary candidate is not coordinate-ready.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "opening_detail_fragment_review_required",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.opening_detail_fragment",
                DiagnosticSeverity.Warning.ToString(),
                "Opening-linked wall/detail fragment requires review before wall placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the source PDF before importing this candidate as wall geometry; it may be door, window, or opening detail linework.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("Opening-linked fragment is not coordinate-ready wall geometry.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "one_endpoint_fragment_review_required",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.one_endpoint_fragment",
                DiagnosticSeverity.Warning.ToString(),
                "One-ended wall fragment requires review before wall placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the unsupported endpoint before importing this fragment as exact wall geometry.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("One-ended wall fragment is not coordinate-ready wall geometry.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "fragmented_short_parallel_pair_review_required",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["thicknessDrawingUnits"] = wall.ThicknessDrawingUnits.ToString("0.###", CultureInfo.InvariantCulture),
                ["thicknessMillimeters"] = wall.ThicknessMillimeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.fragmented_short_parallel_pair",
                DiagnosticSeverity.Warning.ToString(),
                "Fragmented short parallel-face wall candidate requires review before coordinate placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the source PDF before importing this fragmented short wall pair as exact wall geometry.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("Fragmented short wall-pair candidate is not coordinate-ready.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "fragment_geometry_review",
                         StringComparison.Ordinal))
                     .OrderBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture)
            };

            yield return new PlacementIssueExport(
                "placement.review.fragment_geometry",
                DiagnosticSeverity.Warning.ToString(),
                "Wall geometry requires review before coordinate placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                wall.PlacementOmission?.RecommendedAction
                    ?? "Review the source PDF and wall graph before importing exact wall coordinates.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .DefaultIfEmpty("Wall fragment geometry is not coordinate-ready.")),
                properties);
        }

        foreach (var wall in (placementWallsById?.Values ?? Array.Empty<PlacementWallExport>())
                     .Where(wall => string.Equals(
                         wall.PlacementOmission?.Code,
                         "secondary_without_room_boundary_support",
                         StringComparison.Ordinal))
                     .OrderByDescending(IsLikelyMissingSecondaryStructuralWallCandidate)
                     .ThenByDescending(wall => wall.DrawingLength)
                     .ThenBy(wall => wall.PageNumber)
                     .ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            var likelyMissingWallCandidate = IsLikelyMissingSecondaryStructuralWallCandidate(wall);
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["detector"] = "placementWallReliability",
                ["wallId"] = wall.Id,
                ["wallType"] = wall.WallType,
                ["detectionKind"] = wall.DetectionKind,
                ["wallComponentId"] = wall.WallComponentId ?? string.Empty,
                ["wallComponentKind"] = wall.WallComponentKind ?? string.Empty,
                ["placementOmissionCode"] = wall.PlacementOmission?.Code ?? string.Empty,
                ["placementOmissionCategory"] = wall.PlacementOmission?.Category ?? string.Empty,
                ["drawingLength"] = wall.DrawingLength.ToString("0.###", CultureInfo.InvariantCulture),
                ["lengthMeters"] = wall.LengthMeters?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty,
                ["confidence"] = wall.Confidence.ToString("0.###", CultureInfo.InvariantCulture),
                ["readyForCoordinatePlacement"] = wall.Reliability.ReadyForCoordinatePlacement.ToString(CultureInfo.InvariantCulture),
                ["requiresReview"] = wall.Reliability.RequiresReview.ToString(CultureInfo.InvariantCulture),
                ["likelyMissingWallCandidate"] = likelyMissingWallCandidate.ToString(CultureInfo.InvariantCulture),
                ["reviewPriority"] = likelyMissingWallCandidate
                    ? "likely_missing_wall_candidate"
                    : "secondary_structural_review"
            };

            yield return new PlacementIssueExport(
                "placement.review.secondary_structural_wall_without_room_boundary",
                DiagnosticSeverity.Warning.ToString(),
                likelyMissingWallCandidate
                    ? "Secondary structural wall candidate may be a missing real wall, but lacks detected room-boundary support."
                    : "Secondary structural wall candidate lacks detected room-boundary support before coordinate placement.",
                wall.PageNumber,
                new[] { wall.PageNumber },
                wall.Id,
                wall.Bounds,
                wall.BoundsMillimeters,
                ClampRatio(wall.Confidence),
                "Review this candidate against the source plan; promote or correct it only when it is a real room, exterior, or structural boundary.",
                wall.SourcePrimitiveIds,
                wall.SourceLayers,
                BuildIssueEvidence(
                    (wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
                        .Concat(wall.Reliability.Reasons)
                        .Concat(wall.Evidence)
                        .Concat(likelyMissingWallCandidate
                            ? new[] { "Candidate is long/high-confidence enough to review as a possible missing wall." }
                            : Array.Empty<string>())
                        .DefaultIfEmpty("Secondary structural wall candidate is not coordinate-ready.")),
                properties);
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedWallEvidenceReviews(result.WallEvidenceMap)
                     .Where(assessment => !wallIdsWithDedicatedReviewIssues.Contains(assessment.WallId))
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
            var isNonBlockingEndpointGap = !isEndpointOverrun
                && !WallGraphEndpointGapImpactsCleanPlacementTopology(diagnostic, placementWallsById);
            properties["placementImportImpact"] = isNonBlockingEndpointGap
                ? "NonBlockingOmittedEndpoint"
                : "ReviewRequired";

            yield return new PlacementIssueExport(
                isEndpointOverrun
                    ? "placement.review.wall_graph_endpoint_overrun"
                    : isNonBlockingEndpointGap
                        ? "placement.info.wall_graph_endpoint_gap_nonblocking"
                        : "placement.review.wall_graph_endpoint_gap",
                isNonBlockingEndpointGap
                    ? DiagnosticSeverity.Info.ToString()
                    : diagnostic.Severity.ToString(),
                diagnostic.Message,
                diagnostic.PageNumber,
                pageNumbers,
                $"review:{(isEndpointOverrun ? "wall-graph-endpoint-overrun" : "wall-graph-gap")}:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Region is null ? null : ScaleRect(diagnostic.Region.Value, scale),
                ClampRatio(diagnostic.Confidence?.Value ?? 0.5),
                isEndpointOverrun
                    ? "Review or correct this possible endpoint-overrun trim before importing wall graph topology."
                    : isNonBlockingEndpointGap
                        ? "Review this endpoint gap only if the omitted endpoint wall is manually promoted into clean placement topology."
                    : "Review or correct this possible unsnapped wall junction before importing wall graph topology.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                BuildIssueEvidence(new[] { diagnostic.Message }
                    .Concat(gapEvidence)
                    .Concat(wallEvidence)
                    .Concat(isNonBlockingEndpointGap
                        ? new[] { "non-blocking because the endpoint wall is not clean placement-ready" }
                        : Array.Empty<string>())),
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

    private static HashSet<string> BuildDedicatedWallReviewIssueIdSet(
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById)
    {
        if (placementWallsById is null || placementWallsById.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var omissionCodes = new HashSet<string>(
            SpecificPlacementReviewOmissionCodes,
            StringComparer.Ordinal);
        return placementWallsById.Values
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .Where(wall => wall.PlacementOmission?.Code is string code && omissionCodes.Contains(code))
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AddSurfacePatternWallOverlapReviewWallIds(
        HashSet<string> wallIds,
        IEnumerable<PlanDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(diagnostic => string.Equals(
                     diagnostic.Code,
                     SurfacePatternWallOverlapDiagnosticCode,
                     StringComparison.Ordinal)))
        {
            if (diagnostic.Properties.TryGetValue("wallId", out var wallId)
                && !string.IsNullOrWhiteSpace(wallId))
            {
                wallIds.Add(wallId);
            }
        }
    }

    private static bool WallGraphEndpointGapImpactsCleanPlacementTopology(
        PlanDiagnostic diagnostic,
        IReadOnlyDictionary<string, PlacementWallExport>? placementWallsById)
    {
        if (placementWallsById is null
            || placementWallsById.Count == 0
            || !diagnostic.Properties.TryGetValue("wallIds", out var wallIdsText)
            || string.IsNullOrWhiteSpace(wallIdsText))
        {
            return true;
        }

        var wallIds = wallIdsText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (wallIds.Length == 0)
        {
            return true;
        }

        var gapKind = diagnostic.Properties.TryGetValue("gapKind", out var foundGapKind)
            ? foundGapKind
            : string.Empty;
        if (string.Equals(gapKind, WallGraphRepairCandidateKind.EndpointToWall.ToString(), StringComparison.Ordinal)
            && diagnostic.Properties.TryGetValue("hostWallId", out var hostWallId)
            && !string.IsNullOrWhiteSpace(hostWallId))
        {
            var endpointWallIds = wallIds
                .Where(id => !string.Equals(id, hostWallId, StringComparison.Ordinal))
                .ToArray();
            if (endpointWallIds.Length > 0)
            {
                return endpointWallIds.Any(id => IsCleanPlacementWallForEndpointGap(id, placementWallsById));
            }
        }

        return wallIds.Any(id => IsCleanPlacementWallForEndpointGap(id, placementWallsById));
    }

    private static bool IsCleanPlacementWallForEndpointGap(
        string wallId,
        IReadOnlyDictionary<string, PlacementWallExport> placementWallsById) =>
        placementWallsById.TryGetValue(wallId, out var wall)
        && wall.PlacementOmission is null
        && wall.Reliability.ReadyForCoordinatePlacement;

    private static IReadOnlyList<string> BuildIssueEvidence(params string[] evidence) =>
        BuildIssueEvidence((IEnumerable<string>)evidence);

    private static IReadOnlyList<string> BuildIssueEvidence(IEnumerable<string> evidence) =>
        evidence
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static PlanRect ToPlanRect(RectExport rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);

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

    private static bool IsLikelyMissingSecondaryStructuralWallCandidate(PlacementWallExport wall) =>
        wall.DrawingLength >= 72
        && wall.Confidence >= 0.75
        && string.Equals(wall.WallComponentKind, WallGraphComponentKind.SecondaryStructural.ToString(), StringComparison.Ordinal)
        && !wall.ExcludedFromStructuralTopology;

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
        var openingDominatedWallIds = new List<string>();
        var roomSupportedFragmentWallIds = new List<string>();
        var placementOmittedWallIds = new List<string>();
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
            else if (IsNonBlockingDuplicateBoundaryWall(assessment, placementWall))
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
            else if (IsOpeningDominatedTrustedBoundaryWall(assessment, placementWall))
            {
                openingDominatedWallIds.Add(wallId);
            }
            else if (assessment.Decision == WallEvidenceDecision.Review
                || assessment.RequiresReview
                || !assessment.PlacementReady)
            {
                reviewWallIds.Add(wallId);
            }
            else if (IsPlacementOmittedBoundaryWall(placementWall))
            {
                placementOmittedWallIds.Add(wallId);
            }
            else
            {
                readyWallIds.Add(wallId);
            }
        }

        var coordinateBlockingWallIds = reviewWallIds
            .Concat(rejectedWallIds)
            .Concat(placementOmittedWallIds)
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

        if (openingDominatedWallIds.Count > 0)
        {
            evidence.Add($"non-blocking opening-dominated room boundary wall evidence: {string.Join(",", openingDominatedWallIds.Order(StringComparer.Ordinal))}");
        }

        if (roomSupportedFragmentWallIds.Count > 0)
        {
            evidence.Add($"non-blocking room-supported fragment boundary wall evidence: {string.Join(",", roomSupportedFragmentWallIds.Order(StringComparer.Ordinal))}");
        }

        if (placementOmittedWallIds.Count > 0)
        {
            evidence.Add($"coordinate-blocking placement-omitted room boundary wall evidence: {string.Join(",", placementOmittedWallIds.Order(StringComparer.Ordinal))}");
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
            openingDominatedWallIds.Order(StringComparer.Ordinal).ToArray(),
            roomSupportedFragmentWallIds.Order(StringComparer.Ordinal).ToArray(),
            placementOmittedWallIds.Order(StringComparer.Ordinal).ToArray(),
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

    private static bool IsOpeningDominatedTrustedBoundaryWall(
        WallEvidenceWallAssessment assessment,
        PlacementWallExport? wall)
    {
        const double minimumOpeningCoverageRatio = 0.80;
        const double maximumResidualWallLength = 20.0;

        if (wall is null
            || wall.OpeningCutouts.Count == 0
            || wall.TopologySpans.Count > 0
            || wall.DrawingLength <= 0.001
            || wall.PlacementOmission?.Code != "tiny_door_adjacent_topology_suppressed"
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.MediumWallBody))
        {
            return false;
        }

        var coveredRatio = OpeningCutoutCoverageRatio(wall.OpeningCutouts);
        if (coveredRatio < minimumOpeningCoverageRatio)
        {
            return false;
        }

        var largestResidualSpan = wall.SolidSpans.Count == 0
            ? Math.Max(0, wall.DrawingLength * (1 - coveredRatio))
            : wall.SolidSpans.Max(span => span.DrawingLength);
        if (largestResidualSpan > maximumResidualWallLength)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(wall.PlacementOmission?.Evidence ?? Array.Empty<string>())
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

        return evidence.Any(item =>
            item.Contains("room boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("detected room evidence on both sides", StringComparison.OrdinalIgnoreCase)
            || item.Contains("shared by room adjacency boundary", StringComparison.OrdinalIgnoreCase)
            || item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlacementOmittedBoundaryWall(PlacementWallExport? wall) =>
        wall is not null
        && (wall.PlacementOmission is not null
            || !wall.Reliability.ReadyForCoordinatePlacement);

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
        var semanticRoomSeedIsWallBacked = !isSemanticRoomSeed
            || IsWallBackedSemanticRoomSeed(room, boundaryReliability);
        if (isSemanticRoomSeed && !semanticRoomSeedIsWallBacked)
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

        if (boundaryReliability.PlacementOmittedWallIds.Count > 0)
        {
            reasons.Add($"room boundary uses placement-omitted wall geometry: {string.Join(",", boundaryReliability.PlacementOmittedWallIds.Take(12))}");
        }

        var boundaryWallsAreCoordinateReady = semanticRoomSeedIsWallBacked
            && room.WallIds.Count > 0
            && boundaryReliability.CoordinateBlockingWallIds.Count == 0;

        return Create(
            room.Confidence.Value,
            room.Confidence.Value >= 0.5 && room.Boundary.Count >= 3 && boundaryWallsAreCoordinateReady,
            calibration.HasReliableMeasurementScale,
            reasons.Count > 0 || !boundaryWallsAreCoordinateReady,
            reasons);
    }

    private static bool IsWallBackedSemanticRoomSeed(
        RoomRegion room,
        PlacementRoomBoundaryReliabilityExport boundaryReliability)
    {
        if (room.Confidence.Value < 0.6
            || room.WallIds.Count < 4
            || boundaryReliability.BoundaryWallCount < 4
            || boundaryReliability.AssessedWallCount < boundaryReliability.BoundaryWallCount
            || boundaryReliability.CoordinateBlockingWallIds.Count > 0
            || boundaryReliability.ReviewWallIds.Count > 0
            || boundaryReliability.RejectedWallIds.Count > 0
            || boundaryReliability.PlacementOmittedWallIds.Count > 0
            || boundaryReliability.UnassessedWallIds.Count > 0)
        {
            return false;
        }

        var hasBoundedSemanticEvidence = room.Evidence.Any(item =>
            item.Contains("semantic room seed was bounded by nearby orthogonal wall evidence", StringComparison.OrdinalIgnoreCase)
            || item.Contains("semantic room boundary inferred from nearby walls", StringComparison.OrdinalIgnoreCase));
        if (!hasBoundedSemanticEvidence)
        {
            return false;
        }

        var nonBlockingBoundaryWallIds = boundaryReliability.ReadyWallIds
            .Concat(boundaryReliability.NonBlockingDuplicateWallIds)
            .Concat(boundaryReliability.OpeningOnlyWallIds)
            .Concat(boundaryReliability.OpeningDominatedWallIds)
            .Concat(boundaryReliability.RoomSupportedFragmentWallIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return nonBlockingBoundaryWallIds >= boundaryReliability.BoundaryWallCount;
    }

    private static bool IsNonBlockingDuplicateBoundaryWall(
        WallEvidenceWallAssessment assessment,
        PlacementWallExport? wall) =>
        IsNonBlockingDuplicateBoundaryWallEvidence(assessment)
        || wall?.PlacementOmission?.Code is "duplicate_clean_topology_span" or "duplicate_wall_face";

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
        return ContainsDuplicateWallBodyRepresentationEvidence(evidence);
    }

    private static bool ContainsDuplicateWallBodyRepresentationEvidence(IEnumerable<string> evidence)
    {
        var evidenceItems = evidence.ToArray();
        var isExplicitDuplicate = evidenceItems.Any(item =>
            item.Contains("duplicate wall-face", StringComparison.OrdinalIgnoreCase)
            || item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase));
        if (!isExplicitDuplicate)
        {
            return false;
        }

        return evidenceItems.Any(item =>
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
