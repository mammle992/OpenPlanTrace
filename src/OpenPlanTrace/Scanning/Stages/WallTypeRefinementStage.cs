namespace OpenPlanTrace;

internal sealed class WallTypeRefinementStage : IPipelineStage
{
    private const string StageName = "wall-type-refinement";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        if (context.Walls.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var roomIdsByWallId = BuildRoomIdsByWallId(context.Rooms);
        var sharedWallIds = BuildSharedWallIds(context.RoomAdjacencyGraph);
        var componentsByWallId = BuildComponentsByWallId(context.WallGraph);
        var roomsByPage = context.Rooms
            .GroupBy(room => room.PageNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var changed = 0;
        var evidenceUpdated = 0;
        var roomReferenced = 0;
        var twoSidedRoomEvidence = 0;
        var oneSidedRoomEvidence = 0;

        for (var index = 0; index < context.Walls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wall = context.Walls[index];
            var wallRoomIds = roomIdsByWallId.TryGetValue(wall.Id, out var ids)
                ? ids
                : Array.Empty<string>();
            if (wallRoomIds.Length > 0)
            {
                roomReferenced++;
            }

            var sideEvidence = roomsByPage.TryGetValue(wall.PageNumber, out var pageRooms)
                ? AnalyzeRoomSides(wall, pageRooms, context.Options)
                : RoomSideEvidence.Empty;
            if (sideEvidence.PositiveRoomHits > 0 && sideEvidence.NegativeRoomHits > 0)
            {
                twoSidedRoomEvidence++;
            }
            else if (sideEvidence.PositiveRoomHits > 0 || sideEvidence.NegativeRoomHits > 0)
            {
                oneSidedRoomEvidence++;
            }

            var component = componentsByWallId.TryGetValue(wall.Id, out var foundComponent)
                ? foundComponent
                : null;
            var refined = RefineWallType(
                wall,
                wallRoomIds.Length,
                sharedWallIds.Contains(wall.Id),
                sideEvidence,
                component);

            var evidence = IsActionableEvidence(refined.Evidence)
                ? AppendEvidence(wall.Evidence, refined.Evidence)
                : wall.Evidence;
            var evidenceChanged = evidence.Count != wall.Evidence.Count;

            if (refined.WallType == wall.WallType && !evidenceChanged)
            {
                continue;
            }

            context.Walls[index] = wall with
            {
                WallType = refined.WallType,
                Evidence = evidence
            };
            if (refined.WallType != wall.WallType)
            {
                changed++;
            }

            if (evidenceChanged)
            {
                evidenceUpdated++;
            }
        }

        AddDiagnostics(
            context,
            changed,
            evidenceUpdated,
            roomReferenced,
            twoSidedRoomEvidence,
            oneSidedRoomEvidence);
        return ValueTask.CompletedTask;
    }

    private static Dictionary<string, string[]> BuildRoomIdsByWallId(IReadOnlyList<RoomRegion> rooms)
    {
        var builder = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var room in rooms)
        {
            foreach (var wallId in room.WallIds)
            {
                if (!builder.TryGetValue(wallId, out var roomIds))
                {
                    roomIds = new HashSet<string>(StringComparer.Ordinal);
                    builder[wallId] = roomIds;
                }

                roomIds.Add(room.Id);
            }
        }

        return builder.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Order(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static HashSet<string> BuildSharedWallIds(RoomAdjacencyGraph graph) =>
        graph.Edges
            .SelectMany(edge => edge.SharedWallIds)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, WallGraphComponent> BuildComponentsByWallId(WallGraph graph)
    {
        var result = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in graph.Components)
        {
            foreach (var wallId in component.WallIds)
            {
                result[wallId] = component;
            }
        }

        return result;
    }

    private static WallTypeRefinement RefineWallType(
        WallSegment wall,
        int roomReferenceCount,
        bool isSharedByRoomAdjacency,
        RoomSideEvidence sideEvidence,
        WallGraphComponent? component)
    {
        if (IsNonStructuralWallComponent(component))
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                "wall type refined unknown: wall belongs to non-structural or isolated graph component");
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return new WallTypeRefinement(
                WallType.Unknown,
                "wall type refined unknown: fragment-merged wall geometry requires review before exact placement");
        }

        if (isSharedByRoomAdjacency)
        {
            return new WallTypeRefinement(
                WallType.Interior,
                "wall type refined interior: shared by room adjacency boundary");
        }

        if (sideEvidence.HasRoomsOnBothSides && wall.WallType != WallType.Exterior)
        {
            return new WallTypeRefinement(
                WallType.Interior,
                "wall type refined interior: detected room evidence on both sides");
        }

        if (sideEvidence.HasRoomsOnExactlyOneSide
            && IsStructuralWallComponent(component)
            && wall.Confidence.Value >= 0.45)
        {
            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: detected room evidence on one side only");
        }

        if (wall.WallType == WallType.Unknown
            && roomReferenceCount == 1
            && IsStructuralWallComponent(component)
            && wall.Confidence.Value >= 0.6)
        {
            return new WallTypeRefinement(
                WallType.Exterior,
                "wall type refined exterior: structural room boundary with no shared room side");
        }

        return new WallTypeRefinement(wall.WallType, "wall type unchanged: room-side evidence was inconclusive");
    }

    private static bool IsStructuralWallComponent(WallGraphComponent? component) =>
        component is null
        || (!component.ExcludedFromStructuralTopology
            && component.Kind is WallGraphComponentKind.MainStructural or WallGraphComponentKind.SecondaryStructural);

    private static bool IsNonStructuralWallComponent(WallGraphComponent? component) =>
        component is not null
        && (component.ExcludedFromStructuralTopology
            || component.Kind is WallGraphComponentKind.ObjectLikeIsland);

    private static RoomSideEvidence AnalyzeRoomSides(
        WallSegment wall,
        IReadOnlyList<RoomRegion> pageRooms,
        ScannerOptions options)
    {
        if (pageRooms.Count == 0 || wall.CenterLine.Length <= double.Epsilon)
        {
            return RoomSideEvidence.Empty;
        }

        var along = wall.CenterLine.Vector.Normalize();
        if (along.Length <= double.Epsilon)
        {
            return RoomSideEvidence.Empty;
        }

        var normal = new PlanVector(-along.Y, along.X).Normalize();
        var sampleOffset = Math.Max(
            wall.Thickness > 0 ? wall.Thickness * 1.5 : options.DefaultWallThickness * 2.5,
            Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.5));
        var positiveHits = 0;
        var negativeHits = 0;

        foreach (var t in new[] { 0.25, 0.5, 0.75 })
        {
            var point = wall.CenterLine.PointAt(t);
            if (IsInsideAnyRoom(point + (normal * sampleOffset), pageRooms, options.WallSnapTolerance))
            {
                positiveHits++;
            }

            if (IsInsideAnyRoom(point + (normal * -sampleOffset), pageRooms, options.WallSnapTolerance))
            {
                negativeHits++;
            }
        }

        return new RoomSideEvidence(positiveHits, negativeHits);
    }

    private static bool IsInsideAnyRoom(
        PlanPoint point,
        IReadOnlyList<RoomRegion> rooms,
        double tolerance) =>
        rooms.Any(room => IsInsideRoom(point, room, tolerance));

    private static bool IsInsideRoom(PlanPoint point, RoomRegion room, double tolerance)
    {
        if (!room.Bounds.Contains(point, tolerance))
        {
            return false;
        }

        return room.Boundary.Count < 3 || IsPointInPolygon(point, room.Boundary);
    }

    private static bool IsPointInPolygon(PlanPoint point, IReadOnlyList<PlanPoint> polygon)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var currentPoint = polygon[index];
            var previousPoint = polygon[previous];
            var crossesY = currentPoint.Y > point.Y != previousPoint.Y > point.Y;
            if (!crossesY)
            {
                continue;
            }

            var intersectionX = ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y)
                / (previousPoint.Y - currentPoint.Y))
                + currentPoint.X;
            if (point.X < intersectionX)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static IReadOnlyList<string> AppendEvidence(
        IReadOnlyList<string> evidence,
        string refinementEvidence) =>
        evidence
            .Append(refinementEvidence)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool IsActionableEvidence(string evidence) =>
        !evidence.Contains("unchanged", StringComparison.OrdinalIgnoreCase)
        && !evidence.Contains("inconclusive", StringComparison.OrdinalIgnoreCase);

    private static void AddDiagnostics(
        ScanContext context,
        int changed,
        int evidenceUpdated,
        int roomReferenced,
        int twoSidedRoomEvidence,
        int oneSidedRoomEvidence)
    {
        var exterior = context.Walls.Count(wall => wall.WallType == WallType.Exterior);
        var interior = context.Walls.Count(wall => wall.WallType == WallType.Interior);
        var unknown = context.Walls.Count(wall => wall.WallType == WallType.Unknown);
        context.AddDiagnostic(
            "walls.architectural_type_refined",
            DiagnosticSeverity.Info,
            StageName,
            $"Refined wall type classifications for {changed} wall(s).",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            properties: new Dictionary<string, string>
            {
                ["wallCount"] = context.Walls.Count.ToString(),
                ["changedWallTypeCount"] = changed.ToString(),
                ["evidenceUpdatedWallCount"] = evidenceUpdated.ToString(),
                ["roomReferencedWallCount"] = roomReferenced.ToString(),
                ["twoSidedRoomEvidenceWallCount"] = twoSidedRoomEvidence.ToString(),
                ["oneSidedRoomEvidenceWallCount"] = oneSidedRoomEvidence.ToString(),
                ["exteriorWallCount"] = exterior.ToString(),
                ["interiorWallCount"] = interior.ToString(),
                ["unknownWallCount"] = unknown.ToString()
            });
    }

    private readonly record struct RoomSideEvidence(int PositiveRoomHits, int NegativeRoomHits)
    {
        public static RoomSideEvidence Empty { get; } = new(0, 0);

        public bool HasRoomsOnBothSides => PositiveRoomHits > 0 && NegativeRoomHits > 0;

        public bool HasRoomsOnExactlyOneSide => PositiveRoomHits > 0 != NegativeRoomHits > 0;
    }

    private sealed record WallTypeRefinement(WallType WallType, string Evidence);
}
