namespace OpenPlanTrace;

public sealed record RoomAdjacencyGraph(
    IReadOnlyList<RoomAdjacencyEdge> Edges,
    IReadOnlyList<RoomCluster> Clusters)
{
    public static RoomAdjacencyGraph Empty { get; } = new(
        Array.Empty<RoomAdjacencyEdge>(),
        Array.Empty<RoomCluster>());
}
