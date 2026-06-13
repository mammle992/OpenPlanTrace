using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class MeasurementConsistencyTests
{
    [Fact]
    public void MeasurementReport_TreatsBoundedOutliersAsReviewOnlyForMetricImport()
    {
        var report = new MeasurementConsistencyReport(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 70,
            MedianDimensionMillimetersPerDrawingUnit: 71,
            DimensionScaleSpreadRatio: 1.37,
            Confidence: Confidence.High,
            Checks: CreateChecks(consistentCount: 6, outlierCount: 2));

        Assert.True(report.HasOutliers);
        Assert.True(report.HasTolerableOutliers);
        Assert.False(report.HasBlockingOutliers);
        Assert.Equal(0.25, report.OutlierRatio);
    }

    [Fact]
    public void MeasurementReport_TreatsLargeOutlierShareAsBlockingForMetricImport()
    {
        var report = new MeasurementConsistencyReport(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 70,
            MedianDimensionMillimetersPerDrawingUnit: 71,
            DimensionScaleSpreadRatio: 1.37,
            Confidence: Confidence.High,
            Checks: CreateChecks(consistentCount: 2, outlierCount: 2));

        Assert.True(report.HasOutliers);
        Assert.False(report.HasTolerableOutliers);
        Assert.True(report.HasBlockingOutliers);
        Assert.Equal(0.5, report.OutlierRatio);
    }

    [Fact]
    public void MeasurementReport_TreatsWideScaleSpreadAsBlockingForMetricImport()
    {
        var report = new MeasurementConsistencyReport(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 70,
            MedianDimensionMillimetersPerDrawingUnit: 71,
            DimensionScaleSpreadRatio: MeasurementConsistencyReport.BlockingScaleSpreadRatioThreshold,
            Confidence: Confidence.High,
            Checks: CreateChecks(consistentCount: 6, outlierCount: 1));

        Assert.True(report.HasOutliers);
        Assert.True(report.HasBlockingOutliers);
    }

    [Fact]
    public async Task ScanAsync_ReportsDimensionConsistencyWhenCalibrationMatches()
    {
        var document = CreateDimensionOnlyDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("dim-text-1", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Single(result.MeasurementConsistency.Checks);
        Assert.Equal(1, result.MeasurementConsistency.CheckedCount);
        Assert.Equal(1, result.MeasurementConsistency.ConsistentCount);
        Assert.Equal(0, result.MeasurementConsistency.OutlierCount);
        Assert.Equal(15, result.MeasurementConsistency.MedianDimensionMillimetersPerDrawingUnit);

        var check = Assert.Single(result.MeasurementConsistency.Checks);
        Assert.Equal(MeasurementConsistencyStatus.Consistent, check.Status);
        Assert.Equal("page:1:dimension:1", check.DimensionId);
        Assert.Equal(15, check.ImpliedMillimetersPerDrawingUnit);
        Assert.Contains(check.Evidence, item => item.Contains("within", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "measurement_consistency.dimensions_consistent");
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenDimensionConflictsWithSelectedTitleBlockScale()
    {
        var document = CreatePdfScaleConflictDocument();

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.InRange(result.Calibration.MillimetersPerDrawingUnit!.Value, 35.27, 35.28);
        Assert.Equal(1, result.MeasurementConsistency.OutlierCount);

        var check = Assert.Single(result.MeasurementConsistency.Checks);
        Assert.Equal(MeasurementConsistencyStatus.Outlier, check.Status);
        Assert.Equal(10, check.ImpliedMillimetersPerDrawingUnit);
        Assert.InRange(check.RelativeError!.Value, 0.71, 0.72);
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "measurement_consistency.dimension_conflict"
            && message.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenMatchedDimensionsHaveHighScaleSpread()
    {
        var document = CreateDimensionOnlyDocument(
            DimLine("dim-line-short", new PlanPoint(100, 320), new PlanPoint(120, 320)),
            DimText("dim-text-short", "1000 mm", new PlanRect(96, 334, 70, 16)),
            DimLine("dim-line-long", new PlanPoint(240, 320), new PlanPoint(340, 320)),
            DimText("dim-text-long", "1000 mm", new PlanRect(256, 334, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.MeasurementConsistency.DimensionScaleSpreadRatio >= 5);
        var diagnostic = Assert.Single(
            result.Diagnostics.Messages,
            message => message.Code == "measurement_consistency.dimension_scale_spread_high");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(DiagnosticScope.Dimension, diagnostic.Scope);
        Assert.Equal("2", diagnostic.Properties["checkCount"]);
        Assert.Equal("5", diagnostic.Properties["spreadRatio"]);
        Assert.Contains("dim-line-short", diagnostic.SourcePrimitiveIds);
        Assert.Contains("dim-line-long", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task JsonExport_IncludesMeasurementConsistencyReport()
    {
        var document = CreateDimensionOnlyDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("dim-text-1", "3000 mm", new PlanRect(168, 334, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var report = parsed.RootElement.GetProperty("measurementConsistency");
        Assert.Equal(1, report.GetProperty("checkedCount").GetInt32());
        Assert.Equal(1, report.GetProperty("consistentCount").GetInt32());
        Assert.Equal(0, report.GetProperty("outlierCount").GetInt32());
        Assert.Equal(0, report.GetProperty("outlierRatio").GetDouble());
        Assert.False(report.GetProperty("hasBlockingOutliers").GetBoolean());
        Assert.False(report.GetProperty("hasTolerableOutliers").GetBoolean());
        Assert.Equal(MeasurementConsistencyReport.NonBlockingOutlierCountMaximum, report.GetProperty("nonBlockingOutlierCountMaximum").GetInt32());
        Assert.Equal(MeasurementConsistencyReport.NonBlockingOutlierRatioMaximum, report.GetProperty("nonBlockingOutlierRatioMaximum").GetDouble());
        Assert.Equal(MeasurementConsistencyReport.BlockingScaleSpreadRatioThreshold, report.GetProperty("blockingScaleSpreadRatioThreshold").GetDouble());
        Assert.Equal("None", report.GetProperty("metricImportImpact").GetString());
        Assert.Equal(15, report.GetProperty("medianDimensionMillimetersPerDrawingUnit").GetDouble());

        var check = Assert.Single(report.GetProperty("checks").EnumerateArray());
        Assert.Equal("Consistent", check.GetProperty("status").GetString());
        Assert.Equal("page:1:dimension:1", check.GetProperty("dimensionId").GetString());
        Assert.True(check.GetProperty("sourcePrimitiveIds").GetArrayLength() >= 2);
        Assert.True(check.GetProperty("evidence").GetArrayLength() > 0);
    }

    private static PlanDocument CreateDimensionOnlyDocument(params PlanPrimitive[] primitives)
    {
        var allPrimitives = new List<PlanPrimitive>
        {
            WallLine("wall-top", new PlanPoint(100, 100), new PlanPoint(300, 100)),
            WallLine("wall-right", new PlanPoint(300, 100), new PlanPoint(300, 260)),
            WallLine("wall-bottom", new PlanPoint(300, 260), new PlanPoint(100, 260)),
            WallLine("wall-left", new PlanPoint(100, 260), new PlanPoint(100, 100))
        };
        allPrimitives.AddRange(primitives);

        return new PlanDocument(
            "measurement-consistency-test",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 500),
                    allPrimitives)
            });
    }

    private static PlanDocument CreatePdfScaleConflictDocument() =>
        new(
            "measurement-conflict",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    new PlanPrimitive[]
                    {
                        PdfLine("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        PdfLine("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        PdfLine("wall-bottom", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        PdfLine("wall-left", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        PdfRect("title-grid", new PlanRect(700, 650, 260, 120)),
                        PdfText("title-project", "PROJECT OPENPLANTRACE", new PlanRect(720, 670, 150, 18)),
                        PdfText("title-sheet", "A-101", new PlanRect(720, 700, 50, 18)),
                        PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18)),
                        DimLine("dim-line-1", new PlanPoint(100, 520), new PlanPoint(200, 520)),
                        DimText("dim-text-1", "1000 mm", new PlanRect(126, 534, 70, 16))
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                Properties = new Dictionary<string, string>
                {
                    ["format"] = "pdf"
                }
            }
        };

    private static LinePrimitive WallLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "test", "LINE", "A-WALL")
        };

    private static LinePrimitive PdfLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Source = Source(sourceId, "pdf", "line", null)
        };

    private static RectanglePrimitive PdfRect(string sourceId, PlanRect bounds) =>
        new(bounds)
        {
            SourceId = sourceId,
            Source = Source(sourceId, "pdf", "rectangle", null)
        };

    private static TextPrimitive PdfText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Source = Source(sourceId, "pdf", "word", null)
        };

    private static LinePrimitive DimLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "test", "LINE", "A-DIM")
        };

    private static TextPrimitive DimText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "test", "TEXT", "A-DIM")
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string sourceFormat,
        string entityType,
        string? layer) =>
        new()
        {
            SourceFormat = sourceFormat,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Paper
        };

    private static IReadOnlyList<MeasurementConsistencyCheck> CreateChecks(
        int consistentCount,
        int outlierCount)
    {
        var checks = new List<MeasurementConsistencyCheck>();
        for (var index = 0; index < consistentCount; index++)
        {
            checks.Add(CreateCheck(index + 1, MeasurementConsistencyStatus.Consistent, relativeError: 0.03));
        }

        for (var index = 0; index < outlierCount; index++)
        {
            checks.Add(CreateCheck(consistentCount + index + 1, MeasurementConsistencyStatus.Outlier, relativeError: 0.31));
        }

        return checks;
    }

    private static MeasurementConsistencyCheck CreateCheck(
        int index,
        MeasurementConsistencyStatus status,
        double relativeError) =>
        new(
            $"dimension-{index}",
            1,
            status,
            1000,
            14.285714,
            status == MeasurementConsistencyStatus.Consistent ? 70 : 92,
            70,
            1000,
            status == MeasurementConsistencyStatus.Consistent ? 0 : 314,
            relativeError,
            Confidence.High,
            new[] { $"dimension-{index}" },
            new[] { $"Synthetic {status} check." });
}
