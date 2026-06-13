namespace OpenPlanTrace.Dxf;

public sealed class DwgToDxfPlanDocumentLoader : PlanDocumentLoaderBase
{
    private readonly IDwgToDxfConverter converter;
    private readonly IPlanDocumentLoader dxfLoader;

    public DwgToDxfPlanDocumentLoader(
        IDwgToDxfConverter converter,
        IPlanDocumentLoader? dxfLoader = null)
        : base(CreateFormatName(converter), PlanSourceKind.Dwg)
    {
        this.converter = converter;
        this.dxfLoader = dxfLoader ?? new IxMiliaDxfPlanDocumentLoader();

        if (!this.dxfLoader.SupportedSourceKinds.Contains(PlanSourceKind.Dxf))
        {
            throw new ArgumentException("The delegated loader must support DXF sources.", nameof(dxfLoader));
        }
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
                $"DWG-to-DXF loader can only load DWG sources. Received '{source.EffectiveKind}'.");
        }

        await using var conversion = await converter
            .ConvertAsync(stream, source, options, cancellationToken)
            .ConfigureAwait(false);

        if (conversion.DxfStream.CanSeek)
        {
            conversion.DxfStream.Position = 0;
        }

        var dxfSource = CreateDxfSource(source, conversion);
        var document = await dxfLoader
            .LoadAsync(conversion.DxfStream, dxfSource, options, cancellationToken)
            .ConfigureAwait(false);

        return document with
        {
            Id = source.Name ?? source.FilePath ?? document.Id,
            Metadata = document.Metadata with
            {
                SourceName = source.Name ?? document.Metadata.SourceName,
                SourcePath = source.FilePath ?? document.Metadata.SourcePath,
                Properties = CreateMetadataProperties(document, source, conversion)
            }
        };
    }

    private static string CreateFormatName(IDwgToDxfConverter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        return $"DWG-to-DXF/{Clean(converter.ConverterName) ?? "converter"}";
    }

    private PlanSourceDescriptor CreateDxfSource(
        PlanSourceDescriptor source,
        DwgToDxfConversionResult conversion)
    {
        var sourceBaseName = Path.GetFileNameWithoutExtension(source.Name ?? source.FilePath ?? "converted");
        var dxfName = conversion.DxfName ?? $"{sourceBaseName}.dxf";

        return new PlanSourceDescriptor
        {
            Kind = PlanSourceKind.Dxf,
            Name = dxfName,
            FileExtension = ".dxf",
            ContentType = "application/dxf",
            Properties = MergeSourceProperties(source, conversion)
        };
    }

    private IReadOnlyDictionary<string, string> CreateMetadataProperties(
        PlanDocument document,
        PlanSourceDescriptor source,
        DwgToDxfConversionResult conversion)
    {
        var properties = new Dictionary<string, string>(document.Metadata.Properties, StringComparer.Ordinal);

        if (properties.TryGetValue("format", out var intermediateFormat))
        {
            properties["dwg.intermediateFormat"] = intermediateFormat;
        }

        if (properties.TryGetValue("loader", out var intermediateLoader))
        {
            properties["dwg.intermediateLoader"] = intermediateLoader;
        }

        properties["format"] = "dwg";
        properties["loader"] = FormatName;
        properties["sourceKind"] = source.Kind.ToString();
        properties["effectiveSourceKind"] = source.EffectiveKind.ToString();
        properties["dwg.conversion"] = "dwg-to-dxf";
        properties["dwg.converter"] = converter.ConverterName;
        properties["dwg.dxfLoader"] = dxfLoader.FormatName;
        Add(properties, "fileExtension", source.FileExtension);
        Add(properties, "contentType", source.ContentType);

        if (source.ClipboardContentKind is { } clipboardContentKind)
        {
            properties["clipboardContentKind"] = clipboardContentKind.ToString();
        }

        if (!string.IsNullOrWhiteSpace(source.Name))
        {
            properties["dwg.sourceName"] = source.Name;
        }

        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            properties["dwg.sourcePath"] = source.FilePath;
        }

        if (!string.IsNullOrWhiteSpace(conversion.DxfName))
        {
            properties["dwg.convertedDxfName"] = conversion.DxfName!;
        }

        foreach (var (key, value) in conversion.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                properties[$"dwg.converter.{key.Trim()}"] = value;
            }
        }

        foreach (var (key, value) in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                properties[$"source.{key.Trim()}"] = value.Trim();
            }
        }

        return properties;
    }

    private static IReadOnlyDictionary<string, string> MergeSourceProperties(
        PlanSourceDescriptor source,
        DwgToDxfConversionResult conversion)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dwg.originalKind"] = source.EffectiveKind.ToString(),
            ["dwg.conversion"] = "dwg-to-dxf"
        };

        foreach (var (key, value) in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                properties[$"dwg.source.{key.Trim()}"] = value;
            }
        }

        foreach (var (key, value) in conversion.Properties)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                properties[$"dwg.converter.{key.Trim()}"] = value;
            }
        }

        return properties;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(IDictionary<string, string> properties, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[key] = value.Trim();
        }
    }
}
