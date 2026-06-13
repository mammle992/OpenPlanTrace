namespace OpenPlanTrace;

public enum PipelineStageProgressKind
{
    Started = 0,
    Completed
}

public sealed record PipelineStageProgress(
    string StageName,
    PipelineStageProgressKind Kind,
    TimeSpan Duration,
    int InputDetectionCount,
    int OutputDetectionCount,
    int DiagnosticCount);
