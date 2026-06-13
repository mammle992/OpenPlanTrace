namespace OpenPlanTrace;

public sealed record ObjectAggregate(
    string Id,
    int PageNumber,
    PlanRect Bounds,
    ObjectCategory Category,
    ObjectCandidateKind Kind,
    int ChildObjectCount,
    IReadOnlyList<string> ChildObjectIds,
    IReadOnlyList<string> ObjectGroupIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    ObjectRoutingInfluence RoutingInfluence,
    ObjectStructuralInfluence StructuralInfluence,
    bool SuppressChildObjectsForRouting,
    RoomUseKind RoomUseEvidence,
    Confidence Confidence,
    IReadOnlyList<string> Evidence)
{
    public string? Label { get; init; }

    public string? RoomId { get; init; }

    public string? RoomLabel { get; init; }

    public bool RequiresReview { get; init; }

    public IReadOnlyList<string> NearbyText { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> SourceLayers { get; init; } = Array.Empty<string>();

    public ObjectAggregateComposition Composition { get; init; } = ObjectAggregateComposition.Empty;
}

public sealed record ObjectAggregateComposition(
    IReadOnlyList<ObjectAggregateCompositionCount> CategoryCounts,
    IReadOnlyList<ObjectAggregateCompositionCount> KindCounts,
    IReadOnlyList<ObjectAggregateCompositionCount> SourceKindCounts,
    IReadOnlyList<ObjectAggregateCompositionCount> SourceWallComponentKindCounts,
    IReadOnlyList<string> SourceWallComponentIds,
    IReadOnlyList<ObjectAggregateChildObject> Children)
{
    public static ObjectAggregateComposition Empty { get; } =
        new(
            Array.Empty<ObjectAggregateCompositionCount>(),
            Array.Empty<ObjectAggregateCompositionCount>(),
            Array.Empty<ObjectAggregateCompositionCount>(),
            Array.Empty<ObjectAggregateCompositionCount>(),
            Array.Empty<string>(),
            Array.Empty<ObjectAggregateChildObject>());
}

public sealed record ObjectAggregateCompositionCount(string Value, int Count);

public sealed record ObjectAggregateChildObject(
    string ObjectId,
    PlanRect Bounds,
    ObjectCategory Category,
    ObjectCandidateKind Kind,
    ObjectCandidateSourceKind SourceKind,
    string? SourceWallComponentId,
    WallGraphComponentKind? SourceWallComponentKind,
    string? Label,
    string? SymbolName,
    string? DetectedTag,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds);
