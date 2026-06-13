using System.Text.Json;

namespace OpenPlanTrace;

public sealed record KvemoCropManifestLabelProfileOptions
{
    public string? Name { get; init; }

    public string? Version { get; init; } = "draft";

    public bool IncludeCropOnly { get; init; } = true;

    public bool IncludeClassified { get; init; } = true;

    public bool IncludeHardNegativeReviews { get; init; }

    public int? MaxRules { get; init; }
}

public sealed record KvemoCropManifestLabelProfileResult(
    ObjectLabelProfile Profile,
    int EntryCount,
    int RuleCount,
    int SkippedEntryCount,
    int InvalidEntryCount,
    IReadOnlyList<KvemoCropManifestLabelProfileIssue> Issues);

public sealed record KvemoCropManifestLabelProfileIssue(
    int LineNumber,
    string Severity,
    string Message);

public static class KvemoCropManifestLabelProfileBuilder
{
    public static async Task<KvemoCropManifestLabelProfileResult> ReadAsync(
        string manifestPath,
        KvemoCropManifestLabelProfileOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Kvemo crop manifest path is required.", nameof(manifestPath));
        }

        var lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return FromLines(
            lines,
            options,
            Path.GetFileNameWithoutExtension(manifestPath));
    }

    public static KvemoCropManifestLabelProfileResult FromLines(
        IReadOnlyList<string> lines,
        KvemoCropManifestLabelProfileOptions? options = null,
        string? defaultNameSeed = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        options ??= new KvemoCropManifestLabelProfileOptions();
        var issues = new List<KvemoCropManifestLabelProfileIssue>();
        var entries = new List<KvemoCropProfileEntry>();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = TryParseEntry(line, lineNumber, issues);
            if (entry is null)
            {
                continue;
            }

            if (!ShouldInclude(entry, options))
            {
                issues.Add(new KvemoCropManifestLabelProfileIssue(
                    lineNumber,
                    "info",
                    $"Skipped crop {entry.DetectionId} because suggestedTrainingUse is '{entry.SuggestedTrainingUse}'."));
                continue;
            }

            if (SelectorFor(entry) is null)
            {
                issues.Add(new KvemoCropManifestLabelProfileIssue(
                    lineNumber,
                    "warning",
                    $"Skipped crop {entry.DetectionId} because it has no reusable group signature, review key, or detected tag selector."));
                continue;
            }

            entries.Add(entry);
        }

        var name = Clean(options.Name)
            ?? $"{Clean(defaultNameSeed) ?? "Kvemo crops"} object label draft";
        var version = Clean(options.Version) ?? "draft";
        var rules = entries
            .GroupBy(entry => SelectorFor(entry)!.Key, StringComparer.Ordinal)
            .Select(group => CreateRule(group.ToArray()))
            .OrderByDescending(rule => rule.Evidence.Count)
            .ThenBy(rule => rule.Signature ?? rule.DetectedTagPattern, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (options.MaxRules is > 0)
        {
            rules = rules.Take(options.MaxRules.Value).ToArray();
        }

        var profile = new ObjectLabelProfile(
            ObjectLabelProfile.CurrentSchemaVersion,
            name,
            version,
            rules);
        var invalidCount = issues.Count(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var skippedCount = issues.Count(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return new KvemoCropManifestLabelProfileResult(
            profile,
            entries.Count,
            rules.Length,
            skippedCount,
            invalidCount,
            issues);
    }

    private static KvemoCropProfileEntry? TryParseEntry(
        string line,
        int lineNumber,
        ICollection<KvemoCropManifestLabelProfileIssue> issues)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!string.Equals(
                    ReadString(root, "schemaVersion"),
                    VisualAiCropManifestEntry.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                issues.Add(new KvemoCropManifestLabelProfileIssue(
                    lineNumber,
                    "error",
                    $"Unexpected schemaVersion '{ReadString(root, "schemaVersion") ?? "(missing)"}'."));
                return null;
            }

            var detectionId = ReadString(root, "detectionId") ?? $"line:{lineNumber}";
            var classification = ReadClassification(root);
            return new KvemoCropProfileEntry(
                lineNumber,
                detectionId,
                ReadString(root, "detectionKind") ?? "object",
                ReadString(root, "reviewKey"),
                ReadString(root, "groupSignature"),
                ReadInt(root, "pageNumber"),
                ReadEnum(root, "candidateKind", ObjectCandidateKind.Unknown),
                ReadEnum(root, "category", ObjectCategory.Unknown),
                ReadEnum(root, "sourceKind", ObjectCandidateSourceKind.Unknown),
                ReadString(root, "sourceWallComponentKind"),
                ReadProvenanceCounts(root, "sourceKindCounts"),
                ReadProvenanceCounts(root, "sourceWallComponentKindCounts"),
                ReadDouble(root, "deterministicConfidence"),
                ReadString(root, "label"),
                ReadString(root, "symbolName"),
                ReadStringArray(root, "detectedTags"),
                ReadStringArray(root, "nearbyText"),
                ReadStringArray(root, "sourcePrimitiveIds"),
                ReadSourceEvidence(root),
                ReadString(root, "reviewPriority") ?? "Medium",
                ReadStringArray(root, "reviewReasons"),
                ReadString(root, "suggestedTrainingUse") ?? "classification-training-candidate",
                classification);
        }
        catch (JsonException exception)
        {
            issues.Add(new KvemoCropManifestLabelProfileIssue(lineNumber, "error", exception.Message));
            return null;
        }
    }

    private static bool ShouldInclude(
        KvemoCropProfileEntry entry,
        KvemoCropManifestLabelProfileOptions options)
    {
        if (entry.Classification is null && !options.IncludeCropOnly)
        {
            return false;
        }

        if (entry.Classification is not null && !options.IncludeClassified)
        {
            return false;
        }

        return options.IncludeHardNegativeReviews
            || !string.Equals(entry.SuggestedTrainingUse, "hard-negative-review", StringComparison.OrdinalIgnoreCase);
    }

    private static ObjectLabelRule CreateRule(IReadOnlyList<KvemoCropProfileEntry> entries)
    {
        var selector = SelectorFor(entries[0])!;
        var classifications = entries
            .Select(entry => entry.Classification)
            .Where(classification => classification is not null)
            .Select(classification => classification!)
            .ToArray();
        var detectedTags = entries.SelectMany(entry => entry.DetectedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sourcePrimitiveCount = entries.Sum(entry => entry.SourcePrimitiveIds.Count);
        var pageNumbers = entries.Select(entry => entry.PageNumber).Where(page => page > 0).Distinct().Order().ToArray();
        var reviewPriorities = Counts(entries.Select(entry => entry.ReviewPriority));
        var trainingUses = Counts(entries.Select(entry => entry.SuggestedTrainingUse));
        var sourceKinds = SumCounts(entries.SelectMany(entry =>
            entry.SourceKindCounts.Count > 0
                ? entry.SourceKindCounts
                : new[] { new KvemoCropManifestLabelProfileCount(entry.SourceKind.ToString(), 1) }));
        var sourceWallComponentKinds = SumCounts(entries.SelectMany(entry =>
            entry.SourceWallComponentKindCounts.Count > 0
                ? entry.SourceWallComponentKindCounts
                : string.IsNullOrWhiteSpace(entry.SourceWallComponentKind)
                    ? Array.Empty<KvemoCropManifestLabelProfileCount>()
                    : new[] { new KvemoCropManifestLabelProfileCount(entry.SourceWallComponentKind, 1) }));
        var layers = entries.SelectMany(entry => entry.SourceEvidence.Layers).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var blockNames = entries.SelectMany(entry => entry.SourceEvidence.BlockNames).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var evidence = new List<string>
        {
            $"Drafted from Kvemo crop manifest review key '{selector.Key}'.",
            $"{entries.Count} crop{(entries.Count == 1 ? string.Empty : "s")} share this selector.",
            $"Training use: {FormatCounts(trainingUses)}.",
            $"Review priority: {FormatCounts(reviewPriorities)}.",
            "Edit this rule's label/category/requiresReview fields before using it as confirmed knowledge."
        };

        if (pageNumbers.Length > 0)
        {
            evidence.Add($"Pages {string.Join(", ", pageNumbers)}.");
        }

        if (sourcePrimitiveCount > 0)
        {
            evidence.Add($"{sourcePrimitiveCount} source primitive{(sourcePrimitiveCount == 1 ? string.Empty : "s")} referenced by crops.");
        }

        if (sourceKinds.Count > 0)
        {
            evidence.Add($"Source kinds: {FormatCounts(sourceKinds)}.");
        }

        if (sourceWallComponentKinds.Count > 0)
        {
            evidence.Add($"Wall component source kinds: {FormatCounts(sourceWallComponentKinds)}.");
        }

        if (detectedTags.Length > 0)
        {
            evidence.Add($"Detected tags: {string.Join(", ", detectedTags.Take(12))}{(detectedTags.Length > 12 ? ", ..." : string.Empty)}.");
        }

        if (layers.Length > 0)
        {
            evidence.Add($"Source layers: {string.Join(", ", layers.Take(8))}{(layers.Length > 8 ? ", ..." : string.Empty)}.");
        }

        if (blockNames.Length > 0)
        {
            evidence.Add($"Block names: {string.Join(", ", blockNames.Take(8))}{(blockNames.Length > 8 ? ", ..." : string.Empty)}.");
        }

        var bestClassification = classifications
            .OrderByDescending(classification => classification.Confidence)
            .FirstOrDefault();
        if (bestClassification is not null)
        {
            evidence.Add(
                $"Kvemo model candidate: {bestClassification.Label} ({bestClassification.Confidence:0.###}) using {bestClassification.ModelName} {bestClassification.ModelVersion}.");
        }

        return new ObjectLabelRule
        {
            Signature = selector.IsSignature ? selector.Key : null,
            DetectedTagPattern = CommonDetectedTagPattern(detectedTags),
            Category = bestClassification?.Category ?? MostCommon(entries.Select(entry => entry.Category), ObjectCategory.Unknown),
            Kind = MostCommon(entries.Select(entry => entry.CandidateKind), ObjectCandidateKind.Unknown),
            Label = Clean(bestClassification?.Label) ?? MostCommonText(entries.Select(entry => entry.Label)),
            SymbolName = MostCommonText(entries.Select(entry => entry.SymbolName)),
            RequiresReview = true,
            Confidence = new Confidence(RoundConfidence(bestClassification?.Confidence ?? entries.Average(entry => entry.DeterministicConfidence))),
            Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static KvemoCropSelector? SelectorFor(KvemoCropProfileEntry entry)
    {
        var groupSignature = Clean(entry.GroupSignature);
        if (groupSignature is not null)
        {
            return new KvemoCropSelector(groupSignature, true);
        }

        var reviewKey = Clean(entry.ReviewKey);
        if (reviewKey is not null && LooksLikeObjectGroupSignature(reviewKey))
        {
            return new KvemoCropSelector(reviewKey, true);
        }

        var detectedTagPattern = CommonDetectedTagPattern(entry.DetectedTags);
        if (detectedTagPattern is not null)
        {
            return new KvemoCropSelector(detectedTagPattern, false);
        }

        return null;
    }

    private static bool LooksLikeObjectGroupSignature(string value) =>
        value.Contains("|kind:", StringComparison.OrdinalIgnoreCase)
        || value.Contains("|layers:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("geometry:", StringComparison.OrdinalIgnoreCase);

    private static string? CommonDetectedTagPattern(IEnumerable<string> tags)
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
        if (families.Length == 0 || prefixes.Length != 1)
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

    private static IReadOnlyList<KvemoCropManifestLabelProfileCount> Counts(IEnumerable<string?> values) =>
        values
            .Select(Clean)
            .Where(value => value is not null)
            .Select(value => value!)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new KvemoCropManifestLabelProfileCount(group.Key, group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<KvemoCropManifestLabelProfileCount> SumCounts(IEnumerable<KvemoCropManifestLabelProfileCount> counts) =>
        counts
            .Where(count => !string.IsNullOrWhiteSpace(count.Value) && count.Count > 0)
            .GroupBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new KvemoCropManifestLabelProfileCount(group.Key, group.Sum(count => count.Count)))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string FormatCounts(IReadOnlyList<KvemoCropManifestLabelProfileCount> counts) =>
        counts.Count == 0
            ? "-"
            : string.Join(", ", counts.Select(count => $"{count.Value}:{count.Count}"));

    private static T MostCommon<T>(IEnumerable<T> values, T fallback)
        where T : struct, Enum =>
        values
            .Where(value => !EqualityComparer<T>.Default.Equals(value, fallback))
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault(fallback);

    private static string? MostCommonText(IEnumerable<string?> values) =>
        values
            .Select(Clean)
            .Where(value => value is not null)
            .Select(value => value!)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault();

    private static KvemoCropProfileClassification? ReadClassification(JsonElement root)
    {
        if (!root.TryGetProperty("classification", out var classification)
            || classification.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return new KvemoCropProfileClassification(
            ReadString(classification, "label") ?? "unknown",
            ReadEnum(classification, "category", ObjectCategory.Unknown),
            ReadDouble(classification, "confidence"),
            ReadString(classification, "modelName") ?? "unknown-model",
            ReadString(classification, "modelVersion") ?? "unknown-version");
    }

    private static KvemoCropProfileSourceEvidence ReadSourceEvidence(JsonElement root)
    {
        if (!root.TryGetProperty("sourceEvidence", out var sourceEvidence)
            || sourceEvidence.ValueKind != JsonValueKind.Object)
        {
            return new KvemoCropProfileSourceEvidence(0, Array.Empty<string>(), Array.Empty<string>());
        }

        return new KvemoCropProfileSourceEvidence(
            ReadInt(sourceEvidence, "primitiveCount"),
            ReadStringArray(sourceEvidence, "layers"),
            ReadStringArray(sourceEvidence, "blockNames"));
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Clean(property.GetString());
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => Clean(item.GetString()))
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<KvemoCropManifestLabelProfileCount> ReadProvenanceCounts(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<KvemoCropManifestLabelProfileCount>();
        }

        return property
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new
            {
                Value = ReadString(item, "value"),
                Count = ReadInt(item, "count")
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Value) && item.Count > 0)
            .GroupBy(item => item.Value!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new KvemoCropManifestLabelProfileCount(group.Key, group.Sum(item => item.Count)))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ReadInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt32(out var value)
            ? value
            : 0;

    private static double ReadDouble(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetDouble(out var value)
            ? Math.Clamp(value, 0, 1)
            : 0;

    private static TEnum ReadEnum<TEnum>(JsonElement root, string propertyName, TEnum fallback)
        where TEnum : struct, Enum
    {
        var value = ReadString(root, propertyName);
        return value is not null && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private static double RoundConfidence(double value) =>
        Math.Round(Math.Clamp(value, 0, 1), 2, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record KvemoCropProfileEntry(
        int LineNumber,
        string DetectionId,
        string DetectionKind,
        string? ReviewKey,
        string? GroupSignature,
        int PageNumber,
        ObjectCandidateKind CandidateKind,
        ObjectCategory Category,
        ObjectCandidateSourceKind SourceKind,
        string? SourceWallComponentKind,
        IReadOnlyList<KvemoCropManifestLabelProfileCount> SourceKindCounts,
        IReadOnlyList<KvemoCropManifestLabelProfileCount> SourceWallComponentKindCounts,
        double DeterministicConfidence,
        string? Label,
        string? SymbolName,
        IReadOnlyList<string> DetectedTags,
        IReadOnlyList<string> NearbyText,
        IReadOnlyList<string> SourcePrimitiveIds,
        KvemoCropProfileSourceEvidence SourceEvidence,
        string ReviewPriority,
        IReadOnlyList<string> ReviewReasons,
        string SuggestedTrainingUse,
        KvemoCropProfileClassification? Classification);

    private sealed record KvemoCropProfileSourceEvidence(
        int PrimitiveCount,
        IReadOnlyList<string> Layers,
        IReadOnlyList<string> BlockNames);

    private sealed record KvemoCropProfileClassification(
        string Label,
        ObjectCategory Category,
        double Confidence,
        string ModelName,
        string ModelVersion);

    private sealed record KvemoCropSelector(string Key, bool IsSignature);

    private sealed record KvemoCropManifestLabelProfileCount(string Value, int Count);
}
