namespace OpenPlanTrace;

public readonly record struct PlanLineSegment(PlanPoint Start, PlanPoint End)
{
    public double Length => Start.DistanceTo(End);

    public PlanRect Bounds => PlanRect.FromPoints(Start, End);

    public PlanVector Vector => End - Start;

    public double AngleRadians => Math.Atan2(End.Y - Start.Y, End.X - Start.X);

    public bool IsHorizontal(double tolerance = 1.5) => Math.Abs(Start.Y - End.Y) <= tolerance;

    public bool IsVertical(double tolerance = 1.5) => Math.Abs(Start.X - End.X) <= tolerance;

    public PlanPoint Midpoint => new((Start.X + End.X) / 2.0, (Start.Y + End.Y) / 2.0);

    public PlanLineSegment Reverse() => new(End, Start);

    public PlanPoint PointAt(double t) =>
        new(Start.X + ((End.X - Start.X) * t), Start.Y + ((End.Y - Start.Y) * t));

    public double ProjectParameter(PlanPoint point)
    {
        var vector = Vector;
        var lengthSquared = (vector.X * vector.X) + (vector.Y * vector.Y);
        if (lengthSquared <= double.Epsilon)
        {
            return 0;
        }

        var toPoint = point - Start;
        return ((toPoint.X * vector.X) + (toPoint.Y * vector.Y)) / lengthSquared;
    }

    public double DistanceToPoint(PlanPoint point)
    {
        var t = Math.Clamp(ProjectParameter(point), 0, 1);
        return PointAt(t).DistanceTo(point);
    }
}
