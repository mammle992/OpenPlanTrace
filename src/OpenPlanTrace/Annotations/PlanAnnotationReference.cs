namespace OpenPlanTrace;

public sealed record PlanAnnotationReference(
    string Id,
    string Marker,
    string Text,
    PlanRect Bounds,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
