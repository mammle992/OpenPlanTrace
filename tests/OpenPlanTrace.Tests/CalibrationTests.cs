using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class CalibrationTests
{
    [Fact]
    public async Task ScanAsync_ExtractsPdfTitleBlockScale()
    {
        var document = new PlanDocument(
            "pdf-scale",
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
                        PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18))
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

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Equal(100, result.Calibration.ScaleRatio);
        Assert.InRange(result.Calibration.MillimetersPerDrawingUnit!.Value, 35.27, 35.28);
        Assert.Contains(result.Calibration.Evidence, item => item.Kind == CalibrationEvidenceKind.ScaleText);
        Assert.Contains(result.Diagnostics.StageReports, report => report.Stage == "calibration");
    }

    [Fact]
    public async Task ScanAsync_UsesDxfDefaultDrawingUnitsForRoomMeasurements()
    {
        var document = new PlanDocument(
            "dxf-units",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(3600, 2600),
                    new PlanPrimitive[]
                    {
                        DxfLine("wall-top", new PlanPoint(100, 100), new PlanPoint(3100, 100)),
                        DxfLine("wall-right", new PlanPoint(3100, 100), new PlanPoint(3100, 2100)),
                        DxfLine("wall-bottom", new PlanPoint(3100, 2100), new PlanPoint(100, 2100)),
                        DxfLine("wall-left", new PlanPoint(100, 2100), new PlanPoint(100, 100))
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                Properties = new Dictionary<string, string>
                {
                    ["format"] = "dxf",
                    ["dxf.defaultDrawingUnits"] = "Millimeters"
                }
            }
        };

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Equal(1, result.Calibration.MillimetersPerDrawingUnit);
        Assert.Contains(result.Walls, wall => wall.LengthMeters is >= 3);
        Assert.Contains(result.Rooms, room => room.AreaSquareMeters is > 5.99 and < 6.01);
        Assert.All(result.Walls, wall => Assert.Equal("document:scale-group:1", wall.MeasurementScaleGroupId));
        Assert.All(result.Rooms, room => Assert.Equal("document:scale-group:1", room.MeasurementScaleGroupId));
    }

    [Fact]
    public void Analyze_MatchesDimensionTextToNearbyDimensionLine()
    {
        var document = new PlanDocument(
            "dimension-scale",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        DimLine("dim-line", new PlanPoint(100, 300), new PlanPoint(200, 300)),
                        DimText("dim-text", "3.00 m", new PlanRect(126, 316, 50, 14))
                    })
            });

        var calibration = PlanCalibrationAnalyzer.Analyze(document);

        Assert.True(calibration.HasReliableMeasurementScale);
        Assert.Equal(30, calibration.MillimetersPerDrawingUnit);
        Assert.Contains(calibration.Evidence, item => item.Kind == CalibrationEvidenceKind.DimensionText);
    }

    [Fact]
    public void Analyze_GroupsScaleEvidenceBySheetScope()
    {
        var document = PdfDocument(
            "multi-scale-groups",
            PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18)),
            PdfText("detail-scale", "DETAIL SCALE 1:20", new PlanRect(120, 120, 120, 18)));
        var regions = new[]
        {
            new SheetRegion("page:1:title", 1, RegionKind.TitleBlock, new PlanRect(700, 650, 260, 120), Confidence.High),
            new SheetRegion("page:1:main", 1, RegionKind.MainFloorPlan, new PlanRect(50, 50, 500, 400), Confidence.High)
        };

        var calibration = PlanCalibrationAnalyzer.Analyze(document, regions);

        Assert.Equal(2, calibration.ScaleGroups.Count);
        var titleScale = Assert.Single(calibration.ScaleGroups, group => group.Scope == CalibrationScaleScope.TitleBlock);
        Assert.Equal(100, titleScale.ScaleRatio);
        Assert.Contains("title-scale", titleScale.SourcePrimitiveIds);
        Assert.Contains("page:1:title", titleScale.SourceRegionIds);
        Assert.Equal(new PlanRect(720, 730, 90, 18), titleScale.Bounds);

        var mainScale = Assert.Single(calibration.ScaleGroups, group => group.Scope == CalibrationScaleScope.MainFloorPlan);
        Assert.Equal(20, mainScale.ScaleRatio);
        Assert.Contains("detail-scale", mainScale.SourcePrimitiveIds);
        Assert.Contains("page:1:main", mainScale.SourceRegionIds);
        Assert.Equal(new PlanRect(120, 120, 120, 18), mainScale.Bounds);

        Assert.Equal(
            mainScale.Id,
            calibration.SelectMeasurementScaleGroup(1, new PlanRect(180, 180, 20, 20), "page:1:main")?.Id);
        Assert.Null(calibration.SelectMeasurementScaleGroup(1, new PlanRect(900, 60, 20, 20)));
    }

    [Fact]
    public void Analyze_DoesNotPromoteKeyPlanScaleToGlobalCalibration()
    {
        var document = PdfDocument(
            "key-plan-scale-only",
            PdfText("key-scale", "KEY PLAN SCALE 1:200", new PlanRect(740, 100, 120, 18)),
            PdfLine("main-wall", new PlanPoint(100, 200), new PlanPoint(500, 200), "A-WALL"));
        var regions = new[]
        {
            new SheetRegion("page:1:main", 1, RegionKind.MainFloorPlan, new PlanRect(50, 50, 620, 520), Confidence.High),
            new SheetRegion("page:1:key", 1, RegionKind.KeyPlan, new PlanRect(700, 70, 220, 170), Confidence.High)
        };

        var calibration = PlanCalibrationAnalyzer.Analyze(document, regions);

        var keyPlanScale = Assert.Single(calibration.ScaleGroups, group => group.Scope == CalibrationScaleScope.KeyPlan);
        Assert.Equal(200, keyPlanScale.ScaleRatio);
        Assert.Contains("key-scale", keyPlanScale.SourcePrimitiveIds);
        Assert.False(calibration.HasReliableMeasurementScale);
        Assert.Null(calibration.MillimetersPerDrawingUnit);
        Assert.Null(calibration.SelectMeasurementScaleGroup(1, new PlanRect(120, 200, 200, 12), "page:1:main"));
        Assert.Equal(
            keyPlanScale.Id,
            calibration.SelectMeasurementScaleGroup(1, new PlanRect(760, 110, 20, 12), "page:1:key")?.Id);
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenMultipleScaleGroupsAppearOnOnePage()
    {
        var document = PdfDocument(
            "multi-scale-sheet",
            PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18)),
            PdfText("detail-scale", "DETAIL SCALE 1:20", new PlanRect(120, 120, 120, 18)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.ScaleGroups.Count >= 2);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "calibration.multiple_scales_on_page");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("1", diagnostic.Properties["pageNumber"]);
        Assert.Contains("title-block", diagnostic.Properties["sourceRegionIds"]);
        Assert.Contains("main-floorplan", diagnostic.Properties["sourceRegionIds"]);
        Assert.Contains("title-scale", diagnostic.SourcePrimitiveIds);
        Assert.Contains("detail-scale", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_AssignsMeasurementScaleGroupBySourceRegion()
    {
        var document = PdfDocument(
            "multi-scale-measured-sheet",
            PdfLine("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100), "A-WALL"),
            PdfLine("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 400), "A-WALL"),
            PdfLine("wall-bottom", new PlanPoint(500, 400), new PlanPoint(100, 400), "A-WALL"),
            PdfLine("wall-left", new PlanPoint(100, 400), new PlanPoint(100, 100), "A-WALL"),
            PdfRect("title-grid", new PlanRect(700, 650, 260, 120)),
            PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18)),
            PdfText("main-scale", "PLAN SCALE 1:20", new PlanRect(120, 120, 120, 18)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var mainScale = Assert.Single(result.Calibration.ScaleGroups, group => group.Scope == CalibrationScaleScope.MainFloorPlan);
        Assert.All(result.Walls, wall => Assert.Equal(mainScale.Id, wall.MeasurementScaleGroupId));
        Assert.Contains(result.Walls, wall => wall.LengthMeters is > 2.81 and < 2.83);
        Assert.Contains(result.Rooms, room => room.MeasurementScaleGroupId == mainScale.Id);

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        Assert.Contains(
            parsed.RootElement.GetProperty("walls").EnumerateArray(),
            wall => wall.GetProperty("measurementScaleGroupId").GetString() == mainScale.Id);
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenMixedScaleMeasurementsCannotBeAssigned()
    {
        var document = PdfDocument(
            "ambiguous-multi-scale-measurements",
            PdfLine("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100), "A-WALL"),
            PdfLine("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 400), "A-WALL"),
            PdfLine("wall-bottom", new PlanPoint(500, 400), new PlanPoint(100, 400), "A-WALL"),
            PdfLine("wall-left", new PlanPoint(100, 400), new PlanPoint(100, 100), "A-WALL"),
            PdfRect("title-grid", new PlanRect(700, 650, 260, 120)),
            PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 710, 90, 18)),
            PdfText("detail-title-scale", "DETAIL SCALE 1:20", new PlanRect(720, 740, 130, 18)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.ScaleGroups.Count >= 2);
        Assert.NotEmpty(result.Walls);
        Assert.Contains(result.Walls, wall => wall.LengthMeters is null && wall.MeasurementScaleGroupId is null);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "measurement_scale.unassigned_detections");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("measurement-scale-provenance", diagnostic.Stage);
        Assert.Equal(DiagnosticScope.Calibration, diagnostic.Scope);
        Assert.Equal("1", diagnostic.Properties["pageNumber"]);
        Assert.True(int.Parse(diagnostic.Properties["unassignedWallCount"]) > 0);
        Assert.Contains("wall-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("page:1:wall:", diagnostic.Properties["sampleDetectionIds"]);

        Assert.Contains(
            result.Quality.Issues,
            issue => issue.Code == "quality.measurement_scale_provenance_missing");
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenNotToScaleTextIsPresent()
    {
        var document = PdfDocument(
            "not-to-scale-note",
            PdfText("title-scale", "SCALE: 1:100", new PlanRect(720, 730, 90, 18)),
            PdfText("nts-note", "DETAIL NTS", new PlanRect(120, 120, 80, 18)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "calibration.not_to_scale_text.detected");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("1", diagnostic.Properties["noScaleTextCount"]);
        Assert.Contains("nts-note", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_UsesMetricScaleBarEndpointLabels()
    {
        var document = new PlanDocument(
            "pdf-scale-bar",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        PdfLine("scale-line", new PlanPoint(100, 300), new PlanPoint(300, 300), "SCALE-BAR"),
                        PdfText("scale-zero", "0", new PlanRect(92, 308, 16, 14), "SCALE-BAR"),
                        PdfText("scale-five", "5 m", new PlanRect(286, 308, 40, 14), "SCALE-BAR")
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

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Calibration.HasReliableMeasurementScale);
        Assert.Equal(PlanMeasurementUnit.PdfPoint, result.Calibration.DrawingUnit);
        Assert.Equal(PlanMeasurementUnit.Millimeter, result.Calibration.RealWorldUnit);
        Assert.InRange(result.Calibration.MillimetersPerDrawingUnit!.Value, 24.99, 25.01);

        var evidence = Assert.Single(result.Calibration.Evidence, item => item.Kind == CalibrationEvidenceKind.ScaleBar);
        Assert.Equal("scale-line", evidence.SourcePrimitiveId);
        Assert.Equal("5 m", evidence.Text);
        Assert.Contains("Scale bar maps", evidence.Description, StringComparison.Ordinal);

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Contains(
            parsed.RootElement.GetProperty("calibration").GetProperty("evidence").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "ScaleBar");
        Assert.Contains(
            parsed.RootElement.GetProperty("calibration").GetProperty("scaleGroups").EnumerateArray(),
            item => item.GetProperty("sourcePrimitiveIds").EnumerateArray().Any(source => source.GetString() == "scale-line"));
        Assert.Contains(
            parsed.RootElement.GetProperty("calibration").GetProperty("scaleGroups").EnumerateArray(),
            item => item.TryGetProperty("bounds", out var bounds)
                && bounds.ValueKind == JsonValueKind.Object
                && bounds.GetProperty("width").GetDouble() > 0
                && item.TryGetProperty("sourceRegionIds", out var regionIds)
                && regionIds.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public void Analyze_MatchesScaleBarEndpointLabelsInDensePage()
    {
        var primitives = new List<PlanPrimitive>
        {
            PdfLine("scale-line", new PlanPoint(100, 300), new PlanPoint(300, 300), "SCALE-BAR"),
            PdfText("scale-zero", "0", new PlanRect(92, 308, 16, 14), "SCALE-BAR"),
            PdfText("scale-five", "5 m", new PlanRect(286, 308, 40, 14), "SCALE-BAR")
        };

        for (var index = 0; index < 900; index++)
        {
            var x = 20 + (index % 30) * 18;
            var y = 20 + (index / 30) * 10;
            primitives.Add(PdfLine(
                $"dense-line-{index}",
                new PlanPoint(x, y),
                new PlanPoint(x + 12, y)));
        }

        for (var index = 0; index < 240; index++)
        {
            primitives.Add(PdfText(
                $"dense-label-{index}",
                index % 2 == 0 ? "0" : "5 m",
                new PlanRect(520 + (index % 10) * 8, 40 + (index / 10) * 12, 24, 10)));
        }

        var calibration = PlanCalibrationAnalyzer.Analyze(
            new PlanDocument(
                "dense-scale-bar",
                new[] { new PlanPage(1, new PlanSize(800, 600), primitives) })
            {
                Metadata = new PlanMetadata
                {
                    Properties = new Dictionary<string, string> { ["format"] = "pdf" }
                }
            });

        Assert.True(calibration.HasReliableMeasurementScale);
        Assert.InRange(calibration.MillimetersPerDrawingUnit!.Value, 24.99, 25.01);
        var evidence = Assert.Single(calibration.Evidence, item => item.Kind == CalibrationEvidenceKind.ScaleBar);
        Assert.Equal("scale-line", evidence.SourcePrimitiveId);
    }

    [Fact]
    public void Analyze_DoesNotCreateScaleBarFromDimensionLabelWithoutZeroEndpoint()
    {
        var document = new PlanDocument(
            "not-scale-bar",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        PdfLine("scale-like-line", new PlanPoint(100, 300), new PlanPoint(300, 300), "SCALE-BAR"),
                        PdfText("dimension-label", "5 m", new PlanRect(286, 308, 40, 14), "SCALE-BAR")
                    })
            });

        var calibration = PlanCalibrationAnalyzer.Analyze(document);

        Assert.DoesNotContain(calibration.Evidence, item => item.Kind == CalibrationEvidenceKind.ScaleBar);
        Assert.False(calibration.HasReliableMeasurementScale);
    }

    private static PlanDocument PdfDocument(string id, params PlanPrimitive[] primitives) =>
        new(
            id,
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    primitives)
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

    private static LinePrimitive PdfLine(string sourceId, PlanPoint start, PlanPoint end, string? layer = null) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "pdf", "line", layer)
        };

    private static RectanglePrimitive PdfRect(string sourceId, PlanRect bounds) =>
        new(bounds)
        {
            SourceId = sourceId,
            Source = Source(sourceId, "pdf", "rectangle")
        };

    private static TextPrimitive PdfText(string sourceId, string text, PlanRect bounds, string? layer = null) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "pdf", "word", layer)
        };

    private static LinePrimitive DxfLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "dxf", "LINE", layer: "A-WALL", drawingSpace: SourceDrawingSpace.Model)
        };

    private static LinePrimitive DimLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "DIM",
            Source = Source(sourceId, "test", "LINE", layer: "DIM")
        };

    private static TextPrimitive DimText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "DIM",
            Source = Source(sourceId, "test", "TEXT", layer: "DIM")
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string sourceFormat,
        string entityType,
        string? layer = null,
        SourceDrawingSpace drawingSpace = SourceDrawingSpace.Paper) =>
        new()
        {
            SourceFormat = sourceFormat,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = drawingSpace
        };
}
