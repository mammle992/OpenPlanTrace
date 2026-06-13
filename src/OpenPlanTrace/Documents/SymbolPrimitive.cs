namespace OpenPlanTrace;

public sealed record SymbolPrimitive(string Name, PlanRect SymbolBounds) : PlanPrimitive
{
    public override PlanPrimitiveKind Kind => PlanPrimitiveKind.Symbol;

    public override PlanRect Bounds => SymbolBounds;
}
