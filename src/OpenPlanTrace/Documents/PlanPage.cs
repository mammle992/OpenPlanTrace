namespace OpenPlanTrace;

public sealed record PlanPage(
    int Number,
    PlanSize Size,
    IReadOnlyList<PlanPrimitive> Primitives)
{
    public PlanRect Bounds => new(0, 0, Size.Width, Size.Height);

    public IEnumerable<LinePrimitive> Lines => Primitives.OfType<LinePrimitive>();

    public IEnumerable<TextPrimitive> Text => Primitives.OfType<TextPrimitive>();

    public IEnumerable<ArcPrimitive> Arcs => Primitives.OfType<ArcPrimitive>();
}
