namespace OpenPlanTrace;

public interface IPlanDocumentLoaderRegistry
{
    IReadOnlyList<IPlanDocumentLoader> Loaders { get; }

    IPlanDocumentLoader? FindLoader(PlanSourceDescriptor source);

    ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default);
}
