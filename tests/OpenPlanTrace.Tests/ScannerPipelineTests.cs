namespace OpenPlanTrace.Tests;

public sealed class ScannerPipelineTests
{
    [Fact]
    public async Task ScanAsync_DetectsCoreFloorplanOutputs()
    {
        var document = new PlanDocument(
            "synthetic-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(500, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 100), new PlanPoint(500, 400))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 400), new PlanPoint(100, 400))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 400), new PlanPoint(100, 100))) { SourceId = "wall-left" },
                        new RectanglePrimitive(new PlanRect(700, 650, 260, 120)) { SourceId = "title-grid" },
                        new TextPrimitive("PROJECT OPENPLANTRACE", new PlanRect(720, 670, 150, 18)) { SourceId = "title-project" },
                        new TextPrimitive("A-101", new PlanRect(720, 700, 50, 18)) { SourceId = "title-sheet" },
                        new TextPrimitive("REV 01", new PlanRect(840, 700, 50, 18)) { SourceId = "title-revision" },
                        new TextPrimitive("GENERAL NOTES", new PlanRect(650, 120, 130, 18)) { SourceId = "notes-heading" },
                        new TextPrimitive("VERIFY ALL DIMENSIONS", new PlanRect(650, 145, 180, 18)) { SourceId = "notes-body" },
                        new TextPrimitive("12'-0\"", new PlanRect(170, 520, 60, 18)) { SourceId = "dim-1" },
                        new TextPrimitive("8'-0\"", new PlanRect(300, 520, 60, 18)) { SourceId = "dim-2" }
                    })
            });

        var scanner = new OpenPlanTraceScanner();
        var result = await scanner.ScanAsync(document);

        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Sheet);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.MainFloorPlan);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.TitleBlock);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Notes);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Dimensions);
        Assert.Contains(result.Annotations, annotation => annotation.Kind == PlanAnnotationKind.GeneralNotes);
        Assert.True(result.Walls.Count >= 4);
        Assert.True(result.WallGraph.Nodes.Count >= 4);
        Assert.True(result.WallGraph.Edges.Count >= 4);
        Assert.Contains(result.Rooms, room => room.Bounds.Width > 350 && room.Bounds.Height > 250);
        Assert.Contains(result.Rooms, room => room.Boundary.Count >= 4);
        Assert.False(result.Diagnostics.HasErrors);
    }

    [Fact]
    public async Task ScanAsync_CropsMainFloorplanToDenseContentAndLeavesDimensionTextOutside()
    {
        var dimensionText = new TextPrimitive("8 400", new PlanRect(300, 570, 60, 16)) { SourceId = "dim-text-8400" };
        var secondDimensionText = new TextPrimitive("13 000", new PlanRect(420, 570, 70, 16)) { SourceId = "dim-text-13000" };
        var document = new PlanDocument(
            "region-crop-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    new PlanPrimitive[]
                    {
                        new RectanglePrimitive(new PlanRect(0, 0, 1000, 800)) { SourceId = "sheet-frame" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 150), new PlanPoint(540, 150))) { SourceId = "plan-wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(540, 150), new PlanPoint(540, 430))) { SourceId = "plan-wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(540, 430), new PlanPoint(250, 430))) { SourceId = "plan-wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 430), new PlanPoint(250, 150))) { SourceId = "plan-wall-left" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(390, 150), new PlanPoint(390, 430))) { SourceId = "plan-wall-middle" },
                        new TextPrimitive("Sov 10.7 m2", new PlanRect(430, 255, 80, 16)) { SourceId = "room-label" },
                        dimensionText,
                        secondDimensionText,
                        new RectanglePrimitive(new PlanRect(720, 620, 240, 140)) { SourceId = "title-grid" },
                        new TextPrimitive("Prosjekt OpenPlanTrace", new PlanRect(735, 640, 150, 16)) { SourceId = "title-project" },
                        new TextPrimitive("Malestokk 1:100", new PlanRect(735, 685, 120, 16)) { SourceId = "title-scale" },
                        new TextPrimitive("Dato 2026-06-08", new PlanRect(735, 725, 120, 16)) { SourceId = "title-date" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var main = Assert.Single(result.SheetRegions.Where(region => region.Kind == RegionKind.MainFloorPlan));
        Assert.True(main.Bounds.Left > 180, $"Main region should crop empty left sheet space, got {main.Bounds}.");
        Assert.True(main.Bounds.Top > 100, $"Main region should crop empty top sheet space, got {main.Bounds}.");
        Assert.True(main.Bounds.Right < 620, $"Main region should stop near floorplan content, got {main.Bounds}.");
        Assert.True(main.Bounds.Bottom < 520, $"Main region should leave exterior dimension text outside, got {main.Bounds}.");
        Assert.False(main.Bounds.Contains(dimensionText.Bounds.Center), "Exterior dimension text should not be inside the main floorplan region.");
        Assert.Contains(
            result.SheetRegions,
            region => region.Kind == RegionKind.Dimensions && region.Bounds.Contains(secondDimensionText.Bounds.Center));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "layout.main_region.content_refined");
    }

    [Fact]
    public async Task ScanAsync_ClassifiesDoorOpeningWhenGapHasArc()
    {
        var document = new PlanDocument(
            "synthetic-opening",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(220, 100))) { SourceId = "wall-left-run" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 100), new PlanPoint(400, 100))) { SourceId = "wall-right-run" },
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var scanner = new OpenPlanTraceScanner();
        var result = await scanner.ScanAsync(document);

        Assert.Contains(result.Openings, opening => opening.Type == OpeningType.Door);
        Assert.Contains(result.Openings, opening => opening.Operation == OpeningOperation.Hinged);
    }

    [Fact]
    public async Task ScanAsync_ReportsPipelineStageProgressWhenObserverIsProvided()
    {
        var document = new PlanDocument(
            "progress-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 300),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(220, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 100), new PlanPoint(220, 200))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 200), new PlanPoint(100, 200))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 200), new PlanPoint(100, 100))) { SourceId = "wall-left" }
                    })
            });
        var progress = new CapturingProgress();

        await new OpenPlanTraceScanner().ScanAsync(document, progress: progress);

        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Started && item.StageName == "wall-graph");
        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Completed && item.StageName == "wall-graph");
        Assert.All(
            progress.Events.Where(item => item.Kind == PipelineStageProgressKind.Completed),
            item => Assert.True(item.OutputDetectionCount >= item.InputDetectionCount));
    }

    private sealed class CapturingProgress : IProgress<PipelineStageProgress>
    {
        public List<PipelineStageProgress> Events { get; } = new();

        public void Report(PipelineStageProgress value) => Events.Add(value);
    }
}
