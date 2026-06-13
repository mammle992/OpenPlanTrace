namespace OpenPlanTrace;

internal sealed class WallGraphStage : IPipelineStage
{
    public string Name => "wall-graph";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeAccumulator>();
        var edges = new List<WallEdge>();
        var inferredNearTouchJunctionCount = 0;
        var nearTouchTolerance = InferredNearTouchJunctionTolerance(context.Options);
        var pointsByWallId = context.Walls.ToDictionary(
            wall => wall.Id,
            wall => new List<PlanPoint> { wall.CenterLine.Start, wall.CenterLine.End });

        foreach (var pageGroup in context.Walls.GroupBy(wall => wall.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var walls = pageGroup.ToArray();
            for (var leftIndex = 0; leftIndex < walls.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < walls.Length; rightIndex++)
                {
                    if (GeometryOperations.TryIntersect(
                        walls[leftIndex].CenterLine,
                        walls[rightIndex].CenterLine,
                        context.Options.WallSnapTolerance,
                        out var point))
                    {
                        pointsByWallId[walls[leftIndex].Id].Add(point);
                        pointsByWallId[walls[rightIndex].Id].Add(point);
                    }
                    else
                    {
                        foreach (var inferredPoint in InferNearTouchJunctions(
                            walls[leftIndex].CenterLine,
                            walls[rightIndex].CenterLine,
                            nearTouchTolerance,
                            context.Options.WallSnapTolerance))
                        {
                            pointsByWallId[walls[leftIndex].Id].Add(inferredPoint);
                            pointsByWallId[walls[rightIndex].Id].Add(inferredPoint);
                            inferredNearTouchJunctionCount++;
                        }
                    }
                }
            }

            foreach (var wall in walls)
            {
                var orderedPoints = pointsByWallId[wall.Id]
                    .OrderBy(point => wall.CenterLine.ProjectParameter(point))
                    .Aggregate(new List<PlanPoint>(), (unique, point) =>
                    {
                        if (unique.Count == 0 || unique[^1].DistanceTo(point) > context.Options.WallSnapTolerance)
                        {
                            unique.Add(point);
                        }

                        return unique;
                    });

                for (var index = 1; index < orderedPoints.Count; index++)
                {
                    var from = GetOrCreateNode(nodes, pageGroup.Key, orderedPoints[index - 1], context.Options);
                    var to = GetOrCreateNode(nodes, pageGroup.Key, orderedPoints[index], context.Options);

                    if (from.Id == to.Id)
                    {
                        continue;
                    }

                    edges.Add(
                        new WallEdge(
                            $"page:{pageGroup.Key}:edge:{edges.Count + 1}",
                            pageGroup.Key,
                            from.Id,
                            to.Id,
                            wall.Id,
                            wall.Confidence));

                    from.AddIncidentDirection(orderedPoints[index - 1], orderedPoints[index]);
                    to.AddIncidentDirection(orderedPoints[index], orderedPoints[index - 1]);
                }
            }
        }

        var graphNodes = nodes
            .Select(node =>
            {
                var classification = ClassifyNode(node);
                return new WallNode(
                    node.Id,
                    node.PageNumber,
                    node.Position,
                    classification.Kind,
                    node.Degree,
                    classification.Directions,
                    node.Degree > 1 ? Confidence.High : Confidence.Medium,
                    classification.Evidence);
            })
            .ToArray();

        var graphEdges = edges.ToArray();
        var components = BuildComponents(graphNodes, graphEdges, context.Walls, context);
        var repairCandidates = DetectUnresolvedEndpointGaps(graphNodes, graphEdges, components, context.Walls, context.Options).ToArray();
        context.WallGraph = new WallGraph(graphNodes, graphEdges, components, repairCandidates);

        AddComponentDiagnostics(context, components);
        AddSurfacePatternWallOverlapDiagnostics(context, components);
        AddEndpointGapDiagnostics(context, repairCandidates);

        if (inferredNearTouchJunctionCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.near_touch_junctions.inferred",
                DiagnosticSeverity.Info,
                Name,
                "Near-touch wall endpoints were connected into the wall graph.",
                properties: new Dictionary<string, string>
                {
                    ["inferredJunctionCount"] = inferredNearTouchJunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["junctionTolerance"] = nearTouchTolerance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        if (context.Walls.Count > 0 && graphNodes.Length == 0)
        {
            context.AddDiagnostic(
                "wall_graph.empty",
                DiagnosticSeverity.Warning,
                Name,
                "Walls were detected, but no wall graph nodes were produced.",
                confidence: Confidence.Low);
        }

        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<WallGraphComponent> BuildComponents(
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyList<WallSegment> walls,
        ScanContext context)
    {
        if (nodes.Count == 0 && walls.Count == 0)
        {
            return Array.Empty<WallGraphComponent>();
        }

        var nodesById = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var incidentEdges = nodes.ToDictionary(
            node => node.Id,
            _ => new List<WallEdge>(),
            StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            if (incidentEdges.TryGetValue(edge.FromNodeId, out var fromEdges))
            {
                fromEdges.Add(edge);
            }

            if (incidentEdges.TryGetValue(edge.ToNodeId, out var toEdges))
            {
                toEdges.Add(edge);
            }
        }

        var rawComponents = new List<RawWallGraphComponent>();
        var visitedNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes.OrderBy(node => node.PageNumber).ThenBy(node => node.Id, StringComparer.Ordinal))
        {
            if (!visitedNodeIds.Add(node.Id))
            {
                continue;
            }

            var nodeIds = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var edgeIds = new HashSet<string>(StringComparer.Ordinal);
            var wallIds = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(node.Id);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var edge in incidentEdges[current])
                {
                    edgeIds.Add(edge.Id);
                    if (!string.IsNullOrWhiteSpace(edge.WallId))
                    {
                        wallIds.Add(edge.WallId);
                    }

                    var next = string.Equals(edge.FromNodeId, current, StringComparison.Ordinal)
                        ? edge.ToNodeId
                        : edge.FromNodeId;
                    if (visitedNodeIds.Add(next))
                    {
                        nodeIds.Add(next);
                        stack.Push(next);
                    }
                }
            }

            rawComponents.Add(CreateRawComponent(node.PageNumber, wallIds, nodeIds, edgeIds, wallsById, nodesById));
        }

        var wallIdsWithEdges = edges
            .Select(edge => edge.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var wall in walls.OrderBy(wall => wall.PageNumber).ThenBy(wall => wall.Id, StringComparer.Ordinal))
        {
            if (wallIdsWithEdges.Contains(wall.Id))
            {
                continue;
            }

            rawComponents.Add(CreateRawComponent(
                wall.PageNumber,
                new[] { wall.Id },
                Array.Empty<string>(),
                Array.Empty<string>(),
                wallsById,
                nodesById));
        }

        var components = new List<WallGraphComponent>();
        foreach (var pageGroup in rawComponents
            .Where(component => component.WallIds.Count > 0 || component.NodeIds.Count > 0)
            .GroupBy(component => component.PageNumber)
            .OrderBy(group => group.Key))
        {
            var pageBounds = context.Document.Pages.FirstOrDefault(page => page.Number == pageGroup.Key)?.Bounds
                ?? PlanRect.Empty;
            var mainBounds = context.SheetRegions
                .Where(region => region.PageNumber == pageGroup.Key && region.Kind == RegionKind.MainFloorPlan)
                .OrderByDescending(region => region.Bounds.Area)
                .Select(region => region.Bounds)
                .FirstOrDefault();
            if (mainBounds.IsEmpty)
            {
                mainBounds = pageBounds;
            }

            var ordered = pageGroup
                .OrderByDescending(component => component.DrawingLength)
                .ThenByDescending(component => component.WallIds.Count)
                .ThenBy(component => component.Bounds.Top)
                .ThenBy(component => component.Bounds.Left)
                .ToArray();
            var mainComponent = ordered.FirstOrDefault(component => component.WallIds.Count >= 2 || component.EdgeIds.Count >= 2);
            var sequence = 1;
            foreach (var rawComponent in ordered)
            {
                var kind = ClassifyComponent(rawComponent, mainComponent, mainBounds);
                var excludedFromStructuralTopology =
                    context.Options.ExcludeObjectLikeWallComponentsFromStructuralTopology
                    && kind == WallGraphComponentKind.ObjectLikeIsland;
                components.Add(
                    new WallGraphComponent(
                        $"page:{pageGroup.Key}:wall-component:{sequence++}",
                        pageGroup.Key,
                        kind,
                        rawComponent.Bounds,
                        rawComponent.WallIds,
                        rawComponent.NodeIds,
                        rawComponent.EdgeIds,
                        rawComponent.SourcePrimitiveIds,
                        rawComponent.DrawingLength,
                        ComponentConfidence(kind),
                        ComponentEvidence(rawComponent, kind, mainComponent, excludedFromStructuralTopology),
                        excludedFromStructuralTopology));
            }
        }

        return components
            .OrderBy(component => component.PageNumber)
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static RawWallGraphComponent CreateRawComponent(
        int pageNumber,
        IEnumerable<string> wallIds,
        IEnumerable<string> nodeIds,
        IEnumerable<string> edgeIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        IReadOnlyDictionary<string, WallNode> nodesById)
    {
        var componentWallIds = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentNodeIds = nodeIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && nodesById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentEdgeIds = edgeIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var componentWalls = componentWallIds.Select(id => wallsById[id]).ToArray();
        var componentNodes = componentNodeIds.Select(id => nodesById[id]).ToArray();
        var wallBounds = componentWalls.Select(wall => wall.Bounds);
        var nodeBounds = componentNodes.Select(node => PlanRect.FromPoints(node.Position, node.Position));
        var bounds = PlanRect.Union(wallBounds.Concat(nodeBounds));
        var drawingLength = componentWalls.Sum(wall => wall.DrawingLength);
        var sourcePrimitiveIds = componentWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new RawWallGraphComponent(
            pageNumber,
            componentWallIds,
            componentNodeIds,
            componentEdgeIds,
            sourcePrimitiveIds,
            bounds,
            drawingLength);
    }

    private static WallGraphComponentKind ClassifyComponent(
        RawWallGraphComponent component,
        RawWallGraphComponent? mainComponent,
        PlanRect mainBounds)
    {
        if (mainComponent is not null
            && ReferenceEquals(component, mainComponent)
            && (component.WallIds.Count >= 3 || component.EdgeIds.Count >= 3))
        {
            return WallGraphComponentKind.MainStructural;
        }

        if (LooksLikeObjectIsland(component, mainBounds))
        {
            return WallGraphComponentKind.ObjectLikeIsland;
        }

        if (component.WallIds.Count <= 1 || component.EdgeIds.Count == 0 || component.NodeIds.Count <= 2)
        {
            return WallGraphComponentKind.IsolatedFragment;
        }

        return WallGraphComponentKind.SecondaryStructural;
    }

    private static bool LooksLikeObjectIsland(RawWallGraphComponent component, PlanRect mainBounds)
    {
        if (component.Bounds.IsEmpty
            || mainBounds.IsEmpty
            || component.WallIds.Count < 2
            || component.WallIds.Count > 12
            || component.NodeIds.Count > 16)
        {
            return false;
        }

        var componentWidth = Math.Max(component.Bounds.Width, 0);
        var componentHeight = Math.Max(component.Bounds.Height, 0);
        var componentExtent = Math.Max(componentWidth, componentHeight);
        var mainShortSide = Math.Min(mainBounds.Width, mainBounds.Height);
        var mainArea = Math.Max(1, mainBounds.Area);
        var componentAreaRatio = component.Bounds.Area / mainArea;
        var compactExtentLimit = Math.Max(24, mainShortSide * 0.40);

        return componentExtent <= compactExtentLimit
            && componentAreaRatio <= 0.04;
    }

    private static Confidence ComponentConfidence(WallGraphComponentKind kind) =>
        kind switch
        {
            WallGraphComponentKind.MainStructural => Confidence.High,
            WallGraphComponentKind.SecondaryStructural => Confidence.Medium,
            WallGraphComponentKind.ObjectLikeIsland => Confidence.Medium,
            WallGraphComponentKind.IsolatedFragment => Confidence.Low,
            _ => Confidence.Low
        };

    private static IReadOnlyList<string> ComponentEvidence(
        RawWallGraphComponent component,
        WallGraphComponentKind kind,
        RawWallGraphComponent? mainComponent,
        bool excludedFromStructuralTopology)
    {
        var evidence = new List<string>
        {
            $"connected component with {component.WallIds.Count} wall(s), {component.NodeIds.Count} node(s), {component.EdgeIds.Count} edge(s)",
            $"drawing length {component.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"
        };

        if (kind == WallGraphComponentKind.MainStructural)
        {
            evidence.Add("largest structural wall component on page");
        }
        else if (kind == WallGraphComponentKind.ObjectLikeIsland)
        {
            evidence.Add("compact disconnected component; review as possible object or symbol linework");
        }
        else if (kind == WallGraphComponentKind.IsolatedFragment)
        {
            evidence.Add("isolated wall graph fragment with weak topology");
        }
        else if (mainComponent is not null)
        {
            evidence.Add("connected structural component separate from the largest page component");
        }

        if (excludedFromStructuralTopology)
        {
            evidence.Add("excluded from structural room/opening topology solving");
        }

        return evidence;
    }

    private void AddComponentDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        if (components.Count == 0)
        {
            return;
        }

        var mainCount = components.Count(component => component.Kind == WallGraphComponentKind.MainStructural);
        var secondaryCount = components.Count(component => component.Kind == WallGraphComponentKind.SecondaryStructural);
        var objectLikeCount = components.Count(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var isolatedCount = components.Count(component => component.Kind == WallGraphComponentKind.IsolatedFragment);
        var largest = components
            .OrderByDescending(component => component.DrawingLength)
            .ThenByDescending(component => component.WallCount)
            .First();

        context.AddDiagnostic(
            "wall_graph.components.detected",
            DiagnosticSeverity.Info,
            Name,
            "Wall graph connected components were summarized for topology review.",
            confidence: Confidence.Medium,
            properties: new Dictionary<string, string>
            {
                ["componentCount"] = components.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["mainStructuralComponentCount"] = mainCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["secondaryStructuralComponentCount"] = secondaryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objectLikeIslandCount"] = objectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isolatedFragmentCount"] = isolatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["largestComponentId"] = largest.Id,
                ["largestComponentWallCount"] = largest.WallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["largestComponentDrawingLength"] = largest.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            });

        if (objectLikeCount > 0)
        {
            context.AddDiagnostic(
                "wall_graph.object_like_components.review",
                DiagnosticSeverity.Info,
                Name,
                "Compact disconnected wall graph components may be object or symbol linework rather than walls.",
                confidence: Confidence.Medium,
                properties: new Dictionary<string, string>
                {
                    ["objectLikeIslandCount"] = objectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["componentIds"] = string.Join(",", components
                        .Where(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland)
                        .Select(component => component.Id)
                        .Take(20))
                });
        }
    }

    private void AddSurfacePatternWallOverlapDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphComponent> components)
    {
        if (context.SurfacePatterns.Count == 0 || context.Walls.Count == 0)
        {
            return;
        }

        var componentsByWallId = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                componentsByWallId.TryAdd(wallId, component);
            }
        }

        var candidates = new List<SurfacePatternWallOverlapCandidate>();
        foreach (var pattern in context.SurfacePatterns.Where(pattern => pattern.ExcludedFromStructuralTopology))
        {
            var patternSourceIds = pattern.SourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
            foreach (var wall in context.Walls.Where(wall => wall.PageNumber == pattern.PageNumber))
            {
                componentsByWallId.TryGetValue(wall.Id, out var component);
                if (component?.ExcludedFromStructuralTopology == true)
                {
                    continue;
                }

                var wallBounds = wall.Bounds;
                if (!pattern.Bounds.Intersects(wallBounds))
                {
                    continue;
                }

                var intersection = pattern.Bounds.Intersection(wallBounds);
                if (intersection.IsEmpty || intersection.Area <= 0)
                {
                    continue;
                }

                var wallOverlapRatio = intersection.Area / Math.Max(1, wallBounds.Area);
                var patternOverlapRatio = intersection.Area / Math.Max(1, pattern.Bounds.Area);
                var sharedSourceIds = wall.SourcePrimitiveIds
                    .Where(patternSourceIds.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (!ShouldReviewSurfacePatternWallOverlap(
                    wall,
                    pattern,
                    wallOverlapRatio,
                    patternOverlapRatio,
                    sharedSourceIds.Length))
                {
                    continue;
                }

                candidates.Add(new SurfacePatternWallOverlapCandidate(
                    pattern,
                    wall,
                    component,
                    intersection,
                    wallOverlapRatio,
                    patternOverlapRatio,
                    sharedSourceIds));
            }
        }

        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.PageNumber)
                     .ThenByDescending(candidate => candidate.PriorityScore)
                     .ThenBy(candidate => candidate.Pattern.Id, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Wall.Id, StringComparer.Ordinal)
                     .Take(ScanReviewQueueSummary.SurfacePatternWallOverlapReviewQueueLimit))
        {
            var sourcePrimitiveIds = candidate.SharedSourcePrimitiveIds.Count > 0
                ? candidate.SharedSourcePrimitiveIds
                : candidate.Wall.SourcePrimitiveIds;

            context.AddDiagnostic(
                "wall_graph.surface_pattern_wall_overlap.review",
                DiagnosticSeverity.Warning,
                Name,
                "A non-excluded wall overlaps or shares source primitives with dense non-structural surface/detail linework.",
                candidate.PageNumber,
                candidate.IntersectionBounds,
                Confidence.Medium,
                DiagnosticScope.Detection,
                sourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["surfacePatternId"] = candidate.Pattern.Id,
                    ["surfacePatternKind"] = candidate.Pattern.Kind.ToString(),
                    ["surfacePatternOrientation"] = candidate.Pattern.Orientation.ToString(),
                    ["wallId"] = candidate.Wall.Id,
                    ["wallComponentId"] = candidate.Component?.Id ?? string.Empty,
                    ["wallComponentKind"] = candidate.Component?.Kind.ToString() ?? string.Empty,
                    ["excludedFromStructuralTopology"] = (candidate.Component?.ExcludedFromStructuralTopology ?? false).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallOverlapRatio"] = candidate.WallOverlapRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["patternOverlapRatio"] = candidate.PatternOverlapRatio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["sharedSourcePrimitiveCount"] = candidate.SharedSourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallSourcePrimitiveCount"] = candidate.Wall.SourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["surfacePatternSourcePrimitiveCount"] = candidate.Pattern.SourcePrimitiveIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["wallLength"] = candidate.Wall.DrawingLength.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["surfacePatternBounds"] = FormatRect(candidate.Pattern.Bounds),
                    ["wallBounds"] = FormatRect(candidate.Wall.Bounds)
                });
        }
    }

    private static bool ShouldReviewSurfacePatternWallOverlap(
        WallSegment wall,
        SurfacePatternCandidate pattern,
        double wallOverlapRatio,
        double patternOverlapRatio,
        int sharedSourcePrimitiveCount)
    {
        if (sharedSourcePrimitiveCount >= 2)
        {
            return true;
        }

        if (sharedSourcePrimitiveCount >= 1 && wallOverlapRatio >= 0.10)
        {
            return true;
        }

        var patternLongSide = Math.Max(pattern.Bounds.Width, pattern.Bounds.Height);
        var maximumLocalWallLength = Math.Max(64, patternLongSide * 0.75);
        if (wallOverlapRatio >= 0.85 && wall.DrawingLength <= maximumLocalWallLength)
        {
            return true;
        }

        return wallOverlapRatio >= 0.50
            && patternOverlapRatio >= 0.015
            && wall.DrawingLength <= maximumLocalWallLength;
    }

    private static string FormatRect(PlanRect rect) =>
        string.Join(
            ",",
            rect.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            rect.Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

    private sealed record SurfacePatternWallOverlapCandidate(
        SurfacePatternCandidate Pattern,
        WallSegment Wall,
        WallGraphComponent? Component,
        PlanRect IntersectionBounds,
        double WallOverlapRatio,
        double PatternOverlapRatio,
        IReadOnlyList<string> SharedSourcePrimitiveIds)
    {
        public int PageNumber => Pattern.PageNumber;

        public double PriorityScore
        {
            get
            {
                var score = WallOverlapRatio * 100;
                score += Math.Min(SharedSourcePrimitiveIds.Count, 10) * 30;
                if (Component?.Kind == WallGraphComponentKind.MainStructural)
                {
                    score += 20;
                }

                return score;
            }
        }
    }

    private static IReadOnlyList<WallGraphRepairCandidate> DetectUnresolvedEndpointGaps(
        IReadOnlyList<WallNode> nodes,
        IReadOnlyList<WallEdge> edges,
        IReadOnlyList<WallGraphComponent> components,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options)
    {
        if (options.MaxWallGraphEndpointGapReviewItems <= 0)
        {
            return Array.Empty<WallGraphRepairCandidate>();
        }

        var autoConnectTolerance = InferredNearTouchJunctionTolerance(options);
        var minimumReviewDistance = autoConnectTolerance + Math.Max(0.5, options.WallSnapTolerance * 0.25);
        var reviewTolerance = UnresolvedEndpointGapReviewTolerance(options);
        if (reviewTolerance <= autoConnectTolerance)
        {
            return Array.Empty<WallGraphRepairCandidate>();
        }

        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var componentByWallId = BuildComponentByWallId(components);
        var incidentEdgesByNode = nodes.ToDictionary(
            node => node.Id,
            _ => new List<WallEdge>(),
            StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (incidentEdgesByNode.TryGetValue(edge.FromNodeId, out var fromEdges))
            {
                fromEdges.Add(edge);
            }

            if (incidentEdgesByNode.TryGetValue(edge.ToNodeId, out var toEdges))
            {
                toEdges.Add(edge);
            }
        }

        var endpointNodes = nodes
            .Where(node => node.Degree <= 1 || node.Kind == WallNodeKind.Endpoint)
            .OrderBy(node => node.PageNumber)
            .ThenBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        var candidates = new List<WallGraphRepairCandidate>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in endpointNodes)
        {
            var nodeWallIds = WallIdsForNode(node.Id, incidentEdgesByNode)
                .ToHashSet(StringComparer.Ordinal);
            if (nodeWallIds.Count == 0)
            {
                continue;
            }

            if (ContainsObjectLikeWall(nodeWallIds, componentByWallId))
            {
                continue;
            }

            WallGraphRepairCandidate? best = null;

            foreach (var wall in walls
                         .Where(wall => wall.PageNumber == node.PageNumber)
                         .Where(wall => !nodeWallIds.Contains(wall.Id)))
            {
                if (!IsEndpointDirectionPerpendicularToLine(node, wall.CenterLine))
                {
                    continue;
                }

                var hostLength = Math.Max(wall.CenterLine.Length, 1);
                var parameterTolerance = reviewTolerance / hostLength;
                var parameter = wall.CenterLine.ProjectParameter(node.Position);
                if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
                {
                    continue;
                }

                var projected = wall.CenterLine.PointAt(Math.Clamp(parameter, 0, 1));
                var distance = node.Position.DistanceTo(projected);
                if (distance <= minimumReviewDistance || distance > reviewTolerance)
                {
                    continue;
                }

                var involvedWallIds = nodeWallIds.Concat(new[] { wall.Id }).ToArray();
                if (!ShouldReviewEndpointGap(involvedWallIds, componentByWallId))
                {
                    continue;
                }

                var key = $"wall:{node.Id}:{wall.Id}";
                if (!keys.Contains(key))
                {
                    best = ChooseNearest(
                        best,
                        CreateEndpointGapReview(
                            node,
                            projected,
                            targetNodeId: null,
                            hostWallId: wall.Id,
                            WallGraphRepairCandidateKind.EndpointToWall,
                            distance,
                            involvedWallIds,
                            wallsById,
                            options));
                }
            }

            foreach (var other in endpointNodes
                         .Where(other => other.PageNumber == node.PageNumber)
                         .Where(other => string.CompareOrdinal(other.Id, node.Id) > 0))
            {
                if (!HasPerpendicularDirections(node, other))
                {
                    continue;
                }

                var otherWallIds = WallIdsForNode(other.Id, incidentEdgesByNode)
                    .ToHashSet(StringComparer.Ordinal);
                if (otherWallIds.Count == 0 || nodeWallIds.Overlaps(otherWallIds))
                {
                    continue;
                }

                var involvedWallIds = nodeWallIds.Concat(otherWallIds).ToArray();
                if (!ShouldReviewEndpointGap(involvedWallIds, componentByWallId))
                {
                    continue;
                }

                var distance = node.Position.DistanceTo(other.Position);
                if (distance <= minimumReviewDistance || distance > reviewTolerance)
                {
                    continue;
                }

                var pairKey = string.CompareOrdinal(node.Id, other.Id) < 0
                    ? $"node:{node.Id}:{other.Id}"
                    : $"node:{other.Id}:{node.Id}";
                if (!keys.Contains(pairKey))
                {
                    best = ChooseNearest(
                        best,
                        CreateEndpointGapReview(
                            node,
                            other.Position,
                            other.Id,
                            hostWallId: null,
                            WallGraphRepairCandidateKind.EndpointToEndpoint,
                            distance,
                            involvedWallIds,
                            wallsById,
                            options));
                }
            }

            if (best is null)
            {
                continue;
            }

            var bestKey = best.Kind == WallGraphRepairCandidateKind.EndpointToWall
                ? $"wall:{best.SourceNodeId}:{best.HostWallId}"
                : string.CompareOrdinal(best.SourceNodeId, best.TargetNodeId) < 0
                    ? $"node:{best.SourceNodeId}:{best.TargetNodeId}"
                    : $"node:{best.TargetNodeId}:{best.SourceNodeId}";
            if (keys.Add(bestKey))
            {
                candidates.Add(best);
            }
        }

        return candidates
            .OrderBy(candidate => candidate.PageNumber)
            .ThenBy(candidate => candidate.GapDistance)
            .ThenBy(candidate => candidate.SourceNodeId, StringComparer.Ordinal)
            .GroupBy(candidate => EndpointGapDedupeKey(candidate, Math.Max(2, options.WallSnapTolerance * 2.0)), StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(options.MaxWallGraphEndpointGapReviewItems)
            .ToArray();
    }

    private static string EndpointGapDedupeKey(WallGraphRepairCandidate gap, double bucketSize)
    {
        var center = gap.Bounds.Center;
        var xBucket = Math.Round(center.X / bucketSize);
        var yBucket = Math.Round(center.Y / bucketSize);
        return string.Join(
            ":",
            gap.PageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", gap.WallIds),
            xBucket.ToString(System.Globalization.CultureInfo.InvariantCulture),
            yBucket.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildComponentByWallId(
        IReadOnlyList<WallGraphComponent> components)
    {
        var lookup = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    lookup[wallId] = component;
                }
            }
        }

        return lookup;
    }

    private static bool ShouldReviewEndpointGap(
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId)
    {
        var components = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => componentByWallId.TryGetValue(id, out var component) ? component : null)
            .Where(component => component is not null)
            .Cast<WallGraphComponent>()
            .DistinctBy(component => component.Id)
            .ToArray();

        if (components.Any(component =>
                component.ExcludedFromStructuralTopology
                || component.Kind == WallGraphComponentKind.ObjectLikeIsland))
        {
            return false;
        }

        return components.Any(component =>
            component.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural);
    }

    private static bool ContainsObjectLikeWall(
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId) =>
        wallIds.Any(id =>
            componentByWallId.TryGetValue(id, out var component)
            && (component.ExcludedFromStructuralTopology
                || component.Kind == WallGraphComponentKind.ObjectLikeIsland));

    private static WallGraphRepairCandidate? ChooseNearest(WallGraphRepairCandidate? current, WallGraphRepairCandidate candidate) =>
        current is null || candidate.GapDistance < current.GapDistance
            ? candidate
            : current;

    private static WallGraphRepairCandidate CreateEndpointGapReview(
        WallNode node,
        PlanPoint targetPoint,
        string? targetNodeId,
        string? hostWallId,
        WallGraphRepairCandidateKind kind,
        double distance,
        IEnumerable<string> wallIds,
        IReadOnlyDictionary<string, WallSegment> wallsById,
        ScannerOptions options)
    {
        var orderedWallIds = wallIds
            .Where(id => !string.IsNullOrWhiteSpace(id) && wallsById.ContainsKey(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = orderedWallIds
            .SelectMany(id => wallsById[id].SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var inflation = Math.Max(options.DefaultWallThickness, options.WallSnapTolerance * 3.0);
        var bounds = PlanRect.FromPoints(node.Position, targetPoint).Inflate(inflation);
        var evidence = new[]
        {
            $"{kind} gap {distance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} drawing units",
            "outside safe snap tolerance; review before inferring a wall junction",
            $"candidate wall ids: {string.Join(",", orderedWallIds)}"
        };

        var action = kind == WallGraphRepairCandidateKind.EndpointToWall
            ? WallGraphRepairAction.SnapEndpointToWall
            : WallGraphRepairAction.SnapEndpointToEndpoint;

        return new WallGraphRepairCandidate(
            RepairCandidateId(node.PageNumber, node.Id, targetNodeId, hostWallId, kind),
            node.PageNumber,
            kind,
            action,
            node.Id,
            node.Position,
            targetPoint,
            targetNodeId,
            hostWallId,
            distance,
            new PlanLineSegment(node.Position, targetPoint),
            bounds,
            orderedWallIds,
            sourcePrimitiveIds,
            Confidence.Medium,
            true,
            evidence);
    }

    private static string RepairCandidateId(
        int pageNumber,
        string sourceNodeId,
        string? targetNodeId,
        string? hostWallId,
        WallGraphRepairCandidateKind kind)
    {
        var targetId = targetNodeId ?? hostWallId ?? "unknown-target";
        return string.Join(
            ":",
            "page",
            pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "wall-graph-repair",
            kind.ToString(),
            sourceNodeId,
            targetId);
    }

    private static void AddEndpointGapDiagnostics(
        ScanContext context,
        IReadOnlyList<WallGraphRepairCandidate> endpointGaps)
    {
        if (endpointGaps.Count == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "wall_graph.endpoint_gaps.detected",
            DiagnosticSeverity.Warning,
            "wall-graph",
            "Possible unsnapped wall graph endpoint gaps were found and queued for review.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: endpointGaps.SelectMany(gap => gap.SourcePrimitiveIds),
            properties: new Dictionary<string, string>
            {
                ["gapCount"] = endpointGaps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["minGapDistance"] = endpointGaps.Min(gap => gap.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["maxGapDistance"] = endpointGaps.Max(gap => gap.GapDistance).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                ["gapKinds"] = string.Join(",", endpointGaps.Select(gap => gap.Kind.ToString()).Distinct(StringComparer.Ordinal))
            });

        foreach (var gap in endpointGaps)
        {
            context.AddDiagnostic(
                "wall_graph.endpoint_gap.review",
                DiagnosticSeverity.Warning,
                "wall-graph",
                "A wall graph endpoint nearly touches another wall endpoint or host wall but was not safely snapped.",
                pageNumber: gap.PageNumber,
                region: gap.Bounds,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: gap.SourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["gapKind"] = gap.Kind.ToString(),
                    ["gapDistance"] = gap.GapDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["repairCandidateId"] = gap.Id,
                    ["suggestedAction"] = gap.SuggestedAction.ToString(),
                    ["nodeId"] = gap.SourceNodeId,
                    ["targetNodeId"] = gap.TargetNodeId ?? string.Empty,
                    ["hostWallId"] = gap.HostWallId ?? string.Empty,
                    ["nodeX"] = gap.SourcePoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["nodeY"] = gap.SourcePoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["targetX"] = gap.TargetPoint.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["targetY"] = gap.TargetPoint.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                    ["wallIds"] = string.Join(",", gap.WallIds)
                });
        }
    }

    private static IReadOnlyList<string> WallIdsForNode(
        string nodeId,
        IReadOnlyDictionary<string, List<WallEdge>> incidentEdgesByNode) =>
        incidentEdgesByNode.TryGetValue(nodeId, out var incidentEdges)
            ? incidentEdges
                .Select(edge => edge.WallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    private static bool IsEndpointDirectionPerpendicularToLine(WallNode node, PlanLineSegment line)
    {
        var lineIsHorizontal = Math.Abs(line.End.X - line.Start.X) >= Math.Abs(line.End.Y - line.Start.Y);
        return lineIsHorizontal
            ? HasVerticalDirection(node)
            : HasHorizontalDirection(node);
    }

    private static bool HasPerpendicularDirections(WallNode first, WallNode second) =>
        HasHorizontalDirection(first) && HasVerticalDirection(second)
        || HasVerticalDirection(first) && HasHorizontalDirection(second);

    private static bool HasHorizontalDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.East), StringComparer.Ordinal)
        || node.Directions.Contains(nameof(DirectionBucket.West), StringComparer.Ordinal);

    private static bool HasVerticalDirection(WallNode node) =>
        node.Directions.Contains(nameof(DirectionBucket.North), StringComparer.Ordinal)
        || node.Directions.Contains(nameof(DirectionBucket.South), StringComparer.Ordinal);

    private static double UnresolvedEndpointGapReviewTolerance(ScannerOptions options)
    {
        var autoConnectTolerance = InferredNearTouchJunctionTolerance(options);
        var geometryLimit = Math.Max(options.MaxWallFragmentGap * 3.0, options.DefaultWallThickness * 4.0);
        var openingAwareLimit = Math.Max(autoConnectTolerance + options.WallSnapTolerance, options.MaxOpeningGap * 0.35);
        return Math.Min(Math.Max(autoConnectTolerance + options.WallSnapTolerance, geometryLimit), openingAwareLimit);
    }

    private static IReadOnlyList<PlanPoint> InferNearTouchJunctions(
        PlanLineSegment first,
        PlanLineSegment second,
        double tolerance,
        double duplicateTolerance)
    {
        if (!IsNearPerpendicular(first, second))
        {
            return Array.Empty<PlanPoint>();
        }

        var points = new List<PlanPoint>();
        AddProjectedEndpointJunction(first.Start, second, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(first.End, second, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(second.Start, first, tolerance, duplicateTolerance, points);
        AddProjectedEndpointJunction(second.End, first, tolerance, duplicateTolerance, points);
        return points;
    }

    private static void AddProjectedEndpointJunction(
        PlanPoint endpoint,
        PlanLineSegment host,
        double tolerance,
        double duplicateTolerance,
        List<PlanPoint> points)
    {
        var hostLength = Math.Max(host.Length, 1);
        var parameterTolerance = tolerance / hostLength;
        var parameter = host.ProjectParameter(endpoint);
        if (parameter < -parameterTolerance || parameter > 1 + parameterTolerance)
        {
            return;
        }

        var projected = host.PointAt(Math.Clamp(parameter, 0, 1));
        if (endpoint.DistanceTo(projected) > tolerance)
        {
            return;
        }

        if (points.Any(existing => existing.DistanceTo(projected) <= duplicateTolerance))
        {
            return;
        }

        points.Add(projected);
    }

    private static bool IsNearPerpendicular(PlanLineSegment first, PlanLineSegment second)
    {
        var delta = Math.Abs(
            GeometryOperations.NormalizeAngleRadians(first.AngleRadians)
            - GeometryOperations.NormalizeAngleRadians(second.AngleRadians));
        delta = Math.Min(delta, Math.PI - delta);
        return Math.Abs(delta - (Math.PI / 2.0)) <= 0.20;
    }

    private static double InferredNearTouchJunctionTolerance(ScannerOptions options) =>
        Math.Max(
            options.WallSnapTolerance,
            Math.Min(
                options.MaxWallFragmentGap,
                Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness * 1.5)));

    private static NodeClassification ClassifyNode(NodeAccumulator node)
    {
        var directions = node.IncidentDirections
            .Select(DirectionName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(direction => DirectionSortOrder(direction))
            .ToArray();
        var evidence = new List<string>
        {
            $"degree {node.Degree}"
        };

        if (directions.Length > 0)
        {
            evidence.Add($"directions {string.Join(", ", directions)}");
        }

        var kind = node.Degree switch
        {
            <= 1 => WallNodeKind.Endpoint,
            2 when HasOppositePair(node.IncidentDirections) => WallNodeKind.Inline,
            2 when HasPerpendicularPair(node.IncidentDirections) => WallNodeKind.Corner,
            3 when HasOppositePair(node.IncidentDirections) && HasPerpendicularPair(node.IncidentDirections) => WallNodeKind.TJunction,
            >= 4 when HasHorizontalOpposite(node.IncidentDirections) && HasVerticalOpposite(node.IncidentDirections) => WallNodeKind.Crossing,
            _ => WallNodeKind.Junction
        };

        evidence.Add($"classified {kind}");
        return new NodeClassification(kind, directions, evidence);
    }

    private static bool HasOppositePair(IEnumerable<DirectionBucket> directions) =>
        HasHorizontalOpposite(directions) || HasVerticalOpposite(directions);

    private static bool HasHorizontalOpposite(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        return set.Contains(DirectionBucket.East) && set.Contains(DirectionBucket.West);
    }

    private static bool HasVerticalOpposite(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        return set.Contains(DirectionBucket.North) && set.Contains(DirectionBucket.South);
    }

    private static bool HasPerpendicularPair(IEnumerable<DirectionBucket> directions)
    {
        var set = directions.ToHashSet();
        var hasHorizontal = set.Contains(DirectionBucket.East) || set.Contains(DirectionBucket.West);
        var hasVertical = set.Contains(DirectionBucket.North) || set.Contains(DirectionBucket.South);
        return hasHorizontal && hasVertical;
    }

    private static string DirectionName(DirectionBucket direction) =>
        direction.ToString();

    private static int DirectionSortOrder(string direction) =>
        direction switch
        {
            nameof(DirectionBucket.North) => 0,
            nameof(DirectionBucket.East) => 1,
            nameof(DirectionBucket.South) => 2,
            nameof(DirectionBucket.West) => 3,
            _ => 4
        };

    private static NodeAccumulator GetOrCreateNode(
        List<NodeAccumulator> nodes,
        int pageNumber,
        PlanPoint point,
        ScannerOptions options)
    {
        var existing = nodes.FirstOrDefault(node =>
            node.PageNumber == pageNumber
            && node.Position.DistanceTo(point) <= options.WallSnapTolerance);

        if (existing is not null)
        {
            existing.Position = new PlanPoint(
                (existing.Position.X + point.X) / 2.0,
                (existing.Position.Y + point.Y) / 2.0);
            return existing;
        }

        var node = new NodeAccumulator(
            $"page:{pageNumber}:node:{nodes.Count + 1}",
            pageNumber,
            point);

        nodes.Add(node);
        return node;
    }

    private sealed class NodeAccumulator
    {
        public NodeAccumulator(string id, int pageNumber, PlanPoint position)
        {
            Id = id;
            PageNumber = pageNumber;
            Position = position;
        }

        public string Id { get; }

        public int PageNumber { get; }

        public PlanPoint Position { get; set; }

        public List<DirectionBucket> IncidentDirections { get; } = new();

        public int Degree => IncidentDirections.Count;

        public void AddIncidentDirection(PlanPoint from, PlanPoint to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            if (Math.Abs(dx) <= double.Epsilon && Math.Abs(dy) <= double.Epsilon)
            {
                IncidentDirections.Add(DirectionBucket.Other);
                return;
            }

            if (Math.Abs(dx) >= Math.Abs(dy))
            {
                IncidentDirections.Add(dx >= 0 ? DirectionBucket.East : DirectionBucket.West);
            }
            else
            {
                IncidentDirections.Add(dy >= 0 ? DirectionBucket.South : DirectionBucket.North);
            }
        }
    }

    private sealed record NodeClassification(
        WallNodeKind Kind,
        IReadOnlyList<string> Directions,
        IReadOnlyList<string> Evidence);

    private sealed record RawWallGraphComponent(
        int PageNumber,
        IReadOnlyList<string> WallIds,
        IReadOnlyList<string> NodeIds,
        IReadOnlyList<string> EdgeIds,
        IReadOnlyList<string> SourcePrimitiveIds,
        PlanRect Bounds,
        double DrawingLength);

    private enum DirectionBucket
    {
        North,
        East,
        South,
        West,
        Other
    }
}
