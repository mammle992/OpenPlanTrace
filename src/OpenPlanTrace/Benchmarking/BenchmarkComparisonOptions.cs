namespace OpenPlanTrace;

public sealed record BenchmarkComparisonOptions
{
    public double QualityConfidenceRegressionThreshold { get; init; } = 0.05;

    public double DurationRegressionRatio { get; init; } = 1.5;

    public double DurationRegressionMinimumMilliseconds { get; init; } = 250;

    public double MeasurementSpreadRegressionRatio { get; init; } = 1.25;

    public double MeasurementSpreadRegressionMinimumDelta { get; init; } = 0.25;

    public double MeasurementOutlierRatioRegressionThreshold { get; init; } = 0.1;

    public double DetectorRecallRegressionThreshold { get; init; } = 0.05;

    public double DetectorPrecisionRegressionThreshold { get; init; } = 0.05;

    public double DetectorF1RegressionThreshold { get; init; } = 0.05;

    public double ScoreboardOverallRegressionThreshold { get; init; } = 0.03;

    public double ScoreboardConsumerReadinessRegressionThreshold { get; init; } = 0.03;

    public double ImportReadinessScoreRegressionThreshold { get; init; } = 0.03;

    public int ScanReviewQueueItemRegressionMinimumDelta { get; init; } = 10;

    public double ScanReviewQueueItemRegressionRatio { get; init; } = 1.25;

    public int ScanReviewQueueKindRegressionMinimumDelta { get; init; } = 5;

    public double ScanReviewQueueKindRegressionRatio { get; init; } = 1.25;
}
