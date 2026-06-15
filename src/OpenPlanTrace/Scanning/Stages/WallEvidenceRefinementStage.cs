using System.Globalization;

namespace OpenPlanTrace;

internal sealed class WallEvidenceRefinementStage : IPipelineStage
{
    private const string StageName = "wall-evidence";

    public string Name => StageName;

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        context.Walls.Clear();
        if (context.WallCandidates.Count == 0)
        {
            context.WallEvidenceMap = WallEvidenceMap.Empty;
            return ValueTask.CompletedTask;
        }

        var originalWallCount = context.WallCandidates.Count;
        var recoveredWalls = context.Options.EnableWallEvidenceRecovery
            ? RecoverMissingWallBands(context, cancellationToken)
            : Array.Empty<WallSegment>();
        var candidateWalls = context.WallCandidates.Concat(recoveredWalls).ToArray();

        var assessments = new List<WallEvidenceWallAssessment>();
        var segments = new List<WallEvidenceSegment>();
        var bands = new List<WallEvidenceBand>();
        var rejectedWallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wall in candidateWalls)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var assessment = AssessWall(wall, context);
            assessments.Add(assessment);
            segments.Add(new WallEvidenceSegment(
                $"wall-evidence-segment:{wall.Id}",
                wall.PageNumber,
                wall.CenterLine,
                wall.Bounds,
                assessment.Category,
                assessment.Confidence,
                wall.Id,
                wall.SourcePrimitiveIds,
                assessment.Evidence));

            if (wall.PairEvidence is not null)
            {
                bands.Add(CreateBand(wall, assessment));
            }

            if (context.Options.EnableWallEvidenceNoiseRejection && assessment.RejectedAsNoise)
            {
                rejectedWallIds.Add(wall.Id);
            }
        }

        context.Walls.AddRange(candidateWalls.Where(wall => !rejectedWallIds.Contains(wall.Id)));

        for (var index = 0; index < context.Walls.Count; index++)
        {
            var wall = context.Walls[index];
            var assessment = assessments.FirstOrDefault(item => string.Equals(item.WallId, wall.Id, StringComparison.Ordinal));
            if (assessment is null || assessment.RejectedAsNoise)
            {
                continue;
            }

            context.Walls[index] = wall with
            {
                Evidence = AppendEvidence(wall.Evidence, new[] { EvidenceSummary(assessment) })
            };
        }

        context.WallEvidenceMap = new WallEvidenceMap(
            segments.ToArray(),
            bands.ToArray(),
            assessments.ToArray());

        AddDiagnostics(context, originalWallCount, recoveredWalls, rejectedWallIds, assessments, bands);
        return ValueTask.CompletedTask;
    }

    private static WallEvidenceBand CreateBand(
        WallSegment wall,
        WallEvidenceWallAssessment assessment)
    {
        var pair = wall.PairEvidence!;
        return new WallEvidenceBand(
            $"wall-evidence-band:{wall.Id}",
            wall.PageNumber,
            pair.FirstFaceLine,
            pair.SecondFaceLine,
            wall.CenterLine,
            pair.FaceSeparation,
            pair.OverlapRatio,
            assessment.Confidence,
            wall.Id,
            wall.SourcePrimitiveIds,
            assessment.Evidence);
    }

    private static WallEvidenceWallAssessment AssessWall(WallSegment wall, ScanContext context)
    {
        var evidence = new List<string>();
        var category = WallEvidenceCategory.Unknown;
        var confidence = wall.Confidence;
        var placementReady = false;
        var requiresReview = false;
        var rejected = false;

        if (wall.Evidence.Any(item => item.Contains("recovered by wall evidence map", StringComparison.OrdinalIgnoreCase)))
        {
            category = WallEvidenceCategory.RecoveredWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.68));
            placementReady = confidence.Value >= 0.72;
            requiresReview = !placementReady;
            evidence.Add("wall evidence: recovered wall body from unclaimed parallel-face evidence");
        }
        else if (IsStrongPairedWall(wall))
        {
            category = WallEvidenceCategory.StrongWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.78));
            placementReady = true;
            evidence.Add("wall evidence: strong double-edge wall body");
        }
        else if (TryClassifySurfacePatternNoise(wall, context, out var surfaceEvidence))
        {
            category = WallEvidenceCategory.SurfacePatternDetail;
            confidence = new Confidence(Math.Min(0.95, Math.Max(wall.Confidence.Value, 0.72)));
            rejected = true;
            requiresReview = true;
            evidence.Add(surfaceEvidence);
        }
        else if (TryClassifyDoorOrOpeningSymbolNoise(wall, context, out var doorEvidence))
        {
            category = WallEvidenceCategory.DoorOrOpeningSymbol;
            confidence = new Confidence(Math.Min(0.95, Math.Max(wall.Confidence.Value, 0.70)));
            rejected = true;
            requiresReview = true;
            evidence.Add(doorEvidence);
        }
        else if (TryClassifyDimensionOrAnnotationNoise(wall, context, out var dimensionEvidence))
        {
            category = WallEvidenceCategory.DimensionOrAnnotation;
            confidence = new Confidence(Math.Min(0.92, Math.Max(wall.Confidence.Value, 0.68)));
            rejected = true;
            requiresReview = true;
            evidence.Add(dimensionEvidence);
        }
        else if (IsMediumWallBody(wall, context))
        {
            category = WallEvidenceCategory.MediumWallBody;
            confidence = new Confidence(Math.Max(wall.Confidence.Value, 0.62));
            placementReady = wall.FragmentEvidence?.RequiresGeometryReview != true;
            requiresReview = !placementReady;
            evidence.Add("wall evidence: medium wall body from wall-like layer, length, or structural context");
        }
        else
        {
            category = WallEvidenceCategory.WeakSingleLine;
            confidence = new Confidence(Math.Min(wall.Confidence.Value, 0.58));
            requiresReview = true;
            evidence.Add("wall evidence: weak single-line wall candidate; keep for topology but review before exact placement");
        }

        if (wall.FragmentEvidence?.RequiresGeometryReview == true)
        {
            placementReady = false;
            requiresReview = true;
            evidence.Add("wall evidence: fragment-merged geometry requires review before exact placement");
        }

        return new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            category,
            confidence,
            placementReady,
            requiresReview,
            rejected,
            wall.SourcePrimitiveIds,
            AppendEvidence(wall.Evidence, evidence));
    }

    private static bool IsStrongPairedWall(WallSegment wall) =>
        wall.DetectionKind == WallDetectionKind.ParallelLinePair
        && wall.PairEvidence is { Score: >= 0.62, OverlapRatio: >= 0.55 };

    private static bool IsMediumWallBody(WallSegment wall, ScanContext context)
    {
        if (IsWallLayerBacked(wall, context))
        {
            return true;
        }

        if (wall.DetectionKind == WallDetectionKind.FragmentMerged
            && wall.FragmentEvidence?.RequiresGeometryReview != true
            && wall.DrawingLength >= context.Options.MinWallLength * 1.35)
        {
            return true;
        }

        return HasStructuralEndpointSupport(wall.CenterLine, wall.PageNumber, context.WallCandidates, context.Options);
    }

    private static bool TryClassifySurfacePatternNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair
            || context.SurfacePatterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in context.SurfacePatterns.Where(pattern => pattern.PageNumber == wall.PageNumber))
        {
            var sharesSource = wall.SourcePrimitiveIds.Any(pattern.SourcePrimitiveIds.Contains);
            if (!sharesSource)
            {
                continue;
            }

            if (!pattern.ExcludedFromWallDetection && !pattern.ExcludedFromStructuralTopology)
            {
                continue;
            }

            evidence = sharesSource
                ? $"wall evidence: rejected as surface/detail pattern because it shares source primitives with {pattern.Id}"
                : $"wall evidence: rejected as surface/detail pattern because it overlaps {pattern.Id}";
            return true;
        }

        return false;
    }

    private static bool TryClassifyDoorOrOpeningSymbolNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        var page = context.Document.Pages.FirstOrDefault(page => page.Number == wall.PageNumber);
        if (page is null)
        {
            return false;
        }

        var layerCategories = SourceLayerCategories(wall, context).ToArray();
        var doorLayerBacked = layerCategories.Any(category => category is LayerCategory.Door or LayerCategory.Window);
        var arcSupport = NearbyDoorArcSupport(wall, page, context.Options);
        var lengthLimit = Math.Max(context.Options.MaxOpeningGap * 1.7, context.Options.MinWallLength * 2.0);

        if ((doorLayerBacked || arcSupport.Score >= 0.68)
            && wall.DrawingLength <= lengthLimit
            && !HasOpeningFragmentCompanion(wall, context.WallCandidates, context.Options)
            && !HasStructuralEndpointSupport(wall.CenterLine, wall.PageNumber, context.WallCandidates, context.Options))
        {
            evidence = doorLayerBacked && arcSupport.Score > 0
                ? $"wall evidence: rejected as door/opening symbol linework from door/window layer and nearby swing arc {arcSupport.ArcSourceId}"
                : doorLayerBacked
                    ? "wall evidence: rejected as short door/window layer linework"
                    : $"wall evidence: rejected as door/opening symbol linework near swing arc {arcSupport.ArcSourceId}";
            return true;
        }

        return false;
    }

    private static bool HasOpeningFragmentCompanion(
        WallSegment wall,
        IReadOnlyList<WallSegment> candidates,
        ScannerOptions options)
    {
        if (!wall.CenterLine.IsHorizontal() && !wall.CenterLine.IsVertical())
        {
            return false;
        }

        foreach (var other in candidates.Where(other => !string.Equals(other.Id, wall.Id, StringComparison.Ordinal)))
        {
            if (other.PageNumber != wall.PageNumber
                || !AreNearParallel(wall.CenterLine, other.CenterLine)
                || !AreNearCollinear(wall.CenterLine, other.CenterLine, Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness)))
            {
                continue;
            }

            var gap = CollinearGap(wall.CenterLine, other.CenterLine);
            if (gap >= options.MinOpeningGap && gap <= options.MaxOpeningGap * 1.25)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryClassifyDimensionOrAnnotationNoise(
        WallSegment wall,
        ScanContext context,
        out string evidence)
    {
        evidence = string.Empty;
        if (wall.DetectionKind == WallDetectionKind.ParallelLinePair)
        {
            return false;
        }

        var categories = SourceLayerCategories(wall, context).ToArray();
        if (categories.Any(category => category is LayerCategory.Dimension or LayerCategory.Text or LayerCategory.Grid)
            && !IsWallLayerBacked(wall, context))
        {
            evidence = "wall evidence: rejected as dimension, text, or grid layer linework";
            return true;
        }

        return false;
    }

    private static IReadOnlyList<WallSegment> RecoverMissingWallBands(
        ScanContext context,
        CancellationToken cancellationToken)
    {
        var recovered = new List<WallSegment>();
        var surfaceSourceIds = context.SurfacePatterns
            .Where(pattern => pattern.ExcludedFromWallDetection || pattern.ExcludedFromStructuralTopology)
            .SelectMany(pattern => pattern.SourcePrimitiveIds)
            .ToHashSet(StringComparer.Ordinal);
        var usedSourceIds = context.WallCandidates
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .ToHashSet(StringComparer.Ordinal);
        var existingWalls = context.WallCandidates.ToArray();

        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mainRegion = context.SheetRegions
                .Where(region => region.PageNumber == page.Number && region.Kind == RegionKind.MainFloorPlan)
                .OrderByDescending(region => region.Bounds.Area)
                .FirstOrDefault();
            var allowedBounds = mainRegion?.Bounds ?? page.Bounds;
            var lineCandidates = PageLineCandidates(page, allowedBounds, surfaceSourceIds, usedSourceIds, context)
                .ToArray();
            var pageRecovered = RecoverAxisPairsForPage(
                    page.Number,
                    mainRegion?.Id,
                    lineCandidates,
                    existingWalls.Concat(recovered).Where(wall => wall.PageNumber == page.Number).ToArray(),
                    context)
                .Take(context.Options.MaxWallEvidenceRecoveredWallsPerPage)
                .ToArray();
            recovered.AddRange(pageRecovered);
        }

        return recovered;
    }

    private static IEnumerable<PrimitiveLineCandidate> PageLineCandidates(
        PlanPage page,
        PlanRect allowedBounds,
        IReadOnlySet<string> surfaceSourceIds,
        IReadOnlySet<string> usedSourceIds,
        ScanContext context)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not LinePrimitive line)
            {
                continue;
            }

            var sourceId = context.PrimitiveId(page.Number, index, line);
            if (usedSourceIds.Contains(sourceId)
                || surfaceSourceIds.Contains(sourceId)
                || line.Segment.Length < Math.Max(context.Options.MinWallLength * 1.15, 20)
                || !allowedBounds.Intersects(line.Segment.Bounds.Inflate(context.Options.WallSnapTolerance)))
            {
                continue;
            }

            var category = LayerCategoryFor(line.Layer ?? line.Source.Layer, context);
            if (category is LayerCategory.Dimension
                or LayerCategory.Text
                or LayerCategory.Grid
                or LayerCategory.Door
                or LayerCategory.Window
                or LayerCategory.Equipment
                or LayerCategory.Electrical
                or LayerCategory.HVAC
                or LayerCategory.Plumbing
                or LayerCategory.FireSafety)
            {
                continue;
            }

            var orientation = ResolveAxisOrientation(line.Segment);
            if (orientation == WallOrientation.Unknown)
            {
                continue;
            }

            yield return new PrimitiveLineCandidate(
                sourceId,
                page.Number,
                line.Segment,
                orientation,
                category,
                line.Layer ?? line.Source.Layer);
        }
    }

    private static IReadOnlyList<WallSegment> RecoverAxisPairsForPage(
        int pageNumber,
        string? sourceRegionId,
        IReadOnlyList<PrimitiveLineCandidate> candidates,
        IReadOnlyList<WallSegment> existingWalls,
        ScanContext context)
    {
        var recovered = new List<WallSegment>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var pairCandidates = new List<RecoveredPairCandidate>();

        foreach (var group in candidates.GroupBy(candidate => candidate.Orientation))
        {
            var lines = group.OrderBy(candidate => candidate.Coordinate).ToArray();
            for (var leftIndex = 0; leftIndex < lines.Length; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < lines.Length; rightIndex++)
                {
                    var first = lines[leftIndex];
                    var second = lines[rightIndex];
                    var separation = Math.Abs(first.Coordinate - second.Coordinate);
                    if (separation < context.Options.MinWallPairSeparation
                        || separation > context.Options.MaxWallPairSeparation)
                    {
                        continue;
                    }

                    var overlap = AxisOverlap(first, second);
                    if (overlap.Length < Math.Max(context.Options.MinWallLength * 1.2, 26))
                    {
                        continue;
                    }

                    var overlapRatio = overlap.Length / Math.Max(1, Math.Min(first.Length, second.Length));
                    if (overlapRatio < Math.Max(context.Options.MinWallPairOverlapRatio, 0.62))
                    {
                        continue;
                    }

                    var centerLine = CenterLine(first, second, overlap.Start, overlap.End);
                    if (IsRepresentedByExistingWall(centerLine, existingWalls, context.Options))
                    {
                        continue;
                    }

                    var wallLayerBacked = IsWallLikeCategory(first.LayerCategory) || IsWallLikeCategory(second.LayerCategory);
                    var hasStructuralSupport = HasStructuralEndpointSupport(centerLine, pageNumber, existingWalls, context.Options);
                    if (!wallLayerBacked && !hasStructuralSupport)
                    {
                        continue;
                    }

                    var score = Math.Clamp(
                        0.44
                        + (overlapRatio * 0.22)
                        + (wallLayerBacked ? 0.18 : 0)
                        + (hasStructuralSupport ? 0.12 : 0)
                        - (Math.Abs(separation - context.Options.DefaultWallThickness) / Math.Max(context.Options.MaxWallPairSeparation, 1) * 0.08),
                        0,
                        0.93);
                    if (score < 0.62)
                    {
                        continue;
                    }

                    pairCandidates.Add(new RecoveredPairCandidate(first, second, centerLine, separation, overlapRatio, score));
                }
            }
        }

        foreach (var pair in pairCandidates
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.CenterLine.Length))
        {
            if (consumed.Contains(pair.First.SourceId) || consumed.Contains(pair.Second.SourceId))
            {
                continue;
            }

            consumed.Add(pair.First.SourceId);
            consumed.Add(pair.Second.SourceId);
            recovered.Add(CreateRecoveredWall(pageNumber, sourceRegionId, pair, context, recovered.Count + 1));
        }

        return recovered;
    }

    private static WallSegment CreateRecoveredWall(
        int pageNumber,
        string? sourceRegionId,
        RecoveredPairCandidate pair,
        ScanContext context,
        int sequence)
    {
        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(pageNumber, pair.CenterLine.Bounds, sourceRegionId);
        var wall = new WallSegment(
            $"page:{pageNumber}:wall-evidence-recovered:{sequence:000}",
            pageNumber,
            pair.CenterLine,
            pair.FaceSeparation,
            new Confidence(pair.Score))
        {
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourceRegionId = sourceRegionId,
            SourcePrimitiveIds = new[] { pair.First.SourceId, pair.Second.SourceId },
            PairEvidence = new WallPairEvidence(
                pair.First.Segment,
                pair.Second.Segment,
                Math.Round(pair.FaceSeparation, 3),
                Math.Round(pair.OverlapRatio, 3),
                Math.Round(pair.Score, 3),
                1,
                1,
                new[] { pair.First.SourceId },
                new[] { pair.Second.SourceId }),
            Evidence = new[]
            {
                "recovered by wall evidence map from unclaimed parallel wall-face evidence",
                $"pair score {Math.Round(pair.Score, 3).ToString(CultureInfo.InvariantCulture)}",
                $"overlap ratio {Math.Round(pair.OverlapRatio, 3).ToString(CultureInfo.InvariantCulture)}"
            },
            LengthMeters = context.Calibration.ToMeters(pair.CenterLine.Length, scaleGroup),
            ThicknessMillimeters = context.Calibration.ToMillimeters(pair.FaceSeparation, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };

        return wall;
    }

    private static bool IsRepresentedByExistingWall(
        PlanLineSegment centerLine,
        IReadOnlyList<WallSegment> existingWalls,
        ScannerOptions options)
    {
        foreach (var wall in existingWalls)
        {
            if (!AreNearParallel(centerLine, wall.CenterLine))
            {
                continue;
            }

            var distance = Math.Max(
                centerLine.DistanceToPoint(wall.CenterLine.Midpoint),
                wall.CenterLine.DistanceToPoint(centerLine.Midpoint));
            if (distance > Math.Max(options.WallSnapTolerance * 2.0, options.DefaultWallThickness * 1.5))
            {
                continue;
            }

            var overlapRatio = AxisAlignedOverlapRatio(centerLine, wall.CenterLine);
            if (overlapRatio >= 0.45)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasStructuralEndpointSupport(
        PlanLineSegment line,
        int pageNumber,
        IReadOnlyList<WallSegment> walls,
        ScannerOptions options)
    {
        var tolerance = Math.Max(options.WallSnapTolerance * 3.0, options.DefaultWallThickness * 2.5);
        var supportedEndpoints = 0;
        foreach (var endpoint in new[] { line.Start, line.End })
        {
            if (walls
                .Where(wall => wall.PageNumber == pageNumber)
                .Where(wall => wall.PairEvidence is not null || wall.DrawingLength >= options.MinWallLength * 1.4)
                .Any(wall => !SameLine(line, wall.CenterLine, options.WallSnapTolerance)
                    && wall.CenterLine.DistanceToPoint(endpoint) <= tolerance))
            {
                supportedEndpoints++;
            }
        }

        return supportedEndpoints > 0;
    }

    private static NearbyArcSupport NearbyDoorArcSupport(
        WallSegment wall,
        PlanPage page,
        ScannerOptions options)
    {
        var searchBounds = wall.Bounds.Inflate(Math.Max(options.MaxOpeningGap * 0.75, options.WallSnapTolerance * 8.0));
        var best = NearbyArcSupport.Empty;

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not ArcPrimitive arc
                || !arc.Bounds.Intersects(searchBounds)
                || arc.Radius < options.MinOpeningGap * 0.35
                || arc.Radius > options.MaxOpeningGap * 1.35)
            {
                continue;
            }

            var midpointDistanceToArc = Math.Abs(wall.CenterLine.Midpoint.DistanceTo(arc.Center) - arc.Radius);
            var endpointNearCenter = Math.Min(
                wall.CenterLine.Start.DistanceTo(arc.Center),
                wall.CenterLine.End.DistanceTo(arc.Center));
            var score = 0.0;
            if (midpointDistanceToArc <= Math.Max(options.WallSnapTolerance * 3.0, 5.0))
            {
                score += 0.42;
            }

            if (endpointNearCenter <= Math.Max(options.WallSnapTolerance * 4.0, 8.0))
            {
                score += 0.26;
            }

            if (wall.CenterLine.Length <= arc.Radius * 1.35)
            {
                score += 0.18;
            }

            if (Math.Abs(arc.SweepAngleRadians) >= Math.PI * 0.35)
            {
                score += 0.12;
            }

            if (score > best.Score)
            {
                best = new NearbyArcSupport(
                    Math.Min(score, 0.98),
                    page.Primitives[index].SourceId ?? page.Primitives[index].Source.SourceId ?? $"p{page.Number}:primitive:{index}",
                    arc.Bounds);
            }
        }

        return best;
    }

    private static IEnumerable<LayerCategory> SourceLayerCategories(WallSegment wall, ScanContext context)
    {
        var sourceIds = wall.SourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
        foreach (var page in context.Document.Pages.Where(page => page.Number == wall.PageNumber))
        {
            for (var index = 0; index < page.Primitives.Count; index++)
            {
                var primitive = page.Primitives[index];
                var sourceId = context.PrimitiveId(page.Number, index, primitive);
                if (sourceIds.Contains(sourceId))
                {
                    yield return LayerCategoryFor(primitive.Layer ?? primitive.Source.Layer, context);
                }
            }
        }
    }

    private static bool IsWallLayerBacked(WallSegment wall, ScanContext context) =>
        SourceLayerCategories(wall, context).Any(IsWallLikeCategory);

    private static bool IsWallLikeCategory(LayerCategory category) =>
        category is LayerCategory.Wall or LayerCategory.Structural;

    private static LayerCategory LayerCategoryFor(string? layerName, ScanContext context)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return LayerCategory.Unknown;
        }

        return context.LayerAnalysis.Layers
            .FirstOrDefault(layer => string.Equals(layer.Name, layerName, StringComparison.OrdinalIgnoreCase))
            ?.LikelyCategory
            ?? LayerCategory.Unknown;
    }

    private static WallOrientation ResolveAxisOrientation(PlanLineSegment segment)
    {
        if (segment.IsHorizontal())
        {
            return WallOrientation.Horizontal;
        }

        if (segment.IsVertical())
        {
            return WallOrientation.Vertical;
        }

        return WallOrientation.Unknown;
    }

    private static AxisOverlapResult AxisOverlap(PrimitiveLineCandidate first, PrimitiveLineCandidate second)
    {
        var start = Math.Max(first.MinAlong, second.MinAlong);
        var end = Math.Min(first.MaxAlong, second.MaxAlong);
        return new AxisOverlapResult(start, end, Math.Max(0, end - start));
    }

    private static PlanLineSegment CenterLine(
        PrimitiveLineCandidate first,
        PrimitiveLineCandidate second,
        double start,
        double end)
    {
        var coordinate = (first.Coordinate + second.Coordinate) / 2.0;
        return first.Orientation == WallOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, coordinate), new PlanPoint(end, coordinate))
            : new PlanLineSegment(new PlanPoint(coordinate, start), new PlanPoint(coordinate, end));
    }

    private static double AxisAlignedOverlapRatio(PlanLineSegment first, PlanLineSegment second)
    {
        if (first.IsHorizontal() && second.IsHorizontal())
        {
            return OverlapRatio(first.Start.X, first.End.X, second.Start.X, second.End.X);
        }

        if (first.IsVertical() && second.IsVertical())
        {
            return OverlapRatio(first.Start.Y, first.End.Y, second.Start.Y, second.End.Y);
        }

        return 0;
    }

    private static double OverlapRatio(double firstA, double firstB, double secondA, double secondB)
    {
        var firstMin = Math.Min(firstA, firstB);
        var firstMax = Math.Max(firstA, firstB);
        var secondMin = Math.Min(secondA, secondB);
        var secondMax = Math.Max(secondA, secondB);
        var overlap = Math.Max(0, Math.Min(firstMax, secondMax) - Math.Max(firstMin, secondMin));
        return overlap / Math.Max(1, Math.Min(firstMax - firstMin, secondMax - secondMin));
    }

    private static bool AreNearParallel(PlanLineSegment first, PlanLineSegment second)
    {
        var delta = Math.Abs(
            GeometryOperations.NormalizeAngleRadians(first.AngleRadians)
            - GeometryOperations.NormalizeAngleRadians(second.AngleRadians));
        delta = Math.Min(delta, Math.PI - delta);
        return delta <= 0.08;
    }

    private static bool AreNearCollinear(PlanLineSegment first, PlanLineSegment second, double tolerance) =>
        first.IsHorizontal() && second.IsHorizontal() && Math.Abs(first.Midpoint.Y - second.Midpoint.Y) <= tolerance
        || first.IsVertical() && second.IsVertical() && Math.Abs(first.Midpoint.X - second.Midpoint.X) <= tolerance
        || !first.IsHorizontal() && !first.IsVertical()
        && !second.IsHorizontal() && !second.IsVertical()
        && (
        first.DistanceToPoint(second.Start) <= tolerance
        || first.DistanceToPoint(second.End) <= tolerance
        || second.DistanceToPoint(first.Start) <= tolerance
        || second.DistanceToPoint(first.End) <= tolerance);

    private static double CollinearGap(PlanLineSegment first, PlanLineSegment second)
    {
        if (first.IsHorizontal() && second.IsHorizontal())
        {
            return IntervalGap(first.Start.X, first.End.X, second.Start.X, second.End.X);
        }

        if (first.IsVertical() && second.IsVertical())
        {
            return IntervalGap(first.Start.Y, first.End.Y, second.Start.Y, second.End.Y);
        }

        return Math.Min(
            Math.Min(first.Start.DistanceTo(second.Start), first.Start.DistanceTo(second.End)),
            Math.Min(first.End.DistanceTo(second.Start), first.End.DistanceTo(second.End)));
    }

    private static double IntervalGap(double firstA, double firstB, double secondA, double secondB)
    {
        var firstMin = Math.Min(firstA, firstB);
        var firstMax = Math.Max(firstA, firstB);
        var secondMin = Math.Min(secondA, secondB);
        var secondMax = Math.Max(secondA, secondB);
        if (firstMax < secondMin)
        {
            return secondMin - firstMax;
        }

        if (secondMax < firstMin)
        {
            return firstMin - secondMax;
        }

        return 0;
    }

    private static bool SameLine(PlanLineSegment first, PlanLineSegment second, double tolerance) =>
        first.Start.DistanceTo(second.Start) <= tolerance && first.End.DistanceTo(second.End) <= tolerance
        || first.Start.DistanceTo(second.End) <= tolerance && first.End.DistanceTo(second.Start) <= tolerance;

    private static IReadOnlyList<string> AppendEvidence(
        IReadOnlyList<string> existing,
        IEnumerable<string> additions) =>
        existing
            .Concat(additions)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string EvidenceSummary(WallEvidenceWallAssessment assessment)
    {
        var status = assessment.RejectedAsNoise
            ? "rejected"
            : assessment.PlacementReady
                ? "placement-ready"
                : "review";
        return $"wall evidence assessment: {assessment.Category} / {status} / confidence {assessment.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture)}";
    }

    private static void AddDiagnostics(
        ScanContext context,
        int originalWallCount,
        IReadOnlyList<WallSegment> recoveredWalls,
        IReadOnlySet<string> rejectedWallIds,
        IReadOnlyList<WallEvidenceWallAssessment> assessments,
        IReadOnlyList<WallEvidenceBand> bands)
    {
        context.AddDiagnostic(
            "wall_evidence.map_built",
            DiagnosticSeverity.Info,
            StageName,
            "Built wall evidence map and refined wall candidates before topology graphing.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Document,
            properties: new Dictionary<string, string>
            {
                ["inputWallCount"] = originalWallCount.ToString(CultureInfo.InvariantCulture),
                ["outputWallCount"] = context.Walls.Count.ToString(CultureInfo.InvariantCulture),
                ["wallAssessmentCount"] = assessments.Count.ToString(CultureInfo.InvariantCulture),
                ["wallBandCount"] = bands.Count.ToString(CultureInfo.InvariantCulture),
                ["placementReadyWallCount"] = assessments.Count(item => item.PlacementReady).ToString(CultureInfo.InvariantCulture),
                ["reviewWallCount"] = assessments.Count(item => item.RequiresReview && !item.RejectedAsNoise).ToString(CultureInfo.InvariantCulture),
                ["recoveredWallCount"] = recoveredWalls.Count.ToString(CultureInfo.InvariantCulture),
                ["rejectedNoiseWallCount"] = rejectedWallIds.Count.ToString(CultureInfo.InvariantCulture),
                ["strongWallBodyCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.StrongWallBody).ToString(CultureInfo.InvariantCulture),
                ["mediumWallBodyCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.MediumWallBody).ToString(CultureInfo.InvariantCulture),
                ["weakSingleLineCount"] = assessments.Count(item => item.Category == WallEvidenceCategory.WeakSingleLine).ToString(CultureInfo.InvariantCulture)
            });

        if (recoveredWalls.Count > 0)
        {
            context.AddDiagnostic(
                "wall_evidence.missing_wall_bands_recovered",
                DiagnosticSeverity.Info,
                StageName,
                $"{recoveredWalls.Count} missing double-edge wall band(s) were recovered from source primitive evidence.",
                region: PlanRect.Union(recoveredWalls.Select(wall => wall.Bounds)),
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: recoveredWalls.SelectMany(wall => wall.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["recoveredWallCount"] = recoveredWalls.Count.ToString(CultureInfo.InvariantCulture),
                    ["maxRecoveredWallsPerPage"] = context.Options.MaxWallEvidenceRecoveredWallsPerPage.ToString(CultureInfo.InvariantCulture)
                });
        }

        if (rejectedWallIds.Count > 0)
        {
            var rejectedAssessments = assessments
                .Where(assessment => rejectedWallIds.Contains(assessment.WallId))
                .ToArray();
            context.AddDiagnostic(
                "wall_evidence.noise_walls_rejected",
                DiagnosticSeverity.Info,
                StageName,
                $"{rejectedWallIds.Count} wall candidate(s) were rejected by explicit non-wall evidence before graphing.",
                region: PlanRect.Union(rejectedAssessments.Select(item => item.Bounds)),
                confidence: Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: rejectedAssessments.SelectMany(item => item.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["rejectedWallCount"] = rejectedWallIds.Count.ToString(CultureInfo.InvariantCulture),
                    ["surfacePatternRejectedCount"] = rejectedAssessments.Count(item => item.Category == WallEvidenceCategory.SurfacePatternDetail).ToString(CultureInfo.InvariantCulture),
                    ["doorSymbolRejectedCount"] = rejectedAssessments.Count(item => item.Category == WallEvidenceCategory.DoorOrOpeningSymbol).ToString(CultureInfo.InvariantCulture),
                    ["dimensionRejectedCount"] = rejectedAssessments.Count(item => item.Category == WallEvidenceCategory.DimensionOrAnnotation).ToString(CultureInfo.InvariantCulture)
                });
        }
    }

    private readonly record struct PrimitiveLineCandidate(
        string SourceId,
        int PageNumber,
        PlanLineSegment Segment,
        WallOrientation Orientation,
        LayerCategory LayerCategory,
        string? LayerName)
    {
        public double Length => Segment.Length;

        public double Coordinate => Orientation == WallOrientation.Horizontal
            ? (Segment.Start.Y + Segment.End.Y) / 2.0
            : (Segment.Start.X + Segment.End.X) / 2.0;

        public double MinAlong => Orientation == WallOrientation.Horizontal
            ? Math.Min(Segment.Start.X, Segment.End.X)
            : Math.Min(Segment.Start.Y, Segment.End.Y);

        public double MaxAlong => Orientation == WallOrientation.Horizontal
            ? Math.Max(Segment.Start.X, Segment.End.X)
            : Math.Max(Segment.Start.Y, Segment.End.Y);
    }

    private readonly record struct AxisOverlapResult(double Start, double End, double Length);

    private readonly record struct RecoveredPairCandidate(
        PrimitiveLineCandidate First,
        PrimitiveLineCandidate Second,
        PlanLineSegment CenterLine,
        double FaceSeparation,
        double OverlapRatio,
        double Score);

    private readonly record struct NearbyArcSupport(double Score, string? ArcSourceId, PlanRect Bounds)
    {
        public static NearbyArcSupport Empty { get; } = new(0, null, PlanRect.Empty);
    }

    private enum WallOrientation
    {
        Unknown,
        Horizontal,
        Vertical
    }
}
