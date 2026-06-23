namespace OpenPlanTrace;

public static class WallPlacementContextGuards
{
    private const double MinTrustedSecondaryExteriorShellLengthDrawingUnits = 72.0;
    private const double MinTrustedSecondaryExteriorShellPairScore = 0.78;
    private const double MinTrustedSecondaryExteriorShellOverlapRatio = 0.90;
    private const double MinTrustedSecondaryExteriorShellFaceSeparationDrawingUnits = 2.0;
    private const double MaxTrustedSecondaryExteriorShellFaceSeparationDrawingUnits = 18.0;
    private const int MaxTrustedSecondaryExteriorShellFaceFragments = 220;

    public const string SecondaryStructuralWithoutRoomBoundarySupportReason =
        "secondary structural wall component lacks room-boundary support";

    public const string SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason =
        "secondary structural wall overlaps detected stair/object linework without room-boundary support";

    public const string SecondaryStructuralOverSourcedDetailLineworkReason =
        "secondary structural wall has excessive detail/source linework contamination despite room-boundary support";

    public const string FragmentMergedInteriorWithoutRoomBoundarySupportReason =
        "fragment-merged interior wall has suspicious linework and lacks room-boundary support";

    public const string MainStructuralInteriorWithoutSemanticSupportReason =
        "main structural interior wall has risky linework and lacks semantic room-boundary support";

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildReviewReasons(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var roomWallReferences = RoomBoundaryWallReferenceBuilder.Build(
            result.Rooms,
            result.Walls,
            wallSnapTolerance: 2.0);
        var roomWallIds = roomWallReferences.RoomIdsByWallId.Keys.ToHashSet(StringComparer.Ordinal);
        if (result.Rooms.Count == 0)
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
        var detailLineworkCandidatesByPage = BuildDetailLineworkCandidatesByPage(result.ObjectCandidates);

        foreach (var wall in result.Walls)
        {
            componentByWallId.TryGetValue(wall.Id, out var component);
            var wallIsRoomBoundary = roomWallIds.Contains(wall.Id);
            var hasRoomBoundarySupport = SecondaryStructuralComponentHasRoomBoundarySupport(component, roomWallIds);
            if (!wallIsRoomBoundary
                && MainStructuralInteriorWallNeedsSemanticSupportReview(
                    wall,
                    component,
                    wallEvidenceByWallId))
            {
                AddReason(
                    reasonsByWallId,
                    wall.Id,
                    MainStructuralInteriorWithoutSemanticSupportReason);
            }

            if (!wallIsRoomBoundary
                && FragmentMergedInteriorWallNeedsRoomBoundaryReview(
                    wall,
                    component,
                    wallEvidenceByWallId))
            {
                AddReason(
                    reasonsByWallId,
                    wall.Id,
                    FragmentMergedInteriorWithoutRoomBoundarySupportReason);
            }

            if (hasRoomBoundarySupport
                && SecondaryStructuralWallHasOverSourcedDetailLineworkRisk(
                    wall,
                    component,
                    wallById,
                    wallEvidenceByWallId,
                    detailLineworkCandidatesByPage))
            {
                AddReason(
                    reasonsByWallId,
                    wall.Id,
                    SecondaryStructuralOverSourcedDetailLineworkReason);
            }
            else if (!hasRoomBoundarySupport
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
                && !SecondaryStructuralWallHasTrustedExteriorShellSupport(
                    wall,
                    component,
                    wallEvidenceByWallId)
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

    private static IReadOnlyDictionary<int, IReadOnlyList<ObjectCandidate>> BuildDetailLineworkCandidatesByPage(
        IReadOnlyList<ObjectCandidate> objectCandidates)
    {
        return objectCandidates
            .Where(IsWallContaminatingDetailLinework)
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

    private static bool SecondaryStructuralWallHasOverSourcedDetailLineworkRisk(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyDictionary<string, WallSegment> wallById,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId,
        IReadOnlyDictionary<int, IReadOnlyList<ObjectCandidate>> detailLineworkCandidatesByPage)
    {
        if (component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || component.WallIds.Count is < 1 or > 3
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength < 48
            || (!wall.CenterLine.IsHorizontal() && !wall.CenterLine.IsVertical())
            || HasTrustedTwoSidedRoomBoundarySupport(wall, wallEvidenceByWallId)
            || !LooksLikeOverSourcedCompactSecondaryComponent(component, wallById, wallEvidenceByWallId)
            || !detailLineworkCandidatesByPage.TryGetValue(wall.PageNumber, out var candidates))
        {
            return false;
        }

        var guardTolerance = Math.Max(8, wall.Thickness * 2.0);
        return candidates.Any(candidate =>
            LineOverlapsCandidateGuardZone(
                wall.CenterLine,
                candidate.Bounds.Inflate(guardTolerance),
                minimumOverlapLength: Math.Min(48, Math.Max(24, wall.DrawingLength * 0.30)),
                minimumOverlapRatio: 0.35));
    }

    private static bool HasTrustedTwoSidedRoomBoundarySupport(
        WallSegment wall,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        if (wall.WallType != WallType.Interior
            || !wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise)
        {
            return false;
        }

        return wall.Evidence
            .Concat(assessment.Evidence)
            .Any(item =>
                item.Contains("detected room evidence on both sides", StringComparison.OrdinalIgnoreCase)
                || item.Contains("shared by room adjacency boundary", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MainStructuralInteriorWallNeedsSemanticSupportReview(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        if (component?.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || wall.WallType == WallType.Exterior
            || wall.DrawingLength < 72
            || (!wall.CenterLine.IsHorizontal() && !wall.CenterLine.IsVertical())
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || !wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = WallEvidenceFor(wall, assessment);
        if (HasSemanticWallPlacementSupport(evidence)
            || HasTrustedExplicitWallLayerSupport(evidence))
        {
            return false;
        }

        if (assessment.Category == WallEvidenceCategory.RecoveredWallBody)
        {
            return true;
        }

        if (EvidenceContainsAny(
            evidence,
            "layer (unlayered) classified Dimension",
            "layer evidence: contains dimension-like text",
            "dimension-like weak layer"))
        {
            return true;
        }

        var sourcePrimitiveCount = wall.SourcePrimitiveIds
            .Concat(assessment.SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var maxFaceFragmentCount = MaxFaceFragmentCount(wall, assessment);
        var unknownLayer = EvidenceContainsAny(
            evidence,
            "layer (unlayered) classified Unknown",
            "layer evidence: no strong layer name or geometry evidence",
            "source layer category Unknown");

        return unknownLayer
            && (sourcePrimitiveCount >= 18
                || maxFaceFragmentCount >= 24
                || wall.FragmentEvidence?.FragmentCount >= 8
                || wall.FragmentEvidence?.DuplicatePrimitiveCount >= 4);
    }

    private static bool SecondaryStructuralWallHasTrustedExteriorShellSupport(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        if (component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedSecondaryExteriorShellLengthDrawingUnits
            || wall.Confidence.Value < 0.74
            || !wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || !HasStrongPairedWallBodyEvidence(wall, assessment))
        {
            return false;
        }

        var evidence = WallEvidenceFor(wall, assessment)
            .Concat(component.Evidence)
            .ToArray();
        if (!EvidenceContainsAny(
                evidence,
                "near detected floorplan/wall envelope",
                "local outer boundary",
                "trusted exterior shell",
                "exterior shell continuity"))
        {
            return false;
        }

        if (EvidenceContainsAny(
                evidence,
                "outdoor covered-area boundary",
                "unpaired outdoor covered-area boundary",
                "covered-area boundary",
                "outdoor/terrace room evidence alone",
                "terrace",
                "covered entry",
                "covered-entry",
                "overbygd",
                "canopy",
                "railing",
                "trim/detail",
                "trim linework",
                "glazing",
                "detail linework",
                "surface pattern",
                "not trusted",
                "without shell support",
                "alone is not trusted"))
        {
            return false;
        }

        var pairScore = wall.PairEvidence?.Score
            ?? evidence.Select(TryReadPairScore).Where(score => score.HasValue).Select(score => score!.Value).DefaultIfEmpty(0).Max();
        var overlapRatio = wall.PairEvidence?.OverlapRatio
            ?? evidence.Select(TryReadOverlapRatio).Where(ratio => ratio.HasValue).Select(ratio => ratio!.Value).DefaultIfEmpty(0).Max();
        var faceSeparation = wall.PairEvidence?.FaceSeparation
            ?? evidence.Select(TryReadFaceSeparation).Where(separation => separation.HasValue).Select(separation => separation!.Value).DefaultIfEmpty(0).Max();
        if (pairScore < MinTrustedSecondaryExteriorShellPairScore
            || overlapRatio < MinTrustedSecondaryExteriorShellOverlapRatio
            || faceSeparation < MinTrustedSecondaryExteriorShellFaceSeparationDrawingUnits
            || faceSeparation > MaxTrustedSecondaryExteriorShellFaceSeparationDrawingUnits
            || MaxFaceFragmentCount(wall, assessment) > MaxTrustedSecondaryExteriorShellFaceFragments)
        {
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> WallEvidenceFor(
        WallSegment wall,
        WallEvidenceWallAssessment assessment) =>
        wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

    private static bool HasSemanticWallPlacementSupport(IReadOnlyList<string> evidence) =>
        EvidenceContainsAny(
            evidence,
            "detected room evidence on both sides",
            "shared by room adjacency boundary",
            "explicit room boundary support",
            "geometric room boundary support",
            "retained by room boundary support",
            "room-confirmed wall body",
            "clean fragment-merged interior room boundary promoted",
            "room boundary evidence");

    private static bool HasTrustedExplicitWallLayerSupport(IReadOnlyList<string> evidence) =>
        EvidenceContainsAny(
            evidence,
            "wall-like layer",
            "trusted benchmark",
            "trusted exterior shell",
            "exterior shell continuity",
            "wall evidence: retained by exterior shell continuity");

    private static int MaxFaceFragmentCount(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        var pairMax = wall.PairEvidence is null
            ? 0
            : Math.Max(wall.PairEvidence.FirstFaceFragmentCount, wall.PairEvidence.SecondFaceFragmentCount);
        var evidenceMax = WallEvidenceFor(wall, assessment)
            .SelectMany(EvidenceFragmentCounts)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(pairMax, evidenceMax);
    }

    private static IEnumerable<int> EvidenceFragmentCounts(string evidence)
    {
        var markers = new[]
        {
            "first face merged ",
            "second face merged ",
            "face merged ",
            "max face fragments ",
            "total face fragments "
        };

        foreach (var marker in markers)
        {
            var count = TryReadEvidenceCount(evidence, marker);
            if (count.HasValue)
            {
                yield return count.Value;
            }
        }
    }

    private static bool LooksLikeOverSourcedCompactSecondaryComponent(
        WallGraphComponent component,
        IReadOnlyDictionary<string, WallSegment> wallById,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        var shortSide = Math.Min(component.Bounds.Width, component.Bounds.Height);
        var longSide = Math.Max(component.Bounds.Width, component.Bounds.Height);
        if (shortSide <= 0.001
            || longSide < 72
            || shortSide > 18
            || longSide / Math.Max(shortSide, 0.001) < 8)
        {
            return false;
        }

        var distinctSourcePrimitiveCount = component.SourcePrimitiveIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (distinctSourcePrimitiveCount < Math.Max(24, component.WallIds.Count * 12))
        {
            return false;
        }

        var walls = component.WallIds
            .Select(wallId => wallById.TryGetValue(wallId, out var wall) ? wall : null)
            .OfType<WallSegment>()
            .ToArray();
        if (walls.Length != component.WallIds.Count
            || walls.Any(wall => wall.DetectionKind != WallDetectionKind.ParallelLinePair))
        {
            return false;
        }

        return walls.Any(wall =>
            wall.SourcePrimitiveIds.Count >= 18
            || (wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
                && assessment.SourcePrimitiveIds.Count >= 18)
            || wall.Evidence.Any(IsHeavyMergedOrCollapsedFaceEvidence));
    }

    private static bool FragmentMergedInteriorWallNeedsRoomBoundaryReview(
        WallSegment wall,
        WallGraphComponent? component,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceByWallId)
    {
        if (wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence
            || wall.DrawingLength < Math.Max(48, wall.Thickness * 7.0)
            || component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        if (!wallEvidenceByWallId.TryGetValue(wall.Id, out var assessment)
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Category != WallEvidenceCategory.MediumWallBody)
        {
            return false;
        }

        if (HasSemanticWallPlacementSupport(WallEvidenceFor(wall, assessment)))
        {
            return false;
        }

        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, wall.SourcePrimitiveIds.Count);
        return fragmentCount >= 10
            || fragmentEvidence.DuplicatePrimitiveCount >= 4
            || fragmentEvidence.GapRatio >= 0.02
            || fragmentEvidence.TotalHealedGap >= Math.Max(2.0, wall.Thickness * 0.35);
    }

    private static bool IsWallContaminatingObjectLinework(ObjectCandidate candidate) =>
        candidate.Kind == ObjectCandidateKind.Stair
        || candidate.Category == ObjectCategory.Stair
        || candidate.Evidence.Any(item =>
            item.Contains("nearby text", StringComparison.OrdinalIgnoreCase)
            && item.Contains("trapp", StringComparison.OrdinalIgnoreCase));

    private static bool IsWallContaminatingDetailLinework(ObjectCandidate candidate) =>
        IsWallContaminatingObjectLinework(candidate)
        || (candidate.SourceKind == ObjectCandidateSourceKind.WallComponentIsland
            && candidate.SourceWallComponentKind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        || (candidate.SourceKind == ObjectCandidateSourceKind.CompositeLinework
            && candidate.Kind == ObjectCandidateKind.Symbol
            && candidate.Category == ObjectCategory.GenericSymbol);

    private static bool IsHeavyMergedOrCollapsedFaceEvidence(string evidence)
    {
        var value = TryReadEvidenceCount(evidence, "face merged ")
            ?? TryReadEvidenceCount(evidence, "face collapsed ");
        return value >= 18;
    }

    private static int? TryReadEvidenceCount(string evidence, string marker)
    {
        var markerIndex = evidence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        var end = start;
        while (end < evidence.Length && char.IsDigit(evidence[end]))
        {
            end++;
        }

        return end > start && int.TryParse(evidence[start..end], out var value)
            ? value
            : null;
    }

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

    private static double? TryReadOverlapRatio(string evidence)
    {
        const string Prefix = "overlap ratio ";
        var index = evidence.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return TryReadEvidenceDouble(evidence, index + Prefix.Length);
    }

    private static double? TryReadFaceSeparation(string evidence)
    {
        const string Prefix = "face separation ";
        var index = evidence.IndexOf(Prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        return TryReadEvidenceDouble(evidence, index + Prefix.Length);
    }

    private static double? TryReadEvidenceDouble(string evidence, int start)
    {
        var end = start;
        while (end < evidence.Length
            && (char.IsDigit(evidence[end])
                || evidence[end] == '.'
                || evidence[end] == ','))
        {
            end++;
        }

        if (end <= start)
        {
            return null;
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

    private static bool EvidenceContainsAny(
        IReadOnlyList<string> evidence,
        params string[] fragments) =>
        fragments.Any(fragment => EvidenceContains(evidence, fragment));

    private static bool EvidenceContains(
        IReadOnlyList<string> evidence,
        string fragment) =>
        evidence.Any(item => item.Contains(fragment, StringComparison.OrdinalIgnoreCase));

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
