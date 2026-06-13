namespace OpenPlanTrace;

public sealed record GridAxis(
    string Id,
    int PageNumber,
    GridAxisOrientation Orientation,
    string? Label,
    PlanLineSegment Line,
    PlanRect Bounds,
    double Coordinate,
    Confidence Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> LabelSourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
