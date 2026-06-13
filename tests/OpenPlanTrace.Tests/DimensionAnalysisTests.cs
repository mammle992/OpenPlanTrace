using System.Globalization;
using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class DimensionAnalysisTests
{
    [Fact]
    public async Task ScanAsync_ExtractsMatchedMetricDimension()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("dim-text-1", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal(DimensionKind.Linear, dimension.Kind);
        Assert.Equal(DimensionOrientation.Horizontal, dimension.Orientation);
        Assert.Equal("3 m", dimension.NormalizedText);
        Assert.Equal(PlanMeasurementUnit.Meter, dimension.Unit);
        Assert.Equal(3000, dimension.MeasuredMillimeters);
        Assert.Equal(200, dimension.DrawingLength);
        Assert.Equal(15, dimension.MillimetersPerDrawingUnit);
        Assert.NotNull(dimension.DimensionLine);
        Assert.Contains("dim-text-1", dimension.SourcePrimitiveIds);
        Assert.Contains("dim-line-1", dimension.SourcePrimitiveIds);
        Assert.Contains(dimension.Evidence, item => item.Contains("Matched nearby horizontal dimension line", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.StageReports, report => report.Stage == "dimensions");
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "dimensions.detected");
    }

    [Fact]
    public async Task ScanAsync_ExtractsMatchedMetricDimensionFromSpacedMillimeters()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("dim-text-1", "8 400", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal("8400 mm", dimension.NormalizedText);
        Assert.Equal(PlanMeasurementUnit.Millimeter, dimension.Unit);
        Assert.Equal(8400, dimension.MeasuredMillimeters);
        Assert.Contains("dim-text-1", dimension.SourcePrimitiveIds);
        Assert.Contains("dim-line-1", dimension.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_MergesAdjacentPdfMetricDimensionTextFragments()
    {
        var document = CreateDocument(
            PdfLine("pdf-dim-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            PdfLine("pdf-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            PdfLine("pdf-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            PdfText("pdf-dim-text-a", "8", new PlanRect(168, 334, 12, 16)),
            PdfText("pdf-dim-text-b", "400", new PlanRect(184, 334, 34, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal("8 400", dimension.Text);
        Assert.Equal("8400 mm", dimension.NormalizedText);
        Assert.Equal(8400, dimension.MeasuredMillimeters);
        Assert.Contains("pdf-dim-text-a", dimension.SourcePrimitiveIds);
        Assert.Contains("pdf-dim-text-b", dimension.SourcePrimitiveIds);
        Assert.Contains("pdf-dim-line", dimension.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_DoesNotTreatRoomAreaTextAsDimension()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("area-text", "10.7 m^2", new PlanRect(170, 334, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public async Task ScanAsync_RetainsUnmatchedImperialDimensionAsUnitHint()
    {
        var document = CreateDocument(
            DimText("dim-text-1", "12'-0\"", new PlanRect(430, 430, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal(DimensionOrientation.Horizontal, dimension.Orientation);
        Assert.Equal(PlanMeasurementUnit.Foot, dimension.Unit);
        Assert.Equal("12 ft 0 in", dimension.NormalizedText);
        Assert.InRange(dimension.MeasuredMillimeters, 3657.59, 3657.61);
        Assert.Null(dimension.DimensionLine);
        Assert.Null(dimension.DrawingLength);
        Assert.Null(dimension.MillimetersPerDrawingUnit);
        Assert.Contains(dimension.Evidence, item => item.Contains("unit hint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JsonExport_IncludesDimensionAnnotations()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimText("dim-text-1", "3000 mm", new PlanRect(168, 334, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var dimensions = parsed.RootElement.GetProperty("dimensions");
        var dimension = Assert.Single(dimensions.EnumerateArray());
        Assert.Equal("3000 mm", dimension.GetProperty("normalizedText").GetString());
        Assert.Equal("Horizontal", dimension.GetProperty("orientation").GetString());
        Assert.Equal(3000, dimension.GetProperty("measuredMillimeters").GetDouble());
        Assert.Equal(15, dimension.GetProperty("millimetersPerDrawingUnit").GetDouble());
        Assert.True(dimension.GetProperty("sourcePrimitiveIds").GetArrayLength() >= 2);
        Assert.True(dimension.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ScanAsync_ReportsConsistentChainedDimensions()
    {
        var document = CreateDocument(
            DimLine("child-dim-line-a", new PlanPoint(100, 320), new PlanPoint(200, 320)),
            DimText("child-dim-text-a", "1000 mm", new PlanRect(118, 292, 70, 16)),
            DimLine("child-dim-line-b", new PlanPoint(200, 320), new PlanPoint(300, 320)),
            DimText("child-dim-text-b", "2000 mm", new PlanRect(218, 292, 70, 16)),
            DimLine("parent-dim-line", new PlanPoint(100, 360), new PlanPoint(300, 360)),
            DimText("parent-dim-text", "3000 mm", new PlanRect(168, 374, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(3, result.Dimensions.Count);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.chains_consistent");
        Assert.Equal("1", diagnostic.Properties["chainCount"]);
        Assert.Equal("1", diagnostic.Properties["consistentCount"]);
        Assert.Equal("0", diagnostic.Properties["conflictCount"]);
        Assert.Contains("parent-dim-text", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenChainedDimensionsDoNotSumToParent()
    {
        var document = CreateDocument(
            DimLine("child-dim-line-a", new PlanPoint(100, 320), new PlanPoint(200, 320)),
            DimText("child-dim-text-a", "1000 mm", new PlanRect(118, 292, 70, 16)),
            DimLine("child-dim-line-b", new PlanPoint(200, 320), new PlanPoint(300, 320)),
            DimText("child-dim-text-b", "2000 mm", new PlanRect(218, 292, 70, 16)),
            DimLine("parent-dim-line", new PlanPoint(100, 360), new PlanPoint(300, 360)),
            DimText("parent-dim-text", "3200 mm", new PlanRect(168, 374, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(3, result.Dimensions.Count);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.chain_conflict");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("1", diagnostic.Properties["chainCount"]);
        Assert.Equal("1", diagnostic.Properties["conflictCount"]);
        Assert.Equal("3200", diagnostic.Properties["parentMillimeters"]);
        Assert.Equal("3000", diagnostic.Properties["childSumMillimeters"]);
        Assert.Contains("parent-dim-text", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_AddsWitnessLineEvidenceForMatchedDimension()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimLine("dim-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            DimLine("dim-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            DimText("dim-text-1", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);

        Assert.Equal("dim-line-1", dimension.SourcePrimitiveIds[1]);
        Assert.Contains("dim-witness-left", dimension.SourcePrimitiveIds);
        Assert.Contains("dim-witness-right", dimension.SourcePrimitiveIds);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("perpendicular witness/extension line", StringComparison.OrdinalIgnoreCase));
        Assert.True(dimension.Confidence.Value >= 0.9);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.detected");
        Assert.Equal("2", diagnostic.Properties["witnessLineCount"]);
    }

    [Fact]
    public async Task ScanAsync_KeepsDimensionBoundsCompactWhenWitnessLinesAreLong()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimLine("long-witness-left", new PlanPoint(100, 120), new PlanPoint(100, 326)),
            DimLine("long-witness-right", new PlanPoint(300, 120), new PlanPoint(300, 326)),
            DimText("dim-text-1", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);

        Assert.Contains("long-witness-left", dimension.SourcePrimitiveIds);
        Assert.Contains("long-witness-right", dimension.SourcePrimitiveIds);
        Assert.True(dimension.Bounds.Top > 300);
        Assert.True(dimension.Bounds.Height < 45);
    }

    [Fact]
    public async Task ScanAsync_SuppressesDuplicateDimensionTextOnSameMatchedLine()
    {
        var document = CreateDocument(
            DimLine("dim-line-1", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimLine("dim-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            DimLine("dim-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            DimText("dim-text-1", "3000 mm", new PlanRect(168, 334, 70, 16)),
            DimText("dim-text-duplicate", "3000 mm", new PlanRect(170, 336, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Contains("dim-text-1", dimension.SourcePrimitiveIds);
        Assert.Contains("dim-text-duplicate", dimension.SourcePrimitiveIds);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("Suppressed 1 duplicate dimension annotation", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.duplicates_suppressed");
        Assert.Equal("2", diagnostic.Properties["rawDimensionCount"]);
        Assert.Equal("1", diagnostic.Properties["keptDimensionCount"]);
        Assert.Equal("1", diagnostic.Properties["suppressedDimensionCount"]);
    }

    [Fact]
    public async Task ScanAsync_PrefersWitnessSupportedDimensionLineOverCloserPlainLine()
    {
        var document = CreateDocument(
            DimLine("supported-dim-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimLine("supported-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            DimLine("supported-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            DimLine("closer-plain-line", new PlanPoint(100, 338), new PlanPoint(300, 338)),
            DimText("dim-text-1", "3.00 m", new PlanRect(170, 342, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);

        Assert.Contains("supported-dim-line", dimension.SourcePrimitiveIds);
        Assert.DoesNotContain("closer-plain-line", dimension.SourcePrimitiveIds);
        Assert.Equal(200, dimension.DrawingLength);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("supported-witness-left", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_UsesCalibrationToPreferExpectedLengthDimensionLine()
    {
        var document = CreateDocument(
            PdfText("scale-text", "SCALE: 1:100", new PlanRect(430, 430, 90, 16)),
            PdfLine("expected-dim-line", new PlanPoint(100, 320), new PlanPoint(185, 320)),
            PdfLine("expected-witness-left", new PlanPoint(100, 305), new PlanPoint(100, 335)),
            PdfLine("expected-witness-right", new PlanPoint(185, 305), new PlanPoint(185, 335)),
            PdfLine("wrong-close-dim-line", new PlanPoint(100, 338), new PlanPoint(300, 338)),
            PdfLine("wrong-close-witness-left", new PlanPoint(100, 318), new PlanPoint(100, 358)),
            PdfLine("wrong-close-witness-right", new PlanPoint(300, 318), new PlanPoint(300, 358)),
            PdfText("dim-text-1", "3000 mm", new PlanRect(160, 342, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.InRange(result.Calibration.MillimetersPerDrawingUnit!.Value, 35.27, 35.28);
        var dimension = Assert.Single(result.Dimensions);
        Assert.Contains("expected-dim-line", dimension.SourcePrimitiveIds);
        Assert.DoesNotContain("wrong-close-dim-line", dimension.SourcePrimitiveIds);
        Assert.Equal(85, dimension.DrawingLength);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("Calibration expected drawing length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_SuppressesConflictingDimensionTextsOnSameMatchedLine()
    {
        var document = CreateDocument(
            PdfText("scale-text", "SCALE: 1:100", new PlanRect(430, 430, 90, 16)),
            PdfLine("shared-dim-line", new PlanPoint(100, 320), new PlanPoint(200, 320)),
            PdfLine("shared-witness-left", new PlanPoint(100, 305), new PlanPoint(100, 335)),
            PdfLine("shared-witness-right", new PlanPoint(200, 305), new PlanPoint(200, 335)),
            PdfText("weaker-dim-text", "3000 mm", new PlanRect(130, 334, 70, 16)),
            PdfText("better-dim-text", "4000 mm", new PlanRect(132, 352, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal("4000 mm", dimension.NormalizedText);
        Assert.Contains("better-dim-text", dimension.SourcePrimitiveIds);
        Assert.DoesNotContain("weaker-dim-text", dimension.SourcePrimitiveIds);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("Suppressed conflicting dimension annotation", StringComparison.OrdinalIgnoreCase)
                && item.Contains("3000 mm", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.same_line_conflicts_suppressed");
        Assert.Equal("1", diagnostic.Properties["suppressedDimensionCount"]);
        Assert.Contains("weaker-dim-text", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_DoesNotMatchImplausibleUnlayeredLineWhenCalibrationIsReliable()
    {
        var document = CreateDocument(
            PdfText("scale-text", "SCALE: 1:100", new PlanRect(430, 430, 90, 16)),
            PdfLine("wrong-dim-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            PdfLine("wrong-witness-left", new PlanPoint(100, 305), new PlanPoint(100, 335)),
            PdfLine("wrong-witness-right", new PlanPoint(300, 305), new PlanPoint(300, 335)),
            PdfText("dim-text-1", "3000 mm", new PlanRect(168, 334, 70, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public async Task ScanAsync_MatchesUnlayeredAlignedDimensionWithWitnessLines()
    {
        var document = CreateDocument(
            PdfLine("aligned-dim-line", new PlanPoint(120, 320), new PlanPoint(280, 400)),
            PdfLine("aligned-witness-left", new PlanPoint(111.056, 337.889), new PlanPoint(128.944, 302.111)),
            PdfLine("aligned-witness-right", new PlanPoint(271.056, 417.889), new PlanPoint(288.944, 382.111)),
            PdfText("aligned-dim-text", "4.00 m", new PlanRect(190, 352, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);

        Assert.Equal(DimensionOrientation.Aligned, dimension.Orientation);
        Assert.Contains("aligned-dim-line", dimension.SourcePrimitiveIds);
        Assert.Contains("aligned-witness-left", dimension.SourcePrimitiveIds);
        Assert.Contains("aligned-witness-right", dimension.SourcePrimitiveIds);
        Assert.InRange(dimension.DrawingLength!.Value, 178.88, 178.89);
        Assert.InRange(dimension.MillimetersPerDrawingUnit!.Value, 22.36, 22.37);
        Assert.Contains(
            dimension.Evidence,
            item => item.Contains("Matched nearby aligned dimension line", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.detected");
        Assert.Equal("1", diagnostic.Properties["alignedCount"]);
        Assert.Equal("2", diagnostic.Properties["witnessLineCount"]);
    }

    [Fact]
    public async Task ScanAsync_MatchesUnlayeredHorizontalDimensionWithWitnessLines()
    {
        var document = CreateDocument(
            PdfLine("pdf-dim-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            PdfLine("pdf-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            PdfLine("pdf-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            PdfText("pdf-dim-text", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var dimension = Assert.Single(result.Dimensions);
        Assert.Equal(DimensionOrientation.Horizontal, dimension.Orientation);
        Assert.Contains("pdf-dim-line", dimension.SourcePrimitiveIds);
        Assert.Contains("pdf-witness-left", dimension.SourcePrimitiveIds);
        Assert.Contains("pdf-witness-right", dimension.SourcePrimitiveIds);
        Assert.Equal(200, dimension.DrawingLength);
    }

    [Fact]
    public async Task ScanAsync_DoesNotMatchUnlayeredHorizontalLineWithoutWitnesses()
    {
        var document = CreateDocument(
            PdfLine("ordinary-plan-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            PdfText("near-dimension-text", "3.00 m", new PlanRect(170, 334, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public async Task ScanAsync_DoesNotMatchUnlayeredAlignedLineWithoutWitnesses()
    {
        var document = CreateDocument(
            PdfLine("plain-diagonal-line", new PlanPoint(120, 320), new PlanPoint(280, 400)),
            PdfText("near-dimension-text", "4.00 m", new PlanRect(190, 352, 60, 16)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Empty(result.Dimensions);
    }

    [Fact]
    public async Task ScanAsync_LimitsDenseDimensionLineCandidatesButKeepsDimensionLayerGeometry()
    {
        var noise = Enumerable.Range(0, 40)
            .Select(index => PdfLine(
                $"noise-{index}",
                new PlanPoint(20 + index, 20 + index),
                new PlanPoint(50 + index, 20 + index)))
            .Cast<PlanPrimitive>()
            .ToArray();
        var primitives = new List<PlanPrimitive>
        {
            DimLine("limited-dim-line", new PlanPoint(100, 320), new PlanPoint(300, 320)),
            DimLine("limited-witness-left", new PlanPoint(100, 294), new PlanPoint(100, 326)),
            DimLine("limited-witness-right", new PlanPoint(300, 294), new PlanPoint(300, 326)),
            DimText("limited-dim-text", "3000 mm", new PlanRect(168, 334, 70, 16))
        };
        primitives.AddRange(noise);
        var document = CreateDocument(primitives.ToArray());

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                MaxDimensionLineCandidatesPerPage = 6,
                MaxDimensionLineMatchCandidatesPerText = 4
            });

        var dimension = Assert.Single(result.Dimensions);
        Assert.Contains("limited-dim-line", dimension.SourcePrimitiveIds);
        Assert.Contains("limited-witness-left", dimension.SourcePrimitiveIds);
        Assert.Contains("limited-witness-right", dimension.SourcePrimitiveIds);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "dimensions.text_candidate_pool.pruned"));
        Assert.Equal("47", diagnostic.Properties["lineCandidateCountBeforePruning"]);
        Assert.Equal("1", diagnostic.Properties["dimensionTextCandidateCount"]);
        Assert.Equal("0", diagnostic.Properties["localLimitAppliedCount"]);
        Assert.True(int.Parse(diagnostic.Properties["uniqueRetainedLineCandidateCount"], CultureInfo.InvariantCulture) < 47);
    }

    private static PlanDocument CreateDocument(params PlanPrimitive[] primitives)
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
            "dimension-test",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 500),
                    allPrimitives)
            });
    }

    private static LinePrimitive WallLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static LinePrimitive DimLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "LINE", "A-DIM")
        };

    private static TextPrimitive DimText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "TEXT", "A-DIM")
        };

    private static LinePrimitive PdfLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Source = Source(sourceId, "line", null, "pdf")
        };

    private static TextPrimitive PdfText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Source = Source(sourceId, "word", null, "pdf")
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string entityType,
        string? layer,
        string sourceFormat = "test") =>
        new()
        {
            SourceFormat = sourceFormat,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Paper
        };
}
