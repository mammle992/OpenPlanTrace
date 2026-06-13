namespace OpenPlanTrace.Dxf;

public sealed record ExternalDwgToDxfConverterOptions
{
    public string ConverterName { get; init; } = "ExternalDWG";

    public string ExecutablePath { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();

    public string OutputFileName { get; init; } = "{sourceBaseName}.dxf";

    public string? WorkingDirectory { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public bool DeleteTemporaryFiles { get; init; } = true;

    public bool CaptureProcessOutputInProperties { get; init; }

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
