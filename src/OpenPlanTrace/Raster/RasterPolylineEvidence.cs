namespace OpenPlanTrace;

public sealed record RasterPolylineEvidence(
    IReadOnlyList<PlanPoint> Points,
    Confidence Confidence)
{
    public string? SourceId { get; init; }

    public bool Closed { get; init; }

    public double StrokeWidth { get; init; }

    public string? EngineName { get; init; }

    public string? EngineVersion { get; init; }

    public string? ModelName { get; init; }

    public string? ModelVersion { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
