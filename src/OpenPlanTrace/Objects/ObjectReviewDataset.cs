namespace OpenPlanTrace;

public sealed record ObjectReviewDataset(
    string SchemaVersion,
    string? Name,
    string? Version,
    DateTimeOffset GeneratedAt,
    string DocumentId,
    string? SourceName,
    string? SourcePath,
    IReadOnlyList<ObjectReviewGroup> Groups,
    IReadOnlyList<ObjectReviewCandidate> UngroupedCandidates)
{
    public const string CurrentSchemaVersion = "openplantrace.object-review-dataset.v2";
}

public sealed record ObjectReviewGroup(
    string GroupId,
    string Signature,
    ObjectCandidateKind Kind,
    ObjectCategory Category,
    int Count,
    PlanRect RepresentativeBounds,
    PlanRect ReviewCropBounds,
    IReadOnlyList<int> PageNumbers,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    bool RequiresReview,
    double Confidence,
    string? Label,
    string? SymbolName,
    IReadOnlyList<string> DetectedTags,
    ObjectReviewRuleSuggestion SuggestedRule,
    IReadOnlyList<ObjectReviewCandidate> Candidates,
    IReadOnlyList<ObjectReviewTextEvidence> NearbyText,
    IReadOnlyList<string> Evidence);

public sealed record ObjectReviewCandidate(
    string CandidateId,
    string? GroupId,
    int PageNumber,
    ObjectCandidateKind Kind,
    ObjectCategory Category,
    ObjectCandidateSourceKind SourceKind,
    string? SourceWallComponentId,
    WallGraphComponentKind? SourceWallComponentKind,
    PlanRect Bounds,
    PlanRect ReviewCropBounds,
    double Confidence,
    string? Label,
    string? SymbolName,
    string? DetectedTag,
    string? DetectedTagSourcePrimitiveId,
    string? RoomId,
    string? RoomLabel,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<ObjectReviewTextEvidence> NearbyText,
    IReadOnlyList<string> Evidence);

public sealed record ObjectReviewTextEvidence(
    string Text,
    int PageNumber,
    PlanRect Bounds,
    string SourcePrimitiveId,
    double Distance);

public sealed record ObjectReviewRuleSuggestion(
    string? Signature,
    string? SymbolNamePattern,
    string? LabelPattern,
    string? LayerPattern,
    string? SourceFormat,
    ObjectCategory? MatchCategory,
    ObjectCandidateKind? MatchKind,
    ObjectCategory? Category,
    ObjectCandidateKind? Kind,
    string? Label,
    string? SymbolName,
    bool? RequiresReview,
    double? Confidence,
    IReadOnlyList<string> Evidence);
