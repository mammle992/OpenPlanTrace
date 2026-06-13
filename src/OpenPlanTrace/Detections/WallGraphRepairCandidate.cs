namespace OpenPlanTrace;

public enum WallGraphRepairCandidateKind
{
    EndpointToWall,
    EndpointToEndpoint
}

public enum WallGraphRepairAction
{
    SnapEndpointToWall,
    SnapEndpointToEndpoint
}

public sealed record WallGraphRepairCandidate(
    string Id,
    int PageNumber,
    WallGraphRepairCandidateKind Kind,
    WallGraphRepairAction SuggestedAction,
    string SourceNodeId,
    PlanPoint SourcePoint,
    PlanPoint TargetPoint,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistance,
    PlanLineSegment RepairLine,
    PlanRect Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    Confidence Confidence,
    bool RequiresReview,
    IReadOnlyList<string> Evidence);
