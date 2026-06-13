namespace OpenPlanTrace;

public sealed record DimensionAnnotation(
    string Id,
    int PageNumber,
    DimensionKind Kind,
    DimensionOrientation Orientation,
    string Text,
    string NormalizedText,
    PlanRect Bounds,
    PlanMeasurementUnit Unit,
    double MeasuredMillimeters,
    PlanLineSegment? DimensionLine,
    double? DrawingLength,
    double? MillimetersPerDrawingUnit,
    Confidence Confidence,
    string? SourceRegionId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
