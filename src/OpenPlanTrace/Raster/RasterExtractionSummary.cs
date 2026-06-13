using System.Globalization;

namespace OpenPlanTrace;

internal sealed record RasterExtractionSummary(
    int PageCount,
    int TextCount,
    int LineCount,
    int PolylineCount,
    int PrimitiveCount,
    int LowConfidenceCount,
    double? AverageConfidence,
    double? MinimumConfidence,
    double? MaximumConfidence,
    IReadOnlyList<string> SourceImageIds,
    IReadOnlyList<double> DpiValues)
{
    public double LowConfidenceRatio =>
        PrimitiveCount == 0 ? 0 : LowConfidenceCount / (double)PrimitiveCount;

    public static RasterExtractionSummary From(RasterExtractionResult extraction)
    {
        ArgumentNullException.ThrowIfNull(extraction);

        var confidenceValues = extraction.Pages
            .SelectMany(page => page.Text.Select(item => item.Confidence.Value)
                .Concat(page.Lines.Select(item => item.Confidence.Value))
                .Concat(page.Polylines.Select(item => item.Confidence.Value)))
            .ToArray();
        var sourceImageIds = extraction.Pages
            .Select(page => page.SourceImageId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var dpiValues = extraction.Pages
            .Select(page => page.Dpi)
            .Where(value => value is > 0)
            .Select(value => Math.Round(value!.Value, 3))
            .Distinct()
            .Order()
            .ToArray();
        var textCount = extraction.Pages.Sum(page => page.Text.Count);
        var lineCount = extraction.Pages.Sum(page => page.Lines.Count);
        var polylineCount = extraction.Pages.Sum(page => page.Polylines.Count);

        return new RasterExtractionSummary(
            extraction.Pages.Count,
            textCount,
            lineCount,
            polylineCount,
            textCount + lineCount + polylineCount,
            confidenceValues.Count(value => value < 0.5),
            confidenceValues.Length == 0 ? null : confidenceValues.Average(),
            confidenceValues.Length == 0 ? null : confidenceValues.Min(),
            confidenceValues.Length == 0 ? null : confidenceValues.Max(),
            sourceImageIds,
            dpiValues);
    }

    public void AddProperties(IDictionary<string, string> properties, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(properties);

        Add(properties, prefix, "pageCount", PageCount);
        Add(properties, prefix, "textCount", TextCount);
        Add(properties, prefix, "lineCount", LineCount);
        Add(properties, prefix, "polylineCount", PolylineCount);
        Add(properties, prefix, "primitiveCount", PrimitiveCount);
        Add(properties, prefix, "lowConfidenceCount", LowConfidenceCount);
        Add(properties, prefix, "lowConfidenceRatio", LowConfidenceRatio);
        Add(properties, prefix, "averageConfidence", AverageConfidence);
        Add(properties, prefix, "minimumConfidence", MinimumConfidence);
        Add(properties, prefix, "maximumConfidence", MaximumConfidence);
        Add(properties, prefix, "sourceImageIdCount", SourceImageIds.Count);
        if (SourceImageIds.Count > 0)
        {
            properties[Key(prefix, "sourceImageIds")] = string.Join(",", SourceImageIds.Take(20));
        }

        if (DpiValues.Count > 0)
        {
            properties[Key(prefix, "dpiValues")] = string.Join(",", DpiValues.Select(Format));
        }
    }

    private static void Add(IDictionary<string, string> properties, string prefix, string key, int value) =>
        properties[Key(prefix, key)] = value.ToString(CultureInfo.InvariantCulture);

    private static void Add(IDictionary<string, string> properties, string prefix, string key, double? value)
    {
        if (value is not null)
        {
            properties[Key(prefix, key)] = Format(value.Value);
        }
    }

    private static string Key(string prefix, string key) =>
        string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}.{key}";

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
