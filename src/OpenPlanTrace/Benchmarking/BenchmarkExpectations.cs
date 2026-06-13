namespace OpenPlanTrace;

public sealed record BenchmarkExpectations
{
    public int? MinPages { get; init; }

    public int? MinRegions { get; init; }

    public int? MinDimensions { get; init; }

    public int? MinAnnotations { get; init; }

    public int? MinAnnotationReferences { get; init; }

    public int? MinGridAxes { get; init; }

    public int? MinGridBaySpacings { get; init; }

    public int? MinSurfacePatterns { get; init; }

    public int? MinWalls { get; init; }

    public int? MinWallNodes { get; init; }

    public int? MinWallEdges { get; init; }

    public int? MinRooms { get; init; }

    public int? MinRoomAdjacencies { get; init; }

    public int? MinRoomClusters { get; init; }

    public int? MinOpenings { get; init; }

    public int? MinObjects { get; init; }

    public int? MinObjectGroups { get; init; }

    public int? MinObjectAggregates { get; init; }

    public int? MinRoutingItems { get; init; }

    public int? MinRoutingSuppressedObjects { get; init; }

    public int? MaxWalls { get; init; }

    public int? MaxRooms { get; init; }

    public int? MaxRoomAdjacencies { get; init; }

    public int? MaxRoomClusters { get; init; }

    public int? MaxOpenings { get; init; }

    public int? MaxObjects { get; init; }

    public int? MaxObjectGroups { get; init; }

    public int? MaxObjectAggregates { get; init; }

    public int? MaxRoutingItems { get; init; }

    public int? MaxRoutingSuppressedObjects { get; init; }

    public int? MaxDimensions { get; init; }

    public int? MaxAnnotations { get; init; }

    public int? MaxAnnotationReferences { get; init; }

    public int? MaxGridAxes { get; init; }

    public int? MaxGridBaySpacings { get; init; }

    public int? MaxSurfacePatterns { get; init; }

    public int? MaxDiagnosticWarnings { get; init; }

    public int? MaxDiagnosticErrors { get; init; }

    public double? MaxDurationMilliseconds { get; init; }

    public PlanScanQualityGrade? MinQualityGrade { get; init; }

    public double? MinQualityConfidence { get; init; }

    public int? MaxQualityIssues { get; init; }

    public int? MaxScanRiskIssues { get; init; }

    public int? MaxScanReviewQueueItems { get; init; }

    public IReadOnlyDictionary<string, int> MaxScanReviewQueueKindCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> RequiredScanReviewQueueKinds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenScanReviewQueueKinds { get; init; } = Array.Empty<string>();

    public PlanImportReadinessGrade? MinImportReadinessGrade { get; init; }

    public double? MinImportReadinessScore { get; init; }

    public bool? RequireGeometryImportReady { get; init; }

    public bool? RequireMetricImportReady { get; init; }

    public bool? RequireRoutingImportReady { get; init; }

    public bool? AllowImportReview { get; init; }

    public bool? RequiresReliableCalibration { get; init; }

    public int? MinMeasurementCheckedCount { get; init; }

    public int? MinMeasurementConsistentCount { get; init; }

    public int? MaxMeasurementOutlierCount { get; init; }

    public double? MaxMeasurementOutlierRatio { get; init; }

    public double? MaxMeasurementScaleSpreadRatio { get; init; }

    public IReadOnlyList<string> RequiredDiagnosticCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenDiagnosticCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredQualityIssueCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenQualityIssueCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> RequiredImportIssueCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenImportIssueCodes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BenchmarkStageExpectation> StageExpectations { get; init; } = Array.Empty<BenchmarkStageExpectation>();

    public IReadOnlyList<RegionKind> RequiredRegionKinds { get; init; } = Array.Empty<RegionKind>();

    public IReadOnlyList<PlanAnnotationKind> RequiredAnnotationKinds { get; init; } = Array.Empty<PlanAnnotationKind>();

    public IReadOnlyList<string> RequiredGridLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<OpeningType> RequiredOpeningTypes { get; init; } = Array.Empty<OpeningType>();

    public IReadOnlyList<OpeningOperation> RequiredOpeningOperations { get; init; } = Array.Empty<OpeningOperation>();

    public IReadOnlyList<ObjectCategory> RequiredObjectCategories { get; init; } = Array.Empty<ObjectCategory>();

    public IReadOnlyList<string> RequiredRoomLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenRoomLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<LayerCategory> RequiredLayerCategories { get; init; } = Array.Empty<LayerCategory>();

    public BenchmarkDetectorMetricExpectations RegionMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations DimensionMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations AnnotationMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations AnnotationReferenceMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations GridAxisMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations WallMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoomMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations OpeningMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations ObjectMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations ObjectGroupMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations ObjectAggregateMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoutingBarrierMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoutingPassageMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoutingObstacleMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoutingRoomUseHintMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations RoutingSuppressedObjectMetrics { get; init; } = new();

    public BenchmarkDetectorMetricExpectations LayerMetrics { get; init; } = new();
}
