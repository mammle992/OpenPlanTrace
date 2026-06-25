namespace OpenPlanTrace;

internal sealed record ExteriorShellRepairSupport(
    string WallId,
    int PageNumber,
    string SupportKind,
    IReadOnlyList<string> SupportWallIds,
    double SupportScore,
    IReadOnlyList<string> Evidence);

internal sealed record ExteriorShellRepairSupportResult(
    IReadOnlyDictionary<string, ExteriorShellRepairSupport> SupportByWallId,
    int CandidateWallCount)
{
    public static ExteriorShellRepairSupportResult Empty { get; } =
        new(
            new Dictionary<string, ExteriorShellRepairSupport>(StringComparer.Ordinal),
            0);
}

internal static class ExteriorShellRepairSupportBuilder
{
    public static ExteriorShellRepairSupportResult Build(
        IReadOnlyList<WallSegment> placementWalls,
        IReadOnlyList<WallSegment> candidateWalls,
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<SheetRegion> sheetRegions,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (candidateWalls.Count == 0 || evidenceByWallId.Count == 0)
        {
            return ExteriorShellRepairSupportResult.Empty;
        }

        var trustedExteriorWalls = placementWalls
            .Where(wall => evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                && IsTrustedExteriorShellWall(wall, assessment, options))
            .ToArray();
        var structuralEnvelopesByPage = BuildStructuralEnvelopesByPage(
            placementWalls,
            rooms,
            sheetRegions,
            evidenceByWallId,
            options);
        var shellChainSupportByWallId = BuildGlobalEnvelopeShellChainSupport(
            candidateWalls,
            structuralEnvelopesByPage,
            evidenceByWallId,
            options);

        var supportByWallId = new Dictionary<string, ExteriorShellRepairSupport>(StringComparer.Ordinal);
        var candidateCount = 0;
        foreach (var wall in candidateWalls
                     .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
                     .GroupBy(wall => wall.Id, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (!evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                || !IsExteriorShellRepairCandidate(wall, assessment, options))
            {
                continue;
            }

            candidateCount++;
            if (!TryFindExteriorShellSupport(
                    wall,
                    assessment,
                    trustedExteriorWalls,
                    options,
                    out var support)
                && !TryFindGlobalEnvelopeShellSupport(
                    wall,
                    assessment,
                    structuralEnvelopesByPage,
                    trustedExteriorWalls,
                    options,
                    out support)
                && !shellChainSupportByWallId.TryGetValue(wall.Id, out support))
            {
                continue;
            }

            supportByWallId[wall.Id] = support;
        }

        return new ExteriorShellRepairSupportResult(supportByWallId, candidateCount);
    }

    private static IReadOnlyDictionary<int, StructuralExteriorEnvelope> BuildStructuralEnvelopesByPage(
        IReadOnlyList<WallSegment> placementWalls,
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<SheetRegion> sheetRegions,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        var pages = placementWalls
            .Select(wall => wall.PageNumber)
            .Concat(rooms.Select(room => room.PageNumber))
            .Concat(sheetRegions.Select(region => region.PageNumber))
            .Distinct()
            .Order()
            .ToArray();
        var result = new Dictionary<int, StructuralExteriorEnvelope>();
        foreach (var pageNumber in pages)
        {
            var roomBounds = rooms
                .Where(room => room.PageNumber == pageNumber
                    && room.UseKind != RoomUseKind.Outdoor
                    && room.Confidence.Value >= 0.50
                    && !room.Bounds.IsEmpty
                    && room.Bounds.Area >= Math.Pow(Math.Max(18.0, options.MinWallLength), 2) * 0.10)
                .Select(room => room.Bounds)
                .ToArray();
            var trustedWallBounds = placementWalls
                .Where(wall => wall.PageNumber == pageNumber
                    && evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                    && IsTrustedEnvelopeSeedWall(wall, assessment, options))
                .Select(wall => wall.Bounds)
                .ToArray();
            var seedBounds = roomBounds
                .Concat(trustedWallBounds)
                .ToArray();
            if (seedBounds.Length == 0)
            {
                var mainFloorplan = sheetRegions
                    .Where(region => region.PageNumber == pageNumber
                        && region.Kind == RegionKind.MainFloorPlan
                        && region.Confidence.Value >= 0.55
                        && !region.Bounds.IsEmpty)
                    .OrderByDescending(region => region.Bounds.Area)
                    .Select(region => (PlanRect?)region.Bounds)
                    .FirstOrDefault();
                if (mainFloorplan is null)
                {
                    continue;
                }

                result[pageNumber] = new StructuralExteriorEnvelope(
                    pageNumber,
                    mainFloorplan.Value,
                    "main floorplan region fallback",
                    RoomSeedCount: 0,
                    WallSeedCount: 0,
                    UsesMainFloorplanFallback: true);
                continue;
            }

            var envelope = PlanRect.Union(seedBounds);
            if (envelope.IsEmpty)
            {
                continue;
            }

            result[pageNumber] = new StructuralExteriorEnvelope(
                pageNumber,
                envelope,
                roomBounds.Length > 0
                    ? "indoor room and trusted wall envelope"
                    : "trusted wall envelope",
                roomBounds.Length,
                trustedWallBounds.Length,
                UsesMainFloorplanFallback: false);
        }

        return result;
    }

    private static bool IsTrustedEnvelopeSeedWall(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (!assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody)
            || wall.WallType == WallType.Unknown
            || wall.DrawingLength < Math.Max(28.0, options.MinWallLength)
            || !TryResolveAxisInterval(wall.CenterLine, out _, out _, out _, out _)
            || HasExteriorShellRepairBlocker(
                wall,
                assessment,
                allowGraphObjectLikeReclassification: false,
                allowDimensionLikeStructuralShell: false))
        {
            return false;
        }

        return true;
    }

    private static bool IsTrustedExteriorShellWall(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (wall.WallType != WallType.Exterior
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.DrawingLength < Math.Max(32.0, options.MinWallLength * 1.25)
            || !TryResolveAxisInterval(wall.CenterLine, out _, out _, out _, out _))
        {
            return false;
        }

        return !HasExteriorShellRepairBlocker(
            wall,
            assessment,
            allowGraphObjectLikeReclassification: false,
            allowDimensionLikeStructuralShell: false);
    }

    private static bool IsExteriorShellRepairCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        var evidence = EvidenceFor(wall, assessment);
        var graphObjectLikeReclassification = IsGraphObjectLikeReclassificationEvidence(evidence);
        var exteriorEvidence = wall.WallType == WallType.Exterior
            || evidence.Any(item =>
                item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase));
        var hasHighTrustRecoverableGeometry =
            assessment.Category is WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody
            && wall.PairEvidence is { Score: >= 0.84, OverlapRatio: >= 0.94 };
        var hasHighTrustReadyShellGeometry =
            wall.WallType != WallType.Exterior
            && assessment.PlacementReady
            && !assessment.RequiresReview
            && !assessment.RejectedAsNoise
            && assessment.Decision != WallEvidenceDecision.Reject
            && assessment.Category is WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody
            && wall.PairEvidence is
            {
                Score: >= 0.86,
                OverlapRatio: >= 0.90,
                FaceSeparation: >= 1.5
            } readyPair
            && readyPair.FaceSeparation <= Math.Max(26.0, options.DefaultWallThickness * 5.5)
            && Math.Max(readyPair.FirstFaceFragmentCount, readyPair.SecondFaceFragmentCount) <= 180
            && readyPair.FirstFaceFragmentCount + readyPair.SecondFaceFragmentCount <= 260;
        var needsShellRepair =
            assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType == WallType.Unknown
            || graphObjectLikeReclassification;

        if ((!exteriorEvidence && !hasHighTrustRecoverableGeometry && !hasHighTrustReadyShellGeometry)
            || (!needsShellRepair && !hasHighTrustReadyShellGeometry)
            || assessment.Category is WallEvidenceCategory.DoorOrOpeningSymbol
                or WallEvidenceCategory.SurfacePatternDetail
            || (assessment.Category == WallEvidenceCategory.ObjectOrFixtureDetail && !graphObjectLikeReclassification)
            || wall.DrawingLength < Math.Max(24.0, options.MinWallLength)
            || !TryResolveAxisInterval(wall.CenterLine, out _, out _, out _, out _))
        {
            return false;
        }

        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair || wall.PairEvidence is not { } pair)
        {
            return IsStructuralShellStrokeCandidate(wall, assessment, evidence, options)
                && !HasExteriorShellRepairBlocker(
                    wall,
                    assessment,
                    graphObjectLikeReclassification,
                    allowDimensionLikeStructuralShell: true);
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true
            || HasExteriorShellRepairBlocker(
                wall,
                assessment,
                graphObjectLikeReclassification,
                allowDimensionLikeStructuralShell: hasHighTrustReadyShellGeometry))
        {
            return false;
        }

        var highTrustPair =
            pair.Score >= 0.70
            && pair.OverlapRatio >= 0.84
            && pair.FaceSeparation >= 1.5
            && pair.FaceSeparation <= Math.Max(24.0, options.DefaultWallThickness * 5.0)
            && Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) <= 160
            && pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount <= 220;
        var chainEligibleExteriorPair =
            exteriorEvidence
            && pair.Score >= 0.66
            && pair.OverlapRatio >= 0.70
            && pair.FaceSeparation >= 1.5
            && pair.FaceSeparation <= Math.Max(26.0, options.DefaultWallThickness * 5.5)
            && Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) <= 180
            && pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount <= 260;

        return highTrustPair || chainEligibleExteriorPair;
    }

    private static bool TryFindExteriorShellSupport(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        IReadOnlyList<WallSegment> trustedExteriorWalls,
        ScannerOptions options,
        out ExteriorShellRepairSupport support)
    {
        support = default!;
        if (!TryResolveAxisInterval(wall.CenterLine, out var orientation, out var coordinate, out var start, out var end))
        {
            return false;
        }

        var coordinateTolerance = Math.Max(
            18.0,
            Math.Max(options.WallSnapTolerance * 6.0, options.DefaultWallThickness * 5.0));
        var endpointTolerance = Math.Max(options.WallSnapTolerance * 4.0, options.DefaultWallThickness * 3.0);
        var bridgeGapTolerance = Math.Max(options.MaxOpeningGap * 0.45, options.DefaultWallThickness * 7.0);
        var beforeIds = new List<string>();
        var afterIds = new List<string>();
        var startAnchorIds = new List<string>();
        var endAnchorIds = new List<string>();

        foreach (var other in trustedExteriorWalls)
        {
            if (string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || other.PageNumber != wall.PageNumber
                || !TryResolveAxisInterval(other.CenterLine, out var otherOrientation, out var otherCoordinate, out var otherStart, out var otherEnd))
            {
                continue;
            }

            if (otherOrientation == orientation && Math.Abs(otherCoordinate - coordinate) <= coordinateTolerance)
            {
                var beforeGap = start - otherEnd;
                if (beforeGap >= -coordinateTolerance && beforeGap <= bridgeGapTolerance)
                {
                    beforeIds.Add(other.Id);
                }

                var afterGap = otherStart - end;
                if (afterGap >= -coordinateTolerance && afterGap <= bridgeGapTolerance)
                {
                    afterIds.Add(other.Id);
                }
            }
            else if (otherOrientation != orientation)
            {
                var startDistance = DistanceFromAxisEndpointToSegment(
                    orientation,
                    wall.CenterLine.Start,
                    other.CenterLine,
                    endpointTolerance);
                if (startDistance <= endpointTolerance)
                {
                    startAnchorIds.Add(other.Id);
                }

                var endDistance = DistanceFromAxisEndpointToSegment(
                    orientation,
                    wall.CenterLine.End,
                    other.CenterLine,
                    endpointTolerance);
                if (endDistance <= endpointTolerance)
                {
                    endAnchorIds.Add(other.Id);
                }
            }
        }

        var supportIds = beforeIds
            .Concat(afterIds)
            .Concat(startAnchorIds)
            .Concat(endAnchorIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var pair = wall.PairEvidence;
        var hasSingleCollinearExtension =
            pair is { Score: >= 0.84, OverlapRatio: >= 0.94 }
            && wall.DrawingLength >= Math.Max(48.0, options.MinWallLength * 2.0)
            && (beforeIds.Count > 0 || afterIds.Count > 0)
            && wall.Evidence
                .Concat(assessment.Evidence)
                .Any(item =>
                    item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)
                    || item.Contains("recovered wall body", StringComparison.OrdinalIgnoreCase));
        if (supportIds.Length < 2 && !hasSingleCollinearExtension)
        {
            return false;
        }

        var hasBridge = beforeIds.Count > 0 && afterIds.Count > 0;
        var hasAnchoredSpan =
            (startAnchorIds.Count > 0 || beforeIds.Count > 0)
            && (endAnchorIds.Count > 0 || afterIds.Count > 0);
        if (!hasBridge && !hasAnchoredSpan && !hasSingleCollinearExtension)
        {
            return false;
        }

        var pairScore = pair?.Score ?? 0.0;
        var supportScore =
            pairScore
            + (hasBridge ? 0.24 : 0.0)
            + (hasAnchoredSpan ? 0.18 : 0.0)
            + (hasSingleCollinearExtension ? 0.16 : 0.0)
            + Math.Min(0.18, wall.DrawingLength / Math.Max(400.0, options.MinWallLength * 16.0));
        if (supportScore < 0.92)
        {
            return false;
        }

        var supportKind = hasBridge
            ? "collinear-shell-bridge"
            : hasAnchoredSpan
                ? "anchored-shell-span"
                : "collinear-shell-extension";
        support = new ExteriorShellRepairSupport(
            wall.Id,
            wall.PageNumber,
            supportKind,
            supportIds,
            supportScore,
            new[]
            {
                $"wall evidence: exterior shell repair support {supportKind} from trusted exterior wall graph",
                $"wall evidence: exterior shell repair support score {supportScore:0.###}, pair score {pairScore:0.###}, support walls {string.Join(",", supportIds)}"
            });
        return true;
    }

    private static bool TryFindGlobalEnvelopeShellSupport(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        IReadOnlyDictionary<int, StructuralExteriorEnvelope> envelopesByPage,
        IReadOnlyList<WallSegment> trustedExteriorWalls,
        ScannerOptions options,
        out ExteriorShellRepairSupport support)
    {
        support = default!;
        if (!envelopesByPage.TryGetValue(wall.PageNumber, out var envelope)
            || envelope.Bounds.IsEmpty
            || !TryResolveAxisInterval(wall.CenterLine, out var orientation, out var coordinate, out var start, out var end)
            || wall.PairEvidence is not { } pair)
        {
            return false;
        }

        var coordinateTolerance = Math.Max(
            18.0,
            Math.Max(options.WallSnapTolerance * 6.0, options.DefaultWallThickness * 5.0));
        if (!TryMatchEnvelopeEdge(
                envelope.Bounds,
                orientation,
                coordinate,
                start,
                end,
                coordinateTolerance,
                out var edgeName,
                out var axisDistance,
                out var overlapLength,
                out var wallCoverageRatio,
                out var edgeCoverageRatio))
        {
            return false;
        }

        var evidence = EvidenceFor(wall, assessment);
        var explicitExteriorEvidence = wall.WallType == WallType.Exterior
            || evidence.Any(item =>
                item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase));
        if (pair.Score < 0.84 || pair.OverlapRatio < 0.94)
        {
            return false;
        }

        var supportWallIds = FindEnvelopeSupportWallIds(
            wall,
            trustedExteriorWalls,
            orientation,
            coordinate,
            start,
            end,
            coordinateTolerance)
            .ToArray();
        var minimumOverlapLength = Math.Max(42.0, options.MinWallLength * 1.75);
        var minimumWallCoverage = envelope.UsesMainFloorplanFallback ? 0.74 : 0.54;
        var rejectedCandidate = assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject;
        if (overlapLength < minimumOverlapLength
            || wallCoverageRatio < minimumWallCoverage
            || (rejectedCandidate && !explicitExteriorEvidence && supportWallIds.Length == 0)
            || (envelope.UsesMainFloorplanFallback && (!explicitExteriorEvidence || pair.Score < 0.88))
            || (!envelope.UsesMainFloorplanFallback
                && edgeCoverageRatio < 0.06
                && supportWallIds.Length == 0
                && wall.DrawingLength < Math.Max(84.0, options.MinWallLength * 3.5)))
        {
            return false;
        }

        var supportScore =
            pair.Score
            + (wallCoverageRatio * 0.18)
            + (Math.Min(edgeCoverageRatio, 0.60) * 0.14)
            + (envelope.RoomSeedCount > 0 ? 0.12 : 0.0)
            + (envelope.WallSeedCount > 0 ? 0.06 : 0.0)
            + (supportWallIds.Length > 0 ? 0.08 : 0.0)
            - Math.Min(0.10, axisDistance / Math.Max(1.0, coordinateTolerance * 10.0));
        var minimumSupportScore = envelope.UsesMainFloorplanFallback ? 1.08 : 0.98;
        if (supportScore < minimumSupportScore)
        {
            return false;
        }

        var supportKind = envelope.UsesMainFloorplanFallback
            ? "global-floorplan-envelope-edge"
            : "global-room-envelope-edge";
        support = new ExteriorShellRepairSupport(
            wall.Id,
            wall.PageNumber,
            supportKind,
            supportWallIds,
            supportScore,
            new[]
            {
                $"wall evidence: exterior shell repair support {supportKind} from {envelope.SourceKind}",
                $"wall evidence: envelope edge {edgeName}, axis distance {axisDistance:0.###}, wall coverage {wallCoverageRatio:0.###}, edge coverage {edgeCoverageRatio:0.###}, overlap {overlapLength:0.###}",
                $"wall evidence: envelope seeds rooms {envelope.RoomSeedCount}, trusted walls {envelope.WallSeedCount}, support score {supportScore:0.###}"
            });
        return true;
    }

    private static IReadOnlyDictionary<string, ExteriorShellRepairSupport> BuildGlobalEnvelopeShellChainSupport(
        IReadOnlyList<WallSegment> candidateWalls,
        IReadOnlyDictionary<int, StructuralExteriorEnvelope> envelopesByPage,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> evidenceByWallId,
        ScannerOptions options)
    {
        if (candidateWalls.Count == 0 || envelopesByPage.Count == 0)
        {
            return new Dictionary<string, ExteriorShellRepairSupport>(StringComparer.Ordinal);
        }

        var coordinateTolerance = Math.Max(
            18.0,
            Math.Max(options.WallSnapTolerance * 6.0, options.DefaultWallThickness * 5.0));
        var chainMembers = new List<ShellChainMember>();
        foreach (var wall in candidateWalls
                     .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
                     .GroupBy(wall => wall.Id, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            if (!evidenceByWallId.TryGetValue(wall.Id, out var assessment)
                || !IsGlobalShellChainCandidate(wall, assessment, options)
                || !envelopesByPage.TryGetValue(wall.PageNumber, out var envelope)
                || envelope.Bounds.IsEmpty
                || !TryResolveAxisInterval(wall.CenterLine, out var orientation, out var coordinate, out var start, out var end)
                || !TryMatchEnvelopeEdge(
                    envelope.Bounds,
                    orientation,
                    coordinate,
                    start,
                    end,
                    coordinateTolerance,
                    out var edgeName,
                    out var axisDistance,
                    out var overlapLength,
                    out var wallCoverageRatio,
                    out var edgeCoverageRatio))
            {
                continue;
            }

            chainMembers.Add(new ShellChainMember(
                wall.Id,
                wall.PageNumber,
                edgeName,
                orientation,
                wall.DetectionKind,
                coordinate,
                start,
                end,
                overlapLength,
                wallCoverageRatio,
                edgeCoverageRatio,
                axisDistance,
                StructuralShellMemberScore(wall, assessment),
                envelope));
        }

        if (chainMembers.Count == 0)
        {
            return new Dictionary<string, ExteriorShellRepairSupport>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, ExteriorShellRepairSupport>(StringComparer.Ordinal);
        foreach (var group in chainMembers
                     .GroupBy(member => new ShellChainKey(member.PageNumber, member.EdgeName, member.Orientation)))
        {
            var ordered = group
                .OrderBy(member => member.Start)
                .ThenBy(member => member.End)
                .ToArray();
            var current = new List<ShellChainMember>();
            foreach (var member in ordered)
            {
                if (current.Count == 0)
                {
                    current.Add(member);
                    continue;
                }

                var currentEnd = current.Max(item => item.End);
                var currentCoordinate = current.Average(item => item.Coordinate);
                var gap = member.Start - currentEnd;
                var sameRun =
                    gap <= Math.Max(options.MaxOpeningGap * 1.8, options.DefaultWallThickness * 12.0)
                    && Math.Abs(member.Coordinate - currentCoordinate) <= coordinateTolerance;
                if (sameRun)
                {
                    current.Add(member);
                    continue;
                }

                AddChainSupport(current, result, options);
                current.Clear();
                current.Add(member);
            }

            AddChainSupport(current, result, options);
        }

        return result;
    }

    private static bool IsGlobalShellChainCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        ScannerOptions options)
    {
        if (wall.DetectionKind is not (WallDetectionKind.ParallelLinePair
                or WallDetectionKind.FragmentMerged
                or WallDetectionKind.SingleLine)
            || (wall.DetectionKind == WallDetectionKind.ParallelLinePair && wall.PairEvidence is null)
            || (wall.FragmentEvidence?.RequiresGeometryReview == true
                && wall.DrawingLength < Math.Max(96.0, options.MinWallLength * 4.0))
            || wall.DrawingLength < Math.Max(18.0, options.MinWallLength * 0.75)
            || assessment.Category is WallEvidenceCategory.DoorOrOpeningSymbol
                or WallEvidenceCategory.SurfacePatternDetail
            || HasExteriorShellRepairBlocker(
                wall,
                assessment,
                allowGraphObjectLikeReclassification: IsGraphObjectLikeReclassificationEvidence(EvidenceFor(wall, assessment)),
                allowDimensionLikeStructuralShell: true))
        {
            return false;
        }

        var evidence = EvidenceFor(wall, assessment);
        var hasExplicitExteriorEvidence = wall.WallType == WallType.Exterior
            || evidence.Any(item =>
                item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase));
        if (!hasExplicitExteriorEvidence)
        {
            return false;
        }

        if (wall.PairEvidence is not { } pair)
        {
            return IsStructuralShellStrokeCandidate(wall, assessment, evidence, options);
        }

        var maxFaceFragments = Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount);
        var totalFaceFragments = pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount;
        return pair.Score >= 0.66
            && pair.OverlapRatio >= 0.70
            && pair.FaceSeparation >= 1.5
            && pair.FaceSeparation <= Math.Max(26.0, options.DefaultWallThickness * 5.5)
            && maxFaceFragments <= 180
            && totalFaceFragments <= 260;
    }

    private static void AddChainSupport(
        IReadOnlyList<ShellChainMember> members,
        Dictionary<string, ExteriorShellRepairSupport> result,
        ScannerOptions options)
    {
        var ordered = members.OrderBy(member => member.Start).ToArray();
        var canStandAlone =
            ordered.Length == 1
            && ordered[0].DetectionKind is WallDetectionKind.FragmentMerged or WallDetectionKind.SingleLine
            && ordered[0].OverlapLength >= Math.Max(120.0, options.MinWallLength * 5.0)
            && ordered[0].WallCoverageRatio >= 0.70
            && ordered[0].EdgeCoverageRatio >= 0.20
            && ordered[0].MemberScore >= 0.74;
        if (members.Count < 2 && !canStandAlone)
        {
            return;
        }

        var chainStart = ordered.Min(member => member.Start);
        var chainEnd = ordered.Max(member => member.End);
        var chainSpan = Math.Max(1.0, chainEnd - chainStart);
        var coveredLength = MergedIntervalLength(ordered.Select(member => (member.Start, member.End)));
        var chainCoverage = coveredLength / chainSpan;
        var edgeCoverage = ordered.Sum(member => member.OverlapLength)
            / Math.Max(1.0, Math.Abs(ordered[0].EnvelopeEdgeEnd - ordered[0].EnvelopeEdgeStart));
        var averageMemberScore = ordered.Average(member => member.MemberScore);
        var minMemberScore = ordered.Min(member => member.MemberScore);
        var maxAxisDistance = ordered.Max(member => member.AxisDistance);
        var supportScore =
            averageMemberScore
            + Math.Min(0.18, chainCoverage * 0.18)
            + Math.Min(0.14, edgeCoverage * 0.42)
            + Math.Min(0.10, coveredLength / Math.Max(240.0, options.MinWallLength * 10.0))
            - Math.Min(0.08, maxAxisDistance / Math.Max(1.0, options.DefaultWallThickness * 30.0));
        if (coveredLength < Math.Max(84.0, options.MinWallLength * 3.5)
            || chainCoverage < 0.42
            || edgeCoverage < 0.08
            || minMemberScore < 0.64
            || supportScore < 0.88)
        {
            return;
        }

        var supportIds = ordered
            .Select(member => member.WallId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var member in ordered)
        {
            var memberScore =
                supportScore
                + Math.Min(0.06, member.WallCoverageRatio * 0.06)
                + Math.Min(0.04, member.EdgeCoverageRatio * 0.16);
            var support = new ExteriorShellRepairSupport(
                member.WallId,
                member.PageNumber,
                "global-envelope-fragment-chain",
                supportIds.Where(id => !string.Equals(id, member.WallId, StringComparison.Ordinal)).ToArray(),
                memberScore,
                new[]
                {
                    "wall evidence: exterior shell repair support global-envelope-fragment-chain from collinear exterior candidates on same envelope edge",
                    $"wall evidence: shell chain edge {member.EdgeName}, members {supportIds.Length}, covered {coveredLength:0.###}, chain coverage {chainCoverage:0.###}, edge coverage {edgeCoverage:0.###}, support score {memberScore:0.###}",
                    $"wall evidence: shell chain member score {member.MemberScore:0.###}, detection {member.DetectionKind}, axis distance {member.AxisDistance:0.###}, wall coverage {member.WallCoverageRatio:0.###}"
                });

            if (!result.TryGetValue(member.WallId, out var existing) || support.SupportScore > existing.SupportScore)
            {
                result[member.WallId] = support;
            }
        }
    }

    private static double MergedIntervalLength(IEnumerable<(double Start, double End)> intervals)
    {
        var ordered = intervals
            .Select(item => (Start: Math.Min(item.Start, item.End), End: Math.Max(item.Start, item.End)))
            .Where(item => item.End > item.Start)
            .OrderBy(item => item.Start)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var total = 0.0;
        var start = ordered[0].Start;
        var end = ordered[0].End;
        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (current.Start <= end)
            {
                end = Math.Max(end, current.End);
                continue;
            }

            total += end - start;
            start = current.Start;
            end = current.End;
        }

        total += end - start;
        return total;
    }

    private static bool IsStructuralShellStrokeCandidate(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        IReadOnlyList<string> evidence,
        ScannerOptions options)
    {
        if (wall.DetectionKind is not (WallDetectionKind.FragmentMerged or WallDetectionKind.SingleLine)
            || wall.DrawingLength < Math.Max(72.0, options.MinWallLength * 3.0)
            || wall.Confidence.Value < 0.55
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody
                or WallEvidenceCategory.DimensionOrAnnotation)
            || evidence.Any(item =>
                item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
                || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var explicitExteriorEvidence = wall.WallType == WallType.Exterior
            || evidence.Any(item =>
                item.Contains("wall type exterior", StringComparison.OrdinalIgnoreCase)
                || item.Contains("near detected floorplan/wall envelope", StringComparison.OrdinalIgnoreCase)
                || item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase));
        if (!explicitExteriorEvidence)
        {
            return false;
        }

        var fragment = wall.FragmentEvidence;
        if (fragment is null)
        {
            return wall.DrawingLength >= Math.Max(120.0, options.MinWallLength * 5.0);
        }

        return fragment.FragmentCount >= 1
            && fragment.FragmentCount <= 180
            && fragment.DuplicatePrimitiveCount <= 48
            && (fragment.RequiresGeometryReview == false
                || wall.DrawingLength >= Math.Max(120.0, options.MinWallLength * 5.0))
            && fragment.GapRatio <= 0.45
            && fragment.MaxHealedGap <= Math.Max(120.0, wall.DrawingLength * 0.45);
    }

    private static double StructuralShellMemberScore(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        if (wall.PairEvidence is { } pair)
        {
            return pair.Score;
        }

        var score = 0.62;
        if (wall.WallType == WallType.Exterior)
        {
            score += 0.08;
        }

        if (wall.DetectionKind == WallDetectionKind.FragmentMerged)
        {
            score += 0.06;
        }

        if (wall.DrawingLength >= 160.0)
        {
            score += 0.07;
        }
        else if (wall.DrawingLength >= 96.0)
        {
            score += 0.04;
        }

        if (assessment.Category == WallEvidenceCategory.StrongWallBody)
        {
            score += 0.08;
        }
        else if (assessment.Category is WallEvidenceCategory.MediumWallBody or WallEvidenceCategory.RecoveredWallBody)
        {
            score += 0.05;
        }
        else if (assessment.Category == WallEvidenceCategory.DimensionOrAnnotation)
        {
            score -= 0.06;
        }

        if (wall.FragmentEvidence is { } fragment)
        {
            if (fragment.RequiresGeometryReview)
            {
                score -= 0.04;
            }

            if (fragment.GapRatio <= 0.02)
            {
                score += 0.03;
            }
        }

        return Math.Clamp(score, 0.0, 0.86);
    }

    private static bool TryMatchEnvelopeEdge(
        PlanRect envelope,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end,
        double coordinateTolerance,
        out string edgeName,
        out double axisDistance,
        out double overlapLength,
        out double wallCoverageRatio,
        out double edgeCoverageRatio)
    {
        edgeName = string.Empty;
        axisDistance = 0;
        overlapLength = 0;
        wallCoverageRatio = 0;
        edgeCoverageRatio = 0;

        var candidates = orientation == AxisOrientation.Horizontal
            ? new[]
            {
                new EnvelopeEdge("top", envelope.Top, envelope.Left, envelope.Right),
                new EnvelopeEdge("bottom", envelope.Bottom, envelope.Left, envelope.Right)
            }
            : new[]
            {
                new EnvelopeEdge("left", envelope.Left, envelope.Top, envelope.Bottom),
                new EnvelopeEdge("right", envelope.Right, envelope.Top, envelope.Bottom)
            };
        EnvelopeEdge? best = null;
        var bestAxisDistance = double.PositiveInfinity;
        var bestOverlapLength = 0.0;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(coordinate - candidate.Coordinate);
            if (distance > coordinateTolerance)
            {
                continue;
            }

            var overlap = OverlapLength(start, end, candidate.Start, candidate.End);
            if (overlap <= 0)
            {
                continue;
            }

            if (best is null
                || overlap > bestOverlapLength
                || Math.Abs(overlap - bestOverlapLength) <= 0.001 && distance < bestAxisDistance)
            {
                best = candidate;
                bestAxisDistance = distance;
                bestOverlapLength = overlap;
            }
        }

        if (best is null)
        {
            return false;
        }

        var wallLength = Math.Max(1.0, Math.Abs(end - start));
        var edgeLength = Math.Max(1.0, Math.Abs(best.Value.End - best.Value.Start));
        edgeName = best.Value.Name;
        axisDistance = bestAxisDistance;
        overlapLength = bestOverlapLength;
        wallCoverageRatio = bestOverlapLength / wallLength;
        edgeCoverageRatio = bestOverlapLength / edgeLength;
        return true;
    }

    private static IEnumerable<string> FindEnvelopeSupportWallIds(
        WallSegment wall,
        IReadOnlyList<WallSegment> trustedExteriorWalls,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end,
        double coordinateTolerance)
    {
        foreach (var other in trustedExteriorWalls)
        {
            if (other.PageNumber != wall.PageNumber
                || string.Equals(other.Id, wall.Id, StringComparison.Ordinal)
                || !TryResolveAxisInterval(other.CenterLine, out var otherOrientation, out var otherCoordinate, out var otherStart, out var otherEnd))
            {
                continue;
            }

            if (otherOrientation == orientation
                && Math.Abs(otherCoordinate - coordinate) <= coordinateTolerance * 1.35
                && OverlapLength(start, end, otherStart, otherEnd) >= Math.Min(24.0, Math.Abs(end - start) * 0.20))
            {
                yield return other.Id;
            }
            else if (otherOrientation != orientation
                && (EndpointTouchesAxisLine(other.CenterLine.Start, orientation, coordinate, start, end, coordinateTolerance)
                    || EndpointTouchesAxisLine(other.CenterLine.End, orientation, coordinate, start, end, coordinateTolerance)))
            {
                yield return other.Id;
            }
        }
    }

    private static bool EndpointTouchesAxisLine(
        PlanPoint endpoint,
        AxisOrientation orientation,
        double coordinate,
        double start,
        double end,
        double tolerance)
    {
        var endpointCoordinate = orientation == AxisOrientation.Horizontal ? endpoint.Y : endpoint.X;
        var endpointAlong = orientation == AxisOrientation.Horizontal ? endpoint.X : endpoint.Y;
        return Math.Abs(endpointCoordinate - coordinate) <= tolerance
            && endpointAlong >= Math.Min(start, end) - tolerance
            && endpointAlong <= Math.Max(start, end) + tolerance;
    }

    private static double OverlapLength(double firstA, double firstB, double secondA, double secondB)
    {
        var firstMin = Math.Min(firstA, firstB);
        var firstMax = Math.Max(firstA, firstB);
        var secondMin = Math.Min(secondA, secondB);
        var secondMax = Math.Max(secondA, secondB);
        return Math.Max(0, Math.Min(firstMax, secondMax) - Math.Max(firstMin, secondMin));
    }

    private static double DistanceFromAxisEndpointToSegment(
        AxisOrientation endpointOrientation,
        PlanPoint endpoint,
        PlanLineSegment segment,
        double endpointTolerance)
    {
        var segmentHorizontal = Math.Abs(segment.End.X - segment.Start.X) >= Math.Abs(segment.End.Y - segment.Start.Y);
        if (endpointOrientation == AxisOrientation.Horizontal && segmentHorizontal
            || endpointOrientation == AxisOrientation.Vertical && !segmentHorizontal)
        {
            return double.PositiveInfinity;
        }

        var parameterTolerance = endpointTolerance / Math.Max(segment.Length, 1.0);
        var parameter = segment.ProjectParameter(endpoint);
        if (parameter < -parameterTolerance || parameter > 1.0 + parameterTolerance)
        {
            return double.PositiveInfinity;
        }

        var projected = segment.PointAt(Math.Clamp(parameter, 0.0, 1.0));
        return endpoint.DistanceTo(projected);
    }

    private static bool HasExteriorShellRepairBlocker(
        WallSegment wall,
        WallEvidenceWallAssessment assessment,
        bool allowGraphObjectLikeReclassification,
        bool allowDimensionLikeStructuralShell)
    {
        var evidence = EvidenceFor(wall, assessment);
        return evidence.Any(item =>
        {
            if (allowGraphObjectLikeReclassification
                && (IsGraphObjectLikeReclassificationEvidence(item)
                    || item.Contains("explicit non-wall evidence: ObjectOrFixtureDetail", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (allowDimensionLikeStructuralShell
                && IsDimensionLikeEvidence(item))
            {
                return false;
            }

            return item.Contains("outdoor covered-area", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-area boundary", StringComparison.OrdinalIgnoreCase)
                || item.Contains("unpaired outdoor", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                || item.Contains("overbygd", StringComparison.OrdinalIgnoreCase)
                || item.Contains("terrace", StringComparison.OrdinalIgnoreCase)
                || item.Contains("canopy", StringComparison.OrdinalIgnoreCase)
                || item.Contains("railing", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("surface/detail pattern", StringComparison.OrdinalIgnoreCase)
                || item.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
                || item.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door swing", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
                || item.Contains("door arc", StringComparison.OrdinalIgnoreCase)
                || item.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
                || item.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
                || item.Contains("witness/extension", StringComparison.OrdinalIgnoreCase)
                || item.Contains("stair", StringComparison.OrdinalIgnoreCase)
                || item.Contains("non-wall", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsDimensionLikeEvidence(string evidence) =>
        evidence.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("classified Dimension", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("layer evidence: contains dimension", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> EvidenceFor(
        WallSegment wall,
        WallEvidenceWallAssessment assessment) =>
        wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

    private static bool IsGraphObjectLikeReclassificationEvidence(IReadOnlyList<string> evidence) =>
        evidence.Any(IsGraphObjectLikeReclassificationEvidence);

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

    private readonly record struct StructuralExteriorEnvelope(
        int PageNumber,
        PlanRect Bounds,
        string SourceKind,
        int RoomSeedCount,
        int WallSeedCount,
        bool UsesMainFloorplanFallback);

    private readonly record struct EnvelopeEdge(
        string Name,
        double Coordinate,
        double Start,
        double End);

    private readonly record struct ShellChainKey(
        int PageNumber,
        string EdgeName,
        AxisOrientation Orientation);

    private readonly record struct ShellChainMember(
        string WallId,
        int PageNumber,
        string EdgeName,
        AxisOrientation Orientation,
        WallDetectionKind DetectionKind,
        double Coordinate,
        double Start,
        double End,
        double OverlapLength,
        double WallCoverageRatio,
        double EdgeCoverageRatio,
        double AxisDistance,
        double MemberScore,
        StructuralExteriorEnvelope Envelope)
    {
        public double EnvelopeEdgeStart =>
            Orientation == AxisOrientation.Horizontal
                ? Envelope.Bounds.Left
                : Envelope.Bounds.Top;

        public double EnvelopeEdgeEnd =>
            Orientation == AxisOrientation.Horizontal
                ? Envelope.Bounds.Right
                : Envelope.Bounds.Bottom;
    }
}
