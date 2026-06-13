namespace OpenPlanTrace;

public sealed record RoomRegion(
    string Id,
    int PageNumber,
    PlanRect Bounds,
    IReadOnlyList<PlanPoint> Boundary,
    IReadOnlyList<string> WallIds,
    Confidence Confidence)
{
    public string? Label { get; init; }

    public RoomUseKind UseKind { get; init; } = RoomUseKind.Unknown;

    public IReadOnlyList<string> LabelSourcePrimitiveIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public double DrawingArea
    {
        get
        {
            var area = PolygonArea(Boundary);
            return area > 0 ? area : Bounds.Area;
        }
    }

    public double? AreaSquareMeters { get; init; }

    public string? MeasurementScaleGroupId { get; init; }

    private static double PolygonArea(IReadOnlyList<PlanPoint> boundary)
    {
        if (boundary.Count < 3)
        {
            return 0;
        }

        var sum = 0.0;
        for (var index = 0; index < boundary.Count; index++)
        {
            var current = boundary[index];
            var next = boundary[(index + 1) % boundary.Count];
            sum += (current.X * next.Y) - (next.X * current.Y);
        }

        return Math.Abs(sum) / 2.0;
    }
}
