namespace OpenPlanTrace;

public sealed record WallGraphComponent(
    string Id,
    int PageNumber,
    WallGraphComponentKind Kind,
    PlanRect Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    double DrawingLength,
    Confidence Confidence,
    IReadOnlyList<string> Evidence,
    bool ExcludedFromStructuralTopology = false)
{
    public int WallCount => WallIds.Count;

    public int NodeCount => NodeIds.Count;

    public int EdgeCount => EdgeIds.Count;
}
