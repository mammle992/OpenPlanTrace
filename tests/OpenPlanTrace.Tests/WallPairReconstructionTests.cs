using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class WallPairReconstructionTests
{
    [Fact]
    public async Task ScanAsync_ReconstructsDoubleLineWallsIntoCenterlines()
    {
        var document = new PlanDocument(
            "double-line-room",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
                        WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
                        WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
                        WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.All(result.Walls, wall => Assert.Equal(WallType.Exterior, wall.WallType));
        Assert.All(result.Walls, wall => Assert.Equal(10, wall.Thickness));
        Assert.Contains(result.Walls, wall => wall.CenterLine.IsHorizontal() && Math.Abs(wall.CenterLine.Start.Y - 105) < 0.01);
        Assert.Contains(result.Walls, wall => wall.CenterLine.IsVertical() && Math.Abs(wall.CenterLine.Start.X - 495) < 0.01);
        Assert.Contains(result.Rooms, room => room.Bounds.Width > 380 && room.Bounds.Height > 280);
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.parallel_pairs.reconstructed");
        Assert.NotEmpty(result.WallEvidenceMap.Bands);
        Assert.Equal(4, result.WallEvidenceMap.StrongWallBodyCount);
        Assert.Equal(4, result.WallEvidenceMap.PlacementReadyWallCount);
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "wall_evidence.map_built");
    }

    [Fact]
    public async Task ScanAsync_LeavesUnpairedCenterlineWallsAsSingleLine()
    {
        var document = new PlanDocument(
            "centerline-room",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("right", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("bottom", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("left", new PlanPoint(100, 400), new PlanPoint(100, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.SingleLine, wall.DetectionKind));
        Assert.All(result.Walls, wall => Assert.Equal(WallType.Exterior, wall.WallType));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesConcaveEnvelopeWallsAsExterior()
    {
        var document = new PlanDocument(
            "l-shaped-exterior-envelope",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(640, 520),
                    new PlanPrimitive[]
                    {
                        WallFace("outer-top-left", new PlanPoint(100, 100), new PlanPoint(300, 100)),
                        WallFace("outer-notch-vertical-a", new PlanPoint(300, 100), new PlanPoint(300, 200)),
                        WallFace("outer-concave-top", new PlanPoint(300, 200), new PlanPoint(500, 200)),
                        WallFace("outer-right", new PlanPoint(500, 200), new PlanPoint(500, 400)),
                        WallFace("outer-bottom-right", new PlanPoint(500, 400), new PlanPoint(300, 400)),
                        WallFace("outer-notch-vertical-b", new PlanPoint(300, 400), new PlanPoint(300, 300)),
                        WallFace("outer-concave-bottom", new PlanPoint(300, 300), new PlanPoint(100, 300)),
                        WallFace("outer-left", new PlanPoint(100, 300), new PlanPoint(100, 100)),
                        WallFace("interior-partition", new PlanPoint(240, 100), new PlanPoint(240, 300))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Contains(result.Walls, wall =>
            wall.SourcePrimitiveIds.Contains("outer-concave-top")
            && wall.WallType == WallType.Exterior
            && wall.Evidence.Any(item => item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Walls, wall =>
            wall.SourcePrimitiveIds.Contains("outer-concave-bottom")
            && wall.WallType == WallType.Exterior
            && wall.Evidence.Any(item => item.Contains("local outer boundary", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Walls, wall =>
            wall.SourcePrimitiveIds.Contains("interior-partition")
            && wall.WallType == WallType.Interior);
    }

    [Fact]
    public async Task ScanAsync_SuppressesUnsupportedSingleLineDetailInsideWallBodyContext()
    {
        var document = new PlanDocument(
            "double-line-room-with-furniture-detail",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
                        WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
                        WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
                        WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100)),
                        DetailLine("bed-edge", new PlanPoint(190, 240), new PlanPoint(330, 240))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Contains("bed-edge"));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.unsupported_wall_body_linework_filtered"));
        Assert.Equal("1", diagnostic.Properties["filteredWallCount"]);
        Assert.Contains("bed-edge", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_KeepsSupportedSingleLinePartitionInsideWallBodyContext()
    {
        var document = new PlanDocument(
            "double-line-room-with-centerline-partition",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
                        WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
                        WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
                        WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100)),
                        DetailLine("interior-partition", new PlanPoint(300, 105), new PlanPoint(300, 395))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(5, result.Walls.Count);
        Assert.Contains(result.Walls, wall =>
            wall.SourcePrimitiveIds.Contains("interior-partition")
            && wall.DetectionKind == WallDetectionKind.SingleLine
            && wall.WallType == WallType.Interior
            && wall.Evidence.Any(item => item.Contains("room", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, result.Walls.Count(wall => wall.WallType == WallType.Exterior));
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "walls.architectural_type_refined"
            && message.Properties["wallCount"] == "5");
        Assert.DoesNotContain(result.Diagnostics.Messages, message =>
            message.Code == "walls.unsupported_wall_body_linework_filtered"
            && message.SourcePrimitiveIds.Contains("interior-partition"));
    }

    [Fact]
    public async Task ScanAsync_SuppressesConnectedSymbolLineworkInsideWallBodyContext()
    {
        var document = new PlanDocument(
            "double-line-room-with-connected-symbol-detail",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
                        WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
                        WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
                        WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100)),
                        DetailLine("symbol-diagonal-a", new PlanPoint(190, 210), new PlanPoint(260, 280)),
                        DetailLine("symbol-diagonal-b", new PlanPoint(260, 210), new PlanPoint(190, 280)),
                        DetailLine("symbol-crossbar-h", new PlanPoint(185, 245), new PlanPoint(265, 245)),
                        DetailLine("symbol-crossbar-v", new PlanPoint(225, 205), new PlanPoint(225, 285))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false,
                FilterDenseOrthogonalPatternsFromWalls = false
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Any(id => id.StartsWith("symbol-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.unsupported_wall_body_linework_filtered"));
        Assert.Equal("4", diagnostic.Properties["filteredWallCount"]);
        Assert.Contains("symbol-diagonal-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("symbol-crossbar-v", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_SuppressesDenseThinPairedSurfaceBandsInsideWallBodyContext()
    {
        var primitives = new List<PlanPrimitive>
        {
            WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
            WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
            WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
            WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
            WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
            WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
            WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
            WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100))
        };

        for (var index = 0; index < 5; index++)
        {
            var y = 185 + (index * 8);
            primitives.Add(DetailLine($"surface-band-{index}-a", new PlanPoint(180, y), new PlanPoint(330, y)));
            primitives.Add(DetailLine($"surface-band-{index}-b", new PlanPoint(180, y + 2.6), new PlanPoint(330, y + 2.6)));
        }

        var document = new PlanDocument(
            "double-line-room-with-dense-thin-paired-surface-bands",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false,
                FilterDenseOrthogonalPatternsFromWalls = false
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Any(id => id.StartsWith("surface-band-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.unsupported_wall_body_linework_filtered"));
        Assert.Equal("5", diagnostic.Properties["filteredWallCount"]);
        Assert.Contains("surface-band-0-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("surface-band-4-b", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_SuppressesShortRepeatedPairedDetailSlotsInsideWallBodyContext()
    {
        var primitives = new List<PlanPrimitive>
        {
            WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
            WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
            WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
            WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
            WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
            WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
            WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
            WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100))
        };

        for (var index = 0; index < 6; index++)
        {
            var x = 180 + (index * 14.8);
            primitives.Add(DetailLine($"detail-slot-{index}-a", new PlanPoint(x, 150), new PlanPoint(x, 176)));
            primitives.Add(DetailLine($"detail-slot-{index}-b", new PlanPoint(x + 7.4, 150), new PlanPoint(x + 7.4, 176)));
        }

        var document = new PlanDocument(
            "double-line-room-with-short-repeated-paired-detail-slots",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false,
                FilterDenseOrthogonalPatternsFromWalls = false
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Any(id => id.StartsWith("detail-slot-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.unsupported_wall_body_linework_filtered"));
        Assert.Equal("6", diagnostic.Properties["filteredWallCount"]);
        Assert.Contains("detail-slot-0-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("detail-slot-5-b", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_SuppressesDetailRailsSupportedOnlyByRejectedSlots()
    {
        var primitives = new List<PlanPrimitive>
        {
            WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(500, 100)),
            WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(500, 110)),
            WallFace("right-outer", new PlanPoint(500, 100), new PlanPoint(500, 400)),
            WallFace("right-inner", new PlanPoint(490, 100), new PlanPoint(490, 400)),
            WallFace("bottom-outer", new PlanPoint(500, 400), new PlanPoint(100, 400)),
            WallFace("bottom-inner", new PlanPoint(500, 390), new PlanPoint(100, 390)),
            WallFace("left-outer", new PlanPoint(100, 400), new PlanPoint(100, 100)),
            WallFace("left-inner", new PlanPoint(110, 400), new PlanPoint(110, 100)),
            DetailLine("detail-slot-rail", new PlanPoint(176, 176), new PlanPoint(270, 176))
        };

        for (var index = 0; index < 6; index++)
        {
            var x = 180 + (index * 14.8);
            primitives.Add(DetailLine($"detail-slot-{index}-a", new PlanPoint(x, 150), new PlanPoint(x, 176)));
            primitives.Add(DetailLine($"detail-slot-{index}-b", new PlanPoint(x + 7.4, 150), new PlanPoint(x + 7.4, 176)));
        }

        var document = new PlanDocument(
            "double-line-room-with-detail-rail-supported-by-rejected-slots",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                FilterCompactObjectLineworkFromWalls = false,
                FilterDenseOrthogonalPatternsFromWalls = false
            });

        Assert.Equal(4, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Contains("detail-slot-rail"));
        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Any(id => id.StartsWith("detail-slot-", StringComparison.Ordinal)));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.unsupported_wall_body_linework_filtered"));
        Assert.Equal("7", diagnostic.Properties["filteredWallCount"]);
        Assert.Contains("detail-slot-rail", diagnostic.SourcePrimitiveIds);
        Assert.Contains("detail-slot-5-b", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_MergesShortCollinearFragmentsIntoWallRun()
    {
        var document = new PlanDocument(
            "fragmented-centerline-room",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top-fragment-a", new PlanPoint(100, 100), new PlanPoint(118, 100)),
                        WallFace("top-fragment-b", new PlanPoint(122, 100), new PlanPoint(140, 100)),
                        WallFace("top-fragment-c", new PlanPoint(144, 100), new PlanPoint(170, 100)),
                        WallFace("top-fragment-d", new PlanPoint(174, 100), new PlanPoint(210, 100)),
                        WallFace("right", new PlanPoint(210, 100), new PlanPoint(210, 300)),
                        WallFace("bottom", new PlanPoint(210, 300), new PlanPoint(100, 300)),
                        WallFace("left", new PlanPoint(100, 300), new PlanPoint(100, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var mergedWall = Assert.Single(result.Walls.Where(wall => wall.DetectionKind == WallDetectionKind.FragmentMerged));
        Assert.True(mergedWall.CenterLine.IsHorizontal());
        Assert.Equal(4, mergedWall.SourcePrimitiveIds.Count);
        Assert.Contains("top-fragment-a", mergedWall.SourcePrimitiveIds);
        Assert.Contains(mergedWall.Evidence, item => item.Contains("merged 4 fragments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(mergedWall.Evidence, item => item.Contains("healed 12", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.fragments.merged");
        Assert.Contains(result.Rooms, room => room.Boundary.Count >= 4);
    }

    [Fact]
    public async Task ScanAsync_FlagsHeavyUnlayeredFragmentMergeForGeometryReview()
    {
        var primitives = new List<PlanPrimitive>();
        for (var index = 0; index < 20; index++)
        {
            var x = 80 + (index * 8);
            primitives.Add(UnlayeredLine(
                $"uncertain-fragment-{index}",
                new PlanPoint(x, 120),
                new PlanPoint(x + 4.5, 120)));
        }

        var document = new PlanDocument(
            "uncertain-heavy-fragment-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(360, 240),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.FragmentMerged, wall.DetectionKind);
        Assert.Equal(WallType.Unknown, wall.WallType);
        var fragmentEvidence = Assert.IsType<WallFragmentEvidence>(wall.FragmentEvidence);
        Assert.Equal(20, fragmentEvidence.FragmentCount);
        Assert.True(fragmentEvidence.RequiresGeometryReview);
        Assert.True(fragmentEvidence.GapRatio > 0.08);
        Assert.True(wall.Confidence.Value <= 0.54);
        Assert.Contains(
            wall.Evidence,
            item => item.Contains("requires review", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "walls.fragment_merged_geometry.review"
                && diagnostic.Properties["wallCount"] == "1");

        using var scanDocument = JsonDocument.Parse(PlanTraceJsonExporter.Serialize(result));
        var scanWall = scanDocument.RootElement.GetProperty("walls")[0];
        var scanFragmentEvidence = scanWall.GetProperty("fragmentEvidence");
        Assert.Equal(20, scanFragmentEvidence.GetProperty("fragmentCount").GetInt32());
        Assert.True(scanFragmentEvidence.GetProperty("requiresGeometryReview").GetBoolean());

        using var placementDocument = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var placementWall = placementDocument.RootElement.GetProperty("walls")[0];
        var placementFragmentEvidence = placementWall.GetProperty("fragmentEvidence");
        Assert.Equal(20, placementFragmentEvidence.GetProperty("fragmentCount").GetInt32());
        Assert.True(placementFragmentEvidence.GetProperty("requiresGeometryReview").GetBoolean());
        var reliability = placementWall.GetProperty("reliability");
        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            reliability.GetProperty("reasons").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("fragment geometry requires review", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_ReconstructsParallelPairsFromFragmentedWallFaces()
    {
        var document = new PlanDocument(
            "fragmented-double-line-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 350),
                    new PlanPrimitive[]
                    {
                        WallFace("outer-a", new PlanPoint(100, 100), new PlanPoint(126, 100)),
                        WallFace("outer-b", new PlanPoint(130, 100), new PlanPoint(160, 100)),
                        WallFace("outer-c", new PlanPoint(164, 100), new PlanPoint(220, 100)),
                        WallFace("inner-a", new PlanPoint(100, 110), new PlanPoint(126, 110)),
                        WallFace("inner-b", new PlanPoint(130, 110), new PlanPoint(160, 110)),
                        WallFace("inner-c", new PlanPoint(164, 110), new PlanPoint(220, 110))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind);
        Assert.Equal(10, wall.Thickness);
        var pairEvidence = Assert.IsType<WallPairEvidence>(wall.PairEvidence);
        Assert.Equal(10, pairEvidence.FaceSeparation);
        Assert.Equal(1, pairEvidence.OverlapRatio);
        Assert.Equal(3, pairEvidence.FirstFaceFragmentCount);
        Assert.Equal(3, pairEvidence.SecondFaceFragmentCount);
        Assert.Contains("outer-a", pairEvidence.FirstFaceSourcePrimitiveIds);
        Assert.Contains("inner-a", pairEvidence.SecondFaceSourcePrimitiveIds);
        Assert.Equal(6, wall.SourcePrimitiveIds.Count);
        Assert.Contains(wall.Evidence, item => item.Contains("first face merged 3 fragments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wall.Evidence, item => item.Contains("second face merged 3 fragments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wall.Evidence, item => item.Contains("pair score", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.fragments.merged");
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.parallel_pairs.reconstructed");
    }

    [Fact]
    public async Task ScanAsync_MergesNonOrthogonalWallFragmentsIntoAngledWallRun()
    {
        var document = new PlanDocument(
            "fragmented-angled-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 350),
                    new PlanPrimitive[]
                    {
                        WallFace("angled-a", new PlanPoint(80, 80), new PlanPoint(110, 110)),
                        WallFace("angled-b", new PlanPoint(113, 113), new PlanPoint(150, 150)),
                        WallFace("angled-c", new PlanPoint(153, 153), new PlanPoint(210, 210))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.FragmentMerged, wall.DetectionKind);
        Assert.False(wall.CenterLine.IsHorizontal());
        Assert.False(wall.CenterLine.IsVertical());
        Assert.Equal(3, wall.SourcePrimitiveIds.Count);
        Assert.Contains("angled-a", wall.SourcePrimitiveIds);
        Assert.Contains(wall.Evidence, item => item.Contains("non-orthogonal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wall.Evidence, item => item.Contains("merged 3 fragments", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.fragments.merged"));
        Assert.Equal("1", diagnostic.Properties["nonOrthogonalMergedRunCount"]);
    }

    [Fact]
    public async Task ScanAsync_ReconstructsNonOrthogonalParallelWallFaces()
    {
        var document = new PlanDocument(
            "angled-double-line-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 350),
                    new PlanPrimitive[]
                    {
                        WallFace("angled-face-a", new PlanPoint(80, 80), new PlanPoint(220, 220)),
                        WallFace("angled-face-b", new PlanPoint(80, 90), new PlanPoint(220, 230))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.ParallelLinePair, wall.DetectionKind);
        Assert.False(wall.CenterLine.IsHorizontal());
        Assert.False(wall.CenterLine.IsVertical());
        Assert.InRange(wall.Thickness, 7.0, 7.2);
        Assert.Equal(2, wall.SourcePrimitiveIds.Count);
        Assert.Contains(wall.Evidence, item => item.Contains("non-orthogonal parallel wall-face pair", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.parallel_pairs.reconstructed"));
        Assert.Equal("1", diagnostic.Properties["nonOrthogonalPairCount"]);
    }

    [Fact]
    public async Task ScanAsync_WarnsWhenReconstructedPairThicknessesVaryStrongly()
    {
        var document = new PlanDocument(
            "mixed-wall-thickness-pairs",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 350),
                    new PlanPrimitive[]
                    {
                        WallFace("thin-face-a", new PlanPoint(80, 100), new PlanPoint(260, 100)),
                        WallFace("thin-face-b", new PlanPoint(80, 106), new PlanPoint(260, 106)),
                        WallFace("thick-face-a", new PlanPoint(80, 180), new PlanPoint(260, 180)),
                        WallFace("thick-face-b", new PlanPoint(80, 198), new PlanPoint(260, 198))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(2, result.Walls.Count);
        Assert.All(result.Walls, wall => Assert.NotNull(wall.PairEvidence));

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.parallel_pair_thickness_variance"));
        Assert.Equal("2", diagnostic.Properties["wallCount"]);
        Assert.Equal("6", diagnostic.Properties["minFaceSeparation"]);
        Assert.Equal("12", diagnostic.Properties["medianFaceSeparation"]);
        Assert.Equal("18", diagnostic.Properties["maxFaceSeparation"]);
        Assert.Contains("thin-face-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("thick-face-b", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_UsesDominantPairSeparationToSuppressOutlierWidthPairs()
    {
        var primitives = new List<PlanPrimitive>();
        for (var index = 0; index < 12; index++)
        {
            var y = 70 + (index * 28);
            primitives.Add(WallFace($"dominant-{index}-a", new PlanPoint(80, y), new PlanPoint(260, y)));
            primitives.Add(WallFace($"dominant-{index}-b", new PlanPoint(80, y + 6), new PlanPoint(260, y + 6)));
        }

        primitives.Add(WallFace("outlier-a", new PlanPoint(80, 450), new PlanPoint(260, 450)));
        primitives.Add(WallFace("outlier-b", new PlanPoint(80, 468), new PlanPoint(260, 468)));
        var document = new PlanDocument(
            "dominant-wall-thickness-profile",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 520),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(12, result.Walls.Count(wall => wall.DetectionKind == WallDetectionKind.ParallelLinePair));
        Assert.Equal(2, result.Walls.Count(wall => wall.DetectionKind == WallDetectionKind.SingleLine));
        Assert.DoesNotContain(result.Walls, wall => wall.PairEvidence?.FaceSeparation == 18);
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.parallel_pair_thickness_variance");

        var profile = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.parallel_pairs.separation_profile"));
        Assert.Equal("axis", profile.Properties["runKind"]);
        Assert.Equal("6", profile.Properties["dominantFaceSeparation"]);
        Assert.Equal("12", profile.Properties["maxTrustedFaceSeparation"]);
    }

    [Fact]
    public async Task ScanAsync_UsesSharedAxisSeparationProfileAcrossOrientations()
    {
        var primitives = new List<PlanPrimitive>();
        for (var index = 0; index < 12; index++)
        {
            var y = 70 + (index * 28);
            primitives.Add(WallFace($"dominant-horizontal-{index}-a", new PlanPoint(80, y), new PlanPoint(260, y)));
            primitives.Add(WallFace($"dominant-horizontal-{index}-b", new PlanPoint(80, y + 6), new PlanPoint(260, y + 6)));
        }

        primitives.Add(WallFace("sparse-vertical-outlier-a", new PlanPoint(340, 70), new PlanPoint(340, 250)));
        primitives.Add(WallFace("sparse-vertical-outlier-b", new PlanPoint(358, 70), new PlanPoint(358, 250)));
        var document = new PlanDocument(
            "shared-axis-wall-thickness-profile",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 520),
                    primitives)
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(12, result.Walls.Count(wall => wall.DetectionKind == WallDetectionKind.ParallelLinePair));
        Assert.Equal(2, result.Walls.Count(wall => wall.DetectionKind == WallDetectionKind.SingleLine));
        Assert.DoesNotContain(result.Walls, wall => wall.PairEvidence?.FaceSeparation == 18);
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.parallel_pair_thickness_variance");

        var profile = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.parallel_pairs.separation_profile"));
        Assert.Equal("axis", profile.Properties["runKind"]);
        Assert.Equal("6", profile.Properties["dominantFaceSeparation"]);
        Assert.Equal("12", profile.Properties["maxTrustedFaceSeparation"]);
        Assert.Equal("12", profile.Properties["reconstructedPairCount"]);
    }

    [Fact]
    public async Task ScanAsync_DoesNotHealFragmentGapBeyondConfiguredLimit()
    {
        var document = new PlanDocument(
            "fragment-gap-too-large",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 250),
                    new PlanPrimitive[]
                    {
                        WallFace("fragment-a", new PlanPoint(80, 100), new PlanPoint(100, 100)),
                        WallFace("fragment-b", new PlanPoint(120, 100), new PlanPoint(140, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                MinWallLength = 36,
                MinWallFragmentLength = 4,
                MaxWallFragmentGap = 8
            });

        Assert.Empty(result.Walls);
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.fragments.merged");
    }

    [Fact]
    public async Task ScanAsync_CollapsesDuplicateAxisWallLineworkWithoutFragmentEvidence()
    {
        var document = new PlanDocument(
            "duplicate-axis-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 600),
                    new PlanPrimitive[]
                    {
                        WallFace("top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("top-duplicate", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        WallFace("right", new PlanPoint(500, 100), new PlanPoint(500, 400)),
                        WallFace("bottom", new PlanPoint(500, 400), new PlanPoint(100, 400)),
                        WallFace("left", new PlanPoint(100, 400), new PlanPoint(100, 100))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(4, result.Walls.Count);
        var top = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("top"));
        Assert.Equal(WallDetectionKind.SingleLine, top.DetectionKind);
        Assert.Contains("top-duplicate", top.SourcePrimitiveIds);
        Assert.Contains(top.Evidence, item => item.Contains("collapsed 1 duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.fragments.merged");
        Assert.Contains(result.Rooms, room => room.Boundary.Count >= 4);

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.duplicates.collapsed"));
        Assert.Equal("1", diagnostic.Properties["duplicatePrimitiveCount"]);
        Assert.Equal("1", diagnostic.Properties["axisDuplicateRunCount"]);
        Assert.Contains("top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("top-duplicate", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_CollapsesDuplicateNonOrthogonalWallLinework()
    {
        var document = new PlanDocument(
            "duplicate-angled-wall",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 350),
                    new PlanPrimitive[]
                    {
                        WallFace("angled", new PlanPoint(80, 80), new PlanPoint(220, 220)),
                        WallFace("angled-duplicate", new PlanPoint(80, 80), new PlanPoint(220, 220))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var wall = Assert.Single(result.Walls);
        Assert.Equal(WallDetectionKind.SingleLine, wall.DetectionKind);
        Assert.Equal(2, wall.SourcePrimitiveIds.Count);
        Assert.Contains("angled-duplicate", wall.SourcePrimitiveIds);
        Assert.Contains(wall.Evidence, item => item.Contains("collapsed 1 duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.fragments.merged");

        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(message => message.Code == "walls.duplicates.collapsed"));
        Assert.Equal("1", diagnostic.Properties["duplicatePrimitiveCount"]);
        Assert.Equal("0", diagnostic.Properties["axisDuplicateRunCount"]);
        Assert.Equal("1", diagnostic.Properties["nonOrthogonalDuplicateRunCount"]);
    }

    [Fact]
    public async Task JsonExporter_IncludesWallDetectionKindAndEvidence()
    {
        var document = new PlanDocument(
            "wall-pair-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        WallFace("top-outer", new PlanPoint(100, 100), new PlanPoint(300, 100)),
                        WallFace("top-inner", new PlanPoint(100, 110), new PlanPoint(300, 110))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var wall = parsed.RootElement.GetProperty("walls")[0];

        Assert.Equal("ParallelLinePair", wall.GetProperty("detectionKind").GetString());
        var pairEvidence = wall.GetProperty("pairEvidence");
        Assert.Equal(10, pairEvidence.GetProperty("faceSeparation").GetDouble());
        Assert.Equal(1, pairEvidence.GetProperty("overlapRatio").GetDouble());
        Assert.Equal(1, pairEvidence.GetProperty("firstFaceFragmentCount").GetInt32());
        Assert.Equal(1, pairEvidence.GetProperty("secondFaceFragmentCount").GetInt32());
        Assert.Contains(
            pairEvidence.GetProperty("firstFaceSourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()),
            item => item == "top-outer");
        Assert.Contains(
            pairEvidence.GetProperty("secondFaceSourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()),
            item => item == "top-inner");
        Assert.True(wall.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Contains(
            wall.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()),
            item => item is not null && item.Contains("classified Wall", StringComparison.OrdinalIgnoreCase));
    }

    private static LinePrimitive WallFace(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "A-WALL",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static LinePrimitive DetailLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-FURN",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "A-FURN",
                DrawingSpace = SourceDrawingSpace.Model
            }
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
}
