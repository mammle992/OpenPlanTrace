using System.Globalization;

namespace OpenPlanTrace;

internal sealed class MeasurementConsistencyStage : IPipelineStage
{
    private const double RelativeTolerance = 0.12;
    private const double HighSpreadRatioThreshold = 2.0;

    public string Name => "measurement-consistency";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var matchedDimensions = context.Dimensions
            .Where(dimension => dimension.DimensionLine is not null)
            .Where(dimension => dimension.DrawingLength is > 0)
            .Where(dimension => dimension.MillimetersPerDrawingUnit is > 0)
            .OrderBy(dimension => dimension.PageNumber)
            .ThenBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToArray();

        if (matchedDimensions.Length == 0)
        {
            context.MeasurementConsistency = MeasurementConsistencyReport.Empty with
            {
                HasReliableCalibration = context.Calibration.HasReliableMeasurementScale,
                SelectedMillimetersPerDrawingUnit = context.Calibration.MillimetersPerDrawingUnit
            };

            if (context.Dimensions.Count > 0)
            {
                context.AddDiagnostic(
                    "measurement_consistency.no_matched_dimension_lines",
                    DiagnosticSeverity.Info,
                    Name,
                    "Dimension text was found, but no dimension annotation has a matched drawing line for scale consistency checks.",
                    confidence: Confidence.Low,
                    scope: DiagnosticScope.Dimension,
                    sourcePrimitiveIds: context.Dimensions.SelectMany(dimension => dimension.SourcePrimitiveIds),
                    properties: new Dictionary<string, string>
                    {
                        ["dimensionCount"] = context.Dimensions.Count.ToString(CultureInfo.InvariantCulture)
                    });
            }

            return ValueTask.CompletedTask;
        }

        var selected = context.Calibration.MillimetersPerDrawingUnit;
        var checks = matchedDimensions
            .Select(dimension => CreateCheck(dimension, selected))
            .ToArray();
        var impliedValues = matchedDimensions
            .Select(dimension => dimension.MillimetersPerDrawingUnit!.Value)
            .Order()
            .ToArray();
        var median = Median(impliedValues);
        var spread = impliedValues.Length >= 2 && impliedValues.Min() > 0
            ? impliedValues.Max() / impliedValues.Min()
            : (double?)null;
        var reportConfidence = new Confidence(Math.Min(0.92, checks.Average(check => check.Confidence.Value)));

        context.MeasurementConsistency = new MeasurementConsistencyReport(
            context.Calibration.HasReliableMeasurementScale,
            selected,
            median,
            spread,
            reportConfidence,
            checks);

        AddDiagnostics(context, checks, median, spread);

        return ValueTask.CompletedTask;
    }

    private static MeasurementConsistencyCheck CreateCheck(
        DimensionAnnotation dimension,
        double? selectedMillimetersPerDrawingUnit)
    {
        var implied = dimension.MillimetersPerDrawingUnit!.Value;
        var drawingLength = dimension.DrawingLength!.Value;
        var expected = selectedMillimetersPerDrawingUnit is > 0
            ? drawingLength * selectedMillimetersPerDrawingUnit.Value
            : (double?)null;
        double? delta = expected is null
            ? null
            : dimension.MeasuredMillimeters - expected.Value;
        var relativeError = selectedMillimetersPerDrawingUnit is > 0
            ? Math.Abs(implied - selectedMillimetersPerDrawingUnit.Value) / selectedMillimetersPerDrawingUnit.Value
            : (double?)null;
        var status = relativeError is null
            ? MeasurementConsistencyStatus.Unchecked
            : relativeError <= RelativeTolerance
                ? MeasurementConsistencyStatus.Consistent
                : MeasurementConsistencyStatus.Outlier;
        var confidence = new Confidence(Math.Min(0.95, dimension.Confidence.Value + (status == MeasurementConsistencyStatus.Outlier ? 0.04 : 0)));

        var evidence = new List<string>
        {
            $"Dimension {dimension.Id} implies {Format(implied)} mm/drawing-unit.",
            $"Dimension text {dimension.NormalizedText} over {Format(drawingLength)} drawing units."
        };

        if (selectedMillimetersPerDrawingUnit is > 0)
        {
            evidence.Add($"Selected calibration is {Format(selectedMillimetersPerDrawingUnit.Value)} mm/drawing-unit.");
            evidence.Add(status == MeasurementConsistencyStatus.Consistent
                ? $"Relative scale error {Format((relativeError ?? 0) * 100)}% is within the {Format(RelativeTolerance * 100)}% tolerance."
                : $"Relative scale error {Format((relativeError ?? 0) * 100)}% exceeds the {Format(RelativeTolerance * 100)}% tolerance.");
        }
        else
        {
            evidence.Add("No selected calibration was available; retained as an unchecked dimension scale candidate.");
        }

        return new MeasurementConsistencyCheck(
            dimension.Id,
            dimension.PageNumber,
            status,
            dimension.MeasuredMillimeters,
            drawingLength,
            implied,
            selectedMillimetersPerDrawingUnit,
            expected,
            delta,
            relativeError,
            confidence,
            dimension.SourcePrimitiveIds,
            evidence);
    }

    private void AddDiagnostics(
        ScanContext context,
        IReadOnlyList<MeasurementConsistencyCheck> checks,
        double median,
        double? spread)
    {
        var outliers = checks.Where(check => check.Status == MeasurementConsistencyStatus.Outlier).ToArray();
        var consistent = checks.Where(check => check.Status == MeasurementConsistencyStatus.Consistent).ToArray();

        if (spread is >= HighSpreadRatioThreshold)
        {
            var impliedValues = checks
                .Select(check => check.ImpliedMillimetersPerDrawingUnit)
                .Where(value => value > 0)
                .Order()
                .ToArray();

            context.AddDiagnostic(
                "measurement_consistency.dimension_scale_spread_high",
                DiagnosticSeverity.Warning,
                Name,
                "Matched dimensions imply widely different drawing scales; dimension-line matching or mixed-scale detail regions need review.",
                confidence: context.MeasurementConsistency.Confidence,
                scope: DiagnosticScope.Dimension,
                sourcePrimitiveIds: checks.SelectMany(check => check.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["checkCount"] = checks.Count.ToString(CultureInfo.InvariantCulture),
                    ["spreadRatio"] = Format(spread.Value),
                    ["minimumDimensionMillimetersPerDrawingUnit"] = Format(impliedValues.First()),
                    ["maximumDimensionMillimetersPerDrawingUnit"] = Format(impliedValues.Last()),
                    ["medianDimensionMillimetersPerDrawingUnit"] = Format(median),
                    ["selectedMillimetersPerDrawingUnit"] = Format(context.Calibration.MillimetersPerDrawingUnit ?? 0)
                });
        }

        if (!context.Calibration.HasReliableMeasurementScale)
        {
            context.AddDiagnostic(
                "measurement_consistency.dimension_scale_candidate",
                DiagnosticSeverity.Info,
                Name,
                "Matched dimensions imply a drawing scale, but no reliable selected calibration is available yet.",
                confidence: context.MeasurementConsistency.Confidence,
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: checks.SelectMany(check => check.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["checkCount"] = checks.Count.ToString(CultureInfo.InvariantCulture),
                    ["medianDimensionMillimetersPerDrawingUnit"] = Format(median),
                    ["spreadRatio"] = spread is null ? string.Empty : Format(spread.Value)
                });
            return;
        }

        if (outliers.Length > 0)
        {
            var worst = outliers
                .OrderByDescending(check => check.RelativeError ?? 0)
                .First();
            var outlierRatio = outliers.Length / (double)checks.Count;
            var metricImportImpact = context.MeasurementConsistency.HasBlockingOutliers
                ? "blocking"
                : "review";

            context.AddDiagnostic(
                "measurement_consistency.dimension_conflict",
                DiagnosticSeverity.Warning,
                Name,
                "One or more matched dimensions disagree with the selected calibration beyond tolerance.",
                worst.PageNumber,
                null,
                worst.Confidence,
                DiagnosticScope.Calibration,
                outliers.SelectMany(check => check.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["outlierCount"] = outliers.Length.ToString(CultureInfo.InvariantCulture),
                    ["checkedCount"] = checks.Count.ToString(CultureInfo.InvariantCulture),
                    ["outlierRatio"] = Format(outlierRatio),
                    ["metricImportImpact"] = metricImportImpact,
                    ["selectedMillimetersPerDrawingUnit"] = Format(context.Calibration.MillimetersPerDrawingUnit ?? 0),
                    ["medianDimensionMillimetersPerDrawingUnit"] = Format(median),
                    ["worstDimensionId"] = worst.DimensionId,
                    ["worstRelativeErrorPercent"] = Format((worst.RelativeError ?? 0) * 100)
                });
            return;
        }

        if (consistent.Length > 0)
        {
            context.AddDiagnostic(
                "measurement_consistency.dimensions_consistent",
                DiagnosticSeverity.Info,
                Name,
                "Matched dimensions are consistent with the selected calibration.",
                confidence: context.MeasurementConsistency.Confidence,
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: consistent.SelectMany(check => check.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["checkedCount"] = consistent.Length.ToString(CultureInfo.InvariantCulture),
                    ["selectedMillimetersPerDrawingUnit"] = Format(context.Calibration.MillimetersPerDrawingUnit ?? 0),
                    ["medianDimensionMillimetersPerDrawingUnit"] = Format(median),
                    ["spreadRatio"] = spread is null ? string.Empty : Format(spread.Value)
                });
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2.0
            : values[middle];
    }

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
