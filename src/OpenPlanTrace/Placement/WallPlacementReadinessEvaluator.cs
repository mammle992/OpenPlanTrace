namespace OpenPlanTrace;

public static class WallPlacementReadinessEvaluator
{
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
        var readyForCoordinatePlacement =
            wall.Confidence.Value >= 0.5
            && !coordinatePlacementBlocked
            && !coordinatePlacementBlockedByReviewReason
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
            || evidenceAssessment?.RequiresReview == true
            || evidenceAssessment?.RejectedAsNoise == true
            || evidenceAssessment?.PlacementReady == false,
            wall.Confidence,
            coordinatePlacementBlocked || coordinatePlacementBlockedByReviewReason,
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

    private static bool IsCoordinateBlockingReviewReason(string reason) =>
        (reason.Contains("wall graph repair candidate", StringComparison.OrdinalIgnoreCase)
        && reason.Contains(nameof(WallGraphRepairImportImpact.TopologyImportBlocked), StringComparison.OrdinalIgnoreCase))
        || reason.Contains(
            WallPlacementContextGuards.SecondaryStructuralWithoutRoomBoundarySupportReason,
            StringComparison.OrdinalIgnoreCase)
        || reason.Contains(
            WallPlacementContextGuards.SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason,
            StringComparison.OrdinalIgnoreCase);
}
