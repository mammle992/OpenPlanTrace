namespace OpenPlanTrace;

public sealed record ArcPrimitive(
    PlanPoint Center,
    double Radius,
    double StartAngleRadians,
    double SweepAngleRadians) : PlanPrimitive
{
    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Arc;

    public override PlanRect Bounds => new(
        Center.X - Radius,
        Center.Y - Radius,
        Radius * 2,
        Radius * 2);
}
