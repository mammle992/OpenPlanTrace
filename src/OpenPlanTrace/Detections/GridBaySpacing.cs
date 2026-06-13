namespace OpenPlanTrace;

public sealed record GridBaySpacing(
    string Id,
    int PageNumber,
    GridAxisOrientation AxisOrientation,
    string FirstAxisId,
    string? FirstAxisLabel,
    string SecondAxisId,
    string? SecondAxisLabel,
    PlanLineSegment Line,
    PlanRect Bounds,
    double DrawingDistance,
    double? DistanceMeters,
    Confidence Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence)
{
    public string? MeasurementScaleGroupId { get; init; }
}
