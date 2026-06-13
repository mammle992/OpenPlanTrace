namespace OpenPlanTrace;

public sealed record PlanCalibration(
    PlanMeasurementUnit DrawingUnit,
    PlanMeasurementUnit RealWorldUnit,
    double? ScaleRatio,
    double? MillimetersPerDrawingUnit,
    Confidence Confidence,
    IReadOnlyList<CalibrationEvidence> Evidence,
    IReadOnlyList<CalibrationScaleGroup> ScaleGroups)
{
    public static PlanCalibration Empty { get; } =
        new(
            PlanMeasurementUnit.Unknown,
            PlanMeasurementUnit.Unknown,
            null,
            null,
            Confidence.None,
            Array.Empty<CalibrationEvidence>(),
            Array.Empty<CalibrationScaleGroup>());

    public bool HasReliableMeasurementScale =>
        MillimetersPerDrawingUnit is > 0 && Confidence.Value >= 0.5;

    public double? ToMillimeters(double drawingUnits) =>
        MillimetersPerDrawingUnit is > 0 ? drawingUnits * MillimetersPerDrawingUnit.Value : null;

    public double? ToMillimeters(double drawingUnits, CalibrationScaleGroup? scaleGroup)
    {
        if (scaleGroup?.MillimetersPerDrawingUnit is > 0)
        {
            return drawingUnits * scaleGroup.MillimetersPerDrawingUnit.Value;
        }

        return HasReliableScaleGroups ? null : ToMillimeters(drawingUnits);
    }

    public double? ToMeters(double drawingUnits) =>
        ToMillimeters(drawingUnits) is { } millimeters ? millimeters / 1000.0 : null;

    public double? ToMeters(double drawingUnits, CalibrationScaleGroup? scaleGroup) =>
        ToMillimeters(drawingUnits, scaleGroup) is { } millimeters ? millimeters / 1000.0 : null;

    public double? ToSquareMeters(double drawingArea) =>
        MillimetersPerDrawingUnit is > 0
            ? drawingArea * MillimetersPerDrawingUnit.Value * MillimetersPerDrawingUnit.Value / 1_000_000.0
            : null;

    public double? ToSquareMeters(double drawingArea, CalibrationScaleGroup? scaleGroup)
    {
        if (scaleGroup?.MillimetersPerDrawingUnit is > 0)
        {
            var unit = scaleGroup.MillimetersPerDrawingUnit.Value;
            return drawingArea * unit * unit / 1_000_000.0;
        }

        return HasReliableScaleGroups ? null : ToSquareMeters(drawingArea);
    }

    public CalibrationScaleGroup? SelectMeasurementScaleGroup(
        int pageNumber,
        PlanRect bounds,
        string? sourceRegionId = null,
        CalibrationScaleScope preferredScope = CalibrationScaleScope.MainFloorPlan)
    {
        var candidates = ScaleGroups
            .Where(group => group.MillimetersPerDrawingUnit is > 0 && group.Confidence.Value >= 0.5)
            .Where(group => group.PageNumber is null || group.PageNumber == pageNumber)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var pageCandidates = candidates
            .Where(group => group.PageNumber == pageNumber)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(sourceRegionId))
        {
            var regionMatch = SelectUnambiguous(
                pageCandidates
                    .Where(group => group.SourceRegionIds.Contains(sourceRegionId, StringComparer.Ordinal))
                    .ToArray(),
                preferredScope);
            if (regionMatch is not null)
            {
                return regionMatch;
            }
        }

        if (!bounds.IsEmpty)
        {
            var spatialMatch = SelectUnambiguous(
                pageCandidates
                    .Where(group => group.Bounds is { } groupBounds
                        && (groupBounds.Contains(bounds.Center, 1) || groupBounds.Intersects(bounds, 1)))
                    .ToArray(),
                preferredScope);
            if (spatialMatch is not null)
            {
                return spatialMatch;
            }
        }

        var pageMatch = SelectSingle(
            pageCandidates
                .Where(group => CanUseAsPageFallback(group.Scope, preferredScope))
                .ToArray());
        if (pageMatch is not null)
        {
            return pageMatch;
        }

        return SelectSingle(
            candidates
                .Where(group => group.PageNumber is null || group.Scope == CalibrationScaleScope.Document)
                .ToArray());
    }

    private bool HasReliableScaleGroups =>
        ScaleGroups.Any(group => group.MillimetersPerDrawingUnit is > 0 && group.Confidence.Value >= 0.5);

    private static CalibrationScaleGroup? SelectUnambiguous(
        IReadOnlyList<CalibrationScaleGroup> candidates,
        CalibrationScaleScope preferredScope)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var preferred = candidates
            .Where(group => group.Scope == preferredScope)
            .ToArray();
        if (preferred.Length == 1)
        {
            return preferred[0];
        }

        var document = candidates
            .Where(group => group.Scope == CalibrationScaleScope.Document || group.PageNumber is null)
            .ToArray();
        return document.Length == 1 ? document[0] : null;
    }

    private static CalibrationScaleGroup? SelectSingle(IReadOnlyList<CalibrationScaleGroup> candidates) =>
        candidates.Count == 1 ? candidates[0] : null;

    private static bool CanUseAsPageFallback(CalibrationScaleScope scope, CalibrationScaleScope preferredScope) =>
        scope == preferredScope
        || scope is CalibrationScaleScope.Document
            or CalibrationScaleScope.TitleBlock
            or CalibrationScaleScope.MainFloorPlan
            or CalibrationScaleScope.Dimensions
            or CalibrationScaleScope.Page;
}
