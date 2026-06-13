namespace OpenPlanTrace;

public sealed record LayerSummary(
    string Name,
    string? SourceFormat,
    int EntityCount,
    IReadOnlyDictionary<PlanPrimitiveKind, int> PrimitiveKindCounts,
    double TotalLineLength,
    PlanRect Bounds,
    LayerCategory LikelyCategory,
    Confidence Confidence,
    IReadOnlyList<LayerCategoryScore> CategoryScores,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<int> PageNumbers);
