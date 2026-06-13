namespace OpenPlanTrace;

public sealed record RoomAdjacencyEdge(
    string Id,
    int PageNumber,
    string FirstRoomId,
    string? FirstRoomLabel,
    string SecondRoomId,
    string? SecondRoomLabel,
    RoomAdjacencyKind Kind,
    RoomAdjacencyDirection DirectionFromFirstToSecond,
    RoomAdjacencyDirection DirectionFromSecondToFirst,
    double SharedBoundaryLength,
    PlanLineSegment? SharedBoundary,
    Confidence Confidence,
    IReadOnlyList<string> SharedWallIds,
    IReadOnlyList<string> OpeningIds,
    IReadOnlyList<string> Evidence);
