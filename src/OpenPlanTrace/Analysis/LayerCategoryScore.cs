namespace OpenPlanTrace;

public sealed record LayerCategoryScore(
    LayerCategory Category,
    double Score,
    IReadOnlyList<string> Evidence);
