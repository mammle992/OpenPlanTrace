namespace OpenPlanTrace;

public sealed record BenchmarkAssertionResult(
    string Name,
    bool Passed,
    string Expected,
    string Actual,
    string Message);
