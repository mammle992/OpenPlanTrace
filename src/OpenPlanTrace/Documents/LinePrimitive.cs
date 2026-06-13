namespace OpenPlanTrace;

public sealed record LinePrimitive(PlanLineSegment Segment) : PlanPrimitive
{
    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Line;

    public override PlanRect Bounds => Segment.Bounds;
}
