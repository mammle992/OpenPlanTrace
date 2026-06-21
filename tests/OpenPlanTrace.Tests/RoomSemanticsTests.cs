using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class RoomSemanticsTests
{
    [Fact]
    public async Task ScanAsync_AddsRoomBoundaryLabelEvidenceAndArea()
    {
        var document = new PlanDocument(
            "room-semantics",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("wall-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("wall-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        RoomText("room-name", "OFFICE", new PlanRect(238, 190, 70, 16)),
                        RoomText("room-number", "101", new PlanRect(260, 214, 34, 16))
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                Properties = new Dictionary<string, string>
                {
                    ["dxf.defaultDrawingUnits"] = "Millimeters"
                }
            }
        };

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var room = Assert.Single(result.Rooms);

        Assert.Equal(4, room.Boundary.Count);
        Assert.Equal(100_000, room.DrawingArea);
        Assert.Equal(0.1, room.AreaSquareMeters);
        Assert.Equal("OFFICE 101", room.Label);
        Assert.Equal(RoomUseKind.Office, room.UseKind);
        Assert.Contains("room-name", room.LabelSourcePrimitiveIds);
        Assert.Contains("room-number", room.LabelSourcePrimitiveIds);
        Assert.Contains(room.Evidence, item => item.Contains("closed orthogonal", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(room.Evidence, item => item.Contains("OFFICE 101", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(room.Evidence, item => item.Contains("Office", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_DoesNotUseCompactTagsOrDimensionFragmentsAsRoomLabels()
    {
        var document = new PlanDocument(
            "room-label-fragment-filtering",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("wall-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("wall-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        RoomText("equipment-code", "P14", new PlanRect(230, 190, 34, 16)),
                        RoomText("key-code", "K8", new PlanRect(270, 190, 28, 16)),
                        RoomText("dimension-text", "10M", new PlanRect(310, 190, 34, 16)),
                        RoomText("area-superscript", "2", new PlanRect(350, 190, 12, 16)),
                        RoomText("fragment-letter", "i", new PlanRect(370, 190, 10, 16)),
                        RoomText("decimal-area-fragment", "6,0", new PlanRect(390, 190, 34, 16)),
                        RoomText("glazing-fragment", "frostet sidelfelt", new PlanRect(430, 190, 118, 16)),
                        RoomText("door-note", "d\u00f8r L\u00d8FTE-", new PlanRect(210, 220, 80, 16)),
                        RoomText("gross-area-note", "1.etg BTA", new PlanRect(310, 220, 74, 16)),
                        RoomText("floor-note", "1.etg", new PlanRect(410, 220, 46, 16))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var room = Assert.Single(result.Rooms);

        Assert.Null(room.Label);
        Assert.Empty(room.LabelSourcePrimitiveIds);
        Assert.DoesNotContain(room.Evidence, item => item.Contains("matched room label", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_AddsReviewGradeSemanticRoomSeedFromLabelAndAreaWhenLoopIsMissing()
    {
        var document = new PlanDocument(
            "semantic-room-seed",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 120), new PlanPoint(480, 120)),
                        Wall("wall-left", new PlanPoint(100, 120), new PlanPoint(100, 360)),
                        Wall("wall-right-fragment", new PlanPoint(480, 120), new PlanPoint(480, 240)),
                        RoomText("office-label", "OFFICE", new PlanRect(246, 194, 70, 16)),
                        RoomText("office-area", "12.5 m2", new PlanRect(252, 218, 64, 16))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var room = Assert.Single(result.Rooms);

        Assert.Equal("OFFICE", room.Label);
        Assert.Equal(RoomUseKind.Office, room.UseKind);
        Assert.Empty(room.WallIds);
        Assert.True(room.Confidence.Value < 0.5);
        Assert.Equal(12.5, room.AreaSquareMeters);
        Assert.Contains("office-label", room.LabelSourcePrimitiveIds);
        Assert.Contains("office-area", room.LabelSourcePrimitiveIds);
        Assert.Contains(room.Evidence, item => item.Contains("semantic room seed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(room.Evidence, item => item.Contains("requires wall-boundary review", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "rooms.semantic_label_seeds.detected");
    }

    [Fact]
    public async Task ScanAsync_DoesNotCreateSemanticRoomSeedFromPureNumericLabelWithoutLayerHint()
    {
        var document = new PlanDocument(
            "semantic-room-numeric-label",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 500),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 120), new PlanPoint(480, 120)),
                        Wall("wall-left", new PlanPoint(100, 120), new PlanPoint(100, 360)),
                        Wall("wall-right-fragment", new PlanPoint(480, 120), new PlanPoint(480, 240)),
                        new TextPrimitive("148", new PlanRect(246, 194, 40, 16))
                        {
                            SourceId = "numeric-note",
                            Layer = "A-ANNO",
                            Source = Source("numeric-note", "TEXT", "A-ANNO")
                        },
                        new TextPrimitive("20.1 m2", new PlanRect(252, 218, 64, 16))
                        {
                            SourceId = "area-note",
                            Layer = "A-ANNO",
                            Source = Source("area-note", "TEXT", "A-ANNO")
                        }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Empty(result.Rooms);
        Assert.DoesNotContain(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "rooms.semantic_label_seeds.detected");
    }

    [Fact]
    public async Task JsonExporter_IncludesRoomBoundaryAndLabelEvidence()
    {
        var result = await ScanLabeledRoomAsync();

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var room = parsed.RootElement.GetProperty("rooms")[0];

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, room.GetProperty("boundary").GetArrayLength());
        Assert.Equal("OFFICE", room.GetProperty("label").GetString());
        Assert.Equal("Office", room.GetProperty("useKind").GetString());
        Assert.True(room.GetProperty("labelSourcePrimitiveIds").GetArrayLength() > 0);
        Assert.True(room.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public async Task SvgRenderer_DrawsRoomPolygon()
    {
        var result = await ScanLabeledRoomAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("<polygon", svg);
        Assert.Contains("OFFICE", svg);
        Assert.Contains("Office", svg);
    }

    [Fact]
    public async Task ScanAsync_ClassifiesIndustrialAndResidentialRoomUseKinds()
    {
        var result = await ScanRoomUseKindsAsync();

        Assert.Contains(result.Rooms, room => room.Label == "PUMP ROOM" && room.UseKind == RoomUseKind.Mechanical);
        Assert.Contains(result.Rooms, room => room.Label == "EL" && room.UseKind == RoomUseKind.Electrical);
        Assert.Contains(result.Rooms, room => room.Label == "KITCHEN" && room.UseKind == RoomUseKind.Kitchen);
        Assert.Contains(result.Rooms, room => room.Label == "BEDROOM" && room.UseKind == RoomUseKind.Bedroom);
        Assert.Contains(result.Rooms, room => room.Label == "TERRASSE" && room.UseKind == RoomUseKind.Outdoor);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "rooms.use_semantics.detected"
                && diagnostic.Properties["knownUseKindCount"] == "5"
                && diagnostic.Properties["useKinds"].Contains("Mechanical:1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScanAsync_UsesWallGraphFacesForAdjacentRooms()
    {
        var result = await ScanAdjacentRoomsAsync();

        Assert.Equal(2, result.Rooms.Count);
        Assert.Contains(result.Rooms, room => room.Label == "OFFICE");
        Assert.Contains(result.Rooms, room => room.Label == "STORAGE");
        Assert.All(result.Rooms, room => Assert.Contains(room.Evidence, item => item.Contains("wall graph", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(result.Rooms, room => room.Bounds.Width > 350);
        Assert.Contains(result.Diagnostics.Messages, diagnostic => diagnostic.Code == "rooms.wall_graph_cycles.detected");
    }

    [Fact]
    public async Task RoomDetectionStage_AnchorsGraphEdgesToWallAxisWhenMergedNodeDrifts()
    {
        var document = new PlanDocument(
            "room-graph-node-drift",
            new[]
            {
                new PlanPage(1, new PlanSize(420, 420), Array.Empty<PlanPrimitive>())
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                WallSnapTolerance = 4,
                GeometryTolerance = new GeometryTolerance(Distance: 1.5)
            });

        var top = GraphWall("wall-top", new PlanPoint(100, 100), new PlanPoint(300, 100));
        var right = GraphWall("wall-right", new PlanPoint(300, 100), new PlanPoint(300, 300));
        var bottom = GraphWall("wall-bottom", new PlanPoint(300, 300), new PlanPoint(100, 300));
        var left = GraphWall("wall-left", new PlanPoint(100, 300), new PlanPoint(100, 100));
        context.Walls.AddRange(new[] { top, right, bottom, left });

        context.WallGraph = new WallGraph(
            new[]
            {
                GraphNode("n1", new PlanPoint(100, 100)),
                GraphNode("n2", new PlanPoint(300, 100)),
                GraphNode("n3", new PlanPoint(300, 300)),
                GraphNode("n4", new PlanPoint(100, 303))
            },
            new[]
            {
                GraphEdge("e1", "n1", "n2", top.Id),
                GraphEdge("e2", "n2", "n3", right.Id),
                GraphEdge("e3", "n3", "n4", bottom.Id),
                GraphEdge("e4", "n4", "n1", left.Id)
            });

        await new RoomDetectionStage().ExecuteAsync(context, CancellationToken.None);

        var room = Assert.Single(context.Rooms);
        Assert.Equal(100, room.Bounds.Left, precision: 1);
        Assert.Equal(100, room.Bounds.Top, precision: 1);
        Assert.Equal(300, room.Bounds.Right, precision: 1);
        Assert.Equal(300, room.Bounds.Bottom, precision: 1);
        Assert.Contains(bottom.Id, room.WallIds);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "rooms.wall_graph_cycles.detected");
    }

    [Fact]
    public async Task RoomDetectionStage_SuppressesSkinnyOffsetFacesAsRoomNoise()
    {
        var document = new PlanDocument(
            "room-graph-sliver-offset",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(680, 460),
                    new PlanPrimitive[]
                    {
                        RoomText("office-label", "OFFICE", new PlanRect(255, 210, 70, 16))
                    })
            });
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                WallSnapTolerance = 4,
                GeometryTolerance = new GeometryTolerance(Distance: 1.5)
            });

        var mainTop = GraphWall("wall-main-top", new PlanPoint(100, 100), new PlanPoint(500, 100));
        var sliverTop = GraphWall("wall-sliver-top", new PlanPoint(500, 100), new PlanPoint(520, 100));
        var sliverRight = GraphWall("wall-sliver-right", new PlanPoint(520, 100), new PlanPoint(520, 350));
        var sliverBottom = GraphWall("wall-sliver-bottom", new PlanPoint(520, 350), new PlanPoint(500, 350));
        var shared = GraphWall("wall-shared-offset", new PlanPoint(500, 100), new PlanPoint(500, 350));
        var mainBottom = GraphWall("wall-main-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350));
        var mainLeft = GraphWall("wall-main-left", new PlanPoint(100, 350), new PlanPoint(100, 100));
        context.Walls.AddRange(new[] { mainTop, sliverTop, sliverRight, sliverBottom, shared, mainBottom, mainLeft });

        context.WallGraph = new WallGraph(
            new[]
            {
                GraphNode("n1", new PlanPoint(100, 100)),
                GraphNode("n2", new PlanPoint(500, 100)),
                GraphNode("n3", new PlanPoint(520, 100)),
                GraphNode("n4", new PlanPoint(520, 350)),
                GraphNode("n5", new PlanPoint(500, 350)),
                GraphNode("n6", new PlanPoint(100, 350))
            },
            new[]
            {
                GraphEdge("e1", "n1", "n2", mainTop.Id),
                GraphEdge("e2", "n2", "n3", sliverTop.Id),
                GraphEdge("e3", "n3", "n4", sliverRight.Id),
                GraphEdge("e4", "n4", "n5", sliverBottom.Id),
                GraphEdge("e5", "n5", "n2", shared.Id),
                GraphEdge("e6", "n5", "n6", mainBottom.Id),
                GraphEdge("e7", "n6", "n1", mainLeft.Id)
            });

        await new RoomDetectionStage().ExecuteAsync(context, CancellationToken.None);

        var room = Assert.Single(context.Rooms);
        Assert.Equal("OFFICE", room.Label);
        Assert.Equal(100, room.Bounds.Left, precision: 1);
        Assert.Equal(500, room.Bounds.Right, precision: 1);
        Assert.DoesNotContain(context.Rooms, candidate => Math.Min(candidate.Bounds.Width, candidate.Bounds.Height) <= 32);

        var diagnostic = Assert.Single(
            context.Diagnostics.Build().Messages,
            item => item.Code == "rooms.sliver_faces.suppressed");
        Assert.Equal("1", diagnostic.Properties["suppressedRoomCandidateCount"]);
        Assert.Contains("wall-shared-offset", diagnostic.SourcePrimitiveIds);
    }

    [Fact]
    public async Task ScanAsync_AddsRoomAdjacencyGraphForSharedRoomBoundary()
    {
        var result = await ScanAdjacentRoomsAsync();
        var edge = Assert.Single(result.RoomAdjacencyGraph.Edges);

        Assert.Equal(RoomAdjacencyKind.BoundaryAdjacent, edge.Kind);
        Assert.Contains(edge.FirstRoomLabel, new[] { "OFFICE", "STORAGE" });
        Assert.Contains(edge.SecondRoomLabel, new[] { "OFFICE", "STORAGE" });
        Assert.NotEqual(edge.FirstRoomId, edge.SecondRoomId);
        Assert.Equal(RoomAdjacencyDirection.East, edge.DirectionFromFirstToSecond);
        Assert.Equal(RoomAdjacencyDirection.West, edge.DirectionFromSecondToFirst);
        Assert.True(edge.SharedBoundaryLength > 200);
        Assert.NotNull(edge.SharedBoundary);
        Assert.Single(edge.SharedWallIds);
        Assert.Contains(edge.SharedWallIds, id => result.Walls.Single(wall => wall.Id == id).SourcePrimitiveIds.Contains("divider"));
        Assert.Contains(edge.Evidence, item => item.Contains("shared boundary", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "rooms.adjacency_graph.detected"
                && diagnostic.SourcePrimitiveIds.Contains("divider"));
        var cluster = Assert.Single(result.RoomAdjacencyGraph.Clusters);
        Assert.Equal(RoomClusterKind.CompartmentGroup, cluster.Kind);
        Assert.Equal(2, cluster.RoomIds.Count);
        Assert.Equal(new[] { "OFFICE", "STORAGE" }, cluster.RoomLabels.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(edge.Id, cluster.RoomAdjacencyIds);
        Assert.Contains(cluster.Evidence, item => item.Contains("CompartmentGroup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_AddsNorthSouthRoomAdjacencyDirection()
    {
        var result = await ScanStackedRoomsAsync();
        var edge = Assert.Single(result.RoomAdjacencyGraph.Edges);

        Assert.Equal("UPPER", edge.FirstRoomLabel);
        Assert.Equal("LOWER", edge.SecondRoomLabel);
        Assert.Equal(RoomAdjacencyDirection.South, edge.DirectionFromFirstToSecond);
        Assert.Equal(RoomAdjacencyDirection.North, edge.DirectionFromSecondToFirst);
        Assert.Contains(edge.Evidence, item => item.Contains("South", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_AddsSingletonRoomClustersForDisconnectedRooms()
    {
        var result = await ScanDisconnectedRoomsAsync();

        Assert.Equal(2, result.Rooms.Count);
        Assert.Empty(result.RoomAdjacencyGraph.Edges);
        Assert.Equal(2, result.RoomAdjacencyGraph.Clusters.Count);
        Assert.All(result.RoomAdjacencyGraph.Clusters, cluster =>
        {
            Assert.Single(cluster.RoomIds);
            Assert.Empty(cluster.RoomAdjacencyIds);
            Assert.Contains(cluster.Evidence, item => item.Contains("singleton", StringComparison.OrdinalIgnoreCase));
        });
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "rooms.clusters.detected"
                && diagnostic.Properties["singletonClusterCount"] == "2"
                && diagnostic.Properties["singleRoomClusterCount"] == "2");
    }

    [Fact]
    public async Task ScanAsync_AttachesConnectedRoomsToOpeningsOnSharedBoundary()
    {
        var result = await ScanAdjacentRoomsWithDividerOpeningAsync();
        var opening = Assert.Single(result.Openings);
        var edge = Assert.Single(result.RoomAdjacencyGraph.Edges);

        Assert.Equal(RoomAdjacencyKind.ConnectedByOpening, edge.Kind);
        Assert.Contains(opening.Id, edge.OpeningIds);
        Assert.Equal(2, opening.ConnectedRoomIds.Count);
        Assert.Contains(result.Rooms.Single(room => room.Label == "OFFICE").Id, opening.ConnectedRoomIds);
        Assert.Contains(result.Rooms.Single(room => room.Label == "STORAGE").Id, opening.ConnectedRoomIds);
        Assert.Contains("OFFICE", opening.ConnectedRoomLabels);
        Assert.Contains("STORAGE", opening.ConnectedRoomLabels);
        Assert.Equal(2, opening.ConnectedRoomLinks.Count);
        Assert.Contains(opening.ConnectedRoomLinks, link =>
            link.RoomLabel == "OFFICE"
            && link.SharesHostWall
            && link.Side == OpeningRoomSide.PositiveNormalSide
            && link.RoomSidePoint is { X: < 300 }
            && link.NearestBoundaryPoint is { X: 300 }
            && link.SignedDistanceFromOpening > 0
            && link.RoomAdjacencyIds.Contains(edge.Id)
            && link.DistanceToOpening <= 1
            && link.Confidence.Value > 0.85);
        Assert.Contains(opening.ConnectedRoomLinks, link =>
            link.RoomLabel == "STORAGE"
            && link.SharesHostWall
            && link.Side == OpeningRoomSide.NegativeNormalSide
            && link.RoomSidePoint is { X: > 300 }
            && link.NearestBoundaryPoint is { X: 300 }
            && link.SignedDistanceFromOpening < 0
            && link.RoomAdjacencyIds.Contains(edge.Id)
            && link.DistanceToOpening <= 1
            && link.Confidence.Value > 0.85);
        Assert.Single(opening.RoomAdjacencyIds);
        Assert.Contains(edge.Id, opening.RoomAdjacencyIds);
        Assert.Contains(opening.Evidence, item => item.Contains("connected rooms", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "openings.room_connectivity.detected"
                && diagnostic.Properties["multiRoomOpeningCount"] == "1"
                && diagnostic.Properties["connectionLinkCount"] == "2"
                && diagnostic.Properties["hostWallConnectionLinkCount"] == "2");
        var cluster = Assert.Single(result.RoomAdjacencyGraph.Clusters);
        Assert.Equal(RoomClusterKind.ConnectedSuite, cluster.Kind);
        Assert.Contains(cluster.Evidence, item => item.Contains("opening", StringComparison.OrdinalIgnoreCase));

        var routingPassage = Assert.Single(result.RoutingLayer.Passages);
        Assert.Equal(opening.Id, routingPassage.SourceId);
        Assert.Equal(opening.ConnectedRoomIds, routingPassage.ConnectedRoomIds);
        Assert.Equal(opening.ConnectedRoomLabels, routingPassage.ConnectedRoomLabels);
        Assert.Equal(opening.RoomAdjacencyIds, routingPassage.RoomAdjacencyIds);
        Assert.Equal(2, routingPassage.ConnectedRoomLinks.Count);
        Assert.Contains(routingPassage.ConnectedRoomLinks, link =>
            link.RoomLabel == "OFFICE"
            && link.Side == OpeningRoomSide.PositiveNormalSide
            && link.RoomSidePoint is { X: < 300 });
        Assert.Contains(routingPassage.ConnectedRoomLinks, link =>
            link.RoomLabel == "STORAGE"
            && link.Side == OpeningRoomSide.NegativeNormalSide
            && link.RoomSidePoint is { X: > 300 });
    }

    [Fact]
    public async Task ScanAsync_ClassifiesCorridorLikeRoomClusterFromLabelAndShape()
    {
        var result = await ScanCorridorRoomAsync();
        var cluster = Assert.Single(result.RoomAdjacencyGraph.Clusters);

        Assert.Equal(RoomClusterKind.CorridorLike, cluster.Kind);
        Assert.Contains("CORRIDOR", cluster.RoomLabels);
        Assert.Contains(cluster.Evidence, item => item.Contains("circulation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "rooms.clusters.detected"
                && diagnostic.Properties["corridorLikeClusterCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_ClassifiesOpenPlanRoomClusterFromLabel()
    {
        var result = await ScanOpenPlanRoomAsync();
        var cluster = Assert.Single(result.RoomAdjacencyGraph.Clusters);

        Assert.Equal(RoomClusterKind.OpenPlan, cluster.Kind);
        Assert.Contains("OPEN OFFICE", cluster.RoomLabels);
        Assert.Contains(cluster.Evidence, item => item.Contains("open-plan", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "rooms.clusters.detected"
                && diagnostic.Properties["openPlanClusterCount"] == "1");
    }

    [Fact]
    public async Task JsonExporter_IncludesRoomAdjacencyGraph()
    {
        var result = await ScanAdjacentRoomsAsync();

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var graph = parsed.RootElement.GetProperty("roomAdjacencyGraph");
        var edge = graph.GetProperty("edges")[0];
        var cluster = graph.GetProperty("clusters")[0];

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("BoundaryAdjacent", edge.GetProperty("kind").GetString());
        Assert.Equal("East", edge.GetProperty("directionFromFirstToSecond").GetString());
        Assert.Equal("West", edge.GetProperty("directionFromSecondToFirst").GetString());
        Assert.True(edge.GetProperty("sharedBoundaryLength").GetDouble() > 200);
        Assert.Equal(1, edge.GetProperty("sharedWallIds").GetArrayLength());
        Assert.True(edge.GetProperty("sharedBoundary").GetProperty("start").GetProperty("x").GetDouble() > 0);
        Assert.True(edge.GetProperty("evidence").GetArrayLength() > 0);
        Assert.Equal(2, cluster.GetProperty("roomIds").GetArrayLength());
        Assert.Equal(1, cluster.GetProperty("roomAdjacencyIds").GetArrayLength());
        Assert.Equal("CompartmentGroup", cluster.GetProperty("kind").GetString());
        Assert.True(cluster.GetProperty("drawingArea").GetDouble() > 0);
    }

    [Fact]
    public async Task JsonExporter_IncludesOpeningRoomConnectivity()
    {
        var result = await ScanAdjacentRoomsWithDividerOpeningAsync();

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var opening = parsed.RootElement.GetProperty("openings")[0];

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(2, opening.GetProperty("connectedRoomIds").GetArrayLength());
        Assert.Contains("OFFICE", opening.GetProperty("connectedRoomLabels").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("STORAGE", opening.GetProperty("connectedRoomLabels").EnumerateArray().Select(item => item.GetString()));
        var links = opening.GetProperty("connectedRoomLinks").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Contains(links, link =>
            link.GetProperty("roomLabel").GetString() == "OFFICE"
            && link.GetProperty("sharesHostWall").GetBoolean()
            && link.GetProperty("side").GetString() == "PositiveNormalSide"
            && link.GetProperty("roomSidePoint").GetProperty("x").GetDouble() < 300
            && link.GetProperty("nearestBoundaryPoint").GetProperty("x").GetDouble() == 300
            && link.GetProperty("signedDistanceFromOpening").GetDouble() > 0
            && link.GetProperty("roomAdjacencyIds").GetArrayLength() == 1
            && link.GetProperty("distanceToOpening").GetDouble() <= 1);
        Assert.Contains(links, link =>
            link.GetProperty("roomLabel").GetString() == "STORAGE"
            && link.GetProperty("sharesHostWall").GetBoolean()
            && link.GetProperty("side").GetString() == "NegativeNormalSide"
            && link.GetProperty("roomSidePoint").GetProperty("x").GetDouble() > 300
            && link.GetProperty("nearestBoundaryPoint").GetProperty("x").GetDouble() == 300
            && link.GetProperty("signedDistanceFromOpening").GetDouble() < 0
            && link.GetProperty("confidence").GetDouble() > 0.85);
        Assert.Equal(1, opening.GetProperty("roomAdjacencyIds").GetArrayLength());

        var passage = parsed.RootElement.GetProperty("routingLayer").GetProperty("passages")[0];
        Assert.Equal(2, passage.GetProperty("connectedRoomLinks").GetArrayLength());
        Assert.Equal(1, passage.GetProperty("roomAdjacencyIds").GetArrayLength());
        Assert.Contains(
            "PositiveNormalSide",
            passage.GetProperty("connectedRoomLinks")
                .EnumerateArray()
                .Select(link => link.GetProperty("side").GetString()));
    }

    [Fact]
    public async Task GeoJsonExporter_IncludesOpeningRoomConnectivity()
    {
        var result = await ScanAdjacentRoomsWithDividerOpeningAsync();

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(geoJson);
        var openingFeature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "opening");
        var properties = openingFeature.GetProperty("properties");

        Assert.Equal(2, properties.GetProperty("connectedRoomIds").GetArrayLength());
        Assert.Contains("OFFICE", properties.GetProperty("connectedRoomLabels").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("STORAGE", properties.GetProperty("connectedRoomLabels").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(2, properties.GetProperty("connectedRoomLinkCount").GetInt32());
        Assert.Equal(2, properties.GetProperty("connectedRoomLinkDistances").GetArrayLength());
        Assert.Equal(2, properties.GetProperty("connectedRoomLinkConfidences").GetArrayLength());
        Assert.Contains("PositiveNormalSide", properties.GetProperty("connectedRoomLinkSides").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("NegativeNormalSide", properties.GetProperty("connectedRoomLinkSides").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(properties.GetProperty("connectedRoomLinkSignedDistances").EnumerateArray(), item => item.GetDouble() > 0);
        Assert.Contains(properties.GetProperty("connectedRoomLinkSignedDistances").EnumerateArray(), item => item.GetDouble() < 0);
        Assert.Equal(2, properties.GetProperty("connectedRoomLinkRoomSidePoints").GetArrayLength());
        Assert.Equal(2, properties.GetProperty("connectedRoomLinkNearestBoundaryPoints").GetArrayLength());
        Assert.Equal(1, properties.GetProperty("roomAdjacencyIds").GetArrayLength());

        var routingPassageFeature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "routingPassage");
        var routingPassageProperties = routingPassageFeature.GetProperty("properties");
        Assert.Equal(2, routingPassageProperties.GetProperty("connectedRoomLinkCount").GetInt32());
        Assert.Contains("PositiveNormalSide", routingPassageProperties.GetProperty("connectedRoomLinkSides").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("NegativeNormalSide", routingPassageProperties.GetProperty("connectedRoomLinkSides").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(1, routingPassageProperties.GetProperty("roomAdjacencyIds").GetArrayLength());
    }

    [Fact]
    public async Task PlacementJsonExporter_IncludesOpeningRoomSideLinks()
    {
        var result = await ScanAdjacentRoomsWithDividerOpeningAsync();

        var json = PlanPlacementJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var opening = parsed.RootElement.GetProperty("openings")[0];

        Assert.Equal(2, opening.GetProperty("connectedRoomIds").GetArrayLength());
        Assert.Equal(2, opening.GetProperty("connectedRoomLabels").GetArrayLength());
        Assert.Equal(1, opening.GetProperty("roomAdjacencyIds").GetArrayLength());
        var links = opening.GetProperty("connectedRoomLinks").EnumerateArray().ToArray();
        Assert.Equal(2, links.Length);
        Assert.Contains(links, link =>
            link.GetProperty("roomLabel").GetString() == "OFFICE"
            && link.GetProperty("side").GetString() == "PositiveNormalSide"
            && link.GetProperty("roomSidePoint").GetProperty("x").GetDouble() < 300
            && link.GetProperty("signedDistanceFromOpening").GetDouble() > 0);
        Assert.Contains(links, link =>
            link.GetProperty("roomLabel").GetString() == "STORAGE"
            && link.GetProperty("side").GetString() == "NegativeNormalSide"
            && link.GetProperty("roomSidePoint").GetProperty("x").GetDouble() > 300
            && link.GetProperty("signedDistanceFromOpening").GetDouble() < 0);

        var passage = parsed.RootElement.GetProperty("routingLayer").GetProperty("passages")[0];
        Assert.Equal(2, passage.GetProperty("connectedRoomLinks").GetArrayLength());
        Assert.Equal(1, passage.GetProperty("roomAdjacencyIds").GetArrayLength());
        Assert.Contains(
            "NegativeNormalSide",
            passage.GetProperty("connectedRoomLinks")
                .EnumerateArray()
                .Select(link => link.GetProperty("side").GetString()));
    }

    [Fact]
    public async Task GeoJsonExporter_IncludesRoomAdjacencyDirection()
    {
        var result = await ScanAdjacentRoomsAsync();

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(geoJson);
        var adjacencyFeature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "roomAdjacency");
        var properties = adjacencyFeature.GetProperty("properties");

        Assert.Equal("East", properties.GetProperty("directionFromFirstToSecond").GetString());
        Assert.Equal("West", properties.GetProperty("directionFromSecondToFirst").GetString());
    }

    [Fact]
    public async Task GeoJsonExporter_IncludesRoomClusterFeature()
    {
        var result = await ScanAdjacentRoomsAsync();

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(geoJson);
        var clusterFeature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "roomCluster");
        var properties = clusterFeature.GetProperty("properties");

        Assert.Equal(2, properties.GetProperty("roomIds").GetArrayLength());
        Assert.Equal(1, properties.GetProperty("roomAdjacencyIds").GetArrayLength());
        Assert.Equal("CompartmentGroup", properties.GetProperty("clusterKind").GetString());
        Assert.Equal("Polygon", clusterFeature.GetProperty("geometry").GetProperty("type").GetString());
    }

    [Fact]
    public async Task GeoJsonExporter_IncludesRoomUseKind()
    {
        var result = await ScanLabeledRoomAsync();

        var geoJson = PlanTraceGeoJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(geoJson);
        var roomFeature = parsed.RootElement
            .GetProperty("features")
            .EnumerateArray()
            .First(feature => feature.GetProperty("properties").GetProperty("featureType").GetString() == "room");
        var properties = roomFeature.GetProperty("properties");

        Assert.Equal("Office", properties.GetProperty("roomUseKind").GetString());
    }

    [Fact]
    public async Task SvgRenderer_DrawsRoomClusterKind()
    {
        var result = await ScanAdjacentRoomsAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("id=\"room-clusters\"", svg);
        Assert.Contains("class=\"room-cluster\"", svg);
        Assert.Contains("CompartmentGroup", svg);
    }

    [Fact]
    public async Task SvgRenderer_DrawsRoomAdjacencyGraph()
    {
        var result = await ScanAdjacentRoomsAsync();

        var svg = PlanOverlaySvgRenderer.RenderPage(result, 1);

        Assert.Contains("id=\"room-adjacency\"", svg);
        Assert.Contains("class=\"room-adjacency\"", svg);
        Assert.Contains("BoundaryAdjacent", svg);
    }

    [Fact]
    public async Task BenchmarkEvaluator_CanAssertRoomAdjacencyCounts()
    {
        var result = await ScanAdjacentRoomsAsync();
        var fixture = new BenchmarkFixture
        {
            Id = "adjacent-room-benchmark",
            SourcePath = "adjacent-rooms.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinRooms = 2,
                MinRoomAdjacencies = 1,
                MaxRoomAdjacencies = 1,
                MinRoomClusters = 1,
                MaxRoomClusters = 1
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(5));

        Assert.True(benchmark.Passed);
        Assert.Equal(1, benchmark.Counts.RoomAdjacencies);
        Assert.Equal(1, benchmark.Counts.RoomClusters);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_adjacencies.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_adjacencies.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_clusters.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_clusters.max" && assertion.Passed);
    }

    [Fact]
    public async Task ScanAsync_TracesConcaveRoomBoundaryFromWallGraph()
    {
        var document = new PlanDocument(
            "l-shaped-room",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(700, 550),
                    new PlanPrimitive[]
                    {
                        Wall("top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("right-upper", new PlanPoint(500, 100), new PlanPoint(500, 250)),
                        Wall("notch", new PlanPoint(500, 250), new PlanPoint(300, 250)),
                        Wall("inner-drop", new PlanPoint(300, 250), new PlanPoint(300, 400)),
                        Wall("bottom", new PlanPoint(300, 400), new PlanPoint(100, 400)),
                        Wall("left", new PlanPoint(100, 400), new PlanPoint(100, 100)),
                        RoomText("lab-label", "LAB", new PlanRect(180, 175, 40, 16))
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var room = Assert.Single(result.Rooms);

        Assert.Equal("LAB", room.Label);
        Assert.Equal(6, room.Boundary.Count);
        Assert.Equal(90_000, room.DrawingArea);
        Assert.Contains(room.Boundary, point => point.X == 300 && point.Y == 250);
        Assert.Contains(room.Evidence, item => item.Contains("6 boundary vertices", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<PlanScanResult> ScanLabeledRoomAsync()
    {
        var document = new PlanDocument(
            "room-export",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 420),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(400, 100)),
                        Wall("wall-right", new PlanPoint(400, 100), new PlanPoint(400, 300)),
                        Wall("wall-bottom", new PlanPoint(400, 300), new PlanPoint(100, 300)),
                        Wall("wall-left", new PlanPoint(100, 300), new PlanPoint(100, 100)),
                        RoomText("room-name", "OFFICE", new PlanRect(220, 184, 70, 16))
                    })
            });

        return await new OpenPlanTraceScanner().ScanAsync(document);
    }

    private static async Task<PlanScanResult> ScanAdjacentRoomsAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "adjacent-rooms",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(700, 500),
                        new PlanPrimitive[]
                        {
                            Wall("outer-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                            Wall("outer-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                            Wall("outer-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                            Wall("outer-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                            Wall("divider", new PlanPoint(300, 100), new PlanPoint(300, 350)),
                            RoomText("office-label", "OFFICE", new PlanRect(168, 190, 70, 16)),
                            RoomText("storage-label", "STORAGE", new PlanRect(365, 190, 90, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanCorridorRoomAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "corridor-room",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(760, 360),
                        new PlanPrimitive[]
                        {
                            Wall("corridor-top", new PlanPoint(100, 120), new PlanPoint(620, 120)),
                            Wall("corridor-right", new PlanPoint(620, 120), new PlanPoint(620, 220)),
                            Wall("corridor-bottom", new PlanPoint(620, 220), new PlanPoint(100, 220)),
                            Wall("corridor-left", new PlanPoint(100, 220), new PlanPoint(100, 120)),
                            RoomText("corridor-label", "CORRIDOR", new PlanRect(330, 160, 86, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanOpenPlanRoomAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "open-plan-room",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(760, 500),
                        new PlanPrimitive[]
                        {
                            Wall("open-top", new PlanPoint(120, 110), new PlanPoint(620, 110)),
                            Wall("open-right", new PlanPoint(620, 110), new PlanPoint(620, 380)),
                            Wall("open-bottom", new PlanPoint(620, 380), new PlanPoint(120, 380)),
                            Wall("open-left", new PlanPoint(120, 380), new PlanPoint(120, 110)),
                            RoomText("open-label", "OPEN OFFICE", new PlanRect(315, 235, 112, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanRoomUseKindsAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "room-use-kinds",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(920, 620),
                        new PlanPrimitive[]
                        {
                            Wall("outer-top", new PlanPoint(80, 80), new PlanPoint(820, 80)),
                            Wall("outer-right", new PlanPoint(820, 80), new PlanPoint(820, 500)),
                            Wall("outer-bottom", new PlanPoint(820, 500), new PlanPoint(80, 500)),
                            Wall("outer-left", new PlanPoint(80, 500), new PlanPoint(80, 80)),
                            Wall("divider-v1", new PlanPoint(270, 80), new PlanPoint(270, 500)),
                            Wall("divider-v2", new PlanPoint(520, 80), new PlanPoint(520, 500)),
                            Wall("divider-v3", new PlanPoint(670, 80), new PlanPoint(670, 500)),
                            Wall("divider-h1", new PlanPoint(80, 290), new PlanPoint(820, 290)),
                            RoomText("pump-label", "PUMP ROOM", new PlanRect(145, 175, 102, 16)),
                            RoomText("el-label", "EL", new PlanRect(392, 175, 24, 16)),
                            RoomText("kitchen-label", "KITCHEN", new PlanRect(565, 175, 84, 16)),
                            RoomText("terrace-label", "TERRASSE", new PlanRect(705, 175, 90, 16)),
                            RoomText("bed-label", "BEDROOM", new PlanRect(610, 380, 92, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanAdjacentRoomsWithDividerOpeningAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "adjacent-rooms-with-opening",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(700, 500),
                        new PlanPrimitive[]
                        {
                            Wall("outer-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                            Wall("outer-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                            Wall("outer-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                            Wall("outer-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                            Wall("divider", new PlanPoint(300, 100), new PlanPoint(300, 350)),
                            OpeningTick("divider-window-a", new PlanPoint(294, 190), new PlanPoint(306, 190)),
                            OpeningTick("divider-window-b", new PlanPoint(294, 230), new PlanPoint(306, 230)),
                            RoomText("office-label", "OFFICE", new PlanRect(168, 190, 70, 16)),
                            RoomText("storage-label", "STORAGE", new PlanRect(365, 190, 90, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanStackedRoomsAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "stacked-rooms",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(600, 650),
                        new PlanPrimitive[]
                        {
                            Wall("outer-top", new PlanPoint(100, 100), new PlanPoint(450, 100)),
                            Wall("outer-right", new PlanPoint(450, 100), new PlanPoint(450, 500)),
                            Wall("outer-bottom", new PlanPoint(450, 500), new PlanPoint(100, 500)),
                            Wall("outer-left", new PlanPoint(100, 500), new PlanPoint(100, 100)),
                            Wall("divider", new PlanPoint(100, 300), new PlanPoint(450, 300)),
                            RoomText("upper-label", "UPPER", new PlanRect(225, 190, 70, 16)),
                            RoomText("lower-label", "LOWER", new PlanRect(225, 390, 70, 16))
                        })
                }));

    private static async Task<PlanScanResult> ScanDisconnectedRoomsAsync() =>
        await new OpenPlanTraceScanner().ScanAsync(
            new PlanDocument(
                "disconnected-rooms",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(800, 420),
                        new PlanPrimitive[]
                        {
                            Wall("left-top", new PlanPoint(100, 100), new PlanPoint(260, 100)),
                            Wall("left-right", new PlanPoint(260, 100), new PlanPoint(260, 260)),
                            Wall("left-bottom", new PlanPoint(260, 260), new PlanPoint(100, 260)),
                            Wall("left-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                            Wall("right-top", new PlanPoint(420, 100), new PlanPoint(580, 100)),
                            Wall("right-right", new PlanPoint(580, 100), new PlanPoint(580, 260)),
                            Wall("right-bottom", new PlanPoint(580, 260), new PlanPoint(420, 260)),
                            Wall("right-left", new PlanPoint(420, 260), new PlanPoint(420, 100)),
                            RoomText("left-label", "LEFT", new PlanRect(155, 170, 55, 16)),
                            RoomText("right-label", "RIGHT", new PlanRect(470, 170, 65, 16))
                        })
                }));

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static TextPrimitive RoomText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-ROOM-NAME",
            Source = Source(sourceId, "TEXT", "A-ROOM-NAME")
        };

    private static LinePrimitive OpeningTick(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WINDOW",
            Source = Source(sourceId, "LINE", "A-WINDOW")
        };

    private static WallSegment GraphWall(string id, PlanPoint start, PlanPoint end) =>
        new(id, 1, new PlanLineSegment(start, end), 4, Confidence.High)
        {
            SourcePrimitiveIds = new[] { id },
            Evidence = new[] { "test graph wall" }
        };

    private static WallNode GraphNode(string id, PlanPoint position) =>
        new(
            id,
            1,
            position,
            WallNodeKind.Corner,
            2,
            Array.Empty<string>(),
            Confidence.High,
            Array.Empty<string>());

    private static WallEdge GraphEdge(string id, string fromNodeId, string toNodeId, string wallId) =>
        new(id, 1, fromNodeId, toNodeId, wallId, Confidence.High);

    private static PrimitiveSourceMetadata Source(string sourceId, string entityType, string layer) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            DrawingSpace = SourceDrawingSpace.Model
        };
}
