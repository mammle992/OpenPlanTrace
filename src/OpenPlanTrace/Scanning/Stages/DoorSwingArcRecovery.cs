namespace OpenPlanTrace;

internal enum DoorSwingArcRecoveryProfile
{
    OpeningDetection,
    WallNoiseRejection
}

internal static class DoorSwingArcRecovery
{
    public static bool TryRecoverFromPolyline(
        PolylinePrimitive polyline,
        ScannerOptions options,
        DoorSwingArcRecoveryProfile profile,
        out ArcPrimitive arc)
    {
        arc = default!;
        if (polyline.Closed || polyline.Points.Count < 4)
        {
            return false;
        }

        var points = polyline.Points
            .Where(point => IsFinite(point.X) && IsFinite(point.Y))
            .Aggregate(new List<PlanPoint>(), (accumulator, point) =>
            {
                if (accumulator.Count == 0 || accumulator[^1].DistanceTo(point) > 0.05)
                {
                    accumulator.Add(point);
                }

                return accumulator;
            });
        if (points.Count < 4)
        {
            return false;
        }

        var start = points[0];
        var middle = points[points.Count / 2];
        var end = points[^1];
        if (!TryFitCircle(start, middle, end, out var center, out var radius)
            || !RadiusMatchesProfile(radius, options, profile))
        {
            return false;
        }

        var maxRadialError = Math.Max(
            1.5,
            Math.Max(
                options.WallSnapTolerance * (profile == DoorSwingArcRecoveryProfile.OpeningDetection ? 1.5 : 2.0),
                radius * (profile == DoorSwingArcRecoveryProfile.OpeningDetection ? 0.10 : 0.14)));
        var radialError = points.Max(point => Math.Abs(point.DistanceTo(center) - radius));
        if (radialError > maxRadialError)
        {
            return false;
        }

        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var middleAngle = Math.Atan2(middle.Y - center.Y, middle.X - center.X);
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var sweep = ResolveSweepThroughMiddle(startAngle, middleAngle, endAngle);
        if (!SweepMatchesProfile(sweep, profile))
        {
            return false;
        }

        var chord = start.DistanceTo(end);
        if (chord < options.MinOpeningGap * 0.35
            || chord > options.MaxOpeningGap * (profile == DoorSwingArcRecoveryProfile.OpeningDetection ? 1.8 : 2.1))
        {
            return false;
        }

        var arcLength = Math.Abs(sweep) * radius;
        if (arcLength < options.MinOpeningGap * 0.5
            || arcLength > options.MaxOpeningGap * (profile == DoorSwingArcRecoveryProfile.OpeningDetection ? 2.2 : 2.6))
        {
            return false;
        }

        arc = new ArcPrimitive(center, radius, startAngle, sweep)
        {
            SourceId = polyline.SourceId,
            Layer = polyline.Layer,
            StrokeWidth = polyline.StrokeWidth,
            Source = polyline.Source
        };
        return true;
    }

    public static bool IsPlausibleDoorSwingArc(ArcPrimitive arc, ScannerOptions options)
    {
        var sweep = Math.Abs(arc.SweepAngleRadians);
        return arc.Radius >= Math.Max(1, options.MinOpeningGap * 0.35)
            && arc.Radius <= Math.Max(options.MaxOpeningGap * 1.75, options.MinWallLength * 3.0)
            && sweep >= Math.PI / 8.0
            && sweep <= Math.PI * 1.15;
    }

    private static bool RadiusMatchesProfile(
        double radius,
        ScannerOptions options,
        DoorSwingArcRecoveryProfile profile)
    {
        return profile == DoorSwingArcRecoveryProfile.OpeningDetection
            ? radius >= options.MinOpeningGap * 0.75 && radius <= options.MaxOpeningGap * 1.25
            : radius >= Math.Max(1, options.MinOpeningGap * 0.35)
                && radius <= Math.Max(options.MaxOpeningGap * 1.75, options.MinWallLength * 3.0);
    }

    private static bool SweepMatchesProfile(double sweep, DoorSwingArcRecoveryProfile profile)
    {
        var magnitude = Math.Abs(sweep);
        return profile == DoorSwingArcRecoveryProfile.OpeningDetection
            ? magnitude >= Math.PI / 6 && magnitude <= Math.PI * 1.25
            : magnitude >= Math.PI / 8 && magnitude <= Math.PI * 1.15;
    }

    private static bool TryFitCircle(
        PlanPoint a,
        PlanPoint b,
        PlanPoint c,
        out PlanPoint center,
        out double radius)
    {
        center = default;
        radius = 0;
        var d = (2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y))));
        if (Math.Abs(d) <= 0.001)
        {
            return false;
        }

        var a2 = (a.X * a.X) + (a.Y * a.Y);
        var b2 = (b.X * b.X) + (b.Y * b.Y);
        var c2 = (c.X * c.X) + (c.Y * c.Y);
        var ux = ((a2 * (b.Y - c.Y)) + (b2 * (c.Y - a.Y)) + (c2 * (a.Y - b.Y))) / d;
        var uy = ((a2 * (c.X - b.X)) + (b2 * (a.X - c.X)) + (c2 * (b.X - a.X))) / d;
        center = new PlanPoint(ux, uy);
        radius = center.DistanceTo(a);
        return IsFinite(radius) && radius > 0.001;
    }

    private static double ResolveSweepThroughMiddle(
        double startAngle,
        double middleAngle,
        double endAngle)
    {
        var counterClockwiseSweep = NormalizePositiveAngle(endAngle - startAngle);
        var middleCounterClockwise = NormalizePositiveAngle(middleAngle - startAngle);
        return middleCounterClockwise <= counterClockwiseSweep
            ? counterClockwiseSweep
            : -(Math.PI * 2 - counterClockwiseSweep);
    }

    private static double NormalizePositiveAngle(double angle)
    {
        var normalized = angle % (Math.PI * 2);
        return normalized < 0 ? normalized + (Math.PI * 2) : normalized;
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);
}
