namespace OpenPlanTrace;

internal sealed class PipelineDiagnosticsBuilder
{
    private readonly List<PipelineStageReport> _stageReports = new();
    private readonly List<PlanDiagnostic> _messages = new();

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public int MessageCount => _messages.Count;

    public void Add(PlanDiagnostic diagnostic) => _messages.Add(diagnostic);

    public void AddStageReport(PipelineStageReport report) => _stageReports.Add(report);

    public IReadOnlyList<PlanDiagnostic> MessagesSince(int startIndex) =>
        _messages.Skip(startIndex).ToArray();

    public PipelineDiagnostics Build() =>
        new(
            StartedAt,
            DateTimeOffset.UtcNow,
            _stageReports.ToArray(),
            _messages.ToArray());
}
