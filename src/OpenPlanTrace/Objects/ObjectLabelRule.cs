namespace OpenPlanTrace;

public sealed record ObjectLabelRule
{
    public string? Signature { get; init; }

    public string? SymbolNamePattern { get; init; }

    public string? LabelPattern { get; init; }

    public string? LayerPattern { get; init; }

    public string? DetectedTagPattern { get; init; }

    public string? SourceFormat { get; init; }

    public ObjectCategory? MatchCategory { get; init; }

    public ObjectCandidateKind? MatchKind { get; init; }

    public ObjectCategory? Category { get; init; }

    public ObjectCandidateKind? Kind { get; init; }

    public string? Label { get; init; }

    public string? SymbolName { get; init; }

    public bool? RequiresReview { get; init; }

    public Confidence? Confidence { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public bool HasSelector =>
        !string.IsNullOrWhiteSpace(Signature)
        || !string.IsNullOrWhiteSpace(SymbolNamePattern)
        || !string.IsNullOrWhiteSpace(LabelPattern)
        || !string.IsNullOrWhiteSpace(LayerPattern)
        || !string.IsNullOrWhiteSpace(DetectedTagPattern)
        || !string.IsNullOrWhiteSpace(SourceFormat)
        || MatchCategory is not null
        || MatchKind is not null;

    public bool HasOutput =>
        Category is not null
        || Kind is not null
        || !string.IsNullOrWhiteSpace(Label)
        || !string.IsNullOrWhiteSpace(SymbolName)
        || RequiresReview is not null
        || Confidence is not null
        || Evidence.Count > 0;
}
