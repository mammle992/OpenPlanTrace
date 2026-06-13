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
