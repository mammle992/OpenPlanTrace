using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class WallGraphTopologyTests
{
    [Fact]
    public async Task ScanAsync_ClassifiesRectangularWallGraphCorners()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-corners",
                Wall("top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("left", new PlanPoint(100, 260), new PlanPoint(100, 100))));

        var cornerNodes = result.WallGraph.Nodes.Where(node => node.Kind == WallNodeKind.Corner).ToArray();

        Assert.Equal(4, cornerNodes.Length);
        Assert.All(cornerNodes, node => Assert.Equal(2, node.Degree));
        Assert.All(cornerNodes, node => Assert.Contains(node.Evidence, item => item.Contains("classified Corner", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesTWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-t-junction",
                Wall("horizontal", new PlanPoint(100, 100), new PlanPoint(420, 100)),
                Wall("vertical-stem", new PlanPoint(250, 100), new PlanPoint(250, 280))));

        var node = Assert.Single(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.TJunction);

        Assert.Equal(3, node.Degree);
        Assert.Contains("East", node.Directions);
        Assert.Contains("West", node.Directions);
        Assert.Contains("South", node.Directions);
    }

    [Fact]
    public async Task ScanAsync_InferenceConnectsNearTouchWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-near-touch-t-junction",
                Wall("horizontal-short", new PlanPoint(100, 100), new PlanPoint(195, 100)),
                Wall("vertical-host", new PlanPoint(200, 60), new PlanPoint(200, 140))));

        var node = Assert.Single(result.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && Math.Abs(node.Position.X - 200) <= 0.5
            && Math.Abs(node.Position.Y - 100) <= 0.5);

        Assert.Equal(3, node.Degree);
        Assert.Contains("North", node.Directions);
        Assert.Contains("South", node.Directions);
        Assert.Contains("West", node.Directions);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.near_touch_junctions.inferred"
                && diagnostic.Properties["inferredJunctionCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_QueuesReviewForUnresolvedWallEndpointGap()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-review",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        Assert.DoesNotContain(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.TJunction);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, diagnostic =>
            diagnostic.Code == "wall_graph.endpoint_gap.review");
        var repairCandidate = Assert.Single(result.WallGraph.RepairCandidates);

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.PageNumber);
        Assert.Equal("EndpointToWall", diagnostic.Properties["gapKind"]);
        Assert.Equal("12", diagnostic.Properties["gapDistance"]);
        Assert.Equal(repairCandidate.Id, diagnostic.Properties["repairCandidateId"]);
        Assert.Equal("SnapEndpointToWall", diagnostic.Properties["suggestedAction"]);
        Assert.Contains("room-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("partition-gap", diagnostic.SourcePrimitiveIds);
        Assert.NotNull(diagnostic.Region);
        Assert.Equal(WallGraphRepairCandidateKind.EndpointToWall, repairCandidate.Kind);
        Assert.Equal(WallGraphRepairAction.SnapEndpointToWall, repairCandidate.SuggestedAction);
        Assert.Equal(12, repairCandidate.GapDistance);
        Assert.True(repairCandidate.RequiresReview);
        Assert.Contains("room-top", repairCandidate.SourcePrimitiveIds);
        Assert.Contains("partition-gap", repairCandidate.SourcePrimitiveIds);
        Assert.Equal(repairCandidate.SourcePoint, repairCandidate.RepairLine.Start);
        Assert.Equal(repairCandidate.TargetPoint, repairCandidate.RepairLine.End);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gaps.detected"
                && diagnostic.Properties["gapCount"] == "1");
    }

    [Fact]
    public async Task JsonExporter_IncludesWallGraphRepairCandidates()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-repair-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var candidate = Assert.Single(parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("repairCandidates")
            .EnumerateArray());

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("EndpointToWall", candidate.GetProperty("kind").GetString());
        Assert.Equal("SnapEndpointToWall", candidate.GetProperty("suggestedAction").GetString());
        Assert.Equal(12, candidate.GetProperty("gapDistance").GetDouble());
        Assert.True(candidate.GetProperty("requiresReview").GetBoolean());
        Assert.Equal(200, candidate.GetProperty("sourcePoint").GetProperty("x").GetDouble());
        Assert.Equal(112, candidate.GetProperty("sourcePoint").GetProperty("y").GetDouble());
        Assert.Equal(200, candidate.GetProperty("targetPoint").GetProperty("x").GetDouble());
        Assert.Equal(100, candidate.GetProperty("targetPoint").GetProperty("y").GetDouble());
        Assert.Contains("room-top", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("partition-gap", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task PlacementExporter_IncludesWallGraphRepairCandidatesWithMetricCoordinates()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-placement-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        result = result with
        {
            Calibration = result.Calibration with
            {
                MillimetersPerDrawingUnit = 10,
                Confidence = Confidence.High
            }
        };

        var json = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var candidate = Assert.Single(root.GetProperty("wallGraphRepairCandidates").EnumerateArray());

        Assert.Equal(1, root.GetProperty("summary").GetProperty("wallGraphRepairCandidateCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageSummaries")[0].GetProperty("wallGraphRepairCandidateCount").GetInt32());
        Assert.Equal("EndpointToWall", candidate.GetProperty("kind").GetString());
        Assert.Equal("SnapEndpointToWall", candidate.GetProperty("suggestedAction").GetString());
        Assert.Equal(12, candidate.GetProperty("gapDistanceDrawingUnits").GetDouble());
        Assert.Equal(120, candidate.GetProperty("gapDistanceMillimeters").GetDouble());
        Assert.Equal(2000, candidate.GetProperty("sourcePointMillimeters").GetProperty("x").GetDouble());
        Assert.Equal(1120, candidate.GetProperty("sourcePointMillimeters").GetProperty("y").GetDouble());
        Assert.Equal(2000, candidate.GetProperty("targetPointMillimeters").GetProperty("x").GetDouble());
        Assert.Equal(1000, candidate.GetProperty("targetPointMillimeters").GetProperty("y").GetDouble());
        Assert.True(candidate.GetProperty("requiresReview").GetBoolean());
        Assert.Contains("endpoint-to-wall", candidate.GetProperty("recommendedAction").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("room-top", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesCrossingWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-crossing",
                Wall("horizontal", new PlanPoint(100, 100), new PlanPoint(420, 100)),
                Wall("vertical", new PlanPoint(250, 40), new PlanPoint(250, 260))));

        var node = Assert.Single(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.Crossing);

        Assert.Equal(4, node.Degree);
        Assert.Contains("North", node.Directions);
        Assert.Contains("East", node.Directions);
        Assert.Contains("South", node.Directions);
        Assert.Contains("West", node.Directions);
    }

    [Fact]
    public async Task ScanAsync_SummarizesWallGraphComponentsForStructuralReview()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-components",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("table-top", new PlanPoint(420, 160), new PlanPoint(455, 160)),
                Wall("table-right", new PlanPoint(455, 160), new PlanPoint(455, 185)),
                Wall("table-bottom", new PlanPoint(455, 185), new PlanPoint(420, 185)),
                Wall("table-left", new PlanPoint(420, 185), new PlanPoint(420, 160))));

        var main = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.MainStructural);
        var objectLike = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);

        Assert.Equal(4, main.WallCount);
        Assert.Equal(4, objectLike.WallCount);
        Assert.Equal(4, objectLike.EdgeCount);
        Assert.Contains("table-top", objectLike.SourcePrimitiveIds);
        Assert.Contains(objectLike.Evidence, item => item.Contains("possible object or symbol linework", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.object_like_components.review"
                && diagnostic.Properties["objectLikeIslandCount"] == "1");
    }

    [Fact]
    public async Task JsonExporter_IncludesWallNodeTopologyFields()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-node-export",
                Wall("top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("right", new PlanPoint(320, 100), new PlanPoint(320, 260))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var node = parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("nodes")
            .EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "Corner");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(2, node.GetProperty("degree").GetInt32());
        Assert.True(node.GetProperty("directions").GetArrayLength() >= 2);
        Assert.Contains("classified Corner", node.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task JsonExporter_IncludesWallGraphComponents()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-component-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("symbol-top", new PlanPoint(420, 160), new PlanPoint(455, 160)),
                Wall("symbol-right", new PlanPoint(455, 160), new PlanPoint(455, 185)),
                Wall("symbol-bottom", new PlanPoint(455, 185), new PlanPoint(420, 185)),
                Wall("symbol-left", new PlanPoint(420, 185), new PlanPoint(420, 160))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var components = parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("components")
            .EnumerateArray()
            .ToArray();
        var objectLike = Assert.Single(components, component => component.GetProperty("kind").GetString() == "ObjectLikeIsland");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, objectLike.GetProperty("wallCount").GetInt32());
        Assert.Equal(4, objectLike.GetProperty("edgeCount").GetInt32());
        Assert.Contains("symbol-top", objectLike.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("A-WALL", objectLike.GetProperty("sourceLayers").EnumerateArray().Select(item => item.GetString()));
        Assert.True(objectLike.GetProperty("bounds").GetProperty("width").GetDouble() > 0);
    }

    private static PlanDocument Document(string id, params PlanPrimitive[] primitives) =>
        new(
            id,
            new[]
            {
                new PlanPage(1, new PlanSize(600, 400), primitives)
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
