namespace OpenPlanTrace;

internal sealed record RoomBoundaryWallReferenceResult(
    IReadOnlyDictionary<string, string[]> RoomIdsByWallId,
    IReadOnlySet<string> GeometricRoomBoundaryWallIds,
    int GeometricRoomBoundaryReferencedWallCount,
    int GeometricRoomBoundaryReferenceCount);

internal static class RoomBoundaryWallReferenceBuilder
{
    public static RoomBoundaryWallReferenceResult Build(
        IReadOnlyList<RoomRegion> rooms,
        IReadOnlyList<WallSegment> walls,
        double wallSnapTolerance)
    {
        var builder = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var geometryReferencedWallIds = new HashSet<string>(StringComparer.Ordinal);
        var geometryReferenceCount = 0;

        foreach (var room in rooms)
        {
            foreach (var wallId in room.WallIds)
            {
                if (string.IsNullOrWhiteSpace(wallId))
                {
                    continue;
                }

                if (!builder.TryGetValue(wallId, out var roomIds))
                {
                    roomIds = new HashSet<string>(StringComparer.Ordinal);
                    builder[wallId] = roomIds;
                }

                roomIds.Add(room.Id);
            }

            if (!CanUseRoomBoundaryGeometryForWallSupport(room))
            {
                continue;
            }

            foreach (var wall in walls)
            {
                if (wall.PageNumber != room.PageNumber
                    || string.IsNullOrWhiteSpace(wall.Id)
                    || !WallAlignsWithRoomBoundary(wall, room, wallSnapTolerance))
                {
                    continue;
                }

                if (!builder.TryGetValue(wall.Id, out var roomIds))
                {
                    roomIds = new HashSet<string>(StringComparer.Ordinal);
                    builder[wall.Id] = roomIds;
                }

                roomIds.Add(room.Id);
                geometryReferencedWallIds.Add(wall.Id);
                geometryReferenceCount++;
            }
        }

        return new RoomBoundaryWallReferenceResult(
            builder.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            geometryReferencedWallIds,
            geometryReferencedWallIds.Count,
            geometryReferenceCount);
    }

    private static bool CanUseRoomBoundaryGeometryForWallSupport(RoomRegion room)
    {
        if (room.UseKind == RoomUseKind.Outdoor
            || room.Boundary.Count < 4
            || room.Confidence.Value < 0.55)
        {
            return false;
        }

        if (room.WallIds.Count >= 2)
        {
            return true;
        }

        return room.Evidence.Any(item =>
            item.Contains("closed orthogonal cycle", StringComparison.OrdinalIgnoreCase)
            || item.Contains("bounded by nearby orthogonal wall evidence", StringComparison.OrdinalIgnoreCase)
            || item.Contains("semantic room boundary inferred from nearby walls", StringComparison.OrdinalIgnoreCase));
    }

    private static bool WallAlignsWithRoomBoundary(
        WallSegment wall,
        RoomRegion room,
        double wallSnapTolerance)
    {
        if (!TryResolveAxisInterval(wall.CenterLine, out var wallOrientation, out var wallCoordinate, out var wallStart, out var wallEnd))
        {
            return false;
        }

        var tolerance = Math.Max(
            wallSnapTolerance * 2.0,
            Math.Max(4.0, wall.Thickness * 1.5));
        var minimumOverlapLength = Math.Min(
            Math.Max(16.0, wall.DrawingLength * 0.55),
            Math.Max(18.0, wall.DrawingLength - tolerance));

        foreach (var edge in RoomBoundaryEdges(room))
        {
            if (!TryResolveAxisInterval(edge, out var edgeOrientation, out var edgeCoordinate, out var edgeStart, out var edgeEnd)
                || edgeOrientation != wallOrientation
                || Math.Abs(edgeCoordinate - wallCoordinate) > tolerance)
            {
                continue;
            }

            var overlap = Math.Min(wallEnd, edgeEnd) - Math.Max(wallStart, edgeStart);
            if (overlap >= minimumOverlapLength
                && overlap / Math.Max(wall.DrawingLength, 0.001) >= 0.45)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<PlanLineSegment> RoomBoundaryEdges(RoomRegion room)
    {
        for (var index = 0; index < room.Boundary.Count; index++)
        {
            var current = room.Boundary[index];
            var next = room.Boundary[(index + 1) % room.Boundary.Count];
            var edge = new PlanLineSegment(current, next);
            if (edge.Length > 0.001)
            {
                yield return edge;
            }
        }
    }

    private static bool TryResolveAxisInterval(
        PlanLineSegment line,
        out AxisOrientation orientation,
        out double coordinate,
        out double start,
        out double end)
    {
        var dx = Math.Abs(line.End.X - line.Start.X);
        var dy = Math.Abs(line.End.Y - line.Start.Y);
        if (dx >= dy && dy <= Math.Max(1.0, dx * 0.02))
        {
            orientation = AxisOrientation.Horizontal;
            coordinate = (line.Start.Y + line.End.Y) / 2.0;
            start = Math.Min(line.Start.X, line.End.X);
            end = Math.Max(line.Start.X, line.End.X);
            return true;
        }

        if (dy > dx && dx <= Math.Max(1.0, dy * 0.02))
        {
            orientation = AxisOrientation.Vertical;
            coordinate = (line.Start.X + line.End.X) / 2.0;
            start = Math.Min(line.Start.Y, line.End.Y);
            end = Math.Max(line.Start.Y, line.End.Y);
            return true;
        }

        orientation = AxisOrientation.Unknown;
        coordinate = 0;
        start = 0;
        end = 0;
        return false;
    }

    private enum AxisOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }
}
