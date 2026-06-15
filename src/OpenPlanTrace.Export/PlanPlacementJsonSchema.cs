using System.Reflection;
using System.Text;

namespace OpenPlanTrace.Export;

public static class PlanPlacementJsonSchema
{
    public const string CurrentSchemaVersion = PlanPlacementExport.CurrentSchemaVersion;

    public const string CurrentResourceName =
        "OpenPlanTrace.Export.Schemas.openplantrace.placement.v6.schema.json";

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
        var assembly = typeof(PlanPlacementJsonSchema).GetTypeInfo().Assembly;
        return assembly.GetManifestResourceStream(CurrentResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded OpenPlanTrace placement schema resource '{CurrentResourceName}' was not found.");
    }
}
