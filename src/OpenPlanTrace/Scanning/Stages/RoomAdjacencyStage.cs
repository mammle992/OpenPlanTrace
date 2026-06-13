namespace OpenPlanTrace;

internal sealed class RoomAdjacencyStage : IPipelineStage
{
    public string Name => "room-adjacency";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        var edges = new List<RoomAdjacencyEdge>();

        foreach (var pageGroup in context.Rooms.GroupBy(room => room.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rooms = pageGroup
                .OrderBy(room => room.Bounds.Top)
                .ThenBy(room => room.Bounds.Left)
                .ToArray();

            for (var firstIndex = 0; firstIndex < rooms.Length; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < rooms.Length; secondIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var edge = TryCreateAdjacency(
                        rooms[firstIndex],
                        rooms[secondIndex],
                        context,
                        edges.Count + 1);

                    if (edge is not null)
                    {
                        edges.Add(edge);
                    }
                }
            }

            AddPageDiagnostics(pageGroup.Key, rooms, edges.Where(edge => edge.PageNumber == pageGroup.Key).ToArray(), context);
        }

        EnrichOpeningsWithRoomConnectivity(context, edges);
        var clusters = BuildRoomClusters(context, edges);
        AddClusterDiagnostics(context, clusters);
        context.RoomAdjacencyGraph = new RoomAdjacencyGraph(edges.ToArray(), clusters);
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<RoomCluster> BuildRoomClusters(
        ScanContext context,
        IReadOnlyList<RoomAdjacencyEdge> edges)
    {
        var clusters = new List<RoomCluster>();

        foreach (var pageGroup in context.Rooms.GroupBy(room => room.PageNumber))
        {
            var rooms = pageGroup
                .OrderBy(room => room.Bounds.Top)
                .ThenBy(room => room.Bounds.Left)
                .ThenBy(room => room.Id, StringComparer.Ordinal)
                .ToArray();
            var roomsById = rooms.ToDictionary(room => room.Id, StringComparer.Ordinal);
            var edgesOnPage = edges
                .Where(edge => edge.PageNumber == pageGroup.Key)
                .ToArray();
            var neighbors = roomsById.Keys.ToDictionary(
                roomId => roomId,
                _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);

            foreach (var edge in edgesOnPage)
            {
                if (neighbors.TryGetValue(edge.FirstRoomId, out var firstNeighbors)
                    && neighbors.TryGetValue(edge.SecondRoomId, out var secondNeighbors))
                {
                    firstNeighbors.Add(edge.SecondRoomId);
                    secondNeighbors.Add(edge.FirstRoomId);
                }
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var room in rooms)
            {
                if (!visited.Add(room.Id))
                {
                    continue;
                }

                var component = new List<RoomRegion>();
                var queue = new Queue<string>();
                queue.Enqueue(room.Id);

                while (queue.Count > 0)
                {
                    var roomId = queue.Dequeue();
                    if (!roomsById.TryGetValue(roomId, out var current))
                    {
                        continue;
                    }

                    component.Add(current);
                    foreach (var neighborId in neighbors[roomId].Order(StringComparer.Ordinal))
                    {
                        if (visited.Add(neighborId))
                        {
                            queue.Enqueue(neighborId);
                        }
                    }
                }

                clusters.Add(CreateRoomCluster(pageGroup.Key, clusters.Count + 1, component, edgesOnPage));
            }
        }

        return clusters.ToArray();
    }

    private static RoomCluster CreateRoomCluster(
        int pageNumber,
        int clusterNumber,
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<RoomAdjacencyEdge> pageEdges)
    {
        var roomIds = rooms
            .Select(room => room.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var roomIdSet = roomIds.ToHashSet(StringComparer.Ordinal);
        var clusterEdges = pageEdges
            .Where(edge => roomIdSet.Contains(edge.FirstRoomId) && roomIdSet.Contains(edge.SecondRoomId))
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        var openingIds = clusterEdges
            .SelectMany(edge => edge.OpeningIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var roomLabels = rooms
            .Select(room => room.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var drawingArea = rooms.Sum(room => room.DrawingArea);
        var areaSquareMeters = rooms.All(room => room.AreaSquareMeters is not null)
            ? rooms.Sum(room => room.AreaSquareMeters!.Value)
            : (double?)null;
        var confidenceValues = rooms
            .Select(room => room.Confidence.Value)
            .Concat(clusterEdges.Select(edge => edge.Confidence.Value))
            .ToArray();
        var confidence = confidenceValues.Length == 0
            ? Confidence.Low
            : new Confidence(Math.Clamp(confidenceValues.Average(), 0.1, 1.0));
        var evidence = new List<string>
        {
            rooms.Count == 1
                ? "singleton room cluster"
                : $"connected room cluster with {rooms.Count} rooms",
            $"{clusterEdges.Length} adjacency edge(s)"
        };

        if (openingIds.Length > 0)
        {
            evidence.Add($"{openingIds.Length} connected opening(s)");
        }

        var classification = ClassifyRoomCluster(rooms, clusterEdges, openingIds);
        evidence.Add($"cluster kind {classification.Kind}");
        evidence.AddRange(classification.Evidence);

        return new RoomCluster(
            $"page:{pageNumber}:room-cluster:{clusterNumber}",
            pageNumber,
            roomIds,
            roomLabels,
            PlanRect.Union(rooms.Select(room => room.Bounds)),
            drawingArea,
            areaSquareMeters,
            clusterEdges.Select(edge => edge.Id).ToArray(),
            openingIds,
            confidence,
            evidence)
        {
            Kind = classification.Kind
        };
    }

    private static RoomClusterClassification ClassifyRoomCluster(
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<RoomAdjacencyEdge> clusterEdges,
        IReadOnlyList<string> openingIds)
    {
        var labels = rooms
            .Select(room => room.Label)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label!)
            .ToArray();

        if (TryMatchRoomUse(labels, OpenPlanTerms, out var openPlanTerm))
        {
            return new RoomClusterClassification(
                RoomClusterKind.OpenPlan,
                new[] { $"label evidence matched open-plan term '{openPlanTerm}'" });
        }

        if (TryMatchRoomUse(labels, CorridorTerms, out var corridorTerm))
        {
            return new RoomClusterClassification(
                RoomClusterKind.CorridorLike,
                new[] { $"label evidence matched circulation term '{corridorTerm}'" });
        }

        var corridorLikeRoom = rooms.FirstOrDefault(IsCorridorLikeShape);
        if (corridorLikeRoom is not null)
        {
            var ratio = AspectRatio(corridorLikeRoom.Bounds);
            return new RoomClusterClassification(
                RoomClusterKind.CorridorLike,
                new[] { $"long narrow room geometry {Math.Round(ratio, 2)}:1 on {corridorLikeRoom.Id}" });
        }

        if (rooms.Count == 1)
        {
            return new RoomClusterClassification(RoomClusterKind.SingleRoom, Array.Empty<string>());
        }

        if (openingIds.Count > 0 || clusterEdges.Any(edge => edge.Kind == RoomAdjacencyKind.ConnectedByOpening))
        {
            return new RoomClusterClassification(
                RoomClusterKind.ConnectedSuite,
                new[] { "rooms connected through one or more opening candidates" });
        }

        return new RoomClusterClassification(
            RoomClusterKind.CompartmentGroup,
            new[] { "rooms share boundaries without detected opening links" });
    }

    private static bool TryMatchRoomUse(
        IReadOnlyList<string> labels,
        IReadOnlyList<string> terms,
        out string matchedTerm)
    {
        foreach (var label in labels)
        {
            var normalized = NormalizeLabel(label);
            foreach (var term in terms)
            {
                if (normalized.Contains(term, StringComparison.Ordinal))
                {
                    matchedTerm = term;
                    return true;
                }
            }
        }

        matchedTerm = string.Empty;
        return false;
    }

    private static bool IsCorridorLikeShape(RoomRegion room)
    {
        if (room.Bounds.IsEmpty)
        {
            return false;
        }

        var smallerSpan = Math.Min(room.Bounds.Width, room.Bounds.Height);
        var largerSpan = Math.Max(room.Bounds.Width, room.Bounds.Height);
        if (smallerSpan <= 0)
        {
            return false;
        }

        return largerSpan / smallerSpan >= 4.0;
    }

    private static double AspectRatio(PlanRect bounds)
    {
        var smallerSpan = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
        var largerSpan = Math.Max(bounds.Width, bounds.Height);
        return largerSpan / smallerSpan;
    }

    private static string NormalizeLabel(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(
            ' ',
            builder
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void AddClusterDiagnostics(
        ScanContext context,
        IReadOnlyList<RoomCluster> clusters)
    {
        foreach (var pageGroup in clusters.GroupBy(cluster => cluster.PageNumber))
        {
            var pageClusters = pageGroup.ToArray();
            var singletonCount = pageClusters.Count(cluster => cluster.RoomIds.Count == 1);
            var largestClusterSize = pageClusters
                .Select(cluster => cluster.RoomIds.Count)
                .DefaultIfEmpty(0)
                .Max();
            var kindCounts = pageClusters
                .GroupBy(cluster => cluster.Kind)
                .ToDictionary(group => group.Key, group => group.Count());

            context.AddDiagnostic(
                "rooms.clusters.detected",
                DiagnosticSeverity.Info,
                "room-adjacency",
                $"Detected {pageClusters.Length} room cluster(s).",
                pageGroup.Key,
                PlanRect.Union(pageClusters.Select(cluster => cluster.Bounds)),
                Confidence.Medium,
                scope: DiagnosticScope.Room,
                properties: new Dictionary<string, string>
                {
                    ["clusterCount"] = pageClusters.Length.ToString(),
                    ["singletonClusterCount"] = singletonCount.ToString(),
                    ["largestClusterRoomCount"] = largestClusterSize.ToString(),
                    ["singleRoomClusterCount"] = CountKind(kindCounts, RoomClusterKind.SingleRoom).ToString(),
                    ["connectedSuiteClusterCount"] = CountKind(kindCounts, RoomClusterKind.ConnectedSuite).ToString(),
                    ["compartmentGroupClusterCount"] = CountKind(kindCounts, RoomClusterKind.CompartmentGroup).ToString(),
                    ["corridorLikeClusterCount"] = CountKind(kindCounts, RoomClusterKind.CorridorLike).ToString(),
                    ["openPlanClusterCount"] = CountKind(kindCounts, RoomClusterKind.OpenPlan).ToString()
                });
        }
    }

    private static int CountKind(
        IReadOnlyDictionary<RoomClusterKind, int> counts,
        RoomClusterKind kind) =>
        counts.TryGetValue(kind, out var count) ? count : 0;

    private static void EnrichOpeningsWithRoomConnectivity(
        ScanContext context,
        IReadOnlyList<RoomAdjacencyEdge> edges)
    {
        if (context.Openings.Count == 0 || context.Rooms.Count == 0)
        {
            return;
        }

        var connectivityByOpeningId = context.Openings
            .ToDictionary(
                opening => opening.Id,
                opening => new OpeningRoomConnectivity(opening),
                StringComparer.Ordinal);
        var openingsById = context.Openings.ToDictionary(opening => opening.Id, StringComparer.Ordinal);
        var roomsById = context.Rooms.ToDictionary(room => room.Id, StringComparer.Ordinal);

        foreach (var edge in edges)
        {
            foreach (var openingId in edge.OpeningIds)
            {
                if (!connectivityByOpeningId.TryGetValue(openingId, out var connectivity))
                {
                    continue;
                }

                if (openingsById.TryGetValue(openingId, out var opening)
                    && roomsById.TryGetValue(edge.FirstRoomId, out var firstRoom))
                {
                    connectivity.AddRoomConnection(
                        opening,
                        firstRoom,
                        edge.Id,
                        $"connected to room through adjacency {edge.Id}",
                        context.Options.WallSnapTolerance);
                }
                else
                {
                    connectivity.AddRoom(edge.FirstRoomId, edge.FirstRoomLabel);
                }

                if (openingsById.TryGetValue(openingId, out opening)
                    && roomsById.TryGetValue(edge.SecondRoomId, out var secondRoom))
                {
                    connectivity.AddRoomConnection(
                        opening,
                        secondRoom,
                        edge.Id,
                        $"connected to room through adjacency {edge.Id}",
                        context.Options.WallSnapTolerance);
                }
                else
                {
                    connectivity.AddRoom(edge.SecondRoomId, edge.SecondRoomLabel);
                }

                connectivity.AddAdjacency(edge.Id);
                connectivity.AddEvidence($"connected rooms from adjacency {edge.Id}");
            }
        }

        foreach (var pair in context.Openings.Select((opening, index) => new { opening, index }).ToArray())
        {
            var connectedRooms = context.Rooms
                .Where(room => room.PageNumber == pair.opening.PageNumber)
                .Where(room => OpeningTouchesRoom(pair.opening, room, context.Options.WallSnapTolerance))
                .OrderBy(room => room.Bounds.Center.DistanceTo(pair.opening.CenterLine.Midpoint))
                .ToArray();

            if (!connectivityByOpeningId.TryGetValue(pair.opening.Id, out var connectivity))
            {
                continue;
            }

            foreach (var room in connectedRooms)
            {
                connectivity.AddRoomConnection(
                    pair.opening,
                    room,
                    adjacencyId: null,
                    evidenceValue: "opening touches room boundary",
                    tolerance: context.Options.WallSnapTolerance);
            }

            if (connectedRooms.Length > 0)
            {
                connectivity.AddEvidence(
                    connectedRooms.Length == 1
                        ? $"touches room boundary {connectedRooms[0].Id}"
                        : $"touches {connectedRooms.Length} room boundaries");
            }

            if (!connectivity.HasChanges)
            {
                continue;
            }

            context.Openings[pair.index] = pair.opening with
            {
                ConnectedRoomIds = connectivity.RoomIds,
                ConnectedRoomLabels = connectivity.RoomLabels,
                ConnectedRoomLinks = connectivity.RoomLinks,
                RoomAdjacencyIds = connectivity.RoomAdjacencyIds,
                Evidence = pair.opening.Evidence
                    .Concat(connectivity.Evidence)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        AddOpeningConnectivityDiagnostics(context);
    }

    private static bool OpeningTouchesRoom(
        OpeningCandidate opening,
        RoomRegion room,
        double tolerance)
    {
        if (!room.Bounds.Inflate(Math.Max(2, tolerance * 3)).Intersects(opening.Bounds))
        {
            return false;
        }

        if (opening.HostWallIds.Count > 0
            && room.WallIds.Intersect(opening.HostWallIds, StringComparer.Ordinal).Any())
        {
            return true;
        }

        var distanceTolerance = Math.Max(2, tolerance * 3);
        return BoundaryEdges(room).Any(edge =>
            edge.DistanceToPoint(opening.CenterLine.Midpoint) <= distanceTolerance
            || edge.Bounds.Inflate(distanceTolerance).Intersects(opening.Bounds));
    }

    private static void AddOpeningConnectivityDiagnostics(ScanContext context)
    {
        foreach (var pageGroup in context.Openings
            .Where(opening => opening.ConnectedRoomIds.Count > 0)
            .GroupBy(opening => opening.PageNumber))
        {
            var openings = pageGroup.ToArray();
            var linkedOpeningCount = openings.Length;
            var multiRoomOpeningCount = openings.Count(opening => opening.ConnectedRoomIds.Count >= 2);
            var adjacencyLinkedOpeningCount = openings.Count(opening => opening.RoomAdjacencyIds.Count > 0);
            var connectionLinkCount = openings.Sum(opening => opening.ConnectedRoomLinks.Count);
            var hostWallLinkCount = openings.Sum(opening => opening.ConnectedRoomLinks.Count(link => link.SharesHostWall));

            context.AddDiagnostic(
                "openings.room_connectivity.detected",
                DiagnosticSeverity.Info,
                "room-adjacency",
                $"Attached room connectivity to {linkedOpeningCount} opening candidate(s).",
                pageGroup.Key,
                PlanRect.Union(openings.Select(opening => opening.Bounds)),
                Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: openings.SelectMany(opening => opening.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["openingCount"] = linkedOpeningCount.ToString(),
                    ["multiRoomOpeningCount"] = multiRoomOpeningCount.ToString(),
                    ["adjacencyLinkedOpeningCount"] = adjacencyLinkedOpeningCount.ToString(),
                    ["connectionLinkCount"] = connectionLinkCount.ToString(),
                    ["hostWallConnectionLinkCount"] = hostWallLinkCount.ToString()
                });
        }
    }

    private static double DistanceToRoomBoundary(OpeningCandidate opening, RoomRegion room)
    {
        var midpoint = opening.CenterLine.Midpoint;
        var distances = BoundaryEdges(room)
            .Select(edge => edge.DistanceToPoint(midpoint))
            .ToArray();
        return Math.Round(distances.Length == 0 ? midpoint.DistanceTo(room.Bounds.Center) : distances.Min(), 3);
    }

    private static bool SharesHostWall(OpeningCandidate opening, RoomRegion room) =>
        opening.HostWallIds.Count > 0
        && room.WallIds.Intersect(opening.HostWallIds, StringComparer.Ordinal).Any();

    private static Confidence RoomConnectionConfidence(
        double distanceToOpening,
        bool sharesHostWall,
        bool hasAdjacency,
        double tolerance)
    {
        var distanceTolerance = Math.Max(2, tolerance * 3);
        var distanceScore = distanceToOpening <= distanceTolerance
            ? 0.18
            : distanceToOpening <= distanceTolerance * 2
                ? 0.08
                : 0.0;
        var value = 0.42
            + distanceScore
            + (sharesHostWall ? 0.16 : 0)
            + (hasAdjacency ? 0.18 : 0);

        return new Confidence(Math.Clamp(value, 0.42, 0.94));
    }

    private static RoomAdjacencyEdge? TryCreateAdjacency(
        RoomRegion first,
        RoomRegion second,
        ScanContext context,
        int edgeNumber)
    {
        var intersectingWallIds = first.WallIds
            .Intersect(second.WallIds, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sharedBoundary = LongestSharedBoundary(first, second, context.Options.WallSnapTolerance);
        var sharedWallIds = FilterSharedWallIds(
                intersectingWallIds,
                sharedBoundary,
                context,
                context.Options.WallSnapTolerance)
            .ToArray();

        if (intersectingWallIds.Length == 0 && sharedBoundary is null)
        {
            return null;
        }

        var openingIds = MatchingOpeningIds(first.PageNumber, sharedWallIds, sharedBoundary, context).ToArray();
        var kind = openingIds.Length > 0
            ? RoomAdjacencyKind.ConnectedByOpening
            : RoomAdjacencyKind.BoundaryAdjacent;
        var sharedLength = sharedBoundary?.Length ?? EstimateSharedLengthFromWalls(sharedWallIds, context);
        var directionFromFirstToSecond = DirectionBetween(first.Bounds.Center, second.Bounds.Center);
        var directionFromSecondToFirst = Opposite(directionFromFirstToSecond);
        var confidence = AdjacencyConfidence(sharedWallIds.Length, openingIds.Length, sharedLength, first, second);
        var evidence = Evidence(sharedWallIds, openingIds, sharedBoundary, sharedLength, directionFromFirstToSecond).ToArray();

        return new RoomAdjacencyEdge(
            $"page:{first.PageNumber}:room-adjacency:{edgeNumber}",
            first.PageNumber,
            first.Id,
            first.Label,
            second.Id,
            second.Label,
            kind,
            directionFromFirstToSecond,
            directionFromSecondToFirst,
            sharedLength,
            sharedBoundary,
            confidence,
            sharedWallIds,
            openingIds,
            evidence);
    }

    private static IEnumerable<string> MatchingOpeningIds(
        int pageNumber,
        IReadOnlyList<string> sharedWallIds,
        PlanLineSegment? sharedBoundary,
        ScanContext context)
    {
        foreach (var opening in context.Openings.Where(opening => opening.PageNumber == pageNumber))
        {
            if (sharedWallIds.Count > 0
                && opening.HostWallIds.Intersect(sharedWallIds, StringComparer.Ordinal).Any())
            {
                yield return opening.Id;
                continue;
            }

            if (sharedBoundary is { } boundary
                && IsOpeningOnBoundary(opening, boundary, context.Options.WallSnapTolerance))
            {
                yield return opening.Id;
            }
        }
    }

    private static IEnumerable<string> FilterSharedWallIds(
        IReadOnlyList<string> candidateWallIds,
        PlanLineSegment? sharedBoundary,
        ScanContext context,
        double tolerance)
    {
        if (sharedBoundary is null)
        {
            return candidateWallIds;
        }

        return context.Walls
            .Where(wall => candidateWallIds.Contains(wall.Id, StringComparer.Ordinal)
                && SharedOverlap(wall.CenterLine, sharedBoundary.Value, tolerance) is not null)
            .Select(wall => wall.Id)
            .Distinct(StringComparer.Ordinal);
    }

    private static bool IsOpeningOnBoundary(
        OpeningCandidate opening,
        PlanLineSegment boundary,
        double tolerance)
    {
        var expanded = boundary.Bounds.Inflate(Math.Max(2, tolerance * 2));
        return opening.Bounds.Intersects(expanded)
            || boundary.DistanceToPoint(opening.CenterLine.Midpoint) <= Math.Max(2, tolerance * 2);
    }

    private static Confidence AdjacencyConfidence(
        int sharedWallCount,
        int openingCount,
        double sharedLength,
        RoomRegion first,
        RoomRegion second)
    {
        var smallerRoomSpan = Math.Max(
            1,
            Math.Min(
                Math.Min(first.Bounds.Width, first.Bounds.Height),
                Math.Min(second.Bounds.Width, second.Bounds.Height)));
        var lengthRatio = Math.Clamp(sharedLength / smallerRoomSpan, 0, 1);
        var value = 0.48
            + Math.Min(0.18, sharedWallCount * 0.08)
            + (lengthRatio * 0.18)
            + Math.Min(0.16, openingCount * 0.12);

        return new Confidence(Math.Clamp(value, 0.45, 0.94));
    }

    private static IEnumerable<string> Evidence(
        IReadOnlyList<string> sharedWallIds,
        IReadOnlyList<string> openingIds,
        PlanLineSegment? sharedBoundary,
        double sharedLength,
        RoomAdjacencyDirection direction)
    {
        if (sharedWallIds.Count > 0)
        {
            yield return $"rooms share {sharedWallIds.Count} wall id(s)";
        }

        if (sharedBoundary is not null)
        {
            yield return $"shared boundary length {Math.Round(sharedLength, 3)} drawing units";
        }

        if (openingIds.Count > 0)
        {
            yield return $"connected by {openingIds.Count} opening candidate(s)";
        }

        if (direction != RoomAdjacencyDirection.Unknown)
        {
            yield return $"second room is {direction} of first room";
        }
    }

    private static RoomAdjacencyDirection DirectionBetween(PlanPoint firstCenter, PlanPoint secondCenter)
    {
        var dx = secondCenter.X - firstCenter.X;
        var dy = secondCenter.Y - firstCenter.Y;
        var absX = Math.Abs(dx);
        var absY = Math.Abs(dy);
        var minimumOffset = 1.0;

        if (absX < minimumOffset && absY < minimumOffset)
        {
            return RoomAdjacencyDirection.Unknown;
        }

        if (absX >= absY * 1.75)
        {
            return dx >= 0 ? RoomAdjacencyDirection.East : RoomAdjacencyDirection.West;
        }

        if (absY >= absX * 1.75)
        {
            return dy >= 0 ? RoomAdjacencyDirection.South : RoomAdjacencyDirection.North;
        }

        return (dx >= 0, dy >= 0) switch
        {
            (true, true) => RoomAdjacencyDirection.Southeast,
            (true, false) => RoomAdjacencyDirection.Northeast,
            (false, true) => RoomAdjacencyDirection.Southwest,
            _ => RoomAdjacencyDirection.Northwest
        };
    }

    private static RoomAdjacencyDirection Opposite(RoomAdjacencyDirection direction) =>
        direction switch
        {
            RoomAdjacencyDirection.North => RoomAdjacencyDirection.South,
            RoomAdjacencyDirection.South => RoomAdjacencyDirection.North,
            RoomAdjacencyDirection.East => RoomAdjacencyDirection.West,
            RoomAdjacencyDirection.West => RoomAdjacencyDirection.East,
            RoomAdjacencyDirection.Northeast => RoomAdjacencyDirection.Southwest,
            RoomAdjacencyDirection.Northwest => RoomAdjacencyDirection.Southeast,
            RoomAdjacencyDirection.Southeast => RoomAdjacencyDirection.Northwest,
            RoomAdjacencyDirection.Southwest => RoomAdjacencyDirection.Northeast,
            _ => RoomAdjacencyDirection.Unknown
        };

    private static double EstimateSharedLengthFromWalls(
        IReadOnlyList<string> sharedWallIds,
        ScanContext context) =>
        sharedWallIds
            .Select(id => context.Walls.FirstOrDefault(wall => wall.Id == id)?.DrawingLength ?? 0)
            .DefaultIfEmpty(0)
            .Max();

    private static PlanLineSegment? LongestSharedBoundary(
        RoomRegion first,
        RoomRegion second,
        double tolerance)
    {
        var firstEdges = BoundaryEdges(first).ToArray();
        var secondEdges = BoundaryEdges(second).ToArray();

        var sharedEdges = firstEdges
            .SelectMany(firstEdge => secondEdges.Select(secondEdge => SharedOverlap(firstEdge, secondEdge, tolerance)))
            .Where(edge => edge is not null && edge.Value.Length > Math.Max(1, tolerance))
            .Select(edge => edge!.Value)
            .ToArray();

        return sharedEdges.Length == 0
            ? null
            : sharedEdges.OrderByDescending(edge => edge.Length).First();
    }

    private static IEnumerable<PlanLineSegment> BoundaryEdges(RoomRegion room)
    {
        if (room.Boundary.Count >= 2)
        {
            for (var index = 0; index < room.Boundary.Count; index++)
            {
                yield return new PlanLineSegment(room.Boundary[index], room.Boundary[(index + 1) % room.Boundary.Count]);
            }

            yield break;
        }

        var bounds = room.Bounds;
        var topLeft = new PlanPoint(bounds.Left, bounds.Top);
        var topRight = new PlanPoint(bounds.Right, bounds.Top);
        var bottomRight = new PlanPoint(bounds.Right, bounds.Bottom);
        var bottomLeft = new PlanPoint(bounds.Left, bounds.Bottom);

        yield return new PlanLineSegment(topLeft, topRight);
        yield return new PlanLineSegment(topRight, bottomRight);
        yield return new PlanLineSegment(bottomRight, bottomLeft);
        yield return new PlanLineSegment(bottomLeft, topLeft);
    }

    private static PlanLineSegment? SharedOverlap(
        PlanLineSegment first,
        PlanLineSegment second,
        double tolerance)
    {
        if (first.IsHorizontal(tolerance) && second.IsHorizontal(tolerance))
        {
            var firstY = (first.Start.Y + first.End.Y) / 2.0;
            var secondY = (second.Start.Y + second.End.Y) / 2.0;
            if (Math.Abs(firstY - secondY) > tolerance)
            {
                return null;
            }

            var start = Math.Max(Math.Min(first.Start.X, first.End.X), Math.Min(second.Start.X, second.End.X));
            var end = Math.Min(Math.Max(first.Start.X, first.End.X), Math.Max(second.Start.X, second.End.X));
            return end > start + tolerance
                ? new PlanLineSegment(new PlanPoint(start, (firstY + secondY) / 2.0), new PlanPoint(end, (firstY + secondY) / 2.0))
                : null;
        }

        if (first.IsVertical(tolerance) && second.IsVertical(tolerance))
        {
            var firstX = (first.Start.X + first.End.X) / 2.0;
            var secondX = (second.Start.X + second.End.X) / 2.0;
            if (Math.Abs(firstX - secondX) > tolerance)
            {
                return null;
            }

            var start = Math.Max(Math.Min(first.Start.Y, first.End.Y), Math.Min(second.Start.Y, second.End.Y));
            var end = Math.Min(Math.Max(first.Start.Y, first.End.Y), Math.Max(second.Start.Y, second.End.Y));
            return end > start + tolerance
                ? new PlanLineSegment(new PlanPoint((firstX + secondX) / 2.0, start), new PlanPoint((firstX + secondX) / 2.0, end))
                : null;
        }

        return null;
    }

    private void AddPageDiagnostics(
        int pageNumber,
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<RoomAdjacencyEdge> edges,
        ScanContext context)
    {
        if (edges.Count > 0)
        {
            context.AddDiagnostic(
                "rooms.adjacency_graph.detected",
                DiagnosticSeverity.Info,
                Name,
                $"Detected {edges.Count} room adjacency edge(s).",
                pageNumber,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Room,
                sourcePrimitiveIds: SourcePrimitiveIds(edges, context),
                properties: new Dictionary<string, string>
                {
                    ["roomCount"] = rooms.Count.ToString(),
                    ["edgeCount"] = edges.Count.ToString(),
                    ["connectedByOpeningCount"] = edges.Count(edge => edge.Kind == RoomAdjacencyKind.ConnectedByOpening).ToString()
                });
            return;
        }

        if (rooms.Count >= 2)
        {
            context.AddDiagnostic(
                "rooms.adjacency_graph.none",
                DiagnosticSeverity.Info,
                Name,
                "Multiple rooms were detected, but no shared room boundaries were found.",
                pageNumber,
                confidence: Confidence.Low,
                scope: DiagnosticScope.Room,
                properties: new Dictionary<string, string>
                {
                    ["roomCount"] = rooms.Count.ToString()
                });
        }
    }

    private static IReadOnlyList<string> SourcePrimitiveIds(
        IReadOnlyList<RoomAdjacencyEdge> edges,
        ScanContext context)
    {
        var sharedWallIds = edges
            .SelectMany(edge => edge.SharedWallIds)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        if (sharedWallIds.Count == 0)
        {
            return Array.Empty<string>();
        }

        return context.Walls
            .Where(wall => sharedWallIds.Contains(wall.Id))
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class OpeningRoomConnectivity
    {
        private readonly List<string> roomIds = new();
        private readonly List<string> roomLabels = new();
        private readonly List<string> roomAdjacencyIds = new();
        private readonly List<string> evidence = new();
        private readonly Dictionary<string, OpeningRoomConnectionBuilder> roomLinks = new(StringComparer.Ordinal);
        private readonly HashSet<string> initialRoomIds;
        private readonly HashSet<string> initialRoomAdjacencyIds;
        private readonly HashSet<string> initialRoomLinkIds;

        public OpeningRoomConnectivity(OpeningCandidate opening)
        {
            roomIds.AddRange(opening.ConnectedRoomIds);
            roomLabels.AddRange(opening.ConnectedRoomLabels.Where(label => !string.IsNullOrWhiteSpace(label)));
            roomAdjacencyIds.AddRange(opening.RoomAdjacencyIds);
            foreach (var link in opening.ConnectedRoomLinks)
            {
                roomLinks[link.RoomId] = OpeningRoomConnectionBuilder.From(link);
            }

            initialRoomIds = opening.ConnectedRoomIds.ToHashSet(StringComparer.Ordinal);
            initialRoomAdjacencyIds = opening.RoomAdjacencyIds.ToHashSet(StringComparer.Ordinal);
            initialRoomLinkIds = opening.ConnectedRoomLinks
                .Select(link => link.RoomId)
                .ToHashSet(StringComparer.Ordinal);
        }

        public IReadOnlyList<string> RoomIds => roomIds.ToArray();

        public IReadOnlyList<string> RoomLabels => roomLabels.ToArray();

        public IReadOnlyList<string> RoomAdjacencyIds => roomAdjacencyIds.ToArray();

        public IReadOnlyList<OpeningRoomConnection> RoomLinks => roomLinks.Values
            .Select(builder => builder.ToConnection())
            .OrderBy(link => link.DistanceToOpening)
            .ThenBy(link => link.RoomId, StringComparer.Ordinal)
            .ToArray();

        public IReadOnlyList<string> Evidence => evidence.ToArray();

        public bool HasChanges =>
            roomIds.Any(id => !initialRoomIds.Contains(id))
            || roomAdjacencyIds.Any(id => !initialRoomAdjacencyIds.Contains(id))
            || roomLinks.Keys.Any(id => !initialRoomLinkIds.Contains(id));

        public void AddRoom(string roomId, string? roomLabel)
        {
            if (!string.IsNullOrWhiteSpace(roomId)
                && !roomIds.Contains(roomId, StringComparer.Ordinal))
            {
                roomIds.Add(roomId);
            }

            if (!string.IsNullOrWhiteSpace(roomLabel)
                && !roomLabels.Contains(roomLabel, StringComparer.Ordinal))
            {
                roomLabels.Add(roomLabel);
            }
        }

        public void AddAdjacency(string adjacencyId)
        {
            if (!string.IsNullOrWhiteSpace(adjacencyId)
                && !roomAdjacencyIds.Contains(adjacencyId, StringComparer.Ordinal))
            {
                roomAdjacencyIds.Add(adjacencyId);
            }
        }

        public void AddRoomConnection(
            OpeningCandidate opening,
            RoomRegion room,
            string? adjacencyId,
            string evidenceValue,
            double tolerance)
        {
            AddRoom(room.Id, room.Label);
            if (!string.IsNullOrWhiteSpace(adjacencyId))
            {
                AddAdjacency(adjacencyId);
            }

            if (!roomLinks.TryGetValue(room.Id, out var builder))
            {
                builder = new OpeningRoomConnectionBuilder(room.Id, room.Label, room.UseKind);
                roomLinks.Add(room.Id, builder);
            }

            var distanceToOpening = DistanceToRoomBoundary(opening, room);
            var sharesHostWall = SharesHostWall(opening, room);
            builder.RoomLabel ??= room.Label;
            builder.RoomUseKind = room.UseKind;
            builder.DistanceToOpening = Math.Min(builder.DistanceToOpening, distanceToOpening);
            builder.SharesHostWall |= sharesHostWall;
            builder.Confidence = RoomConnectionConfidence(
                builder.DistanceToOpening,
                builder.SharesHostWall,
                builder.RoomAdjacencyIds.Count > 0 || !string.IsNullOrWhiteSpace(adjacencyId),
                tolerance);
            builder.AddAdjacency(adjacencyId);
            builder.AddEvidence($"{evidenceValue}; distance {distanceToOpening:0.###} drawing units");
            if (sharesHostWall)
            {
                builder.AddEvidence("room shares opening host wall");
            }
        }

        public void AddEvidence(string value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !evidence.Contains(value, StringComparer.Ordinal))
            {
                evidence.Add(value);
            }
        }
    }

    private sealed class OpeningRoomConnectionBuilder
    {
        private readonly List<string> roomAdjacencyIds = new();
        private readonly List<string> evidence = new();

        public OpeningRoomConnectionBuilder(string roomId, string? roomLabel, RoomUseKind roomUseKind)
        {
            RoomId = roomId;
            RoomLabel = roomLabel;
            RoomUseKind = roomUseKind;
        }

        public string RoomId { get; }

        public string? RoomLabel { get; set; }

        public RoomUseKind RoomUseKind { get; set; }

        public double DistanceToOpening { get; set; } = double.PositiveInfinity;

        public bool SharesHostWall { get; set; }

        public Confidence Confidence { get; set; } = Confidence.Low;

        public IReadOnlyList<string> RoomAdjacencyIds => roomAdjacencyIds;

        public static OpeningRoomConnectionBuilder From(OpeningRoomConnection connection)
        {
            var builder = new OpeningRoomConnectionBuilder(
                connection.RoomId,
                connection.RoomLabel,
                connection.RoomUseKind)
            {
                DistanceToOpening = connection.DistanceToOpening,
                SharesHostWall = connection.SharesHostWall,
                Confidence = connection.Confidence
            };
            builder.roomAdjacencyIds.AddRange(connection.RoomAdjacencyIds);
            builder.evidence.AddRange(connection.Evidence);
            return builder;
        }

        public void AddAdjacency(string? adjacencyId)
        {
            if (!string.IsNullOrWhiteSpace(adjacencyId)
                && !roomAdjacencyIds.Contains(adjacencyId, StringComparer.Ordinal))
            {
                roomAdjacencyIds.Add(adjacencyId);
            }
        }

        public void AddEvidence(string value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !evidence.Contains(value, StringComparer.Ordinal))
            {
                evidence.Add(value);
            }
        }

        public OpeningRoomConnection ToConnection() =>
            new(
                RoomId,
                RoomLabel,
                RoomUseKind,
                roomAdjacencyIds.ToArray(),
                double.IsFinite(DistanceToOpening) ? DistanceToOpening : 0,
                SharesHostWall,
                Confidence,
                evidence.ToArray());
    }

    private sealed record RoomClusterClassification(
        RoomClusterKind Kind,
        IReadOnlyList<string> Evidence);

    private static readonly string[] CorridorTerms =
    {
        "corridor",
        "corr",
        "hallway",
        "passage",
        "passasje",
        "korridor",
        "gang"
    };

    private static readonly string[] OpenPlanTerms =
    {
        "open plan",
        "open office",
        "open workspace",
        "office landscape",
        "workspace",
        "landscape",
        "kontorlandskap",
        "landskap",
        "atrium",
        "lobby",
        "reception"
    };
}
