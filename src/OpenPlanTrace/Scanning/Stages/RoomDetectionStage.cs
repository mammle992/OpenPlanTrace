using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed class RoomDetectionStage : IPipelineStage
{
    private const string StageName = "rooms";
    private static readonly HashSet<string> CompactEquipmentTagPrefixes = new(StringComparer.Ordinal)
    {
        "P",
        "PU",
        "V",
        "HV",
        "XV",
        "CV",
        "SV",
        "PSV",
        "TK",
        "T",
        "VES",
        "HX",
        "HE",
        "PMP",
        "CMP",
        "AHU",
        "VAV",
        "FCU",
        "RTU",
        "EF",
        "SF",
        "RF",
        "FAN",
        "DB",
        "MCC",
        "MSB",
        "LP",
        "PP",
        "FA",
        "FACP",
        "SP"
    };
    private static readonly string[] NonRoomLabelTerms =
    {
        "frostet",
        "sidelfelt",
        "fast",
        "glass",
        "glassfelt",
        "glazing",
        "platform",
        "plattform",
        "window",
        "vindu",
        "ved siden",
        "door",
        "d\u00f8r",
        "siden",
        "sidefelt",
        "sidelfelt",
        "opening",
        "\u00e5pning",
        "bta",
        "bra",
        "gross",
        "area",
        "areal"
    };
    private static readonly Regex RoomAreaMeasurementRegex =
        new(@"^\s*\d+(?:[\.,]\d+)?\s*m(?:2|\^2|\u00B2|\u33A1)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AreaBackedCompactRoomCodeRegex =
        new(@"^[A-Z]{1,2}\d{1,4}[A-Z]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var pageGroup in context.Walls.GroupBy(wall => wall.PageNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = context.Document.Pages.FirstOrDefault(candidate => candidate.Number == pageGroup.Key);
            if (page is null)
            {
                continue;
            }

            var structuralWalls = WallTopologyFilter.StructuralWallsForPage(
                context,
                pageGroup.Key,
                out var excludedComponents,
                out var excludedEvidenceAssessments);
            WallTopologyFilter.AddStructuralTopologyExclusionDiagnostic(context, Name, pageGroup.Key, excludedComponents);
            WallTopologyFilter.AddRejectedWallEvidenceExclusionDiagnostic(context, Name, pageGroup.Key, excludedEvidenceAssessments);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var addedForPage = AddGraphRooms(page, pageGroup.Key, structuralWalls, context, seen, cancellationToken);

            if (addedForPage == 0)
            {
                addedForPage = AddRectangularFallbackRooms(page, pageGroup.Key, structuralWalls, context, seen, cancellationToken);
            }

            addedForPage += AddSemanticLabelRooms(page, pageGroup.Key, structuralWalls, context, seen, cancellationToken);

            if (addedForPage == 0 && structuralWalls.Count > 0)
            {
                AddNoRoomsDiagnostic(pageGroup.Key, structuralWalls, context);
            }
        }

        AddRoomUseDiagnostics(context);
        return ValueTask.CompletedTask;
    }

    private int AddGraphRooms(
        PlanPage page,
        int pageNumber,
        IReadOnlyList<WallSegment> structuralWalls,
        ScanContext context,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var grid = OrthogonalRoomGrid.TryCreate(pageNumber, context, structuralWalls);
        if (grid is null)
        {
            return 0;
        }

        var components = grid.FindInteriorComponents(context.Options.MaxRoomCandidatesPerPage + 1).ToArray();
        var added = 0;
        var suppressed = new List<SuppressedRoomCandidate>();

        foreach (var component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (added >= context.Options.MaxRoomCandidatesPerPage)
            {
                AddRoomLimitDiagnostic(pageNumber, added, context);
                return added;
            }

            var boundary = grid.TraceBoundary(component).ToArray();
            if (boundary.Length < 4)
            {
                continue;
            }

            var drawingArea = PolygonArea(boundary);
            if (drawingArea < context.Options.MinRoomArea)
            {
                continue;
            }

            var bounds = PlanRect.Union(boundary.Select(point => new PlanRect(point.X, point.Y, 0, 0)));
            var key = RoomKey(boundary, context.Options.WallSnapTolerance);
            if (!seen.Add(key))
            {
                continue;
            }

            var wallIds = grid.BoundaryWallIds(component).ToArray();
            if (wallIds.Length == 0)
            {
                continue;
            }

            if (AddRoom(page, pageNumber, bounds, boundary, wallIds, "wall graph orthogonal face", context, suppressed))
            {
                added++;
            }
        }

        AddSliverRoomSuppressionDiagnostic(pageNumber, suppressed, context);

        if (added > 0)
        {
            var roomWallIds = context.Rooms
                .Where(room => room.PageNumber == pageNumber)
                .SelectMany(room => room.WallIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var sourcePrimitiveIds = context.Walls
                .Where(wall => roomWallIds.Contains(wall.Id, StringComparer.Ordinal))
                .SelectMany(wall => wall.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            context.AddDiagnostic(
                "rooms.wall_graph_cycles.detected",
                DiagnosticSeverity.Info,
                StageName,
                $"{added} room candidates were extracted from closed wall-graph faces.",
                pageNumber,
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: sourcePrimitiveIds,
                properties: new Dictionary<string, string>
                {
                    ["roomCount"] = added.ToString(),
                    ["gridCellCount"] = grid.CellCount.ToString(),
                    ["graphEdgeCount"] = grid.GraphEdgeCount.ToString()
                });
        }

        return added;
    }

    private int AddRectangularFallbackRooms(
        PlanPage page,
        int pageNumber,
        IReadOnlyList<WallSegment> structuralWalls,
        ScanContext context,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var semanticBoundaryWalls = SemanticBoundaryWallCandidates(pageNumber, structuralWalls, context);
        var axisWalls = semanticBoundaryWalls
            .Select(wall => AxisWall.TryCreate(wall, context.Options.GeometryTolerance.Distance))
            .Where(axis => axis is not null)
            .Select(axis => axis!)
            .ToArray();

        var horizontal = axisWalls.Where(axis => axis.Orientation == WallOrientation.Horizontal).ToArray();
        var vertical = axisWalls.Where(axis => axis.Orientation == WallOrientation.Vertical).ToArray();
        var addedForPage = 0;
        var suppressed = new List<SuppressedRoomCandidate>();

        for (var h1Index = 0; h1Index < horizontal.Length; h1Index++)
        {
            for (var h2Index = h1Index + 1; h2Index < horizontal.Length; h2Index++)
            {
                var top = horizontal[h1Index];
                var bottom = horizontal[h2Index];
                var yTop = Math.Min(top.Coordinate, bottom.Coordinate);
                var yBottom = Math.Max(top.Coordinate, bottom.Coordinate);

                if (yBottom - yTop < context.Options.MinOpeningGap)
                {
                    continue;
                }

                for (var v1Index = 0; v1Index < vertical.Length; v1Index++)
                {
                    for (var v2Index = v1Index + 1; v2Index < vertical.Length; v2Index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var left = vertical[v1Index];
                        var right = vertical[v2Index];
                        var xLeft = Math.Min(left.Coordinate, right.Coordinate);
                        var xRight = Math.Max(left.Coordinate, right.Coordinate);

                        if (xRight - xLeft < context.Options.MinOpeningGap)
                        {
                            continue;
                        }

                        var roomBounds = PlanRect.FromEdges(xLeft, yTop, xRight, yBottom);
                        if (roomBounds.Area < context.Options.MinRoomArea)
                        {
                            continue;
                        }

                        if (!top.Covers(xLeft, xRight, context.Options.WallSnapTolerance)
                            || !bottom.Covers(xLeft, xRight, context.Options.WallSnapTolerance)
                            || !left.Covers(yTop, yBottom, context.Options.WallSnapTolerance)
                            || !right.Covers(yTop, yBottom, context.Options.WallSnapTolerance))
                        {
                            continue;
                        }

                        var boundary = RectangleBoundary(xLeft, yTop, xRight, yBottom);
                        var key = RoomKey(boundary, context.Options.WallSnapTolerance);
                        if (!seen.Add(key))
                        {
                            continue;
                        }

                        var wallIds = new[] { top.Wall.Id, bottom.Wall.Id, left.Wall.Id, right.Wall.Id }
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();

                        if (AddRoom(page, pageNumber, roomBounds, boundary, wallIds, "rectangular wall coverage fallback", context, suppressed))
                        {
                            addedForPage++;
                        }

                        if (addedForPage >= context.Options.MaxRoomCandidatesPerPage)
                        {
                            AddSliverRoomSuppressionDiagnostic(pageNumber, suppressed, context);
                            AddRoomLimitDiagnostic(pageNumber, addedForPage, context);
                            return addedForPage;
                        }
                    }
                }
            }
        }

        AddSliverRoomSuppressionDiagnostic(pageNumber, suppressed, context);
        return addedForPage;
    }

    private int AddSemanticLabelRooms(
        PlanPage page,
        int pageNumber,
        IReadOnlyList<WallSegment> structuralWalls,
        ScanContext context,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var mainRegion = context.SheetRegions
            .Where(region => region.PageNumber == pageNumber && region.Kind == RegionKind.MainFloorPlan)
            .OrderBy(region => region.Bounds.Area)
            .FirstOrDefault();
        if (mainRegion is null)
        {
            return 0;
        }

        var textLines = BuildRoomTextLines(page, context, mainRegion.Bounds).ToArray();
        if (textLines.Length < 2)
        {
            return 0;
        }

        var axisWalls = structuralWalls
            .Select(wall => AxisWall.TryCreate(wall, context.Options.GeometryTolerance.Distance))
            .Where(axis => axis is not null)
            .Select(axis => axis!)
            .ToArray();
        var candidates = BuildSemanticRoomCandidates(page, textLines, mainRegion.Bounds, context.Options).ToArray();
        var added = 0;
        var approximateCount = 0;
        var wallBoundedCount = 0;
        var sourcePrimitiveIds = new List<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (added >= context.Options.MaxRoomCandidatesPerPage)
            {
                AddRoomLimitDiagnostic(pageNumber, added, context);
                break;
            }

            if (context.Rooms
                .Where(room => room.PageNumber == pageNumber)
                .Any(room => room.Bounds.Contains(candidate.AnchorPoint, context.Options.WallSnapTolerance)
                    || OverlapRatio(candidate.EvidenceBounds, room.Bounds) > 0.55))
            {
                continue;
            }

            var boundary = TryInferSemanticRoomBoundary(
                candidate,
                axisWalls,
                mainRegion.Bounds,
                context.Options,
                context.Calibration,
                pageNumber,
                mainRegion.Id,
                out var wallIds,
                out var boundaryEvidence);
            var isWallBounded = boundary.Count >= 4 && wallIds.Length >= 2;
            var seedScaleGroup = context.Calibration.SelectMeasurementScaleGroup(
                pageNumber,
                candidate.EvidenceBounds,
                mainRegion.Id);
            var roomBounds = isWallBounded
                ? PlanRect.Union(boundary.Select(point => new PlanRect(point.X, point.Y, 0, 0)))
                : BuildApproximateSemanticRoomBounds(
                    candidate,
                    mainRegion.Bounds,
                    context.Options,
                    context.Calibration,
                    seedScaleGroup);

            if (!isWallBounded)
            {
                boundary = RectangleBoundary(roomBounds.Left, roomBounds.Top, roomBounds.Right, roomBounds.Bottom);
            }

            if (roomBounds.IsEmpty || roomBounds.Area < Math.Max(80, context.Options.MinRoomArea * 0.15))
            {
                continue;
            }

            var key = $"semantic:{pageNumber}:{NormalizeLabel(candidate.Label.Text)}:{Math.Round(candidate.Area.Bounds.Center.X)}:{Math.Round(candidate.Area.Bounds.Center.Y)}";
            if (!seen.Add(key))
            {
                continue;
            }

            var useClassification = ClassifyRoomUse(candidate.Label.Text, roomBounds);
            var confidence = isWallBounded ? 0.62 : 0.48;
            var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(pageNumber, roomBounds, mainRegion.Id)
                ?? seedScaleGroup;
            var evidence = new List<string>
            {
                $"semantic room seed from label '{candidate.Label.Text}' and area text '{candidate.Area.Text}'",
                isWallBounded
                    ? "semantic room seed was bounded by nearby orthogonal wall evidence"
                    : "semantic room seed has approximate label/area bounds and requires wall-boundary review"
            };
            evidence.AddRange(boundaryEvidence);
            evidence.AddRange(useClassification.Evidence);

            context.Rooms.Add(
                new RoomRegion(
                    $"page:{pageNumber}:room:{context.Rooms.Count + 1}",
                    pageNumber,
                    roomBounds,
                    boundary,
                    wallIds,
                    new Confidence(confidence))
                {
                    Label = candidate.Label.Text,
                    UseKind = useClassification.Kind,
                    LabelSourcePrimitiveIds = candidate.SourcePrimitiveIds,
                    Evidence = evidence.ToArray(),
                    AreaSquareMeters = candidate.AreaSquareMeters,
                    MeasurementScaleGroupId = scaleGroup?.Id
                });

            added++;
            approximateCount += isWallBounded ? 0 : 1;
            wallBoundedCount += isWallBounded ? 1 : 0;
            sourcePrimitiveIds.AddRange(candidate.SourcePrimitiveIds);
        }

        if (added > 0)
        {
            context.AddDiagnostic(
                "rooms.semantic_label_seeds.detected",
                DiagnosticSeverity.Info,
                StageName,
                $"Added {added} semantic room seed(s) from room label and area text evidence.",
                pageNumber,
                PlanRect.Union(context.Rooms
                    .Where(room => room.PageNumber == pageNumber)
                    .TakeLast(added)
                    .Select(room => room.Bounds)),
                Confidence.Medium,
                DiagnosticScope.Room,
                sourcePrimitiveIds.Distinct(StringComparer.Ordinal),
                new Dictionary<string, string>
                {
                    ["semanticRoomSeedCount"] = added.ToString(),
                    ["wallBoundedSeedCount"] = wallBoundedCount.ToString(),
                    ["approximateSeedCount"] = approximateCount.ToString()
                });
        }

        return added;
    }

    private static IReadOnlyList<WallSegment> SemanticBoundaryWallCandidates(
        int pageNumber,
        IReadOnlyList<WallSegment> structuralWalls,
        ScanContext context)
    {
        var structuralWallIds = structuralWalls
            .Select(wall => wall.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var componentByWallId = context.WallGraph.Components
            .SelectMany(component => component.WallIds.Select(wallId => new { wallId, component }))
            .Where(item => !string.IsNullOrWhiteSpace(item.wallId))
            .GroupBy(item => item.wallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().component, StringComparer.Ordinal);
        var assessmentByWallId = context.WallEvidenceMap.WallAssessments
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .GroupBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        return context.Walls
            .Where(wall => wall.PageNumber == pageNumber
                && !string.IsNullOrWhiteSpace(wall.Id)
                && wall.DrawingLength >= Math.Max(context.Options.MinWallLength, context.Options.DefaultWallThickness * 8.0)
                && (wall.CenterLine.IsHorizontal(context.Options.GeometryTolerance.Distance)
                    || wall.CenterLine.IsVertical(context.Options.GeometryTolerance.Distance))
                && (structuralWallIds.Contains(wall.Id)
                    || IsSemanticBoundarySupportWall(
                        wall,
                        assessmentByWallId.TryGetValue(wall.Id, out var assessment) ? assessment : null,
                        componentByWallId.TryGetValue(wall.Id, out var component) ? component : null)))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static bool IsSemanticBoundarySupportWall(
        WallSegment wall,
        WallEvidenceWallAssessment? assessment,
        WallGraphComponent? component)
    {
        if (component is { ExcludedFromStructuralTopology: true }
            || component?.Kind is WallGraphComponentKind.ObjectLikeIsland or WallGraphComponentKind.IsolatedFragment)
        {
            return false;
        }

        var evidence = assessment is null
            ? wall.Evidence
            : wall.Evidence.Concat(assessment.Evidence).ToArray();
        if (evidence.Any(IsSemanticBoundaryBlockingWallEvidence))
        {
            return false;
        }

        if (wall.WallType == WallType.Exterior
            && wall.Confidence.Value >= 0.75
            && (assessment is null || (!assessment.RejectedAsNoise && !assessment.RequiresReview))
            && wall.DetectionKind is WallDetectionKind.SingleLine
                or WallDetectionKind.ParallelLinePair
                or WallDetectionKind.FragmentMerged)
        {
            return true;
        }

        if (assessment is null)
        {
            return wall.Confidence.Value >= 0.55;
        }

        if (assessment.RejectedAsNoise
            || assessment.RequiresReview
            || !assessment.PlacementReady
            || assessment.Category is not (WallEvidenceCategory.StrongWallBody
                or WallEvidenceCategory.MediumWallBody
                or WallEvidenceCategory.RecoveredWallBody))
        {
            return false;
        }

        return true;
    }

    private static bool IsSemanticBoundaryBlockingWallEvidence(string evidence) =>
        evidence.Contains("dimension-like", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("dense local detail", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("surface pattern", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("opening detail", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("stair/object", StringComparison.OrdinalIgnoreCase)
        || evidence.Contains("object linework", StringComparison.OrdinalIgnoreCase);

    private bool AddRoom(
        PlanPage page,
        int pageNumber,
        PlanRect bounds,
        IReadOnlyList<PlanPoint> boundary,
        IReadOnlyList<string> wallIds,
        string solverEvidence,
        ScanContext context,
        ICollection<SuppressedRoomCandidate> suppressed)
    {
        var label = MatchRoomLabel(page, bounds, boundary, context);
        var useClassification = ClassifyRoomUse(label?.Text, bounds);
        if (ShouldSuppressSliverRoom(bounds, wallIds, label, useClassification, context, out var suppression))
        {
            suppressed.Add(suppression);
            return false;
        }

        var averageConfidence = wallIds
            .Select(id => context.Walls.First(wall => wall.Id == id).Confidence.Value)
            .DefaultIfEmpty(0.5)
            .Average();
        var roomConfidence = Math.Min(0.9, averageConfidence + 0.1 + (label is not null ? 0.04 : 0));
        var drawingArea = PolygonArea(boundary);
        if (drawingArea <= 0)
        {
            drawingArea = bounds.Area;
        }
        var sourceRegionId = ResolveRoomSourceRegionId(pageNumber, bounds, wallIds, context);
        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(pageNumber, bounds, sourceRegionId);

        context.Rooms.Add(
            new RoomRegion(
                $"page:{pageNumber}:room:{context.Rooms.Count + 1}",
                pageNumber,
                bounds,
                boundary,
                wallIds,
                new Confidence(roomConfidence))
            {
                Label = label?.Text,
                UseKind = useClassification.Kind,
                LabelSourcePrimitiveIds = label?.SourcePrimitiveIds ?? Array.Empty<string>(),
                Evidence = RoomEvidence(wallIds, label, solverEvidence, boundary)
                    .Concat(useClassification.Evidence)
                    .ToArray(),
                AreaSquareMeters = context.Calibration.ToSquareMeters(drawingArea, scaleGroup),
                MeasurementScaleGroupId = scaleGroup?.Id
            });
        return true;
    }

    private static IEnumerable<RoomTextLine> BuildRoomTextLines(
        PlanPage page,
        ScanContext context,
        PlanRect mainBounds)
    {
        var annotationSourceIds = AnnotationSourcePrimitiveIds(context);
        var words = page.Primitives
            .Select((primitive, index) => new
            {
                Primitive = primitive,
                SourceId = context.PrimitiveId(page.Number, index, primitive)
            })
            .Where(item => item.Primitive is TextPrimitive text
                && !string.IsNullOrWhiteSpace(text.Text)
                && !annotationSourceIds.Contains(item.SourceId)
                && mainBounds.Contains(text.Bounds.Center, Math.Max(2, context.Options.WallSnapTolerance)))
            .Select(item =>
            {
                var text = (TextPrimitive)item.Primitive;
                return new RoomTextWord(
                    text.Text.Trim(),
                    item.SourceId,
                    text.Bounds,
                    HasRoomLayerHint(text));
            })
            .Where(word => !LooksLikeAnnotationOnlyRoomText(word.Text))
            .OrderBy(word => word.Bounds.Center.Y)
            .ThenBy(word => word.Bounds.Left)
            .ToArray();

        if (words.Length == 0)
        {
            yield break;
        }

        var rowTolerance = Math.Max(3, MedianValue(words.Select(word => Math.Max(1, word.Bounds.Height)).DefaultIfEmpty(12)) * 0.85);
        var gapTolerance = Math.Max(26, Math.Min(page.Size.Width, page.Size.Height) * 0.035);
        var rows = new List<List<RoomTextWord>>();

        foreach (var word in words)
        {
            var row = rows
                .OrderBy(candidate => Math.Abs(candidate.Average(item => item.Bounds.Center.Y) - word.Bounds.Center.Y))
                .FirstOrDefault(candidate => Math.Abs(candidate.Average(item => item.Bounds.Center.Y) - word.Bounds.Center.Y) <= rowTolerance);

            if (row is null)
            {
                row = new List<RoomTextWord>();
                rows.Add(row);
            }

            row.Add(word);
        }

        foreach (var row in rows.OrderBy(row => row.Average(word => word.Bounds.Center.Y)))
        {
            var ordered = row.OrderBy(word => word.Bounds.Left).ToArray();
            var current = new List<RoomTextWord>();
            foreach (var word in ordered)
            {
                if (current.Count > 0)
                {
                    var previous = current[^1];
                    var gap = word.Bounds.Left - previous.Bounds.Right;
                    var localGapTolerance = Math.Max(gapTolerance, Math.Max(previous.Bounds.Width, word.Bounds.Width) * 1.8);
                    if (gap > localGapTolerance)
                    {
                        var line = BuildRoomTextLine(current);
                        if (line is not null)
                        {
                            yield return line;
                        }

                        current.Clear();
                    }
                }

                current.Add(word);
            }

            var finalLine = BuildRoomTextLine(current);
            if (finalLine is not null)
            {
                yield return finalLine;
            }
        }
    }

    private static RoomTextLine? BuildRoomTextLine(IReadOnlyList<RoomTextWord> words)
    {
        if (words.Count == 0)
        {
            return null;
        }

        var text = string.Join(" ", words.Select(word => word.Text)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return new RoomTextLine(
            text,
            PlanRect.Union(words.Select(word => word.Bounds)),
            words.Select(word => word.SourcePrimitiveId).Distinct(StringComparer.Ordinal).ToArray(),
            words.Any(word => word.HasLayerHint));
    }

    private static IEnumerable<SemanticRoomCandidate> BuildSemanticRoomCandidates(
        PlanPage page,
        IReadOnlyList<RoomTextLine> textLines,
        PlanRect mainBounds,
        ScannerOptions options)
    {
        var minPageSpan = Math.Min(page.Size.Width, page.Size.Height);
        var maxVerticalGap = Math.Max(36, minPageSpan * 0.065);
        var maxHorizontalDrift = Math.Max(90, minPageSpan * 0.18);
        var maxCompactCodeVerticalGap = Math.Max(10, minPageSpan * 0.02);
        var maxCompactCodeHorizontalDrift = Math.Max(18, minPageSpan * 0.035);
        var areaLines = textLines
            .Where(line => LooksLikeRoomAreaText(line.Text))
            .OrderBy(line => line.Bounds.Top)
            .ThenBy(line => line.Bounds.Left)
            .ToArray();
        var yielded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var area in areaLines)
        {
            if (!TryParseRoomAreaSquareMeters(area.Text, out var squareMeters))
            {
                continue;
            }

            var label = textLines
                .Where(candidate => !ReferenceEquals(candidate, area)
                    && candidate.Bounds.Bottom <= area.Bounds.Bottom + Math.Max(4, options.WallSnapTolerance)
                    && area.Bounds.Top - candidate.Bounds.Bottom <= maxVerticalGap
                    && Math.Abs(candidate.Bounds.Center.X - area.Bounds.Center.X) <= maxHorizontalDrift
                    && (IsTrustedSemanticRoomSeedLabel(candidate.Text, candidate.HasLayerHint || area.HasLayerHint)
                        || IsAreaBackedCompactRoomCodeLabel(
                            candidate,
                            area,
                            maxCompactCodeVerticalGap,
                            maxCompactCodeHorizontalDrift)))
                .Where(candidate => !LooksLikeRoomAreaText(candidate.Text))
                .Select(candidate => new
                {
                    Line = candidate,
                    VerticalGap = Math.Max(0, area.Bounds.Top - candidate.Bounds.Bottom),
                    HorizontalDrift = Math.Abs(candidate.Bounds.Center.X - area.Bounds.Center.X),
                    Overlap = HorizontalOverlapRatio(candidate.Bounds, area.Bounds),
                    KnownUse = HasKnownRoomUseTerm(candidate.Text),
                    AreaBackedCode = IsAreaBackedCompactRoomCodeLabel(
                        candidate,
                        area,
                        maxCompactCodeVerticalGap,
                        maxCompactCodeHorizontalDrift)
                })
                .Where(candidate => candidate.Overlap > 0.08 || candidate.HorizontalDrift <= maxHorizontalDrift * 0.55)
                .OrderByDescending(candidate => candidate.Line.HasLayerHint)
                .ThenByDescending(candidate => candidate.KnownUse)
                .ThenByDescending(candidate => candidate.AreaBackedCode)
                .ThenByDescending(candidate => candidate.Overlap)
                .ThenBy(candidate => candidate.VerticalGap)
                .ThenBy(candidate => candidate.HorizontalDrift)
                .Select(candidate => candidate.Line)
                .FirstOrDefault();

            if (label is null)
            {
                continue;
            }

            var evidenceBounds = PlanRect.Union(label.Bounds, area.Bounds).ClampTo(mainBounds);
            var key = string.Join("|", label.SourcePrimitiveIds.Concat(area.SourcePrimitiveIds).Order(StringComparer.Ordinal));
            if (!yielded.Add(key))
            {
                continue;
            }

            yield return new SemanticRoomCandidate(
                label,
                area,
                evidenceBounds,
                evidenceBounds.Center,
                squareMeters,
                label.SourcePrimitiveIds.Concat(area.SourcePrimitiveIds).Distinct(StringComparer.Ordinal).ToArray());
        }
    }

    private static IReadOnlyList<PlanPoint> TryInferSemanticRoomBoundary(
        SemanticRoomCandidate candidate,
        IReadOnlyList<AxisWall> axisWalls,
        PlanRect mainBounds,
        ScannerOptions options,
        PlanCalibration calibration,
        int pageNumber,
        string sourceRegionId,
        out string[] wallIds,
        out IReadOnlyList<string> evidence)
    {
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            pageNumber,
            candidate.EvidenceBounds,
            sourceRegionId);
        var expectedDrawingArea = ExpectedDrawingArea(candidate.AreaSquareMeters, calibration, scaleGroup);
        var expectedSpan = expectedDrawingArea is > 0 ? Math.Sqrt(expectedDrawingArea.Value) : 0;
        var searchRadius = Math.Max(
            Math.Max(
                140,
                Math.Max(
                    Math.Min(mainBounds.Width, mainBounds.Height) * 0.45,
                    Math.Max(mainBounds.Width, mainBounds.Height) * 0.55)),
            expectedSpan * 1.35);
        var spanTolerance = Math.Max(options.WallSnapTolerance * 4.0, options.DefaultWallThickness * 8.0);
        var boundaryCandidates = BuildAxisBoundaryCandidates(axisWalls, options).ToArray();
        var vertical = boundaryCandidates.Where(axis => axis.Orientation == WallOrientation.Vertical).ToArray();
        var horizontal = boundaryCandidates.Where(axis => axis.Orientation == WallOrientation.Horizontal).ToArray();
        var leftCandidates = SemanticBoundarySideCandidates(
            vertical,
            axis => axis.Coordinate < candidate.AnchorPoint.X,
            axis => candidate.AnchorPoint.X - axis.Coordinate,
            searchRadius);
        var rightCandidates = SemanticBoundarySideCandidates(
            vertical,
            axis => axis.Coordinate > candidate.AnchorPoint.X,
            axis => axis.Coordinate - candidate.AnchorPoint.X,
            searchRadius);
        var topCandidates = SemanticBoundarySideCandidates(
            horizontal,
            axis => axis.Coordinate < candidate.AnchorPoint.Y,
            axis => candidate.AnchorPoint.Y - axis.Coordinate,
            searchRadius);
        var bottomCandidates = SemanticBoundarySideCandidates(
            horizontal,
            axis => axis.Coordinate > candidate.AnchorPoint.Y,
            axis => axis.Coordinate - candidate.AnchorPoint.Y,
            searchRadius);

        var best = default(SemanticRoomBoundaryMatch?);
        foreach (var left in leftCandidates)
        {
            foreach (var right in rightCandidates)
            {
                foreach (var top in topCandidates)
                {
                    foreach (var bottom in bottomCandidates)
                    {
                        var match = ScoreSemanticRoomBoundaryCandidate(
                            candidate,
                            mainBounds,
                            left,
                            right,
                            top,
                            bottom,
                            expectedDrawingArea,
                            spanTolerance,
                            options);
                        if (match is not null
                            && (best is null || match.Score > best.Score))
                        {
                            best = match;
                        }
                    }
                }
            }
        }

        if (best is null)
        {
            wallIds = Array.Empty<string>();
            evidence = new[]
            {
                "semantic room boundary could not be closed from four nearby orthogonal walls",
                $"semantic room boundary candidate counts left={leftCandidates.Length}, right={rightCandidates.Length}, top={topCandidates.Length}, bottom={bottomCandidates.Length}"
            };
            return Array.Empty<PlanPoint>();
        }

        var matched = best;
        var bounds = matched.Bounds.ClampTo(mainBounds);
        if (bounds.IsEmpty || bounds.Area < Math.Max(80, options.MinRoomArea * 0.15))
        {
            wallIds = Array.Empty<string>();
            evidence = new[] { "semantic room boundary from nearby walls was too small or outside the main floorplan" };
            return Array.Empty<PlanPoint>();
        }

        if (!bounds.Contains(candidate.EvidenceBounds.Center, spanTolerance))
        {
            wallIds = Array.Empty<string>();
            evidence = new[] { "semantic room label/area evidence did not sit inside inferred wall bounds" };
            return Array.Empty<PlanPoint>();
        }

        wallIds = matched.WallIds;
        var evidenceItems = new List<string>
        {
            $"semantic room boundary inferred from nearby wall candidates {string.Join(",", wallIds)}",
            $"semantic room boundary wall coverage {matched.AverageCoverage.ToString("0.###", CultureInfo.InvariantCulture)} across {matched.StrongSideCount} strong side(s)",
            $"semantic room boundary trusted wall support {matched.TrustedWallRatio.ToString("0.###", CultureInfo.InvariantCulture)} across {matched.TrustedSideCount} side(s)"
        };
        if (expectedDrawingArea is > 0)
        {
            evidenceItems.Add(
                $"semantic room boundary area match ratio {matched.AreaRatio.ToString("0.###", CultureInfo.InvariantCulture)} from printed room area");
        }

        evidence = evidenceItems;
        return RectangleBoundary(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
    }

    private static SemanticRoomBoundaryMatch? ScoreSemanticRoomBoundaryCandidate(
        SemanticRoomCandidate candidate,
        PlanRect mainBounds,
        AxisBoundaryCandidate left,
        AxisBoundaryCandidate right,
        AxisBoundaryCandidate top,
        AxisBoundaryCandidate bottom,
        double? expectedDrawingArea,
        double tolerance,
        ScannerOptions options)
    {
        var bounds = PlanRect
            .FromEdges(left.Coordinate, top.Coordinate, right.Coordinate, bottom.Coordinate)
            .ClampTo(mainBounds);
        if (bounds.IsEmpty
            || bounds.Width < Math.Max(options.MinOpeningGap, options.DefaultWallThickness * 8.0)
            || bounds.Height < Math.Max(options.MinOpeningGap, options.DefaultWallThickness * 8.0)
            || bounds.Area < Math.Max(80, options.MinRoomArea * 0.15)
            || !bounds.Contains(candidate.EvidenceBounds.Center, tolerance)
            || !bounds.Intersects(candidate.EvidenceBounds, tolerance))
        {
            return null;
        }

        var topCoverage = top.Coverage(bounds.Left, bounds.Right, tolerance);
        var bottomCoverage = bottom.Coverage(bounds.Left, bounds.Right, tolerance);
        var leftCoverage = left.Coverage(bounds.Top, bounds.Bottom, tolerance);
        var rightCoverage = right.Coverage(bounds.Top, bounds.Bottom, tolerance);
        var coverages = new[] { topCoverage, bottomCoverage, leftCoverage, rightCoverage };
        var strongSideCount = coverages.Count(value => value >= 0.35);
        var averageCoverage = coverages.Average();
        if (strongSideCount < 3 || averageCoverage < 0.42)
        {
            return null;
        }

        var areaScore = 0.55;
        var areaRatio = 1.0;
        if (expectedDrawingArea is > 0)
        {
            areaRatio = bounds.Area / expectedDrawingArea.Value;
            if (areaRatio < 0.2 || areaRatio > 5.0)
            {
                return null;
            }

            areaScore = 1.0 / (1.0 + Math.Abs(Math.Log(areaRatio)));
        }

        var distancePenalty =
            (Math.Abs(candidate.AnchorPoint.X - left.Coordinate)
                + Math.Abs(right.Coordinate - candidate.AnchorPoint.X)
                + Math.Abs(candidate.AnchorPoint.Y - top.Coordinate)
                + Math.Abs(bottom.Coordinate - candidate.AnchorPoint.Y))
            / Math.Max(1, Math.Min(mainBounds.Width, mainBounds.Height) * 4.0);
        var labelCentering =
            1.0
            - Math.Min(
                1.0,
                candidate.AnchorPoint.DistanceTo(bounds.Center)
                / Math.Max(1, Math.Sqrt(bounds.Area)));

        var topWalls = top.Walls(bounds.Left, bounds.Right, tolerance).ToArray();
        var bottomWalls = bottom.Walls(bounds.Left, bounds.Right, tolerance).ToArray();
        var leftWalls = left.Walls(bounds.Top, bounds.Bottom, tolerance).ToArray();
        var rightWalls = right.Walls(bounds.Top, bounds.Bottom, tolerance).ToArray();
        var allWalls = topWalls
            .Concat(bottomWalls)
            .Concat(leftWalls)
            .Concat(rightWalls)
            .Where(wall => !string.IsNullOrWhiteSpace(wall.Id))
            .GroupBy(wall => wall.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        if (allWalls.Length < 3)
        {
            return null;
        }

        var trustedWallIds = allWalls
            .Where(IsTrustedSemanticRoomReferenceWall)
            .Select(wall => wall.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var trustedSideCount = new[]
        {
            topWalls.Any(IsTrustedSemanticRoomReferenceWall),
            bottomWalls.Any(IsTrustedSemanticRoomReferenceWall),
            leftWalls.Any(IsTrustedSemanticRoomReferenceWall),
            rightWalls.Any(IsTrustedSemanticRoomReferenceWall)
        }.Count(value => value);
        var hasTrustedBoundaryReference = trustedWallIds.Length >= 3 && trustedSideCount >= 3;
        var canUseReviewBoundaryFallback = !hasTrustedBoundaryReference
            && CanUseReviewSupportedSemanticBoundary(candidate.Label.Text)
            && strongSideCount >= 3
            && averageCoverage >= 0.68
            && areaRatio is >= 0.35 and <= 2.75;
        if (!hasTrustedBoundaryReference && !canUseReviewBoundaryFallback)
        {
            return null;
        }

        var outputWallIds = hasTrustedBoundaryReference
            ? trustedWallIds
            : allWalls.Select(wall => wall.Id).Distinct(StringComparer.Ordinal).ToArray();
        var trustedWallRatio = trustedWallIds.Length / (double)allWalls.Length;
        var noisyWallPenalty = (allWalls.Length - trustedWallIds.Length) * 0.16;
        var reviewFallbackPenalty = hasTrustedBoundaryReference ? 0 : 0.75;
        var score = (averageCoverage * 1.65)
            + (areaScore * 1.25)
            + (strongSideCount * 0.12)
            + (labelCentering * 0.3)
            + (trustedSideCount * 0.08)
            + (trustedWallRatio * 0.45)
            - noisyWallPenalty
            - reviewFallbackPenalty
            - distancePenalty;
        return new SemanticRoomBoundaryMatch(
            bounds,
            outputWallIds,
            score,
            averageCoverage,
            strongSideCount,
            areaRatio,
            trustedWallRatio,
            trustedSideCount);
    }

    private static bool CanUseReviewSupportedSemanticBoundary(string label)
    {
        var kind = ClassifyRoomUse(label, new PlanRect(0, 0, 20, 20)).Kind;
        return kind is not RoomUseKind.Unknown
            and not RoomUseKind.Corridor
            and not RoomUseKind.Outdoor
            and not RoomUseKind.Stair
            and not RoomUseKind.Elevator
            and not RoomUseKind.Shaft;
    }

    private static bool IsTrustedSemanticRoomReferenceWall(WallSegment wall)
    {
        if (wall.Id.Contains("recovered-short", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (wall.Evidence.Any(IsSemanticBoundaryBlockingWallEvidence)
            || wall.Evidence.Any(evidence =>
                evidence.Contains("requires review", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("/ review", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("review required", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("rejected", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (wall.Evidence.Any(evidence => evidence.Contains("/ placement-ready", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return wall.Confidence.Value >= 0.72
            && wall.DetectionKind is WallDetectionKind.ParallelLinePair
                or WallDetectionKind.SingleLine
                or WallDetectionKind.FragmentMerged;
    }

    private static double? ExpectedDrawingArea(
        double areaSquareMeters,
        PlanCalibration calibration,
        CalibrationScaleGroup? scaleGroup)
    {
        var millimetersPerDrawingUnit = scaleGroup?.MillimetersPerDrawingUnit;
        if (millimetersPerDrawingUnit is not > 0
            && calibration.MillimetersPerDrawingUnit is > 0
            && calibration.Confidence.Value >= 0.5)
        {
            millimetersPerDrawingUnit = calibration.MillimetersPerDrawingUnit.Value;
        }

        return areaSquareMeters > 0 && millimetersPerDrawingUnit is > 0
            ? areaSquareMeters * 1_000_000.0 / (millimetersPerDrawingUnit.Value * millimetersPerDrawingUnit.Value)
            : null;
    }

    private static PlanRect BuildApproximateSemanticRoomBounds(
        SemanticRoomCandidate candidate,
        PlanRect mainBounds,
        ScannerOptions options,
        PlanCalibration calibration,
        CalibrationScaleGroup? scaleGroup)
    {
        var padding = Math.Max(8, options.DefaultWallThickness * 3.0);
        var fallback = candidate.EvidenceBounds
            .Inflate(padding)
            .ClampTo(mainBounds);
        var expectedDrawingArea = ExpectedDrawingArea(candidate.AreaSquareMeters, calibration, scaleGroup);
        if (expectedDrawingArea is not > 0)
        {
            return fallback;
        }

        var aspectRatio = candidate.AreaSquareMeters >= 20 ? 1.35 : 1.15;
        var width = Math.Sqrt(expectedDrawingArea.Value * aspectRatio);
        var height = Math.Sqrt(expectedDrawingArea.Value / aspectRatio);
        width = Math.Max(width, Math.Max(fallback.Width, candidate.EvidenceBounds.Width + padding * 2.0));
        height = Math.Max(height, Math.Max(fallback.Height, candidate.EvidenceBounds.Height + padding * 2.0));

        if (width > mainBounds.Width)
        {
            width = mainBounds.Width;
        }

        if (height > mainBounds.Height)
        {
            height = mainBounds.Height;
        }

        return ShiftInsideMainBounds(
            new PlanRect(
                candidate.AnchorPoint.X - width / 2.0,
                candidate.AnchorPoint.Y - height / 2.0,
                width,
                height),
            mainBounds);
    }

    private static PlanRect ShiftInsideMainBounds(PlanRect bounds, PlanRect mainBounds)
    {
        var width = Math.Min(bounds.Width, mainBounds.Width);
        var height = Math.Min(bounds.Height, mainBounds.Height);
        var x = bounds.X;
        var y = bounds.Y;

        if (x < mainBounds.Left)
        {
            x = mainBounds.Left;
        }
        else if (x + width > mainBounds.Right)
        {
            x = mainBounds.Right - width;
        }

        if (y < mainBounds.Top)
        {
            y = mainBounds.Top;
        }
        else if (y + height > mainBounds.Bottom)
        {
            y = mainBounds.Bottom - height;
        }

        return new PlanRect(x, y, width, height);
    }

    private static AxisBoundaryCandidate[] SemanticBoundarySideCandidates(
        IReadOnlyList<AxisBoundaryCandidate> candidates,
        Func<AxisBoundaryCandidate, bool> predicate,
        Func<AxisBoundaryCandidate, double> distance,
        double searchRadius) =>
        candidates
            .Where(candidate => predicate(candidate) && distance(candidate) <= searchRadius)
            .OrderBy(distance)
            .Take(12)
            .ToArray();

    private static IEnumerable<AxisBoundaryCandidate> BuildAxisBoundaryCandidates(
        IReadOnlyList<AxisWall> axisWalls,
        ScannerOptions options)
    {
        var coordinateTolerance = Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness * 1.25);
        foreach (var group in axisWalls.GroupBy(axis => axis.Orientation))
        {
            var ordered = group.OrderBy(axis => axis.Coordinate).ToArray();
            var cluster = new List<AxisWall>();
            foreach (var axis in ordered)
            {
                if (cluster.Count > 0
                    && Math.Abs(axis.Coordinate - cluster.Average(item => item.Coordinate)) > coordinateTolerance)
                {
                    yield return AxisBoundaryCandidate.From(cluster);
                    cluster.Clear();
                }

                cluster.Add(axis);
            }

            if (cluster.Count > 0)
            {
                yield return AxisBoundaryCandidate.From(cluster);
            }
        }
    }

    private static bool LooksLikeAnnotationOnlyRoomText(string text)
    {
        var trimmed = text.Trim();
        if (IsPotentialAreaBackedCompactRoomCodeText(trimmed))
        {
            return false;
        }

        return trimmed.Length == 0
            || LooksLikeDimensionText(trimmed)
            || LooksLikeFloorLevelText(trimmed)
            || LooksLikeEquipmentTagText(trimmed)
            || LooksLikeNonRoomDescriptor(trimmed);
    }

    private static bool LooksLikeRoomAreaText(string text) =>
        RoomAreaMeasurementRegex.IsMatch(text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal));

    private static bool TryParseRoomAreaSquareMeters(string text, out double squareMeters)
    {
        squareMeters = 0;
        var normalized = text
            .Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("m2", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("m^2", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("m\u00B2", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\u33A1", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(',', '.');

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out squareMeters)
            && squareMeters > 0;
    }

    private static bool LooksLikeRoomSemanticLabel(string text)
    {
        var trimmed = text.Trim();
        if (!LabelHasLetters(trimmed) || trimmed.Length > 48)
        {
            return false;
        }

        var hasKnownRoomUse = HasKnownRoomUseTerm(trimmed);
        if (!hasKnownRoomUse)
        {
            return false;
        }

        return !LooksLikeNonRoomDescriptor(trimmed)
            && !LooksLikeFloorLevelText(trimmed)
            && !LooksLikeEquipmentTagText(trimmed)
            && (!LooksLikeDimensionText(trimmed) || hasKnownRoomUse);
    }

    private static bool IsTrustedSemanticRoomSeedLabel(string text, bool hasLayerHint)
    {
        var trimmed = text.Trim();
        if (!LabelHasLetters(trimmed) && !hasLayerHint)
        {
            return false;
        }

        if (LooksLikeRoomAreaText(trimmed)
            || LooksLikeSingleLetterClusterText(trimmed)
            || LooksLikeMultiCodeTagText(trimmed)
            || LooksLikeNonRoomDescriptor(trimmed)
            || LooksLikeFloorLevelText(trimmed)
            || LooksLikeEquipmentTagText(trimmed))
        {
            return false;
        }

        if (LooksLikeDimensionText(trimmed) && !HasKnownRoomUseTerm(trimmed))
        {
            return false;
        }

        return hasLayerHint
            ? LooksLikeRoomLabel(trimmed) || HasKnownRoomUseTerm(trimmed)
            : LooksLikeRoomSemanticLabel(trimmed);
    }

    private static bool IsAreaBackedCompactRoomCodeLabel(
        RoomTextLine candidate,
        RoomTextLine area,
        double maxVerticalGap,
        double maxHorizontalDrift)
    {
        var trimmed = candidate.Text.Trim();
        var verticalGap = area.Bounds.Top - candidate.Bounds.Bottom;
        if (verticalGap < -Math.Max(2, candidate.Bounds.Height * 0.35)
            || verticalGap > maxVerticalGap
            || Math.Abs(candidate.Bounds.Center.X - area.Bounds.Center.X) > maxHorizontalDrift)
        {
            return false;
        }

        if (!IsPotentialAreaBackedCompactRoomCodeText(trimmed)
            || LooksLikeDimensionText(trimmed)
            || LooksLikeFloorLevelText(trimmed)
            || LooksLikeNonRoomDescriptor(trimmed))
        {
            return false;
        }

        var prefixLength = trimmed.TakeWhile(char.IsLetter).Count();
        return prefixLength is 1 or 2
            && trimmed.Skip(prefixLength).Any(char.IsDigit);
    }

    private static bool IsPotentialAreaBackedCompactRoomCodeText(string text) =>
        AreaBackedCompactRoomCodeRegex.IsMatch(text.Trim());

    private static bool HasKnownRoomUseTerm(string text)
    {
        var normalized = NormalizeLabel(text);
        return RoomUseRules
            .SelectMany(rule => rule.Terms)
            .Any(term => ContainsTerm(normalized, NormalizeLabel(term)));
    }

    private static double HorizontalOverlapRatio(PlanRect first, PlanRect second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var minWidth = Math.Max(1, Math.Min(first.Width, second.Width));
        return overlap / minWidth;
    }

    private static double OverlapRatio(PlanRect first, PlanRect second)
    {
        var intersection = first.OverlapArea(second);
        var denominator = Math.Max(1, Math.Min(first.Area, second.Area));
        return intersection / denominator;
    }

    private static double MedianValue(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static bool ShouldSuppressSliverRoom(
        PlanRect bounds,
        IReadOnlyList<string> wallIds,
        RoomLabelMatch? label,
        RoomUseClassification useClassification,
        ScanContext context,
        out SuppressedRoomCandidate suppression)
    {
        var minorSpan = Math.Min(bounds.Width, bounds.Height);
        var majorSpan = Math.Max(bounds.Width, bounds.Height);
        var aspectRatio = AspectRatio(bounds);
        var sliverSpanThreshold = SliverRoomSpanThreshold(context.Options);
        var hasTrustedSemanticLabel = label is not null
            && useClassification.Kind != RoomUseKind.Unknown
            && LabelHasLetters(label.Text);

        var isOffsetFace = minorSpan <= sliverSpanThreshold
            && aspectRatio >= 2.75
            && majorSpan >= sliverSpanThreshold * 1.25;
        var isSevereThreadFace = minorSpan <= sliverSpanThreshold * 0.6
            && aspectRatio >= 2.0
            && majorSpan >= sliverSpanThreshold;

        if (!hasTrustedSemanticLabel && (isOffsetFace || isSevereThreadFace))
        {
            suppression = new SuppressedRoomCandidate(
                bounds,
                wallIds.ToArray(),
                label?.Text,
                Math.Round(minorSpan, 3),
                Math.Round(majorSpan, 3),
                Math.Round(aspectRatio, 3));
            return true;
        }

        suppression = default;
        return false;
    }

    private static double SliverRoomSpanThreshold(ScannerOptions options) =>
        Math.Max(options.MaxWallPairSeparation, options.DefaultWallThickness * 8.0);

    private static bool LabelHasLetters(string text) =>
        text.Any(char.IsLetter);

    private static void AddSliverRoomSuppressionDiagnostic(
        int pageNumber,
        IReadOnlyList<SuppressedRoomCandidate> suppressed,
        ScanContext context)
    {
        if (suppressed.Count == 0)
        {
            return;
        }

        var sourcePrimitiveIds = suppressed
            .SelectMany(candidate => candidate.WallIds)
            .Select(id => context.Walls.FirstOrDefault(wall => wall.Id == id))
            .Where(wall => wall is not null)
            .SelectMany(wall => wall!.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var exampleBounds = suppressed
            .Take(5)
            .Select(candidate => $"{Math.Round(candidate.Bounds.Left, 1)},{Math.Round(candidate.Bounds.Top, 1)} {Math.Round(candidate.Bounds.Width, 1)}x{Math.Round(candidate.Bounds.Height, 1)}")
            .ToArray();

        context.AddDiagnostic(
            "rooms.sliver_faces.suppressed",
            DiagnosticSeverity.Info,
            StageName,
            $"Suppressed {suppressed.Count} skinny room face(s) that look like wall/detail offsets rather than usable rooms.",
            pageNumber,
            PlanRect.Union(suppressed.Select(candidate => candidate.Bounds)),
            Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["suppressedRoomCandidateCount"] = suppressed.Count.ToString(),
                ["maxMinorSpan"] = suppressed.Max(candidate => candidate.MinorSpan).ToString("0.###", CultureInfo.InvariantCulture),
                ["maxMajorSpan"] = suppressed.Max(candidate => candidate.MajorSpan).ToString("0.###", CultureInfo.InvariantCulture),
                ["maxAspectRatio"] = suppressed.Max(candidate => candidate.AspectRatio).ToString("0.###", CultureInfo.InvariantCulture),
                ["examples"] = string.Join(";", exampleBounds)
            });
    }

    private static string? ResolveRoomSourceRegionId(
        int pageNumber,
        PlanRect bounds,
        IReadOnlyList<string> wallIds,
        ScanContext context)
    {
        var wallRegion = wallIds
            .Select(id => context.Walls.FirstOrDefault(wall => wall.Id == id)?.SourceRegionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(wallRegion))
        {
            return wallRegion;
        }

        return context.SheetRegions
            .Where(region => region.PageNumber == pageNumber && region.Kind == RegionKind.MainFloorPlan)
            .Where(region => region.Bounds.Contains(bounds.Center, context.Options.SheetMargin))
            .OrderBy(region => region.Bounds.Area)
            .Select(region => region.Id)
            .FirstOrDefault();
    }

    private static RoomUseClassification ClassifyRoomUse(string? label, PlanRect bounds)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var normalized = NormalizeLabel(label);
            foreach (var rule in RoomUseRules)
            {
                foreach (var term in rule.Terms)
                {
                    if (ContainsTerm(normalized, term))
                    {
                        return new RoomUseClassification(
                            rule.Kind,
                            new[] { $"room use kind {rule.Kind}", $"label evidence matched room-use term '{term}'" });
                    }
                }
            }
        }

        if (IsLongNarrow(bounds))
        {
            return new RoomUseClassification(
                RoomUseKind.Corridor,
                new[]
                {
                    "room use kind Corridor",
                    $"long narrow room geometry {Math.Round(AspectRatio(bounds), 2)}:1"
                });
        }

        return new RoomUseClassification(
            RoomUseKind.Unknown,
            new[] { "room use kind Unknown" });
    }

    private static bool ContainsTerm(string normalizedLabel, string normalizedTerm) =>
        normalizedLabel.Equals(normalizedTerm, StringComparison.Ordinal)
        || normalizedLabel.StartsWith($"{normalizedTerm} ", StringComparison.Ordinal)
        || normalizedLabel.EndsWith($" {normalizedTerm}", StringComparison.Ordinal)
        || normalizedLabel.Contains($" {normalizedTerm} ", StringComparison.Ordinal);

    private static bool IsLongNarrow(PlanRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return false;
        }

        var smallerSpan = Math.Min(bounds.Width, bounds.Height);
        var largerSpan = Math.Max(bounds.Width, bounds.Height);
        return smallerSpan > 0 && largerSpan / smallerSpan >= 4.0;
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

    private static void AddRoomUseDiagnostics(ScanContext context)
    {
        foreach (var pageGroup in context.Rooms.GroupBy(room => room.PageNumber))
        {
            var rooms = pageGroup.ToArray();
            if (rooms.Length == 0)
            {
                continue;
            }

            var kindCounts = rooms
                .GroupBy(room => room.UseKind)
                .OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
                .ToArray();
            var knownCount = rooms.Count(room => room.UseKind != RoomUseKind.Unknown);

            context.AddDiagnostic(
                "rooms.use_semantics.detected",
                DiagnosticSeverity.Info,
                StageName,
                $"Classified room-use hints for {knownCount} of {rooms.Length} room(s).",
                pageGroup.Key,
                PlanRect.Union(rooms.Select(room => room.Bounds)),
                knownCount > 0 ? Confidence.Medium : Confidence.Low,
                scope: DiagnosticScope.Room,
                sourcePrimitiveIds: rooms.SelectMany(room => room.LabelSourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["roomCount"] = rooms.Length.ToString(),
                    ["knownUseKindCount"] = knownCount.ToString(),
                    ["unknownUseKindCount"] = (rooms.Length - knownCount).ToString(),
                    ["useKinds"] = string.Join(",", kindCounts.Select(group => $"{group.Key}:{group.Count()}"))
                });
        }
    }

    private void AddRoomLimitDiagnostic(int pageNumber, int addedForPage, ScanContext context)
    {
        context.AddDiagnostic(
            "rooms.limit_reached",
            DiagnosticSeverity.Warning,
            Name,
            "Room candidate generation reached the configured per-page limit.",
            pageNumber,
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Page,
            properties: new Dictionary<string, string>
            {
                ["maxRoomCandidatesPerPage"] = context.Options.MaxRoomCandidatesPerPage.ToString(),
                ["addedForPage"] = addedForPage.ToString()
            });
    }

    private void AddNoRoomsDiagnostic(
        int pageNumber,
        IReadOnlyList<WallSegment> structuralWalls,
        ScanContext context)
    {
        var axisWalls = structuralWalls
            .Select(wall => AxisWall.TryCreate(wall, context.Options.GeometryTolerance.Distance))
            .Where(axis => axis is not null)
            .Select(axis => axis!)
            .ToArray();
        var horizontal = axisWalls.Count(axis => axis.Orientation == WallOrientation.Horizontal);
        var vertical = axisWalls.Count(axis => axis.Orientation == WallOrientation.Vertical);
        var hasRoomLikeWallSet = horizontal >= 2 && vertical >= 2;

        context.AddDiagnostic(
            hasRoomLikeWallSet ? "rooms.closed_loops.missing" : "rooms.none_detected",
            hasRoomLikeWallSet ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
            Name,
            hasRoomLikeWallSet
                ? "Axis-aligned walls were found, but no closed room loops could be formed."
                : "No closed axis-aligned wall loops were found.",
            pageNumber,
            confidence: Confidence.Low,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: structuralWalls.SelectMany(wall => wall.SourcePrimitiveIds),
            properties: new Dictionary<string, string>
            {
                ["wallCount"] = structuralWalls.Count.ToString(),
                ["axisWallCount"] = axisWalls.Length.ToString(),
                ["horizontalWallCount"] = horizontal.ToString(),
                ["verticalWallCount"] = vertical.ToString()
            });
    }

    private static IEnumerable<string> RoomEvidence(
        IReadOnlyList<string> wallIds,
        RoomLabelMatch? label,
        string solverEvidence,
        IReadOnlyList<PlanPoint> boundary)
    {
        yield return $"closed orthogonal cycle from {wallIds.Count} walls";
        yield return solverEvidence;
        yield return $"{boundary.Count} boundary vertices";

        if (label is not null)
        {
            yield return $"matched room label: {label.Text}";
        }
    }

    private static PlanPoint[] RectangleBoundary(double left, double top, double right, double bottom) =>
        new[]
        {
            new PlanPoint(left, top),
            new PlanPoint(right, top),
            new PlanPoint(right, bottom),
            new PlanPoint(left, bottom)
        };

    private static double PolygonArea(IReadOnlyList<PlanPoint> boundary)
    {
        if (boundary.Count < 3)
        {
            return 0;
        }

        var sum = 0.0;
        for (var index = 0; index < boundary.Count; index++)
        {
            var current = boundary[index];
            var next = boundary[(index + 1) % boundary.Count];
            sum += (current.X * next.Y) - (next.X * current.Y);
        }

        return Math.Abs(sum) / 2.0;
    }

    private static RoomLabelMatch? MatchRoomLabel(
        PlanPage page,
        PlanRect roomBounds,
        IReadOnlyList<PlanPoint> boundary,
        ScanContext context)
    {
        var center = roomBounds.Center;
        var annotationSourceIds = AnnotationSourcePrimitiveIds(context);
        var candidates = page.Primitives
            .Select((primitive, index) => new
            {
                Primitive = primitive,
                SourceId = context.PrimitiveId(page.Number, index, primitive)
            })
            .Where(item => item.Primitive is TextPrimitive text
                && !annotationSourceIds.Contains(item.SourceId)
                && RoomContains(roomBounds, boundary, text.Bounds.Center, context.Options.WallSnapTolerance)
                && LooksLikeRoomLabel(text.Text))
            .Select(item =>
            {
                var text = (TextPrimitive)item.Primitive;
                return new RoomLabelCandidate(
                    text.Text.Trim(),
                    item.SourceId,
                    text.Bounds,
                    text.Bounds.Center.DistanceTo(center),
                    HasRoomLayerHint(text));
            })
            .OrderByDescending(candidate => candidate.HasLayerHint)
            .ThenBy(candidate => candidate.DistanceToRoomCenter)
            .ThenBy(candidate => candidate.Bounds.Top)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        var selected = candidates
            .Take(candidates[0].HasLayerHint ? 3 : 2)
            .Where(candidate => candidate.DistanceToRoomCenter <= candidates[0].DistanceToRoomCenter + Math.Max(20, Math.Min(roomBounds.Width, roomBounds.Height) * 0.22))
            .OrderBy(candidate => candidate.Bounds.Top)
            .ThenBy(candidate => candidate.Bounds.Left)
            .ToArray();

        if (selected.Length == 0)
        {
            selected = new[] { candidates[0] };
        }

        return new RoomLabelMatch(
            string.Join(" ", selected.Select(candidate => candidate.Text)),
            selected.Select(candidate => candidate.SourcePrimitiveId).ToArray());
    }

    private static HashSet<string> AnnotationSourcePrimitiveIds(ScanContext context) =>
        context.Annotations
            .SelectMany(annotation => annotation.SourcePrimitiveIds
                .Concat(annotation.Items.SelectMany(item => item.SourcePrimitiveIds))
                .Concat(annotation.Items.SelectMany(item => item.References.SelectMany(reference => reference.SourcePrimitiveIds))))
            .ToHashSet(StringComparer.Ordinal);

    private static bool RoomContains(
        PlanRect bounds,
        IReadOnlyList<PlanPoint> boundary,
        PlanPoint point,
        double tolerance)
    {
        if (!bounds.Contains(point, tolerance))
        {
            return false;
        }

        return boundary.Count < 3 || PointInPolygon(point, boundary);
    }

    private static bool PointInPolygon(PlanPoint point, IReadOnlyList<PlanPoint> polygon)
    {
        var inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            var currentPoint = polygon[index];
            var previousPoint = polygon[previous];
            var intersects = ((currentPoint.Y > point.Y) != (previousPoint.Y > point.Y))
                && (point.X < ((previousPoint.X - currentPoint.X) * (point.Y - currentPoint.Y) / ((previousPoint.Y - currentPoint.Y) == 0 ? double.Epsilon : previousPoint.Y - currentPoint.Y)) + currentPoint.X);

            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool LooksLikeRoomLabel(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is < 1 or > 48)
        {
            return false;
        }

        if (!trimmed.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        if (trimmed.Length == 1)
        {
            return false;
        }

        if (trimmed.Length < 3 && trimmed.All(char.IsDigit))
        {
            return false;
        }

        if (LooksLikeNumericFragmentText(trimmed))
        {
            return false;
        }

        if (LooksLikeSingleLetterClusterText(trimmed))
        {
            return false;
        }

        if (LooksLikeMultiCodeTagText(trimmed))
        {
            return false;
        }

        if (LooksLikeDimensionText(trimmed))
        {
            return false;
        }

        if (LooksLikeFloorLevelText(trimmed))
        {
            return false;
        }

        if (LooksLikeCompactPlanCodeText(trimmed))
        {
            return false;
        }

        if (LooksLikeEquipmentTagText(trimmed))
        {
            return false;
        }

        if (LooksLikeNonRoomDescriptor(trimmed))
        {
            return false;
        }

        var upper = trimmed.ToUpperInvariant();
        return !upper.Contains("SCALE", StringComparison.Ordinal)
            && !upper.Contains("PROJECT", StringComparison.Ordinal)
            && !upper.Contains("GENERAL", StringComparison.Ordinal)
            && !upper.Contains("NOTES", StringComparison.Ordinal)
            && !upper.Contains("REV", StringComparison.Ordinal)
            && !upper.Contains("VERIFY", StringComparison.Ordinal);
    }

    private static bool LooksLikeNumericFragmentText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Any(char.IsDigit)
            && trimmed.Any(character => character is ',' or '.')
            && trimmed.All(character => char.IsDigit(character) || character is ',' or '.' || char.IsWhiteSpace(character));
    }

    private static bool LooksLikeSingleLetterClusterText(string text)
    {
        var tokens = NormalizeLabel(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length >= 2 && tokens.All(token => token.Length == 1 && token.All(char.IsLetter));
    }

    private static bool LooksLikeMultiCodeTagText(string text)
    {
        var tokens = NormalizeLabel(text).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is < 2 or > 4)
        {
            return false;
        }

        var compactCodeCount = tokens.Count(token =>
            token.Length is >= 2 and <= 4
            && token.Any(char.IsLetter)
            && token.Any(char.IsDigit)
            && token.All(char.IsLetterOrDigit));

        return compactCodeCount >= 2
            && tokens.All(token => token.Length <= 4 && token.All(char.IsLetterOrDigit));
    }

    private static bool LooksLikeNonRoomDescriptor(string text)
    {
        var normalized = NormalizeLabel(text);
        return NonRoomLabelTerms.Any(term => ContainsTerm(normalized, term));
    }

    private static bool LooksLikeDimensionText(string text)
    {
        var trimmed = text.Trim();
        var withoutUnit = trimmed.EndsWith("m", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^1]
            : string.Empty;
        if (withoutUnit.Length > 0
            && withoutUnit.Any(char.IsDigit)
            && withoutUnit.All(character => char.IsDigit(character) || character is '.' or ','))
        {
            return true;
        }

        return trimmed.Any(char.IsDigit)
            && (trimmed.Contains('\'')
                || trimmed.Contains('"')
                || trimmed.Contains("mm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("cm", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(" m", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(" m ", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('x')
                || trimmed.Contains('X'));
    }

    private static bool LooksLikeFloorLevelText(string text)
    {
        var parts = NormalizeLabel(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 2 and <= 3
            && parts.Any(part => part.All(char.IsDigit))
            && parts.Any(part => part is "etg" or "etasje");
    }

    private static bool LooksLikeCompactPlanCodeText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length is < 2 or > 3)
        {
            return false;
        }

        return char.IsLetter(trimmed[0])
            && trimmed.Skip(1).All(char.IsDigit);
    }

    private static bool LooksLikeEquipmentTagText(string text)
    {
        var upper = text.ToUpperInvariant();
        var tokens = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasHyphenatedNumber = tokens.Any(token => token.Contains('-') && token.Any(char.IsDigit));
        if (hasHyphenatedNumber)
        {
            if (upper.Contains("ROOM", StringComparison.Ordinal))
            {
                return false;
            }

            if (tokens.Length == 1 && tokens[0].StartsWith("MECH-", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        return tokens.Any(LooksLikeCompactEquipmentTag);
    }

    private static bool LooksLikeCompactEquipmentTag(string token)
    {
        var prefixLength = 0;
        while (prefixLength < token.Length && char.IsLetter(token[prefixLength]))
        {
            prefixLength++;
        }

        if (prefixLength <= 0 || prefixLength >= token.Length)
        {
            return false;
        }

        var prefix = token[..prefixLength];
        var suffix = token[prefixLength..];
        if (!CompactEquipmentTagPrefixes.Contains(prefix)
            || !suffix.All(char.IsLetterOrDigit)
            || !suffix.Any(char.IsDigit))
        {
            return false;
        }

        if (prefix.Length == 1 && suffix.Count(char.IsDigit) < 2)
        {
            return false;
        }

        return true;
    }

    private static bool HasRoomLayerHint(PlanPrimitive primitive)
    {
        var layer = primitive.Source.Layer ?? primitive.Layer;
        return !string.IsNullOrWhiteSpace(layer)
            && (layer.Contains("room", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("space", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("name", StringComparison.OrdinalIgnoreCase));
    }

    private static string RoomKey(IReadOnlyList<PlanPoint> boundary, double tolerance)
    {
        var bucket = Math.Max(0.1, tolerance);
        return string.Join(
            "|",
            boundary
                .Select(point => $"{Math.Round(point.X / bucket)}:{Math.Round(point.Y / bucket)}")
                .Order(StringComparer.Ordinal));
    }

    private enum WallOrientation
    {
        Horizontal,
        Vertical
    }

    private sealed record AxisWall(
        WallSegment Wall,
        WallOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public bool Covers(double start, double end, double tolerance) =>
            Start <= start + tolerance && End >= end - tolerance;

        public static AxisWall? TryCreate(WallSegment wall, double tolerance)
        {
            if (wall.CenterLine.IsHorizontal(tolerance))
            {
                return new AxisWall(
                    wall,
                    WallOrientation.Horizontal,
                    (wall.CenterLine.Start.Y + wall.CenterLine.End.Y) / 2.0,
                    Math.Min(wall.CenterLine.Start.X, wall.CenterLine.End.X),
                    Math.Max(wall.CenterLine.Start.X, wall.CenterLine.End.X));
            }

            if (wall.CenterLine.IsVertical(tolerance))
            {
                return new AxisWall(
                    wall,
                    WallOrientation.Vertical,
                    (wall.CenterLine.Start.X + wall.CenterLine.End.X) / 2.0,
                    Math.Min(wall.CenterLine.Start.Y, wall.CenterLine.End.Y),
                    Math.Max(wall.CenterLine.Start.Y, wall.CenterLine.End.Y));
            }

            return null;
        }
    }

    private sealed record AxisBoundaryCandidate(
        WallOrientation Orientation,
        double Coordinate,
        IReadOnlyList<AxisWall> Axes)
    {
        public static AxisBoundaryCandidate From(IReadOnlyList<AxisWall> axes) =>
            new(
                axes[0].Orientation,
                MedianValue(axes.Select(axis => axis.Coordinate)),
                axes.ToArray());

        public double Coverage(double start, double end, double tolerance)
        {
            var span = Math.Max(0, end - start);
            if (span <= 0)
            {
                return 0;
            }

            var intervals = Axes
                .Select(axis => (
                    Start: Math.Max(start, axis.Start - tolerance),
                    End: Math.Min(end, axis.End + tolerance)))
                .Where(item => item.End > item.Start)
                .OrderBy(item => item.Start)
                .ToArray();
            if (intervals.Length == 0)
            {
                return 0;
            }

            var covered = 0.0;
            var currentStart = intervals[0].Start;
            var currentEnd = intervals[0].End;
            foreach (var interval in intervals.Skip(1))
            {
                if (interval.Start <= currentEnd)
                {
                    currentEnd = Math.Max(currentEnd, interval.End);
                    continue;
                }

                covered += currentEnd - currentStart;
                currentStart = interval.Start;
                currentEnd = interval.End;
            }

            covered += currentEnd - currentStart;
            return Math.Min(1.0, covered / span);
        }

        public IEnumerable<string> WallIds(double start, double end, double tolerance) =>
            Walls(start, end, tolerance)
                .Select(wall => wall.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal);

        public IEnumerable<WallSegment> Walls(double start, double end, double tolerance) =>
            Axes
                .Where(axis => Math.Min(axis.End + tolerance, end) - Math.Max(axis.Start - tolerance, start) > 0)
                .Select(axis => axis.Wall);
    }

    private sealed record SemanticRoomBoundaryMatch(
        PlanRect Bounds,
        string[] WallIds,
        double Score,
        double AverageCoverage,
        int StrongSideCount,
        double AreaRatio,
        double TrustedWallRatio,
        int TrustedSideCount);

    private sealed class OrthogonalRoomGrid
    {
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly Dictionary<HorizontalBoundary, HashSet<string>> _horizontalWalls;
        private readonly Dictionary<VerticalBoundary, HashSet<string>> _verticalWalls;

        private OrthogonalRoomGrid(
            double[] x,
            double[] y,
            Dictionary<HorizontalBoundary, HashSet<string>> horizontalWalls,
            Dictionary<VerticalBoundary, HashSet<string>> verticalWalls,
            int graphEdgeCount)
        {
            _x = x;
            _y = y;
            _horizontalWalls = horizontalWalls;
            _verticalWalls = verticalWalls;
            GraphEdgeCount = graphEdgeCount;
        }

        public int CellCount => XCellCount * YCellCount;

        public int GraphEdgeCount { get; }

        private int XCellCount => _x.Length - 1;

        private int YCellCount => _y.Length - 1;

        public static OrthogonalRoomGrid? TryCreate(
            int pageNumber,
            ScanContext context,
            IReadOnlyList<WallSegment> structuralWalls)
        {
            var nodes = context.WallGraph.Nodes
                .Where(node => node.PageNumber == pageNumber)
                .ToDictionary(node => node.Id, StringComparer.Ordinal);
            var walls = structuralWalls
                .ToDictionary(wall => wall.Id, StringComparer.Ordinal);

            var edges = context.WallGraph.Edges
                .Where(edge => edge.PageNumber == pageNumber)
                .Select(edge => AxisGraphEdge.TryCreate(edge, nodes, walls, context.Options.GeometryTolerance.Distance))
                .Where(edge => edge is not null)
                .Select(edge => edge!)
                .ToArray();

            if (edges.Length < 4)
            {
                return null;
            }

            var baseX = ClusterCoordinates(
                edges.SelectMany(edge => edge.Orientation == WallOrientation.Horizontal
                    ? new[] { edge.Start, edge.End }
                    : new[] { edge.Coordinate }),
                context.Options.WallSnapTolerance);
            var baseY = ClusterCoordinates(
                edges.SelectMany(edge => edge.Orientation == WallOrientation.Horizontal
                    ? new[] { edge.Coordinate }
                    : new[] { edge.Start, edge.End }),
                context.Options.WallSnapTolerance);

            if (baseX.Length < 2 || baseY.Length < 2)
            {
                return null;
            }

            var margin = Math.Max(1, context.Options.WallSnapTolerance * 4);
            var x = new[] { baseX[0] - margin }
                .Concat(baseX)
                .Concat(new[] { baseX[^1] + margin })
                .ToArray();
            var y = new[] { baseY[0] - margin }
                .Concat(baseY)
                .Concat(new[] { baseY[^1] + margin })
                .ToArray();

            var horizontalWalls = new Dictionary<HorizontalBoundary, HashSet<string>>();
            var verticalWalls = new Dictionary<VerticalBoundary, HashSet<string>>();

            foreach (var edge in edges)
            {
                if (edge.Orientation == WallOrientation.Horizontal)
                {
                    var yIndex = FindCoordinateIndex(edge.Coordinate, baseY, context.Options.WallSnapTolerance) + 1;
                    var start = FindCoordinateIndex(edge.Start, baseX, context.Options.WallSnapTolerance) + 1;
                    var end = FindCoordinateIndex(edge.End, baseX, context.Options.WallSnapTolerance) + 1;
                    for (var index = Math.Min(start, end); index < Math.Max(start, end); index++)
                    {
                        AddWall(horizontalWalls, new HorizontalBoundary(yIndex, index), edge.Wall.Id);
                    }
                }
                else
                {
                    var xIndex = FindCoordinateIndex(edge.Coordinate, baseX, context.Options.WallSnapTolerance) + 1;
                    var start = FindCoordinateIndex(edge.Start, baseY, context.Options.WallSnapTolerance) + 1;
                    var end = FindCoordinateIndex(edge.End, baseY, context.Options.WallSnapTolerance) + 1;
                    for (var index = Math.Min(start, end); index < Math.Max(start, end); index++)
                    {
                        AddWall(verticalWalls, new VerticalBoundary(xIndex, index), edge.Wall.Id);
                    }
                }
            }

            return new OrthogonalRoomGrid(x, y, horizontalWalls, verticalWalls, edges.Length);
        }

        public IEnumerable<IReadOnlySet<Cell>> FindInteriorComponents(int maxComponents)
        {
            var outside = FloodOutside();
            var visited = new HashSet<Cell>();
            var components = 0;

            for (var yIndex = 0; yIndex < YCellCount; yIndex++)
            {
                for (var xIndex = 0; xIndex < XCellCount; xIndex++)
                {
                    var cell = new Cell(xIndex, yIndex);
                    if (outside[xIndex, yIndex] || !visited.Add(cell))
                    {
                        continue;
                    }

                    var component = CollectComponent(cell, outside, visited);
                    yield return component;
                    components++;
                    if (components >= maxComponents)
                    {
                        yield break;
                    }
                }
            }
        }

        public IEnumerable<string> BoundaryWallIds(IReadOnlySet<Cell> component)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var cell in component)
            {
                AddBoundaryWallIds(component, cell, result);
            }

            return result;
        }

        public IEnumerable<PlanPoint> TraceBoundary(IReadOnlySet<Cell> component)
        {
            var boundaryEdges = BoundaryEdges(component).Distinct().ToHashSet();
            var loops = new List<List<GridPoint>>();

            while (boundaryEdges.Count > 0)
            {
                var edge = boundaryEdges
                    .OrderBy(item => item.Start.YIndex)
                    .ThenBy(item => item.Start.XIndex)
                    .ThenBy(item => item.End.YIndex)
                    .ThenBy(item => item.End.XIndex)
                    .First();
                var start = edge.Start;
                var loop = new List<GridPoint> { start };
                var guard = 0;

                while (guard++ < 10_000)
                {
                    boundaryEdges.Remove(edge);
                    loop.Add(edge.End);

                    if (edge.End == start)
                    {
                        break;
                    }

                    var next = boundaryEdges.FirstOrDefault(candidate => candidate.Start == edge.End);
                    if (next == default)
                    {
                        break;
                    }

                    edge = next;
                }

                if (loop.Count > 3 && loop[^1] == loop[0])
                {
                    loop.RemoveAt(loop.Count - 1);
                }

                if (loop.Count >= 4)
                {
                    loops.Add(loop);
                }
            }

            var best = loops
                .Select(loop => SimplifyLoop(loop.Select(ToPlanPoint).ToArray()))
                .Where(loop => loop.Length >= 4)
                .OrderByDescending(PolygonArea)
                .FirstOrDefault();

            return best ?? Array.Empty<PlanPoint>();
        }

        private bool[,] FloodOutside()
        {
            var outside = new bool[XCellCount, YCellCount];
            var queue = new Queue<Cell>();

            for (var xIndex = 0; xIndex < XCellCount; xIndex++)
            {
                EnqueueOutside(new Cell(xIndex, 0));
                EnqueueOutside(new Cell(xIndex, YCellCount - 1));
            }

            for (var yIndex = 0; yIndex < YCellCount; yIndex++)
            {
                EnqueueOutside(new Cell(0, yIndex));
                EnqueueOutside(new Cell(XCellCount - 1, yIndex));
            }

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                foreach (var neighbor in Neighbors(cell))
                {
                    if (!IsInRange(neighbor) || outside[neighbor.XIndex, neighbor.YIndex] || IsBlocked(cell, neighbor))
                    {
                        continue;
                    }

                    EnqueueOutside(neighbor);
                }
            }

            return outside;

            void EnqueueOutside(Cell cell)
            {
                if (!IsInRange(cell) || outside[cell.XIndex, cell.YIndex])
                {
                    return;
                }

                outside[cell.XIndex, cell.YIndex] = true;
                queue.Enqueue(cell);
            }
        }

        private HashSet<Cell> CollectComponent(Cell start, bool[,] outside, HashSet<Cell> visited)
        {
            var component = new HashSet<Cell> { start };
            var queue = new Queue<Cell>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                foreach (var neighbor in Neighbors(cell))
                {
                    if (!IsInRange(neighbor)
                        || outside[neighbor.XIndex, neighbor.YIndex]
                        || IsBlocked(cell, neighbor)
                        || !visited.Add(neighbor))
                    {
                        continue;
                    }

                    component.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }

            return component;
        }

        private IEnumerable<BoundaryEdge> BoundaryEdges(IReadOnlySet<Cell> component)
        {
            foreach (var cell in component)
            {
                var top = new Cell(cell.XIndex, cell.YIndex - 1);
                if (!component.Contains(top))
                {
                    yield return new BoundaryEdge(
                        new GridPoint(cell.XIndex, cell.YIndex),
                        new GridPoint(cell.XIndex + 1, cell.YIndex));
                }

                var right = new Cell(cell.XIndex + 1, cell.YIndex);
                if (!component.Contains(right))
                {
                    yield return new BoundaryEdge(
                        new GridPoint(cell.XIndex + 1, cell.YIndex),
                        new GridPoint(cell.XIndex + 1, cell.YIndex + 1));
                }

                var bottom = new Cell(cell.XIndex, cell.YIndex + 1);
                if (!component.Contains(bottom))
                {
                    yield return new BoundaryEdge(
                        new GridPoint(cell.XIndex + 1, cell.YIndex + 1),
                        new GridPoint(cell.XIndex, cell.YIndex + 1));
                }

                var left = new Cell(cell.XIndex - 1, cell.YIndex);
                if (!component.Contains(left))
                {
                    yield return new BoundaryEdge(
                        new GridPoint(cell.XIndex, cell.YIndex + 1),
                        new GridPoint(cell.XIndex, cell.YIndex));
                }
            }
        }

        private void AddBoundaryWallIds(IReadOnlySet<Cell> component, Cell cell, HashSet<string> result)
        {
            if (!component.Contains(new Cell(cell.XIndex, cell.YIndex - 1))
                && _horizontalWalls.TryGetValue(new HorizontalBoundary(cell.YIndex, cell.XIndex), out var top))
            {
                result.UnionWith(top);
            }

            if (!component.Contains(new Cell(cell.XIndex, cell.YIndex + 1))
                && _horizontalWalls.TryGetValue(new HorizontalBoundary(cell.YIndex + 1, cell.XIndex), out var bottom))
            {
                result.UnionWith(bottom);
            }

            if (!component.Contains(new Cell(cell.XIndex - 1, cell.YIndex))
                && _verticalWalls.TryGetValue(new VerticalBoundary(cell.XIndex, cell.YIndex), out var left))
            {
                result.UnionWith(left);
            }

            if (!component.Contains(new Cell(cell.XIndex + 1, cell.YIndex))
                && _verticalWalls.TryGetValue(new VerticalBoundary(cell.XIndex + 1, cell.YIndex), out var right))
            {
                result.UnionWith(right);
            }
        }

        private static IEnumerable<Cell> Neighbors(Cell cell)
        {
            yield return new Cell(cell.XIndex + 1, cell.YIndex);
            yield return new Cell(cell.XIndex - 1, cell.YIndex);
            yield return new Cell(cell.XIndex, cell.YIndex + 1);
            yield return new Cell(cell.XIndex, cell.YIndex - 1);
        }

        private bool IsBlocked(Cell from, Cell to)
        {
            if (to.XIndex == from.XIndex + 1)
            {
                return _verticalWalls.ContainsKey(new VerticalBoundary(from.XIndex + 1, from.YIndex));
            }

            if (to.XIndex == from.XIndex - 1)
            {
                return _verticalWalls.ContainsKey(new VerticalBoundary(from.XIndex, from.YIndex));
            }

            if (to.YIndex == from.YIndex + 1)
            {
                return _horizontalWalls.ContainsKey(new HorizontalBoundary(from.YIndex + 1, from.XIndex));
            }

            if (to.YIndex == from.YIndex - 1)
            {
                return _horizontalWalls.ContainsKey(new HorizontalBoundary(from.YIndex, from.XIndex));
            }

            return true;
        }

        private bool IsInRange(Cell cell) =>
            cell.XIndex >= 0
            && cell.YIndex >= 0
            && cell.XIndex < XCellCount
            && cell.YIndex < YCellCount;

        private PlanPoint ToPlanPoint(GridPoint point) =>
            new(_x[point.XIndex], _y[point.YIndex]);

        private static PlanPoint[] SimplifyLoop(IReadOnlyList<PlanPoint> points)
        {
            var result = points.ToList();
            var changed = true;

            while (changed && result.Count >= 4)
            {
                changed = false;
                for (var index = 0; index < result.Count; index++)
                {
                    var previous = result[(index - 1 + result.Count) % result.Count];
                    var current = result[index];
                    var next = result[(index + 1) % result.Count];
                    var collinearX = Math.Abs(previous.X - current.X) <= 0.001 && Math.Abs(current.X - next.X) <= 0.001;
                    var collinearY = Math.Abs(previous.Y - current.Y) <= 0.001 && Math.Abs(current.Y - next.Y) <= 0.001;

                    if (!collinearX && !collinearY)
                    {
                        continue;
                    }

                    result.RemoveAt(index);
                    changed = true;
                    break;
                }
            }

            return result.ToArray();
        }

        private static double[] ClusterCoordinates(IEnumerable<double> values, double tolerance)
        {
            var sorted = values.Order().ToArray();
            if (sorted.Length == 0)
            {
                return Array.Empty<double>();
            }

            var result = new List<double>();
            var cluster = new List<double> { sorted[0] };

            for (var index = 1; index < sorted.Length; index++)
            {
                if (Math.Abs(sorted[index] - cluster.Average()) <= tolerance)
                {
                    cluster.Add(sorted[index]);
                    continue;
                }

                result.Add(Median(cluster));
                cluster = new List<double> { sorted[index] };
            }

            result.Add(Median(cluster));
            return result.ToArray();
        }

        private static double Median(IReadOnlyList<double> values)
        {
            var sorted = values.Order().ToArray();
            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) / 2.0;
        }

        private static int FindCoordinateIndex(double value, IReadOnlyList<double> coordinates, double tolerance)
        {
            var bestIndex = 0;
            var bestDistance = double.MaxValue;
            for (var index = 0; index < coordinates.Count; index++)
            {
                var distance = Math.Abs(coordinates[index] - value);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }

            if (bestDistance > Math.Max(0.001, tolerance * 2))
            {
                throw new InvalidOperationException("Graph room coordinate could not be mapped back to the orthogonal grid.");
            }

            return bestIndex;
        }

        private static void AddWall<TKey>(
            Dictionary<TKey, HashSet<string>> index,
            TKey key,
            string wallId)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out var wallIds))
            {
                wallIds = new HashSet<string>(StringComparer.Ordinal);
                index[key] = wallIds;
            }

            wallIds.Add(wallId);
        }
    }

    private sealed record AxisGraphEdge(
        WallEdge Edge,
        WallSegment Wall,
        WallOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public static AxisGraphEdge? TryCreate(
            WallEdge edge,
            IReadOnlyDictionary<string, WallNode> nodes,
            IReadOnlyDictionary<string, WallSegment> walls,
            double tolerance)
        {
            if (!nodes.TryGetValue(edge.FromNodeId, out var from)
                || !nodes.TryGetValue(edge.ToNodeId, out var to)
                || !walls.TryGetValue(edge.WallId, out var wall))
            {
                return null;
            }

            var segment = new PlanLineSegment(from.Position, to.Position);
            if (segment.Length <= tolerance)
            {
                return null;
            }

            if (segment.IsHorizontal(tolerance))
            {
                return new AxisGraphEdge(
                    edge,
                    wall,
                    WallOrientation.Horizontal,
                    (from.Position.Y + to.Position.Y) / 2.0,
                    Math.Min(from.Position.X, to.Position.X),
                    Math.Max(from.Position.X, to.Position.X));
            }

            if (segment.IsVertical(tolerance))
            {
                return new AxisGraphEdge(
                    edge,
                    wall,
                    WallOrientation.Vertical,
                    (from.Position.X + to.Position.X) / 2.0,
                    Math.Min(from.Position.Y, to.Position.Y),
                    Math.Max(from.Position.Y, to.Position.Y));
            }

            if (wall.CenterLine.IsHorizontal(tolerance))
            {
                var start = ProjectAlongHorizontalWall(wall.CenterLine, from.Position);
                var end = ProjectAlongHorizontalWall(wall.CenterLine, to.Position);
                if (Math.Abs(end - start) <= tolerance)
                {
                    return null;
                }

                return new AxisGraphEdge(
                    edge,
                    wall,
                    WallOrientation.Horizontal,
                    wall.CenterLine.Midpoint.Y,
                    Math.Min(start, end),
                    Math.Max(start, end));
            }

            if (wall.CenterLine.IsVertical(tolerance))
            {
                var start = ProjectAlongVerticalWall(wall.CenterLine, from.Position);
                var end = ProjectAlongVerticalWall(wall.CenterLine, to.Position);
                if (Math.Abs(end - start) <= tolerance)
                {
                    return null;
                }

                return new AxisGraphEdge(
                    edge,
                    wall,
                    WallOrientation.Vertical,
                    wall.CenterLine.Midpoint.X,
                    Math.Min(start, end),
                    Math.Max(start, end));
            }

            return null;
        }

        private static double ProjectAlongHorizontalWall(PlanLineSegment wallLine, PlanPoint point)
        {
            var t = Math.Clamp(wallLine.ProjectParameter(point), 0, 1);
            return wallLine.PointAt(t).X;
        }

        private static double ProjectAlongVerticalWall(PlanLineSegment wallLine, PlanPoint point)
        {
            var t = Math.Clamp(wallLine.ProjectParameter(point), 0, 1);
            return wallLine.PointAt(t).Y;
        }

    }

    private readonly record struct Cell(int XIndex, int YIndex);

    private readonly record struct GridPoint(int XIndex, int YIndex);

    private readonly record struct BoundaryEdge(GridPoint Start, GridPoint End);

    private readonly record struct HorizontalBoundary(int YIndex, int XIntervalIndex);

    private readonly record struct VerticalBoundary(int XIndex, int YIntervalIndex);

    private sealed record RoomLabelCandidate(
        string Text,
        string SourcePrimitiveId,
        PlanRect Bounds,
        double DistanceToRoomCenter,
        bool HasLayerHint);

    private sealed record RoomLabelMatch(
        string Text,
        IReadOnlyList<string> SourcePrimitiveIds);

    private sealed record RoomTextWord(
        string Text,
        string SourcePrimitiveId,
        PlanRect Bounds,
        bool HasLayerHint);

    private sealed record RoomTextLine(
        string Text,
        PlanRect Bounds,
        IReadOnlyList<string> SourcePrimitiveIds,
        bool HasLayerHint);

    private sealed record SemanticRoomCandidate(
        RoomTextLine Label,
        RoomTextLine Area,
        PlanRect EvidenceBounds,
        PlanPoint AnchorPoint,
        double AreaSquareMeters,
        IReadOnlyList<string> SourcePrimitiveIds);

    private sealed record RoomUseClassification(
        RoomUseKind Kind,
        IReadOnlyList<string> Evidence);

    private readonly record struct SuppressedRoomCandidate(
        PlanRect Bounds,
        IReadOnlyList<string> WallIds,
        string? Label,
        double MinorSpan,
        double MajorSpan,
        double AspectRatio);

    private sealed record RoomUseRule(
        RoomUseKind Kind,
        IReadOnlyList<string> Terms);

    private static readonly RoomUseRule[] RoomUseRules =
    {
        new(RoomUseKind.Corridor, new[] { "corridor", "corr", "hallway", "passage", "passasje", "korridor", "gang" }),
        new(RoomUseKind.Outdoor, new[] { "outdoor", "outside", "terrace", "terrasse", "balcony", "balkong", "veranda", "patio", "porch", "covered", "covered entry", "covered entrance", "overbygd", "overbygget", "overbygd inngang", "overbygget inngang", "canopy", "carport", "uteplass" }),
        new(RoomUseKind.Lobby, new[] { "lobby", "reception", "vestibule", "entrance", "foyer", "inngang" }),
        new(RoomUseKind.Restroom, new[] { "wc", "toilet", "restroom", "washroom", "lavatory", "urinal" }),
        new(RoomUseKind.Bathroom, new[] { "bath", "bathroom", "bad", "shower", "dusj" }),
        new(RoomUseKind.Office, new[] { "office", "kontor", "workspace", "workplace" }),
        new(RoomUseKind.Meeting, new[] { "meeting", "conference", "moterom", "møterom", "boardroom" }),
        new(RoomUseKind.Storage, new[] { "storage", "store", "lager", "closet", "archive", "arkiv", "gard", "garderobe", "wardrobe" }),
        new(RoomUseKind.Mechanical, new[] { "mechanical", "mech", "pump", "boiler", "plant", "machine", "maskin", "teknisk" }),
        new(RoomUseKind.Electrical, new[] { "electrical", "elec", "el", "switchgear", "transformer", "server", "data" }),
        new(RoomUseKind.Plumbing, new[] { "plumbing", "vvs", "water", "sprinkler" }),
        new(RoomUseKind.HVAC, new[] { "hvac", "ventilation", "vent", "ahu", "fan room" }),
        new(RoomUseKind.Utility, new[] { "utility", "service", "renhold", "janitor", "cleaner" }),
        new(RoomUseKind.Kitchen, new[] { "kitchen", "kitchenette", "kjokken", "kjøkken", "pantry" }),
        new(RoomUseKind.Living, new[] { "living", "stue", "lounge", "family room" }),
        new(RoomUseKind.Bedroom, new[] { "bedroom", "soverom", "sov", "sleeping" }),
        new(RoomUseKind.Stair, new[] { "stair", "stairs", "staircase", "trapp", "trapperom" }),
        new(RoomUseKind.Elevator, new[] { "elevator", "lift", "heis" }),
        new(RoomUseKind.Shaft, new[] { "shaft", "sjakt", "riser" }),
        new(RoomUseKind.Laboratory, new[] { "lab", "laboratory", "laboratorium" }),
        new(RoomUseKind.Industrial, new[] { "production", "process", "workshop", "verksted", "industrial" }),
        new(RoomUseKind.Parking, new[] { "parking", "garage", "garasje" }),
        new(RoomUseKind.CommonArea, new[] { "common", "amenity", "break", "cafeteria", "kantine" })
    };
}
