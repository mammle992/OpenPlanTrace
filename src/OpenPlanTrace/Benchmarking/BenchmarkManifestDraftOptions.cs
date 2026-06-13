namespace OpenPlanTrace;

public sealed record BenchmarkManifestDraftOptions
{
    public string FixtureId { get; init; } = "scan-draft";

    public string? FixtureName { get; init; }

    public string? ManifestName { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    public bool Optional { get; init; }

    public string? SkipReason { get; init; }

    public int MaxTargetsPerDetector { get; init; } = 8;

    public double TargetRecall { get; init; } = 1.0;

    public double? TargetPrecision { get; init; }

    public bool IncludeBounds { get; init; } = true;
}
