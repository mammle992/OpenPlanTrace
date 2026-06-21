namespace OpenPlanTrace;

public static class WallPlacementContextGuards
{
    public const string SecondaryStructuralWithoutRoomBoundarySupportReason =
        "secondary structural wall component lacks room-boundary support";

    public const string SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason =
        "secondary structural wall overlaps detected stair/object linework without room-boundary support";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildReviewReasons(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var roomWallIds = BuildRoomWallIds(result.Rooms);
        if (roomWallIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        }

        var componentByWallId = BuildComponentByWallId(result.WallGraph.Components);
        var wallById = result.Walls
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var wallEvidenceByWallId = result.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .ToDictionary(assessment => assessment.WallId, StringComparer.Ordinal);
        var reasonsByWallId = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var objectLineworkCandidatesByPage = BuildObjectLineworkCandidatesByPage(result.ObjectCandidates);

        foreach (var wall in result.Walls)
        {
            componentByWallId.TryGetValue(wall.Id, out var component);
            var hasRoomBoundarySupport = SecondaryStructuralComponentHasRoomBoundarySupport(component, roomWallIds);
            if (!hasRoomBoundarySupport
                && SecondaryStructuralWallOverlapsObjectLinework(
                    wall,
                    component,
                    objectLineworkCandidatesByPage))
            {
                AddReason(
                    reasonsByWallId,
                    wall.Id,
                    SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason);
            }
            else if (!hasRoomBoundarySupport
                && !SecondaryStructuralComponentHasTrustedPairedWallBodySupport(
                    component,
                    wallById,
                    wallEvidenceByWallId))
            {
                AddReason(reasonsByWallId, wall.Id, SecondaryStructuralWithoutRoomBoundarySupportReason);
            }
        }

        return reasonsByWallId.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    public static bool SecondaryStructuralComponentHasRoomBoundarySupport(
        WallGraphComponent? component,
        IReadOnlySet<string> roomWallIds)
    {
        ArgumentNullException.ThrowIfNull(roomWallIds);

        if (component?.Kind != WallGraphComponentKind.SecondaryStructural)
        {
            return true;
        }

        return component.WallIds.Any(roomWallIds.Contains);
    }

    private static IReadOnlyDictionary<int, IReadOnlyList<ObjectCandidate>> BuildObjectLineworkCandidatesByPage(
        IReadOnlyList<ObjectCandidate> objectCandidates)
    {
        return objectCandidates
            .Where(IsWallContaminatingObjectLinework)
            .GroupBy(candidate => candidate.PageNumber)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ObjectCandidate>)group.ToArray());
    }

    private static bool SecondaryStructuralWallOverlapsObjectLinework(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyDictionary<int, IReadOnlyList<ObjectCandidate>> objectLineworkCandidatesByPage)
    {
        if (component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength < 36
            || (!wall.CenterLine.IsHorizontal() && !wall.CenterLine.IsVertical())
            || !objectLineworkCandidatesByPage.TryGetValue(wall.PageNumber, out var candidates))
        {
            return false;
        }

        var guardTolerance = Math.Max(8, wall.Thickness * 1.5);
        foreach (var candidate in candidates)
        {
            if (LineOverlapsCandidateGuardZone(
                wall.CenterLine,
                candidate.Bounds.Inflate(guardTolerance),
                minimumOverlapLength: Math.Min(42, Math.Max(24, wall.DrawingLength * 0.35)),
                minimumOverlapRatio: 0.45))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWallContaminatingObjectLinework(ObjectCandidate candidate) =>
        candidate.Kind == ObjectCandidateKind.Stair
        || candidate.Category == ObjectCategory.Stair
        || candidate.Evidence.Any(item =>
            item.Contains("nearby text", StringComparison.OrdinalIgnoreCase)
            && item.Contains("trapp", StringComparison.OrdinalIgnoreCase));

    private static bool LineOverlapsCandidateGuardZone(
        PlanLineSegment line,
        PlanRect guardZone,
        double minimumOverlapLength,
        double minimumOverlapRatio)
    {
        if (guardZone.IsEmpty)
        {
            return false;
        }

        if (line.IsVertical())
        {
            var x = (line.Start.X + line.End.X) / 2.0;
            if (x < guardZone.Left || x > guardZone.Right)
            {
                return false;
            }

            var lineMin = Math.Min(line.Start.Y, line.End.Y);
            var lineMax = Math.Max(line.Start.Y, line.End.Y);
            return HasAxisOverlap(
                lineMin,
                lineMax,
                guardZone.Top,
                guardZone.Bottom,
                line.Length,
                minimumOverlapLength,
                minimumOverlapRatio);
        }

        if (line.IsHorizontal())
        {
            var y = (line.Start.Y + line.End.Y) / 2.0;
            if (y < guardZone.Top || y > guardZone.Bottom)
            {
                return false;
            }

            var lineMin = Math.Min(line.Start.X, line.End.X);
            var lineMax = Math.Max(line.Start.X, line.End.X);
            return HasAxisOverlap(
                lineMin,
                lineMax,
                guardZone.Left,
                guardZone.Right,
                line.Length,
                minimumOverlapLength,
                minimumOverlapRatio);
        }

        return false;
    }

    private static bool HasAxisOverlap(
        double lineMin,
        double lineMax,
        double zoneMin,
        double zoneMax,
        double lineLength,
        double minimumOverlapLength,
        double minimumOverlapRatio)
    {
        var overlap = Math.Min(lineMax, zoneMax) - Math.Max(lineMin, zoneMin);
        if (overlap <= 0)
        {
            return false;
        }

        return overlap >= minimumOverlapLength
            && overlap / Math.Max(lineLength, 0.001) >= minimumOverlapRatio;
    }

    private static bool SecondaryStructuralComponentHasTrustedPairedWallBodySupport(
        WallGraphComponent? component,
        IReadOnlyDictionary<string, WallSegment> wallById,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        if (component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || component.WallIds.Count < 1
            || component.WallIds.Count > 4
            || component.Confidence.Value < 0.6)
        {
            return false;
        }

        var walls = component.WallIds
            .Select(wallId => wallById.TryGetValue(wallId, out var wall) ? wall : null)
            .OfType<WallSegment>()
            .ToArray();
        if (walls.Length != component.WallIds.Count
            || walls.Any(wall => wall.Confidence.Value < 0.74
                || wall.DetectionKind != WallDetectionKind.ParallelLinePair))
        {
            return false;
        }

        if (!walls.All(wall =>
            wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
            && assessment.PlacementReady
            && !assessment.RequiresReview
            && !assessment.RejectedAsNoise
            && assessment.Category == WallEvidenceCategory.StrongWallBody
            && HasStrongPairedWallBodyEvidence(wall, assessment)))
        {
            return false;
        }

        var assessments = walls
            .Select(wall => wallEvidenceByWallId[wall.Id])
            .ToArray();

        if (component.WallIds.Count == 1)
        {
            return LooksLikeTrustedAnchoredSinglePairedWallBody(component, walls[0], assessments[0]);
        }

        return LooksLikeTrustedLongThinPairedWallBodyChain(component)
            || LooksLikeTrustedCompactPairedReturn(component, walls, assessments);
    }

    private static bool LooksLikeTrustedLongThinPairedWallBodyChain(WallGraphComponent component) =>
        component.DrawingLength >= 150
        && IsLongThinComponent(component.Bounds);

    private static bool LooksLikeTrustedAnchoredSinglePairedWallBody(
        WallGraphComponent component,
        WallSegment wall,
        WallEvidenceWallAssessment assessment) =>
        component.DrawingLength >= 72
        && wall.DrawingLength >= 72
        && wall.DetectionKind == WallDetectionKind.ParallelLinePair
        && assessment.Category == WallEvidenceCategory.StrongWallBody
        && component.Evidence.Any(item =>
            item.Contains("anchored single paired-wall body", StringComparison.OrdinalIgnoreCase))
        && IsThinComponent(component.Bounds, minimumLongSide: 72, maxShortSide: 18, minimumAspectRatio: 3);

    private static bool LooksLikeTrustedCompactPairedReturn(
        WallGraphComponent component,
        IReadOnlyList<WallSegment> walls,
        IReadOnlyList<WallEvidenceWallAssessment> assessments)
    {
        if (component.WallIds.Count is < 2 or > 3
            || component.DrawingLength < 96
            || !walls.All(wall => wall.CenterLine.IsHorizontal() || wall.CenterLine.IsVertical())
            || !walls.Any(wall => wall.CenterLine.IsHorizontal())
            || !walls.Any(wall => wall.CenterLine.IsVertical())
            || !assessments.Any(HasStructuralEndpointSupportEvidence))
        {
            return false;
        }

        var pairScores = walls
            .SelectMany(wall => wall.Evidence)
            .Concat(assessments.SelectMany(assessment => assessment.Evidence))
            .Select(TryReadPairScore)
            .Where(score => score.HasValue)
            .Select(score => score!.Value)
            .ToArray();

        return pairScores.Length == 0
            || (pairScores.Any(score => score >= 0.68)
                && pairScores.All(score => score >= 0.60));
    }

    private static bool IsLongThinComponent(PlanRect bounds)
        => IsThinComponent(bounds, minimumLongSide: 120, maxShortSide: 12, minimumAspectRatio: 10);

    private static bool IsThinComponent(
        PlanRect bounds,
        double minimumLongSide,
        double maxShortSide,
        double minimumAspectRatio)
    {
        var shortSide = Math.Min(bounds.Width, bounds.Height);
        var longSide = Math.Max(bounds.Width, bounds.Height);
        return longSide >= minimumLongSide
            && shortSide <= maxShortSide
            && longSide / Math.Max(shortSide, 0.001) >= minimumAspectRatio;
    }

    private static bool HasStrongPairedWallBodyEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        var evidence = wall.Evidence.Concat(assessment.Evidence).ToArray();
        return evidence.Any(item => item.Contains("parallel wall-face pair", StringComparison.OrdinalIgnoreCase))
            && evidence.Any(item => item.Contains("strong double-edge wall body", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasStructuralEndpointSupportEvidence(WallEvidenceWallAssessment assessment) =>
        assessment.Evidence
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Any(item =>
                item.Contains("endpoint supported by structural context", StringComparison.OrdinalIgnoreCase)
                || item.Contains("endpoints supported by structural context", StringComparison.OrdinalIgnoreCase)
                || item.Contains("structural graph support", StringComparison.OrdinalIgnoreCase));

    private static double? TryReadPairScore(string evidence)
    {
        const string Prefix = "pair score ";
        var index = evidence.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + Prefix.Length;
        var end = start;
        while (end < evidence.Length
            && (char.IsDigit(evidence[end])
                || evidence[end] == '.'
                || evidence[end] == ','))
        {
            end++;
        }

        var valueText = evidence[start..end].Replace(',', '.');
        return double.TryParse(
            valueText,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static IReadOnlySet<string> BuildRoomWallIds(IReadOnlyList<RoomRegion> rooms)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            foreach (var wallId in room.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    ids.Add(wallId);
                }
            }
        }

        return ids;
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildComponentByWallId(
        IReadOnlyList<WallGraphComponent> components)
    {
        var result = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    result[wallId] = component;
                }
            }
        }

        return result;
    }

    private static void AddReason(
        Dictionary<string, List<string>> reasonsByWallId,
        string wallId,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(wallId))
        {
            return;
        }

        if (!reasonsByWallId.TryGetValue(wallId, out var reasons))
        {
            reasons = new List<string>();
            reasonsByWallId[wallId] = reasons;
        }

        reasons.Add(reason);
    }
}
