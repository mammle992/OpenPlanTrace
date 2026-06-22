using System.Text.Json;
using OpenPlanTrace.Export;

namespace OpenPlanTrace.Tests;

public sealed class ScanQualityTests
{
    [Fact]
    public async Task ScanAsync_AttachesDeterministicQualityReport()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateRoomDocument());

        Assert.Equal(1, result.Quality.PageCount);
        Assert.True(result.Quality.PrimitiveCount > 0);
        Assert.True(result.Quality.DetectionCount > 0);
        Assert.InRange(result.Quality.OverallConfidence.Value, 0, 1);
        Assert.Contains(result.Quality.Detectors, detector => detector.Name == "walls" && detector.ItemCount > 0);
        Assert.Contains(result.Quality.Detectors, detector => detector.Name == "rooms" && detector.ItemCount > 0);
        Assert.Contains(result.Quality.Evidence, item => item.Contains("readiness score", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_FlagsEmptyDocumentsAsReviewRequired()
    {
        var document = new PlanDocument(
            "empty",
            new[] { new PlanPage(1, new PlanSize(500, 400), Array.Empty<PlanPrimitive>()) });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.True(result.Quality.RequiresReview);
        Assert.True(result.Quality.OverallConfidence.Value < 0.65);
        Assert.Contains(result.Quality.Issues, issue => issue.Code == "quality.no_primitives");
        Assert.Contains(result.Quality.Issues, issue => issue.Code == "quality.no_walls");
    }

    [Fact]
    public async Task JsonExporter_IncludesQualityReport()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateRoomDocument());
        using var document = JsonDocument.Parse(
            PlanTraceJsonExporter.Serialize(
                result,
                new PlanTraceJsonExportOptions { WriteIndented = false }));

        var quality = document.RootElement.GetProperty("quality");
        Assert.Equal(result.Quality.Grade.ToString(), quality.GetProperty("grade").GetString());
        Assert.Equal(result.Quality.RequiresReview, quality.GetProperty("requiresReview").GetBoolean());
        Assert.True(quality.GetProperty("detectors").GetArrayLength() > 0);
        Assert.True(quality.GetProperty("evidence").GetArrayLength() > 0);
    }

    [Fact]
    public void Analyze_FlagsStructuralGeometryInNonFloorplanRegions()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 1000, 700), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 700, 700), Confidence.High),
            new SheetRegion("title", 1, RegionKind.TitleBlock, new PlanRect(720, 0, 280, 700), Confidence.High)
        };
        var walls = Enumerable.Range(0, 4)
            .Select(index => SyntheticWall($"main-wall-{index}", 100 + index * 30, 100, 120 + index * 30, 100, withEvidence: false))
            .Concat(Enumerable.Range(0, 6)
                .Select(index => SyntheticWall($"title-wall-{index}", 750 + index * 8, 80, 750 + index * 8, 240, withEvidence: false)))
            .ToArray();
        var result = CreateSyntheticResult(regions: regions, walls: walls);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.True(quality.RequiresReview);
        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.sheet_contamination");
        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.geometry_outside_main_region");
        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.fragmented_wall_graph");
        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.weak_source_provenance");
        Assert.Contains(quality.Evidence, item => item.Contains("professional scan-risk audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_FlagsOpeningsNotLinkedToRoomTopology()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100),
            SyntheticWall("w5", 260, 100, 410, 100),
            SyntheticWall("w6", 410, 100, 410, 250),
            SyntheticWall("w7", 410, 250, 260, 250),
            SyntheticWall("w8", 260, 250, 260, 100)
        };
        var wallGraph = new WallGraph(
            new[]
            {
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250),
                SyntheticNode("n5", 260, 100),
                SyntheticNode("n6", 410, 100),
                SyntheticNode("n7", 410, 250),
                SyntheticNode("n8", 260, 250)
            },
            new[]
            {
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4"),
                SyntheticEdge("e5", "n5", "n6", "w5"),
                SyntheticEdge("e6", "n6", "n7", "w6"),
                SyntheticEdge("e7", "n7", "n8", "w7"),
                SyntheticEdge("e8", "n8", "n5", "w8")
            });
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"]),
            SyntheticRoom("r2", new PlanRect(265, 105, 140, 140), ["w5", "w6", "w7", "w8"])
        };
        var openings = Enumerable.Range(0, 4)
            .Select(index => new OpeningCandidate($"o{index + 1}", 1, OpeningType.Door, new PlanRect(230 + index * 15, 120, 12, 28), Confidence.Medium)
            {
                HostWallIds = ["w2"],
                CenterLine = new PlanLineSegment(new PlanPoint(230 + index * 15, 120), new PlanPoint(230 + index * 15, 148)),
                Evidence = ["host wall gap candidate"]
            })
            .ToArray();
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms,
            openings: openings);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.True(quality.RequiresReview);
        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.opening_room_mismatch");
    }

    [Fact]
    public void Analyze_FlagsDetectedRoomsWithoutLinkedWallEvidence()
    {
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var rooms = new[]
        {
            SyntheticRoom("room-linked", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"]),
            SyntheticRoom("room-detached", new PlanRect(300, 105, 80, 120), [])
        };
        var result = CreateSyntheticResult(walls: walls, rooms: rooms);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        var issue = Assert.Single(
            quality.Issues,
            issue => issue.Code == "quality.scan_risk.rooms_without_wall_links");
        Assert.Equal(DiagnosticSeverity.Warning, issue.Severity);
        Assert.Equal("2", issue.Properties["roomCount"]);
        Assert.Equal("1", issue.Properties["roomsWithoutWallLinks"]);
        Assert.Equal("room-detached", issue.Properties["roomIds"]);
        Assert.Equal("0.5", issue.Properties["roomsWithoutWallLinksRatio"]);
    }

    [Fact]
    public void Analyze_FlagsRoomLinkedOpeningsWithoutSideAwareConnections()
    {
        var scenario = CreateTwoRoomOpeningScenario(withSideAwareLinks: false);
        var result = CreateSyntheticResult(
            regions: scenario.Regions,
            walls: scenario.Walls,
            wallGraph: scenario.WallGraph,
            rooms: scenario.Rooms,
            openings: scenario.Openings);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        var openingIssue = Assert.Single(quality.Issues, issue => issue.Code == "quality.scan_risk.opening_room_side_links_incomplete");
        Assert.Equal("4", openingIssue.Properties["roomConnectedOpeningCount"]);
        Assert.Equal("0", openingIssue.Properties["sideAwareConnectedOpeningCount"]);
        Assert.Equal("0", openingIssue.Properties["sideAwareConnectedOpeningRatio"]);

        var routingIssue = Assert.Single(quality.Issues, issue => issue.Code == "quality.scan_risk.routing_passage_room_side_links_incomplete");
        Assert.Equal("4", routingIssue.Properties["roomLinkedRoutingPassageCount"]);
        Assert.Equal("0", routingIssue.Properties["sideAwareRoutingPassageCount"]);

        var readiness = PlanImportReadiness.FromScanResult(result with { Quality = quality });

        Assert.True(readiness.ReadyForGeometryImport);
        Assert.True(readiness.ReadyForRoutingImport);
        Assert.True(readiness.RequiresReview);
        Assert.Equal("ReviewRequired", readiness.Grade);
        Assert.Contains("quality.scan_risk.opening_room_side_links_incomplete", readiness.ReviewIssueCodes);
        Assert.Contains("quality.scan_risk.routing_passage_room_side_links_incomplete", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("room-side links", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_AcceptsRoomLinkedOpeningsWithSideAwareConnections()
    {
        var scenario = CreateTwoRoomOpeningScenario(withSideAwareLinks: true);
        var result = CreateSyntheticResult(
            regions: scenario.Regions,
            walls: scenario.Walls,
            wallGraph: scenario.WallGraph,
            rooms: scenario.Rooms,
            openings: scenario.Openings);

        var quality = PlanScanQualityAnalyzer.Analyze(result);
        var openingDetector = Assert.Single(quality.Detectors, detector => detector.Name == "openings");

        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.opening_room_side_links_incomplete");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.routing_passage_room_side_links_incomplete");
        Assert.Equal(0, openingDetector.ReviewRequiredCount);

        var readiness = PlanImportReadiness.FromScanResult(result with { Quality = quality });

        Assert.True(readiness.ReadyForGeometryImport);
        Assert.True(readiness.ReadyForRoutingImport);
        Assert.False(readiness.RequiresReview);
        Assert.Empty(readiness.ReviewIssueCodes);
    }

    [Fact]
    public void Analyze_DoesNotFlagFullyCoveredMinorWallGraphIslandsAsFragmentedRisk()
    {
        var walls = Enumerable.Range(0, 10)
            .Select(index => SyntheticWall($"w{index + 1}", 100 + index * 35, 100, 122 + index * 35, 100))
            .ToArray();
        var nodes = Enumerable.Range(0, walls.Length * 2)
            .Select(index => SyntheticNode($"n{index + 1}", 100 + index * 20, 100))
            .ToArray();
        var edges = walls
            .Select((wall, index) => SyntheticEdge($"e{index + 1}", nodes[index * 2].Id, nodes[(index * 2) + 1].Id, wall.Id))
            .ToArray();
        var result = CreateSyntheticResult(
            walls: walls,
            wallGraph: new WallGraph(nodes, edges));

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.fragmented_wall_graph");
    }

    [Fact]
    public void Analyze_FlagsDiffuseMajorWallGraphComponentsAsFragmentedRisk()
    {
        var walls = Enumerable.Range(0, 55)
            .Select(index => SyntheticWall($"w{index + 1}", 100 + index * 10, 100, 108 + index * 10, 100))
            .ToArray();
        var nodes = Enumerable.Range(0, 60)
            .Select(index => SyntheticNode($"n{index + 1}", 100 + index * 10, 100 + (index / 12) * 25))
            .ToArray();
        var edges = new List<WallEdge>();
        var wallIndex = 0;
        for (var componentIndex = 0; componentIndex < 5; componentIndex++)
        {
            var nodeOffset = componentIndex * 12;
            for (var index = 0; index < 11; index++)
            {
                edges.Add(SyntheticEdge(
                    $"e{edges.Count + 1}",
                    nodes[nodeOffset + index].Id,
                    nodes[nodeOffset + index + 1].Id,
                    walls[wallIndex++].Id));
            }
        }

        var result = CreateSyntheticResult(
            walls: walls,
            wallGraph: new WallGraph(nodes, edges));

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        var issue = Assert.Single(quality.Issues, issue => issue.Code == "quality.scan_risk.fragmented_wall_graph");
        Assert.Equal("5", issue.Properties["significantComponentCount"]);
        Assert.Equal("12,12,12,12,12", issue.Properties["topComponentNodeCounts"]);
    }

    [Fact]
    public void Analyze_FlagsDenseUnconfirmedObjectNoise()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var objects = Enumerable.Range(0, 60)
            .Select(index => new ObjectCandidate($"obj-{index:000}", 1, ObjectCandidateKind.Symbol, new PlanRect(120 + index % 10 * 18, 130 + index / 10 * 18, 8, 8), Confidence.Medium)
            {
                Category = index % 2 == 0 ? ObjectCategory.Unknown : ObjectCategory.GenericSymbol,
                Evidence = ["compact repeated symbol candidate"]
            })
            .ToArray();
        var groups = new[]
        {
            new ObjectCandidateGroup(
                "group-1",
                "symbol:unknown-a",
                ObjectCandidateKind.Symbol,
                ObjectCategory.GenericSymbol,
                30,
                objects[0].Bounds,
                [1],
                objects.Take(30).Select(candidate => candidate.Id).ToArray(),
                [],
                true,
                Confidence.Medium,
                ["repeated unconfirmed symbol family"]),
            new ObjectCandidateGroup(
                "group-2",
                "symbol:unknown-b",
                ObjectCandidateKind.Symbol,
                ObjectCategory.GenericSymbol,
                30,
                objects[30].Bounds,
                [1],
                objects.Skip(30).Select(candidate => candidate.Id).ToArray(),
                [],
                true,
                Confidence.Medium,
                ["repeated unconfirmed symbol family"])
        };
        var result = CreateSyntheticResult(regions: regions, walls: walls, objects: objects, objectGroups: groups);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.Contains(quality.Issues, issue => issue.Code == "quality.scan_risk.object_noise_dominance");
    }

    [Fact]
    public void Analyze_DoesNotFlagObjectNoiseDominanceWithoutStructuralWalls()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var objects = Enumerable.Range(0, 60)
            .Select(index => new ObjectCandidate($"obj-{index:000}", 1, ObjectCandidateKind.Symbol, new PlanRect(120 + index % 10 * 18, 130 + index / 10 * 18, 8, 8), Confidence.Medium)
            {
                Category = ObjectCategory.Unknown,
                Evidence = ["compact repeated symbol candidate"]
            })
            .ToArray();
        var groups = new[]
        {
            new ObjectCandidateGroup(
                "group-1",
                "symbol:unknown-a",
                ObjectCandidateKind.Symbol,
                ObjectCategory.GenericSymbol,
                60,
                objects[0].Bounds,
                [1],
                objects.Select(candidate => candidate.Id).ToArray(),
                [],
                true,
                Confidence.Medium,
                ["repeated unconfirmed symbol family"])
        };
        var result = CreateSyntheticResult(regions: regions, objects: objects, objectGroups: groups);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.Contains(quality.Issues, issue => issue.Code == "quality.no_walls");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.object_noise_dominance");
    }

    [Fact]
    public void Analyze_ReportsObjectReviewBacklogWithoutForcingScanQualityReview()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4")
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var group = new ObjectCandidateGroup(
            "group-review",
            "symbol:generic",
            ObjectCandidateKind.Symbol,
            ObjectCategory.GenericSymbol,
            4,
            new PlanRect(130, 130, 12, 12),
            [1],
            ["object-1", "object-2", "object-3", "object-4"],
            ["symbol-src"],
            true,
            Confidence.Medium,
            ["unreviewed symbol group"]);
        var aggregate = new ObjectAggregate(
            "aggregate-review",
            1,
            new PlanRect(180, 130, 30, 20),
            ObjectCategory.GenericSymbol,
            ObjectCandidateKind.Symbol,
            4,
            ["object-1", "object-2", "object-3", "object-4"],
            ["group-review"],
            ["aggregate-src"],
            ObjectRoutingInfluence.SoftObstacle,
            ObjectStructuralInfluence.NonStructural,
            false,
            RoomUseKind.Unknown,
            Confidence.Medium,
            ["unreviewed aggregate"])
        {
            RequiresReview = true
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms,
            objectGroups: [group],
            objectAggregates: [aggregate]);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.False(quality.RequiresReview);
        Assert.Equal(PlanScanQualityGrade.Usable, quality.Grade);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "quality.object_groups_require_review"
            && issue.Severity == DiagnosticSeverity.Info);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "quality.object_aggregates_require_review"
            && issue.Severity == DiagnosticSeverity.Info);

        var readiness = PlanImportReadiness.FromScanResult(result with { Quality = quality });

        Assert.False(readiness.RequiresReview);
        Assert.True(readiness.ReadyForGeometryImport);
        Assert.True(readiness.ReadyForMetricImport);
        Assert.DoesNotContain("ReviewRequired", readiness.Grade);
        Assert.DoesNotContain("quality.object_groups_require_review", readiness.ReviewIssueCodes);
        Assert.DoesNotContain("quality.object_aggregates_require_review", readiness.ReviewIssueCodes);
        Assert.Empty(readiness.ReviewIssueCodes);
    }

    [Fact]
    public void ImportReadiness_RequiresReviewForWallGraphEndpointGap()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4")
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms,
            diagnostics:
            [
                new PlanDiagnostic(
                    "wall_graph.endpoint_gap.review",
                    DiagnosticSeverity.Warning,
                    "wall-graph",
                    "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.")
                {
                    PageNumber = 1,
                    Region = new PlanRect(180, 92, 28, 24),
                    Confidence = Confidence.Medium,
                    SourcePrimitiveIds = ["w1", "w2"],
                    Properties = new Dictionary<string, string>
                    {
                        ["gapKind"] = "EndpointToWall",
                        ["gapDistance"] = "12",
                        ["nodeId"] = "n1",
                        ["hostWallId"] = "w2",
                        ["wallIds"] = "w1,w2"
                    }
                }
            ]);
        var quality = PlanScanQualityAnalyzer.Analyze(result);

        var readiness = PlanImportReadiness.FromScanResult(result with { Quality = quality });

        Assert.True(readiness.RequiresReview);
        Assert.Equal("ReviewRequired", readiness.Grade);
        Assert.True(readiness.ReadyForGeometryImport);
        Assert.Contains("placement.wall_graph.endpoint_gaps.require_review", readiness.ReviewIssueCodes);
        Assert.DoesNotContain("quality.diagnostic_warnings", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("endpoint gaps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportReadiness_BlocksGeometryForReviewRequiredWallEvidence()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4")
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms) with
        {
            Quality = UsableQuality(),
            WallEvidenceMap = new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                new[]
                {
                    new WallEvidenceWallAssessment(
                        "w1",
                        1,
                        walls[0].Bounds,
                        WallEvidenceCategory.WeakSingleLine,
                        new Confidence(0.42),
                        PlacementReady: false,
                        RequiresReview: true,
                        RejectedAsNoise: false,
                        SourcePrimitiveIds: ["w1"],
                        Evidence: ["synthetic wall evidence requires review before placement"])
                    {
                        Decision = WallEvidenceDecision.Review
                    }
                })
        };

        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.False(readiness.ReadyForGeometryImport);
        Assert.False(readiness.ReadyForMetricImport);
        Assert.True(readiness.RequiresReview);
        Assert.Equal("Blocked", readiness.Grade);
        Assert.Contains("placement.import.low_coordinate_ready_ratio", readiness.BlockingIssueCodes);
        Assert.Contains("placement.import.low_metric_ready_ratio", readiness.BlockingIssueCodes);
        Assert.Contains("placement.wall_evidence.requires_review", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("Wall Evidence V2", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            readiness.Evidence,
            evidence => evidence.Contains("wall/room/opening/evidence entities 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportReadiness_ExposesFragmentedShortWallPairReviewCode()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var wall = SyntheticWall("fragmented-short-pair", 100, 100, 160, 100) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            PairEvidence = new WallPairEvidence(
                new PlanLineSegment(new PlanPoint(100, 97), new PlanPoint(160, 97)),
                new PlanLineSegment(new PlanPoint(100, 103), new PlanPoint(160, 103)),
                6,
                0.91,
                0.72,
                78,
                7,
                ["fragmented-face-a"],
                ["fragmented-face-b"]),
            Evidence =
            [
                "wall evidence: topology-supported fragmented paired wall promoted after both endpoints aligned to trusted structural graph"
            ]
        };
        var assessment = new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.MediumWallBody,
            new Confidence(0.84),
            PlacementReady: true,
            RequiresReview: false,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            [
                "wall evidence: short unlayered parallel-face candidate has noisy fragmented face evidence (score 0.72, max face fragments 78, total face fragments 85); keep for topology but block exact placement until reviewed",
                "wall evidence: topology-supported fragmented paired wall promoted after both endpoints aligned to trusted structural graph"
            ])
        {
            Decision = WallEvidenceDecision.Accept
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            rooms: [SyntheticRoom("r1", new PlanRect(110, 120, 80, 60), [wall.Id])],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [assessment])) with
        {
            Quality = UsableQuality()
        };

        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.False(readiness.ReadyForGeometryImport);
        Assert.True(readiness.RequiresReview);
        Assert.Contains("placement.wall_pairs.fragmented_short_pairs_require_review", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("fragmented short parallel wall-pair", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            readiness.Evidence,
            evidence => evidence.Contains("wall/room/opening/evidence entities 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportReadiness_ExposesCoveredAreaBoundaryReviewCode()
    {
        var wall = SyntheticWall("covered-entry-boundary", 100, 148, 220, 148) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Exterior,
            Evidence =
            [
                "wall type exterior: near detected floorplan/wall envelope or local outer boundary"
            ]
        };
        var assessment = new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.SurfacePatternDetail,
            new Confidence(0.82),
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            [
                "wall evidence: outdoor covered-area boundary near 'overbygd' is review-only; thin unlayered local-boundary pair has no distinct structural support for placement"
            ])
        {
            Decision = WallEvidenceDecision.Review
        };
        var result = CreateSyntheticResult(
            walls: [wall],
            rooms: [SyntheticRoom("r1", new PlanRect(130, 170, 80, 60), [wall.Id])],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [assessment])) with
        {
            Quality = UsableQuality()
        };

        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.True(readiness.RequiresReview);
        Assert.Contains("placement.wall_evidence.requires_review", readiness.ReviewIssueCodes);
        Assert.Contains("placement.wall_exterior.covered_area_boundaries_require_review", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.RecommendedActions,
            action => action.Contains("covered-entry", StringComparison.OrdinalIgnoreCase)
                && action.Contains("exterior walls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImportReadiness_BlocksSecondaryStructuralComponentWithoutRoomBoundarySupport()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("secondary-a", 200, 100, 200, 220),
            SyntheticWall("secondary-b", 200, 220, 200, 340)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 200, 100),
                SyntheticNode("n2", 200, 220),
                SyntheticNode("n3", 200, 340)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "secondary-a"),
                SyntheticEdge("e2", "n2", "n3", "secondary-b")
            ],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(198, 100, 4, 240),
                    ["secondary-a", "secondary-b"],
                    ["n1", "n2", "n3"],
                    ["e1", "e2"],
                    ["secondary-a", "secondary-b"],
                    240,
                    Confidence.High,
                    ["synthetic secondary structural linework"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(320, 120, 120, 120), ["room-boundary-anchor"])],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                walls.Select(wall => new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    WallEvidenceCategory.StrongWallBody,
                    wall.Confidence,
                    PlacementReady: true,
                    RequiresReview: false,
                    RejectedAsNoise: false,
                    wall.SourcePrimitiveIds,
                    ["synthetic strong paired wall evidence"])).ToArray())) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.Contains("secondary-a", reasons.Keys);
        Assert.Contains(
            WallPlacementContextGuards.SecondaryStructuralWithoutRoomBoundarySupportReason,
            reasons["secondary-a"]);
        Assert.False(readiness.ReadyForGeometryImport);
        Assert.True(readiness.RequiresReview);
        Assert.Equal("Blocked", readiness.Grade);
        Assert.Contains("placement.import.low_coordinate_ready_ratio", readiness.BlockingIssueCodes);
    }

    [Fact]
    public void PlacementExporter_AllowsTrustedPairedSecondaryWallBodyChainWithoutRoomBoundarySupport()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("secondary-a", 200, 100, 200, 220) with
            {
                DetectionKind = WallDetectionKind.ParallelLinePair,
                WallType = WallType.Interior,
                Evidence = ["parallel wall-face pair", "wall evidence: strong double-edge wall body"]
            },
            SyntheticWall("secondary-b", 200, 220, 200, 340) with
            {
                DetectionKind = WallDetectionKind.ParallelLinePair,
                WallType = WallType.Interior,
                Evidence = ["parallel wall-face pair", "wall evidence: strong double-edge wall body"]
            }
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 200, 100),
                SyntheticNode("n2", 200, 220),
                SyntheticNode("n3", 200, 340)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "secondary-a"),
                SyntheticEdge("e2", "n2", "n3", "secondary-b")
            ],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(198, 100, 4, 240),
                    ["secondary-a", "secondary-b"],
                    ["n1", "n2", "n3"],
                    ["e1", "e2"],
                    ["secondary-a", "secondary-b"],
                    240,
                    Confidence.High,
                    ["synthetic long paired secondary wall body chain"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(320, 120, 120, 120), ["room-boundary-anchor"])],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                walls.Select(wall => new WallEvidenceWallAssessment(
                    wall.Id,
                    wall.PageNumber,
                    wall.Bounds,
                    WallEvidenceCategory.StrongWallBody,
                    wall.Confidence,
                    PlacementReady: true,
                    RequiresReview: false,
                    RejectedAsNoise: false,
                    wall.SourcePrimitiveIds,
                    ["parallel wall-face pair", "wall evidence: strong double-edge wall body"])).ToArray())) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var secondaryWalls = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Where(item => item.GetProperty("id").GetString()?.StartsWith("secondary-", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.DoesNotContain("secondary-a", reasons.Keys);
        Assert.DoesNotContain("secondary-b", reasons.Keys);
        Assert.All(secondaryWalls, wall =>
        {
            Assert.True(wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
            Assert.Equal(JsonValueKind.Null, wall.GetProperty("placementOmission").ValueKind);
        });
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("secondary_without_room_boundary_support", out _));
    }

    [Fact]
    public void PlacementExporter_BlocksSecondaryWallBodyOverlappingStairObjectLinework()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var wall = SyntheticWall("stair-overlap-secondary", 307, 420, 307, 566) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = ["parallel wall-face pair", "wall evidence: strong double-edge wall body"]
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 307, 420), SyntheticNode("n2", 307, 566)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(303, 420, 8, 146),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    [wall.Id],
                    146,
                    Confidence.High,
                    ["anchored single paired-wall body"])
            ]);
        var stairObject = new ObjectCandidate(
            "stair-object",
            1,
            ObjectCandidateKind.Stair,
            new PlanRect(240, 442, 65, 152),
            Confidence.High)
        {
            Category = ObjectCategory.Stair,
            SourceKind = ObjectCandidateSourceKind.WallComponentIsland,
            SourceWallComponentKind = WallGraphComponentKind.ObjectLikeIsland,
            Evidence = ["nearby text 'Trapperom' matches 'trapp'"]
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(380, 120, 120, 120), ["room-boundary-anchor"])],
            objects: [stairObject],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [StrongPairedWallEvidence(wall, "both endpoints supported by structural context")])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.Contains(wall.Id, reasons.Keys);
        Assert.Contains(
            WallPlacementContextGuards.SecondaryStructuralObjectLineworkWithoutRoomBoundarySupportReason,
            reasons[wall.Id]);
        Assert.False(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(
            "secondary_object_linework_without_room_boundary_support",
            exportedWall.GetProperty("placementOmission").GetProperty("code").GetString());
        Assert.Equal(
            1,
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .GetProperty("secondary_object_linework_without_room_boundary_support")
                .GetInt32());
    }

    [Fact]
    public void PlacementExporter_AllowsStairAdjacentSecondaryWallWhenItIsRoomBoundary()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var wall = SyntheticWall("stair-room-boundary", 307, 420, 307, 566) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            Evidence = ["parallel wall-face pair", "wall evidence: strong double-edge wall body"]
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 307, 420), SyntheticNode("n2", 307, 566)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(303, 420, 8, 146),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    [wall.Id],
                    146,
                    Confidence.High,
                    ["anchored single paired-wall body"])
            ]);
        var stairObject = new ObjectCandidate(
            "stair-object",
            1,
            ObjectCandidateKind.Stair,
            new PlanRect(240, 442, 65, 152),
            Confidence.High)
        {
            Category = ObjectCategory.Stair,
            SourceKind = ObjectCandidateSourceKind.WallComponentIsland,
            SourceWallComponentKind = WallGraphComponentKind.ObjectLikeIsland,
            Evidence = ["nearby text 'Trapperom' matches 'trapp'"]
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(280, 420, 160, 146), [wall.Id])],
            objects: [stairObject],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [StrongPairedWallEvidence(wall, "both endpoints supported by structural context")])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.DoesNotContain(wall.Id, reasons.Keys);
        Assert.True(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exportedWall.GetProperty("placementOmission").ValueKind);
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("secondary_object_linework_without_room_boundary_support", out _));
    }

    [Fact]
    public void PlacementExporter_BlocksOverSourcedSecondaryDetailLineworkDespiteRoomBoundary()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var noisySourceIds = Enumerable.Range(1, 36)
            .Select(index => $"detail-source-{index}")
            .ToArray();
        var wall = SyntheticWall("over-sourced-detail-room-boundary", 307, 420, 307, 566) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = noisySourceIds,
            Evidence =
            [
                "parallel wall-face pair",
                "wall evidence: strong double-edge wall body",
                "first face merged 22 fragments",
                "second face collapsed 4 duplicate or near-duplicate wall line primitive(s)"
            ]
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 307, 420), SyntheticNode("n2", 307, 566)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(303, 420, 8, 146),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    noisySourceIds,
                    146,
                    Confidence.High,
                    ["anchored single paired-wall body"])
            ]);
        var detailObject = new ObjectCandidate(
            "generic-detail-object",
            1,
            ObjectCandidateKind.Symbol,
            new PlanRect(241, 442, 65, 152),
            Confidence.Medium)
        {
            Category = ObjectCategory.GenericSymbol,
            SourceKind = ObjectCandidateSourceKind.WallComponentIsland,
            SourceWallComponentKind = WallGraphComponentKind.ObjectLikeIsland,
            Evidence = ["wall graph component island exported as generic object/detail linework"]
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(280, 420, 160, 146), [wall.Id])],
            objects: [detailObject],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [StrongPairedWallEvidence(wall, "both endpoints supported by structural context")])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.Contains(wall.Id, reasons.Keys);
        Assert.Contains(
            WallPlacementContextGuards.SecondaryStructuralOverSourcedDetailLineworkReason,
            reasons[wall.Id]);
        Assert.False(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(
            "secondary_over_sourced_detail_linework",
            exportedWall.GetProperty("placementOmission").GetProperty("code").GetString());
        Assert.Equal(
            1,
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .GetProperty("secondary_over_sourced_detail_linework")
                .GetInt32());
    }

    [Fact]
    public void PlacementExporter_AllowsOverSourcedSecondaryStrongWallWithTwoSidedRoomEvidence()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var noisySourceIds = Enumerable.Range(1, 36)
            .Select(index => $"detail-source-{index}")
            .ToArray();
        var wall = SyntheticWall("over-sourced-two-sided-room-wall", 307, 420, 307, 566) with
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            WallType = WallType.Interior,
            SourcePrimitiveIds = noisySourceIds,
            Evidence =
            [
                "parallel wall-face pair",
                "wall evidence: strong double-edge wall body",
                "first face merged 22 fragments",
                "second face collapsed 4 duplicate or near-duplicate wall line primitive(s)",
                "wall type refined interior: detected room evidence on both sides"
            ]
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 307, 420), SyntheticNode("n2", 307, 566)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "secondary-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(303, 420, 8, 146),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    noisySourceIds,
                    146,
                    Confidence.High,
                    ["anchored single paired-wall body"])
            ]);
        var detailObject = new ObjectCandidate(
            "generic-detail-object",
            1,
            ObjectCandidateKind.Symbol,
            new PlanRect(241, 442, 65, 152),
            Confidence.Medium)
        {
            Category = ObjectCategory.GenericSymbol,
            SourceKind = ObjectCandidateSourceKind.WallComponentIsland,
            SourceWallComponentKind = WallGraphComponentKind.ObjectLikeIsland,
            Evidence = ["wall graph component island exported as generic object/detail linework"]
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms:
            [
                SyntheticRoom("r1", new PlanRect(280, 420, 27, 146), [wall.Id]),
                SyntheticRoom("r2", new PlanRect(307, 420, 133, 146), [wall.Id])
            ],
            objects: [detailObject],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [StrongPairedWallEvidence(wall, "both endpoints supported by structural context")])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.DoesNotContain(wall.Id, reasons.Keys);
        Assert.True(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exportedWall.GetProperty("placementOmission").ValueKind);
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("secondary_over_sourced_detail_linework", out _));
    }

    [Fact]
    public void PlacementExporter_BlocksFragmentMergedInteriorWithoutRoomBoundarySupport()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var roomBoundary = SyntheticWall("room-boundary", 120, 100, 260, 100);
        var suspiciousWall = SyntheticWall("fragmented-interior-detail", 180, 180, 310, 180) with
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(1, 24).Select(index => $"fragment-source-{index}").ToArray(),
            Evidence =
            [
                "merged collinear wall fragments",
                "layer (unlayered) classified Unknown (0.35)",
                "layer evidence: no strong layer name or geometry evidence"
            ],
            FragmentEvidence = new WallFragmentEvidence(
                24,
                2.8,
                2.8,
                0,
                0.024,
                RequiresGeometryReview: false,
                ["fragment geometry: 24 fragment(s)"])
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 120, 100),
                SyntheticNode("n2", 260, 100),
                SyntheticNode("n3", 180, 180),
                SyntheticNode("n4", 310, 180)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", roomBoundary.Id),
                SyntheticEdge("e2", "n3", "n4", suspiciousWall.Id)
            ],
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(116, 96, 198, 88),
                    [roomBoundary.Id, suspiciousWall.Id],
                    ["n1", "n2", "n3", "n4"],
                    ["e1", "e2"],
                    [roomBoundary.Id, suspiciousWall.Id],
                    270,
                    Confidence.High,
                    ["synthetic main structural component"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [roomBoundary, suspiciousWall],
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(120, 100, 140, 120), [roomBoundary.Id])],
            wallEvidenceMap: new WallEvidenceMap(
                [],
                [],
                [
                    StrongPairedWallEvidence(roomBoundary, "both endpoints supported by structural context"),
                    new WallEvidenceWallAssessment(
                        suspiciousWall.Id,
                        suspiciousWall.PageNumber,
                        suspiciousWall.Bounds,
                        WallEvidenceCategory.MediumWallBody,
                        suspiciousWall.Confidence,
                        PlacementReady: true,
                        RequiresReview: false,
                        RejectedAsNoise: false,
                        suspiciousWall.SourcePrimitiveIds,
                        ["wall evidence: medium wall body from wall-like layer, length, or structural context"])
                    {
                        Decision = WallEvidenceDecision.Accept
                    }
                ])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == suspiciousWall.Id);

        Assert.Contains(suspiciousWall.Id, reasons.Keys);
        Assert.Contains(
            WallPlacementContextGuards.FragmentMergedInteriorWithoutRoomBoundarySupportReason,
            reasons[suspiciousWall.Id]);
        Assert.False(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(
            "fragmented_interior_without_room_boundary_support",
            exportedWall.GetProperty("placementOmission").GetProperty("code").GetString());
        Assert.Equal(
            1,
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .GetProperty("fragmented_interior_without_room_boundary_support")
                .GetInt32());
    }

    [Fact]
    public void PlacementExporter_AllowsFragmentMergedInteriorWhenItIsRoomBoundary()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var wall = SyntheticWall("fragmented-room-boundary", 180, 180, 310, 180) with
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(1, 24).Select(index => $"fragment-source-{index}").ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                24,
                2.8,
                2.8,
                0,
                0.024,
                RequiresGeometryReview: false,
                ["fragment geometry: 24 fragment(s)"])
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 180, 180), SyntheticNode("n2", 310, 180)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(176, 176, 138, 8),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    [wall.Id],
                    130,
                    Confidence.High,
                    ["synthetic main structural component"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(180, 120, 130, 120), [wall.Id])],
            wallEvidenceMap: new WallEvidenceMap(
                [],
                [],
                [
                    new WallEvidenceWallAssessment(
                        wall.Id,
                        wall.PageNumber,
                        wall.Bounds,
                        WallEvidenceCategory.MediumWallBody,
                        wall.Confidence,
                        PlacementReady: true,
                        RequiresReview: false,
                        RejectedAsNoise: false,
                        wall.SourcePrimitiveIds,
                        ["wall evidence: medium wall body from wall-like layer, length, or structural context"])
                    {
                        Decision = WallEvidenceDecision.Accept
                    }
                ])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.DoesNotContain(wall.Id, reasons.Keys);
        Assert.True(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exportedWall.GetProperty("placementOmission").ValueKind);
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("fragmented_interior_without_room_boundary_support", out _));
    }

    [Fact]
    public void PlacementExporter_AllowsFragmentMergedInteriorWhenAlignedToReliableRoomBoundary()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var wall = SyntheticWall("fragmented-geometric-room-boundary", 180, 180, 310, 180) with
        {
            DetectionKind = WallDetectionKind.FragmentMerged,
            WallType = WallType.Interior,
            SourcePrimitiveIds = Enumerable.Range(1, 9).Select(index => $"fragment-source-{index}").ToArray(),
            FragmentEvidence = new WallFragmentEvidence(
                3,
                0,
                0,
                6,
                0,
                RequiresGeometryReview: false,
                ["fragment geometry: 3 fragment(s)", "fragment geometry collapsed 6 duplicate primitive(s)"])
        };
        var room = new RoomRegion(
            "r1",
            1,
            new PlanRect(180, 120, 130, 120),
            [
                new PlanPoint(180, 120),
                new PlanPoint(310, 120),
                new PlanPoint(310, 240),
                new PlanPoint(180, 240)
            ],
            [],
            Confidence.High)
        {
            Evidence = ["semantic room boundary inferred from nearby walls synthetic"],
            Label = "R1"
        };
        var wallGraph = new WallGraph(
            [SyntheticNode("n1", 180, 180), SyntheticNode("n2", 310, 180)],
            [SyntheticEdge("e1", "n1", "n2", wall.Id)],
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(176, 176, 138, 8),
                    [wall.Id],
                    ["n1", "n2"],
                    ["e1"],
                    [wall.Id],
                    130,
                    Confidence.High,
                    ["synthetic main structural component"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: [wall],
            wallGraph: wallGraph,
            rooms: [room],
            wallEvidenceMap: new WallEvidenceMap(
                [],
                [],
                [
                    new WallEvidenceWallAssessment(
                        wall.Id,
                        wall.PageNumber,
                        wall.Bounds,
                        WallEvidenceCategory.MediumWallBody,
                        wall.Confidence,
                        PlacementReady: true,
                        RequiresReview: false,
                        RejectedAsNoise: false,
                        wall.SourcePrimitiveIds,
                        ["wall evidence: clean fragment-merged interior room boundary promoted after room refinement confirmed it belongs to a detected room boundary"])
                    {
                        Decision = WallEvidenceDecision.Accept
                    }
                ])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var exportedWall = document.RootElement.GetProperty("walls").EnumerateArray().Single(item =>
            item.GetProperty("id").GetString() == wall.Id);

        Assert.DoesNotContain(wall.Id, reasons.Keys);
        Assert.True(exportedWall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.Equal(JsonValueKind.Null, exportedWall.GetProperty("placementOmission").ValueKind);
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("fragmented_interior_without_room_boundary_support", out _));
    }

    [Fact]
    public void PlacementExporter_AllowsTrustedCompactPairedSecondaryReturnWithoutRoomBoundarySupport()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("secondary-return-a", 200, 100, 280, 100) with
            {
                DetectionKind = WallDetectionKind.ParallelLinePair,
                WallType = WallType.Interior,
                Evidence = ["parallel wall-face pair", "pair score 0.91", "wall evidence: strong double-edge wall body"]
            },
            SyntheticWall("secondary-return-b", 280, 100, 280, 158) with
            {
                DetectionKind = WallDetectionKind.ParallelLinePair,
                WallType = WallType.Interior,
                Evidence = ["parallel wall-face pair", "pair score 0.65", "wall evidence: strong double-edge wall body"]
            },
            SyntheticWall("room-boundary-anchor", 320, 120, 440, 120)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 200, 100),
                SyntheticNode("n2", 280, 100),
                SyntheticNode("n3", 280, 158)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "secondary-return-a"),
                SyntheticEdge("e2", "n2", "n3", "secondary-return-b")
            ],
            [
                new WallGraphComponent(
                    "secondary-return-component",
                    1,
                    WallGraphComponentKind.SecondaryStructural,
                    new PlanRect(196, 96, 88, 66),
                    ["secondary-return-a", "secondary-return-b"],
                    ["n1", "n2", "n3"],
                    ["e1", "e2"],
                    ["secondary-return-a", "secondary-return-b"],
                    138,
                    Confidence.High,
                    ["synthetic compact paired secondary wall return"])
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: [SyntheticRoom("r1", new PlanRect(320, 120, 120, 120), ["room-boundary-anchor"])],
            wallEvidenceMap: new WallEvidenceMap(
                Array.Empty<WallEvidenceSegment>(),
                Array.Empty<WallEvidenceBand>(),
                [
                    StrongPairedWallEvidence(walls[0], "both endpoints supported by structural context"),
                    StrongPairedWallEvidence(walls[1], "one endpoint supported by structural context"),
                    StrongPairedWallEvidence(walls[2], "both endpoints supported by structural context")
                ])) with
        {
            Quality = UsableQuality()
        };

        var reasons = WallPlacementContextGuards.BuildReviewReasons(result);
        var placementJson = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var document = JsonDocument.Parse(placementJson);
        var secondaryWalls = document.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Where(item => item.GetProperty("id").GetString()?.StartsWith("secondary-return-", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.DoesNotContain("secondary-return-a", reasons.Keys);
        Assert.DoesNotContain("secondary-return-b", reasons.Keys);
        Assert.All(secondaryWalls, wall =>
        {
            Assert.True(wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean());
            Assert.Equal(JsonValueKind.Null, wall.GetProperty("placementOmission").ValueKind);
        });
        Assert.False(
            document.RootElement.GetProperty("summary").GetProperty("wallPlacementOmissionCounts")
                .TryGetProperty("secondary_without_room_boundary_support", out _));
    }

    [Fact]
    public void PlacementExporter_DoesNotTreatExcludedWallComponentsAsImportReviewEntities()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100),
            SyntheticWall("object-wall-1", 360, 120, 390, 120),
            SyntheticWall("object-wall-2", 390, 120, 390, 150)
        };
        var nodes = new[]
        {
            SyntheticNode("n1", 100, 100),
            SyntheticNode("n2", 250, 100),
            SyntheticNode("n3", 250, 250),
            SyntheticNode("n4", 100, 250),
            SyntheticNode("o1", 360, 120),
            SyntheticNode("o2", 390, 120),
            SyntheticNode("o3", 390, 150)
        };
        var edges = new[]
        {
            SyntheticEdge("e1", "n1", "n2", "w1"),
            SyntheticEdge("e2", "n2", "n3", "w2"),
            SyntheticEdge("e3", "n3", "n4", "w3"),
            SyntheticEdge("e4", "n4", "n1", "w4"),
            SyntheticEdge("oe1", "o1", "o2", "object-wall-1"),
            SyntheticEdge("oe2", "o2", "o3", "object-wall-2")
        };
        var wallGraph = new WallGraph(
            nodes,
            edges,
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(100, 100, 150, 150),
                    ["w1", "w2", "w3", "w4"],
                    ["n1", "n2", "n3", "n4"],
                    ["e1", "e2", "e3", "e4"],
                    ["w1", "w2", "w3", "w4"],
                    600,
                    Confidence.High,
                    ["main structural component"]),
                new WallGraphComponent(
                    "component-object",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    new PlanRect(360, 120, 30, 30),
                    ["object-wall-1", "object-wall-2"],
                    ["o1", "o2", "o3"],
                    ["oe1", "oe2"],
                    ["object-wall-1", "object-wall-2"],
                    60,
                    Confidence.Medium,
                    ["object-like wall component"],
                    ExcludedFromStructuralTopology: true)
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms);
        var quality = PlanScanQualityAnalyzer.Analyze(result);
        result = result with { Quality = quality };

        using var document = JsonDocument.Parse(
            PlanPlacementJsonExporter.Serialize(
                result,
                new PlanPlacementJsonExportOptions { WriteIndented = false }));
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        var readiness = summary.GetProperty("importReadiness");

        Assert.Equal(2, summary.GetProperty("excludedWallCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("reviewRequiredEntityCount").GetInt32());
        Assert.False(readiness.GetProperty("requiresReview").GetBoolean());
        Assert.True(readiness.GetProperty("readyForGeometryImport").GetBoolean());
        Assert.Empty(readiness.GetProperty("reviewIssueCodes").EnumerateArray());
        Assert.Contains(
            root.GetProperty("walls").EnumerateArray(),
            wall => wall.GetProperty("id").GetString() == "object-wall-1"
                && wall.GetProperty("excludedFromStructuralTopology").GetBoolean()
                && wall.GetProperty("reliability").GetProperty("requiresReview").GetBoolean()
                && wall.GetProperty("reliability").GetProperty("reasons")
                    .EnumerateArray()
                    .Any(reason => reason.GetString() == "wall belongs to compact object-like linework component"));
    }

    [Fact]
    public void ImportReadiness_DoesNotPenalizeExcludedObjectLikeWallComponents()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100),
            SyntheticWall("object-wall-1", 360, 120, 390, 120, confidence: new Confidence(0.25)),
            SyntheticWall("object-wall-2", 390, 120, 390, 150, confidence: new Confidence(0.25))
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250),
                SyntheticNode("o1", 360, 120),
                SyntheticNode("o2", 390, 120),
                SyntheticNode("o3", 390, 150)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4"),
                SyntheticEdge("oe1", "o1", "o2", "object-wall-1"),
                SyntheticEdge("oe2", "o2", "o3", "object-wall-2")
            ],
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(100, 100, 150, 150),
                    ["w1", "w2", "w3", "w4"],
                    ["n1", "n2", "n3", "n4"],
                    ["e1", "e2", "e3", "e4"],
                    ["w1", "w2", "w3", "w4"],
                    600,
                    Confidence.High,
                    ["main structural component"]),
                new WallGraphComponent(
                    "component-object",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    new PlanRect(360, 120, 30, 30),
                    ["object-wall-1", "object-wall-2"],
                    ["o1", "o2", "o3"],
                    ["oe1", "oe2"],
                    ["object-wall-1", "object-wall-2"],
                    60,
                    Confidence.Low,
                    ["object-like wall component"],
                    ExcludedFromStructuralTopology: true)
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var baseResult = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms);
        var quality = PlanScanQualityAnalyzer.Analyze(baseResult);
        var wallDetector = Assert.Single(quality.Detectors, detector => detector.Name == "walls");

        Assert.Equal(4, wallDetector.ItemCount);
        Assert.Equal(0, wallDetector.LowConfidenceCount);
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.no_walls");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.low_detector_confidence");

        var result = baseResult with
        {
            Quality = UsableQuality()
        };

        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.True(readiness.ReadyForGeometryImport);
        Assert.True(readiness.ReadyForMetricImport);
        Assert.DoesNotContain("placement.import.low_coordinate_ready_ratio", readiness.BlockingIssueCodes);
        Assert.DoesNotContain("placement.import.low_metric_ready_ratio", readiness.BlockingIssueCodes);
        Assert.DoesNotContain("placement.import.no_walls", readiness.BlockingIssueCodes);
        Assert.Contains(
            readiness.Evidence,
            evidence => evidence.Contains("coordinate readiness ratio 1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_TreatsExcludedObjectLikeWallComponentsAsNonStructural()
    {
        var walls = new[]
        {
            SyntheticWall("object-wall-1", 360, 120, 390, 120, confidence: new Confidence(0.25)),
            SyntheticWall("object-wall-2", 390, 120, 390, 150, confidence: new Confidence(0.25))
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("o1", 360, 120),
                SyntheticNode("o2", 390, 120),
                SyntheticNode("o3", 390, 150)
            ],
            [
                SyntheticEdge("oe1", "o1", "o2", "object-wall-1"),
                SyntheticEdge("oe2", "o2", "o3", "object-wall-2")
            ],
            [
                new WallGraphComponent(
                    "component-object",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    new PlanRect(360, 120, 30, 30),
                    ["object-wall-1", "object-wall-2"],
                    ["o1", "o2", "o3"],
                    ["oe1", "oe2"],
                    ["object-wall-1", "object-wall-2"],
                    60,
                    Confidence.Low,
                    ["object-like wall component"],
                    ExcludedFromStructuralTopology: true)
            ]);
        var result = CreateSyntheticResult(walls: walls, wallGraph: wallGraph);

        var quality = PlanScanQualityAnalyzer.Analyze(result);
        var wallDetector = Assert.Single(quality.Detectors, detector => detector.Name == "walls");

        Assert.Equal(0, wallDetector.ItemCount);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "quality.no_walls"
            && issue.Message.Contains("structural", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.no_rooms_from_walls");
    }

    [Fact]
    public void Analyze_TreatsRejectedWallEvidenceAsNonStructural()
    {
        var walls = new[]
        {
            SyntheticWall("door-leaf-detail", 200, 120, 260, 120, confidence: new Confidence(0.25)),
            SyntheticWall("fixture-edge-detail", 260, 120, 260, 170, confidence: new Confidence(0.25))
        };
        var evidenceMap = new WallEvidenceMap(
            [],
            [],
            walls.Select(wall => RejectedWallEvidence(wall, WallEvidenceCategory.DoorOrOpeningSymbol)).ToArray());
        var result = CreateSyntheticResult(walls: walls, wallEvidenceMap: evidenceMap);

        var quality = PlanScanQualityAnalyzer.Analyze(result);
        var wallDetector = Assert.Single(quality.Detectors, detector => detector.Name == "walls");

        Assert.Equal(0, wallDetector.ItemCount);
        Assert.Contains(quality.Issues, issue =>
            issue.Code == "quality.no_walls"
            && issue.Message.Contains("structural", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.no_rooms_from_walls");
    }

    [Fact]
    public void ImportReadiness_DoesNotPenalizeRejectedWallEvidenceWalls()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var structuralWalls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100)
        };
        var rejectedWalls = new[]
        {
            SyntheticWall("door-leaf-detail", 180, 116, 236, 116, confidence: new Confidence(0.25)),
            SyntheticWall("fixture-edge-detail", 210, 145, 240, 145, confidence: new Confidence(0.25))
        };
        var walls = structuralWalls.Concat(rejectedWalls).ToArray();
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4")
            ],
            [
                new WallGraphComponent(
                    "component-main",
                    1,
                    WallGraphComponentKind.MainStructural,
                    new PlanRect(100, 100, 150, 150),
                    ["w1", "w2", "w3", "w4"],
                    ["n1", "n2", "n3", "n4"],
                    ["e1", "e2", "e3", "e4"],
                    ["w1", "w2", "w3", "w4"],
                    600,
                    Confidence.High,
                    ["main structural component"])
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"])
        };
        var evidenceMap = new WallEvidenceMap(
            [],
            [],
            rejectedWalls.Select(wall => RejectedWallEvidence(wall, WallEvidenceCategory.DoorOrOpeningSymbol)).ToArray());
        var baseResult = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            rooms: rooms,
            wallEvidenceMap: evidenceMap);

        var quality = PlanScanQualityAnalyzer.Analyze(baseResult);
        var wallDetector = Assert.Single(quality.Detectors, detector => detector.Name == "walls");

        Assert.Equal(4, wallDetector.ItemCount);
        Assert.Equal(0, wallDetector.LowConfidenceCount);

        var result = baseResult with
        {
            Quality = UsableQuality()
        };
        var readiness = PlanImportReadiness.FromScanResult(result);

        Assert.True(readiness.ReadyForGeometryImport);
        Assert.True(readiness.ReadyForMetricImport);
        Assert.False(readiness.RequiresReview);
        Assert.DoesNotContain("placement.import.low_coordinate_ready_ratio", readiness.BlockingIssueCodes);
        Assert.DoesNotContain("placement.import.low_metric_ready_ratio", readiness.BlockingIssueCodes);
        Assert.DoesNotContain("placement.wall_evidence.requires_review", readiness.ReviewIssueCodes);
        Assert.Contains(
            readiness.Evidence,
            evidence => evidence.Contains("coordinate readiness ratio 1", StringComparison.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(
            PlanPlacementJsonExporter.Serialize(
                result,
                new PlanPlacementJsonExportOptions { WriteIndented = false }));
        var root = document.RootElement;
        var summary = root.GetProperty("summary");

        Assert.Equal(6, summary.GetProperty("wallCount").GetInt32());
        Assert.Equal(4, summary.GetProperty("structuralWallCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("excludedWallCount").GetInt32());
        Assert.True(summary.GetProperty("coordinateReadyRatio").GetDouble() < 1);
        Assert.False(summary.GetProperty("importReadiness").GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            JsonStrings(summary.GetProperty("importReadiness").GetProperty("evidence")),
            evidence => evidence.Contains(
                "structural import coordinate readiness ratio 1 (5/5 structural import entities)",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            root.GetProperty("walls").EnumerateArray(),
            wall => wall.GetProperty("id").GetString() == "door-leaf-detail"
                && wall.GetProperty("excludedFromStructuralTopology").GetBoolean()
                && !wall.GetProperty("reliability").GetProperty("readyForCoordinatePlacement").GetBoolean()
                && wall.GetProperty("reliability").GetProperty("reasons")
                    .EnumerateArray()
                    .Any(reason => reason.GetString() == "wall evidence rejected as non-wall/noise (DoorOrOpeningSymbol)"));
    }

    [Fact]
    public void Analyze_DoesNotFlagScanRiskForExcludedObjectLikeWallComponents()
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 1000, 700), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 700), Confidence.High),
            new SheetRegion("title", 1, RegionKind.TitleBlock, new PlanRect(700, 0, 300, 700), Confidence.High)
        };
        var walls = Enumerable.Range(0, 12)
            .Select(index => SyntheticWall(
                $"object-wall-{index + 1}",
                730 + (index % 3) * 24,
                80 + (index / 3) * 24,
                744 + (index % 3) * 24,
                80 + (index / 3) * 24,
                withEvidence: false,
                confidence: Confidence.Low))
            .ToArray();
        var nodes = walls
            .SelectMany((wall, index) => new[]
            {
                SyntheticNode($"n{index + 1}a", wall.CenterLine.Start.X, wall.CenterLine.Start.Y),
                SyntheticNode($"n{index + 1}b", wall.CenterLine.End.X, wall.CenterLine.End.Y)
            })
            .ToArray();
        var edges = walls
            .Select((wall, index) => SyntheticEdge(
                $"e{index + 1}",
                nodes[index * 2].Id,
                nodes[(index * 2) + 1].Id,
                wall.Id))
            .ToArray();
        var wallGraph = new WallGraph(
            nodes,
            edges,
            [
                new WallGraphComponent(
                    "component-object-title",
                    1,
                    WallGraphComponentKind.ObjectLikeIsland,
                    new PlanRect(730, 80, 70, 96),
                    walls.Select(wall => wall.Id).ToArray(),
                    nodes.Select(node => node.Id).ToArray(),
                    edges.Select(edge => edge.Id).ToArray(),
                    walls.SelectMany(wall => wall.SourcePrimitiveIds).ToArray(),
                    walls.Sum(wall => wall.DrawingLength),
                    Confidence.Low,
                    ["title-block object-like linework"],
                    ExcludedFromStructuralTopology: true)
            ]);
        var result = CreateSyntheticResult(
            regions: regions,
            walls: walls,
            wallGraph: wallGraph,
            diagnostics:
            [
                new PlanDiagnostic(
                    "wall_graph.surface_pattern_wall_overlap.review",
                    DiagnosticSeverity.Warning,
                    "wall-graph",
                    "A wall candidate overlaps a non-structural surface/detail pattern.")
                {
                    PageNumber = 1,
                    Region = new PlanRect(730, 80, 70, 96),
                    Confidence = Confidence.Medium,
                    SourcePrimitiveIds = ["object-wall-1"],
                    Properties = new Dictionary<string, string>
                    {
                        ["wallId"] = "object-wall-1",
                        ["surfacePatternId"] = "pattern-title-detail",
                        ["wallOverlapRatio"] = "0.92"
                    }
                }
            ]);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        Assert.Contains(quality.Issues, issue => issue.Code == "quality.no_walls");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.sheet_contamination");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.geometry_outside_main_region");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.fragmented_wall_graph");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.weak_source_provenance");
        Assert.DoesNotContain(quality.Issues, issue => issue.Code == "quality.scan_risk.surface_pattern_wall_overlap");
    }

    [Fact]
    public void Analyze_FlagsHighDimensionScaleSpread()
    {
        var measurement = new MeasurementConsistencyReport(
            true,
            15,
            15,
            5,
            Confidence.High,
            new[]
            {
                new MeasurementConsistencyCheck(
                    "dimension-a",
                    1,
                    MeasurementConsistencyStatus.Consistent,
                    1500,
                    100,
                    15,
                    15,
                    1500,
                    0,
                    0,
                    Confidence.High,
                    ["dim-text-a", "dim-line-a"],
                    ["synthetic consistent dimension"]),
                new MeasurementConsistencyCheck(
                    "dimension-b",
                    1,
                    MeasurementConsistencyStatus.Outlier,
                    7500,
                    100,
                    75,
                    15,
                    1500,
                    6000,
                    4,
                    Confidence.High,
                    ["dim-text-b", "dim-line-b"],
                    ["synthetic high-spread dimension"])
            });
        var result = CreateSyntheticResult(
            regions:
            [
                new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
                new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
            ],
            walls:
            [
                SyntheticWall("w1", 100, 100, 250, 100),
                SyntheticWall("w2", 250, 100, 250, 250),
                SyntheticWall("w3", 250, 250, 100, 250),
                SyntheticWall("w4", 100, 250, 100, 100)
            ],
            measurementConsistency: measurement);

        var quality = PlanScanQualityAnalyzer.Analyze(result);

        var issue = Assert.Single(quality.Issues, item => item.Code == "quality.dimension_scale_spread_high");
        Assert.Equal(DiagnosticSeverity.Warning, issue.Severity);
        Assert.Equal("2", issue.Properties["checkedCount"]);
        Assert.Equal("5", issue.Properties["spreadRatio"]);
        Assert.Equal("15", issue.Properties["medianDimensionMillimetersPerDrawingUnit"]);
        Assert.Equal("15", issue.Properties["selectedMillimetersPerDrawingUnit"]);
    }

    private static PlanDocument CreateRoomDocument() =>
        new(
            "quality-room",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(500, 400),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(100, 100), new PlanPoint(300, 100)),
                        Wall("wall-right", new PlanPoint(300, 100), new PlanPoint(300, 250)),
                        Wall("wall-bottom", new PlanPoint(300, 250), new PlanPoint(100, 250)),
                        Wall("wall-left", new PlanPoint(100, 250), new PlanPoint(100, 100)),
                        new TextPrimitive("OFFICE", new PlanRect(170, 160, 50, 12)) { SourceId = "room-label", Layer = "A-ROOM" },
                        new TextPrimitive("SCALE: 1:100", new PlanRect(340, 330, 90, 12)) { SourceId = "scale-text", Layer = "A-ANNO" }
                    })
            });

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

    private static TwoRoomOpeningScenario CreateTwoRoomOpeningScenario(bool withSideAwareLinks)
    {
        var regions = new[]
        {
            new SheetRegion("sheet", 1, RegionKind.Sheet, new PlanRect(0, 0, 600, 400), Confidence.High),
            new SheetRegion("main", 1, RegionKind.MainFloorPlan, new PlanRect(0, 0, 600, 400), Confidence.High)
        };
        var walls = new[]
        {
            SyntheticWall("w1", 100, 100, 250, 100),
            SyntheticWall("w2", 250, 100, 250, 250),
            SyntheticWall("w3", 250, 250, 100, 250),
            SyntheticWall("w4", 100, 250, 100, 100),
            SyntheticWall("w5", 260, 100, 410, 100),
            SyntheticWall("w6", 410, 100, 410, 250),
            SyntheticWall("w7", 410, 250, 260, 250),
            SyntheticWall("w8", 260, 250, 260, 100)
        };
        var wallGraph = new WallGraph(
            [
                SyntheticNode("n1", 100, 100),
                SyntheticNode("n2", 250, 100),
                SyntheticNode("n3", 250, 250),
                SyntheticNode("n4", 100, 250),
                SyntheticNode("n5", 260, 100),
                SyntheticNode("n6", 410, 100),
                SyntheticNode("n7", 410, 250),
                SyntheticNode("n8", 260, 250)
            ],
            [
                SyntheticEdge("e1", "n1", "n2", "w1"),
                SyntheticEdge("e2", "n2", "n3", "w2"),
                SyntheticEdge("e3", "n3", "n4", "w3"),
                SyntheticEdge("e4", "n4", "n1", "w4"),
                SyntheticEdge("e5", "n5", "n6", "w5"),
                SyntheticEdge("e6", "n6", "n7", "w6"),
                SyntheticEdge("e7", "n7", "n8", "w7"),
                SyntheticEdge("e8", "n8", "n5", "w8")
            ]);
        var rooms = new[]
        {
            SyntheticRoom("r1", new PlanRect(105, 105, 140, 140), ["w1", "w2", "w3", "w4"]),
            SyntheticRoom("r2", new PlanRect(265, 105, 140, 140), ["w5", "w6", "w7", "w8"])
        };
        var openings = Enumerable.Range(0, 4)
            .Select(index => SyntheticRoomLinkedOpening(index + 1, withSideAwareLinks))
            .ToArray();

        return new TwoRoomOpeningScenario(regions, walls, wallGraph, rooms, openings);
    }

    private static OpeningCandidate SyntheticRoomLinkedOpening(int index, bool withSideAwareLinks)
    {
        var y = 118 + (index * 28);
        var centerLine = new PlanLineSegment(new PlanPoint(250, y), new PlanPoint(250, y + 18));
        var midpoint = centerLine.Midpoint;
        var adjacencyId = $"adjacency:o{index}:r1-r2";
        var links = withSideAwareLinks
            ? new[]
            {
                SyntheticRoomConnection(
                    "r1",
                    "R1",
                    adjacencyId,
                    OpeningRoomSide.NegativeNormalSide,
                    midpoint.Translate(-18, 0),
                    midpoint,
                    -18),
                SyntheticRoomConnection(
                    "r2",
                    "R2",
                    adjacencyId,
                    OpeningRoomSide.PositiveNormalSide,
                    midpoint.Translate(18, 0),
                    midpoint,
                    18)
            }
            : Array.Empty<OpeningRoomConnection>();

        return new OpeningCandidate(
            $"o{index}",
            1,
            OpeningType.Door,
            new PlanRect(244, y, 12, 18),
            Confidence.High)
        {
            WallId = "w2",
            AdjacentWallIds = ["w2", "w8"],
            HostWallIds = ["w2"],
            ConnectedRoomIds = ["r1", "r2"],
            ConnectedRoomLabels = ["R1", "R2"],
            ConnectedRoomLinks = links,
            RoomAdjacencyIds = [adjacencyId],
            CenterLine = centerLine,
            Orientation = OpeningOrientation.Vertical,
            Operation = OpeningOperation.PassThrough,
            Placement = SyntheticPlacement("w2", centerLine),
            SourcePrimitiveIds = [$"opening-source-{index}"],
            Evidence = ["synthetic room-linked opening"],
            WidthMillimeters = 900,
            MeasurementScaleGroupId = "document:scale-group:1"
        };
    }

    private static OpeningRoomConnection SyntheticRoomConnection(
        string roomId,
        string roomLabel,
        string adjacencyId,
        OpeningRoomSide side,
        PlanPoint roomSidePoint,
        PlanPoint nearestBoundaryPoint,
        double signedDistance) =>
        new(
            roomId,
            roomLabel,
            RoomUseKind.Unknown,
            [adjacencyId],
            side,
            roomSidePoint,
            nearestBoundaryPoint,
            signedDistance,
            Math.Abs(signedDistance),
            true,
            Confidence.High,
            ["synthetic side-aware opening-room connection"]);

    private static OpeningPlacement SyntheticPlacement(string hostWallId, PlanLineSegment openingLine)
    {
        var referenceLine = new PlanLineSegment(new PlanPoint(250, 100), new PlanPoint(250, 250));
        var startOffset = referenceLine.Start.DistanceTo(openingLine.Start);
        var endOffset = referenceLine.Start.DistanceTo(openingLine.End);
        var centerOffset = referenceLine.Start.DistanceTo(openingLine.Midpoint);
        var footprint = openingLine.Bounds.Inflate(4);
        return new OpeningPlacement(
            hostWallId,
            [hostWallId],
            referenceLine,
            openingLine.Start,
            openingLine.End,
            startOffset,
            endOffset,
            centerOffset,
            openingLine.Length,
            footprint,
            [
                new PlanPoint(footprint.Left, footprint.Top),
                new PlanPoint(footprint.Right, footprint.Top),
                new PlanPoint(footprint.Right, footprint.Bottom),
                new PlanPoint(footprint.Left, footprint.Bottom)
            ],
            new PlanLineSegment(openingLine.Start.Translate(-4, 0), openingLine.Start.Translate(4, 0)),
            new PlanLineSegment(openingLine.End.Translate(-4, 0), openingLine.End.Translate(4, 0)),
            8,
            8,
            startOffset,
            endOffset,
            centerOffset,
            openingLine.Length,
            Math.Clamp(startOffset / referenceLine.Length, 0, 1),
            Math.Clamp(endOffset / referenceLine.Length, 0, 1),
            Math.Clamp(centerOffset / referenceLine.Length, 0, 1),
            new PlanVector(0, 1),
            new PlanVector(1, 0),
            0,
            Confidence.High,
            ["synthetic opening placement"]);
    }

    private static PlanScanResult CreateSyntheticResult(
        IReadOnlyList<SheetRegion>? regions = null,
        IReadOnlyList<WallSegment>? walls = null,
        WallGraph? wallGraph = null,
        IReadOnlyList<RoomRegion>? rooms = null,
        IReadOnlyList<OpeningCandidate>? openings = null,
        IReadOnlyList<ObjectCandidate>? objects = null,
        IReadOnlyList<ObjectCandidateGroup>? objectGroups = null,
        IReadOnlyList<ObjectAggregate>? objectAggregates = null,
        MeasurementConsistencyReport? measurementConsistency = null,
        IReadOnlyList<PlanDiagnostic>? diagnostics = null,
        WallEvidenceMap? wallEvidenceMap = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "synthetic-quality",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(1000, 700),
                        new PlanPrimitive[]
                        {
                            Wall("synthetic-source-line", new PlanPoint(0, 0), new PlanPoint(10, 0))
                        })
                }),
            PlanLayerAnalysis.Empty,
            ReliableCalibration(),
            measurementConsistency ?? new MeasurementConsistencyReport(true, 1, 1, 0, Confidence.High, Array.Empty<MeasurementConsistencyCheck>()),
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            regions ?? Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            walls ?? Array.Empty<WallSegment>(),
            wallGraph ?? WallGraph.Empty,
            rooms ?? Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            openings ?? Array.Empty<OpeningCandidate>(),
            objects ?? Array.Empty<ObjectCandidate>(),
            objectGroups ?? Array.Empty<ObjectCandidateGroup>(),
            objectAggregates ?? Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(now, now, Array.Empty<PipelineStageReport>(), diagnostics ?? Array.Empty<PlanDiagnostic>()))
        {
            WallEvidenceMap = wallEvidenceMap ?? WallEvidenceMap.Empty
        };
    }

    private static PlanCalibration ReliableCalibration() =>
        new(
            PlanMeasurementUnit.PdfPoint,
            PlanMeasurementUnit.Millimeter,
            null,
            1,
            Confidence.High,
            new[]
            {
                new CalibrationEvidence(
                    CalibrationEvidenceKind.ScaleText,
                    1,
                    "scale",
                    "SCALE 1:1",
                    PlanMeasurementUnit.Millimeter,
                    null,
                    1,
                    Confidence.High,
                    "synthetic reliable scale")
            },
            new[]
            {
                new CalibrationScaleGroup(
                    "document:scale-group:1",
                    null,
                    CalibrationScaleScope.Document,
                    PlanMeasurementUnit.PdfPoint,
                    PlanMeasurementUnit.Millimeter,
                    null,
                    1,
                    1,
                    Confidence.High,
                    ["scale"],
                    [],
                    null,
                    ["synthetic reliable scale"])
            });

    private static WallSegment SyntheticWall(
        string id,
        double x1,
        double y1,
        double x2,
        double y2,
        bool withEvidence = true,
        Confidence? confidence = null) =>
        new(id, 1, new PlanLineSegment(new PlanPoint(x1, y1), new PlanPoint(x2, y2)), 8, confidence ?? Confidence.High)
        {
            SourcePrimitiveIds = withEvidence ? [id] : [],
            Evidence = withEvidence ? ["synthetic wall source"] : []
        };

    private static WallEvidenceWallAssessment RejectedWallEvidence(
        WallSegment wall,
        WallEvidenceCategory category) =>
        new(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            category,
            wall.Confidence,
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: true,
            wall.SourcePrimitiveIds,
            ["synthetic rejected wall evidence"])
        {
            Decision = WallEvidenceDecision.Reject
        };

    private static WallEvidenceWallAssessment StrongPairedWallEvidence(
        WallSegment wall,
        string endpointSupportEvidence) =>
        new(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.StrongWallBody,
            wall.Confidence,
            PlacementReady: true,
            RequiresReview: false,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            ["parallel wall-face pair", "wall evidence: strong double-edge wall body", endpointSupportEvidence])
        {
            Decision = WallEvidenceDecision.Accept,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.70,
                0,
                0.70,
                0.50,
                0,
                endpointSupportEvidence.StartsWith("both", StringComparison.OrdinalIgnoreCase) ? 0.20 : 0.10,
                0,
                0,
                0,
                ["strong parallel-face wall pair", endpointSupportEvidence],
                [])
        };

    private static PlanScanQualityReport UsableQuality() =>
        new(
            Confidence.High,
            PlanScanQualityGrade.Usable,
            false,
            1,
            1,
            0,
            0,
            0,
            true,
            0,
            0,
            0,
            Array.Empty<PlanDetectorQualitySummary>(),
            Array.Empty<PlanScanQualityIssue>(),
            new[] { "synthetic usable quality report" });

    private static WallNode SyntheticNode(string id, double x, double y) =>
        new(id, 1, new PlanPoint(x, y), WallNodeKind.Endpoint, 2, [], Confidence.High, ["synthetic wall node"]);

    private static WallEdge SyntheticEdge(string id, string fromNodeId, string toNodeId, string wallId) =>
        new(id, 1, fromNodeId, toNodeId, wallId, Confidence.High);

    private static RoomRegion SyntheticRoom(string id, PlanRect bounds, IReadOnlyList<string> wallIds) =>
        new(
            id,
            1,
            bounds,
            [
                new PlanPoint(bounds.Left, bounds.Top),
                new PlanPoint(bounds.Right, bounds.Top),
                new PlanPoint(bounds.Right, bounds.Bottom),
                new PlanPoint(bounds.Left, bounds.Bottom)
            ],
            wallIds,
            Confidence.High)
        {
            Evidence = ["synthetic room boundary"],
            Label = id.ToUpperInvariant()
        };

    private static IReadOnlyList<string> JsonStrings(JsonElement array) =>
        array.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();

    private sealed record TwoRoomOpeningScenario(
        IReadOnlyList<SheetRegion> Regions,
        IReadOnlyList<WallSegment> Walls,
        WallGraph WallGraph,
        IReadOnlyList<RoomRegion> Rooms,
        IReadOnlyList<OpeningCandidate> Openings);
}
