namespace OpenPlanTrace;

public readonly record struct PlanRect(double X, double Y, double Width, double Height)
{
    public static PlanRect Empty { get; } = new(0, 0, -1, -1);

    public double Left => X;

    public double Top => Y;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    public PlanPoint Center => new(X + (Width / 2.0), Y + (Height / 2.0));

    public bool IsEmpty => Width < 0 || Height < 0;

    public bool Contains(PlanPoint point, double tolerance = 0) =>
        point.X >= Left - tolerance
        && point.X <= Right + tolerance
        && point.Y >= Top - tolerance
        && point.Y <= Bottom + tolerance;

    public bool Contains(PlanRect rect, double tolerance = 0) =>
        Contains(new PlanPoint(rect.Left, rect.Top), tolerance)
        && Contains(new PlanPoint(rect.Right, rect.Bottom), tolerance);

    public bool Intersects(PlanRect rect, double tolerance = 0) =>
        rect.Left <= Right + tolerance
        && rect.Right >= Left - tolerance
        && rect.Top <= Bottom + tolerance
        && rect.Bottom >= Top - tolerance;

    public PlanRect Inflate(double amount) => Inflate(amount, amount);

    public PlanRect Inflate(double xAmount, double yAmount) =>
        new(X - xAmount, Y - yAmount, Width + (xAmount * 2), Height + (yAmount * 2));

    public PlanRect ClampTo(PlanRect bounds)
    {
        var left = Math.Max(Left, bounds.Left);
        var top = Math.Max(Top, bounds.Top);
        var right = Math.Min(Right, bounds.Right);
        var bottom = Math.Min(Bottom, bounds.Bottom);
        return FromEdges(left, top, right, bottom);
    }

    public double OverlapArea(PlanRect rect)
    {
        var intersection = Intersection(rect);
        return intersection.Area;
    }

    public PlanRect Intersection(PlanRect rect)
    {
        var left = Math.Max(Left, rect.Left);
        var top = Math.Max(Top, rect.Top);
        var right = Math.Min(Right, rect.Right);
        var bottom = Math.Min(Bottom, rect.Bottom);
        return FromEdges(left, top, right, bottom);
    }

    public static PlanRect FromEdges(double left, double top, double right, double bottom)
    {
        if (right < left || bottom < top)
        {
            return Empty;
        }

        return new PlanRect(left, top, right - left, bottom - top);
    }

    public static PlanRect FromPoints(PlanPoint first, PlanPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X, second.X);
        var bottom = Math.Max(first.Y, second.Y);
        return FromEdges(left, top, right, bottom);
    }

    public static PlanRect Union(PlanRect first, PlanRect second)
    {
        if (first.IsEmpty)
        {
            return second;
        }

        if (second.IsEmpty)
        {
            return first;
        }

        return FromEdges(
            Math.Min(first.Left, second.Left),
            Math.Min(first.Top, second.Top),
            Math.Max(first.Right, second.Right),
            Math.Max(first.Bottom, second.Bottom));
    }

    public static PlanRect Union(IEnumerable<PlanRect> rects)
    {
        var result = Empty;
        foreach (var rect in rects)
        {
            result = Union(result, rect);
        }

        return result;
    }
}
