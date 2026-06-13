using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public sealed record BenchmarkDetectionTarget
{
    public string? Id { get; init; }

    public int? PageNumber { get; init; }

    public PlanRect? Bounds { get; init; }

    public double? MinIntersectionOverUnion { get; init; }

    public double? MaxCenterDistance { get; init; }

    public string? Label { get; init; }

    public string? Text { get; init; }

    public string? Marker { get; init; }

    public int? MinCount { get; init; }

    public bool? RequiresReview { get; init; }

    public RegionKind? RegionKind { get; init; }

    public DimensionKind? DimensionKind { get; init; }

    public DimensionOrientation? DimensionOrientation { get; init; }

    public PlanAnnotationKind? AnnotationKind { get; init; }

    public GridAxisOrientation? GridAxisOrientation { get; init; }

    public OpeningType? OpeningType { get; init; }

    public OpeningOperation? OpeningOperation { get; init; }

    public ObjectCategory? ObjectCategory { get; init; }

    public ObjectCandidateKind? ObjectKind { get; init; }

    public LayerCategory? LayerCategory { get; init; }

    public RoutingSourceKind? RoutingSourceKind { get; init; }

    public RoutingObstacleKind? RoutingObstacleKind { get; init; }

    public ObjectRoutingInfluence? RoutingInfluence { get; init; }

    public ObjectStructuralInfluence? StructuralInfluence { get; init; }

    public RoomUseKind? RoomUseKind { get; init; }

    public bool? SuppressesChildObjects { get; init; }

    public string? ObjectCandidateId { get; init; }

    public string? SuppressedByAggregateId { get; init; }

    public RoutingSuppressionReason? SuppressionReason { get; init; }

    public RoutingSuppressedObjectAction? SuppressionAction { get; init; }

    public string? ReplacementRoutingObstacleId { get; init; }

    public string? RoomUseHintId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DetectedTags { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SourcePrimitiveIds { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SourceLayers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Evidence { get; init; }
}
