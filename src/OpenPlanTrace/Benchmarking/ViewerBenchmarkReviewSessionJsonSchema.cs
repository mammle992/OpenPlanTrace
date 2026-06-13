using System.Reflection;
using System.Text;

namespace OpenPlanTrace;

public static class ViewerBenchmarkReviewSessionJsonSchema
{
    public const string CurrentSchemaVersion = "openplantrace.viewer-benchmark-review-session.v1";

    public const string CurrentResourceName =
        "OpenPlanTrace.Schemas.openplantrace.viewer-benchmark-review-session.v1.schema.json";

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
        var assembly = typeof(ViewerBenchmarkReviewSessionJsonSchema).GetTypeInfo().Assembly;
        return assembly.GetManifestResourceStream(CurrentResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded OpenPlanTrace viewer benchmark review-session schema resource '{CurrentResourceName}' was not found.");
    }
}
