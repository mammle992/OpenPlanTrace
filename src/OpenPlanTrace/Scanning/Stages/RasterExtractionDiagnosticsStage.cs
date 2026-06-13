using System.Globalization;

namespace OpenPlanTrace;

internal sealed class RasterExtractionDiagnosticsStage : IPipelineStage
{
    private const string StageName = "raster-extraction";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsRasterDocument(context.Document.Metadata.Properties))
        {
            return ValueTask.CompletedTask;
        }

        var properties = RasterDiagnosticProperties(context.Document);
        var primitiveCount = ReadInt(context.Document.Metadata.Properties, "raster.primitiveCount")
            ?? context.Document.Pages.Sum(page => page.Primitives.Count);
        var lowConfidenceCount = ReadInt(context.Document.Metadata.Properties, "raster.lowConfidenceCount") ?? 0;
        var lowConfidenceRatio = ReadDouble(context.Document.Metadata.Properties, "raster.lowConfidenceRatio") ?? 0;
        var confidence = ReadDouble(context.Document.Metadata.Properties, "raster.averageConfidence") is { } average
            ? new Confidence(average)
            : Confidence.Medium;

        context.AddDiagnostic(
            "raster.extraction.summary",
            DiagnosticSeverity.Info,
            StageName,
            "Raster extraction metadata was preserved from the registered raster adapter.",
            confidence: confidence,
            scope: DiagnosticScope.Document,
            sourcePrimitiveIds: SourcePrimitiveIds(context.Document, onlyLowConfidence: false).Take(20),
            properties: properties);

        if (primitiveCount == 0)
        {
            context.AddDiagnostic(
                "raster.extraction.no_primitives",
                DiagnosticSeverity.Warning,
                StageName,
                "The raster adapter returned no text, line, or polyline evidence; downstream detections will remain empty unless a real extractor emits primitives.",
                confidence: Confidence.High,
                scope: DiagnosticScope.Document,
                properties: properties);
        }
        else if (primitiveCount >= 3 && lowConfidenceRatio >= 0.5)
        {
            context.AddDiagnostic(
                "raster.extraction.low_confidence",
                DiagnosticSeverity.Warning,
                StageName,
                "Most raster evidence from the adapter has low confidence and should be reviewed before trusting derived detections.",
                confidence: new Confidence(Math.Min(0.9, Math.Max(0.55, lowConfidenceRatio))),
                scope: DiagnosticScope.Document,
                sourcePrimitiveIds: SourcePrimitiveIds(context.Document, onlyLowConfidence: true).Take(20),
                properties: new Dictionary<string, string>(properties, StringComparer.Ordinal)
                {
                    ["lowConfidenceCount"] = lowConfidenceCount.ToString(CultureInfo.InvariantCulture),
                    ["lowConfidenceRatio"] = Format(lowConfidenceRatio)
                });
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsRasterDocument(IReadOnlyDictionary<string, string> properties) =>
        TryRead(properties, "format", out var format)
            && string.Equals(format, "raster", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> RasterDiagnosticProperties(PlanDocument document)
    {
        var sourceIds = document.Pages
            .SelectMany(page => page.Primitives)
            .Select(primitive => primitive.SourceId ?? primitive.Source.SourceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pageCount"] = ReadInt(document.Metadata.Properties, "raster.pageCount")?.ToString(CultureInfo.InvariantCulture)
                ?? document.Pages.Count.ToString(CultureInfo.InvariantCulture),
            ["primitiveCount"] = ReadInt(document.Metadata.Properties, "raster.primitiveCount")?.ToString(CultureInfo.InvariantCulture)
                ?? document.Pages.Sum(page => page.Primitives.Count).ToString(CultureInfo.InvariantCulture),
            ["textCount"] = ReadInt(document.Metadata.Properties, "raster.textCount")?.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["lineCount"] = ReadInt(document.Metadata.Properties, "raster.lineCount")?.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["polylineCount"] = ReadInt(document.Metadata.Properties, "raster.polylineCount")?.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["sampleSourcePrimitiveIds"] = string.Join(",", sourceIds)
        };

        Copy(document.Metadata.Properties, result, "raster.extractor", "extractor");
        Copy(document.Metadata.Properties, result, "raster.extractorVersion", "extractorVersion");
        Copy(document.Metadata.Properties, result, "raster.modelName", "modelName");
        Copy(document.Metadata.Properties, result, "raster.modelVersion", "modelVersion");
        Copy(document.Metadata.Properties, result, "raster.averageConfidence", "averageConfidence");
        Copy(document.Metadata.Properties, result, "raster.minimumConfidence", "minimumConfidence");
        Copy(document.Metadata.Properties, result, "raster.maximumConfidence", "maximumConfidence");
        Copy(document.Metadata.Properties, result, "raster.lowConfidenceCount", "lowConfidenceCount");
        Copy(document.Metadata.Properties, result, "raster.lowConfidenceRatio", "lowConfidenceRatio");
        Copy(document.Metadata.Properties, result, "raster.sourceImageIdCount", "sourceImageIdCount");
        Copy(document.Metadata.Properties, result, "raster.sourceImageIds", "sourceImageIds");
        Copy(document.Metadata.Properties, result, "raster.dpiValues", "dpiValues");
        return result;
    }

    private static IEnumerable<string> SourcePrimitiveIds(PlanDocument document, bool onlyLowConfidence)
    {
        foreach (var primitive in document.Pages.SelectMany(page => page.Primitives))
        {
            if (onlyLowConfidence
                && (ReadDouble(primitive.Source.Properties, "confidence") is not { } confidence || confidence >= 0.5))
            {
                continue;
            }

            var sourceId = primitive.SourceId ?? primitive.Source.SourceId;
            if (!string.IsNullOrWhiteSpace(sourceId))
            {
                yield return sourceId;
            }
        }
    }

    private static void Copy(
        IReadOnlyDictionary<string, string> source,
        IDictionary<string, string> target,
        string sourceKey,
        string targetKey)
    {
        if (TryRead(source, sourceKey, out var value))
        {
            target[targetKey] = value;
        }
    }

    private static int? ReadInt(IReadOnlyDictionary<string, string> properties, string key) =>
        TryRead(properties, key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

    private static double? ReadDouble(IReadOnlyDictionary<string, string> properties, string key) =>
        TryRead(properties, key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

    private static bool TryRead(
        IReadOnlyDictionary<string, string> properties,
        string key,
        out string value)
    {
        foreach (var property in properties)
        {
            if (string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
