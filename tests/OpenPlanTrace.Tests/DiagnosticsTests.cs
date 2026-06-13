using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task ScanAsync_AddsStructuredLayerMismatchDiagnostics()
    {
        var document = new PlanDocument(
            "diagnostic-layer-mismatch",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("door-line", "A-DOOR", new PlanPoint(20, 20), new PlanPoint(160, 20)),
                        Text("note", "NOTES", "door schedule", new PlanRect(20, 50, 110, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "layers.opening_hint.no_openings");
        var stage = Assert.Single(result.Diagnostics.StageReports, report => report.Stage == "layer-consistency");

        Assert.Equal(DiagnosticScope.Layer, diagnostic.Scope);
        Assert.Equal(1, diagnostic.PageNumber);
        Assert.Contains("door-line", diagnostic.SourcePrimitiveIds);
        Assert.Equal("A-DOOR", diagnostic.Properties["layerName"]);
        Assert.Equal("Door", diagnostic.Properties["likelyCategory"]);
        Assert.True(stage.DiagnosticCount >= 1);
        Assert.True(stage.WarningCount >= 1);
        Assert.True(result.Diagnostics.WarningCount >= 1);
    }

    [Fact]
    public async Task ScanAsync_AddsLowConfidenceOpeningDiagnostics()
    {
        var document = new PlanDocument(
            "diagnostic-opening",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Line("wall-left-run", "A-WALL", new PlanPoint(100, 100), new PlanPoint(220, 100)),
                        Line("wall-right-run", "A-WALL", new PlanPoint(250, 100), new PlanPoint(400, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "openings.low_confidence_candidate");
        var stage = Assert.Single(result.Diagnostics.StageReports, report => report.Stage == "openings");

        Assert.Equal(DiagnosticScope.Detection, diagnostic.Scope);
        Assert.Equal(1, diagnostic.PageNumber);
        Assert.Equal("GenericOpening", diagnostic.Properties["openingType"]);
        Assert.Equal("PassThrough", diagnostic.Properties["operation"]);
        Assert.Contains("page:1:opening:", diagnostic.Properties["openingId"]);
        Assert.True(stage.InfoCount >= 1);
    }

    [Fact]
    public async Task JsonExporter_IncludesStructuredDiagnosticFields()
    {
        var document = new PlanDocument(
            "diagnostic-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("door-line", "A-DOOR", new PlanPoint(20, 20), new PlanPoint(160, 20)),
                        Text("note", "NOTES", "door schedule", new PlanRect(20, 50, 110, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var diagnostics = parsed.RootElement.GetProperty("diagnostics");
        var stage = diagnostics.GetProperty("stages")
            .EnumerateArray()
            .First(item => item.GetProperty("stage").GetString() == "layer-consistency");
        var message = diagnostics.GetProperty("messages")
            .EnumerateArray()
            .First(item => item.GetProperty("code").GetString() == "layers.opening_hint.no_openings");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.True(diagnostics.GetProperty("warningCount").GetInt32() >= 1);
        Assert.True(stage.GetProperty("diagnosticCount").GetInt32() >= 1);
        Assert.True(stage.GetProperty("warningCount").GetInt32() >= 1);
        Assert.Equal("Layer", message.GetProperty("scope").GetString());
        Assert.Contains("door-line", message.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("A-DOOR", message.GetProperty("properties").GetProperty("layerName").GetString());
    }

    private static LinePrimitive Line(string sourceId, string layer, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, layer, "LINE")
        };

    private static TextPrimitive Text(string sourceId, string layer, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, layer, "TEXT")
        };

    private static PrimitiveSourceMetadata Source(string sourceId, string layer, string entityType) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Model
        };
}
