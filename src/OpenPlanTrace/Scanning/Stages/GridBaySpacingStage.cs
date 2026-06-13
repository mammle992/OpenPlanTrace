namespace OpenPlanTrace;

internal sealed class GridBaySpacingStage : IPipelineStage
{
    public string Name => "grid-bays";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var pageGroup in context.GridAxes.GroupBy(axis => axis.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageAxes = pageGroup
                .Where(axis => axis.Orientation is GridAxisOrientation.Horizontal or GridAxisOrientation.Vertical)
                .OrderBy(axis => axis.Orientation)
                .ThenBy(axis => axis.Coordinate)
                .ToArray();
            if (pageAxes.Length < 2)
            {
                continue;
            }

            var pageBays = new List<GridBaySpacing>();
            foreach (var orientationGroup in pageAxes.GroupBy(axis => axis.Orientation))
            {
                var axes = orientationGroup
                    .OrderBy(axis => axis.Coordinate)
                    .ThenBy(axis => axis.Label, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(axis => axis.Id, StringComparer.Ordinal)
                    .ToArray();
                if (axes.Length < 2)
                {
                    continue;
                }

                for (var index = 1; index < axes.Length; index++)
                {
                    var bay = CreateBay(
                        axes[index - 1],
                        axes[index],
                        context,
                        context.GridBaySpacings.Count + pageBays.Count + 1);
                    pageBays.Add(bay);
                }
            }

            if (pageBays.Count == 0)
            {
                continue;
            }

            context.GridBaySpacings.AddRange(pageBays);
            AddDetectedDiagnostics(pageGroup.Key, pageBays, context);
            AddIrregularSpacingDiagnostics(pageGroup.Key, pageBays, context);
        }

        return ValueTask.CompletedTask;
    }

    private static GridBaySpacing CreateBay(
        GridAxis first,
        GridAxis second,
        ScanContext context,
        int bayNumber)
    {
        var distance = Math.Abs(second.Coordinate - first.Coordinate);
        var line = first.Orientation == GridAxisOrientation.Vertical
            ? HorizontalSpacingLine(first, second)
            : VerticalSpacingLine(first, second);
        var confidence = new Confidence(Math.Clamp(
            ((first.Confidence.Value + second.Confidence.Value) / 2.0)
            + (LabelEvidenceBonus(first, second) * 0.04),
            0.35,
            0.94));
        var sourceRegionId = first.SourceRegionId ?? second.SourceRegionId;
        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(
            first.PageNumber,
            line.Bounds.Inflate(3),
            sourceRegionId);
        var distanceMeters = context.Calibration.ToMeters(distance, scaleGroup);
        var sourceIds = first.SourcePrimitiveIds
            .Concat(second.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<string>
        {
            $"adjacent {first.Orientation} grid axes {AxisLabel(first)} to {AxisLabel(second)}",
            $"grid bay spacing {Math.Round(distance, 3)} drawing units"
        };

        if (distanceMeters is > 0)
        {
            evidence.Add($"calibrated grid bay spacing {Math.Round(distanceMeters.Value, 3)} m");
        }

        if (!string.IsNullOrWhiteSpace(first.Label) && !string.IsNullOrWhiteSpace(second.Label))
        {
            evidence.Add("both grid axes have labels");
        }

        return new GridBaySpacing(
            $"page:{first.PageNumber}:grid-bay:{bayNumber}",
            first.PageNumber,
            first.Orientation,
            first.Id,
            first.Label,
            second.Id,
            second.Label,
            line,
            line.Bounds.Inflate(3),
            distance,
            distanceMeters,
            confidence,
            sourceRegionId,
            sourceIds,
            evidence)
        {
            MeasurementScaleGroupId = scaleGroup?.Id
        };
    }

    private static PlanLineSegment HorizontalSpacingLine(GridAxis first, GridAxis second)
    {
        var firstStart = Math.Min(first.Line.Start.Y, first.Line.End.Y);
        var firstEnd = Math.Max(first.Line.Start.Y, first.Line.End.Y);
        var secondStart = Math.Min(second.Line.Start.Y, second.Line.End.Y);
        var secondEnd = Math.Max(second.Line.Start.Y, second.Line.End.Y);
        var overlapStart = Math.Max(firstStart, secondStart);
        var overlapEnd = Math.Min(firstEnd, secondEnd);
        var y = overlapEnd > overlapStart
            ? (overlapStart + overlapEnd) / 2.0
            : (first.Line.Midpoint.Y + second.Line.Midpoint.Y) / 2.0;

        return new PlanLineSegment(
            new PlanPoint(first.Coordinate, y),
            new PlanPoint(second.Coordinate, y));
    }

    private static PlanLineSegment VerticalSpacingLine(GridAxis first, GridAxis second)
    {
        var firstStart = Math.Min(first.Line.Start.X, first.Line.End.X);
        var firstEnd = Math.Max(first.Line.Start.X, first.Line.End.X);
        var secondStart = Math.Min(second.Line.Start.X, second.Line.End.X);
        var secondEnd = Math.Max(second.Line.Start.X, second.Line.End.X);
        var overlapStart = Math.Max(firstStart, secondStart);
        var overlapEnd = Math.Min(firstEnd, secondEnd);
        var x = overlapEnd > overlapStart
            ? (overlapStart + overlapEnd) / 2.0
            : (first.Line.Midpoint.X + second.Line.Midpoint.X) / 2.0;

        return new PlanLineSegment(
            new PlanPoint(x, first.Coordinate),
            new PlanPoint(x, second.Coordinate));
    }

    private static double LabelEvidenceBonus(GridAxis first, GridAxis second) =>
        (!string.IsNullOrWhiteSpace(first.Label) ? 1 : 0)
        + (!string.IsNullOrWhiteSpace(second.Label) ? 1 : 0);

    private static string AxisLabel(GridAxis axis) =>
        string.IsNullOrWhiteSpace(axis.Label) ? axis.Id : axis.Label!;

    private static void AddDetectedDiagnostics(
        int pageNumber,
        IReadOnlyList<GridBaySpacing> bays,
        ScanContext context)
    {
        context.AddDiagnostic(
            "grid_bays.detected",
            DiagnosticSeverity.Info,
            "grid-bays",
            $"Detected {bays.Count} grid bay spacing(s).",
            pageNumber,
            PlanRect.Union(bays.Select(bay => bay.Bounds)),
            bays.Any(bay => bay.DistanceMeters is > 0) ? Confidence.High : Confidence.Medium,
            DiagnosticScope.Grid,
            bays.SelectMany(bay => bay.SourcePrimitiveIds),
            new Dictionary<string, string>
            {
                ["bayCount"] = bays.Count.ToString(),
                ["verticalAxisBayCount"] = bays.Count(bay => bay.AxisOrientation == GridAxisOrientation.Vertical).ToString(),
                ["horizontalAxisBayCount"] = bays.Count(bay => bay.AxisOrientation == GridAxisOrientation.Horizontal).ToString(),
                ["calibratedBayCount"] = bays.Count(bay => bay.DistanceMeters is > 0).ToString()
            });
    }

    private static void AddIrregularSpacingDiagnostics(
        int pageNumber,
        IReadOnlyList<GridBaySpacing> bays,
        ScanContext context)
    {
        foreach (var group in bays.GroupBy(bay => bay.AxisOrientation))
        {
            var distances = group
                .Select(bay => bay.DrawingDistance)
                .Where(distance => distance > 0)
                .OrderBy(distance => distance)
                .ToArray();
            if (distances.Length < 3)
            {
                continue;
            }

            var median = distances[distances.Length / 2];
            if (median <= 0)
            {
                continue;
            }

            var maxRelativeDeviation = distances
                .Select(distance => Math.Abs(distance - median) / median)
                .Max();
            if (maxRelativeDeviation < 0.20)
            {
                continue;
            }

            context.AddDiagnostic(
                "grid_bays.irregular_spacing",
                DiagnosticSeverity.Info,
                "grid-bays",
                $"{group.Key} grid bay spacings vary by more than 20% from the median.",
                pageNumber,
                PlanRect.Union(group.Select(bay => bay.Bounds)),
                Confidence.Medium,
                DiagnosticScope.Grid,
                group.SelectMany(bay => bay.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["axisOrientation"] = group.Key.ToString(),
                    ["bayCount"] = distances.Length.ToString(),
                    ["medianDrawingDistance"] = Math.Round(median, 3).ToString(),
                    ["minimumDrawingDistance"] = Math.Round(distances.Min(), 3).ToString(),
                    ["maximumDrawingDistance"] = Math.Round(distances.Max(), 3).ToString(),
                    ["maxRelativeDeviation"] = maxRelativeDeviation.ToString("0.###")
                });
        }
    }
}
