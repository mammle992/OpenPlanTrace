namespace OpenPlanTrace;

public sealed record ObjectReviewDatasetOptions
{
    public string? Name { get; init; }

    public string? Version { get; init; } = "draft";

    public bool IncludeReviewGroups { get; init; } = true;

    public bool IncludeKnownGroups { get; init; } = true;

    public bool IncludeUngroupedCandidates { get; init; } = true;

    public double NearbyTextSearchRadius { get; init; } = 90;

    public int MaxNearbyTextPerItem { get; init; } = 6;

    public double ReviewCropPadding { get; init; } = 18;
}

public static class ObjectReviewDatasetBuilder
{
    public static ObjectReviewDataset FromScanResult(
        PlanScanResult result,
        ObjectReviewDatasetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        options ??= new ObjectReviewDatasetOptions();
        var primitiveLookup = BuildPrimitiveLookup(result.Document);
        var candidatesById = result.ObjectCandidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var groupedCandidateIds = result.ObjectGroups
            .SelectMany(group => group.CandidateIds)
            .ToHashSet(StringComparer.Ordinal);

        var groups = result.ObjectGroups
            .Where(group => ShouldInclude(group, options))
            .OrderByDescending(group => group.RequiresReview)
            .ThenByDescending(group => group.Count)
            .ThenBy(group => group.Signature, StringComparer.Ordinal)
            .Select(group => CreateGroup(group, candidatesById, primitiveLookup, result, options))
            .ToArray();

        var ungrouped = options.IncludeUngroupedCandidates
            ? result.ObjectCandidates
                .Where(candidate => !groupedCandidateIds.Contains(candidate.Id))
                .OrderBy(candidate => candidate.PageNumber)
                .ThenBy(candidate => candidate.Bounds.Y)
                .ThenBy(candidate => candidate.Bounds.X)
                .Select(candidate => CreateCandidate(candidate, null, primitiveLookup, result, options))
                .ToArray()
            : Array.Empty<ObjectReviewCandidate>();

        return new ObjectReviewDataset(
            ObjectReviewDataset.CurrentSchemaVersion,
            Clean(options.Name) ?? $"{Clean(result.Document.Id) ?? "OpenPlanTrace"} object review dataset",
            Clean(options.Version) ?? "draft",
            DateTimeOffset.UtcNow,
            result.Document.Id,
            Clean(result.Document.Metadata.SourceName),
            Clean(result.Document.Metadata.SourcePath),
            groups,
            ungrouped);
    }

    private static ObjectReviewGroup CreateGroup(
        ObjectCandidateGroup group,
        IReadOnlyDictionary<string, ObjectCandidate> candidatesById,
        IReadOnlyDictionary<string, PrimitiveLookupItem> primitiveLookup,
        PlanScanResult result,
        ObjectReviewDatasetOptions options)
    {
        var candidates = group.CandidateIds
            .Select(candidateId => candidatesById.TryGetValue(candidateId, out var candidate) ? candidate : null)
            .Where(candidate => candidate is not null)
            .Select(candidate => CreateCandidate(candidate!, group.Id, primitiveLookup, result, options))
            .ToArray();
        var sourceLayers = SourceLayers(group.SourcePrimitiveIds, primitiveLookup);
        var nearbyText = group.NearbyText.Count > 0
            ? group.NearbyText.Select(ToReviewTextEvidence).ToArray()
            : NearbyText(group.PageNumbers, group.RepresentativeBounds, group.SourcePrimitiveIds, result, primitiveLookup, options);

        return new ObjectReviewGroup(
            group.Id,
            group.Signature,
            group.Kind,
            group.Category,
            group.Count,
            group.RepresentativeBounds,
            ReviewCropBounds(group.RepresentativeBounds, group.PageNumbers.FirstOrDefault(), result, options),
            group.PageNumbers,
            group.CandidateIds,
            group.SourcePrimitiveIds,
            sourceLayers,
            group.RequiresReview,
            RoundConfidence(group.Confidence.Value),
            Clean(group.Label),
            Clean(group.SymbolName),
            group.DetectedTags,
            CreateSuggestedRule(group),
            candidates,
            nearbyText,
            group.Evidence);
    }

    private static ObjectReviewCandidate CreateCandidate(
        ObjectCandidate candidate,
        string? groupId,
        IReadOnlyDictionary<string, PrimitiveLookupItem> primitiveLookup,
        PlanScanResult result,
        ObjectReviewDatasetOptions options)
    {
        var nearbyText = candidate.NearbyText.Count > 0
            ? candidate.NearbyText.Select(ToReviewTextEvidence).ToArray()
            : NearbyText(new[] { candidate.PageNumber }, candidate.Bounds, candidate.SourcePrimitiveIds, result, primitiveLookup, options);

        return new ObjectReviewCandidate(
            candidate.Id,
            groupId,
            candidate.PageNumber,
            candidate.Kind,
            candidate.Category,
            candidate.SourceKind,
            Clean(candidate.SourceWallComponentId),
            candidate.SourceWallComponentKind,
            candidate.Bounds,
            ReviewCropBounds(candidate.Bounds, candidate.PageNumber, result, options),
            RoundConfidence(candidate.Confidence.Value),
            Clean(candidate.Label),
            Clean(candidate.SymbolName),
            Clean(candidate.DetectedTag),
            Clean(candidate.DetectedTagSourcePrimitiveId),
            Clean(candidate.RoomId),
            Clean(candidate.RoomLabel),
            candidate.SourcePrimitiveIds,
            SourceLayers(candidate.SourcePrimitiveIds, primitiveLookup),
            nearbyText,
            candidate.Evidence);
    }

    private static ObjectReviewTextEvidence ToReviewTextEvidence(ObjectNearbyText text) =>
        new(
            text.Text,
            text.PageNumber,
            text.Bounds,
            text.SourcePrimitiveId,
            text.Distance);

    private static ObjectReviewRuleSuggestion CreateSuggestedRule(ObjectCandidateGroup group)
    {
        var evidence = new List<string>
        {
            $"Review dataset suggestion from object group {group.Id}.",
            group.RequiresReview
                ? "Human confirmation recommended before reusing this rule."
                : "Existing deterministic classification can be accepted or refined."
        };

        if (group.SourcePrimitiveIds.Count > 0)
        {
            evidence.Add($"{group.SourcePrimitiveIds.Count} source primitive{(group.SourcePrimitiveIds.Count == 1 ? string.Empty : "s")} represented.");
        }

        if (group.DetectedTags.Count > 0)
        {
            evidence.Add($"Detected tags: {string.Join(", ", group.DetectedTags)}.");
        }

        return new ObjectReviewRuleSuggestion(
            group.Signature,
            null,
            null,
            null,
            null,
            null,
            null,
            group.Category,
            group.Kind,
            Clean(group.Label),
            Clean(group.SymbolName),
            group.RequiresReview,
            RoundConfidence(group.Confidence.Value),
            evidence);
    }

    private static IReadOnlyList<ObjectReviewTextEvidence> NearbyText(
        IReadOnlyList<int> pageNumbers,
        PlanRect bounds,
        IReadOnlyList<string> excludedSourcePrimitiveIds,
        PlanScanResult result,
        IReadOnlyDictionary<string, PrimitiveLookupItem> primitiveLookup,
        ObjectReviewDatasetOptions options)
    {
        if (options.MaxNearbyTextPerItem <= 0 || options.NearbyTextSearchRadius <= 0)
        {
            return Array.Empty<ObjectReviewTextEvidence>();
        }

        var pages = pageNumbers.Count == 0
            ? new[] { 1 }
            : pageNumbers.Distinct().ToArray();
        var excluded = excludedSourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
        var searchBounds = bounds.Inflate(options.NearbyTextSearchRadius);

        return result.Document.Pages
            .Where(page => pages.Contains(page.Number))
            .SelectMany(page => page.Primitives.Select((primitive, index) => new
            {
                Page = page,
                Primitive = primitive,
                SourceId = PrimitiveId(page.Number, index, primitive)
            }))
            .Where(item => item.Primitive is TextPrimitive text
                && !string.IsNullOrWhiteSpace(text.Text)
                && !excluded.Contains(item.SourceId)
                && item.Primitive.Bounds.Intersects(searchBounds))
            .Select(item =>
            {
                var text = (TextPrimitive)item.Primitive;
                return new ObjectReviewTextEvidence(
                    text.Text.Trim(),
                    item.Page.Number,
                    text.Bounds,
                    item.SourceId,
                    Math.Round(DistanceBetween(bounds, text.Bounds), 3));
            })
            .OrderBy(text => text.Distance)
            .ThenBy(text => text.PageNumber)
            .ThenBy(text => text.Bounds.Y)
            .ThenBy(text => text.Bounds.X)
            .Take(options.MaxNearbyTextPerItem)
            .ToArray();
    }

    private static IReadOnlyList<string> SourceLayers(
        IReadOnlyList<string> sourcePrimitiveIds,
        IReadOnlyDictionary<string, PrimitiveLookupItem> primitiveLookup) =>
        sourcePrimitiveIds
            .Select(sourceId => primitiveLookup.TryGetValue(sourceId, out var item) ? item.Layer : null)
            .Where(layer => !string.IsNullOrWhiteSpace(layer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(layer => layer!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyDictionary<string, PrimitiveLookupItem> BuildPrimitiveLookup(PlanDocument document)
    {
        var lookup = new Dictionary<string, PrimitiveLookupItem>(StringComparer.Ordinal);
        foreach (var page in document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var sourceId = PrimitiveId(page.Number, index, primitive);
                lookup[sourceId] = new PrimitiveLookupItem(
                    sourceId,
                    Clean(primitive.Source.Layer) ?? Clean(primitive.Layer),
                    primitive);
            }
        }

        return lookup;
    }

    private static bool ShouldInclude(
        ObjectCandidateGroup group,
        ObjectReviewDatasetOptions options)
    {
        if (string.IsNullOrWhiteSpace(group.Signature))
        {
            return false;
        }

        return group.RequiresReview
            ? options.IncludeReviewGroups
            : options.IncludeKnownGroups;
    }

    private static double DistanceBetween(PlanRect first, PlanRect second) =>
        first.Intersects(second)
            ? 0
            : first.Center.DistanceTo(second.Center);

    private static PlanRect ReviewCropBounds(
        PlanRect bounds,
        int pageNumber,
        PlanScanResult result,
        ObjectReviewDatasetOptions options)
    {
        var padded = bounds.Inflate(Math.Max(0, options.ReviewCropPadding));
        var page = result.Document.Pages.FirstOrDefault(item => item.Number == pageNumber)
            ?? result.Document.Pages.FirstOrDefault();
        return page is null
            ? padded
            : padded.ClampTo(new PlanRect(0, 0, page.Size.Width, page.Size.Height));
    }

    private static string PrimitiveId(int pageNumber, int primitiveIndex, PlanPrimitive primitive) =>
        primitive.SourceId ?? primitive.Source.SourceId ?? $"p{pageNumber}:primitive:{primitiveIndex}";

    private static double RoundConfidence(double value) =>
        Math.Round(Math.Clamp(value, 0, 1), 2, MidpointRounding.AwayFromZero);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PrimitiveLookupItem(
        string SourceId,
        string? Layer,
        PlanPrimitive Primitive);
}
