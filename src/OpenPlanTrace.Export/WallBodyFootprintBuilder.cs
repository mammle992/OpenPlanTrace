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
    bool UsesPairedFaceEvidence,
    bool AnchoredToSourceWallExtents = false);

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
            sourceLine,
            normalizedStart,
            normalizedEnd,
            out var pairCenterLine,
            out var anchoredToSourceWallExtents))
        {
            return new WallPlacementAxis(
                pairCenterLine,
                "detected paired wall-face midpoint",
                UsesPairedFaceEvidence: true,
                anchoredToSourceWallExtents);
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
        var sourceLine = new PlanLineSegment(
            wall.CenterLine.PointAt(normalizedStart),
            wall.CenterLine.PointAt(normalizedEnd));
        var placementAxis = BuildPlacementAxis(wall, normalizedStart, normalizedEnd);
        var line = placementAxis.CenterLine;
        var alongVector = line.Vector.Normalize();
        var fallbackNormalVector = new PlanVector(-alongVector.Y, alongVector.X);
        var hasPairEvidenceBody = TryBuildBodyPolygonFromPairEvidence(
            wall.PairEvidence,
            sourceLine,
            normalizedStart,
            normalizedEnd,
            fallbackNormalVector,
            out var bodyPolygon,
            out var normalVector);
        var geometrySource = hasPairEvidenceBody
            ? placementAxis.AnchoredToSourceWallExtents
                ? "detected paired wall-face evidence anchored to source wall extents"
                : "detected paired wall-face evidence"
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
        IEnumerable<WallGraphTopologySpan> topologySpans,
        bool includePlacementReadyFallbackBodies = false)
    {
        ArgumentNullException.ThrowIfNull(result);

        var visibleWallIds = topologySpans
            .Select(span => span.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var componentByWallId = result.WallGraph.Components
            .SelectMany(component => component.WallIds.Select(wallId => new { wallId, component }))
            .GroupBy(item => item.wallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().component, StringComparer.Ordinal);
        var assessmentByWallId = result.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var openingsByWallId = BuildOpeningLookup(result.Openings);
        var footprints = new List<WallBodyFootprint>();
        foreach (var wall in result.Walls.Where(wall =>
            visibleWallIds.Contains(wall.Id)
            || (includePlacementReadyFallbackBodies
                && IsPlacementReadyWallBodyFootprintCandidate(wall, componentByWallId, assessmentByWallId))))
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

    private static bool IsPlacementReadyWallBodyFootprintCandidate(
        WallSegment wall,
        IReadOnlyDictionary<string, WallGraphComponent> componentByWallId,
        IReadOnlyDictionary<string, WallEvidenceWallAssessment> assessmentByWallId)
    {
        if (wall.WallType == WallType.Unknown
            || wall.DrawingLength <= 0.001
            || wall.Thickness <= 0.001
            || wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            return false;
        }

        componentByWallId.TryGetValue(wall.Id, out var component);
        assessmentByWallId.TryGetValue(wall.Id, out var assessment);
        var trustedRoomBoundaryIsolatedExteriorWall =
            WallPlacementReadinessEvaluator.IsTrustedRoomBoundaryIsolatedExteriorWall(
                wall,
                component,
                assessment);
        if ((component?.ExcludedFromStructuralTopology == true && !trustedRoomBoundaryIsolatedExteriorWall)
            || (component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment
                && !trustedRoomBoundaryIsolatedExteriorWall)
            || assessment is null
            || !assessment.PlacementReady
            || assessment.RequiresReview
            || assessment.RejectedAsNoise
            || assessment.Decision == WallEvidenceDecision.Reject
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        var evidence = wall.Evidence
            .Concat(assessment.Evidence)
            .Concat(assessment.ScoreBreakdown.NegativeEvidence)
            .Concat(component?.Evidence ?? Array.Empty<string>())
            .ToArray();
        return !evidence.Any(IsNonPlacementWallBodyEvidence);
    }

    private static bool IsNonPlacementWallBodyEvidence(string evidence) =>
        evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("object/fixture", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("fixture detail", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("repeated short detail", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("door/opening", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("door swing", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("door leaf", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("door arc", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("dimension/annotation", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("witness/extension", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("non-wall", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("railing", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("stair-like", StringComparison.OrdinalIgnoreCase);

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
        PlanLineSegment sourceLine,
        double startParameter,
        double endParameter,
        PlanVector fallbackNormalVector,
        out IReadOnlyList<PlanPoint> bodyPolygon,
        out PlanVector normalVector)
    {
        normalVector = fallbackNormalVector;
        bodyPolygon = Array.Empty<PlanPoint>();

        if (!TryBuildSourceAnchoredPairFaceSegment(
            pairEvidence,
            sourceLine,
            startParameter,
            endParameter,
            out var firstStart,
            out var firstEnd,
            out var secondStart,
            out var secondEnd,
            out var faceNormalVector,
            out _,
            out _))
        {
            return false;
        }

        if (faceNormalVector.Length > 0.001)
        {
            normalVector = faceNormalVector;
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
        PlanLineSegment sourceLine,
        double startParameter,
        double endParameter,
        out PlanLineSegment centerLine,
        out bool anchoredToSourceWallExtents)
    {
        centerLine = default;
        anchoredToSourceWallExtents = false;
        if (!TryBuildSourceAnchoredPairFaceSegment(
            pairEvidence,
            sourceLine,
            startParameter,
            endParameter,
            out var firstStart,
            out var firstEnd,
            out var secondStart,
            out var secondEnd,
            out _,
            out var parameterCenterLine,
            out anchoredToSourceWallExtents))
        {
            return false;
        }

        centerLine = new PlanLineSegment(
            Midpoint(firstStart, secondStart),
            Midpoint(firstEnd, secondEnd));
        if (centerLine.Length <= 0.001)
        {
            return false;
        }

        anchoredToSourceWallExtents = anchoredToSourceWallExtents
            || centerLine.Start.DistanceTo(parameterCenterLine.Start) > 0.75
            || centerLine.End.DistanceTo(parameterCenterLine.End) > 0.75
            || Math.Abs(centerLine.Length - parameterCenterLine.Length) > 0.75;
        return true;
    }

    private static bool TryBuildSourceAnchoredPairFaceSegment(
        WallPairEvidence? pairEvidence,
        PlanLineSegment sourceLine,
        double startParameter,
        double endParameter,
        out PlanPoint firstStart,
        out PlanPoint firstEnd,
        out PlanPoint secondStart,
        out PlanPoint secondEnd,
        out PlanVector faceNormalVector,
        out PlanLineSegment parameterCenterLine,
        out bool anchoredToSourceWallExtents)
    {
        firstStart = default;
        firstEnd = default;
        secondStart = default;
        secondEnd = default;
        faceNormalVector = default;
        parameterCenterLine = default;
        anchoredToSourceWallExtents = false;
        if (pairEvidence is null
            || sourceLine.Length <= 0.001
            || pairEvidence.FirstFaceLine.Length <= 0.001
            || pairEvidence.SecondFaceLine.Length <= 0.001
            || pairEvidence.FaceSeparation <= 0.001
            || pairEvidence.OverlapRatio < 0.55)
        {
            return false;
        }

        var firstFaceLine = pairEvidence.FirstFaceLine;
        var secondFaceLine = pairEvidence.SecondFaceLine;
        var sameDirectionDistance = firstFaceLine.Start.DistanceTo(secondFaceLine.Start)
            + firstFaceLine.End.DistanceTo(secondFaceLine.End);
        var reverseDirectionDistance = firstFaceLine.Start.DistanceTo(secondFaceLine.End)
            + firstFaceLine.End.DistanceTo(secondFaceLine.Start);
        if (reverseDirectionDistance < sameDirectionDistance)
        {
            secondFaceLine = secondFaceLine.Reverse();
        }

        var pairCenterAxis = new PlanLineSegment(
            Midpoint(firstFaceLine.Start, secondFaceLine.Start),
            Midpoint(firstFaceLine.End, secondFaceLine.End));
        if (pairCenterAxis.Length <= 0.001)
        {
            return false;
        }

        var sourceVector = sourceLine.Vector.Normalize();
        var pairVector = pairCenterAxis.Vector.Normalize();
        if (sourceVector.Length <= 0.001
            || pairVector.Length <= 0.001
            || Math.Abs(sourceVector.Dot(pairVector)) < 0.985)
        {
            return false;
        }

        var maxAxisOffset = Math.Max(2.0, (pairEvidence.FaceSeparation / 2.0) + 2.0);
        if (Math.Min(
            sourceLine.DistanceToPoint(pairCenterAxis.Midpoint),
            pairCenterAxis.DistanceToPoint(sourceLine.Midpoint)) > maxAxisOffset)
        {
            return false;
        }

        var sourceStartOnPairAxis = pairCenterAxis.ProjectParameter(sourceLine.Start);
        var sourceEndOnPairAxis = pairCenterAxis.ProjectParameter(sourceLine.End);
        var maxExtension = Math.Max(24.0, sourceLine.Length * 0.35);
        var extensionBefore = Math.Max(0, -Math.Min(sourceStartOnPairAxis, sourceEndOnPairAxis)) * pairCenterAxis.Length;
        var extensionAfter = Math.Max(0, Math.Max(sourceStartOnPairAxis, sourceEndOnPairAxis) - 1) * pairCenterAxis.Length;
        if (Math.Max(extensionBefore, extensionAfter) > maxExtension)
        {
            return false;
        }

        firstStart = firstFaceLine.PointAt(sourceStartOnPairAxis);
        firstEnd = firstFaceLine.PointAt(sourceEndOnPairAxis);
        secondStart = secondFaceLine.PointAt(sourceStartOnPairAxis);
        secondEnd = secondFaceLine.PointAt(sourceEndOnPairAxis);
        if (!PairFaceCapsAreOrthogonal(
            firstStart,
            firstEnd,
            secondStart,
            secondEnd,
            pairEvidence.FaceSeparation))
        {
            return false;
        }

        var parameterFirstStart = firstFaceLine.PointAt(startParameter);
        var parameterFirstEnd = firstFaceLine.PointAt(endParameter);
        var parameterSecondStart = secondFaceLine.PointAt(startParameter);
        var parameterSecondEnd = secondFaceLine.PointAt(endParameter);
        parameterCenterLine = new PlanLineSegment(
            Midpoint(parameterFirstStart, parameterSecondStart),
            Midpoint(parameterFirstEnd, parameterSecondEnd));

        faceNormalVector = (secondFaceLine.Midpoint - firstFaceLine.Midpoint).Normalize();
        anchoredToSourceWallExtents = firstStart.DistanceTo(parameterFirstStart) > 0.75
            || firstEnd.DistanceTo(parameterFirstEnd) > 0.75
            || secondStart.DistanceTo(parameterSecondStart) > 0.75
            || secondEnd.DistanceTo(parameterSecondEnd) > 0.75;
        return true;
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
