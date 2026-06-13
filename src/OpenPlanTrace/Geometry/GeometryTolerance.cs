namespace OpenPlanTrace;

public sealed record GeometryTolerance(
    double Distance = 1.5,
    double AngleDegrees = 2.0)
{
    public double AngleRadians => AngleDegrees * Math.PI / 180.0;
}
