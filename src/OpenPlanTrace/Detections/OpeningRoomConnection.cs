namespace OpenPlanTrace;

public sealed record OpeningRoomConnection(
    string RoomId,
    string? RoomLabel,
    RoomUseKind RoomUseKind,
    IReadOnlyList<string> RoomAdjacencyIds,
    double DistanceToOpening,
    bool SharesHostWall,
    Confidence Confidence,
    IReadOnlyList<string> Evidence);
