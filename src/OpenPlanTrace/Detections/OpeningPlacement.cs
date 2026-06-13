namespace OpenPlanTrace;

public sealed record OpeningPlacement(
    string? HostWallId,
    IReadOnlyList<string> AnchorWallIds,
    PlanLineSegment ReferenceLine,
    PlanPoint StartPoint,
    PlanPoint EndPoint,
    double StartOffsetDrawingUnits,
    double EndOffsetDrawingUnits,
    double CenterOffsetDrawingUnits,
    double LengthDrawingUnits,
    double? StartOffsetMillimeters,
    double? EndOffsetMillimeters,
    double? CenterOffsetMillimeters,
    double? LengthMillimeters,
    double HostWallStartParameter,
    double HostWallEndParameter,
    double HostWallCenterParameter,
    PlanVector AlongVector,
    PlanVector NormalVector,
    double CrossWallOffsetDrawingUnits,
    Confidence Confidence,
    IReadOnlyList<string> Evidence);
