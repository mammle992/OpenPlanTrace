namespace OpenPlanTrace.Export;

internal static class WallTopologySpanVisibility
{
    private const double MaxCleanDanglingSpanLength = 36.0;
    private const double MinTrustedShortStructuralDanglingSpanLength = 18.0;
    private const double MaxCleanRunJoinGapDrawingUnits = 12.0;
    private const double MinCleanRunLengthDrawingUnits = 8.0;
    private const double MinOpeningAdjacentCleanRunLengthDrawingUnits = 20.0;
    private const double MinTrustedOpeningAdjacentCleanRunLengthDrawingUnits = 6.0;
    private const double MaxContainedDuplicateAxisDistanceDrawingUnits = 1.25;
    private const double MaxNearContainedDuplicateAxisDistanceDrawingUnits = 4.0;
    private const double MaxOverlappingCollinearMergeAxisDistanceDrawingUnits = 1.25;
    private const double MaxCollinearExteriorRunBridgeAxisDistanceDrawingUnits = 5.0;
    private const double MaxCollinearExteriorRunBridgeGapDrawingUnits = 72.0;
    private const double MinCollinearExteriorRunBridgeLongNeighborLengthDrawingUnits = 48.0;
    private const double MaxCollinearExteriorRunBridgeGapToLongNeighborRatio = 0.45;
    private const double MaxCollinearInteriorSourceRunBridgeGapDrawingUnits = 48.0;
    private const double MinCollinearInteriorSourceRunBridgeLongNeighborLengthDrawingUnits = 48.0;
    private const double MaxCollinearInteriorSourceRunBridgeGapToLongNeighborRatio = 0.45;
    private const double MaxCollinearInteriorRunBridgeAxisDistanceDrawingUnits = 2.5;
    private const double MaxCollinearInteriorRunBridgeGapDrawingUnits = 36.0;
    private const double MinCollinearInteriorRunBridgeLongNeighborLengthDrawingUnits = 48.0;
    private const double MaxCollinearInteriorRunBridgeGapToLongNeighborRatio = 0.30;
    private const double MinTrustedShortInteriorRunBridgeLengthDrawingUnits = 36.0;
    private const double MaxTrustedShortInteriorRunBridgeGapDrawingUnits = 18.0;
    private const double MinTrustedShortInteriorRunBridgePairScore = 0.90;
    private const double MinContinuousExteriorOpeningTopologyLengthDrawingUnits = 80.0;
    private const double MaxCleanEndpointSnapToOrthogonalWallDistanceDrawingUnits = 8.0;
    private const double MaxCleanEndpointSnapProjectionOverrunDrawingUnits = 8.0;
    private const double MinContainedDuplicateOverlapRatio = 0.92;
    private const double MinNearContainedDuplicateOverlapRatio = 0.95;
    private const double MaxNearContainedDuplicateOverhangDrawingUnits = 4.0;
    private const double MaxContainedExteriorFragmentAxisDistanceDrawingUnits = 6.0;
    private const double MaxContainedExteriorFragmentLengthRatio = 0.55;
    private const double MinContainedExteriorFragmentOverlapRatio = 0.985;
    private const double MaxContainedSourceBackedFallbackAxisDistanceDrawingUnits = 6.0;
    private const double MaxContainedSourceBackedFallbackLengthRatio = 0.55;
    private const double MinContainedSourceBackedFallbackOverlapRatio = 0.985;
    private const double MaxTinyContainedSameTypeAxisDistanceDrawingUnits = 8.0;
    private const double MaxTinyContainedSameTypeLengthDrawingUnits = 8.0;
    private const double MaxTinyContainedSameTypeLengthRatio = 0.20;
    private const double MinTinyContainedSameTypeOverlapRatio = 0.985;
    private const double MinExteriorFacePairAxisDistanceDrawingUnits = 2.0;
    private const double MaxExteriorFacePairAxisDistanceDrawingUnits = 18.0;
    private const double MinExteriorFacePairOverlapRatio = 0.88;
    private const double MinExteriorFacePairSpanLengthDrawingUnits = 80.0;
    private const double MaxExteriorFacePairLengthRatio = 1.75;
    private const double MaxExteriorFacePairOverrunDrawingUnits = 36.0;
    private const double MinPlacementRegularizationToleranceDrawingUnits = 1.25;
    private const double MaxPlacementRegularizationToleranceDrawingUnits = 6.0;
    private const double MinPlacementRegularizationClusterLengthDrawingUnits = 60.0;
    private const double MinDominantExteriorAxisHostLengthDrawingUnits = 72.0;
    private const double MinDominantExteriorAxisHostLengthRatio = 1.45;
    private const double MaxDominantExteriorFragmentAxisSnapDrawingUnits = 6.5;
    private const double MaxDominantExteriorFragmentAxisSnapGapDrawingUnits = 18.0;
    private const double MaxDominantExteriorFragmentLengthRatio = 0.65;
    private const double MaxDominantExteriorFragmentLengthDrawingUnits = 120.0;
    private const double MaxDominantAxisSkewRatio = 0.04;
    private const double MaxDominantAxisSkewDrawingUnits = 8.0;
    private const double MinSourceBackedFallbackWallLengthDrawingUnits = 48.0;
    private const double MinTrustedInferredExteriorShellFallbackSourceCoverage = 0.85;
    private const int MinTrustedInferredExteriorShellFallbackSourcePrimitiveCount = 4;
    private const double MinSourceBackedFallbackPairScore = 0.70;
    private const double MinSourceBackedFallbackStrictPairScore = 0.74;
    private const double MinSourceBackedFallbackOverlapRatio = 0.72;
    private const double MinSourceBackedFallbackRelaxedScoreOverlapRatio = 0.96;
    private const double MinSourceBackedFallbackFaceSeparationDrawingUnits = 1.5;
    private const double MaxSourceBackedFallbackFaceSeparationDrawingUnits = 24.0;
    private const double MaxSourceBackedFallbackExistingCoverageRatio = 0.68;
    private const double MinLongSourceBackedFallbackWallLengthDrawingUnits = 120.0;
    private const double MinTrustedShortExteriorSourceBackedFallbackWallLengthDrawingUnits = 36.0;
    private const double MinTrustedShortExteriorSourceBackedFallbackPairScore = 0.88;
    private const double MinTrustedShortExteriorSourceBackedFallbackOverlapRatio = 0.95;
    private const double MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackWallLengthDrawingUnits = 30.0;
    private const double MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackConfidence = 0.86;
    private const double MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackPairScore = 0.92;
    private const int MaxTrustedRoomBoundaryShortExteriorSourceBackedFallbackFaceFragmentCount = 12;
    private const double MinRoomSupportedShortExteriorFallbackFaceSeparationDrawingUnits = 1.2;
    private const double MinUnsafeCleanTopologyProjectionDriftDrawingUnits = 12.0;
    private const double MaxUnsafeCleanTopologyProjectionDriftThicknessRatio = 3.0;
    private const double MinUnsafeCleanTopologyLengthOverrunRatio = 0.10;
    private const double MinTrustedInteriorUnsafeCleanProjectionLengthDrawingUnits = 72.0;
    private const double MinTrustedInteriorUnsafeCleanProjectionPairScore = 0.80;
    private const double MinTrustedInteriorUnsafeCleanProjectionOverlapRatio = 0.95;
    private const int MaxTrustedInteriorUnsafeCleanProjectionFaceFragments = 96;
    private const int MaxTrustedInteriorUnsafeCleanProjectionTotalFaceFragments = 180;
    private const double MinTrustedFilledExteriorUnsafeCleanProjectionLengthDrawingUnits = 72.0;
    private const double MinTrustedFilledExteriorUnsafeCleanProjectionPairScore = 0.80;
    private const double MinTrustedFilledExteriorUnsafeCleanProjectionOverlapRatio = 0.95;
    private const int MaxTrustedFilledExteriorUnsafeCleanProjectionFaceFragments = 72;
    private const int MaxTrustedFilledExteriorUnsafeCleanProjectionTotalFaceFragments = 120;
    private const int MaxSourceBackedFallbackFaceFragmentCount = 48;
    private const int MaxLongSourceBackedFallbackFaceFragmentCount = 72;
    private const int MaxTopologySupportedSourceBackedFallbackFaceFragmentCount = 96;
    private const int MaxTrustedNoisySourceBackedFallbackFaceFragmentCount = 360;
    private const double MinTrustedLongSecondaryFragmentFallbackLengthDrawingUnits = 120.0;
    private const double MinTrustedLongSecondaryFragmentFallbackConfidence = 0.82;
    private const int MaxTrustedLongSecondaryFragmentFallbackFragmentCount = 12;
    private const double MaxTrustedLongSecondaryFragmentFallbackGapRatio = 0.05;
    private const double MaxTrustedLongSecondaryFragmentFallbackTotalHealedGapDrawingUnits = 8.0;
    private const double MinTrustedFilledSecondaryPairFallbackLengthDrawingUnits = 54.0;
    private const double MinTrustedFilledSecondaryPairFallbackConfidence = 0.86;
    private const double MinTrustedFilledSecondaryPairFallbackPairScore = 0.92;
    private const double MinTrustedFilledSecondaryPairFallbackOverlapRatio = 0.98;
    private const int MaxTrustedFilledSecondaryPairFallbackFaceFragmentCount = 8;
    private const int MaxTrustedFilledSecondaryPairFallbackTotalFaceFragmentCount = 12;
    private const double MinRoomSupportedShortPairFallbackWallLengthDrawingUnits = 24.0;
    private const double MaxRoomSupportedShortPairFallbackWallLengthDrawingUnits = 64.0;
    private const double MinRoomSupportedShortPairFallbackPairScore = 0.88;
    private const double MinRoomSupportedShortPairFallbackOverlapRatio = 0.95;
    private const int MaxRoomSupportedShortPairFallbackFaceFragmentCount = 12;
    private const double MinTrustedShortRecoveredRoomBoundaryLengthDrawingUnits = 12.0;
    private const double MaxTrustedShortRecoveredRoomBoundaryLengthDrawingUnits = 36.0;
    private const double MinTrustedShortRecoveredRoomBoundaryConfidence = 0.76;
    private const double MinTopologyBlockedFallbackPairScore = 0.80;
    private const double MinTopologyBlockedFallbackOverlapRatio = 0.95;
    private const int MaxTopologyBlockedFallbackFaceFragmentCount = 48;

    public static IReadOnlyList<WallGraphTopologySpan> BuildVisibleTopologySpans(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options)
    {
        var context = BuildContext(result);
        var spans = context.Spans
            .Where(span => span.PageNumber == pageNumber)
            .Where(span => IsVisibleTopologySpan(span, context, options))
            .ToArray();

        var cleanSpans = BuildCleanPlacementTopologySpans(spans, result.Openings, context, result.Walls, pageNumber);
        return options.RequirePlacementReadyStructuralWallTopologySpans
            ? cleanSpans
                .Where(span => IsStrictPlacementReadyStructuralTopologySpan(span, context))
                .ToArray()
            : cleanSpans;
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans) =>
        FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(MergeCleanTopologyRuns(spans)),
            Array.Empty<OpeningCandidate>());

    private static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var merged = MergeCleanTopologyRuns(spans);
        return FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(SplitCleanTopologyRunsAroundOpenings(merged, openings)),
            openings);
    }

    private static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlyList<OpeningCandidate> openings,
        WallTopologySpanVisibilityContext context,
        IReadOnlyList<WallSegment> walls,
        int? pageNumber)
    {
        var merged = MergeCleanTopologyRuns(spans);
        var graphCleanSpans = FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(
                SplitCleanTopologyRunsAroundOpenings(merged, openings)),
            openings);
        var sourceBackedFallbackSpans = BuildSourceBackedFallbackSpans(
            walls,
            graphCleanSpans,
            context,
            pageNumber);
        var unsafeSourceBackedFallbackWallIds = sourceBackedFallbackSpans
            .Where(IsUnsafeCleanProjectionSourceBackedFallbackSpan)
            .Select(span => span.WallId)
            .ToHashSet(StringComparer.Ordinal);
        var mergedForSecondPass = unsafeSourceBackedFallbackWallIds.Count == 0
            ? merged
            : merged
                .Where(span => !unsafeSourceBackedFallbackWallIds.Contains(span.WallId))
                .ToArray();
        var combined = sourceBackedFallbackSpans.Count == 0
            ? graphCleanSpans
            : mergedForSecondPass.Concat(sourceBackedFallbackSpans).ToArray();

        if (sourceBackedFallbackSpans.Count == 0)
        {
            return graphCleanSpans;
        }

        return FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(
                SplitCleanTopologyRunsAroundOpenings(combined, openings)),
            openings);
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var options = SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview);
        var context = BuildContext(result);
        var spans = context.Spans
            .Where(span => IsVisibleTopologySpan(span, context, options))
            .ToArray();

        return BuildCleanPlacementTopologySpans(spans, result.Openings, context, result.Walls, pageNumber: null);
    }

    internal static bool IsSourceBackedFallbackTopologySpan(WallGraphTopologySpan span) =>
        span.Id.Contains(":source-backed-fallback:", StringComparison.Ordinal);

    public static IReadOnlyList<WallGraphTopologySpan> BuildRegularizedPlacementTopologySpans(
        PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var options = SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview);
        var context = BuildContext(result);
        var spans = context.Spans
            .Where(span => IsVisibleTopologySpan(span, context, options))
            .ToArray();

        var projected = ProjectSpansToOrthogonalSourceAxes(spans);
        return FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(SplitCleanTopologyRunsAroundOpenings(projected, result.Openings)),
            result.Openings);
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildHiddenNonPlacementTopologySpans(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options)
    {
        var context = BuildContext(result);
        return context.Spans
            .Where(span => span.PageNumber == pageNumber)
            .Where(span => !IsVisibleTopologySpan(span, context, options))
            .Where(span => options.IncludeSuppressedDetailWallTopologySpans
                || !IsSuppressedDetailTopologySpan(span, context, result.Openings))
            .ToArray();
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildSuppressedDetailTopologySpans(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options)
    {
        var context = BuildContext(result);
        return context.Spans
            .Where(span => span.PageNumber == pageNumber)
            .Where(span => !IsVisibleTopologySpan(span, context, options))
            .Where(span => IsSuppressedDetailTopologySpan(span, context, result.Openings))
            .ToArray();
    }

    public static bool IsPlacementReadyStructuralSpan(
        WallGraphTopologySpan span,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> wallEvidenceAssessments)
    {
        componentByWallId.TryGetValue(span.WallId, out var component);
        wallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);
        return IsPlacementReadyStructuralSpan(component, assessment);
    }

    public static bool IsPlacementReadyStructuralSpan(
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, assessment))
        {
            return false;
        }

        if (component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        return assessment is null || assessment.PlacementReady;
    }

    private static bool IsStrictPlacementReadyStructuralTopologySpan(
        WallGraphTopologySpan span,
        WallTopologySpanVisibilityContext context)
    {
        context.ComponentByWallId.TryGetValue(span.WallId, out var component);
        context.WallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);
        var hasTrustedLongIsolatedExteriorShellWallBody =
            WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                span.SourceWall,
                component,
                assessment);
        var hasTrustedMainStructuralExteriorWallBody =
            WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(
                span.SourceWall,
                component,
                assessment);
        if (!IsPlacementReadyStructuralSpan(component, assessment)
            && !hasTrustedMainStructuralExteriorWallBody
            && !hasTrustedLongIsolatedExteriorShellWallBody)
        {
            return false;
        }

        if (context.TopologyImportBlockedWallIds.Contains(span.WallId))
        {
            return false;
        }

        if (span.SourceWall is null)
        {
            return true;
        }

        var reviewReasons = context.ReviewReasonsByWallId.TryGetValue(span.WallId, out var reasons)
            ? reasons
            : Array.Empty<string>();
        return WallPlacementReadinessEvaluator.Evaluate(
            span.SourceWall,
            context.Calibration,
            component,
            assessment,
            reviewReasons).ReadyForCoordinatePlacement;
    }

    private static bool IsVisibleTopologySpan(
        WallGraphTopologySpan span,
        WallTopologySpanVisibilityContext context,
        SvgOverlayRenderOptions options)
    {
        context.ComponentByWallId.TryGetValue(span.WallId, out var component);
        context.WallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);
        var reviewReasons = context.ReviewReasonsByWallId.TryGetValue(span.WallId, out var reasons)
            ? reasons
            : Array.Empty<string>();
        var trustedExteriorShellContinuityFragment =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellContinuityFragment(
                span.SourceWall,
                component,
                assessment);
        var trustedExteriorShellRepairSupportedWall =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                span.SourceWall,
                component,
                assessment);
        var trustedRoomBoundaryIsolatedFragment =
            WallPlacementReadinessEvaluator.IsTrustedRoomBoundaryIsolatedFragment(
                span.SourceWall,
                component,
                assessment);
        if (!trustedExteriorShellContinuityFragment
            && !trustedExteriorShellRepairSupportedWall
            && !trustedRoomBoundaryIsolatedFragment
            && !IsPlacementReadyStructuralSpan(component, assessment))
        {
            return false;
        }

        if (span.SourceWall is not null
            && !WallPlacementReadinessEvaluator.Evaluate(
                span.SourceWall,
                context.Calibration,
                component,
                assessment,
                reviewReasons).ReadyForCoordinatePlacement)
        {
            return false;
        }

        if (context.TopologyImportBlockedWallIds.Contains(span.WallId)
            && !trustedExteriorShellRepairSupportedWall)
        {
            return false;
        }

        return !IsShortDanglingTopologySpan(span, component, assessment, context.NodeDegreeById);
    }

    private static bool IsSuppressedDetailTopologySpan(
        WallGraphTopologySpan span,
        WallTopologySpanVisibilityContext context,
        IReadOnlyList<OpeningCandidate> openings)
    {
        context.ComponentByWallId.TryGetValue(span.WallId, out var component);
        context.WallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);

        if (component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return true;
        }

        if (assessment is null)
        {
            return false;
        }

        if (assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
        {
            return true;
        }

        if (IsOpeningLinkedOneEndpointFragmentSpan(span, assessment, openings))
        {
            return true;
        }

        return assessment.Category is WallEvidenceCategory.DoorOrOpeningSymbol
            or WallEvidenceCategory.SurfacePatternDetail
            or WallEvidenceCategory.DimensionOrAnnotation
            or WallEvidenceCategory.ObjectOrFixtureDetail;
    }

    private static bool IsOpeningLinkedOneEndpointFragmentSpan(
        WallGraphTopologySpan span,
        WallEvidenceWallAssessment assessment,
        IReadOnlyList<OpeningCandidate> openings)
    {
        if (assessment.Category != WallEvidenceCategory.MediumWallBody
            || assessment.Decision != WallEvidenceDecision.Review
            || !assessment.RequiresReview
            || span.SourceWall?.DetectionKind != WallDetectionKind.FragmentMerged)
        {
            return false;
        }

        var evidence = span.Evidence
            .Concat(span.SourceWall.Evidence)
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .ToArray();
        if (!ContainsEvidence(evidence, "unlayered fragment-merged wall candidate")
            || !ContainsEvidence(evidence, "only one trusted structural endpoint"))
        {
            return false;
        }

        return openings.Any(opening => OpeningReferencesWall(opening, span.WallId));
    }

    private static bool OpeningReferencesWall(OpeningCandidate opening, string wallId) =>
        string.Equals(opening.WallId, wallId, StringComparison.Ordinal)
        || opening.HostWallIds.Contains(wallId, StringComparer.Ordinal)
        || opening.AdjacentWallIds.Contains(wallId, StringComparer.Ordinal)
        || opening.Placement?.AnchorWallIds.Contains(wallId, StringComparer.Ordinal) == true
        || string.Equals(opening.Placement?.HostWallId, wallId, StringComparison.Ordinal);

    private static IReadOnlySet<string> BuildOpeningLinkedWallIdSet(
        IReadOnlyList<OpeningCandidate> openings)
    {
        if (openings.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var wallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var opening in openings)
        {
            AddOpeningWallId(wallIds, opening.WallId);
            foreach (var wallId in opening.HostWallIds)
            {
                AddOpeningWallId(wallIds, wallId);
            }

            foreach (var wallId in opening.AdjacentWallIds)
            {
                AddOpeningWallId(wallIds, wallId);
            }

            AddOpeningWallId(wallIds, opening.Placement?.HostWallId);
            if (opening.Placement is not null)
            {
                foreach (var wallId in opening.Placement.AnchorWallIds)
                {
                    AddOpeningWallId(wallIds, wallId);
                }
            }
        }

        return wallIds;
    }

    private static void AddOpeningWallId(HashSet<string> wallIds, string? wallId)
    {
        if (!string.IsNullOrWhiteSpace(wallId))
        {
            wallIds.Add(wallId);
        }
    }

    private static WallTopologySpanVisibilityContext BuildContext(PlanScanResult result) =>
        new(
            WallGraphTopologySpanBuilder.Build(result.WallGraph, result.Walls),
            BuildWallComponentLookup(result.WallGraph.Components),
            WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap),
            BuildNodeIncidentLookup(result.WallGraph.Edges),
            BuildTopologyImportBlockedWallIds(result.WallGraph.RepairCandidates),
            WallPlacementContextGuards.BuildReviewReasons(result),
            BuildRoomReferenceCounts(result.Rooms),
            result.Calibration);

    private static IReadOnlyDictionary<string, int> BuildRoomReferenceCounts(IReadOnlyList<RoomRegion> rooms)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            foreach (var wallId in room.WallIds)
            {
                if (string.IsNullOrWhiteSpace(wallId))
                {
                    continue;
                }

                counts.TryGetValue(wallId, out var existing);
                counts[wallId] = existing + 1;
            }
        }

        return counts;
    }

    private static bool IsShortDanglingTopologySpan(
        WallGraphTopologySpan span,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment,
        IReadOnlyDictionary<string, int> nodeDegreeById)
    {
        if (span.DrawingLength > MaxCleanDanglingSpanLength)
        {
            return false;
        }

        var fromDegree = nodeDegreeById.TryGetValue(span.FromNodeId, out var foundFromDegree)
            ? foundFromDegree
            : 0;
        var toDegree = nodeDegreeById.TryGetValue(span.ToNodeId, out var foundToDegree)
            ? foundToDegree
            : 0;

        if (fromDegree > 1 && toDegree > 1)
        {
            return false;
        }

        return !IsTrustedShortStructuralDanglingSpan(span, component, assessment);
    }

    private static bool IsTrustedShortStructuralDanglingSpan(
        WallGraphTopologySpan span,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (span.DrawingLength < MinTrustedShortStructuralDanglingSpanLength)
        {
            return false;
        }

        if (span.SourceWall?.DetectionKind != WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        if (component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        if (assessment is null
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject)
        {
            return false;
        }

        if (assessment.Category is WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.RecoveredWallBody)
        {
            return true;
        }

        if (assessment.Category != WallEvidenceCategory.MediumWallBody
            || component.Kind != WallGraphComponentKind.MainStructural)
        {
            return false;
        }

        if (ContainsEvidence(
                assessment.Evidence
                    .Concat(span.Evidence)
                    .Concat(component.Evidence),
                WallPlacementReadinessEvaluator.TopologySupportedFragmentedPairPromotionEvidence))
        {
            return true;
        }

        return ContainsEvidence(
            assessment.Evidence
                .Concat(span.Evidence)
                .Concat(component.Evidence),
            "promoted to placement-ready by main structural graph component");
    }

    private static bool ContainsEvidence(IEnumerable<string> evidence, string value) =>
        evidence.Any(item => item.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAnyEvidence(IEnumerable<string> evidence, params string[] values)
    {
        var evidenceItems = evidence.ToArray();
        return values.Any(value => ContainsEvidence(evidenceItems, value));
    }

    private static IReadOnlyList<string> FilterTopologyImportBlockedReviewReasons(
        IReadOnlyList<string> reviewReasons) =>
        reviewReasons
            .Where(reason => !IsTopologyImportBlockedWallGraphRepairReason(reason))
            .ToArray();

    private static IReadOnlyList<string> FilterSourceBackedFallbackReviewReasons(
        IEnumerable<string> reviewReasons) =>
        reviewReasons
            .Where(reason => !reason.Contains("no clean wall graph topology span", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool IsTopologyImportBlockedWallGraphRepairReason(string reason) =>
        reason.Contains("wall graph repair candidate", StringComparison.OrdinalIgnoreCase)
        && reason.Contains(nameof(WallGraphRepairImportImpact.TopologyImportBlocked), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<WallGraphTopologySpan> BuildSourceBackedFallbackSpans(
        IReadOnlyList<WallSegment> walls,
        IReadOnlyList<WallGraphTopologySpan> cleanSpans,
        WallTopologySpanVisibilityContext context,
        int? pageNumber)
    {
        if (walls.Count == 0)
        {
            return Array.Empty<WallGraphTopologySpan>();
        }

        var fallbackSpans = new List<WallGraphTopologySpan>();
        foreach (var wall in walls)
        {
            if (pageNumber is not null && wall.PageNumber != pageNumber.Value)
            {
                continue;
            }

            var unsafeCleanProjectionSpanCount = UnsafeCleanPlacementProjectionSpanCount(wall, cleanSpans);
            var canRecoverUnsafeCleanProjection = unsafeCleanProjectionSpanCount > 0
                && (IsTrustedExteriorSourceBackedFallbackForUnsafeCleanProjection(wall, context)
                    || IsTrustedInteriorSourceBackedFallbackForUnsafeCleanProjection(wall, context));
            var coverageRatio = CleanPlacementCoverageRatio(
                wall,
                cleanSpans,
                ignoreUnsafeCleanPlacementProjection: canRecoverUnsafeCleanProjection);
            if ((coverageRatio >= MaxSourceBackedFallbackExistingCoverageRatio && !canRecoverUnsafeCleanProjection)
                || !ShouldBuildSourceBackedFallbackSpan(wall, context))
            {
                continue;
            }

            var span = CreateSourceBackedFallbackSpan(
                wall,
                context,
                coverageRatio,
                canRecoverUnsafeCleanProjection ? unsafeCleanProjectionSpanCount : 0);
            if (span is not null)
            {
                fallbackSpans.Add(span);
            }
        }

        return fallbackSpans
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ShouldBuildSourceBackedFallbackSpan(
        WallSegment wall,
        WallTopologySpanVisibilityContext context)
    {
        context.ComponentByWallId.TryGetValue(wall.Id, out var component);
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
        var trustedUnsafeExteriorCleanProjectionFallback =
            IsTrustedExteriorSourceBackedFallbackForUnsafeCleanProjection(wall, context);
        var trustedUnsafeInteriorCleanProjectionFallback =
            IsTrustedInteriorSourceBackedFallbackForUnsafeCleanProjection(wall, context);
        var trustedExteriorShellRepairSupportedWall =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                wall,
                component,
                assessment);
        var topologyImportBlocked = context.TopologyImportBlockedWallIds.Contains(wall.Id);
        var trustedTopologyImportBlockedFallback = topologyImportBlocked
            && (trustedExteriorShellRepairSupportedWall
                || IsTrustedSourceBackedFallbackDespiteTopologyImportBlock(wall, component, assessment));
        var hasTrustedMainStructuralExteriorWallBody =
            WallPlacementReadinessEvaluator.IsTrustedMainStructuralExteriorWallBody(
                wall,
                component,
                assessment);
        var hasTrustedLongIsolatedExteriorShellWallBody =
            WallPlacementReadinessEvaluator.IsTrustedLongIsolatedExteriorShellWallBody(
                wall,
                component,
                assessment);
        var hasTrustedRoomSupportedShortPairPromotion = IsTrustedRoomSupportedShortParallelPairPromotion(
            wall,
            component,
            assessment);
        var hasTrustedSourceBackedExteriorShellClosure =
            IsTrustedSourceBackedExteriorShellClosureFallback(wall, assessment);
        var hasTrustedInferredExteriorShellFallback =
            IsTrustedInferredExteriorShellFallback(wall, component, assessment);
        var hasTrustedShortExteriorWallBody =
            IsTrustedShortExteriorSourceBackedFallbackWallBody(wall, component, assessment);
        var hasTrustedRoomBoundaryShortExteriorWallBody =
            IsTrustedRoomBoundaryShortExteriorSourceBackedFallbackWallBody(wall, component, assessment);
        var hasTrustedShortRecoveredRoomBoundary =
            IsTrustedShortRecoveredRoomBoundaryFallback(wall, component, assessment);
        var hasTrustedFilledSecondaryStructuralPair =
            IsTrustedFilledSecondaryStructuralPairFallback(wall, component, assessment);

        if ((wall.CenterLine.Length < MinSourceBackedFallbackWallLengthDrawingUnits
                && !hasTrustedRoomSupportedShortPairPromotion
                && !hasTrustedLongIsolatedExteriorShellWallBody
                && !trustedExteriorShellRepairSupportedWall
                && !hasTrustedSourceBackedExteriorShellClosure
                && !hasTrustedInferredExteriorShellFallback
                && !hasTrustedShortExteriorWallBody
                && !hasTrustedRoomBoundaryShortExteriorWallBody
                && !hasTrustedShortRecoveredRoomBoundary
                && !hasTrustedFilledSecondaryStructuralPair)
            || (wall.WallType == WallType.Unknown && !hasTrustedLongIsolatedExteriorShellWallBody)
            || (wall.FragmentEvidence?.RequiresGeometryReview == true
                && !trustedUnsafeExteriorCleanProjectionFallback
                && !trustedUnsafeInteriorCleanProjectionFallback
                && !trustedExteriorShellRepairSupportedWall)
            || ResolveDominantOrthogonalOrientation(wall.CenterLine) == PlacementRunOrientation.Unknown
            || (topologyImportBlocked && !trustedTopologyImportBlockedFallback))
        {
            return false;
        }

        var reviewReasons = context.ReviewReasonsByWallId.TryGetValue(wall.Id, out var foundReviewReasons)
            ? foundReviewReasons
            : Array.Empty<string>();
        var hasTopologySupportedFragmentedPairPromotion = IsTopologySupportedFragmentedPairPromotion(
            wall,
            component,
            assessment);
        var hasTrustedFragmentMergedPromotion = IsTrustedSourceBackedFallbackFragmentEvidence(
            wall,
            component,
            assessment);
        var hasTrustedExteriorShellContinuityFragment =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellContinuityFragment(
                wall,
                component,
                assessment);
        var hasTrustedRoomBoundaryIsolatedFragment =
            WallPlacementReadinessEvaluator.IsTrustedRoomBoundaryIsolatedFragment(
                wall,
                component,
                assessment);
        var hasTrustedRecoveredRoomBoundaryObjectLikeWall =
            WallPlacementReadinessEvaluator.IsTrustedRecoveredRoomBoundaryObjectLikeWall(
                wall,
                component,
                assessment);
        var hasTrustedInferredSharedRoomBoundary =
            IsTrustedInferredSharedRoomBoundaryFallback(
                wall,
                component,
                assessment);
        var hasTrustedTwoSidedFragmentMergedRoomBoundary =
            WallPlacementReadinessEvaluator.IsTrustedTwoSidedFragmentMergedRoomBoundary(
                wall,
                component,
                assessment);
        var hasTrustedOneEndpointNoisyMainStructuralInterior =
            WallPlacementContextGuards.IsTrustedOneEndpointNoisyMainStructuralInteriorWallBody(
                wall,
                component,
                assessment);
        var hasTrustedLongOneEndpointFragmentMergedInterior =
            WallPlacementContextGuards.IsTrustedLongOneEndpointFragmentMergedInteriorWallBody(
                wall,
                component,
                assessment);
        var hasTrustedLongSecondaryStructuralFragment =
            IsTrustedLongSecondaryStructuralFragmentFallback(
                wall,
                component,
                assessment);
        var hasTrustedGeometricRoomBoundaryPairPromotion =
            IsTrustedGeometricRoomBoundaryPairPromotion(
                wall,
                component,
                assessment);
        var hasTrustedRoomReferencedPlacementReadyPair =
            IsTrustedRoomReferencedPlacementReadyPairFallback(
                wall,
                component,
                assessment,
                context.RoomReferenceCountsByWallId);
        if ((!IsPlacementReadyStructuralSpan(component, assessment)
                && !hasTrustedExteriorShellContinuityFragment
                && !hasTrustedRoomBoundaryIsolatedFragment
                && !hasTrustedRecoveredRoomBoundaryObjectLikeWall
                && !hasTrustedInferredSharedRoomBoundary
                && !hasTrustedTwoSidedFragmentMergedRoomBoundary
                && !hasTrustedOneEndpointNoisyMainStructuralInterior
                && !hasTrustedLongOneEndpointFragmentMergedInterior
                && !hasTrustedLongSecondaryStructuralFragment
                && !hasTrustedFilledSecondaryStructuralPair
                && !hasTrustedMainStructuralExteriorWallBody
                && !hasTrustedLongIsolatedExteriorShellWallBody
                && !hasTrustedGeometricRoomBoundaryPairPromotion
                && !hasTrustedRoomReferencedPlacementReadyPair
                && !trustedUnsafeInteriorCleanProjectionFallback
                && !trustedExteriorShellRepairSupportedWall
                && !hasTrustedSourceBackedExteriorShellClosure
                && !hasTrustedInferredExteriorShellFallback
                && !hasTrustedShortExteriorWallBody
                && !hasTrustedRoomBoundaryShortExteriorWallBody
                && !hasTrustedShortRecoveredRoomBoundary)
            || assessment is null
            || (!hasTrustedTwoSidedFragmentMergedRoomBoundary
                && !hasTrustedRecoveredRoomBoundaryObjectLikeWall
                && !hasTrustedInferredSharedRoomBoundary
                && !hasTrustedLongOneEndpointFragmentMergedInterior
                && !hasTrustedLongSecondaryStructuralFragment
                && !hasTrustedFilledSecondaryStructuralPair
                && !hasTrustedMainStructuralExteriorWallBody
                && !hasTrustedLongIsolatedExteriorShellWallBody
                && !hasTrustedGeometricRoomBoundaryPairPromotion
                && !hasTrustedRoomReferencedPlacementReadyPair
                && !trustedUnsafeInteriorCleanProjectionFallback
                && !trustedExteriorShellRepairSupportedWall
                && !hasTrustedSourceBackedExteriorShellClosure
                && !hasTrustedInferredExteriorShellFallback
                && !hasTrustedShortExteriorWallBody
                && !hasTrustedRoomBoundaryShortExteriorWallBody
                && !hasTrustedShortRecoveredRoomBoundary
                && !assessment.PlacementReady)
            || (!hasTrustedTwoSidedFragmentMergedRoomBoundary
                && !hasTrustedRecoveredRoomBoundaryObjectLikeWall
                && !hasTrustedInferredSharedRoomBoundary
                && !hasTrustedLongOneEndpointFragmentMergedInterior
                && !hasTrustedLongSecondaryStructuralFragment
                && !hasTrustedFilledSecondaryStructuralPair
                && !hasTrustedMainStructuralExteriorWallBody
                && !hasTrustedLongIsolatedExteriorShellWallBody
                && !hasTrustedGeometricRoomBoundaryPairPromotion
                && !hasTrustedRoomReferencedPlacementReadyPair
                && !trustedUnsafeInteriorCleanProjectionFallback
                && !trustedExteriorShellRepairSupportedWall
                && !hasTrustedSourceBackedExteriorShellClosure
                && !hasTrustedInferredExteriorShellFallback
                && !hasTrustedShortExteriorWallBody
                && !hasTrustedRoomBoundaryShortExteriorWallBody
                && !hasTrustedShortRecoveredRoomBoundary
                && assessment.RequiresReview)
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || (assessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.RecoveredWallBody)
                && !hasTopologySupportedFragmentedPairPromotion
                && !hasTrustedFragmentMergedPromotion
                && !hasTrustedRoomSupportedShortPairPromotion
                && !hasTrustedExteriorShellContinuityFragment
                && !hasTrustedRoomBoundaryIsolatedFragment
                && !hasTrustedRecoveredRoomBoundaryObjectLikeWall
                && !hasTrustedInferredSharedRoomBoundary
                && !hasTrustedTwoSidedFragmentMergedRoomBoundary
                && !hasTrustedLongOneEndpointFragmentMergedInterior
                && !hasTrustedLongSecondaryStructuralFragment
                && !hasTrustedFilledSecondaryStructuralPair
                && !hasTrustedMainStructuralExteriorWallBody
                && !hasTrustedLongIsolatedExteriorShellWallBody
                && !hasTrustedGeometricRoomBoundaryPairPromotion
                && !hasTrustedRoomReferencedPlacementReadyPair
                && !trustedUnsafeInteriorCleanProjectionFallback
                && !trustedExteriorShellRepairSupportedWall
                && !hasTrustedSourceBackedExteriorShellClosure
                && !hasTrustedInferredExteriorShellFallback
                && !hasTrustedShortExteriorWallBody
                && !hasTrustedRoomBoundaryShortExteriorWallBody
                && !hasTrustedShortRecoveredRoomBoundary))
        {
            return false;
        }

        var placementReviewReasons = trustedTopologyImportBlockedFallback
            ? FilterTopologyImportBlockedReviewReasons(reviewReasons)
            : reviewReasons;
        placementReviewReasons = FilterSourceBackedFallbackReviewReasons(placementReviewReasons);
        if (!WallPlacementReadinessEvaluator.Evaluate(
            wall,
            context.Calibration,
            component,
            assessment,
            placementReviewReasons).ReadyForCoordinatePlacement
            && !trustedUnsafeExteriorCleanProjectionFallback
            && !hasTrustedMainStructuralExteriorWallBody
            && !hasTrustedLongIsolatedExteriorShellWallBody
            && !trustedUnsafeInteriorCleanProjectionFallback
            && !hasTrustedLongSecondaryStructuralFragment
            && !hasTrustedFilledSecondaryStructuralPair
            && !trustedExteriorShellRepairSupportedWall
            && !hasTrustedSourceBackedExteriorShellClosure
            && !hasTrustedInferredExteriorShellFallback
            && !hasTrustedShortExteriorWallBody
            && !hasTrustedRoomBoundaryShortExteriorWallBody
            && !hasTrustedShortRecoveredRoomBoundary)
        {
            return false;
        }

        return trustedUnsafeExteriorCleanProjectionFallback
            || trustedUnsafeInteriorCleanProjectionFallback
            || trustedExteriorShellRepairSupportedWall
            || HasTrustedSourceBackedFallbackPairEvidence(wall, component, assessment)
            || hasTrustedFragmentMergedPromotion
            || hasTrustedRoomSupportedShortPairPromotion
            || hasTrustedExteriorShellContinuityFragment
            || hasTrustedRoomBoundaryIsolatedFragment
            || hasTrustedRecoveredRoomBoundaryObjectLikeWall
            || hasTrustedInferredSharedRoomBoundary
            || hasTrustedTwoSidedFragmentMergedRoomBoundary
            || hasTrustedOneEndpointNoisyMainStructuralInterior
            || hasTrustedLongOneEndpointFragmentMergedInterior
            || hasTrustedLongSecondaryStructuralFragment
            || hasTrustedFilledSecondaryStructuralPair
            || hasTrustedMainStructuralExteriorWallBody
            || hasTrustedLongIsolatedExteriorShellWallBody
            || hasTrustedGeometricRoomBoundaryPairPromotion
            || hasTrustedRoomReferencedPlacementReadyPair
            || hasTrustedSourceBackedExteriorShellClosure
            || hasTrustedInferredExteriorShellFallback
            || hasTrustedShortExteriorWallBody
            || hasTrustedRoomBoundaryShortExteriorWallBody
            || hasTrustedShortRecoveredRoomBoundary;
    }

    private static bool IsTrustedInferredExteriorShellFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.SingleLine
            || wall.DrawingLength < MinSourceBackedFallbackWallLengthDrawingUnits
            || wall.SourcePrimitiveIds.Count < MinTrustedInferredExteriorShellFallbackSourcePrimitiveCount
            || assessment.Category != WallEvidenceCategory.RecoveredWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || ResolveDominantOrthogonalOrientation(wall.CenterLine) == PlacementRunOrientation.Unknown
            || component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!ContainsEvidence(evidence, "inferred exterior shell wall from indoor room boundary with outside on opposite side")
            || !ContainsEvidence(evidence, "exterior-shell inference source-line support coverage"))
        {
            return false;
        }

        var coverage = MaxEvidenceNumberAfter(evidence, "source-line support coverage ");
        if (coverage is null || coverage.Value < MinTrustedInferredExteriorShellFallbackSourceCoverage)
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "surface pattern",
            "non-structural surface",
            "surface/detail",
            "object/fixture",
            "fixture detail",
            "repeated short detail",
            "review as detail/object",
            "covered-area",
            "covered entry",
            "covered-entry",
            "overbygd",
            "canopy",
            "terrace",
            "railing",
            "stair",
            "door swing",
            "door leaf",
            "door arc",
            "dimension annotation");
    }

    private static bool IsTrustedShortExteriorSourceBackedFallbackWallBody(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || wall.WallType != WallType.Exterior
            || wall.DrawingLength < MinTrustedShortExteriorSourceBackedFallbackWallLengthDrawingUnits
            || wall.DrawingLength >= MinSourceBackedFallbackWallLengthDrawingUnits
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTrustedShortExteriorSourceBackedFallbackPairScore
            || pair.OverlapRatio < MinTrustedShortExteriorSourceBackedFallbackOverlapRatio
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        var hasSolidWallBodyEvidence =
            ContainsEvidence(evidence, "filled wall-solid primitive")
            || ContainsEvidence(evidence, "filled closed vector wall body")
            || ContainsEvidence(evidence, "strong double-edge wall body");
        var hasExteriorSupport =
            ContainsEvidence(evidence, "wall type exterior")
            || ContainsEvidence(evidence, "exterior shell")
            || ContainsEvidence(evidence, "floorplan/wall envelope")
            || ContainsEvidence(evidence, "local outer boundary")
            || ContainsEvidence(evidence, "near detected floorplan")
            || ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "outdoor/terrace room evidence");
        var hasRoomBoundarySupport =
            ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "detected room evidence on one side")
            || ContainsEvidence(evidence, "detected room evidence on both sides")
            || ContainsEvidence(evidence, "shared by room adjacency boundary");
        if (!hasSolidWallBodyEvidence || !hasExteriorSupport)
        {
            return false;
        }

        if (pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits)
        {
            if (!hasRoomBoundarySupport
                || pair.FaceSeparation < MinRoomSupportedShortExteriorFallbackFaceSeparationDrawingUnits)
            {
                return false;
            }
        }

        if (ContainsAnyEvidence(
            evidence,
            "object/fixture",
            "fixture detail",
            "repeated short detail",
            "review as detail/object",
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered entry",
            "covered-entry",
            "overbygd",
            "terrace boundary",
            "railing",
            "stair-like linework",
            "door swing",
            "door leaf"))
        {
            return false;
        }

        if (ContainsAnyEvidence(
                evidence,
                "surface pattern",
                "non-structural surface",
                "surface/detail")
            && !hasRoomBoundarySupport)
        {
            return false;
        }

        return true;
    }

    private static bool IsTrustedRoomBoundaryShortExteriorSourceBackedFallbackWallBody(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || wall.WallType != WallType.Exterior
            || wall.DrawingLength < MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackWallLengthDrawingUnits
            || wall.DrawingLength >= MinSourceBackedFallbackWallLengthDrawingUnits
            || wall.Confidence.Value < MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackConfidence
            || assessment.Confidence.Value < MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackConfidence
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTrustedRoomBoundaryShortExteriorSourceBackedFallbackPairScore
            || pair.OverlapRatio < MinTrustedShortExteriorSourceBackedFallbackOverlapRatio
            || pair.FaceSeparation < MinRoomSupportedShortExteriorFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount)
                > MaxTrustedRoomBoundaryShortExteriorSourceBackedFallbackFaceFragmentCount
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        var hasSolidWallBodyEvidence =
            ContainsEvidence(evidence, "filled wall-solid primitive")
            || ContainsEvidence(evidence, "filled closed vector wall body");
        var hasRoomBoundarySupport =
            ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "shared by room adjacency boundary");
        var hasExteriorBoundarySupport =
            ContainsEvidence(evidence, "wall type exterior")
            || ContainsEvidence(evidence, "wall type refined exterior")
            || ContainsEvidence(evidence, "floorplan/wall envelope")
            || ContainsEvidence(evidence, "local outer boundary")
            || ContainsEvidence(evidence, "outdoor/terrace room evidence")
            || ContainsEvidence(evidence, "outdoor/terrace space");
        var hasStructuralSupport =
            component?.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural
            || ContainsEvidence(evidence, "one endpoint supported by structural context")
            || ContainsEvidence(evidence, "supported endpoint");
        if (!hasSolidWallBodyEvidence
            || !hasRoomBoundarySupport
            || !hasExteriorBoundarySupport
            || !hasStructuralSupport)
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "dimension-like",
            "layer (unlayered) classified Dimension",
            "object/fixture",
            "object-like",
            "fixture detail",
            "repeated short detail",
            "review as detail/object",
            "surface pattern",
            "non-structural surface",
            "surface/detail",
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered entry",
            "covered-entry",
            "overbygd",
            "canopy",
            "terrace boundary",
            "railing",
            "stair-like linework",
            "door/opening",
            "door swing",
            "door leaf",
            "door arc");
    }

    private static bool IsTrustedShortRecoveredRoomBoundaryFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.SingleLine
            || wall.DrawingLength < MinTrustedShortRecoveredRoomBoundaryLengthDrawingUnits
            || wall.DrawingLength > MaxTrustedShortRecoveredRoomBoundaryLengthDrawingUnits
            || wall.Confidence.Value < MinTrustedShortRecoveredRoomBoundaryConfidence
            || assessment.Confidence.Value < MinTrustedShortRecoveredRoomBoundaryConfidence
            || assessment.Category != WallEvidenceCategory.RecoveredWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || ResolveDominantOrthogonalOrientation(wall.CenterLine) == PlacementRunOrientation.Unknown
            || component?.ExcludedFromStructuralTopology == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!ContainsEvidence(evidence, "recovered by wall evidence map as short supported wall segment")
            || !ContainsEvidence(evidence, "short recovery used two-ended structural support")
            || !ContainsEvidence(evidence, "structural endpoint support count 2")
            || !ContainsEvidence(evidence, "room-confirmed wall body promoted to placement-ready")
            || !ContainsEvidence(evidence, "detected room evidence on both sides")
            || !ContainsEvidence(evidence, "two-sided room evidence True"))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "layer (unlayered) classified Dimension",
            "layer evidence: contains dimension-like text",
            "dimension-like",
            "classified Dimension",
            "dimension annotation",
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
            "repeated short detail",
            "door/opening",
            "door swing",
            "door leaf",
            "door arc",
            "opening detail",
            "stair",
            "not trusted",
            "without shell support",
            "alone is not trusted");
    }

    private static bool IsTrustedSourceBackedExteriorShellClosureFallback(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || wall.WallType != WallType.Exterior
            || wall.DrawingLength < MinLongSourceBackedFallbackWallLengthDrawingUnits
            || wall.SourcePrimitiveIds.Count == 0
            || assessment.Category != WallEvidenceCategory.RecoveredWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || ResolveDominantOrthogonalOrientation(wall.CenterLine) == PlacementRunOrientation.Unknown)
        {
            return false;
        }

        return ContainsEvidence(
            wall.Evidence
                .Concat(assessment.Evidence)
                .Concat(assessment.ScoreBreakdown.PositiveEvidence),
            "source-backed exterior shell closure");
    }

    private static bool IsTrustedExteriorSourceBackedFallbackForUnsafeCleanProjection(
        WallSegment wall,
        WallTopologySpanVisibilityContext context)
    {
        context.ComponentByWallId.TryGetValue(wall.Id, out var component);
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
        if (assessment is null
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || wall.WallType != WallType.Exterior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTrustedFilledExteriorUnsafeCleanProjectionPairScore
            || pair.OverlapRatio < 0.88
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
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

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(component.Evidence)
            .ToArray();
        var hasFilledWallBody =
            ContainsEvidence(evidence, "filled wall-solid primitive")
            && ContainsEvidence(evidence, "filled closed vector wall body");
        var hasRoomBoundarySupport =
            ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "detected room evidence on both sides")
            || ContainsEvidence(evidence, "shared by room adjacency boundary");
        var hasContinuitySupportedSyncedWallBody =
            hasFilledWallBody
            && hasRoomBoundarySupport
            && ContainsEvidence(evidence, "continuity-supported short paired wall body")
            && ContainsEvidence(evidence, "wall evidence geometry synchronized after wall graph topology normalization");
        if (ContainsAnyEvidence(
                evidence,
                "outdoor covered-area boundary",
                "unpaired outdoor covered-area boundary",
                "covered-area boundary",
                "covered entry",
                "covered-entry",
                "overbygd",
                "terrace boundary",
                "terrace detail",
                "railing",
                "surface pattern",
                "object/fixture",
                "fixture detail",
                "repeated short detail",
                "door swing",
                "door leaf"))
        {
            return false;
        }

        if (ContainsEvidence(evidence, "terrace")
            && !hasContinuitySupportedSyncedWallBody)
        {
            return false;
        }

        var hasExteriorSupport = component.Kind == WallGraphComponentKind.MainStructural
            || ContainsAnyEvidence(
                evidence,
                "exterior shell",
                "global envelope",
                "floorplan/wall envelope",
                "local outer boundary",
                "near detected floorplan",
                "wall type exterior");
        if (!hasExteriorSupport)
        {
            return false;
        }

        if (pair.Score >= 0.84)
        {
            return true;
        }

        var maxFaceFragmentCount = Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount);
        var totalFaceFragmentCount = pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount;
        return wall.DrawingLength >= MinTrustedFilledExteriorUnsafeCleanProjectionLengthDrawingUnits
            && assessment.Category == WallEvidenceCategory.StrongWallBody
            && pair.OverlapRatio >= MinTrustedFilledExteriorUnsafeCleanProjectionOverlapRatio
            && maxFaceFragmentCount <= MaxTrustedFilledExteriorUnsafeCleanProjectionFaceFragments
            && totalFaceFragmentCount <= MaxTrustedFilledExteriorUnsafeCleanProjectionTotalFaceFragments
            && hasFilledWallBody;
    }

    private static bool IsTrustedInteriorSourceBackedFallbackForUnsafeCleanProjection(
        WallSegment wall,
        WallTopologySpanVisibilityContext context)
    {
        context.ComponentByWallId.TryGetValue(wall.Id, out var component);
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
        if (assessment is null
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is not (WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural)
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedInteriorUnsafeCleanProjectionLengthDrawingUnits
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTrustedInteriorUnsafeCleanProjectionPairScore
            || pair.OverlapRatio < MinTrustedInteriorUnsafeCleanProjectionOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxTrustedInteriorUnsafeCleanProjectionFaceFragments
            || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > MaxTrustedInteriorUnsafeCleanProjectionTotalFaceFragments
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

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        var hasSolidWallBodyEvidence =
            ContainsEvidence(evidence, "filled wall-solid primitive")
            || ContainsEvidence(evidence, "filled closed vector wall body")
            || ContainsEvidence(evidence, "strong double-edge wall body");
        var hasRoomBoundarySupport =
            ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "detected room evidence on both sides")
            || ContainsEvidence(evidence, "shared by room adjacency boundary");
        if (!hasSolidWallBodyEvidence
            || !hasRoomBoundarySupport
            || !ContainsEvidence(evidence, "supported wall evidence inside exterior envelope"))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered entry",
            "covered-entry",
            "overbygd",
            "terrace",
            "railing",
            "surface pattern",
            "object/fixture",
            "fixture detail",
            "repeated short detail",
            "review as detail/object",
            "door swing",
            "door leaf",
            "door arc",
            "stair",
            "not trusted",
            "without shell support",
            "alone is not trusted");
    }

    private static bool IsTrustedRoomReferencedPlacementReadyPairFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment,
        IReadOnlyDictionary<string, int> roomReferenceCountsByWallId)
    {
        if (assessment is null
            || component is null
            || !roomReferenceCountsByWallId.TryGetValue(wall.Id, out var roomReferenceCount)
            || roomReferenceCount <= 0
            || component.ExcludedFromStructuralTopology
            || component.Kind is not (WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural)
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || wall.DrawingLength < 72.0
            || pair.Score < 0.80
            || pair.OverlapRatio < 0.90
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > 72
            || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 96
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

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(component.Evidence)
            .ToArray();
        var hasRoomBoundaryEvidence =
            roomReferenceCount >= 2
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "shared by room adjacency boundary");
        if (!hasRoomBoundaryEvidence)
        {
            return false;
        }

        return !ContainsEvidence(evidence, "surface pattern")
            && !ContainsEvidence(evidence, "object/fixture")
            && !ContainsEvidence(evidence, "object-like")
            && !ContainsEvidence(evidence, "repeated short detail")
            && !ContainsEvidence(evidence, "review as detail/object")
            && !ContainsEvidence(evidence, "outdoor covered-area boundary")
            && !ContainsEvidence(evidence, "unpaired outdoor covered-area boundary")
            && !ContainsEvidence(evidence, "covered-area boundary")
            && !ContainsEvidence(evidence, "covered entry")
            && !ContainsEvidence(evidence, "covered-entry")
            && !ContainsEvidence(evidence, "overbygd")
            && !ContainsEvidence(evidence, "terrace")
            && !ContainsEvidence(evidence, "railing")
            && !ContainsEvidence(evidence, "stair-like linework");
    }

    private static bool IsTrustedInferredSharedRoomBoundaryFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.SingleLine
            || wall.DrawingLength < 54.0
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        if (!ContainsEvidence(evidence, "inferred interior wall from unsupported shared indoor room-boundary edge")
            || !ContainsEvidence(evidence, "shared room-boundary inference rooms"))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "outdoor",
            "terrace",
            "covered-area",
            "covered entry",
            "covered-entry",
            "overbygd",
            "canopy",
            "surface pattern",
            "object/fixture",
            "fixture detail",
            "door/opening",
            "door swing",
            "door leaf",
            "door arc",
            "railing",
            "stair",
            "non-wall",
            "dimension annotation");
    }

    private static bool IsTrustedGeometricRoomBoundaryPairPromotion(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        if (!ContainsEvidence(evidence, "geometric room-boundary paired wall promoted")
            || !ContainsEvidence(evidence, "geometric room boundary support")
            || !ContainsEvidence(evidence, "supported wall evidence inside exterior envelope")
            || !ContainsEvidence(evidence, "parallel wall-face pair"))
        {
            return false;
        }

        if (pair.Score < 0.60
            || pair.OverlapRatio < 0.90
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > 144
            || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 180)
        {
            return false;
        }

        if (ContainsAnyEvidence(
                evidence,
                "outdoor",
                "terrace",
                "covered-area",
                "covered entry",
                "covered-entry",
                "overbygd",
                "canopy",
                "railing",
                "surface pattern",
                "object/fixture",
                "fixture detail",
                "repeated short detail",
                "door/opening",
                "door swing",
                "door leaf",
                "door arc",
                "stair",
                "not trusted",
                "without shell support",
                "alone is not trusted"))
        {
            return false;
        }

        return true;
    }

    private static bool IsTrustedRoomSupportedShortParallelPairPromotion(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || component?.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.WallType != WallType.Interior
            || wall.DrawingLength < MinRoomSupportedShortPairFallbackWallLengthDrawingUnits
            || wall.DrawingLength > MaxRoomSupportedShortPairFallbackWallLengthDrawingUnits
            || wall.PairEvidence is not { } pair
            || pair.OverlapRatio < MinRoomSupportedShortPairFallbackOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxRoomSupportedShortPairFallbackFaceFragmentCount)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        var hasExplicitOrGeometricRoomBoundary =
            ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "geometric room boundary support");
        var hasStrongTwoSidedTopologySupport =
            ContainsEvidence(evidence, "two-sided room evidence True")
            && HasTopologySupportedEndpointCount(evidence, minimumEndpointCount: 2);
        if (!ContainsEvidence(evidence, "room-confirmed wall body promoted to placement-ready")
            || !ContainsEvidence(evidence, "supported wall evidence inside exterior envelope")
            || (!hasExplicitOrGeometricRoomBoundary && !hasStrongTwoSidedTopologySupport))
        {
            return false;
        }

        var minimumPairScore = hasStrongTwoSidedTopologySupport
            ? 0.76
            : MinRoomSupportedShortPairFallbackPairScore;
        if (pair.Score < minimumPairScore)
        {
            return false;
        }

        if (ContainsAnyEvidence(
                evidence,
                "dimension-like",
                "layer (unlayered) classified Dimension")
            && (!hasExplicitOrGeometricRoomBoundary || pair.Score < 0.80))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "surface pattern",
            "object/fixture",
            "fixture detail",
            "repeated short detail",
            "outdoor",
            "covered-area",
            "covered entry",
            "covered-entry",
            "overbygd",
            "door/opening",
            "stair",
            "railing");
    }

    private static bool HasTopologySupportedEndpointCount(
        IReadOnlyList<string> evidence,
        int minimumEndpointCount)
    {
        foreach (var item in evidence)
        {
            var count = TryReadEvidenceCount(item, "topology-supported endpoints ");
            if (count.HasValue && count.Value >= minimumEndpointCount)
            {
                return true;
            }
        }

        return false;
    }

    private static int? TryReadEvidenceCount(string evidence, string marker)
    {
        var index = evidence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var end = start;
        while (end < evidence.Length && char.IsDigit(evidence[end]))
        {
            end++;
        }

        return end > start
            && int.TryParse(end == evidence.Length ? evidence[start..] : evidence[start..end], out var count)
                ? count
                : null;
    }

    private static double? MaxEvidenceNumberAfter(IReadOnlyList<string> evidence, string marker)
    {
        double? best = null;
        foreach (var item in evidence)
        {
            var value = TryReadEvidenceNumberAfter(item, marker);
            if (value is null)
            {
                continue;
            }

            best = best is null ? value.Value : Math.Max(best.Value, value.Value);
        }

        return best;
    }

    private static double? TryReadEvidenceNumberAfter(string evidence, string marker)
    {
        var index = evidence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        while (start < evidence.Length && char.IsWhiteSpace(evidence[start]))
        {
            start++;
        }

        var end = start;
        while (end < evidence.Length
               && (char.IsDigit(evidence[end])
                   || evidence[end] is '.' or ','))
        {
            end++;
        }

        if (end <= start)
        {
            return null;
        }

        var token = evidence[start..end].Replace(',', '.');
        return double.TryParse(
            token,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;
    }

    private static bool IsTrustedSourceBackedFallbackFragmentEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.WallType != WallType.Interior
            || wall.PairEvidence is not null
            || component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence)
        {
            return false;
        }

        if (wall.DrawingLength < Math.Max(72.0, wall.Thickness * 10.0))
        {
            return false;
        }

        var uniqueSourcePrimitiveCount = Math.Max(0, wall.SourcePrimitiveIds.Count - fragmentEvidence.DuplicatePrimitiveCount);
        var fragmentCount = Math.Max(fragmentEvidence.FragmentCount, uniqueSourcePrimitiveCount);
        if (fragmentCount is < 2 or > 4
            || fragmentEvidence.DuplicatePrimitiveCount > 8
            || fragmentEvidence.GapRatio > 0.001
            || fragmentEvidence.TotalHealedGap > 0.001)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        if (!ContainsEvidence(evidence, "clean fragment-merged interior room boundary promoted")
            || !ContainsEvidence(evidence, "supported wall evidence inside exterior envelope"))
        {
            return false;
        }

        return ContainsEvidence(evidence, "both endpoints supported by structural context")
            || ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support");
    }

    private static bool IsTrustedLongSecondaryStructuralFragmentFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.FragmentMerged
            || wall.DrawingLength < MinTrustedLongSecondaryFragmentFallbackLengthDrawingUnits
            || wall.Confidence.Value < MinTrustedLongSecondaryFragmentFallbackConfidence
            || assessment.Confidence.Value < MinTrustedLongSecondaryFragmentFallbackConfidence
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.FragmentEvidence is not { RequiresGeometryReview: false } fragmentEvidence)
        {
            return false;
        }

        if (fragmentEvidence.FragmentCount > MaxTrustedLongSecondaryFragmentFallbackFragmentCount
            || fragmentEvidence.GapRatio > MaxTrustedLongSecondaryFragmentFallbackGapRatio
            || fragmentEvidence.TotalHealedGap > MaxTrustedLongSecondaryFragmentFallbackTotalHealedGapDrawingUnits
            || fragmentEvidence.DuplicatePrimitiveCount > MaxTrustedLongSecondaryFragmentFallbackFragmentCount)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(fragmentEvidence.Evidence)
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();

        if (!ContainsEvidence(evidence, "supported wall evidence inside exterior envelope")
            || !ContainsEvidence(evidence, "merged collinear wall fragments")
            || !ContainsEvidence(evidence, "medium wall-body geometry"))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered entry",
            "covered-entry",
            "overbygd",
            "terrace",
            "canopy",
            "railing",
            "stair",
            "surface pattern",
            "surface/detail",
            "object/fixture",
            "fixture detail",
            "door leaf",
            "door swing",
            "opening-linked wall fragment",
            "glazing",
            "trim/detail",
            "detail linework",
            "repeated short detail");
    }

    private static bool IsTrustedFilledSecondaryStructuralPairFallback(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || component?.Kind != WallGraphComponentKind.SecondaryStructural
            || component.ExcludedFromStructuralTopology
            || wall.WallType != WallType.Interior
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.DrawingLength < MinTrustedFilledSecondaryPairFallbackLengthDrawingUnits
            || wall.Confidence.Value < MinTrustedFilledSecondaryPairFallbackConfidence
            || assessment.Confidence.Value < MinTrustedFilledSecondaryPairFallbackConfidence
            || assessment.Category != WallEvidenceCategory.StrongWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTrustedFilledSecondaryPairFallbackPairScore
            || pair.OverlapRatio < MinTrustedFilledSecondaryPairFallbackOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxTrustedFilledSecondaryPairFallbackFaceFragmentCount
            || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > MaxTrustedFilledSecondaryPairFallbackTotalFaceFragmentCount)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();

        if (!ContainsEvidence(evidence, "filled wall-solid primitive")
            || !ContainsEvidence(evidence, "filled closed vector wall body")
            || !ContainsEvidence(evidence, "parallel wall-face pair")
            || !ContainsEvidence(evidence, "supported wall evidence inside exterior envelope"))
        {
            return false;
        }

        return !ContainsAnyEvidence(
            evidence,
            "outdoor covered-area boundary",
            "unpaired outdoor covered-area boundary",
            "covered-area boundary",
            "covered entry",
            "covered-entry",
            "overbygd",
            "terrace",
            "canopy",
            "railing",
            "stair",
            "surface pattern",
            "surface/detail",
            "object/fixture",
            "fixture detail",
            "door leaf",
            "door swing",
            "opening-linked wall fragment",
            "glazing",
            "trim/detail",
            "detail linework",
            "repeated short detail",
            "dimension-like",
            "layer (unlayered) classified Dimension",
            "demoted from placement-ready");
    }

    private static bool HasTrustedSourceBackedFallbackPairEvidence(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment assessment)
    {
        var pair = wall.PairEvidence;
        if (pair is null
            || pair.OverlapRatio < MinSourceBackedFallbackOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits)
        {
            return false;
        }

        var hasTopologySupportedFragmentedPairPromotion = IsTopologySupportedFragmentedPairPromotion(
            wall,
            component,
            assessment);
        var hasTrustedExteriorShellContinuityFragment =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellContinuityFragment(
                wall,
                component,
                assessment);
        var hasTrustedNoisyOneEndpointMainStructuralInterior =
            WallPlacementContextGuards.IsTrustedOneEndpointNoisyMainStructuralInteriorWallBody(
                wall,
                component,
                assessment);
        if (!hasTrustedExteriorShellContinuityFragment
            && pair.Score < MinSourceBackedFallbackPairScore)
        {
            return false;
        }

        if (!hasTopologySupportedFragmentedPairPromotion
            && !hasTrustedExteriorShellContinuityFragment
            && pair.Score < MinSourceBackedFallbackStrictPairScore
            && pair.OverlapRatio < MinSourceBackedFallbackRelaxedScoreOverlapRatio)
        {
            return false;
        }

        var maxFaceFragmentCount = Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount);
        var fragmentLimit = hasTopologySupportedFragmentedPairPromotion
            ? MaxTopologySupportedSourceBackedFallbackFaceFragmentCount
            : hasTrustedNoisyOneEndpointMainStructuralInterior
                ? MaxTrustedNoisySourceBackedFallbackFaceFragmentCount
            : hasTrustedExteriorShellContinuityFragment
                ? int.MaxValue
            : wall.DrawingLength >= MinLongSourceBackedFallbackWallLengthDrawingUnits
            && component?.Kind == WallGraphComponentKind.MainStructural
                ? MaxLongSourceBackedFallbackFaceFragmentCount
                : MaxSourceBackedFallbackFaceFragmentCount;
        if (maxFaceFragmentCount > fragmentLimit)
        {
            return false;
        }

        var evidence = wall.Evidence.Concat(assessment.Evidence).ToArray();
        if (component?.Kind == WallGraphComponentKind.SecondaryStructural
            && wall.WallType == WallType.Interior
            && ContainsAnyEvidence(
                evidence,
                "dimension-like",
                "layer (unlayered) classified Dimension",
                "dense local detail",
                "stair-like linework",
                "demoted from placement-ready",
                "unsupported severe fragmented-face evidence"))
        {
            return false;
        }

        return ContainsEvidence(evidence, "parallel wall-face pair")
            || ContainsEvidence(evidence, "strong double-edge wall body")
            || ContainsEvidence(evidence, "pair score");
    }

    private static bool IsTopologySupportedFragmentedPairPromotion(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (assessment is null
            || assessment.Category != WallEvidenceCategory.MediumWallBody
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || component?.Kind != WallGraphComponentKind.MainStructural
            || component.ExcludedFromStructuralTopology
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.WallType != WallType.Interior)
        {
            return false;
        }

        return ContainsEvidence(
            wall.Evidence
                .Concat(assessment.Evidence)
                .Concat(assessment.ScoreBreakdown.PositiveEvidence)
                .Concat(component.Evidence),
            WallPlacementReadinessEvaluator.TopologySupportedFragmentedPairPromotionEvidence);
    }

    private static WallGraphTopologySpan? CreateSourceBackedFallbackSpan(
        WallSegment wall,
        WallTopologySpanVisibilityContext context,
        double existingCoverageRatio,
        int unsafeCleanProjectionSpanCount)
    {
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
        context.ComponentByWallId.TryGetValue(wall.Id, out var component);
        var trustedTwoSidedFragmentMergedRoomBoundary =
            WallPlacementReadinessEvaluator.IsTrustedTwoSidedFragmentMergedRoomBoundary(
                wall,
                component,
                assessment);
        var trustedOneEndpointNoisyMainStructuralInterior =
            WallPlacementContextGuards.IsTrustedOneEndpointNoisyMainStructuralInteriorWallBody(
                wall,
                component,
                assessment);
        var trustedLongOneEndpointFragmentMergedInterior =
            WallPlacementContextGuards.IsTrustedLongOneEndpointFragmentMergedInteriorWallBody(
                wall,
                component,
                assessment);
        var trustedLongSecondaryStructuralFragment =
            IsTrustedLongSecondaryStructuralFragmentFallback(
                wall,
                component,
                assessment);
        var trustedFilledSecondaryStructuralPair =
            IsTrustedFilledSecondaryStructuralPairFallback(
                wall,
                component,
                assessment);
        var trustedUnsafeInteriorCleanProjectionFallback =
            IsTrustedInteriorSourceBackedFallbackForUnsafeCleanProjection(wall, context);
        var trustedInferredSharedRoomBoundary =
            IsTrustedInferredSharedRoomBoundaryFallback(
                wall,
                component,
                assessment);
        var trustedRoomSupportedShortParallelPairPromotion = IsTrustedRoomSupportedShortParallelPairPromotion(
            wall,
            component,
            assessment);
        var trustedRoomReferencedPlacementReadyPair =
            IsTrustedRoomReferencedPlacementReadyPairFallback(
                wall,
                component,
                assessment,
                context.RoomReferenceCountsByWallId);
        var trustedExteriorShellRepairSupportedWall =
            WallPlacementReadinessEvaluator.IsTrustedExteriorShellRepairSupportedWall(
                wall,
                component,
                assessment);
        var trustedSourceBackedExteriorShellClosure =
            IsTrustedSourceBackedExteriorShellClosureFallback(wall, assessment);
        var trustedShortExteriorWallBody =
            IsTrustedShortExteriorSourceBackedFallbackWallBody(wall, component, assessment);
        var trustedRoomBoundaryShortExteriorWallBody =
            IsTrustedRoomBoundaryShortExteriorSourceBackedFallbackWallBody(wall, component, assessment);
        var trustedShortRecoveredRoomBoundary =
            IsTrustedShortRecoveredRoomBoundaryFallback(wall, component, assessment);
        var placementAxis = WallBodyFootprintBuilder.BuildPlacementAxis(wall, 0, 1);
        var centerLine = placementAxis.CenterLine;
        if (centerLine.Length <= 0.001)
        {
            return null;
        }

        var thickness = Math.Max(wall.Thickness, wall.PairEvidence?.FaceSeparation ?? wall.Thickness);
        var bounds = centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
        var confidence = assessment is null
            ? wall.Confidence
            : new Confidence(Math.Min(wall.Confidence.Value, assessment.Confidence.Value));
        var evidence = new List<string>
        {
            "source-backed clean placement fallback: wall graph did not provide enough clean topology coverage",
            $"source-backed fallback previous clean coverage {existingCoverageRatio:0.###}"
        };
        if (wall.DetectionKind == WallDetectionKind.FragmentMerged)
        {
            evidence.Add(trustedTwoSidedFragmentMergedRoomBoundary
                ? "source-backed fallback accepted because trusted two-sided fragment-merged room boundary evidence is importable"
                : trustedLongOneEndpointFragmentMergedInterior
                    ? "source-backed fallback accepted because long one-end fragment-merged interior wall body is clean, structural, and coordinate-safe"
                    : trustedLongSecondaryStructuralFragment
                    ? "source-backed fallback accepted because long secondary structural fragment is inside the exterior envelope and has clean healed geometry"
                    : trustedExteriorShellRepairSupportedWall
                    ? "source-backed fallback accepted because global exterior-shell repair confirmed the fragmented exterior wall run"
                    : "source-backed fallback accepted because clean promoted fragment wall-body evidence is placement-ready");
        }
        else if (trustedExteriorShellRepairSupportedWall)
        {
            evidence.Add("source-backed fallback accepted because global exterior-shell repair confirmed the exterior wall run");
        }
        else if (trustedSourceBackedExteriorShellClosure)
        {
            evidence.Add("source-backed fallback accepted because source-backed exterior shell closure is placement-ready");
        }
        else if (IsTrustedInferredExteriorShellFallback(wall, component, assessment))
        {
            evidence.Add("source-backed fallback accepted because inferred exterior shell has strong room-boundary and source-line support");
        }
        else if (trustedShortExteriorWallBody)
        {
            evidence.Add("source-backed fallback accepted because short exterior wall-solid body is placement-ready and has trusted exterior boundary support");
        }
        else if (trustedRoomBoundaryShortExteriorWallBody)
        {
            evidence.Add("source-backed fallback accepted because short exterior wall-solid body has trusted geometric room-boundary support");
        }
        else if (trustedFilledSecondaryStructuralPair)
        {
            evidence.Add("source-backed fallback accepted because filled secondary structural wall body is strong, placement-ready, and inside the exterior envelope");
        }
        else if (trustedInferredSharedRoomBoundary)
        {
            evidence.Add("source-backed fallback accepted because shared indoor room-boundary inference is placement-ready");
        }
        else if (trustedShortRecoveredRoomBoundary)
        {
            evidence.Add("source-backed fallback accepted because short recovered wall has two-ended structural support and two-sided room evidence");
        }
        else
        {
            evidence.Add(trustedRoomSupportedShortParallelPairPromotion
                ? "source-backed fallback accepted because high-score short paired wall body has explicit room-boundary support"
                : trustedRoomReferencedPlacementReadyPair
                    ? "source-backed fallback accepted because placement-ready paired wall is explicitly referenced by detected room boundaries"
                    : trustedOneEndpointNoisyMainStructuralInterior
                    ? "source-backed fallback accepted because noisy one-end main structural wall body has strong paired-face geometry"
                    : "source-backed fallback accepted only because paired wall-face evidence is placement-ready");
        }

        if (context.TopologyImportBlockedWallIds.Contains(wall.Id))
        {
            evidence.Add("source-backed fallback accepted despite blocked graph repair because source wall-body geometry is independently coordinate-safe");
        }

        if (unsafeCleanProjectionSpanCount > 0)
        {
            evidence.Add(
                (trustedUnsafeInteriorCleanProjectionFallback
                    ? "source-backed fallback accepted because existing clean topology projected away from trusted interior room boundary; "
                    : "source-backed fallback accepted because existing clean topology projected away from trusted exterior shell; ")
                + $"unsafe clean span count {unsafeCleanProjectionSpanCount}");
        }

        if (placementAxis.UsesPairedFaceEvidence)
        {
            evidence.Add($"source-backed fallback centered between paired wall faces using {placementAxis.GeometrySource}");
        }

        if (wall.PairEvidence is { } pair)
        {
            evidence.Add(
                $"source-backed fallback pair score {pair.Score:0.###}, overlap {pair.OverlapRatio:0.###}, face separation {pair.FaceSeparation:0.###} drawing units");
            evidence.Add(
                $"source-backed fallback face fragments {pair.FirstFaceFragmentCount} and {pair.SecondFaceFragmentCount}");
        }
        else if (wall.FragmentEvidence is { } fragment)
        {
            evidence.Add(
                $"source-backed fallback fragment wall body {fragment.FragmentCount} fragment(s), healed gap ratio {fragment.GapRatio:0.###}");
        }

        evidence.AddRange(wall.Evidence);
        evidence.AddRange(assessment?.Evidence ?? Array.Empty<string>());

        return new WallGraphTopologySpan(
            $"{wall.Id}:source-backed-fallback:1",
            wall.PageNumber,
            wall.Id,
            $"{wall.Id}:source-backed-fallback:start",
            $"{wall.Id}:source-backed-fallback:end",
            centerLine,
            bounds,
            centerLine.Length,
            0,
            wall.CenterLine.Length,
            wall.CenterLine.Length,
            0,
            1,
            0.5,
            wall.CenterLine.DistanceToPoint(centerLine.Start),
            wall.CenterLine.DistanceToPoint(centerLine.End),
            thickness,
            confidence,
            wall.SourcePrimitiveIds,
            Array.Empty<string>(),
            evidence
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            wall);
    }

    private static double CleanPlacementCoverageRatio(
        WallSegment wall,
        IReadOnlyList<WallGraphTopologySpan> cleanSpans,
        bool ignoreUnsafeCleanPlacementProjection = false)
    {
        if (wall.CenterLine.Length <= 0.001)
        {
            return 1;
        }

        var intervals = cleanSpans
            .Where(span => span.WallId == wall.Id)
            .Where(span => !ignoreUnsafeCleanPlacementProjection
                || !IsUnsafeCleanPlacementProjection(wall, span))
            .Select(span => CleanSpanIntervalForWall(span, wall))
            .Where(interval => interval.End - interval.Start > 0.001)
            .OrderBy(interval => interval.Start)
            .ToArray();
        if (intervals.Length == 0)
        {
            return 0;
        }

        var covered = 0.0;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        for (var index = 1; index < intervals.Length; index++)
        {
            var next = intervals[index];
            if (next.Start <= currentEnd + 0.001)
            {
                currentEnd = Math.Max(currentEnd, next.End);
                continue;
            }

            covered += currentEnd - currentStart;
            currentStart = next.Start;
            currentEnd = next.End;
        }

        covered += currentEnd - currentStart;
        return Math.Clamp(covered, 0, 1);
    }

    private static int UnsafeCleanPlacementProjectionSpanCount(
        WallSegment wall,
        IReadOnlyList<WallGraphTopologySpan> cleanSpans) =>
        cleanSpans.Count(span => span.WallId == wall.Id && IsUnsafeCleanPlacementProjection(wall, span));

    private static bool IsUnsafeCleanProjectionSourceBackedFallbackSpan(WallGraphTopologySpan span) =>
        IsSourceBackedFallbackSpan(span)
        && ContainsEvidence(span.Evidence, "existing clean topology projected away from trusted");

    private static bool IsUnsafeCleanPlacementProjection(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var limit = CleanTopologyProjectionDriftLimit(wall, span);
        var maxProjectionDistance = MaxNullable(
            span.SourceWallStartProjectionDistanceDrawingUnits,
            span.SourceWallEndProjectionDistanceDrawingUnits);
        if (maxProjectionDistance is > 0 && maxProjectionDistance.Value > limit)
        {
            return true;
        }

        if (span.SourceWallProjectedLengthDrawingUnits is not { } sourceProjectedLength
            || sourceProjectedLength <= 0.001)
        {
            return false;
        }

        var overrun = Math.Max(0, span.DrawingLength - sourceProjectedLength);
        return overrun > limit
            && overrun / sourceProjectedLength >= MinUnsafeCleanTopologyLengthOverrunRatio;
    }

    private static double CleanTopologyProjectionDriftLimit(
        WallSegment wall,
        WallGraphTopologySpan span)
    {
        var thickness = Math.Max(Math.Max(wall.Thickness, span.Thickness), 1.0);
        return Math.Max(
            MinUnsafeCleanTopologyProjectionDriftDrawingUnits,
            thickness * MaxUnsafeCleanTopologyProjectionDriftThicknessRatio);
    }

    private static CleanSpanInterval CleanSpanIntervalForWall(
        WallGraphTopologySpan span,
        WallSegment wall)
    {
        var start = span.SourceWallStartParameter ?? wall.CenterLine.ProjectParameter(span.CenterLine.Start);
        var end = span.SourceWallEndParameter ?? wall.CenterLine.ProjectParameter(span.CenterLine.End);
        return new CleanSpanInterval(
            Math.Clamp(Math.Min(start, end), 0, 1),
            Math.Clamp(Math.Max(start, end), 0, 1));
    }

    private static void AddCleanRunIfLongEnough(
        List<WallGraphTopologySpan> spans,
        CleanRunInterval interval,
        WallSegment sourceWall,
        ref int runIndex)
    {
        if (interval.LengthDrawingUnits < MinCleanRunLengthDrawingUnits)
        {
            return;
        }

        spans.Add(interval.ToSpan(sourceWall, runIndex++));
    }

    private static IReadOnlyList<WallGraphTopologySpan> MergeCleanTopologyRuns(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count == 0)
        {
            return spans;
        }

        var merged = new List<WallGraphTopologySpan>();
        foreach (var group in spans.GroupBy(span => span.WallId, StringComparer.Ordinal))
        {
            var groupSpans = group.ToArray();
            var sourceWall = groupSpans.FirstOrDefault(span => span.SourceWall is not null)?.SourceWall;
            if (sourceWall is null || sourceWall.CenterLine.Length <= 0.001)
            {
                merged.AddRange(groupSpans);
                continue;
            }

            var intervals = groupSpans
                .Select(span => CleanRunInterval.From(span, sourceWall.CenterLine))
                .Where(interval => interval.LengthDrawingUnits > 0.001)
                .OrderBy(interval => interval.StartParameter)
                .ToArray();

            if (intervals.Length == 0)
            {
                continue;
            }

            if (intervals.Length == 1 && groupSpans.Length == 1)
            {
                var singleRunIndex = 1;
                AddCleanRunIfLongEnough(merged, intervals[0], sourceWall, ref singleRunIndex);
                continue;
            }

            var current = intervals[0];
            var runIndex = 1;
            for (var index = 1; index < intervals.Length; index++)
            {
                var next = intervals[index];
                var gap = Math.Max(0, next.StartParameter - current.EndParameter) * sourceWall.CenterLine.Length;
                if (gap <= CleanRunJoinGapLimit(sourceWall, current, next))
                {
                    current = current.Merge(next, gap);
                    continue;
                }

                AddCleanRunIfLongEnough(merged, current, sourceWall, ref runIndex);
                current = next;
            }

            AddCleanRunIfLongEnough(merged, current, sourceWall, ref runIndex);
        }

        return merged
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static double CleanRunJoinGapLimit(
        WallSegment sourceWall,
        CleanRunInterval current,
        CleanRunInterval next)
    {
        var longerInterval = Math.Max(current.LengthDrawingUnits, next.LengthDrawingUnits);
        if (sourceWall.WallType == WallType.Interior)
        {
            return InteriorCleanRunJoinGapLimit(sourceWall, current, next, longerInterval);
        }

        if (sourceWall.WallType != WallType.Exterior)
        {
            return MaxCleanRunJoinGapDrawingUnits;
        }

        if (longerInterval < MinCollinearExteriorRunBridgeLongNeighborLengthDrawingUnits
            || sourceWall.Confidence.Value < 0.70
            || sourceWall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return MaxCleanRunJoinGapDrawingUnits;
        }

        var evidence = sourceWall.Evidence
            .Concat(current.Evidence)
            .Concat(next.Evidence)
            .ToArray();
        if (ContainsAnyEvidence(
                evidence,
                "door leaf",
                "door swing",
                "fixture detail",
                "object/fixture",
                "repeated short detail",
                "surface pattern",
                "wall-like linework near anchored opening"))
        {
            return MaxCleanRunJoinGapDrawingUnits;
        }

        return Math.Clamp(
            longerInterval * MaxCollinearExteriorRunBridgeGapToLongNeighborRatio,
            MaxCleanRunJoinGapDrawingUnits,
            MaxCollinearExteriorRunBridgeGapDrawingUnits);
    }

    private static double InteriorCleanRunJoinGapLimit(
        WallSegment sourceWall,
        CleanRunInterval current,
        CleanRunInterval next,
        double longerInterval)
    {
        if (longerInterval < MinCollinearInteriorSourceRunBridgeLongNeighborLengthDrawingUnits
            || sourceWall.Confidence.Value < 0.70
            || sourceWall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return MaxCleanRunJoinGapDrawingUnits;
        }

        var evidence = sourceWall.Evidence
            .Concat(current.Evidence)
            .Concat(next.Evidence)
            .ToArray();
        if (ContainsAnyEvidence(
                evidence,
                "door leaf",
                "door swing",
                "door arc",
                "fixture detail",
                "object/fixture",
                "repeated short detail",
                "surface pattern",
                "stair",
                "railing",
                "witness/extension",
                "non-wall"))
        {
            return MaxCleanRunJoinGapDrawingUnits;
        }

        return Math.Clamp(
            longerInterval * MaxCollinearInteriorSourceRunBridgeGapToLongNeighborRatio,
            MaxCleanRunJoinGapDrawingUnits,
            MaxCollinearInteriorSourceRunBridgeGapDrawingUnits);
    }

    private static IReadOnlyList<WallGraphTopologySpan> RegularizeCleanPlacementRuns(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var replacements = new Dictionary<string, WallGraphTopologySpan>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .Where(span => !UsesPairedPlacementAxis(span))
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                span.SourceWall?.WallType ?? WallType.Unknown,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var axisTolerance = PlacementRegularizationTolerance(group);
            var ordered = group
                .OrderBy(AxisCoordinate)
                .ThenBy(span => AxisMin(span.CenterLine))
                .ToArray();
            var clusters = new List<List<WallGraphTopologySpan>>();
            foreach (var span in ordered)
            {
                var current = clusters.Count == 0 ? null : clusters[^1];
                if (current is null
                    || Math.Abs(AxisCoordinate(span) - WeightedAxisCoordinate(current)) > axisTolerance)
                {
                    clusters.Add([span]);
                    continue;
                }

                current.Add(span);
            }

            foreach (var cluster in clusters)
            {
                var totalLength = cluster.Sum(span => span.DrawingLength);
                if (cluster.Count < 2
                    || totalLength < MinPlacementRegularizationClusterLengthDrawingUnits)
                {
                    continue;
                }

                var targetCoordinate = WeightedAxisCoordinate(cluster);
                foreach (var span in cluster)
                {
                    var shift = Math.Abs(AxisCoordinate(span) - targetCoordinate);
                    if (shift <= 0.001 || shift > axisTolerance)
                    {
                        continue;
                    }

                    replacements[span.Id] = RegularizePlacementSpan(span, targetCoordinate, shift);
                }
            }
        }

        if (replacements.Count == 0)
        {
            return spans;
        }

        return spans
            .Select(span => replacements.TryGetValue(span.Id, out var replacement) ? replacement : span)
            .ToArray();
    }

    private static IReadOnlyList<WallGraphTopologySpan> FinalizeCleanPlacementSpans(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var openingLinkedWallIds = BuildOpeningLinkedWallIdSet(openings);
        var canonical = CanonicalizeExteriorParallelFaceSpans(spans);
        var dominantAxisSnapped = SnapExteriorFragmentsToDominantPlacementAxis(canonical);
        var overlapped = MergeOverlappingCollinearPlacementSpans(dominantAxisSnapped);
        var interiorBridged = BridgeCollinearInteriorPlacementRunGaps(overlapped, openingLinkedWallIds);
        var bridged = BridgeCollinearExteriorPlacementRunGaps(interiorBridged);
        var endpointSnapped = SnapCleanPlacementSpanEndpointsToNearbyOrthogonalSpans(bridged);
        var resnapped = SnapExteriorFragmentsToDominantPlacementAxis(endpointSnapped);
        var interiorRebridged = BridgeCollinearInteriorPlacementRunGaps(resnapped, openingLinkedWallIds);
        var rebridged = BridgeCollinearExteriorPlacementRunGaps(interiorRebridged);
        return SuppressContainedDuplicatePlacementSpans(MergeOverlappingCollinearPlacementSpans(rebridged));
    }

    private static IReadOnlyList<WallGraphTopologySpan> SnapExteriorFragmentsToDominantPlacementAxis(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var replacements = new Dictionary<string, WallGraphTopologySpan>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .Where(span => span.SourceWall?.WallType == WallType.Exterior)
            .Where(span => !IsSourceBackedFallbackSpan(span))
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                WallType.Exterior,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var candidates = group
                .OrderBy(span => span.DrawingLength)
                .ThenBy(span => span.Id, StringComparer.Ordinal)
                .ToArray();
            foreach (var candidate in candidates)
            {
                if (!IsDominantExteriorAxisSnapCandidate(candidate)
                    || !TryFindDominantExteriorAxisHost(candidate, candidates, out var host))
                {
                    continue;
                }

                replacements[candidate.Id] = SnapExteriorFragmentToDominantAxis(candidate, host);
            }
        }

        if (replacements.Count == 0)
        {
            return spans;
        }

        return spans
            .Select(span => replacements.TryGetValue(span.Id, out var replacement) ? replacement : span)
            .ToArray();
    }

    private static bool IsDominantExteriorAxisSnapCandidate(WallGraphTopologySpan span)
    {
        if (span.DrawingLength < MinCleanRunLengthDrawingUnits
            || span.DrawingLength > MaxDominantExteriorFragmentLengthDrawingUnits)
        {
            return false;
        }

        return !HasDominantExteriorAxisSnapBlockedEvidence(span);
    }

    private static bool TryFindDominantExteriorAxisHost(
        WallGraphTopologySpan candidate,
        IReadOnlyList<WallGraphTopologySpan> spans,
        out DominantExteriorAxisSnapHost host)
    {
        host = default;
        DominantExteriorAxisSnapHost? best = null;
        foreach (var current in spans)
        {
            if (current.Id == candidate.Id
                || current.SourceWall?.WallType != WallType.Exterior
                || IsSourceBackedFallbackSpan(current)
                || HasDominantExteriorAxisSnapBlockedEvidence(current)
                || current.DrawingLength < MinDominantExteriorAxisHostLengthDrawingUnits
                || current.DrawingLength < candidate.DrawingLength * MinDominantExteriorAxisHostLengthRatio
                || candidate.DrawingLength > current.DrawingLength * MaxDominantExteriorFragmentLengthRatio)
            {
                continue;
            }

            var axisDistance = Math.Abs(AxisCoordinate(candidate) - AxisCoordinate(current));
            if (axisDistance <= MaxOverlappingCollinearMergeAxisDistanceDrawingUnits
                || axisDistance > MaxDominantExteriorFragmentAxisSnapDrawingUnits)
            {
                continue;
            }

            if (!HasDominantExteriorAxisIntervalSupport(candidate, current, out var intervalGap, out var overlapRatio))
            {
                continue;
            }

            var score = axisDistance + (intervalGap * 0.25) + ((1 - overlapRatio) * 2.0);
            var next = new DominantExteriorAxisSnapHost(current, axisDistance, intervalGap, overlapRatio, score);
            if (best is null
                || next.Score < best.Value.Score
                || (Math.Abs(next.Score - best.Value.Score) <= 0.001
                    && next.Span.DrawingLength > best.Value.Span.DrawingLength))
            {
                best = next;
            }
        }

        if (best is null)
        {
            return false;
        }

        host = best.Value;
        return true;
    }

    private static bool HasDominantExteriorAxisSnapBlockedEvidence(WallGraphTopologySpan span)
    {
        var evidence = (span.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(span.Evidence)
            .ToArray();
        return ContainsAnyEvidence(
            evidence,
            "covered-area",
            "covered entry",
            "covered-entry",
            "dimension",
            "door leaf",
            "door swing",
            "fixture detail",
            "object/fixture",
            "opening detail",
            "overbygd",
            "railing",
            "repeated short detail",
            "stair",
            "surface pattern",
            "terrace detail",
            "witness/extension",
            "non-wall");
    }

    private static bool HasDominantExteriorAxisIntervalSupport(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan host,
        out double intervalGap,
        out double overlapRatio)
    {
        var candidateMin = AxisMin(candidate.CenterLine);
        var candidateMax = AxisMax(candidate.CenterLine);
        var hostMin = AxisMin(host.CenterLine);
        var hostMax = AxisMax(host.CenterLine);
        var overlap = Math.Min(candidateMax, hostMax) - Math.Max(candidateMin, hostMin);
        intervalGap = IntervalGap(candidateMin, candidateMax, hostMin, hostMax);
        overlapRatio = Math.Max(0, overlap) / Math.Max(candidate.DrawingLength, 0.001);

        return intervalGap > 0.001
            && intervalGap <= MaxDominantExteriorFragmentAxisSnapGapDrawingUnits;
    }

    private static WallGraphTopologySpan SnapExteriorFragmentToDominantAxis(
        WallGraphTopologySpan candidate,
        DominantExteriorAxisSnapHost host)
    {
        var orientation = ResolveAxisOrientation(candidate.CenterLine);
        var targetCoordinate = AxisCoordinate(host.Span);
        var line = orientation == PlacementRunOrientation.Horizontal
            ? new PlanLineSegment(
                new PlanPoint(candidate.CenterLine.Start.X, targetCoordinate),
                new PlanPoint(candidate.CenterLine.End.X, targetCoordinate))
            : new PlanLineSegment(
                new PlanPoint(targetCoordinate, candidate.CenterLine.Start.Y),
                new PlanPoint(targetCoordinate, candidate.CenterLine.End.Y));
        var evidence =
            "clean placement dominant exterior axis snap: aligned short exterior fragment to dominant exterior span "
            + $"{host.Span.Id}; axis shift {host.AxisDistance:0.###}, interval gap "
            + $"{host.IntervalGap:0.###}, overlap ratio {host.OverlapRatio:0.###}";

        return RebuildPlacementSpanLine(candidate, line, [evidence], host.AxisDistance);
    }

    private static IReadOnlyList<WallGraphTopologySpan> SnapCleanPlacementSpanEndpointsToNearbyOrthogonalSpans(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var candidates = spans
            .Where(IsAxisAlignedPlacementSpan)
            .Where(span => !IsSourceBackedFallbackSpan(span))
            .ToArray();
        if (candidates.Length <= 1)
        {
            return spans;
        }

        var replacements = new Dictionary<string, WallGraphTopologySpan>(StringComparer.Ordinal);
        foreach (var span in candidates)
        {
            var orientation = ResolveAxisOrientation(span.CenterLine);
            if (orientation == PlacementRunOrientation.Unknown)
            {
                continue;
            }

            var start = span.CenterLine.Start;
            var end = span.CenterLine.End;
            var evidence = new List<string>();
            var startShift = 0.0;
            var endShift = 0.0;
            if (TrySnapEndpointToOrthogonalSpan(
                    span,
                    span.CenterLine.Start,
                    orientation,
                    candidates,
                    out var snappedStart,
                    out var startEvidence,
                    out startShift))
            {
                start = snappedStart;
                evidence.Add(startEvidence);
            }

            if (TrySnapEndpointToOrthogonalSpan(
                    span,
                    span.CenterLine.End,
                    orientation,
                    candidates,
                    out var snappedEnd,
                    out var endEvidence,
                    out endShift))
            {
                end = snappedEnd;
                evidence.Add(endEvidence);
            }

            if (evidence.Count == 0)
            {
                continue;
            }

            var line = new PlanLineSegment(start, end);
            if (line.Length < MinCleanRunLengthDrawingUnits)
            {
                continue;
            }

            replacements[span.Id] = RebuildPlacementSpanLine(
                span,
                line,
                evidence,
                Math.Max(startShift, endShift));
        }

        if (replacements.Count == 0)
        {
            return spans;
        }

        return spans
            .Select(span => replacements.TryGetValue(span.Id, out var replacement) ? replacement : span)
            .ToArray();
    }

    private static bool TrySnapEndpointToOrthogonalSpan(
        WallGraphTopologySpan span,
        PlanPoint endpoint,
        PlacementRunOrientation orientation,
        IReadOnlyList<WallGraphTopologySpan> candidates,
        out PlanPoint snappedEndpoint,
        out string evidence,
        out double shift)
    {
        snappedEndpoint = endpoint;
        evidence = string.Empty;
        shift = 0;
        EndpointSnapCandidate? best = null;
        foreach (var target in candidates)
        {
            if (ReferenceEquals(span, target)
                || span.Id == target.Id
                || span.PageNumber != target.PageNumber
                || target.DrawingLength < MinCleanRunLengthDrawingUnits
                || IsSourceBackedFallbackSpan(target))
            {
                continue;
            }

            var targetOrientation = ResolveAxisOrientation(target.CenterLine);
            if (!IsOrthogonal(orientation, targetOrientation))
            {
                continue;
            }

            var axisDistance = orientation == PlacementRunOrientation.Horizontal
                ? Math.Abs(endpoint.X - AxisCoordinate(target))
                : Math.Abs(endpoint.Y - AxisCoordinate(target));
            if (axisDistance <= 0.001
                || axisDistance > MaxCleanEndpointSnapToOrthogonalWallDistanceDrawingUnits)
            {
                continue;
            }

            var projectedCoordinate = orientation == PlacementRunOrientation.Horizontal
                ? endpoint.Y
                : endpoint.X;
            var projectionOverrun = IntervalOverrun(
                projectedCoordinate,
                AxisMin(target.CenterLine),
                AxisMax(target.CenterLine));
            if (projectionOverrun > MaxCleanEndpointSnapProjectionOverrunDrawingUnits)
            {
                continue;
            }

            var score = axisDistance + projectionOverrun;
            if (best is null
                || score < best.Score
                || (Math.Abs(score - best.Score) <= 0.001 && target.DrawingLength > best.Target.DrawingLength))
            {
                best = new EndpointSnapCandidate(target, axisDistance, projectionOverrun, score);
            }
        }

        if (best is null)
        {
            return false;
        }

        snappedEndpoint = orientation == PlacementRunOrientation.Horizontal
            ? new PlanPoint(AxisCoordinate(best.Target), endpoint.Y)
            : new PlanPoint(endpoint.X, AxisCoordinate(best.Target));
        shift = best.AxisDistance;
        evidence =
            "clean placement endpoint snap: aligned endpoint to nearby orthogonal wall span "
            + $"{best.Target.Id}; axis shift {best.AxisDistance:0.###}, projection overrun "
            + $"{best.ProjectionOverrun:0.###} drawing units";
        return true;
    }

    private static bool IsOrthogonal(
        PlacementRunOrientation first,
        PlacementRunOrientation second) =>
        first == PlacementRunOrientation.Horizontal && second == PlacementRunOrientation.Vertical
        || first == PlacementRunOrientation.Vertical && second == PlacementRunOrientation.Horizontal;

    private static double IntervalOverrun(double value, double min, double max)
    {
        if (value < min)
        {
            return min - value;
        }

        if (value > max)
        {
            return value - max;
        }

        return 0;
    }

    private static double IntervalGap(double firstMin, double firstMax, double secondMin, double secondMax)
    {
        if (firstMax < secondMin)
        {
            return secondMin - firstMax;
        }

        if (secondMax < firstMin)
        {
            return firstMin - secondMax;
        }

        return 0;
    }

    private static WallGraphTopologySpan RebuildPlacementSpanLine(
        WallGraphTopologySpan span,
        PlanLineSegment line,
        IReadOnlyList<string> extraEvidence,
        double maxEndpointShift)
    {
        var bounds = line.Bounds.Inflate(Math.Max(span.Thickness / 2.0, 0.5));
        var evidence = span.Evidence
            .Concat(extraEvidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceWall = span.SourceWall;
        if (sourceWall is null || sourceWall.CenterLine.Length <= 0.001)
        {
            return span with
            {
                CenterLine = line,
                Bounds = bounds,
                DrawingLength = line.Length,
                SourceWallStartProjectionDistanceDrawingUnits = MaxNullable(
                    span.SourceWallStartProjectionDistanceDrawingUnits,
                    maxEndpointShift),
                SourceWallEndProjectionDistanceDrawingUnits = MaxNullable(
                    span.SourceWallEndProjectionDistanceDrawingUnits,
                    maxEndpointShift),
                Evidence = evidence
            };
        }

        var sourceLine = sourceWall.CenterLine;
        var sourceLength = sourceLine.Length;
        var startParameter = sourceLine.ProjectParameter(line.Start);
        var endParameter = sourceLine.ProjectParameter(line.End);
        var centerParameter = sourceLine.ProjectParameter(line.Midpoint);
        var startOffset = startParameter * sourceLength;
        var endOffset = endParameter * sourceLength;

        return span with
        {
            CenterLine = line,
            Bounds = bounds,
            DrawingLength = line.Length,
            SourceWallStartOffsetDrawingUnits = startOffset,
            SourceWallEndOffsetDrawingUnits = endOffset,
            SourceWallProjectedLengthDrawingUnits = Math.Abs(endOffset - startOffset),
            SourceWallStartParameter = startParameter,
            SourceWallEndParameter = endParameter,
            SourceWallCenterParameter = centerParameter,
            SourceWallStartProjectionDistanceDrawingUnits = sourceLine.DistanceToPoint(line.Start),
            SourceWallEndProjectionDistanceDrawingUnits = sourceLine.DistanceToPoint(line.End),
            Evidence = evidence
        };
    }

    private static IReadOnlyList<WallGraphTopologySpan> MergeOverlappingCollinearPlacementSpans(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var merged = new List<WallGraphTopologySpan>();
        var axisAlignedSpanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                span.SourceWall?.WallType ?? WallType.Unknown,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var ordered = group
                .OrderBy(AxisCoordinate)
                .ThenBy(span => AxisMin(span.CenterLine))
                .ThenByDescending(span => span.DrawingLength)
                .ThenBy(span => span.Id, StringComparer.Ordinal)
                .ToArray();
            var clusters = new List<List<WallGraphTopologySpan>>();
            foreach (var span in ordered)
            {
                axisAlignedSpanIds.Add(span.Id);
                var current = clusters.Count == 0 ? null : clusters[^1];
                if (current is null
                    || Math.Abs(AxisCoordinate(span) - WeightedAxisCoordinate(current))
                        > MaxOverlappingCollinearMergeAxisDistanceDrawingUnits)
                {
                    clusters.Add([span]);
                    continue;
                }

                current.Add(span);
            }

            foreach (var cluster in clusters)
            {
                merged.AddRange(MergeOverlappingCollinearPlacementCluster(cluster));
            }
        }

        merged.AddRange(spans.Where(span => !axisAlignedSpanIds.Contains(span.Id)));
        return merged
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<WallGraphTopologySpan> MergeOverlappingCollinearPlacementCluster(
        IReadOnlyList<WallGraphTopologySpan> cluster)
    {
        if (cluster.Count <= 1)
        {
            return cluster;
        }

        var merged = new List<WallGraphTopologySpan>();
        WallGraphTopologySpan? current = null;
        foreach (var span in cluster
            .OrderBy(span => AxisMin(span.CenterLine))
            .ThenByDescending(span => span.DrawingLength)
            .ThenBy(span => span.Id, StringComparer.Ordinal))
        {
            if (current is null)
            {
                current = span;
                continue;
            }

            if (ShouldMergeOverlappingCollinearPlacementSpans(current, span))
            {
                current = CreateMergedCollinearPlacementSpan(current, span);
                continue;
            }

            merged.Add(current);
            current = span;
        }

        if (current is not null)
        {
            merged.Add(current);
        }

        return merged;
    }

    private static IReadOnlyList<WallGraphTopologySpan> BridgeCollinearInteriorPlacementRunGaps(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlySet<string> openingLinkedWallIds)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var bridged = new List<WallGraphTopologySpan>();
        var axisAlignedSpanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .Where(span => span.SourceWall?.WallType == WallType.Interior)
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                WallType.Interior,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var ordered = group
                .OrderBy(AxisCoordinate)
                .ThenBy(span => AxisMin(span.CenterLine))
                .ThenByDescending(span => span.DrawingLength)
                .ThenBy(span => span.Id, StringComparer.Ordinal)
                .ToArray();
            var clusters = new List<List<WallGraphTopologySpan>>();
            foreach (var span in ordered)
            {
                axisAlignedSpanIds.Add(span.Id);
                var current = clusters.Count == 0 ? null : clusters[^1];
                if (current is null
                    || Math.Abs(AxisCoordinate(span) - WeightedAxisCoordinate(current))
                        > MaxCollinearInteriorRunBridgeAxisDistanceDrawingUnits)
                {
                    clusters.Add([span]);
                    continue;
                }

                current.Add(span);
            }

            foreach (var cluster in clusters)
            {
                bridged.AddRange(BridgeCollinearInteriorPlacementRunGapCluster(cluster, openingLinkedWallIds));
            }
        }

        bridged.AddRange(spans.Where(span => !axisAlignedSpanIds.Contains(span.Id)));
        return bridged
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<WallGraphTopologySpan> BridgeCollinearInteriorPlacementRunGapCluster(
        IReadOnlyList<WallGraphTopologySpan> cluster,
        IReadOnlySet<string> openingLinkedWallIds)
    {
        if (cluster.Count <= 1)
        {
            return cluster;
        }

        var bridged = new List<WallGraphTopologySpan>();
        WallGraphTopologySpan? current = null;
        foreach (var span in cluster
            .OrderBy(span => AxisMin(span.CenterLine))
            .ThenByDescending(span => span.DrawingLength)
            .ThenBy(span => span.Id, StringComparer.Ordinal))
        {
            if (current is null)
            {
                current = span;
                continue;
            }

            if (ShouldBridgeCollinearInteriorPlacementRunGap(current, span, openingLinkedWallIds, out var gap))
            {
                current = CreateBridgedCollinearInteriorPlacementSpan(current, span, gap);
                continue;
            }

            bridged.Add(current);
            current = span;
        }

        if (current is not null)
        {
            bridged.Add(current);
        }

        return bridged;
    }

    private static bool IsOpeningLinkedInteriorBridgeSpan(
        WallGraphTopologySpan span,
        IReadOnlySet<string> openingLinkedWallIds)
    {
        if (openingLinkedWallIds.Count == 0)
        {
            return false;
        }

        return openingLinkedWallIds.Contains(span.WallId)
            || (span.SourceWall is not null && openingLinkedWallIds.Contains(span.SourceWall.Id));
    }

    private static bool ShouldBridgeCollinearInteriorPlacementRunGap(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second,
        IReadOnlySet<string> openingLinkedWallIds,
        out double gap)
    {
        gap = AxisMin(second.CenterLine) - AxisMax(first.CenterLine);
        if (first.WallId == second.WallId
            || first.SourceWall?.WallType != WallType.Interior
            || second.SourceWall?.WallType != WallType.Interior
            || !IsTrustedInteriorPlacementRunBridgeSpan(first)
            || !IsTrustedInteriorPlacementRunBridgeSpan(second)
            || IsOpeningLinkedInteriorBridgeSpan(first, openingLinkedWallIds)
            || IsOpeningLinkedInteriorBridgeSpan(second, openingLinkedWallIds)
            || ResolveAxisOrientation(first.CenterLine) != ResolveAxisOrientation(second.CenterLine)
            || Math.Abs(AxisCoordinate(first) - AxisCoordinate(second))
                > MaxCollinearInteriorRunBridgeAxisDistanceDrawingUnits
            || gap <= 0.001)
        {
            return false;
        }

        var longerLength = Math.Max(first.DrawingLength, second.DrawingLength);
        if (longerLength < MinCollinearInteriorRunBridgeLongNeighborLengthDrawingUnits)
        {
            return gap <= MaxTrustedShortInteriorRunBridgeGapDrawingUnits
                && IsTrustedShortInteriorPlacementRunBridgePair(first, second);
        }

        var adaptiveGapLimit = Math.Clamp(
            longerLength * MaxCollinearInteriorRunBridgeGapToLongNeighborRatio,
            MaxCleanRunJoinGapDrawingUnits,
            MaxCollinearInteriorRunBridgeGapDrawingUnits);
        return gap <= adaptiveGapLimit;
    }

    private static bool IsTrustedShortInteriorPlacementRunBridgePair(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second)
    {
        if (first.DrawingLength < MinTrustedShortInteriorRunBridgeLengthDrawingUnits
            || second.DrawingLength < MinTrustedShortInteriorRunBridgeLengthDrawingUnits)
        {
            return false;
        }

        return HasTrustedShortInteriorRunBridgePairEvidence(first)
            && HasTrustedShortInteriorRunBridgePairEvidence(second);
    }

    private static bool HasTrustedShortInteriorRunBridgePairEvidence(WallGraphTopologySpan span) =>
        span.SourceWall?.PairEvidence is { } pair
        && pair.Score >= MinTrustedShortInteriorRunBridgePairScore
        && pair.OverlapRatio >= 0.90
        && Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) <= 24
        && pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount <= 48;

    private static bool IsTrustedInteriorPlacementRunBridgeSpan(WallGraphTopologySpan span)
    {
        if (span.SourceWall is null
            || span.SourceWall.Confidence.Value < 0.70
            || span.Confidence.Value < 0.70
            || span.SourceWall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        var evidence = span.SourceWall.Evidence
            .Concat(span.Evidence)
            .ToArray();
        if (span.SourceWall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            if (span.SourceWall.PairEvidence is not { } pair
                || pair.Score < 0.80
                || pair.OverlapRatio < 0.90
                || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > 24
                || pair.FirstFaceFragmentCount + pair.SecondFaceFragmentCount > 48)
            {
                return false;
            }
        }

        if (ContainsAnyEvidence(
                evidence,
                "covered-area",
                "covered entry",
                "covered-entry",
                "dimension",
                "door leaf",
                "door swing",
                "fixture detail",
                "glazing",
                "object/fixture",
                "opening-linked",
                "overbygd",
                "railing",
                "repeated short detail",
                "stair",
                "surface pattern",
                "surface/detail",
                "terrace",
                "trim/detail",
                "wall-like linework near anchored opening",
                "witness/extension",
                "non-wall"))
        {
            return false;
        }

        return ContainsAnyEvidence(
            evidence,
            "detected room evidence on both sides",
            "explicit room boundary support",
            "geometric room boundary support",
            "room-confirmed",
            "shared by room adjacency boundary",
            "synthetic trusted interior room-boundary bridge");
    }

    private static WallGraphTopologySpan CreateBridgedCollinearInteriorPlacementSpan(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second,
        double gap) =>
        CreateMultiSourceBridgedCollinearPlacementSpan(
            first,
            second,
            gap,
            "clean placement interior run bridge: bridged trusted collinear interior room-boundary placement spans",
            emitSingleSourceProjection: false);

    private static IReadOnlyList<WallGraphTopologySpan> BridgeCollinearExteriorPlacementRunGaps(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var bridged = new List<WallGraphTopologySpan>();
        var axisAlignedSpanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .Where(span => span.SourceWall?.WallType == WallType.Exterior)
            .Where(span => !IsSourceBackedFallbackSpan(span))
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                WallType.Exterior,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var ordered = group
                .OrderBy(AxisCoordinate)
                .ThenBy(span => AxisMin(span.CenterLine))
                .ThenByDescending(span => span.DrawingLength)
                .ThenBy(span => span.Id, StringComparer.Ordinal)
                .ToArray();
            var clusters = new List<List<WallGraphTopologySpan>>();
            foreach (var span in ordered)
            {
                axisAlignedSpanIds.Add(span.Id);
                var current = clusters.Count == 0 ? null : clusters[^1];
                if (current is null
                    || Math.Abs(AxisCoordinate(span) - WeightedAxisCoordinate(current))
                        > MaxCollinearExteriorRunBridgeAxisDistanceDrawingUnits)
                {
                    clusters.Add([span]);
                    continue;
                }

                current.Add(span);
            }

            foreach (var cluster in clusters)
            {
                bridged.AddRange(BridgeCollinearExteriorPlacementRunGapCluster(cluster));
            }
        }

        bridged.AddRange(spans.Where(span => !axisAlignedSpanIds.Contains(span.Id)));
        return bridged
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<WallGraphTopologySpan> BridgeCollinearExteriorPlacementRunGapCluster(
        IReadOnlyList<WallGraphTopologySpan> cluster)
    {
        if (cluster.Count <= 1)
        {
            return cluster;
        }

        var bridged = new List<WallGraphTopologySpan>();
        WallGraphTopologySpan? current = null;
        foreach (var span in cluster
            .OrderBy(span => AxisMin(span.CenterLine))
            .ThenByDescending(span => span.DrawingLength)
            .ThenBy(span => span.Id, StringComparer.Ordinal))
        {
            if (current is null)
            {
                current = span;
                continue;
            }

            if (ShouldBridgeCollinearExteriorPlacementRunGap(current, span, out var gap))
            {
                current = CreateBridgedCollinearExteriorPlacementSpan(current, span, gap);
                continue;
            }

            bridged.Add(current);
            current = span;
        }

        if (current is not null)
        {
            bridged.Add(current);
        }

        return bridged;
    }

    private static bool ShouldBridgeCollinearExteriorPlacementRunGap(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second,
        out double gap)
    {
        gap = AxisMin(second.CenterLine) - AxisMax(first.CenterLine);
        if (first.WallId == second.WallId
            || first.SourceWall?.WallType != WallType.Exterior
            || second.SourceWall?.WallType != WallType.Exterior
            || IsSourceBackedFallbackSpan(first)
            || IsSourceBackedFallbackSpan(second)
            || HasCollinearExteriorBridgeBlockedEvidence(first)
            || HasCollinearExteriorBridgeBlockedEvidence(second)
            || ResolveAxisOrientation(first.CenterLine) != ResolveAxisOrientation(second.CenterLine)
            || Math.Abs(AxisCoordinate(first) - AxisCoordinate(second))
                > MaxCollinearExteriorRunBridgeAxisDistanceDrawingUnits
            || gap <= 0.001)
        {
            return false;
        }

        var longerLength = Math.Max(first.DrawingLength, second.DrawingLength);
        if (longerLength < MinCollinearExteriorRunBridgeLongNeighborLengthDrawingUnits)
        {
            return false;
        }

        var adaptiveGapLimit = Math.Clamp(
            longerLength * MaxCollinearExteriorRunBridgeGapToLongNeighborRatio,
            MaxCleanRunJoinGapDrawingUnits,
            MaxCollinearExteriorRunBridgeGapDrawingUnits);
        return gap <= adaptiveGapLimit;
    }

    private static bool HasCollinearExteriorBridgeBlockedEvidence(WallGraphTopologySpan span)
    {
        var evidence = (span.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(span.Evidence)
            .ToArray();

        return ContainsAnyEvidence(
            evidence,
            "covered-area",
            "covered entry",
            "covered-entry",
            "dimension",
            "door leaf",
            "door swing",
            "fixture detail",
            "glazing",
            "object/fixture",
            "opening detail",
            "overbygd",
            "railing",
            "repeated short detail",
            "stair",
            "surface pattern",
            "surface/detail",
            "terrace detail",
            "trim/detail",
            "wall-like linework near anchored opening",
            "witness/extension",
            "non-wall");
    }

    private static WallGraphTopologySpan CreateBridgedCollinearExteriorPlacementSpan(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second,
        double gap) =>
        CreateMultiSourceBridgedCollinearPlacementSpan(
            first,
            second,
            gap,
            "clean placement exterior run bridge: bridged collinear exterior placement spans",
            emitSingleSourceProjection: true);

    private static WallGraphTopologySpan CreateMultiSourceBridgedCollinearPlacementSpan(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second,
        double gap,
        string evidencePrefix,
        bool emitSingleSourceProjection)
    {
        var orientation = ResolveAxisOrientation(first.CenterLine);
        var targetCoordinate = WeightedAxisCoordinate([first, second]);
        var start = Math.Min(AxisMin(first.CenterLine), AxisMin(second.CenterLine));
        var end = Math.Max(AxisMax(first.CenterLine), AxisMax(second.CenterLine));
        var line = orientation == PlacementRunOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, targetCoordinate), new PlanPoint(end, targetCoordinate))
            : new PlanLineSegment(new PlanPoint(targetCoordinate, start), new PlanPoint(targetCoordinate, end));
        var axisShift = Math.Max(
            Math.Abs(AxisCoordinate(first) - targetCoordinate),
            Math.Abs(AxisCoordinate(second) - targetCoordinate));
        var thickness = Math.Max(first.Thickness, second.Thickness);
        var bounds = line.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
        var evidence = first.Evidence
            .Concat(second.Evidence)
            .Append(
                $"{evidencePrefix} {first.Id} and {second.Id}; gap {gap:0.###}, "
                + $"axis shift {axisShift:0.###} drawing units");
        if (!emitSingleSourceProjection)
        {
            evidence = evidence.Append("clean placement multi-source bridge: source offsets are not emitted because span combines multiple source walls");
        }

        var evidenceArray = evidence
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return first with
        {
            Id = $"{first.Id}:bridged",
            ToNodeId = second.ToNodeId,
            CenterLine = line,
            Bounds = bounds,
            DrawingLength = line.Length,
            SourceWallStartOffsetDrawingUnits = emitSingleSourceProjection
                ? MinNullable(first.SourceWallStartOffsetDrawingUnits, second.SourceWallStartOffsetDrawingUnits)
                : null,
            SourceWallEndOffsetDrawingUnits = emitSingleSourceProjection
                ? MaxNullable(first.SourceWallEndOffsetDrawingUnits, second.SourceWallEndOffsetDrawingUnits)
                : null,
            SourceWallProjectedLengthDrawingUnits = emitSingleSourceProjection
                ? MaxNullable(first.SourceWallProjectedLengthDrawingUnits, second.SourceWallProjectedLengthDrawingUnits)
                : null,
            SourceWallStartParameter = emitSingleSourceProjection
                ? MinNullable(first.SourceWallStartParameter, second.SourceWallStartParameter)
                : null,
            SourceWallEndParameter = emitSingleSourceProjection
                ? MaxNullable(first.SourceWallEndParameter, second.SourceWallEndParameter)
                : null,
            SourceWallCenterParameter = emitSingleSourceProjection
                ? AverageNullable(
                    MinNullable(first.SourceWallStartParameter, second.SourceWallStartParameter),
                    MaxNullable(first.SourceWallEndParameter, second.SourceWallEndParameter))
                : null,
            SourceWallStartProjectionDistanceDrawingUnits = emitSingleSourceProjection
                ? MaxNullable(
                    MaxNullable(first.SourceWallStartProjectionDistanceDrawingUnits, second.SourceWallStartProjectionDistanceDrawingUnits),
                    axisShift)
                : null,
            SourceWallEndProjectionDistanceDrawingUnits = emitSingleSourceProjection
                ? MaxNullable(
                    MaxNullable(first.SourceWallEndProjectionDistanceDrawingUnits, second.SourceWallEndProjectionDistanceDrawingUnits),
                    axisShift)
                : null,
            Thickness = thickness,
            Confidence = new Confidence(Math.Min(first.Confidence.Value, second.Confidence.Value)),
            SourcePrimitiveIds = first.SourcePrimitiveIds
                .Concat(second.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceWallGraphEdgeIds = first.SourceWallGraphEdgeIds
                .Concat(second.SourceWallGraphEdgeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidenceArray
        };
    }

    private static bool ShouldMergeOverlappingCollinearPlacementSpans(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second)
    {
        if (first.WallId == second.WallId
            || IsSourceBackedFallbackSpan(first)
            || IsSourceBackedFallbackSpan(second)
            || ResolveAxisOrientation(first.CenterLine) != ResolveAxisOrientation(second.CenterLine)
            || Math.Abs(AxisCoordinate(first) - AxisCoordinate(second))
                > MaxOverlappingCollinearMergeAxisDistanceDrawingUnits)
        {
            return false;
        }

        var overlap = Math.Min(AxisMax(first.CenterLine), AxisMax(second.CenterLine))
            - Math.Max(AxisMin(first.CenterLine), AxisMin(second.CenterLine));
        if (overlap <= 0.001)
        {
            return false;
        }

        var shorterLength = Math.Max(Math.Min(first.DrawingLength, second.DrawingLength), 0.001);
        return overlap / shorterLength < MinContainedDuplicateOverlapRatio;
    }

    private static WallGraphTopologySpan CreateMergedCollinearPlacementSpan(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second)
    {
        var orientation = ResolveAxisOrientation(first.CenterLine);
        var targetCoordinate = WeightedAxisCoordinate([first, second]);
        var start = Math.Min(AxisMin(first.CenterLine), AxisMin(second.CenterLine));
        var end = Math.Max(AxisMax(first.CenterLine), AxisMax(second.CenterLine));
        var line = orientation == PlacementRunOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, targetCoordinate), new PlanPoint(end, targetCoordinate))
            : new PlanLineSegment(new PlanPoint(targetCoordinate, start), new PlanPoint(targetCoordinate, end));
        var axisShift = Math.Max(
            Math.Abs(AxisCoordinate(first) - targetCoordinate),
            Math.Abs(AxisCoordinate(second) - targetCoordinate));
        var thickness = Math.Max(first.Thickness, second.Thickness);
        var bounds = line.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
        var evidence = first.Evidence
            .Concat(second.Evidence)
            .Append(
                "clean placement overlap merge: merged collinear placement spans "
                + $"{first.Id} and {second.Id}; axis shift {axisShift:0.###} drawing units")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return first with
        {
            Id = $"{first.Id}:merged",
            ToNodeId = second.ToNodeId,
            CenterLine = line,
            Bounds = bounds,
            DrawingLength = line.Length,
            SourceWallStartOffsetDrawingUnits = MinNullable(first.SourceWallStartOffsetDrawingUnits, second.SourceWallStartOffsetDrawingUnits),
            SourceWallEndOffsetDrawingUnits = MaxNullable(first.SourceWallEndOffsetDrawingUnits, second.SourceWallEndOffsetDrawingUnits),
            SourceWallProjectedLengthDrawingUnits = MaxNullable(first.SourceWallProjectedLengthDrawingUnits, second.SourceWallProjectedLengthDrawingUnits),
            SourceWallStartParameter = MinNullable(first.SourceWallStartParameter, second.SourceWallStartParameter),
            SourceWallEndParameter = MaxNullable(first.SourceWallEndParameter, second.SourceWallEndParameter),
            SourceWallCenterParameter = AverageNullable(
                MinNullable(first.SourceWallStartParameter, second.SourceWallStartParameter),
                MaxNullable(first.SourceWallEndParameter, second.SourceWallEndParameter)),
            SourceWallStartProjectionDistanceDrawingUnits = MaxNullable(
                first.SourceWallStartProjectionDistanceDrawingUnits,
                axisShift),
            SourceWallEndProjectionDistanceDrawingUnits = MaxNullable(
                first.SourceWallEndProjectionDistanceDrawingUnits,
                axisShift),
            Thickness = thickness,
            Confidence = new Confidence(Math.Min(first.Confidence.Value, second.Confidence.Value)),
            SourcePrimitiveIds = first.SourcePrimitiveIds
                .Concat(second.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceWallGraphEdgeIds = first.SourceWallGraphEdgeIds
                .Concat(second.SourceWallGraphEdgeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence
        };
    }

    private static IReadOnlyList<WallGraphTopologySpan> CanonicalizeExteriorParallelFaceSpans(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var replacements = new Dictionary<string, WallGraphTopologySpan>(StringComparer.Ordinal);
        var suppressedSpanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(span => span.SourceWall?.WallType == WallType.Exterior)
            .Where(span => !IsSourceBackedFallbackSpan(span))
            .Where(IsAxisAlignedPlacementSpan)
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                WallType.Exterior,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var ordered = group
                .OrderByDescending(span => span.DrawingLength)
                .ThenByDescending(span => span.Confidence.Value)
                .ThenBy(span => span.Id, StringComparer.Ordinal)
                .ToArray();

            foreach (var primary in ordered)
            {
                if (suppressedSpanIds.Contains(primary.Id) || replacements.ContainsKey(primary.Id))
                {
                    continue;
                }

                var partner = ordered
                    .Where(candidate => candidate.Id != primary.Id)
                    .Where(candidate => !suppressedSpanIds.Contains(candidate.Id))
                    .Where(candidate => !replacements.ContainsKey(candidate.Id))
                    .Where(candidate => IsExteriorFacePairCandidate(primary, candidate))
                    .OrderBy(candidate => Math.Abs(AxisCoordinate(candidate) - AxisCoordinate(primary)))
                    .ThenByDescending(candidate => candidate.DrawingLength)
                    .FirstOrDefault();
                if (partner is null)
                {
                    continue;
                }

                replacements[primary.Id] = CreateCanonicalExteriorFaceSpan(primary, partner);
                suppressedSpanIds.Add(partner.Id);
            }
        }

        if (replacements.Count == 0 && suppressedSpanIds.Count == 0)
        {
            return spans;
        }

        return spans
            .Where(span => !suppressedSpanIds.Contains(span.Id))
            .Select(span => replacements.TryGetValue(span.Id, out var replacement) ? replacement : span)
            .ToArray();
    }

    private static bool IsExteriorFacePairCandidate(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second)
    {
        if (first.DrawingLength < MinExteriorFacePairSpanLengthDrawingUnits
            || second.DrawingLength < MinExteriorFacePairSpanLengthDrawingUnits
            || ResolveAxisOrientation(first.CenterLine) != ResolveAxisOrientation(second.CenterLine)
            || first.WallId == second.WallId)
        {
            return false;
        }

        var axisDistance = Math.Abs(AxisCoordinate(first) - AxisCoordinate(second));
        if (axisDistance < MinExteriorFacePairAxisDistanceDrawingUnits
            || axisDistance > MaxExteriorFacePairAxisDistanceDrawingUnits)
        {
            return false;
        }

        var overlap = Math.Min(AxisMax(first.CenterLine), AxisMax(second.CenterLine))
            - Math.Max(AxisMin(first.CenterLine), AxisMin(second.CenterLine));
        if (overlap <= 0)
        {
            return false;
        }

        var shorterLength = Math.Max(Math.Min(first.DrawingLength, second.DrawingLength), 0.001);
        var longerLength = Math.Max(first.DrawingLength, second.DrawingLength);
        if (longerLength / shorterLength > MaxExteriorFacePairLengthRatio)
        {
            return false;
        }

        var firstOverrun = ExteriorFacePairOverrun(first, second);
        var secondOverrun = ExteriorFacePairOverrun(second, first);
        if (Math.Max(firstOverrun, secondOverrun) > MaxExteriorFacePairOverrunDrawingUnits)
        {
            return false;
        }

        return overlap / shorterLength >= MinExteriorFacePairOverlapRatio;
    }

    private static double ExteriorFacePairOverrun(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan reference)
    {
        var candidateMin = AxisMin(candidate.CenterLine);
        var candidateMax = AxisMax(candidate.CenterLine);
        var referenceMin = AxisMin(reference.CenterLine);
        var referenceMax = AxisMax(reference.CenterLine);
        return Math.Max(0, referenceMin - candidateMin) + Math.Max(0, candidateMax - referenceMax);
    }

    private static WallGraphTopologySpan CreateCanonicalExteriorFaceSpan(
        WallGraphTopologySpan primary,
        WallGraphTopologySpan partner)
    {
        var orientation = ResolveAxisOrientation(primary.CenterLine);
        var targetCoordinate = (AxisCoordinate(primary) + AxisCoordinate(partner)) / 2.0;
        var start = Math.Min(AxisMin(primary.CenterLine), AxisMin(partner.CenterLine));
        var end = Math.Max(AxisMax(primary.CenterLine), AxisMax(partner.CenterLine));
        var line = orientation == PlacementRunOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, targetCoordinate), new PlanPoint(end, targetCoordinate))
            : new PlanLineSegment(new PlanPoint(targetCoordinate, start), new PlanPoint(targetCoordinate, end));
        var axisDistance = Math.Abs(AxisCoordinate(primary) - AxisCoordinate(partner));
        var shift = Math.Abs(AxisCoordinate(primary) - targetCoordinate);
        var bounds = line.Bounds.Inflate(Math.Max(Math.Max(primary.Thickness, partner.Thickness) / 2.0, 0.5));
        var evidence = primary.Evidence
            .Concat(partner.Evidence)
            .Append(
                "clean placement exterior face canonicalization: centered between close parallel exterior face spans "
                + $"{primary.WallId} and {partner.WallId}; face separation {axisDistance:0.###} drawing units")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return primary with
        {
            CenterLine = line,
            Bounds = bounds,
            DrawingLength = line.Length,
            SourceWallStartProjectionDistanceDrawingUnits = MaxNullable(
                primary.SourceWallStartProjectionDistanceDrawingUnits,
                shift),
            SourceWallEndProjectionDistanceDrawingUnits = MaxNullable(
                primary.SourceWallEndProjectionDistanceDrawingUnits,
                shift),
            Thickness = Math.Max(primary.Thickness, partner.Thickness),
            Confidence = new Confidence(Math.Min(primary.Confidence.Value, partner.Confidence.Value)),
            SourcePrimitiveIds = primary.SourcePrimitiveIds
                .Concat(partner.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            SourceWallGraphEdgeIds = primary.SourceWallGraphEdgeIds
                .Concat(partner.SourceWallGraphEdgeIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence
        };
    }

    private static IReadOnlyList<WallGraphTopologySpan> SuppressContainedDuplicatePlacementSpans(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
        {
            return spans;
        }

        var suppressedSpanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in spans
            .Where(IsAxisAlignedPlacementSpan)
            .GroupBy(span => new ContainedDuplicatePlacementKey(
                span.PageNumber,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var kept = new List<WallGraphTopologySpan>();
            foreach (var span in group
                .OrderBy(span => IsTopologyBlockedSourceBackedFallbackSpan(span) ? 1 : 0)
                .ThenByDescending(span => span.DrawingLength)
                .ThenBy(span => IsSourceBackedFallbackSpan(span) ? 1 : 0)
                .ThenByDescending(span => span.Confidence.Value)
                .ThenBy(span => span.Id, StringComparer.Ordinal))
            {
                if (IsContainedDuplicatePlacementSpan(span, kept))
                {
                    suppressedSpanIds.Add(span.Id);
                    continue;
                }

                kept.Add(span);
            }
        }

        if (suppressedSpanIds.Count == 0)
        {
            return spans;
        }

        return spans
            .Where(span => !suppressedSpanIds.Contains(span.Id))
            .ToArray();
    }

    private static bool IsContainedDuplicatePlacementSpan(
        WallGraphTopologySpan candidate,
        IReadOnlyList<WallGraphTopologySpan> keptSpans)
    {
        if (candidate.DrawingLength <= 0.001)
        {
            return true;
        }

        var candidateOrientation = ResolveAxisOrientation(candidate.CenterLine);
        if (candidateOrientation == PlacementRunOrientation.Unknown)
        {
            return false;
        }

        var candidateMin = AxisMin(candidate.CenterLine);
        var candidateMax = AxisMax(candidate.CenterLine);
        var candidateIsSourceBackedFallback = IsSourceBackedFallbackSpan(candidate);
        foreach (var kept in keptSpans)
        {
            var maxAxisDistance = ContainedDuplicateAxisDistance(candidate, kept);
            var axisDistance = Math.Abs(AxisCoordinate(candidate) - AxisCoordinate(kept));
            var couldBeTinyContainedSameType = CouldBeTinyContainedSameTypePlacementSpan(candidate, kept, axisDistance);
            if (ResolveAxisOrientation(kept.CenterLine) != candidateOrientation
                || (axisDistance > maxAxisDistance && !couldBeTinyContainedSameType))
            {
                continue;
            }

            var overlap = Math.Min(candidateMax, AxisMax(kept.CenterLine)) - Math.Max(candidateMin, AxisMin(kept.CenterLine));
            if (overlap <= 0)
            {
                continue;
            }

            var overlapRatio = overlap / Math.Max(candidate.DrawingLength, 0.001);
            var isTinyContainedSameType = IsTinyContainedSameTypePlacementSpan(candidate, kept, overlapRatio, axisDistance);
            if (IsContainedDuplicateOverlapAcceptable(candidate, kept, overlapRatio, axisDistance)
                || isTinyContainedSameType)
            {
                if (IsSyntheticExteriorShellMaskingTrustedInteriorPlacementSpan(candidate, kept))
                {
                    continue;
                }

                if (isTinyContainedSameType)
                {
                    return true;
                }

                if (candidate.SourceWall?.WallType == WallType.Exterior
                    && kept.SourceWall?.WallType == WallType.Exterior
                    && !candidateIsSourceBackedFallback
                    && !IsSourceBackedFallbackSpan(kept)
                    && !IsSameLineContainedExteriorDuplicate(candidate, kept)
                    && !IsContainedExteriorFragmentRepresentedByLongSpan(candidate, kept, overlapRatio, axisDistance)
                    && !HasComparableExteriorFaceExtent(candidate, kept))
                {
                    continue;
                }

                if (candidateIsSourceBackedFallback
                    && !IsSameSourceBackedFallbackPlacement(candidate, kept)
                    && !CanRepresentContainedSourceBackedFallback(candidate, kept, overlapRatio, axisDistance))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsSyntheticExteriorShellMaskingTrustedInteriorPlacementSpan(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept)
    {
        if (candidate.SourceWall?.WallType != WallType.Interior
            || candidate.SourceWall.DetectionKind != WallDetectionKind.ParallelLinePair
            || candidate.SourceWall.PairEvidence is not { Score: >= 0.80, OverlapRatio: >= 0.90 }
            || kept.SourceWall?.WallType != WallType.Exterior
            || candidate.DrawingLength < MinRoomSupportedShortPairFallbackWallLengthDrawingUnits
            || !IsSyntheticExteriorShellPlacementSpan(kept))
        {
            return false;
        }

        var evidence = (candidate.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(candidate.Evidence)
            .ToArray();
        return !ContainsAnyEvidence(
            evidence,
            "covered-area",
            "covered entry",
            "covered-entry",
            "door leaf",
            "door swing",
            "fixture detail",
            "object/fixture",
            "overbygd",
            "railing",
            "repeated short detail",
            "stair",
            "surface pattern",
            "terrace");
    }

    private static bool IsSyntheticExteriorShellPlacementSpan(WallGraphTopologySpan span) =>
        span.WallId.Contains("wall-exterior-shell-inferred:", StringComparison.Ordinal)
        || span.WallId.Contains("wall-exterior-shell-source-backed:", StringComparison.Ordinal)
        || span.SourceWall?.Id.Contains("wall-exterior-shell-inferred:", StringComparison.Ordinal) == true
        || span.SourceWall?.Id.Contains("wall-exterior-shell-source-backed:", StringComparison.Ordinal) == true;

    private static bool CouldBeTinyContainedSameTypePlacementSpan(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept,
        double axisDistance)
    {
        var candidateWallType = candidate.SourceWall?.WallType ?? WallType.Unknown;
        var keptWallType = kept.SourceWall?.WallType ?? WallType.Unknown;
        return candidateWallType != WallType.Unknown
            && candidateWallType == keptWallType
            && candidate.DrawingLength <= MaxTinyContainedSameTypeLengthDrawingUnits
            && candidate.DrawingLength <= kept.DrawingLength * MaxTinyContainedSameTypeLengthRatio
            && axisDistance <= MaxTinyContainedSameTypeAxisDistanceDrawingUnits;
    }

    private static bool IsTinyContainedSameTypePlacementSpan(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept,
        double overlapRatio,
        double axisDistance) =>
        CouldBeTinyContainedSameTypePlacementSpan(candidate, kept, axisDistance)
        && overlapRatio >= MinTinyContainedSameTypeOverlapRatio
        && AxisMin(candidate.CenterLine) >= AxisMin(kept.CenterLine) - MaxContainedDuplicateAxisDistanceDrawingUnits
        && AxisMax(candidate.CenterLine) <= AxisMax(kept.CenterLine) + MaxContainedDuplicateAxisDistanceDrawingUnits;

    private static bool IsContainedExteriorFragmentRepresentedByLongSpan(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept,
        double overlapRatio,
        double axisDistance)
    {
        if (axisDistance > MaxContainedExteriorFragmentAxisDistanceDrawingUnits
            || overlapRatio < MinContainedExteriorFragmentOverlapRatio
            || candidate.DrawingLength <= 0.001
            || kept.DrawingLength <= 0.001
            || candidate.DrawingLength > kept.DrawingLength * MaxContainedExteriorFragmentLengthRatio
            || (!HasContainedExteriorFragmentNoiseEvidence(candidate)
                && !IsFilledWallSolidContainedInNoisyLongExteriorRun(candidate, kept)))
        {
            return false;
        }

        var overhang = Math.Max(0, AxisMin(kept.CenterLine) - AxisMin(candidate.CenterLine))
            + Math.Max(0, AxisMax(candidate.CenterLine) - AxisMax(kept.CenterLine));
        return overhang <= NearContainedDuplicateOverhangTolerance(candidate, kept);
    }

    private static bool HasContainedExteriorFragmentNoiseEvidence(WallGraphTopologySpan span)
    {
        var evidence = (span.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(span.Evidence)
            .ToArray();
        return ContainsAnyEvidence(
            evidence,
            "duplicate or near-duplicate",
            "healed",
            "fragmented exterior",
            "repeated short detail",
            "wall-like linework near anchored opening");
    }

    private static bool IsFilledWallSolidContainedInNoisyLongExteriorRun(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept)
    {
        var candidateEvidence = (candidate.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(candidate.Evidence)
            .ToArray();
        if (!ContainsEvidence(candidateEvidence, "filled wall-solid primitive"))
        {
            return false;
        }

        var keptEvidence = (kept.SourceWall?.Evidence ?? Array.Empty<string>())
            .Concat(kept.Evidence)
            .ToArray();
        return ContainsAnyEvidence(
            keptEvidence,
            "duplicate or near-duplicate",
            "fragment geometry",
            "merged collinear wall fragments",
            "run collapsed",
            "run merged");
    }

    private static bool IsContainedDuplicateOverlapAcceptable(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept,
        double overlapRatio,
        double axisDistance)
    {
        if (axisDistance <= MaxContainedDuplicateAxisDistanceDrawingUnits
            && overlapRatio >= MinContainedDuplicateOverlapRatio)
        {
            return true;
        }

        if (overlapRatio < MinNearContainedDuplicateOverlapRatio)
        {
            return false;
        }

        var candidateMin = AxisMin(candidate.CenterLine);
        var candidateMax = AxisMax(candidate.CenterLine);
        var keptMin = AxisMin(kept.CenterLine);
        var keptMax = AxisMax(kept.CenterLine);
        var overhang = Math.Max(0, keptMin - candidateMin)
            + Math.Max(0, candidateMax - keptMax);
        return overhang <= NearContainedDuplicateOverhangTolerance(candidate, kept);
    }

    private static bool IsSameLineContainedExteriorDuplicate(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept)
    {
        if (Math.Abs(AxisCoordinate(candidate) - AxisCoordinate(kept)) > MaxContainedDuplicateAxisDistanceDrawingUnits)
        {
            return false;
        }

        var candidateMin = AxisMin(candidate.CenterLine);
        var candidateMax = AxisMax(candidate.CenterLine);
        var keptMin = AxisMin(kept.CenterLine);
        var keptMax = AxisMax(kept.CenterLine);
        var overlap = Math.Min(candidateMax, keptMax) - Math.Max(candidateMin, keptMin);
        if (overlap <= 0)
        {
            return false;
        }

        var containedRatio = overlap / Math.Max(candidate.DrawingLength, 0.001);
        return containedRatio >= 0.985
            && candidateMin >= keptMin - MaxContainedDuplicateAxisDistanceDrawingUnits
            && candidateMax <= keptMax + MaxContainedDuplicateAxisDistanceDrawingUnits;
    }

    private static bool IsSameSourceBackedFallbackPlacement(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept) =>
        string.Equals(candidate.WallId, kept.WallId, StringComparison.Ordinal)
        || HasSharedSourceReference(candidate.SourcePrimitiveIds, kept.SourcePrimitiveIds)
        || HasSharedSourceReference(candidate.SourceWallGraphEdgeIds, kept.SourceWallGraphEdgeIds);

    private static bool CanRepresentContainedSourceBackedFallback(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept,
        double overlapRatio,
        double axisDistance)
    {
        var candidateWallType = candidate.SourceWall?.WallType ?? WallType.Unknown;
        var keptWallType = kept.SourceWall?.WallType ?? WallType.Unknown;
        if (candidateWallType != keptWallType
            || candidateWallType == WallType.Unknown
            || IsTopologyBlockedSourceBackedFallbackSpan(candidate)
            || IsTopologyBlockedSourceBackedFallbackSpan(kept)
            || candidate.DrawingLength > kept.DrawingLength)
        {
            return false;
        }

        if (candidateWallType == WallType.Exterior
            && ContainsAnyEvidence(
                candidate.Evidence,
                "global exterior-shell repair confirmed",
                "trusted exterior shell continuity",
                "exterior shell repair confirmed",
                "exterior-shell repair confirmed"))
        {
            return true;
        }

        return axisDistance <= MaxContainedSourceBackedFallbackAxisDistanceDrawingUnits
            && overlapRatio >= MinContainedSourceBackedFallbackOverlapRatio
            && candidate.DrawingLength <= kept.DrawingLength * MaxContainedSourceBackedFallbackLengthRatio
            && ContainsEvidence(candidate.Evidence, "source-backed fallback accepted");
    }

    private static bool HasComparableExteriorFaceExtent(
        WallGraphTopologySpan first,
        WallGraphTopologySpan second)
    {
        var shorterLength = Math.Max(Math.Min(first.DrawingLength, second.DrawingLength), 0.001);
        var longerLength = Math.Max(first.DrawingLength, second.DrawingLength);
        if (longerLength / shorterLength > MaxExteriorFacePairLengthRatio)
        {
            return false;
        }

        return Math.Max(
            ExteriorFacePairOverrun(first, second),
            ExteriorFacePairOverrun(second, first)) <= MaxExteriorFacePairOverrunDrawingUnits;
    }

    private static bool HasSharedSourceReference(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0 || second.Count == 0)
        {
            return false;
        }

        var lookup = first
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        return second.Any(id => lookup.Contains(id));
    }

    private static bool IsSourceBackedFallbackSpan(WallGraphTopologySpan span) =>
        span.Id.Contains(":source-backed-fallback:", StringComparison.Ordinal);

    private static bool IsTopologyBlockedSourceBackedFallbackSpan(WallGraphTopologySpan span) =>
        IsSourceBackedFallbackSpan(span)
        && ContainsEvidence(span.Evidence, "despite blocked graph repair");

    private static double ContainedDuplicateAxisDistance(WallGraphTopologySpan candidate, WallGraphTopologySpan kept) =>
        candidate.SourceWall?.WallType == WallType.Exterior && kept.SourceWall?.WallType == WallType.Exterior
            ? MaxExteriorFacePairAxisDistanceDrawingUnits
            : Math.Clamp(
                Math.Max(candidate.Thickness, kept.Thickness),
                MaxContainedDuplicateAxisDistanceDrawingUnits,
                MaxNearContainedDuplicateAxisDistanceDrawingUnits);

    private static double NearContainedDuplicateOverhangTolerance(WallGraphTopologySpan candidate, WallGraphTopologySpan kept) =>
        Math.Clamp(
            Math.Max(candidate.Thickness, kept.Thickness) / 2.0,
            MaxContainedDuplicateAxisDistanceDrawingUnits,
            MaxNearContainedDuplicateOverhangDrawingUnits);

    private static IReadOnlyList<WallGraphTopologySpan> SplitCleanTopologyRunsAroundOpenings(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlyList<OpeningCandidate> openings)
    {
        if (spans.Count == 0 || openings.Count == 0)
        {
            return spans;
        }

        var split = new List<WallGraphTopologySpan>(spans.Count);
        foreach (var span in spans)
        {
            split.AddRange(SplitCleanTopologyRunAroundOpenings(span, openings));
        }

        return split.ToArray();
    }

    private static IReadOnlyList<WallGraphTopologySpan> SplitCleanTopologyRunAroundOpenings(
        WallGraphTopologySpan span,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var sourceWall = span.SourceWall;
        if (sourceWall is null || sourceWall.CenterLine.Length <= 0.001)
        {
            return [span];
        }

        var spanStart = Math.Clamp(
            Math.Min(
                span.SourceWallStartParameter ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.Start),
                span.SourceWallEndParameter ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.End)),
            0,
            1);
        var spanEnd = Math.Clamp(
            Math.Max(
                span.SourceWallStartParameter ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.Start),
                span.SourceWallEndParameter ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.End)),
            0,
            1);
        if (spanEnd - spanStart <= 0.001)
        {
            return [span];
        }

        var cutouts = BuildTopologyOpeningCutouts(sourceWall, openings)
            .Where(cutout => cutout.EndParameter > spanStart + 0.001
                && cutout.StartParameter < spanEnd - 0.001)
            .OrderBy(cutout => cutout.StartParameter)
            .ThenBy(cutout => cutout.EndParameter)
            .ToArray();
        if (cutouts.Length == 0)
        {
            return [span];
        }

        if (ShouldKeepExteriorTopologyContinuousAcrossOpeningCutouts(span, sourceWall, cutouts))
        {
            return [CreateContinuousExteriorOpeningTopologySpan(span, cutouts)];
        }

        var pieces = new List<WallGraphTopologySpan>();
        var cursor = spanStart;
        var sequence = 1;
        PlacementWallOpeningCutoutExport? previousCutout = null;
        foreach (var cutout in cutouts)
        {
            var start = Math.Max(spanStart, cutout.StartParameter);
            var end = Math.Min(spanEnd, cutout.EndParameter);
            if (end <= start + 0.001 || end <= cursor + 0.001)
            {
                continue;
            }

            AddSplitPiece(
                pieces,
                span,
                sourceWall,
                cursor,
                Math.Min(start, spanEnd),
                ref sequence,
                previousCutout,
                cutout);
            cursor = Math.Max(cursor, end);
            previousCutout = cutout;
        }

        AddSplitPiece(
            pieces,
            span,
            sourceWall,
            cursor,
            spanEnd,
            ref sequence,
            previousCutout,
            nextCutout: null);

        return pieces.Count == 0 ? Array.Empty<WallGraphTopologySpan>() : pieces.ToArray();
    }

    private static bool ShouldKeepExteriorTopologyContinuousAcrossOpeningCutouts(
        WallGraphTopologySpan span,
        WallSegment sourceWall,
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        if (sourceWall.WallType != WallType.Exterior
            || sourceWall.DrawingLength < MinContinuousExteriorOpeningTopologyLengthDrawingUnits
            || sourceWall.Confidence.Value < 0.70
            || span.Confidence.Value < 0.70
            || sourceWall.FragmentEvidence?.RequiresGeometryReview == true
            || cutouts.Count == 0)
        {
            return false;
        }

        var wallEvidence = sourceWall.Evidence
            .Concat(span.Evidence)
            .ToArray();
        if (ContainsAnyEvidence(
            wallEvidence,
            "door leaf",
            "door swing",
            "fixture detail",
            "object/fixture",
            "repeated short detail",
            "surface pattern",
            "wall-like linework near anchored opening"))
        {
            return false;
        }

        var cutoutEvidence = cutouts
            .SelectMany(cutout => cutout.Evidence)
            .ToArray();
        return !ContainsAnyEvidence(
            cutoutEvidence,
            "fixture detail",
            "object/fixture",
            "repeated short detail",
            "surface pattern",
            "wall-like linework near anchored opening");
    }

    private static WallGraphTopologySpan CreateContinuousExteriorOpeningTopologySpan(
        WallGraphTopologySpan span,
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        var openingIds = string.Join(
            ", ",
            cutouts
                .Select(cutout => cutout.OpeningId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal));
        var evidence = span.Evidence
            .Append(
                "clean placement exterior topology kept continuous across anchored opening cutouts"
                + (string.IsNullOrWhiteSpace(openingIds) ? "" : $" {openingIds}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return span with { Evidence = evidence };
    }

    private static void AddSplitPiece(
        List<WallGraphTopologySpan> pieces,
        WallGraphTopologySpan span,
        WallSegment sourceWall,
        double startParameter,
        double endParameter,
        ref int sequence,
        PlacementWallOpeningCutoutExport? previousCutout,
        PlacementWallOpeningCutoutExport? nextCutout)
    {
        var length = (endParameter - startParameter) * sourceWall.CenterLine.Length;
        var minimumLength = OpeningAdjacentMinimumCleanRunLength(
            span,
            sourceWall,
            previousCutout,
            nextCutout);
        if (length < minimumLength)
        {
            return;
        }

        pieces.Add(CreateOpeningSplitSpan(
            span,
            sourceWall,
            startParameter,
            endParameter,
            sequence++,
            previousCutout?.OpeningId,
            nextCutout?.OpeningId));
    }

    private static double OpeningAdjacentMinimumCleanRunLength(
        WallGraphTopologySpan span,
        WallSegment sourceWall,
        PlacementWallOpeningCutoutExport? previousCutout,
        PlacementWallOpeningCutoutExport? nextCutout)
    {
        if (previousCutout is null && nextCutout is null)
        {
            return MinCleanRunLengthDrawingUnits;
        }

        if (HasDoorLikeAdjacentCutout(previousCutout) || HasDoorLikeAdjacentCutout(nextCutout))
        {
            return IsTrustedOpeningAdjacentShortRun(span, sourceWall)
                ? MinTrustedOpeningAdjacentCleanRunLengthDrawingUnits
                : MinOpeningAdjacentCleanRunLengthDrawingUnits;
        }

        return IsTrustedOpeningAdjacentShortRun(span, sourceWall)
            ? MinTrustedOpeningAdjacentCleanRunLengthDrawingUnits
            : MinOpeningAdjacentCleanRunLengthDrawingUnits;
    }

    private static bool HasDoorLikeAdjacentCutout(PlacementWallOpeningCutoutExport? cutout)
    {
        if (cutout is null)
        {
            return false;
        }

        return string.Equals(cutout.Type, OpeningType.Door.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Type, OpeningType.GenericOpening.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.PassThrough.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.Hinged.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.DoubleSwing.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.Sliding.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(cutout.Operation, OpeningOperation.PocketSliding.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrustedOpeningAdjacentShortRun(
        WallGraphTopologySpan span,
        WallSegment sourceWall)
    {
        if (sourceWall.DetectionKind != WallDetectionKind.ParallelLinePair
            || sourceWall.WallType == WallType.Unknown
            || sourceWall.FragmentEvidence?.RequiresGeometryReview == true
            || sourceWall.PairEvidence is not { } pair
            || pair.Score < MinSourceBackedFallbackPairScore
            || pair.OverlapRatio < MinSourceBackedFallbackOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits)
        {
            return false;
        }

        if (pair.Score < MinSourceBackedFallbackStrictPairScore
            && pair.OverlapRatio < MinSourceBackedFallbackRelaxedScoreOverlapRatio)
        {
            return false;
        }

        var maxFaceFragmentCount = Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount);
        var evidence = sourceWall.Evidence
            .Concat(span.Evidence)
            .ToArray();
        var trustedNoisyMainStructuralShortRun =
            sourceWall.WallType == WallType.Interior
            && sourceWall.DrawingLength >= 72.0
            && sourceWall.Confidence.Value >= 0.84
            && pair.Score >= 0.76
            && pair.OverlapRatio >= 0.85
            && ContainsEvidence(evidence, "supported wall evidence inside exterior envelope")
            && !ContainsAnyEvidence(
                evidence,
                "covered-area",
                "covered entry",
                "covered-entry",
                "door swing",
                "door leaf",
                "fixture",
                "object",
                "railing",
                "stair",
                "terrace");
        var fragmentLimit = trustedNoisyMainStructuralShortRun
            ? 360
            : sourceWall.DrawingLength >= MinLongSourceBackedFallbackWallLengthDrawingUnits
            ? MaxLongSourceBackedFallbackFaceFragmentCount
            : MaxSourceBackedFallbackFaceFragmentCount;
        if (maxFaceFragmentCount > fragmentLimit)
        {
            return false;
        }

        return span.Confidence.Value >= 0.7 && sourceWall.Confidence.Value >= 0.7;
    }

    private static WallGraphTopologySpan CreateOpeningSplitSpan(
        WallGraphTopologySpan span,
        WallSegment sourceWall,
        double startParameter,
        double endParameter,
        int sequence,
        string? previousOpeningId,
        string? nextOpeningId)
    {
        var placementAxis = WallBodyFootprintBuilder.BuildPlacementAxis(sourceWall, startParameter, endParameter);
        var centerLine = placementAxis.CenterLine;
        var thickness = Math.Max(span.Thickness, sourceWall.Thickness);
        var bounds = centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
        var sourceLength = sourceWall.CenterLine.Length;
        var evidence = span.Evidence
            .Append("clean placement topology span split around anchored door/opening cutouts");
        if (!string.IsNullOrWhiteSpace(previousOpeningId))
        {
            evidence = evidence.Append($"previous adjacent opening cutout {previousOpeningId}");
        }

        if (!string.IsNullOrWhiteSpace(nextOpeningId))
        {
            evidence = evidence.Append($"next adjacent opening cutout {nextOpeningId}");
        }

        if (placementAxis.UsesPairedFaceEvidence)
        {
            evidence = evidence.Append($"split topology span centered between paired wall faces using {placementAxis.GeometrySource}");
        }

        return span with
        {
            Id = $"{span.Id}:opening-piece:{sequence}",
            CenterLine = centerLine,
            Bounds = bounds,
            DrawingLength = centerLine.Length,
            SourceWallStartOffsetDrawingUnits = startParameter * sourceLength,
            SourceWallEndOffsetDrawingUnits = endParameter * sourceLength,
            SourceWallProjectedLengthDrawingUnits = (endParameter - startParameter) * sourceLength,
            SourceWallStartParameter = startParameter,
            SourceWallEndParameter = endParameter,
            SourceWallCenterParameter = (startParameter + endParameter) / 2.0,
            SourceWallStartProjectionDistanceDrawingUnits = sourceWall.CenterLine.DistanceToPoint(centerLine.Start),
            SourceWallEndProjectionDistanceDrawingUnits = sourceWall.CenterLine.DistanceToPoint(centerLine.End),
            Thickness = thickness,
            Evidence = evidence
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static bool IsTrustedSourceBackedFallbackDespiteTopologyImportBlock(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? assessment)
    {
        if (component is null
            || component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
            || assessment is null
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.RecoveredWallBody)
            || wall.DetectionKind != WallDetectionKind.ParallelLinePair
            || wall.PairEvidence is not { } pair
            || pair.Score < MinTopologyBlockedFallbackPairScore
            || pair.OverlapRatio < MinTopologyBlockedFallbackOverlapRatio
            || pair.FaceSeparation < MinSourceBackedFallbackFaceSeparationDrawingUnits
            || pair.FaceSeparation > MaxSourceBackedFallbackFaceSeparationDrawingUnits
            || Math.Max(pair.FirstFaceFragmentCount, pair.SecondFaceFragmentCount) > MaxTopologyBlockedFallbackFaceFragmentCount)
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.PositiveEvidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component.Evidence)
            .ToArray();
        if (ContainsAnyEvidence(
                evidence,
                "outdoor covered-area boundary",
                "unpaired outdoor covered-area boundary",
                "covered-entry",
                "covered entry",
                "overbygd",
                "surface pattern",
                "object/fixture",
                "fixture detail",
                "repeated short detail",
                "door/opening"))
        {
            return false;
        }

        return ContainsEvidence(evidence, "geometric room boundary support")
            || ContainsEvidence(evidence, "explicit room boundary support")
            || ContainsEvidence(evidence, "detected room evidence on both sides")
            || component.Kind == WallGraphComponentKind.MainStructural;
    }

    private static IReadOnlyList<PlacementWallOpeningCutoutExport> BuildTopologyOpeningCutouts(
        WallSegment wall,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var cutouts = new List<PlacementWallOpeningCutoutExport>();
        var sequence = 1;
        foreach (var opening in openings)
        {
            if (!ShouldSplitTopologyForOpening(opening)
                || !OpeningDirectlyReferencesWall(wall, opening))
            {
                continue;
            }

            var cutout = PlacementWallOpeningCutoutExport.From(wall, opening, millimetersPerDrawingUnit: null, sequence++);
            if (cutout is not null)
            {
                cutouts.Add(cutout);
            }
        }

        return MergeTopologyOpeningCutouts(cutouts);
    }

    private static bool ShouldSplitTopologyForOpening(OpeningCandidate opening) =>
        opening.Type is OpeningType.Door or OpeningType.GenericOpening
        || opening.Operation is OpeningOperation.PassThrough
            or OpeningOperation.Hinged
            or OpeningOperation.DoubleSwing
            or OpeningOperation.Sliding
            or OpeningOperation.PocketSliding;

    private static bool OpeningDirectlyReferencesWall(WallSegment wall, OpeningCandidate opening) =>
        string.Equals(opening.WallId, wall.Id, StringComparison.Ordinal)
        || opening.HostWallIds.Contains(wall.Id, StringComparer.Ordinal)
        || string.Equals(opening.Placement?.HostWallId, wall.Id, StringComparison.Ordinal)
        || opening.Placement?.AnchorWallIds.Contains(wall.Id, StringComparer.Ordinal) == true;

    private static IReadOnlyList<PlacementWallOpeningCutoutExport> MergeTopologyOpeningCutouts(
        IReadOnlyList<PlacementWallOpeningCutoutExport> cutouts)
    {
        if (cutouts.Count <= 1)
        {
            return cutouts;
        }

        var merged = new List<PlacementWallOpeningCutoutExport>();
        foreach (var cutout in cutouts.OrderBy(item => item.StartParameter).ThenBy(item => item.EndParameter))
        {
            if (merged.Count == 0
                || cutout.StartParameter > merged[^1].EndParameter + 0.001)
            {
                merged.Add(cutout);
                continue;
            }

            var previous = merged[^1];
            if (cutout.EndParameter > previous.EndParameter)
            {
                merged[^1] = previous with
                {
                    EndParameter = cutout.EndParameter,
                    CenterParameter = (previous.StartParameter + cutout.EndParameter) / 2.0,
                    EndOffsetDrawingUnits = cutout.EndOffsetDrawingUnits,
                    CenterOffsetDrawingUnits = (previous.StartOffsetDrawingUnits + cutout.EndOffsetDrawingUnits) / 2.0,
                    LengthDrawingUnits = cutout.EndOffsetDrawingUnits - previous.StartOffsetDrawingUnits,
                    Evidence = previous.Evidence.Concat(cutout.Evidence).Distinct(StringComparer.Ordinal).ToArray()
                };
            }
        }

        return merged;
    }

    private static IReadOnlyList<WallGraphTopologySpan> ProjectSpansToOrthogonalSourceAxes(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count == 0)
        {
            return spans;
        }

        var projected = new List<WallGraphTopologySpan>(spans.Count);
        foreach (var span in spans)
        {
            var replacement = ProjectSpanToOrthogonalSourceAxis(span);
            if (replacement is not null)
            {
                projected.Add(replacement);
            }
        }

        return projected;
    }

    private static WallGraphTopologySpan? ProjectSpanToOrthogonalSourceAxis(WallGraphTopologySpan span)
    {
        var sourceWall = span.SourceWall;
        if (sourceWall is null || sourceWall.CenterLine.Length <= 0.001)
        {
            return span;
        }

        var orientation = ResolveDominantOrthogonalOrientation(sourceWall.CenterLine);
        if (orientation == PlacementRunOrientation.Unknown)
        {
            return span;
        }

        var sourceLine = sourceWall.CenterLine;
        var sourceLength = sourceLine.Length;
        var startParameter = Math.Clamp(span.SourceWallStartParameter ?? sourceLine.ProjectParameter(span.CenterLine.Start), 0, 1);
        var endParameter = Math.Clamp(span.SourceWallEndParameter ?? sourceLine.ProjectParameter(span.CenterLine.End), 0, 1);
        var sourceStart = sourceLine.PointAt(startParameter);
        var sourceEnd = sourceLine.PointAt(endParameter);
        var centerAxis = orientation == PlacementRunOrientation.Horizontal
            ? (sourceLine.Start.Y + sourceLine.End.Y) / 2.0
            : (sourceLine.Start.X + sourceLine.End.X) / 2.0;
        var centerLine = orientation == PlacementRunOrientation.Horizontal
            ? new PlanLineSegment(
                new PlanPoint(sourceStart.X, centerAxis),
                new PlanPoint(sourceEnd.X, centerAxis))
            : new PlanLineSegment(
                new PlanPoint(centerAxis, sourceStart.Y),
                new PlanPoint(centerAxis, sourceEnd.Y));

        if (centerLine.Length <= 0.001)
        {
            return null;
        }

        if (centerLine.Length < MinCleanRunLengthDrawingUnits
            && SpanLeavesSourceAxis(span, orientation))
        {
            return null;
        }

        var axisShift = MaxSourceAxisShift(span, orientation, centerLine);
        var sourceWallStartProjectionDistance = sourceLine.DistanceToPoint(centerLine.Start);
        var sourceWallEndProjectionDistance = sourceLine.DistanceToPoint(centerLine.End);
        var centerParameter = (startParameter + endParameter) / 2.0;
        var bounds = centerLine.Bounds.Inflate(Math.Max(span.Thickness / 2.0, 0.5));
        var evidence = axisShift > 0.001
            ? span.Evidence
                .Append($"clean placement orthogonalization: projected graph span back to source wall axis by up to {axisShift:0.###} drawing units")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : span.Evidence;

        return span with
        {
            CenterLine = centerLine,
            Bounds = bounds,
            DrawingLength = centerLine.Length,
            SourceWallStartOffsetDrawingUnits = startParameter * sourceLength,
            SourceWallEndOffsetDrawingUnits = endParameter * sourceLength,
            SourceWallProjectedLengthDrawingUnits = Math.Abs(endParameter - startParameter) * sourceLength,
            SourceWallStartParameter = startParameter,
            SourceWallEndParameter = endParameter,
            SourceWallCenterParameter = centerParameter,
            SourceWallStartProjectionDistanceDrawingUnits = sourceWallStartProjectionDistance,
            SourceWallEndProjectionDistanceDrawingUnits = sourceWallEndProjectionDistance,
            Evidence = evidence
        };
    }

    private static PlacementRunOrientation ResolveDominantOrthogonalOrientation(PlanLineSegment line)
    {
        if (line.IsHorizontal())
        {
            return PlacementRunOrientation.Horizontal;
        }

        if (line.IsVertical())
        {
            return PlacementRunOrientation.Vertical;
        }

        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        var dominant = Math.Max(dx, dy);
        var minor = Math.Min(dx, dy);
        if (dominant <= 0.001
            || minor > MaxDominantAxisSkewDrawingUnits
            || minor / dominant > MaxDominantAxisSkewRatio)
        {
            return PlacementRunOrientation.Unknown;
        }

        return dx >= dy
            ? PlacementRunOrientation.Horizontal
            : PlacementRunOrientation.Vertical;
    }

    private static bool SpanLeavesSourceAxis(
        WallGraphTopologySpan span,
        PlacementRunOrientation orientation)
    {
        var dx = Math.Abs(span.CenterLine.End.X - span.CenterLine.Start.X);
        var dy = Math.Abs(span.CenterLine.End.Y - span.CenterLine.Start.Y);
        return orientation == PlacementRunOrientation.Horizontal
            ? dy > MinPlacementRegularizationToleranceDrawingUnits
            : dx > MinPlacementRegularizationToleranceDrawingUnits;
    }

    private static double MaxSourceAxisShift(
        WallGraphTopologySpan span,
        PlacementRunOrientation orientation,
        PlanLineSegment projectedLine)
    {
        if (orientation == PlacementRunOrientation.Horizontal)
        {
            return Math.Max(
                Math.Abs(span.CenterLine.Start.Y - projectedLine.Start.Y),
                Math.Abs(span.CenterLine.End.Y - projectedLine.End.Y));
        }

        return Math.Max(
            Math.Abs(span.CenterLine.Start.X - projectedLine.Start.X),
            Math.Abs(span.CenterLine.End.X - projectedLine.End.X));
    }

    private static WallGraphTopologySpan RegularizePlacementSpan(
        WallGraphTopologySpan span,
        double targetCoordinate,
        double shift)
    {
        var line = ResolveAxisOrientation(span.CenterLine) switch
        {
            PlacementRunOrientation.Horizontal => new PlanLineSegment(
                new PlanPoint(span.CenterLine.Start.X, targetCoordinate),
                new PlanPoint(span.CenterLine.End.X, targetCoordinate)),
            PlacementRunOrientation.Vertical => new PlanLineSegment(
                new PlanPoint(targetCoordinate, span.CenterLine.Start.Y),
                new PlanPoint(targetCoordinate, span.CenterLine.End.Y)),
            _ => span.CenterLine
        };
        var bounds = line.Bounds.Inflate(Math.Max(span.Thickness / 2.0, 0.5));
        var evidence = span.Evidence
            .Append($"clean placement regularization: snapped nearly-collinear run by {shift:0.###} drawing units")
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return span with
        {
            CenterLine = line,
            Bounds = bounds,
            DrawingLength = line.Length,
            SourceWallStartProjectionDistanceDrawingUnits = MaxNullable(
                span.SourceWallStartProjectionDistanceDrawingUnits,
                shift),
            SourceWallEndProjectionDistanceDrawingUnits = MaxNullable(
                span.SourceWallEndProjectionDistanceDrawingUnits,
                shift),
            Evidence = evidence
        };
    }

    private static double PlacementRegularizationTolerance(IEnumerable<WallGraphTopologySpan> spans)
    {
        var thicknesses = spans
            .Select(span => span.Thickness)
            .Where(thickness => thickness > 0)
            .OrderBy(thickness => thickness)
            .ToArray();
        var medianThickness = thicknesses.Length == 0
            ? 4.0
            : thicknesses[thicknesses.Length / 2];

        return Math.Clamp(
            medianThickness * 0.75,
            MinPlacementRegularizationToleranceDrawingUnits,
            MaxPlacementRegularizationToleranceDrawingUnits);
    }

    private static bool IsAxisAlignedPlacementSpan(WallGraphTopologySpan span) =>
        ResolveAxisOrientation(span.CenterLine) is not PlacementRunOrientation.Unknown;

    private static bool UsesPairedPlacementAxis(WallGraphTopologySpan span)
    {
        var sourceWall = span.SourceWall;
        if (sourceWall?.PairEvidence is null || sourceWall.CenterLine.Length <= 0.001)
        {
            return false;
        }

        var startParameter = span.SourceWallStartParameter
            ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.Start);
        var endParameter = span.SourceWallEndParameter
            ?? sourceWall.CenterLine.ProjectParameter(span.CenterLine.End);
        return WallBodyFootprintBuilder.BuildPlacementAxis(
                sourceWall,
                startParameter,
                endParameter)
            .UsesPairedFaceEvidence;
    }

    private static PlacementRunOrientation ResolveAxisOrientation(PlanLineSegment line)
    {
        if (line.IsHorizontal())
        {
            return PlacementRunOrientation.Horizontal;
        }

        if (line.IsVertical())
        {
            return PlacementRunOrientation.Vertical;
        }

        return PlacementRunOrientation.Unknown;
    }

    private static double AxisCoordinate(WallGraphTopologySpan span) =>
        ResolveAxisOrientation(span.CenterLine) == PlacementRunOrientation.Horizontal
            ? (span.CenterLine.Start.Y + span.CenterLine.End.Y) / 2.0
            : (span.CenterLine.Start.X + span.CenterLine.End.X) / 2.0;

    private static double AxisMin(PlanLineSegment line) =>
        ResolveAxisOrientation(line) == PlacementRunOrientation.Horizontal
            ? Math.Min(line.Start.X, line.End.X)
            : Math.Min(line.Start.Y, line.End.Y);

    private static double AxisMax(PlanLineSegment line) =>
        ResolveAxisOrientation(line) == PlacementRunOrientation.Horizontal
            ? Math.Max(line.Start.X, line.End.X)
            : Math.Max(line.Start.Y, line.End.Y);

    private static double WeightedAxisCoordinate(IReadOnlyList<WallGraphTopologySpan> spans)
    {
        var totalLength = spans.Sum(span => Math.Max(span.DrawingLength, 0.001));
        return spans.Sum(span => AxisCoordinate(span) * Math.Max(span.DrawingLength, 0.001)) / totalLength;
    }

    private static double MaxNullable(double? existing, double candidate) =>
        Math.Max(existing ?? 0, candidate);

    private static double? MinNullable(double? first, double? second) =>
        first.HasValue && second.HasValue
            ? Math.Min(first.Value, second.Value)
            : first ?? second;

    private static double? MaxNullable(double? first, double? second) =>
        first.HasValue && second.HasValue
            ? Math.Max(first.Value, second.Value)
            : first ?? second;

    private static double? AverageNullable(double? first, double? second) =>
        first.HasValue && second.HasValue
            ? (first.Value + second.Value) / 2.0
            : first ?? second;

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

    private static IReadOnlyDictionary<string, int> BuildNodeIncidentLookup(
        IReadOnlyList<WallEdge> edges)
    {
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Add(edge.FromNodeId);
            Add(edge.ToNodeId);
        }

        return lookup;

        void Add(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return;
            }

            lookup[nodeId] = lookup.TryGetValue(nodeId, out var count)
                ? count + 1
                : 1;
        }
    }

    private static IReadOnlySet<string> BuildTopologyImportBlockedWallIds(
        IReadOnlyList<WallGraphRepairCandidate> repairCandidates)
    {
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in repairCandidates.Where(candidate =>
            candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked))
        {
            foreach (var wallId in WallGraphRepairCandidateImpact.CoordinateImpactedWallIds(candidate))
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    blocked.Add(wallId);
                }
            }
        }

        return blocked;
    }

    private sealed record WallTopologySpanVisibilityContext(
        IReadOnlyList<WallGraphTopologySpan> Spans,
        IReadOnlyDictionary<string, WallGraphComponent> ComponentByWallId,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> WallEvidenceAssessments,
        IReadOnlyDictionary<string, int> NodeDegreeById,
        IReadOnlySet<string> TopologyImportBlockedWallIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ReviewReasonsByWallId,
        IReadOnlyDictionary<string, int> RoomReferenceCountsByWallId,
        PlanCalibration Calibration);

    private readonly record struct PlacementRegularizationKey(
        int PageNumber,
        WallType WallType,
        PlacementRunOrientation Orientation);

    private readonly record struct ContainedDuplicatePlacementKey(
        int PageNumber,
        PlacementRunOrientation Orientation);

    private readonly record struct DominantExteriorAxisSnapHost(
        WallGraphTopologySpan Span,
        double AxisDistance,
        double IntervalGap,
        double OverlapRatio,
        double Score);

    private readonly record struct CleanSpanInterval(double Start, double End);

    private sealed record EndpointSnapCandidate(
        WallGraphTopologySpan Target,
        double AxisDistance,
        double ProjectionOverrun,
        double Score);

    private enum PlacementRunOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }

    private sealed record CleanRunInterval(
        string WallId,
        int PageNumber,
        double StartParameter,
        double EndParameter,
        Confidence Confidence,
        double Thickness,
        IReadOnlyList<string> SourcePrimitiveIds,
        IReadOnlyList<string> Evidence,
        WallSegment SourceWall,
        string SourceFromNodeId,
        string SourceToNodeId,
        IReadOnlyList<string> SourceSpanIds)
    {
        public double LengthDrawingUnits => (EndParameter - StartParameter) * SourceWall.CenterLine.Length;

        public static CleanRunInterval From(WallGraphTopologySpan span, PlanLineSegment sourceLine)
        {
            var start = span.SourceWallStartParameter ?? sourceLine.ProjectParameter(span.CenterLine.Start);
            var end = span.SourceWallEndParameter ?? sourceLine.ProjectParameter(span.CenterLine.End);
            var min = Math.Clamp(Math.Min(start, end), 0, 1);
            var max = Math.Clamp(Math.Max(start, end), 0, 1);
            return new CleanRunInterval(
                span.WallId,
                span.PageNumber,
                min,
                max,
                span.Confidence,
                span.Thickness,
                span.SourcePrimitiveIds,
                span.Evidence.Append($"merged clean placement run includes source topology span {span.Id}").ToArray(),
                span.SourceWall!,
                span.FromNodeId,
                span.ToNodeId,
                [span.Id]);
        }

        public CleanRunInterval Merge(CleanRunInterval next, double gapDrawingUnits)
        {
            var evidence = Evidence
                .Concat(next.Evidence)
                .ToList();
            if (gapDrawingUnits > MaxCleanRunJoinGapDrawingUnits)
            {
                evidence.Add(
                    SourceWall.WallType == WallType.Interior
                        ? $"clean placement interior source-wall run bridge: merged clean intervals across gap {gapDrawingUnits:0.###} drawing units"
                        : $"clean placement exterior source-wall run bridge: merged clean intervals across gap {gapDrawingUnits:0.###} drawing units");
            }

            return this with
            {
                EndParameter = Math.Max(EndParameter, next.EndParameter),
                Confidence = new Confidence(Math.Min(Confidence.Value, next.Confidence.Value)),
                Thickness = Math.Max(Thickness, next.Thickness),
                SourcePrimitiveIds = SourcePrimitiveIds.Concat(next.SourcePrimitiveIds).Distinct(StringComparer.Ordinal).ToArray(),
                Evidence = evidence.Distinct(StringComparer.Ordinal).ToArray(),
                SourceToNodeId = next.SourceToNodeId,
                SourceSpanIds = SourceSpanIds.Concat(next.SourceSpanIds).Distinct(StringComparer.Ordinal).ToArray()
            };
        }

        public WallGraphTopologySpan ToSpan(WallSegment sourceWall, int runIndex)
        {
            var placementAxis = WallBodyFootprintBuilder.BuildPlacementAxis(
                sourceWall,
                StartParameter,
                EndParameter);
            var centerLine = placementAxis.CenterLine;
            var thickness = Math.Max(Thickness, sourceWall.Thickness);
            var bounds = centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
            var sourceLength = sourceWall.CenterLine.Length;
            var startOffset = StartParameter * sourceLength;
            var endOffset = EndParameter * sourceLength;
            var sourceProjectionDistanceStart = sourceWall.CenterLine.DistanceToPoint(centerLine.Start);
            var sourceProjectionDistanceEnd = sourceWall.CenterLine.DistanceToPoint(centerLine.End);
            var evidence = Evidence
                .Prepend("clean placement run projected onto source wall centerline")
                .Prepend($"clean placement run merged {SourceSpanIds.Count} topology span(s)")
                .ToList();
            if (placementAxis.UsesPairedFaceEvidence)
            {
                evidence.Add($"clean placement run centered between paired wall faces using {placementAxis.GeometrySource}");
            }

            return new WallGraphTopologySpan(
                $"{WallId}:clean-run:{runIndex}",
                PageNumber,
                WallId,
                SourceFromNodeId,
                SourceToNodeId,
                centerLine,
                bounds,
                centerLine.Length,
                startOffset,
                endOffset,
                Math.Abs(endOffset - startOffset),
                StartParameter,
                EndParameter,
                (StartParameter + EndParameter) / 2.0,
                sourceProjectionDistanceStart,
                sourceProjectionDistanceEnd,
                thickness,
                Confidence,
                SourcePrimitiveIds,
                SourceSpanIds,
                evidence
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                sourceWall);
        }
    }
}
