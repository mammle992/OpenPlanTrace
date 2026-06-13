using OpenPlanTrace;

namespace OpenPlanTrace.Ai;

internal static class OnnxVisualAiLabelLoader
{
    public static IReadOnlyList<OnnxVisualAiLabel> Load(string? labelsPath)
    {
        if (string.IsNullOrWhiteSpace(labelsPath))
        {
            return Array.Empty<OnnxVisualAiLabel>();
        }

        if (!File.Exists(labelsPath))
        {
            throw new FileNotFoundException("Visual AI labels file was not found.", labelsPath);
        }

        return File.ReadLines(labelsPath)
            .Select(Parse)
            .Where(label => label is not null)
            .Select(label => label!)
            .ToArray();
    }

    private static OnnxVisualAiLabel? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return null;
        }

        var parts = trimmed
            .Split(new[] { '|', '\t' }, 2, StringSplitOptions.TrimEntries);
        var label = parts[0].Trim();
        if (label.Length == 0)
        {
            return null;
        }

        var category = parts.Length > 1
            && Enum.TryParse<ObjectCategory>(parts[1], ignoreCase: true, out var parsed)
                ? parsed
                : VisualAiCategoryMapper.MapLabel(label);

        return new OnnxVisualAiLabel(label, category);
    }
}
