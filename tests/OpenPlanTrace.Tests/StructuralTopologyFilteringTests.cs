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

        Assert.True(objectLike.ExcludedFromStructuralTopology);
        Assert.Contains("excluded from structural room/opening topology solving", objectLike.Evidence);
        Assert.Single(result.Rooms);
        Assert.DoesNotContain(result.Rooms, room => room.WallIds.Intersect(objectLikeWallIds, StringComparer.Ordinal).Any());
        Assert.Contains(result.Diagnostics.Messages, message =>
            message.Code == "rooms.object_like_wall_components_excluded"
            && message.Properties["excludedComponentCount"] == "1");

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
            message.Code == "openings.object_like_wall_components_excluded"
            && message.Properties["excludedComponentCount"] == "1");

        Assert.Contains(unfiltered.Openings, opening => opening.HostWallIds.Intersect(objectLikeWallIds, StringComparer.Ordinal).Any());
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
}
