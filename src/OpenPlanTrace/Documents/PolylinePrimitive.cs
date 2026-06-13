namespace OpenPlanTrace;

public sealed record PolylinePrimitive(IReadOnlyList<PlanPoint> Points, bool Closed = false) : PlanPrimitive
{
    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Polyline;

    public override PlanRect Bounds => Points.Count == 0
        ? PlanRect.Empty
        : PlanRect.Union(Points.Select(point => new PlanRect(point.X, point.Y, 0.001, 0.001)));
}
