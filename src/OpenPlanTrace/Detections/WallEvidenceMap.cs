namespace OpenPlanTrace;

public enum WallEvidenceCategory
{
    Unknown = 0,
    StrongWallBody,
    MediumWallBody,
    WeakSingleLine,
    RecoveredWallBody,
    DoorOrOpeningSymbol,
    SurfacePatternDetail,
    DimensionOrAnnotation,
    ObjectOrFixtureDetail
}

public enum WallEvidenceDecision
{
    Unknown = 0,
    Accept,
    Review,
    Reject
}

public sealed record WallEvidenceScoreBreakdown(
    double PositiveScore,
    double NegativeScore,
    double DecisionScore,
    double PairSupportScore,
    double LayerSupportScore,
    double StructuralSupportScore,
    double RecoverySupportScore,
    double NoisePenalty,
    double FragmentReviewPenalty,
    IReadOnlyList<string> PositiveEvidence,
    IReadOnlyList<string> NegativeEvidence)
{
    public static WallEvidenceScoreBreakdown Empty { get; } =
        new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            Array.Empty<string>(),
            Array.Empty<string>());
}

public sealed record WallEvidenceMap(
    IReadOnlyList<WallEvidenceSegment> Segments,
    IReadOnlyList<WallEvidenceBand> Bands,
    IReadOnlyList<WallEvidenceWallAssessment> WallAssessments,
    int SourceCandidateWallCount = 0,
    int RecoveredCandidateWallCount = 0)
{
    public static WallEvidenceMap Empty { get; } =
        new(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            Array.Empty<WallEvidenceWallAssessment>());

    public int TotalCandidateWallCount =>
        SourceCandidateWallCount + RecoveredCandidateWallCount;

    public int StrongWallBodyCount =>
        WallAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.StrongWallBody);

    public int MediumWallBodyCount =>
        WallAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.MediumWallBody);

    public int WeakSingleLineCount =>
        WallAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.WeakSingleLine);

    public int RecoveredWallBodyCount =>
        WallAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.RecoveredWallBody);

    public int RejectedNoiseCount =>
        WallAssessments.Count(assessment => assessment.RejectedAsNoise);

    public int RejectedDoorOrOpeningSymbolCount =>
        RejectedCountByCategory(WallEvidenceCategory.DoorOrOpeningSymbol);

    public int RejectedSurfacePatternDetailCount =>
        RejectedCountByCategory(WallEvidenceCategory.SurfacePatternDetail);

    public int RejectedDimensionOrAnnotationCount =>
        RejectedCountByCategory(WallEvidenceCategory.DimensionOrAnnotation);

    public int RejectedObjectOrFixtureDetailCount =>
        RejectedCountByCategory(WallEvidenceCategory.ObjectOrFixtureDetail);

    public int AcceptedWallCount =>
        WallAssessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Accept);

    public int ReviewDecisionWallCount =>
        WallAssessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Review);

    public int RejectedWallCount =>
        WallAssessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Reject);

    public int PlacementReadyWallCount =>
        WallAssessments.Count(assessment => assessment.PlacementReady);

    public int ReviewWallCount =>
        WallAssessments.Count(assessment => assessment.RequiresReview);

    public IReadOnlyList<string> AcceptedWallIds =>
        WallIdsByDecision(WallEvidenceDecision.Accept);

    public IReadOnlyList<string> ReviewWallIds =>
        WallIdsByDecision(WallEvidenceDecision.Review);

    public IReadOnlyList<string> RejectedWallIds =>
        WallIdsByDecision(WallEvidenceDecision.Reject);

    public IReadOnlyList<string> RejectedDoorOrOpeningSymbolWallIds =>
        RejectedWallIdsByCategory(WallEvidenceCategory.DoorOrOpeningSymbol);

    public IReadOnlyList<string> RejectedSurfacePatternDetailWallIds =>
        RejectedWallIdsByCategory(WallEvidenceCategory.SurfacePatternDetail);

    public IReadOnlyList<string> RejectedDimensionOrAnnotationWallIds =>
        RejectedWallIdsByCategory(WallEvidenceCategory.DimensionOrAnnotation);

    public IReadOnlyList<string> RejectedObjectOrFixtureDetailWallIds =>
        RejectedWallIdsByCategory(WallEvidenceCategory.ObjectOrFixtureDetail);

    private IReadOnlyList<string> WallIdsByDecision(WallEvidenceDecision decision) =>
        WallAssessments
            .Where(assessment => assessment.Decision == decision)
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    private int RejectedCountByCategory(WallEvidenceCategory category) =>
        WallAssessments.Count(assessment => assessment.RejectedAsNoise && assessment.Category == category);

    private IReadOnlyList<string> RejectedWallIdsByCategory(WallEvidenceCategory category) =>
        WallAssessments
            .Where(assessment => assessment.RejectedAsNoise && assessment.Category == category)
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
}

public sealed record WallEvidenceSegment(
    string Id,
    int PageNumber,
    PlanLineSegment Line,
    PlanRect Bounds,
    WallEvidenceCategory Category,
    Confidence Confidence,
    string? WallId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record WallEvidenceBand(
    string Id,
    int PageNumber,
    PlanLineSegment FirstFaceLine,
    PlanLineSegment SecondFaceLine,
    PlanLineSegment CenterLine,
    double FaceSeparation,
    double OverlapRatio,
    Confidence Confidence,
    string? WallId,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);

public sealed record WallEvidenceWallAssessment(
    string WallId,
    int PageNumber,
    PlanRect Bounds,
    WallEvidenceCategory Category,
    Confidence Confidence,
    bool PlacementReady,
    bool RequiresReview,
    bool RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence)
{
    public WallEvidenceDecision Decision { get; init; } = WallEvidenceDecision.Review;

    public WallEvidenceScoreBreakdown ScoreBreakdown { get; init; } = WallEvidenceScoreBreakdown.Empty;
}
