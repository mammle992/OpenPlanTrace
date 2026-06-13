using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class TitleBlockAnalysisTests
{
    [Fact]
    public async Task ScanAsync_ExtractsStructuredTitleBlockFields()
    {
        var document = CreateTitleBlockDocument(new PlanPrimitive[]
        {
            TitleText("title-project", "PROJECT: OPENPLANTRACE INDUSTRIAL", new PlanRect(690, 620, 210, 16)),
            TitleText("title-sheet-number", "SHEET NO: A-101", new PlanRect(690, 646, 110, 16)),
            TitleText("title-sheet-title", "TITLE: FIRST FLOOR PLAN", new PlanRect(690, 672, 170, 16)),
            TitleText("title-revision", "REV 01", new PlanRect(830, 646, 55, 16)),
            TitleText("title-date", "DATE: 2026-06-04", new PlanRect(690, 698, 135, 16)),
            TitleText("title-scale", "SCALE: 1:100", new PlanRect(690, 724, 90, 16)),
            TitleText("title-discipline", "DISCIPLINE: ARCHITECTURAL", new PlanRect(820, 724, 155, 16))
        });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var titleBlock = Assert.Single(result.TitleBlocks);
        Assert.Equal("OPENPLANTRACE INDUSTRIAL", titleBlock.ProjectName);
        Assert.Equal("A-101", titleBlock.SheetNumber);
        Assert.Equal("FIRST FLOOR PLAN", titleBlock.SheetTitle);
        Assert.Equal("01", titleBlock.Revision);
        Assert.Equal("2026-06-04", titleBlock.IssueDate);
        Assert.Equal("1:100", titleBlock.Scale);
        Assert.Contains(titleBlock.Fields, field => field.Kind == TitleBlockFieldKind.Discipline && field.Value == "ARCHITECTURAL");
        Assert.All(titleBlock.Fields, field =>
        {
            Assert.NotEmpty(field.SourcePrimitiveIds);
            Assert.NotEmpty(field.Evidence);
            Assert.InRange(field.Confidence.Value, 0.45, 1.0);
        });
        Assert.Contains(result.Diagnostics.StageReports, report => report.Stage == "title-block-analysis");
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "title_block.fields.detected");
    }

    [Fact]
    public async Task ScanAsync_PairsNearbyTitleBlockLabelsWithValues()
    {
        var document = CreateTitleBlockDocument(new PlanPrimitive[]
        {
            TitleText("label-project", "PROJECT", new PlanRect(690, 620, 60, 16)),
            TitleText("value-project", "OPENPLANTRACE", new PlanRect(770, 620, 110, 16)),
            TitleText("label-sheet", "SHEET NO", new PlanRect(690, 646, 65, 16)),
            TitleText("value-sheet", "M-204", new PlanRect(770, 646, 58, 16)),
            TitleText("label-drawn", "DRAWN BY", new PlanRect(690, 672, 70, 16)),
            TitleText("value-drawn", "AB", new PlanRect(770, 672, 24, 16)),
            TitleText("label-checked", "CHECKED BY", new PlanRect(820, 672, 82, 16)),
            TitleText("value-checked", "CD", new PlanRect(915, 672, 24, 16))
        });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var titleBlock = Assert.Single(result.TitleBlocks);
        Assert.Equal("OPENPLANTRACE", titleBlock.ProjectName);
        Assert.Equal("M-204", titleBlock.SheetNumber);
        Assert.Null(titleBlock.SheetTitle);
        Assert.Contains(titleBlock.Fields, field => field.Kind == TitleBlockFieldKind.DrawnBy && field.Value == "AB");
        Assert.Contains(titleBlock.Fields, field => field.Kind == TitleBlockFieldKind.CheckedBy && field.Value == "CD");

        var drawnBy = titleBlock.FirstField(TitleBlockFieldKind.DrawnBy);
        Assert.NotNull(drawnBy);
        Assert.True(drawnBy.SourcePrimitiveIds.Count >= 2);
        Assert.Contains(drawnBy.Evidence, item => item.Contains("paired with adjacent text", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task JsonExport_IncludesTitleBlockSchemaAndFieldEvidence()
    {
        var document = CreateTitleBlockDocument(new PlanPrimitive[]
        {
            TitleText("title-project", "PROJECT: OPENPLANTRACE", new PlanRect(690, 620, 160, 16)),
            TitleText("title-sheet-number", "SHEET NO: A-101", new PlanRect(690, 646, 110, 16)),
            TitleText("title-scale", "SCALE: 1:100", new PlanRect(690, 672, 90, 16))
        });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());

        var titleBlocks = parsed.RootElement.GetProperty("titleBlocks");
        var titleBlock = Assert.Single(titleBlocks.EnumerateArray());
        Assert.Equal("A-101", titleBlock.GetProperty("sheetNumber").GetString());
        Assert.Equal("OPENPLANTRACE", titleBlock.GetProperty("projectName").GetString());
        Assert.True(titleBlock.GetProperty("fields").GetArrayLength() >= 3);

        var sheetNumber = titleBlock.GetProperty("fields")
            .EnumerateArray()
            .First(field => field.GetProperty("kind").GetString() == nameof(TitleBlockFieldKind.SheetNumber));
        Assert.Equal("A-101", sheetNumber.GetProperty("value").GetString());
        Assert.True(sheetNumber.GetProperty("sourcePrimitiveIds").GetArrayLength() > 0);
        Assert.True(sheetNumber.GetProperty("evidence").GetArrayLength() > 0);
    }

    private static PlanDocument CreateTitleBlockDocument(IEnumerable<PlanPrimitive> titlePrimitives)
    {
        var primitives = new List<PlanPrimitive>
        {
            WallLine("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
            WallLine("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 400)),
            WallLine("wall-bottom", new PlanPoint(500, 400), new PlanPoint(100, 400)),
            WallLine("wall-left", new PlanPoint(100, 400), new PlanPoint(100, 100)),
            TitleRect("title-grid", new PlanRect(660, 600, 300, 160))
        };
        primitives.AddRange(titlePrimitives);

        return new PlanDocument(
            "title-block-test",
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
    }

    private static LinePrimitive WallLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static RectanglePrimitive TitleRect(string sourceId, PlanRect bounds) =>
        new(bounds)
        {
            SourceId = sourceId,
            Layer = "A-TTLB",
            Source = Source(sourceId, "RECTANGLE", "A-TTLB")
        };

    private static TextPrimitive TitleText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-TTLB-TEXT",
            Source = Source(sourceId, "TEXT", "A-TTLB-TEXT")
        };

    private static PrimitiveSourceMetadata Source(string sourceId, string entityType, string layer) =>
        new()
        {
            SourceFormat = "pdf",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Paper
        };
}
