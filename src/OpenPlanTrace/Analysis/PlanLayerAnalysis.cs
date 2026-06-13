namespace OpenPlanTrace;

public sealed record PlanLayerAnalysis(IReadOnlyList<LayerSummary> Layers)
{
    public static PlanLayerAnalysis Empty { get; } = new(Array.Empty<LayerSummary>());

    public LayerSummary? Find(string layerName, string? sourceFormat = null) =>
        Layers.FirstOrDefault(layer =>
            string.Equals(layer.Name, layerName, StringComparison.OrdinalIgnoreCase)
            && (sourceFormat is null || string.Equals(layer.SourceFormat, sourceFormat, StringComparison.OrdinalIgnoreCase)));
}
