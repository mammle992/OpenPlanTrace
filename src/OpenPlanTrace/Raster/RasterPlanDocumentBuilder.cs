namespace OpenPlanTrace;

public static class RasterPlanDocumentBuilder
{
    public static PlanDocument FromExtraction(
        RasterExtractionResult extraction,
        PlanSourceDescriptor source)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(source);

        var pages = extraction.Pages
            .Select(page => new PlanPage(
                page.PageNumber,
                page.Size,
                BuildPrimitives(extraction, page, source).ToArray()))
            .ToArray();

        return new PlanDocument(extraction.DocumentId, pages)
        {
            Metadata = new PlanMetadata
            {
                SourceName = source.Name,
                SourcePath = source.FilePath,
                Properties = BuildDocumentProperties(extraction, source)
            }
        };
    }

    private static IEnumerable<PlanPrimitive> BuildPrimitives(
        RasterExtractionResult extraction,
        RasterPageExtraction page,
        PlanSourceDescriptor source)
    {
        var ordinal = 1;

        foreach (var item in page.Text)
        {
            var sourceId = Clean(item.SourceId) ?? $"raster:p{page.PageNumber}:ocr-text:{ordinal++}";
            yield return new TextPrimitive(item.Text, item.Bounds)
            {
                SourceId = sourceId,
                FontSize = item.FontSize,
                Source = SourceMetadata(
                    extraction,
                    source,
                    page,
                    sourceId,
                    "ocr-text",
                    item.Confidence,
                    item.EngineName,
                    item.EngineVersion,
                    item.ModelName,
                    item.ModelVersion,
                    item.Properties,
                    ("language", item.Language))
            };
        }

        foreach (var item in page.Lines)
        {
            var sourceId = Clean(item.SourceId) ?? $"raster:p{page.PageNumber}:line:{ordinal++}";
            yield return new LinePrimitive(item.Segment)
            {
                SourceId = sourceId,
                StrokeWidth = item.StrokeWidth,
                Source = SourceMetadata(
                    extraction,
                    source,
                    page,
                    sourceId,
                    "raster-line",
                    item.Confidence,
                    item.EngineName,
                    item.EngineVersion,
                    item.ModelName,
                    item.ModelVersion,
                    item.Properties)
            };
        }

        foreach (var item in page.Polylines)
        {
            var sourceId = Clean(item.SourceId) ?? $"raster:p{page.PageNumber}:polyline:{ordinal++}";
            yield return new PolylinePrimitive(item.Points, item.Closed)
            {
                SourceId = sourceId,
                StrokeWidth = item.StrokeWidth,
                Source = SourceMetadata(
                    extraction,
                    source,
                    page,
                    sourceId,
                    item.Closed ? "raster-closed-polyline" : "raster-polyline",
                    item.Confidence,
                    item.EngineName,
                    item.EngineVersion,
                    item.ModelName,
                    item.ModelVersion,
                    item.Properties)
            };
        }
    }

    private static PrimitiveSourceMetadata SourceMetadata(
        RasterExtractionResult extraction,
        PlanSourceDescriptor source,
        RasterPageExtraction page,
        string sourceId,
        string entityType,
        Confidence confidence,
        string? engineName,
        string? engineVersion,
        string? modelName,
        string? modelVersion,
        IReadOnlyDictionary<string, string> properties,
        params (string Key, string? Value)[] extra)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceKind"] = source.EffectiveKind.ToString(),
            ["pageNumber"] = page.PageNumber.ToString(),
            ["confidence"] = confidence.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
        };

        Add(merged, "dpi", page.Dpi);
        Add(merged, "sourceImageId", page.SourceImageId);
        Add(merged, "extractorName", Clean(engineName) ?? Clean(extraction.ExtractorName));
        Add(merged, "extractorVersion", Clean(engineVersion) ?? Clean(extraction.ExtractorVersion));
        Add(merged, "modelName", Clean(modelName) ?? Clean(extraction.ModelName));
        Add(merged, "modelVersion", Clean(modelVersion) ?? Clean(extraction.ModelVersion));

        foreach (var pair in extraction.Properties)
        {
            Add(merged, $"extraction.{pair.Key}", pair.Value);
        }

        foreach (var pair in properties)
        {
            Add(merged, pair.Key, pair.Value);
        }

        foreach (var pair in extra)
        {
            Add(merged, pair.Key, pair.Value);
        }

        return new PrimitiveSourceMetadata
        {
            SourceFormat = "raster",
            SourceDocumentId = extraction.DocumentId,
            SourceName = source.Name,
            SourcePath = source.FilePath,
            SourceId = sourceId,
            EntityType = entityType,
            DrawingSpace = SourceDrawingSpace.Raster,
            Properties = merged
        };
    }

    private static IReadOnlyDictionary<string, string> BuildDocumentProperties(
        RasterExtractionResult extraction,
        PlanSourceDescriptor source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sourceKind"] = source.Kind.ToString(),
            ["effectiveSourceKind"] = source.EffectiveKind.ToString(),
            ["extractionKind"] = "RasterEvidence"
        };

        Add(result, "fileExtension", source.FileExtension);
        Add(result, "contentType", source.ContentType);
        if (source.ClipboardContentKind is { } clipboardContentKind)
        {
            result["clipboardContentKind"] = clipboardContentKind.ToString();
        }

        Add(result, "extractorName", extraction.ExtractorName);
        Add(result, "extractorVersion", extraction.ExtractorVersion);
        Add(result, "modelName", extraction.ModelName);
        Add(result, "modelVersion", extraction.ModelVersion);
        RasterExtractionSummary.From(extraction).AddProperties(result);

        foreach (var pair in extraction.Properties)
        {
            Add(result, pair.Key, pair.Value);
        }

        foreach (var pair in source.Properties)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                Add(result, $"source.{pair.Key.Trim()}", pair.Value);
            }
        }

        return result;
    }

    private static void Add(IDictionary<string, string> properties, string key, double? value)
    {
        if (value is not null)
        {
            properties[key] = value.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
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
