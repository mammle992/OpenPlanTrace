namespace OpenPlanTrace;

public sealed record BenchmarkStageExpectation
{
    public string Stage { get; init; } = string.Empty;

    public double? MaxDurationMilliseconds { get; init; }

    public int? MaxDiagnostics { get; init; }

    public int? MaxWarnings { get; init; }

    public int? MaxErrors { get; init; }
}
