namespace OpenPlanTrace;

public sealed record BenchmarkTargetMatchResult(
    int TargetIndex,
    string? TargetId,
    bool Matched,
    string? DetectionId,
    double Score,
    string Evidence);
