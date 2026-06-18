namespace OpenPlanTrace.Tests;

public sealed class ArcDoorLeafWallFilteringTests
{
    [Fact]
    public async Task ScanAsync_SuppressesArcDoorLeafLineworkFromWalls()
    {
        var document = new PlanDocument(
            "arc-door-leaf-wall-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        Wall("room-right", new PlanPoint(400, 100), new PlanPoint(400, 300)),
                        Wall("room-bottom", new PlanPoint(400, 300), new PlanPoint(100, 300)),
                        Wall("room-left", new PlanPoint(100, 300), new PlanPoint(100, 100)),
                        Wall("real-short-partition", new PlanPoint(300, 100), new PlanPoint(300, 158)),
                        DoorLeaf("door-leaf-noise", new PlanPoint(220, 100), new PlanPoint(220, 130)),
                        DoorArc("door-swing", new PlanPoint(220, 100), 30, 0, Math.PI / 2)
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.DoesNotContain(result.Walls, wall => wall.SourcePrimitiveIds.Contains("door-leaf-noise"));
        Assert.Contains(result.Walls, wall => wall.SourcePrimitiveIds.Contains("real-short-partition"));
        Assert.Contains(result.Openings, opening => opening.SourcePrimitiveIds.Contains("door-swing"));
        var diagnostic = Assert.Single(result.Diagnostics.Messages.Where(
            message => message.Code == "walls.door_detail_symbol_walls_filtered"));
        Assert.Contains("door-leaf-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsRadialDoorLeafEvenWithWallEndpointSupport()
    {
        var document = new PlanDocument(
            "wall-evidence-radial-door-leaf-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLeaf("door-leaf-noise", new PlanPoint(200, 100), new PlanPoint(200, 132)),
                        DoorArc("door-swing", new PlanPoint(200, 100), 32, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-door-leaf-noise",
            1,
            new PlanLineSegment(new PlanPoint(200, 100), new PlanPoint(200, 132)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "door-leaf-noise" },
            Evidence = new[] { "test candidate touching host wall endpoint support" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("door-leaf-noise"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("door-leaf-noise"));
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.True(rejected.ScoreBreakdown.NoisePenalty >= 0.8);
        Assert.Contains(
            rejected.ScoreBreakdown.NegativeEvidence,
            item => item.Contains(nameof(WallEvidenceCategory.DoorOrOpeningSymbol), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rejected.Evidence, item => item.Contains("radially tied to swing arc", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(
            message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("1", diagnostic.Properties["doorSymbolRejectedCount"]);
        Assert.Contains("door-leaf-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsDoorLayerArcLineDespiteEndpointSupport()
    {
        var document = new PlanDocument(
            "wall-evidence-door-layer-arc-line-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLayerLine("door-layer-line-noise", new PlanPoint(210, 110), new PlanPoint(242, 110)),
                        DoorArc("door-swing", new PlanPoint(200, 100), 32, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-door-layer-line-noise",
            1,
            new PlanLineSegment(new PlanPoint(210, 110), new PlanPoint(242, 110)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "door-layer-line-noise" },
            Evidence = new[] { "test short line touches host wall but follows door swing symbol" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("door-layer-line-noise"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("door-layer-line-noise"));
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NoisePenalty >= 0.8);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("despite structural endpoint support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            rejected.ScoreBreakdown.NegativeEvidence,
            item => item.Contains(nameof(WallEvidenceCategory.DoorOrOpeningSymbol), StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages.Where(
            message => message.Code == "wall_evidence.noise_walls_rejected"));
        Assert.Equal("1", diagnostic.Properties["doorSymbolRejectedCount"]);
        Assert.Contains("door-layer-line-noise", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsDoorLayerParallelPairNearSwingArcBeforeStrongWallAcceptance()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(210, 112), new PlanPoint(246, 112));
        var secondFace = new PlanLineSegment(new PlanPoint(210, 118), new PlanPoint(246, 118));
        var document = new PlanDocument(
            "wall-evidence-door-layer-paired-frame-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLayerLine("door-frame-face-a", firstFace.Start, firstFace.End),
                        DoorLayerLine("door-frame-face-b", secondFace.Start, secondFace.End),
                        DoorArc("door-swing", new PlanPoint(200, 100), 36, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60,
                DefaultWallThickness = 4
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-door-frame-pair-noise",
            1,
            new PlanLineSegment(new PlanPoint(210, 115), new PlanPoint(246, 115)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = new[] { "door-frame-face-a", "door-frame-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.90,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "door-frame-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "door-frame-face-b" }),
            Evidence = new[] { "test high-scoring paired door frame" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("door-frame-face-a"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("door-frame-face-a"));
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("paired door/window frame linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsUnlayeredParallelDoorFrameNearSwingArcBeforeStrongWallAcceptance()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(210, 112), new PlanPoint(246, 112));
        var secondFace = new PlanLineSegment(new PlanPoint(210, 118), new PlanPoint(246, 118));
        var document = new PlanDocument(
            "wall-evidence-unlayered-paired-door-frame-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLeaf("unlayered-frame-face-a", firstFace.Start, firstFace.End),
                        DoorLeaf("unlayered-frame-face-b", secondFace.Start, secondFace.End),
                        DoorArc("door-swing", new PlanPoint(200, 100), 36, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60,
                DefaultWallThickness = 4
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-unlayered-frame-pair-noise",
            1,
            new PlanLineSegment(new PlanPoint(210, 115), new PlanPoint(246, 115)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = new[] { "unlayered-frame-face-a", "unlayered-frame-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.90,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "unlayered-frame-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "unlayered-frame-face-b" }),
            Evidence = new[] { "test high-scoring unlayered paired door frame" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("unlayered-frame-face-a"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("unlayered-frame-face-a"));
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.True(rejected.ScoreBreakdown.NegativeScore > rejected.ScoreBreakdown.PositiveScore);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("unlayered paired door/window frame linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsUnlayeredArcLineWithOnlyOneEndpointSupport()
    {
        var document = new PlanDocument(
            "wall-evidence-unlayered-arc-line-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLeaf("unlayered-arc-line-noise", new PlanPoint(210, 110), new PlanPoint(242, 110)),
                        DoorArc("door-swing", new PlanPoint(200, 100), 32, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-unlayered-arc-line-noise",
            1,
            new PlanLineSegment(new PlanPoint(210, 110), new PlanPoint(242, 110)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "unlayered-arc-line-noise" },
            Evidence = new[] { "test unlayered short line follows door swing symbol" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        Assert.DoesNotContain(context.Walls, wall => wall.SourcePrimitiveIds.Contains("unlayered-arc-line-noise"));

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.SourcePrimitiveIds.Contains("unlayered-arc-line-noise"));
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("only one structural support wall", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_DowngradesShortUnlayeredOneSupportedCandidateToReview()
    {
        var document = new PlanDocument(
            "wall-evidence-short-one-supported-line-review",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLeaf("short-unlayered-detail", new PlanPoint(200, 100), new PlanPoint(200, 132))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinWallLength = 36,
                DefaultWallThickness = 4
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-short-unlayered-detail",
            1,
            new PlanLineSegment(new PlanPoint(200, 100), new PlanPoint(200, 132)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "short-unlayered-detail" },
            Evidence = new[] { "test short unlayered one-supported line" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);
        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("short-unlayered-detail"));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.SourcePrimitiveIds.Contains("short-unlayered-detail"));

        Assert.Equal(WallEvidenceCategory.WeakSingleLine, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.False(assessment.RejectedAsNoise);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("only one structural endpoint support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("wall-short-unlayered-detail", context.WallTopologyPreparation.GraphWallIds);
        Assert.Contains("wall-short-unlayered-detail", context.WallTopologyPreparation.ReviewGraphWallIds);
        Assert.DoesNotContain("wall-short-unlayered-detail", context.WallTopologyPreparation.AutomaticCoordinateRepairWallIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DowngradesShortUnlayeredCandidateSupportedOnlyByOneDistinctWall()
    {
        var document = new PlanDocument(
            "wall-evidence-short-same-host-support-review",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
                        DoorLeaf("short-near-host-detail", new PlanPoint(180, 106), new PlanPoint(220, 106))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinWallLength = 36,
                DefaultWallThickness = 4
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host",
            1,
            new PlanLineSegment(new PlanPoint(80, 100), new PlanPoint(320, 100)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-wall" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-short-near-host-detail",
            1,
            new PlanLineSegment(new PlanPoint(180, 106), new PlanPoint(220, 106)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "short-near-host-detail" },
            Evidence = new[] { "test short unlayered line near one structural wall" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);
        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("short-near-host-detail"));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.SourcePrimitiveIds.Contains("short-near-host-detail"));

        Assert.Equal(WallEvidenceCategory.WeakSingleLine, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.False(assessment.RejectedAsNoise);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("one distinct structural wall", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("wall-short-near-host-detail", context.WallTopologyPreparation.ReviewGraphWallIds);
        Assert.DoesNotContain("wall-short-near-host-detail", context.WallTopologyPreparation.AutomaticCoordinateRepairWallIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_KeepsUnlayeredShortWallWithTwoEndpointSupportsNearDoorArc()
    {
        var document = new PlanDocument(
            "wall-evidence-supported-short-wall-near-arc",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(420, 260),
                    new PlanPrimitive[]
                    {
                        Wall("host-left", new PlanPoint(210, 60), new PlanPoint(210, 160)),
                        Wall("host-right", new PlanPoint(242, 60), new PlanPoint(242, 160)),
                        DoorLeaf("real-short-wall", new PlanPoint(210, 110), new PlanPoint(242, 110)),
                        DoorArc("nearby-door-swing", new PlanPoint(200, 100), 32, 0, Math.PI / 2)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = true,
                MinOpeningGap = 8,
                MaxOpeningGap = 60
            })
        {
            LayerAnalysis = new PlanLayerAnalysis(new[]
            {
                Layer("A-WALL", LayerCategory.Wall, Confidence.High),
                Layer("A-DOOR", LayerCategory.Door, Confidence.High)
            })
        };
        context.WallCandidates.Add(new WallSegment(
            "wall-host-left",
            1,
            new PlanLineSegment(new PlanPoint(210, 60), new PlanPoint(210, 160)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-left" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-host-right",
            1,
            new PlanLineSegment(new PlanPoint(242, 60), new PlanPoint(242, 160)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { "host-right" },
            Evidence = new[] { "test structural wall" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-real-short-wall",
            1,
            new PlanLineSegment(new PlanPoint(210, 110), new PlanPoint(242, 110)),
            4,
            Confidence.Medium)
        {
            SourcePrimitiveIds = new[] { "real-short-wall" },
            Evidence = new[] { "test short wall with two structural endpoint supports" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.SourcePrimitiveIds.Contains("real-short-wall"));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.SourcePrimitiveIds.Contains("real-short-wall"));
        Assert.NotEqual(WallEvidenceDecision.Reject, assessment.Decision);
        Assert.False(assessment.RejectedAsNoise);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
    }

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
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

    private static LinePrimitive DoorLayerLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-DOOR",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "A-DOOR",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static LinePrimitive DoorLeaf(string sourceId, PlanPoint start, PlanPoint end) =>
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

    private static ArcPrimitive DoorArc(
        string sourceId,
        PlanPoint center,
        double radius,
        double startAngleRadians,
        double sweepAngleRadians) =>
        new(center, radius, startAngleRadians, sweepAngleRadians)
        {
            SourceId = sourceId,
            Layer = "A-DOOR",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "ARC",
                Layer = "A-DOOR",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };
}
