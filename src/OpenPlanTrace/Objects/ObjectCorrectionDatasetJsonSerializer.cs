using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public static class ObjectCorrectionDatasetJsonSerializer
{
    public static string Serialize(ObjectCorrectionDataset dataset, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return JsonSerializer.Serialize(dataset, CreateOptions(writeIndented));
    }

    public static async ValueTask WriteAsync(
        ObjectCorrectionDataset dataset,
        Stream output,
        bool writeIndented = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(output);

        await JsonSerializer.SerializeAsync(
                output,
                dataset,
                CreateOptions(writeIndented),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
