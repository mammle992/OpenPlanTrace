namespace OpenPlanTrace;

public sealed record WallPlacementReadiness(
    bool ReadyForCoordinatePlacement,
    bool ReadyForMetricPlacement,
    bool RequiresReview,
    Confidence Confidence,
    bool CoordinatePlacementBlocked,
    IReadOnlyList<string> Reasons);
