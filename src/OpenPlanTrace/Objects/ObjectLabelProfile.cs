using System.Text.Json;

namespace OpenPlanTrace;

public sealed record ObjectLabelProfile(
    string SchemaVersion,
    string? Name,
    string? Version,
    IReadOnlyList<ObjectLabelRule> Rules)
{
    public const string CurrentSchemaVersion = "openplantrace.object-label-profile.v1";

    public static ObjectLabelProfile ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Object label profile JSON is empty.", nameof(json));
        }

        ObjectLabelProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ObjectLabelProfileDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Object label profile JSON is invalid: {exception.Message}", exception);
        }

        if (profile is null)
        {
            throw new ArgumentException("Object label profile JSON did not contain an object.", nameof(json));
        }

        var schemaVersion = string.IsNullOrWhiteSpace(profile.SchemaVersion)
            ? CurrentSchemaVersion
            : profile.SchemaVersion.Trim();

        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported object label profile schemaVersion '{schemaVersion}'. Expected '{CurrentSchemaVersion}'.");
        }

        var rules = new List<ObjectLabelRule>();
        var index = 0;
        foreach (var item in profile.Rules ?? Array.Empty<ObjectLabelRuleDto>())
        {
            index++;
            rules.Add(ParseRule(item, index));
        }

        return new ObjectLabelProfile(
            CurrentSchemaVersion,
            Clean(profile.Name),
            Clean(profile.Version),
            rules);
    }

    private static ObjectLabelRule ParseRule(ObjectLabelRuleDto item, int index)
    {
        var confidence = item.Confidence is null ? (Confidence?)null : new Confidence(item.Confidence.Value);
        var rule = new ObjectLabelRule
        {
            Signature = Clean(item.Signature),
            SymbolNamePattern = Clean(item.SymbolNamePattern),
            LabelPattern = Clean(item.LabelPattern),
            LayerPattern = Clean(item.LayerPattern),
            DetectedTagPattern = Clean(item.DetectedTagPattern),
            SourceFormat = Clean(item.SourceFormat),
            MatchCategory = ParseOptionalEnum<ObjectCategory>(item.MatchCategory, $"object label rule #{index} matchCategory"),
            MatchKind = ParseOptionalEnum<ObjectCandidateKind>(item.MatchKind, $"object label rule #{index} matchKind"),
            Category = ParseOptionalEnum<ObjectCategory>(item.Category, $"object label rule #{index} category"),
            Kind = ParseOptionalEnum<ObjectCandidateKind>(item.Kind, $"object label rule #{index} kind"),
            Label = Clean(item.Label),
            SymbolName = Clean(item.SymbolName),
            RequiresReview = item.RequiresReview,
            Confidence = confidence,
            Evidence = item.Evidence?
                .Select(Clean)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray()
                ?? Array.Empty<string>()
        };

        if (!rule.HasSelector)
        {
            throw new ArgumentException($"Object label profile rule #{index} requires at least one selector.");
        }

        if (!rule.HasOutput)
        {
            throw new ArgumentException($"Object label profile rule #{index} requires at least one label output.");
        }

        return rule;
    }

    private static TEnum? ParseOptionalEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        var cleaned = Clean(value);
        if (cleaned is null)
        {
            return null;
        }

        if (Enum.TryParse<TEnum>(cleaned, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{fieldName} has invalid value '{cleaned}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ObjectLabelProfileDto(
        string? SchemaVersion,
        string? Name,
        string? Version,
        IReadOnlyList<ObjectLabelRuleDto>? Rules);

    private sealed record ObjectLabelRuleDto(
        string? Signature,
        string? SymbolNamePattern,
        string? LabelPattern,
        string? LayerPattern,
        string? DetectedTagPattern,
        string? SourceFormat,
        string? MatchCategory,
        string? MatchKind,
        string? Category,
        string? Kind,
        string? Label,
        string? SymbolName,
        bool? RequiresReview,
        double? Confidence,
        IReadOnlyList<string?>? Evidence);
}
