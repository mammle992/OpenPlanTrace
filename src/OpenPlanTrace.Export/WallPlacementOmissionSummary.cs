using System.Globalization;

namespace OpenPlanTrace.Export;

public sealed record PlanOverlayWallPlacementSummary(
    int PlacementReadyWallCount,
    int PlacementOmittedWallCount,
    IReadOnlyDictionary<string, int> OmissionCounts,
    IReadOnlyList<PlanOverlayWallPlacementOmissionSummary> TopOmissions)
{
    public IEnumerable<string> TopOmissionRows() =>
        TopOmissions.Select(item => $"omit: {item.Label} {item.Count.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record PlanOverlayWallPlacementOmissionSummary(
    string Code,
    string Label,
    int Count,
    bool IsPriority);

internal static class WallPlacementOmissionSummary
{
    private static readonly string[] PriorityOmissionCodes =
    [
        "fragmented_pair_review_required",
        "topology_import_blocked",
        "fragment_geometry_review"
    ];

    public static PlanOverlayWallPlacementSummary From(
        PlanScanResult result,
        int pageNumber,
        int maxTopOmissions = 5)
    {
        var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
        var reviewReasonsByWallId = WallReviewReasonMerger.Merge(
            BuildWallReviewReasons(result.Diagnostics.Messages),
            WallPlacementContextGuards.BuildReviewReasons(result));
        var repairCandidatesByWallId = BuildWallGraphRepairCandidateLookup(result.WallGraph.RepairCandidates);
        var topologySpansByWallId = WallTopologySpanVisibility
            .BuildCleanPlacementTopologySpans(result)
            .Where(span => span.PageNumber == pageNumber)
            .GroupBy(span => span.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var omissionCodes = new List<string>();
        var readyCount = 0;

        foreach (var wall in result.Walls.Where(wall => wall.PageNumber == pageNumber))
        {
            componentByWallId.TryGetValue(wall.Id, out var component);
            wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
            var repairCandidates = repairCandidatesByWallId.TryGetValue(wall.Id, out var wallRepairCandidates)
                ? wallRepairCandidates
                : Array.Empty<WallGraphRepairCandidate>();
            var reviewReasons = reviewReasonsByWallId.TryGetValue(wall.Id, out var wallReviewReasons)
                ? wallReviewReasons
                : Array.Empty<string>();
            var combinedReviewReasons = reviewReasons
                .Concat(repairCandidates.Where(candidate => candidate.RequiresReview).Select(WallGraphRepairReviewReason))
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var topologySpans = topologySpansByWallId.TryGetValue(wall.Id, out var spans)
                ? spans
                : Array.Empty<WallGraphTopologySpan>();
            var excludedFromStructuralTopology =
                WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, assessment);
            var reliability = PlacementReliability.ForWall(
                wall,
                result.Calibration,
                component,
                assessment,
                combinedReviewReasons);
            var omission = PlacementWallOmissionExport.From(
                wall,
                component,
                assessment,
                reliability,
                topologySpans,
                excludedFromStructuralTopology,
                repairCandidates,
                combinedReviewReasons);

            if (omission is null && reliability.ReadyForCoordinatePlacement)
            {
                readyCount++;
            }
            else if (omission is not null)
            {
                omissionCodes.Add(omission.Code);
            }
        }

        var omissionCounts = omissionCodes
            .GroupBy(code => code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var topOmissions = TopOmissions(omissionCounts, maxTopOmissions);
        return new PlanOverlayWallPlacementSummary(
            readyCount,
            omissionCodes.Count,
            omissionCounts,
            topOmissions);
    }

    private static IReadOnlyList<PlanOverlayWallPlacementOmissionSummary> TopOmissions(
        IReadOnlyDictionary<string, int> omissionCounts,
        int maxRows)
    {
        if (maxRows <= 0 || omissionCounts.Count == 0)
        {
            return Array.Empty<PlanOverlayWallPlacementOmissionSummary>();
        }

        var prioritized = PriorityOmissionCodes
            .Where(code => omissionCounts.ContainsKey(code))
            .Select(code => ToSummary(code, omissionCounts[code], isPriority: true))
            .ToArray();
        var remaining = omissionCounts
            .Where(pair => !PriorityOmissionCodes.Contains(pair.Key, StringComparer.Ordinal))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => ToSummary(pair.Key, pair.Value, isPriority: false))
            .ToArray();

        return prioritized
            .Concat(remaining)
            .Take(maxRows)
            .ToArray();
    }

    private static PlanOverlayWallPlacementOmissionSummary ToSummary(
        string code,
        int count,
        bool isPriority) =>
        new(code, OmissionLabel(code), count, isPriority);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildWallReviewReasons(
        IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(message => string.Equals(
                     message.Code,
                     "wall_graph.surface_pattern_wall_overlap.review",
                     StringComparison.Ordinal)))
        {
            if (!diagnostic.Properties.TryGetValue("wallId", out var wallId)
                || string.IsNullOrWhiteSpace(wallId))
            {
                continue;
            }

            if (!reasons.TryGetValue(wallId, out var wallReasons))
            {
                wallReasons = new List<string>();
                reasons[wallId] = wallReasons;
            }

            var surfacePatternId = diagnostic.Properties.TryGetValue("surfacePatternId", out var patternId)
                && !string.IsNullOrWhiteSpace(patternId)
                    ? patternId
                    : "unknown";
            var overlap = diagnostic.Properties.TryGetValue("wallOverlapRatio", out var ratio)
                && !string.IsNullOrWhiteSpace(ratio)
                    ? $" at wall overlap ratio {ratio}"
                    : string.Empty;
            wallReasons.Add($"wall overlaps non-structural surface/detail pattern {surfacePatternId}{overlap}");
        }

        return reasons.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WallGraphRepairCandidate>> BuildWallGraphRepairCandidateLookup(
        IReadOnlyList<WallGraphRepairCandidate> candidates)
    {
        var lookup = new Dictionary<string, List<WallGraphRepairCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var wallId in WallGraphRepairCandidateImpact.CoordinateImpactedWallIds(candidate).Distinct(StringComparer.Ordinal))
            {
                if (!lookup.TryGetValue(wallId, out var wallCandidates))
                {
                    wallCandidates = new List<WallGraphRepairCandidate>();
                    lookup[wallId] = wallCandidates;
                }

                wallCandidates.Add(candidate);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WallGraphRepairCandidate>)pair.Value
                .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static string WallGraphRepairReviewReason(WallGraphRepairCandidate candidate)
    {
        var action = candidate.SuggestedAction switch
        {
            WallGraphRepairAction.TrimEndpointOverrun => "endpoint-overrun trim",
            WallGraphRepairAction.SnapEndpointToWall => "endpoint-to-wall snap",
            WallGraphRepairAction.SnapEndpointToEndpoint => "endpoint-to-endpoint snap",
            _ => candidate.SuggestedAction.ToString()
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"wall graph repair candidate {candidate.Id} requires review for {action} ({candidate.Kind}, {candidate.ImportImpact}, {candidate.GapDistance:0.###} drawing units)");
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
        IReadOnlyList<WallGraphComponent> components)
    {
        var lookup = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    lookup[wallId] = component;
                }
            }
        }

        return lookup;
    }

    private static string OmissionLabel(string code) =>
        code switch
        {
            "duplicate_wall_face" => "duplicate faces",
            "fragmented_pair_review_required" => "fragmented pairs",
            "isolated_fragment" => "isolated fragments",
            "no_clean_topology_spans" => "no clean spans",
            "object_like_linework" => "object linework",
            "rejected_wall_evidence" => "rejected evidence",
            "secondary_without_room_boundary_support" => "secondary no room",
            "topology_import_blocked" => "blocked repairs",
            "wall_evidence_review_required" => "review evidence",
            _ => code.Replace('_', ' ')
        };
}
