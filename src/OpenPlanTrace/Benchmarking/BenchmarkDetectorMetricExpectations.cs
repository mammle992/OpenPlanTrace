namespace OpenPlanTrace;

public sealed record BenchmarkDetectorMetricExpectations
{
    public IReadOnlyList<BenchmarkDetectionTarget> Targets { get; init; } =
        Array.Empty<BenchmarkDetectionTarget>();

    public double? MinRecall { get; init; }

    public double? MinPrecision { get; init; }

    public bool? CompleteTruthSet { get; init; }

    public bool PrecisionScoringEnabled =>
        MinPrecision is not null
        || CompleteTruthSet == true;

    public bool HasExpectations =>
        Targets.Count > 0
        || MinRecall is not null
        || MinPrecision is not null
        || CompleteTruthSet == true;
}
