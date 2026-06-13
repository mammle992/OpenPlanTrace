using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed class SheetRegionDetectionStage : IPipelineStage
{
    private const int MaxMainRegionSourcePrimitiveIds = 250;
    private static readonly Regex MetricWholeMillimeterDimensionRegex =
        new(@"^\s*\d{1,3}(?:\s+\d{3})+(?:\s*mm)?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AreaMeasurementRegex =
        new(@"(?i)\b\d+(?:[\.,]\d+)?\s*m(?:2|\^2|²|㎡)\b", RegexOptions.Compiled);

    private static readonly Regex GridEndpointLabelRegex =
        new(@"^(?:[A-Z]{1,3}|\d{1,3}|[A-Z]\d{1,2})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "sheet-regions";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheetRegion = new SheetRegion(
                $"page:{page.Number}:sheet",
                page.Number,
                RegionKind.Sheet,
                page.Bounds,
                Confidence.High)
            {
                Label = "Sheet"
            };

            context.SheetRegions.Add(sheetRegion);

            var titleBlock = DetectTitleBlock(page, context);
            if (titleBlock is not null)
            {
                context.SheetRegions.Add(titleBlock);
            }
            else
            {
                context.AddDiagnostic(
                    "layout.title_block.not_found",
                    DiagnosticSeverity.Info,
                    Name,
                    "No title block region met the density threshold.",
                    page.Number,
                    page.Bounds,
                    Confidence.Low);
            }

            var mainRegion = DetectMainRegion(page, context, titleBlock);
            context.SheetRegions.Add(mainRegion);

            foreach (var classifiedRegion in DetectSecondaryRegions(page, context, mainRegion, titleBlock))
            {
                context.SheetRegions.Add(classifiedRegion);
            }
        }

        return ValueTask.CompletedTask;
    }

    private SheetRegion? DetectTitleBlock(PlanPage page, ScanContext context)
    {
        var bounds = page.Bounds;
        var candidates = new[]
        {
            new CandidateRegion(
                new PlanRect(bounds.Width * 0.64, bounds.Height * 0.62, bounds.Width * 0.36, bounds.Height * 0.38),
                "bottom-right",
                1.5),
            new CandidateRegion(
                new PlanRect(bounds.Width * 0.72, 0, bounds.Width * 0.28, bounds.Height),
                "right",
                0),
            new CandidateRegion(
                new PlanRect(0, bounds.Height * 0.78, bounds.Width, bounds.Height * 0.22),
                "bottom",
                1.0)
        };

        var best = candidates
            .Select(candidate => candidate with
            {
                Score = ScoreRegion(page, candidate.Bounds)
                    + TitleBlockTextBonus(page, candidate.Bounds)
                    + candidate.Bias
                    - AreaPenalty(page, candidate.Bounds)
            })
            .OrderByDescending(candidate => candidate.Score)
            .First();

        if (best.Score < 3.0)
        {
            return null;
        }

        var regionBounds = best.Bounds.ClampTo(page.Bounds);
        var hintBounds = TitleBlockHintBounds(page, best.Bounds);
        if (hintBounds is not null && ShouldRefineTitleBlockBounds(page, best))
        {
            regionBounds = RefineTitleBlockBoundsFromHints(page, best.Bounds, hintBounds.Value);
        }
        else if (!ContainsTitleBlockGeometry(page, best.Bounds)
            && hintBounds is { } fallbackHintBounds)
        {
            regionBounds = RefineTitleBlockBoundsFromHints(page, best.Bounds, fallbackHintBounds);
        }

        var sourceIds = SourceIdsFor(page, context, regionBounds).ToArray();
        var confidence = new Confidence(Math.Min(0.9, 0.45 + (best.Score / 20.0)));

        return new SheetRegion(
            $"page:{page.Number}:title-block",
            page.Number,
            RegionKind.TitleBlock,
            regionBounds,
            confidence)
        {
            Label = $"Title block ({best.Label})",
            SourcePrimitiveIds = sourceIds
        };
    }

    private SheetRegion DetectMainRegion(
        PlanPage page,
        ScanContext context,
        SheetRegion? titleBlock)
    {
        var options = context.Options;
        var sheet = page.Bounds;
        var margin = Math.Max(0, options.SheetMargin);
        var fallback = sheet.Inflate(-margin).ClampTo(sheet);

        if (titleBlock is not null)
        {
            var title = titleBlock.Bounds;
            var fallbackCandidates = new List<PlanRect>();

            if (title.Left > sheet.Width * 0.45)
            {
                fallbackCandidates.Add(PlanRect.FromEdges(
                    fallback.Left,
                    fallback.Top,
                    Math.Min(fallback.Right, title.Left - margin),
                    fallback.Bottom));
            }

            if (title.Top > sheet.Height * 0.45)
            {
                fallbackCandidates.Add(PlanRect.FromEdges(
                    fallback.Left,
                    fallback.Top,
                    fallback.Right,
                    Math.Min(fallback.Bottom, title.Top - margin)));
            }

            fallback = fallbackCandidates
                .Where(candidate => !candidate.IsEmpty && candidate.Area >= sheet.Area * 0.2)
                .OrderByDescending(candidate => candidate.Area)
                .FirstOrDefault(fallback);
        }

        var contentRegion = DetectMainContentRegion(page, context, titleBlock, fallback);
        var main = contentRegion?.Bounds ?? fallback;
        if (contentRegion is not null)
        {
            context.AddDiagnostic(
                "layout.main_region.content_refined",
                DiagnosticSeverity.Info,
                Name,
                "Main floorplan region was cropped from dense drawing content instead of broad sheet extents.",
                page.Number,
                main,
                Confidence.Medium,
                DiagnosticScope.Region,
                contentRegion.SourcePrimitiveIds,
                new Dictionary<string, string>
                {
                    ["candidateCount"] = contentRegion.CandidateCount.ToString(),
                    ["gridEndpointAnchorCount"] = contentRegion.GridEndpointAnchorCount.ToString(),
                    ["sourcePrimitiveIdCount"] = contentRegion.SourcePrimitiveIds.Length.ToString(),
                    ["fallbackBounds"] = FormatBounds(fallback),
                    ["refinedBounds"] = FormatBounds(main)
                });
        }

        return new SheetRegion(
            $"page:{page.Number}:main-floorplan",
            page.Number,
            RegionKind.MainFloorPlan,
            main,
            contentRegion is null
                ? titleBlock is null ? Confidence.Medium : Confidence.High
                : Confidence.High)
        {
            Label = "Main floorplan area",
            SourcePrimitiveIds = contentRegion?.SourcePrimitiveIds ?? Array.Empty<string>()
        };
    }

    private MainContentRegion? DetectMainContentRegion(
        PlanPage page,
        ScanContext context,
        SheetRegion? titleBlock,
        PlanRect fallback)
    {
        var candidates = EnumerateMainContentCandidates(page, context, titleBlock).ToArray();
        if (candidates.Length < 4)
        {
            return null;
        }

        var left = WeightedQuantile(candidates.Select(candidate => new WeightedValue(candidate.Bounds.Left, candidate.Score)), 0.02);
        var top = WeightedQuantile(candidates.Select(candidate => new WeightedValue(candidate.Bounds.Top, candidate.Score)), 0.02);
        var right = WeightedQuantile(candidates.Select(candidate => new WeightedValue(candidate.Bounds.Right, candidate.Score)), 0.98);
        var bottom = WeightedQuantile(candidates.Select(candidate => new WeightedValue(candidate.Bounds.Bottom, candidate.Score)), 0.98);
        var padding = Math.Max(context.Options.SheetMargin, Math.Min(page.Size.Width, page.Size.Height) * 0.025);
        var bounds = PlanRect.FromEdges(left, top, right, bottom)
            .Inflate(padding)
            .ClampTo(page.Bounds);
        var gridEndpointAnchors = EnumerateGridEndpointMainRegionAnchors(page, context, titleBlock).ToArray();
        if (gridEndpointAnchors.Length > 0)
        {
            bounds = PlanRect.Union(
                    bounds,
                    PlanRect.Union(gridEndpointAnchors.Select(anchor => anchor.Bounds))
                        .Inflate(padding)
                        .ClampTo(page.Bounds))
                .ClampTo(page.Bounds);
        }

        if (bounds.IsEmpty
            || bounds.Area < page.Bounds.Area * 0.015
            || bounds.Width < Math.Max(80, page.Size.Width * 0.08)
            || bounds.Height < Math.Max(80, page.Size.Height * 0.08))
        {
            return null;
        }

        if (fallback.Area > 0 && bounds.Area > fallback.Area * 0.97)
        {
            return null;
        }

        if (CrossesTitleBoundary(bounds, titleBlock, page, context.Options))
        {
            return null;
        }

        var sourceIds = candidates
            .Where(candidate => bounds.Contains(candidate.Bounds.Center, context.Options.SheetMargin))
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.SourcePrimitiveId)
            .Concat(gridEndpointAnchors.SelectMany(anchor => anchor.SourcePrimitiveIds))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMainRegionSourcePrimitiveIds)
            .ToArray();

        return new MainContentRegion(bounds, candidates.Length, gridEndpointAnchors.Length, sourceIds);
    }

    private IEnumerable<MainContentAnchor> EnumerateGridEndpointMainRegionAnchors(
        PlanPage page,
        ScanContext context,
        SheetRegion? titleBlock)
    {
        var titleExclusion = titleBlock?.Bounds.Inflate(Math.Max(4, context.Options.SheetMargin));
        var bubbles = page.Primitives
            .Select((primitive, index) => new PrimitiveWithIndex(primitive, index))
            .Where(item => IsGridLabelBubbleGeometry(item.Primitive))
            .Select(item => new GridLabelBubble(
                item.Primitive.Bounds,
                context.PrimitiveId(page.Number, item.Index, item.Primitive)))
            .ToArray();

        var labels = page.Primitives
            .Select((primitive, index) => new PrimitiveWithIndex(primitive, index))
            .Where(item => item.Primitive is TextPrimitive text && IsGridEndpointLabel(text.Text))
            .Select(item =>
            {
                var text = (TextPrimitive)item.Primitive;
                var matchedBubbles = bubbles
                    .Where(bubble => bubble.Bounds.Contains(text.Bounds.Center, 4)
                        || bubble.Bounds.Intersects(text.Bounds.Inflate(4)))
                    .ToArray();

                return new GridEndpointLabel(
                    text,
                    context.PrimitiveId(page.Number, item.Index, text),
                    matchedBubbles);
            })
            .Where(label => label.Bubbles.Length > 0)
            .ToArray();

        if (labels.Length == 0)
        {
            yield break;
        }

        var minimumLength = Math.Max(
            context.Options.MinGridAxisLength,
            Math.Min(page.Size.Width, page.Size.Height) * 0.2);
        var labelDistance = Math.Max(1, context.Options.MaxGridAxisLabelDistance);
        foreach (var line in PrimitiveGeometry.EnumerateLines(page, context))
        {
            if (line.Primitive is not LinePrimitive)
            {
                continue;
            }

            if (!line.Segment.IsHorizontal(context.Options.GeometryTolerance.Distance)
                && !line.Segment.IsVertical(context.Options.GeometryTolerance.Distance))
            {
                continue;
            }

            if (line.Segment.Length < minimumLength || IsSheetFramePrimitive(line.Primitive, page, context.Options))
            {
                continue;
            }

            if (titleExclusion is { } title
                && (title.Contains(line.Segment.Bounds.Center, 2) || OverlapRatio(line.Segment.Bounds, title) > 0.2))
            {
                continue;
            }

            var label = labels
                .Select(item => new
                {
                    Label = item,
                    Distance = Math.Min(
                        item.Text.Bounds.Center.DistanceTo(line.Segment.Start),
                        item.Text.Bounds.Center.DistanceTo(line.Segment.End))
                })
                .Where(item => item.Distance <= labelDistance)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (label is null)
            {
                continue;
            }

            var bounds = PlanRect.Union(
                new[] { line.Segment.Bounds, label.Label.Text.Bounds }
                    .Concat(label.Label.Bubbles.Select(bubble => bubble.Bounds)));
            yield return new MainContentAnchor(
                bounds,
                new[] { line.PrimitiveId, label.Label.SourcePrimitiveId }
                    .Concat(label.Label.Bubbles.Select(bubble => bubble.SourcePrimitiveId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
        }
    }

    private static bool CrossesTitleBoundary(
        PlanRect bounds,
        SheetRegion? titleBlock,
        PlanPage page,
        ScannerOptions options)
    {
        if (titleBlock is null)
        {
            return false;
        }

        var title = titleBlock.Bounds;
        var margin = Math.Max(0, options.SheetMargin);
        return title.Left > page.Size.Width * 0.45 && bounds.Right > title.Left - margin
            || title.Top > page.Size.Height * 0.45 && bounds.Bottom > title.Top - margin;
    }

    private IEnumerable<MainContentCandidate> EnumerateMainContentCandidates(
        PlanPage page,
        ScanContext context,
        SheetRegion? titleBlock)
    {
        var titleExclusion = titleBlock?.Bounds.Inflate(Math.Max(4, context.Options.SheetMargin));
        var dimensionTextExclusions = page.Primitives
            .OfType<TextPrimitive>()
            .Where(text => LooksLikeDimensionText(text.Text))
            .Select(text => text.Bounds.Inflate(Math.Max(12, Math.Min(page.Size.Width, page.Size.Height) * 0.018)))
            .ToArray();

        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            var bounds = primitive.Bounds;
            if (bounds.IsEmpty || !page.Bounds.Intersects(bounds))
            {
                continue;
            }

            if (titleExclusion is { } title
                && (title.Contains(bounds.Center, 2) || OverlapRatio(bounds, title) > 0.35))
            {
                continue;
            }

            if (IsSheetFramePrimitive(primitive, page, context.Options))
            {
                continue;
            }

            var score = MainContentScore(primitive, page, context.Options, dimensionTextExclusions);
            if (score <= 0)
            {
                continue;
            }

            yield return new MainContentCandidate(
                bounds,
                score,
                context.PrimitiveId(page.Number, index, primitive));
        }
    }

    private static double MainContentScore(
        PlanPrimitive primitive,
        PlanPage page,
        ScannerOptions options,
        IReadOnlyList<PlanRect> dimensionTextExclusions)
    {
        return primitive switch
        {
            LinePrimitive line => LineMainContentScore(line, page, options, dimensionTextExclusions),
            RectanglePrimitive rectangle => ClosedGeometryMainContentScore(rectangle.Bounds, page),
            PolylinePrimitive polyline => ClosedGeometryMainContentScore(polyline.Bounds, page),
            ArcPrimitive arc => ArcMainContentScore(arc.Bounds, page),
            _ => 0
        };
    }

    private static double LineMainContentScore(
        LinePrimitive line,
        PlanPage page,
        ScannerOptions options,
        IReadOnlyList<PlanRect> dimensionTextExclusions)
    {
        var length = line.Segment.Length;
        if (length < Math.Max(2, options.MinWallFragmentLength))
        {
            return 0;
        }

        if (IsNearDimensionText(line.Bounds, dimensionTextExclusions)
            && !LooksLikeWallLayer(line))
        {
            return 0;
        }

        if (length > Math.Max(page.Size.Width, page.Size.Height) * 0.82)
        {
            return 0;
        }

        return Math.Clamp(length / Math.Max(1, options.MinWallLength), 0.2, 6.0);
    }

    private static double ClosedGeometryMainContentScore(PlanRect bounds, PlanPage page)
    {
        if (bounds.IsEmpty || bounds.Area > page.Bounds.Area * 0.2)
        {
            return 0;
        }

        if (bounds.Width > page.Size.Width * 0.8 || bounds.Height > page.Size.Height * 0.8)
        {
            return 0;
        }

        var span = Math.Max(bounds.Width, bounds.Height);
        if (span < 2)
        {
            return 0;
        }

        return Math.Clamp(span / 24.0, 0.25, 4.0);
    }

    private static double ArcMainContentScore(PlanRect bounds, PlanPage page)
    {
        if (bounds.IsEmpty || bounds.Area > page.Bounds.Area * 0.05)
        {
            return 0;
        }

        var span = Math.Max(bounds.Width, bounds.Height);
        return span < 2 ? 0 : Math.Clamp(span / 24.0, 0.25, 3.0);
    }

    private static bool IsSheetFramePrimitive(PlanPrimitive primitive, PlanPage page, ScannerOptions options)
    {
        var bounds = primitive.Bounds;
        if (bounds.Width >= page.Size.Width * 0.88 && bounds.Height >= page.Size.Height * 0.88)
        {
            return true;
        }

        if (primitive is not LinePrimitive line)
        {
            return false;
        }

        var margin = Math.Max(options.SheetMargin * 2, Math.Min(page.Size.Width, page.Size.Height) * 0.025);
        if (line.Segment.IsHorizontal(options.WallSnapTolerance)
            && line.Segment.Length >= page.Size.Width * 0.6
            && (Math.Abs(line.Segment.Start.Y - page.Bounds.Top) <= margin
                || Math.Abs(line.Segment.Start.Y - page.Bounds.Bottom) <= margin))
        {
            return true;
        }

        return line.Segment.IsVertical(options.WallSnapTolerance)
            && line.Segment.Length >= page.Size.Height * 0.6
            && (Math.Abs(line.Segment.Start.X - page.Bounds.Left) <= margin
                || Math.Abs(line.Segment.Start.X - page.Bounds.Right) <= margin);
    }

    private static bool IsGridEndpointLabel(string text) =>
        GridEndpointLabelRegex.IsMatch(text.Trim());

    private static bool IsGridLabelBubbleGeometry(PlanPrimitive primitive)
    {
        var bounds = primitive.Bounds;
        if (bounds.IsEmpty || bounds.Width < 8 || bounds.Height < 8 || bounds.Width > 90 || bounds.Height > 90)
        {
            return false;
        }

        var aspectRatio = bounds.Width / Math.Max(1, bounds.Height);
        if (aspectRatio is < 0.45 or > 2.25)
        {
            return false;
        }

        return primitive switch
        {
            RectanglePrimitive => true,
            PolylinePrimitive { Closed: true } => true,
            ArcPrimitive arc => Math.Abs(arc.SweepAngleRadians) >= Math.PI * 1.65,
            _ => false
        };
    }

    private static bool IsNearDimensionText(PlanRect bounds, IReadOnlyList<PlanRect> dimensionTextExclusions) =>
        dimensionTextExclusions.Any(exclusion => exclusion.Intersects(bounds.Inflate(2)));

    private static bool LooksLikeWallLayer(PlanPrimitive primitive)
    {
        var layer = primitive.Source.Layer ?? primitive.Layer;
        return !string.IsNullOrWhiteSpace(layer)
            && (layer.Contains("wall", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("vegg", StringComparison.OrdinalIgnoreCase)
                || layer.Contains("a-wall", StringComparison.OrdinalIgnoreCase));
    }

    private static double WeightedQuantile(IEnumerable<WeightedValue> values, double quantile)
    {
        var ordered = values
            .Where(value => value.Weight > 0)
            .OrderBy(value => value.Value)
            .ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var target = ordered.Sum(value => value.Weight) * Math.Clamp(quantile, 0, 1);
        var cumulative = 0.0;
        foreach (var value in ordered)
        {
            cumulative += value.Weight;
            if (cumulative >= target)
            {
                return value.Value;
            }
        }

        return ordered[^1].Value;
    }

    private IEnumerable<SheetRegion> DetectSecondaryRegions(
        PlanPage page,
        ScanContext context,
        SheetRegion mainRegion,
        SheetRegion? titleBlock)
    {
        var titleBounds = titleBlock?.Bounds;
        var outsideMain = page.Primitives
            .Select((primitive, index) => new PrimitiveWithIndex(primitive, index))
            .Where(item => !mainRegion.Bounds.Contains(item.Primitive.Bounds.Center, context.Options.SheetMargin))
            .ToArray();

        var dimensionText = outsideMain
            .Where(item => item.Primitive is TextPrimitive text && LooksLikeDimensionText(text.Text))
            .ToArray();

        if (dimensionText.Length >= 2)
        {
            var bounds = PlanRect.Union(dimensionText.Select(item => item.Primitive.Bounds)).Inflate(4).ClampTo(page.Bounds);
            yield return new SheetRegion(
                $"page:{page.Number}:dimensions",
                page.Number,
                RegionKind.Dimensions,
                bounds,
                Confidence.Medium)
            {
                Label = "Dimension annotations",
                SourcePrimitiveIds = dimensionText
                    .Select(item => context.PrimitiveId(page.Number, item.Index, item.Primitive))
                    .ToArray()
            };
        }

        var noteText = outsideMain
            .Where(item => item.Primitive is TextPrimitive text
                && text.Text.Trim().Length >= 8
                && !LooksLikeDimensionText(text.Text)
                && (titleBounds is null || !titleBounds.Value.Contains(text.Bounds.Center)))
            .ToArray();

        if (noteText.Length >= 2)
        {
            var bounds = PlanRect.Union(noteText.Select(item => item.Primitive.Bounds)).Inflate(6).ClampTo(page.Bounds);
            yield return new SheetRegion(
                $"page:{page.Number}:notes",
                page.Number,
                RegionKind.Notes,
                bounds,
                Confidence.Medium)
            {
                Label = "Notes",
                SourcePrimitiveIds = noteText
                    .Select(item => context.PrimitiveId(page.Number, item.Index, item.Primitive))
                    .ToArray()
            };
        }

        var compactClosedGeometry = outsideMain
            .Where(item => item.Primitive is RectanglePrimitive
                || item.Primitive is PolylinePrimitive { Closed: true })
            .Where(item => item.Primitive.Bounds.Area > 100
                && item.Primitive.Bounds.Area < page.Bounds.Area * 0.08)
            .ToArray();

        if (compactClosedGeometry.Length > 0)
        {
            var bounds = PlanRect.Union(compactClosedGeometry.Select(item => item.Primitive.Bounds)).Inflate(4).ClampTo(page.Bounds);
            yield return new SheetRegion(
                $"page:{page.Number}:key-plan",
                page.Number,
                RegionKind.KeyPlan,
                bounds,
                new Confidence(0.45))
            {
                Label = "Key plan candidate",
                SourcePrimitiveIds = compactClosedGeometry
                    .Select(item => context.PrimitiveId(page.Number, item.Index, item.Primitive))
                    .ToArray()
            };
        }
    }

    private static double ScoreRegion(PlanPage page, PlanRect region)
    {
        var score = 0.0;
        foreach (var primitive in page.Primitives.Where(primitive => region.Contains(primitive.Bounds.Center) || primitive.Bounds.Intersects(region)))
        {
            var primitiveScore = primitive.Kind switch
            {
                PlanPrimitiveKind.Text => 1.4,
                PlanPrimitiveKind.Rectangle => 1.0,
                PlanPrimitiveKind.Line => 0.35,
                PlanPrimitiveKind.Polyline => 0.6,
                _ => 0.1
            };

            score += region.Contains(primitive.Bounds.Center) ? primitiveScore : primitiveScore * 0.25;
        }

        return score;
    }

    private static double TitleBlockTextBonus(PlanPage page, PlanRect region)
    {
        var titleHints = page.Primitives
            .OfType<TextPrimitive>()
            .Where(text => region.Contains(text.Bounds.Center) || text.Bounds.Intersects(region))
            .Select(text => text.Text.Trim())
            .Where(LooksLikeTitleBlockText)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Count();

        return titleHints * 1.35;
    }

    private static bool ContainsTitleBlockGeometry(PlanPage page, PlanRect region) =>
        page.Primitives.Any(primitive =>
            primitive.Bounds.Intersects(region)
            && primitive.Bounds.Area >= page.Bounds.Area * 0.002
            && primitive is RectanglePrimitive or PolylinePrimitive { Closed: true });

    private static PlanRect? TitleBlockHintBounds(PlanPage page, PlanRect region)
    {
        var hints = page.Primitives
            .OfType<TextPrimitive>()
            .Where(text => region.Contains(text.Bounds.Center) || text.Bounds.Intersects(region))
            .Where(text => LooksLikeTitleBlockText(text.Text.Trim()))
            .Select(text => text.Bounds)
            .ToArray();

        return hints.Length == 0 ? null : PlanRect.Union(hints);
    }

    private static bool ShouldRefineTitleBlockBounds(PlanPage page, CandidateRegion candidate) =>
        candidate.Label is "right" or "bottom"
        || candidate.Bounds.Area >= page.Bounds.Area * 0.18;

    private static PlanRect RefineTitleBlockBoundsFromHints(PlanPage page, PlanRect candidateRegion, PlanRect hintBounds)
    {
        var padding = Math.Max(10, Math.Min(page.Size.Width, page.Size.Height) * 0.035);
        var search = hintBounds.Inflate(padding).ClampTo(candidateRegion.ClampTo(page.Bounds));
        var nearbyGeometry = page.Primitives
            .Where(primitive => primitive.Bounds.Intersects(search)
                && primitive is LinePrimitive or RectanglePrimitive or PolylinePrimitive { Closed: true })
            .Where(primitive => primitive.Bounds.Area < page.Bounds.Area * 0.12)
            .Where(primitive => primitive.Bounds.Width <= page.Size.Width * 0.35
                && primitive.Bounds.Height <= page.Size.Height * 0.25)
            .Select(primitive => primitive.Bounds)
            .ToArray();

        var bounds = nearbyGeometry.Length == 0
            ? hintBounds
            : PlanRect.Union(nearbyGeometry.Append(hintBounds));

        return bounds
            .Inflate(Math.Max(4, padding * 0.25))
            .ClampTo(candidateRegion.ClampTo(page.Bounds));
    }

    private static double AreaPenalty(PlanPage page, PlanRect region) =>
        page.Bounds.Area <= 0 ? 0 : (region.Area / page.Bounds.Area) * 8.0;

    private static IEnumerable<string> SourceIdsFor(PlanPage page, ScanContext context, PlanRect region)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (primitive.Bounds.Intersects(region))
            {
                yield return context.PrimitiveId(page.Number, index, primitive);
            }
        }
    }

    private static bool LooksLikeDimensionText(string text)
    {
        var trimmed = text.Trim();
        if (LooksLikeAreaMeasurementText(trimmed))
        {
            return false;
        }

        if (MetricWholeMillimeterDimensionRegex.IsMatch(trimmed))
        {
            return true;
        }

        return trimmed.Any(char.IsDigit)
            && (trimmed.Contains('\'')
                || trimmed.Contains('"')
                || trimmed.Contains("mm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("cm", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains('m')
                || trimmed.Contains('x')
                || trimmed.Contains('X'));
    }

    private static bool LooksLikeAreaMeasurementText(string text) =>
        AreaMeasurementRegex.IsMatch(text);

    private static bool LooksLikeTitleBlockText(string text)
    {
        if (text.Length is < 2 or > 80)
        {
            return false;
        }

        return text.Contains("scale", StringComparison.OrdinalIgnoreCase)
            || text.Contains("sheet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("project", StringComparison.OrdinalIgnoreCase)
            || text.Contains("prosjekt", StringComparison.OrdinalIgnoreCase)
            || text.Contains("drawn", StringComparison.OrdinalIgnoreCase)
            || text.Contains("checked", StringComparison.OrdinalIgnoreCase)
            || text.Contains("kontroll", StringComparison.OrdinalIgnoreCase)
            || text.Contains("revision", StringComparison.OrdinalIgnoreCase)
            || text.Contains("revisjon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("date", StringComparison.OrdinalIgnoreCase);
    }

    private static double OverlapRatio(PlanRect bounds, PlanRect region) =>
        bounds.Area <= 0 ? 0 : bounds.OverlapArea(region) / bounds.Area;

    private static string FormatBounds(PlanRect bounds) =>
        $"{Math.Round(bounds.X, 3)},{Math.Round(bounds.Y, 3)},{Math.Round(bounds.Width, 3)},{Math.Round(bounds.Height, 3)}";

    private sealed record CandidateRegion(PlanRect Bounds, string Label, double Bias)
    {
        public double Score { get; init; }
    }

    private sealed record PrimitiveWithIndex(PlanPrimitive Primitive, int Index);

    private sealed record MainContentCandidate(PlanRect Bounds, double Score, string SourcePrimitiveId);

    private sealed record MainContentAnchor(PlanRect Bounds, string[] SourcePrimitiveIds);

    private sealed record GridEndpointLabel(
        TextPrimitive Text,
        string SourcePrimitiveId,
        GridLabelBubble[] Bubbles);

    private sealed record GridLabelBubble(PlanRect Bounds, string SourcePrimitiveId);

    private sealed record MainContentRegion(
        PlanRect Bounds,
        int CandidateCount,
        int GridEndpointAnchorCount,
        string[] SourcePrimitiveIds);

    private readonly record struct WeightedValue(double Value, double Weight);
}
