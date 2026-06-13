using System.Diagnostics;

namespace OpenPlanTrace;

public sealed class OpenPlanTraceScanner : IFloorplanScanner
{
    private readonly IReadOnlyList<IPipelineStage> _stages;

    public OpenPlanTraceScanner()
        : this(CreateDefaultStages())
    {
    }

    internal OpenPlanTraceScanner(IReadOnlyList<IPipelineStage> stages)
    {
        _stages = stages;
    }

    public async ValueTask<PlanScanResult> ScanAsync(
        PlanDocument document,
        ScannerOptions? options = null,
        IProgress<PipelineStageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var context = new ScanContext(document, options ?? new ScannerOptions());

        foreach (var stage in _stages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inputCount = context.TotalDetectionCount;
            var diagnosticStart = context.Diagnostics.MessageCount;
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new PipelineStageProgress(
                stage.Name,
                PipelineStageProgressKind.Started,
                TimeSpan.Zero,
                inputCount,
                inputCount,
                0));
            await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var stageDiagnostics = context.Diagnostics.MessagesSince(diagnosticStart);
            progress?.Report(new PipelineStageProgress(
                stage.Name,
                PipelineStageProgressKind.Completed,
                stopwatch.Elapsed,
                inputCount,
                context.TotalDetectionCount,
                stageDiagnostics.Count));
            context.Diagnostics.AddStageReport(
                new PipelineStageReport(
                    stage.Name,
                    stopwatch.Elapsed,
                    inputCount,
                    context.TotalDetectionCount,
                    stageDiagnostics.Count,
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Info),
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Warning),
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Error)));
        }

        return context.ToResult();
    }

    private static IReadOnlyList<IPipelineStage> CreateDefaultStages() =>
        new IPipelineStage[]
        {
            new LayerAnalysisStage(),
            new RasterExtractionDiagnosticsStage(),
            new PdfImageDiagnosticsStage(),
            new SheetRegionDetectionStage(),
            new TitleBlockAnalysisStage(),
            new CalibrationStage(),
            new DimensionAnalysisStage(),
            new AnnotationAnalysisStage(),
            new GridAxisDetectionStage(),
            new GridBaySpacingStage(),
            new MeasurementConsistencyStage(),
            new DimensionChainConsistencyStage(),
            new WallDetectionStage(),
            new WallGraphStage(),
            new OpeningDetectionStage(),
            new RoomDetectionStage(),
            new RoomAdjacencyStage(),
            new MeasurementScaleProvenanceStage(),
            new ObjectCandidateStage(),
            new ObjectGroupingStage(),
            new ObjectAggregationStage(),
            new VisualAiClassificationStage(),
            new LayerConsistencyStage()
        };
}
