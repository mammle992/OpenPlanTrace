namespace OpenPlanTrace;

public sealed record RasterExtractionOptions
{
    public double? TargetDpi { get; init; }

    public bool ExtractText { get; init; } = true;

    public bool ExtractLinework { get; init; } = true;

    public bool PreserveIntermediateImages { get; init; }
}
