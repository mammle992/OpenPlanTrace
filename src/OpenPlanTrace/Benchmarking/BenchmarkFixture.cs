using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public sealed record BenchmarkFixture
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string SourcePath { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipReason { get; init; }

    public BenchmarkExpectations Expectations { get; init; } = new();

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
