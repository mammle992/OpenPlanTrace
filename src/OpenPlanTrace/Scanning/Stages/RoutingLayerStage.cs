namespace OpenPlanTrace;

internal sealed class RoutingLayerStage : IPipelineStage
{
    public string Name => "routing-layer";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var routingLayer = PlanRoutingLayerBuilder.FromScanResult(context.ToRoutingSourceResult());
        context.RoutingLayer = routingLayer;
        context.HasRoutingLayer = true;

        foreach (var group in context.WallGraph.Components
            .Where(component => component.ExcludedFromStructuralTopology && component.WallIds.Count > 0)
            .GroupBy(component => component.PageNumber)
            .OrderBy(group => group.Key))
        {
            WallTopologyFilter.AddStructuralTopologyExclusionDiagnostic(
                context,
                "routing",
                group.Key,
                group.ToArray());
        }

        if (routingLayer.Barriers.Count == 0
            && routingLayer.Passages.Count == 0
            && routingLayer.Obstacles.Count == 0
            && routingLayer.RoomUseHints.Count == 0
            && routingLayer.SuppressedObjects.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        context.AddDiagnostic(
            "routing_layer.generated",
            DiagnosticSeverity.Info,
            Name,
            $"Generated trusted routing layer with {routingLayer.Barriers.Count} barrier(s), {routingLayer.Passages.Count} passage(s), {routingLayer.Obstacles.Count} obstacle(s), {routingLayer.RoomUseHints.Count} room-use hint(s), and {routingLayer.SuppressedObjects.Count} suppressed child object record(s).",
            confidence: Confidence.High,
            scope: DiagnosticScope.Document,
            sourcePrimitiveIds: routingLayer.Barriers
                .SelectMany(barrier => barrier.SourcePrimitiveIds)
                .Concat(routingLayer.Passages.SelectMany(passage => passage.SourcePrimitiveIds))
                .Concat(routingLayer.Obstacles.SelectMany(obstacle => obstacle.SourcePrimitiveIds))
                .Concat(routingLayer.RoomUseHints.SelectMany(hint => hint.SourcePrimitiveIds))
                .Concat(routingLayer.SuppressedObjects.SelectMany(item => item.SourcePrimitiveIds)),
            properties: new Dictionary<string, string>
            {
                ["barrierCount"] = routingLayer.Barriers.Count.ToString(),
                ["passageCount"] = routingLayer.Passages.Count.ToString(),
                ["obstacleCount"] = routingLayer.Obstacles.Count.ToString(),
                ["roomUseHintCount"] = routingLayer.RoomUseHints.Count.ToString(),
                ["suppressedObjectCount"] = routingLayer.SuppressedObjects.Count.ToString(),
                ["ignoredObjectCount"] = routingLayer.IgnoredObjects.Count.ToString()
            });

        return ValueTask.CompletedTask;
    }
}
