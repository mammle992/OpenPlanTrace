namespace OpenPlanTrace;

public sealed record WallEdge(
    string Id,
    int PageNumber,
    string FromNodeId,
    string ToNodeId,
    string WallId,
    Confidence Confidence);
