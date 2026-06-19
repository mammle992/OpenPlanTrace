namespace OpenPlanTrace.Export;

internal static class WallTopologySpanVisibility
{
    private const double MaxCleanDanglingSpanLength = 36.0;
    private const double MinTrustedShortStructuralDanglingSpanLength = 18.0;
    private const double MaxCleanRunJoinGapDrawingUnits = 12.0;
    private const double MinCleanRunLengthDrawingUnits = 8.0;
    private const double MinPlacementRegularizationToleranceDrawingUnits = 1.25;
    private const double MaxPlacementRegularizationToleranceDrawingUnits = 6.0;
    private const double MinPlacementRegularizationClusterLengthDrawingUnits = 60.0;
    private const double MaxDominantAxisSkewRatio = 0.04;
    private const double MaxDominantAxisSkewDrawingUnits = 8.0;

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

        return options.IncludeReviewOnlyWallTopologySpans
            ? spans
            : BuildCleanPlacementTopologySpans(spans);
    }

    public static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        IReadOnlyList<WallGraphTopologySpan> spans) =>
        RegularizeCleanPlacementRuns(MergeCleanTopologyRuns(spans));

    public static IReadOnlyList<WallGraphTopologySpan> BuildCleanPlacementTopologySpans(
        PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var options = SvgOverlayRenderOptions.ForProfile(SvgOverlayRenderProfile.PlacementReview);
        var context = BuildContext(result);
        var spans = context.Spans
            .Where(span => IsVisibleTopologySpan(span, context, options))
            .ToArray();

        return BuildCleanPlacementTopologySpans(spans);
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

        return RegularizeCleanPlacementRuns(ProjectSpansToOrthogonalSourceAxes(spans));
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
        if (options.IncludeReviewOnlyWallTopologySpans)
        {
            return true;
        }

        context.ComponentByWallId.TryGetValue(span.WallId, out var component);
        context.WallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);
        var reviewReasons = context.ReviewReasonsByWallId.TryGetValue(span.WallId, out var reasons)
            ? reasons
            : Array.Empty<string>();
        if (!IsPlacementReadyStructuralSpan(component, assessment))
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

        return ContainsEvidence(
            assessment.Evidence
                .Concat(span.Evidence)
                .Concat(component.Evidence),
            "promoted to placement-ready by main structural graph component");
    }

    private static bool ContainsEvidence(IEnumerable<string> evidence, string value) =>
        evidence.Any(item => item.Contains(value, StringComparison.OrdinalIgnoreCase));

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

    private static double WeightedAxisCoordinate(IReadOnlyList<WallGraphTopologySpan> spans)
    {
        var totalLength = spans.Sum(span => Math.Max(span.DrawingLength, 0.001));
        return spans.Sum(span => AxisCoordinate(span) * Math.Max(span.DrawingLength, 0.001)) / totalLength;
    }

    private static double MaxNullable(double? existing, double candidate) =>
        Math.Max(existing ?? 0, candidate);

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
            foreach (var wallId in candidate.WallIds)
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
