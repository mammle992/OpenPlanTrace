namespace OpenPlanTrace;

public sealed record BenchmarkDetectorMetrics(
    string Detector,
    int ExpectedCount,
    int DetectedCount,
    int MatchedCount,
    int MissedCount,
    int ExtraCount,
    double Recall,
    double Precision,
    double F1,
    IReadOnlyList<BenchmarkTargetMatchResult> Matches)
{
    public bool PrecisionScoringEnabled { get; init; }

    public int ScoredDetectionCount { get; init; } = DetectedCount;

    public int ReviewOnlyDetectionCount { get; init; }

    public IReadOnlyList<BenchmarkDetectionSummary> ExtraDetections { get; init; } =
        Array.Empty<BenchmarkDetectionSummary>();

    public IReadOnlyList<BenchmarkDetectionSummary> ReviewOnlyDetections { get; init; } =
        Array.Empty<BenchmarkDetectionSummary>();
}
