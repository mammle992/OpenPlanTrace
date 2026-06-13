namespace OpenPlanTrace;

public sealed class OpenPlanTraceEngine
{
    private readonly IPlanDocumentLoaderRegistry _loaderRegistry;
    private readonly IFloorplanScanner _scanner;

    public OpenPlanTraceEngine(
        IPlanDocumentLoaderRegistry loaderRegistry,
        IFloorplanScanner? scanner = null)
    {
        _loaderRegistry = loaderRegistry;
        _scanner = scanner ?? new OpenPlanTraceScanner();
    }

    public async ValueTask<PlanScanResult> ScanAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? loadOptions = null,
        ScannerOptions? scannerOptions = null,
        IProgress<PipelineStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var document = await _loaderRegistry
            .LoadAsync(stream, source, loadOptions, cancellationToken)
            .ConfigureAwait(false);

        return await _scanner
            .ScanAsync(document, scannerOptions, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<PlanScanResult> ScanFileAsync(
        string filePath,
        PlanLoadOptions? loadOptions = null,
        ScannerOptions? scannerOptions = null,
        IProgress<PipelineStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = File.OpenRead(filePath);
        return await ScanAsync(
            stream,
            PlanSourceDescriptor.FromFilePath(filePath),
            loadOptions,
            scannerOptions,
            progress,
            cancellationToken).ConfigureAwait(false);
    }
}
