using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class WallLayerFilteringTests
{
    [Fact]
    public async Task ScanAsync_FiltersDimensionAndGridLayersFromWallDetection()
    {
        var document = new PlanDocument(
            "layer-filtered-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Line("wall-top", "A-WALL", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Line("wall-right", "A-WALL", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Line("wall-bottom", "A-WALL", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Line("wall-left", "A-WALL", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        Line("grid-a", "A-GRID", new PlanPoint(75, 220), new PlanPoint(550, 220)),
                        Line("grid-b", "A-GRID", new PlanPoint(300, 60), new PlanPoint(300, 400)),
                        Line("dim-line", "A-DIM", new PlanPoint(100, 405), new PlanPoint(500, 405)),
                        new TextPrimitive("4000 mm", new PlanRect(250, 418, 72, 16))
                        {
                            SourceId = "dim-text",
                            Layer = "A-DIM",
                            Source = Source("dim-text", "TEXT", "A-DIM")
                        }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain("grid", string.Join(",", wall.SourcePrimitiveIds), StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Walls, wall => Assert.DoesNotContain("dim-line", wall.SourcePrimitiveIds));
        Assert.Contains(result.Walls, wall => wall.Evidence.Any(item => item.Contains("classified Wall", StringComparison.OrdinalIgnoreCase)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.layer_filtered_candidates"));
        Assert.Contains("grid-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("grid-b", diagnostic.SourcePrimitiveIds);
        Assert.Contains("dim-line", diagnostic.SourcePrimitiveIds);
        Assert.Contains("Grid", diagnostic.Properties["categories"]);
        Assert.Contains("Dimension", diagnostic.Properties["categories"]);
    }

    [Fact]
    public async Task ScanAsync_KeepsUnlayeredLineworkEligibleForWalls()
    {
        var document = new PlanDocument(
            "unlayered-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(300, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(300, 100), new PlanPoint(300, 260))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(300, 260), new PlanPoint(100, 260))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 260), new PlanPoint(100, 100))) { SourceId = "wall-left" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.DoesNotContain(result.Diagnostics.Messages, message => message.Code == "walls.layer_filtered_candidates");
    }

    [Fact]
    public async Task ScanAsync_FiltersCompactUnlayeredObjectLineworkFromWalls()
    {
        var document = new PlanDocument(
            "compact-object-linework-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        UnlayeredLine("wall-top", new PlanPoint(100, 100), new PlanPoint(560, 100)),
                        UnlayeredLine("wall-right", new PlanPoint(560, 100), new PlanPoint(560, 360)),
                        UnlayeredLine("wall-bottom", new PlanPoint(560, 360), new PlanPoint(100, 360)),
                        UnlayeredLine("wall-left", new PlanPoint(100, 360), new PlanPoint(100, 100)),
                        UnlayeredLine("table-top", new PlanPoint(240, 190), new PlanPoint(340, 190)),
                        UnlayeredLine("table-right", new PlanPoint(340, 190), new PlanPoint(340, 250)),
                        UnlayeredLine("table-bottom", new PlanPoint(340, 250), new PlanPoint(240, 250)),
                        UnlayeredLine("table-left", new PlanPoint(240, 250), new PlanPoint(240, 190)),
                        UnlayeredLine("table-center-a", new PlanPoint(252, 220), new PlanPoint(328, 220)),
                        UnlayeredLine("table-center-b", new PlanPoint(290, 198), new PlanPoint(290, 242))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("table-", StringComparison.Ordinal)));
        Assert.Contains(
            result.ObjectCandidates,
            candidate => candidate.SourcePrimitiveIds.Any(sourceId => sourceId.StartsWith("table-", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.compact_linework_filtered"));
        Assert.Equal("1", diagnostic.Properties["clusterCount"]);
        Assert.Contains("table-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("table-center-b", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersCompactObjectLineworkDespiteWeakUnlayeredDimensionHint()
    {
        var document = new PlanDocument(
            "compact-object-linework-weak-dimension-layer-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        UnlayeredLine("wall-top", new PlanPoint(100, 100), new PlanPoint(560, 100)),
                        UnlayeredLine("wall-right", new PlanPoint(560, 100), new PlanPoint(560, 360)),
                        UnlayeredLine("wall-bottom", new PlanPoint(560, 360), new PlanPoint(100, 360)),
                        UnlayeredLine("wall-left", new PlanPoint(100, 360), new PlanPoint(100, 100)),
                        UnlayeredLine("cabinet-top", new PlanPoint(240, 190), new PlanPoint(340, 190)),
                        UnlayeredLine("cabinet-right", new PlanPoint(340, 190), new PlanPoint(340, 250)),
                        UnlayeredLine("cabinet-bottom", new PlanPoint(340, 250), new PlanPoint(240, 250)),
                        UnlayeredLine("cabinet-left", new PlanPoint(240, 250), new PlanPoint(240, 190)),
                        UnlayeredLine("cabinet-shelf", new PlanPoint(252, 220), new PlanPoint(328, 220)),
                        UnlayeredLine("cabinet-divider", new PlanPoint(290, 198), new PlanPoint(290, 242)),
                        UnlayeredText("dim-text-a", "4000 mm", new PlanRect(210, 410, 72, 16)),
                        UnlayeredText("dim-text-b", "2600 mm", new PlanRect(410, 255, 72, 16))
                    })
            });

        var layer = Assert.Single(LayerAnalyzer.Analyze(document).Layers);
        Assert.Equal(LayerCategory.Dimension, layer.LikelyCategory);
        Assert.True(layer.Confidence.Value < 0.45);

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("cabinet-", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.compact_linework_filtered"));
        Assert.Contains("cabinet-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("cabinet-divider", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersDenseOrthogonalSurfacePatternsFromWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80))
        };

        for (var index = 0; index <= 10; index++)
        {
            var offset = 400 + (index * 16);
            primitives.Add(UnlayeredLine(
                $"terrace-grid-v-{index}",
                new PlanPoint(offset, 96),
                new PlanPoint(offset, 256)));
            primitives.Add(UnlayeredLine(
                $"terrace-grid-h-{index}",
                new PlanPoint(400, 96 + (index * 16)),
                new PlanPoint(560, 96 + (index * 16))));
        }

        var document = new PlanDocument(
            "dense-orthogonal-surface-pattern-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("terrace-grid-", StringComparison.Ordinal)));

        var pattern = Assert.Single(result.SurfacePatterns);
        Assert.Equal(SurfacePatternKind.DenseOrthogonalGrid, pattern.Kind);
        Assert.Equal(SurfacePatternOrientation.Orthogonal, pattern.Orientation);
        Assert.True(pattern.ExcludedFromWallDetection);
        Assert.True(pattern.ExcludedFromStructuralTopology);
        Assert.True(pattern.RequiresReview);
        Assert.Equal(22, pattern.LineCount);
        Assert.Equal(11, pattern.HorizontalLineCount);
        Assert.Equal(11, pattern.VerticalLineCount);
        Assert.Contains("terrace-grid-v-0", pattern.SourcePrimitiveIds);
        Assert.Contains("terrace-grid-h-10", pattern.SourcePrimitiveIds);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.dense_orthogonal_pattern_filtered"));
        Assert.Equal("1", diagnostic.Properties["clusterCount"]);
        Assert.Equal("22", diagnostic.Properties["filteredLineCount"]);
        Assert.Contains("11h/11v", diagnostic.Properties["patterns"]);
        Assert.Contains("terrace-grid-v-0", diagnostic.SourcePrimitiveIds);
        Assert.Contains("terrace-grid-h-10", diagnostic.SourcePrimitiveIds);

        using var scanJson = JsonDocument.Parse(PlanTraceJsonExporter.Serialize(result));
        var exportedPattern = Assert.Single(scanJson.RootElement.GetProperty("surfacePatterns").EnumerateArray());
        Assert.Equal("DenseOrthogonalGrid", exportedPattern.GetProperty("kind").GetString());
        Assert.Equal(22, exportedPattern.GetProperty("lineCount").GetInt32());
        Assert.True(exportedPattern.GetProperty("excludedFromStructuralTopology").GetBoolean());
        var surfacePatternReview = Assert.Single(
            scanJson.RootElement.GetProperty("reviewQueue").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == ScanReviewQueueKinds.SurfacePatternReview
                && item.GetProperty("itemId").GetString() == pattern.Id);
        Assert.Equal("surfacePatterns", surfacePatternReview.GetProperty("detector").GetString());
        Assert.Equal(pattern.PageNumber, surfacePatternReview.GetProperty("pageNumber").GetInt32());
        Assert.Equal(pattern.SourcePrimitiveIds.Count, surfacePatternReview.GetProperty("sourcePrimitiveIds").GetArrayLength());
        Assert.Contains("dense non-structural surface/detail pattern", surfacePatternReview.GetProperty("recommendedAction").GetString());
        Assert.Equal("22", surfacePatternReview.GetProperty("properties").GetProperty("lineCount").GetString());

        var snapshot = PlanOverlaySnapshot.From(result);
        Assert.Equal(1, snapshot.ReviewQueueKindBreakdown[ScanReviewQueueKinds.SurfacePatternReview]);
        var snapshotPatternReview = Assert.Single(
            snapshot.Pages.Single(page => page.PageNumber == 1).ReviewQueue,
            item => item.Kind == ScanReviewQueueKinds.SurfacePatternReview);
        Assert.Equal(pattern.Id, snapshotPatternReview.ItemId);
        Assert.Equal(pattern.SourcePrimitiveIds.Count, snapshotPatternReview.SourcePrimitiveCount);

        using var placementJson = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        Assert.Equal(1, placementJson.RootElement.GetProperty("summary").GetProperty("surfacePatternCount").GetInt32());
        Assert.Equal(1, placementJson.RootElement.GetProperty("summary").GetProperty("pageSummaries")[0].GetProperty("surfacePatternCount").GetInt32());
        var placementPattern = Assert.Single(placementJson.RootElement.GetProperty("surfacePatterns").EnumerateArray());
        Assert.Equal("DenseOrthogonalGrid", placementPattern.GetProperty("kind").GetString());
        Assert.Contains("non-structural detail", placementPattern.GetProperty("recommendedAction").GetString());
    }

    [Fact]
    public async Task ScanAsync_FiltersWeakPerimeterFrameAroundDenseSurfaceGridFromWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
            UnlayeredLine("terrace-frame-top", new PlanPoint(404, 104), new PlanPoint(596, 104)),
            UnlayeredLine("terrace-frame-right", new PlanPoint(596, 104), new PlanPoint(596, 296)),
            UnlayeredLine("terrace-frame-bottom", new PlanPoint(596, 296), new PlanPoint(404, 296)),
            UnlayeredLine("terrace-frame-left", new PlanPoint(404, 296), new PlanPoint(404, 104))
        };

        for (var index = 0; index < 8; index++)
        {
            var coordinate = 430 + (index * 20);
            primitives.Add(UnlayeredLine(
                $"terrace-grid-v-{index}",
                new PlanPoint(coordinate, 130),
                new PlanPoint(coordinate, 270)));
            primitives.Add(UnlayeredLine(
                $"terrace-grid-h-{index}",
                new PlanPoint(430, coordinate - 300),
                new PlanPoint(570, coordinate - 300)));
        }

        var document = new PlanDocument(
            "dense-surface-grid-frame-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 440),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(
            result.Walls.Count == 4,
            DumpWallFilteringResult(result));
        Assert.All(result.Walls, wall =>
        {
            Assert.DoesNotContain(
                wall.SourcePrimitiveIds,
                sourceId => sourceId.StartsWith("terrace-grid-", StringComparison.Ordinal));
            Assert.DoesNotContain(
                wall.SourcePrimitiveIds,
                sourceId => sourceId.StartsWith("terrace-frame-", StringComparison.Ordinal));
        });

        var pattern = Assert.Single(result.SurfacePatterns);
        Assert.Equal(SurfacePatternKind.DenseOrthogonalGrid, pattern.Kind);
        Assert.True(pattern.ExcludedFromWallDetection);
        Assert.Contains("terrace-frame-top", pattern.SourcePrimitiveIds);
        Assert.Contains("terrace-frame-right", pattern.SourcePrimitiveIds);
        Assert.Contains("terrace-frame-bottom", pattern.SourcePrimitiveIds);
        Assert.Contains("terrace-frame-left", pattern.SourcePrimitiveIds);
        Assert.True(pattern.LineCount >= 20);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.dense_orthogonal_pattern_filtered"));
        Assert.Contains("terrace-frame-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("terrace-frame-left", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_QueuesSurfacePatternWallOverlapForReview()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80)),
            Line("review-wall", "A-WALL", new PlanPoint(420, 176), new PlanPoint(520, 176))
        };

        for (var index = 0; index <= 10; index++)
        {
            var offset = 400 + (index * 16);
            primitives.Add(UnlayeredLine(
                $"terrace-grid-v-{index}",
                new PlanPoint(offset, 96),
                new PlanPoint(offset, 256)));
            primitives.Add(UnlayeredLine(
                $"terrace-grid-h-{index}",
                new PlanPoint(400, 96 + (index * 16)),
                new PlanPoint(560, 96 + (index * 16))));
        }

        var document = new PlanDocument(
            "surface-pattern-wall-overlap-review",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var pattern = Assert.Single(result.SurfacePatterns);
        Assert.Contains(result.Walls, wall => wall.SourcePrimitiveIds.Contains("review-wall"));
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("terrace-grid-", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "wall_graph.surface_pattern_wall_overlap.review"));
        Assert.Equal(pattern.Id, diagnostic.Properties["surfacePatternId"]);
        Assert.Equal("0", diagnostic.Properties["sharedSourcePrimitiveCount"]);
        Assert.Contains("review-wall", diagnostic.SourcePrimitiveIds);
        Assert.True(double.Parse(diagnostic.Properties["wallOverlapRatio"], System.Globalization.CultureInfo.InvariantCulture) >= 0.85);

        var summary = ScanReviewQueueSummary.From(result);
        Assert.Equal(1, summary.KindCounts[ScanReviewQueueKinds.SurfacePatternWallOverlapReview]);

        using var scanJson = JsonDocument.Parse(PlanTraceJsonExporter.Serialize(result));
        var reviewItem = Assert.Single(
            scanJson.RootElement.GetProperty("reviewQueue").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == ScanReviewQueueKinds.SurfacePatternWallOverlapReview);
        Assert.Equal("wall-graph", reviewItem.GetProperty("detector").GetString());
        Assert.Equal(pattern.Id, reviewItem.GetProperty("properties").GetProperty("surfacePatternId").GetString());
        Assert.Contains("wall/surface-pattern overlap", reviewItem.GetProperty("recommendedAction").GetString());

        var snapshot = PlanOverlaySnapshot.From(result);
        Assert.Equal(1, snapshot.ReviewQueueKindBreakdown[ScanReviewQueueKinds.SurfacePatternWallOverlapReview]);
        Assert.Contains(
            snapshot.Pages.Single(page => page.PageNumber == 1).ReviewQueue,
            item => item.Kind == ScanReviewQueueKinds.SurfacePatternWallOverlapReview);

        Assert.Contains(
            result.Quality.Issues,
            issue => issue.Code == "quality.scan_risk.surface_pattern_wall_overlap"
                && issue.Properties["affectedSurfacePatternCount"] == "1");

        var readiness = PlanImportReadiness.FromScanResult(result);
        Assert.Contains(
            "placement.wall_graph.surface_pattern_wall_overlaps.require_review",
            readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("surface/detail patterns", StringComparison.OrdinalIgnoreCase));

        using var placementJson = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var placementIssue = Assert.Single(
            placementJson.RootElement.GetProperty("issues").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "placement.review.surface_pattern_wall_overlap");
        Assert.Equal(
            pattern.Id,
            placementIssue.GetProperty("properties").GetProperty("surfacePatternId").GetString());
        var placementWall = Assert.Single(
            placementJson.RootElement.GetProperty("walls").EnumerateArray(),
            wall => wall.GetProperty("sourcePrimitiveIds").EnumerateArray().Any(source => source.GetString() == "review-wall"));
        Assert.True(placementWall.GetProperty("reliability").GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            placementWall
                .GetProperty("reliability")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(item => item.GetString()),
            reason => reason is not null
                && reason.Contains(pattern.Id, StringComparison.Ordinal)
                && reason.Contains("surface/detail pattern", StringComparison.OrdinalIgnoreCase));
        var unrelatedWall = Assert.Single(
            placementJson.RootElement.GetProperty("walls").EnumerateArray(),
            wall => wall.GetProperty("sourcePrimitiveIds").EnumerateArray().Any(source => source.GetString() == "wall-top"));
        Assert.DoesNotContain(
            unrelatedWall
                .GetProperty("reliability")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(item => item.GetString()),
            reason => reason is not null
                && reason.Contains("surface/detail pattern", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            "placement.wall_graph.surface_pattern_wall_overlaps.require_review",
            placementJson.RootElement
                .GetProperty("summary")
                .GetProperty("importReadiness")
                .GetProperty("reviewIssueCodes")
                .EnumerateArray()
                .Select(item => item.GetString()));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsUnlayeredWallCandidatesInsideExcludedSurfacePatterns()
    {
        var document = new PlanDocument(
            "surface-pattern-overlap-noise-rejection",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    new PlanPrimitive[]
                    {
                        Line("protected-wall-over-pattern", "A-WALL", new PlanPoint(420, 180), new PlanPoint(520, 180)),
                        UnlayeredLine("false-wall-over-pattern", new PlanPoint(420, 204), new PlanPoint(520, 204))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions { EnableWallEvidenceNoiseRejection = true });
        context.LayerAnalysis = new PlanLayerAnalysis(new[]
        {
            Layer("A-WALL", LayerCategory.Wall, Confidence.High)
        });
        context.SurfacePatterns.Add(new SurfacePatternCandidate(
            "page:1:surface-pattern:001",
            1,
            SurfacePatternKind.DenseOrthogonalGrid,
            SurfacePatternOrientation.Orthogonal,
            new PlanRect(400, 96, 160, 160),
            null,
            22,
            11,
            11,
            100,
            16,
            16,
            null,
            ExcludedFromWallDetection: true,
            ExcludedFromStructuralTopology: true,
            new[] { "terrace-grid-source" },
            Confidence.High,
            RequiresReview: true,
            new[] { "synthetic dense terrace/detail pattern" }));
        context.WallCandidates.Add(new WallSegment(
            "wall-protected",
            1,
            new PlanLineSegment(new PlanPoint(420, 180), new PlanPoint(520, 180)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "protected-wall-over-pattern" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-false-pattern-detail",
            1,
            new PlanLineSegment(new PlanPoint(420, 204), new PlanPoint(520, 204)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "false-wall-over-pattern" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("protected-wall-over-pattern"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("false-wall-over-pattern"));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("1", diagnostic.Properties["surfacePatternRejectedCount"]);
        Assert.Contains("false-wall-over-pattern", diagnostic.SourcePrimitiveIds);

        Assert.Contains(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("false-wall-over-pattern")
                && assessment.Category == WallEvidenceCategory.SurfacePatternDetail
                && assessment.RejectedAsNoise
                && assessment.Evidence.Any(item => item.Contains("page:1:surface-pattern:001", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScanAsync_CanKeepDenseOrthogonalPatternsWhenFilterIsDisabled()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80))
        };

        for (var index = 0; index <= 6; index++)
        {
            var offset = 400 + (index * 16);
            primitives.Add(UnlayeredLine(
                $"surface-pattern-v-{index}",
                new PlanPoint(offset, 96),
                new PlanPoint(offset, 192)));
            primitives.Add(UnlayeredLine(
                $"surface-pattern-h-{index}",
                new PlanPoint(400, 96 + (index * 16)),
                new PlanPoint(496, 96 + (index * 16))));
        }

        var document = new PlanDocument(
            "dense-orthogonal-surface-pattern-filter-disabled",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterDenseOrthogonalPatternsFromWalls = false
            });

        Assert.True(result.Walls.Count > 4);
        Assert.Contains(
            result.Walls,
            wall => wall.SourcePrimitiveIds.Any(sourceId => sourceId.StartsWith("surface-pattern-", StringComparison.Ordinal)));
        Assert.Empty(result.SurfacePatterns);
        Assert.DoesNotContain(result.Diagnostics.Messages, message => message.Code == "walls.dense_orthogonal_pattern_filtered");
    }

    [Fact]
    public async Task ScanAsync_FiltersDenseParallelSurfaceBandsFromWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80))
        };

        for (var index = 0; index < 30; index++)
        {
            primitives.Add(UnlayeredLine(
                $"terrace-band-h-{index}",
                new PlanPoint(420, 96 + (index * 4.25)),
                new PlanPoint(456, 96 + (index * 4.25))));
        }

        primitives.Add(UnlayeredLine("terrace-band-v-left", new PlanPoint(416, 94), new PlanPoint(416, 224)));
        primitives.Add(UnlayeredLine("terrace-band-v-mid", new PlanPoint(438, 94), new PlanPoint(438, 224)));
        primitives.Add(UnlayeredLine("terrace-band-v-right", new PlanPoint(460, 94), new PlanPoint(460, 224)));

        var document = new PlanDocument(
            "dense-parallel-surface-band-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("terrace-band-", StringComparison.Ordinal)));

        var pattern = Assert.Single(result.SurfacePatterns);
        Assert.Equal(SurfacePatternKind.DenseParallelBand, pattern.Kind);
        Assert.Equal(SurfacePatternOrientation.Horizontal, pattern.Orientation);
        Assert.True(pattern.ExcludedFromWallDetection);
        Assert.True(pattern.ExcludedFromStructuralTopology);
        Assert.True(pattern.RequiresReview);
        Assert.True(pattern.LineCount >= 30);
        Assert.Contains("terrace-band-h-0", pattern.SourcePrimitiveIds);
        Assert.Contains("terrace-band-v-mid", pattern.SourcePrimitiveIds);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.dense_orthogonal_pattern_filtered"));
        Assert.Equal("1", diagnostic.Properties["parallelClusterCount"]);
        Assert.Contains("Horizontal", diagnostic.Properties["patterns"]);
        Assert.Contains("terrace-band-h-0", diagnostic.SourcePrimitiveIds);
        Assert.Contains("terrace-band-v-mid", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersTightSurfaceGridTailsFromWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80))
        };

        for (var index = 0; index < 30; index++)
        {
            primitives.Add(UnlayeredLine(
                $"crossing-band-h-{index}",
                new PlanPoint(420, 96 + (index * 4.25)),
                new PlanPoint(565, 96 + (index * 4.25))));
            primitives.Add(UnlayeredLine(
                $"crossing-band-v-{index}",
                new PlanPoint(438 + (index * 4.25), 140),
                new PlanPoint(438 + (index * 4.25), 220)));
        }

        var document = new PlanDocument(
            "crossing-dense-parallel-surface-bands",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 420),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false
            });

        Assert.True(
            result.Walls.Count == 4,
            DumpWallFilteringResult(result));
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("crossing-band-", StringComparison.Ordinal)));

        var grid = Assert.Single(
            result.SurfacePatterns,
            pattern => pattern.Kind == SurfacePatternKind.DenseOrthogonalGrid);
        Assert.Equal(SurfacePatternOrientation.Orthogonal, grid.Orientation);
        Assert.True(grid.LineCount >= 40);
        Assert.Equal(30, grid.HorizontalLineCount);
        Assert.True(grid.VerticalLineCount >= 10);
        Assert.True(grid.IntersectionCount >= 100, DumpWallFilteringResult(result));
        Assert.True(grid.Bounds.Width < 180);
        Assert.True(grid.Bounds.Height < 150);
        Assert.Contains("crossing-band-h-0", grid.SourcePrimitiveIds);
        Assert.Contains("crossing-band-h-29", grid.SourcePrimitiveIds);
        Assert.Contains("crossing-band-v-0", grid.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersGappedSurfaceLatticeFragmentsFromWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(340, 80)),
            UnlayeredLine("wall-right", new PlanPoint(340, 80), new PlanPoint(340, 320)),
            UnlayeredLine("wall-bottom", new PlanPoint(340, 320), new PlanPoint(80, 320)),
            UnlayeredLine("wall-left", new PlanPoint(80, 320), new PlanPoint(80, 80))
        };

        for (var row = 0; row < 8; row++)
        {
            var y = 110 + (row * 16);
            for (var span = 0; span < 3; span++)
            {
                var x = 400 + (span * 52);
                primitives.Add(UnlayeredLine(
                    $"gapped-lattice-h-{row}-{span}",
                    new PlanPoint(x, y),
                    new PlanPoint(x + 34, y)));
            }
        }

        for (var column = 0; column < 8; column++)
        {
            var x = 410 + (column * 16);
            for (var span = 0; span < 3; span++)
            {
                var y = 100 + (span * 52);
                primitives.Add(UnlayeredLine(
                    $"gapped-lattice-v-{column}-{span}",
                    new PlanPoint(x, y),
                    new PlanPoint(x, y + 34)));
            }
        }

        var document = new PlanDocument(
            "gapped-surface-lattice-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 440),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(
            result.Walls.Count == 4,
            DumpWallFilteringResult(result));
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("gapped-lattice-", StringComparison.Ordinal)));

        var pattern = Assert.Single(result.SurfacePatterns, pattern => pattern.SourcePrimitiveIds.Any(sourceId => sourceId.StartsWith("gapped-lattice-", StringComparison.Ordinal)));
        Assert.Equal(SurfacePatternKind.DenseOrthogonalGrid, pattern.Kind);
        Assert.Equal(SurfacePatternOrientation.Orthogonal, pattern.Orientation);
        Assert.True(pattern.ExcludedFromWallDetection);
        Assert.True(pattern.ExcludedFromStructuralTopology);
        Assert.True(pattern.RequiresReview);
        Assert.True(pattern.LineCount >= 40);
        Assert.True(pattern.HorizontalLineCount >= 8);
        Assert.True(pattern.VerticalLineCount >= 8);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.dense_orthogonal_pattern_filtered"));
        Assert.Contains("gapped-lattice-h-0-0", diagnostic.SourcePrimitiveIds);
        Assert.Contains("gapped-lattice-v-7-2", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_DoesNotClassifyLineOnlySurfaceLatticeAsTitleBlock()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("room-wall-top", new PlanPoint(18, 18), new PlanPoint(282, 18)),
            UnlayeredLine("room-wall-right", new PlanPoint(282, 18), new PlanPoint(282, 262)),
            UnlayeredLine("room-wall-bottom", new PlanPoint(282, 262), new PlanPoint(18, 262)),
            UnlayeredLine("room-wall-left", new PlanPoint(18, 262), new PlanPoint(18, 18))
        };

        for (var row = 0; row < 8; row++)
        {
            var y = 58 + (row * 16);
            for (var span = 0; span < 3; span++)
            {
                var x = 332 + (span * 52);
                primitives.Add(UnlayeredLine(
                    $"side-lattice-h-{row}-{span}",
                    new PlanPoint(x, y),
                    new PlanPoint(x + 34, y)));
            }
        }

        for (var column = 0; column < 8; column++)
        {
            var x = 342 + (column * 16);
            for (var span = 0; span < 3; span++)
            {
                var y = 48 + (span * 52);
                primitives.Add(UnlayeredLine(
                    $"side-lattice-v-{column}-{span}",
                    new PlanPoint(x, y),
                    new PlanPoint(x, y + 34)));
            }
        }

        var document = new PlanDocument(
            "line-only-side-lattice-is-not-title-block",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(498, 280),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.DoesNotContain(result.SheetRegions, region => region.Kind == RegionKind.TitleBlock);
        Assert.True(
            result.Walls.Count == 4,
            DumpWallFilteringResult(result));
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("side-lattice-", StringComparison.Ordinal)));
        Assert.Contains(
            result.SurfacePatterns,
            pattern => pattern.Kind == SurfacePatternKind.DenseOrthogonalGrid
                && pattern.SourcePrimitiveIds.Any(sourceId => sourceId.StartsWith("side-lattice-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScanAsync_FiltersShortDenseFragmentRunsFromWalls()
    {
        var fixtureFragments = Enumerable.Range(0, 8)
            .Select(index => UnlayeredLine(
                $"fixture-fragment-{index}",
                new PlanPoint(230 + (index * 11), 230),
                new PlanPoint(238 + (index * 11), 230)))
            .Cast<PlanPrimitive>()
            .ToArray();
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(100, 100), new PlanPoint(560, 100)),
            UnlayeredLine("wall-right", new PlanPoint(560, 100), new PlanPoint(560, 360)),
            UnlayeredLine("wall-bottom", new PlanPoint(560, 360), new PlanPoint(100, 360)),
            UnlayeredLine("wall-left", new PlanPoint(100, 360), new PlanPoint(100, 100))
        };
        primitives.AddRange(fixtureFragments);

        var document = new PlanDocument(
            "fragment-noise-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterDenseFragmentLineworkFromWalls = true
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("fixture-fragment-", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.fragment_noise_filtered"));
        Assert.Equal("1", diagnostic.Properties["filteredRunCount"]);
        Assert.Contains("fixture-fragment-0", diagnostic.SourcePrimitiveIds);
        Assert.Contains("fixture-fragment-7", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersDimensionLikeFragmentRunsFromWallsByDefault()
    {
        var dimensionFragments = Enumerable.Range(0, 24)
            .Select(index => UnlayeredLine(
                $"dimension-fragment-{index}",
                new PlanPoint(180 + (index * 8), 225),
                new PlanPoint(184.5 + (index * 8), 225)))
            .Cast<PlanPrimitive>()
            .ToArray();
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(100, 100), new PlanPoint(560, 100)),
            UnlayeredLine("wall-right", new PlanPoint(560, 100), new PlanPoint(560, 360)),
            UnlayeredLine("wall-bottom", new PlanPoint(560, 360), new PlanPoint(100, 360)),
            UnlayeredLine("wall-left", new PlanPoint(100, 360), new PlanPoint(100, 100)),
            UnlayeredText("dim-text-a", "4000 mm", new PlanRect(220, 390, 72, 16)),
            UnlayeredText("dim-text-b", "2600 mm", new PlanRect(580, 210, 72, 16))
        };
        primitives.AddRange(dimensionFragments);

        var document = new PlanDocument(
            "dimension-fragment-noise-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("dimension-fragment-", StringComparison.Ordinal)));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.dimension_fragment_noise_filtered"));
        Assert.Equal("1", diagnostic.Properties["filteredRunCount"]);
        Assert.Contains("dimension-fragment-0", diagnostic.SourcePrimitiveIds);
        Assert.Contains("dimension-fragment-23", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_KeepsDimensionLikeFragmentRunsWhenTheyFormParallelWallBody()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredText("dim-text-a", "4000 mm", new PlanRect(210, 210, 72, 16)),
            UnlayeredText("dim-text-b", "2600 mm", new PlanRect(310, 230, 72, 16))
        };
        for (var index = 0; index < 18; index++)
        {
            var x = 80 + (index * 9);
            primitives.Add(UnlayeredLine(
                $"wall-face-a-{index}",
                new PlanPoint(x, 120),
                new PlanPoint(x + 6, 120)));
            primitives.Add(UnlayeredLine(
                $"wall-face-b-{index}",
                new PlanPoint(x, 128),
                new PlanPoint(x + 6, 128)));
        }

        var document = new PlanDocument(
            "dimension-like-fragmented-wall-body",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(360, 240),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind);
        Assert.Contains("wall-face-a-0", wall.SourcePrimitiveIds);
        Assert.Contains("wall-face-b-17", wall.SourcePrimitiveIds);
        Assert.DoesNotContain(result.Diagnostics.Messages, message => message.Code == "walls.dimension_fragment_noise_filtered");
    }

    [Fact]
    public async Task ScanAsync_FiltersDoorSymbolLineworkBeforeWallSeeds()
    {
        var document = new PlanDocument(
            "door-symbol-linework-not-walls",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 360),
                    new PlanPrimitive[]
                    {
                        UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
                        UnlayeredLine("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 280)),
                        UnlayeredLine("wall-bottom", new PlanPoint(420, 280), new PlanPoint(80, 280)),
                        UnlayeredLine("wall-left", new PlanPoint(80, 280), new PlanPoint(80, 80)),
                        UnlayeredLine("door-leaf-a", new PlanPoint(180, 170), new PlanPoint(220, 170)),
                        UnlayeredLine("door-leaf-b", new PlanPoint(180, 174), new PlanPoint(220, 174)),
                        UnlayeredLine("door-return", new PlanPoint(180, 170), new PlanPoint(180, 190)),
                        UnlayeredText("dim-text", "900 mm", new PlanRect(220, 300, 58, 14))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("door-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.door_symbol_linework_filtered"));
        Assert.Contains("door-leaf-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("door-return", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_FiltersShortFragmentMergedDoorDetailWalls()
    {
        var primitives = new List<PlanPrimitive>
        {
            UnlayeredLine("wall-top", new PlanPoint(80, 80), new PlanPoint(420, 80)),
            UnlayeredLine("wall-right", new PlanPoint(420, 80), new PlanPoint(420, 280)),
            UnlayeredLine("wall-bottom", new PlanPoint(420, 280), new PlanPoint(80, 280)),
            UnlayeredLine("wall-left", new PlanPoint(80, 280), new PlanPoint(80, 80)),
            UnlayeredText("dim-text-a", "900 mm", new PlanRect(220, 300, 58, 14)),
            UnlayeredText("dim-text-b", "1200 mm", new PlanRect(120, 42, 64, 14))
        };

        for (var index = 0; index < 10; index++)
        {
            var x = 190 + (index * 1.2);
            primitives.Add(UnlayeredLine(
                $"door-fragment-{index}",
                new PlanPoint(x, 175),
                new PlanPoint(x + 1.2, 175)));
        }

        var document = new PlanDocument(
            "short-fragment-door-detail-not-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 360),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                MinWallLength = 12,
                MinWallFragmentLength = 1
            });

        Assert.All(result.Walls, wall => Assert.DoesNotContain(
            wall.SourcePrimitiveIds,
            sourceId => sourceId.StartsWith("door-fragment-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.door_detail_symbol_walls_filtered"));
        Assert.Equal("1", diagnostic.Properties["filteredWallCount"]);
    }

    [Fact]
    public async Task ScanAsync_UsesLayerCategoryOverrideForWallFiltering()
    {
        var document = new PlanDocument(
            "overridden-wall-layer",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Line("wall-top", "EQUIP-LINE", new PlanPoint(100, 100), new PlanPoint(300, 100)),
                        Line("wall-right", "EQUIP-LINE", new PlanPoint(300, 100), new PlanPoint(300, 260)),
                        Line("wall-bottom", "EQUIP-LINE", new PlanPoint(300, 260), new PlanPoint(100, 260)),
                        Line("wall-left", "EQUIP-LINE", new PlanPoint(100, 260), new PlanPoint(100, 100))
                    })
            });

        var withoutOverride = await new OpenPlanTraceScanner().ScanAsync(document);
        var withOverride = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                LayerCategoryOverrides = new[] { new LayerCategoryOverride("EQUIP-LINE", LayerCategory.Wall) }
            });

        Assert.Empty(withoutOverride.Walls);
        Assert.Equal(4, withOverride.Walls.Count);
        Assert.Equal(LayerCategory.Wall, withOverride.LayerAnalysis.Find("EQUIP-LINE")!.LikelyCategory);
        Assert.Contains(withOverride.Walls, wall => wall.Evidence.Any(item => item.Contains("override", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsServiceLayerLineworkAsObjectFixtureDetail()
    {
        var document = new PlanDocument(
            "wall-evidence-service-layer-linework-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(460, 280),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(360, 100)),
                        Line("hvac-linework-noise", "M-HVAC-EQPM", new PlanPoint(140, 120), new PlanPoint(260, 120))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("M-HVAC-EQPM", LayerCategory.HVAC, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(360, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-hvac-linework-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 120), new PlanPoint(260, 120)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "hvac-linework-noise" },
            Evidence = new[] { "test service equipment linework candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("hvac-linework-noise"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("hvac-linework-noise"));
        Assert.Equal(WallEvidenceCategory.ObjectOrFixtureDetail, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NoisePenalty >= 0.75);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("object/fixture/service linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            rejected.ScoreBreakdown.NegativeEvidence,
            item => item.Contains(nameof(WallEvidenceCategory.ObjectOrFixtureDetail), StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(
            message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("1", diagnostic.Properties["objectFixtureRejectedCount"]);
        Assert.Contains("hvac-linework-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsFurnitureAndFixtureLayerLineworkAsObjectFixtureDetail()
    {
        var document = new PlanDocument(
            "wall-evidence-furniture-fixture-layer-linework-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(420, 100)),
                        Line("furniture-linework-noise", "A-FURN", new PlanPoint(140, 145), new PlanPoint(220, 145)),
                        Line("fixture-linework-noise", "A-FIXTURE", new PlanPoint(260, 145), new PlanPoint(340, 145))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-FURN", LayerCategory.Furniture, Confidence.High),
                Layer("A-FIXTURE", LayerCategory.Fixture, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(420, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-furniture-linework-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 145), new PlanPoint(220, 145)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "furniture-linework-noise" },
            Evidence = new[] { "test furniture linework candidate" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-fixture-linework-noise",
            1,
            new PlanLineSegment(new PlanPoint(260, 145), new PlanPoint(340, 145)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "fixture-linework-noise" },
            Evidence = new[] { "test fixture linework candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("furniture-linework-noise"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("fixture-linework-noise"));

        var rejected = context.WallEvidenceMap.WallAssessments
            .Where(assessment => assessment.SourcePrimitiveIds.Any(
                sourceId => sourceId is "furniture-linework-noise" or "fixture-linework-noise"))
            .OrderBy(assessment => assessment.SourcePrimitiveIds[0], StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.All(rejected, assessment =>
        {
            Assert.Equal(WallEvidenceCategory.ObjectOrFixtureDetail, assessment.Category);
            Assert.Equal(WallEvidenceDecision.Reject, assessment.Decision);
            Assert.True(assessment.RejectedAsNoise);
            Assert.True(assessment.ScoreBreakdown.NoisePenalty >= 0.75);
            Assert.Contains(
                assessment.Evidence,
                item => item.Contains("object/fixture/service linework", StringComparison.OrdinalIgnoreCase));
        });

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(
            message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("2", diagnostic.Properties["objectFixtureRejectedCount"]);
        Assert.Contains("furniture-linework-noise", diagnostic.SourcePrimitiveIds);
        Assert.Contains("fixture-linework-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsObjectFixtureParallelPairBeforeStrongWallAcceptance()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(140, 145), new PlanPoint(240, 145));
        var secondFace = new PlanLineSegment(new PlanPoint(140, 151), new PlanPoint(240, 151));
        var document = new PlanDocument(
            "wall-evidence-object-fixture-pair-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(420, 100)),
                        Line("fixture-pair-face-a", "A-FIXTURE", firstFace.Start, firstFace.End),
                        Line("fixture-pair-face-b", "A-FIXTURE", secondFace.Start, secondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-FIXTURE", LayerCategory.Fixture, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(420, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-fixture-pair-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 148), new PlanPoint(240, 148)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = new[] { "fixture-pair-face-a", "fixture-pair-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.91,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "fixture-pair-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "fixture-pair-face-b" }),
            Evidence = new[] { "test high-scoring fixture pair candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("fixture-pair-face-a"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("fixture-pair-face-a"));
        Assert.Equal(WallEvidenceCategory.ObjectOrFixtureDetail, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("paired object/fixture/service linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsDimensionGridParallelPairBeforeStrongWallAcceptance()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(140, 185), new PlanPoint(340, 185));
        var secondFace = new PlanLineSegment(new PlanPoint(140, 191), new PlanPoint(340, 191));
        var document = new PlanDocument(
            "wall-evidence-dimension-grid-pair-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(420, 100)),
                        Line("dimension-pair-face-a", "A-DIMS", firstFace.Start, firstFace.End),
                        Line("dimension-pair-face-b", "A-DIMS", secondFace.Start, secondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DIMS", LayerCategory.Dimension, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(420, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-dimension-pair-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 188), new PlanPoint(340, 188)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = new[] { "dimension-pair-face-a", "dimension-pair-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.92,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "dimension-pair-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "dimension-pair-face-b" }),
            Evidence = new[] { "test high-scoring dimension pair candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("dimension-pair-face-a"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("dimension-pair-face-a"));
        Assert.Equal(WallEvidenceCategory.DimensionOrAnnotation, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("paired dimension, text, or grid layer linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsSurfacePatternLayerLineworkAsSurfacePatternDetail()
    {
        var document = new PlanDocument(
            "wall-evidence-surface-pattern-layer-linework-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(420, 100)),
                        Line("hatch-linework-noise", "A-HATCH", new PlanPoint(140, 145), new PlanPoint(340, 145))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-HATCH", LayerCategory.SurfacePattern, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(420, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-hatch-linework-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 145), new PlanPoint(340, 145)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "hatch-linework-noise" },
            Evidence = new[] { "test hatch linework candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("hatch-linework-noise"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("hatch-linework-noise"));
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NoisePenalty >= 0.85);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("hatch/surface-pattern linework", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(
            message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("1", diagnostic.Properties["surfacePatternRejectedCount"]);
        Assert.Contains("hatch-linework-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsSurfacePatternParallelPairBeforeStrongWallAcceptance()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(140, 145), new PlanPoint(340, 145));
        var secondFace = new PlanLineSegment(new PlanPoint(140, 151), new PlanPoint(340, 151));
        var document = new PlanDocument(
            "wall-evidence-surface-pattern-pair-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("host-wall", "A-WALL", new PlanPoint(80, 100), new PlanPoint(420, 100)),
                        Line("hatch-pair-face-a", "A-HATCH", firstFace.Start, firstFace.End),
                        Line("hatch-pair-face-b", "A-HATCH", secondFace.Start, secondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-HATCH", LayerCategory.SurfacePattern, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(420, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-hatch-pair-noise",
            1,
            new PlanLineSegment(new PlanPoint(140, 148), new PlanPoint(340, 148)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = new[] { "hatch-pair-face-a", "hatch-pair-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.92,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "hatch-pair-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "hatch-pair-face-b" }),
            Evidence = new[] { "test high-scoring hatch pair candidate" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("hatch-pair-face-a"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("hatch-pair-face-a"));
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("paired hatch/surface-pattern linework", StringComparison.OrdinalIgnoreCase));
    }

    private static LinePrimitive Line(string sourceId, string layer, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "LINE", layer)
        };

    private static LinePrimitive UnlayeredLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static TextPrimitive UnlayeredText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "TEXT",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static LayerSummary Layer(string name, LayerCategory category, Confidence confidence) =>
        new(
            name,
            "test",
            1,
            new Dictionary<PlanPrimitiveKind, int> { [PlanPrimitiveKind.Line] = 1 },
            100,
            new PlanRect(0, 0, 100, 10),
            category,
            confidence,
            new[] { new LayerCategoryScore(category, confidence.Value, new[] { "test layer summary" }) },
            new[] { $"classified {category}" },
            new[] { 1 });

    private static PrimitiveSourceMetadata Source(string sourceId, string entityType, string layer) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Model
        };

    private static string DumpWallFilteringResult(PlanScanResult result)
    {
        var walls = string.Join(
            Environment.NewLine,
            result.Walls.Select(wall =>
                $"{wall.Id} {wall.Bounds.X:0.###},{wall.Bounds.Y:0.###},{wall.Bounds.Width:0.###},{wall.Bounds.Height:0.###} sources={string.Join(",", wall.SourcePrimitiveIds)}"));
        var patterns = string.Join(
            Environment.NewLine,
            result.SurfacePatterns.Select(pattern =>
                $"{pattern.Id} {pattern.Kind} {pattern.Bounds.X:0.###},{pattern.Bounds.Y:0.###},{pattern.Bounds.Width:0.###},{pattern.Bounds.Height:0.###} h={pattern.HorizontalLineCount} v={pattern.VerticalLineCount} x={pattern.IntersectionCount} sources={string.Join(",", pattern.SourcePrimitiveIds)}"));
        var diagnostics = string.Join(
            Environment.NewLine,
            result.Diagnostics.Messages.Select(message =>
                $"{message.Code} {message.Message}"));

        return $"walls={result.Walls.Count}{Environment.NewLine}{walls}{Environment.NewLine}patterns={result.SurfacePatterns.Count}{Environment.NewLine}{patterns}{Environment.NewLine}diagnostics={Environment.NewLine}{diagnostics}";
    }
}
