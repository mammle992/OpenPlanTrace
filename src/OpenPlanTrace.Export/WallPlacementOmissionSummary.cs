using System.Globalization;
using System.Text.RegularExpressions;
using static OpenPlanTrace.Export.PlacementMetricTransform;

namespace OpenPlanTrace.Export;

public sealed record PlanOverlayWallPlacementSummary(
    int PlacementReadyWallCount,
    int PlacementOmittedWallCount,
    int RepresentedWallCount,
    int PlacementSuppressedWallCount,
    int PlacementReviewWallCount,
    PlanOverlayWallGraphResidualSummary ResidualEndpointOnHostWall,
    IReadOnlyDictionary<string, int> OmissionCounts,
    IReadOnlyList<PlanOverlayWallPlacementOmissionSummary> TopOmissions,
    IReadOnlyList<PlanOverlayWallPlacementOmittedWallExample> OmittedWallExamples)
{
    public IEnumerable<string> TopOmissionRows() =>
        TopOmissions.Select(item => $"omit: {item.Label} {item.Count.ToString(CultureInfo.InvariantCulture)}");
}

public sealed record PlanOverlayWallPlacementOmissionSummary(
    string Code,
    string Label,
    int Count,
    bool IsPriority);

public sealed record PlanOverlayWallGraphResidualSummary(
    int CandidateEndpointCount,
    int CoincidentCandidateEndpointCount,
    int SameAxisCandidateEndpointCount,
    int PerpendicularCandidateEndpointCount,
    double MaxDistance)
{
    public static PlanOverlayWallGraphResidualSummary Empty { get; } = new(0, 0, 0, 0, 0);

    public static PlanOverlayWallGraphResidualSummary FromCandidates(
        IEnumerable<PlacementWallGraphResidualEndpointOnHostCandidateExport>? candidates,
        int pageNumber)
    {
        if (candidates is null)
        {
            return Empty;
        }

        var pageCandidates = candidates
            .Where(candidate => candidate.PageNumber == pageNumber)
            .ToArray();
        if (pageCandidates.Length == 0)
        {
            return Empty;
        }

        return new PlanOverlayWallGraphResidualSummary(
            pageCandidates.Length,
            pageCandidates.Count(candidate => candidate.DistanceDrawingUnits <= 1.0),
            pageCandidates.Count(candidate => string.Equals(candidate.Relationship, "SameAxis", StringComparison.Ordinal)),
            pageCandidates.Count(candidate => string.Equals(candidate.Relationship, "Perpendicular", StringComparison.Ordinal)),
            PlanOverlaySnapshot.Round(pageCandidates.Max(candidate => candidate.DistanceDrawingUnits)));
    }

    public static PlanOverlayWallGraphResidualSummary FromEvidence(IReadOnlyList<string>? evidence)
    {
        var line = evidence?.FirstOrDefault(item =>
            item.StartsWith(
                "placement wall graph residual endpoint-on-host-wall candidates after cleanup:",
                StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
        {
            return Empty;
        }

        var match = Regex.Match(
            line,
            @"(?<total>\d+) total,\s+(?<coincident>\d+) coincident,\s+(?<sameAxis>\d+) same-axis,\s+(?<perpendicular>\d+) perpendicular,\s+max distance (?<distance>[0-9]+(?:[\.,][0-9]+)?) drawing units",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return Empty;
        }

        return new PlanOverlayWallGraphResidualSummary(
            ParseInt(match, "total"),
            ParseInt(match, "coincident"),
            ParseInt(match, "sameAxis"),
            ParseInt(match, "perpendicular"),
            PlanOverlaySnapshot.Round(ParseDouble(match, "distance")));
    }

    private static int ParseInt(Match match, string groupName) =>
        int.TryParse(match.Groups[groupName].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double ParseDouble(Match match, string groupName) =>
        double.TryParse(
            match.Groups[groupName].Value.Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
}

public sealed record PlanOverlayWallPlacementOmittedWallExample(
    string WallId,
    int PageNumber,
    string Code,
    string Label,
    bool IsPriority,
    string WallType,
    string DetectionKind,
    double Confidence,
    double DrawingLength,
    PlanRectSnapshot Bounds,
    LineExport CenterLine,
    int TopologySpanCount,
    int SourcePrimitiveCount,
    IReadOnlyList<string> Evidence);

internal static class WallPlacementOmissionSummary
{
    private static readonly string[] PriorityOmissionCodes =
    [
        "fragmented_pair_review_required",
        "fragmented_interior_without_room_boundary_support",
        "weak_promoted_fragment_room_boundary_review_required",
        "opening_detail_fragment_review_required",
        "one_endpoint_fragment_review_required",
        "main_structural_semantic_support_review_required",
        "fragmented_short_parallel_pair_review_required",
        "very_short_parallel_pair_review_required",
        "short_parallel_pair_review_required",
        "covered_area_boundary_review_required",
        "repeated_short_detail_review_required",
        "opening_linked_isolated_fragment_suppressed",
        "tiny_door_adjacent_topology_suppressed",
        "short_dense_detail_review_required",
        "secondary_over_sourced_detail_linework",
        "topology_import_blocked",
        "fragment_geometry_review",
    ];

    public static PlanOverlayWallPlacementSummary From(
        PlanScanResult result,
        int pageNumber,
        int maxTopOmissions = 5,
        int maxOmittedWallExamples = 12,
        PlacementPageSummaryExport? placementPageSummary = null,
        PlacementWallGraphExport? placementWallGraph = null)
    {
        var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
        var reviewReasonsByWallId = WallReviewReasonMerger.Merge(
            BuildWallReviewReasons(result.Diagnostics.Messages),
            WallPlacementContextGuards.BuildReviewReasons(result));
        var repairCandidatesByWallId = BuildWallGraphRepairCandidateLookup(result.WallGraph.RepairCandidates);
        var openingsByWallId = BuildWallOpeningLookup(result.Openings);
        var cleanTopologySpans = WallTopologySpanVisibility
            .BuildCleanPlacementTopologySpans(result)
            .Where(span => span.PageNumber == pageNumber)
            .ToArray();
        var topologySpansByWallId = cleanTopologySpans
            .GroupBy(span => span.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var omissionCodes = new List<string>();
        var omittedWalls = new List<PlanOverlayWallPlacementOmittedWallExample>();
        var readyCount = 0;
        var representedCount = 0;
        var suppressedCount = 0;

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
            var scale = ResolveMillimetersPerDrawingUnit(result.Calibration, wall.MeasurementScaleGroupId);
            var wallOpenings = openingsByWallId.TryGetValue(wall.Id, out var linkedOpenings)
                ? linkedOpenings
                : Array.Empty<OpeningCandidate>();
            var cutouts = wallOpenings.Count > 0
                ? wallOpenings
                    .Select((opening, index) => PlacementWallOpeningCutoutExport.From(wall, opening, scale, index + 1))
                    .Where(cutout => cutout is not null)
                    .Select(cutout => cutout!)
                    .DistinctBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
                    .OrderBy(cutout => cutout.StartParameter)
                    .ThenBy(cutout => cutout.EndParameter)
                    .ThenBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<PlacementWallOpeningCutoutExport>();
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
                cleanTopologySpans,
                cutouts,
                wallOpenings,
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
                if (IsRepresentedWall(omission.Code))
                {
                    representedCount++;
                }
                else if (IsPlacementSuppressedWall(omission.Code))
                {
                    suppressedCount++;
                }

                omittedWalls.Add(ToOmittedWallExample(wall, omission, topologySpans));
            }
        }

        var omissionCounts = omissionCodes
            .GroupBy(code => code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var topOmissions = TopOmissions(omissionCounts, maxTopOmissions);
        var summary = new PlanOverlayWallPlacementSummary(
            readyCount,
            omissionCodes.Count,
            representedCount,
            suppressedCount,
            Math.Max(0, omissionCodes.Count - representedCount - suppressedCount),
            ExtractResidualEndpointOnHostWallSummary(placementWallGraph, pageNumber),
            omissionCounts,
            topOmissions,
            TopOmittedWallExamples(omittedWalls, maxOmittedWallExamples));
        return placementPageSummary is null
            ? summary
            : summary with
            {
                PlacementReadyWallCount = placementPageSummary.PlacementReadyWallCount,
                PlacementOmittedWallCount = placementPageSummary.PlacementOmittedWallCount,
                RepresentedWallCount = placementPageSummary.RepresentedWallCount,
                PlacementSuppressedWallCount = placementPageSummary.PlacementSuppressedWallCount,
                PlacementReviewWallCount = placementPageSummary.PlacementReviewWallCount,
                OmissionCounts = placementPageSummary.WallPlacementOmissionCounts,
                TopOmissions = TopOmissions(placementPageSummary.WallPlacementOmissionCounts, maxTopOmissions)
            };
    }

    private static PlanOverlayWallGraphResidualSummary ExtractResidualEndpointOnHostWallSummary(
        PlacementWallGraphExport? placementWallGraph,
        int pageNumber)
    {
        if (placementWallGraph is null)
        {
            return PlanOverlayWallGraphResidualSummary.Empty;
        }

        var summary = PlanOverlayWallGraphResidualSummary.FromCandidates(
            placementWallGraph.ResidualEndpointOnHostCandidates,
            pageNumber);
        return summary.CandidateEndpointCount > 0
            ? summary
            : PlanOverlayWallGraphResidualSummary.FromEvidence(placementWallGraph.Evidence);
    }

    private static bool IsRepresentedWall(string code) =>
        string.Equals(code, "duplicate_clean_topology_span", StringComparison.Ordinal)
        || string.Equals(code, "duplicate_wall_face", StringComparison.Ordinal);

    private static bool IsPlacementSuppressedWall(string code) =>
        string.Equals(code, "rejected_wall_evidence", StringComparison.Ordinal)
        || string.Equals(code, "object_like_linework", StringComparison.Ordinal)
        || string.Equals(code, "structural_topology_excluded", StringComparison.Ordinal)
        || string.Equals(code, "opening_consumed_wall_remainder", StringComparison.Ordinal)
        || string.Equals(code, "opening_linked_isolated_fragment_suppressed", StringComparison.Ordinal)
        || string.Equals(code, "repeated_short_detail_review_required", StringComparison.Ordinal)
        || string.Equals(code, "tiny_door_adjacent_topology_suppressed", StringComparison.Ordinal);

    private static IReadOnlyList<PlanOverlayWallPlacementOmittedWallExample> TopOmittedWallExamples(
        IReadOnlyList<PlanOverlayWallPlacementOmittedWallExample> examples,
        int maxRows)
    {
        if (maxRows <= 0 || examples.Count == 0)
        {
            return Array.Empty<PlanOverlayWallPlacementOmittedWallExample>();
        }

        return examples
            .OrderByDescending(item => item.IsPriority)
            .ThenBy(item => OmissionSortRank(item.Code))
            .ThenByDescending(item => item.Confidence)
            .ThenByDescending(item => item.DrawingLength)
            .ThenBy(item => item.WallId, StringComparer.Ordinal)
            .Take(maxRows)
            .ToArray();
    }

    private static PlanOverlayWallPlacementOmittedWallExample ToOmittedWallExample(
        WallSegment wall,
        PlacementWallOmissionExport omission,
        IReadOnlyList<WallGraphTopologySpan> topologySpans) =>
        new(
            wall.Id,
            wall.PageNumber,
            omission.Code,
            OmissionLabel(omission.Code),
            PriorityOmissionCodes.Contains(omission.Code, StringComparer.Ordinal),
            wall.WallType.ToString(),
            wall.DetectionKind.ToString(),
            PlanOverlaySnapshot.Round(wall.Confidence.Value),
            PlanOverlaySnapshot.Round(wall.DrawingLength),
            PlanRectSnapshot.From(wall.Bounds),
            LineExport.From(wall.CenterLine),
            topologySpans.Count,
            wall.SourcePrimitiveIds.Count,
            omission.Evidence.Take(4).ToArray());

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

    private static int OmissionSortRank(string code)
    {
        var priorityIndex = Array.IndexOf(PriorityOmissionCodes, code);
        if (priorityIndex >= 0)
        {
            return priorityIndex;
        }

        return code switch
        {
            "no_clean_topology_spans" => 10,
            "wall_evidence_review_required" => 20,
            "fragmented_interior_without_room_boundary_support" => 24,
            "weak_promoted_fragment_room_boundary_review_required" => 24,
            "opening_detail_fragment_review_required" => 24,
            "one_endpoint_fragment_review_required" => 24,
            "main_structural_semantic_support_review_required" => 25,
            "secondary_object_linework_without_room_boundary_support" => 25,
            "secondary_over_sourced_detail_linework" => 26,
            "short_dense_detail_review_required" => 27,
            "fragmented_short_parallel_pair_review_required" => 27,
            "very_short_parallel_pair_review_required" => 28,
            "short_parallel_pair_review_required" => 29,
            "covered_area_boundary_review_required" => 29,
            "secondary_without_room_boundary_support" => 30,
            "isolated_fragment" => 40,
            "rejected_wall_evidence" => 80,
            "object_like_linework" => 82,
            "structural_topology_excluded" => 84,
            "opening_consumed_wall_remainder" => 86,
            "opening_linked_isolated_fragment_suppressed" => 87,
            "repeated_short_detail_review_required" => 88,
            "tiny_door_adjacent_topology_suppressed" => 89,
            "duplicate_clean_topology_span" => 90,
            "duplicate_wall_face" => 92,
            _ => 100
        };
    }

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

    private static IReadOnlyDictionary<string, IReadOnlyList<OpeningCandidate>> BuildWallOpeningLookup(
        IReadOnlyList<OpeningCandidate> openings)
    {
        var lookup = new Dictionary<string, List<OpeningCandidate>>(StringComparer.Ordinal);
        foreach (var opening in openings.Where(opening => opening.Placement is not null))
        {
            foreach (var wallId in OpeningWallIds(opening))
            {
                if (!lookup.TryGetValue(wallId, out var wallOpenings))
                {
                    wallOpenings = new List<OpeningCandidate>();
                    lookup[wallId] = wallOpenings;
                }

                wallOpenings.Add(opening);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<OpeningCandidate>)pair.Value
                .DistinctBy(opening => opening.Id, StringComparer.Ordinal)
                .OrderBy(opening => opening.Placement?.HostWallCenterParameter ?? 0)
                .ThenBy(opening => opening.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> OpeningWallIds(OpeningCandidate opening)
    {
        if (opening.Placement?.HostWallId is { Length: > 0 } hostWallId)
        {
            yield return hostWallId;
        }

        foreach (var wallId in opening.HostWallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }

        if (opening.Placement is null)
        {
            yield break;
        }

        foreach (var wallId in opening.Placement.AnchorWallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }
    }

    private static string OmissionLabel(string code) =>
        code switch
        {
            "duplicate_clean_topology_span" => "duplicate clean spans",
            "duplicate_wall_face" => "duplicate faces",
            "covered_area_boundary_review_required" => "covered/outdoor boundaries",
            "fragmented_interior_without_room_boundary_support" => "fragmented interior no room",
            "fragmented_pair_review_required" => "fragmented pairs",
            "isolated_fragment" => "isolated fragments",
            "no_clean_topology_spans" => "no clean spans",
            "object_like_linework" => "object linework",
            "weak_promoted_fragment_room_boundary_review_required" => "weak promoted fragments",
            "opening_consumed_wall_remainder" => "opening-consumed walls",
            "opening_linked_isolated_fragment_suppressed" => "opening-linked fragments",
            "opening_detail_fragment_review_required" => "opening detail fragments",
            "one_endpoint_fragment_review_required" => "one-ended fragments",
            "main_structural_semantic_support_review_required" => "main semantic support",
            "rejected_wall_evidence" => "rejected evidence",
            "fragmented_short_parallel_pair_review_required" => "fragmented short pairs",
            "repeated_short_detail_review_required" => "repeated short details",
            "secondary_object_linework_without_room_boundary_support" => "secondary object linework",
            "secondary_over_sourced_detail_linework" => "secondary source clutter",
            "secondary_without_room_boundary_support" => "secondary no room",
            "short_dense_detail_review_required" => "short dense details",
            "short_parallel_pair_review_required" => "short paired reviews",
            "very_short_parallel_pair_review_required" => "very short pairs",
            "tiny_door_adjacent_topology_suppressed" => "tiny door slivers",
            "topology_import_blocked" => "blocked repairs",
            "wall_evidence_review_required" => "review evidence",
            _ => code.Replace('_', ' ')
        };
}
