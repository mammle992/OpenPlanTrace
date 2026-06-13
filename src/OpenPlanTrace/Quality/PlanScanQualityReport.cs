namespace OpenPlanTrace;

public enum PlanScanQualityGrade
{
    Unknown = 0,
    Poor,
    ReviewRequired,
    Usable,
    Strong
}

public sealed record PlanScanQualityReport(
    Confidence OverallConfidence,
    PlanScanQualityGrade Grade,
    bool RequiresReview,
    int PageCount,
    int PrimitiveCount,
    int DetectionCount,
    int DetectorCount,
    int DetectorWithFindingsCount,
    bool HasReliableCalibration,
    int DiagnosticInfoCount,
    int DiagnosticWarningCount,
    int DiagnosticErrorCount,
    IReadOnlyList<PlanDetectorQualitySummary> Detectors,
    IReadOnlyList<PlanScanQualityIssue> Issues,
    IReadOnlyList<string> Evidence)
{
    public static PlanScanQualityReport Empty { get; } =
        new(
            Confidence.None,
            PlanScanQualityGrade.Unknown,
            true,
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            0,
            0,
            Array.Empty<PlanDetectorQualitySummary>(),
            Array.Empty<PlanScanQualityIssue>(),
            new[] { "quality report has not been calculated" });
}

public sealed record PlanDetectorQualitySummary(
    string Name,
    int ItemCount,
    Confidence AverageConfidence,
    Confidence MinimumConfidence,
    Confidence MaximumConfidence,
    int LowConfidenceCount,
    int ReviewRequiredCount,
    int EvidenceBearingCount,
    Confidence Confidence,
    IReadOnlyList<string> Evidence);

public sealed record PlanScanQualityIssue(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    Confidence Confidence,
    IReadOnlyDictionary<string, string> Properties);
