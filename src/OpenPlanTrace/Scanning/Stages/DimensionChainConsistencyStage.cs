using System.Globalization;

namespace OpenPlanTrace;

internal sealed class DimensionChainConsistencyStage : IPipelineStage
{
    private const string StageName = "dimension-chains";
    private const double RelativeTolerance = 0.03;

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var pageGroup in context.Dimensions
            .Where(IsEligible)
            .GroupBy(dimension => dimension.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageChains = FindPageChains(pageGroup.ToArray(), context.Options).ToArray();
            if (pageChains.Length == 0)
            {
                continue;
            }

            AddDiagnostics(context, pageGroup.Key, pageChains);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<DimensionChainCandidate> FindPageChains(
        IReadOnlyList<DimensionAnnotation> dimensions,
        ScannerOptions options)
    {
        foreach (var orientationGroup in dimensions
            .Select(DimensionInterval.From)
            .Where(interval => interval is not null)
            .Select(interval => interval!)
            .GroupBy(interval => interval.Orientation))
        {
            var intervals = orientationGroup
                .OrderByDescending(interval => interval.Length)
                .ThenBy(interval => interval.Dimension.Id, StringComparer.Ordinal)
                .ToArray();

            foreach (var parent in intervals)
            {
                var maxOffset = MaxParallelOffset(parent.Length, options);
                var candidates = intervals
                    .Where(child => !string.Equals(child.Dimension.Id, parent.Dimension.Id, StringComparison.Ordinal))
                    .Where(child => child.Length < parent.Length * 0.94)
                    .Where(child => Math.Abs(child.AxisCoordinate - parent.AxisCoordinate) <= maxOffset)
                    .Where(child => child.Start >= parent.Start - CoordinateTolerance(options)
                        && child.End <= parent.End + CoordinateTolerance(options))
                    .OrderBy(child => child.Start)
                    .ThenByDescending(child => child.End)
                    .ToArray();

                if (TryCreateChain(parent, candidates, options) is { } chain)
                {
                    yield return chain;
                }
            }
        }
    }

    private static DimensionChainCandidate? TryCreateChain(
        DimensionInterval parent,
        IReadOnlyList<DimensionInterval> candidates,
        ScannerOptions options)
    {
        if (candidates.Count < 2)
        {
            return null;
        }

        var tolerance = CoordinateTolerance(options);
        var selected = new List<DimensionInterval>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var cursor = parent.Start;

        while (cursor < parent.End - tolerance)
        {
            var next = candidates
                .Where(candidate => !used.Contains(candidate.Dimension.Id))
                .Where(candidate => candidate.Start <= cursor + tolerance)
                .Where(candidate => candidate.End > cursor + tolerance)
                .OrderBy(candidate => Math.Abs(candidate.Start - cursor))
                .ThenByDescending(candidate => candidate.End)
                .FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            selected.Add(next);
            used.Add(next.Dimension.Id);
            cursor = Math.Max(cursor, next.End);
        }

        if (selected.Count < 2 || Math.Abs(cursor - parent.End) > tolerance)
        {
            return null;
        }

        var childDrawingLength = selected.Sum(child => child.Length);
        var drawingRelativeError = Math.Abs(childDrawingLength - parent.Length) / Math.Max(1, parent.Length);
        if (drawingRelativeError > 0.08)
        {
            return null;
        }

        var childMillimeters = selected.Sum(child => child.Dimension.MeasuredMillimeters);
        var relativeError = Math.Abs(childMillimeters - parent.Dimension.MeasuredMillimeters)
            / Math.Max(1, parent.Dimension.MeasuredMillimeters);
        var confidence = new Confidence(Math.Clamp(
            selected.Append(parent).Average(item => item.Dimension.Confidence.Value)
            + (relativeError <= RelativeTolerance ? 0.04 : 0.08),
            0.35,
            0.94));

        return new DimensionChainCandidate(
            parent.Dimension,
            selected.Select(child => child.Dimension).ToArray(),
            parent.Orientation,
            parent.Dimension.MeasuredMillimeters,
            childMillimeters,
            relativeError,
            confidence);
    }

    private static void AddDiagnostics(
        ScanContext context,
        int pageNumber,
        IReadOnlyList<DimensionChainCandidate> chains)
    {
        var conflicts = chains
            .Where(chain => chain.RelativeError > RelativeTolerance)
            .OrderByDescending(chain => chain.RelativeError)
            .ToArray();
        var bounds = PlanRect.Union(chains.SelectMany(chain => chain.AllDimensions).Select(dimension => dimension.Bounds));

        if (conflicts.Length > 0)
        {
            var worst = conflicts[0];
            context.AddDiagnostic(
                "dimensions.chain_conflict",
                DiagnosticSeverity.Warning,
                StageName,
                "One or more chained dimensions do not sum to their parent dimension.",
                pageNumber,
                bounds,
                worst.Confidence,
                DiagnosticScope.Dimension,
                conflicts.SelectMany(chain => chain.AllDimensions).SelectMany(dimension => dimension.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["chainCount"] = chains.Count.ToString(CultureInfo.InvariantCulture),
                    ["conflictCount"] = conflicts.Length.ToString(CultureInfo.InvariantCulture),
                    ["consistentCount"] = (chains.Count - conflicts.Length).ToString(CultureInfo.InvariantCulture),
                    ["worstParentDimensionId"] = worst.Parent.Id,
                    ["worstChildDimensionIds"] = string.Join(",", worst.Children.Select(child => child.Id)),
                    ["parentMillimeters"] = Format(worst.ParentMillimeters),
                    ["childSumMillimeters"] = Format(worst.ChildSumMillimeters),
                    ["relativeErrorPercent"] = Format(worst.RelativeError * 100.0),
                    ["tolerancePercent"] = Format(RelativeTolerance * 100.0)
                });
            return;
        }

        context.AddDiagnostic(
            "dimensions.chains_consistent",
            DiagnosticSeverity.Info,
            StageName,
            "Detected chained dimensions whose child dimensions sum to their parent dimensions.",
            pageNumber,
            bounds,
            new Confidence(Math.Min(0.92, chains.Average(chain => chain.Confidence.Value))),
            DiagnosticScope.Dimension,
            chains.SelectMany(chain => chain.AllDimensions).SelectMany(dimension => dimension.SourcePrimitiveIds),
            new Dictionary<string, string>
            {
                ["chainCount"] = chains.Count.ToString(CultureInfo.InvariantCulture),
                ["consistentCount"] = chains.Count.ToString(CultureInfo.InvariantCulture),
                ["conflictCount"] = "0",
                ["parentDimensionIds"] = string.Join(",", chains.Select(chain => chain.Parent.Id)),
                ["tolerancePercent"] = Format(RelativeTolerance * 100.0)
            });
    }

    private static bool IsEligible(DimensionAnnotation dimension) =>
        dimension.Kind == DimensionKind.Linear
        && dimension.Orientation is DimensionOrientation.Horizontal or DimensionOrientation.Vertical
        && dimension.DimensionLine is not null
        && dimension.DrawingLength is > 0
        && dimension.MeasuredMillimeters > 0;

    private static double CoordinateTolerance(ScannerOptions options) =>
        Math.Max(4, options.WallSnapTolerance * 2);

    private static double MaxParallelOffset(double parentLength, ScannerOptions options) =>
        Math.Max(32, Math.Min(120, Math.Max(options.MaxOpeningGap, parentLength * 0.35)));

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record DimensionChainCandidate(
        DimensionAnnotation Parent,
        IReadOnlyList<DimensionAnnotation> Children,
        DimensionOrientation Orientation,
        double ParentMillimeters,
        double ChildSumMillimeters,
        double RelativeError,
        Confidence Confidence)
    {
        public IEnumerable<DimensionAnnotation> AllDimensions =>
            Children.Prepend(Parent);
    }

    private sealed record DimensionInterval(
        DimensionAnnotation Dimension,
        DimensionOrientation Orientation,
        double Start,
        double End,
        double AxisCoordinate,
        double Length)
    {
        public static DimensionInterval? From(DimensionAnnotation dimension)
        {
            if (dimension.DimensionLine is not { } line)
            {
                return null;
            }

            return dimension.Orientation switch
            {
                DimensionOrientation.Horizontal => new DimensionInterval(
                    dimension,
                    dimension.Orientation,
                    Math.Min(line.Start.X, line.End.X),
                    Math.Max(line.Start.X, line.End.X),
                    (line.Start.Y + line.End.Y) / 2.0,
                    line.Length),
                DimensionOrientation.Vertical => new DimensionInterval(
                    dimension,
                    dimension.Orientation,
                    Math.Min(line.Start.Y, line.End.Y),
                    Math.Max(line.Start.Y, line.End.Y),
                    (line.Start.X + line.End.X) / 2.0,
                    line.Length),
                _ => null
            };
        }
    }
}
