namespace OpenPlanTrace;

public sealed record WallTopologyPreparation(
    IReadOnlyList<string> GraphWallIds,
    IReadOnlyList<WallTopologyRejectedWall> RejectedWalls,
    IReadOnlyList<string> AcceptedGraphWallIds,
    IReadOnlyList<string> ReviewGraphWallIds,
    IReadOnlyList<string> UnassessedGraphWallIds)
{
    public static WallTopologyPreparation Empty { get; } =
        new(
            Array.Empty<string>(),
            Array.Empty<WallTopologyRejectedWall>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>());

    public WallTopologyPreparation(
        IReadOnlyList<string> graphWallIds,
        IReadOnlyList<WallTopologyRejectedWall> rejectedWalls)
        : this(
            graphWallIds,
            rejectedWalls,
            Array.Empty<string>(),
            Array.Empty<string>(),
            graphWallIds)
    {
    }

    public bool HasPreparedSelection =>
        GraphWallIds.Count > 0 || RejectedWalls.Count > 0;

    public int GraphWallCount =>
        GraphWallIds.Count;

    public int AcceptedGraphWallCount =>
        AcceptedGraphWallIds.Count;

    public int ReviewGraphWallCount =>
        ReviewGraphWallIds.Count;

    public int UnassessedGraphWallCount =>
        UnassessedGraphWallIds.Count;

    public IReadOnlyList<string> AutomaticCoordinateRepairWallIds =>
        AcceptedGraphWallIds
            .Concat(UnassessedGraphWallIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    public int AutomaticCoordinateRepairWallCount =>
        AutomaticCoordinateRepairWallIds.Count;

    public int RejectedWallCount =>
        RejectedWallIds.Count;

    public IReadOnlyList<string> RejectedWallIds =>
        RejectedWalls
            .Select(wall => wall.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    public int DoorOrOpeningSymbolCount =>
        RejectedCount(WallEvidenceCategory.DoorOrOpeningSymbol);

    public int SurfacePatternDetailCount =>
        RejectedCount(WallEvidenceCategory.SurfacePatternDetail);

    public int DimensionOrAnnotationCount =>
        RejectedCount(WallEvidenceCategory.DimensionOrAnnotation);

    public int ObjectOrFixtureDetailCount =>
        RejectedCount(WallEvidenceCategory.ObjectOrFixtureDetail);

    public bool IsGraphWall(string wallId) =>
        GraphWallIds.Contains(wallId, StringComparer.Ordinal);

    public bool IsAcceptedGraphWall(string wallId) =>
        AcceptedGraphWallIds.Contains(wallId, StringComparer.Ordinal);

    public bool IsReviewGraphWall(string wallId) =>
        ReviewGraphWallIds.Contains(wallId, StringComparer.Ordinal);

    public bool IsUnassessedGraphWall(string wallId) =>
        UnassessedGraphWallIds.Contains(wallId, StringComparer.Ordinal);

    public bool AllowsAutomaticCoordinateRepair(string wallId) =>
        AutomaticCoordinateRepairWallIds.Contains(wallId, StringComparer.Ordinal);

    public int RejectedCount(WallEvidenceCategory category) =>
        RejectedWalls.Count(wall => wall.Category == category);
}

public sealed record WallTopologyRejectedWall(
    string WallId,
    int PageNumber,
    PlanRect Bounds,
    WallEvidenceCategory Category,
    WallEvidenceDecision Decision,
    bool RejectedAsNoise,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> Evidence);
