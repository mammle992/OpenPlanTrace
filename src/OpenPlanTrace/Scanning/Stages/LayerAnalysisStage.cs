namespace OpenPlanTrace;

internal sealed class LayerAnalysisStage : IPipelineStage
{
    public string Name => "layer-analysis";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        context.LayerAnalysis = LayerAnalyzer.Analyze(context.Document, context.Options.LayerCategoryOverrides);

        if (context.Document.Pages.Sum(page => page.Primitives.Count) > 0
            && context.LayerAnalysis.Layers.Count == 0)
        {
            context.AddDiagnostic(
                "layers.none",
                DiagnosticSeverity.Info,
                Name,
                "No source layers were available for analysis.",
                confidence: Confidence.Low,
                scope: DiagnosticScope.Document,
                properties: new Dictionary<string, string>
                {
                    ["primitiveCount"] = context.Document.Pages.Sum(page => page.Primitives.Count).ToString(),
                    ["pageCount"] = context.Document.Pages.Count.ToString()
                });
        }

        return ValueTask.CompletedTask;
    }
}
