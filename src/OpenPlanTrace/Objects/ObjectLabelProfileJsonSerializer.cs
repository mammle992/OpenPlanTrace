using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public static class ObjectLabelProfileJsonSerializer
{
    public static string Serialize(ObjectLabelProfile profile, bool writeIndented = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(ToDto(profile), CreateOptions(writeIndented));
    }

    public static async ValueTask WriteAsync(
        ObjectLabelProfile profile,
        Stream output,
        bool writeIndented = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(output);

        await JsonSerializer.SerializeAsync(
                output,
                ToDto(profile),
                CreateOptions(writeIndented),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ObjectLabelProfileDto ToDto(ObjectLabelProfile profile) =>
        new(
            profile.SchemaVersion,
            Clean(profile.Name),
            Clean(profile.Version),
            profile.Rules.Select(ToDto).ToArray());

    private static ObjectLabelRuleDto ToDto(ObjectLabelRule rule) =>
        new(
            Clean(rule.Signature),
            Clean(rule.SymbolNamePattern),
            Clean(rule.LabelPattern),
            Clean(rule.LayerPattern),
            Clean(rule.DetectedTagPattern),
            Clean(rule.SourceFormat),
            rule.MatchCategory,
            rule.MatchKind,
            rule.Category,
            rule.Kind,
            Clean(rule.Label),
            Clean(rule.SymbolName),
            rule.RequiresReview,
            rule.Confidence is null ? null : RoundConfidence(rule.Confidence.Value.Value),
            rule.Evidence.Count == 0 ? null : rule.Evidence.Select(Clean).Where(item => item is not null).Select(item => item!).ToArray());

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

    private static double RoundConfidence(double value) =>
        Math.Round(Math.Clamp(value, 0, 1), 2, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ObjectLabelProfileDto(
        string SchemaVersion,
        string? Name,
        string? Version,
        IReadOnlyList<ObjectLabelRuleDto> Rules);

    private sealed record ObjectLabelRuleDto(
        string? Signature,
        string? SymbolNamePattern,
        string? LabelPattern,
        string? LayerPattern,
        string? DetectedTagPattern,
        string? SourceFormat,
        ObjectCategory? MatchCategory,
        ObjectCandidateKind? MatchKind,
        ObjectCategory? Category,
        ObjectCandidateKind? Kind,
        string? Label,
        string? SymbolName,
        bool? RequiresReview,
        double? Confidence,
        IReadOnlyList<string>? Evidence);
}
