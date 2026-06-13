using System.Text.Json;

namespace OpenPlanTrace;

public enum ObjectCorrectionTargetKind
{
    Group = 0,
    Candidate
}

public enum ObjectCorrectionDecision
{
    Unreviewed = 0,
    Confirmed,
    Corrected,
    Ignored,
    Unknown
}

public enum ObjectCorrectionApplyScope
{
    TargetOnly = 0,
    MatchingSignature,
    MatchingSymbolAndLayer,
    MatchingDetectedTagPattern
}

public sealed record ObjectCorrectionDataset(
    string SchemaVersion,
    string? Name,
    string? Version,
    DateTimeOffset CreatedAt,
    string? SourceReviewDatasetSchemaVersion,
    string? DocumentId,
    string? SourceName,
    string? SourcePath,
    IReadOnlyList<ObjectCorrectionAction> Actions)
{
    public const string CurrentSchemaVersion = "openplantrace.object-correction-dataset.v1";

    public static ObjectCorrectionDataset ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Object correction dataset JSON is empty.", nameof(json));
        }

        ObjectCorrectionDatasetDto? dataset;
        try
        {
            dataset = JsonSerializer.Deserialize<ObjectCorrectionDatasetDto>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Object correction dataset JSON is invalid: {exception.Message}", exception);
        }

        if (dataset is null)
        {
            throw new ArgumentException("Object correction dataset JSON did not contain an object.", nameof(json));
        }

        var schemaVersion = string.IsNullOrWhiteSpace(dataset.SchemaVersion)
            ? CurrentSchemaVersion
            : dataset.SchemaVersion.Trim();
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported object correction dataset schemaVersion '{schemaVersion}'. Expected '{CurrentSchemaVersion}'.");
        }

        if (dataset.Actions is null)
        {
            throw new ArgumentException("Object correction dataset requires an actions array.");
        }

        var actions = new List<ObjectCorrectionAction>();
        var index = 0;
        foreach (var action in dataset.Actions)
        {
            index++;
            actions.Add(ParseAction(action, index));
        }

        return new ObjectCorrectionDataset(
            CurrentSchemaVersion,
            Clean(dataset.Name),
            Clean(dataset.Version),
            dataset.CreatedAt ?? DateTimeOffset.UtcNow,
            Clean(dataset.SourceReviewDatasetSchemaVersion),
            Clean(dataset.DocumentId),
            Clean(dataset.SourceName),
            Clean(dataset.SourcePath),
            actions);
    }

    public ObjectLabelProfile ToObjectLabelProfile(ObjectCorrectionLabelProfileOptions? options = null) =>
        ObjectCorrectionLabelProfileBuilder.FromCorrections(this, options);

    private static ObjectCorrectionAction ParseAction(ObjectCorrectionActionDto action, int index)
    {
        var actionId = RequireClean(action.ActionId, $"object correction action #{index} actionId");
        var targetKind = ParseRequiredEnum<ObjectCorrectionTargetKind>(
            action.TargetKind,
            $"object correction action #{index} targetKind");
        var decision = ParseRequiredEnum<ObjectCorrectionDecision>(
            action.Decision,
            $"object correction action #{index} decision");
        var applyScope = ParseRequiredEnum<ObjectCorrectionApplyScope>(
            action.ApplyScope,
            $"object correction action #{index} applyScope");
        var correctedCategory = ParseOptionalEnum<ObjectCategory>(
            action.CorrectedCategory,
            $"object correction action #{index} correctedCategory");
        var correctedKind = ParseOptionalEnum<ObjectCandidateKind>(
            action.CorrectedKind,
            $"object correction action #{index} correctedKind");

        if (decision is ObjectCorrectionDecision.Confirmed or ObjectCorrectionDecision.Corrected)
        {
            var hasOutput = correctedCategory is not null
                || correctedKind is not null
                || !string.IsNullOrWhiteSpace(action.CorrectedLabel)
                || !string.IsNullOrWhiteSpace(action.CorrectedSymbolName)
                || action.RequiresReview is not null;
            if (!hasOutput)
            {
                throw new ArgumentException($"Object correction action #{index} is reviewed but has no corrected label output.");
            }
        }

        if (action.Confidence is < 0 or > 1)
        {
            throw new ArgumentException($"Object correction action #{index} confidence must be between 0 and 1.");
        }

        return new ObjectCorrectionAction(
            actionId,
            targetKind,
            decision,
            applyScope,
            Clean(action.GroupId),
            Clean(action.CandidateId),
            Clean(action.Signature),
            ParseOptionalEnum<ObjectCandidateKind>(action.OriginalKind, $"object correction action #{index} originalKind"),
            ParseOptionalEnum<ObjectCategory>(action.OriginalCategory, $"object correction action #{index} originalCategory"),
            Clean(action.OriginalLabel),
            Clean(action.OriginalSymbolName),
            correctedKind,
            correctedCategory,
            Clean(action.CorrectedLabel),
            Clean(action.CorrectedSymbolName),
            action.RequiresReview,
            action.Confidence,
            action.ReviewCropBounds,
            CleanList(action.DetectedTags),
            CleanPageNumbers(action.PageNumbers),
            CleanList(action.CandidateIds),
            CleanList(action.SourcePrimitiveIds),
            CleanList(action.SourceLayers),
            action.NearbyText ?? Array.Empty<ObjectReviewTextEvidence>(),
            Clean(action.Reviewer),
            action.ReviewedAt,
            CleanList(action.Evidence));
    }

    private static TEnum ParseRequiredEnum<TEnum>(string? value, string fieldName)
        where TEnum : struct, Enum
    {
        var cleaned = RequireClean(value, fieldName);

        if (Enum.TryParse<TEnum>(cleaned, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"{fieldName} has invalid value '{cleaned}'. Valid values: {string.Join(", ", Enum.GetNames<TEnum>())}.");
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

    private static IReadOnlyList<string> CleanList(IReadOnlyList<string?>? values) =>
        values?
            .Select(Clean)
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .ToArray()
        ?? Array.Empty<string>();

    private static IReadOnlyList<int> CleanPageNumbers(IReadOnlyList<int>? values) =>
        values?
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToArray()
        ?? Array.Empty<int>();

    private static string RequireClean(string? value, string fieldName) =>
        Clean(value)
        ?? throw new ArgumentException($"{fieldName} is required.");

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ObjectCorrectionDatasetDto(
        string? SchemaVersion,
        string? Name,
        string? Version,
        DateTimeOffset? CreatedAt,
        string? SourceReviewDatasetSchemaVersion,
        string? DocumentId,
        string? SourceName,
        string? SourcePath,
        IReadOnlyList<ObjectCorrectionActionDto>? Actions);

    private sealed record ObjectCorrectionActionDto(
        string? ActionId,
        string? TargetKind,
        string? Decision,
        string? ApplyScope,
        string? GroupId,
        string? CandidateId,
        string? Signature,
        string? OriginalKind,
        string? OriginalCategory,
        string? OriginalLabel,
        string? OriginalSymbolName,
        string? CorrectedKind,
        string? CorrectedCategory,
        string? CorrectedLabel,
        string? CorrectedSymbolName,
        bool? RequiresReview,
        double? Confidence,
        PlanRect? ReviewCropBounds,
        IReadOnlyList<string?>? DetectedTags,
        IReadOnlyList<int>? PageNumbers,
        IReadOnlyList<string?>? CandidateIds,
        IReadOnlyList<string?>? SourcePrimitiveIds,
        IReadOnlyList<string?>? SourceLayers,
        IReadOnlyList<ObjectReviewTextEvidence>? NearbyText,
        string? Reviewer,
        DateTimeOffset? ReviewedAt,
        IReadOnlyList<string?>? Evidence);
}

public sealed record ObjectCorrectionAction(
    string ActionId,
    ObjectCorrectionTargetKind TargetKind,
    ObjectCorrectionDecision Decision,
    ObjectCorrectionApplyScope ApplyScope,
    string? GroupId,
    string? CandidateId,
    string? Signature,
    ObjectCandidateKind? OriginalKind,
    ObjectCategory? OriginalCategory,
    string? OriginalLabel,
    string? OriginalSymbolName,
    ObjectCandidateKind? CorrectedKind,
    ObjectCategory? CorrectedCategory,
    string? CorrectedLabel,
    string? CorrectedSymbolName,
    bool? RequiresReview,
    double? Confidence,
    PlanRect? ReviewCropBounds,
    IReadOnlyList<string> DetectedTags,
    IReadOnlyList<int> PageNumbers,
    IReadOnlyList<string> CandidateIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceLayers,
    IReadOnlyList<ObjectReviewTextEvidence> NearbyText,
    string? Reviewer,
    DateTimeOffset? ReviewedAt,
    IReadOnlyList<string> Evidence)
{
    public bool IsReviewed =>
        Decision is ObjectCorrectionDecision.Confirmed
            or ObjectCorrectionDecision.Corrected
            or ObjectCorrectionDecision.Ignored
            or ObjectCorrectionDecision.Unknown;
}

public sealed record ObjectCorrectionLabelProfileOptions
{
    public string? Name { get; init; }

    public string? Version { get; init; }

    public bool IncludeConfirmed { get; init; } = true;

    public bool IncludeCorrected { get; init; } = true;
}

public static class ObjectCorrectionLabelProfileBuilder
{
    public static ObjectLabelProfile FromCorrections(
        ObjectCorrectionDataset dataset,
        ObjectCorrectionLabelProfileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        options ??= new ObjectCorrectionLabelProfileOptions();
        var rules = dataset.Actions
            .Where(action => ShouldCreateRule(action, options))
            .OrderBy(action => action.Signature, StringComparer.Ordinal)
            .ThenBy(action => action.ActionId, StringComparer.Ordinal)
            .Select(ToRule)
            .ToArray();

        return new ObjectLabelProfile(
            ObjectLabelProfile.CurrentSchemaVersion,
            Clean(options.Name) ?? $"{Clean(dataset.DocumentId) ?? "OpenPlanTrace"} object correction labels",
            Clean(options.Version) ?? Clean(dataset.Version) ?? "corrections",
            rules);
    }

    private static bool ShouldCreateRule(
        ObjectCorrectionAction action,
        ObjectCorrectionLabelProfileOptions options)
    {
        if (action.Decision == ObjectCorrectionDecision.Confirmed && !options.IncludeConfirmed)
        {
            return false;
        }

        if (action.Decision == ObjectCorrectionDecision.Corrected && !options.IncludeCorrected)
        {
            return false;
        }

        if (action.Decision is not (ObjectCorrectionDecision.Confirmed or ObjectCorrectionDecision.Corrected))
        {
            return false;
        }

        var hasOutput = action.CorrectedCategory is not null
            || action.CorrectedKind is not null
            || !string.IsNullOrWhiteSpace(action.CorrectedLabel)
            || !string.IsNullOrWhiteSpace(action.CorrectedSymbolName)
            || action.RequiresReview is not null
            || action.Confidence is not null;
        if (!hasOutput)
        {
            return false;
        }

        return action.ApplyScope switch
        {
            ObjectCorrectionApplyScope.MatchingSignature => !string.IsNullOrWhiteSpace(action.Signature),
            ObjectCorrectionApplyScope.MatchingDetectedTagPattern => CommonDetectedTagPattern(action.DetectedTags) is not null,
            ObjectCorrectionApplyScope.MatchingSymbolAndLayer =>
                !string.IsNullOrWhiteSpace(action.CorrectedSymbolName ?? action.OriginalSymbolName)
                || action.SourceLayers.Count > 0,
            _ => false
        };
    }

    private static ObjectLabelRule ToRule(ObjectCorrectionAction action)
    {
        var evidence = new List<string>
        {
            $"Created from object correction action {action.ActionId}.",
            $"Human decision: {action.Decision}."
        };

        if (action.CandidateIds.Count > 0)
        {
            evidence.Add(
                $"Correction applies to {action.CandidateIds.Count} reviewed occurrence{(action.CandidateIds.Count == 1 ? string.Empty : "s")} in the source plan.");
        }

        if (action.PageNumbers.Count > 0)
        {
            evidence.Add($"Reviewed occurrence pages: {string.Join(", ", action.PageNumbers)}.");
        }

        if (action.DetectedTags.Count > 0)
        {
            evidence.Add($"Reviewed occurrence tags: {string.Join(", ", action.DetectedTags)}.");
        }

        if (action.ApplyScope == ObjectCorrectionApplyScope.MatchingDetectedTagPattern
            && CommonDetectedTagPattern(action.DetectedTags) is { } tagPattern)
        {
            evidence.Add($"Correction applies by detected tag pattern {tagPattern}.");
        }

        evidence.AddRange(action.Evidence);

        if (!string.IsNullOrWhiteSpace(action.Reviewer))
        {
            evidence.Add($"Reviewer: {action.Reviewer}.");
        }

        if (action.ReviewedAt is not null)
        {
            evidence.Add($"Reviewed at {action.ReviewedAt.Value:O}.");
        }

        return new ObjectLabelRule
        {
            Signature = action.ApplyScope == ObjectCorrectionApplyScope.MatchingSignature
                ? Clean(action.Signature)
                : null,
            DetectedTagPattern = action.ApplyScope == ObjectCorrectionApplyScope.MatchingDetectedTagPattern
                ? CommonDetectedTagPattern(action.DetectedTags)
                : null,
            SymbolNamePattern = action.ApplyScope == ObjectCorrectionApplyScope.MatchingSymbolAndLayer
                ? Clean(action.CorrectedSymbolName) ?? Clean(action.OriginalSymbolName)
                : null,
            LayerPattern = action.ApplyScope == ObjectCorrectionApplyScope.MatchingSymbolAndLayer
                ? action.SourceLayers.FirstOrDefault()
                : null,
            Category = action.CorrectedCategory ?? action.OriginalCategory,
            Kind = action.CorrectedKind ?? action.OriginalKind,
            Label = Clean(action.CorrectedLabel) ?? Clean(action.OriginalLabel),
            SymbolName = Clean(action.CorrectedSymbolName) ?? Clean(action.OriginalSymbolName),
            RequiresReview = action.RequiresReview ?? false,
            Confidence = action.Confidence is null ? null : new Confidence(action.Confidence.Value),
            Evidence = evidence
        };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
}
