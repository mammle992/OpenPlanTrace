using System.Globalization;

namespace OpenPlanTrace.Export;

public sealed record WallGraphTopologySpan(
    string Id,
    int PageNumber,
    string WallId,
    string FromNodeId,
    string ToNodeId,
    PlanLineSegment CenterLine,
    PlanRect Bounds,
    double DrawingLength,
    double? SourceWallStartOffsetDrawingUnits,
    double? SourceWallEndOffsetDrawingUnits,
    double? SourceWallProjectedLengthDrawingUnits,
    double? SourceWallStartParameter,
    double? SourceWallEndParameter,
    double? SourceWallCenterParameter,
    double? SourceWallStartProjectionDistanceDrawingUnits,
    double? SourceWallEndProjectionDistanceDrawingUnits,
    double Thickness,
    Confidence Confidence,
    IReadOnlyList<string> SourcePrimitiveIds,
    IReadOnlyList<string> SourceWallGraphEdgeIds,
    IReadOnlyList<string> Evidence,
    WallSegment? SourceWall);

internal static class WallGraphTopologySpanBuilder
{
    public static IReadOnlyList<WallGraphTopologySpan> Build(
        WallGraph graph,
        IReadOnlyList<WallSegment> walls)
    {
        if (graph.Edges.Count == 0 || graph.Nodes.Count == 0)
        {
            return Array.Empty<WallGraphTopologySpan>();
        }

        var nodesById = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var wallsById = walls.ToDictionary(wall => wall.Id, StringComparer.Ordinal);
        var spans = new List<WallGraphTopologySpan>();

        foreach (var edge in graph.Edges)
        {
            if (!nodesById.TryGetValue(edge.FromNodeId, out var from)
                || !nodesById.TryGetValue(edge.ToNodeId, out var to))
            {
                continue;
            }

            var centerLine = new PlanLineSegment(from.Position, to.Position);
            if (centerLine.Length <= 0.001)
            {
                continue;
            }

            wallsById.TryGetValue(edge.WallId, out var wall);
            var thickness = wall?.Thickness ?? 1.0;
            var bounds = centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5));
            var sourcePrimitiveIds = wall?.SourcePrimitiveIds ?? Array.Empty<string>();
            var placement = SourceWallPlacement(centerLine, wall);
            var evidence = new List<string>
            {
                $"wall graph topology span from {edge.FromNodeId} to {edge.ToNodeId}",
                $"source wall {edge.WallId}"
            };

            if (wall is not null)
            {
                evidence.AddRange(wall.Evidence);
            }

            if (placement is not null)
            {
                evidence.Add(
                    $"span projects to source wall offsets {Format(placement.StartOffset)} -> {Format(placement.EndOffset)} drawing units");
                evidence.Add(
                    $"span endpoint projection distances {Format(placement.StartProjectionDistance)} and {Format(placement.EndProjectionDistance)} drawing units");
            }

            spans.Add(new WallGraphTopologySpan(
                edge.Id,
                edge.PageNumber,
                edge.WallId,
                edge.FromNodeId,
                edge.ToNodeId,
                centerLine,
                bounds,
                centerLine.Length,
                placement?.StartOffset,
                placement?.EndOffset,
                placement?.ProjectedLength,
                placement?.StartParameter,
                placement?.EndParameter,
                placement?.CenterParameter,
                placement?.StartProjectionDistance,
                placement?.EndProjectionDistance,
                thickness,
                edge.Confidence,
                sourcePrimitiveIds,
                [edge.Id],
                evidence.Distinct(StringComparer.Ordinal).ToArray(),
                wall));
        }

        return spans;
    }

    private static SourceWallSpanPlacement? SourceWallPlacement(
        PlanLineSegment span,
        WallSegment? wall)
    {
        if (wall is null || wall.CenterLine.Length <= 0.001)
        {
            return null;
        }

        var sourceLine = wall.CenterLine;
        var sourceLength = sourceLine.Length;
        var startParameter = sourceLine.ProjectParameter(span.Start);
        var endParameter = sourceLine.ProjectParameter(span.End);
        var centerParameter = sourceLine.ProjectParameter(span.Midpoint);
        var startOffset = startParameter * sourceLength;
        var endOffset = endParameter * sourceLength;

        return new SourceWallSpanPlacement(
            startOffset,
            endOffset,
            Math.Abs(endOffset - startOffset),
            startParameter,
            endParameter,
            centerParameter,
            sourceLine.DistanceToPoint(span.Start),
            sourceLine.DistanceToPoint(span.End));
    }

    private static string Format(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record SourceWallSpanPlacement(
        double StartOffset,
        double EndOffset,
        double ProjectedLength,
        double StartParameter,
        double EndParameter,
        double CenterParameter,
        double StartProjectionDistance,
        double EndProjectionDistance);
}
