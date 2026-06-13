using System.Text.Json;

namespace OpenPlanTrace;

public sealed record LayerCategoryProfile(
    string SchemaVersion,
    string? Name,
    string? Version,
    IReadOnlyList<LayerCategoryOverride> Overrides)
{
    public const string CurrentSchemaVersion = "openplantrace.layer-profile.v1";

    public static LayerCategoryProfile ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Layer category profile JSON is empty.", nameof(json));
        }

        LayerCategoryProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<LayerCategoryProfileDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Layer category profile JSON is invalid: {exception.Message}", exception);
        }

        if (profile is null)
        {
            throw new ArgumentException("Layer category profile JSON did not contain an object.", nameof(json));
        }

        var schemaVersion = string.IsNullOrWhiteSpace(profile.SchemaVersion)
            ? CurrentSchemaVersion
            : profile.SchemaVersion.Trim();

        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported layer category profile schemaVersion '{schemaVersion}'. Expected '{CurrentSchemaVersion}'.");
        }

        var overrides = new List<LayerCategoryOverride>();
        var index = 0;
        foreach (var item in profile.Overrides ?? Array.Empty<LayerCategoryOverrideDto>())
        {
            index++;
            overrides.Add(ParseOverride(item, index));
        }

        return new LayerCategoryProfile(
            CurrentSchemaVersion,
            Clean(profile.Name),
            Clean(profile.Version),
            overrides);
    }

    private static LayerCategoryOverride ParseOverride(LayerCategoryOverrideDto item, int index)
    {
        var pattern = Clean(item.Pattern);
        if (pattern is null)
        {
            throw new ArgumentException($"Layer category profile override #{index} requires a non-empty pattern.");
        }

        var categoryText = Clean(item.Category);
        if (categoryText is null)
        {
            throw new ArgumentException($"Layer category profile override '{pattern}' requires a category.");
        }

        if (!Enum.TryParse<LayerCategory>(categoryText, ignoreCase: true, out var category))
        {
            throw new ArgumentException($"Layer category profile override '{pattern}' has invalid category '{categoryText}'. Valid categories: {string.Join(", ", Enum.GetNames<LayerCategory>())}.");
        }

        return new LayerCategoryOverride(pattern, category, Clean(item.SourceFormat));
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record LayerCategoryProfileDto(
        string? SchemaVersion,
        string? Name,
        string? Version,
        IReadOnlyList<LayerCategoryOverrideDto>? Overrides);

    private sealed record LayerCategoryOverrideDto(
        string? Pattern,
        string? Category,
        string? SourceFormat);
}
