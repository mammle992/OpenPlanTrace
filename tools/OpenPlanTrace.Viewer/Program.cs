using System.Diagnostics;
using System.Text.Json.Serialization;
using OpenPlanTrace;
using OpenPlanTrace.Export;
using OpenPlanTrace.Pdf;

var launch = ViewerLaunchOptions.Parse(args);
if (await ViewerProcessHelpers.IsViewerReadyAsync(launch.Url).ConfigureAwait(false))
{
    if (launch.AutoOpenBrowser)
    {
        ViewerProcessHelpers.OpenBrowser(launch.Url);
    }
    else
    {
        Console.WriteLine($"OpenPlanTrace Viewer already running at {launch.Url}");
    }

    return;
}

var builder = WebApplication.CreateBuilder(launch.AspNetArgs);
if (!launch.HasExplicitAspNetUrl)
{
    builder.WebHost.UseUrls(launch.Url);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ready" }));

app.MapGet("/api/kvemo-crop-image", (string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "Image path is required." });
    }

    var fullPath = Path.GetFullPath(path);
    if (!ViewerImageFilePolicy.TryGetContentType(fullPath, out var contentType))
    {
        return Results.BadRequest(new { error = "Only PNG, JPEG, and WebP crop images can be served." });
    }

    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound(new { error = "Crop image was not found." });
    }

    var info = new FileInfo(fullPath);
    if (info.Length > ViewerImageFilePolicy.MaximumImageBytes)
    {
        return Results.BadRequest(new { error = "Crop image is too large for viewer preview." });
    }

    return Results.File(fullPath, contentType);
});

app.MapGet("/api/kvemo-crop-manifest", async (string path, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "Manifest path is required." });
    }

    var fullPath = Path.GetFullPath(path);
    if (!Path.GetExtension(fullPath).Equals(".jsonl", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only Kvemo JSONL manifests can be loaded." });
    }

    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound(new { error = "Kvemo manifest was not found." });
    }

    var info = new FileInfo(fullPath);
    if (info.Length > ViewerImageFilePolicy.MaximumManifestBytes)
    {
        return Results.BadRequest(new { error = "Kvemo manifest is too large for viewer preview." });
    }

    var text = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
    return Results.Text(text, "application/x-ndjson");
});

app.MapGet("/api/json-file", async (string path, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "JSON path is required." });
    }

    var fullPath = Path.GetFullPath(path);
    if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only JSON files can be loaded." });
    }

    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound(new { error = "JSON file was not found." });
    }

    var info = new FileInfo(fullPath);
    if (info.Length > ViewerJsonFilePolicy.MaximumJsonBytes)
    {
        return Results.BadRequest(new { error = "JSON file is too large for viewer preview." });
    }

    var text = await System.IO.File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
    return Results.Text(text, "application/json");
});

app.MapGet("/api/pdf-file", (string path) =>
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.BadRequest(new { error = "PDF path is required." });
    }

    var fullPath = Path.GetFullPath(path);
    if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Only PDF files can be loaded." });
    }

    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound(new { error = "PDF file was not found." });
    }

    var info = new FileInfo(fullPath);
    if (info.Length > ViewerPdfFilePolicy.MaximumPdfBytes)
    {
        return Results.BadRequest(new { error = "PDF file is too large for viewer preview." });
    }

    return Results.File(fullPath, "application/pdf", Path.GetFileName(fullPath));
});

app.MapPost("/api/scan", async (IFormFile file, CancellationToken cancellationToken) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest(new { error = "The uploaded PDF is empty." });
    }

    if (!Path.GetExtension(file.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "OpenPlanTrace Viewer currently accepts PDF files." });
    }

    var registry = new PlanDocumentLoaderRegistry(new IPlanDocumentLoader[]
    {
        new PdfPigPlanDocumentLoader()
    });
    var engine = new OpenPlanTraceEngine(registry);
    var source = PlanSourceDescriptor.FromFileNameOrExtension(file.FileName);

    try
    {
        await using var stream = file.OpenReadStream();
        var result = await engine.ScanAsync(
            stream,
            source,
            scannerOptions: new ScannerOptions
            {
                SheetMargin = 12,
                MinWallLength = 24,
                WallMergeTolerance = 2.5,
                WallSnapTolerance = 3,
                DefaultWallThickness = 4,
                MinOpeningGap = 8,
                MaxOpeningGap = 70
            },
            cancellationToken: cancellationToken);

        return Results.Ok(PlanTraceExport.From(result));
    }
    catch (Exception exception)
    {
        return Results.Problem(
            detail: exception.Message,
            title: "PDF scan failed",
            statusCode: StatusCodes.Status422UnprocessableEntity);
    }
}).DisableAntiforgery();

try
{
    await app.StartAsync().ConfigureAwait(false);
}
catch (IOException exception)
{
    Console.Error.WriteLine($"OpenPlanTrace Viewer could not start at {launch.Url}: {exception.Message}");
    return;
}

Console.WriteLine($"OpenPlanTrace Viewer running at {launch.Url}");
if (launch.AutoOpenBrowser)
{
    ViewerProcessHelpers.OpenBrowser(launch.Url);
}

await app.WaitForShutdownAsync().ConfigureAwait(false);

internal sealed record ViewerLaunchOptions(
    string Url,
    bool AutoOpenBrowser,
    bool HasExplicitAspNetUrl,
    string[] AspNetArgs)
{
    public static ViewerLaunchOptions Parse(string[] args)
    {
        var aspNetArgs = new List<string>();
        var url = Environment.GetEnvironmentVariable("OPENPLANTRACE_VIEWER_URL");
        var autoOpen = true;
        var hasExplicitAspNetUrl = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--viewer-url":
                    url = ReadValue(args, ref index, arg);
                    break;
                case "--no-open":
                    autoOpen = false;
                    break;
                case "--open":
                    autoOpen = true;
                    break;
                case "--urls":
                    hasExplicitAspNetUrl = true;
                    aspNetArgs.Add(arg);
                    aspNetArgs.Add(ReadValue(args, ref index, arg));
                    break;
                default:
                    if (arg.StartsWith("--urls=", StringComparison.Ordinal))
                    {
                        hasExplicitAspNetUrl = true;
                    }

                    aspNetArgs.Add(arg);
                    break;
            }
        }

        return new ViewerLaunchOptions(
            string.IsNullOrWhiteSpace(url) ? "http://127.0.0.1:5077" : url.Trim(),
            autoOpen,
            hasExplicitAspNetUrl,
            aspNetArgs.ToArray());
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal static class ViewerProcessHelpers
{
    public static async Task<bool> IsViewerReadyAsync(string url)
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(500)
            };
            using var response = await client.GetAsync($"{url.TrimEnd('/')}/api/health").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return content.Contains("\"ready\"", StringComparison.OrdinalIgnoreCase)
                || content.Contains("ready", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not open browser automatically: {exception.Message}");
            Console.Error.WriteLine($"Open this URL manually: {url}");
        }
    }
}

internal static class ViewerImageFilePolicy
{
    public const long MaximumImageBytes = 20 * 1024 * 1024;
    public const long MaximumManifestBytes = 100 * 1024 * 1024;

    public static bool TryGetContentType(string path, out string contentType)
    {
        contentType = string.Empty;
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                contentType = "image/png";
                return true;
            case ".jpg":
            case ".jpeg":
                contentType = "image/jpeg";
                return true;
            case ".webp":
                contentType = "image/webp";
                return true;
            default:
                return false;
        }
    }
}

internal static class ViewerJsonFilePolicy
{
    public const long MaximumJsonBytes = 250 * 1024 * 1024;
}

internal static class ViewerPdfFilePolicy
{
    public const long MaximumPdfBytes = 250 * 1024 * 1024;
}

internal sealed record ScanResponse(
    string DocumentId,
    IReadOnlyList<PageDto> Pages,
    IReadOnlyList<LayerDto> Layers,
    CalibrationDto Calibration,
    MeasurementConsistencyDto MeasurementConsistency,
    IReadOnlyList<TitleBlockDto> TitleBlocks,
    IReadOnlyList<DimensionDto> Dimensions,
    IReadOnlyList<AnnotationBlockDto> Annotations,
    IReadOnlyList<GridAxisDto> GridAxes,
    IReadOnlyList<GridBaySpacingDto> GridBaySpacings,
    IReadOnlyList<RegionDto> Regions,
    IReadOnlyList<SurfacePatternDto> SurfacePatterns,
    IReadOnlyList<WallDto> Walls,
    IReadOnlyList<NodeDto> Nodes,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<WallComponentDto> WallComponents,
    IReadOnlyList<WallGraphRepairCandidateDto> WallGraphRepairCandidates,
    IReadOnlyList<RoomDto> Rooms,
    RoomAdjacencyGraphDto RoomAdjacencyGraph,
    IReadOnlyList<OpeningDto> Openings,
    IReadOnlyList<ObjectDto> Objects,
    IReadOnlyList<ObjectGroupDto> ObjectGroups,
    IReadOnlyList<ObjectAggregateDto> ObjectAggregates,
    RoutingLayerDto RoutingLayer,
    ObjectReviewDataset ObjectReviewDataset,
    QualityDto Quality,
    DiagnosticsDto Diagnostics)
{
    public static ScanResponse From(PlanScanResult result) =>
        Create(result);

    private static ScanResponse Create(PlanScanResult result)
    {
        var sourceLayerLookup = BuildSourceLayerLookup(result.Document);
        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);

        return new ScanResponse(
            result.Document.Id,
            result.Document.Pages.Select(PageDto.From).ToArray(),
            result.LayerAnalysis.Layers.Select(LayerDto.From).ToArray(),
            CalibrationDto.From(result.Calibration),
            MeasurementConsistencyDto.From(result.MeasurementConsistency, sourceLayerLookup),
            result.TitleBlocks.Select(titleBlock => TitleBlockDto.From(titleBlock, sourceLayerLookup)).ToArray(),
            result.Dimensions.Select(dimension => DimensionDto.From(dimension, sourceLayerLookup)).ToArray(),
            result.Annotations.Select(annotation => AnnotationBlockDto.From(annotation, sourceLayerLookup)).ToArray(),
            result.GridAxes.Select(axis => GridAxisDto.From(axis, sourceLayerLookup)).ToArray(),
            result.GridBaySpacings.Select(bay => GridBaySpacingDto.From(bay, sourceLayerLookup)).ToArray(),
            result.SheetRegions.Select(region => RegionDto.From(region, sourceLayerLookup)).ToArray(),
            result.SurfacePatterns.Select(pattern => SurfacePatternDto.From(pattern, sourceLayerLookup)).ToArray(),
            result.Walls.Select(wall => WallDto.From(wall, sourceLayerLookup, wallComponentLookup)).ToArray(),
            result.WallGraph.Nodes.Select(NodeDto.From).ToArray(),
            result.WallGraph.Edges.Select(EdgeDto.From).ToArray(),
            result.WallGraph.Components.Select(component => WallComponentDto.From(component, sourceLayerLookup)).ToArray(),
            result.WallGraph.RepairCandidates.Select(candidate => WallGraphRepairCandidateDto.From(candidate, sourceLayerLookup)).ToArray(),
            result.Rooms.Select(RoomDto.From).ToArray(),
            RoomAdjacencyGraphDto.From(result.RoomAdjacencyGraph),
            result.Openings.Select(opening => OpeningDto.From(opening, sourceLayerLookup)).ToArray(),
            result.ObjectCandidates.Select(candidate => ObjectDto.From(candidate, sourceLayerLookup)).ToArray(),
            result.ObjectGroups.Select(group => ObjectGroupDto.From(group, sourceLayerLookup)).ToArray(),
            result.ObjectAggregates.Select(aggregate => ObjectAggregateDto.From(aggregate, sourceLayerLookup)).ToArray(),
            RoutingLayerDto.From(result.RoutingLayer, sourceLayerLookup),
            ObjectReviewDatasetBuilder.FromScanResult(result),
            QualityDto.From(result.Quality),
            DiagnosticsDto.From(result.Diagnostics));
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
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

    private static IReadOnlyDictionary<string, string> BuildSourceLayerLookup(PlanDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var sourceId = primitive.SourceId ?? primitive.Source.SourceId ?? $"p{page.Number}:primitive:{index}";
                var layer = primitive.Source.Layer ?? primitive.Layer;
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    result[sourceId] = layer;
                }
            }
        }

        return result;
    }
}

internal sealed record RoomAdjacencyGraphDto(
    IReadOnlyList<RoomAdjacencyEdgeDto> Edges,
    IReadOnlyList<RoomClusterDto> Clusters)
{
    public static RoomAdjacencyGraphDto From(RoomAdjacencyGraph graph) =>
        new(
            graph.Edges.Select(RoomAdjacencyEdgeDto.From).ToArray(),
            graph.Clusters.Select(RoomClusterDto.From).ToArray());
}

internal sealed record RoomClusterDto(
    string Id,
    int PageNumber,
    IReadOnlyList<string> RoomIds,
    IReadOnlyList<string> RoomLabels,
    RoomClusterKind Kind,
    RectDto Bounds,
    double DrawingArea,
    double? AreaSquareMeters,
    IReadOnlyList<string> RoomAdjacencyIds,
    IReadOnlyList<string> OpeningIds,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static RoomClusterDto From(RoomCluster cluster) =>
        new(
            cluster.Id,
            cluster.PageNumber,
            cluster.RoomIds,
            cluster.RoomLabels,
            cluster.Kind,
            RectDto.From(cluster.Bounds),
            cluster.DrawingArea,
            cluster.AreaSquareMeters,
            cluster.RoomAdjacencyIds,
            cluster.OpeningIds,
            cluster.Confidence.Value,
            cluster.Evidence);
}

internal sealed record RoomAdjacencyEdgeDto(
    string Id,
    int PageNumber,
    string FirstRoomId,
    string? FirstRoomLabel,
    string SecondRoomId,
    string? SecondRoomLabel,
    RoomAdjacencyKind Kind,
    RoomAdjacencyDirection DirectionFromFirstToSecond,
    RoomAdjacencyDirection DirectionFromSecondToFirst,
    double SharedBoundaryLength,
    LineDto? SharedBoundary,
    double Confidence,
    IReadOnlyList<string> SharedWallIds,
    IReadOnlyList<string> OpeningIds,
    IReadOnlyList<string> Evidence)
{
    public static RoomAdjacencyEdgeDto From(RoomAdjacencyEdge edge) =>
        new(
            edge.Id,
            edge.PageNumber,
            edge.FirstRoomId,
            edge.FirstRoomLabel,
            edge.SecondRoomId,
            edge.SecondRoomLabel,
            edge.Kind,
            edge.DirectionFromFirstToSecond,
            edge.DirectionFromSecondToFirst,
            edge.SharedBoundaryLength,
            edge.SharedBoundary is null ? null : LineDto.From(edge.SharedBoundary.Value),
            edge.Confidence.Value,
            edge.SharedWallIds,
            edge.OpeningIds,
            edge.Evidence);
}

internal sealed record GridAxisDto(
    string Id,
    int PageNumber,
    GridAxisOrientation Orientation,
    string? Label,
    LineDto Line,
    RectDto Bounds,
    double Coordinate,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> LabelSourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static GridAxisDto From(
        GridAxis axis,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            axis.Id,
            axis.PageNumber,
            axis.Orientation,
            axis.Label,
            LineDto.From(axis.Line),
            RectDto.From(axis.Bounds),
            axis.Coordinate,
            axis.Confidence.Value,
            axis.SourceRegionId,
            axis.SourcePrimitiveIds,
            axis.LabelSourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(axis.SourcePrimitiveIds, sourceLayerLookup),
            axis.Evidence);
}

internal sealed record GridBaySpacingDto(
    string Id,
    int PageNumber,
    GridAxisOrientation AxisOrientation,
    string FirstAxisId,
    string? FirstAxisLabel,
    string SecondAxisId,
    string? SecondAxisLabel,
    LineDto Line,
    RectDto Bounds,
    double DrawingDistance,
    double? DistanceMeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static GridBaySpacingDto From(
        GridBaySpacing bay,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            bay.Id,
            bay.PageNumber,
            bay.AxisOrientation,
            bay.FirstAxisId,
            bay.FirstAxisLabel,
            bay.SecondAxisId,
            bay.SecondAxisLabel,
            LineDto.From(bay.Line),
            RectDto.From(bay.Bounds),
            bay.DrawingDistance,
            bay.DistanceMeters,
            bay.MeasurementScaleGroupId,
            bay.Confidence.Value,
            bay.SourceRegionId,
            bay.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(bay.SourcePrimitiveIds, sourceLayerLookup),
            bay.Evidence);
}

internal sealed record AnnotationBlockDto(
    string Id,
    int PageNumber,
    PlanAnnotationKind Kind,
    string? Label,
    RectDto Bounds,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<AnnotationItemDto> Items)
{
    public static AnnotationBlockDto From(
        PlanAnnotationBlock annotation,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            annotation.Id,
            annotation.PageNumber,
            annotation.Kind,
            annotation.Label,
            RectDto.From(annotation.Bounds),
            annotation.Confidence.Value,
            annotation.SourceRegionId,
            annotation.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(annotation.SourcePrimitiveIds, sourceLayerLookup),
            annotation.Evidence,
            annotation.Items.Select(item => AnnotationItemDto.From(item, sourceLayerLookup)).ToArray());
}

internal sealed record AnnotationItemDto(
    string Id,
    int PageNumber,
    PlanAnnotationItemKind Kind,
    string Text,
    string? Marker,
    RectDto Bounds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static AnnotationItemDto From(
        PlanAnnotationItem item,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            item.Id,
            item.PageNumber,
            item.Kind,
            item.Text,
            item.Marker,
            RectDto.From(item.Bounds),
            item.Confidence.Value,
            item.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(item.SourcePrimitiveIds, sourceLayerLookup),
            item.Evidence);
}

internal sealed record MeasurementConsistencyDto(
    bool HasReliableCalibration,
    double? SelectedMillimetersPerDrawingUnit,
    double? MedianDimensionMillimetersPerDrawingUnit,
    double? DimensionScaleSpreadRatio,
    double Confidence,
    int CheckedCount,
    int ConsistentCount,
    int OutlierCount,
    IReadOnlyList<MeasurementConsistencyCheckDto> Checks)
{
    public static MeasurementConsistencyDto From(
        MeasurementConsistencyReport report,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            report.HasReliableCalibration,
            report.SelectedMillimetersPerDrawingUnit,
            report.MedianDimensionMillimetersPerDrawingUnit,
            report.DimensionScaleSpreadRatio,
            report.Confidence.Value,
            report.CheckedCount,
            report.ConsistentCount,
            report.OutlierCount,
            report.Checks.Select(check => MeasurementConsistencyCheckDto.From(check, sourceLayerLookup)).ToArray());
}

internal sealed record MeasurementConsistencyCheckDto(
    string DimensionId,
    int PageNumber,
    MeasurementConsistencyStatus Status,
    double DimensionMillimeters,
    double DrawingLength,
    double ImpliedMillimetersPerDrawingUnit,
    double? SelectedMillimetersPerDrawingUnit,
    double? ExpectedMillimeters,
    double? DeltaMillimeters,
    double? RelativeError,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static MeasurementConsistencyCheckDto From(
        MeasurementConsistencyCheck check,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            check.DimensionId,
            check.PageNumber,
            check.Status,
            check.DimensionMillimeters,
            check.DrawingLength,
            check.ImpliedMillimetersPerDrawingUnit,
            check.SelectedMillimetersPerDrawingUnit,
            check.ExpectedMillimeters,
            check.DeltaMillimeters,
            check.RelativeError,
            check.Confidence.Value,
            check.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(check.SourcePrimitiveIds, sourceLayerLookup),
            check.Evidence);
}

internal sealed record DimensionDto(
    string Id,
    int PageNumber,
    DimensionKind Kind,
    DimensionOrientation Orientation,
    string Text,
    string NormalizedText,
    RectDto Bounds,
    PlanMeasurementUnit Unit,
    double MeasuredMillimeters,
    LineDto? DimensionLine,
    double? DrawingLength,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static DimensionDto From(
        DimensionAnnotation dimension,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            dimension.Id,
            dimension.PageNumber,
            dimension.Kind,
            dimension.Orientation,
            dimension.Text,
            dimension.NormalizedText,
            RectDto.From(dimension.Bounds),
            dimension.Unit,
            dimension.MeasuredMillimeters,
            dimension.DimensionLine is null ? null : LineDto.From(dimension.DimensionLine.Value),
            dimension.DrawingLength,
            dimension.MillimetersPerDrawingUnit,
            dimension.Confidence.Value,
            dimension.SourceRegionId,
            dimension.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(dimension.SourcePrimitiveIds, sourceLayerLookup),
            dimension.Evidence);
}

internal sealed record TitleBlockDto(
    string RegionId,
    int PageNumber,
    RectDto Bounds,
    double Confidence,
    string? ProjectName,
    string? SheetNumber,
    string? SheetTitle,
    string? Revision,
    string? IssueDate,
    string? Scale,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<TitleBlockFieldDto> Fields)
{
    public static TitleBlockDto From(
        TitleBlockAnalysis titleBlock,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            titleBlock.RegionId,
            titleBlock.PageNumber,
            RectDto.From(titleBlock.Bounds),
            titleBlock.Confidence.Value,
            titleBlock.ProjectName,
            titleBlock.SheetNumber,
            titleBlock.SheetTitle,
            titleBlock.Revision,
            titleBlock.IssueDate,
            titleBlock.Scale,
            titleBlock.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(titleBlock.SourcePrimitiveIds, sourceLayerLookup),
            titleBlock.Fields.Select(field => TitleBlockFieldDto.From(field, sourceLayerLookup)).ToArray());
}

internal sealed record TitleBlockFieldDto(
    TitleBlockFieldKind Kind,
    string Value,
    string RawText,
    int PageNumber,
    RectDto Bounds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static TitleBlockFieldDto From(
        TitleBlockField field,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            field.Kind,
            field.Value,
            field.RawText,
            field.PageNumber,
            RectDto.From(field.Bounds),
            field.Confidence.Value,
            field.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(field.SourcePrimitiveIds, sourceLayerLookup),
            field.Evidence);
}

internal sealed record PageDto(int Number, double Width, double Height, int PrimitiveCount)
{
    public static PageDto From(PlanPage page) =>
        new(page.Number, page.Size.Width, page.Size.Height, page.Primitives.Count);
}

internal sealed record LayerDto(
    string Name,
    string? SourceFormat,
    int EntityCount,
    IReadOnlyDictionary<string, int> PrimitiveKindCounts,
    double TotalLineLength,
    RectDto Bounds,
    LayerCategory LikelyCategory,
    double Confidence,
    IReadOnlyList<LayerCategoryScoreDto> CategoryScores,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<int> PageNumbers)
{
    public static LayerDto From(LayerSummary summary) =>
        new(
            summary.Name,
            summary.SourceFormat,
            summary.EntityCount,
            summary.PrimitiveKindCounts.ToDictionary(item => item.Key.ToString(), item => item.Value),
            summary.TotalLineLength,
            RectDto.From(summary.Bounds),
            summary.LikelyCategory,
            summary.Confidence.Value,
            summary.CategoryScores.Select(LayerCategoryScoreDto.From).ToArray(),
            summary.Evidence,
            summary.PageNumbers);
}

internal sealed record LayerCategoryScoreDto(
    LayerCategory Category,
    double Score,
    IReadOnlyList<string> Evidence)
{
    public static LayerCategoryScoreDto From(LayerCategoryScore score) =>
        new(
            score.Category,
            score.Score,
            score.Evidence);
}

internal sealed record CalibrationDto(
    PlanMeasurementUnit DrawingUnit,
    PlanMeasurementUnit RealWorldUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    bool HasReliableMeasurementScale,
    IReadOnlyList<CalibrationScaleGroupDto> ScaleGroups,
    IReadOnlyList<CalibrationEvidenceDto> Evidence)
{
    public static CalibrationDto From(PlanCalibration calibration) =>
        new(
            calibration.DrawingUnit,
            calibration.RealWorldUnit,
            calibration.ScaleRatio,
            calibration.MillimetersPerDrawingUnit,
            calibration.Confidence.Value,
            calibration.HasReliableMeasurementScale,
            calibration.ScaleGroups.Select(CalibrationScaleGroupDto.From).ToArray(),
            calibration.Evidence.Select(CalibrationEvidenceDto.From).ToArray());
}

internal sealed record CalibrationScaleGroupDto(
    string Id,
    int? PageNumber,
    CalibrationScaleScope Scope,
    PlanMeasurementUnit DrawingUnit,
    PlanMeasurementUnit EvidenceUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    int EvidenceCount,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceRegionIds,
    RectDto? Bounds,
    IReadOnlyList<string> Evidence)
{
    public static CalibrationScaleGroupDto From(CalibrationScaleGroup group) =>
        new(
            group.Id,
            group.PageNumber,
            group.Scope,
            group.DrawingUnit,
            group.RealWorldUnit,
            group.ScaleRatio,
            group.MillimetersPerDrawingUnit,
            group.EvidenceCount,
            group.Confidence.Value,
            group.SourcePrimitiveIds,
            group.SourceRegionIds,
            group.Bounds is null ? null : RectDto.From(group.Bounds.Value),
            group.Evidence);
}

internal sealed record CalibrationEvidenceDto(
    CalibrationEvidenceKind Kind,
    int? PageNumber,
    string? SourcePrimitiveId,
    string? Text,
    PlanMeasurementUnit Unit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    string Description)
{
    public static CalibrationEvidenceDto From(CalibrationEvidence evidence) =>
        new(
            evidence.Kind,
            evidence.PageNumber,
            evidence.SourcePrimitiveId,
            evidence.Text,
            evidence.Unit,
            evidence.ScaleRatio,
            evidence.MillimetersPerDrawingUnit,
            evidence.Confidence.Value,
            evidence.Description);
}

internal sealed record RegionDto(
    string Id,
    int PageNumber,
    RegionKind Kind,
    RectDto Bounds,
    double Confidence,
    string? Label,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers)
{
    public static RegionDto From(SheetRegion region, IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            region.Id,
            region.PageNumber,
            region.Kind,
            RectDto.From(region.Bounds),
            region.Confidence.Value,
            region.Label,
            region.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(region.SourcePrimitiveIds, sourceLayerLookup));
}

internal sealed record SurfacePatternDto(
    string Id,
    int PageNumber,
    SurfacePatternKind Kind,
    SurfacePatternOrientation Orientation,
    RectDto Bounds,
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
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static SurfacePatternDto From(
        SurfacePatternCandidate pattern,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            pattern.Id,
            pattern.PageNumber,
            pattern.Kind,
            pattern.Orientation,
            RectDto.From(pattern.Bounds),
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
            pattern.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(pattern.SourcePrimitiveIds, sourceLayerLookup),
            pattern.Evidence);
}

internal sealed record WallDto(
    string Id,
    int PageNumber,
    LineDto CenterLine,
    RectDto Bounds,
    double Thickness,
    WallDetectionKind DetectionKind,
    string? WallComponentId,
    WallGraphComponentKind? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    WallPairEvidenceDto? PairEvidence,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static WallDto From(
        WallSegment wall,
        IReadOnlyDictionary<string, string> sourceLayerLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        return
        new(
            wall.Id,
            wall.PageNumber,
            LineDto.From(wall.CenterLine),
            RectDto.From(wall.Bounds),
            wall.Thickness,
            wall.DetectionKind,
            component?.Id,
            component?.Kind,
            component?.ExcludedFromStructuralTopology ?? false,
            wall.DrawingLength,
            wall.LengthMeters,
            wall.ThicknessMillimeters,
            wall.MeasurementScaleGroupId,
            wall.Confidence.Value,
            wall.SourcePrimitiveIds,
            wall.PairEvidence is null ? null : WallPairEvidenceDto.From(wall.PairEvidence),
            wall.Evidence,
            SourceLayerDtoHelpers.SourceLayers(wall.SourcePrimitiveIds, sourceLayerLookup));
    }
}

internal sealed record WallPairEvidenceDto(
    LineDto FirstFaceLine,
    LineDto SecondFaceLine,
    double FaceSeparation,
    double OverlapRatio,
    double Score,
    int FirstFaceFragmentCount,
    int SecondFaceFragmentCount,
    IReadOnlyList<string> FirstFaceSourcePrimitiveIds,
    IReadOnlyList<string> SecondFaceSourcePrimitiveIds)
{
    public static WallPairEvidenceDto From(WallPairEvidence evidence) =>
        new(
            LineDto.From(evidence.FirstFaceLine),
            LineDto.From(evidence.SecondFaceLine),
            evidence.FaceSeparation,
            evidence.OverlapRatio,
            evidence.Score,
            evidence.FirstFaceFragmentCount,
            evidence.SecondFaceFragmentCount,
            evidence.FirstFaceSourcePrimitiveIds,
            evidence.SecondFaceSourcePrimitiveIds);
}

internal sealed record NodeDto(
    string Id,
    int PageNumber,
    PointDto Position,
    WallNodeKind Kind,
    int Degree,
    IReadOnlyList<string> Directions,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static NodeDto From(WallNode node) =>
        new(
            node.Id,
            node.PageNumber,
            PointDto.From(node.Position),
            node.Kind,
            node.Degree,
            node.Directions,
            node.Confidence.Value,
            node.Evidence);
}

internal sealed record EdgeDto(
    string Id,
    int PageNumber,
    string FromNodeId,
    string ToNodeId,
    string WallId,
    double Confidence)
{
    public static EdgeDto From(WallEdge edge) =>
        new(edge.Id, edge.PageNumber, edge.FromNodeId, edge.ToNodeId, edge.WallId, edge.Confidence.Value);
}

internal sealed record WallComponentDto(
    string Id,
    int PageNumber,
    WallGraphComponentKind Kind,
    RectDto Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    int WallCount,
    int NodeCount,
    int EdgeCount,
    double DrawingLength,
    double Confidence,
    bool ExcludedFromStructuralTopology,
    IReadOnlyList<string> Evidence)
{
    public static WallComponentDto From(
        WallGraphComponent component,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            component.Id,
            component.PageNumber,
            component.Kind,
            RectDto.From(component.Bounds),
            component.WallIds,
            component.NodeIds,
            component.EdgeIds,
            component.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(component.SourcePrimitiveIds, sourceLayerLookup),
            component.WallCount,
            component.NodeCount,
            component.EdgeCount,
            component.DrawingLength,
            component.Confidence.Value,
            component.ExcludedFromStructuralTopology,
            component.Evidence);
}

internal sealed record WallGraphRepairCandidateDto(
    string Id,
    int PageNumber,
    WallGraphRepairCandidateKind Kind,
    WallGraphRepairAction SuggestedAction,
    string SourceNodeId,
    PointDto SourcePoint,
    PointDto TargetPoint,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistance,
    LineDto RepairLine,
    RectDto Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    double Confidence,
    bool RequiresReview,
    IReadOnlyList<string> Evidence)
{
    public static WallGraphRepairCandidateDto From(
        WallGraphRepairCandidate candidate,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            candidate.Id,
            candidate.PageNumber,
            candidate.Kind,
            candidate.SuggestedAction,
            candidate.SourceNodeId,
            PointDto.From(candidate.SourcePoint),
            PointDto.From(candidate.TargetPoint),
            candidate.TargetNodeId,
            candidate.HostWallId,
            candidate.GapDistance,
            LineDto.From(candidate.RepairLine),
            RectDto.From(candidate.Bounds),
            candidate.WallIds,
            candidate.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(candidate.SourcePrimitiveIds, sourceLayerLookup),
            candidate.Confidence.Value,
            candidate.RequiresReview,
            candidate.Evidence);
}

internal sealed record RoomDto(
    string Id,
    int PageNumber,
    RectDto Bounds,
    IReadOnlyList<PointDto> Boundary,
    IReadOnlyList<string> WallIds,
    double DrawingArea,
    double? AreaSquareMeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    string? Label,
    RoomUseKind UseKind,
    IReadOnlyList<string> LabelSourcePrimitiveIds,
    IReadOnlyList<string> Evidence)
{
    public static RoomDto From(RoomRegion room) =>
        new(
            room.Id,
            room.PageNumber,
            RectDto.From(room.Bounds),
            room.Boundary.Select(PointDto.From).ToArray(),
            room.WallIds,
            room.DrawingArea,
            room.AreaSquareMeters,
            room.MeasurementScaleGroupId,
            room.Confidence.Value,
            room.Label,
            room.UseKind,
            room.LabelSourcePrimitiveIds,
            room.Evidence);
}

internal sealed record OpeningDto(
    string Id,
    int PageNumber,
    OpeningType Type,
    OpeningOperation Operation,
    OpeningOrientation Orientation,
    LineDto CenterLine,
    RectDto Bounds,
    IReadOnlyList<string> AdjacentWallIds,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    IReadOnlyList<OpeningRoomConnectionDto> ConnectedRoomLinks,
    IReadOnlyList<string> RoomAdjacencyIds,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    OpeningHingeSide HingeSide,
    OpeningSwingSide SwingSide,
    OpeningSwingDirection SwingDirection,
    PointDto? HingePoint,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static OpeningDto From(OpeningCandidate opening, IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            opening.Id,
            opening.PageNumber,
            opening.Type,
            opening.Operation,
            opening.Orientation,
            LineDto.From(opening.CenterLine),
            RectDto.From(opening.Bounds),
            opening.AdjacentWallIds,
            opening.HostWallIds,
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.ConnectedRoomLinks.Select(OpeningRoomConnectionDto.From).ToArray(),
            opening.RoomAdjacencyIds,
            opening.DrawingWidth,
            opening.WidthMillimeters,
            opening.MeasurementScaleGroupId,
            opening.HingeSide,
            opening.SwingSide,
            opening.SwingDirection,
            opening.HingePoint is null ? null : PointDto.From(opening.HingePoint.Value),
            opening.Confidence.Value,
            opening.SourcePrimitiveIds,
            opening.Evidence,
            SourceLayerDtoHelpers.SourceLayers(opening.SourcePrimitiveIds, sourceLayerLookup));
}

internal sealed record OpeningRoomConnectionDto(
    string RoomId,
    string? RoomLabel,
    RoomUseKind RoomUseKind,
    IReadOnlyList<string> RoomAdjacencyIds,
    double DistanceToOpening,
    bool SharesHostWall,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static OpeningRoomConnectionDto From(OpeningRoomConnection connection) =>
        new(
            connection.RoomId,
            connection.RoomLabel,
            connection.RoomUseKind,
            connection.RoomAdjacencyIds,
            connection.DistanceToOpening,
            connection.SharesHostWall,
            connection.Confidence.Value,
            connection.Evidence);
}

internal sealed record ObjectDto(
    string Id,
    int PageNumber,
    ObjectCandidateKind Kind,
    ObjectCategory Category,
    RectDto Bounds,
    double Confidence,
    string? Label,
    string? SymbolName,
    string? DetectedTag,
    string? DetectedTagSourcePrimitiveId,
    string? RoomId,
    string? RoomLabel,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<ObjectNearbyTextDto> NearbyText,
    VisualAiDto? VisualAi,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static ObjectDto From(ObjectCandidate candidate, IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            candidate.Id,
            candidate.PageNumber,
            candidate.Kind,
            candidate.Category,
            RectDto.From(candidate.Bounds),
            candidate.Confidence.Value,
            candidate.Label,
            candidate.SymbolName,
            candidate.DetectedTag,
            candidate.DetectedTagSourcePrimitiveId,
            candidate.RoomId,
            candidate.RoomLabel,
            candidate.SourcePrimitiveIds,
            candidate.NearbyText.Select(ObjectNearbyTextDto.From).ToArray(),
            candidate.VisualAi is null ? null : VisualAiDto.From(candidate.VisualAi),
            candidate.Evidence,
            SourceLayerDtoHelpers.SourceLayers(candidate.SourcePrimitiveIds, sourceLayerLookup));
}

internal sealed record ObjectNearbyTextDto(
    string Text,
    int PageNumber,
    RectDto Bounds,
    string SourcePrimitiveId,
    double Distance)
{
    public static ObjectNearbyTextDto From(ObjectNearbyText text) =>
        new(
            text.Text,
            text.PageNumber,
            RectDto.From(text.Bounds),
            text.SourcePrimitiveId,
            text.Distance);
}

internal sealed record VisualAiDto(
    string Label,
    ObjectCategory Category,
    double Confidence,
    string ModelName,
    string ModelVersion,
    string InferenceEngine,
    int PageNumber,
    RectDto CropBounds,
    string CropSourceId,
    IReadOnlyList<VisualAiAlternativeDto> Alternatives,
    IReadOnlyList<string> Evidence)
{
    public static VisualAiDto From(VisualAiClassification classification) =>
        new(
            classification.Label,
            classification.Category,
            classification.Confidence,
            classification.ModelName,
            classification.ModelVersion,
            classification.InferenceEngine,
            classification.PageNumber,
            RectDto.From(classification.CropBounds),
            classification.CropSourceId,
            classification.Alternatives.Select(VisualAiAlternativeDto.From).ToArray(),
            classification.Evidence);
}

internal sealed record VisualAiAlternativeDto(
    string Label,
    ObjectCategory Category,
    double Confidence,
    IReadOnlyDictionary<string, string> Evidence)
{
    public static VisualAiAlternativeDto From(VisualAiClassificationCandidate candidate) =>
        new(candidate.Label, candidate.Category, candidate.Confidence, candidate.Evidence);
}

internal sealed record ObjectGroupDto(
    string Id,
    string Signature,
    ObjectCandidateKind Kind,
    ObjectCategory Category,
    int Count,
    RectDto RepresentativeBounds,
    IReadOnlyList<int> PageNumbers,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    bool RequiresReview,
    double Confidence,
    string? Label,
    string? SymbolName,
    IReadOnlyList<string> DetectedTags,
    IReadOnlyList<ObjectNearbyTextDto> NearbyText,
    VisualAiDto? VisualAi,
    IReadOnlyList<string> Evidence)
{
    public static ObjectGroupDto From(
        ObjectCandidateGroup group,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            group.Id,
            group.Signature,
            group.Kind,
            group.Category,
            group.Count,
            RectDto.From(group.RepresentativeBounds),
            group.PageNumbers,
            group.CandidateIds,
            group.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(group.SourcePrimitiveIds, sourceLayerLookup),
            group.RequiresReview,
            group.Confidence.Value,
            group.Label,
            group.SymbolName,
            group.DetectedTags,
            group.NearbyText.Select(ObjectNearbyTextDto.From).ToArray(),
            group.VisualAi is null ? null : VisualAiDto.From(group.VisualAi),
            group.Evidence);
}

internal sealed record ObjectAggregateDto(
    string Id,
    int PageNumber,
    RectDto Bounds,
    ObjectCategory Category,
    ObjectCandidateKind Kind,
    int ChildObjectCount,
    IReadOnlyList<string> ChildObjectIds,
    IReadOnlyList<string> ObjectGroupIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    ObjectRoutingInfluence RoutingInfluence,
    ObjectStructuralInfluence StructuralInfluence,
    bool SuppressChildObjectsForRouting,
    RoomUseKind RoomUseEvidence,
    double Confidence,
    string? Label,
    string? RoomId,
    string? RoomLabel,
    bool RequiresReview,
    IReadOnlyList<string> NearbyText,
    IReadOnlyList<string> Evidence)
{
    public static ObjectAggregateDto From(
        ObjectAggregate aggregate,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            aggregate.Id,
            aggregate.PageNumber,
            RectDto.From(aggregate.Bounds),
            aggregate.Category,
            aggregate.Kind,
            aggregate.ChildObjectCount,
            aggregate.ChildObjectIds,
            aggregate.ObjectGroupIds,
            aggregate.SourcePrimitiveIds,
            aggregate.SourceLayers.Count > 0
                ? aggregate.SourceLayers
                : SourceLayerDtoHelpers.SourceLayers(aggregate.SourcePrimitiveIds, sourceLayerLookup),
            aggregate.RoutingInfluence,
            aggregate.StructuralInfluence,
            aggregate.SuppressChildObjectsForRouting,
            aggregate.RoomUseEvidence,
            aggregate.Confidence.Value,
            aggregate.Label,
            aggregate.RoomId,
            aggregate.RoomLabel,
            aggregate.RequiresReview,
            aggregate.NearbyText,
            aggregate.Evidence);
}

internal sealed record RoutingLayerDto(
    IReadOnlyList<RoutingBarrierDto> Barriers,
    IReadOnlyList<RoutingPassageDto> Passages,
    IReadOnlyList<RoutingObstacleDto> Obstacles,
    IReadOnlyList<RoutingRoomUseHintDto> RoomUseHints,
    IReadOnlyList<RoutingSuppressedObjectDto> SuppressedObjects,
    IReadOnlyList<RoutingIgnoredObjectDto> IgnoredObjects,
    IReadOnlyList<string> SuppressedObjectCandidateIds,
    IReadOnlyList<string> IgnoredObjectCandidateIds,
    IReadOnlyList<string> Evidence)
{
    public static RoutingLayerDto From(
        PlanRoutingLayer routingLayer,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            routingLayer.Barriers.Select(barrier => RoutingBarrierDto.From(barrier, sourceLayerLookup)).ToArray(),
            routingLayer.Passages.Select(passage => RoutingPassageDto.From(passage, sourceLayerLookup)).ToArray(),
            routingLayer.Obstacles.Select(obstacle => RoutingObstacleDto.From(obstacle, sourceLayerLookup)).ToArray(),
            routingLayer.RoomUseHints.Select(hint => RoutingRoomUseHintDto.From(hint, sourceLayerLookup)).ToArray(),
            routingLayer.SuppressedObjects.Select(item => RoutingSuppressedObjectDto.From(item, sourceLayerLookup)).ToArray(),
            routingLayer.IgnoredObjects.Select(item => RoutingIgnoredObjectDto.From(item, sourceLayerLookup)).ToArray(),
            routingLayer.SuppressedObjectCandidateIds,
            routingLayer.IgnoredObjectCandidateIds,
            routingLayer.Evidence);
}

internal sealed record RoutingBarrierDto(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    LineDto CenterLine,
    RectDto Bounds,
    double Thickness,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    string? WallComponentId,
    WallGraphComponentKind? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingBarrierDto From(
        RoutingBarrier barrier,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            barrier.Id,
            barrier.PageNumber,
            barrier.SourceId,
            barrier.SourceKind,
            LineDto.From(barrier.CenterLine),
            RectDto.From(barrier.Bounds),
            barrier.Thickness,
            barrier.DrawingLength,
            barrier.LengthMeters,
            barrier.ThicknessMillimeters,
            barrier.MeasurementScaleGroupId,
            barrier.WallComponentId,
            barrier.WallComponentKind,
            barrier.ExcludedFromStructuralTopology,
            barrier.Confidence.Value,
            barrier.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(barrier.SourcePrimitiveIds, sourceLayerLookup),
            barrier.Evidence);
}

internal sealed record RoutingPassageDto(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    OpeningType Type,
    OpeningOperation Operation,
    OpeningOrientation Orientation,
    LineDto CenterLine,
    RectDto Bounds,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    RoutingOpeningPlacementDto? Placement,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingPassageDto From(
        RoutingPassage passage,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            passage.Id,
            passage.PageNumber,
            passage.SourceId,
            passage.SourceKind,
            passage.Type,
            passage.Operation,
            passage.Orientation,
            LineDto.From(passage.CenterLine),
            RectDto.From(passage.Bounds),
            passage.DrawingWidth,
            passage.WidthMillimeters,
            passage.MeasurementScaleGroupId,
            passage.HostWallIds,
            passage.ConnectedRoomIds,
            passage.ConnectedRoomLabels,
            passage.Placement is null ? null : RoutingOpeningPlacementDto.From(passage.Placement),
            passage.Confidence.Value,
            passage.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(passage.SourcePrimitiveIds, sourceLayerLookup),
            passage.Evidence);
}

internal sealed record RoutingOpeningPlacementDto(
    string? HostWallId,
    IReadOnlyList<string> AnchorWallIds,
    LineDto ReferenceLine,
    PointDto StartPoint,
    PointDto EndPoint,
    double StartOffsetDrawingUnits,
    double EndOffsetDrawingUnits,
    double CenterOffsetDrawingUnits,
    double LengthDrawingUnits,
    double? StartOffsetMillimeters,
    double? EndOffsetMillimeters,
    double? CenterOffsetMillimeters,
    double? LengthMillimeters,
    double HostWallStartParameter,
    double HostWallEndParameter,
    double HostWallCenterParameter,
    VectorDto AlongVector,
    VectorDto NormalVector,
    double CrossWallOffsetDrawingUnits,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static RoutingOpeningPlacementDto From(OpeningPlacement placement) =>
        new(
            placement.HostWallId,
            placement.AnchorWallIds,
            LineDto.From(placement.ReferenceLine),
            PointDto.From(placement.StartPoint),
            PointDto.From(placement.EndPoint),
            placement.StartOffsetDrawingUnits,
            placement.EndOffsetDrawingUnits,
            placement.CenterOffsetDrawingUnits,
            placement.LengthDrawingUnits,
            placement.StartOffsetMillimeters,
            placement.EndOffsetMillimeters,
            placement.CenterOffsetMillimeters,
            placement.LengthMillimeters,
            placement.HostWallStartParameter,
            placement.HostWallEndParameter,
            placement.HostWallCenterParameter,
            VectorDto.From(placement.AlongVector),
            VectorDto.From(placement.NormalVector),
            placement.CrossWallOffsetDrawingUnits,
            placement.Confidence.Value,
            placement.Evidence);
}

internal sealed record RoutingObstacleDto(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    RoutingObstacleKind ObstacleKind,
    ObjectRoutingInfluence RoutingInfluence,
    ObjectStructuralInfluence StructuralInfluence,
    ObjectCategory Category,
    ObjectCandidateKind ObjectKind,
    RectDto Bounds,
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
    public static RoutingObstacleDto From(
        RoutingObstacle obstacle,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            obstacle.Id,
            obstacle.PageNumber,
            obstacle.SourceId,
            obstacle.SourceKind,
            obstacle.ObstacleKind,
            obstacle.RoutingInfluence,
            obstacle.StructuralInfluence,
            obstacle.Category,
            obstacle.ObjectKind,
            RectDto.From(obstacle.Bounds),
            obstacle.Label,
            obstacle.RoomId,
            obstacle.RoomLabel,
            obstacle.SuppressesChildObjects,
            obstacle.ChildObjectIds,
            obstacle.Confidence.Value,
            obstacle.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(obstacle.SourcePrimitiveIds, sourceLayerLookup),
            obstacle.Evidence);
}

internal sealed record RoutingRoomUseHintDto(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    RoomUseKind RoomUseKind,
    RectDto Bounds,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingRoomUseHintDto From(
        RoutingRoomUseHint hint,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            hint.Id,
            hint.PageNumber,
            hint.SourceId,
            hint.SourceKind,
            hint.RoomUseKind,
            RectDto.From(hint.Bounds),
            hint.RoomId,
            hint.RoomLabel,
            hint.Confidence.Value,
            hint.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(hint.SourcePrimitiveIds, sourceLayerLookup),
            hint.Evidence);
}

internal sealed record RoutingSuppressedObjectDto(
    string Id,
    int PageNumber,
    string ObjectCandidateId,
    string SuppressedByAggregateId,
    RoutingSuppressionReason Reason,
    RoutingSuppressedObjectAction Action,
    string? ReplacementRoutingObstacleId,
    string? RoomUseHintId,
    ObjectRoutingInfluence AggregateRoutingInfluence,
    ObjectStructuralInfluence AggregateStructuralInfluence,
    ObjectCategory CandidateCategory,
    ObjectCandidateKind CandidateKind,
    RectDto CandidateBounds,
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingSuppressedObjectDto From(
        RoutingSuppressedObject suppressed,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            suppressed.Id,
            suppressed.PageNumber,
            suppressed.ObjectCandidateId,
            suppressed.SuppressedByAggregateId,
            suppressed.Reason,
            suppressed.Action,
            suppressed.ReplacementRoutingObstacleId,
            suppressed.RoomUseHintId,
            suppressed.AggregateRoutingInfluence,
            suppressed.AggregateStructuralInfluence,
            suppressed.CandidateCategory,
            suppressed.CandidateKind,
            RectDto.From(suppressed.CandidateBounds),
            suppressed.CandidateLabel,
            suppressed.RoomId,
            suppressed.RoomLabel,
            suppressed.Confidence.Value,
            suppressed.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(suppressed.SourcePrimitiveIds, sourceLayerLookup),
            suppressed.Evidence);
}

internal sealed record RoutingIgnoredObjectDto(
    string Id,
    int PageNumber,
    string ObjectCandidateId,
    RoutingIgnoredObjectReason Reason,
    ObjectRoutingInfluence RoutingInfluence,
    ObjectStructuralInfluence StructuralInfluence,
    ObjectCategory CandidateCategory,
    ObjectCandidateKind CandidateKind,
    ObjectCandidateSourceKind CandidateSourceKind,
    string? SourceWallComponentId,
    WallGraphComponentKind? SourceWallComponentKind,
    RectDto CandidateBounds,
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
    public static RoutingIgnoredObjectDto From(
        RoutingIgnoredObject ignored,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        new(
            ignored.Id,
            ignored.PageNumber,
            ignored.ObjectCandidateId,
            ignored.Reason,
            ignored.RoutingInfluence,
            ignored.StructuralInfluence,
            ignored.CandidateCategory,
            ignored.CandidateKind,
            ignored.CandidateSourceKind,
            ignored.SourceWallComponentId,
            ignored.SourceWallComponentKind,
            RectDto.From(ignored.CandidateBounds),
            ignored.CandidateLabel,
            ignored.RoomId,
            ignored.RoomLabel,
            ignored.SuppressedObjectId,
            ignored.SuppressedByAggregateId,
            ignored.RoomUseHintId,
            ignored.Confidence.Value,
            ignored.SourcePrimitiveIds,
            SourceLayerDtoHelpers.SourceLayers(ignored.SourcePrimitiveIds, sourceLayerLookup),
            ignored.Evidence);
}

internal sealed record QualityDto(
    double OverallConfidence,
    string Grade,
    bool RequiresReview,
    int PageCount,
    int PrimitiveCount,
    int DetectionCount,
    int DetectorCount,
    int DetectorWithFindingsCount,
    bool HasReliableCalibration,
    int DiagnosticInfoCount,
    int DiagnosticWarningCount,
    int DiagnosticErrorCount,
    IReadOnlyList<DetectorQualityDto> Detectors,
    IReadOnlyList<QualityIssueDto> Issues,
    IReadOnlyList<string> Evidence)
{
    public static QualityDto From(PlanScanQualityReport quality) =>
        new(
            quality.OverallConfidence.Value,
            quality.Grade.ToString(),
            quality.RequiresReview,
            quality.PageCount,
            quality.PrimitiveCount,
            quality.DetectionCount,
            quality.DetectorCount,
            quality.DetectorWithFindingsCount,
            quality.HasReliableCalibration,
            quality.DiagnosticInfoCount,
            quality.DiagnosticWarningCount,
            quality.DiagnosticErrorCount,
            quality.Detectors.Select(DetectorQualityDto.From).ToArray(),
            quality.Issues.Select(QualityIssueDto.From).ToArray(),
            quality.Evidence);
}

internal sealed record DetectorQualityDto(
    string Name,
    int ItemCount,
    double AverageConfidence,
    double MinimumConfidence,
    double MaximumConfidence,
    int LowConfidenceCount,
    int ReviewRequiredCount,
    int EvidenceBearingCount,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static DetectorQualityDto From(PlanDetectorQualitySummary detector) =>
        new(
            detector.Name,
            detector.ItemCount,
            detector.AverageConfidence.Value,
            detector.MinimumConfidence.Value,
            detector.MaximumConfidence.Value,
            detector.LowConfidenceCount,
            detector.ReviewRequiredCount,
            detector.EvidenceBearingCount,
            detector.Confidence.Value,
            detector.Evidence);
}

internal sealed record QualityIssueDto(
    string Code,
    string Severity,
    string Message,
    double Confidence,
    IReadOnlyDictionary<string, string> Properties)
{
    public static QualityIssueDto From(PlanScanQualityIssue issue) =>
        new(
            issue.Code,
            issue.Severity.ToString(),
            issue.Message,
            issue.Confidence.Value,
            issue.Properties);
}

internal sealed record DiagnosticsDto(
    double DurationMilliseconds,
    bool HasErrors,
    int InfoCount,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<StageDto> Stages,
    IReadOnlyList<DiagnosticDto> Messages)
{
    public static DiagnosticsDto From(PipelineDiagnostics diagnostics) =>
        new(
            diagnostics.Duration.TotalMilliseconds,
            diagnostics.HasErrors,
            diagnostics.InfoCount,
            diagnostics.WarningCount,
            diagnostics.ErrorCount,
            diagnostics.StageReports.Select(StageDto.From).ToArray(),
            diagnostics.Messages.Select(DiagnosticDto.From).ToArray());
}

internal sealed record StageDto(
    string Stage,
    double DurationMilliseconds,
    int InputCount,
    int OutputCount,
    int DiagnosticCount,
    int InfoCount,
    int WarningCount,
    int ErrorCount)
{
    public static StageDto From(PipelineStageReport report) =>
        new(
            report.Stage,
            report.Duration.TotalMilliseconds,
            report.InputCount,
            report.OutputCount,
            report.DiagnosticCount,
            report.InfoCount,
            report.WarningCount,
            report.ErrorCount);
}

internal sealed record DiagnosticDto(
    string Code,
    DiagnosticSeverity Severity,
    string Stage,
    DiagnosticScope Scope,
    string Message,
    int? PageNumber,
    RectDto? Region,
    double? Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyDictionary<string, string> Properties)
{
    public static DiagnosticDto From(PlanDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.Stage,
            diagnostic.Scope,
            diagnostic.Message,
            diagnostic.PageNumber,
            diagnostic.Region is null ? null : RectDto.From(diagnostic.Region.Value),
            diagnostic.Confidence?.Value,
            diagnostic.SourcePrimitiveIds,
            diagnostic.Properties);
}

internal sealed record RectDto(double X, double Y, double Width, double Height)
{
    public static RectDto From(PlanRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);
}

internal sealed record PointDto(double X, double Y)
{
    public static PointDto From(PlanPoint point) => new(point.X, point.Y);
}

internal sealed record VectorDto(double X, double Y)
{
    public static VectorDto From(PlanVector vector) => new(vector.X, vector.Y);
}

internal sealed record LineDto(PointDto Start, PointDto End)
{
    public static LineDto From(PlanLineSegment line) =>
        new(PointDto.From(line.Start), PointDto.From(line.End));
}

internal static class SourceLayerDtoHelpers
{
    public static IReadOnlyList<string> SourceLayers(
        IReadOnlyList<string> sourcePrimitiveIds,
        IReadOnlyDictionary<string, string> sourceLayerLookup) =>
        sourcePrimitiveIds
            .Select(sourceId => sourceLayerLookup.TryGetValue(sourceId, out var layer) ? layer : null)
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(layer => layer!)
            .ToArray();
}
