namespace OpenPlanTrace;

public interface IRasterPlanPrimitiveExtractor
{
    string Name { get; }

    string? Version { get; }

    ValueTask<RasterExtractionResult> ExtractAsync(
        Stream stream,
        PlanSourceDescriptor source,
        RasterExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}
