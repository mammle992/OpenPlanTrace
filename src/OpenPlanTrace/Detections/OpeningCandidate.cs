namespace OpenPlanTrace;

public sealed record OpeningCandidate(
    string Id,
    int PageNumber,
    OpeningType Type,
    PlanRect Bounds,
    Confidence Confidence)
{
    public string? WallId { get; init; }

    public IReadOnlyList<string> AdjacentWallIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> HostWallIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConnectedRoomIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ConnectedRoomLabels { get; init; } = Array.Empty<string>();

    public IReadOnlyList<OpeningRoomConnection> ConnectedRoomLinks { get; init; } = Array.Empty<OpeningRoomConnection>();

    public IReadOnlyList<string> RoomAdjacencyIds { get; init; } = Array.Empty<string>();

    public PlanLineSegment CenterLine { get; init; }

    public OpeningOrientation Orientation { get; init; } = OpeningOrientation.Unknown;

    public OpeningOperation Operation { get; init; } = OpeningOperation.Unknown;

    public OpeningHingeSide HingeSide { get; init; } = OpeningHingeSide.Unknown;

    public OpeningSwingSide SwingSide { get; init; } = OpeningSwingSide.Unknown;

    public OpeningSwingDirection SwingDirection { get; init; } = OpeningSwingDirection.Unknown;

    public PlanPoint? HingePoint { get; init; }

    public OpeningPlacement? Placement { get; init; }

    public IReadOnlyList<string> SourcePrimitiveIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public double DrawingWidth => CenterLine.Length > 0
        ? CenterLine.Length
        : Math.Max(Bounds.Width, Bounds.Height);

    public double? WidthMillimeters { get; init; }

    public string? MeasurementScaleGroupId { get; init; }
}
