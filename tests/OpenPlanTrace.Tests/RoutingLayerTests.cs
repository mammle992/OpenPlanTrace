namespace OpenPlanTrace.Tests;

public sealed class RoutingLayerTests
{
    [Fact]
    public void RoutingLayer_UsesWallGraphEdgesForBarrierSpansWhenNodesAreAvailable()
    {
        var wall = Wall("wall-top", 100, 100, 300, 100);
        var divider = Wall("wall-divider", 200, 100, 200, 240);
        var component = new WallGraphComponent(
            "component-main",
            1,
            WallGraphComponentKind.MainStructural,
            new PlanRect(100, 100, 200, 140),
            [wall.Id, divider.Id],
            ["node-left", "node-mid", "node-right", "node-divider-end"],
            ["edge-left", "edge-right", "edge-divider"],
            wall.SourcePrimitiveIds.Concat(divider.SourcePrimitiveIds).ToArray(),
            wall.DrawingLength + divider.DrawingLength,
            Confidence.High,
            ["synthetic main structural component"]);
        var result = SyntheticResult(
            [wall, divider],
            new WallGraph(
                [
                    Node("node-left", 100, 100, WallNodeKind.Endpoint),
                    Node("node-mid", 200, 100, WallNodeKind.TJunction),
                    Node("node-right", 300, 100, WallNodeKind.Endpoint),
                    Node("node-divider-end", 200, 240, WallNodeKind.Endpoint)
                ],
                [
                    new WallEdge("edge-left", 1, "node-left", "node-mid", wall.Id, Confidence.High),
                    new WallEdge("edge-right", 1, "node-mid", "node-right", wall.Id, Confidence.High),
                    new WallEdge("edge-divider", 1, "node-mid", "node-divider-end", divider.Id, Confidence.High)
                ],
                [component]));

        var barriers = result.RoutingLayer.Barriers
            .Where(barrier => barrier.SourceId == wall.Id)
            .OrderBy(barrier => barrier.CenterLine.Start.X)
            .ToArray();

        Assert.Equal(2, barriers.Length);
        Assert.Equal("routing-barrier:wall-top:span:1", barriers[0].Id);
        Assert.Equal(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(200, 100)), barriers[0].CenterLine);
        Assert.Equal(100, barriers[0].DrawingLength, precision: 3);
        Assert.Equal("routing-barrier:wall-top:span:2", barriers[1].Id);
        Assert.Equal(new PlanLineSegment(new PlanPoint(200, 100), new PlanPoint(300, 100)), barriers[1].CenterLine);
        Assert.Equal(100, barriers[1].DrawingLength, precision: 3);
        Assert.All(barriers, barrier => Assert.Contains("routing barrier split at hard wall graph junction nodes", barrier.Evidence));
        Assert.Contains(
            result.RoutingLayer.Evidence,
            item => item == "routing barriers are split at wall graph junctions when node geometry is available");
    }

    [Fact]
    public void RoutingLayer_CompressesMinorTeeDetailNodesInsideBarrierSpans()
    {
        var wall = Wall("wall-top", 100, 100, 300, 100);
        var detail = Wall("short-detail", 200, 100, 200, 124);
        var component = new WallGraphComponent(
            "component-main",
            1,
            WallGraphComponentKind.MainStructural,
            new PlanRect(100, 100, 200, 24),
            [wall.Id, detail.Id],
            ["node-left", "node-mid", "node-right", "node-detail-end"],
            ["edge-left", "edge-right", "edge-detail"],
            wall.SourcePrimitiveIds.Concat(detail.SourcePrimitiveIds).ToArray(),
            wall.DrawingLength + detail.DrawingLength,
            Confidence.High,
            ["synthetic main structural component"]);
        var result = SyntheticResult(
            [wall, detail],
            new WallGraph(
                [
                    Node("node-left", 100, 100, WallNodeKind.Endpoint),
                    Node("node-mid", 200, 100, WallNodeKind.TJunction),
                    Node("node-right", 300, 100, WallNodeKind.Endpoint),
                    Node("node-detail-end", 200, 124, WallNodeKind.Endpoint)
                ],
                [
                    new WallEdge("edge-left", 1, "node-left", "node-mid", wall.Id, Confidence.High),
                    new WallEdge("edge-right", 1, "node-mid", "node-right", wall.Id, Confidence.High),
                    new WallEdge("edge-detail", 1, "node-mid", "node-detail-end", detail.Id, Confidence.High)
                ],
                [component]));

        var barrier = Assert.Single(result.RoutingLayer.Barriers.Where(item => item.SourceId == wall.Id));

        Assert.Equal("routing-barrier:wall-top:span:1", barrier.Id);
        Assert.Equal(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(300, 100)), barrier.CenterLine);
        Assert.Contains("compressed 1 minor routing junction node(s)", barrier.Evidence);
    }

    [Fact]
    public void RoutingLayer_SuppressesDenseMinorSecondaryDetailPatterns()
    {
        var host = Wall("detail-host", 100, 100, 300, 100);
        var teeth = new[]
        {
            Wall("detail-tooth-1", 140, 100, 140, 124),
            Wall("detail-tooth-2", 180, 100, 180, 124),
            Wall("detail-tooth-3", 220, 100, 220, 124),
            Wall("detail-tooth-4", 260, 100, 260, 124)
        };
        var walls = new[] { host }.Concat(teeth).ToArray();
        var component = new WallGraphComponent(
            "component-secondary",
            1,
            WallGraphComponentKind.SecondaryStructural,
            new PlanRect(100, 100, 200, 24),
            walls.Select(wall => wall.Id).ToArray(),
            ["node-left", "node-t1", "node-t2", "node-t3", "node-t4", "node-right", "node-t1-end", "node-t2-end", "node-t3-end", "node-t4-end"],
            ["edge-host-1", "edge-host-2", "edge-host-3", "edge-host-4", "edge-host-5", "edge-tooth-1", "edge-tooth-2", "edge-tooth-3", "edge-tooth-4"],
            walls.SelectMany(wall => wall.SourcePrimitiveIds).ToArray(),
            walls.Sum(wall => wall.DrawingLength),
            Confidence.Medium,
            ["synthetic secondary detail component"]);
        var result = SyntheticResult(
            walls,
            new WallGraph(
                [
                    Node("node-left", 100, 100, WallNodeKind.Endpoint),
                    Node("node-t1", 140, 100, WallNodeKind.TJunction),
                    Node("node-t2", 180, 100, WallNodeKind.TJunction),
                    Node("node-t3", 220, 100, WallNodeKind.TJunction),
                    Node("node-t4", 260, 100, WallNodeKind.TJunction),
                    Node("node-right", 300, 100, WallNodeKind.Endpoint),
                    Node("node-t1-end", 140, 124, WallNodeKind.Endpoint),
                    Node("node-t2-end", 180, 124, WallNodeKind.Endpoint),
                    Node("node-t3-end", 220, 124, WallNodeKind.Endpoint),
                    Node("node-t4-end", 260, 124, WallNodeKind.Endpoint)
                ],
                [
                    new WallEdge("edge-host-1", 1, "node-left", "node-t1", host.Id, Confidence.High),
                    new WallEdge("edge-host-2", 1, "node-t1", "node-t2", host.Id, Confidence.High),
                    new WallEdge("edge-host-3", 1, "node-t2", "node-t3", host.Id, Confidence.High),
                    new WallEdge("edge-host-4", 1, "node-t3", "node-t4", host.Id, Confidence.High),
                    new WallEdge("edge-host-5", 1, "node-t4", "node-right", host.Id, Confidence.High),
                    new WallEdge("edge-tooth-1", 1, "node-t1", "node-t1-end", teeth[0].Id, Confidence.High),
                    new WallEdge("edge-tooth-2", 1, "node-t2", "node-t2-end", teeth[1].Id, Confidence.High),
                    new WallEdge("edge-tooth-3", 1, "node-t3", "node-t3-end", teeth[2].Id, Confidence.High),
                    new WallEdge("edge-tooth-4", 1, "node-t4", "node-t4-end", teeth[3].Id, Confidence.High)
                ],
                [component]));

        var denseWallIds = walls.Select(wall => wall.Id).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(result.RoutingLayer.Barriers, barrier => denseWallIds.Contains(barrier.SourceId));
        Assert.Contains(
            result.RoutingLayer.Evidence,
            item => item == "dense minor-detail routing barriers suppressed: 5");
    }

    [Fact]
    public void RoutingLayer_SuppressesShortUnreferencedStructuralFragmentsWhenRoomsExist()
    {
        var result = SyntheticResult(
            Room("room-1", ["wall-top", "wall-right", "wall-bottom", "wall-left"]),
            includeShortWallInStructuralComponent: true);

        var routing = result.RoutingLayer;

        Assert.DoesNotContain(routing.Barriers, barrier => barrier.SourceId == "short-detail");
        Assert.Contains(routing.Barriers, barrier => barrier.SourceId == "wall-top");
        Assert.Contains(
            routing.Evidence,
            item => item == "short unreferenced wall fragments suppressed as routing barriers: 1");
    }

    [Fact]
    public void RoutingLayer_KeepsShortStructuralBarrierWhenRoomReferencesIt()
    {
        var result = SyntheticResult(
            Room("room-1", ["wall-top", "wall-right", "wall-bottom", "wall-left", "short-detail"]),
            includeShortWallInStructuralComponent: true);

        var routing = result.RoutingLayer;

        Assert.Contains(routing.Barriers, barrier => barrier.SourceId == "short-detail");
        Assert.Contains(
            routing.Evidence,
            item => item == "short unreferenced wall fragments suppressed as routing barriers: 0");
    }

    [Fact]
    public void RoutingLayer_KeepsShortStructuralBarrierWhenNoRoomsWereSolved()
    {
        var result = SyntheticResult(
            room: null,
            includeShortWallInStructuralComponent: true);

        var routing = result.RoutingLayer;

        Assert.Contains(routing.Barriers, barrier => barrier.SourceId == "short-detail");
        Assert.Contains(
            routing.Evidence,
            item => item == "short unreferenced wall fragments suppressed as routing barriers: 0");
    }

    [Fact]
    public void RoutingLayer_SuppressesUnprotectedReviewRequiredWallEvidence()
    {
        var acceptedWall = Wall("accepted-wall", 100, 100, 300, 100);
        var reviewWall = Wall("review-wall", 140, 140, 140, 240);
        var result = SyntheticResult(
            [acceptedWall, reviewWall],
            WallGraph.Empty,
            rooms: Array.Empty<RoomRegion>(),
            wallEvidenceMap: WallEvidenceMapFor(ReviewAssessment(reviewWall)));

        var routing = result.RoutingLayer;

        Assert.Contains(routing.Barriers, barrier => barrier.SourceId == acceptedWall.Id);
        Assert.DoesNotContain(routing.Barriers, barrier => barrier.SourceId == reviewWall.Id);
        Assert.Contains(
            routing.Evidence,
            item => item == "wall-evidence review barriers suppressed: 1");
        Assert.Contains(
            routing.Evidence,
            item => item == "non-trusted wall evidence blocked from routing protection: 1");
    }

    [Fact]
    public void RoutingLayer_SuppressesReviewRequiredWallEvidenceEvenWhenReferencedByRoomTopology()
    {
        var reviewWall = Wall("review-wall", 100, 100, 300, 100);
        var result = SyntheticResult(
            [reviewWall],
            WallGraph.Empty,
            rooms: [Room("room-1", [reviewWall.Id])],
            wallEvidenceMap: WallEvidenceMapFor(ReviewAssessment(reviewWall)));

        var routing = result.RoutingLayer;

        Assert.DoesNotContain(routing.Barriers, barrier => barrier.SourceId == reviewWall.Id);
        Assert.Contains(
            routing.Evidence,
            item => item == "wall-evidence review barriers suppressed: 1");
        Assert.Contains(
            routing.Evidence,
            item => item == "non-trusted wall evidence blocked from routing protection: 1");
    }

    private static PlanScanResult SyntheticResult(RoomRegion? room, bool includeShortWallInStructuralComponent)
    {
        var walls = new[]
        {
            Wall("wall-top", 100, 100, 300, 100),
            Wall("wall-right", 300, 100, 300, 260),
            Wall("wall-bottom", 300, 260, 100, 260),
            Wall("wall-left", 100, 260, 100, 100),
            Wall("short-detail", 180, 100, 180, 124)
        };
        var wallIds = includeShortWallInStructuralComponent
            ? walls.Select(wall => wall.Id).ToArray()
            : walls.Where(wall => wall.Id != "short-detail").Select(wall => wall.Id).ToArray();
        var component = new WallGraphComponent(
            "component-main",
            1,
            WallGraphComponentKind.MainStructural,
            new PlanRect(100, 100, 200, 160),
            wallIds,
            Array.Empty<string>(),
            wallIds.Select((_, index) => $"edge-{index + 1}").ToArray(),
            wallIds,
            walls.Where(wall => wallIds.Contains(wall.Id, StringComparer.Ordinal)).Sum(wall => wall.DrawingLength),
            Confidence.High,
            ["synthetic main structural component"]);
        var wallGraph = new WallGraph(
            Array.Empty<WallNode>(),
            wallIds.Select((wallId, index) => new WallEdge(
                $"edge-{index + 1}",
                1,
                $"node-{index + 1}-a",
                $"node-{index + 1}-b",
                wallId,
                Confidence.High)).ToArray(),
            [component]);
        var rooms = room is null
            ? Array.Empty<RoomRegion>()
            : new[] { room };

        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "synthetic-routing",
                [new PlanPage(1, new PlanSize(500, 400), Array.Empty<PlanPrimitive>())]),
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            walls,
            wallGraph,
            rooms,
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()));
    }

    private static PlanScanResult SyntheticResult(
        IReadOnlyList<WallSegment> walls,
        WallGraph wallGraph,
        IReadOnlyList<RoomRegion>? rooms = null,
        WallEvidenceMap? wallEvidenceMap = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "synthetic-routing",
                [new PlanPage(1, new PlanSize(500, 400), Array.Empty<PlanPrimitive>())]),
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            walls,
            wallGraph,
            rooms ?? Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()))
        {
            WallEvidenceMap = wallEvidenceMap ?? WallEvidenceMap.Empty
        };
    }

    private static WallEvidenceMap WallEvidenceMapFor(params WallEvidenceWallAssessment[] assessments) =>
        new(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            assessments,
            SourceCandidateWallCount: assessments.Length);

    private static WallEvidenceWallAssessment ReviewAssessment(WallSegment wall) =>
        new(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.WeakSingleLine,
            new Confidence(0.52),
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            ["wall evidence: review-required synthetic wall"])
        {
            Decision = WallEvidenceDecision.Review
        };

    private static WallSegment Wall(string id, double x1, double y1, double x2, double y2) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = [id],
            Evidence = ["synthetic wall"]
        };

    private static WallNode Node(string id, double x, double y, WallNodeKind kind) =>
        new(id, 1, new PlanPoint(x, y), kind, 2, Array.Empty<string>(), Confidence.High, ["synthetic wall node"]);

    private static RoomRegion Room(string id, IReadOnlyList<string> wallIds) =>
        new(
            id,
            1,
            new PlanRect(100, 100, 200, 160),
            [
                new PlanPoint(100, 100),
                new PlanPoint(300, 100),
                new PlanPoint(300, 260),
                new PlanPoint(100, 260)
            ],
            wallIds,
            Confidence.High);
}
