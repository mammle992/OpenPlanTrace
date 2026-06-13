namespace OpenPlanTrace;

public sealed record PipelineStageReport(
    string Stage,
    TimeSpan Duration,
    int InputCount,
    int OutputCount,
    int DiagnosticCount = 0,
    int InfoCount = 0,
    int WarningCount = 0,
    int ErrorCount = 0);
