namespace OpenPlanTrace;

public sealed class PlanDocumentLoaderRegistry : IPlanDocumentLoaderRegistry
{
    public PlanDocumentLoaderRegistry(IEnumerable<IPlanDocumentLoader> loaders)
    {
        ArgumentNullException.ThrowIfNull(loaders);
        Loaders = loaders.ToArray();
    }

    public IReadOnlyList<IPlanDocumentLoader> Loaders { get; }

    public IPlanDocumentLoader? FindLoader(PlanSourceDescriptor source) =>
        Loaders.FirstOrDefault(loader => loader.CanLoad(source));

    public IReadOnlyList<PlanSourceCapability> GetCapabilities() =>
        PlanSourceCapabilityCatalog.Describe(Loaders);

    public PlanSourceCapability GetCapability(PlanSourceDescriptor source) =>
        PlanSourceCapabilityCatalog.Describe(source, Loaders);

    public ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);

        var loader = FindLoader(source);
        if (loader is null)
        {
            var capability = GetCapability(source);
            throw new PlanLoadException(
                $"No OpenPlanTrace loader is registered for source kind '{source.Kind}'"
                + (source.EffectiveKind == source.Kind ? string.Empty : $" with effective content kind '{source.EffectiveKind}'")
                + (source.FileExtension is null ? "." : $" and extension '{source.FileExtension}'.")
                + $" {capability.Message}"
                + (string.IsNullOrWhiteSpace(capability.AdapterRequirement) ? string.Empty : $" Adapter: {capability.AdapterRequirement}")
                + (string.IsNullOrWhiteSpace(capability.LicenseNote) ? string.Empty : $" Licensing: {capability.LicenseNote}"));
        }

        return loader.LoadAsync(stream, source, options, cancellationToken);
    }
}
