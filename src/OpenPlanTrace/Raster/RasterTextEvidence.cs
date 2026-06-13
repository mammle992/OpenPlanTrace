namespace OpenPlanTrace;

public sealed record RasterTextEvidence(
    string Text,
    PlanRect Bounds,
    Confidence Confidence)
{
    public string? SourceId { get; init; }

    public string? Language { get; init; }

    public double FontSize { get; init; }

    public string? EngineName { get; init; }

    public string? EngineVersion { get; init; }

    public string? ModelName { get; init; }

    public string? ModelVersion { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
