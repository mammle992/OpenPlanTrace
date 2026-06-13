namespace OpenPlanTrace;

public abstract record PlanPrimitive
{
    public string? SourceId { get; init; }

    public string? Layer { get; init; }

    public double StrokeWidth { get; init; }

    public PrimitiveSourceMetadata Source { get; init; } = PrimitiveSourceMetadata.Empty;

    public abstract PlanPrimitiveKind Kind { get; }

    public abstract PlanRect Bounds { get; }
}
