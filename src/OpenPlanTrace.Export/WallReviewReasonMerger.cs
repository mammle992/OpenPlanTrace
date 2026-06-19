namespace OpenPlanTrace.Export;

internal static class WallReviewReasonMerger
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Merge(
        params IReadOnlyDictionary<string, IReadOnlyList<string>>[] reasonMaps)
    {
        var merged = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var reasonMap in reasonMaps)
        {
            foreach (var pair in reasonMap)
            {
                if (!merged.TryGetValue(pair.Key, out var reasons))
                {
                    reasons = new List<string>();
                    merged[pair.Key] = reasons;
                }

                reasons.AddRange(pair.Value.Where(reason => !string.IsNullOrWhiteSpace(reason)));
            }
        }

        return merged.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }
}
