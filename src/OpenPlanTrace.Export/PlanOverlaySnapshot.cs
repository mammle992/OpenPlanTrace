using System.Text.Json;

namespace OpenPlanTrace.Export;

public sealed record PlanOverlaySnapshot(
    string SchemaVersion,
    string DocumentId,
    string CoordinateSpace,
    string Origin,
    string XAxisDirection,
    string YAxisDirection,
    string Unit,
    string ScanSchemaVersion,
    string? QualityGrade,
    double QualityConfidence,
    bool RequiresReview,
    int ReviewQueueCount,
    IReadOnlyDictionary<string, int> ReviewQueueKindBreakdown,
    IReadOnlyDictionary<string, int> ReviewQueueSeverityBreakdown,
    IReadOnlyList<PlanOverlayPageSnapshot> Pages,
    IReadOnlyList<PlanOverlaySnapshotIssue> Issues)
{
    public const string CurrentSchemaVersion = "openplantrace.visual-snapshot.v2";

    public static PlanOverlaySnapshot From(
        PlanScanResult result,
        IReadOnlyDictionary<int, string>? svgPathsByPage = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sourceLookup = PrimitiveSourceExport.From(result.Document)
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var reviewQueue = ScanReviewQueueItemExport.From(result, sourceLookup);
        var pages = result.Document.Pages
            .Select(page => PlanOverlayPageSnapshot.From(result, page, reviewQueue, svgPathsByPage))
            .ToArray();

        return new PlanOverlaySnapshot(
            CurrentSchemaVersion,
            result.Document.Id,
            "OpenPlanTracePageCoordinates",
            "TopLeft",
            "Right",
            "Down",
            "DrawingUnit",
            PlanTraceExport.CurrentSchemaVersion,
            result.Quality.Grade.ToString(),
            Round(result.Quality.OverallConfidence.Value),
            result.Quality.RequiresReview,
            reviewQueue.Count,
            CountBy(reviewQueue, item => item.Kind),
            CountBy(reviewQueue, item => item.Severity),
            pages,
            pages.SelectMany(page => page.Issues).ToArray());
    }

    internal static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    internal static IReadOnlyDictionary<string, int> CountBy<T>(
        IEnumerable<T> items,
        Func<T, string?> keySelector) =>
        items
            .Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Cast<string>()
            .GroupBy(key => key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

public sealed record PlanOverlayPageSnapshot(
    int PageNumber,
    double Width,
    double Height,
    PlanRectSnapshot PageBounds,
    PlanRectSnapshot DetectionBounds,
    double DetectionCoverage,
    int DrawableItemCount,
    int PrimitiveCount,
    string? SvgPath,
    IReadOnlyList<PlanOverlayLayerSnapshot> Layers,
    int ReviewQueueCount,
    IReadOnlyDictionary<string, int> ReviewQueueKindBreakdown,
    IReadOnlyDictionary<string, int> ReviewQueueSeverityBreakdown,
    IReadOnlyList<PlanOverlayReviewQueueItemSnapshot> ReviewQueue,
    IReadOnlyList<PlanOverlaySnapshotIssue> Issues)
{
    public static PlanOverlayPageSnapshot From(
        PlanScanResult result,
        PlanPage page,
        IReadOnlyList<ScanReviewQueueItemExport> reviewQueue,
        IReadOnlyDictionary<int, string>? svgPathsByPage = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(reviewQueue);

        var pageBounds = new PlanRect(0, 0, page.Size.Width, page.Size.Height);
        var layers = BuildLayers(result, page.Number).ToArray();
        var detectionBounds = PlanRect.Union(layers.Select(layer => layer.Bounds.ToPlanRect()));
        var coverage = pageBounds.Area <= 0 || detectionBounds.IsEmpty
            ? 0
            : Math.Clamp(detectionBounds.ClampTo(pageBounds).Area / pageBounds.Area, 0, 1);
        var pageReviewQueue = reviewQueue
            .Where(item => ItemAppliesToPage(item, page.Number))
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        var reviewItems = pageReviewQueue
            .Select(PlanOverlayReviewQueueItemSnapshot.From)
            .ToArray();
        var issues = BuildIssues(result, page, pageBounds, layers, detectionBounds, coverage, pageReviewQueue).ToArray();

        return new PlanOverlayPageSnapshot(
            page.Number,
            PlanOverlaySnapshot.Round(page.Size.Width),
            PlanOverlaySnapshot.Round(page.Size.Height),
            PlanRectSnapshot.From(pageBounds),
            PlanRectSnapshot.From(detectionBounds),
            PlanOverlaySnapshot.Round(coverage),
            layers.Sum(layer => layer.Count),
            page.Primitives.Count,
            SvgPathFor(page.Number, svgPathsByPage),
            layers,
            pageReviewQueue.Length,
            PlanOverlaySnapshot.CountBy(pageReviewQueue, item => item.Kind),
            PlanOverlaySnapshot.CountBy(pageReviewQueue, item => item.Severity),
            reviewItems,
            issues);
    }

    private static IEnumerable<PlanOverlayLayerSnapshot> BuildLayers(PlanScanResult result, int pageNumber)
    {
        var routing = result.RoutingLayer;

        yield return Layer(
            "regions",
            result.SheetRegions.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.SheetRegions
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "titleBlocks",
            result.TitleBlocks.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "dimensions",
            result.Dimensions.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "annotations",
            result.Annotations.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "gridAxes",
            result.GridAxes.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.GridAxes
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Orientation.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "gridBaySpacings",
            result.GridBaySpacings.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "wallComponents",
            result.WallGraph.Components.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.WallGraph.Components
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "surfacePatterns",
            result.SurfacePatterns.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.SurfacePatterns
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Kind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "walls",
            result.Walls.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "wallNodes",
            result.WallGraph.Nodes.Where(item => item.PageNumber == pageNumber),
            item => new PlanRect(item.Position.X, item.Position.Y, 0, 0),
            item => item.Confidence);

        yield return Layer(
            "rooms",
            result.Rooms.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.Rooms
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.UseKind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "roomClusters",
            result.RoomAdjacencyGraph.Clusters.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "roomAdjacency",
            result.RoomAdjacencyGraph.Edges.Where(item => item.PageNumber == pageNumber),
            item => item.SharedBoundary?.Bounds ?? PlanRect.Empty,
            item => item.Confidence);

        yield return Layer(
            "openings",
            result.Openings.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.Openings
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Type.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "objects",
            result.ObjectCandidates.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.ObjectCandidates
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Category.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "objectGroups",
            result.ObjectGroups.Where(item => item.PageNumbers.Contains(pageNumber)),
            item => item.RepresentativeBounds,
            item => item.Confidence,
            result.ObjectGroups
                .Where(item => item.PageNumbers.Contains(pageNumber))
                .GroupBy(item => item.Category.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "objectAggregates",
            result.ObjectAggregates.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            result.ObjectAggregates
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.Category.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "routingBarriers",
            routing.Barriers.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "routingPassages",
            routing.Passages.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);

        yield return Layer(
            "routingObstacles",
            routing.Obstacles.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence,
            routing.Obstacles
                .Where(item => item.PageNumber == pageNumber)
                .GroupBy(item => item.ObstacleKind.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

        yield return Layer(
            "routingRoomUseHints",
            routing.RoomUseHints.Where(item => item.PageNumber == pageNumber),
            item => item.Bounds,
            item => item.Confidence);
    }

    private static PlanOverlayLayerSnapshot Layer<T>(
        string name,
        IEnumerable<T> items,
        Func<T, PlanRect> bounds,
        Func<T, Confidence> confidence,
        IReadOnlyDictionary<string, int>? breakdown = null)
    {
        var materialized = items.ToArray();
        var rects = materialized
            .Select(bounds)
            .Where(rect => !rect.IsEmpty)
            .ToArray();
        var confidenceValues = materialized
            .Select(item => confidence(item).Value)
            .ToArray();

        return new PlanOverlayLayerSnapshot(
            name,
            materialized.Length,
            PlanRectSnapshot.From(PlanRect.Union(rects)),
            confidenceValues.Length == 0 ? null : PlanOverlaySnapshot.Round(confidenceValues.Average()),
            confidenceValues.Length == 0 ? null : PlanOverlaySnapshot.Round(confidenceValues.Min()),
            confidenceValues.Length == 0 ? null : PlanOverlaySnapshot.Round(confidenceValues.Max()),
            breakdown ?? new Dictionary<string, int>(StringComparer.Ordinal));
    }

    private static IEnumerable<PlanOverlaySnapshotIssue> BuildIssues(
        PlanScanResult result,
        PlanPage page,
        PlanRect pageBounds,
        IReadOnlyList<PlanOverlayLayerSnapshot> layers,
        PlanRect detectionBounds,
        double coverage,
        IReadOnlyList<ScanReviewQueueItemExport> reviewQueue)
    {
        var pageNumber = page.Number;
        if (layers.Sum(layer => layer.Count) == 0)
        {
            yield return Issue("visual.empty_overlay", pageNumber, "error", "No drawable scan overlay items were produced for this page.");
        }

        if (!result.SheetRegions.Any(region => region.PageNumber == pageNumber && region.Kind == RegionKind.MainFloorPlan))
        {
            yield return Issue("visual.missing_main_floorplan_region", pageNumber, "warning", "No main floorplan region was detected for this page.");
        }

        if (!detectionBounds.IsEmpty && !pageBounds.Contains(detectionBounds, tolerance: 1))
        {
            yield return Issue("visual.detections_outside_page", pageNumber, "warning", "Some overlay detections extend outside the page coordinate frame.");
        }

        if (coverage > 0.92)
        {
            yield return Issue("visual.overlay_coverage_high", pageNumber, "info", "Detection bounds cover most of the page; sheet/title/notes regions may be included in the debug view.");
        }

        var nodeCount = LayerCount(layers, "wallNodes");
        var wallCount = LayerCount(layers, "walls");
        if (nodeCount > 0 && wallCount > 0 && nodeCount > wallCount * 4)
        {
            yield return Issue("visual.wall_node_density_high", pageNumber, "warning", "Wall node count is much higher than wall count; visual review should check snapping and fragmentation.");
        }

        var objectCount = LayerCount(layers, "objects");
        var aggregateCount = LayerCount(layers, "objectAggregates");
        if (objectCount >= 20 && aggregateCount == 0)
        {
            yield return Issue("visual.object_aggregation_missing", pageNumber, "warning", "Many objects were detected but no object aggregate was created; clutter may affect routing consumers.");
        }

        if (reviewQueue.Count > 0)
        {
            var blockingCount = reviewQueue.Count(item => IsBlockingReviewSeverity(item.Severity));
            var warningCount = reviewQueue.Count(item => IsWarningReviewSeverity(item.Severity));
            var severity = blockingCount > 0 ? "error" : warningCount > 0 ? "warning" : "info";
            var action = blockingCount > 0
                ? "blocking"
                : warningCount > 0
                    ? "warning"
                    : "informational";
            yield return Issue(
                "visual.scan_review_queue_present",
                pageNumber,
                severity,
                $"Scan review queue has {reviewQueue.Count} {action} item(s) on this page.");
        }
    }

    private static bool ItemAppliesToPage(ScanReviewQueueItemExport item, int pageNumber) =>
        item.PageNumber == pageNumber || item.PageNumbers.Contains(pageNumber);

    private static bool IsBlockingReviewSeverity(string severity) =>
        string.Equals(severity, "Error", StringComparison.OrdinalIgnoreCase);

    private static bool IsWarningReviewSeverity(string severity) =>
        string.Equals(severity, "Warning", StringComparison.OrdinalIgnoreCase);

    private static int LayerCount(IReadOnlyList<PlanOverlayLayerSnapshot> layers, string name) =>
        layers.FirstOrDefault(layer => string.Equals(layer.Name, name, StringComparison.Ordinal))?.Count ?? 0;

    private static PlanOverlaySnapshotIssue Issue(string code, int pageNumber, string severity, string message) =>
        new(code, severity, message, pageNumber);

    private static string? SvgPathFor(int pageNumber, IReadOnlyDictionary<int, string>? svgPathsByPage) =>
        svgPathsByPage is not null && svgPathsByPage.TryGetValue(pageNumber, out var path)
            ? path
            : null;
}

public sealed record PlanOverlayLayerSnapshot(
    string Name,
    int Count,
    PlanRectSnapshot Bounds,
    double? AverageConfidence,
    double? MinimumConfidence,
    double? MaximumConfidence,
    IReadOnlyDictionary<string, int> Breakdown);

public sealed record PlanOverlaySnapshotIssue(
    string Code,
    string Severity,
    string Message,
    int PageNumber);

public sealed record PlanOverlayReviewQueueItemSnapshot(
    string Id,
    string Kind,
    string Detector,
    string ItemId,
    int Priority,
    string Severity,
    int PageNumber,
    PlanRectSnapshot Bounds,
    double Confidence,
    string RecommendedAction,
    int SourcePrimitiveCount,
    int SourceLayerCount,
    IReadOnlyList<string> Evidence)
{
    public static PlanOverlayReviewQueueItemSnapshot From(ScanReviewQueueItemExport item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var pageNumber = item.PageNumber ?? item.PageNumbers.FirstOrDefault();
        var bounds = item.Bounds is null
            ? PlanRect.Empty
            : new PlanRect(item.Bounds.X, item.Bounds.Y, item.Bounds.Width, item.Bounds.Height);

        return new PlanOverlayReviewQueueItemSnapshot(
            item.Id,
            item.Kind,
            item.Detector,
            item.ItemId,
            item.Priority,
            item.Severity,
            pageNumber,
            PlanRectSnapshot.From(bounds),
            PlanOverlaySnapshot.Round(item.Confidence),
            item.RecommendedAction,
            item.SourcePrimitiveIds.Count,
            item.SourceLayers.Count,
            item.Evidence.Take(3).ToArray());
    }
}

public sealed record PlanRectSnapshot(
    double X,
    double Y,
    double Width,
    double Height,
    double Left,
    double Top,
    double Right,
    double Bottom,
    double CenterX,
    double CenterY,
    double Area)
{
    public bool IsEmpty => Width < 0 || Height < 0;

    public static PlanRectSnapshot From(PlanRect rect) =>
        rect.IsEmpty
            ? new PlanRectSnapshot(0, 0, -1, -1, 0, 0, -1, -1, 0, 0, 0)
            : new PlanRectSnapshot(
                PlanOverlaySnapshot.Round(rect.X),
                PlanOverlaySnapshot.Round(rect.Y),
                PlanOverlaySnapshot.Round(rect.Width),
                PlanOverlaySnapshot.Round(rect.Height),
                PlanOverlaySnapshot.Round(rect.Left),
                PlanOverlaySnapshot.Round(rect.Top),
                PlanOverlaySnapshot.Round(rect.Right),
                PlanOverlaySnapshot.Round(rect.Bottom),
                PlanOverlaySnapshot.Round(rect.Center.X),
                PlanOverlaySnapshot.Round(rect.Center.Y),
                PlanOverlaySnapshot.Round(rect.Area));

    public PlanRect ToPlanRect() => new(X, Y, Width, Height);
}

public sealed record PlanOverlaySnapshotJsonExportOptions
{
    public bool WriteIndented { get; init; } = true;
}

public static class PlanOverlaySnapshotJsonExporter
{
    public static string Serialize(
        PlanOverlaySnapshot snapshot,
        PlanOverlaySnapshotJsonExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, CreateJsonOptions(options));
    }

    public static string Serialize(
        PlanScanResult result,
        PlanOverlaySnapshotJsonExportOptions? options = null,
        IReadOnlyDictionary<int, string>? svgPathsByPage = null) =>
        Serialize(PlanOverlaySnapshot.From(result, svgPathsByPage), options);

    public static async ValueTask WriteAsync(
        PlanOverlaySnapshot snapshot,
        Stream stream,
        PlanOverlaySnapshotJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(stream);

        await JsonSerializer.SerializeAsync(
                stream,
                snapshot,
                CreateJsonOptions(options),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanOverlaySnapshotJsonExportOptions? options = null,
        IReadOnlyDictionary<int, string>? svgPathsByPage = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(PlanOverlaySnapshot.From(result, svgPathsByPage), stream, options, cancellationToken);

    private static JsonSerializerOptions CreateJsonOptions(PlanOverlaySnapshotJsonExportOptions? options)
    {
        options ??= new PlanOverlaySnapshotJsonExportOptions();
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = options.WriteIndented
        };
    }
}
