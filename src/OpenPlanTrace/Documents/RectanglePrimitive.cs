namespace OpenPlanTrace;

public sealed record RectanglePrimitive(PlanRect Rectangle) : PlanPrimitive
{
    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Rectangle;

    public override PlanRect Bounds => Rectangle;
}
