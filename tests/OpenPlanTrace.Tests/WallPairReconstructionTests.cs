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
        Assert.All(result.Walls, wall => Assert.Equal(10, wall.Thickness));
        Assert.Contains(result.Walls, wall => wall.CenterLine.IsHorizontal() && Math.Abs(wall.CenterLine.Start.Y - 105) < 0.01);
        Assert.Contains(result.Walls, wall => wall.CenterLine.IsVertical() && Math.Abs(wall.CenterLine.Start.X - 495) < 0.01);
        Assert.Contains(result.Rooms, room => room.Bounds.Width > 380 && room.Bounds.Height > 280);
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "walls.parallel_pairs.reconstructed");
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
}
