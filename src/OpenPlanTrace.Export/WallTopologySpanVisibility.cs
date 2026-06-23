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
    private const double MaxOverlappingCollinearMergeAxisDistanceDrawingUnits = 1.25;
    private const double MinContainedDuplicateOverlapRatio = 0.92;
    private const double MinExteriorFacePairAxisDistanceDrawingUnits = 2.0;
    private const double MaxExteriorFacePairAxisDistanceDrawingUnits = 18.0;
    private const double MinExteriorFacePairOverlapRatio = 0.88;
    private const double MinExteriorFacePairSpanLengthDrawingUnits = 80.0;
    private const double MinPlacementRegularizationToleranceDrawingUnits = 1.25;
    private const double MaxPlacementRegularizationToleranceDrawingUnits = 6.0;
    private const double MinPlacementRegularizationClusterLengthDrawingUnits = 60.0;
    private const double MaxDominantAxisSkewRatio = 0.04;
    private const double MaxDominantAxisSkewDrawingUnits = 8.0;
    private const double MinSourceBackedFallbackWallLengthDrawingUnits = 48.0;
    private const double MinSourceBackedFallbackPairScore = 0.70;
    private const double MinSourceBackedFallbackStrictPairScore = 0.74;
    private const double MinSourceBackedFallbackOverlapRatio = 0.72;
    private const double MinSourceBackedFallbackRelaxedScoreOverlapRatio = 0.96;
    private const double MinSourceBackedFallbackFaceSeparationDrawingUnits = 1.5;
    private const double MaxSourceBackedFallbackFaceSeparationDrawingUnits = 24.0;
    private const double MaxSourceBackedFallbackExistingCoverageRatio = 0.68;
    private const double MinLongSourceBackedFallbackWallLengthDrawingUnits = 120.0;
    private const int MaxSourceBackedFallbackFaceFragmentCount = 48;
    private const int MaxLongSourceBackedFallbackFaceFragmentCount = 72;
    private const int MaxTopologySupportedSourceBackedFallbackFaceFragmentCount = 96;

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

        return BuildCleanPlacementTopologySpans(spans, result.Openings, context, result.Walls, pageNumber);
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans) =>
        FinalizeCleanPlacementSpans(RegularizeCleanPlacementRuns(MergeCleanTopologyRuns(spans)));

    private static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var merged = MergeCleanTopologyRuns(spans);
        return FinalizeCleanPlacementSpans(RegularizeCleanPlacementRuns(SplitCleanTopologyRunsAroundOpenings(merged, openings)));
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
                SplitCleanTopologyRunsAroundOpenings(merged, openings)));
        var sourceBackedFallbackSpans = BuildSourceBackedFallbackSpans(
            walls,
            graphCleanSpans,
            context,
            pageNumber);
        var combined = sourceBackedFallbackSpans.Count == 0
            ? graphCleanSpans
            : merged.Concat(sourceBackedFallbackSpans).ToArray();

        if (sourceBackedFallbackSpans.Count == 0)
        {
            return graphCleanSpans;
        }

        return FinalizeCleanPlacementSpans(
            RegularizeCleanPlacementRuns(
                SplitCleanTopologyRunsAroundOpenings(combined, openings)));
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
        return FinalizeCleanPlacementSpans(RegularizeCleanPlacementRuns(SplitCleanTopologyRunsAroundOpenings(projected, result.Openings)));
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
        if (!trustedExteriorShellContinuityFragment
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

        if (context.TopologyImportBlockedWallIds.Contains(span.WallId))
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

    private static WallTopologySpanVisibilityContext BuildContext(PlanScanResult result) =>
        new(
            WallGraphTopologySpanBuilder.Build(result.WallGraph, result.Walls),
            BuildWallComponentLookup(result.WallGraph.Components),
            WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap),
            BuildNodeIncidentLookup(result.WallGraph.Edges),
            BuildTopologyImportBlockedWallIds(result.WallGraph.RepairCandidates),
            WallPlacementContextGuards.BuildReviewReasons(result),
            result.Calibration);

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

            var coverageRatio = CleanPlacementCoverageRatio(wall, cleanSpans);
            if (coverageRatio >= MaxSourceBackedFallbackExistingCoverageRatio
                || !ShouldBuildSourceBackedFallbackSpan(wall, context))
            {
                continue;
            }

            var span = CreateSourceBackedFallbackSpan(wall, context, coverageRatio);
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
        if (wall.CenterLine.Length < MinSourceBackedFallbackWallLengthDrawingUnits
            || wall.WallType == WallType.Unknown
            || wall.FragmentEvidence?.RequiresGeometryReview == true
            || ResolveDominantOrthogonalOrientation(wall.CenterLine) == PlacementRunOrientation.Unknown
            || context.TopologyImportBlockedWallIds.Contains(wall.Id))
        {
            return false;
        }

        context.ComponentByWallId.TryGetValue(wall.Id, out var component);
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
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
        if ((!IsPlacementReadyStructuralSpan(component, assessment)
                && !hasTrustedExteriorShellContinuityFragment)
            || assessment is null
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || (assessment.Category is not (WallEvidenceCategory.StrongWallBody or WallEvidenceCategory.RecoveredWallBody)
                && !hasTopologySupportedFragmentedPairPromotion
                && !hasTrustedFragmentMergedPromotion
                && !hasTrustedExteriorShellContinuityFragment))
        {
            return false;
        }

        if (!WallPlacementReadinessEvaluator.Evaluate(
            wall,
            context.Calibration,
            component,
            assessment,
            reviewReasons).ReadyForCoordinatePlacement)
        {
            return false;
        }

        return HasTrustedSourceBackedFallbackPairEvidence(wall, component, assessment)
            || hasTrustedFragmentMergedPromotion
            || hasTrustedExteriorShellContinuityFragment;
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
        double existingCoverageRatio)
    {
        context.WallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
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
            evidence.Add("source-backed fallback accepted because clean promoted fragment wall-body evidence is placement-ready");
        }
        else
        {
            evidence.Add("source-backed fallback accepted only because paired wall-face evidence is placement-ready");
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
        IReadOnlyList<WallGraphTopologySpan> cleanSpans)
    {
        if (wall.CenterLine.Length <= 0.001)
        {
            return 1;
        }

        var intervals = cleanSpans
            .Where(span => span.WallId == wall.Id)
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
                if (gap <= MaxCleanRunJoinGapDrawingUnits)
                {
                    current = current.Merge(next);
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
        IReadOnlyList<WallGraphTopologySpan> spans) =>
        SuppressContainedDuplicatePlacementSpans(
            MergeOverlappingCollinearPlacementSpans(
                CanonicalizeExteriorParallelFaceSpans(spans)));

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
        return overlap / shorterLength >= MinExteriorFacePairOverlapRatio;
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
            .GroupBy(span => new PlacementRegularizationKey(
                span.PageNumber,
                span.SourceWall?.WallType ?? WallType.Unknown,
                ResolveAxisOrientation(span.CenterLine))))
        {
            var kept = new List<WallGraphTopologySpan>();
            foreach (var span in group
                .OrderBy(span => IsSourceBackedFallbackSpan(span) ? 1 : 0)
                .ThenByDescending(span => span.DrawingLength)
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
            if (ResolveAxisOrientation(kept.CenterLine) != candidateOrientation
                || Math.Abs(AxisCoordinate(candidate) - AxisCoordinate(kept)) > maxAxisDistance)
            {
                continue;
            }

            var overlap = Math.Min(candidateMax, AxisMax(kept.CenterLine)) - Math.Max(candidateMin, AxisMin(kept.CenterLine));
            if (overlap <= 0)
            {
                continue;
            }

            var overlapRatio = overlap / Math.Max(candidate.DrawingLength, 0.001);
            if (overlapRatio >= MinContainedDuplicateOverlapRatio)
            {
                if (candidateIsSourceBackedFallback
                    && !IsSameSourceBackedFallbackPlacement(candidate, kept))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static bool IsSameSourceBackedFallbackPlacement(
        WallGraphTopologySpan candidate,
        WallGraphTopologySpan kept) =>
        string.Equals(candidate.WallId, kept.WallId, StringComparison.Ordinal)
        || HasSharedSourceReference(candidate.SourcePrimitiveIds, kept.SourcePrimitiveIds)
        || HasSharedSourceReference(candidate.SourceWallGraphEdgeIds, kept.SourceWallGraphEdgeIds);

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

    private static double ContainedDuplicateAxisDistance(WallGraphTopologySpan candidate, WallGraphTopologySpan kept) =>
        candidate.SourceWall?.WallType == WallType.Exterior && kept.SourceWall?.WallType == WallType.Exterior
            ? MaxExteriorFacePairAxisDistanceDrawingUnits
            : MaxContainedDuplicateAxisDistanceDrawingUnits;

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
            return MinOpeningAdjacentCleanRunLengthDrawingUnits;
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
        var fragmentLimit = sourceWall.DrawingLength >= MinLongSourceBackedFallbackWallLengthDrawingUnits
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

    private static IReadOnlyList<PlacementWallOpeningCutoutExport> BuildTopologyOpeningCutouts(
        WallSegment wall,
        IReadOnlyList<OpeningCandidate> openings)
    {
        var cutouts = new List<PlacementWallOpeningCutoutExport>();
        var sequence = 1;
        foreach (var opening in openings)
        {
            if (!ShouldSplitTopologyForOpening(opening))
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
        PlanCalibration Calibration);

    private readonly record struct PlacementRegularizationKey(
        int PageNumber,
        WallType WallType,
        PlacementRunOrientation Orientation);

    private readonly record struct CleanSpanInterval(double Start, double End);

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

        public CleanRunInterval Merge(CleanRunInterval next) =>
            this with
            {
                EndParameter = Math.Max(EndParameter, next.EndParameter),
                Confidence = new Confidence(Math.Min(Confidence.Value, next.Confidence.Value)),
                Thickness = Math.Max(Thickness, next.Thickness),
                SourcePrimitiveIds = SourcePrimitiveIds.Concat(next.SourcePrimitiveIds).Distinct(StringComparer.Ordinal).ToArray(),
                Evidence = Evidence.Concat(next.Evidence).Distinct(StringComparer.Ordinal).ToArray(),
                SourceToNodeId = next.SourceToNodeId,
                SourceSpanIds = SourceSpanIds.Concat(next.SourceSpanIds).Distinct(StringComparer.Ordinal).ToArray()
            };

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
