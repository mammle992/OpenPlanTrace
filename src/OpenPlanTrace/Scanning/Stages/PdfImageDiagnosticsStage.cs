using System.Globalization;

namespace OpenPlanTrace;

internal sealed class PdfImageDiagnosticsStage : IPipelineStage
{
    private const string StageName = "pdf-image-analysis";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsPdfDocument(context.Document.Metadata.Properties))
        {
            return ValueTask.CompletedTask;
        }

        var imageOnlyPageCount = ReadInt(context.Document.Metadata.Properties, "pdf.imageOnlyPageCount") ?? 0;
        var imageCount = ReadInt(context.Document.Metadata.Properties, "pdf.imageCount") ?? 0;
        if (imageCount == 0)
        {
            return ValueTask.CompletedTask;
        }

        var properties = PdfImageProperties(context.Document.Metadata.Properties);
        context.AddDiagnostic(
            "pdf.images.detected",
            DiagnosticSeverity.Info,
            StageName,
            "PDF embedded image evidence was recorded for raster/OCR routing decisions.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Document,
            properties: properties);

        if (imageOnlyPageCount > 0)
        {
            context.AddDiagnostic(
                "pdf.raster_image_only_pages",
                DiagnosticSeverity.Warning,
                StageName,
                "One or more PDF pages contain embedded images but no extracted text/vector primitives; scan quality depends on registering a real raster/OCR adapter.",
                confidence: Confidence.High,
                scope: DiagnosticScope.Document,
                properties: new Dictionary<string, string>(properties, StringComparer.Ordinal)
                {
                    ["adapterRequirement"] = nameof(IRasterPlanPrimitiveExtractor)
                });
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsPdfDocument(IReadOnlyDictionary<string, string> properties) =>
        TryRead(properties, "format", out var format)
        && string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> PdfImageProperties(IReadOnlyDictionary<string, string> source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["imageCount"] = ReadInt(source, "pdf.imageCount")?.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["imagePageCount"] = ReadInt(source, "pdf.imagePageCount")?.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["imageOnlyPageCount"] = ReadInt(source, "pdf.imageOnlyPageCount")?.ToString(CultureInfo.InvariantCulture) ?? "0"
        };

        Copy(source, result, "pdf.imagePages", "imagePages");
        Copy(source, result, "pdf.imageOnlyPages", "imageOnlyPages");
        Copy(source, result, "pdf.maxImagePageCoverage", "maxImagePageCoverage");
        Copy(source, result, "pdf.imageSamplePixels", "imageSamplePixels");
        Copy(source, result, "pdf.imageMaskCount", "imageMaskCount");
        Copy(source, result, "pdf.inlineImageCount", "inlineImageCount");
        return result;
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

    private static bool TryRead(
        IReadOnlyDictionary<string, string> properties,
        string key,
        out string value)
    {
        value = string.Empty;
        return properties.TryGetValue(key, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
            && (value = raw.Trim()).Length > 0;
    }
}
