namespace OpenPlanTrace;

public sealed record MeasurementConsistencyCheck(
    string DimensionId,
    int PageNumber,
    MeasurementConsistencyStatus Status,
    double DimensionMillimeters,
    double DrawingLength,
    double ImpliedMillimetersPerDrawingUnit,
    double? SelectedMillimetersPerDrawingUnit,
    double? ExpectedMillimeters,
    double? DeltaMillimeters,
    double? RelativeError,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
