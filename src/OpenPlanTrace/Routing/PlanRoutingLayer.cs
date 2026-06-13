namespace OpenPlanTrace;

public sealed record PlanRoutingLayer(
    IReadOnlyList<RoutingBarrier> Barriers,
    IReadOnlyList<RoutingPassage> Passages,
    IReadOnlyList<RoutingObstacle> Obstacles,
    IReadOnlyList<RoutingRoomUseHint> RoomUseHints,
    IReadOnlyList<RoutingSuppressedObject> SuppressedObjects,
    IReadOnlyList<RoutingIgnoredObject> IgnoredObjects,
    IReadOnlyList<string> SuppressedObjectCandidateIds,
    IReadOnlyList<string> IgnoredObjectCandidateIds,
    IReadOnlyList<string> Evidence)
{
    public static PlanRoutingLayer Empty { get; } =
        new(
            Array.Empty<RoutingBarrier>(),
            Array.Empty<RoutingPassage>(),
            Array.Empty<RoutingObstacle>(),
            Array.Empty<RoutingRoomUseHint>(),
            Array.Empty<RoutingSuppressedObject>(),
            Array.Empty<RoutingIgnoredObject>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());
}

public enum RoutingSourceKind
{
    Unknown = 0,
    Wall,
    Opening,
    Room,
    ObjectCandidate,
    ObjectAggregate
}

public enum RoutingObstacleKind
{
    Unknown = 0,
    SoftObstacle,
    HardObstacle,
    StructuralBarrier
}

public enum RoutingSuppressionReason
{
    Unknown = 0,
    ReplacedByObjectAggregate,
    AggregateRoomUseEvidenceOnly
}

public enum RoutingSuppressedObjectAction
{
    Unknown = 0,
    IgnoreForRouting,
    UseAggregateObstacle,
    UseAggregateRoomUseHint
}

public enum RoutingIgnoredObjectReason
{
    Unknown = 0,
    SuppressedByAggregate,
    ExplicitlyIgnored,
    RoomUseEvidenceOnly,
    UnclassifiedReviewCandidate,
    UnknownRoutingInfluence
}

public sealed record RoutingBarrier(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    PlanLineSegment CenterLine,
    PlanRect Bounds,
    double Thickness,
    double DrawingLength,
    double? LengthMeters,
    double? ThicknessMillimeters,
    string? MeasurementScaleGroupId,
    string? WallComponentId,
    WallGraphComponentKind? WallComponentKind,
    bool ExcludedFromStructuralTopology,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record RoutingPassage(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    OpeningType Type,
    OpeningOperation Operation,
    OpeningOrientation Orientation,
    PlanLineSegment CenterLine,
    PlanRect Bounds,
    double DrawingWidth,
    double? WidthMillimeters,
    string? MeasurementScaleGroupId,
    IReadOnlyList<string> HostWallIds,
    IReadOnlyList<string> ConnectedRoomIds,
    IReadOnlyList<string> ConnectedRoomLabels,
    OpeningPlacement? Placement,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record RoutingObstacle(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    RoutingObstacleKind ObstacleKind,
    ObjectRoutingInfluence RoutingInfluence,
    ObjectStructuralInfluence StructuralInfluence,
    ObjectCategory Category,
    ObjectCandidateKind ObjectKind,
    PlanRect Bounds,
    string? Label,
    string? RoomId,
    string? RoomLabel,
    bool SuppressesChildObjects,
    IReadOnlyList<string> ChildObjectIds,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record RoutingRoomUseHint(
    string Id,
    int PageNumber,
    string SourceId,
    RoutingSourceKind SourceKind,
    RoomUseKind RoomUseKind,
    PlanRect Bounds,
    string? RoomId,
    string? RoomLabel,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record RoutingSuppressedObject(
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
    PlanRect CandidateBounds,
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record RoutingIgnoredObject(
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
    PlanRect CandidateBounds,
    string? CandidateLabel,
    string? RoomId,
    string? RoomLabel,
    string? SuppressedObjectId,
    string? SuppressedByAggregateId,
    string? RoomUseHintId,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
