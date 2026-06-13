using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public sealed record BenchmarkCaseResult(
    string FixtureId,
    string? FixtureName,
    string SourcePath,
    bool Passed,
    bool ScanSucceeded,
    double DurationMilliseconds,
    BenchmarkCounts Counts,
    IReadOnlyList<BenchmarkAssertionResult> Assertions,
    string? ErrorMessage)
{
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<BenchmarkDetectorMetrics> Metrics { get; init; } =
        Array.Empty<BenchmarkDetectorMetrics>();

    public IReadOnlyList<BenchmarkCaseIssueSummary> QualityIssues { get; init; } =
        Array.Empty<BenchmarkCaseIssueSummary>();

    public IReadOnlyList<BenchmarkCaseIssueSummary> DiagnosticIssues { get; init; } =
        Array.Empty<BenchmarkCaseIssueSummary>();

    public IReadOnlyList<BenchmarkStageSummary> Stages { get; init; } =
        Array.Empty<BenchmarkStageSummary>();

    public PlanImportReadiness ImportReadiness { get; init; } =
        PlanImportReadiness.Empty;

    public ScanReviewQueueSummary ScanReviewQueue { get; init; } =
        ScanReviewQueueSummary.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Skipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipReason { get; init; }

    public int PassedAssertionCount => Assertions.Count(assertion => assertion.Passed);

    public int FailedAssertionCount => Assertions.Count(assertion => !assertion.Passed);
}
