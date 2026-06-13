namespace OpenPlanTrace;

public abstract class PlanDocumentLoaderBase : IPlanDocumentLoader
{
    protected PlanDocumentLoaderBase(
        string formatName,
        params PlanSourceKind[] supportedSourceKinds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);

        FormatName = formatName;
        SupportedSourceKinds = supportedSourceKinds.Length == 0
            ? new HashSet<PlanSourceKind> { PlanSourceKind.Unknown }
            : supportedSourceKinds.ToHashSet();
    }

    public string FormatName { get; }

    public IReadOnlySet<PlanSourceKind> SupportedSourceKinds { get; }

    public virtual bool CanLoad(PlanSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SupportedSourceKinds.Contains(source.EffectiveKind);
    }

    public abstract ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default);
}
