namespace OpenPlanTrace;

public sealed record BenchmarkDetectionSummary(
    string DetectionId,
    int? PageNumber,
    PlanRect? Bounds,
    string? Label,
    string? Text,
    string? Marker,
    string? Category,
    string? Kind,
    string? LayerCategory,
    string? RoutingSourceKind,
    string? RoutingObstacleKind,
    string? RoutingInfluence,
    string? StructuralInfluence,
    string? RoomUseKind,
    int? Count,
    bool? RequiresReview,
    bool? SuppressesChildObjects,
    IReadOnlyList<string> DetectedTags,
    string Evidence);
