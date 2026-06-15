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
}
