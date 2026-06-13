namespace OpenPlanTrace.Tests;

public sealed class LayerAnalysisTests
{
    [Fact]
    public void Analyze_ClassifiesLayersFromNamesAndGeometry()
    {
        var document = new PlanDocument(
            "layer-analysis",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("wall-1", "A-WALL", new PlanPoint(10, 10), new PlanPoint(210, 10)),
                        Line("wall-2", "A-WALL", new PlanPoint(10, 30), new PlanPoint(210, 30)),
                        Text("dim-1", "DIMENSIONS", "1200 mm", new PlanRect(20, 60, 70, 14)),
                        Symbol("eq-1", "HVAC-VENT", "AHU", new PlanRect(100, 100, 30, 30)),
                        Text("note-1", "NOTES", "verify dimensions", new PlanRect(20, 120, 120, 14))
                    })
            });

        var analysis = LayerAnalyzer.Analyze(document);

        Assert.Equal(LayerCategory.Wall, analysis.Find("A-WALL")!.LikelyCategory);
        Assert.True(analysis.Find("A-WALL")!.Confidence.Value >= 0.7);
        Assert.Equal(LayerCategory.Dimension, analysis.Find("DIMENSIONS")!.LikelyCategory);
        Assert.Equal(LayerCategory.HVAC, analysis.Find("HVAC-VENT")!.LikelyCategory);
        Assert.Equal(LayerCategory.Text, analysis.Find("NOTES")!.LikelyCategory);
        Assert.Contains(analysis.Find("A-WALL")!.Evidence, evidence => evidence.Contains("wall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_AppliesLayerCategoryOverridesBeforeHeuristics()
    {
        var document = new PlanDocument(
            "layer-overrides",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("candidate-1", "XREF-123-LINEWORK", new PlanPoint(10, 10), new PlanPoint(210, 10)),
                        Text("note-1", "NOTES", "verify dimensions", new PlanRect(20, 120, 120, 14))
                    })
            });

        var analysis = LayerAnalyzer.Analyze(
            document,
            new[]
            {
                new LayerCategoryOverride("XREF-*-LINEWORK", LayerCategory.Wall),
                new LayerCategoryOverride("NOTES", LayerCategory.FireSafety)
            });

        var wallLayer = analysis.Find("XREF-123-LINEWORK")!;
        Assert.Equal(LayerCategory.Wall, wallLayer.LikelyCategory);
        Assert.Equal(Confidence.High, wallLayer.Confidence);
        Assert.Contains(wallLayer.Evidence, item => item.Contains("override", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LayerCategory.FireSafety, analysis.Find("NOTES")!.LikelyCategory);
    }

    [Fact]
    public void Analyze_ProvidesRankedCategoryScoresForAmbiguousLayers()
    {
        var document = new PlanDocument(
            "layer-alternatives",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("ambiguous-1", "A-DOOR-WINDOW", new PlanPoint(20, 20), new PlanPoint(160, 20)),
                        Line("ambiguous-2", "A-DOOR-WINDOW", new PlanPoint(20, 40), new PlanPoint(160, 40))
                    })
            });

        var layer = LayerAnalyzer.Analyze(document).Find("A-DOOR-WINDOW")!;

        Assert.Equal(LayerCategory.Door, layer.LikelyCategory);
        Assert.Equal(LayerCategory.Door, layer.CategoryScores[0].Category);
        Assert.Contains(layer.CategoryScores, score => score.Category == LayerCategory.Window && score.Score >= 0.7);
        Assert.Contains(layer.CategoryScores[0].Evidence, item => item.Contains("door", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Scanner_AddsLayerConsistencyDiagnosticsForUnmatchedDoorLayer()
    {
        var document = new PlanDocument(
            "layer-consistency",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("door-line", "A-DOOR", new PlanPoint(20, 20), new PlanPoint(160, 20)),
                        Text("label", "NOTES", "door schedule", new PlanRect(20, 50, 110, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Contains(result.LayerAnalysis.Layers, layer => layer.Name == "A-DOOR" && layer.LikelyCategory == LayerCategory.Door);
        Assert.Contains(result.Diagnostics.StageReports, report => report.Stage == "layer-analysis");
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "layers.opening_hint.no_openings");
    }

    [Fact]
    public async Task Scanner_AddsAmbiguousLayerCategoryDiagnosticForCloseScores()
    {
        var document = new PlanDocument(
            "layer-ambiguity",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("ambiguous-1", "A-DOOR-WINDOW", new PlanPoint(20, 20), new PlanPoint(160, 20)),
                        Line("ambiguous-2", "A-DOOR-WINDOW", new PlanPoint(20, 40), new PlanPoint(160, 40))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var diagnostic = Assert.Single(result.Diagnostics.Messages, item => item.Code == "layers.category_ambiguous");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("A-DOOR-WINDOW", diagnostic.Properties["layerName"]);
        Assert.Equal("Door", diagnostic.Properties["topCategory"]);
        Assert.Equal("Window", diagnostic.Properties["secondCategory"]);
        Assert.Contains("ambiguous-1", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task Scanner_SeparatesLayerConsistencyDiagnosticsBySourceFormat()
    {
        var document = new PlanDocument(
            "layer-source-format",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("pdf-door", "A-DOOR", new PlanPoint(20, 20), new PlanPoint(160, 20), "pdf"),
                        Line("dxf-door", "A-DOOR", new PlanPoint(20, 80), new PlanPoint(160, 80), "dxf")
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var diagnostics = result.Diagnostics.Messages
            .Where(item => item.Code == "layers.opening_hint.no_openings")
            .OrderBy(item => item.Properties["sourceFormat"], StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Equal("dxf", diagnostics[0].Properties["sourceFormat"]);
        Assert.Equal("pdf", diagnostics[1].Properties["sourceFormat"]);
        Assert.Single(diagnostics[0].SourcePrimitiveIds);
        Assert.Single(diagnostics[1].SourcePrimitiveIds);
    }

    private static LinePrimitive Line(
        string sourceId,
        string layer,
        PlanPoint start,
        PlanPoint end,
        string sourceFormat = "test") =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, layer, "LINE", sourceFormat: sourceFormat)
        };

    private static TextPrimitive Text(string sourceId, string layer, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, layer, "TEXT")
        };

    private static SymbolPrimitive Symbol(string sourceId, string layer, string name, PlanRect bounds) =>
        new(name, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, layer, "INSERT", blockName: name)
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string layer,
        string entityType,
        string? blockName = null,
        string sourceFormat = "test") =>
        new()
        {
            SourceFormat = sourceFormat,
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            BlockName = blockName,
            DrawingSpace = SourceDrawingSpace.Model
        };
}
