using System.Reflection;
using System.Text;

namespace OpenPlanTrace;

public static class ObjectReviewDatasetJsonSchema
{
    public const string CurrentSchemaVersion = ObjectReviewDataset.CurrentSchemaVersion;

    public const string CurrentResourceName =
        "OpenPlanTrace.Schemas.openplantrace.object-review-dataset.v2.schema.json";

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
        var assembly = typeof(ObjectReviewDatasetJsonSchema).GetTypeInfo().Assembly;
        return assembly.GetManifestResourceStream(CurrentResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded OpenPlanTrace object review dataset schema resource '{CurrentResourceName}' was not found.");
    }
}
