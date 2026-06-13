namespace OpenPlanTrace;

public sealed record PlanAnnotationBlock(
    string Id,
    int PageNumber,
    PlanAnnotationKind Kind,
    string? Label,
    PlanRect Bounds,
    Confidence Confidence,
    string? SourceRegionId,
    IReadOnlyList<PlanAnnotationItem> Items,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
