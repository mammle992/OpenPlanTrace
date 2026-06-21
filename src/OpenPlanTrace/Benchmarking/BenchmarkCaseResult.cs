using System.Text.Json.Serialization;

namespace OpenPlanTrace;

public sealed record BenchmarkCaseResult(
    string FixtureId,
    string? FixtureName,
    string SourcePath,
    bool Passed,
    bool ScanSucceeded,
    double DurationMilliseconds,
    BenchmarkCounts Counts,
    IReadOnlyList<BenchmarkAssertionResult> Assertions,
    string? ErrorMessage)
{
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyList<BenchmarkDetectorMetrics> Metrics { get; init; } =
        Array.Empty<BenchmarkDetectorMetrics>();

    public IReadOnlyList<BenchmarkCaseIssueSummary> QualityIssues { get; init; } =
        Array.Empty<BenchmarkCaseIssueSummary>();

    public IReadOnlyList<BenchmarkCaseIssueSummary> DiagnosticIssues { get; init; } =
        Array.Empty<BenchmarkCaseIssueSummary>();

    public IReadOnlyList<BenchmarkPipelinePlanIssueSummary> PlanIssues { get; init; } =
        Array.Empty<BenchmarkPipelinePlanIssueSummary>();

    public IReadOnlyList<BenchmarkStageSummary> Stages { get; init; } =
        Array.Empty<BenchmarkStageSummary>();

    public IReadOnlyList<PipelineArtifactSnapshot> ArtifactInventory { get; init; } =
        Array.Empty<PipelineArtifactSnapshot>();

    public IReadOnlyList<BenchmarkArtifactPlanSummary> ArtifactPlans { get; init; } =
        Array.Empty<BenchmarkArtifactPlanSummary>();

    public IReadOnlyList<BenchmarkExecutionWaveSummary> ExecutionWaves { get; init; } =
        Array.Empty<BenchmarkExecutionWaveSummary>();

    public IReadOnlyList<BenchmarkRerunImpactSummary> RerunImpacts { get; init; } =
        Array.Empty<BenchmarkRerunImpactSummary>();

    public IReadOnlyList<BenchmarkRerunPlanSummary> RerunPlans { get; init; } =
        Array.Empty<BenchmarkRerunPlanSummary>();

    public BenchmarkPipelineHealthSummary PipelineHealth { get; init; } =
        BenchmarkPipelineHealthSummary.Empty;

    public PlanImportReadiness ImportReadiness { get; init; } =
        PlanImportReadiness.Empty;

    public BenchmarkWallPlacementSummary WallPlacement { get; init; } =
        BenchmarkWallPlacementSummary.Empty;

    public ScanReviewQueueSummary ScanReviewQueue { get; init; } =
        ScanReviewQueueSummary.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Skipped { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SkipReason { get; init; }

    public int PassedAssertionCount => Assertions.Count(assertion => assertion.Passed);

    public int FailedAssertionCount => Assertions.Count(assertion => !assertion.Passed);
}

public sealed record BenchmarkArtifactPlanSummary(
    string Artifact,
    bool IsSourceArtifact,
    bool IsProducedByStage,
    bool IsConsumedByStage,
    bool IsTerminalArtifact,
    IReadOnlyList<string> ProducerStages,
    IReadOnlyList<string> RequiredConsumerStages,
    IReadOnlyList<string> OptionalConsumerStages,
    IReadOnlyList<string> ConsumerStages,
    int ProducerCount,
    int ConsumerCount,
    int FirstProducerWave,
    int LastProducerWave,
    int FirstConsumerWave,
    int LastConsumerWave,
    bool HasMultipleProducers,
    bool HasRequiredConsumers,
    string DependencyRole,
    IReadOnlyList<string> Evidence)
{
    public static BenchmarkArtifactPlanSummary From(PipelineArtifactPlan plan) =>
        new(
            plan.Artifact.ToString(),
            plan.IsSourceArtifact,
            plan.IsProducedByStage,
            plan.IsConsumedByStage,
            plan.IsTerminalArtifact,
            plan.ProducerStages,
            plan.RequiredConsumerStages,
            plan.OptionalConsumerStages,
            plan.ConsumerStages,
            plan.ProducerCount,
            plan.ConsumerCount,
            plan.FirstProducerWave,
            plan.LastProducerWave,
            plan.FirstConsumerWave,
            plan.LastConsumerWave,
            plan.HasMultipleProducers,
            plan.HasRequiredConsumers,
            plan.DependencyRole,
            plan.Evidence);
}

public sealed record BenchmarkPipelinePlanIssueSummary(
    string Code,
    string Severity,
    string Stage,
    IReadOnlyList<string> Artifacts,
    string Message)
{
    public static BenchmarkPipelinePlanIssueSummary From(PipelinePlanIssue issue) =>
        new(
            issue.Code,
            issue.Severity.ToString(),
            issue.Stage,
            issue.Artifacts.Select(artifact => artifact.ToString()).ToArray(),
            issue.Message);
}

public sealed record BenchmarkExecutionWaveSummary(
    int Level,
    int StageCount,
    IReadOnlyList<string> Stages,
    IReadOnlyList<string> Reads,
    IReadOnlyList<string> Writes,
    IReadOnlyList<string> DirectDownstreamStages,
    int DirectDownstreamStageCount,
    IReadOnlyList<string> DownstreamReadArtifacts,
    IReadOnlyList<string> WriteConflictArtifacts,
    int IntraWaveDependencyCount,
    bool HasWriteConflicts,
    bool HasIntraWaveDependencies,
    bool IsParallelCandidate,
    string ParallelReadiness,
    IReadOnlyList<string> SchedulingReasons,
    string RecommendedExecutionMode)
{
    public static BenchmarkExecutionWaveSummary From(PipelineExecutionWave wave) =>
        new(
            wave.Level,
            wave.StageCount,
            wave.Stages,
            wave.Reads.Select(artifact => artifact.ToString()).ToArray(),
            wave.Writes.Select(artifact => artifact.ToString()).ToArray(),
            wave.DirectDownstreamStages,
            wave.DirectDownstreamStageCount,
            wave.DownstreamReadArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            wave.WriteConflictArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            wave.IntraWaveDependencies.Count,
            wave.HasWriteConflicts,
            wave.HasIntraWaveDependencies,
            wave.IsParallelCandidate,
            wave.ParallelReadiness,
            wave.SchedulingReasons,
            wave.RecommendedExecutionMode);
}

public sealed record BenchmarkRerunImpactSummary(
    string Artifact,
    bool IsSourceArtifact,
    string ImpactScope,
    IReadOnlyList<string> ProducerStages,
    IReadOnlyList<string> DirectConsumerStages,
    IReadOnlyList<string> AffectedStages,
    IReadOnlyList<string> AffectedArtifacts,
    int FirstAffectedWave,
    int AffectedStageCount,
    bool HasImpact,
    IReadOnlyList<string> Evidence)
{
    public static BenchmarkRerunImpactSummary From(PipelineRerunImpact impact) =>
        new(
            impact.Artifact.ToString(),
            impact.IsSourceArtifact,
            impact.ImpactScope,
            impact.ProducerStages,
            impact.DirectConsumerStages,
            impact.AffectedStages,
            impact.AffectedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            impact.FirstAffectedWave,
            impact.AffectedStageCount,
            impact.HasImpact,
            impact.Evidence);
}

public sealed record BenchmarkRerunPlanSummary(
    string PlanId,
    string DisplayName,
    IReadOnlyList<string> ChangedArtifacts,
    IReadOnlyList<string> ChangedSourceArtifacts,
    IReadOnlyList<string> DirectConsumerStages,
    IReadOnlyList<string> RerunStages,
    IReadOnlyList<int> RerunWaves,
    IReadOnlyList<string> AffectedArtifacts,
    int FirstRerunWave,
    int LastRerunWave,
    int RerunStageCount,
    int AffectedArtifactCount,
    bool HasWork,
    string RecommendedExecutionMode,
    IReadOnlyList<string> Evidence)
{
    public static BenchmarkRerunPlanSummary From(PipelineRerunPlan plan) =>
        new(
            plan.PlanId,
            plan.DisplayName,
            plan.ChangedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.ChangedSourceArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.DirectConsumerStages,
            plan.RerunStages,
            plan.RerunWaves,
            plan.AffectedArtifacts.Select(artifact => artifact.ToString()).ToArray(),
            plan.FirstRerunWave,
            plan.LastRerunWave,
            plan.RerunStageCount,
            plan.AffectedArtifactCount,
            plan.HasWork,
            plan.RecommendedExecutionMode,
            plan.Evidence);
}

public sealed record BenchmarkPipelineHealthSummary(
    bool DependencyReady,
    int PlanIssueCount,
    int PlanWarningCount,
    int PlanErrorCount,
    int StageCount,
    int DependencyReadyStageCount,
    int NotDependencyReadyStageCount,
    int MissingRequiredReadCount,
    int MissingOptionalReadCount,
    bool RuntimeRequiredReadsHaveData,
    bool RuntimeOptionalReadsHaveData,
    int EmptyRequiredRuntimeReadCount,
    int EmptyOptionalRuntimeReadCount,
    bool WritesOnlyDeclaredArtifacts,
    int ContractViolationStageCount,
    int UndeclaredChangedArtifactCount,
    int EmptyDeclaredOutputCount,
    IReadOnlyList<string> ReviewStageNames,
    IReadOnlyList<string> Evidence)
{
    public static BenchmarkPipelineHealthSummary Empty { get; } = new(
        DependencyReady: true,
        PlanIssueCount: 0,
        PlanWarningCount: 0,
        PlanErrorCount: 0,
        StageCount: 0,
        DependencyReadyStageCount: 0,
        NotDependencyReadyStageCount: 0,
        MissingRequiredReadCount: 0,
        MissingOptionalReadCount: 0,
        RuntimeRequiredReadsHaveData: true,
        RuntimeOptionalReadsHaveData: true,
        EmptyRequiredRuntimeReadCount: 0,
        EmptyOptionalRuntimeReadCount: 0,
        WritesOnlyDeclaredArtifacts: true,
        ContractViolationStageCount: 0,
        UndeclaredChangedArtifactCount: 0,
        EmptyDeclaredOutputCount: 0,
        ReviewStageNames: Array.Empty<string>(),
        Evidence: new[] { "pipeline health unavailable" });

    public static BenchmarkPipelineHealthSummary From(
        PipelineExecutionPlan? plan,
        IReadOnlyList<BenchmarkStageSummary> stages)
    {
        stages ??= Array.Empty<BenchmarkStageSummary>();
        plan ??= PipelineExecutionPlan.Empty;

        var planWarnings = plan.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Warning);
        var planErrors = plan.Issues.Count(issue => issue.Severity == DiagnosticSeverity.Error);
        var notDependencyReadyStages = stages.Count(stage => !stage.IsDependencyReady);
        var missingRequiredReads = stages.Sum(stage => stage.MissingRequiredReads.Count);
        var missingOptionalReads = stages.Sum(stage => stage.MissingOptionalReads.Count);
        var emptyRequiredReads = stages.Sum(stage => stage.RuntimeReadiness.EmptyRequiredReads.Count);
        var emptyOptionalReads = stages.Sum(stage => stage.RuntimeReadiness.EmptyOptionalReads.Count);
        var contractViolationStages = stages.Count(stage => !stage.Contract.WritesOnlyDeclaredArtifacts);
        var undeclaredChangedArtifacts = stages.Sum(stage => stage.Contract.UndeclaredChangedArtifacts.Count);
        var emptyDeclaredOutputs = stages.Sum(CountEmptyDeclaredOutputs);
        var reviewStages = stages
            .Where(stage => !stage.IsDependencyReady
                || stage.RuntimeReadiness.HasEmptyRequiredReads
                || !stage.Contract.WritesOnlyDeclaredArtifacts
                || CountEmptyDeclaredOutputs(stage) > 0)
            .Select(stage => stage.Stage)
            .Where(stage => !string.IsNullOrWhiteSpace(stage))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();
        var dependencyReady = plan.IsDependencyReady && notDependencyReadyStages == 0;
        var runtimeRequiredReady = emptyRequiredReads == 0;
        var runtimeOptionalReady = emptyOptionalReads == 0;
        var writesOnlyDeclared = contractViolationStages == 0 && undeclaredChangedArtifacts == 0;

        return new BenchmarkPipelineHealthSummary(
            dependencyReady,
            plan.Issues.Count,
            planWarnings,
            planErrors,
            stages.Count,
            stages.Count - notDependencyReadyStages,
            notDependencyReadyStages,
            missingRequiredReads,
            missingOptionalReads,
            runtimeRequiredReady,
            runtimeOptionalReady,
            emptyRequiredReads,
            emptyOptionalReads,
            writesOnlyDeclared,
            contractViolationStages,
            undeclaredChangedArtifacts,
            emptyDeclaredOutputs,
            reviewStages,
            new[]
            {
                $"plan issues {plan.Issues.Count}, warnings {planWarnings}, errors {planErrors}",
                $"dependency ready stages {stages.Count - notDependencyReadyStages}/{stages.Count}",
                $"empty runtime reads required {emptyRequiredReads}, optional {emptyOptionalReads}",
                $"contract violations {contractViolationStages}, undeclared changes {undeclaredChangedArtifacts}, empty declared outputs {emptyDeclaredOutputs}"
            });
    }

    private static int CountEmptyDeclaredOutputs(BenchmarkStageSummary stage)
    {
        var readiness = stage.OutputReadiness ?? PipelineStageOutputReadiness.Empty;
        return readiness.IsAvailable
            ? readiness.EmptyDeclaredOutputs.Count
            : stage.ArtifactDeltas.Count(delta => delta.IsEmptyDeclaredOutput);
    }
}
