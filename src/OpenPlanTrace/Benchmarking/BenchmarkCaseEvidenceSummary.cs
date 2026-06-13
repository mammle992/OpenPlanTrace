namespace OpenPlanTrace;

public sealed record BenchmarkCaseIssueSummary(
    string Code,
    DiagnosticSeverity Severity,
    string Stage,
    string Scope,
    int Count,
    string Message,
    IReadOnlyList<int> PageNumbers,
    double? MaxConfidence,
    int SourcePrimitiveCount,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyDictionary<string, string> Properties);

public sealed record BenchmarkStageSummary(
    string Stage,
    double DurationMilliseconds,
    int InputCount,
    int OutputCount,
    int DiagnosticCount,
    int InfoCount,
    int WarningCount,
    int ErrorCount);
