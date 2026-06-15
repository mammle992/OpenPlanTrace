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
        var executionPlan = PipelineExecutionPlan.FromStages(_stages.Select(stage => stage.Metadata));
        context.Diagnostics.SetExecutionPlan(executionPlan);
        AddPipelinePlanDiagnostics(context, executionPlan);

        foreach (var stage in _stages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var metadata = stage.Metadata;
            var inputCount = context.TotalDetectionCount;
            var artifactCountsBefore = context.ArtifactCounts();
            var inputArtifacts = context.SnapshotArtifacts(metadata.Reads.Concat(metadata.OptionalReads));
            var diagnosticStart = context.Diagnostics.MessageCount;
            var stopwatch = Stopwatch.StartNew();
            progress?.Report(new PipelineStageProgress(
                metadata.Stage,
                PipelineStageProgressKind.Started,
                TimeSpan.Zero,
                inputCount,
                inputCount,
                0));
            await stage.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            var preliminaryArtifactCountsAfter = context.ArtifactCounts();
            var preliminaryChangedArtifacts = ScanContext.ChangedArtifacts(
                artifactCountsBefore,
                preliminaryArtifactCountsAfter);
            var preliminaryContract = PipelineStageContract.From(
                metadata.Writes,
                preliminaryChangedArtifacts.Select(change => change.Artifact));
            AddPipelineStageContractDiagnostic(context, metadata, preliminaryContract);

            var artifactCountsAfter = context.ArtifactCounts();
            var changedArtifacts = ScanContext.ChangedArtifacts(artifactCountsBefore, artifactCountsAfter);
            var artifactDeltas = ScanContext.ArtifactDeltas(
                artifactCountsBefore,
                artifactCountsAfter,
                metadata.Writes,
                changedArtifacts.Select(change => change.Artifact));
            var stageDiagnostics = context.Diagnostics.MessagesSince(diagnosticStart);
            progress?.Report(new PipelineStageProgress(
                metadata.Stage,
                PipelineStageProgressKind.Completed,
                stopwatch.Elapsed,
                inputCount,
                context.TotalDetectionCount,
                stageDiagnostics.Count));
            context.Diagnostics.AddStageReport(
                new PipelineStageReport(
                    metadata.Stage,
                    stopwatch.Elapsed,
                    inputCount,
                    context.TotalDetectionCount,
                    stageDiagnostics.Count,
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Info),
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Warning),
                    stageDiagnostics.Count(message => message.Severity == DiagnosticSeverity.Error),
                    metadata,
                    inputArtifacts,
                    context.SnapshotArtifacts(metadata.Writes),
                    changedArtifacts,
                    artifactDeltas));
        }

        context.Diagnostics.SetArtifactInventory(
            context.SnapshotArtifacts(Enum.GetValues<PlanArtifactKind>()));

        return context.ToResult();
    }

    private static void AddPipelineStageContractDiagnostic(
        ScanContext context,
        PipelineStageMetadata metadata,
        PipelineStageContract contract)
    {
        if (contract.WritesOnlyDeclaredArtifacts)
        {
            return;
        }

        context.AddDiagnostic(
            "pipeline.stage.undeclared_artifact_change",
            DiagnosticSeverity.Warning,
            metadata.Stage,
            "Pipeline stage changed artifacts outside its declared write contract.",
            confidence: Confidence.High,
            scope: DiagnosticScope.Document,
            properties: new Dictionary<string, string>
            {
                ["declaredWrites"] = string.Join(",", contract.DeclaredWrites.Select(artifact => artifact.ToString())),
                ["changedArtifacts"] = string.Join(",", contract.ChangedArtifacts.Select(artifact => artifact.ToString())),
                ["undeclaredChangedArtifacts"] = string.Join(",", contract.UndeclaredChangedArtifacts.Select(artifact => artifact.ToString()))
            });
    }

    private static void AddPipelinePlanDiagnostics(ScanContext context, PipelineExecutionPlan executionPlan)
    {
        foreach (var issue in executionPlan.Issues)
        {
            context.AddDiagnostic(
                issue.Code,
                issue.Severity,
                issue.Stage,
                issue.Message,
                confidence: Confidence.High,
                scope: DiagnosticScope.Document,
                properties: new Dictionary<string, string>
                {
                    ["artifacts"] = string.Join(",", issue.Artifacts.Select(artifact => artifact.ToString())),
                    ["executionModel"] = executionPlan.ExecutionModel
                });
        }
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
            new WallEvidenceRefinementStage(),
            new WallGraphStage(),
            new OpeningDetectionStage(),
            new RoomDetectionStage(),
            new RoomAdjacencyStage(),
            new WallTypeRefinementStage(),
            new MeasurementScaleProvenanceStage(),
            new ObjectCandidateStage(),
            new ObjectGroupingStage(),
            new ObjectAggregationStage(),
            new RoutingLayerStage(),
            new VisualAiClassificationStage(),
            new LayerConsistencyStage()
        };
}
