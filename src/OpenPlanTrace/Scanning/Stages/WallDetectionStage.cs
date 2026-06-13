namespace OpenPlanTrace;

internal sealed class WallDetectionStage : IPipelineStage
{
    public string Name => "walls";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mainRegion = context.SheetRegions.FirstOrDefault(
                region => region.PageNumber == page.Number && region.Kind == RegionKind.MainFloorPlan);

            if (mainRegion is null)
            {
                context.AddDiagnostic(
                    "walls.main_region.missing",
                    DiagnosticSeverity.Warning,
                    Name,
                    "Wall detection skipped because no main floorplan region was available.",
                    page.Number,
                    scope: DiagnosticScope.Page,
                    properties: new Dictionary<string, string>
                    {
                        ["primitiveCount"] = page.Primitives.Count.ToString()
                    });
                continue;
            }

            var minFragmentLength = Math.Min(context.Options.MinWallLength, Math.Max(0.1, context.Options.MinWallFragmentLength));
            var candidates = PrimitiveGeometry
                .EnumerateLines(page, context)
                .Where(line => line.Segment.Length >= minFragmentLength)
                .Where(line => mainRegion.Bounds.Intersects(line.Segment.Bounds.Inflate(context.Options.WallSnapTolerance)))
                .ToArray();
            var gridAxisSourceIds = context.GridAxes
                .Where(axis => axis.PageNumber == page.Number)
                .SelectMany(axis => axis.SourcePrimitiveIds)
                .ToHashSet(StringComparer.Ordinal);
            var classifiedCandidates = candidates
                .Select(line => WallLineCandidate.From(line, context, gridAxisSourceIds))
                .ToArray();
            var layerFilteredCandidates = classifiedCandidates
                .Where(candidate => !candidate.UseForWallDetection)
                .ToArray();
            var compactLineworkClusters = DetectCompactObjectLineworkWallNoise(classifiedCandidates, mainRegion.Bounds, context.Options);
            var compactLineworkSourceIds = compactLineworkClusters
                .SelectMany(cluster => cluster.SourcePrimitiveIds)
                .ToHashSet(StringComparer.Ordinal);
            var densePatternClusters = DetectDenseOrthogonalPatternWallNoise(classifiedCandidates, mainRegion.Bounds, context.Options);
            var densePatternSourceIds = densePatternClusters
                .SelectMany(cluster => cluster.SourcePrimitiveIds)
                .ToHashSet(StringComparer.Ordinal);
            var wallCandidates = classifiedCandidates
                .Select(candidate => compactLineworkSourceIds.Contains(candidate.PrimitiveId)
                    ? candidate with
                    {
                        UseForWallDetection = false,
                        LayerEvidence = candidate.LayerEvidence
                            .Append("suppressed from wall detection as compact object-like linework")
                            .ToArray()
                    }
                    : densePatternSourceIds.Contains(candidate.PrimitiveId)
                        ? candidate with
                        {
                            UseForWallDetection = false,
                            LayerEvidence = candidate.LayerEvidence
                                .Append("suppressed from wall detection as dense repeated orthogonal pattern")
                                .ToArray()
                        }
                    : candidate)
                .ToArray();
            var filteredCandidates = wallCandidates
                .Where(candidate => !candidate.UseForWallDetection)
                .ToArray();
            var uncappedSeeds = wallCandidates
                .Where(candidate => candidate.UseForWallDetection)
                .Select(candidate => WallSeed.From(candidate, context.Options))
                .ToArray();
            var seeds = LimitWallSeeds(uncappedSeeds, context, page.Number, mainRegion.Bounds, mainRegion.Id);

            var wallStartCount = context.Walls.Count;

            var horizontalRuns = MergeAxisRuns(seeds.Where(seed => seed.Orientation == WallOrientation.Horizontal), context.Options).ToArray();
            var verticalRuns = MergeAxisRuns(seeds.Where(seed => seed.Orientation == WallOrientation.Vertical), context.Options).ToArray();
            var nonAxisRuns = MergeNonAxisRuns(seeds.Where(seed => seed.Orientation == WallOrientation.Other), context.Options).ToArray();
            var densePatternRunClusters = DetectDenseOrthogonalPatternWallRunNoise(
                horizontalRuns.Concat(verticalRuns).ToArray(),
                mainRegion.Bounds,
                context.Options);
            var denseParallelRunClusters = DetectDenseParallelPatternWallRunNoise(
                horizontalRuns.Concat(verticalRuns).ToArray(),
                mainRegion.Bounds,
                context.Options);
            var densePatternRuns = densePatternRunClusters
                .SelectMany(cluster => cluster.Runs)
                .Concat(denseParallelRunClusters.SelectMany(cluster => cluster.Runs))
                .ToHashSet();
            if (densePatternRuns.Count > 0)
            {
                horizontalRuns = horizontalRuns.Where(run => !densePatternRuns.Contains(run)).ToArray();
                verticalRuns = verticalRuns.Where(run => !densePatternRuns.Contains(run)).ToArray();
            }

            var surfacePatternCandidates = CreateSurfacePatternCandidates(
                page.Number,
                mainRegion.Id,
                densePatternClusters,
                densePatternRunClusters,
                denseParallelRunClusters,
                context.Options,
                context.SurfacePatterns.Count);
            context.SurfacePatterns.AddRange(surfacePatternCandidates);

            var axisFragmentNoiseRuns = context.Options.FilterDenseFragmentLineworkFromWalls
                ? horizontalRuns
                    .Concat(verticalRuns)
                    .Where(run => IsLikelyFragmentNoiseRun(run, context.Options))
                    .ToHashSet()
                : new HashSet<AxisRun>();
            var nonAxisFragmentNoiseRuns = context.Options.FilterDenseFragmentLineworkFromWalls
                ? nonAxisRuns
                    .Where(run => IsLikelyFragmentNoiseRun(run, context.Options))
                    .ToHashSet()
                : new HashSet<NonAxisRun>();
            if (axisFragmentNoiseRuns.Count > 0 || nonAxisFragmentNoiseRuns.Count > 0)
            {
                horizontalRuns = horizontalRuns.Where(run => !axisFragmentNoiseRuns.Contains(run)).ToArray();
                verticalRuns = verticalRuns.Where(run => !axisFragmentNoiseRuns.Contains(run)).ToArray();
                nonAxisRuns = nonAxisRuns.Where(run => !nonAxisFragmentNoiseRuns.Contains(run)).ToArray();
            }

            var mergedAxisFragmentRuns = horizontalRuns
                .Concat(verticalRuns)
                .Where(run => run.FragmentCount > 1)
                .ToArray();
            var mergedNonAxisFragmentRuns = nonAxisRuns
                .Where(run => run.FragmentCount > 1)
                .ToArray();
            var duplicateAxisRuns = horizontalRuns
                .Concat(verticalRuns)
                .Where(run => run.DuplicatePrimitiveCount > 0)
                .ToArray();
            var duplicateNonAxisRuns = nonAxisRuns
                .Where(run => run.DuplicatePrimitiveCount > 0)
                .ToArray();

            var axisPairCount =
                AddAxisWalls(page.Number, mainRegion.Id, horizontalRuns, context)
                + AddAxisWalls(page.Number, mainRegion.Id, verticalRuns, context);
            var nonAxisPairCount = AddNonAxisWalls(page.Number, mainRegion.Id, nonAxisRuns, context);
            var reconstructedPairs = axisPairCount + nonAxisPairCount;

            if (layerFilteredCandidates.Length > 0)
            {
                var filteredCategories = layerFilteredCandidates
                    .GroupBy(candidate => candidate.LayerCategory)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key)
                    .ToArray();
                context.AddDiagnostic(
                    "walls.layer_filtered_candidates",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{layerFilteredCandidates.Length} line candidates were skipped by layer-aware wall filtering.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Medium,
                    scope: DiagnosticScope.Layer,
                    sourcePrimitiveIds: layerFilteredCandidates.Select(candidate => candidate.PrimitiveId).Distinct(StringComparer.Ordinal),
                    properties: new Dictionary<string, string>
                    {
                        ["filteredLineCount"] = layerFilteredCandidates.Length.ToString(),
                        ["eligibleLineCount"] = seeds.Length.ToString(),
                        ["eligibleLineCountBeforeLimit"] = uncappedSeeds.Length.ToString(),
                        ["categories"] = string.Join(",", filteredCategories.Select(group => $"{group.Key}:{group.Count()}")),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            if (compactLineworkClusters.Count > 0)
            {
                context.AddDiagnostic(
                    "walls.compact_linework_filtered",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{compactLineworkSourceIds.Count} compact object-like line primitive(s) were kept out of wall detection.",
                    page.Number,
                    PlanRect.Union(compactLineworkClusters.Select(cluster => cluster.Bounds)).ClampTo(page.Bounds),
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: compactLineworkSourceIds,
                    properties: new Dictionary<string, string>
                    {
                        ["clusterCount"] = compactLineworkClusters.Count.ToString(),
                        ["filteredLineCount"] = compactLineworkSourceIds.Count.ToString(),
                        ["eligibleLineCount"] = seeds.Length.ToString(),
                        ["eligibleLineCountBeforeLimit"] = uncappedSeeds.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            if (densePatternClusters.Count > 0 || densePatternRunClusters.Count > 0 || denseParallelRunClusters.Count > 0)
            {
                var densePatternFilteredSourceIds = densePatternSourceIds
                    .Concat(densePatternRunClusters.SelectMany(cluster => cluster.SourcePrimitiveIds))
                    .Concat(denseParallelRunClusters.SelectMany(cluster => cluster.SourcePrimitiveIds))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var densePatternBounds = densePatternClusters
                    .Select(cluster => cluster.Bounds)
                    .Concat(densePatternRunClusters.Select(cluster => cluster.Bounds))
                    .Concat(denseParallelRunClusters.Select(cluster => cluster.Bounds))
                    .ToArray();
                context.AddDiagnostic(
                    "walls.dense_orthogonal_pattern_filtered",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{densePatternFilteredSourceIds.Length} dense repeated surface/detail pattern line primitive(s) were kept out of wall detection.",
                    page.Number,
                    PlanRect.Union(densePatternBounds).ClampTo(page.Bounds),
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: densePatternFilteredSourceIds,
                    properties: new Dictionary<string, string>
                    {
                        ["clusterCount"] = (densePatternClusters.Count + densePatternRunClusters.Count + denseParallelRunClusters.Count).ToString(),
                        ["candidateClusterCount"] = densePatternClusters.Count.ToString(),
                        ["runClusterCount"] = densePatternRunClusters.Count.ToString(),
                        ["parallelClusterCount"] = denseParallelRunClusters.Count.ToString(),
                        ["filteredLineCount"] = densePatternFilteredSourceIds.Length.ToString(),
                        ["filteredRunCount"] = densePatternRuns.Count.ToString(),
                        ["eligibleLineCount"] = seeds.Length.ToString(),
                        ["eligibleLineCountBeforeLimit"] = uncappedSeeds.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id,
                        ["patterns"] = string.Join(";", densePatternClusters
                            .Select(cluster => $"{cluster.HorizontalLineCount}h/{cluster.VerticalLineCount}v/{cluster.IntersectionCount}x")
                            .Concat(densePatternRunClusters
                                .Select(cluster => $"{cluster.HorizontalLineCount}h/{cluster.VerticalLineCount}v/{cluster.IntersectionCount}x"))
                            .Concat(denseParallelRunClusters
                                .Select(cluster => $"{cluster.Orientation}:{cluster.LineCount} lines/{cluster.MedianSpacing:0.###} spacing")))
                    });
            }

            if (axisFragmentNoiseRuns.Count > 0 || nonAxisFragmentNoiseRuns.Count > 0)
            {
                var filteredFragmentNoiseSourceIds = axisFragmentNoiseRuns
                    .SelectMany(run => run.SourcePrimitiveIds)
                    .Concat(nonAxisFragmentNoiseRuns.SelectMany(run => run.SourcePrimitiveIds))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                context.AddDiagnostic(
                    "walls.fragment_noise_filtered",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{axisFragmentNoiseRuns.Count + nonAxisFragmentNoiseRuns.Count} short dense fragment run(s) were kept out of wall detection.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: filteredFragmentNoiseSourceIds,
                    properties: new Dictionary<string, string>
                    {
                        ["filteredRunCount"] = (axisFragmentNoiseRuns.Count + nonAxisFragmentNoiseRuns.Count).ToString(),
                        ["axisRunCount"] = axisFragmentNoiseRuns.Count.ToString(),
                        ["nonOrthogonalRunCount"] = nonAxisFragmentNoiseRuns.Count.ToString(),
                        ["filteredSourcePrimitiveIdCount"] = filteredFragmentNoiseSourceIds.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            var duplicateRunCount = duplicateAxisRuns.Length + duplicateNonAxisRuns.Length;
            if (duplicateRunCount > 0)
            {
                var duplicatePrimitiveCount =
                    duplicateAxisRuns.Sum(run => run.DuplicatePrimitiveCount)
                    + duplicateNonAxisRuns.Sum(run => run.DuplicatePrimitiveCount);
                context.AddDiagnostic(
                    "walls.duplicates.collapsed",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{duplicatePrimitiveCount} duplicate or near-duplicate wall line primitive(s) were collapsed into {duplicateRunCount} wall run(s).",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: duplicateAxisRuns.SelectMany(run => run.SourcePrimitiveIds)
                        .Concat(duplicateNonAxisRuns.SelectMany(run => run.SourcePrimitiveIds)),
                    properties: new Dictionary<string, string>
                    {
                        ["duplicatePrimitiveCount"] = duplicatePrimitiveCount.ToString(),
                        ["duplicateRunCount"] = duplicateRunCount.ToString(),
                        ["axisDuplicateRunCount"] = duplicateAxisRuns.Length.ToString(),
                        ["nonOrthogonalDuplicateRunCount"] = duplicateNonAxisRuns.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            var mergedFragmentRunCount = mergedAxisFragmentRuns.Length + mergedNonAxisFragmentRuns.Length;
            if (mergedFragmentRunCount > 0)
            {
                var healedRuns =
                    mergedAxisFragmentRuns.Count(run => run.TotalGapLength > context.Options.WallMergeTolerance)
                    + mergedNonAxisFragmentRuns.Count(run => run.TotalGapLength > context.Options.WallMergeTolerance);
                context.AddDiagnostic(
                    "walls.fragments.merged",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{mergedFragmentRunCount} fragmented wall runs were merged before wall reconstruction.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: mergedAxisFragmentRuns.SelectMany(run => run.SourcePrimitiveIds)
                        .Concat(mergedNonAxisFragmentRuns.SelectMany(run => run.SourcePrimitiveIds)),
                    properties: new Dictionary<string, string>
                    {
                        ["mergedRunCount"] = mergedFragmentRunCount.ToString(),
                        ["axisMergedRunCount"] = mergedAxisFragmentRuns.Length.ToString(),
                        ["nonOrthogonalMergedRunCount"] = mergedNonAxisFragmentRuns.Length.ToString(),
                        ["gapHealedRunCount"] = healedRuns.ToString(),
                        ["shortSeedCount"] = seeds.Count(seed => seed.Segment.Length < context.Options.MinWallLength).ToString(),
                        ["maxWallFragmentGap"] = context.Options.MaxWallFragmentGap.ToString("0.###"),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            if (reconstructedPairs > 0)
            {
                var reconstructedWalls = context.Walls
                    .Skip(wallStartCount)
                    .Where(wall => wall.DetectionKind == WallDetectionKind.ParallelLinePair)
                    .ToArray();
                var reconstructedSourceIds = reconstructedWalls
                    .SelectMany(wall => wall.SourcePrimitiveIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                context.AddDiagnostic(
                    "walls.parallel_pairs.reconstructed",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{reconstructedPairs} double-line wall pairs were reconstructed into centerline walls.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Medium,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: reconstructedSourceIds,
                    properties: new Dictionary<string, string>
                    {
                        ["reconstructedPairCount"] = reconstructedPairs.ToString(),
                        ["axisPairCount"] = axisPairCount.ToString(),
                        ["nonOrthogonalPairCount"] = nonAxisPairCount.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });

                AddWallPairThicknessVarianceDiagnostic(
                    page.Number,
                    mainRegion,
                    reconstructedWalls,
                    context);
            }

            var added = context.Walls.Count - wallStartCount;
            var lowConfidenceWalls = context.Walls
                .Skip(wallStartCount)
                .Where(wall => wall.Confidence.Value < 0.55)
                .ToArray();

            if (lowConfidenceWalls.Length > 0)
            {
                context.AddDiagnostic(
                    "walls.low_confidence_candidates",
                    DiagnosticSeverity.Info,
                    Name,
                    $"{lowConfidenceWalls.Length} wall candidates have low confidence and should be reviewed.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Low,
                    scope: DiagnosticScope.Detection,
                    sourcePrimitiveIds: lowConfidenceWalls.SelectMany(wall => wall.SourcePrimitiveIds),
                    properties: new Dictionary<string, string>
                    {
                        ["candidateCount"] = lowConfidenceWalls.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }

            if (added == 0)
            {
                context.AddDiagnostic(
                    "walls.none_detected",
                    DiagnosticSeverity.Info,
                    Name,
                    "No wall-length vector runs were found in the main floorplan region.",
                    page.Number,
                    mainRegion.Bounds,
                    Confidence.Low,
                    scope: DiagnosticScope.Region,
                    properties: new Dictionary<string, string>
                    {
                        ["seedCount"] = seeds.Length.ToString(),
                        ["seedCountBeforeLimit"] = uncappedSeeds.Length.ToString(),
                        ["filteredLineCount"] = filteredCandidates.Length.ToString(),
                        ["sourceRegionId"] = mainRegion.Id
                    });
            }
        }

        return ValueTask.CompletedTask;
    }

    private WallSeed[] LimitWallSeeds(
        IReadOnlyList<WallSeed> seeds,
        ScanContext context,
        int pageNumber,
        PlanRect mainRegionBounds,
        string sourceRegionId)
    {
        var limit = context.Options.MaxWallCandidateSeedsPerPage;
        if (limit <= 0 || seeds.Count <= limit)
        {
            return seeds.ToArray();
        }

        var indexed = seeds
            .Select((seed, index) => new IndexedWallSeed(seed, index))
            .ToArray();
        var kept = indexed
            .OrderByDescending(item => SeedPriority(item.Seed))
            .ThenByDescending(item => item.Seed.Segment.Length)
            .ThenBy(item => item.Seed.PrimitiveId, StringComparer.Ordinal)
            .ThenBy(item => item.Index)
            .Take(limit)
            .ToArray();
        var keptIndexes = kept.Select(item => item.Index).ToHashSet();
        var limited = kept.Select(item => item.Seed).ToArray();
        var skipped = indexed
            .Where(item => !keptIndexes.Contains(item.Index))
            .Select(item => item.Seed)
            .ToArray();

        context.AddDiagnostic(
            "walls.candidate_limit_applied",
            DiagnosticSeverity.Warning,
            Name,
            "Wall candidate count exceeded the configured per-page limit; the longest and strongest-layer candidates were kept.",
            pageNumber,
            mainRegionBounds,
            Confidence.Medium,
            scope: DiagnosticScope.Page,
            sourcePrimitiveIds: skipped.Select(seed => seed.PrimitiveId).Take(50),
            properties: new Dictionary<string, string>
            {
                ["eligibleLineCountBeforeLimit"] = seeds.Count.ToString(),
                ["keptSeedCount"] = limited.Length.ToString(),
                ["skippedSeedCount"] = skipped.Length.ToString(),
                ["maxWallCandidateSeedsPerPage"] = limit.ToString(),
                ["sampledSkippedSourcePrimitiveIds"] = string.Join(",", skipped.Select(seed => seed.PrimitiveId).Take(10)),
                ["sourceRegionId"] = sourceRegionId
            });

        return limited;
    }

    private static int SeedPriority(WallSeed seed) =>
        seed.LayerCategory switch
        {
            LayerCategory.Wall => 4,
            LayerCategory.Structural => 3,
            LayerCategory.Unknown => 2,
            _ => 1
        };

    private sealed record IndexedWallSeed(WallSeed Seed, int Index);

    private static IReadOnlyList<CompactLineworkWallNoiseCluster> DetectCompactObjectLineworkWallNoise(
        IReadOnlyList<WallLineCandidate> candidates,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (!options.FilterCompactObjectLineworkFromWalls)
        {
            return Array.Empty<CompactLineworkWallNoiseCluster>();
        }

        var items = candidates
            .Where(candidate => IsCompactObjectLineworkWallNoiseCandidate(candidate, options))
            .Select(candidate => new CompactLineworkWallNoiseItem(
                candidate.PrimitiveId,
                candidate.Line.Segment,
                candidate.Line.Segment.Bounds,
                candidate.Line.Segment.Length,
                ResolveWallOrientation(candidate.Line.Segment, options)))
            .ToArray();
        if (items.Length < Math.Max(5, options.MinCompositeObjectPrimitiveCount + 2))
        {
            return Array.Empty<CompactLineworkWallNoiseCluster>();
        }

        return BuildCompactLineworkClusters(items, options.CompositeObjectClusterTolerance)
            .Where(cluster => QualifiesCompactObjectLineworkWallNoise(cluster, mainRegionBounds, options))
            .Select(cluster => new CompactLineworkWallNoiseCluster(
                PlanRect.Union(cluster.Select(item => item.Bounds)),
                cluster.Select(item => item.PrimitiveId).Distinct(StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    private static bool IsCompactObjectLineworkWallNoiseCandidate(
        WallLineCandidate candidate,
        ScannerOptions options)
    {
        if (!candidate.UseForWallDetection
            || candidate.LayerCategory != LayerCategory.Unknown
            || candidate.LayerConfidence.Value >= 0.45)
        {
            return false;
        }

        var length = candidate.Line.Segment.Length;
        return length >= Math.Max(1, options.MinWallFragmentLength)
            && length <= Math.Max(options.MinWallLength, options.MaxCompositeObjectPrimitiveLength);
    }

    private static IReadOnlyList<IReadOnlyList<CompactLineworkWallNoiseItem>> BuildCompactLineworkClusters(
        IReadOnlyList<CompactLineworkWallNoiseItem> items,
        double tolerance)
    {
        var clusters = new List<IReadOnlyList<CompactLineworkWallNoiseItem>>();
        var visited = new bool[items.Count];
        var searchTolerance = Math.Max(0.1, tolerance);
        var spatialIndex = CompactLineworkWallNoiseSpatialIndex.Create(items, searchTolerance);

        for (var index = 0; index < items.Count; index++)
        {
            if (visited[index])
            {
                continue;
            }

            var cluster = new List<CompactLineworkWallNoiseItem>();
            var queue = new Queue<int>();
            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = items[currentIndex];
                cluster.Add(current);

                foreach (var candidateIndex in spatialIndex.Query(current.Bounds.Inflate(searchTolerance)))
                {
                    if (visited[candidateIndex]
                        || !current.Bounds.Inflate(searchTolerance).Intersects(items[candidateIndex].Bounds))
                    {
                        continue;
                    }

                    visited[candidateIndex] = true;
                    queue.Enqueue(candidateIndex);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static bool QualifiesCompactObjectLineworkWallNoise(
        IReadOnlyList<CompactLineworkWallNoiseItem> cluster,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (cluster.Count < Math.Max(5, options.MinCompositeObjectPrimitiveCount + 2))
        {
            return false;
        }

        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        if (bounds.IsEmpty || bounds.Width < 4 || bounds.Height < 4)
        {
            return false;
        }

        var maximumSpan = Math.Min(
            Math.Min(mainRegionBounds.Width, mainRegionBounds.Height) * 0.45,
            Math.Max(options.MaxCompositeObjectPrimitiveLength, options.MinWallLength * 4));
        if (bounds.Width > maximumSpan || bounds.Height > maximumSpan)
        {
            return false;
        }

        var maximumArea = mainRegionBounds.Area * Math.Clamp(options.MaxCompositeObjectAreaRatio, 0.001, 0.5);
        if (bounds.Area <= 0 || bounds.Area > maximumArea)
        {
            return false;
        }

        var hasHorizontal = cluster.Any(item => item.Orientation == WallOrientation.Horizontal);
        var hasVertical = cluster.Any(item => item.Orientation == WallOrientation.Vertical);
        var hasOther = cluster.Any(item => item.Orientation == WallOrientation.Other);
        if ((hasHorizontal ? 1 : 0) + (hasVertical ? 1 : 0) + (hasOther ? 1 : 0) < 2)
        {
            return false;
        }

        var perimeter = Math.Max(1, (bounds.Width * 2) + (bounds.Height * 2));
        var totalLength = cluster.Sum(item => item.Length);
        return totalLength >= perimeter * 1.08 || cluster.Count >= Math.Max(8, options.MinCompositeObjectPrimitiveCount + 5);
    }

    private static IReadOnlyList<DenseOrthogonalPatternWallNoiseCluster> DetectDenseOrthogonalPatternWallNoise(
        IReadOnlyList<WallLineCandidate> candidates,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (!options.FilterDenseOrthogonalPatternsFromWalls)
        {
            return Array.Empty<DenseOrthogonalPatternWallNoiseCluster>();
        }

        var items = candidates
            .Where(candidate => IsDenseOrthogonalPatternWallNoiseCandidate(candidate, options))
            .Select(candidate => DenseOrthogonalPatternWallNoiseItem.From(candidate, options))
            .ToArray();
        if (items.Length < 12)
        {
            return Array.Empty<DenseOrthogonalPatternWallNoiseCluster>();
        }

        return BuildDenseOrthogonalPatternClusters(items, Math.Max(2.5, options.WallSnapTolerance * 2.25))
            .Select(cluster => TryCreateDenseOrthogonalPatternCluster(cluster, mainRegionBounds, options))
            .Where(cluster => cluster is not null)
            .Select(cluster => cluster!)
            .ToArray();
    }

    private static bool IsDenseOrthogonalPatternWallNoiseCandidate(
        WallLineCandidate candidate,
        ScannerOptions options)
    {
        if (!candidate.UseForWallDetection
            || HasStrongWallLayerEvidence(candidate.LayerEvidence))
        {
            return false;
        }

        if (candidate.LayerCategory is LayerCategory.Wall or LayerCategory.Structural
            && candidate.LayerConfidence.Value >= 0.35)
        {
            return false;
        }

        var orientation = ResolveWallOrientation(candidate.Line.Segment, options);
        return orientation is WallOrientation.Horizontal or WallOrientation.Vertical
            && candidate.Line.Segment.Length >= Math.Max(1, options.MinWallFragmentLength);
    }

    private static IReadOnlyList<IReadOnlyList<DenseOrthogonalPatternWallNoiseItem>> BuildDenseOrthogonalPatternClusters(
        IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> items,
        double tolerance)
    {
        var clusters = new List<IReadOnlyList<DenseOrthogonalPatternWallNoiseItem>>();
        var visited = new bool[items.Count];
        var spatialIndex = DenseOrthogonalPatternWallNoiseSpatialIndex.Create(items, tolerance);

        for (var index = 0; index < items.Count; index++)
        {
            if (visited[index])
            {
                continue;
            }

            var cluster = new List<DenseOrthogonalPatternWallNoiseItem>();
            var queue = new Queue<int>();
            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = items[currentIndex];
                cluster.Add(current);

                foreach (var candidateIndex in spatialIndex.Query(current.Bounds.Inflate(tolerance)))
                {
                    if (visited[candidateIndex]
                        || !current.Bounds.Inflate(tolerance).Intersects(items[candidateIndex].Bounds))
                    {
                        continue;
                    }

                    visited[candidateIndex] = true;
                    queue.Enqueue(candidateIndex);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static DenseOrthogonalPatternWallNoiseCluster? TryCreateDenseOrthogonalPatternCluster(
        IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> cluster,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        var horizontal = cluster
            .Where(item => item.Orientation == WallOrientation.Horizontal)
            .ToArray();
        var vertical = cluster
            .Where(item => item.Orientation == WallOrientation.Vertical)
            .ToArray();
        if (horizontal.Length < 5 || vertical.Length < 5)
        {
            return null;
        }

        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        if (bounds.IsEmpty || bounds.Width < options.MinWallLength || bounds.Height < options.MinWallLength)
        {
            return null;
        }

        var mainArea = Math.Max(1, mainRegionBounds.Area);
        if (bounds.Area > mainArea * 0.28)
        {
            return null;
        }

        var horizontalCoordinates = CoordinateBuckets(horizontal.Select(item => item.Coordinate), options.WallSnapTolerance);
        var verticalCoordinates = CoordinateBuckets(vertical.Select(item => item.Coordinate), options.WallSnapTolerance);
        if (horizontalCoordinates.Count < 5 || verticalCoordinates.Count < 5)
        {
            return null;
        }

        var horizontalSpacings = AdjacentSpacings(horizontalCoordinates, options.WallSnapTolerance).Order().ToArray();
        var verticalSpacings = AdjacentSpacings(verticalCoordinates, options.WallSnapTolerance).Order().ToArray();
        if (horizontalSpacings.Length < 4 || verticalSpacings.Length < 4)
        {
            return null;
        }

        var horizontalMedianSpacing = Median(horizontalSpacings);
        var verticalMedianSpacing = Median(verticalSpacings);
        var maxDenseSpacing = MaxDenseOrthogonalPatternSpacing(options);
        if (horizontalMedianSpacing > maxDenseSpacing
            || verticalMedianSpacing > maxDenseSpacing
            || horizontalMedianSpacing <= 0
            || verticalMedianSpacing <= 0)
        {
            return null;
        }

        if (SpacingRegularity(horizontalSpacings, horizontalMedianSpacing) < 0.55
            || SpacingRegularity(verticalSpacings, verticalMedianSpacing) < 0.55)
        {
            return null;
        }

        var intersectionCount = CountAxisIntersections(horizontal, vertical, Math.Max(1.5, options.WallSnapTolerance));
        var potentialIntersectionCount = horizontal.Length * vertical.Length;
        var minimumIntersections = Math.Max(16, (int)Math.Ceiling(potentialIntersectionCount * 0.32));
        if (intersectionCount < minimumIntersections)
        {
            return null;
        }

        var totalLength = cluster.Sum(item => item.Length);
        var perimeter = Math.Max(1, (bounds.Width * 2) + (bounds.Height * 2));
        if (totalLength < perimeter * 2.1)
        {
            return null;
        }

        return new DenseOrthogonalPatternWallNoiseCluster(
            bounds,
            cluster.Select(item => item.PrimitiveId).Distinct(StringComparer.Ordinal).ToArray(),
            horizontal.Length,
            vertical.Length,
            intersectionCount,
            Math.Round(horizontalMedianSpacing, 3),
            Math.Round(verticalMedianSpacing, 3));
    }

    private static IReadOnlyList<DenseOrthogonalPatternWallRunNoiseCluster> DetectDenseOrthogonalPatternWallRunNoise(
        IReadOnlyList<AxisRun> runs,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (!options.FilterDenseOrthogonalPatternsFromWalls)
        {
            return Array.Empty<DenseOrthogonalPatternWallRunNoiseCluster>();
        }

        var items = runs
            .Where(run => !HasStrongWallLayerEvidence(run.LayerEvidence))
            .Select(DenseOrthogonalPatternWallRunNoiseItem.From)
            .ToArray();
        if (items.Length < 12)
        {
            return Array.Empty<DenseOrthogonalPatternWallRunNoiseCluster>();
        }

        return BuildDenseOrthogonalPatternRunClusters(items, Math.Max(2.5, options.WallSnapTolerance * 2.25))
            .Select(cluster => TryCreateDenseOrthogonalPatternRunCluster(cluster, mainRegionBounds, options))
            .Where(cluster => cluster is not null)
            .Select(cluster => cluster!)
            .ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem>> BuildDenseOrthogonalPatternRunClusters(
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> items,
        double tolerance)
    {
        var clusters = new List<IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem>>();
        var visited = new bool[items.Count];
        var spatialIndex = DenseOrthogonalPatternWallRunNoiseSpatialIndex.Create(items, tolerance);

        for (var index = 0; index < items.Count; index++)
        {
            if (visited[index])
            {
                continue;
            }

            var cluster = new List<DenseOrthogonalPatternWallRunNoiseItem>();
            var queue = new Queue<int>();
            visited[index] = true;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                var currentIndex = queue.Dequeue();
                var current = items[currentIndex];
                cluster.Add(current);

                foreach (var candidateIndex in spatialIndex.Query(current.Bounds.Inflate(tolerance)))
                {
                    if (visited[candidateIndex]
                        || !current.Bounds.Inflate(tolerance).Intersects(items[candidateIndex].Bounds))
                    {
                        continue;
                    }

                    visited[candidateIndex] = true;
                    queue.Enqueue(candidateIndex);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private static DenseOrthogonalPatternWallRunNoiseCluster? TryCreateDenseOrthogonalPatternRunCluster(
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> cluster,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        var horizontal = cluster
            .Where(item => item.Orientation == WallOrientation.Horizontal)
            .ToArray();
        var vertical = cluster
            .Where(item => item.Orientation == WallOrientation.Vertical)
            .ToArray();
        if (horizontal.Length < 5 || vertical.Length < 5)
        {
            return null;
        }

        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        if (bounds.IsEmpty || bounds.Width < options.MinWallLength || bounds.Height < options.MinWallLength)
        {
            return null;
        }

        var mainArea = Math.Max(1, mainRegionBounds.Area);
        if (bounds.Area > mainArea * 0.28)
        {
            return null;
        }

        var horizontalCoordinates = CoordinateBuckets(horizontal.Select(item => item.Coordinate), options.WallSnapTolerance);
        var verticalCoordinates = CoordinateBuckets(vertical.Select(item => item.Coordinate), options.WallSnapTolerance);
        if (horizontalCoordinates.Count < 5 || verticalCoordinates.Count < 5)
        {
            return null;
        }

        var horizontalSpacings = AdjacentSpacings(horizontalCoordinates, options.WallSnapTolerance).Order().ToArray();
        var verticalSpacings = AdjacentSpacings(verticalCoordinates, options.WallSnapTolerance).Order().ToArray();
        if (horizontalSpacings.Length < 4 || verticalSpacings.Length < 4)
        {
            return null;
        }

        var horizontalMedianSpacing = Median(horizontalSpacings);
        var verticalMedianSpacing = Median(verticalSpacings);
        var maxDenseSpacing = MaxDenseOrthogonalPatternSpacing(options);
        if (horizontalMedianSpacing > maxDenseSpacing
            || verticalMedianSpacing > maxDenseSpacing
            || horizontalMedianSpacing <= 0
            || verticalMedianSpacing <= 0)
        {
            return null;
        }

        if (SpacingRegularity(horizontalSpacings, horizontalMedianSpacing) < 0.5
            || SpacingRegularity(verticalSpacings, verticalMedianSpacing) < 0.5)
        {
            return null;
        }

        var intersectionCount = CountAxisRunIntersections(horizontal, vertical, Math.Max(1.5, options.WallSnapTolerance));
        var potentialIntersectionCount = horizontal.Length * vertical.Length;
        var minimumIntersections = Math.Max(16, (int)Math.Ceiling(potentialIntersectionCount * 0.28));
        if (intersectionCount < minimumIntersections)
        {
            return null;
        }

        var totalLength = cluster.Sum(item => item.Length);
        var perimeter = Math.Max(1, (bounds.Width * 2) + (bounds.Height * 2));
        if (totalLength < perimeter * 1.9)
        {
            return null;
        }

        return new DenseOrthogonalPatternWallRunNoiseCluster(
            bounds,
            cluster.SelectMany(item => item.Run.SourcePrimitiveIds).Distinct(StringComparer.Ordinal).ToArray(),
            cluster.Select(item => item.Run).Distinct().ToArray(),
            horizontal.Length,
            vertical.Length,
            intersectionCount,
            Math.Round(horizontalMedianSpacing, 3),
            Math.Round(verticalMedianSpacing, 3));
    }

    private static double MaxDenseOrthogonalPatternSpacing(ScannerOptions options) =>
        Math.Max(options.MinWallLength * 1.25, options.DefaultWallThickness * 8.0);

    private static IReadOnlyList<DenseParallelPatternWallRunNoiseCluster> DetectDenseParallelPatternWallRunNoise(
        IReadOnlyList<AxisRun> runs,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (!options.FilterDenseOrthogonalPatternsFromWalls)
        {
            return Array.Empty<DenseParallelPatternWallRunNoiseCluster>();
        }

        var items = runs
            .Where(run => !HasStrongWallLayerEvidence(run.LayerEvidence)
                && run.Length >= Math.Max(1, options.MinWallFragmentLength)
                && run.Length <= Math.Max(options.MaxCompositeObjectPrimitiveLength, options.MinWallLength * 6.0))
            .Select(DenseOrthogonalPatternWallRunNoiseItem.From)
            .ToArray();
        if (items.Length < 10)
        {
            return Array.Empty<DenseParallelPatternWallRunNoiseCluster>();
        }

        var allItems = runs
            .Where(run => !HasStrongWallLayerEvidence(run.LayerEvidence))
            .Select(DenseOrthogonalPatternWallRunNoiseItem.From)
            .ToArray();
        var clusters = new List<DenseParallelPatternWallRunNoiseCluster>();
        foreach (var group in items.GroupBy(item => item.Orientation))
        {
            if (group.Key is not (WallOrientation.Horizontal or WallOrientation.Vertical))
            {
                continue;
            }

            var spanBucketSize = Math.Max(MaxDenseParallelPatternSpacing(options) * 3.0, options.DefaultWallThickness * 8.0);
            foreach (var spanGroup in group.GroupBy(item => SpanBucket(item, spanBucketSize)))
            {
                if (spanGroup.Count() < 8)
                {
                    continue;
                }

                clusters.AddRange(BuildDenseParallelPatternRunClusters(
                        spanGroup.OrderBy(item => item.Coordinate).ToArray(),
                        allItems,
                        mainRegionBounds,
                        options)
                    .Where(cluster => cluster is not null)
                    .Select(cluster => cluster!));
            }
        }

        return clusters;
    }

    private static IEnumerable<DenseParallelPatternWallRunNoiseCluster?> BuildDenseParallelPatternRunClusters(
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> items,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> allItems,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (items.Count == 0)
        {
            yield break;
        }

        var maxSpacing = MaxDenseParallelPatternSpacing(options);
        var cluster = new List<DenseOrthogonalPatternWallRunNoiseItem>();
        foreach (var item in items)
        {
            if (cluster.Count == 0)
            {
                cluster.Add(item);
                continue;
            }

            var previous = cluster[^1];
            if (item.Coordinate - previous.Coordinate <= maxSpacing * 2.75
                && HasCompatibleDenseParallelSpan(item, cluster, options))
            {
                cluster.Add(item);
                continue;
            }

            yield return TryCreateDenseParallelPatternRunCluster(cluster, allItems, mainRegionBounds, options);
            cluster = new List<DenseOrthogonalPatternWallRunNoiseItem> { item };
        }

        yield return TryCreateDenseParallelPatternRunCluster(cluster, allItems, mainRegionBounds, options);
    }

    private static (int Start, int End) SpanBucket(DenseOrthogonalPatternWallRunNoiseItem item, double bucketSize) =>
        ((int)Math.Round(item.Start / bucketSize), (int)Math.Round(item.End / bucketSize));

    private static DenseParallelPatternWallRunNoiseCluster? TryCreateDenseParallelPatternRunCluster(
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> cluster,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> allItems,
        PlanRect mainRegionBounds,
        ScannerOptions options)
    {
        if (cluster.Count < 8)
        {
            return null;
        }

        var orientation = cluster[0].Orientation;
        var coordinateBuckets = CoordinateBuckets(cluster.Select(item => item.Coordinate), options.WallSnapTolerance * 0.5);
        if (coordinateBuckets.Count < 8)
        {
            return null;
        }

        var spacings = AdjacentSpacings(coordinateBuckets, options.WallSnapTolerance * 0.25).Order().ToArray();
        if (spacings.Length < 7)
        {
            return null;
        }

        var medianSpacing = Median(spacings);
        if (medianSpacing <= 0 || medianSpacing > MaxDenseParallelPatternSpacing(options))
        {
            return null;
        }

        if (SpacingRegularity(spacings, medianSpacing) < 0.64)
        {
            return null;
        }

        var bounds = PlanRect.Union(cluster.Select(item => item.Bounds));
        if (bounds.IsEmpty)
        {
            return null;
        }

        var mainArea = Math.Max(1, mainRegionBounds.Area);
        if (bounds.Area <= 0 || bounds.Area > mainArea * 0.18)
        {
            return null;
        }

        var repeatedSpan = orientation == WallOrientation.Horizontal ? bounds.Width : bounds.Height;
        var patternDepth = orientation == WallOrientation.Horizontal ? bounds.Height : bounds.Width;
        var maximumRepeatedSpan = Math.Max(options.MinWallLength * 3.0, options.MaxCompositeObjectPrimitiveLength * 0.65);
        if (repeatedSpan < options.MinWallLength
            || repeatedSpan > maximumRepeatedSpan
            || patternDepth < medianSpacing * 6.0)
        {
            return null;
        }

        var totalLength = cluster.Sum(item => item.Length);
        var perimeter = Math.Max(1, (bounds.Width * 2) + (bounds.Height * 2));
        if (totalLength < perimeter * 1.55)
        {
            return null;
        }

        var runs = cluster
            .Select(item => item.Run)
            .Concat(FindPerpendicularRunsInsideDenseParallelPattern(bounds, orientation, allItems, medianSpacing, options))
            .Distinct()
            .ToArray();
        var sourcePrimitiveIds = runs
            .SelectMany(run => run.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var finalBounds = PlanRect.Union(runs.Select(run => DenseOrthogonalPatternWallRunNoiseItem.From(run).Bounds));

        return new DenseParallelPatternWallRunNoiseCluster(
            finalBounds,
            sourcePrimitiveIds,
            runs,
            orientation,
            coordinateBuckets.Count,
            Math.Round(medianSpacing, 3));
    }

    private static bool HasCompatibleDenseParallelSpan(
        DenseOrthogonalPatternWallRunNoiseItem item,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> cluster,
        ScannerOptions options)
    {
        var starts = cluster.Select(value => value.Start).Order().ToArray();
        var ends = cluster.Select(value => value.End).Order().ToArray();
        var medianStart = Median(starts);
        var medianEnd = Median(ends);
        var overlapRatio = SpanOverlapRatio(item.Start, item.End, medianStart, medianEnd);
        var endpointTolerance = Math.Max(options.DefaultWallThickness * 3.0, MaxDenseParallelPatternSpacing(options));

        return overlapRatio >= 0.58
            || (Math.Abs(item.Start - medianStart) <= endpointTolerance
                && Math.Abs(item.End - medianEnd) <= endpointTolerance);
    }

    private static IEnumerable<AxisRun> FindPerpendicularRunsInsideDenseParallelPattern(
        PlanRect bounds,
        WallOrientation orientation,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> allItems,
        double medianSpacing,
        ScannerOptions options)
    {
        var perpendicular = orientation == WallOrientation.Horizontal
            ? WallOrientation.Vertical
            : WallOrientation.Horizontal;
        var edgeTolerance = Math.Max(medianSpacing * 1.75, options.DefaultWallThickness * 2.5);
        var patternStart = orientation == WallOrientation.Horizontal ? bounds.Top : bounds.Left;
        var patternEnd = orientation == WallOrientation.Horizontal ? bounds.Bottom : bounds.Right;
        var spanStart = orientation == WallOrientation.Horizontal ? bounds.Left : bounds.Top;
        var spanEnd = orientation == WallOrientation.Horizontal ? bounds.Right : bounds.Bottom;
        var minimumOverlap = Math.Min(
            Math.Max(options.MinWallLength, medianSpacing * 4.0),
            Math.Max(1, (patternEnd - patternStart) * 0.3));

        foreach (var item in allItems)
        {
            if (item.Orientation != perpendicular)
            {
                continue;
            }

            if (item.Coordinate < spanStart - edgeTolerance || item.Coordinate > spanEnd + edgeTolerance)
            {
                continue;
            }

            var overlap = OverlapLength(item.Start, item.End, patternStart, patternEnd);
            if (overlap >= minimumOverlap)
            {
                yield return item.Run;
            }
        }
    }

    private static double MaxDenseParallelPatternSpacing(ScannerOptions options) =>
        Math.Max(options.DefaultWallThickness * 2.75, options.MinWallLength * 0.5);

    private static IReadOnlyList<DenseCrossingParallelPatternGridCluster> DetectCrossingDenseParallelSurfaceGrids(
        IReadOnlyList<DenseParallelPatternWallRunNoiseCluster> parallelClusters,
        ScannerOptions options)
    {
        var horizontal = parallelClusters
            .Where(cluster => cluster.Orientation == WallOrientation.Horizontal)
            .ToArray();
        var vertical = parallelClusters
            .Where(cluster => cluster.Orientation == WallOrientation.Vertical)
            .ToArray();
        if (horizontal.Length == 0 || vertical.Length == 0)
        {
            return Array.Empty<DenseCrossingParallelPatternGridCluster>();
        }

        var clusters = new List<DenseCrossingParallelPatternGridCluster>();
        var seenSourceSets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var horizontalCluster in horizontal)
        {
            foreach (var verticalCluster in vertical)
            {
                var cluster = TryCreateCrossingDenseParallelSurfaceGrid(horizontalCluster, verticalCluster, options);
                if (cluster is null)
                {
                    continue;
                }

                var sourceKey = string.Join("|", cluster.SourcePrimitiveIds.Order(StringComparer.Ordinal));
                if (sourceKey.Length > 0 && seenSourceSets.Add(sourceKey))
                {
                    clusters.Add(cluster);
                }
            }
        }

        return clusters
            .OrderBy(cluster => cluster.Bounds.Top)
            .ThenBy(cluster => cluster.Bounds.Left)
            .ToArray();
    }

    private static DenseCrossingParallelPatternGridCluster? TryCreateCrossingDenseParallelSurfaceGrid(
        DenseParallelPatternWallRunNoiseCluster horizontalCluster,
        DenseParallelPatternWallRunNoiseCluster verticalCluster,
        ScannerOptions options)
    {
        var crossingBounds = horizontalCluster.Bounds.Intersection(verticalCluster.Bounds);
        if (crossingBounds.IsEmpty)
        {
            return null;
        }

        var medianSpacing = Math.Min(horizontalCluster.MedianSpacing, verticalCluster.MedianSpacing);
        var tolerance = Math.Max(options.WallSnapTolerance, medianSpacing * 1.5);
        var minimumSize = Math.Max(options.DefaultWallThickness * 4.0, medianSpacing * 3.0);
        if (crossingBounds.Width < minimumSize || crossingBounds.Height < minimumSize)
        {
            return null;
        }

        var horizontalRuns = horizontalCluster.Runs
            .Select(DenseOrthogonalPatternWallRunNoiseItem.From)
            .Where(item => item.Orientation == WallOrientation.Horizontal
                && CrossesSurfacePatternGridWindow(item, crossingBounds, tolerance))
            .ToArray();
        var verticalRuns = verticalCluster.Runs
            .Select(DenseOrthogonalPatternWallRunNoiseItem.From)
            .Where(item => item.Orientation == WallOrientation.Vertical
                && CrossesSurfacePatternGridWindow(item, crossingBounds, tolerance))
            .ToArray();
        if (horizontalRuns.Length < 5 || verticalRuns.Length < 5)
        {
            return null;
        }

        var horizontalCoordinates = CoordinateBuckets(horizontalRuns.Select(item => item.Coordinate), options.WallSnapTolerance * 0.5);
        var verticalCoordinates = CoordinateBuckets(verticalRuns.Select(item => item.Coordinate), options.WallSnapTolerance * 0.5);
        if (horizontalCoordinates.Count < 5 || verticalCoordinates.Count < 5)
        {
            return null;
        }

        var horizontalSpacings = AdjacentSpacings(horizontalCoordinates, options.WallSnapTolerance * 0.25).Order().ToArray();
        var verticalSpacings = AdjacentSpacings(verticalCoordinates, options.WallSnapTolerance * 0.25).Order().ToArray();
        if (horizontalSpacings.Length < 4 || verticalSpacings.Length < 4)
        {
            return null;
        }

        var horizontalMedianSpacing = Median(horizontalSpacings);
        var verticalMedianSpacing = Median(verticalSpacings);
        var maxDenseSpacing = MaxDenseParallelPatternSpacing(options);
        if (horizontalMedianSpacing <= 0
            || verticalMedianSpacing <= 0
            || horizontalMedianSpacing > maxDenseSpacing
            || verticalMedianSpacing > maxDenseSpacing)
        {
            return null;
        }

        if (SpacingRegularity(horizontalSpacings, horizontalMedianSpacing) < 0.48
            || SpacingRegularity(verticalSpacings, verticalMedianSpacing) < 0.48)
        {
            return null;
        }

        var intersectionCount = CountAxisRunIntersections(horizontalRuns, verticalRuns, tolerance);
        var potentialIntersections = horizontalCoordinates.Count * verticalCoordinates.Count;
        var minimumIntersections = Math.Max(16, (int)Math.Ceiling(potentialIntersections * 0.32));
        if (intersectionCount < minimumIntersections)
        {
            return null;
        }

        var sourcePrimitiveIds = horizontalRuns
            .Concat(verticalRuns)
            .SelectMany(item => item.Run.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sourcePrimitiveIds.Length < 10)
        {
            return null;
        }

        var finalBounds = crossingBounds
            .Inflate(Math.Min(horizontalMedianSpacing, verticalMedianSpacing) * 0.5)
            .ClampTo(PlanRect.Union(horizontalCluster.Bounds, verticalCluster.Bounds));

        return new DenseCrossingParallelPatternGridCluster(
            finalBounds,
            sourcePrimitiveIds,
            horizontalCoordinates.Count,
            verticalCoordinates.Count,
            intersectionCount,
            Math.Round(horizontalMedianSpacing, 3),
            Math.Round(verticalMedianSpacing, 3));
    }

    private static bool CrossesSurfacePatternGridWindow(
        DenseOrthogonalPatternWallRunNoiseItem item,
        PlanRect bounds,
        double tolerance)
    {
        if (item.Orientation == WallOrientation.Horizontal)
        {
            var overlap = OverlapLength(item.Start, item.End, bounds.Left, bounds.Right);
            return item.Coordinate >= bounds.Top - tolerance
                && item.Coordinate <= bounds.Bottom + tolerance
                && overlap >= Math.Max(1, bounds.Width * 0.35);
        }

        if (item.Orientation == WallOrientation.Vertical)
        {
            var overlap = OverlapLength(item.Start, item.End, bounds.Top, bounds.Bottom);
            return item.Coordinate >= bounds.Left - tolerance
                && item.Coordinate <= bounds.Right + tolerance
                && overlap >= Math.Max(1, bounds.Height * 0.35);
        }

        return false;
    }

    private static IReadOnlyList<SurfacePatternCandidate> CreateSurfacePatternCandidates(
        int pageNumber,
        string sourceRegionId,
        IReadOnlyList<DenseOrthogonalPatternWallNoiseCluster> candidateClusters,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseCluster> runClusters,
        IReadOnlyList<DenseParallelPatternWallRunNoiseCluster> parallelClusters,
        ScannerOptions options,
        int existingCount)
    {
        var candidates = new List<SurfacePatternCandidate>();
        var seenSourceSets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var cluster in DetectCrossingDenseParallelSurfaceGrids(parallelClusters, options))
        {
            AddIfNew(
                new SurfacePatternCandidate(
                    CreateSurfacePatternId(pageNumber, existingCount + candidates.Count + 1),
                    pageNumber,
                    SurfacePatternKind.DenseOrthogonalGrid,
                    SurfacePatternOrientation.Orthogonal,
                    cluster.Bounds,
                    sourceRegionId,
                    cluster.SourcePrimitiveIds.Length,
                    cluster.HorizontalLineCount,
                    cluster.VerticalLineCount,
                    cluster.IntersectionCount,
                    cluster.HorizontalMedianSpacing,
                    cluster.VerticalMedianSpacing,
                    null,
                    ExcludedFromWallDetection: true,
                    ExcludedFromStructuralTopology: true,
                    cluster.SourcePrimitiveIds,
                    new Confidence(0.74),
                    RequiresReview: true,
                    EvidenceForCrossingParallelSurfaceGrid(
                        cluster.SourcePrimitiveIds.Length,
                        cluster.HorizontalLineCount,
                        cluster.VerticalLineCount,
                        cluster.IntersectionCount,
                        cluster.HorizontalMedianSpacing,
                        cluster.VerticalMedianSpacing,
                        sourceRegionId)));
        }

        foreach (var cluster in candidateClusters)
        {
            AddIfNew(
                new SurfacePatternCandidate(
                    CreateSurfacePatternId(pageNumber, existingCount + candidates.Count + 1),
                    pageNumber,
                    SurfacePatternKind.DenseOrthogonalGrid,
                    SurfacePatternOrientation.Orthogonal,
                    cluster.Bounds,
                    sourceRegionId,
                    cluster.SourcePrimitiveIds.Length,
                    cluster.HorizontalLineCount,
                    cluster.VerticalLineCount,
                    cluster.IntersectionCount,
                    cluster.HorizontalMedianSpacing,
                    cluster.VerticalMedianSpacing,
                    null,
                    ExcludedFromWallDetection: true,
                    ExcludedFromStructuralTopology: true,
                    cluster.SourcePrimitiveIds,
                    new Confidence(0.78),
                    RequiresReview: true,
                    EvidenceForDenseOrthogonalPattern(
                        cluster.SourcePrimitiveIds.Length,
                        cluster.HorizontalLineCount,
                        cluster.VerticalLineCount,
                        cluster.IntersectionCount,
                        cluster.HorizontalMedianSpacing,
                        cluster.VerticalMedianSpacing,
                        sourceRegionId)));
        }

        foreach (var cluster in runClusters)
        {
            AddIfNew(
                new SurfacePatternCandidate(
                    CreateSurfacePatternId(pageNumber, existingCount + candidates.Count + 1),
                    pageNumber,
                    SurfacePatternKind.DenseOrthogonalGrid,
                    SurfacePatternOrientation.Orthogonal,
                    cluster.Bounds,
                    sourceRegionId,
                    cluster.SourcePrimitiveIds.Length,
                    cluster.HorizontalLineCount,
                    cluster.VerticalLineCount,
                    cluster.IntersectionCount,
                    cluster.HorizontalMedianSpacing,
                    cluster.VerticalMedianSpacing,
                    null,
                    ExcludedFromWallDetection: true,
                    ExcludedFromStructuralTopology: true,
                    cluster.SourcePrimitiveIds,
                    new Confidence(0.76),
                    RequiresReview: true,
                    EvidenceForDenseOrthogonalPattern(
                        cluster.SourcePrimitiveIds.Length,
                        cluster.HorizontalLineCount,
                        cluster.VerticalLineCount,
                        cluster.IntersectionCount,
                        cluster.HorizontalMedianSpacing,
                        cluster.VerticalMedianSpacing,
                        sourceRegionId)));
        }

        foreach (var cluster in parallelClusters)
        {
            AddIfNew(
                new SurfacePatternCandidate(
                    CreateSurfacePatternId(pageNumber, existingCount + candidates.Count + 1),
                    pageNumber,
                    SurfacePatternKind.DenseParallelBand,
                    ToSurfacePatternOrientation(cluster.Orientation),
                    cluster.Bounds,
                    sourceRegionId,
                    cluster.SourcePrimitiveIds.Length,
                    cluster.Orientation == WallOrientation.Horizontal ? cluster.LineCount : 0,
                    cluster.Orientation == WallOrientation.Vertical ? cluster.LineCount : 0,
                    0,
                    null,
                    null,
                    cluster.MedianSpacing,
                    ExcludedFromWallDetection: true,
                    ExcludedFromStructuralTopology: true,
                    cluster.SourcePrimitiveIds,
                    new Confidence(0.72),
                    RequiresReview: true,
                    EvidenceForDenseParallelPattern(
                        cluster.SourcePrimitiveIds.Length,
                        cluster.Orientation,
                        cluster.LineCount,
                        cluster.MedianSpacing,
                        sourceRegionId)));
        }

        return candidates;

        void AddIfNew(SurfacePatternCandidate candidate)
        {
            var key = string.Join("|", candidate.SourcePrimitiveIds.Order(StringComparer.Ordinal));
            if (key.Length == 0 || !seenSourceSets.Add(key))
            {
                return;
            }

            candidates.Add(candidate);
        }
    }

    private static string CreateSurfacePatternId(int pageNumber, int index) =>
        $"page:{pageNumber}:surface-pattern:{index:000}";

    private static SurfacePatternOrientation ToSurfacePatternOrientation(WallOrientation orientation) =>
        orientation switch
        {
            WallOrientation.Horizontal => SurfacePatternOrientation.Horizontal,
            WallOrientation.Vertical => SurfacePatternOrientation.Vertical,
            _ => SurfacePatternOrientation.Unknown
        };

    private static IReadOnlyList<string> EvidenceForDenseOrthogonalPattern(
        int lineCount,
        int horizontalLineCount,
        int verticalLineCount,
        int intersectionCount,
        double horizontalMedianSpacing,
        double verticalMedianSpacing,
        string sourceRegionId) =>
        new[]
        {
            "classified as dense repeated surface/detail pattern, not structural wall geometry",
            "excluded from wall detection and structural topology",
            $"source region {sourceRegionId}",
            $"{lineCount} source line primitive(s)",
            $"{horizontalLineCount} horizontal and {verticalLineCount} vertical lines",
            $"{intersectionCount} grid intersection(s)",
            $"median spacing h={FormatPatternNumber(horizontalMedianSpacing)}, v={FormatPatternNumber(verticalMedianSpacing)} drawing units"
        };

    private static IReadOnlyList<string> EvidenceForDenseParallelPattern(
        int sourceLineCount,
        WallOrientation orientation,
        int repeatedLineCount,
        double medianSpacing,
        string sourceRegionId) =>
        new[]
        {
            "classified as dense repeated surface/detail band, not structural wall geometry",
            "excluded from wall detection and structural topology",
            $"source region {sourceRegionId}",
            $"{sourceLineCount} source line primitive(s)",
            $"{repeatedLineCount} repeated {orientation.ToString().ToLowerInvariant()} line(s)",
            $"median spacing {FormatPatternNumber(medianSpacing)} drawing units"
        };

    private static IReadOnlyList<string> EvidenceForCrossingParallelSurfaceGrid(
        int sourceLineCount,
        int horizontalLineCount,
        int verticalLineCount,
        int intersectionCount,
        double horizontalMedianSpacing,
        double verticalMedianSpacing,
        string sourceRegionId) =>
        new[]
        {
            "classified as dense crossing surface/detail grid from overlapping parallel bands, not structural wall geometry",
            "excluded from wall detection and structural topology",
            $"source region {sourceRegionId}",
            $"{sourceLineCount} source line primitive(s)",
            $"{horizontalLineCount} horizontal and {verticalLineCount} vertical repeated line coordinate(s)",
            $"{intersectionCount} crossing grid intersection(s)",
            $"median spacing h={FormatPatternNumber(horizontalMedianSpacing)}, v={FormatPatternNumber(verticalMedianSpacing)} drawing units"
        };

    private static string FormatPatternNumber(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static double SpanOverlapRatio(double firstStart, double firstEnd, double secondStart, double secondEnd)
    {
        var overlap = OverlapLength(firstStart, firstEnd, secondStart, secondEnd);
        var shortest = Math.Min(Math.Abs(firstEnd - firstStart), Math.Abs(secondEnd - secondStart));
        return shortest <= 0 ? 0 : overlap / shortest;
    }

    private static double OverlapLength(double firstStart, double firstEnd, double secondStart, double secondEnd) =>
        Math.Max(0, Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart));

    private static IReadOnlyList<double> CoordinateBuckets(IEnumerable<double> coordinates, double tolerance)
    {
        var ordered = coordinates.Order().ToArray();
        if (ordered.Length == 0)
        {
            return Array.Empty<double>();
        }

        var result = new List<double>();
        var sum = ordered[0];
        var count = 1;
        var last = ordered[0];
        var bucketTolerance = Math.Max(0.25, tolerance);

        for (var index = 1; index < ordered.Length; index++)
        {
            var value = ordered[index];
            if (Math.Abs(value - last) <= bucketTolerance)
            {
                sum += value;
                count++;
                last = value;
                continue;
            }

            result.Add(sum / count);
            sum = value;
            count = 1;
            last = value;
        }

        result.Add(sum / count);
        return result;
    }

    private static IEnumerable<double> AdjacentSpacings(IReadOnlyList<double> coordinates, double tolerance)
    {
        for (var index = 1; index < coordinates.Count; index++)
        {
            var spacing = coordinates[index] - coordinates[index - 1];
            if (spacing > Math.Max(0.25, tolerance * 0.5))
            {
                yield return spacing;
            }
        }
    }

    private static double SpacingRegularity(IReadOnlyList<double> spacings, double medianSpacing)
    {
        if (spacings.Count == 0 || medianSpacing <= 0)
        {
            return 0;
        }

        var tolerance = Math.Max(1.5, medianSpacing * 0.35);
        return spacings.Count(spacing => Math.Abs(spacing - medianSpacing) <= tolerance) / (double)spacings.Count;
    }

    private static int CountAxisIntersections(
        IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> horizontal,
        IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> vertical,
        double tolerance)
    {
        var count = 0;
        foreach (var h in horizontal)
        {
            foreach (var v in vertical)
            {
                if (v.Coordinate >= h.Start - tolerance
                    && v.Coordinate <= h.End + tolerance
                    && h.Coordinate >= v.Start - tolerance
                    && h.Coordinate <= v.End + tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CountAxisRunIntersections(
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> horizontal,
        IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> vertical,
        double tolerance)
    {
        var count = 0;
        foreach (var h in horizontal)
        {
            foreach (var v in vertical)
            {
                if (v.Coordinate >= h.Start - tolerance
                    && v.Coordinate <= h.End + tolerance
                    && h.Coordinate >= v.Start - tolerance
                    && h.Coordinate <= v.End + tolerance)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool IsLikelyFragmentNoiseRun(AxisRun run, ScannerOptions options)
    {
        if (HasStrongWallLayerEvidence(run.LayerEvidence) || run.FragmentCount < 7)
        {
            return false;
        }

        var maxLength = Math.Max(options.MinWallLength * 5, options.MaxCompositeObjectPrimitiveLength);
        if (run.Length > maxLength)
        {
            return false;
        }

        var fragmentDensity = run.FragmentCount / Math.Max(1, run.Length);
        return fragmentDensity >= 0.08
            && (run.TotalGapLength > options.WallMergeTolerance || run.FragmentCount >= 10);
    }

    private static bool IsLikelyFragmentNoiseRun(NonAxisRun run, ScannerOptions options)
    {
        if (HasStrongWallLayerEvidence(run.LayerEvidence) || run.FragmentCount < 3)
        {
            return false;
        }

        var maxLength = Math.Max(options.MinWallLength * 3.5, options.MaxCompositeObjectPrimitiveLength * 0.65);
        if (run.Length > maxLength)
        {
            return false;
        }

        var fragmentDensity = run.FragmentCount / Math.Max(1, run.Length);
        return fragmentDensity >= 0.045
            && (run.TotalGapLength > options.WallMergeTolerance || run.FragmentCount >= 5);
    }

    private static bool HasStrongWallLayerEvidence(IEnumerable<string> layerEvidence) =>
        layerEvidence.Any(item =>
            item.Contains("classified Wall", StringComparison.OrdinalIgnoreCase)
            || item.Contains("classified Structural", StringComparison.OrdinalIgnoreCase)
            || item.Contains("override", StringComparison.OrdinalIgnoreCase));

    private static WallOrientation ResolveWallOrientation(PlanLineSegment segment, ScannerOptions options)
    {
        var tolerance = options.GeometryTolerance.Distance;
        return segment.IsHorizontal(tolerance)
            ? WallOrientation.Horizontal
            : segment.IsVertical(tolerance)
                ? WallOrientation.Vertical
                : WallOrientation.Other;
    }

    private static int AddAxisWalls(
        int pageNumber,
        string sourceRegionId,
        IReadOnlyList<AxisRun> runs,
        ScanContext context)
    {
        var consumed = new HashSet<AxisRun>();
        var profile = context.Options.EnableWallPairReconstruction
            ? WallPairSeparationProfile.FromAxisRuns(runs, context.Options)
            : WallPairSeparationProfile.Empty;
        var pairs = context.Options.EnableWallPairReconstruction
            ? ReconstructWallPairs(runs, context.Options, consumed, profile).ToArray()
            : Array.Empty<WallPairRun>();

        foreach (var pair in pairs)
        {
            context.Walls.Add(
                CreatePairedAxisWall(
                    pageNumber,
                    sourceRegionId,
                    pair,
                    context.Walls.Count + 1,
                    context.Options,
                    context.Calibration));
        }

        foreach (var run in runs.Where(run => !consumed.Contains(run)))
        {
            context.Walls.Add(CreateAxisWall(pageNumber, sourceRegionId, run, context.Walls.Count + 1, context.Options, context.Calibration));
        }

        AddWallPairSeparationProfileDiagnostic(pageNumber, sourceRegionId, "axis", profile, pairs.Length, context);
        return pairs.Length;
    }

    private static int AddNonAxisWalls(
        int pageNumber,
        string sourceRegionId,
        IReadOnlyList<NonAxisRun> runs,
        ScanContext context)
    {
        var consumed = new HashSet<NonAxisRun>();
        var profile = context.Options.EnableWallPairReconstruction
            ? WallPairSeparationProfile.FromNonAxisRuns(runs, context.Options)
            : WallPairSeparationProfile.Empty;
        var pairs = context.Options.EnableWallPairReconstruction
            ? ReconstructNonAxisWallPairs(runs, context.Options, consumed, profile).ToArray()
            : Array.Empty<NonAxisWallPairRun>();

        foreach (var pair in pairs)
        {
            context.Walls.Add(
                CreatePairedNonAxisWall(
                    pageNumber,
                    sourceRegionId,
                    pair,
                    context.Walls.Count + 1,
                    context.Options,
                    context.Calibration));
        }

        foreach (var run in runs.Where(run => !consumed.Contains(run)))
        {
            context.Walls.Add(CreateNonAxisWall(pageNumber, sourceRegionId, run, context.Walls.Count + 1, context.Options, context.Calibration));
        }

        AddWallPairSeparationProfileDiagnostic(pageNumber, sourceRegionId, "nonOrthogonal", profile, pairs.Length, context);
        return pairs.Length;
    }

    private static WallSegment CreateAxisWall(
        int pageNumber,
        string sourceRegionId,
        AxisRun run,
        int wallNumber,
        ScannerOptions options,
        PlanCalibration calibration)
    {
        var centerLine = run.Orientation == WallOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(run.Start, run.Coordinate), new PlanPoint(run.End, run.Coordinate))
            : new PlanLineSegment(new PlanPoint(run.Coordinate, run.Start), new PlanPoint(run.Coordinate, run.End));

        var confidence = AxisRunConfidence(run, centerLine.Length, options);

        var thickness = ResolveThickness(run.StrokeWidth, options);
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            pageNumber,
            centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5)),
            sourceRegionId);
        return new WallSegment(
            $"page:{pageNumber}:wall:{wallNumber}",
            pageNumber,
            centerLine,
            thickness,
            confidence)
        {
            SourceRegionId = sourceRegionId,
            DetectionKind = run.FragmentCount > 1 ? WallDetectionKind.FragmentMerged : WallDetectionKind.SingleLine,
            SourcePrimitiveIds = run.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray(),
            Evidence = AxisRunEvidence(run),
            LengthMeters = calibration.ToMeters(centerLine.Length, scaleGroup),
            ThicknessMillimeters = calibration.ToMillimeters(thickness, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };
    }

    private static WallSegment CreatePairedAxisWall(
        int pageNumber,
        string sourceRegionId,
        WallPairRun pair,
        int wallNumber,
        ScannerOptions options,
        PlanCalibration calibration)
    {
        var centerCoordinate = (pair.First.Coordinate + pair.Second.Coordinate) / 2.0;
        var centerLine = pair.Orientation == WallOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(pair.Start, centerCoordinate), new PlanPoint(pair.End, centerCoordinate))
            : new PlanLineSegment(new PlanPoint(centerCoordinate, pair.Start), new PlanPoint(centerCoordinate, pair.End));

        var thickness = Math.Abs(pair.First.Coordinate - pair.Second.Coordinate);
        var gapRatio = (pair.First.TotalGapLength + pair.Second.TotalGapLength) / Math.Max(1, centerLine.Length * 2);
        var fragmentPenalty = Math.Min(0.14, gapRatio * 0.65);
        if (pair.First.FragmentCount + pair.Second.FragmentCount > 4)
        {
            fragmentPenalty += 0.03;
        }

        var confidence = new Confidence(Math.Clamp(
            Math.Min(
            0.94,
            0.58
            + (pair.OverlapRatio * 0.22)
            + Math.Min(0.14, centerLine.Length / Math.Max(1, options.MinWallLength * 16)))
            - fragmentPenalty,
            0.45,
            0.94));

        var evidence = new List<string>
        {
            "parallel wall-face pair",
            $"face separation {Math.Round(thickness, 3)} drawing units",
            $"pair score {Math.Round(pair.Score, 3)}",
            $"overlap ratio {Math.Round(pair.OverlapRatio, 3)}"
        };
        AddFragmentEvidence(evidence, "first face", pair.First);
        AddFragmentEvidence(evidence, "second face", pair.Second);
        AddDuplicateEvidence(evidence, "first face", pair.First);
        AddDuplicateEvidence(evidence, "second face", pair.Second);
        AddLayerEvidence(evidence, pair.First);
        AddLayerEvidence(evidence, pair.Second);
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            pageNumber,
            centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5)),
            sourceRegionId);

        return new WallSegment(
            $"page:{pageNumber}:wall:{wallNumber}",
            pageNumber,
            centerLine,
            thickness,
            confidence)
        {
            SourceRegionId = sourceRegionId,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = pair.First.SourcePrimitiveIds
                .Concat(pair.Second.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence,
            PairEvidence = new WallPairEvidence(
                AxisFaceLine(pair.Orientation, pair.Start, pair.End, pair.First.Coordinate),
                AxisFaceLine(pair.Orientation, pair.Start, pair.End, pair.Second.Coordinate),
                Math.Round(thickness, 3),
                Math.Round(pair.OverlapRatio, 3),
                Math.Round(pair.Score, 3),
                pair.First.FragmentCount,
                pair.Second.FragmentCount,
                pair.First.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray(),
                pair.Second.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray()),
            LengthMeters = calibration.ToMeters(centerLine.Length, scaleGroup),
            ThicknessMillimeters = calibration.ToMillimeters(thickness, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };
    }

    private static WallSegment CreateNonAxisWall(
        int pageNumber,
        string sourceRegionId,
        NonAxisRun run,
        int wallNumber,
        ScannerOptions options,
        PlanCalibration calibration)
    {
        var centerLine = run.ToLineSegment();
        var thickness = ResolveThickness(run.StrokeWidth, options);
        var confidence = RunConfidence(run.FragmentCount, run.TotalGapLength, centerLine.Length, options, 0.82);
        var evidence = new List<string>
        {
            run.FragmentCount > 1 ? "merged non-orthogonal collinear wall fragments" : "non-orthogonal wall-length vector",
            $"angle {Math.Round(run.AngleDegrees, 2)} degrees"
        };
        AddFragmentEvidence(evidence, "run", run);
        AddDuplicateEvidence(evidence, "run", run);
        AddLayerEvidence(evidence, run);
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            pageNumber,
            centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5)),
            sourceRegionId);

        return new WallSegment(
            $"page:{pageNumber}:wall:{wallNumber}",
            pageNumber,
            centerLine,
            thickness,
            confidence)
        {
            SourceRegionId = sourceRegionId,
            DetectionKind = run.FragmentCount > 1 ? WallDetectionKind.FragmentMerged : WallDetectionKind.SingleLine,
            SourcePrimitiveIds = run.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray(),
            Evidence = evidence,
            LengthMeters = calibration.ToMeters(centerLine.Length, scaleGroup),
            ThicknessMillimeters = calibration.ToMillimeters(thickness, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };
    }

    private static WallSegment CreatePairedNonAxisWall(
        int pageNumber,
        string sourceRegionId,
        NonAxisWallPairRun pair,
        int wallNumber,
        ScannerOptions options,
        PlanCalibration calibration)
    {
        var centerLine = pair.ToCenterLine();
        var thickness = Math.Abs(pair.First.NormalCoordinate - pair.Second.NormalCoordinate);
        var gapRatio = (pair.First.TotalGapLength + pair.Second.TotalGapLength) / Math.Max(1, centerLine.Length * 2);
        var fragmentPenalty = Math.Min(0.14, gapRatio * 0.65);
        if (pair.First.FragmentCount + pair.Second.FragmentCount > 4)
        {
            fragmentPenalty += 0.03;
        }

        var confidence = new Confidence(Math.Clamp(
            Math.Min(
                0.9,
                0.54
                + (pair.OverlapRatio * 0.22)
                + Math.Min(0.12, centerLine.Length / Math.Max(1, options.MinWallLength * 18)))
            - fragmentPenalty,
            0.43,
            0.9));

        var evidence = new List<string>
        {
            "non-orthogonal parallel wall-face pair",
            $"angle {Math.Round(pair.AngleDegrees, 2)} degrees",
            $"face separation {Math.Round(thickness, 3)} drawing units",
            $"pair score {Math.Round(pair.Score, 3)}",
            $"overlap ratio {Math.Round(pair.OverlapRatio, 3)}"
        };
        AddFragmentEvidence(evidence, "first face", pair.First);
        AddFragmentEvidence(evidence, "second face", pair.Second);
        AddDuplicateEvidence(evidence, "first face", pair.First);
        AddDuplicateEvidence(evidence, "second face", pair.Second);
        AddLayerEvidence(evidence, pair.First);
        AddLayerEvidence(evidence, pair.Second);
        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            pageNumber,
            centerLine.Bounds.Inflate(Math.Max(thickness / 2.0, 0.5)),
            sourceRegionId);

        return new WallSegment(
            $"page:{pageNumber}:wall:{wallNumber}",
            pageNumber,
            centerLine,
            thickness,
            confidence)
        {
            SourceRegionId = sourceRegionId,
            DetectionKind = WallDetectionKind.ParallelLinePair,
            SourcePrimitiveIds = pair.First.SourcePrimitiveIds
                .Concat(pair.Second.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Evidence = evidence,
            PairEvidence = new WallPairEvidence(
                pair.FirstFaceLine(),
                pair.SecondFaceLine(),
                Math.Round(thickness, 3),
                Math.Round(pair.OverlapRatio, 3),
                Math.Round(pair.Score, 3),
                pair.First.FragmentCount,
                pair.Second.FragmentCount,
                pair.First.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray(),
                pair.Second.SourcePrimitiveIds.Distinct(StringComparer.Ordinal).ToArray()),
            LengthMeters = calibration.ToMeters(centerLine.Length, scaleGroup),
            ThicknessMillimeters = calibration.ToMillimeters(thickness, scaleGroup),
            MeasurementScaleGroupId = scaleGroup?.Id
        };
    }

    private static void AddWallPairThicknessVarianceDiagnostic(
        int pageNumber,
        SheetRegion mainRegion,
        IReadOnlyList<WallSegment> reconstructedWalls,
        ScanContext context)
    {
        if (reconstructedWalls.Count < 2)
        {
            return;
        }

        var thicknesses = reconstructedWalls
            .Select(wall => wall.PairEvidence?.FaceSeparation ?? wall.Thickness)
            .Where(thickness => thickness > 0)
            .Order()
            .ToArray();
        if (thicknesses.Length < 2)
        {
            return;
        }

        var min = thicknesses.First();
        var max = thicknesses.Last();
        var median = Median(thicknesses);
        if (median <= 0)
        {
            return;
        }

        var relativeSpread = (max - min) / median;
        var p10 = Percentile(thicknesses, 0.10);
        var p90 = Percentile(thicknesses, 0.90);
        var percentileSpread = (p90 - p10) / median;
        var highOutlierLimit = median * 2.0;
        var lowOutlierLimit = median * 0.5;
        var outlierCount = thicknesses.Count(thickness => thickness > highOutlierLimit || thickness < lowOutlierLimit);
        var outlierRatio = outlierCount / (double)thicknesses.Length;
        var rangeThreshold = Math.Max(4, context.Options.WallMergeTolerance * 2);
        var hasVariance = thicknesses.Length >= 8
            ? percentileSpread >= 0.75 && outlierRatio >= 0.10 && p90 - p10 >= rangeThreshold
            : relativeSpread >= 0.45 && max - min >= rangeThreshold;
        if (!hasVariance)
        {
            return;
        }

        context.AddDiagnostic(
            "walls.parallel_pair_thickness_variance",
            DiagnosticSeverity.Warning,
            "walls",
            "Reconstructed double-line wall pairs have inconsistent face separations; review wall thickness assumptions for this page.",
            pageNumber,
            mainRegion.Bounds,
            Confidence.Medium,
            scope: DiagnosticScope.Detection,
            sourcePrimitiveIds: reconstructedWalls.SelectMany(wall => wall.SourcePrimitiveIds),
            properties: new Dictionary<string, string>
            {
                ["wallCount"] = reconstructedWalls.Count.ToString(),
                ["minFaceSeparation"] = min.ToString("0.###"),
                ["medianFaceSeparation"] = median.ToString("0.###"),
                ["maxFaceSeparation"] = max.ToString("0.###"),
                ["p10FaceSeparation"] = p10.ToString("0.###"),
                ["p90FaceSeparation"] = p90.ToString("0.###"),
                ["relativeSpread"] = relativeSpread.ToString("0.###"),
                ["percentileSpread"] = percentileSpread.ToString("0.###"),
                ["outlierCount"] = outlierCount.ToString(),
                ["outlierRatio"] = outlierRatio.ToString("0.###"),
                ["sourceRegionId"] = mainRegion.Id
            });
    }

    private static void AddWallPairSeparationProfileDiagnostic(
        int pageNumber,
        string sourceRegionId,
        string runKind,
        WallPairSeparationProfile profile,
        int reconstructedPairCount,
        ScanContext context)
    {
        if (!profile.IsEstablished || reconstructedPairCount == 0)
        {
            return;
        }

        context.AddDiagnostic(
            "walls.parallel_pairs.separation_profile",
            DiagnosticSeverity.Info,
            "walls",
            "A dominant double-line wall face separation was used to avoid reconstructing outlier-width wall pairs.",
            pageNumber,
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Detection,
            properties: new Dictionary<string, string>
            {
                ["runKind"] = runKind,
                ["sampleCount"] = profile.SampleCount.ToString(),
                ["dominantFaceSeparation"] = profile.DominantSeparation.ToString("0.###"),
                ["maxTrustedFaceSeparation"] = profile.MaxTrustedSeparation.ToString("0.###"),
                ["reconstructedPairCount"] = reconstructedPairCount.ToString(),
                ["sourceRegionId"] = sourceRegionId
            });
    }

    private static double Median(IReadOnlyList<double> sortedValues)
    {
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2.0
            : sortedValues[middle];
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var position = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = position - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * weight);
    }

    private static PlanLineSegment AxisFaceLine(
        WallOrientation orientation,
        double start,
        double end,
        double coordinate) =>
        orientation == WallOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(start, coordinate), new PlanPoint(end, coordinate))
            : new PlanLineSegment(new PlanPoint(coordinate, start), new PlanPoint(coordinate, end));

    private static IEnumerable<WallPairRun> ReconstructWallPairs(
        IReadOnlyList<AxisRun> runs,
        ScannerOptions options,
        ISet<AxisRun> consumed,
        WallPairSeparationProfile profile)
    {
        var ordered = runs
            .OrderBy(run => run.Coordinate)
            .ThenBy(run => run.Start)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            var run = ordered[index];
            if (consumed.Contains(run))
            {
                continue;
            }

            var match = ordered
                .Where(candidate => !ReferenceEquals(candidate, run) && !consumed.Contains(candidate))
                .Select(candidate => WallPairRun.TryCreate(run, candidate, options, profile))
                .Where(pair => pair is not null)
                .Select(pair => pair!)
                .OrderByDescending(pair => pair.Score)
                .FirstOrDefault();

            if (match is null)
            {
                continue;
            }

            consumed.Add(match.First);
            consumed.Add(match.Second);
            yield return match;
        }
    }

    private static IEnumerable<NonAxisWallPairRun> ReconstructNonAxisWallPairs(
        IReadOnlyList<NonAxisRun> runs,
        ScannerOptions options,
        ISet<NonAxisRun> consumed,
        WallPairSeparationProfile profile)
    {
        var ordered = runs
            .OrderBy(run => run.AngleRadians)
            .ThenBy(run => run.NormalCoordinate)
            .ThenBy(run => run.Start)
            .ToArray();

        for (var index = 0; index < ordered.Length; index++)
        {
            var run = ordered[index];
            if (consumed.Contains(run))
            {
                continue;
            }

            var match = ordered
                .Where(candidate => !ReferenceEquals(candidate, run) && !consumed.Contains(candidate))
                .Select(candidate => NonAxisWallPairRun.TryCreate(run, candidate, options, profile))
                .Where(pair => pair is not null)
                .Select(pair => pair!)
                .OrderByDescending(pair => pair.Score)
                .FirstOrDefault();

            if (match is null)
            {
                continue;
            }

            consumed.Add(match.First);
            consumed.Add(match.Second);
            yield return match;
        }
    }

    private static IEnumerable<AxisRun> MergeAxisRuns(IEnumerable<WallSeed> seeds, ScannerOptions options)
    {
        var runs = seeds
            .Select(AxisRun.FromSeed)
            .OrderBy(run => run.Coordinate)
            .ThenBy(run => run.Start)
            .ToList();

        var merged = new List<AxisRun>();

        foreach (var run in runs)
        {
            var match = merged.FirstOrDefault(existing =>
                existing.Orientation == run.Orientation
                && Math.Abs(existing.Coordinate - run.Coordinate) <= options.WallMergeTolerance
                && run.Start <= existing.End + Math.Max(options.WallMergeTolerance, options.MaxWallFragmentGap)
                && run.End >= existing.Start - Math.Max(options.WallMergeTolerance, options.MaxWallFragmentGap));

            if (match is null)
            {
                merged.Add(run);
                continue;
            }

            if (match.IsDuplicateOf(run, options))
            {
                match.MergeDuplicate(run);
            }
            else
            {
                match.MergeFragment(run);
            }
        }

        return merged.Where(run => run.End - run.Start >= options.MinWallLength);
    }

    private static IEnumerable<NonAxisRun> MergeNonAxisRuns(IEnumerable<WallSeed> seeds, ScannerOptions options)
    {
        var runs = seeds
            .Select(NonAxisRun.FromSeed)
            .OrderBy(run => run.AngleRadians)
            .ThenBy(run => run.NormalCoordinate)
            .ThenBy(run => run.Start)
            .ToList();

        var merged = new List<NonAxisRun>();

        foreach (var run in runs)
        {
            var match = merged.FirstOrDefault(existing =>
                DirectionAngleDelta(existing.Direction, run.Direction) <= options.GeometryTolerance.AngleRadians
                && Math.Abs(existing.NormalCoordinate - run.NormalCoordinate) <= options.WallMergeTolerance
                && run.Start <= existing.End + Math.Max(options.WallMergeTolerance, options.MaxWallFragmentGap)
                && run.End >= existing.Start - Math.Max(options.WallMergeTolerance, options.MaxWallFragmentGap));

            if (match is null)
            {
                merged.Add(run);
                continue;
            }

            if (match.IsDuplicateOf(run, options))
            {
                match.MergeDuplicate(run);
            }
            else
            {
                match.MergeFragment(run);
            }
        }

        return merged.Where(run => run.End - run.Start >= options.MinWallLength);
    }

    private static double ResolveThickness(double strokeWidth, ScannerOptions options) =>
        Math.Max(options.DefaultWallThickness, strokeWidth <= 0 ? 0 : strokeWidth);

    private static Confidence AxisRunConfidence(AxisRun run, double length, ScannerOptions options)
    {
        var maximum = run.FragmentCount > 1 ? 0.9 : 0.88;
        return RunConfidence(run.FragmentCount, run.TotalGapLength, length, options, maximum);
    }

    private static Confidence RunConfidence(
        int fragmentCount,
        double totalGapLength,
        double length,
        ScannerOptions options,
        double maximum)
    {
        var value = Math.Min(maximum, 0.45 + (length / (options.MinWallLength * 8)));
        if (fragmentCount > 1)
        {
            value += 0.04;
        }

        if (totalGapLength > options.WallMergeTolerance)
        {
            var gapRatio = totalGapLength / Math.Max(1, length);
            value -= Math.Min(0.20, gapRatio * 0.75);
        }

        if (fragmentCount > 6)
        {
            value -= 0.04;
        }

        return new Confidence(Math.Clamp(value, 0.35, maximum));
    }

    private static IReadOnlyList<string> AxisRunEvidence(AxisRun run)
    {
        var evidence = new List<string>
        {
            run.FragmentCount > 1 ? "merged collinear wall fragments" : "single wall-length vector run"
        };
        AddFragmentEvidence(evidence, "run", run);
        AddDuplicateEvidence(evidence, "run", run);
        AddLayerEvidence(evidence, run);
        return evidence;
    }

    private static void AddFragmentEvidence(ICollection<string> evidence, string label, AxisRun run)
    {
        if (run.FragmentCount <= 1)
        {
            return;
        }

        evidence.Add($"{label} merged {run.FragmentCount} fragments");
        if (run.TotalGapLength > 0)
        {
            evidence.Add($"{label} healed {Math.Round(run.TotalGapLength, 3)} drawing units of gaps; max gap {Math.Round(run.MaxGapLength, 3)}");
        }
    }

    private static void AddDuplicateEvidence(ICollection<string> evidence, string label, AxisRun run)
    {
        if (run.DuplicatePrimitiveCount <= 0)
        {
            return;
        }

        evidence.Add($"{label} collapsed {run.DuplicatePrimitiveCount} duplicate or near-duplicate wall line primitive(s)");
    }

    private static void AddLayerEvidence(ICollection<string> evidence, AxisRun run)
    {
        foreach (var item in run.LayerEvidence.Distinct(StringComparer.Ordinal))
        {
            evidence.Add(item);
        }
    }

    private static void AddFragmentEvidence(ICollection<string> evidence, string label, NonAxisRun run)
    {
        if (run.FragmentCount <= 1)
        {
            return;
        }

        evidence.Add($"{label} merged {run.FragmentCount} fragments");
        if (run.TotalGapLength > 0)
        {
            evidence.Add($"{label} healed {Math.Round(run.TotalGapLength, 3)} drawing units of gaps; max gap {Math.Round(run.MaxGapLength, 3)}");
        }
    }

    private static void AddDuplicateEvidence(ICollection<string> evidence, string label, NonAxisRun run)
    {
        if (run.DuplicatePrimitiveCount <= 0)
        {
            return;
        }

        evidence.Add($"{label} collapsed {run.DuplicatePrimitiveCount} duplicate or near-duplicate wall line primitive(s)");
    }

    private static void AddLayerEvidence(ICollection<string> evidence, NonAxisRun run)
    {
        foreach (var item in run.LayerEvidence.Distinct(StringComparer.Ordinal))
        {
            evidence.Add(item);
        }
    }

    private static double DirectionAngleDelta(PlanVector first, PlanVector second)
    {
        var dot = Math.Clamp(first.Dot(second), -1.0, 1.0);
        return Math.Acos(dot);
    }

    private static double Dot(PlanPoint point, PlanVector vector) =>
        (point.X * vector.X) + (point.Y * vector.Y);

    private static PlanPoint FromBasis(PlanVector direction, PlanVector normal, double projection, double normalCoordinate) =>
        new(
            (direction.X * projection) + (normal.X * normalCoordinate),
            (direction.Y * projection) + (normal.Y * normalCoordinate));

    private enum WallOrientation
    {
        Horizontal,
        Vertical,
        Other
    }

    private sealed record WallSeed(
        PlanLineSegment Segment,
        string PrimitiveId,
        WallOrientation Orientation,
        LayerCategory LayerCategory,
        double StrokeWidth,
        IReadOnlyList<string> LayerEvidence)
    {
        public static WallSeed From(WallLineCandidate candidate, ScannerOptions options)
        {
            var tolerance = options.GeometryTolerance.Distance;
            var line = candidate.Line;
            var orientation = line.Segment.IsHorizontal(tolerance)
                ? WallOrientation.Horizontal
                : line.Segment.IsVertical(tolerance)
                    ? WallOrientation.Vertical
                    : WallOrientation.Other;

            return new WallSeed(
                line.Segment,
                line.PrimitiveId,
                orientation,
                candidate.LayerCategory,
                line.Primitive.StrokeWidth,
                candidate.LayerEvidence);
        }
    }

    private sealed record WallLineCandidate(
        PrimitiveLine Line,
        string PrimitiveId,
        bool UseForWallDetection,
        LayerCategory LayerCategory,
        string LayerName,
        Confidence LayerConfidence,
        IReadOnlyList<string> LayerEvidence)
    {
        private const double StrongLayerConfidence = 0.45;

        public static WallLineCandidate From(
            PrimitiveLine line,
            ScanContext context,
            IReadOnlySet<string> gridAxisSourceIds)
        {
            var layerName = LayerNameFor(line.Primitive);
            var sourceFormat = Clean(line.Primitive.Source.SourceFormat);
            var layer = context.LayerAnalysis.Find(layerName, sourceFormat)
                ?? context.LayerAnalysis.Find(layerName);
            var category = layer?.LikelyCategory ?? LayerCategory.Unknown;
            var confidence = layer?.Confidence ?? Confidence.Low;
            var usedByGridAxis = gridAxisSourceIds.Contains(line.PrimitiveId);
            var excluded = usedByGridAxis || (IsExcludedWallLayer(category) && confidence.Value >= StrongLayerConfidence);
            var evidence = new List<string>();

            if (usedByGridAxis)
            {
                evidence.Add("source primitive already used by structural grid-axis detection");
            }

            if (layer is not null)
            {
                evidence.Add($"layer {layer.Name} classified {category} ({confidence.Value:0.##})");
                evidence.AddRange(layer.Evidence.Select(item => $"layer evidence: {item}"));
            }
            else if (layerName != LayerAnalyzer.UnlayeredName)
            {
                evidence.Add($"layer {layerName} had no layer analysis match");
            }

            return new WallLineCandidate(
                line,
                line.PrimitiveId,
                !excluded,
                category,
                layerName,
                confidence,
                evidence);
        }

        private static bool IsExcludedWallLayer(LayerCategory category) =>
            category is LayerCategory.Door
                or LayerCategory.Window
                or LayerCategory.Room
                or LayerCategory.Dimension
                or LayerCategory.Text
                or LayerCategory.Grid
                or LayerCategory.Equipment
                or LayerCategory.Electrical
                or LayerCategory.HVAC
                or LayerCategory.Plumbing
                or LayerCategory.FireSafety;

        private static string LayerNameFor(PlanPrimitive primitive) =>
            Clean(primitive.Source.Layer)
            ?? Clean(primitive.Layer)
            ?? LayerAnalyzer.UnlayeredName;

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record CompactLineworkWallNoiseCluster(PlanRect Bounds, string[] SourcePrimitiveIds);

    private sealed record DenseOrthogonalPatternWallNoiseCluster(
        PlanRect Bounds,
        string[] SourcePrimitiveIds,
        int HorizontalLineCount,
        int VerticalLineCount,
        int IntersectionCount,
        double HorizontalMedianSpacing,
        double VerticalMedianSpacing);

    private sealed record DenseOrthogonalPatternWallRunNoiseCluster(
        PlanRect Bounds,
        string[] SourcePrimitiveIds,
        IReadOnlyList<AxisRun> Runs,
        int HorizontalLineCount,
        int VerticalLineCount,
        int IntersectionCount,
        double HorizontalMedianSpacing,
        double VerticalMedianSpacing);

    private sealed record DenseParallelPatternWallRunNoiseCluster(
        PlanRect Bounds,
        string[] SourcePrimitiveIds,
        IReadOnlyList<AxisRun> Runs,
        WallOrientation Orientation,
        int LineCount,
        double MedianSpacing);

    private sealed record DenseCrossingParallelPatternGridCluster(
        PlanRect Bounds,
        string[] SourcePrimitiveIds,
        int HorizontalLineCount,
        int VerticalLineCount,
        int IntersectionCount,
        double HorizontalMedianSpacing,
        double VerticalMedianSpacing);

    private sealed record CompactLineworkWallNoiseItem(
        string PrimitiveId,
        PlanLineSegment Segment,
        PlanRect Bounds,
        double Length,
        WallOrientation Orientation);

    private sealed record DenseOrthogonalPatternWallNoiseItem(
        string PrimitiveId,
        PlanRect Bounds,
        double Length,
        WallOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public static DenseOrthogonalPatternWallNoiseItem From(
            WallLineCandidate candidate,
            ScannerOptions options)
        {
            var segment = candidate.Line.Segment;
            var orientation = ResolveWallOrientation(segment, options);
            var coordinate = orientation == WallOrientation.Horizontal
                ? (segment.Start.Y + segment.End.Y) / 2.0
                : (segment.Start.X + segment.End.X) / 2.0;
            var start = orientation == WallOrientation.Horizontal
                ? Math.Min(segment.Start.X, segment.End.X)
                : Math.Min(segment.Start.Y, segment.End.Y);
            var end = orientation == WallOrientation.Horizontal
                ? Math.Max(segment.Start.X, segment.End.X)
                : Math.Max(segment.Start.Y, segment.End.Y);

            return new DenseOrthogonalPatternWallNoiseItem(
                candidate.PrimitiveId,
                segment.Bounds,
                segment.Length,
                orientation,
                coordinate,
                start,
                end);
        }
    }

    private sealed record DenseOrthogonalPatternWallRunNoiseItem(
        AxisRun Run,
        PlanRect Bounds,
        double Length,
        WallOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public static DenseOrthogonalPatternWallRunNoiseItem From(AxisRun run)
        {
            var bounds = run.Orientation == WallOrientation.Horizontal
                ? PlanRect.FromPoints(new PlanPoint(run.Start, run.Coordinate), new PlanPoint(run.End, run.Coordinate))
                : PlanRect.FromPoints(new PlanPoint(run.Coordinate, run.Start), new PlanPoint(run.Coordinate, run.End));

            return new DenseOrthogonalPatternWallRunNoiseItem(
                run,
                bounds,
                run.Length,
                run.Orientation,
                run.Coordinate,
                run.Start,
                run.End);
        }
    }

    private sealed class CompactLineworkWallNoiseSpatialIndex
    {
        private readonly Dictionary<Cell, List<int>> _cells;
        private readonly IReadOnlyList<CompactLineworkWallNoiseItem> _items;
        private readonly double _cellSize;

        private CompactLineworkWallNoiseSpatialIndex(
            IReadOnlyList<CompactLineworkWallNoiseItem> items,
            Dictionary<Cell, List<int>> cells,
            double cellSize)
        {
            _items = items;
            _cells = cells;
            _cellSize = cellSize;
        }

        public static CompactLineworkWallNoiseSpatialIndex Create(
            IReadOnlyList<CompactLineworkWallNoiseItem> items,
            double searchTolerance)
        {
            var cellSize = Math.Max(2, searchTolerance * 2);
            var cells = new Dictionary<Cell, List<int>>();

            for (var index = 0; index < items.Count; index++)
            {
                foreach (var cell in CellsFor(items[index].Bounds, cellSize))
                {
                    if (!cells.TryGetValue(cell, out var bucket))
                    {
                        bucket = new List<int>();
                        cells[cell] = bucket;
                    }

                    bucket.Add(index);
                }
            }

            return new CompactLineworkWallNoiseSpatialIndex(items, cells, cellSize);
        }

        public IEnumerable<int> Query(PlanRect search)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<int>();
            foreach (var cell in CellsFor(search, _cellSize))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (yielded.Add(index) && search.Intersects(_items[index].Bounds))
                    {
                        yield return index;
                    }
                }
            }
        }
    }

    private sealed class DenseOrthogonalPatternWallNoiseSpatialIndex
    {
        private readonly Dictionary<Cell, List<int>> _cells;
        private readonly IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> _items;
        private readonly double _cellSize;

        private DenseOrthogonalPatternWallNoiseSpatialIndex(
            IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> items,
            Dictionary<Cell, List<int>> cells,
            double cellSize)
        {
            _items = items;
            _cells = cells;
            _cellSize = cellSize;
        }

        public static DenseOrthogonalPatternWallNoiseSpatialIndex Create(
            IReadOnlyList<DenseOrthogonalPatternWallNoiseItem> items,
            double searchTolerance)
        {
            var cellSize = Math.Max(2, searchTolerance * 2);
            var cells = new Dictionary<Cell, List<int>>();

            for (var index = 0; index < items.Count; index++)
            {
                foreach (var cell in CellsFor(items[index].Bounds, cellSize))
                {
                    if (!cells.TryGetValue(cell, out var bucket))
                    {
                        bucket = new List<int>();
                        cells[cell] = bucket;
                    }

                    bucket.Add(index);
                }
            }

            return new DenseOrthogonalPatternWallNoiseSpatialIndex(items, cells, cellSize);
        }

        public IEnumerable<int> Query(PlanRect search)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<int>();
            foreach (var cell in CellsFor(search, _cellSize))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (yielded.Add(index) && search.Intersects(_items[index].Bounds))
                    {
                        yield return index;
                    }
                }
            }
        }
    }

    private sealed class DenseOrthogonalPatternWallRunNoiseSpatialIndex
    {
        private readonly Dictionary<Cell, List<int>> _cells;
        private readonly IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> _items;
        private readonly double _cellSize;

        private DenseOrthogonalPatternWallRunNoiseSpatialIndex(
            IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> items,
            Dictionary<Cell, List<int>> cells,
            double cellSize)
        {
            _items = items;
            _cells = cells;
            _cellSize = cellSize;
        }

        public static DenseOrthogonalPatternWallRunNoiseSpatialIndex Create(
            IReadOnlyList<DenseOrthogonalPatternWallRunNoiseItem> items,
            double searchTolerance)
        {
            var cellSize = Math.Max(2, searchTolerance * 2);
            var cells = new Dictionary<Cell, List<int>>();

            for (var index = 0; index < items.Count; index++)
            {
                foreach (var cell in CellsFor(items[index].Bounds, cellSize))
                {
                    if (!cells.TryGetValue(cell, out var bucket))
                    {
                        bucket = new List<int>();
                        cells[cell] = bucket;
                    }

                    bucket.Add(index);
                }
            }

            return new DenseOrthogonalPatternWallRunNoiseSpatialIndex(items, cells, cellSize);
        }

        public IEnumerable<int> Query(PlanRect search)
        {
            if (search.IsEmpty || _cells.Count == 0)
            {
                yield break;
            }

            var yielded = new HashSet<int>();
            foreach (var cell in CellsFor(search, _cellSize))
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                {
                    continue;
                }

                foreach (var index in bucket)
                {
                    if (yielded.Add(index) && search.Intersects(_items[index].Bounds))
                    {
                        yield return index;
                    }
                }
            }
        }
    }

    private static IEnumerable<Cell> CellsFor(PlanRect bounds, double cellSize)
    {
        var minX = CellCoordinate(bounds.Left, cellSize);
        var maxX = CellCoordinate(bounds.Right, cellSize);
        var minY = CellCoordinate(bounds.Top, cellSize);
        var maxY = CellCoordinate(bounds.Bottom, cellSize);

        for (var x = minX; x <= maxX; x++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                yield return new Cell(x, y);
            }
        }
    }

    private static int CellCoordinate(double value, double cellSize) =>
        (int)Math.Floor(value / cellSize);

    private readonly record struct Cell(int X, int Y);

    private sealed class AxisRun
    {
        private AxisRun(
            WallOrientation orientation,
            double coordinate,
            double start,
            double end,
            double strokeWidth,
            IReadOnlyList<string> sourcePrimitiveIds,
            IReadOnlyList<string> layerEvidence)
        {
            Orientation = orientation;
            Coordinate = coordinate;
            Start = start;
            End = end;
            StrokeWidth = strokeWidth;
            SourcePrimitiveIds = sourcePrimitiveIds.ToList();
            LayerEvidence = layerEvidence.ToList();
        }

        public WallOrientation Orientation { get; }

        public double Coordinate { get; private set; }

        public double Start { get; private set; }

        public double End { get; private set; }

        public double Length => End - Start;

        public int FragmentCount { get; private set; } = 1;

        public double TotalGapLength { get; private set; }

        public double MaxGapLength { get; private set; }

        public int DuplicatePrimitiveCount { get; private set; }

        public double StrokeWidth { get; private set; }

        public List<string> SourcePrimitiveIds { get; }

        public List<string> LayerEvidence { get; }

        public static AxisRun FromSeed(WallSeed seed)
        {
            if (seed.Orientation == WallOrientation.Horizontal)
            {
                return new AxisRun(
                    seed.Orientation,
                    (seed.Segment.Start.Y + seed.Segment.End.Y) / 2.0,
                    Math.Min(seed.Segment.Start.X, seed.Segment.End.X),
                    Math.Max(seed.Segment.Start.X, seed.Segment.End.X),
                    seed.StrokeWidth,
                    new[] { seed.PrimitiveId },
                    seed.LayerEvidence);
            }

            return new AxisRun(
                seed.Orientation,
                (seed.Segment.Start.X + seed.Segment.End.X) / 2.0,
                Math.Min(seed.Segment.Start.Y, seed.Segment.End.Y),
                Math.Max(seed.Segment.Start.Y, seed.Segment.End.Y),
                seed.StrokeWidth,
                new[] { seed.PrimitiveId },
                seed.LayerEvidence);
        }

        public bool IsDuplicateOf(AxisRun run, ScannerOptions options)
        {
            if (Orientation != run.Orientation)
            {
                return false;
            }

            var tolerance = Math.Max(options.WallMergeTolerance, options.WallSnapTolerance);
            if (Math.Abs(Coordinate - run.Coordinate) > tolerance)
            {
                return false;
            }

            var overlap = Math.Min(End, run.End) - Math.Max(Start, run.Start);
            if (overlap <= 0)
            {
                return false;
            }

            var minimumLength = Math.Min(Length, run.Length);
            var maximumLength = Math.Max(Length, run.Length);
            if (minimumLength <= 0 || maximumLength <= 0)
            {
                return false;
            }

            var overlapRatio = overlap / minimumLength;
            var lengthBalance = minimumLength / maximumLength;
            return overlapRatio >= 0.95 && lengthBalance >= 0.9;
        }

        public void MergeFragment(AxisRun run)
        {
            var currentLength = End - Start;
            var nextLength = run.End - run.Start;
            var totalLength = Math.Max(1, currentLength + nextLength);
            var gap = GapTo(run);

            Coordinate = ((Coordinate * currentLength) + (run.Coordinate * nextLength)) / totalLength;
            Start = Math.Min(Start, run.Start);
            End = Math.Max(End, run.End);
            StrokeWidth = Math.Max(StrokeWidth, run.StrokeWidth);
            FragmentCount += run.FragmentCount;
            DuplicatePrimitiveCount += run.DuplicatePrimitiveCount;
            TotalGapLength += run.TotalGapLength + gap;
            MaxGapLength = Math.Max(Math.Max(MaxGapLength, run.MaxGapLength), gap);
            SourcePrimitiveIds.AddRange(run.SourcePrimitiveIds);
            LayerEvidence.AddRange(run.LayerEvidence);
        }

        public void MergeDuplicate(AxisRun run)
        {
            var currentLength = End - Start;
            var nextLength = run.End - run.Start;
            var totalLength = Math.Max(1, currentLength + nextLength);

            Coordinate = ((Coordinate * currentLength) + (run.Coordinate * nextLength)) / totalLength;
            Start = Math.Min(Start, run.Start);
            End = Math.Max(End, run.End);
            StrokeWidth = Math.Max(StrokeWidth, run.StrokeWidth);
            FragmentCount = Math.Max(FragmentCount, run.FragmentCount);
            DuplicatePrimitiveCount += run.FragmentCount + run.DuplicatePrimitiveCount;
            TotalGapLength = Math.Max(TotalGapLength, run.TotalGapLength);
            MaxGapLength = Math.Max(MaxGapLength, run.MaxGapLength);
            SourcePrimitiveIds.AddRange(run.SourcePrimitiveIds);
            LayerEvidence.AddRange(run.LayerEvidence);
        }

        private double GapTo(AxisRun run) =>
            Math.Max(0, Math.Max(run.Start - End, Start - run.End));
    }

    private sealed class NonAxisRun
    {
        private NonAxisRun(
            PlanVector direction,
            PlanVector normal,
            double normalCoordinate,
            double start,
            double end,
            double strokeWidth,
            IReadOnlyList<string> sourcePrimitiveIds,
            IReadOnlyList<string> layerEvidence)
        {
            Direction = direction;
            Normal = normal;
            NormalCoordinate = normalCoordinate;
            Start = start;
            End = end;
            StrokeWidth = strokeWidth;
            SourcePrimitiveIds = sourcePrimitiveIds.ToList();
            LayerEvidence = layerEvidence.ToList();
        }

        public PlanVector Direction { get; private set; }

        public PlanVector Normal { get; private set; }

        public double NormalCoordinate { get; private set; }

        public double Start { get; private set; }

        public double End { get; private set; }

        public double Length => End - Start;

        public double AngleRadians => GeometryOperations.NormalizeAngleRadians(Math.Atan2(Direction.Y, Direction.X));

        public double AngleDegrees => AngleRadians * 180.0 / Math.PI;

        public int FragmentCount { get; private set; } = 1;

        public double TotalGapLength { get; private set; }

        public double MaxGapLength { get; private set; }

        public int DuplicatePrimitiveCount { get; private set; }

        public double StrokeWidth { get; private set; }

        public List<string> SourcePrimitiveIds { get; }

        public List<string> LayerEvidence { get; }

        public static NonAxisRun FromSeed(WallSeed seed)
        {
            var direction = seed.Segment.Vector.Normalize();
            if (direction.X < 0 || (Math.Abs(direction.X) <= double.Epsilon && direction.Y < 0))
            {
                direction *= -1;
            }

            var normal = new PlanVector(-direction.Y, direction.X);
            var start = Dot(seed.Segment.Start, direction);
            var end = Dot(seed.Segment.End, direction);
            if (start > end)
            {
                (start, end) = (end, start);
            }

            return new NonAxisRun(
                direction,
                normal,
                Dot(seed.Segment.Midpoint, normal),
                start,
                end,
                seed.StrokeWidth,
                new[] { seed.PrimitiveId },
                seed.LayerEvidence);
        }

        public bool IsDuplicateOf(NonAxisRun run, ScannerOptions options)
        {
            var tolerance = Math.Max(options.WallMergeTolerance, options.WallSnapTolerance);
            if (DirectionAngleDelta(Direction, run.Direction) > options.GeometryTolerance.AngleRadians
                || Math.Abs(NormalCoordinate - run.NormalCoordinate) > tolerance)
            {
                return false;
            }

            var overlap = Math.Min(End, run.End) - Math.Max(Start, run.Start);
            if (overlap <= 0)
            {
                return false;
            }

            var minimumLength = Math.Min(Length, run.Length);
            var maximumLength = Math.Max(Length, run.Length);
            if (minimumLength <= 0 || maximumLength <= 0)
            {
                return false;
            }

            var overlapRatio = overlap / minimumLength;
            var lengthBalance = minimumLength / maximumLength;
            return overlapRatio >= 0.95 && lengthBalance >= 0.9;
        }

        public void MergeFragment(NonAxisRun run)
        {
            var currentLength = End - Start;
            var nextLength = run.End - run.Start;
            var totalLength = Math.Max(1, currentLength + nextLength);
            var gap = GapTo(run);
            var mergedDirection = new PlanVector(
                ((Direction.X * currentLength) + (run.Direction.X * nextLength)) / totalLength,
                ((Direction.Y * currentLength) + (run.Direction.Y * nextLength)) / totalLength).Normalize();

            if (mergedDirection.X < 0 || (Math.Abs(mergedDirection.X) <= double.Epsilon && mergedDirection.Y < 0))
            {
                mergedDirection *= -1;
            }

            Direction = mergedDirection;
            Normal = new PlanVector(-Direction.Y, Direction.X);
            NormalCoordinate = ((NormalCoordinate * currentLength) + (run.NormalCoordinate * nextLength)) / totalLength;
            Start = Math.Min(Start, run.Start);
            End = Math.Max(End, run.End);
            StrokeWidth = Math.Max(StrokeWidth, run.StrokeWidth);
            FragmentCount += run.FragmentCount;
            DuplicatePrimitiveCount += run.DuplicatePrimitiveCount;
            TotalGapLength += run.TotalGapLength + gap;
            MaxGapLength = Math.Max(Math.Max(MaxGapLength, run.MaxGapLength), gap);
            SourcePrimitiveIds.AddRange(run.SourcePrimitiveIds);
            LayerEvidence.AddRange(run.LayerEvidence);
        }

        public void MergeDuplicate(NonAxisRun run)
        {
            var currentLength = End - Start;
            var nextLength = run.End - run.Start;
            var totalLength = Math.Max(1, currentLength + nextLength);
            var mergedDirection = new PlanVector(
                ((Direction.X * currentLength) + (run.Direction.X * nextLength)) / totalLength,
                ((Direction.Y * currentLength) + (run.Direction.Y * nextLength)) / totalLength).Normalize();

            if (mergedDirection.X < 0 || (Math.Abs(mergedDirection.X) <= double.Epsilon && mergedDirection.Y < 0))
            {
                mergedDirection *= -1;
            }

            Direction = mergedDirection;
            Normal = new PlanVector(-Direction.Y, Direction.X);
            NormalCoordinate = ((NormalCoordinate * currentLength) + (run.NormalCoordinate * nextLength)) / totalLength;
            Start = Math.Min(Start, run.Start);
            End = Math.Max(End, run.End);
            StrokeWidth = Math.Max(StrokeWidth, run.StrokeWidth);
            FragmentCount = Math.Max(FragmentCount, run.FragmentCount);
            DuplicatePrimitiveCount += run.FragmentCount + run.DuplicatePrimitiveCount;
            TotalGapLength = Math.Max(TotalGapLength, run.TotalGapLength);
            MaxGapLength = Math.Max(MaxGapLength, run.MaxGapLength);
            SourcePrimitiveIds.AddRange(run.SourcePrimitiveIds);
            LayerEvidence.AddRange(run.LayerEvidence);
        }

        public PlanLineSegment ToLineSegment() =>
            new(
                FromBasis(Direction, Normal, Start, NormalCoordinate),
                FromBasis(Direction, Normal, End, NormalCoordinate));

        private double GapTo(NonAxisRun run) =>
            Math.Max(0, Math.Max(run.Start - End, Start - run.End));
    }

    private sealed record WallPairSeparationProfile(
        int SampleCount,
        double DominantSeparation,
        double MaxTrustedSeparation)
    {
        private const int MinimumSampleCount = 12;
        private const double MinimumDominantShare = 0.25;

        public static WallPairSeparationProfile Empty { get; } = new(0, 0, double.MaxValue);

        public bool IsEstablished => SampleCount >= MinimumSampleCount && DominantSeparation > 0;

        public bool Accepts(double separation) =>
            !IsEstablished || separation <= MaxTrustedSeparation;

        public double Score(double separation, double fallbackMaxSeparation)
        {
            if (!IsEstablished)
            {
                return 1.0 - Math.Min(1.0, separation / Math.Max(1.0, fallbackMaxSeparation));
            }

            var range = Math.Max(1.0, MaxTrustedSeparation - DominantSeparation);
            var delta = Math.Abs(separation - DominantSeparation);
            return 1.0 - Math.Min(1.0, delta / range);
        }

        public static WallPairSeparationProfile FromAxisRuns(IReadOnlyList<AxisRun> runs, ScannerOptions options)
        {
            if (runs.Count < MinimumSampleCount)
            {
                return Empty;
            }

            var samples = new List<double>();
            foreach (var group in runs.GroupBy(run => run.Orientation))
            {
                var ordered = group.ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    var nearest = ordered
                        .Where(candidate => !ReferenceEquals(candidate, ordered[index]))
                        .Select(candidate => AxisCandidateSeparation(ordered[index], candidate, options))
                        .Where(separation => separation is not null)
                        .Select(separation => separation!.Value)
                        .Order()
                        .FirstOrDefault();

                    if (nearest > 0)
                    {
                        samples.Add(nearest);
                    }
                }
            }

            return FromSamples(samples, options);
        }

        public static WallPairSeparationProfile FromNonAxisRuns(IReadOnlyList<NonAxisRun> runs, ScannerOptions options)
        {
            if (runs.Count < MinimumSampleCount)
            {
                return Empty;
            }

            var samples = new List<double>();
            for (var index = 0; index < runs.Count; index++)
            {
                var nearest = runs
                    .Where(candidate => !ReferenceEquals(candidate, runs[index]))
                    .Select(candidate => NonAxisCandidateSeparation(runs[index], candidate, options))
                    .Where(separation => separation is not null)
                    .Select(separation => separation!.Value)
                    .Order()
                    .FirstOrDefault();

                if (nearest > 0)
                {
                    samples.Add(nearest);
                }
            }

            return FromSamples(samples, options);
        }

        private static WallPairSeparationProfile FromSamples(List<double> samples, ScannerOptions options)
        {
            if (samples.Count < MinimumSampleCount)
            {
                return Empty;
            }

            samples.Sort();
            var bucketWidth = Math.Max(0.25, options.WallSnapTolerance / 4.0);
            var dominantBucket = samples
                .GroupBy(sample => Math.Round(sample / bucketWidth) * bucketWidth)
                .Select(group => new
                {
                    Center = group.Key,
                    Count = group.Count()
                })
                .OrderByDescending(group => group.Count)
                .ThenBy(group => group.Center)
                .First();

            if (dominantBucket.Count < MinimumSampleCount
                || dominantBucket.Count < samples.Count * MinimumDominantShare)
            {
                return Empty;
            }

            var cluster = samples
                .Where(sample => Math.Abs(sample - dominantBucket.Center) <= bucketWidth)
                .Order()
                .ToArray();
            var dominant = Math.Max(Median(cluster), options.DefaultWallThickness);
            var maxTrusted = Math.Min(
                Math.Max(options.MinWallPairSeparation, options.MaxWallPairSeparation),
                Math.Max(dominant * 2.0, dominant + Math.Max(3.0, options.DefaultWallThickness)));

            return new WallPairSeparationProfile(samples.Count, dominant, maxTrusted);
        }

        private static double? AxisCandidateSeparation(AxisRun first, AxisRun second, ScannerOptions options)
        {
            if (first.Orientation != second.Orientation)
            {
                return null;
            }

            var separation = Math.Abs(first.Coordinate - second.Coordinate);
            return CandidateSeparation(
                separation,
                first.Start,
                first.End,
                first.Length,
                second.Start,
                second.End,
                second.Length,
                options);
        }

        private static double? NonAxisCandidateSeparation(NonAxisRun first, NonAxisRun second, ScannerOptions options)
        {
            if (DirectionAngleDelta(first.Direction, second.Direction) > options.GeometryTolerance.AngleRadians)
            {
                return null;
            }

            var separation = Math.Abs(first.NormalCoordinate - second.NormalCoordinate);
            return CandidateSeparation(
                separation,
                first.Start,
                first.End,
                first.Length,
                second.Start,
                second.End,
                second.Length,
                options);
        }

        private static double? CandidateSeparation(
            double separation,
            double firstStart,
            double firstEnd,
            double firstLength,
            double secondStart,
            double secondEnd,
            double secondLength,
            ScannerOptions options)
        {
            var minSeparation = Math.Max(0.1, options.MinWallPairSeparation);
            var maxSeparation = Math.Max(minSeparation, options.MaxWallPairSeparation);
            if (separation < minSeparation || separation > maxSeparation)
            {
                return null;
            }

            var overlap = Math.Min(firstEnd, secondEnd) - Math.Max(firstStart, secondStart);
            if (overlap < options.MinWallLength)
            {
                return null;
            }

            var overlapRatio = overlap / Math.Max(1, Math.Min(firstLength, secondLength));
            return overlapRatio >= options.MinWallPairOverlapRatio
                ? separation
                : null;
        }
    }

    private sealed record WallPairRun(
        AxisRun First,
        AxisRun Second,
        WallOrientation Orientation,
        double Start,
        double End,
        double OverlapRatio,
        double Score)
    {
        public static WallPairRun? TryCreate(
            AxisRun first,
            AxisRun second,
            ScannerOptions options,
            WallPairSeparationProfile profile)
        {
            if (first.Orientation != second.Orientation)
            {
                return null;
            }

            var separation = Math.Abs(first.Coordinate - second.Coordinate);
            var minSeparation = Math.Max(0.1, options.MinWallPairSeparation);
            var maxSeparation = Math.Max(minSeparation, options.MaxWallPairSeparation);
            if (separation < minSeparation || separation > maxSeparation)
            {
                return null;
            }

            if (!profile.Accepts(separation))
            {
                return null;
            }

            var start = Math.Max(first.Start, second.Start);
            var end = Math.Min(first.End, second.End);
            var overlap = end - start;
            if (overlap < options.MinWallLength)
            {
                return null;
            }

            var overlapRatio = overlap / Math.Max(1, Math.Min(first.Length, second.Length));
            if (overlapRatio < options.MinWallPairOverlapRatio)
            {
                return null;
            }

            var separationScore = profile.Score(separation, maxSeparation);
            var lengthBalance = Math.Min(first.Length, second.Length) / Math.Max(first.Length, second.Length);
            var score = (overlapRatio * 0.58) + (lengthBalance * 0.26) + (separationScore * 0.16);

            return new WallPairRun(
                first,
                second,
                first.Orientation,
                start,
                end,
                overlapRatio,
                score);
        }
    }

    private sealed record NonAxisWallPairRun(
        NonAxisRun First,
        NonAxisRun Second,
        double Start,
        double End,
        double CenterNormalCoordinate,
        double OverlapRatio,
        double Score)
    {
        public double AngleDegrees => First.AngleDegrees;

        public PlanLineSegment ToCenterLine() =>
            new(
                FromBasis(First.Direction, First.Normal, Start, CenterNormalCoordinate),
                FromBasis(First.Direction, First.Normal, End, CenterNormalCoordinate));

        public PlanLineSegment FirstFaceLine() =>
            new(
                FromBasis(First.Direction, First.Normal, Start, First.NormalCoordinate),
                FromBasis(First.Direction, First.Normal, End, First.NormalCoordinate));

        public PlanLineSegment SecondFaceLine() =>
            new(
                FromBasis(First.Direction, First.Normal, Start, Second.NormalCoordinate),
                FromBasis(First.Direction, First.Normal, End, Second.NormalCoordinate));

        public static NonAxisWallPairRun? TryCreate(
            NonAxisRun first,
            NonAxisRun second,
            ScannerOptions options,
            WallPairSeparationProfile profile)
        {
            if (DirectionAngleDelta(first.Direction, second.Direction) > options.GeometryTolerance.AngleRadians)
            {
                return null;
            }

            var separation = Math.Abs(first.NormalCoordinate - second.NormalCoordinate);
            var minSeparation = Math.Max(0.1, options.MinWallPairSeparation);
            var maxSeparation = Math.Max(minSeparation, options.MaxWallPairSeparation);
            if (separation < minSeparation || separation > maxSeparation)
            {
                return null;
            }

            if (!profile.Accepts(separation))
            {
                return null;
            }

            var start = Math.Max(first.Start, second.Start);
            var end = Math.Min(first.End, second.End);
            var overlap = end - start;
            if (overlap < options.MinWallLength)
            {
                return null;
            }

            var overlapRatio = overlap / Math.Max(1, Math.Min(first.Length, second.Length));
            if (overlapRatio < options.MinWallPairOverlapRatio)
            {
                return null;
            }

            var separationScore = profile.Score(separation, maxSeparation);
            var lengthBalance = Math.Min(first.Length, second.Length) / Math.Max(first.Length, second.Length);
            var angleScore = 1.0 - Math.Min(1.0, DirectionAngleDelta(first.Direction, second.Direction) / Math.Max(0.001, options.GeometryTolerance.AngleRadians));
            var score = (overlapRatio * 0.52) + (lengthBalance * 0.24) + (separationScore * 0.14) + (angleScore * 0.10);

            return new NonAxisWallPairRun(
                first,
                second,
                start,
                end,
                (first.NormalCoordinate + second.NormalCoordinate) / 2.0,
                overlapRatio,
                score);
        }
    }
}
