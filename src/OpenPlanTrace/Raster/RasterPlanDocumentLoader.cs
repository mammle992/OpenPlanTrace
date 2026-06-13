namespace OpenPlanTrace;

public sealed class RasterPlanDocumentLoader : PlanDocumentLoaderBase
{
    private readonly IRasterPlanPrimitiveExtractor extractor;
    private readonly RasterExtractionOptions defaultOptions;

    public RasterPlanDocumentLoader(
        IRasterPlanPrimitiveExtractor extractor,
        RasterExtractionOptions? defaultOptions = null)
        : base(CreateFormatName(extractor), PlanSourceKind.RasterImage)
    {
        ArgumentNullException.ThrowIfNull(extractor);

        this.extractor = extractor;
        this.defaultOptions = defaultOptions ?? new RasterExtractionOptions();
    }

    public override async ValueTask<PlanDocument> LoadAsync(
        Stream stream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(source);

        if (!CanLoad(source))
        {
            throw new PlanLoadException(
                $"Raster loader can only load raster image sources. Received '{source.EffectiveKind}'.");
        }

        var extraction = await extractor
            .ExtractAsync(stream, source, CreateExtractionOptions(options), cancellationToken)
            .ConfigureAwait(false);

        var normalizedExtraction = NormalizeExtraction(extraction);
        var document = RasterPlanDocumentBuilder.FromExtraction(normalizedExtraction, source);

        return document with
        {
            Metadata = document.Metadata with
            {
                Properties = CreateMetadataProperties(document, source, normalizedExtraction)
            }
        };
    }

    private static string CreateFormatName(IRasterPlanPrimitiveExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        return $"Raster/{Clean(extractor.Name) ?? "extractor"}";
    }

    private RasterExtractionOptions CreateExtractionOptions(PlanLoadOptions? options) =>
        defaultOptions with
        {
            ExtractText = options?.ExtractText ?? defaultOptions.ExtractText,
            ExtractLinework = options?.ExtractVectorGeometry ?? defaultOptions.ExtractLinework
        };

    private RasterExtractionResult NormalizeExtraction(RasterExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        return extraction with
        {
            ExtractorName = Clean(extraction.ExtractorName) ?? Clean(extractor.Name),
            ExtractorVersion = Clean(extraction.ExtractorVersion) ?? Clean(extractor.Version)
        };
    }

    private IReadOnlyDictionary<string, string> CreateMetadataProperties(
        PlanDocument document,
        PlanSourceDescriptor source,
        RasterExtractionResult extraction)
    {
        var properties = new Dictionary<string, string>(document.Metadata.Properties, StringComparer.Ordinal)
        {
            ["format"] = "raster",
            ["loader"] = FormatName,
            ["raster.adapter"] = nameof(IRasterPlanPrimitiveExtractor),
            ["sourceKind"] = source.Kind.ToString(),
            ["effectiveSourceKind"] = source.EffectiveKind.ToString(),
            ["raster.sourceKind"] = source.EffectiveKind.ToString()
        };
        Add(properties, "fileExtension", source.FileExtension);
        Add(properties, "contentType", source.ContentType);

        if (source.ClipboardContentKind is { } clipboardContentKind)
        {
            properties["clipboardContentKind"] = clipboardContentKind.ToString();
        }

        Add(properties, "raster.extractor", extraction.ExtractorName);
        Add(properties, "raster.extractorVersion", extraction.ExtractorVersion);
        Add(properties, "raster.modelName", extraction.ModelName);
        Add(properties, "raster.modelVersion", extraction.ModelVersion);
        RasterExtractionSummary.From(extraction).AddProperties(properties, "raster");

        foreach (var (key, value) in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                properties[$"source.{key.Trim()}"] = value;
                properties[$"raster.source.{key.Trim()}"] = value;
            }
        }

        return properties;
    }

    private static void Add(IDictionary<string, string> properties, string key, string? value)
    {
        var cleaned = Clean(value);
        if (cleaned is not null)
        {
            properties[key] = cleaned;
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
