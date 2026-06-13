namespace OpenPlanTrace;

public sealed record MeasurementConsistencyReport(
    bool HasReliableCalibration,
    double? SelectedMillimetersPerDrawingUnit,
    double? MedianDimensionMillimetersPerDrawingUnit,
    double? DimensionScaleSpreadRatio,
    Confidence Confidence,
    IReadOnlyList<MeasurementConsistencyCheck> Checks)
{
    public static MeasurementConsistencyReport Empty { get; } =
        new(
            false,
            null,
            null,
            null,
            Confidence.None,
            Array.Empty<MeasurementConsistencyCheck>());

    public const int NonBlockingOutlierCountMaximum = 2;

    public const double NonBlockingOutlierRatioMaximum = 0.25;

    public const double BlockingScaleSpreadRatioThreshold = 1.5;

    public int CheckedCount => Checks.Count(check => check.Status is not MeasurementConsistencyStatus.Unchecked);

    public int ConsistentCount => Checks.Count(check => check.Status == MeasurementConsistencyStatus.Consistent);

    public int OutlierCount => Checks.Count(check => check.Status == MeasurementConsistencyStatus.Outlier);

    public bool HasOutliers => OutlierCount > 0;

    public double OutlierRatio =>
        CheckedCount == 0 ? 0 : Math.Round(OutlierCount / (double)CheckedCount, 6);

    public bool HasBlockingOutliers =>
        HasOutliers
        && (OutlierCount > NonBlockingOutlierCountMaximum
            || OutlierRatio > NonBlockingOutlierRatioMaximum
            || DimensionScaleSpreadRatio is >= BlockingScaleSpreadRatioThreshold);

    public bool HasTolerableOutliers => HasOutliers && !HasBlockingOutliers;
}
