namespace OpenPlanTrace;

public sealed record TitleBlockField(
    TitleBlockFieldKind Kind,
    string Value,
    string RawText,
    int PageNumber,
    PlanRect Bounds,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
