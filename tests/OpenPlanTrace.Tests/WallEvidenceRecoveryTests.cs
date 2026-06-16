namespace OpenPlanTrace.Tests;

public sealed class WallEvidenceRecoveryTests
{
    [Fact]
    public async Task WallEvidenceRefinement_RecoversOnlyUniqueSupportedWallBands()
    {
        var document = new PlanDocument(
            "wall-evidence-recovery-gates",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("recover-face-a", new PlanPoint(100, 118), new PlanPoint(400, 118)),
                        Line("recover-face-b", new PlanPoint(100, 124), new PlanPoint(400, 124)),
                        Line("recover-duplicate-a", new PlanPoint(100, 118.2), new PlanPoint(400, 118.2)),
                        Line("recover-duplicate-b", new PlanPoint(100, 123.8), new PlanPoint(400, 123.8)),
                        Line("unsupported-face-a", new PlanPoint(150, 200), new PlanPoint(260, 200)),
                        Line("unsupported-face-b", new PlanPoint(150, 205), new PlanPoint(260, 205)),
                        Line("surface-face-a", new PlanPoint(100, 258), new PlanPoint(400, 258)),
                        Line("surface-face-b", new PlanPoint(100, 264), new PlanPoint(400, 264))
                    })
            });
        var context = new ScanContext(document, new ScannerOptions { DefaultWallThickness = 6 });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 285)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(400, 50), new PlanPoint(400, 285)));
        context.SurfacePatterns.Add(new SurfacePatternCandidate(
            "page:1:surface-pattern:001",
            1,
            SurfacePatternKind.DenseOrthogonalGrid,
            SurfacePatternOrientation.Orthogonal,
            new PlanRect(90, 245, 320, 35),
            null,
            16,
            8,
            8,
            48,
            6,
            6,
            null,
            ExcludedFromWallDetection: true,
            ExcludedFromStructuralTopology: true,
            new[] { "surface-pattern-source" },
            Confidence.High,
            RequiresReview: true,
            new[] { "synthetic detail pattern" }));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = context.Walls
            .Where(wall => wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var recoveredWall = Assert.Single(recovered);
        Assert.Contains("recover-face-a", recoveredWall.SourcePrimitiveIds);
        Assert.Contains("recover-face-b", recoveredWall.SourcePrimitiveIds);
        Assert.DoesNotContain(recoveredWall.SourcePrimitiveIds, source => source.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Walls.SelectMany(wall => wall.SourcePrimitiveIds), source => source.StartsWith("unsupported-", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Walls.SelectMany(wall => wall.SourcePrimitiveIds), source => source.StartsWith("surface-face-", StringComparison.Ordinal));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(message => message.Code == "wall_evidence.missing_wall_bands_recovered"));
        Assert.Equal("1", diagnostic.Properties["recoveredWallCount"]);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotRecoverDenseParallelDetailBands()
    {
        var primitives = new List<PlanPrimitive>
        {
            Line("recover-face-a", new PlanPoint(100, 118), new PlanPoint(400, 118)),
            Line("recover-face-b", new PlanPoint(100, 124), new PlanPoint(400, 124))
        };
        for (var index = 0; index < 5; index++)
        {
            var y = 175 + (index * 6);
            primitives.Add(Line($"dense-detail-{index}-a", new PlanPoint(100, y), new PlanPoint(400, y)));
            primitives.Add(Line($"dense-detail-{index}-b", new PlanPoint(100, y + 3), new PlanPoint(400, y + 3)));
        }

        var document = new PlanDocument(
            "wall-evidence-dense-parallel-recovery-gate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    primitives)
            });
        var context = new ScanContext(document, new ScannerOptions { DefaultWallThickness = 6 });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 285)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(400, 50), new PlanPoint(400, 285)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = context.Walls
            .Where(wall => wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var recoveredWall = Assert.Single(recovered);
        Assert.Contains("recover-face-a", recoveredWall.SourcePrimitiveIds);
        Assert.Contains("recover-face-b", recoveredWall.SourcePrimitiveIds);
        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source.StartsWith("dense-detail-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RecoversShortSupportedWallSegments()
    {
        var document = new PlanDocument(
            "wall-evidence-short-segment-recovery",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(260, 220),
                    new PlanPrimitive[]
                    {
                        Line("short-supported-wall", new PlanPoint(100, 100), new PlanPoint(122, 100), "A-WALL"),
                        Line("short-door-detail", new PlanPoint(100, 140), new PlanPoint(122, 140), "A-DOOR")
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallFragmentLength = 4,
                MinWallLength = 36
            });
        context.LayerAnalysis = new PlanLayerAnalysis(new[]
        {
            Layer("A-WALL", LayerCategory.Wall),
            Layer("A-DOOR", LayerCategory.Door)
        });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 180)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(122, 50), new PlanPoint(122, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls.Where(wall => wall.SourcePrimitiveIds.Contains("short-supported-wall")));
        Assert.Equal(WallDetectionKind.SingleLine, recovered.DetectionKind);
        Assert.Contains(recovered.Evidence, item => item.Contains("short supported wall segment", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source == "short-door-detail");

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments, item => item.WallId == recovered.Id);
        Assert.Equal(WallEvidenceCategory.RecoveredWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.True(assessment.ScoreBreakdown.RecoverySupportScore > 0);

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages, item => item.Code == "wall_evidence.missing_wall_bands_recovered");
        Assert.Equal("1", diagnostic.Properties["recoveredShortWallCount"]);
        Assert.Equal("0", diagnostic.Properties["recoveredWallBandCount"]);
    }

    private static WallSegment HostWall(string sourceId, PlanPoint start, PlanPoint end) =>
        new($"wall-{sourceId}", 1, new PlanLineSegment(start, end), 4, Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            SourcePrimitiveIds = new[] { sourceId },
            Evidence = new[] { "test host wall" }
        };

    private static LinePrimitive Line(string sourceId, PlanPoint start, PlanPoint end, string? layer = null) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                Layer = layer,
                EntityType = "LINE",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static LayerSummary Layer(string name, LayerCategory category) =>
        new(
            name,
            "test",
            1,
            new Dictionary<PlanPrimitiveKind, int> { [PlanPrimitiveKind.Line] = 1 },
            22,
            new PlanRect(0, 0, 200, 200),
            category,
            Confidence.High,
            new[] { new LayerCategoryScore(category, Confidence.High.Value, new[] { "test layer classification" }) },
            new[] { "test layer classification" },
            new[] { 1 });
}
