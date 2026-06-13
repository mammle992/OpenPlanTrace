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

    public List<WallSegment> Walls { get; } = new();

    public WallGraph WallGraph { get; set; } = WallGraph.Empty;

    public List<RoomRegion> Rooms { get; } = new();

    public RoomAdjacencyGraph RoomAdjacencyGraph { get; set; } = RoomAdjacencyGraph.Empty;

    public List<OpeningCandidate> Openings { get; } = new();

    public List<ObjectCandidate> ObjectCandidates { get; } = new();

    public List<ObjectCandidateGroup> ObjectGroups { get; } = new();

    public List<ObjectAggregate> ObjectAggregates { get; } = new();

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
        + Walls.Count
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
        + ObjectAggregates.Count;

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

        return result with { Quality = PlanScanQualityAnalyzer.Analyze(result) };
    }
}
