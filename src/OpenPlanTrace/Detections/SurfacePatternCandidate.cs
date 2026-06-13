namespace OpenPlanTrace;

public enum SurfacePatternKind
{
    DenseOrthogonalGrid,
    DenseParallelBand
}

public enum SurfacePatternOrientation
{
    Unknown,
    Orthogonal,
    Horizontal,
    Vertical
}

public sealed record SurfacePatternCandidate(
    string Id,
    int PageNumber,
    SurfacePatternKind Kind,
    SurfacePatternOrientation Orientation,
    PlanRect Bounds,
    string? SourceRegionId,
    int LineCount,
    int HorizontalLineCount,
    int VerticalLineCount,
    int IntersectionCount,
    double? HorizontalMedianSpacing,
    double? VerticalMedianSpacing,
    double? MedianSpacing,
    bool ExcludedFromWallDetection,
    bool ExcludedFromStructuralTopology,
    IReadOnlyList<string> SourcePrimitiveIds,
    Confidence Confidence,
    bool RequiresReview,
    IReadOnlyList<string> Evidence);
