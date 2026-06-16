using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class StructuralTopologyFilteringTests
{
    [Fact]
    public async Task ScanAsync_ExcludesObjectLikeWallComponentsFromRoomSolving()
    {
        var document = Document(
            "object-like-component-room-filter",
            Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
            Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
            Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
            Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100)),
            Wall("fixture-top", new PlanPoint(520, 160), new PlanPoint(590, 160)),
            Wall("fixture-right", new PlanPoint(590, 160), new PlanPoint(590, 210)),
            Wall("fixture-bottom", new PlanPoint(590, 210), new PlanPoint(520, 210)),
            Wall("fixture-left", new PlanPoint(520, 210), new PlanPoint(520, 160)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var unfiltered = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ExcludeObjectLikeWallComponentsFromStructuralTopology = false
            });

        var objectLike = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var objectLikeWallIds = objectLike.WallIds.ToHashSet(StringComparer.Ordinal);
        var objectLikeWalls = result.Walls
            .Where(wall => objectLikeWallIds.Contains(wall.Id))
            .ToArray();

        Assert.True(objectLike.ExcludedFromStructuralTopology);
        Assert.All(objectLikeWalls, wall => Assert.Equal(WallType.Unknown, wall.WallType));
        Assert.All(objectLikeWalls, wall => Assert.Contains(
            wall.Evidence,
            item => item.Contains("non-structural or isolated graph component", StringComparison.Ordinal)));
        Assert.Contains("excluded from structural room/opening topology solving", objectLike.Evidence);
        Assert.Single(result.Rooms);
        Assert.DoesNotContain(result.Rooms, room => room.WallIds.Intersect(objectLikeWallIds, StringComparer.Ordinal).Any());
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "rooms.non_structural_wall_components_excluded"
            && message.Properties["excludedComponentCount"] == "1"
            && message.Properties["objectLikeIslandCount"] == "1");

        using var placementJson = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var placementObjectLike = placementJson.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(wall => wall.GetProperty("sourcePrimitiveIds")
                .EnumerateArray()
                .Any(sourceId => sourceId.GetString() == "fixture-top"));
        var reliability = placementObjectLike.GetProperty("reliability");
        var reasons = reliability.GetProperty("reasons")
            .EnumerateArray()
            .Select(reason => reason.GetString())
            .ToArray();

        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.False(reliability.GetProperty("readyForMetricPlacement").GetBoolean());
        Assert.Contains("wall belongs to compact object-like linework component", reasons);
        Assert.Contains("wall component excluded from structural topology", reasons);

        Assert.Contains(unfiltered.Rooms, room => room.WallIds.Any(objectLikeWallIds.Contains));
    }

    [Fact]
    public async Task ScanAsync_ExcludesObjectLikeWallComponentsFromOpeningSolving()
    {
        var document = Document(
            "object-like-component-opening-filter",
            Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
            Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
            Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
            Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100)),
            Wall("fixture-top-left", new PlanPoint(520, 160), new PlanPoint(545, 160)),
            Wall("fixture-top-right", new PlanPoint(560, 160), new PlanPoint(590, 160)),
            Wall("fixture-right", new PlanPoint(590, 160), new PlanPoint(590, 210)),
            Wall("fixture-bottom", new PlanPoint(590, 210), new PlanPoint(520, 210)),
            Wall("fixture-left", new PlanPoint(520, 210), new PlanPoint(520, 160)));

        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var unfiltered = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ExcludeObjectLikeWallComponentsFromStructuralTopology = false
            });

        var objectLike = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var objectLikeWallIds = objectLike.WallIds.ToHashSet(StringComparer.Ordinal);

        Assert.True(objectLike.ExcludedFromStructuralTopology);
        Assert.DoesNotContain(
            unfiltered.WallGraph.Components.Where(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland),
            component => component.ExcludedFromStructuralTopology);
        Assert.DoesNotContain(result.Openings, opening => opening.HostWallIds.Intersect(objectLikeWallIds, StringComparer.Ordinal).Any());
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "openings.non_structural_wall_components_excluded"
            && message.Properties["excludedComponentCount"] == "1"
            && message.Properties["objectLikeIslandCount"] == "1");

        Assert.Contains(unfiltered.Openings, opening => opening.HostWallIds.Intersect(objectLikeWallIds, StringComparer.Ordinal).Any());
    }

    [Fact]
    public async Task ScanAsync_ExcludesWeakIsolatedWallFragmentsFromStructuralSolving()
    {
        var document = Document(
            "isolated-fragment-structural-filter",
            Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
            Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
            Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
            Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100)),
            Wall("detail-fragment", new PlanPoint(520, 160), new PlanPoint(590, 160)));

        var result = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ExcludeWeakWallFragmentsFromStructuralTopology = true
            });
        var unfiltered = await new OpenPlanTraceScanner().ScanAsync(
            document,
            new ScannerOptions
            {
                ExcludeWeakWallFragmentsFromStructuralTopology = false
            });

        var fragment = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.IsolatedFragment);
        var unfilteredFragment = Assert.Single(unfiltered.WallGraph.Components, component => component.Kind == WallGraphComponentKind.IsolatedFragment);
        var fragmentWall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("detail-fragment"));

        Assert.True(fragment.ExcludedFromStructuralTopology);
        Assert.False(unfilteredFragment.ExcludedFromStructuralTopology);
        Assert.Equal(WallType.Unknown, fragmentWall.WallType);
        Assert.Contains(
            fragmentWall.Evidence,
            item => item.Contains("non-structural or isolated graph component", StringComparison.Ordinal));
        Assert.Contains("detail-fragment", fragment.SourcePrimitiveIds);
        Assert.Contains("excluded from structural room/opening topology solving", fragment.Evidence);
        Assert.Contains(
            fragment.Evidence,
            item => item.Contains("isolated wall fragment with weak topology", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "wall_graph.weak_fragments.excluded"
            && message.Properties["excludedIsolatedFragmentCount"] == "1");
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "rooms.non_structural_wall_components_excluded"
            && message.Properties["isolatedFragmentCount"] == "1"
            && message.Properties["excludedWallCount"] == "1");

        using var placementJson = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var placementFragment = placementJson.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(wall => wall.GetProperty("sourcePrimitiveIds")
                .EnumerateArray()
                .Any(sourceId => sourceId.GetString() == "detail-fragment"));
        var reliability = placementFragment.GetProperty("reliability");
        var reasons = reliability.GetProperty("reasons")
            .EnumerateArray()
            .Select(reason => reason.GetString())
            .ToArray();

        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.False(reliability.GetProperty("readyForMetricPlacement").GetBoolean());
        Assert.Contains("wall belongs to isolated wall graph fragment", reasons);
        Assert.Contains("wall component excluded from structural topology", reasons);
    }

    [Fact]
    public async Task ScanAsync_SuppressesFragmentReviewWallsFromRoutingBarriers()
    {
        var primitives = new List<PlanPrimitive>
        {
            Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
            Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
            Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
            Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100))
        };
        for (var index = 0; index < 20; index++)
        {
            var x = 100 + (index * 8);
            primitives.Add(UnlayeredLine(
                $"uncertain-fragment-{index}",
                new PlanPoint(x, 180),
                new PlanPoint(x + 4.5, 180)));
        }

        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document("fragment-review-structural-filter", primitives.ToArray()));

        var fragmentWall = Assert.Single(
            result.Walls,
            wall => wall.SourcePrimitiveIds.Contains("uncertain-fragment-0"));
        var fragmentEvidence = Assert.IsType<WallFragmentEvidence>(fragmentWall.FragmentEvidence);
        Assert.True(fragmentEvidence.RequiresGeometryReview);
        Assert.Equal(WallType.Unknown, fragmentWall.WallType);

        Assert.Contains(
            result.WallGraph.Edges,
            edge => edge.WallId == fragmentWall.Id);
        Assert.DoesNotContain(result.RoutingLayer.Barriers, barrier => barrier.SourceId == fragmentWall.Id);
        Assert.Contains(
            result.RoutingLayer.Evidence,
            item => item == "fragment-review wall barriers suppressed: 1");
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "walls.fragment_merged_geometry.review");

        using var placementJson = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var placementFragment = placementJson.RootElement
            .GetProperty("walls")
            .EnumerateArray()
            .Single(wall => wall.GetProperty("id").GetString() == fragmentWall.Id);
        var reliability = placementFragment.GetProperty("reliability");
        var reasons = reliability.GetProperty("reasons")
            .EnumerateArray()
            .Select(reason => reason.GetString())
            .ToArray();

        Assert.True(reliability.GetProperty("requiresReview").GetBoolean());
        Assert.False(reliability.GetProperty("readyForCoordinatePlacement").GetBoolean());
        Assert.False(reliability.GetProperty("readyForMetricPlacement").GetBoolean());
        Assert.Contains("wall fragment geometry requires review before exact placement", reasons);
    }

    [Fact]
    public async Task ScanAsync_KeepsIsolatedFragmentsNearOpeningEvidenceForOpeningSolving()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "isolated-opening-fragment-keeper",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
                Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
                Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
                Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100)),
                Wall("door-wall-left", new PlanPoint(520, 180), new PlanPoint(580, 180)),
                Wall("door-wall-right", new PlanPoint(610, 180), new PlanPoint(690, 180)),
                DoorArc("door-swing", new PlanPoint(580, 180), 30)));

        var doorWalls = result.Walls
            .Where(wall => wall.SourcePrimitiveIds.Contains("door-wall-left")
                || wall.SourcePrimitiveIds.Contains("door-wall-right"))
            .ToArray();
        var doorWallIds = doorWalls
            .Select(wall => wall.Id)
            .ToHashSet(StringComparer.Ordinal);
        var doorComponents = result.WallGraph.Components
            .Where(component => component.WallIds.Intersect(doorWallIds, StringComparer.Ordinal).Any())
            .ToArray();

        Assert.Equal(2, doorWalls.Length);
        Assert.All(doorComponents, component => Assert.Equal(WallGraphComponentKind.IsolatedFragment, component.Kind));
        Assert.All(doorComponents, component => Assert.False(component.ExcludedFromStructuralTopology));
        Assert.Contains(
            result.Openings,
            opening => opening.SourcePrimitiveIds.Contains("door-swing")
                && opening.HostWallIds.Intersect(doorWallIds, StringComparer.Ordinal).Count() == 2);
    }

    [Fact]
    public async Task RoutingLayer_SuppressesUnusedIsolatedWallFragmentsWhenStructuralWallsExist()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "isolated-fragment-routing-filter",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(430, 100)),
                Wall("room-right", new PlanPoint(430, 100), new PlanPoint(430, 320)),
                Wall("room-bottom", new PlanPoint(430, 320), new PlanPoint(100, 320)),
                Wall("room-left", new PlanPoint(100, 320), new PlanPoint(100, 100)),
                Wall("detail-fragment", new PlanPoint(520, 160), new PlanPoint(590, 160))));

        var detailWall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("detail-fragment"));
        var fragment = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.IsolatedFragment);

        Assert.True(fragment.ExcludedFromStructuralTopology);
        Assert.Contains(detailWall.Id, fragment.WallIds);
        Assert.Equal(WallType.Unknown, detailWall.WallType);
        Assert.DoesNotContain(result.RoutingLayer.Barriers, barrier => barrier.SourceId == detailWall.Id);
        Assert.Contains(
            result.Diagnostics.Messages,
            message => message.Code == "routing.non_structural_wall_components_excluded"
                && message.Properties["isolatedFragmentCount"] == "1");
    }

    [Fact]
    public async Task StructuralFilters_ExcludeRejectedWallEvidenceRetainedForReviewAndRouting()
    {
        var document = Document(
            "retained-rejected-wall-evidence-structural-filter",
            Wall("host-wall", new PlanPoint(80, 100), new PlanPoint(320, 100)),
            DoorLeaf("door-leaf-noise", new PlanPoint(200, 100), new PlanPoint(200, 132)),
            DoorArc("door-swing", new PlanPoint(200, 100), 32));
        var context = new ScanContext(
            document,
            new ScannerOptions
            {
                EnableWallEvidenceNoiseRejection = false,
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
            WallType = WallType.Exterior,
            SourcePrimitiveIds = new[] { "door-leaf-noise" },
            Evidence = new[] { "test retained rejected wall-like detail" }
        });

        await new WallEvidenceRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var retainedDoorLeafWall = Assert.Single(context.Walls, wall => wall.SourcePrimitiveIds.Contains("door-leaf-noise"));
        var hostWall = Assert.Single(context.Walls, wall => wall.SourcePrimitiveIds.Contains("host-wall"));
        var rejected = Assert.Single(
            context.WallEvidenceMap.WallAssessments,
            assessment => assessment.WallId == retainedDoorLeafWall.Id);

        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, rejected.Category);
        Assert.True(rejected.RejectedAsNoise);
        Assert.Equal(WallEvidenceDecision.Reject, rejected.Decision);

        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(hostWall.Id, context.WallTopologyPreparation.GraphWallIds);
        Assert.DoesNotContain(retainedDoorLeafWall.Id, context.WallTopologyPreparation.GraphWallIds);
        var preparedRejectedWall = Assert.Single(context.WallTopologyPreparation.RejectedWalls);
        Assert.Equal(retainedDoorLeafWall.Id, preparedRejectedWall.WallId);
        Assert.Equal(WallEvidenceCategory.DoorOrOpeningSymbol, preparedRejectedWall.Category);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            message => message.Code == "wall_topology_preparation.prepared"
                && message.Properties["graphWallCount"] == "1"
                && message.Properties["rejectedWallCount"] == "1"
                && message.Properties["doorOrOpeningSymbolCount"] == "1");

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(context.WallGraph.Edges, edge => edge.WallId == hostWall.Id);
        Assert.DoesNotContain(context.WallGraph.Edges, edge => edge.WallId == retainedDoorLeafWall.Id);
        Assert.DoesNotContain(
            context.WallGraph.Components,
            component => component.WallIds.Contains(retainedDoorLeafWall.Id));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            message => message.Code == "wall_graph.rejected_wall_evidence_excluded"
                && message.Properties["excludedWallCount"] == "1"
                && message.Properties["doorOrOpeningSymbolCount"] == "1");

        await new WallTypeRefinementStage().ExecuteAsync(context, CancellationToken.None);

        var refinedDoorLeafWall = Assert.Single(context.Walls, wall => wall.Id == retainedDoorLeafWall.Id);
        Assert.Equal(WallType.Unknown, refinedDoorLeafWall.WallType);
        Assert.Contains(
            refinedDoorLeafWall.Evidence,
            item => item.Contains("Wall Evidence V2 rejected candidate", StringComparison.Ordinal));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            message => message.Code == "walls.architectural_type_refined"
                && message.Properties["rejectedEvidenceProtectedWallCount"] == "1");

        var structuralWalls = WallTopologyFilter.StructuralWallsForPage(
            context,
            1,
            out var excludedComponents,
            out var excludedEvidenceAssessments);

        Assert.Empty(excludedComponents);
        var excludedEvidence = Assert.Single(excludedEvidenceAssessments);
        Assert.Equal(retainedDoorLeafWall.Id, excludedEvidence.WallId);
        Assert.Contains(structuralWalls, wall => wall.Id == hostWall.Id);
        Assert.DoesNotContain(structuralWalls, wall => wall.Id == retainedDoorLeafWall.Id);

        WallTopologyFilter.AddRejectedWallEvidenceExclusionDiagnostic(
            context,
            "test-stage",
            1,
            excludedEvidenceAssessments);

        Assert.Contains(
            context.Diagnostics.Build().Messages,
            message => message.Code == "test-stage.rejected_wall_evidence_excluded"
                && message.Properties["excludedWallCount"] == "1"
                && message.Properties["doorOrOpeningSymbolCount"] == "1");

        var routingLayer = PlanRoutingLayerBuilder.FromScanResult(context.ToRoutingSourceResult());

        Assert.Contains(routingLayer.Barriers, barrier => barrier.SourceId == hostWall.Id);
        Assert.DoesNotContain(routingLayer.Barriers, barrier => barrier.SourceId == retainedDoorLeafWall.Id);
        Assert.Contains(
            routingLayer.Evidence,
            item => item == "wall-evidence rejected barriers suppressed: 1");
    }

    [Fact]
    public async Task WallTopologyPreparationStage_SplitsGraphInputByEvidenceDecision()
    {
        var acceptedWall = PreparedWall("wall-accepted", 100);
        var reviewWall = PreparedWall("wall-review", 140);
        var unassessedWall = PreparedWall("wall-unassessed", 180);
        var rejectedWall = PreparedWall("wall-rejected-door-detail", 220);
        var context = new ScanContext(
            Document("wall-topology-preparation-decision-split"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { acceptedWall, reviewWall, unassessedWall, rejectedWall });
        context.WallEvidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            Array.Empty<WallEvidenceBand>(),
            new[]
            {
                Assessment(acceptedWall, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody),
                Assessment(reviewWall, WallEvidenceDecision.Review, WallEvidenceCategory.WeakSingleLine),
                Assessment(rejectedWall, WallEvidenceDecision.Reject, WallEvidenceCategory.DoorOrOpeningSymbol, rejectedAsNoise: true)
            });

        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(new[] { acceptedWall.Id, reviewWall.Id, unassessedWall.Id }, context.WallTopologyPreparation.GraphWallIds);
        Assert.Equal(new[] { acceptedWall.Id }, context.WallTopologyPreparation.AcceptedGraphWallIds);
        Assert.Equal(new[] { reviewWall.Id }, context.WallTopologyPreparation.ReviewGraphWallIds);
        Assert.Equal(new[] { unassessedWall.Id }, context.WallTopologyPreparation.UnassessedGraphWallIds);
        Assert.Equal(new[] { rejectedWall.Id }, context.WallTopologyPreparation.RejectedWallIds);
        Assert.True(context.WallTopologyPreparation.IsAcceptedGraphWall(acceptedWall.Id));
        Assert.True(context.WallTopologyPreparation.IsReviewGraphWall(reviewWall.Id));
        Assert.True(context.WallTopologyPreparation.IsUnassessedGraphWall(unassessedWall.Id));
        Assert.False(context.WallTopologyPreparation.IsGraphWall(rejectedWall.Id));

        Assert.Contains(
            context.Diagnostics.Build().Messages,
            message => message.Code == "wall_topology_preparation.prepared"
                && message.Properties["graphWallCount"] == "3"
                && message.Properties["acceptedGraphWallCount"] == "1"
                && message.Properties["reviewGraphWallCount"] == "1"
                && message.Properties["unassessedGraphWallCount"] == "1"
                && message.Properties["rejectedWallCount"] == "1"
                && message.Properties["doorOrOpeningSymbolCount"] == "1");
    }

    private static PlanDocument Document(string id, params PlanPrimitive[] primitives) =>
        new(
            id,
            new[]
            {
                new PlanPage(1, new PlanSize(760, 460), primitives)
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

    private static ArcPrimitive DoorArc(string sourceId, PlanPoint center, double radius) =>
        new(center, radius, 0, Math.PI / 2)
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

    private static WallSegment PreparedWall(string id, double y) =>
        new(
            id,
            1,
            new PlanLineSegment(new PlanPoint(80, y), new PlanPoint(220, y)),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { id },
            Evidence = new[] { "prepared topology test wall" }
        };

    private static WallEvidenceWallAssessment Assessment(
        WallSegment wall,
        WallEvidenceDecision decision,
        WallEvidenceCategory category,
        bool rejectedAsNoise = false) =>
        new(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            category,
            wall.Confidence,
            decision == WallEvidenceDecision.Accept,
            decision == WallEvidenceDecision.Review,
            rejectedAsNoise,
            wall.SourcePrimitiveIds,
            new[] { "test wall evidence assessment" })
        {
            Decision = decision
        };
}
