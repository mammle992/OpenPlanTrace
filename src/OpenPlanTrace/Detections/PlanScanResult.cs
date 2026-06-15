namespace OpenPlanTrace;

public sealed record PlanScanResult(
    PlanDocument Document,
    PlanLayerAnalysis LayerAnalysis,
    PlanCalibration Calibration,
    MeasurementConsistencyReport MeasurementConsistency,
    IReadOnlyList<TitleBlockAnalysis> TitleBlocks,
    IReadOnlyList<DimensionAnnotation> Dimensions,
    IReadOnlyList<PlanAnnotationBlock> Annotations,
    IReadOnlyList<GridAxis> GridAxes,
    IReadOnlyList<GridBaySpacing> GridBaySpacings,
    IReadOnlyList<SheetRegion> SheetRegions,
    IReadOnlyList<SurfacePatternCandidate> SurfacePatterns,
    IReadOnlyList<WallSegment> Walls,
    WallGraph WallGraph,
    IReadOnlyList<RoomRegion> Rooms,
    RoomAdjacencyGraph RoomAdjacencyGraph,
    IReadOnlyList<OpeningCandidate> Openings,
    IReadOnlyList<ObjectCandidate> ObjectCandidates,
    IReadOnlyList<ObjectCandidateGroup> ObjectGroups,
    IReadOnlyList<ObjectAggregate> ObjectAggregates,
    PipelineDiagnostics Diagnostics)
{
    public WallEvidenceMap WallEvidenceMap { get; init; } = WallEvidenceMap.Empty;

    public PlanScanQualityReport Quality { get; init; } = PlanScanQualityReport.Empty;

    public IEnumerable<SheetRegion> MainFloorPlanRegions =>
        SheetRegions.Where(region => region.Kind == RegionKind.MainFloorPlan);

    public IEnumerable<SheetRegion> TitleBlockRegions =>
        SheetRegions.Where(region => region.Kind == RegionKind.TitleBlock);

    public PlanRoutingLayer RoutingLayer =>
        RoutingLayerSnapshot ?? PlanRoutingLayerBuilder.FromScanResult(this);

    internal PlanRoutingLayer? RoutingLayerSnapshot { get; init; }
}
