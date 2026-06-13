using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed partial class DimensionAnalysisStage : IPipelineStage
{
    private const double MillimetersPerInch = 25.4;
    private const double ExpectedLengthSoftTolerance = 0.12;
    private const double ExpectedLengthUnlayeredRejectTolerance = 0.18;
    private const double ExpectedLengthTrustedContextRejectTolerance = 0.35;

    public string Name => "dimensions";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dimensionTexts = EnumerateDimensionTexts(context, page).ToArray();
            var pageDimensions = new List<DimensionAnnotation>();
            if (dimensionTexts.Length > 0)
            {
                var rawLines = EnumerateLines(context, page).ToArray();
                var linePools = BuildDimensionLineCandidatePools(context, page, rawLines, dimensionTexts);

                foreach (var candidate in dimensionTexts)
                {
                    var match = FindNearbyDimensionLine(
                        candidate.Text,
                        candidate.Parsed,
                        linePools[candidate.SourceId],
                        candidate.Region,
                        page,
                        candidate.InDimensionContext,
                        context.Calibration,
                        context.Options);
                    var dimension = CreateDimension(
                        context,
                        page,
                        candidate.Text,
                        candidate.Parsed,
                        candidate.SourceIds,
                        candidate.Region,
                        match,
                        candidate.InDimensionContext,
                        pageDimensions.Count + 1);

                    if (dimension.Confidence.Value < 0.38)
                    {
                        continue;
                    }

                    pageDimensions.Add(dimension);
                }
            }

            var rawDimensionCount = pageDimensions.Count;
            pageDimensions = SuppressDuplicateMatchedDimensions(context, page, pageDimensions);
            pageDimensions = SuppressConflictingMatchedDimensions(context, page, pageDimensions);

            context.Dimensions.AddRange(pageDimensions);

            if (pageDimensions.Count > 0)
            {
                if (rawDimensionCount > pageDimensions.Count)
                {
                    context.AddDiagnostic(
                        "dimensions.duplicates_suppressed",
                        DiagnosticSeverity.Info,
                        Name,
                        "Duplicate dimension annotations with the same text and matched dimension line were suppressed.",
                        page.Number,
                        PlanRect.Union(pageDimensions.Select(dimension => dimension.Bounds)).ClampTo(page.Bounds),
                        Confidence.Medium,
                        DiagnosticScope.Dimension,
                        pageDimensions.SelectMany(dimension => dimension.SourcePrimitiveIds),
                        new Dictionary<string, string>
                        {
                            ["rawDimensionCount"] = rawDimensionCount.ToString(CultureInfo.InvariantCulture),
                            ["keptDimensionCount"] = pageDimensions.Count.ToString(CultureInfo.InvariantCulture),
                            ["suppressedDimensionCount"] = (rawDimensionCount - pageDimensions.Count).ToString(CultureInfo.InvariantCulture)
                        });
                }

                context.AddDiagnostic(
                    "dimensions.detected",
                    DiagnosticSeverity.Info,
                    Name,
                    $"Detected {pageDimensions.Count} dimension annotation(s).",
                    page.Number,
                    PlanRect.Union(pageDimensions.Select(dimension => dimension.Bounds)).ClampTo(page.Bounds),
                    new Confidence(Math.Min(0.9, pageDimensions.Average(dimension => dimension.Confidence.Value))),
                    DiagnosticScope.Dimension,
                    pageDimensions.SelectMany(dimension => dimension.SourcePrimitiveIds),
                    new Dictionary<string, string>
                    {
                        ["matchedLineCount"] = pageDimensions.Count(dimension => dimension.DimensionLine is not null).ToString(CultureInfo.InvariantCulture),
                        ["unitHintCount"] = pageDimensions.Count(dimension => dimension.DimensionLine is null).ToString(CultureInfo.InvariantCulture),
                        ["witnessLineCount"] = pageDimensions.Sum(WitnessLineSourceCount).ToString(CultureInfo.InvariantCulture),
                        ["alignedCount"] = pageDimensions.Count(dimension => dimension.Orientation == DimensionOrientation.Aligned).ToString(CultureInfo.InvariantCulture)
                    });
            }
            else if (context.SheetRegions.Any(region => region.PageNumber == page.Number && region.Kind == RegionKind.Dimensions))
            {
                var dimensionRegion = context.SheetRegions.First(region => region.PageNumber == page.Number && region.Kind == RegionKind.Dimensions);
                context.AddDiagnostic(
                    "dimensions.region_unresolved",
                    DiagnosticSeverity.Warning,
                    Name,
                    "A dimension annotation region was detected, but no parseable dimension text was extracted.",
                    page.Number,
                    dimensionRegion.Bounds,
                    Confidence.Low,
                    DiagnosticScope.Dimension,
                    dimensionRegion.SourcePrimitiveIds,
                    new Dictionary<string, string> { ["regionId"] = dimensionRegion.Id });
            }
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<DimensionTextCandidate> EnumerateDimensionTexts(
        ScanContext context,
        PlanPage page)
    {
        var textItems = new List<DimensionTextItem>();
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not TextPrimitive text)
            {
                continue;
            }

            var sourceId = context.PrimitiveId(page.Number, index, text);
            textItems.Add(new DimensionTextItem(text, sourceId));

            var rawText = NormalizeText(text.Text);
            if (!TryParseDimensionText(rawText, out var parsed))
            {
                continue;
            }

            var region = FindSourceRegion(context.SheetRegions, page.Number, text.Bounds.Center);
            yield return new DimensionTextCandidate(
                text,
                parsed,
                sourceId,
                new[] { sourceId },
                region,
                IsDimensionContext(text, region));
        }

        foreach (var merged in EnumerateMergedMetricDimensionTexts(context, page, textItems))
        {
            yield return merged;
        }
    }

    private static IEnumerable<DimensionTextCandidate> EnumerateMergedMetricDimensionTexts(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<DimensionTextItem> textItems)
    {
        var ordered = textItems
            .OrderBy(item => item.Text.Bounds.Top)
            .ThenBy(item => item.Text.Bounds.Left)
            .ToArray();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < ordered.Length; index++)
        {
            var first = ordered[index];
            var firstText = NormalizeText(first.Text.Text);
            if (!IsLeadingMetricNumberFragment(firstText))
            {
                continue;
            }

            var sameLineCandidates = ordered
                .Skip(index + 1)
                .Where(candidate => IsSameTextLine(first.Text.Bounds, candidate.Text.Bounds))
                .Where(candidate => candidate.Text.Bounds.Left >= first.Text.Bounds.Right - 1)
                .Where(candidate => candidate.Text.Bounds.Left - first.Text.Bounds.Right <= MaxMetricFragmentGap(first.Text.Bounds, candidate.Text.Bounds))
                .Take(4);

            foreach (var second in sameLineCandidates)
            {
                var secondText = NormalizeText(second.Text.Text);
                if (!IsTrailingMetricNumberFragment(secondText))
                {
                    continue;
                }

                var rawText = $"{firstText} {secondText}";
                if (!TryParseDimensionText(rawText, out var parsed))
                {
                    continue;
                }

                var key = string.Join("|", new[] { first.SourceId, second.SourceId });
                if (!emitted.Add(key))
                {
                    continue;
                }

                var bounds = PlanRect.Union(first.Text.Bounds, second.Text.Bounds);
                var mergedText = new TextPrimitive(rawText, bounds)
                {
                    FontSize = new[] { first.Text.FontSize, second.Text.FontSize }
                        .Where(size => size > 0)
                        .DefaultIfEmpty(0)
                        .Average()
                };
                var sourceId = $"page:{page.Number}:dimension-text-merge:{emitted.Count}";
                var region = FindSourceRegion(context.SheetRegions, page.Number, bounds.Center);

                yield return new DimensionTextCandidate(
                    mergedText,
                    parsed,
                    sourceId,
                    new[] { first.SourceId, second.SourceId },
                    region,
                    IsDimensionContext(mergedText, region)
                        || LooksLikeDimensionLayer(first.Text)
                        || LooksLikeDimensionLayer(second.Text));
            }
        }
    }

    private static Dictionary<string, PrimitiveLineCandidate[]> BuildDimensionLineCandidatePools(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<PrimitiveLineCandidate> rawLines,
        IReadOnlyList<DimensionTextCandidate> dimensionTexts)
    {
        var limit = context.Options.MaxDimensionLineCandidatesPerPage;
        if (limit <= 0 || rawLines.Count <= limit)
        {
            return dimensionTexts.ToDictionary(
                text => text.SourceId,
                _ => rawLines.ToArray(),
                StringComparer.Ordinal);
        }

        var pools = new Dictionary<string, PrimitiveLineCandidate[]>(StringComparer.Ordinal);
        var maxLocalBeforeLimit = 0;
        foreach (var text in dimensionTexts)
        {
            var local = SelectDimensionLineCandidatesForText(context, page, rawLines, text).ToArray();
            maxLocalBeforeLimit = Math.Max(maxLocalBeforeLimit, local.Length);
            pools[text.SourceId] = local;
        }

        var uniqueRetained = pools.Values
            .SelectMany(lines => lines)
            .Select(line => $"{line.SourceId}:{LineKey(line.Segment)}")
            .Distinct(StringComparer.Ordinal)
            .Count();
        var minLocal = pools.Values.Select(lines => lines.Length).DefaultIfEmpty(0).Min();
        var maxLocal = pools.Values.Select(lines => lines.Length).DefaultIfEmpty(0).Max();

        context.AddDiagnostic(
            "dimensions.text_candidate_pool.pruned",
            DiagnosticSeverity.Info,
            "dimensions",
            "Dimension line candidates were localized around parseable dimension text before matching.",
            page.Number,
            page.Bounds,
            Confidence.Medium,
            DiagnosticScope.Dimension,
            properties: new Dictionary<string, string>
            {
                ["lineCandidateCountBeforePruning"] = rawLines.Count.ToString(CultureInfo.InvariantCulture),
                ["dimensionTextCandidateCount"] = dimensionTexts.Count.ToString(CultureInfo.InvariantCulture),
                ["uniqueRetainedLineCandidateCount"] = uniqueRetained.ToString(CultureInfo.InvariantCulture),
                ["minLineCandidatesPerText"] = minLocal.ToString(CultureInfo.InvariantCulture),
                ["maxLineCandidatesPerText"] = maxLocal.ToString(CultureInfo.InvariantCulture),
                ["maxLineCandidatesPerTextBeforeLimit"] = maxLocalBeforeLimit.ToString(CultureInfo.InvariantCulture),
                ["localLimitAppliedCount"] = "0",
                ["configuredGlobalCandidateLimit"] = limit.ToString(CultureInfo.InvariantCulture)
            });

        return pools;
    }

    private static IEnumerable<PrimitiveLineCandidate> SelectDimensionLineCandidatesForText(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<PrimitiveLineCandidate> rawLines,
        DimensionTextCandidate text)
    {
        var searchDistance = DimensionLineSearchDistance(page);
        var expectedDrawingLength = ResolveExpectedDrawingLength(
            text.Parsed,
            text.Text,
            text.Region,
            page,
            context.Calibration);
        var radius = DimensionTextCandidateRadius(page, searchDistance, expectedDrawingLength);
        var neighborhood = text.Text.Bounds.Inflate(radius);
        var textCenter = text.Text.Bounds.Center;

        foreach (var line in rawLines)
        {
            if (line.Segment.Length < 4 && !LooksLikeDimensionLayer(line.Primitive))
            {
                continue;
            }

            if (LooksLikeDimensionLayer(line.Primitive))
            {
                yield return line;
                continue;
            }

            var lineBounds = line.Segment.Bounds.Inflate(8);
            if (text.Region?.Kind == RegionKind.Dimensions && text.Region.Bounds.Intersects(lineBounds))
            {
                yield return line;
                continue;
            }

            if (!neighborhood.Intersects(lineBounds))
            {
                continue;
            }

            if (line.Segment.DistanceToPoint(textCenter) <= radius * 1.15)
            {
                yield return line;
            }
        }
    }

    private static double DimensionTextCandidateRadius(
        PlanPage page,
        double searchDistance,
        double? expectedDrawingLength)
    {
        var pageSpan = Math.Min(page.Size.Width, page.Size.Height);
        var maximum = Math.Max(searchDistance, pageSpan * 0.35);
        var radius = expectedDrawingLength is > 0
            ? (expectedDrawingLength.Value / 2.0) + searchDistance
            : Math.Max(searchDistance, pageSpan * 0.25);
        return Math.Min(maximum, Math.Max(searchDistance, radius));
    }

    private static List<DimensionAnnotation> SuppressDuplicateMatchedDimensions(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<DimensionAnnotation> dimensions)
    {
        if (dimensions.Count <= 1)
        {
            return dimensions.ToList();
        }

        var result = new List<DimensionAnnotation>();
        var originalOrder = dimensions
            .Select((dimension, index) => new { dimension.Id, Index = index })
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        foreach (var group in dimensions
                     .GroupBy(DuplicateKey)
                     .OrderBy(group => originalOrder[group.First().Id]))
        {
            if (group.Key is null || group.Count() == 1)
            {
                result.AddRange(group);
                continue;
            }

            var groupItems = group.ToArray();
            var keeper = groupItems
                .OrderByDescending(dimension => dimension.Confidence.Value)
                .ThenBy(dimension => dimension.Bounds.Area)
                .First();
            var combinedSourceIds = groupItems
                .SelectMany(dimension => dimension.SourcePrimitiveIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var duplicateTextIds = groupItems
                .Select(dimension => dimension.SourcePrimitiveIds.FirstOrDefault())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var evidence = keeper.Evidence
                .Concat(new[]
                {
                    $"Suppressed {groupItems.Length - 1} duplicate dimension annotation(s) on the same matched line: "
                    + string.Join(", ", duplicateTextIds)
                })
                .ToArray();

            result.Add(keeper with
            {
                Bounds = PlanRect.Union(groupItems.Select(dimension => dimension.Bounds)).ClampTo(page.Bounds),
                SourcePrimitiveIds = combinedSourceIds,
                Evidence = evidence
            });
        }

        return result
            .OrderBy(dimension => originalOrder[dimension.Id])
            .ToList();

        static DimensionDuplicateKey? DuplicateKey(DimensionAnnotation dimension)
        {
            if (dimension.DimensionLine is not { } line)
            {
                return null;
            }

            return new DimensionDuplicateKey(
                dimension.PageNumber,
                dimension.NormalizedText,
                LineKey(line));
        }
    }

    private static List<DimensionAnnotation> SuppressConflictingMatchedDimensions(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<DimensionAnnotation> dimensions)
    {
        if (dimensions.Count <= 1)
        {
            return dimensions.ToList();
        }

        var result = new List<DimensionAnnotation>();
        var originalOrder = dimensions
            .Select((dimension, index) => new { dimension.Id, Index = index })
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        var suppressed = new List<DimensionAnnotation>();
        foreach (var group in dimensions
                     .GroupBy(ConflictKey)
                     .OrderBy(group => originalOrder[group.First().Id]))
        {
            if (group.Key is null || group.Select(dimension => dimension.NormalizedText).Distinct(StringComparer.Ordinal).Count() == 1)
            {
                result.AddRange(group);
                continue;
            }

            var groupItems = group.ToArray();
            var keeper = groupItems
                .OrderBy(dimension => CalibrationRelativeDifference(dimension, context.Calibration) ?? double.MaxValue)
                .ThenByDescending(dimension => dimension.Confidence.Value)
                .ThenBy(dimension => dimension.Bounds.Area)
                .First();
            var rejected = groupItems
                .Where(dimension => dimension.Id != keeper.Id)
                .ToArray();
            suppressed.AddRange(rejected);

            result.Add(keeper with
            {
                Evidence = keeper.Evidence
                    .Concat(new[]
                    {
                        "Suppressed conflicting dimension annotation(s) on the same matched line: "
                        + string.Join(", ", rejected.Select(dimension => $"{dimension.NormalizedText} ({dimension.SourcePrimitiveIds.FirstOrDefault() ?? dimension.Id})"))
                    })
                    .ToArray()
            });
        }

        if (suppressed.Count > 0)
        {
            context.AddDiagnostic(
                "dimensions.same_line_conflicts_suppressed",
                DiagnosticSeverity.Info,
                "dimensions",
                "Conflicting dimension annotations matched to the same dimension line were suppressed.",
                page.Number,
                PlanRect.Union(result.Select(dimension => dimension.Bounds)).ClampTo(page.Bounds),
                Confidence.Medium,
                DiagnosticScope.Dimension,
                suppressed.SelectMany(dimension => dimension.SourcePrimitiveIds),
                new Dictionary<string, string>
                {
                    ["keptDimensionCount"] = result.Count.ToString(CultureInfo.InvariantCulture),
                    ["suppressedDimensionCount"] = suppressed.Count.ToString(CultureInfo.InvariantCulture),
                    ["conflictGroupCount"] = suppressed.GroupBy(dimension => dimension.DimensionLine is null ? dimension.Id : LineKey(dimension.DimensionLine.Value)).Count().ToString(CultureInfo.InvariantCulture),
                    ["suppressedDimensionIds"] = string.Join(",", suppressed.Select(dimension => dimension.Id))
                });
        }

        return result
            .OrderBy(dimension => originalOrder[dimension.Id])
            .ToList();

        static DimensionLineConflictKey? ConflictKey(DimensionAnnotation dimension)
        {
            if (dimension.DimensionLine is not { } line)
            {
                return null;
            }

            return new DimensionLineConflictKey(
                dimension.PageNumber,
                LineKey(line));
        }
    }

    private static double? CalibrationRelativeDifference(DimensionAnnotation dimension, PlanCalibration calibration)
    {
        if (calibration.MillimetersPerDrawingUnit is not > 0
            || dimension.MillimetersPerDrawingUnit is not > 0)
        {
            return null;
        }

        return Math.Abs(dimension.MillimetersPerDrawingUnit.Value - calibration.MillimetersPerDrawingUnit.Value)
            / calibration.MillimetersPerDrawingUnit.Value;
    }

    private PrimitiveLineCandidate[] LimitLineCandidates(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<PrimitiveLineCandidate> lines)
    {
        var limit = context.Options.MaxDimensionLineCandidatesPerPage;
        if (limit <= 0 || lines.Count <= limit)
        {
            return lines.ToArray();
        }

        var indexed = lines
            .Select((line, index) => new IndexedLineCandidate(line, index))
            .ToArray();
        var kept = indexed
            .OrderByDescending(item => LinePriority(item.Line))
            .ThenByDescending(item => item.Line.Segment.Length)
            .ThenBy(item => item.Line.SourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Index)
            .Take(limit)
            .ToArray();
        var keptIndexes = kept.Select(item => item.Index).ToHashSet();
        var limited = kept.Select(item => item.Line).ToArray();
        var skipped = indexed
            .Where(item => !keptIndexes.Contains(item.Index))
            .Select(item => item.Line)
            .ToArray();

        context.AddDiagnostic(
            "dimensions.candidate_limit_applied",
            DiagnosticSeverity.Warning,
            Name,
            "Dimension line candidate count exceeded the configured per-page limit; dimension-layer and longest candidates were kept.",
            page.Number,
            page.Bounds,
            Confidence.Medium,
            DiagnosticScope.Dimension,
            skipped.Select(line => line.SourceId).Take(50),
            new Dictionary<string, string>
            {
                ["lineCandidateCountBeforeLimit"] = lines.Count.ToString(CultureInfo.InvariantCulture),
                ["keptLineCandidateCount"] = limited.Length.ToString(CultureInfo.InvariantCulture),
                ["skippedLineCandidateCount"] = skipped.Length.ToString(CultureInfo.InvariantCulture),
                ["maxDimensionLineCandidatesPerPage"] = limit.ToString(CultureInfo.InvariantCulture),
                ["sampledSkippedSourcePrimitiveIds"] = string.Join(",", skipped.Select(line => line.SourceId).Take(10))
            });

        return limited;
    }

    private static int LinePriority(PrimitiveLineCandidate line) =>
        LooksLikeDimensionLayer(line.Primitive) ? 3 : 1;

    private sealed record IndexedLineCandidate(PrimitiveLineCandidate Line, int Index);

    private static DimensionAnnotation CreateDimension(
        ScanContext context,
        PlanPage page,
        TextPrimitive text,
        ParsedDimensionText parsed,
        IReadOnlyList<string> textSourceIds,
        SheetRegion? region,
        DimensionLineMatch? match,
        bool inDimensionContext,
        int pageDimensionNumber)
    {
        var sourceIds = new List<string>(textSourceIds);
        var evidence = new List<string>
        {
            $"Parsed dimension text as {FormatMillimeters(parsed.Millimeters)} mm.",
            $"Source text: {parsed.RawText}"
        };

        var bounds = text.Bounds;
        double? drawingLength = null;
        double? millimetersPerDrawingUnit = null;
        var orientation = DimensionOrientation.Unknown;

        if (match is not null)
        {
            sourceIds.Add(match.Line.SourceId);
            sourceIds.AddRange(match.WitnessLines.Select(line => line.SourceId));
            bounds = DimensionReviewBounds(page, text.Bounds, match.Line.Segment);
            drawingLength = match.Line.Segment.Length;
            millimetersPerDrawingUnit = match.Line.Segment.Length > 0
                ? parsed.Millimeters / match.Line.Segment.Length
                : null;
            orientation = ResolveOrientation(match.Line.Segment);
            evidence.Add($"Matched nearby {orientation.ToString().ToLowerInvariant()} dimension line {match.Line.SourceId}.");
            evidence.Add($"Matched drawing length: {Math.Round(match.Line.Segment.Length, 3).ToString(CultureInfo.InvariantCulture)} units.");

            if (match.WitnessLines.Count > 0)
            {
                evidence.Add(
                    $"Matched {match.WitnessLines.Count} perpendicular witness/extension line(s): "
                    + string.Join(", ", match.WitnessLines.Select(line => line.SourceId)));
            }

            if (match.ExpectedDrawingLength is > 0)
            {
                var relativeDifference = Math.Abs(match.Line.Segment.Length - match.ExpectedDrawingLength.Value) / match.ExpectedDrawingLength.Value;
                evidence.Add(
                    $"Calibration expected drawing length: {Math.Round(match.ExpectedDrawingLength.Value, 3).ToString(CultureInfo.InvariantCulture)} units "
                    + $"({Math.Round(relativeDifference * 100, 2).ToString(CultureInfo.InvariantCulture)}% difference).");
            }
        }
        else
        {
            var expectedDrawingLength = ResolveExpectedDrawingLength(parsed, text, region, page, context.Calibration);
            evidence.Add(expectedDrawingLength is > 0
                ? "No plausible nearby dimension line matched the calibrated expected length; retained as a unit hint."
                : "No nearby dimension line was matched; retained as a unit hint.");
            orientation = text.Bounds.Width >= text.Bounds.Height
                ? DimensionOrientation.Horizontal
                : DimensionOrientation.Vertical;
        }

        var confidence = DimensionConfidence(text, region, match, inDimensionContext);

        return new DimensionAnnotation(
            $"page:{page.Number}:dimension:{pageDimensionNumber}",
            page.Number,
            DimensionKind.Linear,
            orientation,
            parsed.RawText,
            parsed.NormalizedText,
            bounds,
            parsed.Unit,
            parsed.Millimeters,
            match?.Line.Segment,
            drawingLength,
            millimetersPerDrawingUnit,
            confidence,
            region?.Id,
            sourceIds.Distinct(StringComparer.Ordinal).ToArray(),
            evidence);
    }

    private static PlanRect DimensionReviewBounds(PlanPage page, PlanRect textBounds, PlanLineSegment dimensionLine)
    {
        var padding = Math.Clamp(Math.Min(page.Size.Width, page.Size.Height) * 0.004, 3, 8);
        return PlanRect.Union(textBounds, dimensionLine.Bounds)
            .Inflate(padding)
            .ClampTo(page.Bounds);
    }

    private static bool TryParseDimensionText(string text, out ParsedDimensionText dimension)
    {
        dimension = default;
        if (LooksLikeScaleText(text) || LooksLikeAreaMeasurementText(text))
        {
            return false;
        }

        var wholeMillimeters = MetricWholeMillimeterDimensionRegex().Match(text);
        if (wholeMillimeters.Success)
        {
            var value = ParseNumber(wholeMillimeters.Groups["value"].Value.Replace(" ", string.Empty));
            if (value <= 0)
            {
                return false;
            }

            dimension = new ParsedDimensionText(
                text,
                $"{FormatNumber(value)} mm",
                PlanMeasurementUnit.Millimeter,
                value);
            return true;
        }

        var metric = MetricDimensionRegex().Match(text);
        if (metric.Success)
        {
            var value = ParseNumber(metric.Groups["value"].Value);
            var unitText = metric.Groups["unit"].Value.ToLowerInvariant();
            var unit = unitText switch
            {
                "mm" => PlanMeasurementUnit.Millimeter,
                "cm" => PlanMeasurementUnit.Centimeter,
                "m" => PlanMeasurementUnit.Meter,
                _ => PlanMeasurementUnit.Unknown
            };
            var millimeters = value * (MillimetersPerUnit(unit) ?? 0);
            if (millimeters <= 0)
            {
                return false;
            }

            dimension = new ParsedDimensionText(
                text,
                $"{FormatNumber(value)} {unitText}",
                unit,
                millimeters);
            return true;
        }

        var feet = FeetDimensionRegex().Match(text);
        if (feet.Success)
        {
            var feetValue = ParseNumber(feet.Groups["feet"].Value);
            var inchValue = feet.Groups["inches"].Success ? ParseNumber(feet.Groups["inches"].Value) : 0;
            var millimeters = ((feetValue * 12.0) + inchValue) * MillimetersPerInch;
            if (millimeters <= 0)
            {
                return false;
            }

            dimension = new ParsedDimensionText(
                text,
                $"{FormatNumber(feetValue)} ft {FormatNumber(inchValue)} in",
                PlanMeasurementUnit.Foot,
                millimeters);
            return true;
        }

        var inches = InchesDimensionRegex().Match(text);
        if (inches.Success)
        {
            var inchValue = ParseNumber(inches.Groups["inches"].Value);
            var millimeters = inchValue * MillimetersPerInch;
            if (millimeters <= 0)
            {
                return false;
            }

            dimension = new ParsedDimensionText(
                text,
                $"{FormatNumber(inchValue)} in",
                PlanMeasurementUnit.Inch,
                millimeters);
            return true;
        }

        return false;
    }

    private static DimensionLineMatch? FindNearbyDimensionLine(
        TextPrimitive text,
        ParsedDimensionText parsed,
        IReadOnlyList<PrimitiveLineCandidate> lines,
        SheetRegion? sourceRegion,
        PlanPage page,
        bool inDimensionContext,
        PlanCalibration calibration,
        ScannerOptions options)
    {
        var searchDistance = DimensionLineSearchDistance(page);
        var textCenter = text.Bounds.Center;
        var matchLimit = Math.Max(4, options.MaxDimensionLineMatchCandidatesPerText);
        var expectedDrawingLength = ResolveExpectedDrawingLength(parsed, text, sourceRegion, page, calibration);

        return lines
            .Where(line => line.Segment.Length > 8)
            .Where(line => sourceRegion is null || sourceRegion.Bounds.Intersects(line.Segment.Bounds.Inflate(12)))
            .Select(line => new
            {
                Line = line,
                Distance = line.Segment.DistanceToPoint(textCenter),
                Orientation = ResolveOrientation(line.Segment),
                HasDimensionLayer = LooksLikeDimensionLayer(line.Primitive),
                RegionBonus = sourceRegion is not null && sourceRegion.Bounds.Intersects(line.Segment.Bounds.Inflate(8)) ? 16 : 0,
                ContextPenalty = inDimensionContext ? 0 : 18,
                ExpectedLengthRelativeDifference = ExpectedLengthRelativeDifference(line.Segment.Length, expectedDrawingLength),
                ExpectedLengthAdjustment = ExpectedLengthAdjustment(line.Segment.Length, expectedDrawingLength)
            })
            .Select(candidate => new
            {
                candidate.Line,
                candidate.Distance,
                candidate.Orientation,
                candidate.HasDimensionLayer,
                LayerBonus = candidate.HasDimensionLayer ? 32 : 0,
                candidate.RegionBonus,
                candidate.ContextPenalty,
                candidate.ExpectedLengthRelativeDifference,
                candidate.ExpectedLengthAdjustment
            })
            .Where(candidate => candidate.Distance <= searchDistance || candidate.LayerBonus > 0)
            .OrderBy(candidate => candidate.Distance - candidate.LayerBonus - candidate.RegionBonus + candidate.ContextPenalty + candidate.ExpectedLengthAdjustment)
            .Take(matchLimit)
            .Select(candidate => new
            {
                candidate.Line,
                candidate.Distance,
                candidate.Orientation,
                candidate.HasDimensionLayer,
                candidate.LayerBonus,
                candidate.RegionBonus,
                candidate.ContextPenalty,
                candidate.ExpectedLengthRelativeDifference,
                candidate.ExpectedLengthAdjustment,
                WitnessLines = FindWitnessLines(candidate.Line, lines)
            })
            .Where(candidate => IsEligibleDimensionLineCandidate(
                candidate.Orientation,
                candidate.HasDimensionLayer,
                inDimensionContext,
                candidate.WitnessLines))
            .Where(candidate => AcceptsExpectedLength(
                candidate.ExpectedLengthRelativeDifference,
                candidate.HasDimensionLayer,
                inDimensionContext))
            .OrderBy(candidate => candidate.Distance - candidate.LayerBonus - candidate.RegionBonus - WitnessBonus(candidate.WitnessLines) + candidate.ContextPenalty + candidate.ExpectedLengthAdjustment)
            .Select(candidate => new DimensionLineMatch(candidate.Line, candidate.WitnessLines, expectedDrawingLength))
            .FirstOrDefault();
    }

    private static double DimensionLineSearchDistance(PlanPage page) =>
        Math.Max(36, Math.Min(page.Size.Width, page.Size.Height) * 0.11);

    private static double? ResolveExpectedDrawingLength(
        ParsedDimensionText parsed,
        TextPrimitive text,
        SheetRegion? sourceRegion,
        PlanPage page,
        PlanCalibration calibration)
    {
        if (!calibration.HasReliableMeasurementScale)
        {
            return null;
        }

        var scaleGroup = calibration.SelectMeasurementScaleGroup(
            page.Number,
            text.Bounds,
            sourceRegion?.Id);
        var millimetersPerDrawingUnit = scaleGroup?.MillimetersPerDrawingUnit ?? calibration.MillimetersPerDrawingUnit;
        return millimetersPerDrawingUnit is > 0
            ? parsed.Millimeters / millimetersPerDrawingUnit.Value
            : null;
    }

    private static double ExpectedLengthAdjustment(double candidateLength, double? expectedDrawingLength)
    {
        if (expectedDrawingLength is not > 0)
        {
            return 0;
        }

        var relativeDifference = Math.Abs(candidateLength - expectedDrawingLength.Value) / expectedDrawingLength.Value;
        if (relativeDifference <= ExpectedLengthSoftTolerance)
        {
            return -30 * (1 - (relativeDifference / ExpectedLengthSoftTolerance));
        }

        if (relativeDifference <= ExpectedLengthTrustedContextRejectTolerance)
        {
            return relativeDifference * 30;
        }

        return Math.Min(70, 10 + (relativeDifference * 45));
    }

    private static double? ExpectedLengthRelativeDifference(double candidateLength, double? expectedDrawingLength)
    {
        if (expectedDrawingLength is not > 0)
        {
            return null;
        }

        return Math.Abs(candidateLength - expectedDrawingLength.Value) / expectedDrawingLength.Value;
    }

    private static bool AcceptsExpectedLength(
        double? relativeDifference,
        bool hasDimensionLayer,
        bool inDimensionContext)
    {
        if (relativeDifference is null)
        {
            return true;
        }

        var tolerance = hasDimensionLayer || inDimensionContext
            ? ExpectedLengthTrustedContextRejectTolerance
            : ExpectedLengthUnlayeredRejectTolerance;
        return relativeDifference.Value <= tolerance;
    }

    private static bool IsEligibleDimensionLineCandidate(
        DimensionOrientation orientation,
        bool hasDimensionLayer,
        bool inDimensionContext,
        IReadOnlyList<PrimitiveLineCandidate> witnessLines) =>
        hasDimensionLayer
        || witnessLines.Count >= 2
        || (inDimensionContext && orientation is DimensionOrientation.Horizontal or DimensionOrientation.Vertical);

    private static IReadOnlyList<PrimitiveLineCandidate> FindWitnessLines(
        PrimitiveLineCandidate dimensionLine,
        IReadOnlyList<PrimitiveLineCandidate> lines)
    {
        var orientation = ResolveOrientation(dimensionLine.Segment);
        if (orientation == DimensionOrientation.Unknown)
        {
            return Array.Empty<PrimitiveLineCandidate>();
        }

        var witnesses = new List<PrimitiveLineCandidate>();
        AddWitnessIfPresent(witnesses, FindBestWitnessLine(dimensionLine, dimensionLine.Segment.Start, orientation, lines));
        AddWitnessIfPresent(witnesses, FindBestWitnessLine(dimensionLine, dimensionLine.Segment.End, orientation, lines));
        return witnesses;
    }

    private static void AddWitnessIfPresent(
        ICollection<PrimitiveLineCandidate> witnesses,
        PrimitiveLineCandidate? witness)
    {
        if (witness is null || witnesses.Any(existing => existing.SourceId == witness.SourceId && existing.Segment.Equals(witness.Segment)))
        {
            return;
        }

        witnesses.Add(witness);
    }

    private static PrimitiveLineCandidate? FindBestWitnessLine(
        PrimitiveLineCandidate dimensionLine,
        PlanPoint endpoint,
        DimensionOrientation dimensionOrientation,
        IReadOnlyList<PrimitiveLineCandidate> lines)
    {
        const double endpointTolerance = 5.0;
        var maxWitnessLength = Math.Max(18, Math.Min(96, dimensionLine.Segment.Length * 0.45));
        var search = dimensionOrientation == DimensionOrientation.Horizontal
            ? PlanRect.FromEdges(
                endpoint.X - endpointTolerance,
                endpoint.Y - maxWitnessLength - endpointTolerance,
                endpoint.X + endpointTolerance,
                endpoint.Y + maxWitnessLength + endpointTolerance)
            : PlanRect.FromEdges(
                endpoint.X - maxWitnessLength - endpointTolerance,
                endpoint.Y - endpointTolerance,
                endpoint.X + maxWitnessLength + endpointTolerance,
                endpoint.Y + endpointTolerance);

        return lines
            .Where(candidate => !candidate.Segment.Equals(dimensionLine.Segment))
            .Where(candidate => candidate.Segment.Length >= 4)
            .Where(candidate => candidate.Segment.Bounds.Intersects(search) || LooksLikeDimensionLayer(candidate.Primitive))
            .Where(candidate => IsPerpendicularWitness(candidate.Segment, dimensionLine.Segment, dimensionOrientation, endpoint, endpointTolerance))
            .Where(candidate => candidate.Segment.Length <= maxWitnessLength || LooksLikeDimensionLayer(candidate.Primitive))
            .Select(candidate => new
            {
                Line = candidate,
                Distance = candidate.Segment.DistanceToPoint(endpoint),
                LayerBonus = LooksLikeDimensionLayer(candidate.Primitive) ? 3 : 0,
                LengthPenalty = candidate.Segment.Length / Math.Max(1, maxWitnessLength)
            })
            .OrderBy(candidate => candidate.Distance - candidate.LayerBonus + candidate.LengthPenalty)
            .Select(candidate => candidate.Line)
            .FirstOrDefault();
    }

    private static bool IsPerpendicularWitness(
        PlanLineSegment candidate,
        PlanLineSegment dimensionLine,
        DimensionOrientation dimensionOrientation,
        PlanPoint endpoint,
        double endpointTolerance) =>
        dimensionOrientation switch
        {
            DimensionOrientation.Horizontal => candidate.IsVertical(2) && candidate.DistanceToPoint(endpoint) <= endpointTolerance,
            DimensionOrientation.Vertical => candidate.IsHorizontal(2) && candidate.DistanceToPoint(endpoint) <= endpointTolerance,
            DimensionOrientation.Aligned => IsApproximatelyPerpendicular(candidate, dimensionLine)
                && candidate.DistanceToPoint(endpoint) <= endpointTolerance,
            _ => false
        };

    private static bool IsApproximatelyPerpendicular(PlanLineSegment first, PlanLineSegment second)
    {
        var firstVector = first.Vector.Normalize();
        var secondVector = second.Vector.Normalize();
        if (firstVector.Length <= double.Epsilon || secondVector.Length <= double.Epsilon)
        {
            return false;
        }

        return Math.Abs(firstVector.Dot(secondVector)) <= 0.26;
    }

    private static int WitnessBonus(IReadOnlyList<PrimitiveLineCandidate> witnesses) =>
        witnesses.Count >= 2 ? 40 : witnesses.Count == 1 ? 16 : 0;

    private static int WitnessLineSourceCount(DimensionAnnotation dimension) =>
        dimension.Evidence.Any(item => item.Contains("witness/extension", StringComparison.OrdinalIgnoreCase))
            ? Math.Max(0, dimension.SourcePrimitiveIds.Count - 2)
            : 0;

    private static IEnumerable<PrimitiveLineCandidate> EnumerateLines(ScanContext context, PlanPage page)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            var sourceId = context.PrimitiveId(page.Number, index, primitive);

            switch (primitive)
            {
                case LinePrimitive line:
                    yield return new PrimitiveLineCandidate(line.Segment, sourceId, primitive);
                    break;

                case RectanglePrimitive rectangle:
                    foreach (var edge in RectangleToLines(rectangle.Rectangle))
                    {
                        yield return new PrimitiveLineCandidate(edge, sourceId, primitive);
                    }

                    break;

                case PolylinePrimitive polyline:
                    foreach (var segment in PolylineToLines(polyline))
                    {
                        yield return new PrimitiveLineCandidate(segment, sourceId, primitive);
                    }

                    break;
            }
        }
    }

    private static IEnumerable<PlanLineSegment> RectangleToLines(PlanRect rect)
    {
        if (rect.IsEmpty)
        {
            yield break;
        }

        var topLeft = new PlanPoint(rect.Left, rect.Top);
        var topRight = new PlanPoint(rect.Right, rect.Top);
        var bottomRight = new PlanPoint(rect.Right, rect.Bottom);
        var bottomLeft = new PlanPoint(rect.Left, rect.Bottom);

        yield return new PlanLineSegment(topLeft, topRight);
        yield return new PlanLineSegment(topRight, bottomRight);
        yield return new PlanLineSegment(bottomRight, bottomLeft);
        yield return new PlanLineSegment(bottomLeft, topLeft);
    }

    private static IEnumerable<PlanLineSegment> PolylineToLines(PolylinePrimitive polyline)
    {
        if (polyline.Points.Count < 2)
        {
            yield break;
        }

        for (var index = 1; index < polyline.Points.Count; index++)
        {
            yield return new PlanLineSegment(polyline.Points[index - 1], polyline.Points[index]);
        }

        if (polyline.Closed)
        {
            yield return new PlanLineSegment(polyline.Points[^1], polyline.Points[0]);
        }
    }

    private static SheetRegion? FindSourceRegion(
        IReadOnlyList<SheetRegion> sheetRegions,
        int pageNumber,
        PlanPoint point) =>
        sheetRegions
            .Where(region => region.PageNumber == pageNumber)
            .Where(region => region.Kind == RegionKind.Dimensions || region.Kind == RegionKind.MainFloorPlan)
            .Where(region => region.Bounds.Contains(point, 8))
            .OrderByDescending(region => region.Kind == RegionKind.Dimensions)
            .ThenBy(region => region.Bounds.Area)
            .FirstOrDefault();

    private static bool IsDimensionContext(TextPrimitive text, SheetRegion? region) =>
        LooksLikeDimensionLayer(text)
        || region?.Kind == RegionKind.Dimensions;

    private static bool LooksLikeDimensionLayer(PlanPrimitive primitive)
    {
        var layer = primitive.Source.Layer ?? primitive.Layer;
        return !string.IsNullOrWhiteSpace(layer)
            && (layer.Contains("dim", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("anno", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("a-dim", StringComparison.OrdinalIgnoreCase));
    }

    private static DimensionOrientation ResolveOrientation(PlanLineSegment segment)
    {
        if (segment.IsHorizontal(2))
        {
            return DimensionOrientation.Horizontal;
        }

        if (segment.IsVertical(2))
        {
            return DimensionOrientation.Vertical;
        }

        return DimensionOrientation.Aligned;
    }

    private static Confidence DimensionConfidence(
        TextPrimitive text,
        SheetRegion? region,
        DimensionLineMatch? match,
        bool inDimensionContext)
    {
        var value = 0.34;
        if (inDimensionContext)
        {
            value += 0.18;
        }

        if (region?.Kind == RegionKind.Dimensions)
        {
            value += 0.1;
        }

        if (LooksLikeDimensionLayer(text))
        {
            value += 0.14;
        }

        if (match is not null)
        {
            value += 0.22;
            if (LooksLikeDimensionLayer(match.Line.Primitive))
            {
                value += 0.06;
            }

            value += match.WitnessLines.Count >= 2
                ? 0.1
                : match.WitnessLines.Count == 1
                    ? 0.04
                    : 0;
        }

        return new Confidence(Math.Min(0.92, value));
    }

    private static bool LooksLikeScaleText(string text) =>
        text.Contains("scale", StringComparison.OrdinalIgnoreCase)
        || RatioScaleRegex().IsMatch(text)
        || ImperialScaleRegex().IsMatch(text);

    private static bool LooksLikeAreaMeasurementText(string text) =>
        AreaMeasurementRegex().IsMatch(text);

    private static bool IsLeadingMetricNumberFragment(string text) =>
        MetricLeadingNumberFragmentRegex().IsMatch(text);

    private static bool IsTrailingMetricNumberFragment(string text) =>
        MetricTrailingNumberFragmentRegex().IsMatch(text);

    private static bool IsSameTextLine(PlanRect first, PlanRect second)
    {
        var centerDelta = Math.Abs(first.Center.Y - second.Center.Y);
        var maxHeight = Math.Max(first.Height, second.Height);
        return centerDelta <= Math.Max(2, maxHeight * 0.75)
            && VerticalOverlapRatio(first, second) >= 0.35;
    }

    private static double MaxMetricFragmentGap(PlanRect first, PlanRect second) =>
        Math.Max(4, Math.Max(first.Height, second.Height) * 1.8);

    private static double VerticalOverlapRatio(PlanRect first, PlanRect second)
    {
        var overlap = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
        var minimumHeight = Math.Min(first.Height, second.Height);
        return minimumHeight <= 0 ? 0 : Math.Max(0, overlap) / minimumHeight;
    }

    private static double ParseNumber(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        var slash = normalized.Split('/', StringSplitOptions.TrimEntries);
        if (slash.Length == 2
            && double.TryParse(slash[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(slash[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private static double? MillimetersPerUnit(PlanMeasurementUnit unit) =>
        unit switch
        {
            PlanMeasurementUnit.Millimeter => 1.0,
            PlanMeasurementUnit.Centimeter => 10.0,
            PlanMeasurementUnit.Meter => 1000.0,
            PlanMeasurementUnit.Inch => MillimetersPerInch,
            PlanMeasurementUnit.Foot => MillimetersPerInch * 12.0,
            _ => null
        };

    private static string NormalizeText(string value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim();

    private static string FormatMillimeters(double value) =>
        Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string LineKey(PlanLineSegment line)
    {
        var first = line.Start;
        var second = line.End;
        if (second.X < first.X || (Math.Abs(second.X - first.X) < 0.001 && second.Y < first.Y))
        {
            (first, second) = (second, first);
        }

        return string.Join(
            ":",
            Math.Round(first.X, 3).ToString(CultureInfo.InvariantCulture),
            Math.Round(first.Y, 3).ToString(CultureInfo.InvariantCulture),
            Math.Round(second.X, 3).ToString(CultureInfo.InvariantCulture),
            Math.Round(second.Y, 3).ToString(CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)\b1\s*:\s*(?<ratio>\d+(?:[\.,]\d+)?)\b", RegexOptions.Compiled)]
    private static partial Regex RatioScaleRegex();

    [GeneratedRegex(@"(?i)(?<paper>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?:""|in|inch|inches)\s*=\s*(?<feet>\d+(?:[\.,]\d+)?)\s*(?:'|ft|foot|feet)", RegexOptions.Compiled)]
    private static partial Regex ImperialScaleRegex();

    [GeneratedRegex(@"(?i)(?<![A-Za-z])(?<value>\d+(?:[\.,]\d+)?)\s*(?<unit>mm|cm|m)\b", RegexOptions.Compiled)]
    private static partial Regex MetricDimensionRegex();

    [GeneratedRegex(@"(?i)^\s*(?<value>\d{1,3}(?:\s+\d{3})+)(?:\s*mm)?\s*$", RegexOptions.Compiled)]
    private static partial Regex MetricWholeMillimeterDimensionRegex();

    [GeneratedRegex(@"(?i)\b\d+(?:[\.,]\d+)?\s*m(?:2|\^2|²|㎡)\b", RegexOptions.Compiled)]
    private static partial Regex AreaMeasurementRegex();

    [GeneratedRegex(@"^\d{1,3}$", RegexOptions.Compiled)]
    private static partial Regex MetricLeadingNumberFragmentRegex();

    [GeneratedRegex(@"^\d{3}$", RegexOptions.Compiled)]
    private static partial Regex MetricTrailingNumberFragmentRegex();

    [GeneratedRegex(@"(?i)(?<feet>\d+(?:[\.,]\d+)?)\s*'\s*(?:-?\s*(?<inches>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)?)?\s*(?:""|in|inch|inches)?", RegexOptions.Compiled)]
    private static partial Regex FeetDimensionRegex();

    [GeneratedRegex(@"(?i)(?<inches>\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?:""|in|inch|inches)\b", RegexOptions.Compiled)]
    private static partial Regex InchesDimensionRegex();

    private readonly record struct ParsedDimensionText(
        string RawText,
        string NormalizedText,
        PlanMeasurementUnit Unit,
        double Millimeters);

    private sealed record DimensionTextCandidate(
        TextPrimitive Text,
        ParsedDimensionText Parsed,
        string SourceId,
        IReadOnlyList<string> SourceIds,
        SheetRegion? Region,
        bool InDimensionContext);

    private sealed record DimensionTextItem(TextPrimitive Text, string SourceId);

    private sealed record DimensionDuplicateKey(
        int PageNumber,
        string NormalizedText,
        string LineKey);

    private sealed record DimensionLineConflictKey(
        int PageNumber,
        string LineKey);

    private sealed record PrimitiveLineCandidate(
        PlanLineSegment Segment,
        string SourceId,
        PlanPrimitive Primitive);

    private sealed record DimensionLineMatch(
        PrimitiveLineCandidate Line,
        IReadOnlyList<PrimitiveLineCandidate> WitnessLines,
        double? ExpectedDrawingLength);
}
