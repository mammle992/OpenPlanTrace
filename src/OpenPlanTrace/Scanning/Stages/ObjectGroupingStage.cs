namespace OpenPlanTrace;

internal sealed class ObjectGroupingStage : IPipelineStage
{
    public string Name => "object-groups";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var sourceLookup = BuildSourceLookup(context.Document);
        var groupable = context.ObjectCandidates
            .Where(candidate => candidate.Kind != ObjectCandidateKind.TextLabel)
            .Select(candidate => new CandidateWithSignature(
                candidate,
                CreateSignature(candidate, sourceLookup),
                SourceLayers(candidate, sourceLookup).ToArray(),
                SourceFormats(candidate, sourceLookup).ToArray()))
            .ToArray();

        var groups = groupable
            .GroupBy(item => item.Signature.Key, StringComparer.Ordinal)
            .Select((group, index) => CreateGroup(group.ToArray(), index + 1, context.Options.ObjectLabelRules))
            .Where(group => group is not null)
            .Select(group => group!)
            .OrderByDescending(group => group.RequiresReview)
            .ThenByDescending(group => group.Count)
            .ThenBy(group => group.Signature, StringComparer.Ordinal)
            .ToArray();

        ApplyGroupLabelsToCandidates(context, groups);
        context.ObjectGroups.AddRange(groups);

        if (groups.Length > 0)
        {
            var profileLabeledGroups = groups.Count(IsProfileLabeled);
            context.AddDiagnostic(
                "object_groups.detected",
                DiagnosticSeverity.Info,
                Name,
                $"Grouped {groupable.Length} object candidate(s) into {groups.Length} deterministic object group(s).",
                confidence: groups.Any(group => group.RequiresReview) ? Confidence.Medium : Confidence.High,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: groups.SelectMany(group => group.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["candidateCount"] = groupable.Length.ToString(),
                    ["groupCount"] = groups.Length.ToString(),
                    ["reviewGroupCount"] = groups.Count(group => group.RequiresReview).ToString(),
                    ["repeatedGroupCount"] = groups.Count(group => group.Count > 1).ToString(),
                    ["profileRuleCount"] = context.Options.ObjectLabelRules.Count.ToString(),
                    ["profileLabeledGroupCount"] = profileLabeledGroups.ToString()
                });
        }

        return ValueTask.CompletedTask;
    }

    private static ObjectCandidateGroup? CreateGroup(
        IReadOnlyList<CandidateWithSignature> items,
        int groupNumber,
        IReadOnlyList<ObjectLabelRule> labelRules)
    {
        var candidates = items
            .Select(item => item.Candidate)
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .ToArray();
        var first = candidates[0];
        var signature = items[0].Signature;
        var sourceLayers = items
            .SelectMany(item => item.SourceLayers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(layer => layer, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sourceFormats = items
            .SelectMany(item => item.SourceFormats)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(format => format, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var labelRule = FindLabelRule(items, signature, sourceLayers, sourceFormats, labelRules);
        var category = labelRule?.Category ?? first.Category;
        var kind = labelRule?.Kind ?? (labelRule?.Category is { } ruleCategory ? KindFor(ruleCategory) : first.Kind);
        var label = FirstNonEmpty(new[] { labelRule?.Label }.Concat(candidates.Select(candidate => candidate.Label)));
        var symbolName = FirstNonEmpty(new[] { labelRule?.SymbolName }.Concat(candidates.Select(candidate => candidate.SymbolName)));
        var detectedTags = candidates
            .Select(candidate => candidate.DetectedTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var requiresReview = labelRule?.RequiresReview ?? category is ObjectCategory.Unknown or ObjectCategory.GenericSymbol;

        if (candidates.Length == 1 && !requiresReview && labelRule is null)
        {
            return null;
        }

        var evidence = new List<string>
        {
            $"grouped {candidates.Length} object candidate(s) by {signature.Description}",
            $"category {category}",
            $"kind {kind}"
        };

        if (sourceLayers.Length > 0)
        {
            evidence.Add($"source layers: {string.Join(", ", sourceLayers)}");
        }

        var nearbyText = NearbyText(candidates);
        if (nearbyText.Length > 0)
        {
            evidence.Add($"nearby text: {string.Join(", ", nearbyText.Select(item => item.Text).Take(4))}");
        }

        if (detectedTags.Length > 0)
        {
            evidence.Add($"detected tags: {string.Join(", ", detectedTags.Take(8))}");
        }

        if (labelRule is not null)
        {
            evidence.Add($"object label profile matched {LabelRuleSelectorDescription(labelRule)}");
            if (!string.IsNullOrWhiteSpace(labelRule.Label))
            {
                evidence.Add($"profile label '{labelRule.Label}'");
            }

            if (labelRule.Category is not null)
            {
                evidence.Add($"profile category {labelRule.Category}");
            }

            evidence.AddRange(labelRule.Evidence.Select(item => $"profile evidence: {item}"));
        }

        if (requiresReview)
        {
            evidence.Add("review recommended for generic/unknown symbol group");
        }

        var averageConfidence = candidates.Average(candidate => candidate.Confidence.Value);
        var profileConfidence = labelRule?.Confidence?.Value
            ?? (labelRule is null ? averageConfidence : Math.Max(averageConfidence, 0.84));

        return new ObjectCandidateGroup(
            $"object-group:{groupNumber}",
            signature.Key,
            kind,
            category,
            candidates.Length,
            first.Bounds,
            candidates.Select(candidate => candidate.PageNumber).Distinct().Order().ToArray(),
            candidates.Select(candidate => candidate.Id).ToArray(),
            candidates
                .SelectMany(candidate => candidate.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            requiresReview,
            new Confidence(Math.Clamp(Math.Max(averageConfidence, profileConfidence), 0.35, 0.96)),
            evidence)
        {
            Label = label,
            SymbolName = symbolName,
            DetectedTags = detectedTags,
            NearbyText = nearbyText
        };
    }

    private static ObjectNearbyText[] NearbyText(IReadOnlyList<ObjectCandidate> candidates) =>
        candidates
            .SelectMany(candidate => candidate.NearbyText)
            .GroupBy(text => text.SourcePrimitiveId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(text => text.Distance).First())
            .OrderBy(text => text.Distance)
            .ThenBy(text => text.PageNumber)
            .ThenBy(text => text.Bounds.Top)
            .ThenBy(text => text.Bounds.Left)
            .Take(12)
            .ToArray();

    private static void ApplyGroupLabelsToCandidates(
        ScanContext context,
        IReadOnlyList<ObjectCandidateGroup> groups)
    {
        foreach (var group in groups.Where(IsProfileLabeled))
        {
            foreach (var candidateId in group.CandidateIds)
            {
                var index = context.ObjectCandidates.FindIndex(candidate => candidate.Id == candidateId);
                if (index < 0)
                {
                    continue;
                }

                var candidate = context.ObjectCandidates[index];
                var evidence = candidate.Evidence
                    .Concat(new[]
                    {
                        $"object label profile applied via group {group.Signature}"
                    })
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                context.ObjectCandidates[index] = candidate with
                {
                    Kind = group.Kind,
                    Category = group.Category,
                    Label = group.Label ?? candidate.Label,
                    SymbolName = group.SymbolName ?? candidate.SymbolName,
                    Confidence = new Confidence(Math.Clamp(Math.Max(candidate.Confidence.Value, group.Confidence.Value - 0.04), 0.35, 0.96)),
                    Evidence = evidence
                };
            }
        }
    }

    private static bool IsProfileLabeled(ObjectCandidateGroup group) =>
        group.Evidence.Any(item => item.StartsWith("object label profile matched", StringComparison.Ordinal));

    private static ObjectLabelRule? FindLabelRule(
        IReadOnlyList<CandidateWithSignature> items,
        ObjectGroupSignature signature,
        IReadOnlyList<string> sourceLayers,
        IReadOnlyList<string> sourceFormats,
        IReadOnlyList<ObjectLabelRule> labelRules) =>
        labelRules.FirstOrDefault(rule => RuleMatches(rule, items, signature, sourceLayers, sourceFormats));

    private static bool RuleMatches(
        ObjectLabelRule rule,
        IReadOnlyList<CandidateWithSignature> items,
        ObjectGroupSignature signature,
        IReadOnlyList<string> sourceLayers,
        IReadOnlyList<string> sourceFormats)
    {
        if (!rule.HasSelector)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Signature)
            && !string.Equals(rule.Signature.Trim(), signature.Key, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SourceFormat)
            && !sourceFormats.Any(format => string.Equals(format, rule.SourceFormat.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.LayerPattern)
            && !sourceLayers.Any(layer => MatchesPattern(layer, rule.LayerPattern.Trim())))
        {
            return false;
        }

        var candidates = items.Select(item => item.Candidate).ToArray();
        if (!string.IsNullOrWhiteSpace(rule.DetectedTagPattern)
            && !candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.DetectedTag)
                && MatchesPattern(candidate.DetectedTag, rule.DetectedTagPattern.Trim())))
        {
            return false;
        }

        if (rule.MatchCategory is { } matchCategory
            && !candidates.Any(candidate => candidate.Category == matchCategory))
        {
            return false;
        }

        if (rule.MatchKind is { } matchKind
            && !candidates.Any(candidate => candidate.Kind == matchKind))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.SymbolNamePattern)
            && !candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.SymbolName) && MatchesPattern(candidate.SymbolName, rule.SymbolNamePattern.Trim())))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.LabelPattern)
            && !candidates.Any(candidate => !string.IsNullOrWhiteSpace(candidate.Label) && MatchesPattern(candidate.Label, rule.LabelPattern.Trim())))
        {
            return false;
        }

        return true;
    }

    private static string LabelRuleSelectorDescription(ObjectLabelRule rule)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rule.Signature))
        {
            parts.Add($"signature '{rule.Signature}'");
        }

        if (!string.IsNullOrWhiteSpace(rule.SymbolNamePattern))
        {
            parts.Add($"symbol pattern '{rule.SymbolNamePattern}'");
        }

        if (!string.IsNullOrWhiteSpace(rule.LabelPattern))
        {
            parts.Add($"label pattern '{rule.LabelPattern}'");
        }

        if (!string.IsNullOrWhiteSpace(rule.LayerPattern))
        {
            parts.Add($"layer pattern '{rule.LayerPattern}'");
        }

        if (!string.IsNullOrWhiteSpace(rule.DetectedTagPattern))
        {
            parts.Add($"detected tag pattern '{rule.DetectedTagPattern}'");
        }

        if (!string.IsNullOrWhiteSpace(rule.SourceFormat))
        {
            parts.Add($"source format '{rule.SourceFormat}'");
        }

        if (rule.MatchCategory is not null)
        {
            parts.Add($"category {rule.MatchCategory}");
        }

        if (rule.MatchKind is not null)
        {
            parts.Add($"kind {rule.MatchKind}");
        }

        return parts.Count == 0 ? "unspecified selector" : string.Join(", ", parts);
    }

    private static ObjectGroupSignature CreateSignature(
        ObjectCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceMetadata> sourceLookup)
    {
        var layers = SourceLayers(candidate, sourceLookup)
            .Select(Normalize)
            .OrderBy(layer => layer, StringComparer.Ordinal)
            .ToArray();
        var layerPart = layers.Length == 0 ? "unlayered" : string.Join("+", layers);

        if (!string.IsNullOrWhiteSpace(candidate.SymbolName))
        {
            return new ObjectGroupSignature(
                $"symbol:{Normalize(candidate.SymbolName)}|category:{candidate.Category}|kind:{candidate.Kind}|layers:{layerPart}",
                $"symbol name '{candidate.SymbolName}' and source layer set");
        }

        if (!string.IsNullOrWhiteSpace(candidate.Label))
        {
            return new ObjectGroupSignature(
                $"label:{Normalize(candidate.Label)}|category:{candidate.Category}|kind:{candidate.Kind}|layers:{layerPart}",
                $"label '{candidate.Label}' and source layer set");
        }

        var widthBucket = Bucket(candidate.Bounds.Width);
        var heightBucket = Bucket(candidate.Bounds.Height);
        return new ObjectGroupSignature(
            $"geometry:{widthBucket}x{heightBucket}|category:{candidate.Category}|kind:{candidate.Kind}|layers:{layerPart}",
            $"geometry size bucket {widthBucket}x{heightBucket} and source layer set");
    }

    private static IReadOnlyDictionary<string, PrimitiveSourceMetadata> BuildSourceLookup(PlanDocument document)
    {
        var result = new Dictionary<string, PrimitiveSourceMetadata>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var sourceId = primitive.SourceId ?? primitive.Source.SourceId ?? $"p{page.Number}:primitive:{index}";
                result[sourceId] = primitive.Source;
            }
        }

        return result;
    }

    private static IEnumerable<string> SourceLayers(
        ObjectCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceMetadata> sourceLookup) =>
        candidate.SourcePrimitiveIds
            .Select(sourceId => sourceLookup.TryGetValue(sourceId, out var source) ? source.Layer : null)
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Select(layer => layer!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SourceFormats(
        ObjectCandidate candidate,
        IReadOnlyDictionary<string, PrimitiveSourceMetadata> sourceLookup) =>
        candidate.SourcePrimitiveIds
            .Select(sourceId => sourceLookup.TryGetValue(sourceId, out var source) ? source.SourceFormat : null)
            .Where(sourceFormat => !string.IsNullOrWhiteSpace(sourceFormat))
            .Select(sourceFormat => sourceFormat!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static ObjectCandidateKind KindFor(ObjectCategory category) =>
        category switch
        {
            ObjectCategory.Fixture or ObjectCategory.PlumbingFixture => ObjectCandidateKind.Fixture,
            ObjectCategory.Furniture => ObjectCandidateKind.Furniture,
            ObjectCategory.Vehicle => ObjectCandidateKind.Vehicle,
            ObjectCategory.Stair => ObjectCandidateKind.Stair,
            _ => ObjectCandidateKind.Symbol
        };

    private static string Normalize(string value) =>
        value.Trim().ToLowerInvariant().Replace('\\', '-').Replace('/', '-').Replace('.', '-').Replace(' ', '-');

    private static int Bucket(double value) =>
        Math.Max(1, (int)Math.Round(value / 5.0) * 5);

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

        if (!pattern.StartsWith("*", StringComparison.Ordinal)
            && !value.StartsWith(parts[0], StringComparison.OrdinalIgnoreCase))
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

    private sealed record CandidateWithSignature(
        ObjectCandidate Candidate,
        ObjectGroupSignature Signature,
        IReadOnlyList<string> SourceLayers,
        IReadOnlyList<string> SourceFormats);

    private sealed record ObjectGroupSignature(string Key, string Description);
}
