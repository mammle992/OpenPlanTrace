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

    public const string TrustedExteriorShellContinuityEvidence =
        "exterior shell continuity kept fragmented paired wall placement-ready between trusted collinear exterior wall segments";

    public const string RoomConfirmedIsolatedFragmentPromotionEvidence =
        "room-confirmed isolated wall graph fragment kept placement-ready";

    private const double MaxShortDenseDetailCandidateLengthDrawingUnits = 55.0;
    private const double MinShortDenseDetailCandidateSourceDensity = 0.65;
    private const double MaxNoisyTopologySupportedFragmentedPairLengthDrawingUnits = 72.0;
    private const int MaxNoisyTopologySupportedFragmentedPairFaceFragments = 64;
    private const double MaxThinExteriorFacePairSeparationDrawingUnits = 3.25;
    private const double MaxThinExteriorFacePairThicknessMillimeters = 65.0;
    private const double MinRoomBackedThinExteriorLengthDrawingUnits = 80.0;
    private const double MinRoomBackedThinExteriorPairScore = 0.78;
    private const double MinTrustedMainStructuralThinExteriorBridgeLengthDrawingUnits = 100.0;
    private const double MinTrustedMainStructuralThinExteriorBridgePairScore = 0.80;
    private const double MinTrustedMainStructuralThinExteriorBridgeOverlapRatio = 0.95;
    private const int MaxTrustedMainStructuralThinExteriorBridgeFaceFragments = 72;
    private const double MinTrustedExteriorShellContinuityLengthDrawingUnits = 80.0;
    private const double MinTrustedExteriorShellContinuityPairScore = 0.60;
    private const double MinTrustedExteriorShellContinuityOverlapRatio = 0.95;
    private const double MinTrustedExteriorShellContinuityFaceSeparationDrawingUnits = 2.0;
    private const double MaxTrustedExteriorShellContinuityFaceSeparationDrawingUnits = 18.0;
    private const int MaxTrustedExteriorShellContinuityFaceFragments = 144;
    private const double MinTrustedTwoSidedRoomIsolatedLengthDrawingUnits = 72.0;
    private const double MinTrustedTwoSidedRoomIsolatedPairScore = 0.90;
    private const double MinTrustedTwoSidedRoomIsolatedOverlapRatio = 0.95;
    private const double MinTrustedTwoSidedRoomIsolatedFaceSeparationDrawingUnits = 2.0;
    private const double MaxTrustedTwoSidedRoomIsolatedFaceSeparationDrawingUnits = 18.0;
    private const int MaxTrustedTwoSidedRoomIsolatedFaceFragments = 32;
    private const double MinTrustedTwoSidedFragmentMergedRoomBoundaryLengthDrawingUnits = 72.0;
    private const int MaxTrustedTwoSidedFragmentMergedRoomBoundaryFragments = 5;
    private const int MaxTrustedTwoSidedFragmentMergedRoomBoundaryDuplicateFragments = 2;

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

        var trustedExteriorShellContinuityFragment = IsTrustedExteriorShellContinuityFragment(
            wall,
            component,
            evidenceAssessment);
        var trustedRoomBoundaryIsolatedFragment = IsTrustedRoomBoundaryIsolatedFragment(
            wall,
            component,
            evidenceAssessment);
        var trustedTwoSidedFragmentMergedRoomBoundary = IsTrustedTwoSidedFragmentMergedRoomBoundary(
            wall,
            component,
            evidenceAssessment);

        if (component is not null)
        {
            AddComponentReasons(
                component,
                reasons,
                trustedExteriorShellContinuityFragment,
                trustedRoomBoundaryIsolatedFragment);
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            reasons.Add("wall fragment geometry requires review before exact placement");
        }

        if (evidenceAssessment is not null)
        {
            AddEvidenceReasons(evidenceAssessment, reasons, trustedTwoSidedFragmentMergedRoomBoundary);
        }

        var reviewReasonList = reviewReasons?
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .ToArray()
            ?? Array.Empty<string>();
        if (reviewReasonList.Length > 0)
        {
            reasons.AddRange(reviewReasonList);
        }

        var coordinatePlacementBlocked = CoordinatePlacementBlockedByComponent(
            component,
            trustedExteriorShellContinuityFragment,
            trustedRoomBoundaryIsolatedFragment);
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
            CoordinatePlacementBlockedByThinExteriorFacePairWithoutShellSupport(wall, component, evidenceAssessment);
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
            && (evidenceAssessment is null
                || evidenceAssessment.PlacementReady
                || trustedTwoSidedFragmentMergedRoomBoundary);
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
            || (!trustedTwoSidedFragmentMergedRoomBoundary && evidenceAssessment?.RequiresReview == true)
            || evidenceAssessment?.RejectedAsNoise == true
            || (!trustedTwoSidedFragmentMergedRoomBoundary && evidenceAssessment?.PlacementReady == false),
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
        List<string> reasons,
        bool trustedExteriorShellContinuityFragment,
        bool trustedRoomBoundaryIsolatedFragment)
    {
        if (component.ExcludedFromStructuralTopology)
        {
            reasons.Add("wall component excluded from structural topology");
        }

        if (component.Kind == WallGraphComponentKind.ObjectLikeIsland)
        {
            reasons.Add("wall belongs to compact object-like linework component");
        }
        else if (component.Kind == WallGraphComponentKind.IsolatedFragment
            && !trustedExteriorShellContinuityFragment
            && !trustedRoomBoundaryIsolatedFragment)
        {
            reasons.Add("wall belongs to isolated wall graph fragment");
        }
    }

    private static void AddEvidenceReasons(
        WallEvidenceWallAssessment evidenceAssessment,
        List<string> reasons,
        bool trustedTwoSidedFragmentMergedRoomBoundary)
    {
        if (!evidenceAssessment.PlacementReady
            && !trustedTwoSidedFragmentMergedRoomBoundary)
        {
            reasons.Add($"wall evidence not placement-ready ({evidenceAssessment.Category})");
        }

        if (evidenceAssessment.RequiresReview
            && !trustedTwoSidedFragmentMergedRoomBoundary)
        {
            reasons.Add($"wall evidence requires review ({evidenceAssessment.Category})");
        }

        if (evidenceAssessment.RejectedAsNoise)
        {
            reasons.Add($"wall evidence rejected as non-wall/noise ({evidenceAssessment.Category})");
        }
    }

    private static bool CoordinatePlacementBlockedByComponent(
        WallGraphComponent? component,
        bool trustedExteriorShellContinuityFragment,
        bool trustedRoomBoundaryIsolatedFragment) =>
        component?.ExcludedFromStructuralTopology == true
        || component?.Kind == WallGraphComponentKind.ObjectLikeIsland
        || (component?.Kind == WallGraphComponentKind.IsolatedFragment
            && !trustedExteriorShellContinuityFragment
            && !trustedRoomBoundaryIsolatedFragment);

    public static bool IsTrustedExteriorShellContinuityFragment(
        WallSegment? wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (wall is null
            || component is null
            || evidenceAssessment is null
            || component.ExcludedFromStructuralTopology
            || component.Kind != WallGraphComponentKind.IsolatedFragment
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RequiresReview
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || evidenceAssessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody)
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedExteriorShellContinuityLengthDrawingUnits
            || wall.PairEvidence is not { } pair)
        {
            return false;
        }

        if (pair.Score < MinTrustedExteriorShellContinuityPairScore
            || pair.OverlapRatio < MinTrustedExteriorShellContinuityOverlapRatio
            || pair.FaceSeparation < MinTrustedExteriorShellContinuityFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxTrustedExteriorShellContinuityFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxTrustedExteriorShellContinuityFaceFragments)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();

        if (!EvidenceContains(evidence, TrustedExteriorShellContinuityEvidence))
        {
            return false;
        }

        return !EvidenceContainsAny(
            evidence,
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered-entry",
            "covered entry",
            "overbygd",
            "canopy",
            "railing",
            "trim",
            "glazing",
            "detail linework",
            "not trusted",
            "without shell support",
            "alone is not trusted");
    }

    public static bool IsTrustedRoomBoundaryIsolatedFragment(
        WallSegment? wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (wall is null
            || component is null
            || evidenceAssessment is null
            || component.ExcludedFromStructuralTopology
            || component.Kind != WallGraphComponentKind.IsolatedFragment
            || wall.WallType != WallType.Interior
            || wall.DetectionKind is WallDetectionKind.SingleLine or WallDetectionKind.Unknown
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || wall.Confidence.Value < 0.6
            || !evidenceAssessment.PlacementReady
            || evidenceAssessment.RequiresReview
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || evidenceAssessment.Category is not (WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = wall.Evidence.Concat(evidenceAssessment.Evidence).ToArray();
        var hasRoomConfirmedPromotion = EvidenceContains(evidence, RoomConfirmedIsolatedFragmentPromotionEvidence);
        var hasTrustedTwoSidedRoomBoundary = HasTrustedTwoSidedRoomBoundaryIsolatedEvidence(
            wall,
            evidenceAssessment,
            evidence);
        if (!hasRoomConfirmedPromotion && !hasTrustedTwoSidedRoomBoundary)
        {
            return false;
        }

        var blocked = EvidenceContainsAny(
            evidence,
            "outdoor",
            "terrace",
            "covered-area",
            "covered entry",
            "covered-entry",
            "overbygd",
            "object/fixture detail",
            "surface pattern",
            "door/opening")
            || (!hasTrustedTwoSidedRoomBoundary && EvidenceContains(evidence, "dimension-like"));
        return !blocked;
    }

    private static bool HasTrustedTwoSidedRoomBoundaryIsolatedEvidence(
        WallSegment wall,
        WallEvidenceWallAssessment evidenceAssessment,
        IReadOnlyList<string> evidence)
    {
        if (wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedTwoSidedRoomIsolatedLengthDrawingUnits
            || evidenceAssessment.Category != WallEvidenceCategory.StrongWallBody
            || wall.PairEvidence is not { } pair)
        {
            return false;
        }

        if (pair.Score < MinTrustedTwoSidedRoomIsolatedPairScore
            || pair.OverlapRatio < MinTrustedTwoSidedRoomIsolatedOverlapRatio
            || pair.FaceSeparation < MinTrustedTwoSidedRoomIsolatedFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxTrustedTwoSidedRoomIsolatedFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount)
                > MaxTrustedTwoSidedRoomIsolatedFaceFragments)
        {
            return false;
        }

        return EvidenceContains(evidence, "detected room evidence on both sides")
            && EvidenceContains(evidence, "supported wall evidence inside exterior envelope");
    }

    public static bool IsTrustedTwoSidedFragmentMergedRoomBoundary(
        WallSegment? wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        if (wall is null
            || component is null
            || evidenceAssessment is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.PairEvidence is not null
            || wall.DrawingLength < MinTrustedTwoSidedFragmentMergedRoomBoundaryLengthDrawingUnits
            || wall.Confidence.Value < 0.78
            || evidenceAssessment.Confidence.Value < 0.78
            || evidenceAssessment.RejectedAsNoise
            || evidenceAssessment.Decision == WallEvidenceDecision.Reject
            || evidenceAssessment.Category != WallEvidenceCategory.MediumWallBody
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence)
        {
            return false;
        }

        var uniqueSourcePrimitiveCount = Math.Max(0, wall.SourcePrimitiveIds.Count - fragmentEvidence.DuplicatePrimitiveCount);
        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, uniqueSourcePrimitiveCount);
        if (fragmentCount is < 2 or > MaxTrustedTwoSidedFragmentMergedRoomBoundaryFragments
            || fragmentEvidence.DuplicatePrimitiveCount > MaxTrustedTwoSidedFragmentMergedRoomBoundaryDuplicateFragments
            || fragmentEvidence.GapRatio > 0.001
            || fragmentEvidence.TotalHealedGap > 0.001)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(evidenceAssessment.Evidence)
            .Concat(evidenceAssessment.ScoreBreakdown.PositiveEvidence)
            .Concat(evidenceAssessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        if (!EvidenceContains(evidence, "detected room evidence on both sides")
            || !EvidenceContains(evidence, "supported wall evidence inside exterior envelope")
            || !EvidenceContains(evidence, "both endpoints supported by structural context"))
        {
            return false;
        }

        return !EvidenceContainsAny(
            evidence,
            "outdoor",
            "terrace",
            "covered-area",
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
            "object/fixture",
            "fixture detail",
            "stair",
            "door/opening",
            "dimension-like");
    }

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
            "short structural return promoted by room boundary and two supported topology endpoints",
            "both endpoints supported by structural context");
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
        WallGraphComponent? component,
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

        if (HasTrustedOneSidedRoomBackedThinExteriorEvidence(wall, component, evidenceAssessment, pair, evidence))
        {
            return false;
        }

        if (HasTrustedMainStructuralThinExteriorBridgeEvidence(wall, component, evidenceAssessment, pair, evidence))
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

    private static bool HasTrustedOneSidedRoomBackedThinExteriorEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment evidenceAssessment,
        WallPairEvidence pair,
        IReadOnlyList<string> evidence)
    {
        if (component?.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinRoomBackedThinExteriorLengthDrawingUnits
            || pair.Score < MinRoomBackedThinExteriorPairScore
            || pair.OverlapRatio < 0.75
            || evidenceAssessment.Category != WallEvidenceCategory.StrongWallBody)
        {
            return false;
        }

        if (!EvidenceContainsAny(
                evidence,
                "detected room evidence on one side only",
                "geometric room boundary support"))
        {
            return false;
        }

        return !EvidenceContainsAny(
            evidence,
            "outdoor",
            "terrace",
            "covered-area",
            "covered entry",
            "overbygd",
            "canopy",
            "railing",
            "trim",
            "glazing",
            "detail linework",
            "not trusted",
            "without shell support",
            "alone is not trusted");
    }

    private static bool HasTrustedMainStructuralThinExteriorBridgeEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment evidenceAssessment,
        WallPairEvidence pair,
        IReadOnlyList<string> evidence)
    {
        if (component?.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedMainStructuralThinExteriorBridgeLengthDrawingUnits
            || pair.Score < MinTrustedMainStructuralThinExteriorBridgePairScore
            || pair.OverlapRatio < MinTrustedMainStructuralThinExteriorBridgeOverlapRatio
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxTrustedMainStructuralThinExteriorBridgeFaceFragments
            || evidenceAssessment.Category != WallEvidenceCategory.StrongWallBody)
        {
            return false;
        }

        if (!EvidenceContainsAny(
                evidence,
                "near detected floorplan/wall envelope",
                "local outer boundary"))
        {
            return false;
        }

        if (!EvidenceContainsAny(
                evidence,
                "supported endpoint overrun",
                "exterior shell continuity"))
        {
            return false;
        }

        return !EvidenceContainsAny(
            evidence,
            "outdoor",
            "terrace",
            "covered-area",
            "covered entry",
            "covered-entry",
            "overbygd",
            "canopy",
            "railing",
            "trim linework",
            "trim/detail",
            "glazing",
            "detail linework",
            "not trusted",
            "without shell support",
            "alone is not trusted");
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
            StringComparison.OrdinalIgnoreCase)
        || reason.Contains(
            WallPlacementContextGuards.MainStructuralInteriorWithoutSemanticSupportReason,
            StringComparison.OrdinalIgnoreCase);
}
