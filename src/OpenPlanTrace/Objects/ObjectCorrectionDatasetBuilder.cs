namespace OpenPlanTrace;

public sealed record ObjectCorrectionDatasetOptions
{
    public string? Name { get; init; }

    public string? Version { get; init; } = "draft";

    public bool IncludeReviewGroups { get; init; } = true;

    public bool IncludeKnownGroups { get; init; } = true;

    public bool IncludeUngroupedCandidates { get; init; }
}

public static class ObjectCorrectionDatasetBuilder
{
    public static ObjectCorrectionDataset FromScanResult(
        PlanScanResult result,
        ObjectCorrectionDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var reviewDataset = ObjectReviewDatasetBuilder.FromScanResult(
            result,
            new ObjectReviewDatasetOptions
            {
                Name = options?.Name,
                Version = options?.Version,
                IncludeReviewGroups = options?.IncludeReviewGroups ?? true,
                IncludeKnownGroups = options?.IncludeKnownGroups ?? true,
                IncludeUngroupedCandidates = options?.IncludeUngroupedCandidates ?? false
            });

        return FromReviewDataset(reviewDataset, options);
    }

    public static ObjectCorrectionDataset FromReviewDataset(
        ObjectReviewDataset reviewDataset,
        ObjectCorrectionDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(reviewDataset);

        options ??= new ObjectCorrectionDatasetOptions();
        var actions = reviewDataset.Groups
            .Where(group => ShouldInclude(group.RequiresReview, options))
            .OrderByDescending(group => group.RequiresReview)
            .ThenByDescending(group => group.Count)
            .ThenBy(group => group.Signature, StringComparer.Ordinal)
            .Select(CreateGroupAction)
            .Concat(options.IncludeUngroupedCandidates
                ? reviewDataset.UngroupedCandidates.Select(CreateCandidateAction)
                : Array.Empty<ObjectCorrectionAction>())
            .ToArray();

        return new ObjectCorrectionDataset(
            ObjectCorrectionDataset.CurrentSchemaVersion,
            Clean(options.Name) ?? $"{Clean(reviewDataset.DocumentId) ?? "OpenPlanTrace"} object corrections",
            Clean(options.Version) ?? "draft",
            DateTimeOffset.UtcNow,
            reviewDataset.SchemaVersion,
            Clean(reviewDataset.DocumentId),
            Clean(reviewDataset.SourceName),
            Clean(reviewDataset.SourcePath),
            actions);
    }

    private static ObjectCorrectionAction CreateGroupAction(ObjectReviewGroup group)
    {
        var evidence = new List<string>
        {
            $"Drafted from OpenPlanTrace object review group {group.GroupId}.",
            $"{group.Count} grouped candidate{(group.Count == 1 ? string.Empty : "s")} share this signature.",
            "Change decision to Confirmed or Corrected before converting this action into reusable label rules."
        };
        evidence.AddRange(group.Evidence);

        return new ObjectCorrectionAction(
            $"group:{group.GroupId}",
            ObjectCorrectionTargetKind.Group,
            ObjectCorrectionDecision.Unreviewed,
            ObjectCorrectionApplyScope.MatchingSignature,
            group.GroupId,
            null,
            group.Signature,
            group.Kind,
            group.Category,
            Clean(group.Label),
            Clean(group.SymbolName),
            group.Kind,
            group.Category,
            Clean(group.Label),
            Clean(group.SymbolName),
            group.RequiresReview,
            group.Confidence,
            group.ReviewCropBounds,
            group.DetectedTags,
            group.PageNumbers,
            group.CandidateIds,
            group.SourcePrimitiveIds,
            group.SourceLayers,
            group.NearbyText,
            null,
            null,
            evidence);
    }

    private static ObjectCorrectionAction CreateCandidateAction(ObjectReviewCandidate candidate)
    {
        var evidence = new List<string>
        {
            $"Drafted from OpenPlanTrace object candidate {candidate.CandidateId}.",
            "Per-candidate corrections are retained for review history; use grouped signatures for reusable label rules when possible."
        };
        evidence.AddRange(candidate.Evidence);

        return new ObjectCorrectionAction(
            $"candidate:{candidate.CandidateId}",
            ObjectCorrectionTargetKind.Candidate,
            ObjectCorrectionDecision.Unreviewed,
            ObjectCorrectionApplyScope.TargetOnly,
            candidate.GroupId,
            candidate.CandidateId,
            null,
            candidate.Kind,
            candidate.Category,
            Clean(candidate.Label),
            Clean(candidate.SymbolName),
            candidate.Kind,
            candidate.Category,
            Clean(candidate.Label),
            Clean(candidate.SymbolName),
            true,
            candidate.Confidence,
            candidate.ReviewCropBounds,
            Clean(candidate.DetectedTag) is null ? Array.Empty<string>() : new[] { Clean(candidate.DetectedTag)! },
            new[] { candidate.PageNumber },
            new[] { candidate.CandidateId },
            candidate.SourcePrimitiveIds,
            candidate.SourceLayers,
            candidate.NearbyText,
            null,
            null,
            evidence);
    }

    private static bool ShouldInclude(bool requiresReview, ObjectCorrectionDatasetOptions options) =>
        requiresReview ? options.IncludeReviewGroups : options.IncludeKnownGroups;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
