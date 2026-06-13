namespace OpenPlanTrace;

public sealed record PlanDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Stage,
    string Message)
{
    public DiagnosticScope Scope { get; init; } = DiagnosticScope.Document;

    public int? PageNumber { get; init; }

    public PlanRect? Region { get; init; }

    public Confidence? Confidence { get; init; }

    public IReadOnlyList<string> SourcePrimitiveIds { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>();
}
