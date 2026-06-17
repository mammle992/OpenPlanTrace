using System.Globalization;

namespace OpenPlanTrace.Export;

public sealed record PlanTraceExport(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    DocumentExport Document,
    IReadOnlyList<PageExport> Pages,
    CoordinateSystemExport CoordinateSystem,
    IReadOnlyList<PrimitiveSourceExport> PrimitiveSources,
    LayerAnalysisExport LayerAnalysis,
    CalibrationExport Calibration,
    MeasurementConsistencyExport MeasurementConsistency,
    IReadOnlyList<TitleBlockExport> TitleBlocks,
    IReadOnlyList<DimensionExport> Dimensions,
    IReadOnlyList<AnnotationBlockExport> Annotations,
    IReadOnlyList<GridAxisExport> GridAxes,
    IReadOnlyList<GridBaySpacingExport> GridBaySpacings,
    IReadOnlyList<RegionExport> Regions,
    IReadOnlyList<SurfacePatternExport> SurfacePatterns,
    IReadOnlyList<WallExport> Walls,
    WallEvidenceExport WallEvidence,
    WallTopologyPreparationExport WallTopologyPreparation,
    WallGraphExport WallGraph,
    IReadOnlyList<RoomExport> Rooms,
    RoomAdjacencyGraphExport RoomAdjacencyGraph,
    IReadOnlyList<OpeningExport> Openings,
    IReadOnlyList<ObjectExport> Objects,
    IReadOnlyList<ObjectGroupExport> ObjectGroups,
    IReadOnlyList<ObjectAggregateExport> ObjectAggregates,
    RoutingLayerExport RoutingLayer,
    PlanImportReadiness ImportReadiness,
    IReadOnlyList<ScanReviewQueueItemExport> ReviewQueue,
    QualityExport Quality,
    DiagnosticsExport Diagnostics)
{
    public const string CurrentSchemaVersion = "openplantrace.scan.v65";

    public static PlanTraceExport From(PlanScanResult result) =>
        Create(result);

    private static PlanTraceExport Create(PlanScanResult result)
    {
        var primitiveSources = PrimitiveSourceExport.From(result.Document).ToArray();
        var sourceLookup = primitiveSources
            .Where(source => !string.IsNullOrWhiteSpace(source.SourceId))
            .ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var wallComponentLookup = BuildWallComponentLookup(result.WallGraph.Components);
        var wallTopologySpans = WallGraphTopologySpanBuilder.Build(result.WallGraph, result.Walls);
        var wallTopologySpansByWallId = wallTopologySpans
            .GroupBy(span => span.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);

        return new PlanTraceExport(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            DocumentExport.From(result.Document),
            result.Document.Pages.Select(PageExport.From).ToArray(),
            CoordinateSystemExport.From(result.Document.Pages, result.Calibration),
            primitiveSources,
            LayerAnalysisExport.From(result.LayerAnalysis),
            CalibrationExport.From(result.Calibration),
            MeasurementConsistencyExport.From(result.MeasurementConsistency, sourceLookup),
            result.TitleBlocks.Select(titleBlock => TitleBlockExport.From(titleBlock, sourceLookup)).ToArray(),
            result.Dimensions.Select(dimension => DimensionExport.From(dimension, sourceLookup)).ToArray(),
            result.Annotations.Select(annotation => AnnotationBlockExport.From(annotation, sourceLookup)).ToArray(),
            result.GridAxes.Select(axis => GridAxisExport.From(axis, sourceLookup)).ToArray(),
            result.GridBaySpacings.Select(bay => GridBaySpacingExport.From(bay, sourceLookup)).ToArray(),
            result.SheetRegions.Select(region => RegionExport.From(region, sourceLookup)).ToArray(),
            result.SurfacePatterns.Select(pattern => SurfacePatternExport.From(pattern, sourceLookup)).ToArray(),
            result.Walls.Select(wall => WallExport.From(
                wall,
                sourceLookup,
                wallComponentLookup,
                wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment) ? assessment : null,
                wallTopologySpansByWallId.TryGetValue(wall.Id, out var spans) ? spans : Array.Empty<WallGraphTopologySpan>())).ToArray(),
            WallEvidenceExport.From(result.WallEvidenceMap, sourceLookup),
            WallTopologyPreparationExport.From(result.WallTopologyPreparation, sourceLookup),
            WallGraphExport.From(result.WallGraph, wallTopologySpans, sourceLookup),
            result.Rooms.Select(RoomExport.From).ToArray(),
            RoomAdjacencyGraphExport.From(result.RoomAdjacencyGraph),
            result.Openings.Select(opening => OpeningExport.From(opening, sourceLookup)).ToArray(),
            result.ObjectCandidates.Select(candidate => ObjectExport.From(candidate, sourceLookup)).ToArray(),
            result.ObjectGroups.Select(group => ObjectGroupExport.From(group, sourceLookup)).ToArray(),
            result.ObjectAggregates.Select(aggregate => ObjectAggregateExport.From(aggregate, sourceLookup)).ToArray(),
            RoutingLayerExport.From(result.RoutingLayer, sourceLookup),
            PlanImportReadiness.FromScanResult(result),
            ScanReviewQueueItemExport.From(result, sourceLookup),
            QualityExport.From(result.Quality),
            DiagnosticsExport.From(result.Diagnostics));
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

public sealed record RoomAdjacencyGraphExport(
    IReadOnlyList<RoomAdjacencyEdgeExport> Edges,
    IReadOnlyList<RoomClusterExport> Clusters)
{
    public static RoomAdjacencyGraphExport From(RoomAdjacencyGraph graph) =>
        new(
            graph.Edges.Select(RoomAdjacencyEdgeExport.From).ToArray(),
            graph.Clusters.Select(RoomClusterExport.From).ToArray());
}

public sealed record RoomClusterExport(
    string Id,
    int PageNumber,
    IReadOnlyList<string> RoomIds,
    IReadOnlyList<string> RoomLabels,
    string Kind,
    RectExport Bounds,
    double DrawingArea,
    double? AreaSquareMeters,
    IReadOnlyList<string> RoomAdjacencyIds,
    IReadOnlyList<string> OpeningIds,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static RoomClusterExport From(RoomCluster cluster) =>
        new(
            cluster.Id,
            cluster.PageNumber,
            cluster.RoomIds,
            cluster.RoomLabels,
            cluster.Kind.ToString(),
            RectExport.From(cluster.Bounds),
            cluster.DrawingArea,
            cluster.AreaSquareMeters,
            cluster.RoomAdjacencyIds,
            cluster.OpeningIds,
            cluster.Confidence.Value,
            cluster.Evidence);
}

public sealed record RoomAdjacencyEdgeExport(
    string Id,
    int PageNumber,
    string FirstRoomId,
    string? FirstRoomLabel,
    string SecondRoomId,
    string? SecondRoomLabel,
    string Kind,
    string DirectionFromFirstToSecond,
    string DirectionFromSecondToFirst,
    double SharedBoundaryLength,
    LineExport? SharedBoundary,
    double Confidence,
    IReadOnlyList<string> SharedWallIds,
    IReadOnlyList<string> OpeningIds,
    IReadOnlyList<string> Evidence)
{
    public static RoomAdjacencyEdgeExport From(RoomAdjacencyEdge edge) =>
        new(
            edge.Id,
            edge.PageNumber,
            edge.FirstRoomId,
            edge.FirstRoomLabel,
            edge.SecondRoomId,
            edge.SecondRoomLabel,
            edge.Kind.ToString(),
            edge.DirectionFromFirstToSecond.ToString(),
            edge.DirectionFromSecondToFirst.ToString(),
            edge.SharedBoundaryLength,
            edge.SharedBoundary is null ? null : LineExport.From(edge.SharedBoundary.Value),
            edge.Confidence.Value,
            edge.SharedWallIds,
            edge.OpeningIds,
            edge.Evidence);
}

public sealed record GridAxisExport(
    string Id,
    int PageNumber,
    string Orientation,
    string? Label,
    LineExport Line,
    RectExport Bounds,
    double Coordinate,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> LabelSourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static GridAxisExport From(
        GridAxis axis,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            axis.Id,
            axis.PageNumber,
            axis.Orientation.ToString(),
            axis.Label,
            LineExport.From(axis.Line),
            RectExport.From(axis.Bounds),
            axis.Coordinate,
            axis.Confidence.Value,
            axis.SourceRegionId,
            axis.SourcePrimitiveIds,
            axis.LabelSourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(axis.SourcePrimitiveIds, sourceLookup),
            axis.Evidence);
}

public sealed record GridBaySpacingExport(
    string Id,
    int PageNumber,
    string AxisOrientation,
    string FirstAxisId,
    string? FirstAxisLabel,
    string SecondAxisId,
    string? SecondAxisLabel,
    LineExport Line,
    RectExport Bounds,
    double DrawingDistance,
    double? DistanceMeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static GridBaySpacingExport From(
        GridBaySpacing bay,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            bay.Id,
            bay.PageNumber,
            bay.AxisOrientation.ToString(),
            bay.FirstAxisId,
            bay.FirstAxisLabel,
            bay.SecondAxisId,
            bay.SecondAxisLabel,
            LineExport.From(bay.Line),
            RectExport.From(bay.Bounds),
            bay.DrawingDistance,
            bay.DistanceMeters,
            bay.MeasurementScaleGroupId,
            bay.Confidence.Value,
            bay.SourceRegionId,
            bay.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(bay.SourcePrimitiveIds, sourceLookup),
            bay.Evidence);
}

public sealed record AnnotationBlockExport(
    string Id,
    int PageNumber,
    string Kind,
    string? Label,
    RectExport Bounds,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<AnnotationItemExport> Items)
{
    public static AnnotationBlockExport From(
        PlanAnnotationBlock annotation,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            annotation.Id,
            annotation.PageNumber,
            annotation.Kind.ToString(),
            annotation.Label,
            RectExport.From(annotation.Bounds),
            annotation.Confidence.Value,
            annotation.SourceRegionId,
            annotation.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(annotation.SourcePrimitiveIds, sourceLookup),
            annotation.Evidence,
            annotation.Items.Select(item => AnnotationItemExport.From(item, sourceLookup)).ToArray());
}

public sealed record AnnotationItemExport(
    string Id,
    int PageNumber,
    string Kind,
    string Text,
    string? Marker,
    RectExport Bounds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<AnnotationReferenceExport> References,
    IReadOnlyList<string> Evidence)
{
    public static AnnotationItemExport From(
        PlanAnnotationItem item,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            item.Id,
            item.PageNumber,
            item.Kind.ToString(),
            item.Text,
            item.Marker,
            RectExport.From(item.Bounds),
            item.Confidence.Value,
            item.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(item.SourcePrimitiveIds, sourceLookup),
            item.References.Select(reference => AnnotationReferenceExport.From(reference, sourceLookup)).ToArray(),
            item.Evidence);
}

public sealed record AnnotationReferenceExport(
    string Id,
    string Marker,
    string Text,
    RectExport Bounds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static AnnotationReferenceExport From(
        PlanAnnotationReference reference,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            reference.Id,
            reference.Marker,
            reference.Text,
            RectExport.From(reference.Bounds),
            reference.Confidence.Value,
            reference.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(reference.SourcePrimitiveIds, sourceLookup),
            reference.Evidence);
}

public sealed record MeasurementConsistencyExport(
    bool HasReliableCalibration,
    double? SelectedMillimetersPerDrawingUnit,
    double? MedianDimensionMillimetersPerDrawingUnit,
    double? DimensionScaleSpreadRatio,
    double Confidence,
    int CheckedCount,
    int ConsistentCount,
    int OutlierCount,
    double OutlierRatio,
    bool HasBlockingOutliers,
    bool HasTolerableOutliers,
    int NonBlockingOutlierCountMaximum,
    double NonBlockingOutlierRatioMaximum,
    double BlockingScaleSpreadRatioThreshold,
    string MetricImportImpact,
    IReadOnlyList<MeasurementConsistencyCheckExport> Checks)
{
    public static MeasurementConsistencyExport From(
        MeasurementConsistencyReport report,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            report.HasReliableCalibration,
            report.SelectedMillimetersPerDrawingUnit,
            report.MedianDimensionMillimetersPerDrawingUnit,
            report.DimensionScaleSpreadRatio,
            report.Confidence.Value,
            report.CheckedCount,
            report.ConsistentCount,
            report.OutlierCount,
            report.OutlierRatio,
            report.HasBlockingOutliers,
            report.HasTolerableOutliers,
            MeasurementConsistencyReport.NonBlockingOutlierCountMaximum,
            MeasurementConsistencyReport.NonBlockingOutlierRatioMaximum,
            MeasurementConsistencyReport.BlockingScaleSpreadRatioThreshold,
            CalculateMetricImportImpact(report),
            report.Checks.Select(check => MeasurementConsistencyCheckExport.From(check, sourceLookup)).ToArray());

    private static string CalculateMetricImportImpact(MeasurementConsistencyReport report) =>
        report.HasBlockingOutliers
            ? "Blocking"
            : report.HasTolerableOutliers
                ? "ReviewOnly"
                : "None";
}

public sealed record MeasurementConsistencyCheckExport(
    string DimensionId,
    int PageNumber,
    string Status,
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
    public static MeasurementConsistencyCheckExport From(
        MeasurementConsistencyCheck check,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            check.DimensionId,
            check.PageNumber,
            check.Status.ToString(),
            check.DimensionMillimeters,
            check.DrawingLength,
            check.ImpliedMillimetersPerDrawingUnit,
            check.SelectedMillimetersPerDrawingUnit,
            check.ExpectedMillimeters,
            check.DeltaMillimeters,
            check.RelativeError,
            check.Confidence.Value,
            check.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(check.SourcePrimitiveIds, sourceLookup),
            check.Evidence);
}

public sealed record DimensionExport(
    string Id,
    int PageNumber,
    string Kind,
    string Orientation,
    string Text,
    string NormalizedText,
    RectExport Bounds,
    string Unit,
    double MeasuredMillimeters,
    LineExport? DimensionLine,
    double? DrawingLength,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static DimensionExport From(
        DimensionAnnotation dimension,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            dimension.Id,
            dimension.PageNumber,
            dimension.Kind.ToString(),
            dimension.Orientation.ToString(),
            dimension.Text,
            dimension.NormalizedText,
            RectExport.From(dimension.Bounds),
            dimension.Unit.ToString(),
            dimension.MeasuredMillimeters,
            dimension.DimensionLine is null ? null : LineExport.From(dimension.DimensionLine.Value),
            dimension.DrawingLength,
            dimension.MillimetersPerDrawingUnit,
            dimension.Confidence.Value,
            dimension.SourceRegionId,
            dimension.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(dimension.SourcePrimitiveIds, sourceLookup),
            dimension.Evidence);
}

public sealed record TitleBlockExport(
    string RegionId,
    int PageNumber,
    RectExport Bounds,
    double Confidence,
    string? ProjectName,
    string? SheetNumber,
    string? SheetTitle,
    string? Revision,
    string? IssueDate,
    string? Scale,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<TitleBlockFieldExport> Fields)
{
    public static TitleBlockExport From(
        TitleBlockAnalysis titleBlock,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            titleBlock.RegionId,
            titleBlock.PageNumber,
            RectExport.From(titleBlock.Bounds),
            titleBlock.Confidence.Value,
            titleBlock.ProjectName,
            titleBlock.SheetNumber,
            titleBlock.SheetTitle,
            titleBlock.Revision,
            titleBlock.IssueDate,
            titleBlock.Scale,
            titleBlock.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(titleBlock.SourcePrimitiveIds, sourceLookup),
            titleBlock.Fields.Select(field => TitleBlockFieldExport.From(field, sourceLookup)).ToArray());
}

public sealed record TitleBlockFieldExport(
    string Kind,
    string Value,
    string RawText,
    int PageNumber,
    RectExport Bounds,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static TitleBlockFieldExport From(
        TitleBlockField field,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            field.Kind.ToString(),
            field.Value,
            field.RawText,
            field.PageNumber,
            RectExport.From(field.Bounds),
            field.Confidence.Value,
            field.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(field.SourcePrimitiveIds, sourceLookup),
            field.Evidence);
}

public sealed record CalibrationExport(
    string DrawingUnit,
    string RealWorldUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    bool HasReliableMeasurementScale,
    IReadOnlyList<CalibrationScaleGroupExport> ScaleGroups,
    IReadOnlyList<CalibrationEvidenceExport> Evidence)
{
    public static CalibrationExport From(PlanCalibration calibration) =>
        new(
            calibration.DrawingUnit.ToString(),
            calibration.RealWorldUnit.ToString(),
            calibration.ScaleRatio,
            calibration.MillimetersPerDrawingUnit,
            calibration.Confidence.Value,
            calibration.HasReliableMeasurementScale,
            calibration.ScaleGroups.Select(CalibrationScaleGroupExport.From).ToArray(),
            calibration.Evidence.Select(CalibrationEvidenceExport.From).ToArray());
}

public sealed record CalibrationScaleGroupExport(
    string Id,
    int? PageNumber,
    string Scope,
    string DrawingUnit,
    string EvidenceUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    int EvidenceCount,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceRegionIds,
    RectExport? Bounds,
    IReadOnlyList<string> Evidence)
{
    public static CalibrationScaleGroupExport From(CalibrationScaleGroup group) =>
        new(
            group.Id,
            group.PageNumber,
            group.Scope.ToString(),
            group.DrawingUnit.ToString(),
            group.RealWorldUnit.ToString(),
            group.ScaleRatio,
            group.MillimetersPerDrawingUnit,
            group.EvidenceCount,
            group.Confidence.Value,
            group.SourcePrimitiveIds,
            group.SourceRegionIds,
            group.Bounds is null ? null : RectExport.From(group.Bounds.Value),
            group.Evidence);
}

public sealed record CalibrationEvidenceExport(
    string Kind,
    int? PageNumber,
    string? SourcePrimitiveId,
    string? Text,
    string Unit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    double Confidence,
    string Description)
{
    public static CalibrationEvidenceExport From(CalibrationEvidence evidence) =>
        new(
            evidence.Kind.ToString(),
            evidence.PageNumber,
            evidence.SourcePrimitiveId,
            evidence.Text,
            evidence.Unit.ToString(),
            evidence.ScaleRatio,
            evidence.MillimetersPerDrawingUnit,
            evidence.Confidence.Value,
            evidence.Description);
}

public sealed record LayerAnalysisExport(IReadOnlyList<LayerSummaryExport> Layers)
{
    public static LayerAnalysisExport From(PlanLayerAnalysis analysis) =>
        new(analysis.Layers.Select(LayerSummaryExport.From).ToArray());
}

public sealed record LayerSummaryExport(
    string Name,
    string? SourceFormat,
    int EntityCount,
    IReadOnlyDictionary<string, int> PrimitiveKindCounts,
    double TotalLineLength,
    RectExport Bounds,
    string LikelyCategory,
    double Confidence,
    IReadOnlyList<LayerCategoryScoreExport> CategoryScores,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<int> PageNumbers)
{
    public static LayerSummaryExport From(LayerSummary summary) =>
        new(
            summary.Name,
            summary.SourceFormat,
            summary.EntityCount,
            summary.PrimitiveKindCounts.ToDictionary(item => item.Key.ToString(), item => item.Value),
            summary.TotalLineLength,
            RectExport.From(summary.Bounds),
            summary.LikelyCategory.ToString(),
            summary.Confidence.Value,
            summary.CategoryScores.Select(LayerCategoryScoreExport.From).ToArray(),
            summary.Evidence,
            summary.PageNumbers);
}

public sealed record LayerCategoryScoreExport(
    string Category,
    double Score,
    IReadOnlyList<string> Evidence)
{
    public static LayerCategoryScoreExport From(LayerCategoryScore score) =>
        new(
            score.Category.ToString(),
            score.Score,
            score.Evidence);
}

public sealed record DocumentExport(
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
    bool IsRasterDerived,
    string IngestionPath,
    SourceReadinessExport SourceReadiness,
    IReadOnlyDictionary<string, string> Properties)
{
    public static DocumentExport From(PlanDocument document)
    {
        var readiness = PlanSourceReadiness.From(document.Metadata.Properties);
        return new(
            document.Id,
            document.Metadata.SourceName,
            document.Metadata.SourcePath,
            readiness.SourceFormat,
            readiness.Loader,
            readiness.SourceKind,
            readiness.EffectiveSourceKind,
            ReadProperty(document.Metadata.Properties, "clipboardContentKind"),
            ReadProperty(document.Metadata.Properties, "fileExtension"),
            ReadProperty(document.Metadata.Properties, "contentType"),
            readiness.IsDwgDerived,
            readiness.IsRasterDerived,
            readiness.IngestionPath,
            SourceReadinessExport.From(readiness),
            document.Metadata.Properties
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

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

    private static bool ComputeIsRasterDerived(IReadOnlyDictionary<string, string> properties) =>
        string.Equals(ReadProperty(properties, "format"), "raster", StringComparison.OrdinalIgnoreCase)
        || string.Equals(ReadProperty(properties, "sourceKind"), PlanSourceKind.RasterImage.ToString(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(ReadProperty(properties, "effectiveSourceKind"), PlanSourceKind.RasterImage.ToString(), StringComparison.OrdinalIgnoreCase)
        || ReadProperty(properties, "raster.adapter") is not null
        || ReadProperty(properties, "raster.extractor") is not null
        || ReadProperty(properties, "extractorName") is not null;

    private static string ComputeIngestionPath(IReadOnlyDictionary<string, string> properties)
    {
        if (ReadProperty(properties, "dwg.conversion") is not null)
        {
            return "dwg-to-dxf";
        }

        if (ComputeIsDwgDerived(properties))
        {
            return "dwg-adapter";
        }

        if (ComputeIsRasterDerived(properties))
        {
            return "raster-extraction";
        }

        return ReadProperty(properties, "format")?.Trim().ToLowerInvariant() switch
        {
            "pdf" => "pdf-vector",
            "dxf" => "dxf-vector",
            "extracted-primitives" => "extracted-primitives",
            "vector" or "vector-image" => "vector-image",
            _ => "unknown"
        };
    }

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

public sealed record SourceReadinessExport(
    string Status,
    string GeometryBasis,
    bool CanUseVectorGeometry,
    bool RequiresExternalAdapter,
    bool RequiresOcr,
    bool IsLegalAdapterBacked,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> Evidence)
{
    public static SourceReadinessExport From(IReadOnlyDictionary<string, string> properties) =>
        From(PlanSourceReadiness.From(properties));

    public static SourceReadinessExport From(PlanSourceReadiness readiness) =>
        new(
            readiness.Status,
            readiness.GeometryBasis,
            readiness.CanUseVectorGeometry,
            readiness.RequiresExternalAdapter,
            readiness.RequiresOcr,
            readiness.IsLegalAdapterBacked,
            readiness.Messages,
            readiness.Evidence);
}

public sealed record PageExport(
    int Number,
    double Width,
    double Height,
    int PrimitiveCount)
{
    public static PageExport From(PlanPage page) =>
        new(page.Number, page.Size.Width, page.Size.Height, page.Primitives.Count);
}

public sealed record CoordinateSystemExport(
    string CoordinateSpace,
    string Unit,
    string Origin,
    string XAxisDirection,
    string YAxisDirection,
    string GeometryBasis,
    string CoordinateOrder,
    string BoundsKind,
    string Precision,
    string RealWorldUnit,
    double? MillimetersPerDrawingUnit,
    string Note,
    IReadOnlyList<PageCoordinateFrameExport> PageFrames)
{
    public static CoordinateSystemExport From(
        IReadOnlyList<PlanPage> pages,
        PlanCalibration calibration) =>
        new(
            "OpenPlanTracePageCoordinates",
            "drawing-unit",
            "TopLeft",
            "Right",
            "Down",
            "PDF/DXF page coordinate space after OpenPlanTrace normalization",
            "x,y",
            "x,y,width,height for rectangles; start/end for lines; ordered point arrays for polygons",
            "double",
            calibration.RealWorldUnit.ToString(),
            calibration.MillimetersPerDrawingUnit,
            "Coordinates are page-local drawing units, not screen pixels and not WGS84. Use pageFrames to map to normalized page bounds; use calibration or per-detection measurementScaleGroupId for real-world dimensions.",
            pages.Select(PageCoordinateFrameExport.From).ToArray());
}

public sealed record PageCoordinateFrameExport(
    int PageNumber,
    double Width,
    double Height,
    RectExport Bounds,
    IReadOnlyList<double> PageToNormalizedTransform,
    IReadOnlyList<double> NormalizedToPageTransform)
{
    public static PageCoordinateFrameExport From(PlanPage page)
    {
        var width = page.Size.Width;
        var height = page.Size.Height;
        return new PageCoordinateFrameExport(
            page.Number,
            width,
            height,
            new RectExport(0, 0, width, height),
            new[] { SafeInverse(width), 0d, 0d, SafeInverse(height), 0d, 0d },
            new[] { width, 0d, 0d, height, 0d, 0d });
    }

    private static double SafeInverse(double value) =>
        Math.Abs(value) < double.Epsilon ? 0d : 1d / value;
}

public sealed record PrimitiveSourceExport(
    int PageNumber,
    string PrimitiveKind,
    string SourceId,
    RectExport Bounds,
    SourceMetadataExport Metadata)
{
    public static IEnumerable<PrimitiveSourceExport> From(PlanDocument document)
    {
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var sourceId = PrimitiveId(page.Number, index, primitive);
                yield return new PrimitiveSourceExport(
                    page.Number,
                    primitive.Kind.ToString(),
                    sourceId,
                    RectExport.From(primitive.Bounds),
                    SourceMetadataExport.From(primitive, sourceId));
            }
        }
    }

    private static string PrimitiveId(int pageNumber, int primitiveIndex, PlanPrimitive primitive) =>
        primitive.SourceId ?? primitive.Source.SourceId ?? $"p{pageNumber}:primitive:{primitiveIndex}";
}

public sealed record SourceMetadataExport(
    string? SourceFormat,
    string? SourceDocumentId,
    string? SourceName,
    string? SourcePath,
    string? SourceId,
    string? EntityType,
    string? Layer,
    string? Color,
    string? LineType,
    double? LineWeight,
    string DrawingSpace,
    string? BlockName,
    IReadOnlyDictionary<string, string> Properties)
{
    public static SourceMetadataExport From(PlanPrimitive primitive, string sourceId)
    {
        var source = primitive.Source;

        return new SourceMetadataExport(
            source.SourceFormat,
            source.SourceDocumentId,
            source.SourceName,
            source.SourcePath,
            source.SourceId ?? primitive.SourceId ?? sourceId,
            source.EntityType,
            source.Layer ?? primitive.Layer,
            source.Color,
            source.LineType,
            source.LineWeight ?? (primitive.StrokeWidth > 0 ? primitive.StrokeWidth : null),
            source.DrawingSpace.ToString(),
            source.BlockName,
            source.Properties);
    }
}

public sealed record RegionExport(
    string Id,
    int PageNumber,
    string Kind,
    RectExport Bounds,
    double Confidence,
    string? Label,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers)
{
    public static RegionExport From(
        SheetRegion region,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            region.Id,
            region.PageNumber,
            region.Kind.ToString(),
            RectExport.From(region.Bounds),
            region.Confidence.Value,
            region.Label,
            region.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(region.SourcePrimitiveIds, sourceLookup));
}

public sealed record SurfacePatternExport(
    string Id,
    int PageNumber,
    string Kind,
    string Orientation,
    RectExport Bounds,
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
    public static SurfacePatternExport From(
        SurfacePatternCandidate pattern,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            pattern.Id,
            pattern.PageNumber,
            pattern.Kind.ToString(),
            pattern.Orientation.ToString(),
            RectExport.From(pattern.Bounds),
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
            ExportSourceHelpers.SourceLayers(pattern.SourcePrimitiveIds, sourceLookup),
            pattern.Evidence);
}

public sealed record WallExport(
    string Id,
    int PageNumber,
    LineExport CenterLine,
    IReadOnlyList<WallTopologySpanExport> TopologySpans,
    RectExport Bounds,
    double Thickness,
    string DetectionKind,
    string WallType,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    WallPairEvidenceExport? PairEvidence,
    WallFragmentEvidenceExport? FragmentEvidence,
    WallEvidenceAssessmentExport? EvidenceAssessment,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static WallExport From(
        WallSegment wall,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup,
        IReadOnlyDictionary<string, WallGraphComponent> wallComponentLookup,
        WallEvidenceWallAssessment? evidenceAssessment,
        IReadOnlyList<WallGraphTopologySpan> topologySpans)
    {
        wallComponentLookup.TryGetValue(wall.Id, out var component);
        return
        new(
            wall.Id,
            wall.PageNumber,
            LineExport.From(wall.CenterLine),
            topologySpans.Select(span => WallTopologySpanExport.From(span, sourceLookup)).ToArray(),
            RectExport.From(wall.Bounds),
            wall.Thickness,
            wall.DetectionKind.ToString(),
            wall.WallType.ToString(),
            component?.Id,
            component?.Kind.ToString(),
            WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment),
            wall.DrawingLength,
            wall.LengthMeters,
            wall.ThicknessMillimeters,
            wall.MeasurementScaleGroupId,
            wall.Confidence.Value,
            wall.SourceRegionId,
            wall.SourcePrimitiveIds,
            wall.PairEvidence is null ? null : WallPairEvidenceExport.From(wall.PairEvidence),
            wall.FragmentEvidence is null ? null : WallFragmentEvidenceExport.From(wall.FragmentEvidence),
            evidenceAssessment is null ? null : WallEvidenceAssessmentExport.From(evidenceAssessment),
            wall.Evidence,
            ExportSourceHelpers.SourceLayers(wall.SourcePrimitiveIds, sourceLookup));
    }
}

public sealed record WallEvidenceAssessmentExport(
    string Category,
    double Confidence,
    bool PlacementReady,
    bool RequiresReview,
    bool RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence)
{
    public static WallEvidenceAssessmentExport From(WallEvidenceWallAssessment assessment) =>
        new(
            assessment.Category.ToString(),
            assessment.Confidence.Value,
            assessment.PlacementReady,
            assessment.RequiresReview,
            assessment.RejectedAsNoise,
            assessment.SourcePrimitiveIds,
            assessment.Evidence);
}

public sealed record WallEvidenceScoreBreakdownExport(
    double PositiveScore,
    double NegativeScore,
    double DecisionScore,
    double PairSupportScore,
    double LayerSupportScore,
    double StructuralSupportScore,
    double RecoverySupportScore,
    double NoisePenalty,
    double FragmentReviewPenalty,
    IReadOnlyList<string> PositiveEvidence,
    IReadOnlyList<string> NegativeEvidence)
{
    public static WallEvidenceScoreBreakdownExport From(WallEvidenceScoreBreakdown score) =>
        new(
            score.PositiveScore,
            score.NegativeScore,
            score.DecisionScore,
            score.PairSupportScore,
            score.LayerSupportScore,
            score.StructuralSupportScore,
            score.RecoverySupportScore,
            score.NoisePenalty,
            score.FragmentReviewPenalty,
            score.PositiveEvidence,
            score.NegativeEvidence);
}

public sealed record WallTopologyPreparationExport(
    int GraphWallCount,
    int AcceptedGraphWallCount,
    int ReviewGraphWallCount,
    int UnassessedGraphWallCount,
    int AutomaticCoordinateRepairWallCount,
    int RejectedWallCount,
    int RejectedAssessmentCount,
    int DoorOrOpeningSymbolCount,
    int SurfacePatternDetailCount,
    int DimensionOrAnnotationCount,
    int ObjectOrFixtureDetailCount,
    IReadOnlyList<string> GraphWallIds,
    IReadOnlyList<string> AcceptedGraphWallIds,
    IReadOnlyList<string> ReviewGraphWallIds,
    IReadOnlyList<string> UnassessedGraphWallIds,
    IReadOnlyList<string> AutomaticCoordinateRepairWallIds,
    IReadOnlyList<string> RejectedWallIds,
    IReadOnlyList<WallTopologyRejectedWallExport> RejectedWalls,
    IReadOnlyList<string> Evidence)
{
    public static WallTopologyPreparationExport From(
        WallTopologyPreparation preparation,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            preparation.GraphWallCount,
            preparation.AcceptedGraphWallCount,
            preparation.ReviewGraphWallCount,
            preparation.UnassessedGraphWallCount,
            preparation.AutomaticCoordinateRepairWallCount,
            preparation.RejectedWallCount,
            preparation.RejectedWalls.Count,
            preparation.DoorOrOpeningSymbolCount,
            preparation.SurfacePatternDetailCount,
            preparation.DimensionOrAnnotationCount,
            preparation.ObjectOrFixtureDetailCount,
            preparation.GraphWallIds,
            preparation.AcceptedGraphWallIds,
            preparation.ReviewGraphWallIds,
            preparation.UnassessedGraphWallIds,
            preparation.AutomaticCoordinateRepairWallIds,
            preparation.RejectedWallIds,
            preparation.RejectedWalls.Select(wall => WallTopologyRejectedWallExport.From(wall, sourceLookup)).ToArray(),
            BuildEvidence(preparation));

    private static IReadOnlyList<string> BuildEvidence(WallTopologyPreparation preparation)
    {
        var evidence = new List<string>
        {
            $"graph wall input: {preparation.GraphWallCount}",
            $"accepted graph walls: {preparation.AcceptedGraphWallCount}",
            $"review-required graph walls: {preparation.ReviewGraphWallCount}",
            $"unassessed graph walls: {preparation.UnassessedGraphWallCount}",
            $"automatic coordinate repair allowed walls: {preparation.AutomaticCoordinateRepairWallCount}"
        };

        if (preparation.RejectedWallCount > 0)
        {
            evidence.Add($"rejected wall-like detail walls excluded from graph: {preparation.RejectedWallCount}");
        }

        return evidence;
    }
}

public sealed record WallTopologyRejectedWallExport(
    string WallId,
    int PageNumber,
    RectExport Bounds,
    string Category,
    string Decision,
    bool RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallTopologyRejectedWallExport From(
        WallTopologyRejectedWall wall,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            wall.WallId,
            wall.PageNumber,
            RectExport.From(wall.Bounds),
            wall.Category.ToString(),
            wall.Decision.ToString(),
            wall.RejectedAsNoise,
            wall.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(wall.SourcePrimitiveIds, sourceLookup),
            wall.Evidence);
}

public sealed record WallEvidenceExport(
    int SegmentCount,
    int BandCount,
    int WallAssessmentCount,
    int SourceCandidateWallCount,
    int RecoveredCandidateWallCount,
    int TotalCandidateWallCount,
    int StrongWallBodyCount,
    int MediumWallBodyCount,
    int WeakSingleLineCount,
    int RecoveredWallBodyCount,
    int RejectedNoiseCount,
    int RejectedDoorOrOpeningSymbolCount,
    int RejectedSurfacePatternDetailCount,
    int RejectedDimensionOrAnnotationCount,
    int RejectedObjectOrFixtureDetailCount,
    IReadOnlyList<string> RejectedDoorOrOpeningSymbolWallIds,
    IReadOnlyList<string> RejectedSurfacePatternDetailWallIds,
    IReadOnlyList<string> RejectedDimensionOrAnnotationWallIds,
    IReadOnlyList<string> RejectedObjectOrFixtureDetailWallIds,
    int AcceptedWallCount,
    int ReviewDecisionWallCount,
    int RejectedWallCount,
    int PlacementReadyWallCount,
    int ReviewWallCount,
    IReadOnlyList<string> AcceptedWallIds,
    IReadOnlyList<string> ReviewWallIds,
    IReadOnlyList<string> RejectedWallIds,
    IReadOnlyList<WallEvidenceSegmentExport> Segments,
    IReadOnlyList<WallEvidenceBandExport> Bands,
    IReadOnlyList<WallEvidenceAssessmentDetailExport> WallAssessments,
    IReadOnlyList<RejectedWallLikeDetailExport> RejectedWallLikeDetails)
{
    public static WallEvidenceExport From(
        WallEvidenceMap evidenceMap,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var assessmentsByWallId = WallEvidenceExportHelpers.BuildAssessmentLookup(evidenceMap);
        var segmentsByWallId = evidenceMap.Segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.WallId))
            .GroupBy(segment => segment.WallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var bandsByWallId = evidenceMap.Bands
            .Where(band => !string.IsNullOrWhiteSpace(band.WallId))
            .GroupBy(band => band.WallId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        return new WallEvidenceExport(
            evidenceMap.Segments.Count,
            evidenceMap.Bands.Count,
            evidenceMap.WallAssessments.Count,
            evidenceMap.SourceCandidateWallCount,
            evidenceMap.RecoveredCandidateWallCount,
            evidenceMap.TotalCandidateWallCount,
            evidenceMap.StrongWallBodyCount,
            evidenceMap.MediumWallBodyCount,
            evidenceMap.WeakSingleLineCount,
            evidenceMap.RecoveredWallBodyCount,
            evidenceMap.RejectedNoiseCount,
            evidenceMap.RejectedDoorOrOpeningSymbolCount,
            evidenceMap.RejectedSurfacePatternDetailCount,
            evidenceMap.RejectedDimensionOrAnnotationCount,
            evidenceMap.RejectedObjectOrFixtureDetailCount,
            evidenceMap.RejectedDoorOrOpeningSymbolWallIds,
            evidenceMap.RejectedSurfacePatternDetailWallIds,
            evidenceMap.RejectedDimensionOrAnnotationWallIds,
            evidenceMap.RejectedObjectOrFixtureDetailWallIds,
            evidenceMap.AcceptedWallCount,
            evidenceMap.ReviewDecisionWallCount,
            evidenceMap.RejectedWallCount,
            evidenceMap.PlacementReadyWallCount,
            evidenceMap.ReviewWallCount,
            evidenceMap.AcceptedWallIds,
            evidenceMap.ReviewWallIds,
            evidenceMap.RejectedWallIds,
            evidenceMap.Segments
                .Select(segment => WallEvidenceSegmentExport.From(segment, assessmentsByWallId, sourceLookup))
                .ToArray(),
            evidenceMap.Bands
                .Select(band => WallEvidenceBandExport.From(band, assessmentsByWallId, sourceLookup))
                .ToArray(),
            evidenceMap.WallAssessments
                .Select(assessment => WallEvidenceAssessmentDetailExport.From(assessment, sourceLookup))
                .ToArray(),
            evidenceMap.WallAssessments
                .Where(assessment => assessment.RejectedAsNoise)
                .Select(assessment => RejectedWallLikeDetailExport.From(
                    assessment,
                    segmentsByWallId.TryGetValue(assessment.WallId, out var segments) ? segments : Array.Empty<WallEvidenceSegment>(),
                    bandsByWallId.TryGetValue(assessment.WallId, out var bands) ? bands : Array.Empty<WallEvidenceBand>(),
                    sourceLookup))
                .ToArray());
    }
}

public sealed record WallEvidenceSegmentExport(
    string Id,
    int PageNumber,
    LineExport Line,
    RectExport Bounds,
    string Category,
    double Confidence,
    string? WallId,
    string? Decision,
    WallEvidenceScoreBreakdownExport? ScoreBreakdown,
    bool? PlacementReady,
    bool? RequiresReview,
    bool? RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallEvidenceSegmentExport From(
        WallEvidenceSegment segment,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> assessmentsByWallId,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var assessment = segment.WallId is not null && assessmentsByWallId.TryGetValue(segment.WallId, out var found)
            ? found
            : null;
        return new WallEvidenceSegmentExport(
            segment.Id,
            segment.PageNumber,
            LineExport.From(segment.Line),
            RectExport.From(segment.Bounds),
            segment.Category.ToString(),
            segment.Confidence.Value,
            segment.WallId,
            assessment?.Decision.ToString(),
            assessment is null ? null : WallEvidenceScoreBreakdownExport.From(assessment.ScoreBreakdown),
            assessment?.PlacementReady,
            assessment?.RequiresReview,
            assessment?.RejectedAsNoise,
            segment.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(segment.SourcePrimitiveIds, sourceLookup),
            segment.Evidence);
    }
}

public sealed record WallEvidenceBandExport(
    string Id,
    int PageNumber,
    LineExport FirstFaceLine,
    LineExport SecondFaceLine,
    LineExport CenterLine,
    double FaceSeparation,
    double OverlapRatio,
    double Confidence,
    string? WallId,
    string? Decision,
    WallEvidenceScoreBreakdownExport? ScoreBreakdown,
    bool? PlacementReady,
    bool? RequiresReview,
    bool? RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallEvidenceBandExport From(
        WallEvidenceBand band,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> assessmentsByWallId,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var assessment = band.WallId is not null && assessmentsByWallId.TryGetValue(band.WallId, out var found)
            ? found
            : null;
        return new WallEvidenceBandExport(
            band.Id,
            band.PageNumber,
            LineExport.From(band.FirstFaceLine),
            LineExport.From(band.SecondFaceLine),
            LineExport.From(band.CenterLine),
            band.FaceSeparation,
            band.OverlapRatio,
            band.Confidence.Value,
            band.WallId,
            assessment?.Decision.ToString(),
            assessment is null ? null : WallEvidenceScoreBreakdownExport.From(assessment.ScoreBreakdown),
            assessment?.PlacementReady,
            assessment?.RequiresReview,
            assessment?.RejectedAsNoise,
            band.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(band.SourcePrimitiveIds, sourceLookup),
            band.Evidence);
    }
}

public sealed record WallEvidenceAssessmentDetailExport(
    string WallId,
    int PageNumber,
    RectExport Bounds,
    string Category,
    double Confidence,
    string Decision,
    WallEvidenceScoreBreakdownExport ScoreBreakdown,
    bool PlacementReady,
    bool RequiresReview,
    bool RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallEvidenceAssessmentDetailExport From(
        WallEvidenceWallAssessment assessment,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            assessment.WallId,
            assessment.PageNumber,
            RectExport.From(assessment.Bounds),
            assessment.Category.ToString(),
            assessment.Confidence.Value,
            assessment.Decision.ToString(),
            WallEvidenceScoreBreakdownExport.From(assessment.ScoreBreakdown),
            assessment.PlacementReady,
            assessment.RequiresReview,
            assessment.RejectedAsNoise,
            assessment.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(assessment.SourcePrimitiveIds, sourceLookup),
            assessment.Evidence);
}

public sealed record RejectedWallLikeDetailExport(
    string Id,
    string WallId,
    int PageNumber,
    RectExport Bounds,
    LineExport? CenterLine,
    string Category,
    double Confidence,
    string Decision,
    WallEvidenceScoreBreakdownExport ScoreBreakdown,
    IReadOnlyList<string> SegmentIds,
    IReadOnlyList<string> BandIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RejectedWallLikeDetailExport From(
        WallEvidenceWallAssessment assessment,
        IReadOnlyList<WallEvidenceSegment> segments,
        IReadOnlyList<WallEvidenceBand> bands,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var primarySegment = segments
            .OrderByDescending(segment => segment.Line.Length)
            .FirstOrDefault();
        return new RejectedWallLikeDetailExport(
            $"rejected-wall-like-detail:{assessment.WallId}",
            assessment.WallId,
            assessment.PageNumber,
            RectExport.From(assessment.Bounds),
            primarySegment is null ? null : LineExport.From(primarySegment.Line),
            assessment.Category.ToString(),
            assessment.Confidence.Value,
            assessment.Decision.ToString(),
            WallEvidenceScoreBreakdownExport.From(assessment.ScoreBreakdown),
            segments.Select(segment => segment.Id).Distinct(StringComparer.Ordinal).ToArray(),
            bands.Select(band => band.Id).Distinct(StringComparer.Ordinal).ToArray(),
            assessment.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(assessment.SourcePrimitiveIds, sourceLookup),
            assessment.Evidence);
    }
}

internal static class WallEvidenceExportHelpers
{
    public static IReadOnlyDictionary<string, WallEvidenceWallAssessment> BuildAssessmentLookup(
        WallEvidenceMap evidenceMap) =>
        evidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(assessment => assessment.Confidence.Value)
                    .First(),
                StringComparer.Ordinal);

    public static bool IsExcludedFromStructuralTopology(
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment) =>
        WallStructuralTrust.IsExcludedFromStructuralTopology(component, evidenceAssessment);
}

public sealed record WallTopologySpanExport(
    string Id,
    string WallGraphEdgeId,
    int PageNumber,
    string WallId,
    string FromNodeId,
    string ToNodeId,
    LineExport CenterLine,
    RectExport Bounds,
    double DrawingLength,
    double? SourceWallStartOffsetDrawingUnits,
    double? SourceWallEndOffsetDrawingUnits,
    double? SourceWallProjectedLengthDrawingUnits,
    double? SourceWallStartParameter,
    double? SourceWallEndParameter,
    double? SourceWallCenterParameter,
    double? SourceWallStartProjectionDistanceDrawingUnits,
    double? SourceWallEndProjectionDistanceDrawingUnits,
    double Thickness,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallTopologySpanExport From(
        WallGraphTopologySpan span,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            span.Id,
            span.Id,
            span.PageNumber,
            span.WallId,
            span.FromNodeId,
            span.ToNodeId,
            LineExport.From(span.CenterLine),
            RectExport.From(span.Bounds),
            span.DrawingLength,
            span.SourceWallStartOffsetDrawingUnits,
            span.SourceWallEndOffsetDrawingUnits,
            span.SourceWallProjectedLengthDrawingUnits,
            span.SourceWallStartParameter,
            span.SourceWallEndParameter,
            span.SourceWallCenterParameter,
            span.SourceWallStartProjectionDistanceDrawingUnits,
            span.SourceWallEndProjectionDistanceDrawingUnits,
            span.Thickness,
            span.Confidence.Value,
            span.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(span.SourcePrimitiveIds, sourceLookup),
            span.Evidence);
}

public sealed record WallPairEvidenceExport(
    LineExport FirstFaceLine,
    LineExport SecondFaceLine,
    double FaceSeparation,
    double OverlapRatio,
    double Score,
    int FirstFaceFragmentCount,
    int SecondFaceFragmentCount,
    IReadOnlyList<string> FirstFaceSourcePrimitiveIds,
    IReadOnlyList<string> SecondFaceSourcePrimitiveIds)
{
    public static WallPairEvidenceExport From(WallPairEvidence evidence) =>
        new(
            LineExport.From(evidence.FirstFaceLine),
            LineExport.From(evidence.SecondFaceLine),
            evidence.FaceSeparation,
            evidence.OverlapRatio,
            evidence.Score,
            evidence.FirstFaceFragmentCount,
            evidence.SecondFaceFragmentCount,
            evidence.FirstFaceSourcePrimitiveIds,
            evidence.SecondFaceSourcePrimitiveIds);
}

public sealed record WallFragmentEvidenceExport(
    int FragmentCount,
    double TotalHealedGap,
    double MaxHealedGap,
    int DuplicatePrimitiveCount,
    double GapRatio,
    bool RequiresGeometryReview,
    IReadOnlyList<string> Evidence)
{
    public static WallFragmentEvidenceExport From(WallFragmentEvidence evidence) =>
        new(
            evidence.FragmentCount,
            evidence.TotalHealedGap,
            evidence.MaxHealedGap,
            evidence.DuplicatePrimitiveCount,
            evidence.GapRatio,
            evidence.RequiresGeometryReview,
            evidence.Evidence);
}

public sealed record WallGraphExport(
    IReadOnlyList<WallNodeExport> Nodes,
    IReadOnlyList<WallEdgeExport> Edges,
    IReadOnlyList<WallGraphComponentExport> Components,
    IReadOnlyList<WallGraphRepairCandidateExport> RepairCandidates)
{
    public static WallGraphExport From(
        WallGraph graph,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            graph.Nodes.Select(WallNodeExport.From).ToArray(),
            graph.Edges.Select(edge => WallEdgeExport.From(
                edge,
                topologySpans.FirstOrDefault(span => string.Equals(span.Id, edge.Id, StringComparison.Ordinal)),
                sourceLookup)).ToArray(),
            graph.Components.Select(component => WallGraphComponentExport.From(component, sourceLookup)).ToArray(),
            graph.RepairCandidates.Select(candidate => WallGraphRepairCandidateExport.From(candidate, sourceLookup)).ToArray());
}

public sealed record WallGraphComponentExport(
    string Id,
    int PageNumber,
    string Kind,
    RectExport Bounds,
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
    public static WallGraphComponentExport From(
        WallGraphComponent component,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            component.Id,
            component.PageNumber,
            component.Kind.ToString(),
            RectExport.From(component.Bounds),
            component.WallIds,
            component.NodeIds,
            component.EdgeIds,
            component.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(component.SourcePrimitiveIds, sourceLookup),
            component.WallCount,
            component.NodeCount,
            component.EdgeCount,
            component.DrawingLength,
            component.Confidence.Value,
            component.ExcludedFromStructuralTopology,
            component.Evidence);
}

public sealed record WallNodeExport(
    string Id,
    int PageNumber,
    PointExport Position,
    string Kind,
    int Degree,
    IReadOnlyList<string> Directions,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static WallNodeExport From(WallNode node) =>
        new(
            node.Id,
            node.PageNumber,
            PointExport.From(node.Position),
            node.Kind.ToString(),
            node.Degree,
            node.Directions,
            node.Confidence.Value,
            node.Evidence);
}

public sealed record WallEdgeExport(
    string Id,
    int PageNumber,
    string FromNodeId,
    string ToNodeId,
    string WallId,
    LineExport? Line,
    RectExport? Bounds,
    double DrawingLength,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static WallEdgeExport From(
        WallEdge edge,
        WallGraphTopologySpan? topologySpan,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            edge.Id,
            edge.PageNumber,
            edge.FromNodeId,
            edge.ToNodeId,
            edge.WallId,
            topologySpan is null ? null : LineExport.From(topologySpan.CenterLine),
            topologySpan is null ? null : RectExport.From(topologySpan.Bounds),
            topologySpan?.DrawingLength ?? 0,
            edge.Confidence.Value,
            topologySpan?.SourcePrimitiveIds ?? Array.Empty<string>(),
            topologySpan is null
                ? Array.Empty<string>()
                : ExportSourceHelpers.SourceLayers(topologySpan.SourcePrimitiveIds, sourceLookup),
            topologySpan?.Evidence ?? Array.Empty<string>());
}

public sealed record WallGraphRepairCandidateExport(
    string Id,
    int PageNumber,
    string Kind,
    string SuggestedAction,
    string Severity,
    string ImportImpact,
    string Applicability,
    string SourceNodeId,
    PointExport SourcePoint,
    PointExport TargetPoint,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistance,
    double SafeSnapDistance,
    double ReviewDistanceLimit,
    double ExcessDistanceBeyondSafeSnap,
    LineExport RepairLine,
    RectExport Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    double Confidence,
    bool RequiresReview,
    IReadOnlyList<string> Evidence)
{
    public static WallGraphRepairCandidateExport From(
        WallGraphRepairCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            candidate.Id,
            candidate.PageNumber,
            candidate.Kind.ToString(),
            candidate.SuggestedAction.ToString(),
            candidate.Severity.ToString(),
            candidate.ImportImpact.ToString(),
            candidate.Applicability.ToString(),
            candidate.SourceNodeId,
            PointExport.From(candidate.SourcePoint),
            PointExport.From(candidate.TargetPoint),
            candidate.TargetNodeId,
            candidate.HostWallId,
            candidate.GapDistance,
            candidate.SafeSnapDistance,
            candidate.ReviewDistanceLimit,
            candidate.ExcessDistanceBeyondSafeSnap,
            LineExport.From(candidate.RepairLine),
            RectExport.From(candidate.Bounds),
            candidate.WallIds,
            candidate.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(candidate.SourcePrimitiveIds, sourceLookup),
            candidate.Confidence.Value,
            candidate.RequiresReview,
            candidate.Evidence);
}

public sealed record RoomExport(
    string Id,
    int PageNumber,
    RectExport Bounds,
    IReadOnlyList<PointExport> Boundary,
    IReadOnlyList<string> WallIds,
    double DrawingArea,
    double? AreaSquareMeters,
    string? MeasurementScaleGroupId,
    double Confidence,
    string? Label,
    string UseKind,
    IReadOnlyList<string> LabelSourcePrimitiveIds,
    IReadOnlyList<string> Evidence)
{
    public static RoomExport From(RoomRegion room) =>
        new(
            room.Id,
            room.PageNumber,
            RectExport.From(room.Bounds),
            room.Boundary.Select(PointExport.From).ToArray(),
            room.WallIds,
            room.DrawingArea,
            room.AreaSquareMeters,
            room.MeasurementScaleGroupId,
            room.Confidence.Value,
            room.Label,
            room.UseKind.ToString(),
            room.LabelSourcePrimitiveIds,
            room.Evidence);
}

public sealed record OpeningExport(
    string Id,
    int PageNumber,
    string Type,
    string Operation,
    string Orientation,
    LineExport CenterLine,
    RectExport Bounds,
    IReadOnlyList<string> AdjacentWallIds,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    IReadOnlyList<OpeningRoomConnectionExport> ConnectedRoomLinks,
    IReadOnlyList<string> RoomAdjacencyIds,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    OpeningPlacementExport? Placement,
    string HingeSide,
    string SwingSide,
    string SwingDirection,
    PointExport? HingePoint,
    double Confidence,
    string? WallId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static OpeningExport From(
        OpeningCandidate opening,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            opening.Id,
            opening.PageNumber,
            opening.Type.ToString(),
            opening.Operation.ToString(),
            opening.Orientation.ToString(),
            LineExport.From(opening.CenterLine),
            RectExport.From(opening.Bounds),
            opening.AdjacentWallIds,
            opening.HostWallIds,
            opening.ConnectedRoomIds,
            opening.ConnectedRoomLabels,
            opening.ConnectedRoomLinks.Select(OpeningRoomConnectionExport.From).ToArray(),
            opening.RoomAdjacencyIds,
            opening.DrawingWidth,
            opening.WidthMillimeters,
            opening.MeasurementScaleGroupId,
            opening.Placement is null ? null : OpeningPlacementExport.From(opening.Placement),
            opening.HingeSide.ToString(),
            opening.SwingSide.ToString(),
            opening.SwingDirection.ToString(),
            opening.HingePoint is null ? null : PointExport.From(opening.HingePoint.Value),
            opening.Confidence.Value,
            opening.WallId,
            opening.SourcePrimitiveIds,
            opening.Evidence,
            ExportSourceHelpers.SourceLayers(opening.SourcePrimitiveIds, sourceLookup));
}

public sealed record OpeningPlacementExport(
    string? HostWallId,
    IReadOnlyList<string> AnchorWallIds,
    LineExport ReferenceLine,
    LineExport? ReferenceLineMillimeters,
    PointExport StartPoint,
    PointExport? StartPointMillimeters,
    PointExport EndPoint,
    PointExport? EndPointMillimeters,
    double StartOffsetDrawingUnits,
    double EndOffsetDrawingUnits,
    double CenterOffsetDrawingUnits,
    double LengthDrawingUnits,
    RectExport FootprintBounds,
    RectExport? FootprintBoundsMillimeters,
    IReadOnlyList<PointExport> FootprintCorners,
    IReadOnlyList<PointExport>? FootprintCornersMillimeters,
    LineExport StartJambLine,
    LineExport? StartJambLineMillimeters,
    LineExport EndJambLine,
    LineExport? EndJambLineMillimeters,
    double DepthDrawingUnits,
    double? DepthMillimeters,
    double? StartOffsetMillimeters,
    double? EndOffsetMillimeters,
    double? CenterOffsetMillimeters,
    double? LengthMillimeters,
    double HostWallStartParameter,
    double HostWallEndParameter,
    double HostWallCenterParameter,
    VectorExport AlongVector,
    VectorExport NormalVector,
    double CrossWallOffsetDrawingUnits,
    double? CrossWallOffsetMillimeters,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static OpeningPlacementExport From(OpeningPlacement placement, double? millimetersPerDrawingUnit = null)
    {
        static double? ScaleNullable(double value, double? scale) =>
            scale is > 0 ? value * scale.Value : null;

        return new OpeningPlacementExport(
            placement.HostWallId,
            placement.AnchorWallIds,
            LineExport.From(placement.ReferenceLine),
            ScaleLine(placement.ReferenceLine, millimetersPerDrawingUnit),
            PointExport.From(placement.StartPoint),
            ScalePoint(placement.StartPoint, millimetersPerDrawingUnit),
            PointExport.From(placement.EndPoint),
            ScalePoint(placement.EndPoint, millimetersPerDrawingUnit),
            placement.StartOffsetDrawingUnits,
            placement.EndOffsetDrawingUnits,
            placement.CenterOffsetDrawingUnits,
            placement.LengthDrawingUnits,
            RectExport.From(placement.FootprintBounds),
            ScaleRect(placement.FootprintBounds, millimetersPerDrawingUnit),
            placement.FootprintCorners.Select(PointExport.From).ToArray(),
            millimetersPerDrawingUnit is > 0
                ? placement.FootprintCorners.Select(point => ScalePoint(point, millimetersPerDrawingUnit)!).ToArray()
                : null,
            LineExport.From(placement.StartJambLine),
            ScaleLine(placement.StartJambLine, millimetersPerDrawingUnit),
            LineExport.From(placement.EndJambLine),
            ScaleLine(placement.EndJambLine, millimetersPerDrawingUnit),
            placement.DepthDrawingUnits,
            placement.DepthMillimeters ?? ScaleNullable(placement.DepthDrawingUnits, millimetersPerDrawingUnit),
            placement.StartOffsetMillimeters ?? ScaleNullable(placement.StartOffsetDrawingUnits, millimetersPerDrawingUnit),
            placement.EndOffsetMillimeters ?? ScaleNullable(placement.EndOffsetDrawingUnits, millimetersPerDrawingUnit),
            placement.CenterOffsetMillimeters ?? ScaleNullable(placement.CenterOffsetDrawingUnits, millimetersPerDrawingUnit),
            placement.LengthMillimeters ?? ScaleNullable(placement.LengthDrawingUnits, millimetersPerDrawingUnit),
            placement.HostWallStartParameter,
            placement.HostWallEndParameter,
            placement.HostWallCenterParameter,
            VectorExport.From(placement.AlongVector),
            VectorExport.From(placement.NormalVector),
            placement.CrossWallOffsetDrawingUnits,
            ScaleNullable(placement.CrossWallOffsetDrawingUnits, millimetersPerDrawingUnit),
            placement.Confidence.Value,
            placement.Evidence);
    }

    private static PointExport? ScalePoint(PlanPoint point, double? millimetersPerDrawingUnit) =>
        millimetersPerDrawingUnit is > 0
            ? new PointExport(point.X * millimetersPerDrawingUnit.Value, point.Y * millimetersPerDrawingUnit.Value)
            : null;

    private static LineExport? ScaleLine(PlanLineSegment line, double? millimetersPerDrawingUnit) =>
        millimetersPerDrawingUnit is > 0
            ? new LineExport(ScalePoint(line.Start, millimetersPerDrawingUnit)!, ScalePoint(line.End, millimetersPerDrawingUnit)!)
            : null;

    private static RectExport? ScaleRect(PlanRect rect, double? millimetersPerDrawingUnit)
    {
        if (millimetersPerDrawingUnit is not > 0)
        {
            return null;
        }

        var scale = millimetersPerDrawingUnit.Value;
        return new RectExport(rect.X * scale, rect.Y * scale, rect.Width * scale, rect.Height * scale);
    }
}
public sealed record OpeningRoomConnectionExport(
    string RoomId,
    string? RoomLabel,
    string RoomUseKind,
    IReadOnlyList<string> RoomAdjacencyIds,
    string Side,
    PointExport? RoomSidePoint,
    PointExport? NearestBoundaryPoint,
    double SignedDistanceFromOpening,
    double DistanceToOpening,
    bool SharesHostWall,
    double Confidence,
    IReadOnlyList<string> Evidence)
{
    public static OpeningRoomConnectionExport From(OpeningRoomConnection connection) =>
        new(
            connection.RoomId,
            connection.RoomLabel,
            connection.RoomUseKind.ToString(),
            connection.RoomAdjacencyIds,
            connection.Side.ToString(),
            connection.RoomSidePoint is null ? null : PointExport.From(connection.RoomSidePoint.Value),
            connection.NearestBoundaryPoint is null ? null : PointExport.From(connection.NearestBoundaryPoint.Value),
            connection.SignedDistanceFromOpening,
            connection.DistanceToOpening,
            connection.SharesHostWall,
            connection.Confidence.Value,
            connection.Evidence);
}

public sealed record ObjectExport(
    string Id,
    int PageNumber,
    string Kind,
    string Category,
    string SourceKind,
    string? SourceWallComponentId,
    string? SourceWallComponentKind,
    RectExport Bounds,
    double Confidence,
    string? Label,
    string? SymbolName,
    string? DetectedTag,
    string? DetectedTagSourcePrimitiveId,
    string? RoomId,
    string? RoomLabel,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<ObjectNearbyTextExport> NearbyText,
    VisualAiClassificationExport? VisualAi,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> SourceLayers)
{
    public static ObjectExport From(
        ObjectCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            candidate.Id,
            candidate.PageNumber,
            candidate.Kind.ToString(),
            candidate.Category.ToString(),
            candidate.SourceKind.ToString(),
            candidate.SourceWallComponentId,
            candidate.SourceWallComponentKind?.ToString(),
            RectExport.From(candidate.Bounds),
            candidate.Confidence.Value,
            candidate.Label,
            candidate.SymbolName,
            candidate.DetectedTag,
            candidate.DetectedTagSourcePrimitiveId,
            candidate.RoomId,
            candidate.RoomLabel,
            candidate.SourcePrimitiveIds,
            candidate.NearbyText.Select(ObjectNearbyTextExport.From).ToArray(),
            candidate.VisualAi is null ? null : VisualAiClassificationExport.From(candidate.VisualAi),
            candidate.Evidence,
            ExportSourceHelpers.SourceLayers(candidate.SourcePrimitiveIds, sourceLookup));
}

public sealed record ObjectNearbyTextExport(
    string Text,
    int PageNumber,
    RectExport Bounds,
    string SourcePrimitiveId,
    double Distance)
{
    public static ObjectNearbyTextExport From(ObjectNearbyText text) =>
        new(
            text.Text,
            text.PageNumber,
            RectExport.From(text.Bounds),
            text.SourcePrimitiveId,
            text.Distance);
}

public sealed record ObjectGroupExport(
    string Id,
    string Signature,
    string Kind,
    string Category,
    int Count,
    RectExport RepresentativeBounds,
    IReadOnlyList<int> PageNumbers,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    bool RequiresReview,
    double Confidence,
    string? Label,
    string? SymbolName,
    IReadOnlyList<string> DetectedTags,
    IReadOnlyList<ObjectNearbyTextExport> NearbyText,
    VisualAiClassificationExport? VisualAi,
    IReadOnlyList<string> Evidence)
{
    public static ObjectGroupExport From(
        ObjectCandidateGroup group,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            group.Id,
            group.Signature,
            group.Kind.ToString(),
            group.Category.ToString(),
            group.Count,
            RectExport.From(group.RepresentativeBounds),
            group.PageNumbers,
            group.CandidateIds,
            group.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(group.SourcePrimitiveIds, sourceLookup),
            group.RequiresReview,
            group.Confidence.Value,
            group.Label,
            group.SymbolName,
            group.DetectedTags,
            group.NearbyText.Select(ObjectNearbyTextExport.From).ToArray(),
            group.VisualAi is null ? null : VisualAiClassificationExport.From(group.VisualAi),
            group.Evidence);
}

public sealed record ObjectAggregateExport(
    string Id,
    int PageNumber,
    RectExport Bounds,
    string Category,
    string Kind,
    int ChildObjectCount,
    IReadOnlyList<string> ChildObjectIds,
    IReadOnlyList<string> ObjectGroupIds,
    ObjectAggregateCompositionExport Composition,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    string RoutingInfluence,
    string StructuralInfluence,
    bool SuppressChildObjectsForRouting,
    string RoomUseEvidence,
    double Confidence,
    string? Label,
    string? RoomId,
    string? RoomLabel,
    bool RequiresReview,
    IReadOnlyList<string> NearbyText,
    IReadOnlyList<string> Evidence)
{
    public static ObjectAggregateExport From(
        ObjectAggregate aggregate,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            aggregate.Id,
            aggregate.PageNumber,
            RectExport.From(aggregate.Bounds),
            aggregate.Category.ToString(),
            aggregate.Kind.ToString(),
            aggregate.ChildObjectCount,
            aggregate.ChildObjectIds,
            aggregate.ObjectGroupIds,
            ObjectAggregateCompositionExport.From(aggregate.Composition),
            aggregate.SourcePrimitiveIds,
            aggregate.SourceLayers.Count > 0
                ? aggregate.SourceLayers
                : ExportSourceHelpers.SourceLayers(aggregate.SourcePrimitiveIds, sourceLookup),
            aggregate.RoutingInfluence.ToString(),
            aggregate.StructuralInfluence.ToString(),
            aggregate.SuppressChildObjectsForRouting,
            aggregate.RoomUseEvidence.ToString(),
            aggregate.Confidence.Value,
            aggregate.Label,
            aggregate.RoomId,
            aggregate.RoomLabel,
            aggregate.RequiresReview,
            aggregate.NearbyText,
            aggregate.Evidence);
}

public sealed record ObjectAggregateCompositionExport(
    IReadOnlyList<ObjectAggregateCompositionCountExport> CategoryCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> KindCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> SourceKindCounts,
    IReadOnlyList<ObjectAggregateCompositionCountExport> SourceWallComponentKindCounts,
    IReadOnlyList<string> SourceWallComponentIds,
    IReadOnlyList<ObjectAggregateChildObjectExport> Children)
{
    public static ObjectAggregateCompositionExport From(ObjectAggregateComposition composition) =>
        new(
            composition.CategoryCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.KindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceKindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceWallComponentKindCounts.Select(ObjectAggregateCompositionCountExport.From).ToArray(),
            composition.SourceWallComponentIds,
            composition.Children.Select(ObjectAggregateChildObjectExport.From).ToArray());
}

public sealed record ObjectAggregateCompositionCountExport(string Value, int Count)
{
    public static ObjectAggregateCompositionCountExport From(ObjectAggregateCompositionCount count) =>
        new(count.Value, count.Count);
}

public sealed record ObjectAggregateChildObjectExport(
    string ObjectId,
    RectExport Bounds,
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
    public static ObjectAggregateChildObjectExport From(ObjectAggregateChildObject child) =>
        new(
            child.ObjectId,
            RectExport.From(child.Bounds),
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

public sealed record RoutingLayerExport(
    IReadOnlyList<RoutingBarrierExport> Barriers,
    IReadOnlyList<RoutingPassageExport> Passages,
    IReadOnlyList<RoutingObstacleExport> Obstacles,
    IReadOnlyList<RoutingRoomUseHintExport> RoomUseHints,
    IReadOnlyList<RoutingSuppressedObjectExport> SuppressedObjects,
    IReadOnlyList<RoutingIgnoredObjectExport> IgnoredObjects,
    IReadOnlyList<string> SuppressedObjectCandidateIds,
    IReadOnlyList<string> IgnoredObjectCandidateIds,
    IReadOnlyList<string> Evidence)
{
    public static RoutingLayerExport From(
        PlanRoutingLayer routingLayer,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            routingLayer.Barriers.Select(barrier => RoutingBarrierExport.From(barrier, sourceLookup)).ToArray(),
            routingLayer.Passages.Select(passage => RoutingPassageExport.From(passage, sourceLookup)).ToArray(),
            routingLayer.Obstacles.Select(obstacle => RoutingObstacleExport.From(obstacle, sourceLookup)).ToArray(),
            routingLayer.RoomUseHints.Select(hint => RoutingRoomUseHintExport.From(hint, sourceLookup)).ToArray(),
            routingLayer.SuppressedObjects.Select(item => RoutingSuppressedObjectExport.From(item, sourceLookup)).ToArray(),
            routingLayer.IgnoredObjects.Select(item => RoutingIgnoredObjectExport.From(item, sourceLookup)).ToArray(),
            routingLayer.SuppressedObjectCandidateIds,
            routingLayer.IgnoredObjectCandidateIds,
            routingLayer.Evidence);
}

public sealed record RoutingBarrierExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    LineExport CenterLine,
    RectExport Bounds,
    double Thickness,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    string? WallComponentId,
    string? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingBarrierExport From(
        RoutingBarrier barrier,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            barrier.Id,
            barrier.PageNumber,
            barrier.SourceId,
            barrier.SourceKind.ToString(),
            LineExport.From(barrier.CenterLine),
            RectExport.From(barrier.Bounds),
            barrier.Thickness,
            barrier.DrawingLength,
            barrier.LengthMeters,
            barrier.ThicknessMillimeters,
            barrier.MeasurementScaleGroupId,
            barrier.WallComponentId,
            barrier.WallComponentKind?.ToString(),
            barrier.ExcludedFromStructuralTopology,
            barrier.Confidence.Value,
            barrier.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(barrier.SourcePrimitiveIds, sourceLookup),
            barrier.Evidence);
}

public sealed record RoutingPassageExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    string Type,
    string Operation,
    string Orientation,
    LineExport CenterLine,
    RectExport Bounds,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
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
    public static RoutingPassageExport From(
        RoutingPassage passage,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            passage.Id,
            passage.PageNumber,
            passage.SourceId,
            passage.SourceKind.ToString(),
            passage.Type.ToString(),
            passage.Operation.ToString(),
            passage.Orientation.ToString(),
            LineExport.From(passage.CenterLine),
            RectExport.From(passage.Bounds),
            passage.DrawingWidth,
            passage.WidthMillimeters,
            passage.MeasurementScaleGroupId,
            passage.HostWallIds,
            passage.ConnectedRoomIds,
            passage.ConnectedRoomLabels,
            passage.ConnectedRoomLinks.Select(OpeningRoomConnectionExport.From).ToArray(),
            passage.RoomAdjacencyIds,
            passage.Placement is null ? null : OpeningPlacementExport.From(passage.Placement),
            passage.Placement is null ? "Unanchored" : "Anchored",
            passage.ReadyForCoordinatePlacement,
            passage.RequiresReview,
            passage.ReviewReasons,
            passage.Confidence.Value,
            passage.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(passage.SourcePrimitiveIds, sourceLookup),
            passage.Evidence);
}

public sealed record RoutingObstacleExport(
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
    public static RoutingObstacleExport From(
        RoutingObstacle obstacle,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
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

public sealed record RoutingRoomUseHintExport(
    string Id,
    int PageNumber,
    string SourceId,
    string SourceKind,
    string RoomUseKind,
    RectExport Bounds,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingRoomUseHintExport From(
        RoutingRoomUseHint hint,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
            hint.Id,
            hint.PageNumber,
            hint.SourceId,
            hint.SourceKind.ToString(),
            hint.RoomUseKind.ToString(),
            RectExport.From(hint.Bounds),
            hint.RoomId,
            hint.RoomLabel,
            hint.Confidence.Value,
            hint.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(hint.SourcePrimitiveIds, sourceLookup),
            hint.Evidence);
}

public sealed record RoutingSuppressedObjectExport(
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
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    double Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence)
{
    public static RoutingSuppressedObjectExport From(
        RoutingSuppressedObject suppressed,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
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
            suppressed.CandidateLabel,
            suppressed.RoomId,
            suppressed.RoomLabel,
            suppressed.Confidence.Value,
            suppressed.SourcePrimitiveIds,
            ExportSourceHelpers.SourceLayers(suppressed.SourcePrimitiveIds, sourceLookup),
            suppressed.Evidence);
}

public sealed record RoutingIgnoredObjectExport(
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
    public static RoutingIgnoredObjectExport From(
        RoutingIgnoredObject ignored,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        new(
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

public sealed record VisualAiClassificationExport(
    string Label,
    string Category,
    double Confidence,
    string ModelName,
    string ModelVersion,
    string InferenceEngine,
    int PageNumber,
    RectExport CropBounds,
    string CropSourceId,
    IReadOnlyList<VisualAiAlternativeExport> Alternatives,
    IReadOnlyList<string> Evidence)
{
    public static VisualAiClassificationExport From(VisualAiClassification classification) =>
        new(
            classification.Label,
            classification.Category.ToString(),
            classification.Confidence,
            classification.ModelName,
            classification.ModelVersion,
            classification.InferenceEngine,
            classification.PageNumber,
            RectExport.From(classification.CropBounds),
            classification.CropSourceId,
            classification.Alternatives.Select(VisualAiAlternativeExport.From).ToArray(),
            classification.Evidence);
}

public sealed record VisualAiAlternativeExport(
    string Label,
    string Category,
    double Confidence,
    IReadOnlyDictionary<string, string> Evidence)
{
    public static VisualAiAlternativeExport From(VisualAiClassificationCandidate candidate) =>
        new(
            candidate.Label,
            candidate.Category.ToString(),
            candidate.Confidence,
            candidate.Evidence);
}

public sealed record ScanReviewQueueItemExport(
    string Id,
    string Kind,
    string Detector,
    string ItemId,
    int Priority,
    string Severity,
    int? PageNumber,
    IReadOnlyList<int> PageNumbers,
    RectExport? Bounds,
    double Confidence,
    string RecommendedAction,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<string> Evidence,
    IReadOnlyDictionary<string, string> Properties)
{
    public static IReadOnlyList<ScanReviewQueueItemExport> From(
        PlanScanResult result,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup)
    {
        var dimensionsById = result.Dimensions
            .GroupBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var items = new List<ScanReviewQueueItemExport>();

        foreach (var check in result.MeasurementConsistency.Checks
                     .Where(check => check.Status == MeasurementConsistencyStatus.Outlier)
                     .OrderByDescending(check => check.RelativeError ?? 0)
                     .ThenBy(check => check.DimensionId, StringComparer.Ordinal))
        {
            dimensionsById.TryGetValue(check.DimensionId, out var dimension);
            var blocking = result.MeasurementConsistency.HasBlockingOutliers;
            items.Add(new ScanReviewQueueItemExport(
                $"review:measurement:{check.DimensionId}",
                ScanReviewQueueKinds.MeasurementOutlier,
                "measurementConsistency",
                check.DimensionId,
                blocking ? 0 : 10,
                blocking ? DiagnosticSeverity.Warning.ToString() : DiagnosticSeverity.Info.ToString(),
                check.PageNumber,
                new[] { check.PageNumber },
                dimension is null ? null : RectExport.From(dimension.Bounds),
                check.Confidence.Value,
                blocking
                    ? "Review dimension/calibration conflict before using millimeter coordinates."
                    : "Review bounded dimension outlier; selected calibration remains metric-import ready.",
                check.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(check.SourcePrimitiveIds, sourceLookup),
                check.Evidence
                    .Concat(new[] { $"Metric import impact: {(blocking ? "Blocking" : "ReviewOnly")}." })
                    .ToArray(),
                new Dictionary<string, string>
                {
                    ["dimensionId"] = check.DimensionId,
                    ["status"] = check.Status.ToString(),
                    ["relativeError"] = FormatNullable(check.RelativeError),
                    ["impliedMillimetersPerDrawingUnit"] = Format(check.ImpliedMillimetersPerDrawingUnit),
                    ["selectedMillimetersPerDrawingUnit"] = FormatNullable(check.SelectedMillimetersPerDrawingUnit),
                    ["metricImportImpact"] = blocking ? "Blocking" : "ReviewOnly"
                }));
        }

        foreach (var entry in result.Diagnostics.Messages
                     .Where(diagnostic => string.Equals(
                         diagnostic.Code,
                         "walls.dense_orthogonal_pattern_filtered",
                         StringComparison.Ordinal))
                     .OrderBy(diagnostic => diagnostic.PageNumber ?? int.MaxValue)
                     .ThenBy(diagnostic => diagnostic.Stage, StringComparer.Ordinal)
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["filteredLineCount"] = properties.TryGetValue("filteredLineCount", out var filteredLineCount)
                ? filteredLineCount
                : diagnostic.SourcePrimitiveIds.Count.ToString(CultureInfo.InvariantCulture);

            var patternEvidence = properties.TryGetValue("patterns", out var patterns) && !string.IsNullOrWhiteSpace(patterns)
                ? new[] { $"suppressed pattern summary: {patterns}" }
                : Array.Empty<string>();
            var countEvidence = new[]
            {
                $"suppressed {diagnostic.SourcePrimitiveIds.Count} source primitive(s) before wall reconstruction"
            };

            items.Add(new ScanReviewQueueItemExport(
                $"review:suppressed-wall-pattern:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                ScanReviewQueueKinds.SuppressedWallPatternReview,
                diagnostic.Stage,
                diagnostic.Code,
                25,
                diagnostic.Severity == DiagnosticSeverity.Error
                    ? DiagnosticSeverity.Error.ToString()
                    : diagnostic.Severity == DiagnosticSeverity.Warning
                        ? DiagnosticSeverity.Warning.ToString()
                        : DiagnosticSeverity.Info.ToString(),
                diagnostic.PageNumber,
                pageNumbers,
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Confidence?.Value ?? 0.5,
                "Visually verify suppressed dense surface/detail linework before treating the remaining wall graph as final.",
                diagnostic.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(diagnostic.SourcePrimitiveIds, sourceLookup),
                new[] { diagnostic.Message }
                    .Concat(patternEvidence)
                    .Concat(countEvidence)
                    .ToArray(),
                properties));
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedWallEvidenceReviews(result.WallEvidenceMap)
                     .Select((assessment, index) => new { Assessment = assessment, Index = index }))
        {
            var assessment = entry.Assessment;
            var sourcePrimitiveIds = assessment.SourcePrimitiveIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var severity = ScanReviewQueueSummary.WallEvidenceReviewSeverity(assessment);
            var reviewReason = ScanReviewQueueSummary.WallEvidenceReviewReason(assessment);
            var priorityScore = ScanReviewQueueSummary.WallEvidenceReviewPriorityScore(assessment);
            var score = assessment.ScoreBreakdown;

            items.Add(new ScanReviewQueueItemExport(
                $"review:wall-evidence:{assessment.WallId}",
                ScanReviewQueueKinds.WallEvidenceReview,
                "wallEvidence",
                assessment.WallId,
                12 + entry.Index,
                severity.ToString(),
                assessment.PageNumber,
                new[] { assessment.PageNumber },
                RectExport.From(assessment.Bounds),
                assessment.Confidence.Value,
                "Review this wall candidate before importing it as coordinate-ready structural geometry.",
                sourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(sourcePrimitiveIds, sourceLookup),
                assessment.Evidence
                    .Concat(score.PositiveEvidence.Select(evidence => $"positive: {evidence}"))
                    .Concat(score.NegativeEvidence.Select(evidence => $"negative: {evidence}"))
                    .Concat(new[]
                    {
                        $"review queue rank {entry.Index + 1} of {ScanReviewQueueSummary.WallEvidenceReviewQueueLimit}",
                        reviewReason
                    })
                    .ToArray(),
                new Dictionary<string, string>
                {
                    ["wallId"] = assessment.WallId,
                    ["category"] = assessment.Category.ToString(),
                    ["decision"] = assessment.Decision.ToString(),
                    ["placementReady"] = assessment.PlacementReady.ToString(CultureInfo.InvariantCulture),
                    ["requiresReview"] = assessment.RequiresReview.ToString(CultureInfo.InvariantCulture),
                    ["rejectedAsNoise"] = assessment.RejectedAsNoise.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueLimit"] = ScanReviewQueueSummary.WallEvidenceReviewQueueLimit.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueReason"] = reviewReason,
                    ["reviewQueuePriorityScore"] = priorityScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["positiveScore"] = score.PositiveScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["negativeScore"] = score.NegativeScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["decisionScore"] = score.DecisionScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["pairSupportScore"] = score.PairSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["layerSupportScore"] = score.LayerSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["structuralSupportScore"] = score.StructuralSupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["recoverySupportScore"] = score.RecoverySupportScore.ToString("0.###", CultureInfo.InvariantCulture),
                    ["noisePenalty"] = score.NoisePenalty.ToString("0.###", CultureInfo.InvariantCulture),
                    ["fragmentReviewPenalty"] = score.FragmentReviewPenalty.ToString("0.###", CultureInfo.InvariantCulture)
                }));
        }

        foreach (var entry in result.SurfacePatterns
                     .Where(pattern => pattern.RequiresReview)
                     .OrderBy(pattern => pattern.PageNumber)
                     .ThenBy(pattern => pattern.Bounds.Top)
                     .ThenBy(pattern => pattern.Bounds.Left)
                     .ThenBy(pattern => pattern.Id, StringComparer.Ordinal)
                     .Select((pattern, index) => new { Pattern = pattern, Index = index }))
        {
            var pattern = entry.Pattern;
            var sourceLayers = ExportSourceHelpers.SourceLayers(pattern.SourcePrimitiveIds, sourceLookup);
            items.Add(new ScanReviewQueueItemExport(
                $"review:surface-pattern:{pattern.Id}",
                ScanReviewQueueKinds.SurfacePatternReview,
                "surfacePatterns",
                pattern.Id,
                22 + entry.Index,
                DiagnosticSeverity.Info.ToString(),
                pattern.PageNumber,
                new[] { pattern.PageNumber },
                RectExport.From(pattern.Bounds),
                pattern.Confidence.Value,
                "Verify this dense non-structural surface/detail pattern and keep it excluded from wall topology unless review proves it is structural.",
                pattern.SourcePrimitiveIds,
                sourceLayers,
                pattern.Evidence
                    .Concat(new[]
                    {
                        $"{pattern.Kind} surface pattern with {pattern.LineCount.ToString(CultureInfo.InvariantCulture)} source line(s)",
                        $"excluded from wall detection: {pattern.ExcludedFromWallDetection.ToString(CultureInfo.InvariantCulture)}",
                        $"excluded from structural topology: {pattern.ExcludedFromStructuralTopology.ToString(CultureInfo.InvariantCulture)}"
                    })
                    .ToArray(),
                new Dictionary<string, string>
                {
                    ["surfacePatternId"] = pattern.Id,
                    ["kind"] = pattern.Kind.ToString(),
                    ["orientation"] = pattern.Orientation.ToString(),
                    ["sourceRegionId"] = pattern.SourceRegionId ?? string.Empty,
                    ["lineCount"] = pattern.LineCount.ToString(CultureInfo.InvariantCulture),
                    ["horizontalLineCount"] = pattern.HorizontalLineCount.ToString(CultureInfo.InvariantCulture),
                    ["verticalLineCount"] = pattern.VerticalLineCount.ToString(CultureInfo.InvariantCulture),
                    ["intersectionCount"] = pattern.IntersectionCount.ToString(CultureInfo.InvariantCulture),
                    ["horizontalMedianSpacing"] = FormatNullable(pattern.HorizontalMedianSpacing),
                    ["verticalMedianSpacing"] = FormatNullable(pattern.VerticalMedianSpacing),
                    ["medianSpacing"] = FormatNullable(pattern.MedianSpacing),
                    ["excludedFromWallDetection"] = pattern.ExcludedFromWallDetection.ToString(CultureInfo.InvariantCulture),
                    ["excludedFromStructuralTopology"] = pattern.ExcludedFromStructuralTopology.ToString(CultureInfo.InvariantCulture),
                    ["sourceLayerCount"] = sourceLayers.Count.ToString(CultureInfo.InvariantCulture)
                }));
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedSurfacePatternWallOverlapDiagnostics(result.Diagnostics.Messages)
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["detector"] = diagnostic.Stage;
            properties["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture);
            properties["reviewQueueLimit"] = ScanReviewQueueSummary.SurfacePatternWallOverlapReviewQueueLimit.ToString(CultureInfo.InvariantCulture);
            properties["reviewQueueReason"] = ScanReviewQueueSummary.SurfacePatternWallOverlapReviewReason(diagnostic);
            properties["reviewQueuePriorityScore"] = ScanReviewQueueSummary.SurfacePatternWallOverlapPriorityScore(diagnostic).ToString("0.###", CultureInfo.InvariantCulture);

            var surfacePatternId = properties.TryGetValue("surfacePatternId", out var patternIdValue)
                ? patternIdValue
                : string.Empty;
            var wallId = properties.TryGetValue("wallId", out var wallIdValue)
                ? wallIdValue
                : string.Empty;
            var sharedSourceCount = properties.TryGetValue("sharedSourcePrimitiveCount", out var sharedCount)
                ? new[] { $"{sharedCount} shared source primitive(s)" }
                : Array.Empty<string>();
            var overlapEvidence = properties.TryGetValue("wallOverlapRatio", out var wallOverlapRatio)
                ? new[] { $"wall overlap ratio {wallOverlapRatio}" }
                : Array.Empty<string>();

            items.Add(new ScanReviewQueueItemExport(
                $"review:surface-pattern-wall-overlap:{surfacePatternId}:{wallId}",
                ScanReviewQueueKinds.SurfacePatternWallOverlapReview,
                diagnostic.Stage,
                string.IsNullOrWhiteSpace(surfacePatternId) || string.IsNullOrWhiteSpace(wallId)
                    ? diagnostic.Code
                    : $"{surfacePatternId}:{wallId}",
                18 + entry.Index,
                diagnostic.Severity.ToString(),
                diagnostic.PageNumber,
                pageNumbers,
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Confidence?.Value ?? 0.5,
                "Review this wall/surface-pattern overlap before trusting the wall as structural topology.",
                diagnostic.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(diagnostic.SourcePrimitiveIds, sourceLookup),
                new[] { diagnostic.Message }
                    .Concat(sharedSourceCount)
                    .Concat(overlapEvidence)
                    .Concat(new[]
                    {
                        $"review queue rank {entry.Index + 1} of {ScanReviewQueueSummary.SurfacePatternWallOverlapReviewQueueLimit}",
                        ScanReviewQueueSummary.SurfacePatternWallOverlapReviewReason(diagnostic)
                    })
                    .ToArray(),
                properties));
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedWallGraphGapDiagnostics(result.Diagnostics.Messages)
                     .Select((diagnostic, index) => new { Diagnostic = diagnostic, Index = index }))
        {
            var diagnostic = entry.Diagnostic;
            var pageNumbers = diagnostic.PageNumber is int pageNumber
                ? new[] { pageNumber }
                : result.Document.Pages.Select(page => page.Number).ToArray();
            var properties = diagnostic.Properties
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            properties["diagnosticCode"] = diagnostic.Code;
            properties["diagnosticScope"] = diagnostic.Scope.ToString();
            properties["detector"] = diagnostic.Stage;
            properties["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture);
            properties["reviewQueueLimit"] = ScanReviewQueueSummary.WallGraphGapReviewQueueLimit.ToString(CultureInfo.InvariantCulture);
            properties["reviewQueueReason"] = ScanReviewQueueSummary.WallGraphGapReviewReason(diagnostic);
            properties["reviewQueuePriorityScore"] = ScanReviewQueueSummary.WallGraphGapPriorityScore(diagnostic).ToString("0.###", CultureInfo.InvariantCulture);

            var gapEvidence = properties.TryGetValue("gapDistance", out var gapDistance)
                ? new[] { $"gap distance {gapDistance} drawing units" }
                : properties.TryGetValue("overrunDistance", out var overrunDistance)
                    ? new[] { $"overrun distance {overrunDistance} drawing units" }
                    : Array.Empty<string>();
            var wallEvidence = properties.TryGetValue("wallIds", out var wallIds) && !string.IsNullOrWhiteSpace(wallIds)
                ? new[] { $"candidate wall ids: {wallIds}" }
                : Array.Empty<string>();
            var isEndpointOverrun = string.Equals(diagnostic.Code, "wall_graph.endpoint_overrun.review", StringComparison.Ordinal);

            items.Add(new ScanReviewQueueItemExport(
                $"review:{(isEndpointOverrun ? "wall-graph-endpoint-overrun" : "wall-graph-gap")}:{diagnostic.PageNumber?.ToString(CultureInfo.InvariantCulture) ?? "document"}:{entry.Index + 1}",
                ScanReviewQueueKinds.WallGraphGapReview,
                diagnostic.Stage,
                diagnostic.Code,
                15 + entry.Index,
                diagnostic.Severity.ToString(),
                diagnostic.PageNumber,
                pageNumbers,
                diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
                diagnostic.Confidence?.Value ?? 0.5,
                isEndpointOverrun
                    ? "Review or correct this possible endpoint-overrun trim before trusting wall graph topology."
                    : "Review or correct this possible unsnapped wall junction before trusting wall graph topology.",
                diagnostic.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(diagnostic.SourcePrimitiveIds, sourceLookup),
                new[] { diagnostic.Message }
                    .Concat(gapEvidence)
                    .Concat(wallEvidence)
                    .Concat(new[]
                    {
                        $"review queue rank {entry.Index + 1} of {ScanReviewQueueSummary.WallGraphGapReviewQueueLimit}",
                        ScanReviewQueueSummary.WallGraphGapReviewReason(diagnostic)
                    })
                    .ToArray(),
                properties));
        }

        foreach (var entry in ScanReviewQueueSummary.QueuedObjectGroups(result.ObjectGroups)
                     .Select((group, index) => new { Group = group, Index = index }))
        {
            var group = entry.Group;
            var reviewReason = ScanReviewQueueSummary.ObjectGroupReviewReason(group);

            items.Add(new ScanReviewQueueItemExport(
                $"review:object-group:{group.Id}",
                ScanReviewQueueKinds.ObjectGroupReview,
                "objectGroups",
                group.Id,
                30 + entry.Index,
                DiagnosticSeverity.Info.ToString(),
                group.PageNumbers.Count == 1 ? group.PageNumbers[0] : null,
                group.PageNumbers,
                RectExport.From(group.RepresentativeBounds),
                group.Confidence.Value,
                "Label, ignore, or promote this repeated object/symbol group through the correction workflow.",
                group.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(group.SourcePrimitiveIds, sourceLookup),
                group.Evidence
                    .Concat(new[]
                    {
                        $"review queue rank {entry.Index + 1} of {ScanReviewQueueSummary.ObjectGroupReviewQueueLimit}",
                        reviewReason
                    })
                    .ToArray(),
                new Dictionary<string, string>
                {
                    ["signature"] = group.Signature,
                    ["category"] = group.Category.ToString(),
                    ["kind"] = group.Kind.ToString(),
                    ["count"] = group.Count.ToString(CultureInfo.InvariantCulture),
                    ["label"] = group.Label ?? string.Empty,
                    ["symbolName"] = group.SymbolName ?? string.Empty,
                    ["detectedTags"] = string.Join(",", group.DetectedTags),
                    ["reviewQueueRank"] = (entry.Index + 1).ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueLimit"] = ScanReviewQueueSummary.ObjectGroupReviewQueueLimit.ToString(CultureInfo.InvariantCulture),
                    ["reviewQueueReason"] = reviewReason,
                    ["reviewQueuePriorityScore"] = ScanReviewQueueSummary.ObjectGroupReviewPriorityScore(group).ToString(CultureInfo.InvariantCulture)
                }));
        }

        foreach (var aggregate in result.ObjectAggregates
                     .Where(aggregate => aggregate.RequiresReview)
                     .OrderByDescending(aggregate => aggregate.ChildObjectCount)
                     .ThenBy(aggregate => aggregate.Id, StringComparer.Ordinal))
        {
            items.Add(new ScanReviewQueueItemExport(
                $"review:object-aggregate:{aggregate.Id}",
                ScanReviewQueueKinds.ObjectAggregateReview,
                "objectAggregates",
                aggregate.Id,
                35,
                DiagnosticSeverity.Info.ToString(),
                aggregate.PageNumber,
                new[] { aggregate.PageNumber },
                RectExport.From(aggregate.Bounds),
                aggregate.Confidence.Value,
                "Review compound object aggregate before using its routing or room-use influence.",
                aggregate.SourcePrimitiveIds,
                aggregate.SourceLayers.Count > 0
                    ? aggregate.SourceLayers
                    : ExportSourceHelpers.SourceLayers(aggregate.SourcePrimitiveIds, sourceLookup),
                aggregate.Evidence,
                new Dictionary<string, string>
                {
                    ["category"] = aggregate.Category.ToString(),
                    ["kind"] = aggregate.Kind.ToString(),
                    ["childObjectCount"] = aggregate.ChildObjectCount.ToString(CultureInfo.InvariantCulture),
                    ["routingInfluence"] = aggregate.RoutingInfluence.ToString(),
                    ["structuralInfluence"] = aggregate.StructuralInfluence.ToString(),
                    ["roomUseEvidence"] = aggregate.RoomUseEvidence.ToString(),
                    ["label"] = aggregate.Label ?? string.Empty
                }));
        }

        foreach (var opening in result.Openings
                     .Where(NeedsOpeningReview)
                     .OrderBy(opening => opening.PageNumber)
                     .ThenBy(opening => opening.Id, StringComparer.Ordinal))
        {
            var reasons = OpeningReviewReasons(opening).ToArray();
            var placementReady = ScanReviewQueueSummary.OpeningPlacementIsCoordinateReady(opening);
            items.Add(new ScanReviewQueueItemExport(
                $"review:opening:{opening.Id}",
                ScanReviewQueueKinds.OpeningReview,
                "openings",
                opening.Id,
                placementReady ? 45 : 20,
                placementReady ? DiagnosticSeverity.Info.ToString() : DiagnosticSeverity.Warning.ToString(),
                opening.PageNumber,
                new[] { opening.PageNumber },
                RectExport.From(opening.Bounds),
                opening.Confidence.Value,
                "Review opening anchoring, operation, and connected-room evidence before downstream placement use.",
                opening.SourcePrimitiveIds,
                ExportSourceHelpers.SourceLayers(opening.SourcePrimitiveIds, sourceLookup),
                opening.Evidence.Concat(reasons).ToArray(),
                new Dictionary<string, string>
                {
                    ["type"] = opening.Type.ToString(),
                    ["operation"] = opening.Operation.ToString(),
                    ["placementStatus"] = opening.Placement is null ? "Unanchored" : "Anchored",
                    ["hostWallIds"] = string.Join(",", opening.HostWallIds),
                    ["connectedRoomIds"] = string.Join(",", opening.ConnectedRoomIds),
                    ["reasons"] = string.Join("; ", reasons)
                }));
        }

        return items
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.PageNumber ?? int.MaxValue)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool NeedsOpeningReview(OpeningCandidate opening) =>
        ScanReviewQueueSummary.NeedsOpeningReview(opening);

    private static IReadOnlyList<string> OpeningReviewReasons(OpeningCandidate opening) =>
        ScanReviewQueueSummary.OpeningReviewReasons(opening);

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string FormatNullable(double? value) =>
        value is null ? string.Empty : Format(value.Value);
}

public sealed record QualityExport(
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
    IReadOnlyList<DetectorQualityExport> Detectors,
    IReadOnlyList<QualityIssueExport> Issues,
    IReadOnlyList<string> Evidence)
{
    public static QualityExport From(PlanScanQualityReport quality) =>
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
            quality.Detectors.Select(DetectorQualityExport.From).ToArray(),
            quality.Issues.Select(QualityIssueExport.From).ToArray(),
            quality.Evidence);
}

public sealed record DetectorQualityExport(
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
    public static DetectorQualityExport From(PlanDetectorQualitySummary detector) =>
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

public sealed record QualityIssueExport(
    string Code,
    string Severity,
    string Message,
    double Confidence,
    IReadOnlyDictionary<string, string> Properties)
{
    public static QualityIssueExport From(PlanScanQualityIssue issue) =>
        new(
            issue.Code,
            issue.Severity.ToString(),
            issue.Message,
            issue.Confidence.Value,
            issue.Properties);
}

public sealed record DiagnosticsExport(
    double DurationMilliseconds,
    string ExecutionModel,
    int StageCount,
    int ExecutionWaveCount,
    int ParallelCandidateStageCount,
    IReadOnlyList<string> ArtifactKinds,
    IReadOnlyList<string> SourceArtifacts,
    int ArtifactPlanCount,
    IReadOnlyList<ArtifactPlanExport> ArtifactPlans,
    int SourceArtifactPlanCount,
    int TerminalArtifactPlanCount,
    int MultiProducerArtifactPlanCount,
    IReadOnlyList<ArtifactInventoryExport> ArtifactInventory,
    int AvailableArtifactCount,
    int EmptyArtifactCount,
    int ImportCriticalArtifactCount,
    int MissingImportCriticalArtifactCount,
    IReadOnlyList<ExecutionWaveExport> ExecutionWaves,
    int RerunImpactCount,
    IReadOnlyList<RerunImpactExport> RerunImpacts,
    int RerunPlanCount,
    IReadOnlyList<RerunPlanExport> RerunPlans,
    bool IsDependencyReady,
    int PlanIssueCount,
    int PlanWarningCount,
    int PlanErrorCount,
    IReadOnlyList<PipelinePlanIssueExport> PlanIssues,
    bool HasErrors,
    int InfoCount,
    int WarningCount,
    int ErrorCount,
    IReadOnlyList<StageExport> Stages,
    IReadOnlyList<DiagnosticExport> Messages)
{
    public static DiagnosticsExport From(PipelineDiagnostics diagnostics)
    {
        var plan = diagnostics.ExecutionPlan;
        var stagePlans = plan.Stages.ToDictionary(stage => stage.Stage, StringComparer.Ordinal);
        var artifactPlans = plan.ArtifactPlans.Select(ArtifactPlanExport.From).ToArray();
        var artifactInventory = ArtifactInventoryExport.From(diagnostics).ToArray();

        return new DiagnosticsExport(
            diagnostics.Duration.TotalMilliseconds,
            plan.ExecutionModel,
            plan.Stages.Count == 0 ? diagnostics.StageReports.Count : plan.Stages.Count,
            plan.ExecutionWaves.Count,
            plan.ExecutionWaves.Sum(wave => wave.IsParallelCandidate ? wave.Stages.Count : 0),
            Enum.GetNames<PlanArtifactKind>()
                .Where(name => !string.Equals(name, nameof(PlanArtifactKind.Unknown), StringComparison.Ordinal))
                .ToArray(),
            plan.SourceArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            artifactPlans.Length,
            artifactPlans,
            artifactPlans.Count(artifact => artifact.IsSourceArtifact),
            artifactPlans.Count(artifact => artifact.IsTerminalArtifact),
            artifactPlans.Count(artifact => artifact.HasMultipleProducers),
            artifactInventory,
            artifactInventory.Count(artifact => artifact.IsPresent),
            artifactInventory.Count(artifact => !artifact.IsPresent),
            artifactInventory.Count(artifact => artifact.IsImportCritical),
            artifactInventory.Count(artifact => artifact.IsImportCritical && !artifact.IsPresent),
            plan.ExecutionWaves.Select(ExecutionWaveExport.From).ToArray(),
            plan.RerunImpacts.Count,
            plan.RerunImpacts.Select(RerunImpactExport.From).ToArray(),
            plan.RerunPlans.Count,
            plan.RerunPlans.Select(RerunPlanExport.From).ToArray(),
            plan.IsDependencyReady,
            plan.Issues.Count,
            plan.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning),
            plan.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error),
            plan.Issues.Select(PipelinePlanIssueExport.From).ToArray(),
            diagnostics.HasErrors,
            diagnostics.InfoCount,
            diagnostics.WarningCount,
            diagnostics.ErrorCount,
            diagnostics.StageReports
                .Select(report => StageExport.From(report, stagePlans.TryGetValue(report.Stage, out var stagePlan) ? stagePlan : null))
                .ToArray(),
            diagnostics.Messages.Select(DiagnosticExport.From).ToArray());
    }
}

public sealed record ArtifactPlanExport(
    string Artifact,
    bool IsSourceArtifact,
    bool IsProducedByStage,
    bool IsConsumedByStage,
    bool IsTerminalArtifact,
    IReadOnlyList<string> ProducerStages,
    IReadOnlyList<string> RequiredConsumerStages,
    IReadOnlyList<string> OptionalConsumerStages,
    IReadOnlyList<string> ConsumerStages,
    int ProducerCount,
    int ConsumerCount,
    int FirstProducerOrder,
    int LastProducerOrder,
    int FirstConsumerOrder,
    int LastConsumerOrder,
    int FirstProducerWave,
    int LastProducerWave,
    int FirstConsumerWave,
    int LastConsumerWave,
    bool HasMultipleProducers,
    bool HasRequiredConsumers,
    string DependencyRole,
    IReadOnlyList<string> Evidence)
{
    public static ArtifactPlanExport From(PipelineArtifactPlan plan) =>
        new(
            plan.Artifact.ToString(),
            plan.IsSourceArtifact,
            plan.IsProducedByStage,
            plan.IsConsumedByStage,
            plan.IsTerminalArtifact,
            plan.ProducerStages,
            plan.RequiredConsumerStages,
            plan.OptionalConsumerStages,
            plan.ConsumerStages,
            plan.ProducerCount,
            plan.ConsumerCount,
            plan.FirstProducerOrder,
            plan.LastProducerOrder,
            plan.FirstConsumerOrder,
            plan.LastConsumerOrder,
            plan.FirstProducerWave,
            plan.LastProducerWave,
            plan.FirstConsumerWave,
            plan.LastConsumerWave,
            plan.HasMultipleProducers,
            plan.HasRequiredConsumers,
            plan.DependencyRole,
            plan.Evidence);
}

public sealed record ArtifactInventoryExport(
    string Artifact,
    int Count,
    string StateKey,
    int Revision,
    bool IsPresent,
    bool IsSourceArtifact,
    bool IsImportCritical,
    string Importance,
    string ReadinessImpact,
    IReadOnlyList<string> ProducerStages,
    IReadOnlyList<string> ConsumerStages,
    IReadOnlyList<string> Evidence)
{
    public static IReadOnlyList<ArtifactInventoryExport> From(PipelineDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var snapshots = diagnostics.ArtifactInventory.Count > 0
            ? diagnostics.ArtifactInventory
            : Enum.GetValues<PlanArtifactKind>()
                .Where(artifact => artifact != PlanArtifactKind.Unknown)
                .Select(artifact => new PipelineArtifactSnapshot(artifact, 0))
                .ToArray();
        var plan = diagnostics.ExecutionPlan;
        var sourceArtifacts = plan.SourceArtifacts.ToHashSet();

        return snapshots
            .Where(snapshot => snapshot.Artifact != PlanArtifactKind.Unknown)
            .OrderBy(snapshot => snapshot.Artifact.ToString(), StringComparer.Ordinal)
            .Select(snapshot =>
            {
                var producers = plan.Stages
                    .Where(stage => stage.Writes.Contains(snapshot.Artifact))
                    .OrderBy(stage => stage.Order)
                    .Select(stage => stage.Stage)
                    .ToArray();
                var consumers = plan.Stages
                    .Where(stage => stage.Reads.Contains(snapshot.Artifact) || stage.OptionalReads.Contains(snapshot.Artifact))
                    .OrderBy(stage => stage.Order)
                    .Select(stage => stage.Stage)
                    .ToArray();
                var isSourceArtifact = sourceArtifacts.Contains(snapshot.Artifact);
                var evidence = new List<string>
                {
                    snapshot.Count > 0 ? "final inventory has count > 0" : "final inventory count is 0",
                    isSourceArtifact ? "artifact is provided by source ingestion" : "artifact is produced by scanner stages"
                };
                if (producers.Length > 0)
                {
                    evidence.Add($"producer stages: {string.Join(", ", producers)}");
                }

                if (consumers.Length > 0)
                {
                    evidence.Add($"consumer stages: {string.Join(", ", consumers)}");
                }

                return new ArtifactInventoryExport(
                    snapshot.Artifact.ToString(),
                    snapshot.Count,
                    snapshot.StateKey,
                    snapshot.Revision,
                    snapshot.Count > 0,
                    isSourceArtifact,
                    IsImportCriticalArtifact(snapshot.Artifact),
                    ImportanceFor(snapshot.Artifact),
                    ReadinessImpactFor(snapshot.Artifact),
                    producers,
                    consumers,
                    evidence);
            })
            .ToArray();
    }

    private static bool IsImportCriticalArtifact(PlanArtifactKind artifact) =>
        artifact is PlanArtifactKind.Document
            or PlanArtifactKind.Pages
            or PlanArtifactKind.Primitives
            or PlanArtifactKind.SheetRegions
            or PlanArtifactKind.Calibration
            or PlanArtifactKind.Walls
            or PlanArtifactKind.WallGraph;

    private static string ImportanceFor(PlanArtifactKind artifact) =>
        artifact switch
        {
            PlanArtifactKind.Document or PlanArtifactKind.Pages or PlanArtifactKind.Primitives => "Source",
            PlanArtifactKind.RasterImages or PlanArtifactKind.PdfImages => "SourceMedia",
            PlanArtifactKind.Layers or PlanArtifactKind.SheetRegions or PlanArtifactKind.TitleBlocks
                or PlanArtifactKind.Annotations or PlanArtifactKind.GridAxes or PlanArtifactKind.SurfacePatterns => "Layout",
            PlanArtifactKind.Calibration or PlanArtifactKind.Dimensions or PlanArtifactKind.GridBays
                or PlanArtifactKind.MeasurementConsistency or PlanArtifactKind.DimensionChains => "Measurement",
            PlanArtifactKind.Walls => "Geometry",
            PlanArtifactKind.WallGraph or PlanArtifactKind.TopologySpans or PlanArtifactKind.Openings
                or PlanArtifactKind.Rooms or PlanArtifactKind.RoomAdjacency => "Topology",
            PlanArtifactKind.ObjectCandidates or PlanArtifactKind.ObjectGroups or PlanArtifactKind.ObjectAggregates
                or PlanArtifactKind.VisualAiClassifications => "Semantics",
            PlanArtifactKind.RoutingBarriers or PlanArtifactKind.RoutingPassages or PlanArtifactKind.RoutingObstacles
                or PlanArtifactKind.RoutingRoomUseHints or PlanArtifactKind.RoutingSuppressedObjects
                or PlanArtifactKind.RoutingIgnoredObjects => "Routing",
            PlanArtifactKind.Diagnostics or PlanArtifactKind.LayerConsistency or PlanArtifactKind.Quality => "Quality",
            _ => "Other"
        };

    private static string ReadinessImpactFor(PlanArtifactKind artifact) =>
        artifact switch
        {
            PlanArtifactKind.Document or PlanArtifactKind.Pages or PlanArtifactKind.Primitives => "SourceIngestion",
            PlanArtifactKind.RasterImages or PlanArtifactKind.PdfImages => "RasterReview",
            PlanArtifactKind.SheetRegions or PlanArtifactKind.TitleBlocks or PlanArtifactKind.Annotations
                or PlanArtifactKind.GridAxes or PlanArtifactKind.SurfacePatterns => "LayoutReview",
            PlanArtifactKind.Calibration or PlanArtifactKind.Dimensions or PlanArtifactKind.GridBays
                or PlanArtifactKind.MeasurementConsistency or PlanArtifactKind.DimensionChains => "MetricImport",
            PlanArtifactKind.Walls or PlanArtifactKind.WallGraph or PlanArtifactKind.TopologySpans
                or PlanArtifactKind.Openings or PlanArtifactKind.Rooms or PlanArtifactKind.RoomAdjacency => "GeometryImport",
            PlanArtifactKind.ObjectCandidates or PlanArtifactKind.ObjectGroups or PlanArtifactKind.ObjectAggregates
                or PlanArtifactKind.VisualAiClassifications => "SemanticReview",
            PlanArtifactKind.RoutingBarriers or PlanArtifactKind.RoutingPassages or PlanArtifactKind.RoutingObstacles
                or PlanArtifactKind.RoutingRoomUseHints or PlanArtifactKind.RoutingSuppressedObjects
                or PlanArtifactKind.RoutingIgnoredObjects => "RoutingImport",
            PlanArtifactKind.Diagnostics or PlanArtifactKind.LayerConsistency or PlanArtifactKind.Quality => "QualityReview",
            _ => "None"
        };
}

public sealed record StageExport(
    string Stage,
    string DisplayName,
    string Kind,
    int ExecutionWave,
    bool IsParallelCandidate,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> OptionalReads,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> Capabilities,
    bool IsDependencyReady,
    int DependencyLevel,
    int PreferredDependencyLevel,
    IReadOnlyList<string> AvailableBefore,
    IReadOnlyList<string> MissingRequiredReads,
    IReadOnlyList<string> MissingOptionalReads,
    StageRuntimeReadinessExport RuntimeReadiness,
    StageOutputReadinessExport OutputReadiness,
    IReadOnlyList<ArtifactSnapshotExport> InputArtifacts,
    IReadOnlyList<ArtifactSnapshotExport> OutputArtifacts,
    IReadOnlyList<ArtifactChangeExport> ChangedArtifacts,
    IReadOnlyList<ArtifactDeltaExport> ArtifactDeltas,
    StageContractExport Contract,
    double DurationMilliseconds,
    int InputCount,
    int OutputCount,
    int DiagnosticCount,
    int InfoCount,
    int WarningCount,
    int ErrorCount)
{
    public static StageExport From(PipelineStageReport report, PipelineStagePlan? stagePlan = null) =>
        new(
            report.Stage,
            report.Metadata.DisplayName,
            report.Metadata.Kind.ToString(),
            stagePlan?.ExecutionWave ?? 0,
            stagePlan?.IsParallelCandidate ?? false,
            report.Metadata.Reads.Select(artifact => artifact.ToString()).ToArray(),
            report.Metadata.OptionalReads.Select(artifact => artifact.ToString()).ToArray(),
            report.Metadata.Writes.Select(artifact => artifact.ToString()).ToArray(),
            report.Metadata.Capabilities.ToArray(),
            stagePlan?.IsDependencyReady ?? true,
            stagePlan?.DependencyLevel ?? 0,
            stagePlan?.PreferredDependencyLevel ?? 0,
            stagePlan?.AvailableBefore.Select(artifact => artifact.ToString()).ToArray() ?? Array.Empty<string>(),
            stagePlan?.MissingRequiredReads.Select(artifact => artifact.ToString()).ToArray() ?? Array.Empty<string>(),
            stagePlan?.MissingOptionalReads.Select(artifact => artifact.ToString()).ToArray() ?? Array.Empty<string>(),
            StageRuntimeReadinessExport.From(report.RuntimeReadiness),
            StageOutputReadinessExport.From(report.OutputReadiness),
            report.InputArtifacts.Select(ArtifactSnapshotExport.From).ToArray(),
            report.OutputArtifacts.Select(ArtifactSnapshotExport.From).ToArray(),
            report.ChangedArtifacts.Select(ArtifactChangeExport.From).ToArray(),
            report.ArtifactDeltas.Select(ArtifactDeltaExport.From).ToArray(),
            StageContractExport.From(report.Contract),
            report.Duration.TotalMilliseconds,
            report.InputCount,
            report.OutputCount,
            report.DiagnosticCount,
            report.InfoCount,
            report.WarningCount,
            report.ErrorCount);
}

public sealed record StageOutputReadinessExport(
    bool DeclaredOutputsHaveData,
    bool HasEmptyDeclaredOutputs,
    IReadOnlyList<string> NonEmptyDeclaredOutputs,
    IReadOnlyList<string> EmptyDeclaredOutputs,
    IReadOnlyList<string> ChangedDeclaredOutputs,
    IReadOnlyList<string> UnchangedDeclaredOutputs,
    bool HasUndeclaredChanges,
    IReadOnlyList<string> UndeclaredChangedArtifacts,
    IReadOnlyList<string> Evidence)
{
    public static StageOutputReadinessExport From(PipelineStageOutputReadiness readiness) =>
        new(
            readiness.DeclaredOutputsHaveData,
            readiness.HasEmptyDeclaredOutputs,
            readiness.NonEmptyDeclaredOutputs.Select(artifact => artifact.ToString()).ToArray(),
            readiness.EmptyDeclaredOutputs.Select(artifact => artifact.ToString()).ToArray(),
            readiness.ChangedDeclaredOutputs.Select(artifact => artifact.ToString()).ToArray(),
            readiness.UnchangedDeclaredOutputs.Select(artifact => artifact.ToString()).ToArray(),
            readiness.HasUndeclaredChanges,
            readiness.UndeclaredChangedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            readiness.Evidence);
}

public sealed record StageRuntimeReadinessExport(
    bool RequiredReadsHaveData,
    bool HasEmptyRequiredReads,
    IReadOnlyList<string> NonEmptyRequiredReads,
    IReadOnlyList<string> EmptyRequiredReads,
    bool OptionalReadsHaveData,
    bool HasEmptyOptionalReads,
    IReadOnlyList<string> NonEmptyOptionalReads,
    IReadOnlyList<string> EmptyOptionalReads,
    IReadOnlyList<string> Evidence)
{
    public static StageRuntimeReadinessExport From(PipelineStageRuntimeReadiness readiness) =>
        new(
            readiness.RequiredReadsHaveData,
            readiness.HasEmptyRequiredReads,
            readiness.NonEmptyRequiredReads.Select(artifact => artifact.ToString()).ToArray(),
            readiness.EmptyRequiredReads.Select(artifact => artifact.ToString()).ToArray(),
            readiness.OptionalReadsHaveData,
            readiness.HasEmptyOptionalReads,
            readiness.NonEmptyOptionalReads.Select(artifact => artifact.ToString()).ToArray(),
            readiness.EmptyOptionalReads.Select(artifact => artifact.ToString()).ToArray(),
            readiness.Evidence);
}

public sealed record StageContractExport(
    bool WritesOnlyDeclaredArtifacts,
    IReadOnlyList<string> DeclaredWrites,
    IReadOnlyList<string> ChangedArtifacts,
    IReadOnlyList<string> UndeclaredChangedArtifacts,
    IReadOnlyList<string> DeclaredUnchangedArtifacts)
{
    public static StageContractExport From(PipelineStageContract contract) =>
        new(
            contract.WritesOnlyDeclaredArtifacts,
            contract.DeclaredWrites.Select(artifact => artifact.ToString()).ToArray(),
            contract.ChangedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            contract.UndeclaredChangedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            contract.DeclaredUnchangedArtifacts.Select(artifact => artifact.ToString()).ToArray());
}

public sealed record ExecutionWaveExport(
    int Level,
    int StageCount,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> WriteConflictArtifacts,
    IReadOnlyList<PipelineWaveDependencyExport> IntraWaveDependencies,
    IReadOnlyList<string> DirectDownstreamStages,
    int DirectDownstreamStageCount,
    IReadOnlyList<string> DownstreamReadArtifacts,
    bool HasWriteConflicts,
    bool HasIntraWaveDependencies,
    bool IsParallelCandidate,
    string ParallelReadiness,
    IReadOnlyList<string> SchedulingReasons,
    string RecommendedExecutionMode)
{
    public static ExecutionWaveExport From(PipelineExecutionWave wave) =>
        new(
            wave.Level,
            wave.StageCount,
            wave.Stages,
            wave.Reads.Select(artifact => artifact.ToString()).ToArray(),
            wave.Writes.Select(artifact => artifact.ToString()).ToArray(),
            wave.WriteConflictArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            wave.IntraWaveDependencies.Select(PipelineWaveDependencyExport.From).ToArray(),
            wave.DirectDownstreamStages,
            wave.DirectDownstreamStageCount,
            wave.DownstreamReadArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            wave.HasWriteConflicts,
            wave.HasIntraWaveDependencies,
            wave.IsParallelCandidate,
            wave.ParallelReadiness,
            wave.SchedulingReasons,
            wave.RecommendedExecutionMode);
}

public sealed record RerunImpactExport(
    string Artifact,
    bool IsSourceArtifact,
    string ImpactScope,
    IReadOnlyList<string> ProducerStages,
    IReadOnlyList<string> DirectConsumerStages,
    IReadOnlyList<string> AffectedStages,
    IReadOnlyList<string> AffectedArtifacts,
    int FirstAffectedWave,
    int AffectedStageCount,
    bool HasImpact,
    IReadOnlyList<string> Evidence)
{
    public static RerunImpactExport From(PipelineRerunImpact impact) =>
        new(
            impact.Artifact.ToString(),
            impact.IsSourceArtifact,
            impact.ImpactScope,
            impact.ProducerStages,
            impact.DirectConsumerStages,
            impact.AffectedStages,
            impact.AffectedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            impact.FirstAffectedWave,
            impact.AffectedStageCount,
            impact.HasImpact,
            impact.Evidence);
}

public sealed record RerunPlanExport(
    string PlanId,
    string DisplayName,
    IReadOnlyList<string> ChangedArtifacts,
    IReadOnlyList<string> ChangedSourceArtifacts,
    IReadOnlyList<string> DirectConsumerStages,
    IReadOnlyList<string> RerunStages,
    IReadOnlyList<int> RerunWaves,
    IReadOnlyList<string> AffectedArtifacts,
    int FirstRerunWave,
    int LastRerunWave,
    int RerunStageCount,
    int AffectedArtifactCount,
    bool HasWork,
    string RecommendedExecutionMode,
    IReadOnlyList<string> Evidence)
{
    public static RerunPlanExport From(PipelineRerunPlan plan) =>
        new(
            plan.PlanId,
            plan.DisplayName,
            plan.ChangedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.ChangedSourceArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.DirectConsumerStages,
            plan.RerunStages,
            plan.RerunWaves,
            plan.AffectedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.FirstRerunWave,
            plan.LastRerunWave,
            plan.RerunStageCount,
            plan.AffectedArtifactCount,
            plan.HasWork,
            plan.RecommendedExecutionMode,
            plan.Evidence);
}

public sealed record PipelineWaveDependencyExport(
    string Stage,
    string DependsOnStage,
    IReadOnlyList<string> Artifacts)
{
    public static PipelineWaveDependencyExport From(PipelineWaveDependency dependency) =>
        new(
            dependency.Stage,
            dependency.DependsOnStage,
            dependency.Artifacts.Select(artifact => artifact.ToString()).ToArray());
}

public sealed record ArtifactSnapshotExport(
    string Artifact,
    int Count,
    string StateKey,
    int Revision,
    IReadOnlyList<string> Evidence)
{
    public static ArtifactSnapshotExport From(PipelineArtifactSnapshot snapshot) =>
        new(
            snapshot.Artifact.ToString(),
            snapshot.Count,
            snapshot.StateKey,
            snapshot.Revision,
            snapshot.Evidence);
}

public sealed record ArtifactChangeExport(
    string Artifact,
    int BeforeCount,
    int AfterCount,
    int Delta,
    string ChangeKind)
{
    public static ArtifactChangeExport From(PipelineArtifactChange change) =>
        new(
            change.Artifact.ToString(),
            change.BeforeCount,
            change.AfterCount,
            change.Delta,
            change.ChangeKind.ToString());
}

public sealed record ArtifactDeltaExport(
    string Artifact,
    int BeforeCount,
    int AfterCount,
    int Delta,
    string ChangeKind,
    bool Changed,
    bool WasPresent,
    bool IsPresent,
    bool IsDeclaredWrite,
    bool IsEmptyDeclaredOutput,
    IReadOnlyList<string> Evidence)
{
    public static ArtifactDeltaExport From(PipelineArtifactDelta delta) =>
        new(
            delta.Artifact.ToString(),
            delta.BeforeCount,
            delta.AfterCount,
            delta.Delta,
            delta.ChangeKind.ToString(),
            delta.Changed,
            delta.WasPresent,
            delta.IsPresent,
            delta.IsDeclaredWrite,
            delta.IsEmptyDeclaredOutput,
            EvidenceFor(delta));

    private static IReadOnlyList<string> EvidenceFor(PipelineArtifactDelta delta)
    {
        var evidence = new List<string>
        {
            delta.IsDeclaredWrite ? "artifact is declared as a stage output" : "artifact changed outside declared stage outputs",
            $"before count {delta.BeforeCount}, after count {delta.AfterCount}"
        };

        if (delta.IsEmptyDeclaredOutput)
        {
            evidence.Add("declared output count is zero after stage completion");
        }
        else if (!delta.Changed)
        {
            evidence.Add("artifact count did not change during this stage");
        }
        else
        {
            evidence.Add($"artifact lifecycle status: {delta.ChangeKind}");
        }

        return evidence;
    }
}

public sealed record PipelinePlanIssueExport(
    string Code,
    string Severity,
    string Stage,
    string Message,
    IReadOnlyList<string> Artifacts)
{
    public static PipelinePlanIssueExport From(PipelinePlanIssue issue) =>
        new(
            issue.Code,
            issue.Severity.ToString(),
            issue.Stage,
            issue.Message,
            issue.Artifacts.Select(artifact => artifact.ToString()).ToArray());
}

public sealed record DiagnosticExport(
    string Code,
    string Severity,
    string Stage,
    string Scope,
    string Message,
    int? PageNumber,
    RectExport? Region,
    double? Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyDictionary<string, string> Properties)
{
    public static DiagnosticExport From(PlanDiagnostic diagnostic) =>
        new(
            diagnostic.Code,
            diagnostic.Severity.ToString(),
            diagnostic.Stage,
            diagnostic.Scope.ToString(),
            diagnostic.Message,
            diagnostic.PageNumber,
            diagnostic.Region is null ? null : RectExport.From(diagnostic.Region.Value),
            diagnostic.Confidence?.Value,
            diagnostic.SourcePrimitiveIds,
            diagnostic.Properties);
}

public sealed record RectExport(double X, double Y, double Width, double Height)
{
    public static RectExport From(PlanRect rect) =>
        new(rect.X, rect.Y, rect.Width, rect.Height);
}

public sealed record PointExport(double X, double Y)
{
    public static PointExport From(PlanPoint point) => new(point.X, point.Y);
}

public sealed record VectorExport(double X, double Y)
{
    public static VectorExport From(PlanVector vector) => new(vector.X, vector.Y);
}

public sealed record LineExport(PointExport Start, PointExport End)
{
    public static LineExport From(PlanLineSegment line) =>
        new(PointExport.From(line.Start), PointExport.From(line.End));
}

internal static class ExportSourceHelpers
{
    public static IReadOnlyList<string> SourceLayers(
        IReadOnlyList<string> sourcePrimitiveIds,
        IReadOnlyDictionary<string, PrimitiveSourceExport> sourceLookup) =>
        sourcePrimitiveIds
            .Select(sourceId => sourceLookup.TryGetValue(sourceId, out var source) ? source.Metadata.Layer : null)
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(layer => layer!)
            .ToArray();
}
