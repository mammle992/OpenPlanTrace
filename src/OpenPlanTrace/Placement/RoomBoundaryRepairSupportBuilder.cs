namespace OpenPlanTrace;

internal sealed record RoomBoundaryRepairSupport(
    string WallId,
    string RoomId,
    int PageNumber,
    PlanLineSegment RoomBoundaryEdge,
    double AxisDistance,
    double OverlapLength,
    double WallCoverageRatio,
    double EdgeCoverageRatio,
    IReadOnlyList<string> Evidence)
{
    public double Score => OverlapLength
        + (WallCoverageRatio * 48.0)
        + (EdgeCoverageRatio * 32.0)
        - (AxisDistance * 4.0);
}

internal sealed record RoomBoundaryRepairSupportResult(
    IReadOnlyDictionary<string, RoomBoundaryRepairSupport> SupportByWallId,
    int UnsupportedRoomBoundaryEdgeCount,
    int CandidateWallCount)
{
    public static RoomBoundaryRepairSupportResult Empty { get; } =
        new(
            new Dictionary<string, RoomBoundaryRepairSupport>(StringComparer.Ordinal),
            0,
            0);
}

internal static class RoomBoundaryRepairSupportBuilder
{
    public static RoomBoundaryRepairSupportResult Build(
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<WallSegment> placementWalls,
        IReadOnlyList<WallSegment> candidateWalls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (rooms.Count == 0 || candidateWalls.Count == 0 || evidenceByWallId.Count == 0)
        {
            return RoomBoundaryRepairSupportResult.Empty;
        }

        var supportByWallId = new Dictionary<string, RoomBoundaryRepairSupport>(StringComparer.Ordinal);
        var unsupportedEdgeCount = 0;
        var candidateWallCount = 0;
        foreach (var edge in ReliableIndoorRoomBoundaryEdges(rooms))
        {
            if (IsAlreadyCoveredByPlacementReadyWall(edge, placementWalls, evidenceByWallId, options))
            {
                continue;
            }

            unsupportedEdgeCount++;
            foreach (var wall in candidateWalls)
            {
                if (wall.PageNumber != edge.PageNumber
                    || !evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                    || !IsRepairCandidate(wall, assessment, options)
                    || !TryMatchRoomBoundaryEdge(edge.Line, wall, options, relaxed: true, out var match)
                    || HasRepairSupportBlocker(
                        wall,
                        assessment,
                        allowGraphObjectLikeReclassification: IsGraphObjectLikeReclassificationCandidate(
                            EvidenceFor(wall, assessment))))
                {
                    continue;
                }

                candidateWallCount++;
                var support = new RoomBoundaryRepairSupport(
                    wall.Id,
                    edge.RoomId,
                    wall.PageNumber,
                    edge.Line,
                    match.AxisDistance,
                    match.OverlapLength,
                    match.WallCoverageRatio,
                    match.EdgeCoverageRatio,
                    new[]
                    {
                        "wall evidence: unsupported room-boundary edge matched review wall candidate",
                        $"wall evidence: room-boundary repair candidate room {edge.RoomId}, axis distance {match.AxisDistance:0.###}, overlap {match.OverlapLength:0.###}, wall coverage {match.WallCoverageRatio:0.###}, edge coverage {match.EdgeCoverageRatio:0.###}"
                    });

                if (!supportByWallId.TryGetValue(wall.Id, out var existing)
                    || support.Score > existing.Score)
                {
                    supportByWallId[wall.Id] = support;
                }
            }
        }

        return new RoomBoundaryRepairSupportResult(
            supportByWallId,
            unsupportedEdgeCount,
            candidateWallCount);
    }

    private static IEnumerable<RoomBoundaryEdge> ReliableIndoorRoomBoundaryEdges(IReadOnlyList<RoomRegion> rooms)
    {
        foreach (var room in rooms)
        {
            if (!CanUseRoomForRepair(room))
            {
                continue;
            }

            for (var index = 0; index < room.Boundary.Count; index++)
            {
                var current = room.Boundary[index];
                var next = room.Boundary[(index + 1) % room.Boundary.Count];
                var edge = new PlanLineSegment(current, next);
                if (edge.Length < 18.0 || !TryResolveAxisInterval(edge, out _, out _, out _, out _))
                {
                    continue;
                }

                yield return new RoomBoundaryEdge(room.Id, room.PageNumber, edge);
            }
        }
    }

    private static bool CanUseRoomForRepair(RoomRegion room)
    {
        if (room.UseKind == RoomUseKind.Outdoor
            || room.Boundary.Count < 4
            || room.Confidence.Value < 0.55)
        {
            return false;
        }

        return RoomBoundaryReliability.HasReliableBoundaryEvidence(room);
    }

    private static bool IsAlreadyCoveredByPlacementReadyWall(
        RoomBoundaryEdge edge,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        var coveredLength = 0.0;
        foreach (var wall in walls)
        {
            if (wall.PageNumber != edge.PageNumber
                || !evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                || !assessment.PlacementReady
                || assessment.RequiresReview
                || assessment.RejectedAsNoise
                || assessment.Decision == WallEvidenceDecision.Reject
                || !TryMatchRoomBoundaryEdge(edge.Line, wall, options, relaxed: false, out var match))
            {
                continue;
            }

            coveredLength += match.OverlapLength;
            if (coveredLength / Math.Max(edge.Line.Length, 0.001) >= 0.62)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRepairCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (assessment.PlacementReady
            || !assessment.RequiresReview
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength < Math.Max(20.0, options.MinWallLength)
            || wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        var isRejectedCandidate = assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject;
        if (isRejectedCandidate)
        {
            return IsRecoverableRejectedRepairCandidate(wall, assessment, options);
        }

        if (assessment.Category is not (WallEvidenceCategory.StrongWallBody
            or WallEvidenceCategory.MediumWallBody
            or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            return wall.PairEvidence?.Score is null or >= 0.55;
        }

        if (wall.DetectionKind == WallDetectionKind.FragmentMerged)
        {
            return wall.FragmentEvidence?.RequiresGeometryReview == false
                && wall.DrawingLength >= Math.Max(42.0, options.MinWallLength * 1.75);
        }

        if (wall.DetectionKind == WallDetectionKind.SingleLine)
        {
            var evidence = wall.Evidence.Concat(assessment.Evidence).ToArray();
            return wall.DrawingLength >= Math.Max(72.0, options.MinWallLength * 3.0)
                && evidence.Any(item =>
                    item.Contains("wall-like layer", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("medium wall body", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsRecoverableRejectedRepairCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        var evidence = EvidenceFor(wall, assessment);
        var graphObjectLikeFalsePositive = IsGraphObjectLikeReclassificationCandidate(evidence);
        if (assessment.Category is not (WallEvidenceCategory.StrongWallBody
            or WallEvidenceCategory.MediumWallBody
            or WallEvidenceCategory.RecoveredWallBody)
            && !(assessment.Category == WallEvidenceCategory.ObjectOrFixtureDetail && graphObjectLikeFalsePositive))
        {
            return false;
        }

        if (assessment.Confidence.Value < 0.72
            || wall.DrawingLength < Math.Max(48.0, options.MinWallLength * 2.0)
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < 0.82
            || pair.OverlapRatio < 0.90
            || pair.FaceSeparation < 2.0
            || pair.FaceSeparation > Math.Max(18.0, options.DefaultWallThickness * 4.0)
            || HasRepairSupportBlocker(wall, assessment, graphObjectLikeFalsePositive))
        {
            return false;
        }

        return true;
    }

    private static bool TryMatchRoomBoundaryEdge(
        PlanLineSegment edge,
        WallSegment wall,
        ScannerOptions options,
        bool relaxed,
        out BoundaryMatch match)
    {
        match = default;
        if (!TryResolveAxisInterval(edge, out var edgeOrientation, out var edgeCoordinate, out var edgeStart, out var edgeEnd)
            || !TryResolveAxisInterval(wall.CenterLine, out var wallOrientation, out var wallCoordinate, out var wallStart, out var wallEnd)
            || edgeOrientation != wallOrientation)
        {
            return false;
        }

        var coordinateTolerance = relaxed
            ? Math.Max(options.WallSnapTolerance * 5.0, Math.Max(7.0, wall.Thickness * 2.25))
            : Math.Max(options.WallSnapTolerance * 2.5, Math.Max(4.0, wall.Thickness * 1.5));
        var axisDistance = Math.Abs(edgeCoordinate - wallCoordinate);
        if (axisDistance > coordinateTolerance)
        {
            return false;
        }

        var overlap = Math.Min(edgeEnd, wallEnd) - Math.Max(edgeStart, wallStart);
        if (overlap <= 0)
        {
            return false;
        }

        var wallCoverage = overlap / Math.Max(wall.DrawingLength, 0.001);
        var edgeCoverage = overlap / Math.Max(edge.Length, 0.001);
        var minimumOverlap = relaxed
            ? Math.Max(18.0, Math.Min(wall.DrawingLength, edge.Length) * 0.42)
            : Math.Max(18.0, Math.Min(wall.DrawingLength, edge.Length) * 0.55);
        if (overlap < minimumOverlap)
        {
            return false;
        }

        if (relaxed)
        {
            if (wallCoverage < 0.52 && overlap < 54.0)
            {
                return false;
            }

            if (edgeCoverage < 0.24 && overlap < 72.0)
            {
                return false;
            }
        }
        else if (wallCoverage < 0.45 || edgeCoverage < 0.35)
        {
            return false;
        }

        match = new BoundaryMatch(axisDistance, overlap, wallCoverage, edgeCoverage);
        return true;
    }

    private static bool HasRepairSupportBlocker(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool allowGraphObjectLikeReclassification = false)
    {
        var evidence = EvidenceFor(wall, assessment);

        if (evidence.Any(item => IsHardBlockerEvidence(item, allowGraphObjectLikeReclassification)))
        {
            return true;
        }

        var dimensionLike = evidence.Any(item =>
            item.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
            || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
            || item.Contains("witness/extension", StringComparison.OrdinalIgnoreCase));
        if (!dimensionLike)
        {
            return false;
        }

        return wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is null
            || wall.PairEvidence.Score < 0.82
            || wall.PairEvidence.OverlapRatio < 0.92;
    }

    private static IReadOnlyList<string> EvidenceFor(
        WallSegment wall,
        WallEvidenceWallAssessment assessment) =>
        wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

    private static bool IsHardBlockerEvidence(
        string evidence,
        bool allowGraphObjectLikeReclassification)
    {
        if (allowGraphObjectLikeReclassification
            && (IsGraphObjectLikeReclassificationEvidence(evidence)
                || evidence.Contains("explicit non-wall evidence: ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return evidence.Contains("outdoor", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("terrace", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-area", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("canopy", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door swing", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("door arc", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("railing", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("stair", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("non-wall", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("already represented", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGraphObjectLikeReclassificationCandidate(IReadOnlyList<string> evidence) =>
        evidence.Any(IsGraphObjectLikeReclassificationEvidence)
        && evidence.Any(item =>
            item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase)
            || item.Contains("strong double-edge wall body", StringComparison.OrdinalIgnoreCase)
            || item.Contains("supported wall evidence inside exterior envelope", StringComparison.OrdinalIgnoreCase));

    private static bool IsGraphObjectLikeReclassificationEvidence(string evidence) =>
        evidence.Contains("graph component", StringComparison.OrdinalIgnoreCase)
        && (evidence.Contains("ObjectLikeIsland", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("object-like linework", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("reclassified as object/fixture detail", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("component excluded from structural topology as compact object-like linework", StringComparison.OrdinalIgnoreCase));

    private static bool TryResolveAxisInterval(
        PlanLineSegment line,
        out AxisOrientation orientation,
        out double coordinate,
        out double start,
        out double end)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        if (dx >= dy && dy <= Math.Max(1.0, dx * 0.02))
        {
            orientation = AxisOrientation.Horizontal;
            coordinate = (line.Start.Y + line.End.Y) / 2.0;
            start = Math.Min(line.Start.X, line.End.X);
            end = Math.Max(line.Start.X, line.End.X);
            return true;
        }

        if (dy > dx && dx <= Math.Max(1.0, dy * 0.02))
        {
            orientation = AxisOrientation.Vertical;
            coordinate = (line.Start.X + line.End.X) / 2.0;
            start = Math.Min(line.Start.Y, line.End.Y);
            end = Math.Max(line.Start.Y, line.End.Y);
            return true;
        }

        orientation = AxisOrientation.Unknown;
        coordinate = 0;
        start = 0;
        end = 0;
        return false;
    }

    private enum AxisOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }

    private readonly record struct RoomBoundaryEdge(string RoomId, int PageNumber, PlanLineSegment Line);

    private readonly record struct BoundaryMatch(
        double AxisDistance,
        double OverlapLength,
        double WallCoverageRatio,
        double EdgeCoverageRatio);
}
