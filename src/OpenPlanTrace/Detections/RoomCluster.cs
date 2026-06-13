namespace OpenPlanTrace;

public sealed record RoomCluster(
    string Id,
    int PageNumber,
    IReadOnlyList<string> RoomIds,
    IReadOnlyList<string> RoomLabels,
    PlanRect Bounds,
    double DrawingArea,
    double? AreaSquareMeters,
    IReadOnlyList<string> RoomAdjacencyIds,
    IReadOnlyList<string> OpeningIds,
    Confidence Confidence,
    IReadOnlyList<string> Evidence)
{
    public RoomClusterKind Kind { get; init; } = RoomClusterKind.Unknown;
}
