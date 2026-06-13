namespace OpenPlanTrace;

public sealed record WallSegment(
    string Id,
    int PageNumber,
    PlanLineSegment CenterLine,
    double Thickness,
    Confidence Confidence)
{
    public string? SourceRegionId { get; init; }

    public WallDetectionKind DetectionKind { get; init; } = WallDetectionKind.SingleLine;

    public IReadOnlyList<string> SourcePrimitiveIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();

    public WallPairEvidence? PairEvidence { get; init; }

    public double DrawingLength => CenterLine.Length;

    public double? LengthMeters { get; init; }

    public double? ThicknessMillimeters { get; init; }

    public string? MeasurementScaleGroupId { get; init; }

    public PlanRect Bounds => CenterLine.Bounds.Inflate(Math.Max(Thickness / 2.0, 0.5));
}
