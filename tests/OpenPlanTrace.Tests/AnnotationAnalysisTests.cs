using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class AnnotationAnalysisTests
{
    [Fact]
    public async Task ScanAsync_ExtractsGeneralNotesWithMarkedItems()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("notes-heading", "GENERAL NOTES", new PlanRect(560, 120, 130, 18)),
            NoteText("notes-1", "1. VERIFY ALL DIMENSIONS", new PlanRect(560, 145, 180, 18)),
            NoteText("notes-2", "2. COORDINATE WITH STRUCTURAL", new PlanRect(560, 170, 220, 18))
        }));

        var block = Assert.Single(result.Annotations.Where(annotation => annotation.Kind == PlanAnnotationKind.GeneralNotes));

        Assert.Equal("Notes", block.Label);
        Assert.True(block.Confidence.Value >= 0.65);
        Assert.Contains("notes-heading", block.SourcePrimitiveIds);
        Assert.Contains(block.Evidence, item => item.Contains("heading", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "annotations.detected");

        var markedItem = Assert.Single(block.Items.Where(item => item.Marker == "1"));
        Assert.Equal(PlanAnnotationItemKind.Note, markedItem.Kind);
        Assert.Equal("VERIFY ALL DIMENSIONS", markedItem.Text);
        Assert.Contains("notes-1", markedItem.SourcePrimitiveIds);
        Assert.Contains(markedItem.Evidence, item => item.Contains("marker 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesLegendFromHeadingText()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("legend-heading", "SYMBOL LEGEND", new PlanRect(560, 120, 130, 18)),
            NoteText("legend-pump", "P PUMP", new PlanRect(560, 145, 80, 18)),
            NoteText("legend-valve", "V VALVE", new PlanRect(560, 170, 80, 18))
        }));

        var block = Assert.Single(result.Annotations.Where(annotation => annotation.Kind == PlanAnnotationKind.Legend));

        Assert.Contains("legend-heading", block.SourcePrimitiveIds);
        Assert.Contains(block.Items, item => item.Kind == PlanAnnotationItemKind.Heading);
        Assert.Contains(block.Items, item => item.Kind == PlanAnnotationItemKind.LegendEntry && item.Marker == "P");
    }

    [Fact]
    public async Task ScanAsync_ClassifiesRevisionTableRowsWithDateBody()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("revision-heading", "REVISION HISTORY", new PlanRect(560, 120, 150, 18)),
            NoteText("revision-header", "REV DATE DESCRIPTION", new PlanRect(560, 145, 180, 18)),
            NoteText("revision-a", "A 2026-06-06 ISSUED FOR REVIEW", new PlanRect(560, 170, 250, 18)),
            NoteText("revision-b", "B 2026-06-20 IFC PACKAGE", new PlanRect(560, 195, 220, 18))
        }));

        var block = Assert.Single(result.Annotations.Where(annotation => annotation.Kind == PlanAnnotationKind.RevisionTable));

        Assert.Contains("revision-heading", block.SourcePrimitiveIds);
        Assert.Contains(block.Evidence, item => item.Contains("REVISION HISTORY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(block.Items, item => item.Kind == PlanAnnotationItemKind.Heading);

        var revisionA = Assert.Single(block.Items.Where(item => item.Marker == "A"));
        Assert.Equal(PlanAnnotationItemKind.RevisionEntry, revisionA.Kind);
        Assert.Equal("2026-06-06 ISSUED FOR REVIEW", revisionA.Text);
        Assert.Contains("revision-a", revisionA.SourcePrimitiveIds);
        Assert.Contains(revisionA.Evidence, item => item.Contains("marker A", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_AssociatesKeynoteItemsWithPlanMarkerReferences()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("keynotes-heading", "KEYNOTES", new PlanRect(560, 120, 120, 18)),
            NoteText("keynote-1", "1. FIRE RATED WALL", new PlanRect(560, 145, 170, 18)),
            NoteText("keynote-2", "2. EXISTING DOOR", new PlanRect(560, 170, 150, 18)),
            MarkerText("keynote-marker-1", "1", new PlanRect(220, 170, 10, 10)),
            new RectanglePrimitive(new PlanRect(215, 165, 22, 22))
            {
                SourceId = "keynote-bubble-1",
                Layer = "A-ANNO"
            }
        }));

        var block = Assert.Single(result.Annotations.Where(annotation => annotation.Kind == PlanAnnotationKind.Keynotes));
        var item = Assert.Single(block.Items.Where(item => item.Marker == "1"));
        var reference = Assert.Single(item.References);

        Assert.Equal("1", reference.Marker);
        Assert.Equal("1", reference.Text);
        Assert.True(reference.Confidence.Value >= 0.7);
        Assert.Contains("keynote-marker-1", reference.SourcePrimitiveIds);
        Assert.Contains("keynote-bubble-1", reference.SourcePrimitiveIds);
        Assert.Contains(reference.Evidence, item => item.Contains("matched plan marker 1", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "annotations.references.detected");
    }

    [Fact]
    public async Task JsonExport_IncludesAnnotationBlocksAndItems()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("notes-heading", "GENERAL NOTES", new PlanRect(560, 120, 130, 18)),
            NoteText("notes-1", "1. VERIFY ALL DIMENSIONS", new PlanRect(560, 145, 180, 18))
        }));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetString());

        var annotations = document.RootElement.GetProperty("annotations");
        var annotation = Assert.Single(annotations.EnumerateArray());
        Assert.Equal(nameof(PlanAnnotationKind.GeneralNotes), annotation.GetProperty("kind").GetString());
        Assert.True(annotation.GetProperty("sourcePrimitiveIds").GetArrayLength() >= 2);
        Assert.Contains(annotation.GetProperty("sourceLayers").EnumerateArray(), layer => layer.GetString() == "A-NOTE");

        var markedItem = annotation
            .GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("marker").GetString() == "1");
        Assert.Equal("VERIFY ALL DIMENSIONS", markedItem.GetProperty("text").GetString());
        Assert.True(markedItem.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task JsonExport_IncludesRevisionTableAnnotationKind()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("revision-heading", "REVISION TABLE", new PlanRect(560, 120, 130, 18)),
            NoteText("revision-a", "A 2026-06-06 ISSUED FOR REVIEW", new PlanRect(560, 145, 250, 18))
        }));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);

        var annotation = Assert.Single(document.RootElement.GetProperty("annotations").EnumerateArray());
        Assert.Equal(nameof(PlanAnnotationKind.RevisionTable), annotation.GetProperty("kind").GetString());

        var revisionItem = annotation
            .GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("marker").GetString() == "A");
        Assert.Equal(nameof(PlanAnnotationItemKind.RevisionEntry), revisionItem.GetProperty("kind").GetString());
        Assert.Equal("2026-06-06 ISSUED FOR REVIEW", revisionItem.GetProperty("text").GetString());
    }

    [Fact]
    public async Task JsonExport_IncludesAnnotationItemReferences()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationDocument(new PlanPrimitive[]
        {
            NoteText("keynotes-heading", "KEYNOTES", new PlanRect(560, 120, 120, 18)),
            NoteText("keynote-1", "1. FIRE RATED WALL", new PlanRect(560, 145, 170, 18)),
            MarkerText("keynote-marker-1", "1", new PlanRect(220, 170, 10, 10))
        }));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var document = JsonDocument.Parse(json);

        var annotation = Assert.Single(document.RootElement.GetProperty("annotations").EnumerateArray());
        var item = annotation
            .GetProperty("items")
            .EnumerateArray()
            .First(item => item.GetProperty("marker").GetString() == "1");
        var reference = Assert.Single(item.GetProperty("references").EnumerateArray());

        Assert.Equal("1", reference.GetProperty("marker").GetString());
        Assert.Equal("1", reference.GetProperty("text").GetString());
        Assert.Contains(reference.GetProperty("sourcePrimitiveIds").EnumerateArray(), id => id.GetString() == "keynote-marker-1");
    }

    private static PlanDocument CreateAnnotationDocument(IEnumerable<PlanPrimitive> annotationPrimitives)
    {
        var primitives = new List<PlanPrimitive>
        {
            new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(500, 100))) { SourceId = "wall-top" },
            new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 100), new PlanPoint(500, 400))) { SourceId = "wall-right" },
            new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 400), new PlanPoint(100, 400))) { SourceId = "wall-bottom" },
            new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 400), new PlanPoint(100, 100))) { SourceId = "wall-left" },
            new RectanglePrimitive(new PlanRect(700, 650, 260, 120)) { SourceId = "title-grid" },
            new TextPrimitive("PROJECT OPENPLANTRACE", new PlanRect(720, 670, 150, 18)) { SourceId = "title-project" },
            new TextPrimitive("A-101", new PlanRect(720, 700, 50, 18)) { SourceId = "title-sheet" }
        };
        primitives.AddRange(annotationPrimitives);

        return new PlanDocument(
            "annotation-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    primitives)
            });
    }

    private static TextPrimitive NoteText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-NOTE",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "TEXT",
                Layer = "A-NOTE",
                DrawingSpace = SourceDrawingSpace.Paper
            }
        };

    private static TextPrimitive MarkerText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-ANNO",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "TEXT",
                Layer = "A-ANNO",
                DrawingSpace = SourceDrawingSpace.Paper
            }
        };
}
