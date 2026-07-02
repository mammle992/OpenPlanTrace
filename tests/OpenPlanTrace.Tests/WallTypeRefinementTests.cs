namespace OpenPlanTrace.Tests;

public sealed class WallTypeRefinementTests
{
    [Fact]
    public async Task WallTypeRefinement_DoesNotFlipInteriorWallToExteriorFromOneSidedRoomEvidence()
    {
        var wall = new WallSegment(
            "wall-interior-one-sided-room",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope"
            }
        };
        var room = new RoomRegion(
            "room-on-one-side",
            1,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            },
            Array.Empty<string>(),
            Confidence.High);
        var context = new ScanContext(
            new PlanDocument(
                "one-sided-interior-wall",
                new[]
                {
                    new PlanPage(1, new PlanSize(400, 400), Array.Empty<PlanPrimitive>())
                }),
            new ScannerOptions());

        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = new WallGraph(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    wall.Bounds,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.High,
                    Array.Empty<string>())
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("one-sided room evidence did not override interior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteRecoveredUnknownWallToExteriorFromOneSidedRoomEvidence()
    {
        var wall = new WallSegment(
            "wall-recovered-one-sided-room",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "pair score 0.815",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence"
            }
        };
        var room = new RoomRegion(
            "room-on-one-side",
            1,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            },
            Array.Empty<string>(),
            Confidence.High);
        var context = CreateContext("one-sided-recovered-wall");
        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = GraphFor(wall);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("recovered missing-wall candidate", StringComparison.OrdinalIgnoreCase)
                && item.Contains("not trusted as exterior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteRecoveredUnknownWallToExteriorFromOneSidedOutdoorRoom()
    {
        var wall = new WallSegment(
            "wall-recovered-outdoor-side",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "pair score 0.815",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence"
            }
        };
        var room = new RoomRegion(
            "terrace-on-one-side",
            1,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            },
            Array.Empty<string>(),
            Confidence.High)
        {
            UseKind = RoomUseKind.Outdoor
        };
        var context = CreateContext("one-sided-recovered-outdoor-wall");
        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = GraphFor(wall);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("one-sided outdoor/terrace room evidence", StringComparison.OrdinalIgnoreCase)
                && item.Contains("not trusted as exterior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesTrustedRecoveredPerimeterShellToExterior()
    {
        var wall = RecoveredWallBody("wall-recovered-perimeter-shell", 52, 100, 52, 300);
        var context = CreateContext("recovered-perimeter-shell");
        context.SheetRegions.AddRange(new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 400, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(40, 40, 300, 300), Confidence.High)
        });
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.RecoveredWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("recovered wall body aligned to main floorplan perimeter shell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesTrustedLongIsolatedExteriorShellToExterior()
    {
        var wall = new WallSegment(
            "wall-trusted-isolated-exterior-shell",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(330, 100)),
            8,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(330, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(330, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.99,
                Score: 0.93,
                FirstFaceFragmentCount: 24,
                SecondFaceFragmentCount: 28,
                FirstFaceSourcePrimitiveIds: new[] { "trusted-isolated-exterior-a" },
                SecondFaceSourcePrimitiveIds: new[] { "trusted-isolated-exterior-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "filled wall-solid primitive",
                "wall evidence: filled closed vector wall body",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            }
        };
        var context = CreateContext("trusted-isolated-exterior-shell");
        context.Walls.Add(wall);
        context.WallGraph = IsolatedGraphFor(wall, excludedFromStructuralTopology: true);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("trusted long isolated exterior shell wall body", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("trusted long isolated exterior shell promoted", StringComparison.OrdinalIgnoreCase));

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("trusted long isolated exterior shell promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesLongDimensionLikeExteriorShell()
    {
        var wall = new WallSegment(
            "wall-long-dimension-like-exterior-shell",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(398, 100)),
            16.9,
            new Confidence(0.77))
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 92), new PlanPoint(398, 92)),
                new PlanLineSegment(new PlanPoint(100, 108.9), new PlanPoint(398, 108.9)),
                FaceSeparation: 16.9,
                OverlapRatio: 1.0,
                Score: 0.874,
                FirstFaceFragmentCount: 43,
                SecondFaceFragmentCount: 42,
                FirstFaceSourcePrimitiveIds: new[] { "dimension-like-exterior-a" },
                SecondFaceSourcePrimitiveIds: new[] { "dimension-like-exterior-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0.874",
                "overlap ratio 1",
                "first face merged 43 fragments",
                "second face merged 42 fragments",
                "layer (unlayered) classified Dimension (0.24)",
                "layer evidence: contains dimension-like text",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            }
        };
        var context = CreateContext("long-dimension-like-exterior-shell");
        context.Walls.Add(wall);
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("trusted long isolated exterior shell promoted", StringComparison.OrdinalIgnoreCase));

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsWeakIsolatedExteriorLikeDetailUnknown()
    {
        var wall = new WallSegment(
            "wall-weak-isolated-exterior-like-detail",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(150, 100)),
            8,
            Confidence.Medium)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(150, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(150, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.90,
                Score: 0.74,
                FirstFaceFragmentCount: 2,
                SecondFaceFragmentCount: 2,
                FirstFaceSourcePrimitiveIds: new[] { "weak-isolated-exterior-a" },
                SecondFaceSourcePrimitiveIds: new[] { "weak-isolated-exterior-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "surface/detail pattern"
            }
        };
        var context = CreateContext("weak-isolated-exterior-like-detail");
        context.Walls.Add(wall);
        context.WallGraph = IsolatedGraphFor(wall, excludedFromStructuralTopology: true);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.SurfacePatternDetail,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Unknown, refined.WallType);
        Assert.DoesNotContain(
            refined.Evidence,
            item => item.Contains("trusted long isolated exterior shell wall body", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteLongCoveredAreaBoundaryAsExteriorShell()
    {
        var wall = new WallSegment(
            "wall-long-covered-area-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(330, 100)),
            8,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(330, 96)),
                new PlanLineSegment(new PlanPoint(100, 104), new PlanPoint(330, 104)),
                FaceSeparation: 8,
                OverlapRatio: 0.99,
                Score: 0.93,
                FirstFaceFragmentCount: 24,
                SecondFaceFragmentCount: 28,
                FirstFaceSourcePrimitiveIds: new[] { "covered-area-boundary-a" },
                SecondFaceSourcePrimitiveIds: new[] { "covered-area-boundary-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "filled wall-solid primitive",
                "wall evidence: filled closed vector wall body",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall type refined unknown: outdoor covered-area boundary",
                "overbygd"
            }
        };
        var context = CreateContext("long-covered-area-boundary");
        context.Walls.Add(wall);
        context.WallGraph = IsolatedGraphFor(wall, excludedFromStructuralTopology: true);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Unknown, refined.WallType);
        Assert.DoesNotContain(
            refined.Evidence,
            item => item.Contains("trusted long isolated exterior shell promoted", StringComparison.OrdinalIgnoreCase));

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesTrustedRecoveredWallAwayFromPerimeterToInterior()
    {
        var wall = RecoveredWallBody("wall-recovered-away-from-perimeter", 180, 100, 180, 300);
        var context = CreateContext("recovered-away-from-perimeter");
        context.SheetRegions.AddRange(new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 400, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(40, 40, 300, 300), Confidence.High)
        });
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.RecoveredWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains(WallPlacementContextGuards.TrustedRecoveredMainStructuralInteriorEvidence, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            refined.Evidence,
            item => item.Contains("recovered wall body aligned to main floorplan perimeter shell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteDimensionLikeRecoveredWallAwayFromPerimeter()
    {
        var wall = RecoveredWallBody("wall-recovered-dimension-like", 180, 100, 180, 300);
        wall = wall with
        {
            Evidence = wall.Evidence
                .Concat(new[]
                {
                    "layer (unlayered) classified Dimension (0,24)",
                    "layer evidence: contains dimension-like text"
                })
                .ToArray()
        };
        var context = CreateContext("recovered-dimension-like");
        context.SheetRegions.AddRange(new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 400, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(40, 40, 300, 300), Confidence.High)
        });
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.RecoveredWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Unknown, refined.WallType);
        Assert.DoesNotContain(
            refined.Evidence,
            item => item.Contains(WallPlacementContextGuards.TrustedRecoveredMainStructuralInteriorEvidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsRecoveredUnknownWallExteriorWhenOutdoorSideHasShellSupport()
    {
        var wall = new WallSegment(
            "wall-recovered-outdoor-shell-side",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "pair score 0.815",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence",
                "wall evidence: exterior shell continuity support"
            }
        };
        var room = new RoomRegion(
            "terrace-on-one-side",
            1,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            },
            Array.Empty<string>(),
            Confidence.High)
        {
            UseKind = RoomUseKind.Outdoor
        };
        var context = CreateContext("one-sided-recovered-outdoor-shell-wall");
        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = GraphFor(wall);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("one side is outdoor/terrace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_OverridesExteriorGuessForSharedRoomAdjacency()
    {
        var wall = new WallSegment(
            "wall-shared-room-boundary-misclassified-exterior",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall-like layer exterior shell support"
            }
        };
        var context = CreateContext("shared-room-boundary-exterior-override");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-a:room-b",
                    1,
                    "room-a",
                    "Room A",
                    "room-b",
                    "Room B",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.East,
                    RoomAdjacencyDirection.West,
                    200,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency shares the wall" })
            },
            Array.Empty<RoomCluster>());

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("shared by room adjacency", StringComparison.OrdinalIgnoreCase)
                && item.Contains("overrides exterior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_OverridesExteriorGuessWhenRoomsAreDetectedOnBothSides()
    {
        var wall = new WallSegment(
            "wall-two-sided-room-boundary-misclassified-exterior",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall-like layer exterior shell support"
            }
        };
        var context = CreateContext("two-sided-room-boundary-exterior-override");
        context.Walls.Add(wall);
        context.Rooms.Add(new RoomRegion(
            "room-left",
            1,
            new PlanRect(0, 80, 94, 240),
            new[]
            {
                new PlanPoint(0, 80),
                new PlanPoint(94, 80),
                new PlanPoint(94, 320),
                new PlanPoint(0, 320)
            },
            Array.Empty<string>(),
            Confidence.High));
        context.Rooms.Add(new RoomRegion(
            "room-right",
            1,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            },
            Array.Empty<string>(),
            Confidence.High));
        context.WallGraph = GraphFor(wall);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("room evidence on both sides", StringComparison.OrdinalIgnoreCase)
                && item.Contains("overrides exterior", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PreservesExteriorWhenSharedAdjacencyIncludesOutdoorRoom()
    {
        var wall = new WallSegment(
            "wall-indoor-outdoor-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall-like layer exterior shell support"
            }
        };
        var context = CreateContext("shared-outdoor-boundary-preserves-exterior");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-indoor", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-terrace", RoomUseKind.Outdoor, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-indoor:room-terrace",
                    1,
                    "room-indoor",
                    "Office",
                    "room-terrace",
                    "Terrace",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.East,
                    RoomAdjacencyDirection.West,
                    200,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency includes outdoor room" })
            },
            Array.Empty<RoomCluster>());

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("includes outdoor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteSharedCoveredEntryBoundaryWithoutShellSupport()
    {
        var wall = new WallSegment(
            "wall-covered-entry-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "layer evidence: no strong layer name or geometry evidence",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var context = CreateContext("shared-covered-entry-boundary-review");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-indoor", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("covered-entry", RoomUseKind.Outdoor, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-indoor:covered-entry",
                    1,
                    "room-indoor",
                    "Office",
                    "covered-entry",
                    "Overbygd inngang",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.East,
                    RoomAdjacencyDirection.West,
                    200,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency includes covered outdoor room" })
            },
            Array.Empty<RoomCluster>());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Unknown, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("shared outdoor/terrace room evidence", StringComparison.OrdinalIgnoreCase)
                && item.Contains("outdoor covered-area boundary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsTwoSidedOutdoorRoomBoundaryExterior()
    {
        var wall = new WallSegment(
            "wall-two-sided-outdoor-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall-like layer exterior shell support"
            }
        };
        var context = CreateContext("two-sided-outdoor-boundary-preserves-exterior");
        context.Walls.Add(wall);
        context.Rooms.Add(Room(
            "room-indoor",
            RoomUseKind.Office,
            new PlanRect(0, 80, 94, 240),
            new[]
            {
                new PlanPoint(0, 80),
                new PlanPoint(94, 80),
                new PlanPoint(94, 320),
                new PlanPoint(0, 320)
            }));
        context.Rooms.Add(Room(
            "room-terrace",
            RoomUseKind.Outdoor,
            new PlanRect(106, 80, 120, 240),
            new[]
            {
                new PlanPoint(106, 80),
                new PlanPoint(226, 80),
                new PlanPoint(226, 320),
                new PlanPoint(106, 320)
            }));
        context.WallGraph = GraphFor(wall);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refined = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refined.WallType);
        Assert.Contains(
            refined.Evidence,
            item => item.Contains("outdoor/terrace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesRoomConfirmedMediumWallEvidence()
    {
        var wall = new WallSegment(
            "wall-room-confirmed-medium",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            WallType = WallType.Interior,
            Evidence = new[] { "wall type interior: supported wall evidence inside exterior envelope" }
        };
        var context = CreateContext("room-confirmed-medium-wall-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-b", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-a:room-b",
                    1,
                    "room-a",
                    "Room A",
                    "room-b",
                    "Room B",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.East,
                    RoomAdjacencyDirection.West,
                    200,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency shares the wall" })
            },
            Array.Empty<RoomCluster>());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[] { "wall evidence assessment: MediumWallBody / review / confidence 0.88", "parallel wall-face pair" });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomConfirmedPlacementPromotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesRoomBoundaryAlignedMediumWallWithoutExplicitRoomWallIds()
    {
        var wall = new WallSegment(
            "wall-geometric-room-boundary-medium",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0.86"
            }
        };
        var leftRoom = Room(
            "room-left",
            RoomUseKind.Office,
            new PlanRect(20, 80, 80, 240),
            new[]
            {
                new PlanPoint(20, 80),
                new PlanPoint(100, 80),
                new PlanPoint(100, 320),
                new PlanPoint(20, 320)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-left" }
            };
        var rightRoom = Room(
            "room-right",
            RoomUseKind.Office,
            new PlanRect(100, 80, 120, 240),
            new[]
            {
                new PlanPoint(100, 80),
                new PlanPoint(220, 80),
                new PlanPoint(220, 320),
                new PlanPoint(100, 320)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-right" }
            };
        var context = CreateContext("geometric-room-boundary-wall-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(leftRoom);
        context.Rooms.Add(rightRoom);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.88",
                "parallel wall-face pair",
                "pair score 0.86"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("room references 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["geometricRoomBoundaryReferencedWallCount"] == "1"
                && diagnostic.Properties["geometricRoomBoundaryReferenceCount"] == "2"
                && diagnostic.Properties["geometricRoomBoundaryEvidenceAddedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_AddsGeometricRoomBoundaryEvidenceToPlacementReadyShortDenseWall()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = new WallSegment(
            "wall-short-dense-geometric-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = sourceIds,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var room = Room(
            "room-aligned",
            RoomUseKind.Office,
            new PlanRect(90, 100, 80, 90),
            new[]
            {
                new PlanPoint(90, 100),
                new PlanPoint(170, 100),
                new PlanPoint(170, 190),
                new PlanPoint(90, 190)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-short-dense" }
            };
        var context = CreateContext("short-dense-geometric-room-boundary-evidence");
        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var updatedWall = Assert.Single(context.Walls);
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);

        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Contains(
            updatedWall.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["geometricRoomBoundaryEvidenceAddedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_AddsExplicitRoomBoundaryEvidenceToPlacementReadyShortDenseWall()
    {
        var sourceIds = Enumerable.Range(1, 34)
            .Select(index => $"pdf:p1:path:{index}:line:1")
            .ToArray();
        var wall = new WallSegment(
            "wall-short-dense-explicit-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = sourceIds,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "first face merged 29 fragments",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var context = CreateContext("short-dense-explicit-room-boundary-evidence");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-explicit", RoomUseKind.Storage, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var updatedWall = Assert.Single(context.Walls);
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);

        Assert.Contains(
            updatedWall.Evidence,
            item => item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["explicitRoomBoundaryEvidenceAddedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotAddExplicitRoomBoundaryEvidenceForOutdoorRoom()
    {
        var wall = new WallSegment(
            "wall-outdoor-explicit-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(140, 100)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var context = CreateContext("outdoor-explicit-boundary-no-evidence");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("covered-entry", RoomUseKind.Outdoor, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var updatedWall = Assert.Single(context.Walls);
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);

        Assert.DoesNotContain(
            updatedWall.Evidence,
            item => item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            assessment.Evidence,
            item => item.Contains("explicit room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["explicitRoomBoundaryEvidenceAddedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotUseOutdoorBoundaryGeometryToPromoteWallEvidence()
    {
        var wall = new WallSegment(
            "wall-outdoor-boundary-medium",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence = new[]
            {
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "parallel wall-face pair",
                "pair score 0.86"
            }
        };
        var outdoor = Room(
            "covered-entry",
            RoomUseKind.Outdoor,
            new PlanRect(100, 80, 120, 240),
            new[]
            {
                new PlanPoint(100, 80),
                new PlanPoint(220, 80),
                new PlanPoint(220, 320),
                new PlanPoint(100, 320)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls outdoor-covered-entry" }
            };
        var context = CreateContext("outdoor-geometric-boundary-review-only");
        context.Walls.Add(wall);
        context.Rooms.Add(outdoor);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.88",
                "parallel wall-face pair",
                "pair score 0.86"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteRecoveredDuplicateWallBody()
    {
        var wall = new WallSegment(
            "wall-room-confirmed-recovered-duplicate",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            WallType = WallType.Interior,
            Evidence = new[] { "recovered by wall evidence map from unclaimed parallel wall-face evidence" }
        };
        var context = CreateContext("room-confirmed-recovered-duplicate-stays-review");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-b", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-a:room-b",
                    1,
                    "room-a",
                    "Room A",
                    "room-b",
                    "Room B",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.East,
                    RoomAdjacencyDirection.West,
                    200,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency shares the wall" })
            },
            Array.Empty<RoomCluster>());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall evidence: recovered duplicate wall body already represented by stronger nearby paired wall body wall-stronger; keep for review but block exact placement",
                "parallel wall-face pair"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("recovered duplicate wall body", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesShortStructuralReturnWithRoomAndSupportedEndpoints()
    {
        var wall = new WallSegment(
            "wall-short-room-return",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 145)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0.852"
            }
        };
        var context = CreateContext("room-confirmed-short-return-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.WallGraph = SupportedEndpointGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.84",
                "parallel wall-face pair",
                "pair score 0.852",
                "wall evidence: short unlayered parallel-face candidate has only one structurally supported endpoint and short paired wall evidence; keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("short structural return promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("topology-supported endpoints 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesRoomConfirmedIsolatedInteriorBoundary()
    {
        var wall = new WallSegment(
            "wall-isolated-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 205)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0.842"
            }
        };
        var context = CreateContext("isolated-room-boundary-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-b", RoomUseKind.Office, wall.Id));
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.84",
                "parallel wall-face pair",
                "pair score 0.842",
                "wall evidence: short unlayered parallel-face candidate has no structural endpoint support and short paired wall evidence; keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains(WallPlacementReadinessEvaluator.RoomConfirmedIsolatedFragmentPromotionEvidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteShortIsolatedRoomBoundaryDetail()
    {
        var wall = new WallSegment(
            "wall-short-isolated-room-detail",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 135)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0.842"
            }
        };
        var context = CreateContext("short-isolated-room-boundary-stays-review");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-b", RoomUseKind.Office, wall.Id));
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.84",
                "parallel wall-face pair",
                "pair score 0.842"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains(WallPlacementReadinessEvaluator.RoomConfirmedIsolatedFragmentPromotionEvidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesRoomConfirmedIsolatedExteriorShellSegment()
    {
        var wall = new WallSegment(
            "wall-isolated-exterior-room-confirmed-shell",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(164, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = Enumerable.Range(1, 62)
                .Select(index => $"exterior-shell-fragment-{index}")
                .ToArray(),
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96.5), new PlanPoint(164, 96.5)),
                new PlanLineSegment(new PlanPoint(100, 103.5), new PlanPoint(164, 103.5)),
                FaceSeparation: 7,
                OverlapRatio: 1,
                Score: 0.721,
                FirstFaceFragmentCount: 60,
                SecondFaceFragmentCount: 2,
                FirstFaceSourcePrimitiveIds: ["exterior-shell-face-a"],
                SecondFaceSourcePrimitiveIds: ["exterior-shell-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,721",
                "first face merged 60 fragments",
                "second face merged 2 fragments",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall evidence: medium double-edge exterior wall body"
            ]
        };
        var context = CreateContext("room-confirmed-isolated-exterior-shell");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.Rooms.Add(Room("room-b", RoomUseKind.Bathroom, wall.Id));
        context.Rooms.Add(Room("room-c", RoomUseKind.Kitchen, wall.Id));
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains(WallPlacementReadinessEvaluator.RoomConfirmedIsolatedExteriorPromotionEvidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesGeometricIsolatedExteriorShellSegmentBelowNormalThreshold()
    {
        var wall = new WallSegment(
            "wall-geometric-isolated-exterior-shell",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(147.4, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = Enumerable.Range(1, 121)
                .Select(index => $"geometric-exterior-shell-fragment-{index}")
                .ToArray(),
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 89), new PlanPoint(147.4, 89)),
                new PlanLineSegment(new PlanPoint(100, 111), new PlanPoint(147.4, 111)),
                FaceSeparation: 22,
                OverlapRatio: 1,
                Score: 0.663,
                FirstFaceFragmentCount: 16,
                SecondFaceFragmentCount: 105,
                FirstFaceSourcePrimitiveIds: ["geometric-exterior-shell-face-a"],
                SecondFaceSourcePrimitiveIds: ["geometric-exterior-shell-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,663",
                "overlap ratio 1",
                "first face merged 16 fragments",
                "second face merged 105 fragments",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall evidence: medium double-edge exterior wall body",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
            ]
        };
        var context = CreateContext("geometric-isolated-exterior-shell");
        context.Walls.Add(wall);
        context.Rooms.Add(Room(
            "room-geometric-exterior",
            RoomUseKind.Office,
            new PlanRect(100, 100, 48, 42),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(148, 100),
                new PlanPoint(148, 142),
                new PlanPoint(100, 142)
            },
            new[] { wall.Id, "synthetic-room-edge" }));
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains(WallPlacementReadinessEvaluator.RoomConfirmedIsolatedExteriorPromotionEvidence, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteCoveredOutdoorIsolatedExteriorShellSegment()
    {
        var wall = new WallSegment(
            "wall-covered-outdoor-isolated-exterior-shell",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(164, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96.5), new PlanPoint(164, 96.5)),
                new PlanLineSegment(new PlanPoint(100, 103.5), new PlanPoint(164, 103.5)),
                FaceSeparation: 7,
                OverlapRatio: 1,
                Score: 0.82,
                FirstFaceFragmentCount: 8,
                SecondFaceFragmentCount: 4,
                FirstFaceSourcePrimitiveIds: ["covered-face-a"],
                SecondFaceSourcePrimitiveIds: ["covered-face-b"]),
            Evidence =
            [
                "parallel wall-face pair",
                "wall type exterior: outdoor covered-area boundary",
                "wall type exterior: overbygd inngang / covered entry annotation nearby",
                "wall evidence: medium double-edge exterior wall body"
            ]
        };
        var context = CreateContext("covered-outdoor-isolated-exterior-shell");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-outdoor-a", RoomUseKind.Outdoor, wall.Id));
        context.Rooms.Add(Room("room-outdoor-b", RoomUseKind.Outdoor, wall.Id));
        context.Rooms.Add(Room("room-outdoor-c", RoomUseKind.Outdoor, wall.Id));
        context.WallGraph = IsolatedGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains(WallPlacementReadinessEvaluator.RoomConfirmedIsolatedExteriorPromotionEvidence, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesProtectedObjectLikeLongCleanFragmentInterior()
    {
        var wall = new WallSegment(
            "wall-protected-object-like-long-fragment",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 240)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b", "fragment-c", "fragment-d" },
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 4,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 0,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "run merged 4 fragments",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint; keep for topology but block exact placement until graph context is reviewed",
                $"wall evidence: {WallPlacementContextGuards.TrustedObjectLikeLongCleanFragmentInteriorEvidence}"
            }
        };
        var context = CreateContext("protected-object-like-long-fragment-promotion");
        context.Walls.Add(wall);
        context.WallGraph = new WallGraph(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-object-like-long-fragment",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    wall.Bounds,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.Medium,
                    new[] { "wall graph component is compact object-like linework" },
                    ExcludedFromStructuralTopology: true)
            });
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refinedWall = Assert.Single(context.Walls);
        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallType.Interior, refinedWall.WallType);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains(WallPlacementContextGuards.TrustedObjectLikeLongCleanFragmentInteriorEvidence, StringComparison.OrdinalIgnoreCase)
                && item.Contains("promoted to placement-ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesCleanFragmentMergedInteriorRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-fragment-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 205)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b", "fragment-c", "fragment-d" },
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 4,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 0,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "fragment geometry: 4 fragment(s)",
                "fragment geometry healed gap ratio 0",
                "wall type interior: supported wall evidence inside exterior envelope"
            }
        };
        var context = CreateContext("clean-fragment-room-boundary-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Bathroom, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "merged collinear wall fragments",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (4 fragments, gap ratio 0); keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("clean fragment-merged interior room boundary promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesDuplicatedCleanFragmentMergedRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-duplicated-fragment-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 205)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = new[] { "fragment-a", "fragment-b", "fragment-c" },
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 3,
                TotalHealedGap: 0,
                MaxHealedGap: 0,
                DuplicatePrimitiveCount: 6,
                GapRatio: 0,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "fragment geometry: 3 fragment(s)",
                "fragment geometry collapsed 6 duplicate primitive(s)",
                "fragment geometry healed gap ratio 0",
                "wall type interior: supported wall evidence inside exterior envelope"
            }
        };
        var room = Room(
            "room-geometric-boundary",
            RoomUseKind.Storage,
            new PlanRect(100, 80, 120, 160),
            new[]
            {
                new PlanPoint(100, 80),
                new PlanPoint(220, 80),
                new PlanPoint(220, 240),
                new PlanPoint(100, 240)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-storage" }
            };
        var context = CreateContext("duplicated-clean-fragment-room-boundary-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(room);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "merged collinear wall fragments",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (3 fragments, gap ratio 0); keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("clean fragment-merged interior room boundary promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesLongGeometricFragmentMergedRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-long-geometric-fragment-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(270, 100)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(1, 55).Select(index => $"fragment-{index}").ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 55,
                TotalHealedGap: 2.069,
                MaxHealedGap: 2.069,
                DuplicatePrimitiveCount: 2,
                GapRatio: 0.012,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "fragment geometry: 55 fragment(s)",
                "fragment geometry healed gap ratio 0.012",
                "fragment geometry healed 2.069 drawing units; max gap 2.069",
                "layer (unlayered) classified Dimension (0.24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: shared by room adjacency boundary",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
            }
        };
        var context = CreateContext("long-geometric-fragment-room-boundary-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room(
            "room-above",
            RoomUseKind.Office,
            new PlanRect(100, 40, 170, 60),
            new[]
            {
                new PlanPoint(100, 40),
                new PlanPoint(270, 40),
                new PlanPoint(270, 100),
                new PlanPoint(100, 100)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-above" }
            });
        context.Rooms.Add(Room(
            "room-below",
            RoomUseKind.Office,
            new PlanRect(100, 100, 170, 70),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(270, 100),
                new PlanPoint(270, 170),
                new PlanPoint(100, 170)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-below" }
            });
        context.WallGraph = GraphFor(wall);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(
            new[]
            {
                new RoomAdjacencyEdge(
                    "adjacency:room-above:room-below",
                    1,
                    "room-above",
                    "Room Above",
                    "room-below",
                    "Room Below",
                    RoomAdjacencyKind.BoundaryAdjacent,
                    RoomAdjacencyDirection.South,
                    RoomAdjacencyDirection.North,
                    wall.DrawingLength,
                    wall.CenterLine,
                    Confidence.High,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    new[] { "test adjacency shares the long fragment-merged wall" })
            },
            Array.Empty<RoomCluster>());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "merged collinear wall fragments",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("clean fragment-merged interior room boundary promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesShortGeometricTwoSidedFragmentMergedRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-short-geometric-fragment-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(162, 100)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(1, 10).Select(index => $"fragment-{index}").ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 5,
                TotalHealedGap: 0.062,
                MaxHealedGap: 0.062,
                DuplicatePrimitiveCount: 5,
                GapRatio: 0.001,
                RequiresGeometryReview: false,
                Evidence: Array.Empty<string>()),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "run merged 5 fragments",
                "fragment geometry: 5 fragment(s)",
                "fragment geometry healed gap ratio 0.001",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: detected room evidence on both sides",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (5 fragments, gap ratio 0.001); keep for topology but block exact placement until reviewed",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment"
            }
        };
        var context = CreateContext("short-geometric-fragment-room-boundary-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room(
            "room-above",
            RoomUseKind.Office,
            new PlanRect(100, 40, 62, 60),
            new[]
            {
                new PlanPoint(100, 40),
                new PlanPoint(162, 40),
                new PlanPoint(162, 100),
                new PlanPoint(100, 100)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-above" }
            });
        context.Rooms.Add(Room(
            "room-below",
            RoomUseKind.Office,
            new PlanRect(100, 100, 62, 60),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(162, 100),
                new PlanPoint(162, 160),
                new PlanPoint(100, 160)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-below" }
            });
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "merged collinear wall fragments",
                "wall evidence: geometric room boundary support from reliable room-boundary alignment",
                "wall evidence: unlayered fragment-merged wall candidate has only one trusted structural endpoint (5 fragments, gap ratio 0.001); keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("clean fragment-merged interior room boundary promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteNoisyShortStructuralReturnWithLargeHealedGap()
    {
        var wall = new WallSegment(
            "wall-noisy-short-room-return",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 134)),
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0.852",
                "second face healed 5,783 drawing units of gaps; max gap 5,783"
            }
        };
        var context = CreateContext("noisy-short-return-stays-review");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.WallGraph = SupportedEndpointGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.84",
                "parallel wall-face pair",
                "pair score 0.852",
                "second face healed 5,783 drawing units of gaps; max gap 5,783",
                "wall evidence: short unlayered parallel-face candidate has only one structurally supported endpoint and short paired wall evidence; keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("max gap 5,783", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("short structural return promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesTopologySupportedFragmentedShortPair()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(148, 96));
        var secondFace = new PlanLineSegment(new PlanPoint(100, 101), new PlanPoint(148, 101));
        var wall = new WallSegment(
            "wall-topology-supported-fragmented-short-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 98.5), new PlanPoint(148, 98.5)),
            5,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 5,
                OverlapRatio: 1,
                Score: 0.718,
                FirstFaceFragmentCount: 7,
                SecondFaceFragmentCount: 78,
                FirstFaceSourcePrimitiveIds: new[] { "face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "face-b" }),
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0,718",
                "first face merged 7 fragments",
                "second face merged 78 fragments",
                "second face healed 0,68 drawing units of gaps; max gap 0,68",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var context = CreateContext("topology-supported-fragmented-pair-promotion");
        context.Walls.Add(wall);
        context.WallGraph = SupportedEndpointGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.88",
                "parallel wall-face pair",
                "pair score 0,718",
                "first face merged 7 fragments",
                "second face merged 78 fragments",
                "second face healed 0,68 drawing units of gaps; max gap 0,68",
                "wall evidence: short unlayered parallel-face candidate has noisy fragmented face evidence (score 0.718, max face fragments 78, total face fragments 85); keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("topology-supported fragmented paired wall promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            Assert.Single(context.Walls).Evidence,
            item => item.Contains("topology-supported endpoints 2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["topologySupportedFragmentedPairPromotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesGeometricRoomBoundaryFragmentedPairWithOneSupportedEndpoint()
    {
        var wall = new WallSegment(
            "wall-geometric-one-end-fragmented-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(168, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 96.5), new PlanPoint(168, 96.5)),
                new PlanLineSegment(new PlanPoint(100, 103.5), new PlanPoint(168, 103.5)),
                FaceSeparation: 7,
                OverlapRatio: 1,
                Score: 0.642,
                FirstFaceFragmentCount: 4,
                SecondFaceFragmentCount: 107,
                FirstFaceSourcePrimitiveIds: new[] { "face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "face-b" }),
            Evidence =
            [
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0,642",
                "first face merged 4 fragments",
                "second face merged 107 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "wall evidence: demoted from placement-ready because unlayered parallel-face pair has severe fragmented-face evidence; pair score 0.642, max face fragments 107, total face fragments 111, room refs 1, side room hits 3, supported endpoints 1"
            ]
        };
        var context = CreateContext("geometric-one-end-fragmented-pair-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(Room(
            "room-geometric-edge",
            RoomUseKind.Office,
            new PlanRect(92, 100, 90, 60),
            new[]
            {
                new PlanPoint(92, 100),
                new PlanPoint(182, 100),
                new PlanPoint(182, 160),
                new PlanPoint(92, 160)
            }) with
            {
                Evidence = new[] { "semantic room boundary inferred from nearby walls synthetic-geometric-edge" }
            });
        context.WallGraph = OneSupportedEndpointGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("geometric room-boundary paired wall promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            Assert.Single(context.Walls).Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomConfirmedPlacementPromotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteWeakOneEndedFragmentedShortPair()
    {
        var firstFace = new PlanLineSegment(new PlanPoint(100, 96), new PlanPoint(148, 96));
        var secondFace = new PlanLineSegment(new PlanPoint(100, 103), new PlanPoint(148, 103));
        var wall = new WallSegment(
            "wall-weak-one-ended-fragmented-short-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 99.5), new PlanPoint(148, 99.5)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 7,
                OverlapRatio: 1,
                Score: 0.602,
                FirstFaceFragmentCount: 5,
                SecondFaceFragmentCount: 4,
                FirstFaceSourcePrimitiveIds: new[] { "face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "face-b" }),
            Evidence = new[]
            {
                "wall type interior: supported wall evidence inside exterior envelope",
                "parallel wall-face pair",
                "pair score 0,602",
                "first face merged 5 fragments",
                "second face merged 4 fragments",
                "layer (unlayered) classified Unknown (0,35)"
            }
        };
        var context = CreateContext("weak-one-ended-fragmented-pair-stays-review");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[]
            {
                "wall evidence assessment: MediumWallBody / review / confidence 0.88",
                "parallel wall-face pair",
                "pair score 0,602",
                "first face merged 5 fragments",
                "second face merged 4 fragments",
                "wall evidence: short unlayered parallel-face candidate has only one structurally supported endpoint and weak/fragmented pair evidence (score 0.602, 9 face fragments); keep for topology but block exact placement until reviewed"
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("topology-supported fragmented paired wall promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["topologySupportedFragmentedPairPromotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesPlacementReadyShortFragmentedUnlayeredPair()
    {
        var wall = new WallSegment(
            "wall-short-fragmented-placement-ready-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(167, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,642",
                "first face merged 4 fragments",
                "second face merged 107 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall evidence: strong double-edge wall body"
            }
        };
        var context = CreateContext("demote-short-fragmented-placement-ready-pair");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("demoted from placement-ready", StringComparison.OrdinalIgnoreCase)
                && item.Contains("max face fragments 107", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            Assert.Single(context.Walls).Evidence,
            item => item.Contains("severe fragmented-face evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["fragmentedPairPlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesShortPlacementReadyWallInsideDenseLocalDetailLinework()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dense-detail-survivor", 100, 100, 146, 100);
        var context = CreateContext("dense-local-detail-placement-ready-demotion");
        var neighbors = DenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.WallGraph = GraphFor(new[] { wall }.Concat(neighbors).ToArray());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase)
                && item.Contains("nearby walls", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsDenseLocalWallWithExplicitRoomBoundarySupport()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dense-but-room-boundary", 100, 100, 146, 100);
        var context = CreateContext("dense-local-detail-room-boundary-protection");
        var neighbors = DenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room("room-supported", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(new[] { wall }.Concat(neighbors).ToArray());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesDimensionLikeDenseLocalWallDespiteRoomBoundarySupport()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dimension-like-dense-room-boundary", 100, 100, 146, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,85",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("dimension-like-dense-local-detail-room-boundary-demotion");
        var neighbors = DenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room("room-supported", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(new[] { wall }.Concat(neighbors).ToArray());
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase)
                && item.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            demoted.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsDimensionLikeDenseLocalWallWithStructuralEndpointProof()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dimension-like-structural-endpoint-supported", 100, 100, 154.8, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "face separation 21,974 drawing units",
                "pair score 0,84",
                "overlap ratio 1",
                "first face merged 12 fragments",
                "second face merged 31 fragments",
                "first face collapsed 18 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 10 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("dimension-like-dense-local-structural-endpoint-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.WallGraph = SupportedEndpointGraphFor(wall, WallGraphComponentKind.MainStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsDimensionLikeDenseLocalWallWithStrongSideRoomAndEndpointEvidence()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dimension-like-side-room-supported", 100, 100, 146, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,9",
                "overlap ratio 1",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("dimension-like-dense-local-side-room-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 70, 70, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(160, 70),
                new PlanPoint(160, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 105, 70, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(160, 105),
                new PlanPoint(160, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = SupportedEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsFilledDimensionLikeDenseLocalWallWithSideRoomAndEndpointEvidence()
    {
        var wall = ShortUnlayeredInteriorWall("wall-filled-dimension-like-side-room-supported", 100, 100, 164, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "face separation 2,065 drawing units",
                "pair score 0,909",
                "overlap ratio 1",
                "first face merged 28 fragments",
                "second face merged 86 fragments",
                "first face collapsed 2 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "filled wall-solid primitive",
                "wall evidence: filled closed vector wall body",
                "filled wall-solid bounds 43,57 x 1,39 drawing units",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("filled-dimension-like-dense-local-side-room-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-filled-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 70, 86, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(176, 70),
                new PlanPoint(176, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-filled-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 105, 86, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(176, 105),
                new PlanPoint(176, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = SupportedFragmentEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesFragmentedDimensionLikeDenseSideRoomWallWithoutFilledBodyEvidence()
    {
        var wall = ShortUnlayeredInteriorWall("wall-fragmented-dimension-like-side-room-no-filled-body", 100, 100, 164, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "face separation 2,065 drawing units",
                "pair score 0,909",
                "overlap ratio 1",
                "first face merged 28 fragments",
                "second face merged 86 fragments",
                "first face collapsed 2 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("fragmented-dimension-like-dense-side-room-no-filled-body-demotion");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-fragmented-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 70, 86, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(176, 70),
                new PlanPoint(176, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-fragmented-side-supported",
            RoomUseKind.Office,
            new PlanRect(90, 105, 86, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(176, 105),
                new PlanPoint(176, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = SupportedFragmentEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase)
                && item.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesVeryShortDimensionLikeDenseSecondaryWallDespiteSideRoomEvidence()
    {
        var wall = ShortUnlayeredInteriorWall("wall-short-dimension-like-side-room-detail", 100, 100, 126, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,9",
                "overlap ratio 1",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("short-dimension-like-dense-local-side-room-demotion");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-short-detail",
            RoomUseKind.Office,
            new PlanRect(90, 70, 46, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(136, 70),
                new PlanPoint(136, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-short-detail",
            RoomUseKind.Office,
            new PlanRect(90, 105, 46, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(136, 105),
                new PlanPoint(136, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = SupportedEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase)
                && item.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsVeryShortSecondaryDenseWallWithStrongSideRoomAndEndpointEvidence()
    {
        var wall = ShortUnlayeredInteriorWall(
            "wall-very-short-secondary-side-room-supported",
            100,
            100,
            126.5,
            100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "face separation 14.486 drawing units",
                "pair score 0.903",
                "overlap ratio 1",
                "first face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 5 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("very-short-secondary-dense-side-room-endpoint-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-very-short-supported",
            RoomUseKind.Office,
            new PlanRect(90, 70, 48, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(138, 70),
                new PlanPoint(138, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-very-short-supported",
            RoomUseKind.Office,
            new PlanRect(90, 105, 48, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(138, 105),
                new PlanPoint(138, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = SupportedFragmentEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsMainStructuralDimensionLikeDenseLocalWallWithStrongSideRoomAndOneEndpointEvidence()
    {
        var wall = ShortUnlayeredInteriorWall("wall-main-dimension-like-side-room-one-endpoint", 100, 100, 156, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,96",
                "overlap ratio 1",
                "first face merged 4 fragments",
                "second face merged 5 fragments",
                "first face collapsed 4 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 4 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall type refined interior: detected room evidence on both sides",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("main-dimension-like-dense-local-side-room-one-endpoint-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-above-one-endpoint-supported",
            RoomUseKind.Office,
            new PlanRect(90, 70, 76, 25),
            new[]
            {
                new PlanPoint(90, 70),
                new PlanPoint(166, 70),
                new PlanPoint(166, 95),
                new PlanPoint(90, 95)
            }));
        context.Rooms.Add(Room(
            "room-below-one-endpoint-supported",
            RoomUseKind.Office,
            new PlanRect(90, 105, 76, 25),
            new[]
            {
                new PlanPoint(90, 105),
                new PlanPoint(166, 105),
                new PlanPoint(166, 130),
                new PlanPoint(90, 130)
            }));
        context.WallGraph = OneSupportedEndpointGraphFor(wall, WallGraphComponentKind.MainStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsDimensionLikeDenseLocalWallWithGeometricRoomBoundaryAndSupportedEndpoints()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dimension-like-geometric-room-boundary", 100, 100, 154.8, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,84",
                "overlap ratio 1",
                "first face merged 12 fragments",
                "second face merged 31 fragments",
                "first face collapsed 18 duplicate or near-duplicate wall line primitive(s)",
                "second face collapsed 10 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("dimension-like-geometric-room-boundary-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-geometric-supported",
            RoomUseKind.Office,
            new PlanRect(90, 100, 70, 42),
            new[]
            {
                new PlanPoint(90, 100),
                new PlanPoint(160, 100),
                new PlanPoint(160, 142),
                new PlanPoint(90, 142)
            },
            new[] { wall.Id, "synthetic-room-edge" }));
        context.WallGraph = SupportedEndpointGraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsFragmentMergedDimensionLikeDenseWallWithGeometricRoomBoundaryAndSupportedEndpoints()
    {
        var wall = FragmentMergedDimensionLikeInteriorWall("wall-fragment-dimension-like-geometric-room-boundary", 100, 100, 164, 100);
        var context = CreateContext("fragment-dimension-like-geometric-room-boundary-protection");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-fragment-geometric-supported",
            RoomUseKind.Office,
            new PlanRect(90, 100, 82, 42),
            new[]
            {
                new PlanPoint(90, 100),
                new PlanPoint(172, 100),
                new PlanPoint(172, 142),
                new PlanPoint(90, 142)
            },
            new[] { wall.Id, "synthetic-room-edge" }));
        context.WallGraph = SupportedFragmentEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("geometric room boundary support", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesFragmentMergedDimensionLikeDenseWallWithoutGeometricRoomBoundary()
    {
        var wall = FragmentMergedDimensionLikeInteriorWall("wall-fragment-dimension-like-no-room-boundary", 100, 100, 164, 100);
        var context = CreateContext("fragment-dimension-like-dense-no-room-boundary-demotion");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.WallGraph = SupportedFragmentEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase)
                && item.Contains("dimension-like weak layer True", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["denseLocalDetailPlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesVeryShortSecondaryDimensionLikeDenseWallDespiteGeometricRoomBoundary()
    {
        var wall = ShortUnlayeredInteriorWall("wall-short-secondary-dimension-like-geometric-room-boundary", 100, 100, 124, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,86",
                "overlap ratio 1",
                "first face merged 5 fragments",
                "second face merged 3 fragments",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: strong double-edge wall body"
            ]
        };
        var context = CreateContext("very-short-secondary-dimension-like-geometric-room-boundary-demotion");
        var neighbors = AxisDenseDetailNeighborWalls().ToArray();
        context.Walls.Add(wall);
        context.Walls.AddRange(neighbors);
        context.Rooms.Add(Room(
            "room-geometric-supported",
            RoomUseKind.Office,
            new PlanRect(90, 100, 70, 42),
            new[]
            {
                new PlanPoint(90, 100),
                new PlanPoint(160, 100),
                new PlanPoint(160, 142),
                new PlanPoint(90, 142)
            },
            new[] { wall.Id, "synthetic-room-edge" }));
        context.WallGraph = SupportedEndpointGraphFor(wall, WallGraphComponentKind.SecondaryStructural);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, demoted.Category);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("dense local detail/stair-like linework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesNonOrthogonalDimensionLikeSingleLinePlacementWall()
    {
        var wall = NonOrthogonalDimensionLikeInteriorWall("wall-diagonal-dimension-like", 100, 100, 160, 140);
        var context = CreateContext("non-orthogonal-dimension-like-placement-demotion");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("non-orthogonal single-line candidate has dimension-like weak layer evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["nonOrthogonalDimensionLikePlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsNonOrthogonalDimensionLikeWallWithExplicitRoomBoundarySupport()
    {
        var wall = NonOrthogonalDimensionLikeInteriorWall("wall-diagonal-room-boundary", 100, 100, 160, 140);
        var context = CreateContext("non-orthogonal-dimension-like-room-boundary-protection");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-supported", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("non-orthogonal single-line candidate has dimension-like weak layer evidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["nonOrthogonalDimensionLikePlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesShortDimensionLikeSingleLinePlacementWall()
    {
        var wall = ShortDimensionLikeInteriorWall("wall-short-dimension-like", 100, 100, 132, 100);
        var context = CreateContext("short-dimension-like-placement-demotion");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("short or fragmented dimension-like single-line candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["shortDimensionLikePlacementDemotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsShortDimensionLikeWallWithExplicitRoomBoundarySupport()
    {
        var wall = ShortDimensionLikeInteriorWall("wall-short-dimension-like-room", 100, 100, 132, 100);
        var context = CreateContext("short-dimension-like-room-boundary-protection");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-supported", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("short or fragmented dimension-like single-line candidate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["shortDimensionLikePlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DemotesUnsupportedHighScoreFragmentedUnlayeredPair()
    {
        var wall = new WallSegment(
            "wall-unsupported-high-score-fragmented-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(260, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,91",
                "first face merged 12 fragments",
                "second face merged 88 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall evidence: strong double-edge wall body"
            }
        };
        var context = CreateContext("demote-unsupported-high-score-fragmented-pair");
        context.Walls.Add(wall);
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var demoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(demoted.PlacementReady);
        Assert.True(demoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, demoted.Decision);
        Assert.Contains(
            demoted.Evidence,
            item => item.Contains("unsupported severe fragmented-face evidence", StringComparison.OrdinalIgnoreCase)
                && item.Contains("side room hits 0", StringComparison.OrdinalIgnoreCase)
                && item.Contains("supported endpoints 0", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsRoomSupportedHighScoreFragmentedUnlayeredPairPlacementReady()
    {
        var wall = new WallSegment(
            "wall-room-supported-high-score-fragmented-pair",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(260, 100)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,91",
                "first face merged 12 fragments",
                "second face merged 88 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall evidence: strong double-edge wall body"
            }
        };
        var context = CreateContext("keep-room-supported-high-score-fragmented-pair");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("unsupported severe fragmented-face evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsExteriorFragmentedPairWithShellContinuityPlacementReady()
    {
        var leftShell = ExteriorShellWall("wall-left-shell", 100, 100, 190, 100);
        var fragmentedShell = ExteriorShellWall("wall-fragmented-shell-gap", 191, 100, 280, 100) with
        {
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,642",
                "first face merged 8 fragments",
                "second face merged 107 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall evidence: strong double-edge wall body"
            }
        };
        var rightShell = ExteriorShellWall("wall-right-shell", 281, 100, 370, 100);
        var context = CreateContext("keep-exterior-fragmented-shell-continuity");
        context.Walls.Add(leftShell);
        context.Walls.Add(fragmentedShell);
        context.Walls.Add(rightShell);
        context.WallGraph = GraphFor(leftShell, fragmentedShell, rightShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, fragmentedShell, rightShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == fragmentedShell.Id);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("exterior shell continuity kept fragmented paired wall placement-ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("demoted from placement-ready", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["fragmentedExteriorShellContinuityRetainedWallCount"] == "1"
                && diagnostic.Properties["fragmentedPairPlacementDemotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_KeepsThinExteriorPairWithShellContinuityPlacementReady()
    {
        var leftShell = ExteriorShellWall("wall-left-shell", 100, 100, 190, 100);
        var thinShell = ExteriorShellWall("wall-thin-shell-gap", 191, 100, 315, 100) with
        {
            Thickness = 2.95,
            ThicknessMillimeters = 52,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(191, 98.525), new PlanPoint(315, 98.525)),
                new PlanLineSegment(new PlanPoint(191, 101.475), new PlanPoint(315, 101.475)),
                FaceSeparation: 2.95,
                OverlapRatio: 1,
                Score: 0.813,
                FirstFaceFragmentCount: 42,
                SecondFaceFragmentCount: 15,
                FirstFaceSourcePrimitiveIds: new[] { "thin-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "thin-face-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "face separation 2,949 drawing units",
                "pair score 0,813",
                "overlap ratio 1",
                "first face merged 42 fragments",
                "second face merged 15 fragments",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall evidence: strong double-edge wall body"
            }
        };
        var rightShell = ExteriorShellWall("wall-right-shell", 316, 100, 430, 100);
        var context = CreateContext("keep-thin-exterior-shell-continuity");
        context.Walls.Add(leftShell);
        context.Walls.Add(thinShell);
        context.Walls.Add(rightShell);
        context.WallGraph = GraphFor(leftShell, thinShell, rightShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, thinShell, rightShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == thinShell.Id);
        Assert.True(retained.PlacementReady);
        Assert.False(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, retained.Decision);
        Assert.Contains(
            retained.Evidence,
            item => item.Contains("exterior shell continuity kept fragmented paired wall placement-ready", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("demoted from placement-ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesRecoveredExteriorShellExtension()
    {
        var trustedShell = ExteriorShellWall("wall-trusted-shell", 100, 100, 220, 100);
        var trustedCorner = ExteriorShellWall("wall-trusted-shell-corner", 220, 100, 220, 220);
        var recoveredExtension = RecoveredWallBody("wall-recovered-shell-extension", 40, 112, 100, 112) with
        {
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(40, 109), new PlanPoint(100, 109)),
                new PlanLineSegment(new PlanPoint(40, 115), new PlanPoint(100, 115)),
                FaceSeparation: 6,
                OverlapRatio: 0.98,
                Score: 0.9,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "recovered-extension-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "recovered-extension-face-b" })
        };
        var context = CreateContext("promote-recovered-exterior-shell-extension");
        context.Walls.Add(trustedShell);
        context.Walls.Add(trustedCorner);
        context.Walls.Add(recoveredExtension);
        context.WallGraph = GraphFor(trustedShell, trustedCorner, recoveredExtension);
        context.WallEvidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            new[]
            {
                new WallEvidenceWallAssessment(
                    trustedShell.Id,
                    trustedShell.PageNumber,
                    trustedShell.Bounds,
                    WallEvidenceCategory.StrongWallBody,
                    Confidence.High,
                    PlacementReady: true,
                    RequiresReview: false,
                    RejectedAsNoise: false,
                    trustedShell.SourcePrimitiveIds,
                    trustedShell.Evidence)
                {
                    Decision = WallEvidenceDecision.Accept
                },
                new WallEvidenceWallAssessment(
                    trustedCorner.Id,
                    trustedCorner.PageNumber,
                    trustedCorner.Bounds,
                    WallEvidenceCategory.StrongWallBody,
                    Confidence.High,
                    PlacementReady: true,
                    RequiresReview: false,
                    RejectedAsNoise: false,
                    trustedCorner.SourcePrimitiveIds,
                    trustedCorner.Evidence)
                {
                    Decision = WallEvidenceDecision.Accept
                },
                new WallEvidenceWallAssessment(
                    recoveredExtension.Id,
                    recoveredExtension.PageNumber,
                    recoveredExtension.Bounds,
                    WallEvidenceCategory.RecoveredWallBody,
                    Confidence.High,
                    PlacementReady: false,
                    RequiresReview: true,
                    RejectedAsNoise: false,
                    recoveredExtension.SourcePrimitiveIds,
                    recoveredExtension.Evidence)
                {
                    Decision = WallEvidenceDecision.Review
                }
            });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refinedWall = Assert.Single(context.Walls, wall => wall.Id == recoveredExtension.Id);
        Assert.Equal(WallType.Exterior, refinedWall.WallType);
        Assert.Contains(
            refinedWall.Evidence,
            item => item.Contains("global exterior-shell repair matched a trusted shell extension", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == recoveredExtension.Id);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("exterior shell repair promoted wall after global shell continuity scan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellRepairPlacementPromotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_RecoversRejectedExteriorCandidateOnGlobalRoomEnvelope()
    {
        var recoveredShell = RecoveredWallBody("wall-recovered-global-envelope-shell", 80, 100, 320, 100) with
        {
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence",
                "parallel wall-face pair",
                "pair score 0.90",
                "overlap ratio 0.98",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall evidence: strong double-edge wall body"
            },
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(80, 97), new PlanPoint(320, 97)),
                new PlanLineSegment(new PlanPoint(80, 103), new PlanPoint(320, 103)),
                FaceSeparation: 6,
                OverlapRatio: 0.98,
                Score: 0.90,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "global-envelope-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "global-envelope-face-b" })
        };
        var context = CreateContext("recover-global-envelope-shell");
        context.WallCandidates.Add(recoveredShell);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallEvidenceMap = EvidenceMapFor(
            recoveredShell,
            WallEvidenceCategory.RecoveredWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: true,
            recoveredShell.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var recovered = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, recovered.WallType);
        Assert.Contains(
            recovered.Evidence,
            item => item.Contains("global-room-envelope-edge", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.False(assessment.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("rejected exterior-shell candidate restored", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellRepairRejectedRecoveredWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotRecoverCoveredAreaBoundaryOnGlobalRoomEnvelope()
    {
        var coveredBoundary = RecoveredWallBody("wall-covered-entry-boundary", 80, 100, 320, 100) with
        {
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence",
                "parallel wall-face pair",
                "pair score 0.90",
                "overlap ratio 0.98",
                "wall evidence: outdoor covered-area boundary near overbygd inngang"
            },
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(80, 97), new PlanPoint(320, 97)),
                new PlanLineSegment(new PlanPoint(80, 103), new PlanPoint(320, 103)),
                FaceSeparation: 6,
                OverlapRatio: 0.98,
                Score: 0.90,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "covered-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "covered-face-b" })
        };
        var context = CreateContext("do-not-recover-covered-envelope-boundary");
        context.WallCandidates.Add(coveredBoundary);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallEvidenceMap = EvidenceMapFor(
            coveredBoundary,
            WallEvidenceCategory.RecoveredWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: true,
            coveredBoundary.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(context.Walls);
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Reject, assessment.Decision);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellRepairRejectedRecoveredWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesExteriorFragmentsSupportedByGlobalEnvelopeChain()
    {
        var first = LowScoreExteriorShellWall("wall-chain-shell-a", 80, 100, 176, 100, 0.69);
        var second = LowScoreExteriorShellWall("wall-chain-shell-b", 196, 100, 320, 100, 0.72);
        var context = CreateContext("promote-global-envelope-shell-chain");
        context.Walls.Add(first);
        context.Walls.Add(second);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(first, second);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { first, second },
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessments = context.WallEvidenceMap.WallAssessments
            .OrderBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToArray();
        Assert.All(assessments, assessment =>
        {
            Assert.True(assessment.PlacementReady);
            Assert.False(assessment.RequiresReview);
            Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
            Assert.Contains(
                assessment.Evidence,
                item => item.Contains("global-envelope-fragment-chain", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellRepairPlacementPromotedWallCount"] == "2");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteSingleLowScoreExteriorFragmentOnGlobalEnvelope()
    {
        var fragment = LowScoreExteriorShellWall("wall-single-low-score-shell-fragment", 80, 100, 176, 100, 0.69);
        var context = CreateContext("do-not-promote-single-low-score-shell-fragment");
        context.Walls.Add(fragment);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(fragment);
        context.WallEvidenceMap = EvidenceMapFor(
            fragment,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            fragment.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, assessment.Decision);
        Assert.DoesNotContain(
            assessment.Evidence,
            item => item.Contains("global-envelope-fragment-chain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_PromotesLongDimensionLikeExteriorStrokeOnGlobalEnvelope()
    {
        var stroke = FragmentMergedExteriorShellStroke("wall-dimension-like-exterior-stroke", 80, 100, 320, 100);
        var context = CreateContext("promote-dimension-like-exterior-stroke");
        context.Walls.Add(stroke);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(stroke);
        context.WallEvidenceMap = EvidenceMapFor(
            stroke,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            stroke.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("global-envelope-fragment-chain", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("structural stroke support score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteCoveredEntryExteriorStrokeOnGlobalEnvelope()
    {
        var stroke = FragmentMergedExteriorShellStroke("wall-covered-entry-stroke", 80, 100, 320, 100) with
        {
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall evidence: outdoor covered-area boundary near overbygd inngang"
            }
        };
        var context = CreateContext("do-not-promote-covered-entry-exterior-stroke");
        context.Walls.Add(stroke);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(stroke);
        context.WallEvidenceMap = EvidenceMapFor(
            stroke,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            stroke.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(assessment.PlacementReady);
        Assert.True(assessment.RequiresReview);
        Assert.DoesNotContain(
            assessment.Evidence,
            item => item.Contains("global-envelope-fragment-chain", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_ReclassifiesReadyInteriorWallOnGlobalRoomEnvelopeAsExterior()
    {
        var shell = ExteriorShellWall("wall-ready-interior-on-envelope", 80, 100, 320, 100) with
        {
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,9",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall evidence: strong double-edge wall body",
                "wall type interior: supported wall evidence inside exterior envelope"
            }
        };
        var context = CreateContext("ready-interior-global-envelope-reclassify");
        context.Walls.Add(shell);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(shell);
        context.WallEvidenceMap = EvidenceMapFor(
            shell,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            shell.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refinedWall = Assert.Single(context.Walls);
        Assert.Equal(WallType.Exterior, refinedWall.WallType);
        Assert.Contains(
            refinedWall.Evidence,
            item => item.Contains("global exterior-shell repair matched a trusted shell extension", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("global-room-envelope-edge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotReclassifyReadyInteriorWallAwayFromGlobalRoomEnvelope()
    {
        var interior = ExteriorShellWall("wall-ready-interior-away-from-envelope", 80, 180, 320, 180) with
        {
            WallType = WallType.Interior,
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,9",
                "wall evidence: strong double-edge wall body",
                "wall type interior: supported wall evidence inside exterior envelope"
            }
        };
        var context = CreateContext("ready-interior-global-envelope-no-reclassify");
        context.Walls.Add(interior);
        context.Rooms.Add(RepairRoom(
            "room-envelope-seed",
            new PlanRect(80, 100, 240, 160),
            new[]
            {
                new PlanPoint(80, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 260),
                new PlanPoint(80, 260)
            }));
        context.WallGraph = GraphFor(interior);
        context.WallEvidenceMap = EvidenceMapFor(
            interior,
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            interior.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refinedWall = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, refinedWall.WallType);
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.PlacementReady);
        Assert.DoesNotContain(
            assessment.Evidence,
            item => item.Contains("global-room-envelope-edge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotPromoteOneSidedRoomReference()
    {
        var wall = new WallSegment(
            "wall-one-sided-medium",
            1,
            new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(100, 300)),
            6,
            Confidence.High)
        {
            WallType = WallType.Interior,
            Evidence = new[] { "wall type interior: supported wall evidence inside exterior envelope" }
        };
        var context = CreateContext("one-sided-medium-wall-stays-review");
        context.Walls.Add(wall);
        context.Rooms.Add(Room("room-a", RoomUseKind.Office, wall.Id));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            new[] { "wall evidence assessment: MediumWallBody / review / confidence 0.88", "parallel wall-face pair" });

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("room-confirmed wall body promoted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_RepairsReviewWallOnUnsupportedIndoorRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-room-boundary-repair",
            1,
            new PlanLineSegment(new PlanPoint(112, 40), new PlanPoint(112, 220)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(108.5, 40), new PlanPoint(108.5, 220)),
                new PlanLineSegment(new PlanPoint(115.5, 40), new PlanPoint(115.5, 220)),
                FaceSeparation: 7,
                OverlapRatio: 0.98,
                Score: 0.72,
                FirstFaceFragmentCount: 2,
                SecondFaceFragmentCount: 2,
                FirstFaceSourcePrimitiveIds: new[] { "face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "face-b" }),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,72",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };
        var context = CreateContext("room-boundary-repair-promotion");
        context.Walls.Add(wall);
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Equal(WallType.Interior, Assert.Single(context.Walls).WallType);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("iterative room-boundary repair promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomBoundaryRepairPlacementPromotedWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotRepairDoorOpeningCandidateOnRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-door-boundary-review",
            1,
            new PlanLineSegment(new PlanPoint(100, 40), new PlanPoint(100, 220)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(96.5, 40), new PlanPoint(96.5, 220)),
                new PlanLineSegment(new PlanPoint(103.5, 40), new PlanPoint(103.5, 220)),
                FaceSeparation: 7,
                OverlapRatio: 0.98,
                Score: 0.88,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { "door-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "door-face-b" }),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,88",
                "door/opening symbol linework near swing arc",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };
        var context = CreateContext("room-boundary-repair-door-blocker");
        context.Walls.Add(wall);
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.Rooms.Add(RepairRoom(
            "room-right",
            new PlanRect(100, 20, 140, 220),
            new[]
            {
                new PlanPoint(100, 20),
                new PlanPoint(240, 20),
                new PlanPoint(240, 240),
                new PlanPoint(100, 240)
            }));
        context.WallGraph = GraphFor(wall);
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: false,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.False(retained.PlacementReady);
        Assert.True(retained.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("iterative room-boundary repair promoted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomBoundaryRepairPlacementPromotedWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_RestoresRejectedCandidateOnUnsupportedIndoorRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-rejected-room-boundary-recovery",
            1,
            new PlanLineSegment(new PlanPoint(112, 40), new PlanPoint(112, 220)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(108.5, 40), new PlanPoint(108.5, 220)),
                new PlanLineSegment(new PlanPoint(115.5, 40), new PlanPoint(115.5, 220)),
                FaceSeparation: 7,
                OverlapRatio: 0.96,
                Score: 0.86,
                FirstFaceFragmentCount: 2,
                SecondFaceFragmentCount: 2,
                FirstFaceSourcePrimitiveIds: new[] { "rejected-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "rejected-face-b" }),
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,86",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };
        var context = CreateContext("rejected-room-boundary-recovery");
        context.WallCandidates.Add(wall);
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.MediumWallBody,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: true,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var restoredWall = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, restoredWall.WallType);
        var restored = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(restored.PlacementReady);
        Assert.False(restored.RequiresReview);
        Assert.False(restored.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Accept, restored.Decision);
        Assert.Contains(
            restored.Evidence,
            item => item.Contains("rejected room-boundary candidate restored", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomBoundaryRepairRejectedRecoveredWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_RestoresRejectedGraphObjectLikeCandidateOnUnsupportedIndoorRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-rejected-object-like-room-boundary-recovery",
            1,
            new PlanLineSegment(new PlanPoint(112, 40), new PlanPoint(112, 220)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(108.5, 40), new PlanPoint(108.5, 220)),
                new PlanLineSegment(new PlanPoint(115.5, 40), new PlanPoint(115.5, 220)),
                FaceSeparation: 7,
                OverlapRatio: 0.97,
                Score: 0.9,
                FirstFaceFragmentCount: 3,
                SecondFaceFragmentCount: 3,
                FirstFaceSourcePrimitiveIds: new[] { "object-like-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "object-like-face-b" }),
            SourcePrimitiveIds = new[] { "object-like-face-a", "object-like-face-b" },
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,9",
                "wall evidence: strong double-edge wall body",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: reclassified as object/fixture detail because graph component page:1:wall-component:9 is ObjectLikeIsland",
                "wall evidence: component excluded from structural topology as compact object-like linework"
            ]
        };
        var context = CreateContext("rejected-object-like-room-boundary-recovery");
        context.WallCandidates.Add(wall);
        context.WallGraph = ObjectLikeGraphFor(wall);
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.ObjectOrFixtureDetail,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: true,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var restoredWall = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, restoredWall.WallType);
        var restored = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.Equal(WallEvidenceCategory.MediumWallBody, restored.Category);
        Assert.True(restored.PlacementReady);
        Assert.False(restored.RequiresReview);
        Assert.False(restored.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Accept, restored.Decision);
        Assert.Contains(
            restored.Evidence,
            item => item.Contains("rejected room-boundary candidate restored", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["roomBoundaryRepairRejectedRecoveredWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotRestoreRejectedFixtureDetailOnUnsupportedIndoorRoomBoundary()
    {
        var wall = new WallSegment(
            "wall-rejected-fixture-room-boundary",
            1,
            new PlanLineSegment(new PlanPoint(112, 40), new PlanPoint(112, 220)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(108.5, 40), new PlanPoint(108.5, 220)),
                new PlanLineSegment(new PlanPoint(115.5, 40), new PlanPoint(115.5, 220)),
                FaceSeparation: 7,
                OverlapRatio: 0.97,
                Score: 0.9,
                FirstFaceFragmentCount: 3,
                SecondFaceFragmentCount: 3,
                FirstFaceSourcePrimitiveIds: new[] { "fixture-face-a" },
                SecondFaceSourcePrimitiveIds: new[] { "fixture-face-b" }),
            SourcePrimitiveIds = new[] { "fixture-face-a", "fixture-face-b" },
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,9",
                "fixture detail linework inside room",
                "wall evidence: object/fixture linework noise"
            ]
        };
        var context = CreateContext("rejected-fixture-room-boundary-blocked");
        context.WallCandidates.Add(wall);
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.WallEvidenceMap = EvidenceMapFor(
            wall,
            WallEvidenceCategory.ObjectOrFixtureDetail,
            placementReady: false,
            requiresReview: true,
            rejectedAsNoise: true,
            wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(context.Walls);
        var retained = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(retained.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Reject, retained.Decision);
        Assert.DoesNotContain(
            retained.Evidence,
            item => item.Contains("rejected room-boundary candidate restored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_InfersSharedIndoorRoomBoundaryGapWhenNoWallCandidateExists()
    {
        var context = CreateContext("shared-room-boundary-gap-inference");
        context.Rooms.Add(RepairRoom(
            "room-left",
            new PlanRect(0, 20, 100, 220),
            new[]
            {
                new PlanPoint(0, 20),
                new PlanPoint(100, 20),
                new PlanPoint(100, 240),
                new PlanPoint(0, 240)
            }));
        context.Rooms.Add(RepairRoom(
            "room-right",
            new PlanRect(100, 20, 140, 220),
            new[]
            {
                new PlanPoint(100, 20),
                new PlanPoint(240, 20),
                new PlanPoint(240, 240),
                new PlanPoint(100, 240)
            }));

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var inferred = Assert.Single(context.Walls);
        Assert.Equal(WallType.Interior, inferred.WallType);
        Assert.Equal(WallDetectionKind.SingleLine, inferred.DetectionKind);
        Assert.Contains(
            inferred.Evidence,
            item => item.Contains("inferred interior wall from unsupported shared indoor room-boundary edge", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(context.WallEvidenceMap.WallAssessments);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, assessment.Decision);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["sharedRoomBoundaryGapInferredWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_InfersShortAnchoredExteriorShellGap()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 196, 100, 196, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 196, 180);
        var context = CreateContext("short-exterior-shell-gap-inference");
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(RepairRoom(
            "room-with-missing-top-shell",
            new PlanRect(100, 100, 96, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(196, 100),
                new PlanPoint(196, 180),
                new PlanPoint(100, 180)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var inferred = Assert.Single(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-inferred", StringComparison.Ordinal));
        Assert.Equal(WallType.Exterior, inferred.WallType);
        Assert.Equal(96, inferred.DrawingLength, precision: 3);
        Assert.Contains(
            inferred.Evidence,
            item => item.Contains("inferred exterior shell wall", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == inferred.Id);
        Assert.True(assessment.PlacementReady);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellGapInferredWallCount"] == "1");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotInferExteriorShellGapAcrossCoveredOutdoorRoom()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 196, 100, 196, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 196, 180);
        var context = CreateContext("covered-outdoor-exterior-shell-gap-blocked");
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(RepairRoom(
            "room-with-covered-entry-neighbor",
            new PlanRect(100, 100, 96, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(196, 100),
                new PlanPoint(196, 180),
                new PlanPoint(100, 180)
            }));
        context.Rooms.Add(Room(
            "room-covered-entry",
            RoomUseKind.Outdoor,
            new PlanRect(100, 40, 96, 60),
            new[]
            {
                new PlanPoint(100, 40),
                new PlanPoint(196, 40),
                new PlanPoint(196, 100),
                new PlanPoint(100, 100)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-inferred", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellGapInferredWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotInferLongExteriorShellFromRoomRectangle()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 320, 100, 320, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 320, 180);
        var context = CreateContext("long-exterior-shell-room-rectangle-blocked");
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(RepairRoom(
            "room-with-long-missing-top-shell",
            new PlanRect(100, 100, 220, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 180),
                new PlanPoint(100, 180)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-inferred", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellGapInferredWallCount"] == "0");
    }

    [Fact]
    public async Task WallTypeRefinement_InfersLongExteriorShellSpanWhenSourceLineSupportsRoomBoundary()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 320, 100, 320, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 320, 180);
        var context = CreateContext(
            "long-exterior-shell-source-line-support-inference",
            new PlanPrimitive[]
            {
                new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(320, 100)))
                {
                    SourceId = "source-long-exterior-shell-face",
                    StrokeWidth = 0.05
                }
            });
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(RepairRoom(
            "room-with-source-backed-missing-top-shell",
            new PlanRect(100, 100, 220, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 180),
                new PlanPoint(100, 180)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var inferred = Assert.Single(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-source-backed", StringComparison.Ordinal));
        Assert.Equal(WallType.Exterior, inferred.WallType);
        Assert.Equal(220, inferred.DrawingLength, precision: 3);
        Assert.Contains("source-long-exterior-shell-face", inferred.SourcePrimitiveIds);
        Assert.Contains(
            inferred.Evidence,
            item => item.Contains("source-backed shell closure", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == inferred.Id);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Contains("source-long-exterior-shell-face", assessment.SourcePrimitiveIds);
    }

    [Fact]
    public async Task WallTypeRefinement_TrimsSourceBackedExteriorShellBeforeCoveredOutdoorRoom()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 420, 100, 420, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 420, 180);
        var context = CreateContext(
            "source-backed-exterior-shell-outdoor-trim",
            new PlanPrimitive[]
            {
                new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(420, 100)))
                {
                    SourceId = "source-shell-crossing-covered-entry",
                    StrokeWidth = 0.05
                }
            });
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(RepairRoom(
            "room-indoor",
            new PlanRect(100, 100, 200, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(300, 100),
                new PlanPoint(300, 180),
                new PlanPoint(100, 180)
            }));
        context.Rooms.Add(Room(
            "room-covered-entry",
            RoomUseKind.Outdoor,
            new PlanRect(300, 84, 120, 64),
            new[]
            {
                new PlanPoint(300, 84),
                new PlanPoint(420, 84),
                new PlanPoint(420, 148),
                new PlanPoint(300, 148)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var inferred = Assert.Single(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-source-backed", StringComparison.Ordinal));
        Assert.Equal(WallType.Exterior, inferred.WallType);
        Assert.Equal(200, inferred.DrawingLength, precision: 3);
        Assert.Equal(100, inferred.CenterLine.Start.X, precision: 3);
        Assert.Equal(300, inferred.CenterLine.End.X, precision: 3);
        Assert.Contains(
            inferred.Evidence,
            item => item.Contains("clipped around outdoor rooms room-covered-entry", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == inferred.Id);
        Assert.True(assessment.PlacementReady);
        Assert.Contains(
            assessment.Evidence,
            item => item.Contains("clipped around outdoor rooms room-covered-entry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WallTypeRefinement_DoesNotRecoverSourceBackedExteriorShellInsideCoveredOutdoorRoom()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 420, 100, 420, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 420, 180);
        var context = CreateContext(
            "source-backed-exterior-shell-outdoor-blocked",
            new PlanPrimitive[]
            {
                new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(420, 100)))
                {
                    SourceId = "source-shell-inside-covered-entry",
                    StrokeWidth = 0.05
                }
            });
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Rooms.Add(Room(
            "room-covered-entry",
            RoomUseKind.Outdoor,
            new PlanRect(92, 84, 336, 64),
            new[]
            {
                new PlanPoint(92, 84),
                new PlanPoint(428, 84),
                new PlanPoint(428, 148),
                new PlanPoint(92, 148)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        Assert.DoesNotContain(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-source-backed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WallTypeRefinement_InfersLongExteriorShellSpanWhenPartialCollinearShellExists()
    {
        var leftShell = ExteriorShellWall("wall-shell-left", 100, 100, 100, 180);
        var rightShell = ExteriorShellWall("wall-shell-right", 320, 100, 320, 180);
        var bottomShell = ExteriorShellWall("wall-shell-bottom", 100, 180, 320, 180);
        var partialTopShell = ExteriorShellWall("wall-shell-top-partial", 100, 100, 160, 100);
        var context = CreateContext("long-exterior-shell-collinear-support-inference");
        context.Walls.Add(leftShell);
        context.Walls.Add(rightShell);
        context.Walls.Add(bottomShell);
        context.Walls.Add(partialTopShell);
        context.Rooms.Add(RepairRoom(
            "room-with-supported-long-missing-top-shell",
            new PlanRect(100, 100, 220, 80),
            new[]
            {
                new PlanPoint(100, 100),
                new PlanPoint(320, 100),
                new PlanPoint(320, 180),
                new PlanPoint(100, 180)
            }));
        context.WallGraph = GraphFor(leftShell, rightShell, bottomShell, partialTopShell);
        context.WallEvidenceMap = EvidenceMapFor(
            new[] { leftShell, rightShell, bottomShell, partialTopShell },
            WallEvidenceCategory.StrongWallBody,
            placementReady: true,
            requiresReview: false,
            rejectedAsNoise: false,
            wall => wall.Evidence);

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var inferred = Assert.Single(
            context.Walls,
            wall => wall.Id.Contains("wall-exterior-shell-inferred", StringComparison.Ordinal));
        Assert.Equal(WallType.Exterior, inferred.WallType);
        Assert.Equal(220, inferred.DrawingLength, precision: 3);
        Assert.Contains(
            inferred.Evidence,
            item => item.Contains("inferred exterior shell wall", StringComparison.OrdinalIgnoreCase));
        var assessment = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            item => item.WallId == inferred.Id);
        Assert.True(assessment.PlacementReady);
        Assert.False(assessment.RequiresReview);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "walls.architectural_type_refined"
                && diagnostic.Properties["exteriorShellGapInferredWallCount"] == "1");
    }

    private static WallSegment ShortUnlayeredInteriorWall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,91",
                "layer (unlayered) classified Unknown (0,35)",
                "layer evidence: no strong layer name or geometry evidence",
                "wall evidence: strong double-edge wall body"
            ]
        };

    private static WallSegment FragmentMergedDimensionLikeInteriorWall(
        string id,
        double x1,
        double y1,
        double x2,
        double y2,
        int fragmentCount = 38,
        int duplicatePrimitiveCount = 9) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(0, fragmentCount)
                .Select(index => $"{id}:source:{index}")
                .ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                fragmentCount,
                0,
                0,
                duplicatePrimitiveCount,
                0,
                RequiresGeometryReview: false,
                Evidence:
                [
                    $"fragment geometry: {fragmentCount} fragment(s)",
                    "fragment geometry healed gap ratio 0",
                    $"fragment geometry collapsed {duplicatePrimitiveCount} duplicate primitive(s)"
                ]),
            Evidence =
            [
                "merged collinear wall fragments",
                $"run merged {fragmentCount} fragments",
                $"run collapsed {duplicatePrimitiveCount} duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                $"fragment geometry: {fragmentCount} fragment(s)",
                "fragment geometry healed gap ratio 0",
                $"fragment geometry collapsed {duplicatePrimitiveCount} duplicate primitive(s)",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };

    private static WallSegment NonOrthogonalDimensionLikeInteriorWall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            WallType = WallType.Interior,
            SourcePrimitiveIds = new[] { $"{id}:source" },
            Evidence =
            [
                "non-orthogonal wall-length vector",
                "angle 34,18 degrees",
                "layer (unlayered) classified Dimension (0,36)",
                "layer evidence: contains dimension-like text",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };

    private static WallSegment ShortDimensionLikeInteriorWall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            WallType = WallType.Interior,
            SourcePrimitiveIds = new[] { $"{id}:source" },
            Evidence =
            [
                "single wall-length vector run",
                "run collapsed 1 duplicate or near-duplicate wall line primitive(s)",
                "layer (unlayered) classified Dimension (0,36)",
                "layer evidence: contains dimension-like text",
                "fragment geometry: 1 fragment(s)",
                "fragment geometry healed gap ratio 0",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: medium wall body from wall-like layer, length, or structural context"
            ]
        };

    private static IEnumerable<WallSegment> DenseDetailNeighborWalls()
    {
        yield return DetailNeighbor("dense-neighbor-1", 95, 92, 135, 92);
        yield return DetailNeighbor("dense-neighbor-2", 96, 110, 138, 110);
        yield return DetailNeighbor("dense-neighbor-3", 108, 76, 108, 132);
        yield return DetailNeighbor("dense-neighbor-4", 128, 78, 128, 132);
        yield return DetailNeighbor("dense-neighbor-5", 90, 80, 130, 120);
        yield return DetailNeighbor("dense-neighbor-6", 90, 120, 134, 82);
        yield return DetailNeighbor("dense-neighbor-7", 138, 88, 156, 118);
        yield return DetailNeighbor("dense-neighbor-8", 82, 102, 110, 134);
    }

    private static IEnumerable<WallSegment> AxisDenseDetailNeighborWalls()
    {
        yield return DetailNeighbor("axis-dense-neighbor-1", 95, 92, 138, 92);
        yield return DetailNeighbor("axis-dense-neighbor-2", 96, 110, 138, 110);
        yield return DetailNeighbor("axis-dense-neighbor-3", 108, 76, 108, 132);
        yield return DetailNeighbor("axis-dense-neighbor-4", 128, 78, 128, 132);
        yield return DetailNeighbor("axis-dense-neighbor-5", 92, 122, 138, 122);
        yield return DetailNeighbor("axis-dense-neighbor-6", 116, 82, 116, 136);
        yield return DetailNeighbor("axis-dense-neighbor-7", 88, 132, 134, 132);
        yield return DetailNeighbor("axis-dense-neighbor-8", 142, 88, 142, 132);
    }

    private static WallSegment DetailNeighbor(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.Medium)
        {
            DetectionKind = WallDetectionKind.SingleLine,
            WallType = WallType.Unknown,
            Evidence = ["synthetic dense detail neighbor"]
        };

    private static ScanContext CreateContext(string documentId) =>
        new(
            new PlanDocument(
                documentId,
                new[]
                {
                    new PlanPage(1, new PlanSize(400, 400), Array.Empty<PlanPrimitive>())
                }),
            new ScannerOptions());

    private static ScanContext CreateContext(string documentId, IReadOnlyList<PlanPrimitive> primitives) =>
        new(
            new PlanDocument(
                documentId,
                new[]
                {
                    new PlanPage(1, new PlanSize(400, 400), primitives)
                }),
            new ScannerOptions());

    private static WallSegment ExteriorShellWall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { $"{id}:face-a", $"{id}:face-b" },
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(x1, y1 - 3.5), new PlanPoint(x2, y2 - 3.5)),
                new PlanLineSegment(new PlanPoint(x1, y1 + 3.5), new PlanPoint(x2, y2 + 3.5)),
                7,
                OverlapRatio: 1,
                Score: 0.9,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { $"{id}:face-a" },
                SecondFaceSourcePrimitiveIds: new[] { $"{id}:face-b" }),
            Evidence = new[]
            {
                "parallel wall-face pair",
                "pair score 0,9",
                "layer (unlayered) classified Unknown (0,35)",
                "wall evidence: strong double-edge wall body"
            }
        };

    private static WallSegment LowScoreExteriorShellWall(string id, double x1, double y1, double x2, double y2, double score)
    {
        var wall = ExteriorShellWall(id, x1, y1, x2, y2);
        return wall with
        {
            PairEvidence = wall.PairEvidence! with
            {
                Score = score,
                OverlapRatio = 0.86,
                FirstFaceFragmentCount = 18,
                SecondFaceFragmentCount = 22
            },
            Evidence = new[]
            {
                "parallel wall-face pair",
                $"pair score {score:0.###}",
                "overlap ratio 0.86",
                "first face merged 18 fragments",
                "second face merged 22 fragments",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "wall evidence: medium double-edge exterior wall body"
            }
        };
    }

    private static WallSegment FragmentMergedExteriorShellStroke(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            7,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Exterior,
            SourcePrimitiveIds = Enumerable.Range(1, 16)
                .Select(index => $"{id}:fragment-{index}")
                .ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                FragmentCount: 16,
                TotalHealedGap: 1.2,
                MaxHealedGap: 0.3,
                DuplicatePrimitiveCount: 4,
                GapRatio: 0.005,
                RequiresGeometryReview: false,
                Evidence: new[] { "fragment geometry: 16 fragment(s)" }),
            Evidence = new[]
            {
                "merged collinear wall fragments",
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary",
                "layer (unlayered) classified Dimension (0,24)",
                "layer evidence: contains dimension-like text",
                "wall evidence: medium exterior wall body stroke"
            }
        };

    private static WallSegment RecoveredWallBody(string id, double x1, double y1, double x2, double y2)
    {
        var centerLine = new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2));
        var isVertical = Math.Abs(x1 - x2) <= 0.01;
        var firstFace = isVertical
            ? new PlanLineSegment(new PlanPoint(x1 - 3, y1), new PlanPoint(x2 - 3, y2))
            : new PlanLineSegment(new PlanPoint(x1, y1 - 3), new PlanPoint(x2, y2 - 3));
        var secondFace = isVertical
            ? new PlanLineSegment(new PlanPoint(x1 + 3, y1), new PlanPoint(x2 + 3, y2))
            : new PlanLineSegment(new PlanPoint(x1, y1 + 3), new PlanPoint(x2, y2 + 3));

        return new WallSegment(
            id,
            1,
            centerLine,
            6,
            Confidence.High)
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Unknown,
            SourcePrimitiveIds = new[] { $"{id}:face-a", $"{id}:face-b" },
            PairEvidence = new WallPairEvidence(
                firstFace,
                secondFace,
                FaceSeparation: 6,
                OverlapRatio: 0.98,
                Score: 0.86,
                FirstFaceFragmentCount: 1,
                SecondFaceFragmentCount: 1,
                FirstFaceSourcePrimitiveIds: new[] { $"{id}:face-a" },
                SecondFaceSourcePrimitiveIds: new[] { $"{id}:face-b" }),
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                "wall evidence: recovered wall body from unclaimed parallel-face evidence",
                "parallel wall-face pair",
                "pair score 0.86",
                "overlap ratio 0.98",
                "wall evidence: strong double-edge wall body"
            }
        };
    }

    private static WallGraph GraphFor(WallSegment wall) =>
        new(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    wall.Bounds,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                Confidence.High,
                Array.Empty<string>())
            });

    private static WallGraph IsolatedGraphFor(
        WallSegment wall,
        bool excludedFromStructuralTopology = false) =>
        new(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-isolated",
                    1,
                    WallGraphComponentKind.IsolatedFragment,
                    wall.Bounds,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.Low,
                    new[] { "isolated wall graph fragment with weak topology" },
                    ExcludedFromStructuralTopology: excludedFromStructuralTopology)
            });

    private static WallGraph ObjectLikeGraphFor(WallSegment wall) =>
        new(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-object-like",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    wall.Bounds,
                    new[] { wall.Id },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.Medium,
                    new[]
                    {
                        "wall graph component is compact object-like linework",
                        "wall evidence: reclassified as object/fixture detail because graph component page:1:wall-component:9 is ObjectLikeIsland",
                        "wall evidence: component excluded from structural topology as compact object-like linework"
                    },
                    ExcludedFromStructuralTopology: true)
            });

    private static WallGraph GraphFor(params WallSegment[] walls) =>
        new(
            Array.Empty<WallNode>(),
            Array.Empty<WallEdge>(),
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    UnionBounds(walls),
                    walls.Select(wall => wall.Id).ToArray(),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    walls.SelectMany(wall => wall.SourcePrimitiveIds).ToArray(),
                    walls.Sum(wall => wall.DrawingLength),
                    Confidence.High,
                    Array.Empty<string>())
            });

    private static PlanRect UnionBounds(IReadOnlyList<WallSegment> walls)
    {
        var minX = walls.Min(wall => wall.Bounds.X);
        var minY = walls.Min(wall => wall.Bounds.Y);
        var maxX = walls.Max(wall => wall.Bounds.Right);
        var maxY = walls.Max(wall => wall.Bounds.Bottom);
        return new PlanRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static WallGraph SupportedEndpointGraphFor(
        WallSegment wall,
        WallGraphComponentKind componentKind = WallGraphComponentKind.MainStructural) =>
        new(
            new[]
            {
                new WallNode(
                    "node-start",
                    wall.PageNumber,
                    wall.CenterLine.Start,
                    WallNodeKind.Corner,
                    2,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>()),
                new WallNode(
                    "node-end",
                    wall.PageNumber,
                    wall.CenterLine.End,
                    WallNodeKind.Corner,
                    2,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>())
            },
            new[]
            {
                new WallEdge(
                    "edge-wall",
                    wall.PageNumber,
                    "node-start",
                    "node-end",
                    wall.Id,
                    Confidence.High)
            },
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    wall.PageNumber,
                    componentKind,
                    wall.Bounds,
                    new[] { wall.Id },
                    new[] { "node-start", "node-end" },
                    new[] { "edge-wall" },
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.High,
                    Array.Empty<string>())
            });

    private static WallGraph SupportedFragmentEndpointGraphFor(
        WallSegment wall,
        WallGraphComponentKind componentKind = WallGraphComponentKind.MainStructural)
    {
        var midPoint = wall.CenterLine.PointAt(0.5);
        return new WallGraph(
            new[]
            {
                new WallNode(
                    "node-start",
                    wall.PageNumber,
                    wall.CenterLine.Start,
                    WallNodeKind.Corner,
                    2,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>()),
                new WallNode(
                    "node-mid",
                    wall.PageNumber,
                    midPoint,
                    WallNodeKind.TJunction,
                    3,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>()),
                new WallNode(
                    "node-end",
                    wall.PageNumber,
                    wall.CenterLine.End,
                    WallNodeKind.Corner,
                    2,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>())
            },
            new[]
            {
                new WallEdge(
                    "edge-wall-a",
                    wall.PageNumber,
                    "node-start",
                    "node-mid",
                    wall.Id,
                    Confidence.High),
                new WallEdge(
                    "edge-wall-b",
                    wall.PageNumber,
                    "node-mid",
                    "node-end",
                    wall.Id,
                    Confidence.High)
            },
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    wall.PageNumber,
                    componentKind,
                    wall.Bounds,
                    new[] { wall.Id },
                    new[] { "node-start", "node-mid", "node-end" },
                    new[] { "edge-wall-a", "edge-wall-b" },
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.High,
                    Array.Empty<string>())
            });
    }

    private static WallGraph OneSupportedEndpointGraphFor(
        WallSegment wall,
        WallGraphComponentKind componentKind = WallGraphComponentKind.MainStructural) =>
        new(
            new[]
            {
                new WallNode(
                    "node-start",
                    wall.PageNumber,
                    wall.CenterLine.Start,
                    WallNodeKind.Corner,
                    2,
                    Array.Empty<string>(),
                    Confidence.High,
                    Array.Empty<string>()),
                new WallNode(
                    "node-end",
                    wall.PageNumber,
                    wall.CenterLine.End,
                    WallNodeKind.Endpoint,
                    1,
                    Array.Empty<string>(),
                    Confidence.Medium,
                    Array.Empty<string>())
            },
            new[]
            {
                new WallEdge(
                    "edge-wall",
                    wall.PageNumber,
                    "node-start",
                    "node-end",
                    wall.Id,
                    Confidence.High)
            },
            new[]
            {
                new WallGraphComponent(
                    "component-main",
                    wall.PageNumber,
                    componentKind,
                    wall.Bounds,
                    new[] { wall.Id },
                    new[] { "node-start", "node-end" },
                    new[] { "edge-wall" },
                    wall.SourcePrimitiveIds,
                    wall.DrawingLength,
                    Confidence.High,
                    Array.Empty<string>())
            });

    private static RoomRegion Room(string id, RoomUseKind useKind, params string[] wallIds) =>
        Room(
            id,
            useKind,
            new PlanRect(0, 0, 10, 10),
            new[]
            {
                new PlanPoint(0, 0),
                new PlanPoint(10, 0),
                new PlanPoint(10, 10),
                new PlanPoint(0, 10)
            },
            wallIds);

    private static RoomRegion Room(
        string id,
        RoomUseKind useKind,
        PlanRect bounds,
        IReadOnlyList<PlanPoint> boundary,
        IReadOnlyList<string>? wallIds = null) =>
        new(
            id,
            1,
            bounds,
            boundary,
            wallIds ?? Array.Empty<string>(),
            Confidence.High)
        {
            UseKind = useKind
        };

    private static RoomRegion RepairRoom(
        string id,
        PlanRect bounds,
        IReadOnlyList<PlanPoint> boundary) =>
        new(
            id,
            1,
            bounds,
            boundary,
            Array.Empty<string>(),
            Confidence.High)
        {
            UseKind = RoomUseKind.Office,
            Evidence = new[] { "semantic room boundary inferred from nearby walls for room-boundary repair test" }
        };

    private static WallEvidenceMap EvidenceMapFor(
        IReadOnlyList<WallSegment> walls,
        WallEvidenceCategory category,
        bool placementReady,
        bool requiresReview,
        bool rejectedAsNoise,
        Func<WallSegment, IReadOnlyList<string>> evidence) =>
        new(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            walls
                .Select(wall => new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    category,
                    Confidence.High,
                    placementReady,
                    requiresReview,
                    rejectedAsNoise,
                    wall.SourcePrimitiveIds,
                    evidence(wall))
                {
                    Decision = placementReady
                        ? WallEvidenceDecision.Accept
                        : rejectedAsNoise
                            ? WallEvidenceDecision.Reject
                            : WallEvidenceDecision.Review
                })
                .ToArray());

    private static WallEvidenceMap EvidenceMapFor(
        WallSegment wall,
        WallEvidenceCategory category,
        bool placementReady,
        bool requiresReview,
        bool rejectedAsNoise,
        IReadOnlyList<string> evidence) =>
        new(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            new[]
            {
                new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    category,
                    Confidence.High,
                    placementReady,
                    requiresReview,
                    rejectedAsNoise,
                    wall.SourcePrimitiveIds,
                    evidence)
                {
                    Decision = placementReady
                        ? WallEvidenceDecision.Accept
                        : rejectedAsNoise
                            ? WallEvidenceDecision.Reject
                            : WallEvidenceDecision.Review
                }
            });
}
