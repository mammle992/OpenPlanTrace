namespace OpenPlanTrace;

public sealed record BatchScanManifest
{
    public const string CurrentSchemaVersion = "openplantrace.batch-manifest.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? Name { get; init; }

    public string? OutputDirectory { get; init; }

    public string? SummaryJsonPath { get; init; }

    public IReadOnlyList<string> Inputs { get; init; } = Array.Empty<string>();

    public bool Recursive { get; init; }

    public bool? WriteSvg { get; init; }

    public bool? WriteGeoJson { get; init; }

    public int? MaxDegreeOfParallelism { get; init; }

    public int? RetryCount { get; init; }

    public IReadOnlyList<string> LayerProfiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<LayerCategoryOverride> LayerCategoryOverrides { get; init; } = Array.Empty<LayerCategoryOverride>();

    public IReadOnlyList<string> ObjectLabelProfiles { get; init; } = Array.Empty<string>();

    public BatchScannerOptions? ScannerOptions { get; init; }

    public static void ValidateSchemaVersion(BatchScanManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var schemaVersion = string.IsNullOrWhiteSpace(manifest.SchemaVersion)
            ? CurrentSchemaVersion
            : manifest.SchemaVersion.Trim();

        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported batch manifest schemaVersion '{schemaVersion}'. Expected '{CurrentSchemaVersion}'.");
        }
    }
}

public sealed record BatchScannerOptions
{
    public double? SheetMargin { get; init; }

    public double? MinWallLength { get; init; }

    public double? MinWallFragmentLength { get; init; }

    public double? MaxWallFragmentGap { get; init; }

    public int? MaxWallCandidateSeedsPerPage { get; init; }

    public double? WallMergeTolerance { get; init; }

    public double? WallSnapTolerance { get; init; }

    public double? WallThickness { get; init; }

    public double? MinOpeningGap { get; init; }

    public double? MaxOpeningGap { get; init; }

    public double? ObjectNearbyTextSearchRadius { get; init; }

    public int? MaxNearbyTextPerObject { get; init; }
}
