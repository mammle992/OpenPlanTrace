namespace OpenPlanTrace;

public sealed record PrimitiveSourceMetadata
{
    public static PrimitiveSourceMetadata Empty { get; } = new();

    public string? SourceFormat { get; init; }

    public string? SourceDocumentId { get; init; }

    public string? SourceName { get; init; }

    public string? SourcePath { get; init; }

    public string? SourceId { get; init; }

    public string? EntityType { get; init; }

    public string? Layer { get; init; }

    public string? Color { get; init; }

    public string? LineType { get; init; }

    public double? LineWeight { get; init; }

    public SourceDrawingSpace DrawingSpace { get; init; } = SourceDrawingSpace.Unknown;

    public string? BlockName { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public bool HasValue =>
        SourceFormat is not null
        || SourceDocumentId is not null
        || SourceName is not null
        || SourcePath is not null
        || SourceId is not null
        || EntityType is not null
        || Layer is not null
        || Color is not null
        || LineType is not null
        || LineWeight is not null
        || DrawingSpace != SourceDrawingSpace.Unknown
        || BlockName is not null
        || Properties.Count > 0;
}
