namespace OpenPlanTrace;

internal sealed class LayerConsistencyStage : IPipelineStage
{
    private const string StageName = "layer-consistency";
    private const double AmbiguityGapTolerance = 0.18;

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.LayerAnalysis.Layers.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var primitiveLayers = BuildPrimitiveLayerIndex(context);

        AddAmbiguousLayerDiagnostics(context, primitiveLayers);

        foreach (var layer in context.LayerAnalysis.Layers.Where(layer => layer.Confidence.Value >= 0.7))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (layer.LikelyCategory)
            {
                case LayerCategory.Wall when !AnyDetectionFromLayer(context.Walls.Select(wall => wall.SourcePrimitiveIds), primitiveLayers, layer):
                    AddMismatch(context, primitiveLayers, layer, "layers.wall_hint.no_walls", "Layer evidence suggests walls, but no detected walls used primitives from this layer.");
                    break;

                case LayerCategory.Door:
                case LayerCategory.Window:
                    if (!AnyDetectionFromLayer(context.Openings.Select(opening => opening.SourcePrimitiveIds), primitiveLayers, layer))
                    {
                        AddMismatch(context, primitiveLayers, layer, "layers.opening_hint.no_openings", $"Layer evidence suggests {layer.LikelyCategory}, but no detected openings used primitives from this layer.");
                    }
                    break;

                case LayerCategory.Equipment:
                case LayerCategory.Electrical:
                case LayerCategory.HVAC:
                case LayerCategory.Plumbing:
                case LayerCategory.FireSafety:
                    if (!AnyDetectionFromLayer(context.ObjectCandidates.Select(candidate => candidate.SourcePrimitiveIds), primitiveLayers, layer))
                    {
                        AddMismatch(context, primitiveLayers, layer, "layers.object_hint.no_objects", $"Layer evidence suggests {layer.LikelyCategory}, but no object candidates used primitives from this layer.", DiagnosticSeverity.Info);
                    }
                    break;
            }
        }

        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, PrimitiveLayerInfo> BuildPrimitiveLayerIndex(ScanContext context)
    {
        var result = new Dictionary<string, PrimitiveLayerInfo>(StringComparer.Ordinal);

        foreach (var page in context.Document.Pages)
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var layer = primitive.Source.Layer ?? primitive.Layer;
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    result[context.PrimitiveId(page.Number, index, primitive)] = new PrimitiveLayerInfo(
                        layer,
                        string.IsNullOrWhiteSpace(primitive.Source.SourceFormat) ? null : primitive.Source.SourceFormat);
                }
            }
        }

        return result;
    }

    private static bool AnyDetectionFromLayer(
        IEnumerable<IReadOnlyList<string>> detectionSourceIds,
        IReadOnlyDictionary<string, PrimitiveLayerInfo> primitiveLayers,
        LayerSummary layer) =>
        detectionSourceIds
            .SelectMany(sourceIds => sourceIds)
            .Any(sourceId =>
                primitiveLayers.TryGetValue(sourceId, out var sourceLayer)
                && Matches(sourceLayer, layer));

    private static void AddAmbiguousLayerDiagnostics(
        ScanContext context,
        IReadOnlyDictionary<string, PrimitiveLayerInfo> primitiveLayers)
    {
        foreach (var layer in context.LayerAnalysis.Layers)
        {
            var scores = layer.CategoryScores
                .Where(score => score.Category != LayerCategory.Unknown)
                .OrderByDescending(score => score.Score)
                .ThenBy(score => score.Category)
                .ToArray();
            if (scores.Length < 2)
            {
                continue;
            }

            var top = scores[0];
            var second = scores[1];
            var gap = top.Score - second.Score;
            if (top.Score < 0.35 || second.Score < 0.24 || gap > AmbiguityGapTolerance)
            {
                continue;
            }

            var severity = top.Score >= 0.55 && second.Score >= 0.55
                ? DiagnosticSeverity.Warning
                : DiagnosticSeverity.Info;
            var sourceIds = SourceIdsForLayer(primitiveLayers, layer);

            context.AddDiagnostic(
                "layers.category_ambiguous",
                severity,
                StageName,
                $"Layer '{layer.Name}' has close competing category evidence: {top.Category} and {second.Category}.",
                pageNumber: layer.PageNumbers.Count == 1 ? layer.PageNumbers[0] : null,
                region: layer.Bounds.IsEmpty ? null : layer.Bounds,
                confidence: new Confidence(Math.Min(0.9, Math.Max(top.Score, second.Score))),
                scope: DiagnosticScope.Layer,
                sourcePrimitiveIds: sourceIds,
                properties: new Dictionary<string, string>
                {
                    ["layerName"] = layer.Name,
                    ["sourceFormat"] = layer.SourceFormat ?? string.Empty,
                    ["topCategory"] = top.Category.ToString(),
                    ["secondCategory"] = second.Category.ToString(),
                    ["topScore"] = Format(top.Score),
                    ["secondScore"] = Format(second.Score),
                    ["scoreGap"] = Format(gap),
                    ["entityCount"] = layer.EntityCount.ToString()
                });
        }
    }

    private static void AddMismatch(
        ScanContext context,
        IReadOnlyDictionary<string, PrimitiveLayerInfo> primitiveLayers,
        LayerSummary layer,
        string code,
        string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning)
    {
        var sourceIds = SourceIdsForLayer(primitiveLayers, layer);

        context.AddDiagnostic(
            code,
            severity,
            StageName,
            $"{message} Layer '{layer.Name}' classified as {layer.LikelyCategory} with confidence {layer.Confidence}.",
            pageNumber: layer.PageNumbers.Count == 1 ? layer.PageNumbers[0] : null,
            region: layer.Bounds.IsEmpty ? null : layer.Bounds,
            confidence: layer.Confidence,
            scope: DiagnosticScope.Layer,
            sourcePrimitiveIds: sourceIds,
            properties: new Dictionary<string, string>
            {
                ["layerName"] = layer.Name,
                ["sourceFormat"] = layer.SourceFormat ?? string.Empty,
                ["likelyCategory"] = layer.LikelyCategory.ToString(),
                ["entityCount"] = layer.EntityCount.ToString(),
                ["sourcePrimitiveCount"] = sourceIds.Length.ToString()
            });
    }

    private static string[] SourceIdsForLayer(
        IReadOnlyDictionary<string, PrimitiveLayerInfo> primitiveLayers,
        LayerSummary layer) =>
        primitiveLayers
            .Where(item => Matches(item.Value, layer))
            .Select(item => item.Key)
            .ToArray();

    private static bool Matches(PrimitiveLayerInfo sourceLayer, LayerSummary layer) =>
        string.Equals(sourceLayer.LayerName, layer.Name, StringComparison.OrdinalIgnoreCase)
        && (layer.SourceFormat is null
            || string.Equals(sourceLayer.SourceFormat, layer.SourceFormat, StringComparison.OrdinalIgnoreCase));

    private static string Format(double value) =>
        Math.Round(value, 6).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record PrimitiveLayerInfo(string LayerName, string? SourceFormat);
}
