namespace OpenPlanTrace;

public sealed record ObjectCandidateGroup(
    string Id,
    string Signature,
    ObjectCandidateKind Kind,
    ObjectCategory Category,
    int Count,
    PlanRect RepresentativeBounds,
    IReadOnlyList<int> PageNumbers,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    bool RequiresReview,
    Confidence Confidence,
    IReadOnlyList<string> Evidence)
{
    public string? Label { get; init; }

    public string? SymbolName { get; init; }

    public IReadOnlyList<string> DetectedTags { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ObjectNearbyText> NearbyText { get; init; } = Array.Empty<ObjectNearbyText>();

    public VisualAiClassification? VisualAi { get; init; }
}
