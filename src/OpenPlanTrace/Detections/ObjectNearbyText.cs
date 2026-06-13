namespace OpenPlanTrace;

public sealed record ObjectNearbyText(
    string Text,
    int PageNumber,
    PlanRect Bounds,
    string SourcePrimitiveId,
    double Distance);
