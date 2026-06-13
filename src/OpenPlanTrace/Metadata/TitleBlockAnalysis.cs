namespace OpenPlanTrace;

public sealed record TitleBlockAnalysis(
    string RegionId,
    int PageNumber,
    PlanRect Bounds,
    Confidence Confidence,
    IReadOnlyList<TitleBlockField> Fields,
    IReadOnlyList<string> SourcePrimitiveIds)
{
    public string? ProjectName => FirstValue(TitleBlockFieldKind.ProjectName);

    public string? SheetNumber => FirstValue(TitleBlockFieldKind.SheetNumber);

    public string? SheetTitle => FirstValue(TitleBlockFieldKind.SheetTitle);

    public string? Revision => FirstValue(TitleBlockFieldKind.Revision);

    public string? IssueDate => FirstValue(TitleBlockFieldKind.IssueDate);

    public string? Scale => FirstValue(TitleBlockFieldKind.Scale);

    public TitleBlockField? FirstField(TitleBlockFieldKind kind) =>
        Fields
            .Where(field => field.Kind == kind)
            .OrderByDescending(field => field.Confidence.Value)
            .FirstOrDefault();

    private string? FirstValue(TitleBlockFieldKind kind) =>
        FirstField(kind)?.Value;
}
