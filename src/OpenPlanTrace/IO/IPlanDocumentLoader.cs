namespace OpenPlanTrace;

public interface IPlanDocumentLoader
{
    string FormatName { get; }

    IReadOnlySet<PlanSourceKind> SupportedSourceKinds { get; }

    bool CanLoad(PlanSourceDescriptor source);

    bool CanLoad(string fileNameOrExtension) =>
        CanLoad(PlanSourceDescriptor.FromFileNameOrExtension(fileNameOrExtension));

    ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default);
}
