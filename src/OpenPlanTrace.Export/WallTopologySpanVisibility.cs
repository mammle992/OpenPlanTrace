namespace OpenPlanTrace.Export;

internal static class WallTopologySpanVisibility
{
    private const double MaxCleanDanglingSpanLength = 36.0;
    private const double MaxCleanRunJoinGapDrawingUnits = 12.0;
    private const double MinCleanRunLengthDrawingUnits = 8.0;

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
            : MergeCleanTopologyRuns(spans);
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
        if (!IsPlacementReadyStructuralSpan(component, assessment))
        {
            return false;
        }

        return !IsShortDanglingTopologySpan(span, context.NodeDegreeById);
    }

    private static WallTopologySpanVisibilityContext BuildContext(PlanScanResult result) =>
        new(
            WallGraphTopologySpanBuilder.Build(result.WallGraph, result.Walls),
            BuildWallComponentLookup(result.WallGraph.Components),
            WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap),
            BuildNodeIncidentLookup(result.WallGraph.Edges));

    private static bool IsShortDanglingTopologySpan(
        WallGraphTopologySpan span,
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

        return fromDegree <= 1 || toDegree <= 1;
    }

    private static IReadOnlyList<WallGraphTopologySpan> MergeCleanTopologyRuns(
        IReadOnlyList<WallGraphTopologySpan> spans)
    {
        if (spans.Count <= 1)
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
                .Where(interval => interval.LengthDrawingUnits >= MinCleanRunLengthDrawingUnits)
                .OrderBy(interval => interval.StartParameter)
                .ToArray();

            if (intervals.Length == 0)
            {
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

                merged.Add(current.ToSpan(sourceWall, runIndex++));
                current = next;
            }

            merged.Add(current.ToSpan(sourceWall, runIndex));
        }

        return merged
            .OrderBy(span => span.PageNumber)
            .ThenBy(span => span.Bounds.Y)
            .ThenBy(span => span.Bounds.X)
            .ThenBy(span => span.WallId, StringComparer.Ordinal)
            .ToArray();
    }

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

    private sealed record WallTopologySpanVisibilityContext(
        IReadOnlyList<WallGraphTopologySpan> Spans,
        IReadOnlyDictionary<string, WallGraphComponent> ComponentByWallId,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> WallEvidenceAssessments,
        IReadOnlyDictionary<string, int> NodeDegreeById);

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
                SourceSpanIds = SourceSpanIds.Concat(next.SourceSpanIds).Distinct(StringComparer.Ordinal).ToArray()
            };

        public WallGraphTopologySpan ToSpan(WallSegment sourceWall, int runIndex)
        {
            var centerLine = new PlanLineSegment(
                sourceWall.CenterLine.PointAt(StartParameter),
                sourceWall.CenterLine.PointAt(EndParameter));
            var thickness = Math.Max(Thickness, sourceWall.Thickness);
            var bounds = centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
            var sourceLength = sourceWall.CenterLine.Length;
            var startOffset = StartParameter * sourceLength;
            var endOffset = EndParameter * sourceLength;
            return new WallGraphTopologySpan(
                $"{WallId}:clean-run:{runIndex}",
                PageNumber,
                WallId,
                SourceSpanIds.FirstOrDefault() ?? $"{WallId}:clean-run-start",
                SourceSpanIds.LastOrDefault() ?? $"{WallId}:clean-run-end",
                centerLine,
                bounds,
                centerLine.Length,
                startOffset,
                endOffset,
                Math.Abs(endOffset - startOffset),
                StartParameter,
                EndParameter,
                (StartParameter + EndParameter) / 2.0,
                0,
                0,
                thickness,
                Confidence,
                SourcePrimitiveIds,
                Evidence.Prepend($"clean placement run merged {SourceSpanIds.Count} topology span(s)").ToArray(),
                sourceWall);
        }
    }
}
