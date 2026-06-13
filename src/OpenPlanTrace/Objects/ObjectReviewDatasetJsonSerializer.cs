using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public static class ObjectReviewDatasetJsonSerializer
{
    public static string Serialize(ObjectReviewDataset dataset, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return JsonSerializer.Serialize(dataset, CreateOptions(writeIndented));
    }

    public static async ValueTask WriteAsync(
        ObjectReviewDataset dataset,
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
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
