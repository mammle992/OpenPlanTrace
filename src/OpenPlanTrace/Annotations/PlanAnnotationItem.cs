namespace OpenPlanTrace;

public sealed record PlanAnnotationItem(
    string Id,
    int PageNumber,
    PlanAnnotationItemKind Kind,
    string Text,
    string? Marker,
    PlanRect Bounds,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<PlanAnnotationReference> References,
    IReadOnlyList<string> Evidence);
