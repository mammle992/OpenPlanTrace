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

public sealed record WallEvidenceMap(
    IReadOnlyList<WallEvidenceSegment> Segments,
    IReadOnlyList<WallEvidenceBand> Bands,
    IReadOnlyList<WallEvidenceWallAssessment> WallAssessments)
{
    public static WallEvidenceMap Empty { get; } =
        new(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            Array.Empty<WallEvidenceWallAssessment>());

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

    public int PlacementReadyWallCount =>
        WallAssessments.Count(assessment => assessment.PlacementReady);

    public int ReviewWallCount =>
        WallAssessments.Count(assessment => assessment.RequiresReview);
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
    IReadOnlyList<string> Evidence);
