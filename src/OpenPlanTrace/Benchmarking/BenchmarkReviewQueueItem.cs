namespace OpenPlanTrace;

public enum BenchmarkReviewQueueKind
{
    PrecisionExtra = 0,
    SpotCheckExtra,
    ReviewOnly
}

public sealed record BenchmarkReviewQueueItem(
    string FixtureId,
    string? FixtureName,
    string SourcePath,
    string Detector,
    BenchmarkReviewQueueKind Kind,
    bool PrecisionScoringEnabled,
    BenchmarkDetectionSummary Detection,
    string RecommendedAction);
