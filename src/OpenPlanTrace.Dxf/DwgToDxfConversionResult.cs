namespace OpenPlanTrace.Dxf;

public sealed class DwgToDxfConversionResult : IDisposable, IAsyncDisposable
{
    public DwgToDxfConversionResult(
        Stream dxfStream,
        string? dxfName = null,
        IReadOnlyDictionary<string, string>? properties = null,
        bool disposeStream = true)
    {
        ArgumentNullException.ThrowIfNull(dxfStream);

        DxfStream = dxfStream;
        DxfName = string.IsNullOrWhiteSpace(dxfName) ? null : dxfName.Trim();
        Properties = properties ?? new Dictionary<string, string>();
        DisposeStream = disposeStream;
    }

    public Stream DxfStream { get; }

    public string? DxfName { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public bool DisposeStream { get; }

    public void Dispose()
    {
        if (DisposeStream)
        {
            DxfStream.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!DisposeStream)
        {
            return;
        }

        if (DxfStream is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        DxfStream.Dispose();
    }
}
