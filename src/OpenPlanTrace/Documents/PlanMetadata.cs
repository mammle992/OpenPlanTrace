namespace OpenPlanTrace;

public sealed record PlanMetadata
{
    public string? SourceName { get; init; }

    public string? SourcePath { get; init; }

    public string? Author { get; init; }

    public string? ProjectName { get; init; }

    public string? DrawingNumber { get; init; }

    public string? Revision { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
