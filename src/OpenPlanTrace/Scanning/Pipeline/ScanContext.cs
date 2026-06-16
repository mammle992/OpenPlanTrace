using System.Globalization;

namespace OpenPlanTrace;

internal sealed class ScanContext
{
    public ScanContext(PlanDocument document, ScannerOptions options)
    {
        Document = document;
        Options = options;
    }

    public PlanDocument Document { get; }

    public ScannerOptions Options { get; }

    public PipelineDiagnosticsBuilder Diagnostics { get; } = new();

    public PlanLayerAnalysis LayerAnalysis { get; set; } = PlanLayerAnalysis.Empty;

    public PlanCalibration Calibration { get; set; } = PlanCalibration.Empty;

    public MeasurementConsistencyReport MeasurementConsistency { get; set; } = MeasurementConsistencyReport.Empty;

    public List<TitleBlockAnalysis> TitleBlocks { get; } = new();

    public List<DimensionAnnotation> Dimensions { get; } = new();

    public List<PlanAnnotationBlock> Annotations { get; } = new();

    public List<GridAxis> GridAxes { get; } = new();

    public List<GridBaySpacing> GridBaySpacings { get; } = new();

    public List<SheetRegion> SheetRegions { get; } = new();

    public List<SurfacePatternCandidate> SurfacePatterns { get; } = new();

    public List<WallSegment> WallCandidates { get; } = new();

    public List<WallSegment> Walls { get; } = new();

    public WallEvidenceMap WallEvidenceMap { get; set; } = WallEvidenceMap.Empty;

    public WallTopologyPreparation WallTopologyPreparation { get; set; } = WallTopologyPreparation.Empty;

    public WallGraph WallGraph { get; set; } = WallGraph.Empty;

    public List<RoomRegion> Rooms { get; } = new();

    public RoomAdjacencyGraph RoomAdjacencyGraph { get; set; } = RoomAdjacencyGraph.Empty;

    public List<OpeningCandidate> Openings { get; } = new();

    public List<ObjectCandidate> ObjectCandidates { get; } = new();

    public List<ObjectCandidateGroup> ObjectGroups { get; } = new();

    public List<ObjectAggregate> ObjectAggregates { get; } = new();

    public PlanRoutingLayer RoutingLayer { get; set; } = PlanRoutingLayer.Empty;

    public bool HasRoutingLayer { get; set; }

    public int TotalDetectionCount =>
        LayerAnalysis.Layers.Count
        + Calibration.Evidence.Count
        + MeasurementConsistency.Checks.Count
        + TitleBlocks.Count
        + TitleBlocks.Sum(titleBlock => titleBlock.Fields.Count)
        + Dimensions.Count
        + Annotations.Count
        + Annotations.Sum(annotation => annotation.Items.Count)
        + Annotations.Sum(annotation => annotation.Items.Sum(item => item.References.Count))
        + GridAxes.Count
        + GridBaySpacings.Count
        + SheetRegions.Count
        + SurfacePatterns.Count
        + WallCandidates.Count
        + Walls.Count
        + WallEvidenceMap.Segments.Count
        + WallEvidenceMap.Bands.Count
        + WallEvidenceMap.WallAssessments.Count
        + WallTopologyPreparation.GraphWallCount
        + WallTopologyPreparation.RejectedWallCount
        + WallGraph.Nodes.Count
        + WallGraph.Edges.Count
        + WallGraph.Components.Count
        + Rooms.Count
        + RoomAdjacencyGraph.Edges.Count
        + RoomAdjacencyGraph.Clusters.Count
        + Openings.Count
        + ObjectCandidates.Count
        + ObjectCandidates.Count(candidate => candidate.VisualAi is not null)
        + ObjectGroups.Count
        + ObjectGroups.Count(group => group.VisualAi is not null)
        + ObjectAggregates.Count
        + RoutingItemCount(RoutingLayer)
        + RoutingLayer.SuppressedObjects.Count
        + RoutingLayer.IgnoredObjects.Count;

    public IReadOnlyList<PipelineArtifactSnapshot> SnapshotArtifacts(IEnumerable<PlanArtifactKind> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        var counts = ArtifactCounts();
        return artifacts
            .Where(artifact => artifact != PlanArtifactKind.Unknown)
            .Distinct()
            .OrderBy(artifact => artifact.ToString(), StringComparer.Ordinal)
            .Select(artifact => new PipelineArtifactSnapshot(
                artifact,
                counts.TryGetValue(artifact, out var count) ? count : 0))
            .ToArray();
    }

    public IReadOnlyDictionary<PlanArtifactKind, int> ArtifactCounts() =>
        new Dictionary<PlanArtifactKind, int>
        {
            [PlanArtifactKind.Document] = 1,
            [PlanArtifactKind.Pages] = Document.Pages.Count,
            [PlanArtifactKind.Primitives] = Document.Pages.Sum(page => page.Primitives.Count),
            [PlanArtifactKind.Layers] = LayerAnalysis.Layers.Count,
            [PlanArtifactKind.RasterImages] = MetadataCount("raster.sourceImageIdCount")
                ?? MetadataCount("raster.pageCount")
                ?? (IsFormat("raster") ? Document.Pages.Count : 0),
            [PlanArtifactKind.PdfImages] = MetadataCount("pdf.imageCount") ?? 0,
            [PlanArtifactKind.SheetRegions] = SheetRegions.Count,
            [PlanArtifactKind.TitleBlocks] = TitleBlocks.Count,
            [PlanArtifactKind.Calibration] = Calibration.Evidence.Count + Calibration.ScaleGroups.Count,
            [PlanArtifactKind.Dimensions] = Dimensions.Count,
            [PlanArtifactKind.Annotations] = Annotations.Count + Annotations.Sum(annotation => annotation.Items.Count),
            [PlanArtifactKind.GridAxes] = GridAxes.Count,
            [PlanArtifactKind.GridBays] = GridBaySpacings.Count,
            [PlanArtifactKind.MeasurementConsistency] = MeasurementConsistency.Checks.Count,
            [PlanArtifactKind.DimensionChains] = Diagnostics.MessagesSince(0)
                .Count(message => string.Equals(message.Stage, "dimension-chains", StringComparison.Ordinal)),
            [PlanArtifactKind.SurfacePatterns] = SurfacePatterns.Count,
            [PlanArtifactKind.WallCandidates] = WallCandidates.Count,
            [PlanArtifactKind.Walls] = Walls.Count,
            [PlanArtifactKind.WallEvidence] =
                WallEvidenceMap.Segments.Count
                + WallEvidenceMap.Bands.Count
                + WallEvidenceMap.WallAssessments.Count,
            [PlanArtifactKind.WallTopologyPreparation] =
                WallTopologyPreparation.GraphWallCount
                + WallTopologyPreparation.RejectedWallCount,
            [PlanArtifactKind.WallGraph] =
                WallGraph.Nodes.Count
                + WallGraph.Edges.Count
                + WallGraph.Components.Count
                + WallGraph.RepairCandidates.Count,
            [PlanArtifactKind.TopologySpans] = WallGraph.Edges.Count,
            [PlanArtifactKind.Openings] = Openings.Count,
            [PlanArtifactKind.Rooms] = Rooms.Count,
            [PlanArtifactKind.RoomAdjacency] = RoomAdjacencyGraph.Edges.Count + RoomAdjacencyGraph.Clusters.Count,
            [PlanArtifactKind.ObjectCandidates] = ObjectCandidates.Count,
            [PlanArtifactKind.ObjectGroups] = ObjectGroups.Count,
            [PlanArtifactKind.ObjectAggregates] = ObjectAggregates.Count,
            [PlanArtifactKind.RoutingBarriers] = RoutingLayer.Barriers.Count,
            [PlanArtifactKind.RoutingPassages] = RoutingLayer.Passages.Count,
            [PlanArtifactKind.RoutingObstacles] = RoutingLayer.Obstacles.Count,
            [PlanArtifactKind.RoutingRoomUseHints] = RoutingLayer.RoomUseHints.Count,
            [PlanArtifactKind.RoutingSuppressedObjects] = RoutingLayer.SuppressedObjects.Count,
            [PlanArtifactKind.RoutingIgnoredObjects] = RoutingLayer.IgnoredObjects.Count,
            [PlanArtifactKind.VisualAiClassifications] =
                ObjectCandidates.Count(candidate => candidate.VisualAi is not null)
                + ObjectGroups.Count(group => group.VisualAi is not null),
            [PlanArtifactKind.LayerConsistency] = Diagnostics.MessagesSince(0)
                .Count(message => string.Equals(message.Stage, "layer-consistency", StringComparison.Ordinal)),
            [PlanArtifactKind.Diagnostics] = Diagnostics.MessageCount,
            [PlanArtifactKind.Quality] = 0
        };

    public static IReadOnlyList<PipelineArtifactChange> ChangedArtifacts(
        IReadOnlyDictionary<PlanArtifactKind, int> before,
        IReadOnlyDictionary<PlanArtifactKind, int> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        return before.Keys
            .Concat(after.Keys)
            .Where(artifact => artifact != PlanArtifactKind.Unknown)
            .Distinct()
            .OrderBy(artifact => artifact.ToString(), StringComparer.Ordinal)
            .Select(artifact => new PipelineArtifactChange(
                artifact,
                before.TryGetValue(artifact, out var beforeCount) ? beforeCount : 0,
                after.TryGetValue(artifact, out var afterCount) ? afterCount : 0))
            .Where(change => change.Delta != 0)
            .ToArray();
    }

    public static IReadOnlyList<PipelineArtifactDelta> ArtifactDeltas(
        IReadOnlyDictionary<PlanArtifactKind, int> before,
        IReadOnlyDictionary<PlanArtifactKind, int> after,
        IEnumerable<PlanArtifactKind> declaredWrites,
        IEnumerable<PlanArtifactKind> changedArtifacts) =>
        PipelineArtifactDelta.FromCounts(before, after, declaredWrites, changedArtifacts);

    public string PrimitiveId(int pageNumber, int primitiveIndex, PlanPrimitive primitive) =>
        primitive.SourceId ?? primitive.Source.SourceId ?? $"p{pageNumber}:primitive:{primitiveIndex}";

    public void AddDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string stage,
        string message,
        int? pageNumber = null,
        PlanRect? region = null,
        Confidence? confidence = null,
        DiagnosticScope scope = DiagnosticScope.Document,
        IEnumerable<string>? sourcePrimitiveIds = null,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        Diagnostics.Add(
            new PlanDiagnostic(code, severity, stage, message)
            {
                Scope = scope,
                PageNumber = pageNumber,
                Region = region,
                Confidence = confidence,
                SourcePrimitiveIds = sourcePrimitiveIds?
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                    ?? Array.Empty<string>(),
                Properties = properties ?? new Dictionary<string, string>()
            });
    }

    public PlanScanResult ToResult()
    {
        var result = new PlanScanResult(
            Document,
            LayerAnalysis,
            Calibration,
            MeasurementConsistency,
            TitleBlocks.ToArray(),
            Dimensions.ToArray(),
            Annotations.ToArray(),
            GridAxes.ToArray(),
            GridBaySpacings.ToArray(),
            SheetRegions.ToArray(),
            SurfacePatterns.ToArray(),
            Walls.ToArray(),
            WallGraph,
            Rooms.ToArray(),
            RoomAdjacencyGraph,
            Openings.ToArray(),
            ObjectCandidates.ToArray(),
            ObjectGroups.ToArray(),
            ObjectAggregates.ToArray(),
            Diagnostics.Build());

        result = result with
        {
            WallEvidenceMap = WallEvidenceMap,
            WallTopologyPreparation = WallTopologyPreparation
        };

        if (HasRoutingLayer)
        {
            result = result with { RoutingLayerSnapshot = RoutingLayer };
        }

        return result with { Quality = PlanScanQualityAnalyzer.Analyze(result) };
    }

    public PlanScanResult ToRoutingSourceResult()
    {
        var result = new PlanScanResult(
            Document,
            LayerAnalysis,
            Calibration,
            MeasurementConsistency,
            TitleBlocks.ToArray(),
            Dimensions.ToArray(),
            Annotations.ToArray(),
            GridAxes.ToArray(),
            GridBaySpacings.ToArray(),
            SheetRegions.ToArray(),
            SurfacePatterns.ToArray(),
            Walls.ToArray(),
            WallGraph,
            Rooms.ToArray(),
            RoomAdjacencyGraph,
            Openings.ToArray(),
            ObjectCandidates.ToArray(),
            ObjectGroups.ToArray(),
            ObjectAggregates.ToArray(),
            Diagnostics.Build());

        return result with
        {
            WallEvidenceMap = WallEvidenceMap,
            WallTopologyPreparation = WallTopologyPreparation
        };
    }

    private bool IsFormat(string format) =>
        Document.Metadata.Properties.TryGetValue("format", out var value)
        && string.Equals(value, format, StringComparison.OrdinalIgnoreCase);

    private int? MetadataCount(string key)
    {
        foreach (var property in Document.Metadata.Properties)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(property.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 0)
            {
                return parsed;
            }
        }

        return null;
    }

    private static int RoutingItemCount(PlanRoutingLayer routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count;
}
