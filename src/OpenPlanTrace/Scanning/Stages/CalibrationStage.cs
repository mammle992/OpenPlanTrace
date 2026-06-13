using System.Globalization;

namespace OpenPlanTrace;

internal sealed class CalibrationStage : IPipelineStage
{
    private const string StageName = "calibration";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        context.Calibration = PlanCalibrationAnalyzer.Analyze(context.Document, context.SheetRegions);

        var measuredEvidence = context.Calibration.Evidence
            .Where(item => item.MillimetersPerDrawingUnit is > 0)
            .Select(item => item.MillimetersPerDrawingUnit!.Value)
            .ToArray();

        if (!context.Calibration.HasReliableMeasurementScale)
        {
            context.AddDiagnostic(
                "calibration.measurement_scale.missing",
                DiagnosticSeverity.Info,
                StageName,
                "No reliable drawing-to-real-world measurement scale was found. Lengths and areas remain in drawing units.",
                confidence: context.Calibration.Confidence,
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: context.Calibration.Evidence
                    .Select(item => item.SourcePrimitiveId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))!,
                properties: new Dictionary<string, string>
                {
                    ["evidenceCount"] = context.Calibration.Evidence.Count.ToString(),
                    ["hasReliableMeasurementScale"] = context.Calibration.HasReliableMeasurementScale.ToString()
                });
        }
        else if (measuredEvidence.Length >= 2 && measuredEvidence.Max() / measuredEvidence.Min() > 1.25)
        {
            context.AddDiagnostic(
                "calibration.evidence.conflict",
                DiagnosticSeverity.Warning,
                StageName,
                "Multiple scale evidences disagree by more than 25%; the highest-confidence calibration was selected.",
                confidence: context.Calibration.Confidence,
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: context.Calibration.Evidence
                    .Select(item => item.SourcePrimitiveId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))!,
                properties: new Dictionary<string, string>
                {
                    ["minMillimetersPerDrawingUnit"] = Format(measuredEvidence.Min()),
                    ["maxMillimetersPerDrawingUnit"] = Format(measuredEvidence.Max()),
                    ["selectedMillimetersPerDrawingUnit"] = Format(context.Calibration.MillimetersPerDrawingUnit ?? 0)
                });
        }

        AddMultipleScaleDiagnostics(context);
        AddNoScaleDiagnostics(context);

        return ValueTask.CompletedTask;
    }

    private static void AddMultipleScaleDiagnostics(ScanContext context)
    {
        foreach (var pageGroup in context.Calibration.ScaleGroups
            .Where(group => group.PageNumber is not null)
            .GroupBy(group => group.PageNumber!.Value))
        {
            var comparable = pageGroup
                .Where(group => group.MillimetersPerDrawingUnit is > 0 || group.ScaleRatio is > 0)
                .ToArray();
            if (comparable.Length < 2)
            {
                continue;
            }

            var measured = comparable
                .Where(group => group.MillimetersPerDrawingUnit is > 0)
                .Select(group => group.MillimetersPerDrawingUnit!.Value)
                .ToArray();
            var ratios = comparable
                .Where(group => group.ScaleRatio is > 0)
                .Select(group => group.ScaleRatio!.Value)
                .ToArray();
            var values = measured.Length >= 2 ? measured : ratios;
            if (values.Length < 2 || values.Max() / values.Min() <= 1.05)
            {
                continue;
            }

            context.AddDiagnostic(
                "calibration.multiple_scales_on_page",
                DiagnosticSeverity.Warning,
                StageName,
                "Multiple scale groups were detected on the same page; measurements may be viewport/detail specific.",
                pageNumber: pageGroup.Key,
                confidence: new Confidence(Math.Min(0.9, comparable.Max(group => group.Confidence.Value))),
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: comparable.SelectMany(group => group.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["pageNumber"] = pageGroup.Key.ToString(CultureInfo.InvariantCulture),
                    ["scaleGroupCount"] = comparable.Length.ToString(CultureInfo.InvariantCulture),
                    ["scopes"] = string.Join(",", comparable.Select(group => group.Scope).Distinct().OrderBy(scope => scope.ToString())),
                    ["sourceRegionIds"] = string.Join(",", comparable.SelectMany(group => group.SourceRegionIds).Distinct(StringComparer.Ordinal)),
                    ["scaleRatios"] = string.Join(",", comparable.Select(group => group.ScaleRatio).Where(value => value is > 0).Select(value => Format(value!.Value)).Distinct()),
                    ["millimetersPerDrawingUnitValues"] = string.Join(",", comparable.Select(group => group.MillimetersPerDrawingUnit).Where(value => value is > 0).Select(value => Format(value!.Value)).Distinct()),
                    ["minimumScaleValue"] = Format(values.Min()),
                    ["maximumScaleValue"] = Format(values.Max()),
                    ["selectedMillimetersPerDrawingUnit"] = Format(context.Calibration.MillimetersPerDrawingUnit ?? 0)
                });
        }
    }

    private static void AddNoScaleDiagnostics(ScanContext context)
    {
        foreach (var pageGroup in context.Calibration.Evidence
            .Where(item => item.Kind == CalibrationEvidenceKind.NoScaleText)
            .GroupBy(item => item.PageNumber))
        {
            var evidence = pageGroup.ToArray();
            context.AddDiagnostic(
                "calibration.not_to_scale_text.detected",
                DiagnosticSeverity.Warning,
                StageName,
                "Text on the sheet indicates not-to-scale content; calibrated measurements may not apply to those regions.",
                pageNumber: pageGroup.Key,
                confidence: new Confidence(Math.Min(0.85, evidence.Max(item => item.Confidence.Value))),
                scope: DiagnosticScope.Calibration,
                sourcePrimitiveIds: evidence.Select(item => item.SourcePrimitiveId).Where(id => !string.IsNullOrWhiteSpace(id))!,
                properties: new Dictionary<string, string>
                {
                    ["noScaleTextCount"] = evidence.Length.ToString(CultureInfo.InvariantCulture),
                    ["hasReliableMeasurementScale"] = context.Calibration.HasReliableMeasurementScale.ToString(),
                    ["text"] = string.Join(" | ", evidence.Select(item => item.Text).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase))
                });
        }
    }

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
}
