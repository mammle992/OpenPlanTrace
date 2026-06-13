namespace OpenPlanTrace;

public sealed record BenchmarkManifest
{
    public const string CurrentSchemaVersion = "openplantrace.benchmark-manifest.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Name { get; init; }

    public IReadOnlyList<BenchmarkFixture> Fixtures { get; init; } = Array.Empty<BenchmarkFixture>();

    public static void ValidateSchemaVersion(BenchmarkManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var schemaVersion = string.IsNullOrWhiteSpace(manifest.SchemaVersion)
            ? CurrentSchemaVersion
            : manifest.SchemaVersion.Trim();

        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported benchmark manifest schemaVersion '{schemaVersion}'. Expected '{CurrentSchemaVersion}'.");
        }
    }
}
