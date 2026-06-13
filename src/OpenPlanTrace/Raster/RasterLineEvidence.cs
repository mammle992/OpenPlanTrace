namespace OpenPlanTrace;

public sealed record RasterLineEvidence(
    PlanLineSegment Segment,
    Confidence Confidence)
{
    public string? SourceId { get; init; }

    public double StrokeWidth { get; init; }

    public string? EngineName { get; init; }

    public string? EngineVersion { get; init; }

    public string? ModelName { get; init; }

    public string? ModelVersion { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
