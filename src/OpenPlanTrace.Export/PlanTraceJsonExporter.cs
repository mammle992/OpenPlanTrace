using System.Text.Json;

namespace OpenPlanTrace.Export;

public sealed record PlanTraceJsonExportOptions
{
    public bool WriteIndented { get; init; } = true;
}

public static class PlanTraceJsonExporter
{
    public static string Serialize(
        PlanScanResult result,
        PlanTraceJsonExportOptions? options = null)
    {
        var export = PlanTraceExport.From(result);
        return JsonSerializer.Serialize(export, CreateJsonOptions(options));
    }

    public static async ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanTraceJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var export = PlanTraceExport.From(result);
        await JsonSerializer.SerializeAsync(
            stream,
            export,
            CreateJsonOptions(options),
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateJsonOptions(PlanTraceJsonExportOptions? options)
    {
        options ??= new PlanTraceJsonExportOptions();

        return new JsonSerializerOptions
        {
            WriteIndented = options.WriteIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
