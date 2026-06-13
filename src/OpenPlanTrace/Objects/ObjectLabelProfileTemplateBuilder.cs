namespace OpenPlanTrace;

public sealed record ObjectLabelProfileTemplateOptions
{
    public string? Name { get; init; }

    public string? Version { get; init; } = "draft";

    public bool IncludeReviewGroups { get; init; } = true;

    public bool IncludeKnownGroups { get; init; } = true;
}

public static class ObjectLabelProfileTemplateBuilder
{
    public static ObjectLabelProfile FromScanResult(
        PlanScanResult result,
        ObjectLabelProfileTemplateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        options ??= new ObjectLabelProfileTemplateOptions();
        var name = Clean(options.Name)
            ?? $"{Clean(result.Document.Id) ?? "OpenPlanTrace"} object label draft";
        var version = Clean(options.Version) ?? "draft";
        var rules = result.ObjectGroups
            .Where(group => ShouldInclude(group, options))
            .OrderByDescending(group => group.RequiresReview)
            .ThenByDescending(group => group.Count)
            .ThenBy(group => group.Signature, StringComparer.Ordinal)
            .Select(CreateRule)
            .ToArray();

        return new ObjectLabelProfile(
            ObjectLabelProfile.CurrentSchemaVersion,
            name,
            version,
            rules);
    }

    private static bool ShouldInclude(
        ObjectCandidateGroup group,
        ObjectLabelProfileTemplateOptions options)
    {
        if (string.IsNullOrWhiteSpace(group.Signature))
        {
            return false;
        }

        return group.RequiresReview
            ? options.IncludeReviewGroups
            : options.IncludeKnownGroups;
    }

    private static ObjectLabelRule CreateRule(ObjectCandidateGroup group)
    {
        var evidence = new List<string>
        {
            $"Drafted from OpenPlanTrace object group {group.Id}.",
            $"{group.Count} candidate{(group.Count == 1 ? string.Empty : "s")} in group."
        };

        if (group.PageNumbers.Count > 0)
        {
            evidence.Add($"Pages {string.Join(", ", group.PageNumbers)}.");
        }

        if (group.SourcePrimitiveIds.Count > 0)
        {
            evidence.Add($"{group.SourcePrimitiveIds.Count} source primitive{(group.SourcePrimitiveIds.Count == 1 ? string.Empty : "s")} in group.");
        }

        if (group.DetectedTags.Count > 0)
        {
            evidence.Add($"Detected tags: {string.Join(", ", group.DetectedTags)}.");
        }

        evidence.Add("Edit this rule's label/category/requiresReview fields before using it as confirmed knowledge.");

        return new ObjectLabelRule
        {
            Signature = group.Signature,
            DetectedTagPattern = CommonDetectedTagPattern(group.DetectedTags),
            Category = group.Category,
            Kind = group.Kind,
            Label = Clean(group.Label),
            SymbolName = Clean(group.SymbolName),
            RequiresReview = group.RequiresReview,
            Confidence = new Confidence(RoundConfidence(group.Confidence.Value)),
            Evidence = evidence
        };
    }

    private static double RoundConfidence(double value) =>
        Math.Round(Math.Clamp(value, 0, 1), 2, MidpointRounding.AwayFromZero);

    private static string? CommonDetectedTagPattern(IReadOnlyList<string> tags)
    {
        var families = tags
            .Select(TagFamily)
            .Where(family => family is not null)
            .Select(family => family!.Value)
            .ToArray();
        var prefixes = families
            .Select(family => family.Prefix)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (prefixes.Length != 1)
        {
            return null;
        }

        return families.All(family => family.HasSeparator)
            ? $"{prefixes[0]}-*"
            : $"{prefixes[0]}*";
    }

    private static (string Prefix, bool HasSeparator)? TagFamily(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var trimmed = tag.Trim();
        var separator = trimmed.IndexOfAny(new[] { '-', '_' });
        if (separator <= 0)
        {
            var prefixLength = 0;
            while (prefixLength < trimmed.Length && char.IsLetter(trimmed[prefixLength]))
            {
                prefixLength++;
            }

            if (prefixLength <= 0 || prefixLength >= trimmed.Length)
            {
                return null;
            }

            var prefix = trimmed[..prefixLength];
            var suffix = trimmed[prefixLength..];
            return prefix.All(char.IsLetter) && suffix.Any(char.IsDigit)
                ? (prefix.ToUpperInvariant(), false)
                : null;
        }

        var separatedPrefix = trimmed[..separator];
        return separatedPrefix.All(char.IsLetter) ? (separatedPrefix.ToUpperInvariant(), true) : null;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
