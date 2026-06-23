using System.Reflection;
using System.Text;

internal static class BatchScanRunResultJsonSchema
{
    public const string CurrentSchemaVersion = BatchScanRunResult.CurrentSchemaVersion;

    public const string CurrentResourceName =
        "OpenPlanTrace.Cli.Schemas.openplantrace.batch.v6.schema.json";

    public static string ReadCurrent()
    {
        using var stream = OpenCurrentResource();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static async ValueTask WriteCurrentAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var schemaStream = OpenCurrentResource();
        await schemaStream.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static Stream OpenCurrentResource()
    {
        var assembly = typeof(BatchScanRunResultJsonSchema).GetTypeInfo().Assembly;
        return assembly.GetManifestResourceStream(CurrentResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded OpenPlanTrace batch result schema resource '{CurrentResourceName}' was not found.");
    }
}
