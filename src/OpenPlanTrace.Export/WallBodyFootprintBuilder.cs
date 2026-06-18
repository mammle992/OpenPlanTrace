namespace OpenPlanTrace.Export;

internal sealed record WallBodyFootprint(
    string Id,
    int PageNumber,
    string WallId,
    IReadOnlyList<PlanPoint> Polygon,
    PlanRect Bounds,
    PlanLineSegment CenterLine,
    PlanVector AlongVector,
    PlanVector NormalVector,
    double ThicknessDrawingUnits,
    Confidence Confidence,
    string GeometrySource,
    WallSegment SourceWall,
    IReadOnlyList<string> Evidence);

internal static class WallBodyFootprintBuilder
{
    public static WallBodyFootprint Build(
        WallSegment wall,
        double startParameter,
        double endParameter,
        string id,
        Confidence confidence,
        IReadOnlyList<string> evidence)
    {
        var normalizedStart = Math.Clamp(Math.Min(startParameter, endParameter), 0, 1);
        var normalizedEnd = Math.Clamp(Math.Max(startParameter, endParameter), 0, 1);
        var line = new PlanLineSegment(
            wall.CenterLine.PointAt(normalizedStart),
            wall.CenterLine.PointAt(normalizedEnd));
        var alongVector = line.Vector.Normalize();
        var fallbackNormalVector = new PlanVector(-alongVector.Y, alongVector.X);
        var hasPairEvidenceBody = TryBuildBodyPolygonFromPairEvidence(
            wall.PairEvidence,
            normalizedStart,
            normalizedEnd,
            fallbackNormalVector,
            out var bodyPolygon,
            out var normalVector);
        var geometrySource = hasPairEvidenceBody
            ? "detected paired wall-face evidence"
            : "centerline plus wall thickness";
        if (!hasPairEvidenceBody)
        {
            bodyPolygon = BuildFallbackBodyPolygon(line, normalVector, wall.Thickness / 2.0);
        }

        return new WallBodyFootprint(
            id,
            wall.PageNumber,
            wall.Id,
            bodyPolygon,
            BoundsForPoints(bodyPolygon),
            line,
            alongVector,
            normalVector,
            wall.Thickness,
            confidence,
            geometrySource,
            wall,
            evidence);
    }

    public static IReadOnlyList<WallBodyFootprint> FromTopologySpans(
        IEnumerable<WallGraphTopologySpan> spans) =>
        spans
            .Select(FromTopologySpan)
            .Where(footprint => footprint is not null)
            .Select(footprint => footprint!)
            .ToArray();

    private static WallBodyFootprint? FromTopologySpan(WallGraphTopologySpan span)
    {
        if (span.SourceWall is null)
        {
            return null;
        }

        var start = span.SourceWallStartParameter
            ?? span.SourceWall.CenterLine.ProjectParameter(span.CenterLine.Start);
        var end = span.SourceWallEndParameter
            ?? span.SourceWall.CenterLine.ProjectParameter(span.CenterLine.End);

        return Build(
            span.SourceWall,
            start,
            end,
            $"{span.Id}:body-footprint",
            span.Confidence,
            span.Evidence
                .Append($"wall body footprint generated from topology span {span.Id}")
                .ToArray());
    }

    private static bool TryBuildBodyPolygonFromPairEvidence(
        WallPairEvidence? pairEvidence,
        double startParameter,
        double endParameter,
        PlanVector fallbackNormalVector,
        out IReadOnlyList<PlanPoint> bodyPolygon,
        out PlanVector normalVector)
    {
        normalVector = fallbackNormalVector;
        bodyPolygon = Array.Empty<PlanPoint>();

        if (pairEvidence is null
            || pairEvidence.FirstFaceLine.Length <= 0.001
            || pairEvidence.SecondFaceLine.Length <= 0.001
            || pairEvidence.FaceSeparation <= 0.001)
        {
            return false;
        }

        var firstStart = pairEvidence.FirstFaceLine.PointAt(startParameter);
        var firstEnd = pairEvidence.FirstFaceLine.PointAt(endParameter);
        var secondStart = pairEvidence.SecondFaceLine.PointAt(startParameter);
        var secondEnd = pairEvidence.SecondFaceLine.PointAt(endParameter);
        var faceNormal = pairEvidence.SecondFaceLine.Midpoint - pairEvidence.FirstFaceLine.Midpoint;
        var normalizedFaceNormal = faceNormal.Normalize();
        if (normalizedFaceNormal.Length > 0.001)
        {
            normalVector = normalizedFaceNormal;
        }

        bodyPolygon =
        [
            firstStart,
            firstEnd,
            secondEnd,
            secondStart,
            firstStart
        ];
        return true;
    }

    private static IReadOnlyList<PlanPoint> BuildFallbackBodyPolygon(
        PlanLineSegment line,
        PlanVector normalVector,
        double halfThickness)
    {
        var offsetX = normalVector.X * Math.Max(0, halfThickness);
        var offsetY = normalVector.Y * Math.Max(0, halfThickness);
        var startMinusNormal = line.Start.Translate(-offsetX, -offsetY);
        var endMinusNormal = line.End.Translate(-offsetX, -offsetY);
        var endPlusNormal = line.End.Translate(offsetX, offsetY);
        var startPlusNormal = line.Start.Translate(offsetX, offsetY);

        return
        [
            startMinusNormal,
            endMinusNormal,
            endPlusNormal,
            startPlusNormal,
            startMinusNormal
        ];
    }

    private static PlanRect BoundsForPoints(IReadOnlyList<PlanPoint> points)
    {
        if (points.Count == 0)
        {
            return PlanRect.Empty;
        }

        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return PlanRect.FromEdges(left, top, right, bottom);
    }
}
