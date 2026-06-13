using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class GridAxisDetectionTests
{
    [Fact]
    public async Task ScanAsync_DetectsLabeledGridAxesFromGridLayer()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridDocument());

        Assert.Equal(2, result.GridAxes.Count);
        Assert.Contains(result.GridAxes, axis => axis.Orientation == GridAxisOrientation.Vertical && axis.Label == "A");
        Assert.Contains(result.GridAxes, axis => axis.Orientation == GridAxisOrientation.Horizontal && axis.Label == "1");
        Assert.All(result.GridAxes, axis => Assert.Equal("page:1:main-floorplan", axis.SourceRegionId));
        Assert.Contains(result.GridAxes.SelectMany(axis => axis.LabelSourcePrimitiveIds), sourceId => sourceId == "grid-label-a");
        Assert.Contains(result.GridAxes.SelectMany(axis => axis.Evidence), evidence => evidence.Contains("classified Grid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "grid_axes.detected");
        Assert.Empty(result.Walls);
    }

    [Fact]
    public async Task JsonExport_IncludesGridAxesWithLabelsAndEvidence()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridDocument());

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var gridAxes = parsed.RootElement.GetProperty("gridAxes");
        Assert.Equal(2, gridAxes.GetArrayLength());

        var vertical = gridAxes.EnumerateArray().First(axis => axis.GetProperty("orientation").GetString() == nameof(GridAxisOrientation.Vertical));
        Assert.Equal("A", vertical.GetProperty("label").GetString());
        Assert.Contains(vertical.GetProperty("sourceLayers").EnumerateArray(), layer => layer.GetString() == "A-GRID");
        Assert.True(vertical.GetProperty("labelSourcePrimitiveIds").GetArrayLength() > 0);
        Assert.True(vertical.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task SvgRenderer_DrawsGridAxisLayer()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridDocument());

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("id=\"grid-axes\"", svg);
        Assert.Contains("class=\"grid-axis\"", svg);
        Assert.Contains("class=\"grid-label\"", svg);
    }

    [Fact]
    public async Task ScanAsync_DetectsGridBaySpacingsBetweenAdjacentAxes()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridBayDocument());

        Assert.Equal(5, result.GridAxes.Count);
        Assert.Equal(3, result.GridBaySpacings.Count);

        var verticalBay = Assert.Single(
            result.GridBaySpacings,
            bay => bay.AxisOrientation == GridAxisOrientation.Vertical
                && bay.FirstAxisLabel == "A"
                && bay.SecondAxisLabel == "B");

        Assert.Equal(2000, verticalBay.DrawingDistance);
        Assert.Equal(2, verticalBay.DistanceMeters);
        Assert.Equal("page:1:main-floorplan", verticalBay.SourceRegionId);
        Assert.Contains("grid-a", verticalBay.SourcePrimitiveIds);
        Assert.Contains("grid-b", verticalBay.SourcePrimitiveIds);
        Assert.Contains(verticalBay.Evidence, evidence => evidence.Contains("both grid axes have labels", StringComparison.Ordinal));

        Assert.Equal(2, result.GridBaySpacings.Count(bay => bay.AxisOrientation == GridAxisOrientation.Vertical));
        Assert.Single(result.GridBaySpacings, bay => bay.AxisOrientation == GridAxisOrientation.Horizontal);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "grid_bays.detected");
        Assert.Equal("3", diagnostic.Properties["bayCount"]);
        Assert.Equal("3", diagnostic.Properties["calibratedBayCount"]);
    }

    [Fact]
    public async Task JsonExport_IncludesGridBaySpacingsWithMeasurements()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridBayDocument());

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var bays = parsed.RootElement.GetProperty("gridBaySpacings");
        Assert.Equal(3, bays.GetArrayLength());

        var vertical = bays
            .EnumerateArray()
            .First(bay => bay.GetProperty("firstAxisLabel").GetString() == "A");

        Assert.Equal(nameof(GridAxisOrientation.Vertical), vertical.GetProperty("axisOrientation").GetString());
        Assert.Equal("B", vertical.GetProperty("secondAxisLabel").GetString());
        Assert.Equal(2000, vertical.GetProperty("drawingDistance").GetDouble());
        Assert.Equal(2, vertical.GetProperty("distanceMeters").GetDouble());
        Assert.Contains(vertical.GetProperty("sourceLayers").EnumerateArray(), layer => layer.GetString() == "A-GRID");
        Assert.True(vertical.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GeoJsonExporter_IncludesGridBaySpacingFeature()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridBayDocument());

        var geoJson = PlanTraceGeoJsonExporter.Serialize(
            result,
            new PlanTraceGeoJsonExportOptions { WriteIndented = false });
        using var parsed = JsonDocument.Parse(geoJson);

        var feature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(item => item.GetProperty("properties").GetProperty("featureType").GetString() == "gridBaySpacing"
                && item.GetProperty("properties").GetProperty("firstAxisLabel").GetString() == "A");

        Assert.Equal("LineString", feature.GetProperty("geometry").GetProperty("type").GetString());
        Assert.Equal("A", feature.GetProperty("properties").GetProperty("firstAxisLabel").GetString());
        Assert.Equal(2, feature.GetProperty("properties").GetProperty("distanceMeters").GetDouble());
    }

    [Fact]
    public async Task SvgRenderer_DrawsGridBayLayer()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateGridBayDocument());

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("id=\"grid-bays\"", svg);
        Assert.Contains("class=\"grid-bay\"", svg);
        Assert.Contains("A to B", svg);
    }

    [Fact]
    public async Task ScanAsync_DetectsUnlayeredPdfGridAxisFromEndpointBubbleLabel()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "unlayered-pdf-grid",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(800, 600),
                        new PlanPrimitive[]
                        {
                            PdfLine("pdf-grid-b", new PlanPoint(300, 80), new PlanPoint(300, 500)),
                            PdfText("pdf-grid-b-label", "B", new PlanRect(294, 52, 12, 14)),
                            PdfRect("pdf-grid-b-bubble", new PlanRect(284, 42, 32, 32))
                        })
                }));

        var axis = Assert.Single(result.GridAxes);

        Assert.Equal(GridAxisOrientation.Vertical, axis.Orientation);
        Assert.Equal("B", axis.Label);
        Assert.Contains("pdf-grid-b", axis.SourcePrimitiveIds);
        Assert.Contains("pdf-grid-b-label", axis.LabelSourcePrimitiveIds);
        Assert.Contains("pdf-grid-b-bubble", axis.SourcePrimitiveIds);
        Assert.Contains(axis.Evidence, item => item.Contains("endpoint label B", StringComparison.Ordinal));
        Assert.Contains(axis.Evidence, item => item.Contains("grid bubble geometry", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            sourceId => sourceId.StartsWith("pdf-grid-b", StringComparison.Ordinal));

        var detected = Assert.Single(result.Diagnostics.Messages, message => message.Code == "grid_axes.detected");
        Assert.Equal("1", detected.Properties["inferredCount"]);
    }

    [Fact]
    public async Task ScanAsync_DetectsEndpointGridAxisInDenseUnlayeredPdfGeometry()
    {
        var primitives = new List<PlanPrimitive>
        {
            PdfLine("pdf-grid-g", new PlanPoint(320, 90), new PlanPoint(320, 510)),
            PdfText("pdf-grid-g-label", "G", new PlanRect(314, 50, 12, 14)),
            PdfRect("pdf-grid-g-bubble", new PlanRect(304, 40, 32, 32))
        };

        for (var index = 0; index < 2500; index++)
        {
            var y = 120 + (index % 360);
            var x = 60 + ((index / 360) * 22);
            primitives.Add(PdfLine(
                $"pdf-dense-line-{index}",
                new PlanPoint(x, y),
                new PlanPoint(x + 10, y)));
        }

        for (var index = 0; index < 300; index++)
        {
            primitives.Add(PdfText(
                $"pdf-far-grid-like-label-{index}",
                ((char)('A' + (index % 26))).ToString(),
                new PlanRect(710, 80 + (index % 420), 12, 14)));
        }

        var result = await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "dense-unlayered-pdf-grid",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(800, 600),
                        primitives)
                }),
            new ScannerOptions { DetectObjectCandidates = false });

        var axis = Assert.Single(result.GridAxes);

        Assert.Equal(GridAxisOrientation.Vertical, axis.Orientation);
        Assert.Equal("G", axis.Label);
        Assert.Contains("pdf-grid-g", axis.SourcePrimitiveIds);
        Assert.Contains("pdf-grid-g-label", axis.LabelSourcePrimitiveIds);
        Assert.DoesNotContain(axis.SourcePrimitiveIds, sourceId => sourceId.StartsWith("pdf-dense-line-", StringComparison.Ordinal));

        var detected = Assert.Single(result.Diagnostics.Messages, message => message.Code == "grid_axes.detected");
        Assert.Equal("1", detected.Properties["axisCount"]);
        Assert.Equal("1", detected.Properties["inferredCount"]);
    }

    [Fact]
    public async Task ScanAsync_DoesNotInferShortUnlayeredTickAsGridAxis()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "short-unlayered-grid-like-tick",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(800, 600),
                        new PlanPrimitive[]
                        {
                            PdfLine("pdf-short-tick", new PlanPoint(320, 90), new PlanPoint(320, 112)),
                            PdfText("pdf-short-tick-label", "G", new PlanRect(314, 50, 12, 14)),
                            PdfRect("pdf-short-tick-bubble", new PlanRect(304, 40, 32, 32))
                        })
                }),
            new ScannerOptions { DetectObjectCandidates = false });

        Assert.Empty(result.GridAxes);
    }

    [Fact]
    public async Task ScanAsync_DoesNotInferGridAxisFromStrongWallLayer()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "wall-layer-with-grid-like-label",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(800, 600),
                        new PlanPrimitive[]
                        {
                            WallLayerLine("wall-line", new PlanPoint(300, 80), new PlanPoint(300, 500)),
                            PdfText("near-label", "B", new PlanRect(294, 52, 12, 14)),
                            PdfRect("near-label-bubble", new PlanRect(284, 42, 32, 32))
                        })
                }));

        Assert.Empty(result.GridAxes);
    }

    private static PlanDocument CreateGridDocument() =>
        new(
            "grid-axis-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        GridLine("grid-a", new PlanPoint(200, 60), new PlanPoint(200, 460)),
                        GridLabel("grid-label-a", "A", new PlanRect(190, 24, 20, 16)),
                        GridLine("grid-1", new PlanPoint(80, 220), new PlanPoint(650, 220)),
                        GridLabel("grid-label-1", "1", new PlanRect(40, 212, 16, 16))
                    })
            });

    private static PlanDocument CreateGridBayDocument() =>
        new PlanDocument(
            "grid-bay-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(4600, 3400),
                    new PlanPrimitive[]
                    {
                        GridLine("grid-a", new PlanPoint(100, 100), new PlanPoint(100, 3100)),
                        GridLabel("grid-label-a", "A", new PlanRect(90, 64, 20, 16)),
                        GridLine("grid-b", new PlanPoint(2100, 100), new PlanPoint(2100, 3100)),
                        GridLabel("grid-label-b", "B", new PlanRect(2090, 64, 20, 16)),
                        GridLine("grid-c", new PlanPoint(4100, 100), new PlanPoint(4100, 3100)),
                        GridLabel("grid-label-c", "C", new PlanRect(4090, 64, 20, 16)),
                        GridLine("grid-1", new PlanPoint(80, 500), new PlanPoint(4300, 500)),
                        GridLabel("grid-label-1", "1", new PlanRect(28, 492, 16, 16)),
                        GridLine("grid-2", new PlanPoint(80, 2500), new PlanPoint(4300, 2500)),
                        GridLabel("grid-label-2", "2", new PlanRect(28, 2492, 16, 16))
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                Properties = new Dictionary<string, string>
                {
                    ["dxf.defaultDrawingUnits"] = "Millimeters"
                }
            }
        };

    private static LinePrimitive GridLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-GRID",
            Source = Source(sourceId, "LINE")
        };

    private static TextPrimitive GridLabel(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-GRID",
            Source = Source(sourceId, "TEXT")
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

    private static RectanglePrimitive PdfRect(string sourceId, PlanRect bounds) =>
        new(bounds)
        {
            SourceId = sourceId,
            Source = Source(sourceId, "path", null, "pdf")
        };

    private static LinePrimitive WallLayerLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string entityType,
        string? layer = "A-GRID",
        string sourceFormat = "test") =>
        new()
        {
            SourceFormat = sourceFormat,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Model
        };
}
