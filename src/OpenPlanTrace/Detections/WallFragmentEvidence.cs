namespace OpenPlanTrace;

public sealed record WallFragmentEvidence(
    int FragmentCount,
    double TotalHealedGap,
    double MaxHealedGap,
    int DuplicatePrimitiveCount,
    double GapRatio,
    bool RequiresGeometryReview,
    IReadOnlyList<string> Evidence);
