namespace OpenPlanTrace;

public sealed record CalibrationScaleGroup(
    string Id,
    int? PageNumber,
    CalibrationScaleScope Scope,
    PlanMeasurementUnit DrawingUnit,
    PlanMeasurementUnit RealWorldUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    int EvidenceCount,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceRegionIds,
    PlanRect? Bounds,
    IReadOnlyList<string> Evidence);
