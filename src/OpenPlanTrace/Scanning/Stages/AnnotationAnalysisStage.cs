using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed partial class AnnotationAnalysisStage : IPipelineStage
{
    private const double RegionTextTolerance = 6.0;
    private const double HeadingClusterHeight = 260.0;
    private const double HeadingClusterRightReach = 520.0;

    public string Name => "annotations";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var textItems = EnumerateTextItems(context, page)
                .Where(item => IsUsefulText(item.Text.Text))
                .ToArray();

            if (textItems.Length == 0)
            {
                continue;
            }

            var consumedSourceIds = new HashSet<string>(StringComparer.Ordinal);
            var blocks = new List<PlanAnnotationBlock>();
            var ordinal = 1;

            foreach (var region in context.SheetRegions
                .Where(region => region.PageNumber == page.Number && region.Kind is RegionKind.Notes or RegionKind.Legend)
                .OrderBy(region => region.Bounds.Top)
                .ThenBy(region => region.Bounds.Left))
            {
                var regionText = textItems
                    .Where(item => !LooksLikeDimensionText(item.Text.Text))
                    .Where(item => region.Bounds.Contains(item.Bounds.Center, RegionTextTolerance))
                    .OrderBy(item => item.Bounds.Top)
                    .ThenBy(item => item.Bounds.Left)
                    .ToArray();

                regionText = IncludeNearbyRegionHeading(textItems, region, regionText);

                if (regionText.Length == 0)
                {
                    context.AddDiagnostic(
                        "annotations.region_without_text",
                        DiagnosticSeverity.Warning,
                        Name,
                        $"{region.Kind} region did not contain readable text primitives.",
                        page.Number,
                        region.Bounds,
                        Confidence.Low,
                        DiagnosticScope.Annotation,
                        region.SourcePrimitiveIds);
                    continue;
                }

                var preferredKind = region.Kind == RegionKind.Legend
                    ? PlanAnnotationKind.Legend
                    : PlanAnnotationKind.GeneralNotes;
                var block = CreateBlock(context, page, regionText, preferredKind, region.Id, region.Label, ordinal++);
                blocks.Add(block);
                AddConsumed(consumedSourceIds, block.SourcePrimitiveIds);
            }

            foreach (var block in DetectHeadingBlocks(context, page, textItems, consumedSourceIds, ref ordinal))
            {
                blocks.Add(block);
                AddConsumed(consumedSourceIds, block.SourcePrimitiveIds);
            }

            foreach (var block in DetectStandaloneCalloutBlocks(context, page, textItems, consumedSourceIds, ref ordinal))
            {
                blocks.Add(block);
                AddConsumed(consumedSourceIds, block.SourcePrimitiveIds);
            }

            foreach (var block in blocks)
            {
                context.Annotations.Add(block);
            }

            if (blocks.Count > 0)
            {
                var itemCount = blocks.Sum(block => block.Items.Count);
                var referenceCount = blocks.Sum(block => block.Items.Sum(item => item.References.Count));
                context.AddDiagnostic(
                    "annotations.detected",
                    DiagnosticSeverity.Info,
                    Name,
                    $"Detected {blocks.Count} annotation blocks with {itemCount} text items and {referenceCount} plan reference(s).",
                    page.Number,
                    PlanRect.Union(blocks.Select(block => block.Bounds)),
                    new Confidence(Math.Min(0.95, blocks.Max(block => block.Confidence.Value))),
                    DiagnosticScope.Annotation,
                    blocks.SelectMany(block => block.SourcePrimitiveIds),
                    new Dictionary<string, string>
                    {
                        ["blocks"] = blocks.Count.ToString(),
                        ["items"] = itemCount.ToString(),
                        ["references"] = referenceCount.ToString(),
                        ["kinds"] = string.Join(",", blocks.Select(block => block.Kind).Distinct())
                    });

                if (referenceCount > 0)
                {
                    context.AddDiagnostic(
                        "annotations.references.detected",
                        DiagnosticSeverity.Info,
                        Name,
                        $"Associated {referenceCount} plan-side marker reference(s) with annotation items.",
                        page.Number,
                        PlanRect.Union(blocks
                            .SelectMany(block => block.Items)
                            .SelectMany(item => item.References)
                            .Select(reference => reference.Bounds)),
                        Confidence.Medium,
                        DiagnosticScope.Annotation,
                        blocks
                            .SelectMany(block => block.Items)
                            .SelectMany(item => item.References)
                            .SelectMany(reference => reference.SourcePrimitiveIds),
                        new Dictionary<string, string>
                        {
                            ["references"] = referenceCount.ToString()
                        });
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<PlanAnnotationBlock> DetectHeadingBlocks(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<TextItem> textItems,
        HashSet<string> consumedSourceIds,
        ref int ordinal)
    {
        var blocks = new List<PlanAnnotationBlock>();
        var candidates = textItems
            .Where(item => !consumedSourceIds.Contains(item.SourceId))
            .Where(item => !LooksLikeDimensionText(item.Text.Text))
            .Where(item => !IsInExcludedRegion(context, page.Number, item.Bounds))
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToArray();

        foreach (var seed in candidates)
        {
            if (consumedSourceIds.Contains(seed.SourceId))
            {
                continue;
            }

            var headingKind = ClassifyHeading(seed.Value);
            if (headingKind == PlanAnnotationKind.Unknown)
            {
                continue;
            }

            var nextHeadingTop = candidates
                .Where(item => item.Bounds.Top > seed.Bounds.Top + 4)
                .Where(item => ClassifyHeading(item.Value) != PlanAnnotationKind.Unknown)
                .Where(item => Math.Abs(item.Bounds.Left - seed.Bounds.Left) < 120)
                .Select(item => (double?)item.Bounds.Top)
                .FirstOrDefault();

            var bottomLimit = Math.Min(
                seed.Bounds.Top + HeadingClusterHeight,
                nextHeadingTop is null ? page.Bounds.Bottom : nextHeadingTop.Value - 2);

            var cluster = candidates
                .Where(item => !consumedSourceIds.Contains(item.SourceId))
                .Where(item => item.Bounds.Top >= seed.Bounds.Top - 3 && item.Bounds.Top <= bottomLimit)
                .Where(item => IsInHeadingCluster(seed, item))
                .OrderBy(item => item.Bounds.Top)
                .ThenBy(item => item.Bounds.Left)
                .ToArray();

            if (cluster.Length == 0)
            {
                continue;
            }

            var block = CreateBlock(context, page, cluster, headingKind, null, seed.Value, ordinal++);
            if (block.Items.Count < 2 && block.Kind is not (PlanAnnotationKind.Legend or PlanAnnotationKind.Schedule or PlanAnnotationKind.RevisionTable))
            {
                continue;
            }

            blocks.Add(block);
        }

        return blocks;
    }

    private IReadOnlyList<PlanAnnotationBlock> DetectStandaloneCalloutBlocks(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<TextItem> textItems,
        HashSet<string> consumedSourceIds,
        ref int ordinal)
    {
        var items = textItems
            .Where(item => !consumedSourceIds.Contains(item.SourceId))
            .Where(item => !LooksLikeDimensionText(item.Text.Text))
            .Where(item => !IsInExcludedRegion(context, page.Number, item.Bounds))
            .Select(item => new { TextItem = item, Parsed = ParseAnnotationItem(item.Value, PlanAnnotationKind.Callouts) })
            .Where(item => item.Parsed.Kind is PlanAnnotationItemKind.Callout or PlanAnnotationItemKind.Keynote)
            .Where(item => HasMeaningfulStandaloneCallout(item.TextItem.Value, item.Parsed))
            .Select(item => item.TextItem)
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToArray();

        if (items.Length == 0)
        {
            return Array.Empty<PlanAnnotationBlock>();
        }

        var kind = items.Any(item => ParseAnnotationItem(item.Value, PlanAnnotationKind.Callouts).Kind == PlanAnnotationItemKind.Keynote)
            ? PlanAnnotationKind.Keynotes
            : PlanAnnotationKind.Callouts;

        return new[] { CreateBlock(context, page, items, kind, null, "Callouts", ordinal++) };
    }

    private PlanAnnotationBlock CreateBlock(
        ScanContext context,
        PlanPage page,
        IReadOnlyList<TextItem> textItems,
        PlanAnnotationKind preferredKind,
        string? sourceRegionId,
        string? label,
        int ordinal)
    {
        var ordered = textItems
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToArray();
        var kind = ClassifyBlockKind(ordered, preferredKind);
        var blockId = $"page:{page.Number}:annotation:{KindSlug(kind)}:{ordinal:00}";
        var blockBounds = PlanRect.Union(ordered.Select(item => item.Bounds)).Inflate(2).ClampTo(page.Bounds);
        var sourcePrimitiveIds = ordered.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        var confidence = BlockConfidence(kind, ordered, sourceRegionId is not null);
        var blockSourceIds = sourcePrimitiveIds.ToHashSet(StringComparer.Ordinal);
        var items = ordered
            .Select((item, index) => CreateItem(context, page, blockId, kind, item, blockSourceIds, index + 1))
            .ToArray();

        return new PlanAnnotationBlock(
            blockId,
            page.Number,
            kind,
            string.IsNullOrWhiteSpace(label) ? DefaultLabel(kind, ordered) : label,
            blockBounds,
            confidence,
            sourceRegionId,
            items,
            sourcePrimitiveIds,
            BlockEvidence(kind, ordered, sourceRegionId));
    }

    private static TextItem[] IncludeNearbyRegionHeading(
        IReadOnlyList<TextItem> textItems,
        SheetRegion region,
        IReadOnlyList<TextItem> regionText)
    {
        if (regionText.Count == 0)
        {
            return regionText.ToArray();
        }

        var heading = textItems
            .Where(item => !regionText.Any(existing => existing.SourceId == item.SourceId))
            .Where(item => ClassifyHeading(item.Value) != PlanAnnotationKind.Unknown)
            .Where(item => item.Bounds.Bottom >= region.Bounds.Top - 48 && item.Bounds.Bottom <= region.Bounds.Top + RegionTextTolerance)
            .Where(item => item.Bounds.Left <= region.Bounds.Right + 30 && item.Bounds.Right >= region.Bounds.Left - 80)
            .OrderByDescending(item => item.Bounds.Bottom)
            .ThenBy(item => item.Bounds.Left)
            .FirstOrDefault();

        if (heading is null)
        {
            return regionText.ToArray();
        }

        return new[] { heading }
            .Concat(regionText)
            .DistinctBy(item => item.SourceId)
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .ToArray();
    }

    private static PlanAnnotationItem CreateItem(
        ScanContext context,
        PlanPage page,
        string blockId,
        PlanAnnotationKind blockKind,
        TextItem item,
        IReadOnlySet<string> blockSourceIds,
        int itemNumber)
    {
        var parsed = ParseAnnotationItem(item.Value, blockKind);
        var itemId = $"{blockId}:item:{itemNumber:00}";
        var evidence = new List<string>();

        if (!string.IsNullOrWhiteSpace(parsed.Marker))
        {
            evidence.Add($"marker {parsed.Marker}");
        }

        if (parsed.Kind == PlanAnnotationItemKind.Heading)
        {
            evidence.Add("heading keyword");
        }

        evidence.Add($"source text \"{TrimForEvidence(item.Value)}\"");

        return new PlanAnnotationItem(
            itemId,
            page.Number,
            parsed.Kind,
            parsed.Text,
            parsed.Marker,
            item.Bounds,
            ItemConfidence(parsed),
            new[] { item.SourceId },
            FindPlanReferences(context, page, itemId, parsed, blockSourceIds),
            evidence);
    }

    private static IReadOnlyList<PlanAnnotationReference> FindPlanReferences(
        ScanContext context,
        PlanPage page,
        string itemId,
        ParsedAnnotationItem parsed,
        IReadOnlySet<string> blockSourceIds)
    {
        if (parsed.Marker is null || parsed.Kind is not (PlanAnnotationItemKind.Keynote or PlanAnnotationItemKind.Callout))
        {
            return Array.Empty<PlanAnnotationReference>();
        }

        var marker = NormalizeReferenceMarker(parsed.Marker);
        if (marker is null)
        {
            return Array.Empty<PlanAnnotationReference>();
        }

        var references = EnumerateTextItems(context, page)
            .Where(item => !blockSourceIds.Contains(item.SourceId))
            .Where(item => !LooksLikeDimensionText(item.Text.Text))
            .Where(item => !IsInReferenceExcludedRegion(context, page.Number, item.Bounds))
            .Select(item => new { TextItem = item, Marker = NormalizeReferenceMarker(item.Value) })
            .Where(item => string.Equals(item.Marker, marker, StringComparison.Ordinal))
            .OrderBy(item => item.TextItem.Bounds.Top)
            .ThenBy(item => item.TextItem.Bounds.Left)
            .Take(24)
            .Select((item, index) => CreateReference(context, page, itemId, marker, item.TextItem, index + 1))
            .ToArray();

        return references;
    }

    private static PlanAnnotationReference CreateReference(
        ScanContext context,
        PlanPage page,
        string itemId,
        string marker,
        TextItem textItem,
        int referenceNumber)
    {
        var relatedGeometry = FindReferenceGeometry(context, page, textItem).ToArray();
        var sourceIds = new[] { textItem.SourceId }
            .Concat(relatedGeometry.Select(item => item.SourceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var bounds = PlanRect.Union(new[] { textItem.Bounds }.Concat(relatedGeometry.Select(item => item.Primitive.Bounds)))
            .Inflate(1)
            .ClampTo(page.Bounds);
        var evidence = new List<string>
        {
            $"matched plan marker {marker}",
            $"source text \"{TrimForEvidence(textItem.Value)}\""
        };

        if (relatedGeometry.Length > 0)
        {
            evidence.Add($"{relatedGeometry.Length} nearby closed marker geometry primitive(s)");
        }

        return new PlanAnnotationReference(
            $"{itemId}:reference:{referenceNumber:00}",
            marker,
            textItem.Value,
            bounds,
            new Confidence(relatedGeometry.Length > 0 ? 0.74 : 0.62),
            sourceIds,
            evidence);
    }

    private static PlanAnnotationKind ClassifyBlockKind(
        IReadOnlyList<TextItem> textItems,
        PlanAnnotationKind preferredKind)
    {
        var headingKind = textItems
            .Select(item => ClassifyHeading(item.Value))
            .FirstOrDefault(kind => kind != PlanAnnotationKind.Unknown);

        if (headingKind != PlanAnnotationKind.Unknown)
        {
            return headingKind;
        }

        if (preferredKind != PlanAnnotationKind.Unknown)
        {
            return preferredKind;
        }

        var parsedKinds = textItems
            .Select(item => ParseAnnotationItem(item.Value, PlanAnnotationKind.Unknown).Kind)
            .ToArray();

        if (parsedKinds.Any(kind => kind == PlanAnnotationItemKind.Keynote))
        {
            return PlanAnnotationKind.Keynotes;
        }

        if (parsedKinds.Any(kind => kind == PlanAnnotationItemKind.Callout))
        {
            return PlanAnnotationKind.Callouts;
        }

        return PlanAnnotationKind.TextBlock;
    }

    private static ParsedAnnotationItem ParseAnnotationItem(string text, PlanAnnotationKind blockKind)
    {
        var normalized = NormalizeWhitespace(text);
        if (ClassifyHeading(normalized) != PlanAnnotationKind.Unknown)
        {
            return new ParsedAnnotationItem(PlanAnnotationItemKind.Heading, normalized, null);
        }

        if (blockKind == PlanAnnotationKind.RevisionTable)
        {
            var revisionRow = RevisionRowPattern().Match(normalized);
            if (revisionRow.Success)
            {
                var markerText = revisionRow.Groups["marker"].Value.ToUpperInvariant();
                var body = NormalizeWhitespace(revisionRow.Groups["body"].Value);
                if (body.Length >= 2 && !LooksLikeSheetNumber(normalized))
                {
                    return new ParsedAnnotationItem(PlanAnnotationItemKind.RevisionEntry, body, markerText);
                }
            }
        }

        var keynote = KeynotePattern().Match(normalized);
        if (keynote.Success)
        {
            return new ParsedAnnotationItem(
                PlanAnnotationItemKind.Keynote,
                NormalizeWhitespace(keynote.Groups["body"].Value),
                keynote.Groups["marker"].Value);
        }

        var marker = ListMarkerPattern().Match(normalized);
        if (marker.Success)
        {
            var markerText = marker.Groups["marker"].Value;
            var body = NormalizeWhitespace(marker.Groups["body"].Value);
            if (body.Length >= 3)
            {
                var kind = blockKind switch
                {
                    PlanAnnotationKind.Keynotes => PlanAnnotationItemKind.Keynote,
                    PlanAnnotationKind.Callouts => PlanAnnotationItemKind.Callout,
                    PlanAnnotationKind.Legend => PlanAnnotationItemKind.LegendEntry,
                    PlanAnnotationKind.Schedule => PlanAnnotationItemKind.ScheduleEntry,
                    PlanAnnotationKind.RevisionTable => PlanAnnotationItemKind.RevisionEntry,
                    _ => PlanAnnotationItemKind.Note
                };

                return new ParsedAnnotationItem(kind, body, markerText);
            }
        }

        return blockKind switch
        {
            PlanAnnotationKind.Legend => new ParsedAnnotationItem(PlanAnnotationItemKind.LegendEntry, normalized, null),
            PlanAnnotationKind.Schedule => new ParsedAnnotationItem(PlanAnnotationItemKind.ScheduleEntry, normalized, null),
            PlanAnnotationKind.RevisionTable => new ParsedAnnotationItem(PlanAnnotationItemKind.RevisionEntry, normalized, null),
            PlanAnnotationKind.Keynotes => new ParsedAnnotationItem(PlanAnnotationItemKind.Keynote, normalized, null),
            PlanAnnotationKind.Callouts => new ParsedAnnotationItem(PlanAnnotationItemKind.Callout, normalized, null),
            PlanAnnotationKind.GeneralNotes => new ParsedAnnotationItem(PlanAnnotationItemKind.Note, normalized, null),
            _ => new ParsedAnnotationItem(PlanAnnotationItemKind.TextLine, normalized, null)
        };
    }

    private static PlanAnnotationKind ClassifyHeading(string text)
    {
        var normalized = NormalizeForClassification(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return PlanAnnotationKind.Unknown;
        }

        if (LooksLikeRevisionHeading(normalized))
        {
            return PlanAnnotationKind.RevisionTable;
        }

        if (normalized.Contains("SCHEDULE", StringComparison.Ordinal))
        {
            return PlanAnnotationKind.Schedule;
        }

        if (normalized.Contains("LEGEND", StringComparison.Ordinal)
            || normalized.Contains("ABBREVIATION", StringComparison.Ordinal)
            || normalized.Contains("SYMBOL", StringComparison.Ordinal))
        {
            return PlanAnnotationKind.Legend;
        }

        if (normalized.Contains("KEYNOTE", StringComparison.Ordinal))
        {
            return PlanAnnotationKind.Keynotes;
        }

        if (normalized.Contains("CALLOUT", StringComparison.Ordinal))
        {
            return PlanAnnotationKind.Callouts;
        }

        if (normalized == "NOTES"
            || normalized.Contains("GENERAL NOTES", StringComparison.Ordinal)
            || normalized.Contains("PLAN NOTES", StringComparison.Ordinal)
            || normalized.Contains("CONSTRUCTION NOTES", StringComparison.Ordinal))
        {
            return PlanAnnotationKind.GeneralNotes;
        }

        return PlanAnnotationKind.Unknown;
    }

    private static bool LooksLikeRevisionHeading(string normalized) =>
        normalized == "REVISIONS"
        || normalized == "REVISION"
        || normalized == "REVISJON"
        || normalized == "REVISJONER"
        || normalized.Contains("REVISION TABLE", StringComparison.Ordinal)
        || normalized.Contains("REVISION HISTORY", StringComparison.Ordinal)
        || normalized.Contains("REVISION SCHEDULE", StringComparison.Ordinal)
        || normalized.Contains("REVISION LOG", StringComparison.Ordinal)
        || normalized.Contains("REVISJONSTABELL", StringComparison.Ordinal)
        || normalized.Contains("REVISJONSHISTORIKK", StringComparison.Ordinal);

    private static bool IsInHeadingCluster(TextItem seed, TextItem candidate)
    {
        if (candidate.Bounds.Top < seed.Bounds.Top - 3)
        {
            return false;
        }

        var leftWindow = seed.Bounds.Left - 70;
        var rightWindow = Math.Max(seed.Bounds.Right, seed.Bounds.Left + HeadingClusterRightReach);
        var withinColumn = candidate.Bounds.Left >= leftWindow && candidate.Bounds.Left <= rightWindow;
        var overlapsHeading = candidate.Bounds.Left <= seed.Bounds.Right + 60 && candidate.Bounds.Right >= seed.Bounds.Left - 20;
        return withinColumn || overlapsHeading;
    }

    private static Confidence BlockConfidence(
        PlanAnnotationKind kind,
        IReadOnlyList<TextItem> textItems,
        bool hasSourceRegion)
    {
        var score = 0.40;
        if (hasSourceRegion)
        {
            score += 0.18;
        }

        if (kind != PlanAnnotationKind.TextBlock && kind != PlanAnnotationKind.Unknown)
        {
            score += 0.10;
        }

        if (textItems.Any(item => ClassifyHeading(item.Value) != PlanAnnotationKind.Unknown))
        {
            score += 0.12;
        }

        var markerCount = textItems.Count(item => ParseAnnotationItem(item.Value, kind).Marker is not null);
        score += Math.Min(0.12, markerCount * 0.04);

        if (textItems.Count >= 2)
        {
            score += 0.08;
        }

        return new Confidence(Math.Min(0.92, score));
    }

    private static Confidence ItemConfidence(ParsedAnnotationItem item)
    {
        var score = item.Kind switch
        {
            PlanAnnotationItemKind.Heading => 0.82,
            PlanAnnotationItemKind.Keynote or PlanAnnotationItemKind.Callout when item.Marker is not null => 0.76,
            PlanAnnotationItemKind.Note when item.Marker is not null => 0.74,
            PlanAnnotationItemKind.LegendEntry or PlanAnnotationItemKind.ScheduleEntry or PlanAnnotationItemKind.RevisionEntry => 0.68,
            PlanAnnotationItemKind.Note => 0.62,
            PlanAnnotationItemKind.Callout or PlanAnnotationItemKind.Keynote => 0.60,
            _ => 0.52
        };

        return new Confidence(score);
    }

    private static IReadOnlyList<string> BlockEvidence(
        PlanAnnotationKind kind,
        IReadOnlyList<TextItem> textItems,
        string? sourceRegionId)
    {
        var evidence = new List<string>();
        if (sourceRegionId is not null)
        {
            evidence.Add($"text inside region {sourceRegionId}");
        }

        var heading = textItems.FirstOrDefault(item => ClassifyHeading(item.Value) != PlanAnnotationKind.Unknown);
        if (heading is not null)
        {
            evidence.Add($"heading \"{TrimForEvidence(heading.Value)}\"");
        }

        var markerCount = textItems.Count(item => ParseAnnotationItem(item.Value, kind).Marker is not null);
        if (markerCount > 0)
        {
            evidence.Add($"{markerCount} marked annotation item(s)");
        }

        evidence.Add($"{textItems.Count} text primitive(s) grouped as {kind}");
        return evidence;
    }

    private static string? DefaultLabel(PlanAnnotationKind kind, IReadOnlyList<TextItem> textItems)
    {
        var heading = textItems.FirstOrDefault(item => ClassifyHeading(item.Value) != PlanAnnotationKind.Unknown);
        if (heading is not null)
        {
            return heading.Value;
        }

        return kind switch
        {
            PlanAnnotationKind.GeneralNotes => "General notes",
            PlanAnnotationKind.Keynotes => "Keynotes",
            PlanAnnotationKind.Legend => "Legend",
            PlanAnnotationKind.Schedule => "Schedule",
            PlanAnnotationKind.RevisionTable => "Revision table",
            PlanAnnotationKind.Callouts => "Callouts",
            PlanAnnotationKind.TextBlock => "Text block",
            _ => null
        };
    }

    private static bool HasMeaningfulStandaloneCallout(string text, ParsedAnnotationItem item)
    {
        if (item.Marker is null)
        {
            return false;
        }

        var bodyWordCount = item.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return item.Text.Length >= 8 && bodyWordCount >= 2 && !LooksLikeSheetNumber(text);
    }

    private static bool LooksLikeSheetNumber(string text) =>
        SheetNumberLikePattern().IsMatch(NormalizeWhitespace(text));

    private static string? NormalizeReferenceMarker(string text)
    {
        var match = ReferenceMarkerPattern().Match(NormalizeWhitespace(text));
        return match.Success ? match.Groups["marker"].Value.ToUpperInvariant() : null;
    }

    private static bool IsUsefulText(string text) =>
        NormalizeWhitespace(text).Length >= 2;

    private static bool IsInExcludedRegion(ScanContext context, int pageNumber, PlanRect bounds) =>
        context.SheetRegions.Any(region =>
            region.PageNumber == pageNumber
            && region.Kind is RegionKind.TitleBlock or RegionKind.Dimensions
            && region.Bounds.Contains(bounds.Center, RegionTextTolerance));

    private static bool IsInReferenceExcludedRegion(ScanContext context, int pageNumber, PlanRect bounds) =>
        context.SheetRegions.Any(region =>
            region.PageNumber == pageNumber
            && region.Kind is RegionKind.TitleBlock or RegionKind.Dimensions or RegionKind.Notes or RegionKind.Legend or RegionKind.KeyPlan
            && region.Bounds.Contains(bounds.Center, RegionTextTolerance));

    private static IEnumerable<ReferenceGeometryItem> FindReferenceGeometry(
        ScanContext context,
        PlanPage page,
        TextItem textItem)
    {
        var searchDistance = Math.Max(8, Math.Max(textItem.Bounds.Width, textItem.Bounds.Height) * 1.5);
        var search = textItem.Bounds.Inflate(searchDistance);

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (primitive is TextPrimitive || !IsClosedMarkerGeometry(primitive, textItem.Bounds, search))
            {
                continue;
            }

            yield return new ReferenceGeometryItem(
                primitive,
                context.PrimitiveId(page.Number, index, primitive));
        }
    }

    private static bool IsClosedMarkerGeometry(
        PlanPrimitive primitive,
        PlanRect markerBounds,
        PlanRect search)
    {
        if (!primitive.Bounds.Intersects(search) || !primitive.Bounds.Contains(markerBounds.Center, RegionTextTolerance))
        {
            return false;
        }

        var minArea = Math.Max(12, markerBounds.Area * 1.1);
        var maxArea = Math.Max(320, markerBounds.Area * 50);
        if (primitive.Bounds.Area < minArea || primitive.Bounds.Area > maxArea)
        {
            return false;
        }

        return primitive switch
        {
            RectanglePrimitive => true,
            PolylinePrimitive { Closed: true } => true,
            ArcPrimitive arc => Math.Abs(arc.SweepAngleRadians) >= Math.PI * 1.5,
            _ => false
        };
    }

    private static bool LooksLikeDimensionText(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Any(char.IsDigit)
            && (trimmed.Contains('\'')
                || trimmed.Contains('"')
                || trimmed.Contains("mm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("cm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('m')
                || trimmed.Contains('x')
                || trimmed.Contains('X'));
    }

    private static IEnumerable<TextItem> EnumerateTextItems(ScanContext context, PlanPage page)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not TextPrimitive text)
            {
                continue;
            }

            yield return new TextItem(index, text, context.PrimitiveId(page.Number, index, text));
        }
    }

    private static void AddConsumed(HashSet<string> consumedSourceIds, IEnumerable<string> sourcePrimitiveIds)
    {
        foreach (var sourceId in sourcePrimitiveIds)
        {
            consumedSourceIds.Add(sourceId);
        }
    }

    private static string KindSlug(PlanAnnotationKind kind) =>
        kind.ToString().ToLowerInvariant();

    private static string NormalizeWhitespace(string text) =>
        WhitespacePattern().Replace(text.Trim(), " ");

    private static string NormalizeForClassification(string text)
    {
        var upper = NormalizeWhitespace(text).ToUpperInvariant();
        return HeadingPunctuationPattern().Replace(upper, " ").Trim();
    }

    private static string TrimForEvidence(string text) =>
        NormalizeWhitespace(text) is { Length: > 80 } normalized
            ? $"{normalized[..77]}..."
            : NormalizeWhitespace(text);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"[^\w]+")]
    private static partial Regex HeadingPunctuationPattern();

    [GeneratedRegex(@"^\s*(?:KEYNOTE|NOTE|KN)\s*(?<marker>\d{1,3}|[A-Z])[\.)\:\-]?\s+(?<body>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KeynotePattern();

    [GeneratedRegex(@"^\s*(?<marker>\d{1,3}|[A-Z])[\.)\:\-]?\s+(?<body>[A-Za-z].+)$")]
    private static partial Regex ListMarkerPattern();

    [GeneratedRegex(@"^\s*(?<marker>[A-Z]|\d{1,3})[\.)\:\-]?\s+(?<body>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionRowPattern();

    [GeneratedRegex(@"^[A-Z]{0,3}[- ]?\d{1,4}(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetNumberLikePattern();

    [GeneratedRegex(@"^\s*[\(\[\{]?\s*(?<marker>\d{1,3}|[A-Z])\s*[\)\]\}]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceMarkerPattern();

    private sealed record ParsedAnnotationItem(
        PlanAnnotationItemKind Kind,
        string Text,
        string? Marker);

    private sealed record ReferenceGeometryItem(
        PlanPrimitive Primitive,
        string SourceId);

    private sealed record TextItem(
        int Index,
        TextPrimitive Text,
        string SourceId)
    {
        public string Value => NormalizeWhitespace(Text.Text);

        public PlanRect Bounds => Text.Bounds;
    }
}
