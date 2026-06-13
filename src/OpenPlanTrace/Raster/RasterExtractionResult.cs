namespace OpenPlanTrace;

public sealed record RasterExtractionResult(
    string DocumentId,
    IReadOnlyList<RasterPageExtraction> Pages)
{
    public string? ExtractorName { get; init; }

    public string? ExtractorVersion { get; init; }

    public string? ModelName { get; init; }

    public string? ModelVersion { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
