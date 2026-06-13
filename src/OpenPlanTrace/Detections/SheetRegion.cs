namespace OpenPlanTrace;

public sealed record SheetRegion(
    string Id,
    int PageNumber,
    RegionKind Kind,
    PlanRect Bounds,
    Confidence Confidence)
{
    public string? Label { get; init; }

    public IReadOnlyList<string> SourcePrimitiveIds { get; init; } = Array.Empty<string>();
}
