namespace OpenPlanTrace;

public sealed record PlanDocument(
    string Id,
    IReadOnlyList<PlanPage> Pages)
{
    public PlanMetadata Metadata { get; init; } = new();
}
