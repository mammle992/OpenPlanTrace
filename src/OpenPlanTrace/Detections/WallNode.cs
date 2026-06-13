namespace OpenPlanTrace;

public sealed record WallNode(
    string Id,
    int PageNumber,
    PlanPoint Position,
    WallNodeKind Kind,
    int Degree,
    IReadOnlyList<string> Directions,
    Confidence Confidence,
    IReadOnlyList<string> Evidence);
