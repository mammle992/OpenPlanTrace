namespace OpenPlanTrace;

public sealed record ObjectCandidate(
    string Id,
    int PageNumber,
    ObjectCandidateKind Kind,
    PlanRect Bounds,
    Confidence Confidence)
{
    public string? Label { get; init; }

    public ObjectCategory Category { get; init; } = ObjectCategory.Unknown;

    public ObjectCandidateSourceKind SourceKind { get; init; } = ObjectCandidateSourceKind.Unknown;

    public string? SourceWallComponentId { get; init; }

    public WallGraphComponentKind? SourceWallComponentKind { get; init; }

    public string? SymbolName { get; init; }

    public string? DetectedTag { get; init; }

    public string? DetectedTagSourcePrimitiveId { get; init; }

    public string? RoomId { get; init; }

    public string? RoomLabel { get; init; }

    public IReadOnlyList<string> SourcePrimitiveIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ObjectNearbyText> NearbyText { get; init; } = Array.Empty<ObjectNearbyText>();

    public VisualAiClassification? VisualAi { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
}
