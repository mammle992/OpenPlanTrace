namespace OpenPlanTrace;

public static class GeometryOperations
{
    public static bool NearlyEquals(double left, double right, double tolerance) =>
        Math.Abs(left - right) <= tolerance;

    public static bool TryIntersect(
        PlanLineSegment first,
        PlanLineSegment second,
        double tolerance,
        out PlanPoint point)
    {
        var p = first.Start;
        var q = second.Start;
        var r = first.End - first.Start;
        var s = second.End - second.Start;
        var denominator = r.Cross(s);

        if (Math.Abs(denominator) <= tolerance * 0.001)
        {
            point = default;
            return false;
        }

        var qMinusP = q - p;
        var t = qMinusP.Cross(s) / denominator;
        var u = qMinusP.Cross(r) / denominator;
        var firstParameterTolerance = tolerance / Math.Max(first.Length, 1);
        var secondParameterTolerance = tolerance / Math.Max(second.Length, 1);

        if (t < -firstParameterTolerance
            || t > 1 + firstParameterTolerance
            || u < -secondParameterTolerance
            || u > 1 + secondParameterTolerance)
        {
            point = default;
            return false;
        }

        point = first.PointAt(Math.Clamp(t, 0, 1));
        return true;
    }

    public static double NormalizeAngleRadians(double angle)
    {
        while (angle < 0)
        {
            angle += Math.PI;
        }

        while (angle >= Math.PI)
        {
            angle -= Math.PI;
        }

        return angle;
    }
}
