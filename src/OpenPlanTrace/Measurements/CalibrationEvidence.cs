namespace OpenPlanTrace;

public sealed record CalibrationEvidence(
    CalibrationEvidenceKind Kind,
    int? PageNumber,
    string? SourcePrimitiveId,
    string? Text,
    PlanMeasurementUnit Unit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    Confidence Confidence,
    string Description);
