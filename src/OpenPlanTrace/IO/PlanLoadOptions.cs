namespace OpenPlanTrace;

public sealed record PlanLoadOptions
{
    public bool ExtractText { get; init; } = true;

    public bool ExtractVectorGeometry { get; init; } = true;

    public bool NormalizeToTopLeftOrigin { get; init; } = true;
}
