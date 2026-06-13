using System.Globalization;

namespace OpenPlanTrace;

internal sealed class MeasurementScaleProvenanceStage : IPipelineStage
{
    private const string StageName = "measurement-scale-provenance";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var scaleGroups = ComparableScaleGroups(context.Calibration, page.Number);
            if (!HasConflictingScaleGroups(scaleGroups))
            {
                continue;
            }

            var unassigned = UnassignedMeasuredDetections(context, page.Number).ToArray();
            if (unassigned.Length == 0)
            {
                continue;
            }

            context.AddDiagnostic(
                "measurement_scale.unassigned_detections",
                DiagnosticSeverity.Warning,
                StageName,
                "Measured detections on a mixed-scale page could not be assigned to one scale group, so real-world measurements were left unset for review.",
                page.Number,
                PlanRect.Union(unassigned.Select(item => item.Bounds)),
                Confidence.Medium,
                DiagnosticScope.Calibration,
                unassigned.SelectMany(item => item.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["pageNumber"] = page.Number.ToString(CultureInfo.InvariantCulture),
                    ["scaleGroupCount"] = scaleGroups.Length.ToString(CultureInfo.InvariantCulture),
                    ["scaleGroupIds"] = string.Join(",", scaleGroups.Select(group => group.Id)),
                    ["scopes"] = string.Join(",", scaleGroups.Select(group => group.Scope).Distinct().OrderBy(scope => scope.ToString())),
                    ["sourceRegionIds"] = string.Join(",", scaleGroups.SelectMany(group => group.SourceRegionIds).Distinct(StringComparer.Ordinal)),
                    ["unassignedMeasuredDetectionCount"] = unassigned.Length.ToString(CultureInfo.InvariantCulture),
                    ["unassignedWallCount"] = CountKind(unassigned, "wall"),
                    ["unassignedRoomCount"] = CountKind(unassigned, "room"),
                    ["unassignedOpeningCount"] = CountKind(unassigned, "opening"),
                    ["unassignedGridBayCount"] = CountKind(unassigned, "gridBay"),
                    ["sampleDetectionIds"] = SampleIds(unassigned),
                    ["scaleRatios"] = string.Join(",", scaleGroups.Select(group => group.ScaleRatio).Where(value => value is > 0).Select(value => Format(value!.Value)).Distinct()),
                    ["millimetersPerDrawingUnitValues"] = string.Join(",", scaleGroups.Select(group => group.MillimetersPerDrawingUnit).Where(value => value is > 0).Select(value => Format(value!.Value)).Distinct())
                });
        }

        return ValueTask.CompletedTask;
    }

    private static CalibrationScaleGroup[] ComparableScaleGroups(PlanCalibration calibration, int pageNumber) =>
        calibration.ScaleGroups
            .Where(group => group.PageNumber == pageNumber)
            .Where(group => group.MillimetersPerDrawingUnit is > 0 || group.ScaleRatio is > 0)
            .ToArray();

    private static bool HasConflictingScaleGroups(IReadOnlyList<CalibrationScaleGroup> scaleGroups)
    {
        if (scaleGroups.Count < 2)
        {
            return false;
        }

        var measured = scaleGroups
            .Where(group => group.MillimetersPerDrawingUnit is > 0)
            .Select(group => group.MillimetersPerDrawingUnit!.Value)
            .ToArray();
        var ratios = scaleGroups
            .Where(group => group.ScaleRatio is > 0)
            .Select(group => group.ScaleRatio!.Value)
            .ToArray();
        var values = measured.Length >= 2 ? measured : ratios;
        return values.Length >= 2 && values.Max() / values.Min() > 1.05;
    }

    private static IEnumerable<UnassignedMeasuredDetection> UnassignedMeasuredDetections(
        ScanContext context,
        int pageNumber)
    {
        foreach (var wall in context.Walls.Where(wall => wall.PageNumber == pageNumber))
        {
            if (string.IsNullOrWhiteSpace(wall.MeasurementScaleGroupId)
                && (wall.LengthMeters is null || wall.ThicknessMillimeters is null))
            {
                yield return new UnassignedMeasuredDetection(
                    "wall",
                    wall.Id,
                    wall.Bounds,
                    wall.SourcePrimitiveIds);
            }
        }

        foreach (var room in context.Rooms.Where(room => room.PageNumber == pageNumber))
        {
            if (string.IsNullOrWhiteSpace(room.MeasurementScaleGroupId)
                && room.AreaSquareMeters is null)
            {
                yield return new UnassignedMeasuredDetection(
                    "room",
                    room.Id,
                    room.Bounds,
                    room.WallIds
                        .SelectMany(wallId => context.Walls.FirstOrDefault(wall => wall.Id == wallId)?.SourcePrimitiveIds
                            ?? Array.Empty<string>())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            }
        }

        foreach (var opening in context.Openings.Where(opening => opening.PageNumber == pageNumber))
        {
            if (string.IsNullOrWhiteSpace(opening.MeasurementScaleGroupId)
                && opening.WidthMillimeters is null)
            {
                yield return new UnassignedMeasuredDetection(
                    "opening",
                    opening.Id,
                    opening.Bounds,
                    opening.SourcePrimitiveIds);
            }
        }

        foreach (var bay in context.GridBaySpacings.Where(bay => bay.PageNumber == pageNumber))
        {
            if (string.IsNullOrWhiteSpace(bay.MeasurementScaleGroupId)
                && bay.DistanceMeters is null)
            {
                yield return new UnassignedMeasuredDetection(
                    "gridBay",
                    bay.Id,
                    bay.Bounds,
                    bay.SourcePrimitiveIds);
            }
        }
    }

    private static string CountKind(IReadOnlyList<UnassignedMeasuredDetection> detections, string kind) =>
        detections.Count(item => item.Kind == kind).ToString(CultureInfo.InvariantCulture);

    private static string SampleIds(IReadOnlyList<UnassignedMeasuredDetection> detections)
    {
        var ids = detections
            .Select(item => item.Id)
            .Take(12)
            .ToArray();
        return detections.Count > ids.Length
            ? string.Join(",", ids.Append("..."))
            : string.Join(",", ids);
    }

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);

    private sealed record UnassignedMeasuredDetection(
        string Kind,
        string Id,
        PlanRect Bounds,
        IReadOnlyList<string> SourcePrimitiveIds);
}
