namespace OpenPlanTrace;

public sealed record LayerCategoryOverride(
    string Pattern,
    LayerCategory Category,
    string? SourceFormat = null)
{
    public bool Matches(string layerName, string? sourceFormat = null)
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SourceFormat)
            && !string.Equals(SourceFormat, sourceFormat, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesPattern(layerName, Pattern);
    }

    private static bool MatchesPattern(string value, string pattern)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
        }

        var parts = pattern
            .Split('*', StringSplitOptions.None)
            .Where(part => part.Length > 0)
            .ToArray();
        var currentIndex = 0;

        if (parts.Length == 0)
        {
            return true;
        }

        if (!pattern.StartsWith("*", StringComparison.Ordinal) && !value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var part in parts)
        {
            var index = value.IndexOf(part, currentIndex, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            currentIndex = index + part.Length;
        }

        return pattern.EndsWith("*", StringComparison.Ordinal)
            || value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }
}
