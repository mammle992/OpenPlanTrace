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
    public async Task WallTypeRefinement_KeepsDimensionLikeDenseLocalWallWithGeometricRoomBoundaryAndSupportedEndpoints()
    {
        var wall = ShortUnlayeredInteriorWall("wall-dimension-like-geometric-room-boundary", 100, 100, 146, 100) with
        {
            Evidence =
            [
                "parallel wall-face pair",
                "pair score 0,85",
                "overlap ratio 0,94",
                "first face merged 6 fragments",
                "second face merged 8 fragments",
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

    private static WallGraph IsolatedGraphFor(WallSegment wall) =>
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
                    new[] { "isolated wall graph fragment with weak topology" })
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
