namespace OpenPlanTrace;

public sealed record PipelineDiagnostics(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<PipelineStageReport> StageReports,
    IReadOnlyList<PlanDiagnostic> Messages)
{
    public TimeSpan Duration => CompletedAt - StartedAt;

    public bool HasErrors => Messages.Any(message => message.Severity == DiagnosticSeverity.Error);

    public int InfoCount => Messages.Count(message => message.Severity == DiagnosticSeverity.Info);

    public int WarningCount => Messages.Count(message => message.Severity == DiagnosticSeverity.Warning);

    public int ErrorCount => Messages.Count(message => message.Severity == DiagnosticSeverity.Error);
}
