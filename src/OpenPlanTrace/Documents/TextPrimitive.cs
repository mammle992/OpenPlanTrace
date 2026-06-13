namespace OpenPlanTrace;

public sealed record TextPrimitive(string Text, PlanRect TextBounds) : PlanPrimitive
{
    public double FontSize { get; init; }

    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Text;

    public override PlanRect Bounds => TextBounds;
}
