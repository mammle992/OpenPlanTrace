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
    public async Task WallEvidenceRefinement_RecoversWallBandWhenOneFaceWasAlreadyWeakSingleLine()
    {
        var document = new PlanDocument(
            "wall-evidence-recovery-reuses-weak-face",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 280),
                    new PlanPrimitive[]
                    {
                        Line("recover-face-a", new PlanPoint(100, 118), new PlanPoint(400, 118)),
                        Line("recover-face-b", new PlanPoint(100, 124), new PlanPoint(400, 124))
                    })
            });
        var context = new ScanContext(document, new ScannerOptions { DefaultWallThickness = 6 });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 240)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(400, 50), new PlanPoint(400, 240)));
        context.WallCandidates.Add(SingleLineWall("recover-face-a", new PlanPoint(100, 118), new PlanPoint(400, 118)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls.Where(wall =>
            wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal(WallDetectionKind.ParallelLinePair, recovered.DetectionKind);
        Assert.Contains("recover-face-a", recovered.SourcePrimitiveIds);
        Assert.Contains("recover-face-b", recovered.SourcePrimitiveIds);

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments, item => item.WallId == recovered.Id);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.True(assessment.PlacementReady);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RecoversWallBandFromRectangleEdges()
    {
        var document = new PlanDocument(
            "wall-evidence-recovery-rectangle-edges",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 280),
                    new PlanPrimitive[]
                    {
                        Rectangle("rect-wall-body", new PlanRect(100, 118, 300, 6))
                    })
            });
        var context = new ScanContext(document, new ScannerOptions { DefaultWallThickness = 6 });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 240)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(400, 50), new PlanPoint(400, 240)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls.Where(wall =>
            wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase))));
        Assert.Equal(WallDetectionKind.ParallelLinePair, recovered.DetectionKind);
        Assert.Contains("rect-wall-body", recovered.SourcePrimitiveIds);
        Assert.Equal(6, recovered.PairEvidence?.FaceSeparation);
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
    public async Task WallEvidenceRefinement_MarksAdjacentRecoveredBandDuplicateReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-adjacent-recovered-band-deduplication",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(520, 320),
                    new PlanPrimitive[]
                    {
                        Line("primary-face-a", new PlanPoint(100, 118), new PlanPoint(400, 118)),
                        Line("primary-face-b", new PlanPoint(100, 124), new PlanPoint(400, 124)),
                        Line("adjacent-duplicate-face-a", new PlanPoint(100, 128), new PlanPoint(400, 128)),
                        Line("adjacent-duplicate-face-b", new PlanPoint(100, 134), new PlanPoint(400, 134))
                    })
            });
        var context = new ScanContext(document, new ScannerOptions { DefaultWallThickness = 6 });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 285)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(400, 50), new PlanPoint(400, 285)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = context.Walls
            .Where(wall => wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.Equal(2, recovered.Length);
        var recoveredAssessments = context.WallEvidenceMap.WallAssessments
            .Where(assessment => recovered.Any(wall => wall.Id == assessment.WallId))
            .ToArray();

        var accepted = Assert.Single(recoveredAssessments, assessment => assessment.Decision == WallEvidenceDecision.Accept);
        Assert.True(accepted.PlacementReady);

        var review = Assert.Single(recoveredAssessments, assessment => assessment.Decision == WallEvidenceDecision.Review);
        Assert.False(review.PlacementReady);
        Assert.True(review.RequiresReview);
        Assert.Contains(
            review.Evidence,
            item => item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase)
                && item.Contains(accepted.WallId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WallEvidenceRefinement_MarksRecoveredDuplicatePairedBodyReviewOnly()
    {
        var context = new ScanContext(
            new PlanDocument(
                "wall-evidence-recovered-duplicate-body",
                new[]
                {
                    new PlanPage(1, new PlanSize(420, 320), Array.Empty<PlanPrimitive>())
                }),
            new ScannerOptions
            {
                DefaultWallThickness = 6,
                WallSnapTolerance = 4
            });
        var knownExterior = PairedWall(
            "known-exterior",
            new PlanLineSegment(new PlanPoint(200, 60), new PlanPoint(200, 240)),
            faceSeparation: 12,
            pairScore: 0.80,
            WallType.Exterior);
        var recoveredDuplicate = PairedWall(
            "recovered-duplicate",
            new PlanLineSegment(new PlanPoint(210, 62), new PlanPoint(210, 240)),
            faceSeparation: 6,
            pairScore: 0.82,
            WallType.Unknown) with
            {
                Evidence = new[]
                {
                    "recovered by wall evidence map from unclaimed parallel wall-face evidence"
                }
            };
        context.WallCandidates.Add(knownExterior);
        context.WallCandidates.Add(recoveredDuplicate);

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var knownAssessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == knownExterior.Id);
        Assert.Equal(WallEvidenceDecision.Accept, knownAssessment.Decision);
        Assert.True(knownAssessment.PlacementReady);

        var duplicateAssessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == recoveredDuplicate.Id);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, duplicateAssessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, duplicateAssessment.Decision);
        Assert.False(duplicateAssessment.PlacementReady);
        Assert.True(duplicateAssessment.RequiresReview);
        Assert.Contains(
            duplicateAssessment.Evidence,
            item => item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase)
                && item.Contains(knownExterior.Id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotSuppressLongRecoveredWallForShortOverlap()
    {
        var context = new ScanContext(
            new PlanDocument(
                "wall-evidence-long-recovered-not-short-duplicate",
                new[]
                {
                    new PlanPage(1, new PlanSize(420, 320), Array.Empty<PlanPrimitive>())
                }),
            new ScannerOptions
            {
                DefaultWallThickness = 6,
                WallSnapTolerance = 4
            });
        var shortKnownWall = PairedWall(
            "short-known-wall",
            new PlanLineSegment(new PlanPoint(200, 60), new PlanPoint(200, 105)),
            faceSeparation: 8,
            pairScore: 0.86,
            WallType.Interior);
        var recoveredLongWall = PairedWall(
            "long-recovered-wall",
            new PlanLineSegment(new PlanPoint(204, 62), new PlanPoint(204, 240)),
            faceSeparation: 6,
            pairScore: 0.82,
            WallType.Unknown) with
            {
                Evidence = new[]
                {
                    "recovered by wall evidence map from unclaimed parallel wall-face evidence"
                }
            };
        context.WallCandidates.Add(shortKnownWall);
        context.WallCandidates.Add(recoveredLongWall);

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recoveredAssessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == recoveredLongWall.Id);
        Assert.Equal(WallEvidenceCategory.RecoveredWallBody, recoveredAssessment.Category);
        Assert.Equal(WallEvidenceDecision.Accept, recoveredAssessment.Decision);
        Assert.True(recoveredAssessment.PlacementReady);
        Assert.False(recoveredAssessment.RequiresReview);
        Assert.DoesNotContain(
            recoveredAssessment.Evidence,
            item => item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task WallEvidenceRefinement_KeepsShortUnlayeredRecoveredSegmentsReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-short-unlayered-segment-review",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(260, 220),
                    new PlanPrimitive[]
                    {
                        Line("short-unlayered-segment", new PlanPoint(100, 100), new PlanPoint(122, 100))
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
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 180)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(122, 50), new PlanPoint(122, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls.Where(wall => wall.SourcePrimitiveIds.Contains("short-unlayered-segment")));
        Assert.Equal(WallDetectionKind.SingleLine, recovered.DetectionKind);
        Assert.Equal(WallType.Unknown, recovered.WallType);
        Assert.Contains(recovered.Evidence, item => item.Contains("short supported wall segment", StringComparison.OrdinalIgnoreCase));

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments, item => item.WallId == recovered.Id);
        Assert.Equal(WallEvidenceCategory.RecoveredWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("short recovered unlayered/unknown wall segment requires review", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotRecoverUnsupportedOneEndedShortReturns()
    {
        var document = new PlanDocument(
            "wall-evidence-one-ended-short-return-gate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(260, 220),
                    new PlanPrimitive[]
                    {
                        Line("one-ended-short-return", new PlanPoint(100, 100), new PlanPoint(122, 100))
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
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source == "one-ended-short-return");
    }

    [Fact]
    public async Task WallEvidenceRefinement_RecoversStructurallySupportedDimensionLikeShortWallsAsReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-dimension-like-short-wall-recovery",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(260, 220),
                    new PlanPrimitive[]
                    {
                        Line("dimension-like-short-wall", new PlanPoint(100, 100), new PlanPoint(122, 100), "A-DIMS")
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
            Layer("A-DIMS", LayerCategory.Dimension)
        });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 180)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(122, 50), new PlanPoint(122, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls.Where(wall => wall.SourcePrimitiveIds.Contains("dimension-like-short-wall")));
        Assert.Contains(
            recovered.Evidence,
            item => item.Contains("source layer category Dimension", StringComparison.OrdinalIgnoreCase));

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments, item => item.WallId == recovered.Id);
        Assert.Equal(WallEvidenceCategory.RecoveredWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotRecoverOneEndedDoorLeafLineNearSwingArc()
    {
        var document = new PlanDocument(
            "wall-evidence-short-recovery-door-leaf-gate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(300, 220),
                    new PlanPrimitive[]
                    {
                        Line("door-leaf-line", new PlanPoint(200, 100), new PlanPoint(232, 100)),
                        DoorArc("door-swing", new PlanPoint(200, 100), 32, 0, Math.PI / 2)
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
        context.WallCandidates.Add(HostWall("host-hinge-side", new PlanPoint(200, 50), new PlanPoint(200, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source == "door-leaf-line");
    }

    [Fact]
    public async Task WallEvidenceRefinement_KeepsShortUnknownFragmentMergedWallsReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-short-fragment-merged-review",
            new[]
            {
                new PlanPage(1, new PlanSize(260, 220), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallLength = 36
            });
        var fragmentWall = new WallSegment(
            "wall-short-unknown-fragment",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(124, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            FragmentEvidence = new WallFragmentEvidence(
                6,
                8,
                2,
                0,
                0.18,
                RequiresGeometryReview: false,
                new[] { "fragment-merged test wall" }),
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b" },
            Evidence = new[] { "merged collinear wall fragments" }
        };
        context.WallCandidates.Add(fragmentWall);
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 180)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(124, 50), new PlanPoint(124, 180)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == fragmentWall.Id);

        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("short unknown fragment-merged wall candidate requires review", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_KeepsUnsupportedUnlayeredFragmentMergedWallsReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-unsupported-fragment-merged-review",
            new[]
            {
                new PlanPage(1, new PlanSize(320, 220), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallLength = 36
            });
        var fragmentWall = new WallSegment(
            "wall-unsupported-fragment",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(190, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            FragmentEvidence = new WallFragmentEvidence(
                8,
                12,
                3,
                0,
                0.15,
                RequiresGeometryReview: false,
                new[] { "fragment-merged test wall" }),
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b" },
            Evidence = new[] { "merged collinear wall fragments" }
        };
        context.WallCandidates.Add(fragmentWall);
        context.WallCandidates.Add(PairedWall(
            "support-left",
            new PlanLineSegment(new PlanPoint(100, 50), new PlanPoint(100, 180)),
            8,
            0.82,
            WallType.Interior));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == fragmentWall.Id);

        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("unlayered fragment-merged wall candidate", StringComparison.OrdinalIgnoreCase)
                && item.Contains("only one trusted structural endpoint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_KeepsSparseUnlayeredFragmentMergedWallsReviewOnlyEvenWithEndpointSupport()
    {
        var document = new PlanDocument(
            "wall-evidence-sparse-fragment-merged-review",
            new[]
            {
                new PlanPage(1, new PlanSize(340, 240), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallLength = 36
            });
        var fragmentWall = new WallSegment(
            "wall-sparse-fragment-detail",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(230, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            FragmentEvidence = new WallFragmentEvidence(
                0,
                0,
                2,
                0,
                0,
                RequiresGeometryReview: false,
                new[] { "sparse fragment-merged detail-like wall" }),
            SourcePrimitiveIds = new[] { "fixture-fragment-a", "fixture-fragment-b" },
            Evidence = new[] { "merged collinear wall fragments" }
        };
        context.WallCandidates.Add(fragmentWall);
        context.WallCandidates.Add(PairedWall(
            "support-left",
            new PlanLineSegment(new PlanPoint(100, 50), new PlanPoint(100, 180)),
            8,
            0.86,
            WallType.Interior));
        context.WallCandidates.Add(PairedWall(
            "support-right",
            new PlanLineSegment(new PlanPoint(230, 50), new PlanPoint(230, 180)),
            8,
            0.86,
            WallType.Interior));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == fragmentWall.Id);

        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("sparse unlayered fragment-merged wall candidate", StringComparison.OrdinalIgnoreCase)
                && item.Contains("possible fixture/detail linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_AcceptsTrustedSupportedUnlayeredFragmentMergedWalls()
    {
        var document = new PlanDocument(
            "wall-evidence-supported-fragment-merged-accept",
            new[]
            {
                new PlanPage(1, new PlanSize(320, 220), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallLength = 36
            });
        var fragmentWall = new WallSegment(
            "wall-supported-fragment",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(190, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            FragmentEvidence = new WallFragmentEvidence(
                8,
                12,
                3,
                0,
                0.15,
                RequiresGeometryReview: false,
                new[] { "fragment-merged test wall" }),
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b" },
            Evidence = new[] { "merged collinear wall fragments" }
        };
        context.WallCandidates.Add(fragmentWall);
        context.WallCandidates.Add(PairedWall(
            "support-left",
            new PlanLineSegment(new PlanPoint(100, 50), new PlanPoint(100, 180)),
            8,
            0.82,
            WallType.Interior));
        context.WallCandidates.Add(PairedWall(
            "support-right",
            new PlanLineSegment(new PlanPoint(190, 50), new PlanPoint(190, 180)),
            8,
            0.82,
            WallType.Interior));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == fragmentWall.Id);

        Assert.Equal(WallEvidenceCategory.MediumWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("medium wall body from wall-like layer, length, or structural context", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallEvidenceRefinement_KeepsRepeatedShortUnlayeredLineworkReviewOnly()
    {
        var document = new PlanDocument(
            "wall-evidence-repeated-short-unlayered-detail-review",
            new[]
            {
                new PlanPage(1, new PlanSize(320, 240), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                MinWallLength = 36
            });
        var detailWalls = new[]
        {
            SingleLineWall("detail-slot-a", new PlanPoint(100, 100), new PlanPoint(100, 154)),
            SingleLineWall("detail-slot-b", new PlanPoint(150, 100), new PlanPoint(150, 154)),
            SingleLineWall("detail-slot-c", new PlanPoint(200, 100), new PlanPoint(200, 154))
        };
        context.WallCandidates.AddRange(detailWalls);
        context.WallCandidates.Add(HostWall("top-host", new PlanPoint(80, 100), new PlanPoint(220, 100)));
        context.WallCandidates.Add(HostWall("bottom-host", new PlanPoint(80, 154), new PlanPoint(220, 154)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        foreach (var detailWall in detailWalls)
        {
            var assessment = Assert.Single(
                context.WallEvidenceMap.WallAssessments,
                item => item.WallId == detailWall.Id);

            Assert.Equal(WallEvidenceCategory.ObjectOrFixtureDetail, assessment.Category);
            Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
            Assert.False(assessment.PlacementReady);
            Assert.True(assessment.RequiresReview);
            Assert.False(assessment.RejectedAsNoise);
            Assert.Contains(
                assessment.Evidence,
                item => item.Contains("repeated short unlayered vertical linework group", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotRecoverRepeatedShortFixtureSlotsAsWalls()
    {
        var document = new PlanDocument(
            "wall-evidence-repeated-short-slot-recovery-gate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(260, 240),
                    new PlanPrimitive[]
                    {
                        Line("slot-detail-a", new PlanPoint(100, 90), new PlanPoint(122, 90)),
                        Line("slot-detail-b", new PlanPoint(100, 110), new PlanPoint(122, 110)),
                        Line("slot-detail-c", new PlanPoint(100, 130), new PlanPoint(122, 130)),
                        Line("real-short-wall", new PlanPoint(100, 175), new PlanPoint(122, 175), "A-WALL")
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
            Layer("A-WALL", LayerCategory.Wall)
        });
        context.WallCandidates.Add(HostWall("host-left", new PlanPoint(100, 50), new PlanPoint(100, 205)));
        context.WallCandidates.Add(HostWall("host-right", new PlanPoint(122, 50), new PlanPoint(122, 205)));

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source.StartsWith("slot-detail-", StringComparison.Ordinal));

        var recoveredWall = Assert.Single(context.Walls.Where(wall => wall.SourcePrimitiveIds.Contains("real-short-wall")));
        Assert.Equal(WallDetectionKind.SingleLine, recoveredWall.DetectionKind);
        Assert.Contains(recoveredWall.Evidence, item => item.Contains("short supported wall segment", StringComparison.OrdinalIgnoreCase));

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments, item => item.WallId == recoveredWall.Id);
        Assert.Equal(WallEvidenceCategory.RecoveredWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages, item => item.Code == "wall_evidence.short_repeated_slots_suppressed");
        Assert.Equal("3", diagnostic.Properties["suppressedSourceCount"]);
        Assert.Contains("slot-detail-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("slot-detail-c", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DoesNotRecoverRepeatedCollinearShortTicksAsWalls()
    {
        var document = new PlanDocument(
            "wall-evidence-repeated-short-collinear-tick-recovery-gate",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(340, 220),
                    new PlanPrimitive[]
                    {
                        Line("tick-detail-a", new PlanPoint(80, 90), new PlanPoint(102, 90)),
                        Line("tick-detail-b", new PlanPoint(150, 90), new PlanPoint(172, 90)),
                        Line("tick-detail-c", new PlanPoint(220, 90), new PlanPoint(242, 90)),
                        Line("real-short-wall", new PlanPoint(80, 160), new PlanPoint(102, 160), "A-WALL")
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
            Layer("A-WALL", LayerCategory.Wall)
        });
        foreach (var x in new[] { 80, 102, 150, 172, 220, 242 })
        {
            context.WallCandidates.Add(HostWall($"host-{x}", new PlanPoint(x, 50), new PlanPoint(x, 180)));
        }

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls.SelectMany(wall => wall.SourcePrimitiveIds),
            source => source.StartsWith("tick-detail-", StringComparison.Ordinal));

        var recoveredWall = Assert.Single(context.Walls.Where(wall => wall.SourcePrimitiveIds.Contains("real-short-wall")));
        Assert.Equal(WallDetectionKind.SingleLine, recoveredWall.DetectionKind);

        var diagnostic = Assert.Single(context.Diagnostics.Build().Messages, item => item.Code == "wall_evidence.short_repeated_slots_suppressed");
        Assert.Equal("3", diagnostic.Properties["suppressedSourceCount"]);
        Assert.Contains("tick-detail-a", diagnostic.SourcePrimitiveIds);
        Assert.Contains("tick-detail-c", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallEvidenceRefinement_PromotesShortPairedWallWithCollinearShellContinuity()
    {
        var document = new PlanDocument(
            "wall-evidence-short-pair-continuity-support",
            new[]
            {
                new PlanPage(1, new PlanSize(340, 220), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 6,
                MinWallLength = 36
            });
        var shortShellChunk = PairedWall(
            "short-shell-chunk",
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(126, 100)),
            faceSeparation: 6,
            pairScore: 0.66,
            WallType.Exterior);
        var shellContinuation = PairedWall(
            "shell-continuation",
            new PlanLineSegment(new PlanPoint(132, 100), new PlanPoint(260, 100)),
            faceSeparation: 6,
            pairScore: 0.91,
            WallType.Exterior);
        context.WallCandidates.AddRange(new[] { shortShellChunk, shellContinuation });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == shortShellChunk.Id);

        Assert.Equal(WallEvidenceCategory.StrongWallBody, assessment.Category);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("continuity-supported short paired wall body", StringComparison.OrdinalIgnoreCase));

        var diagnostic = Assert.Single(
            context.Diagnostics.Build().Messages,
            item => item.Code == "wall_evidence.continuity_supported_pairs_promoted");
        Assert.Equal("1", diagnostic.Properties["promotedWallCount"]);
        Assert.Equal(shortShellChunk.Id, diagnostic.Properties["wallIds"]);
    }

    [Fact]
    public async Task WallEvidenceRefinement_RejectsThinOutdoorCoveredAreaBoundaryBeforeStrongWallAcceptance()
    {
        var falseBoundaryFirstFace = new PlanLineSegment(new PlanPoint(100, 145), new PlanPoint(220, 145));
        var falseBoundarySecondFace = new PlanLineSegment(new PlanPoint(100, 151), new PlanPoint(220, 151));
        var falseSideBoundaryFirstFace = new PlanLineSegment(new PlanPoint(232, 125), new PlanPoint(232, 172));
        var falseSideBoundarySecondFace = new PlanLineSegment(new PlanPoint(238, 125), new PlanPoint(238, 172));
        var realExteriorFirstFace = new PlanLineSegment(new PlanPoint(258, 120), new PlanPoint(258, 220));
        var realExteriorSecondFace = new PlanLineSegment(new PlanPoint(278, 120), new PlanPoint(278, 220));
        var falseBoundarySourceIds = Enumerable
            .Range(0, 14)
            .Select(index => $"covered-boundary-fragment-{index:00}")
            .ToArray();
        var falseSideBoundarySourceIds = Enumerable
            .Range(0, 9)
            .Select(index => $"covered-side-fragment-{index:00}")
            .ToArray();
        var document = new PlanDocument(
            "wall-evidence-covered-entry-boundary-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(360, 260),
                    new PlanPrimitive[]
                    {
                        Text("covered-entry-label", "Covered entry", new PlanRect(145, 133, 70, 10)),
                        Line("covered-boundary-fragment-00", falseBoundaryFirstFace.Start, falseBoundaryFirstFace.End),
                        Line("covered-boundary-fragment-01", falseBoundarySecondFace.Start, falseBoundarySecondFace.End),
                        Line("covered-side-fragment-00", falseSideBoundaryFirstFace.Start, falseSideBoundaryFirstFace.End),
                        Line("covered-side-fragment-01", falseSideBoundarySecondFace.Start, falseSideBoundarySecondFace.End),
                        Line("real-exterior-face-a", realExteriorFirstFace.Start, realExteriorFirstFace.End),
                        Line("real-exterior-face-b", realExteriorSecondFace.Start, realExteriorSecondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                EnableWallEvidenceNoiseRejection = true
            });

        context.WallCandidates.Add(new WallSegment(
            "wall-covered-entry-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 148), new PlanPoint(220, 148)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = falseBoundarySourceIds,
            PairEvidence = new WallPairEvidence(
                falseBoundaryFirstFace,
                falseBoundarySecondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.93,
                FirstFaceFragmentCount: 7,
                SecondFaceFragmentCount: 7,
                FirstFaceSourcePrimitiveIds: falseBoundarySourceIds.Take(7).ToArray(),
                SecondFaceSourcePrimitiveIds: falseBoundarySourceIds.Skip(7).ToArray()),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-covered-entry-side-boundary",
            1,
            new PlanLineSegment(new PlanPoint(235, 125), new PlanPoint(235, 172)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = falseSideBoundarySourceIds,
            PairEvidence = new WallPairEvidence(
                falseSideBoundaryFirstFace,
                falseSideBoundarySecondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.84,
                FirstFaceFragmentCount: 4,
                SecondFaceFragmentCount: 3,
                FirstFaceSourcePrimitiveIds: falseSideBoundarySourceIds.Take(4).ToArray(),
                SecondFaceSourcePrimitiveIds: falseSideBoundarySourceIds.Skip(4).ToArray()),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-real-exterior",
            1,
            new PlanLineSegment(new PlanPoint(268, 120), new PlanPoint(268, 220)),
            20,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "real-exterior-face-a", "real-exterior-face-b" },
            PairEvidence = new WallPairEvidence(
                realExteriorFirstFace,
                realExteriorSecondFace,
                FaceSeparation: 20,
                OverlapRatio: 1,
                Score: 0.94,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "real-exterior-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "real-exterior-face-b" }),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(context.Walls, wall => wall.Id == "wall-covered-entry-boundary");
        Assert.DoesNotContain(context.Walls, wall => wall.Id == "wall-covered-entry-side-boundary");
        Assert.Contains(context.Walls, wall => wall.Id == "wall-real-exterior");

        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-covered-entry-boundary");
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, rejected.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);
        Assert.True(rejected.RejectedAsNoise);
        Assert.Contains(
            rejected.Evidence,
            item => item.Contains("outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase));
        var rejectedSide = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-covered-entry-side-boundary");
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, rejectedSide.Category);
        Assert.Equal(WallEvidenceDecision.Reject, rejectedSide.Decision);
        Assert.True(rejectedSide.RejectedAsNoise);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DowngradesCleanThinOutdoorBoundaryToReviewBeforeStrongWallAcceptance()
    {
        var falseBoundaryFirstFace = new PlanLineSegment(new PlanPoint(100, 145), new PlanPoint(220, 145));
        var falseBoundarySecondFace = new PlanLineSegment(new PlanPoint(100, 151), new PlanPoint(220, 151));
        var realExteriorFirstFace = new PlanLineSegment(new PlanPoint(258, 120), new PlanPoint(258, 220));
        var realExteriorSecondFace = new PlanLineSegment(new PlanPoint(278, 120), new PlanPoint(278, 220));
        var document = new PlanDocument(
            "wall-evidence-covered-entry-review-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(360, 260),
                    new PlanPrimitive[]
                    {
                        Text("covered-entry-label", "Overbygd inngang", new PlanRect(145, 133, 70, 10)),
                        Line("covered-boundary-face-a", falseBoundaryFirstFace.Start, falseBoundaryFirstFace.End),
                        Line("covered-boundary-face-b", falseBoundarySecondFace.Start, falseBoundarySecondFace.End),
                        Line("real-exterior-face-a", realExteriorFirstFace.Start, realExteriorFirstFace.End),
                        Line("real-exterior-face-b", realExteriorSecondFace.Start, realExteriorSecondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                EnableWallEvidenceNoiseRejection = true
            });

        context.WallCandidates.Add(new WallSegment(
            "wall-covered-entry-clean-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 148), new PlanPoint(220, 148)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "covered-boundary-face-a", "covered-boundary-face-b" },
            PairEvidence = new WallPairEvidence(
                falseBoundaryFirstFace,
                falseBoundarySecondFace,
                FaceSeparation: 6,
                OverlapRatio: 1,
                Score: 0.94,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "covered-boundary-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "covered-boundary-face-b" }),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-real-exterior",
            1,
            new PlanLineSegment(new PlanPoint(268, 120), new PlanPoint(268, 220)),
            20,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "real-exterior-face-a", "real-exterior-face-b" },
            PairEvidence = new WallPairEvidence(
                realExteriorFirstFace,
                realExteriorSecondFace,
                FaceSeparation: 20,
                OverlapRatio: 1,
                Score: 0.94,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "real-exterior-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "real-exterior-face-b" }),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.Id == "wall-covered-entry-clean-boundary");
        Assert.Contains(context.Walls, wall => wall.Id == "wall-real-exterior");

        var review = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-covered-entry-clean-boundary");
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, review.Category);
        Assert.Equal(WallEvidenceDecision.Review, review.Decision);
        Assert.False(review.PlacementReady);
        Assert.True(review.RequiresReview);
        Assert.False(review.RejectedAsNoise);
        Assert.Contains(
            review.Evidence,
            item => item.Contains("outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
                && item.Contains("review-only", StringComparison.OrdinalIgnoreCase));

        var accepted = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-real-exterior");
        Assert.Equal(WallEvidenceCategory.StrongWallBody, accepted.Category);
        Assert.Equal(WallEvidenceDecision.Accept, accepted.Decision);
        Assert.True(accepted.PlacementReady);
    }

    [Fact]
    public async Task WallEvidenceRefinement_DowngradesSingleLineOutdoorBoundaryToReview()
    {
        var falseBoundary = new PlanLineSegment(new PlanPoint(100, 148), new PlanPoint(220, 148));
        var realExteriorFirstFace = new PlanLineSegment(new PlanPoint(258, 120), new PlanPoint(258, 220));
        var realExteriorSecondFace = new PlanLineSegment(new PlanPoint(278, 120), new PlanPoint(278, 220));
        var document = new PlanDocument(
            "wall-evidence-single-line-covered-entry-review-filter",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(360, 260),
                    new PlanPrimitive[]
                    {
                        Text("covered-entry-label", "Overbygd inngang", new PlanRect(145, 133, 70, 10)),
                        Line("covered-boundary-line", falseBoundary.Start, falseBoundary.End),
                        Line("real-exterior-face-a", realExteriorFirstFace.Start, realExteriorFirstFace.End),
                        Line("real-exterior-face-b", realExteriorSecondFace.Start, realExteriorSecondFace.End)
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                DefaultWallThickness = 4,
                EnableWallEvidenceNoiseRejection = true
            });

        context.WallCandidates.Add(new WallSegment(
            "wall-covered-entry-single-line-boundary",
            1,
            falseBoundary,
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "covered-boundary-line" },
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });
        context.WallCandidates.Add(new WallSegment(
            "wall-real-exterior",
            1,
            new PlanLineSegment(new PlanPoint(268, 120), new PlanPoint(268, 220)),
            20,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "real-exterior-face-a", "real-exterior-face-b" },
            PairEvidence = new WallPairEvidence(
                realExteriorFirstFace,
                realExteriorSecondFace,
                FaceSeparation: 20,
                OverlapRatio: 1,
                Score: 0.94,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "real-exterior-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "real-exterior-face-b" }),
            Evidence = new[] { "wall type exterior: near detected floorplan/wall envelope or local outer boundary" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.Walls, wall => wall.Id == "wall-covered-entry-single-line-boundary");
        Assert.Contains(context.Walls, wall => wall.Id == "wall-real-exterior");

        var review = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-covered-entry-single-line-boundary");
        Assert.Equal(WallEvidenceCategory.SurfacePatternDetail, review.Category);
        Assert.Equal(WallEvidenceDecision.Review, review.Decision);
        Assert.False(review.PlacementReady);
        Assert.True(review.RequiresReview);
        Assert.False(review.RejectedAsNoise);
        Assert.Contains(
            review.Evidence,
            item => item.Contains("unpaired outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase)
                && item.Contains("review-only", StringComparison.OrdinalIgnoreCase));

        var accepted = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == "wall-real-exterior");
        Assert.Equal(WallEvidenceCategory.StrongWallBody, accepted.Category);
        Assert.True(accepted.PlacementReady);
    }

    private static WallSegment HostWall(string sourceId, PlanPoint start, PlanPoint end) =>
        new($"wall-{sourceId}", 1, new PlanLineSegment(start, end), 4, Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            SourcePrimitiveIds = new[] { sourceId },
            Evidence = new[] { "test host wall" }
        };

    private static WallSegment SingleLineWall(string sourceId, PlanPoint start, PlanPoint end) =>
        new($"wall-{sourceId}", 1, new PlanLineSegment(start, end), 4, Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            SourcePrimitiveIds = new[] { sourceId },
            Evidence = new[] { "test unlayered single-line wall candidate" }
        };

    private static WallSegment PairedWall(
        string sourceId,
        PlanLineSegment centerLine,
        double faceSeparation,
        double pairScore,
        WallType wallType)
    {
        var half = faceSeparation / 2.0;
        var firstFace = centerLine.IsHorizontal()
            ? new PlanLineSegment(
                new PlanPoint(centerLine.Start.X, centerLine.Start.Y - half),
                new PlanPoint(centerLine.End.X, centerLine.End.Y - half))
            : new PlanLineSegment(
                new PlanPoint(centerLine.Start.X - half, centerLine.Start.Y),
                new PlanPoint(centerLine.End.X - half, centerLine.End.Y));
        var secondFace = centerLine.IsHorizontal()
            ? new PlanLineSegment(
                new PlanPoint(centerLine.Start.X, centerLine.Start.Y + half),
                new PlanPoint(centerLine.End.X, centerLine.End.Y + half))
            : new PlanLineSegment(
                new PlanPoint(centerLine.Start.X + half, centerLine.Start.Y),
                new PlanPoint(centerLine.End.X + half, centerLine.End.Y));

        return new WallSegment($"wall-{sourceId}", 1, centerLine, faceSeparation, new Confidence(pairScore))
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = wallType,
            SourcePrimitiveIds = new[] { $"{sourceId}-face-a", $"{sourceId}-face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                faceSeparation,
                OverlapRatio: 1,
                Score: pairScore,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { $"{sourceId}-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { $"{sourceId}-face-b" }),
            Evidence = new[] { $"test paired {wallType} wall" }
        };
    }

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

    private static RectanglePrimitive Rectangle(string sourceId, PlanRect bounds, string? layer = null) =>
        new(bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                Layer = layer,
                EntityType = "RECTANGLE",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static TextPrimitive Text(string sourceId, string value, PlanRect bounds) =>
        new(value, bounds)
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

    private static ArcPrimitive DoorArc(
        string sourceId,
        PlanPoint center,
        double radius,
        double startAngleRadians,
        double sweepAngleRadians) =>
        new(center, radius, startAngleRadians, sweepAngleRadians)
        {
            SourceId = sourceId,
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "ARC",
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
