namespace OpenPlanTrace;

public enum WallGraphRepairCandidateKind
{
    EndpointToWall,
    EndpointToEndpoint,
    EndpointOverrun
}

public enum WallGraphRepairAction
{
    SnapEndpointToWall,
    SnapEndpointToEndpoint,
    TrimEndpointOverrun
}

public enum WallGraphRepairSeverity
{
    Low,
    Medium,
    High
}

public enum WallGraphRepairImportImpact
{
    TopologyReviewRequired,
    TopologyImportBlocked
}

public enum WallGraphRepairApplicability
{
    ReviewAndApplySuggestedSnap,
    ReviewAndApplySuggestedTrim,
    ManualCorrectionRecommended
}

public sealed record WallGraphRepairCandidate(
    string Id,
    int PageNumber,
    WallGraphRepairCandidateKind Kind,
    WallGraphRepairAction SuggestedAction,
    WallGraphRepairSeverity Severity,
    WallGraphRepairImportImpact ImportImpact,
    WallGraphRepairApplicability Applicability,
    string SourceNodeId,
    PlanPoint SourcePoint,
    PlanPoint TargetPoint,
    string? TargetNodeId,
    string? HostWallId,
    double GapDistance,
    double SafeSnapDistance,
    double ReviewDistanceLimit,
    double ExcessDistanceBeyondSafeSnap,
    PlanLineSegment RepairLine,
    PlanRect Bounds,
    IReadOnlyList<string> WallIds,
    IReadOnlyList<string> SourcePrimitiveIds,
    Confidence Confidence,
    bool RequiresReview,
    IReadOnlyList<string> Evidence);
