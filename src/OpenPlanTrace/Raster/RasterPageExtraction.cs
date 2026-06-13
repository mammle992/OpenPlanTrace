namespace OpenPlanTrace;

public sealed record RasterPageExtraction(
    int PageNumber,
    PlanSize Size)
{
    public double? Dpi { get; init; }

    public string? SourceImageId { get; init; }

    public IReadOnlyList<RasterTextEvidence> Text { get; init; } =
        Array.Empty<RasterTextEvidence>();

    public IReadOnlyList<RasterLineEvidence> Lines { get; init; } =
        Array.Empty<RasterLineEvidence>();

    public IReadOnlyList<RasterPolylineEvidence> Polylines { get; init; } =
        Array.Empty<RasterPolylineEvidence>();
}
