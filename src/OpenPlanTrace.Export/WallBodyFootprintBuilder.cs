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

internal sealed record WallPlacementAxis(
    PlanLineSegment CenterLine,
    string GeometrySource,
    bool UsesPairedFaceEvidence);

internal static class WallBodyFootprintBuilder
{
    public static WallPlacementAxis BuildPlacementAxis(
        WallSegment wall,
        double startParameter,
        double endParameter)
    {
        var normalizedStart = Math.Clamp(Math.Min(startParameter, endParameter), 0, 1);
        var normalizedEnd = Math.Clamp(Math.Max(startParameter, endParameter), 0, 1);
        var sourceLine = new PlanLineSegment(
            wall.CenterLine.PointAt(normalizedStart),
            wall.CenterLine.PointAt(normalizedEnd));

        if (TryBuildCenterLineFromPairEvidence(
            wall.PairEvidence,
            normalizedStart,
            normalizedEnd,
            out var pairCenterLine))
        {
            return new WallPlacementAxis(
                pairCenterLine,
                "detected paired wall-face midpoint",
                UsesPairedFaceEvidence: true);
        }

        return new WallPlacementAxis(
            sourceLine,
            "source wall centerline",
            UsesPairedFaceEvidence: false);
    }

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
        var placementAxis = BuildPlacementAxis(wall, normalizedStart, normalizedEnd);
        var line = placementAxis.CenterLine;
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

    public static IReadOnlyList<WallBodyFootprint> FromPlacementSolidSpans(
        PlanScanResult result,
        IEnumerable<WallGraphTopologySpan> topologySpans)
    {
        ArgumentNullException.ThrowIfNull(result);

        var visibleWallIds = topologySpans
            .Select(span => span.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (visibleWallIds.Count == 0)
        {
            return Array.Empty<WallBodyFootprint>();
        }

        var openingsByWallId = BuildOpeningLookup(result.Openings);
        var footprints = new List<WallBodyFootprint>();
        foreach (var wall in result.Walls.Where(wall => visibleWallIds.Contains(wall.Id)))
        {
            var openings = openingsByWallId.TryGetValue(wall.Id, out var wallOpenings)
                ? wallOpenings
                : Array.Empty<OpeningCandidate>();
            var cutouts = openings
                .Select((opening, index) => PlacementWallOpeningCutoutExport.From(
                    wall,
                    opening,
                    millimetersPerDrawingUnit: null,
                    index + 1))
                .Where(cutout => cutout is not null)
                .Select(cutout => cutout!)
                .DistinctBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
                .OrderBy(cutout => cutout.StartParameter)
                .ThenBy(cutout => cutout.EndParameter)
                .ThenBy(cutout => cutout.OpeningId, StringComparer.Ordinal)
                .ToArray();
            var solidSpans = PlacementWallSolidSpanExport.From(
                wall,
                millimetersPerDrawingUnit: null,
                cutouts,
                openings);

            foreach (var solidSpan in solidSpans)
            {
                var polygon = solidSpan.BodyPolygon
                    .Select(point => new PlanPoint(point.X, point.Y))
                    .ToArray();
                var centerLine = new PlanLineSegment(
                    new PlanPoint(solidSpan.CenterLine.Start.X, solidSpan.CenterLine.Start.Y),
                    new PlanPoint(solidSpan.CenterLine.End.X, solidSpan.CenterLine.End.Y));
                footprints.Add(new WallBodyFootprint(
                    $"{solidSpan.Id}:body-footprint",
                    solidSpan.PageNumber,
                    solidSpan.WallId,
                    polygon,
                    new PlanRect(
                        solidSpan.BodyBounds.X,
                        solidSpan.BodyBounds.Y,
                        solidSpan.BodyBounds.Width,
                        solidSpan.BodyBounds.Height),
                    centerLine,
                    new PlanVector(solidSpan.AlongVector.X, solidSpan.AlongVector.Y),
                    new PlanVector(solidSpan.NormalVector.X, solidSpan.NormalVector.Y),
                    solidSpan.ThicknessDrawingUnits,
                    wall.Confidence,
                    "placement solid span with anchored opening cutouts",
                    wall,
                    solidSpan.Evidence));
            }
        }

        return footprints
            .OrderBy(footprint => footprint.PageNumber)
            .ThenBy(footprint => footprint.Bounds.Y)
            .ThenBy(footprint => footprint.Bounds.X)
            .ThenBy(footprint => footprint.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, OpeningCandidate[]> BuildOpeningLookup(
        IReadOnlyList<OpeningCandidate> openings)
    {
        var lookup = new Dictionary<string, List<OpeningCandidate>>(StringComparer.Ordinal);
        foreach (var opening in openings)
        {
            foreach (var wallId in opening.HostWallIds
                .Concat(opening.AdjacentWallIds)
                .Append(opening.WallId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal))
            {
                if (!lookup.TryGetValue(wallId, out var wallOpenings))
                {
                    wallOpenings = new List<OpeningCandidate>();
                    lookup[wallId] = wallOpenings;
                }

                wallOpenings.Add(opening);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .DistinctBy(opening => opening.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

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
        var sameDirectionDistance = firstStart.DistanceTo(secondStart) + firstEnd.DistanceTo(secondEnd);
        var reverseDirectionDistance = firstStart.DistanceTo(secondEnd) + firstEnd.DistanceTo(secondStart);
        if (reverseDirectionDistance < sameDirectionDistance)
        {
            (secondStart, secondEnd) = (secondEnd, secondStart);
        }

        if (!PairFaceCapsAreOrthogonal(
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            pairEvidence.FaceSeparation))
        {
            return false;
        }

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

    private static bool TryBuildCenterLineFromPairEvidence(
        WallPairEvidence? pairEvidence,
        double startParameter,
        double endParameter,
        out PlanLineSegment centerLine)
    {
        centerLine = default;
        if (pairEvidence is null
            || pairEvidence.FirstFaceLine.Length <= 0.001
            || pairEvidence.SecondFaceLine.Length <= 0.001
            || pairEvidence.FaceSeparation <= 0.001
            || pairEvidence.OverlapRatio < 0.55)
        {
            return false;
        }

        var firstStart = pairEvidence.FirstFaceLine.PointAt(startParameter);
        var firstEnd = pairEvidence.FirstFaceLine.PointAt(endParameter);
        var secondStart = pairEvidence.SecondFaceLine.PointAt(startParameter);
        var secondEnd = pairEvidence.SecondFaceLine.PointAt(endParameter);
        var sameDirectionDistance = firstStart.DistanceTo(secondStart) + firstEnd.DistanceTo(secondEnd);
        var reverseDirectionDistance = firstStart.DistanceTo(secondEnd) + firstEnd.DistanceTo(secondStart);
        if (reverseDirectionDistance < sameDirectionDistance)
        {
            (secondStart, secondEnd) = (secondEnd, secondStart);
        }

        if (!PairFaceCapsAreOrthogonal(
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            pairEvidence.FaceSeparation))
        {
            return false;
        }

        centerLine = new PlanLineSegment(
            Midpoint(firstStart, secondStart),
            Midpoint(firstEnd, secondEnd));
        return centerLine.Length > 0.001;
    }

    private static bool PairFaceCapsAreOrthogonal(
        PlanPoint firstStart,
        PlanPoint firstEnd,
        PlanPoint secondStart,
        PlanPoint secondEnd,
        double faceSeparation)
    {
        var alongVector = (firstEnd - firstStart).Normalize();
        var capSkewTolerance = Math.Max(0.5, faceSeparation * 0.35);
        return alongVector.Length > 0.001
            && Math.Abs((secondStart - firstStart).Dot(alongVector)) <= capSkewTolerance
            && Math.Abs((secondEnd - firstEnd).Dot(alongVector)) <= capSkewTolerance;
    }

    private static PlanPoint Midpoint(PlanPoint first, PlanPoint second) =>
        new((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0);

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
