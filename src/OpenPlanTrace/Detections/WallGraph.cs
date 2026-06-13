namespace OpenPlanTrace;

public sealed record WallGraph(
    IReadOnlyList<WallNode> Nodes,
    IReadOnlyList<WallEdge> Edges,
    IReadOnlyList<WallGraphComponent> Components,
    IReadOnlyList<WallGraphRepairCandidate> RepairCandidates)
{
    public WallGraph(IReadOnlyList<WallNode> nodes, IReadOnlyList<WallEdge> edges)
        : this(nodes, edges, Array.Empty<WallGraphComponent>(), Array.Empty<WallGraphRepairCandidate>())
    {
    }

    public WallGraph(
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyList<WallGraphComponent> components)
        : this(nodes, edges, components, Array.Empty<WallGraphRepairCandidate>())
    {
    }

    public static WallGraph Empty { get; } = new(
        Array.Empty<WallNode>(),
        Array.Empty<WallEdge>(),
        Array.Empty<WallGraphComponent>(),
        Array.Empty<WallGraphRepairCandidate>());
}
