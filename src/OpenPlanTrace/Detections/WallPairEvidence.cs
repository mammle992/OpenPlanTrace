namespace OpenPlanTrace;

public sealed record WallPairEvidence(
    PlanLineSegment FirstFaceLine,
    PlanLineSegment SecondFaceLine,
    double FaceSeparation,
    double OverlapRatio,
    double Score,
    int FirstFaceFragmentCount,
    int SecondFaceFragmentCount,
    IReadOnlyList<string> FirstFaceSourcePrimitiveIds,
    IReadOnlyList<string> SecondFaceSourcePrimitiveIds);
