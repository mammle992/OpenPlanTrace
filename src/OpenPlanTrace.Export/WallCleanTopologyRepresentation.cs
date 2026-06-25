namespace OpenPlanTrace.Export;

internal sealed record WallCleanTopologyRepresentation(
    WallGraphTopologySpan Span,
    double OverlapRatio,
    double AxisDistance);

internal static class WallCleanTopologyRepresentationMatcher
{
    private const double MinRepresentedByCleanTopologyOverlapRatio = 0.92;
    private const double MinRecoveredRoomBoundaryRepresentedOverlapRatio = 0.80;
    private const double MinNearIsolatedFragmentRepresentedOverlapRatio = 0.88;
    private const double MaxInteriorRepresentedByCleanTopologyAxisDistance = 6.0;
    private const double MaxExteriorRepresentedByCleanTopologyAxisDistance = 18.0;
    private const double MaxNearIsolatedFragmentAxisDistance = 4.0;
    private const int MaxNearIsolatedFragmentCount = 8;
    private const double MaxNearIsolatedFragmentGapRatio = 0.001;

    public static WallCleanTopologyRepresentation? FindBest(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        bool readyForCoordinatePlacement,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan> allCleanTopologySpans,
        bool excludedFromStructuralTopology) =>
        FindRepresentingSpans(
                wall,
                component,
                evidenceAssessment,
                readyForCoordinatePlacement,
                topologySpans,
                allCleanTopologySpans,
                excludedFromStructuralTopology,
                maxResults: 1)
            .FirstOrDefault();

    public static IReadOnlyList<WallCleanTopologyRepresentation> FindRepresentingSpans(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        bool readyForCoordinatePlacement,
        IReadOnlyList<WallGraphTopologySpan> topologySpans,
        IReadOnlyList<WallGraphTopologySpan> allCleanTopologySpans,
        bool excludedFromStructuralTopology,
        int maxResults = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(wall);

        if (topologySpans.Count > 0
            || allCleanTopologySpans.Count == 0
            || excludedFromStructuralTopology
            || component?.Kind == WallGraphComponentKind.ObjectLikeIsland
            || evidenceAssessment?.Category == WallEvidenceCategory.ObjectOrFixtureDetail
            || evidenceAssessment?.RejectedAsNoise == true
            || evidenceAssessment?.Decision == WallEvidenceDecision.Reject
            || wall.CenterLine.Length <= 0.001
            || ResolveOrientation(wall.CenterLine) == RepresentedTopologyOrientation.Unknown)
        {
            return Array.Empty<WallCleanTopologyRepresentation>();
        }

        maxResults = Math.Max(1, maxResults);
        return allCleanTopologySpans
            .Where(span => span.PageNumber == wall.PageNumber)
            .Where(span => !string.Equals(span.WallId, wall.Id, StringComparison.Ordinal))
            .Where(span => ResolveOrientation(span.CenterLine) == ResolveOrientation(wall.CenterLine))
            .Select(span => new WallCleanTopologyRepresentation(
                span,
                OverlapRatio(wall, span),
                AxisDistance(wall, span)))
            .Where(item => item.AxisDistance <= AxisDistanceTolerance(wall, item.Span))
            .Where(item => item.OverlapRatio >= OverlapRatioThreshold(
                wall,
                component,
                evidenceAssessment,
                readyForCoordinatePlacement,
                item.Span,
                item.AxisDistance))
            .OrderByDescending(item => item.OverlapRatio)
            .ThenBy(item => item.AxisDistance)
            .ThenByDescending(item => item.Span.DrawingLength)
            .Take(maxResults)
            .ToArray();
    }

    private static double OverlapRatioThreshold(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        bool readyForCoordinatePlacement,
        WallGraphTopologySpan span,
        double axisDistance) =>
        IsNearIsolatedFragmentCandidate(
            wall,
            component,
            evidenceAssessment,
            readyForCoordinatePlacement,
            span,
            axisDistance)
            ? MinNearIsolatedFragmentRepresentedOverlapRatio
            : IsRecoveredRoomBoundaryPairCandidate(
                wall,
                component,
                evidenceAssessment,
                readyForCoordinatePlacement,
                span)
                ? MinRecoveredRoomBoundaryRepresentedOverlapRatio
            : MinRepresentedByCleanTopologyOverlapRatio;

    private static bool IsRecoveredRoomBoundaryPairCandidate(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        bool readyForCoordinatePlacement,
        WallGraphTopologySpan span)
    {
        if (!readyForCoordinatePlacement
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.WallType != WallType.Interior
            || wall.PairEvidence is not { Score: >= 0.85, OverlapRatio: >= 0.90 }
            || evidenceAssessment?.Category != WallEvidenceCategory.RecoveredWallBody
            || evidenceAssessment.Decision != WallEvidenceDecision.Accept
            || evidenceAssessment.RequiresReview
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.PlacementReady != true
            || span.SourceWall is null
            || span.SourceWall.WallType != WallType.Interior
            || span.DrawingLength < wall.DrawingLength)
        {
            return false;
        }

        return ContainsAnyEvidence(
                wall,
                component,
                evidenceAssessment,
                "geometric room boundary support",
                "shared by room adjacency boundary",
                "explicit room boundary support")
            && !ContainsAnyEvidence(
                wall,
                component,
                evidenceAssessment,
                "outdoor",
                "terrace",
                "covered-area",
                "covered entry",
                "covered-entry",
                "overbygd",
                "surface pattern",
                "object/fixture",
                "fixture detail",
                "door/opening",
                "stair",
                "railing");
    }

    private static bool IsNearIsolatedFragmentCandidate(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        bool readyForCoordinatePlacement,
        WallGraphTopologySpan span,
        double axisDistance)
    {
        if (component?.Kind != WallGraphComponentKind.IsolatedFragment
            || component.ExcludedFromStructuralTopology
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.WallType != WallType.Interior
            || evidenceAssessment?.Category != WallEvidenceCategory.MediumWallBody
            || evidenceAssessment.Decision != WallEvidenceDecision.Review
            || evidenceAssessment.RequiresReview != true
            || evidenceAssessment.RejectedAsNoise
            || readyForCoordinatePlacement
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || fragmentEvidence.FragmentCount > MaxNearIsolatedFragmentCount
            || fragmentEvidence.GapRatio > MaxNearIsolatedFragmentGapRatio
            || axisDistance > MaxNearIsolatedFragmentAxisDistance)
        {
            return false;
        }

        if (span.SourceWall is { WallType: not WallType.Unknown } sourceWall
            && sourceWall.WallType != wall.WallType)
        {
            return false;
        }

        return !ContainsAnyEvidence(
            wall,
            component,
            evidenceAssessment,
            "covered-area",
            "door",
            "fixture",
            "furniture",
            "object",
            "opening",
            "railing",
            "stair",
            "terrace");
    }

    private static double OverlapRatio(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var orientation = ResolveOrientation(wall.CenterLine);
        if (orientation == RepresentedTopologyOrientation.Unknown
            || orientation != ResolveOrientation(span.CenterLine)
            || wall.CenterLine.Length <= 0.001)
        {
            return 0;
        }

        var overlap = Math.Min(AxisMax(wall.CenterLine, orientation), AxisMax(span.CenterLine, orientation))
            - Math.Max(AxisMin(wall.CenterLine, orientation), AxisMin(span.CenterLine, orientation));
        return Math.Clamp(overlap / wall.CenterLine.Length, 0, 1);
    }

    private static double AxisDistance(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var orientation = ResolveOrientation(wall.CenterLine);
        if (orientation == RepresentedTopologyOrientation.Unknown
            || orientation != ResolveOrientation(span.CenterLine))
        {
            return double.PositiveInfinity;
        }

        return Math.Abs(AxisCoordinate(wall.CenterLine, orientation) - AxisCoordinate(span.CenterLine, orientation));
    }

    private static double AxisDistanceTolerance(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var baseTolerance = wall.WallType == WallType.Exterior || span.SourceWall?.WallType == WallType.Exterior
            ? MaxExteriorRepresentedByCleanTopologyAxisDistance
            : MaxInteriorRepresentedByCleanTopologyAxisDistance;
        return Math.Max(baseTolerance, Math.Max(wall.Thickness, span.Thickness) + 1.0);
    }

    private static RepresentedTopologyOrientation ResolveOrientation(PlanLineSegment line)
    {
        if (line.IsHorizontal(2))
        {
            return RepresentedTopologyOrientation.Horizontal;
        }

        return line.IsVertical(2)
            ? RepresentedTopologyOrientation.Vertical
            : RepresentedTopologyOrientation.Unknown;
    }

    private static double AxisMin(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? Math.Min(line.Start.X, line.End.X)
            : Math.Min(line.Start.Y, line.End.Y);

    private static double AxisMax(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? Math.Max(line.Start.X, line.End.X)
            : Math.Max(line.Start.Y, line.End.Y);

    private static double AxisCoordinate(
        PlanLineSegment line,
        RepresentedTopologyOrientation orientation) =>
        orientation == RepresentedTopologyOrientation.Horizontal
            ? (line.Start.Y + line.End.Y) / 2.0
            : (line.Start.X + line.End.X) / 2.0;

    private static bool ContainsAnyEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment,
        params string[] text)
    {
        return AllEvidence().Any(evidence => text.Any(token => evidence.Contains(token, StringComparison.OrdinalIgnoreCase)));

        IEnumerable<string> AllEvidence()
        {
            foreach (var evidence in wall.Evidence)
            {
                yield return evidence;
            }

            foreach (var evidence in wall.FragmentEvidence?.Evidence ?? Array.Empty<string>())
            {
                yield return evidence;
            }

            foreach (var evidence in component?.Evidence ?? Array.Empty<string>())
            {
                yield return evidence;
            }

            if (evidenceAssessment is null)
            {
                yield break;
            }

            foreach (var evidence in evidenceAssessment.Evidence)
            {
                yield return evidence;
            }

            foreach (var evidence in evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            {
                yield return evidence;
            }

            foreach (var evidence in evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            {
                yield return evidence;
            }
        }
    }

    private enum RepresentedTopologyOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }
}
