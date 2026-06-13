using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed partial class GridAxisDetectionStage : IPipelineStage
{
    private const double StrongGridLayerConfidence = 0.45;

    public string Name => "grid-axes";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mainRegion = context.SheetRegions.FirstOrDefault(
                region => region.PageNumber == page.Number && region.Kind == RegionKind.MainFloorPlan);

            if (mainRegion is null)
            {
                continue;
            }

            var textItems = EnumerateTextItems(page, context).ToArray();
            var labelIndex = GridLabelSpatialIndex.Create(textItems, context.Options);
            var layerLookup = new GridLayerLookup(context.LayerAnalysis);
            var candidates = PrimitiveGeometry
                .EnumerateLines(page, context)
                .Select(line => GridLineCandidate.TryCreate(line, mainRegion, context.Options, labelIndex, layerLookup))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                .ToArray();

            var runs = MergeRuns(candidates, context.Options)
                .Where(run => IsLongEnough(run, mainRegion, context.Options))
                .OrderBy(run => run.Orientation)
                .ThenBy(run => run.Coordinate)
                .ThenBy(run => run.Start)
                .ToArray();

            if (runs.Length == 0)
            {
                var gridLayerPrimitiveCount = candidates.Length;
                if (gridLayerPrimitiveCount > 0)
                {
                    context.AddDiagnostic(
                        "grid_axes.candidates_without_axes",
                        DiagnosticSeverity.Info,
                        Name,
                        "Grid-axis candidates were present but did not meet axis length thresholds.",
                        page.Number,
                        mainRegion.Bounds,
                        Confidence.Low,
                        DiagnosticScope.Grid,
                        candidates.Select(candidate => candidate.PrimitiveId),
                        new Dictionary<string, string>
                        {
                            ["candidateLineCount"] = gridLayerPrimitiveCount.ToString(),
                            ["sourceRegionId"] = mainRegion.Id
                        });
                }

                continue;
            }

            var axes = runs
                .Select((run, index) => CreateAxis(page, mainRegion, run, labelIndex, context.Options, context.GridAxes.Count + index + 1))
                .Where(axis => axis is not null)
                .Select(axis => axis!)
                .ToArray();

            if (axes.Length == 0)
            {
                context.AddDiagnostic(
                    "grid_axes.candidates_without_labels",
                    DiagnosticSeverity.Info,
                    Name,
                    "Grid-axis candidates were present but did not retain required label evidence after merging.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Low,
                    DiagnosticScope.Grid,
                    runs.SelectMany(run => run.SourcePrimitiveIds),
                    new Dictionary<string, string>
                    {
                        ["candidateRunCount"] = runs.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
                continue;
            }

            context.GridAxes.AddRange(axes);

            var unlabeled = axes.Count(axis => string.IsNullOrWhiteSpace(axis.Label));
            context.AddDiagnostic(
                "grid_axes.detected",
                DiagnosticSeverity.Info,
                Name,
                $"Detected {axes.Length} structural grid axes.",
                page.Number,
                PlanRect.Union(axes.Select(axis => axis.Bounds)),
                axes.Any(axis => axis.Confidence.Value >= 0.8) ? Confidence.High : Confidence.Medium,
                DiagnosticScope.Grid,
                axes.SelectMany(axis => axis.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["axisCount"] = axes.Length.ToString(),
                    ["horizontalCount"] = axes.Count(axis => axis.Orientation == GridAxisOrientation.Horizontal).ToString(),
                    ["verticalCount"] = axes.Count(axis => axis.Orientation == GridAxisOrientation.Vertical).ToString(),
                    ["labeledCount"] = axes.Count(axis => !string.IsNullOrWhiteSpace(axis.Label)).ToString(),
                    ["unlabeledCount"] = unlabeled.ToString(),
                    ["layerBackedCount"] = runs.Count(run => run.HasLayerEvidence).ToString(),
                    ["inferredCount"] = runs.Count(run => run.HasLabelInferenceEvidence && !run.HasLayerEvidence).ToString(),
                    ["candidateLineCount"] = candidates.Length.ToString(),
                    ["candidateRunCount"] = runs.Length.ToString(),
                    ["sourceRegionId"] = mainRegion.Id
                });

            if (unlabeled > 0)
            {
                context.AddDiagnostic(
                    "grid_axes.unlabeled_axes",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{unlabeled} grid axes did not have nearby axis labels.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Low,
                    DiagnosticScope.Grid,
                    axes.Where(axis => string.IsNullOrWhiteSpace(axis.Label)).SelectMany(axis => axis.SourcePrimitiveIds),
                    new Dictionary<string, string>
                    {
                        ["unlabeledCount"] = unlabeled.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }
        }

        return ValueTask.CompletedTask;
    }

    private static GridAxis? CreateAxis(
        PlanPage page,
        SheetRegion mainRegion,
        GridAxisRun run,
        GridLabelSpatialIndex labelIndex,
        ScannerOptions options,
        int axisNumber)
    {
        var line = run.Orientation == GridAxisOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(run.Start, run.Coordinate), new PlanPoint(run.End, run.Coordinate))
            : new PlanLineSegment(new PlanPoint(run.Coordinate, run.Start), new PlanPoint(run.Coordinate, run.End));
        var label = FindNearestLabel(run, line, labelIndex, options);
        if (run.RequiresEndpointLabel && label is null)
        {
            return null;
        }

        var bounds = PlanRect.Union(
                new[] { line.Bounds }
                    .Concat(label is null
                        ? Array.Empty<PlanRect>()
                        : new[] { label.Text.Bounds }.Concat(label.BubbleBounds)))
            .Inflate(2)
            .ClampTo(page.Bounds);
        var sourcePrimitiveIds = run.SourcePrimitiveIds
            .Concat(label is null
                ? Array.Empty<string>()
                : new[] { label.SourceId }.Concat(label.BubbleSourceIds))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var labelSourceIds = label is null ? Array.Empty<string>() : new[] { label.SourceId };
        var evidence = AxisEvidence(run, label, line).ToArray();
        var confidence = AxisConfidence(run, label, line, mainRegion);

        return new GridAxis(
            $"page:{page.Number}:grid-axis:{axisNumber}",
            page.Number,
            run.Orientation,
            label?.Text.Text.Trim(),
            line,
            bounds,
            run.Coordinate,
            confidence,
            mainRegion.Id,
            sourcePrimitiveIds,
            labelSourceIds,
            evidence);
    }

    private static Confidence AxisConfidence(
        GridAxisRun run,
        TextItem? label,
        PlanLineSegment line,
        SheetRegion mainRegion)
    {
        var pageSpan = run.Orientation == GridAxisOrientation.Horizontal
            ? mainRegion.Bounds.Width
            : mainRegion.Bounds.Height;
        var lengthRatio = Math.Clamp(line.Length / Math.Max(1, pageSpan), 0, 1);
        var value = 0.45
            + Math.Min(0.18, run.LayerConfidence.Value * 0.18)
            + Math.Min(0.14, lengthRatio * 0.18);

        if (label is not null)
        {
            value += 0.16;

            if (run.RequiresEndpointLabel)
            {
                value += 0.04;
            }

            if (label.BubbleSourceIds.Count > 0)
            {
                value += 0.06;
            }
        }

        if (!run.HasLayerEvidence && run.HasLabelInferenceEvidence)
        {
            value += 0.04;
        }

        if (run.FragmentCount > 1)
        {
            value += 0.03;
        }

        if (run.TotalGapLength > 0)
        {
            value -= Math.Min(0.08, (run.TotalGapLength / Math.Max(1, line.Length)) * 0.45);
        }

        return new Confidence(Math.Clamp(value, run.HasLayerEvidence ? 0.45 : 0.5, 0.92));
    }

    private static IEnumerable<string> AxisEvidence(
        GridAxisRun run,
        TextItem? label,
        PlanLineSegment line)
    {
        foreach (var item in run.Evidence.Distinct(StringComparer.Ordinal))
        {
            yield return item;
        }

        yield return $"{run.Orientation} grid axis length {Math.Round(line.Length, 3)} drawing units";

        if (run.FragmentCount > 1)
        {
            yield return $"merged {run.FragmentCount} grid fragments";
            if (run.TotalGapLength > 0)
            {
                yield return $"healed {Math.Round(run.TotalGapLength, 3)} drawing units of grid-axis gaps";
            }
        }

        if (label is not null)
        {
            yield return $"matched grid label {label.Text.Text.Trim()}";
            if (label.BubbleSourceIds.Count > 0)
            {
                yield return $"matched grid label bubble from {label.BubbleSourceIds.Count} source primitive(s)";
            }
        }
    }

    private static TextItem? FindNearestLabel(
        GridAxisRun run,
        PlanLineSegment line,
        GridLabelSpatialIndex labelIndex,
        ScannerOptions options)
    {
        var maxDistance = Math.Max(1, options.MaxGridAxisLabelDistance);
        return run.RequiresEndpointLabel
            ? labelIndex.FindNearestEndpointLabel(line, maxDistance)
            : labelIndex.FindNearestAxisLabel(run, line, maxDistance);
    }

    private static double EndpointLabelDistance(PlanLineSegment line, PlanPoint point) =>
        Math.Min(point.DistanceTo(line.Start), point.DistanceTo(line.End));

    private static double LabelDistance(GridAxisRun run, PlanLineSegment line, PlanPoint point)
    {
        var endpointDistance = Math.Min(point.DistanceTo(line.Start), point.DistanceTo(line.End));
        var coordinateDistance = run.Orientation == GridAxisOrientation.Horizontal
            ? Math.Abs(point.Y - run.Coordinate)
            : Math.Abs(point.X - run.Coordinate);

        return Math.Min(endpointDistance, coordinateDistance + DistanceOutsideSpan(run, point));
    }

    private static double DistanceOutsideSpan(GridAxisRun run, PlanPoint point)
    {
        var along = run.Orientation == GridAxisOrientation.Horizontal ? point.X : point.Y;
        if (along < run.Start)
        {
            return run.Start - along;
        }

        if (along > run.End)
        {
            return along - run.End;
        }

        return 0;
    }

    private static GridAxisRun[] MergeRuns(
        IEnumerable<GridLineCandidate> candidates,
        ScannerOptions options)
    {
        var ordered = candidates
            .Select(GridAxisRun.FromCandidate)
            .OrderBy(run => run.Orientation)
            .ThenBy(run => run.Coordinate)
            .ThenBy(run => run.Start)
            .ToArray();
        var merged = new List<GridAxisRun>();
        var maxGap = Math.Max(options.GridAxisMergeTolerance, options.MaxGridAxisFragmentGap);

        foreach (var run in ordered)
        {
            var match = merged.FirstOrDefault(existing =>
                existing.Orientation == run.Orientation
                && Math.Abs(existing.Coordinate - run.Coordinate) <= options.GridAxisMergeTolerance
                && run.Start <= existing.End + maxGap
                && run.End >= existing.Start - maxGap);

            if (match is null)
            {
                merged.Add(run);
                continue;
            }

            match.Merge(run);
        }

        return merged.ToArray();
    }

    private static bool IsLongEnough(GridAxisRun run, SheetRegion mainRegion, ScannerOptions options)
    {
        var pageSpan = run.Orientation == GridAxisOrientation.Horizontal
            ? mainRegion.Bounds.Width
            : mainRegion.Bounds.Height;
        var threshold = Math.Max(options.MinGridAxisLength, pageSpan * options.MinGridAxisLengthRatio);
        return run.Length >= threshold;
    }

    private static IEnumerable<TextItem> EnumerateTextItems(PlanPage page, ScanContext context)
    {
        var bubbles = EnumerateLabelBubbles(page, context).ToArray();

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not TextPrimitive text)
            {
                continue;
            }

            var matchedBubbles = bubbles
                .Where(bubble => bubble.Bounds.Contains(text.Bounds.Center, 4)
                    || bubble.Bounds.Intersects(text.Bounds.Inflate(4)))
                .ToArray();

            yield return new TextItem(
                text,
                context.PrimitiveId(page.Number, index, text),
                matchedBubbles.Select(bubble => bubble.SourceId).Distinct(StringComparer.Ordinal).ToArray(),
                matchedBubbles.Select(bubble => bubble.Bounds).ToArray(),
                index);
        }
    }

    private static IEnumerable<LabelBubble> EnumerateLabelBubbles(PlanPage page, ScanContext context)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (!IsLabelBubbleGeometry(primitive))
            {
                continue;
            }

            yield return new LabelBubble(
                context.PrimitiveId(page.Number, index, primitive),
                primitive.Bounds);
        }
    }

    private static bool IsLabelBubbleGeometry(PlanPrimitive primitive)
    {
        var bounds = primitive.Bounds;
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8 || bounds.Width > 90 || bounds.Height > 90)
        {
            return false;
        }

        var aspectRatio = bounds.Width / Math.Max(1, bounds.Height);
        if (aspectRatio is < 0.45 or > 2.25)
        {
            return false;
        }

        return primitive switch
        {
            RectanglePrimitive => true,
            PolylinePrimitive { Closed: true } => true,
            ArcPrimitive arc => Math.Abs(arc.SweepAngleRadians) >= Math.PI * 1.65,
            _ => false
        };
    }

    private static bool IsGridLabelText(string text)
    {
        var trimmed = text.Trim();
        return GridLabelPattern().IsMatch(trimmed);
    }

    [GeneratedRegex(@"^(?:[A-Z]{1,3}|\d{1,3}|[A-Z]\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex GridLabelPattern();

    private sealed record TextItem(
        TextPrimitive Text,
        string SourceId,
        IReadOnlyList<string> BubbleSourceIds,
        IReadOnlyList<PlanRect> BubbleBounds,
        int Order);

    private sealed record LabelBubble(string SourceId, PlanRect Bounds);

    private sealed class GridLabelSpatialIndex
    {
        private readonly Dictionary<Cell, List<TextItem>> _cells;
        private readonly double _cellSize;

        private GridLabelSpatialIndex(Dictionary<Cell, List<TextItem>> cells, double cellSize)
        {
            _cells = cells;
            _cellSize = cellSize;
        }

        public static GridLabelSpatialIndex Create(IReadOnlyList<TextItem> textItems, ScannerOptions options)
        {
            var cellSize = Math.Max(4, Math.Max(1, options.MaxGridAxisLabelDistance));
            var cells = new Dictionary<Cell, List<TextItem>>();

            foreach (var item in textItems.Where(item => IsGridLabelText(item.Text.Text)))
            {
                var cell = CellFor(item.Text.Bounds.Center, cellSize);
                if (!cells.TryGetValue(cell, out var bucket))
                {
                    bucket = new List<TextItem>();
                    cells[cell] = bucket;
                }

                bucket.Add(item);
            }

            return new GridLabelSpatialIndex(cells, cellSize);
        }

        public TextItem? FindNearestEndpointLabel(PlanLineSegment line, double maxDistance)
        {
            var candidates = QueryAround(line.Start, maxDistance)
                .Concat(QueryAround(line.End, maxDistance))
                .DistinctBy(item => item.SourceId)
                .Select(item => new
                {
                    Item = item,
                    Distance = EndpointLabelDistance(line, item.Text.Bounds.Center)
                })
                .Where(item => item.Distance <= maxDistance)
                .OrderBy(item => item.Distance)
                .ThenByDescending(item => item.Item.BubbleSourceIds.Count)
                .ThenBy(item => item.Item.Order);

            return candidates.Select(item => item.Item).FirstOrDefault();
        }

        public TextItem? FindNearestAxisLabel(GridAxisRun run, PlanLineSegment line, double maxDistance)
        {
            var search = AxisLabelSearchBounds(run, maxDistance);
            var candidates = Query(search)
                .Select(item => new
                {
                    Item = item,
                    Distance = LabelDistance(run, line, item.Text.Bounds.Center)
                })
                .Where(item => item.Distance <= maxDistance)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Item.Text.Bounds.Top)
                .ThenBy(item => item.Item.Text.Bounds.Left)
                .ThenBy(item => item.Item.Order);

            return candidates.Select(item => item.Item).FirstOrDefault();
        }

        private IEnumerable<TextItem> QueryAround(PlanPoint point, double radius) =>
            Query(PlanRect.FromEdges(point.X - radius, point.Y - radius, point.X + radius, point.Y + radius));

        private IEnumerable<TextItem> Query(PlanRect search)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var minX = CoordinateFor(search.Left);
            var maxX = CoordinateFor(search.Right);
            var minY = CoordinateFor(search.Top);
            var maxY = CoordinateFor(search.Bottom);
            var yielded = new HashSet<string>(StringComparer.Ordinal);

            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    if (!_cells.TryGetValue(new Cell(x, y), out var bucket))
                    {
                        continue;
                    }

                    foreach (var item in bucket)
                    {
                        if (yielded.Add(item.SourceId) && search.Contains(item.Text.Bounds.Center))
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        private int CoordinateFor(double value) => (int)Math.Floor(value / _cellSize);

        private static Cell CellFor(PlanPoint point, double cellSize) =>
            new((int)Math.Floor(point.X / cellSize), (int)Math.Floor(point.Y / cellSize));

        private static PlanRect AxisLabelSearchBounds(GridAxisRun run, double maxDistance) =>
            run.Orientation == GridAxisOrientation.Horizontal
                ? PlanRect.FromEdges(
                    run.Start - maxDistance,
                    run.Coordinate - maxDistance,
                    run.End + maxDistance,
                    run.Coordinate + maxDistance)
                : PlanRect.FromEdges(
                    run.Coordinate - maxDistance,
                    run.Start - maxDistance,
                    run.Coordinate + maxDistance,
                    run.End + maxDistance);

        private readonly record struct Cell(int X, int Y);
    }

    private sealed record GridLineCandidate(
        PrimitiveLine Line,
        string PrimitiveId,
        GridAxisOrientation Orientation,
        string LayerName,
        Confidence LayerConfidence,
        bool HasLayerEvidence,
        bool RequiresEndpointLabel,
        bool HasBubbleEvidence,
        IReadOnlyList<string> Evidence)
    {
        public static GridLineCandidate? TryCreate(
            PrimitiveLine line,
            SheetRegion mainRegion,
            ScannerOptions options,
            GridLabelSpatialIndex labelIndex,
            GridLayerLookup layerLookup)
        {
            var orientation = line.Segment.IsHorizontal(options.GeometryTolerance.Distance)
                ? GridAxisOrientation.Horizontal
                : line.Segment.IsVertical(options.GeometryTolerance.Distance)
                    ? GridAxisOrientation.Vertical
                    : GridAxisOrientation.Unknown;

            if (orientation == GridAxisOrientation.Unknown)
            {
                return null;
            }

            if (!mainRegion.Bounds.Intersects(line.Segment.Bounds.Inflate(options.GridAxisMergeTolerance)))
            {
                return null;
            }

            var lineLength = line.Segment.Length;
            var layerResult = layerLookup.Find(line.Primitive);
            var layerName = layerResult.LayerName;
            var layer = layerResult.Summary;

            if (layer?.LikelyCategory == LayerCategory.Grid && layer.Confidence.Value >= StrongGridLayerConfidence)
            {
                return new GridLineCandidate(
                    line,
                    line.PrimitiveId,
                    orientation,
                    layer.Name,
                    layer.Confidence,
                    true,
                    false,
                    false,
                    new[]
                    {
                        $"grid layer {layer.Name} classified Grid ({layer.Confidence.Value:0.##})"
                    });
            }

            if (!CanInferFromEndpointLabel(layer)
                || lineLength < MinimumInferredAxisLineLength(orientation, mainRegion, options)
                || !TryFindEndpointLabel(line.Segment, labelIndex, options, out var label))
            {
                return null;
            }

            return new GridLineCandidate(
                line,
                line.PrimitiveId,
                orientation,
                layer?.Name ?? layerName,
                layer?.Confidence ?? Confidence.Low,
                false,
                true,
                label.BubbleSourceIds.Count > 0,
                InferredAxisEvidence(layer, layerName, label).ToArray());
        }

        private static double MinimumInferredAxisLineLength(
            GridAxisOrientation orientation,
            SheetRegion mainRegion,
            ScannerOptions options)
        {
            var pageSpan = orientation == GridAxisOrientation.Horizontal
                ? mainRegion.Bounds.Width
                : mainRegion.Bounds.Height;
            var finalAxisThreshold = Math.Max(options.MinGridAxisLength, pageSpan * options.MinGridAxisLengthRatio);

            return Math.Max(options.MinGridAxisLength * 0.5, finalAxisThreshold * 0.5);
        }

        private static bool CanInferFromEndpointLabel(LayerSummary? layer) =>
            layer is null
            || layer.Confidence.Value < StrongGridLayerConfidence
            || layer.LikelyCategory is LayerCategory.Unknown
                or LayerCategory.Grid
                or LayerCategory.Structural;

        private static bool TryFindEndpointLabel(
            PlanLineSegment line,
            GridLabelSpatialIndex labelIndex,
            ScannerOptions options,
            out TextItem label)
        {
            var maxDistance = Math.Max(1, options.MaxGridAxisLabelDistance);
            var candidate = labelIndex.FindNearestEndpointLabel(line, maxDistance);

            if (candidate is null)
            {
                label = null!;
                return false;
            }

            label = candidate;
            return true;
        }

        private static IEnumerable<string> InferredAxisEvidence(
            LayerSummary? layer,
            string layerName,
            TextItem label)
        {
            yield return $"inferred grid axis from endpoint label {label.Text.Text.Trim()}";

            if (label.BubbleSourceIds.Count > 0)
            {
                yield return $"endpoint label has grid bubble geometry ({label.BubbleSourceIds.Count} source primitive(s))";
            }

            if (layer is null)
            {
                yield return $"layer {layerName} had no strong category; endpoint label evidence was required";
            }
            else
            {
                yield return $"layer {layer.Name} classified {layer.LikelyCategory} ({layer.Confidence.Value:0.##}); endpoint label evidence was required";
            }
        }

    }

    private sealed class GridLayerLookup
    {
        private readonly PlanLayerAnalysis _analysis;
        private readonly Dictionary<LayerLookupKey, LayerSummary?> _cache = new();

        public GridLayerLookup(PlanLayerAnalysis analysis)
        {
            _analysis = analysis;
        }

        public LayerLookupResult Find(PlanPrimitive primitive)
        {
            var layerName = LayerNameFor(primitive);
            var sourceFormat = Clean(primitive.Source.SourceFormat);
            var key = new LayerLookupKey(
                layerName.ToLowerInvariant(),
                sourceFormat?.ToLowerInvariant());

            if (!_cache.TryGetValue(key, out var summary))
            {
                summary = _analysis.Find(layerName, sourceFormat)
                    ?? _analysis.Find(layerName);
                _cache[key] = summary;
            }

            return new LayerLookupResult(layerName, summary);
        }

        private static string LayerNameFor(PlanPrimitive primitive) =>
            Clean(primitive.Source.Layer)
            ?? Clean(primitive.Layer)
            ?? LayerAnalyzer.UnlayeredName;

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private readonly record struct LayerLookupKey(string LayerName, string? SourceFormat);

    private readonly record struct LayerLookupResult(string LayerName, LayerSummary? Summary);

    private sealed class GridAxisRun
    {
        private GridAxisRun(
            GridAxisOrientation orientation,
            double coordinate,
            double start,
            double end,
            string layerName,
            Confidence layerConfidence,
            bool hasLayerEvidence,
            bool requiresEndpointLabel,
            bool hasBubbleEvidence,
            IReadOnlyList<string> evidence,
            IReadOnlyList<string> sourcePrimitiveIds)
        {
            Orientation = orientation;
            Coordinate = coordinate;
            Start = start;
            End = end;
            LayerName = layerName;
            LayerConfidence = layerConfidence;
            HasLayerEvidence = hasLayerEvidence;
            RequiresEndpointLabel = requiresEndpointLabel;
            HasLabelInferenceEvidence = requiresEndpointLabel && !hasLayerEvidence;
            HasBubbleEvidence = hasBubbleEvidence;
            Evidence = evidence.ToList();
            SourcePrimitiveIds = sourcePrimitiveIds.ToList();
        }

        public GridAxisOrientation Orientation { get; }

        public double Coordinate { get; private set; }

        public double Start { get; private set; }

        public double End { get; private set; }

        public double Length => End - Start;

        public string LayerName { get; }

        public Confidence LayerConfidence { get; private set; }

        public bool HasLayerEvidence { get; private set; }

        public bool RequiresEndpointLabel { get; private set; }

        public bool HasLabelInferenceEvidence { get; private set; }

        public bool HasBubbleEvidence { get; private set; }

        public List<string> Evidence { get; }

        public int FragmentCount { get; private set; } = 1;

        public double TotalGapLength { get; private set; }

        public List<string> SourcePrimitiveIds { get; }

        public static GridAxisRun FromCandidate(GridLineCandidate candidate)
        {
            if (candidate.Orientation == GridAxisOrientation.Horizontal)
            {
                return new GridAxisRun(
                    candidate.Orientation,
                    (candidate.Line.Segment.Start.Y + candidate.Line.Segment.End.Y) / 2.0,
                    Math.Min(candidate.Line.Segment.Start.X, candidate.Line.Segment.End.X),
                    Math.Max(candidate.Line.Segment.Start.X, candidate.Line.Segment.End.X),
                    candidate.LayerName,
                    candidate.LayerConfidence,
                    candidate.HasLayerEvidence,
                    candidate.RequiresEndpointLabel,
                    candidate.HasBubbleEvidence,
                    candidate.Evidence,
                    new[] { candidate.PrimitiveId });
            }

            return new GridAxisRun(
                candidate.Orientation,
                (candidate.Line.Segment.Start.X + candidate.Line.Segment.End.X) / 2.0,
                Math.Min(candidate.Line.Segment.Start.Y, candidate.Line.Segment.End.Y),
                Math.Max(candidate.Line.Segment.Start.Y, candidate.Line.Segment.End.Y),
                candidate.LayerName,
                candidate.LayerConfidence,
                candidate.HasLayerEvidence,
                candidate.RequiresEndpointLabel,
                candidate.HasBubbleEvidence,
                candidate.Evidence,
                new[] { candidate.PrimitiveId });
        }

        public void Merge(GridAxisRun run)
        {
            var currentLength = End - Start;
            var nextLength = run.End - run.Start;
            var totalLength = Math.Max(1, currentLength + nextLength);
            var gap = Math.Max(0, Math.Max(run.Start - End, Start - run.End));

            Coordinate = ((Coordinate * currentLength) + (run.Coordinate * nextLength)) / totalLength;
            Start = Math.Min(Start, run.Start);
            End = Math.Max(End, run.End);
            LayerConfidence = new Confidence(Math.Max(LayerConfidence.Value, run.LayerConfidence.Value));
            HasLayerEvidence |= run.HasLayerEvidence;
            RequiresEndpointLabel &= run.RequiresEndpointLabel;
            HasLabelInferenceEvidence |= run.HasLabelInferenceEvidence;
            HasBubbleEvidence |= run.HasBubbleEvidence;
            Evidence.AddRange(run.Evidence);
            FragmentCount += run.FragmentCount;
            TotalGapLength += run.TotalGapLength + gap;
            SourcePrimitiveIds.AddRange(run.SourcePrimitiveIds);
        }
    }
}
