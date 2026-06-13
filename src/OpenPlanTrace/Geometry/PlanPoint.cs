namespace OpenPlanTrace;

public readonly record struct PlanPoint(double X, double Y)
{
    public double DistanceTo(PlanPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public PlanPoint Translate(double dx, double dy) => new(X + dx, Y + dy);

    public static PlanPoint operator +(PlanPoint point, PlanVector vector) =>
        new(point.X + vector.X, point.Y + vector.Y);

    public static PlanVector operator -(PlanPoint left, PlanPoint right) =>
        new(left.X - right.X, left.Y - right.Y);
}
