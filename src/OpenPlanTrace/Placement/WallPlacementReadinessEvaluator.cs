namespace OpenPlanTrace;

public static class WallPlacementReadinessEvaluator
{
    public const string TopologySupportedFragmentedPairPromotionEvidence =
        "topology-supported fragmented paired wall promoted";

    public const string WeakPromotedFragmentRoomBoundaryReason =
        "promoted fragment-merged room boundary has no supported topology endpoint; keep for review until explicit or geometric room-boundary support confirms exact placement";

    public const string ThinExteriorFacePairWithoutShellSupportReason =
        "thin exterior parallel-face wall candidate lacks trusted exterior shell support";

    public const string NoisyTopologySupportedFragmentedPairReason =
        "topology-supported fragmented paired wall has excessive face fragmentation for exact placement";

    private const double MaxShortDenseDetailCandidateLengthDrawingUnits = 55.0;
    private const double MinShortDenseDetailCandidateSourceDensity = 0.65;
    private const double MaxNoisyTopologySupportedFragmentedPairLengthDrawingUnits = 72.0;
    private const int MaxNoisyTopologySupportedFragmentedPairFaceFragments = 64;
    private const double MaxThinExteriorFacePairSeparationDrawingUnits = 3.25;
    private const double MaxThinExteriorFacePairThicknessMillimeters = 65.0;

    public static WallPlacementReadiness Evaluate(
        WallSegment wall,
        PlanCalibration calibration,
        WallGraphComponent? component = null,
        WallEvidenceWallAssessment? evidenceAssessment = null,
        IEnumerable<string>? reviewReasons = null)
    {
        ArgumentNullException.ThrowIfNull(wall);
        ArgumentNullException.ThrowIfNull(calibration);

        var reasons = new List<string>();
        if (wall.Confidence.Value < 0.5)
        {
            reasons.Add("wall confidence below 0.5");
        }

        if (!calibration.HasReliableMeasurementScale)
        {
            reasons.Add("metric scale unavailable");
        }

        if (component is not null)
        {
            AddComponentReasons(component, reasons);
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            reasons.Add("wall fragment geometry requires review before exact placement");
        }

        if (evidenceAssessment is not null)
        {
            AddEvidenceReasons(evidenceAssessment, reasons);
        }

        var reviewReasonList = reviewReasons?
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .ToArray()
            ?? Array.Empty<string>();
        if (reviewReasonList.Length > 0)
        {
            reasons.AddRange(reviewReasonList);
        }

        var coordinatePlacementBlocked = CoordinatePlacementBlockedByComponent(component);
        var coordinatePlacementBlockedByReviewReason = reviewReasonList.Any(IsCoordinateBlockingReviewReason);
        var coordinatePlacementBlockedByRecoveredExteriorEvidence =
            CoordinatePlacementBlockedByRecoveredOneSidedExteriorEvidence(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByRecoveredExteriorEvidence)
        {
            reasons.Add("recovered exterior wall has only one-sided room evidence and no trusted exterior shell support");
        }

        var coordinatePlacementBlockedByShortDenseDetailEvidence =
            CoordinatePlacementBlockedByShortDenseDetailEvidence(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByShortDenseDetailEvidence)
        {
            reasons.Add("short high-density unknown-layer wall/detail candidate requires review before exact placement");
        }

        var coordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence =
            CoordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence)
        {
            reasons.Add("outdoor/terrace room evidence alone is not trusted as exterior wall placement support");
        }

        var coordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary =
            CoordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary)
        {
            reasons.Add(WeakPromotedFragmentRoomBoundaryReason);
        }

        var coordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair =
            CoordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair)
        {
            reasons.Add(NoisyTopologySupportedFragmentedPairReason);
        }

        var coordinatePlacementBlockedByThinExteriorFacePair =
            CoordinatePlacementBlockedByThinExteriorFacePairWithoutShellSupport(wall, evidenceAssessment);
        if (coordinatePlacementBlockedByThinExteriorFacePair)
        {
            reasons.Add(ThinExteriorFacePairWithoutShellSupportReason);
        }

        var readyForCoordinatePlacement =
            wall.Confidence.Value >= 0.5
            && !coordinatePlacementBlocked
            && !coordinatePlacementBlockedByReviewReason
            && !coordinatePlacementBlockedByRecoveredExteriorEvidence
            && !coordinatePlacementBlockedByShortDenseDetailEvidence
            && !coordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence
            && !coordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary
            && !coordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair
            && !coordinatePlacementBlockedByThinExteriorFacePair
            && wall.FragmentEvidence?.RequiresGeometryReview != true
            && (evidenceAssessment is null || evidenceAssessment.PlacementReady);
        var readyForMetricPlacement =
            readyForCoordinatePlacement
            && calibration.HasReliableMeasurementScale;

        return new WallPlacementReadiness(
            readyForCoordinatePlacement,
            readyForMetricPlacement,
            reasons.Count > 0
            || coordinatePlacementBlocked
            || coordinatePlacementBlockedByReviewReason
            || coordinatePlacementBlockedByShortDenseDetailEvidence
            || coordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence
            || coordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary
            || coordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair
            || coordinatePlacementBlockedByThinExteriorFacePair
            || evidenceAssessment?.RequiresReview == true
            || evidenceAssessment?.RejectedAsNoise == true
            || evidenceAssessment?.PlacementReady == false,
            wall.Confidence,
            coordinatePlacementBlocked
            || coordinatePlacementBlockedByReviewReason
            || coordinatePlacementBlockedByRecoveredExteriorEvidence
            || coordinatePlacementBlockedByShortDenseDetailEvidence
            || coordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence
            || coordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary
            || coordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair
            || coordinatePlacementBlockedByThinExteriorFacePair,
            reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void AddComponentReasons(
        WallGraphComponent component,
        List<string> reasons)
    {
        if (component.ExcludedFromStructuralTopology)
        {
            reasons.Add("wall component excluded from structural topology");
        }

        if (component.Kind == WallGraphComponentKind.ObjectLikeIsland)
        {
            reasons.Add("wall belongs to compact object-like linework component");
        }
        else if (component.Kind == WallGraphComponentKind.IsolatedFragment)
        {
            reasons.Add("wall belongs to isolated wall graph fragment");
        }
    }

    private static void AddEvidenceReasons(
        WallEvidenceWallAssessment evidenceAssessment,
        List<string> reasons)
    {
        if (!evidenceAssessment.PlacementReady)
        {
            reasons.Add($"wall evidence not placement-ready ({evidenceAssessment.Category})");
        }

        if (evidenceAssessment.RequiresReview)
        {
            reasons.Add($"wall evidence requires review ({evidenceAssessment.Category})");
        }

        if (evidenceAssessment.RejectedAsNoise)
        {
            reasons.Add($"wall evidence rejected as non-wall/noise ({evidenceAssessment.Category})");
        }
    }

    private static bool CoordinatePlacementBlockedByComponent(WallGraphComponent? component) =>
        component?.ExcludedFromStructuralTopology == true
        || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment;

    private static bool CoordinatePlacementBlockedByRecoveredOneSidedExteriorEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment?.Category != WallEvidenceCategory.RecoveredWallBody
            || wall.WallType != WallType.Exterior)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .ToArray();
        if (!evidence.Any(item => item.Contains("detected room evidence on one side only", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !evidence.Any(IsTrustedRecoveredExteriorSupportEvidence);
    }

    private static bool IsTrustedRecoveredExteriorSupportEvidence(string evidence)
    {
        if (evidence.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("without shell support", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("alone is not", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return evidence.Contains("exterior shell", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("wall-like layer", StringComparison.OrdinalIgnoreCase)
            || evidence.Contains("trusted benchmark", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CoordinatePlacementBlockedByShortDenseDetailEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment is null
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.MediumWallBody)
            || wall.DrawingLength <= 0.001
            || wall.DrawingLength > MaxShortDenseDetailCandidateLengthDrawingUnits)
        {
            return false;
        }

        var sourceDensity = evidenceAssessment.SourcePrimitiveIds
            .Concat(wall.SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Count() / wall.DrawingLength;
        if (sourceDensity < MinShortDenseDetailCandidateSourceDensity)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();

        return EvidenceContains(evidence, "layer (unlayered) classified Unknown")
            && EvidenceContainsAny(
                evidence,
                "collapsed",
                "merged",
                "fragment")
            && !EvidenceContains(evidence, TopologySupportedFragmentedPairPromotionEvidence)
            && !EvidenceContainsAny(
                evidence,
                "wall-like layer",
                "room boundary",
                "detected room evidence on both sides",
                "exterior shell",
                "outdoor/terrace",
                "trusted benchmark");
    }

    private static bool CoordinatePlacementBlockedByUntrustedOutdoorBoundaryEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment is null
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!EvidenceContainsAny(
                evidence,
                "outdoor/terrace room evidence alone is not trusted as exterior",
                "outdoor/terrace room evidence is not trusted as exterior without shell support"))
        {
            return false;
        }

        return !evidence.Any(IsTrustedRecoveredExteriorSupportEvidence);
    }

    private static bool CoordinatePlacementBlockedByWeakPromotedFragmentRoomBoundary(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment is null
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.WallType != WallType.Interior
            || wall.PairEvidence is not null
            || wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!EvidenceContains(evidence, "clean fragment-merged interior room boundary promoted")
            || !EvidenceContains(evidence, "room references 1")
            || !EvidenceContains(evidence, "shared adjacency False")
            || !EvidenceContains(evidence, "two-sided room evidence False")
            || !EvidenceContains(evidence, "topology-supported endpoints 0"))
        {
            return false;
        }

        return !EvidenceContainsAny(
            evidence,
            "geometric room boundary support",
            "explicit room boundary support",
            "detected room evidence on both sides",
            "short structural return promoted by room boundary and two supported topology endpoints");
    }

    private static bool CoordinatePlacementBlockedByNoisyTopologySupportedFragmentedPair(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment is null
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || evidenceAssessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.WallType != WallType.Interior
            || wall.DrawingLength > MaxNoisyTopologySupportedFragmentedPairLengthDrawingUnits
            || wall.PairEvidence is not { } pair)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!EvidenceContains(evidence, TopologySupportedFragmentedPairPromotionEvidence)
            || EvidenceContainsAny(
                evidence,
                "wall-like layer",
                "explicit room boundary support",
                "detected room evidence on both sides",
                "short structural return promoted by room boundary"))
        {
            return false;
        }

        return Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount)
            > MaxNoisyTopologySupportedFragmentedPairFaceFragments;
    }

    private static bool CoordinatePlacementBlockedByThinExteriorFacePairWithoutShellSupport(
        WallSegment wall,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (evidenceAssessment is null
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType != WallType.Exterior
            || wall.PairEvidence is not { FaceSeparation: > 0 } pair)
        {
            return false;
        }

        var thinInDrawingUnits = pair.FaceSeparation < MaxThinExteriorFacePairSeparationDrawingUnits;
        var thinInMillimeters = wall.ThicknessMillimeters is > 0
            && wall.ThicknessMillimeters < MaxThinExteriorFacePairThicknessMillimeters;
        if (!thinInDrawingUnits && !thinInMillimeters)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (evidence.Any(IsTrustedRecoveredExteriorSupportEvidence))
        {
            return false;
        }

        return EvidenceContainsAny(
                evidence,
                "layer (unlayered) classified Unknown",
                "layer evidence: no strong layer",
                "source layer category Unknown")
            && EvidenceContainsAny(
                evidence,
                "near detected floorplan/wall envelope",
                "local outer boundary",
                "detected room evidence on one side only",
                "geometric room boundary support");
    }

    private static bool EvidenceContainsAny(
        IReadOnlyList<string> evidence,
        params string[] fragments) =>
        fragments.Any(fragment => EvidenceContains(evidence, fragment));

    private static bool EvidenceContains(
        IReadOnlyList<string> evidence,
        string fragment) =>
        evidence.Any(item => item.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool IsCoordinateBlockingReviewReason(string reason) =>
        (reason.Contains("wall graph repair candidate", StringComparison.OrdinalIgnoreCase)
        && reason.Contains(nameof(WallGraphRepairImportImpact.TopologyImportBlocked), StringComparison.OrdinalIgnoreCase))
        || reason.Contains(
            WallPlacementContextGuards.SecondaryStructuralWithoutRoomBoundarySupportReason,
            StringComparison.OrdinalIgnoreCase)
        || reason.Contains(
            WallPlacementContextGuards.SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason,
            StringComparison.OrdinalIgnoreCase)
        || reason.Contains(
            WallPlacementContextGuards.SecondaryStructuralOverSourcedDetailLineworkReason,
            StringComparison.OrdinalIgnoreCase)
        || reason.Contains(
            WallPlacementContextGuards.FragmentMergedInteriorWithoutRoomBoundarySupportReason,
            StringComparison.OrdinalIgnoreCase);
}
