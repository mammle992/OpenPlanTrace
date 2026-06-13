using System.Text.RegularExpressions;

namespace OpenPlanTrace;

internal sealed partial class TitleBlockAnalysisStage : IPipelineStage
{
    private static readonly LabelDefinition[] Labels =
    {
        new(TitleBlockFieldKind.ProjectName, "project", ExactProjectLabel(), InlineProjectLabel()),
        new(TitleBlockFieldKind.SheetNumber, "sheet number", ExactSheetNumberLabel(), InlineSheetNumberLabel()),
        new(TitleBlockFieldKind.SheetTitle, "sheet title", ExactSheetTitleLabel(), InlineSheetTitleLabel()),
        new(TitleBlockFieldKind.Revision, "revision", ExactRevisionLabel(), InlineRevisionLabel()),
        new(TitleBlockFieldKind.IssueDate, "issue date", ExactIssueDateLabel(), InlineIssueDateLabel()),
        new(TitleBlockFieldKind.Scale, "scale", ExactScaleLabel(), InlineScaleLabel()),
        new(TitleBlockFieldKind.DrawnBy, "drawn by", ExactDrawnByLabel(), InlineDrawnByLabel()),
        new(TitleBlockFieldKind.CheckedBy, "checked by", ExactCheckedByLabel(), InlineCheckedByLabel()),
        new(TitleBlockFieldKind.Discipline, "discipline", ExactDisciplineLabel(), InlineDisciplineLabel())
    };

    public string Name => "title-block-analysis";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var region in context.SheetRegions.Where(region => region.Kind == RegionKind.TitleBlock))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = context.Document.Pages.FirstOrDefault(item => item.Number == region.PageNumber);
            if (page is null)
            {
                continue;
            }

            var analysis = AnalyzeRegion(context, page, region);
            if (analysis.Fields.Count > 0)
            {
                context.TitleBlocks.Add(analysis);
                context.AddDiagnostic(
                    "title_block.fields.detected",
                    DiagnosticSeverity.Info,
                    Name,
                    $"Extracted {analysis.Fields.Count} title-block field(s).",
                    page.Number,
                    region.Bounds,
                    analysis.Confidence,
                    DiagnosticScope.TitleBlock,
                    analysis.SourcePrimitiveIds,
                    new Dictionary<string, string>
                    {
                        ["regionId"] = region.Id,
                        ["fieldKinds"] = string.Join(",", analysis.Fields.Select(field => field.Kind).Distinct())
                    });
            }
            else
            {
                context.AddDiagnostic(
                    "title_block.fields.not_found",
                    DiagnosticSeverity.Warning,
                    Name,
                    "The title-block region was detected, but no structured metadata fields were found.",
                    page.Number,
                    region.Bounds,
                    Confidence.Low,
                    DiagnosticScope.TitleBlock,
                    region.SourcePrimitiveIds,
                    new Dictionary<string, string> { ["regionId"] = region.Id });
            }
        }

        return ValueTask.CompletedTask;
    }

    private TitleBlockAnalysis AnalyzeRegion(ScanContext context, PlanPage page, SheetRegion region)
    {
        var texts = EnumerateTextItems(context, page, region).ToArray();
        if (texts.Length == 0)
        {
            context.AddDiagnostic(
                "title_block.text.not_found",
                DiagnosticSeverity.Warning,
                Name,
                "The title-block region has no text primitives to analyze.",
                page.Number,
                region.Bounds,
                Confidence.Low,
                DiagnosticScope.TitleBlock,
                region.SourcePrimitiveIds,
                new Dictionary<string, string> { ["regionId"] = region.Id });

            return new TitleBlockAnalysis(
                region.Id,
                page.Number,
                region.Bounds,
                Confidence.Low,
                Array.Empty<TitleBlockField>(),
                region.SourcePrimitiveIds);
        }

        var candidates = new List<FieldCandidate>();
        AddInlineLabelCandidates(texts, candidates);
        AddNearbyLabelCandidates(texts, region, candidates);
        AddPatternCandidates(texts, candidates);
        AddFallbackTitleCandidate(texts, candidates);

        var fields = SelectFields(candidates, context, page.Number, region).ToArray();
        var sourceIds = fields
            .SelectMany(field => field.SourcePrimitiveIds)
            .Concat(region.SourcePrimitiveIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var confidence = fields.Length == 0
            ? Confidence.Low
            : new Confidence(Math.Min(0.92, 0.45 + fields.Average(field => field.Confidence.Value) * 0.45));

        if (!fields.Any(field => field.Kind == TitleBlockFieldKind.SheetNumber))
        {
            context.AddDiagnostic(
                "title_block.sheet_number.not_found",
                DiagnosticSeverity.Info,
                Name,
                "No sheet number was identified in the title block.",
                page.Number,
                region.Bounds,
                Confidence.Low,
                DiagnosticScope.TitleBlock,
                sourceIds,
                new Dictionary<string, string> { ["regionId"] = region.Id });
        }

        return new TitleBlockAnalysis(
            region.Id,
            page.Number,
            region.Bounds,
            confidence,
            fields,
            sourceIds);
    }

    private static IEnumerable<TextItem> EnumerateTextItems(ScanContext context, PlanPage page, SheetRegion region)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not TextPrimitive text)
            {
                continue;
            }

            if (!region.Bounds.Contains(text.Bounds.Center, 2) && !region.Bounds.Intersects(text.Bounds, 2))
            {
                continue;
            }

            var value = NormalizeText(text.Text);
            if (value.Length == 0)
            {
                continue;
            }

            yield return new TextItem(
                text,
                index,
                context.PrimitiveId(page.Number, index, text),
                value,
                text.Bounds);
        }
    }

    private static void AddInlineLabelCandidates(IReadOnlyList<TextItem> texts, List<FieldCandidate> candidates)
    {
        foreach (var text in texts)
        {
            foreach (var label in Labels)
            {
                if (label.ExactPattern.IsMatch(text.Text))
                {
                    continue;
                }

                var match = label.InlinePattern.Match(text.Text);
                if (!match.Success)
                {
                    continue;
                }

                var value = CleanValue(match.Groups["value"].Value);
                value = NormalizeFieldValue(label.Kind, value);
                if (!IsUsefulValue(label.Kind, value))
                {
                    continue;
                }

                candidates.Add(
                    new FieldCandidate(
                        label.Kind,
                        value,
                        text.Text,
                        text.Bounds,
                        new Confidence(0.82),
                        new[] { text.SourceId },
                        new[]
                        {
                            $"Inline title-block label '{label.Label}' identified this field.",
                            $"Source text: {text.Text}"
                        },
                        0.82));
            }
        }
    }

    private static void AddNearbyLabelCandidates(
        IReadOnlyList<TextItem> texts,
        SheetRegion region,
        List<FieldCandidate> candidates)
    {
        foreach (var labelText in texts)
        {
            var label = Labels.FirstOrDefault(definition => definition.ExactPattern.IsMatch(labelText.Text));
            if (label is null)
            {
                continue;
            }

            var valueText = FindNearestValueText(labelText, texts, region.Bounds);
            if (valueText is null)
            {
                continue;
            }

            var value = NormalizeFieldValue(label.Kind, CleanValue(valueText.Text));
            if (!IsUsefulValue(label.Kind, value))
            {
                continue;
            }

            candidates.Add(
                new FieldCandidate(
                    label.Kind,
                    value,
                    valueText.Text,
                    PlanRect.Union(labelText.Bounds, valueText.Bounds),
                    new Confidence(0.78),
                    new[] { labelText.SourceId, valueText.SourceId },
                    new[]
                    {
                        $"Nearby title-block label '{label.Label}' was paired with adjacent text.",
                        $"Label text: {labelText.Text}",
                        $"Value text: {valueText.Text}"
                    },
                    0.78));
        }
    }

    private static void AddPatternCandidates(IReadOnlyList<TextItem> texts, List<FieldCandidate> candidates)
    {
        foreach (var text in texts)
        {
            AddScaleCandidate(text, candidates);
            AddDateCandidate(text, candidates);
            AddSheetNumberCandidate(text, candidates);
            AddRevisionCandidate(text, candidates);
        }
    }

    private static void AddScaleCandidate(TextItem text, List<FieldCandidate> candidates)
    {
        var match = ScalePattern().Match(text.Text);
        if (!match.Success)
        {
            return;
        }

        var value = NormalizeScale(match.Groups["value"].Value);
        candidates.Add(
            new FieldCandidate(
                TitleBlockFieldKind.Scale,
                value,
                text.Text,
                text.Bounds,
                new Confidence(text.Text.Contains("scale", StringComparison.OrdinalIgnoreCase) ? 0.82 : 0.68),
                new[] { text.SourceId },
                new[] { "Scale text pattern matched inside the title block.", $"Source text: {text.Text}" },
                text.Text.Contains("scale", StringComparison.OrdinalIgnoreCase) ? 0.82 : 0.68));
    }

    private static void AddDateCandidate(TextItem text, List<FieldCandidate> candidates)
    {
        var match = DatePattern().Match(text.Text);
        if (!match.Success)
        {
            return;
        }

        candidates.Add(
            new FieldCandidate(
                TitleBlockFieldKind.IssueDate,
                NormalizeText(match.Groups["date"].Value),
                text.Text,
                text.Bounds,
                new Confidence(text.Text.Contains("date", StringComparison.OrdinalIgnoreCase) ? 0.78 : 0.66),
                new[] { text.SourceId },
                new[] { "Date pattern matched inside the title block.", $"Source text: {text.Text}" },
                text.Text.Contains("date", StringComparison.OrdinalIgnoreCase) ? 0.78 : 0.66));
    }

    private static void AddSheetNumberCandidate(TextItem text, List<FieldCandidate> candidates)
    {
        if (LooksLikeNonSheetIdentifier(text.Text))
        {
            return;
        }

        var match = SheetNumberPattern().Match(text.Text);
        if (!match.Success)
        {
            return;
        }

        candidates.Add(
            new FieldCandidate(
                TitleBlockFieldKind.SheetNumber,
                NormalizeSheetNumber(match.Groups["sheet"].Value),
                text.Text,
                text.Bounds,
                new Confidence(text.Text.Contains("sheet", StringComparison.OrdinalIgnoreCase) ? 0.78 : 0.7),
                new[] { text.SourceId },
                new[] { "Sheet-number pattern matched inside the title block.", $"Source text: {text.Text}" },
                text.Text.Contains("sheet", StringComparison.OrdinalIgnoreCase) ? 0.78 : 0.7));
    }

    private static void AddRevisionCandidate(TextItem text, List<FieldCandidate> candidates)
    {
        var match = RevisionPattern().Match(text.Text);
        if (!match.Success)
        {
            return;
        }

        candidates.Add(
            new FieldCandidate(
                TitleBlockFieldKind.Revision,
                CleanValue(match.Groups["rev"].Value),
                text.Text,
                text.Bounds,
                Confidence.Medium,
                new[] { text.SourceId },
                new[] { "Revision label pattern matched inside the title block.", $"Source text: {text.Text}" },
                0.65));
    }

    private static void AddFallbackTitleCandidate(IReadOnlyList<TextItem> texts, List<FieldCandidate> candidates)
    {
        if (candidates.Any(candidate => candidate.Kind == TitleBlockFieldKind.SheetTitle))
        {
            return;
        }

        var usedSourceIds = candidates
            .SelectMany(candidate => candidate.SourcePrimitiveIds)
            .ToHashSet(StringComparer.Ordinal);

        var title = texts
            .Where(text => text.Text.Length >= 6)
            .Where(text => !usedSourceIds.Contains(text.SourceId))
            .Where(text => !Labels.Any(label => label.ExactPattern.IsMatch(text.Text)))
            .Where(text => !LooksLikeStructuredValue(text.Text))
            .OrderByDescending(text => TitleTextScore(text.Text))
            .FirstOrDefault();

        if (title is null)
        {
            return;
        }

        candidates.Add(
            new FieldCandidate(
                TitleBlockFieldKind.SheetTitle,
                title.Text,
                title.Text,
                title.Bounds,
                new Confidence(0.48),
                new[] { title.SourceId },
                new[] { "Longest non-field title-block text was retained as a low-confidence sheet-title candidate." },
                0.48));
    }

    private IEnumerable<TitleBlockField> SelectFields(
        IReadOnlyList<FieldCandidate> candidates,
        ScanContext context,
        int pageNumber,
        SheetRegion region)
    {
        var deduplicated = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Value))
            .GroupBy(
                candidate => $"{candidate.Kind}:{candidate.Value}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .ToArray();

        foreach (var group in deduplicated.GroupBy(candidate => candidate.Kind))
        {
            var ordered = group
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Confidence.Value)
                .ToArray();
            var selected = ordered[0];

            if (ordered.Length > 1
                && !string.Equals(ordered[0].Value, ordered[1].Value, StringComparison.OrdinalIgnoreCase)
                && ordered[0].Score - ordered[1].Score < 0.16)
            {
                context.AddDiagnostic(
                    "title_block.field.ambiguous",
                    DiagnosticSeverity.Info,
                    Name,
                    $"Multiple plausible {group.Key} values were found in the title block; the highest-confidence value was selected.",
                    pageNumber,
                    region.Bounds,
                    selected.Confidence,
                    DiagnosticScope.TitleBlock,
                    ordered.Take(2).SelectMany(candidate => candidate.SourcePrimitiveIds),
                    new Dictionary<string, string>
                    {
                        ["regionId"] = region.Id,
                        ["kind"] = group.Key.ToString(),
                        ["selected"] = selected.Value,
                        ["alternate"] = ordered[1].Value
                    });
            }

            yield return selected.ToField(pageNumber);
        }
    }

    private static TextItem? FindNearestValueText(TextItem label, IReadOnlyList<TextItem> texts, PlanRect regionBounds)
    {
        var sameRow = texts
            .Where(text => !ReferenceEquals(text, label))
            .Where(text => !LooksLikeLabelOnly(text.Text))
            .Where(text => text.Bounds.Left >= label.Bounds.Right - 2)
            .Where(text => VerticalOverlapRatio(label.Bounds, text.Bounds) >= 0.35
                || Math.Abs(text.Bounds.Center.Y - label.Bounds.Center.Y) <= Math.Max(6, label.Bounds.Height))
            .Select(text => new
            {
                Text = text,
                Score = (text.Bounds.Left - label.Bounds.Right)
                    + Math.Abs(text.Bounds.Center.Y - label.Bounds.Center.Y) * 2
            })
            .Where(candidate => candidate.Score <= Math.Max(220, regionBounds.Width * 0.75))
            .OrderBy(candidate => candidate.Score)
            .Select(candidate => candidate.Text)
            .FirstOrDefault();

        if (sameRow is not null)
        {
            return sameRow;
        }

        return texts
            .Where(text => !ReferenceEquals(text, label))
            .Where(text => !LooksLikeLabelOnly(text.Text))
            .Where(text => text.Bounds.Top >= label.Bounds.Bottom - 2)
            .Where(text => HorizontalOverlapRatio(label.Bounds.Inflate(12, 0), text.Bounds) >= 0.25
                || Math.Abs(text.Bounds.Left - label.Bounds.Left) <= 24)
            .Select(text => new
            {
                Text = text,
                Score = (text.Bounds.Top - label.Bounds.Bottom)
                    + Math.Abs(text.Bounds.Left - label.Bounds.Left) * 0.6
            })
            .Where(candidate => candidate.Score <= Math.Max(100, regionBounds.Height * 0.45))
            .OrderBy(candidate => candidate.Score)
            .Select(candidate => candidate.Text)
            .FirstOrDefault();
    }

    private static bool LooksLikeLabelOnly(string text) =>
        Labels.Any(label => label.ExactPattern.IsMatch(text));

    private static bool LooksLikeStructuredValue(string text) =>
        ScalePattern().IsMatch(text)
        || DatePattern().IsMatch(text)
        || RevisionPattern().IsMatch(text)
        || SheetNumberPattern().IsMatch(text)
        || Labels.Any(label => label.InlinePattern.IsMatch(text));

    private static bool LooksLikeNonSheetIdentifier(string text) =>
        text.Contains("rev", StringComparison.OrdinalIgnoreCase)
        || text.Contains("date", StringComparison.OrdinalIgnoreCase)
        || text.Contains("scale", StringComparison.OrdinalIgnoreCase)
        || text.Contains("project", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsefulValue(TitleBlockFieldKind kind, string value)
    {
        if (value.Length == 0
            || value.Length > (kind is TitleBlockFieldKind.ProjectName or TitleBlockFieldKind.SheetTitle ? 120 : 48)
            || LooksLikeLabelOnly(value))
        {
            return false;
        }

        return kind switch
        {
            TitleBlockFieldKind.SheetNumber => SheetNumberPattern().IsMatch(value),
            TitleBlockFieldKind.IssueDate => DatePattern().IsMatch(value),
            TitleBlockFieldKind.Scale => ScalePattern().IsMatch(value),
            TitleBlockFieldKind.Revision => value.Length <= 12,
            _ => true
        };
    }

    private static string NormalizeFieldValue(TitleBlockFieldKind kind, string value) =>
        kind switch
        {
            TitleBlockFieldKind.SheetNumber => NormalizeSheetNumber(value),
            TitleBlockFieldKind.Scale => NormalizeScale(value),
            _ => CleanValue(value)
        };

    private static string NormalizeSheetNumber(string value) =>
        Regex.Replace(CleanValue(value).ToUpperInvariant(), @"\s+", string.Empty);

    private static string NormalizeScale(string value)
    {
        var cleaned = CleanValue(value);
        if (cleaned.Equals("NOT TO SCALE", StringComparison.OrdinalIgnoreCase))
        {
            return "NTS";
        }

        return RatioWhitespacePattern().Replace(cleaned.ToUpperInvariant(), ":");
    }

    private static string CleanValue(string value) =>
        NormalizeText(value).Trim(' ', ':', '-', '#', '.', '|');

    private static string NormalizeText(string value) =>
        WhitespacePattern().Replace(value ?? string.Empty, " ").Trim();

    private static double TitleTextScore(string text)
    {
        var letterCount = text.Count(char.IsLetter);
        var upperCount = text.Count(char.IsUpper);
        var digitPenalty = text.Count(char.IsDigit) * 1.5;
        return text.Length + letterCount + (upperCount * 0.35) - digitPenalty;
    }

    private static double VerticalOverlapRatio(PlanRect first, PlanRect second)
    {
        var overlap = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return overlap / Math.Max(1, Math.Min(first.Height, second.Height));
    }

    private static double HorizontalOverlapRatio(PlanRect first, PlanRect second)
    {
        var overlap = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        return overlap / Math.Max(1, Math.Min(first.Width, second.Width));
    }

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?i)\b(?<value>(?:1\s*:\s*\d+(?:[\.,]\d+)?)|(?:\d+(?:[\.,]\d+)?|\d+\s*/\s*\d+)\s*(?:""|in|inch|inches)\s*=\s*\d+(?:[\.,]\d+)?\s*(?:'|ft|foot|feet)|NTS|NOT\s+TO\s+SCALE)\b", RegexOptions.Compiled)]
    private static partial Regex ScalePattern();

    [GeneratedRegex(@"(?i)\b(?<date>(?:\d{4}[-/]\d{1,2}[-/]\d{1,2})|(?:\d{1,2}[-/.]\d{1,2}[-/.]\d{2,4})|(?:\d{1,2}\s+(?:JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)[A-Z]*\s+\d{2,4}))\b", RegexOptions.Compiled)]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?i)\b(?<sheet>[A-Z]{1,4}\s*[-.]\s*\d{1,4}(?:\.\d+)?|[A-Z]{1,3}\d{2,4})\b", RegexOptions.Compiled)]
    private static partial Regex SheetNumberPattern();

    [GeneratedRegex(@"(?i)\b(?:rev(?:ision)?|issue)\s*[:#-]?\s*(?<rev>[A-Z0-9][A-Z0-9._-]{0,8})\b", RegexOptions.Compiled)]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"\s*:\s*", RegexOptions.Compiled)]
    private static partial Regex RatioWhitespacePattern();

    [GeneratedRegex(@"(?i)^\s*(?:project(?:\s+name)?|job|client)\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactProjectLabel();

    [GeneratedRegex(@"(?i)^\s*(?:project(?:\s+name)?|job|client)\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineProjectLabel();

    [GeneratedRegex(@"(?i)^\s*(?:sheet(?:\s*(?:no|number|#))?|drawing\s*(?:no|number|#)|dwg\s*(?:no|number|#))\.?\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactSheetNumberLabel();

    [GeneratedRegex(@"(?i)^\s*(?:sheet(?:\s*(?:no|number|#))?|drawing\s*(?:no|number|#)|dwg\s*(?:no|number|#))\.?\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineSheetNumberLabel();

    [GeneratedRegex(@"(?i)^\s*(?:sheet\s+title|drawing\s+title|title)\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactSheetTitleLabel();

    [GeneratedRegex(@"(?i)^\s*(?:sheet\s+title|drawing\s+title|title)\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineSheetTitleLabel();

    [GeneratedRegex(@"(?i)^\s*(?:rev(?:ision)?)\.?\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactRevisionLabel();

    [GeneratedRegex(@"(?i)^\s*(?:rev(?:ision)?)\.?\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineRevisionLabel();

    [GeneratedRegex(@"(?i)^\s*(?:date|issue\s+date|issued)\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactIssueDateLabel();

    [GeneratedRegex(@"(?i)^\s*(?:date|issue\s+date|issued)\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineIssueDateLabel();

    [GeneratedRegex(@"(?i)^\s*scale\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactScaleLabel();

    [GeneratedRegex(@"(?i)^\s*scale\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineScaleLabel();

    [GeneratedRegex(@"(?i)^\s*(?:drawn\s+by|drawn|drwn)\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactDrawnByLabel();

    [GeneratedRegex(@"(?i)^\s*(?:drawn\s+by|drawn|drwn)\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineDrawnByLabel();

    [GeneratedRegex(@"(?i)^\s*(?:checked\s+by|checked|chkd|chk)\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactCheckedByLabel();

    [GeneratedRegex(@"(?i)^\s*(?:checked\s+by|checked|chkd|chk)\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineCheckedByLabel();

    [GeneratedRegex(@"(?i)^\s*discipline\s*$", RegexOptions.Compiled)]
    private static partial Regex ExactDisciplineLabel();

    [GeneratedRegex(@"(?i)^\s*discipline\s*[:#-]?\s*(?<value>.+)$", RegexOptions.Compiled)]
    private static partial Regex InlineDisciplineLabel();

    private sealed record LabelDefinition(
        TitleBlockFieldKind Kind,
        string Label,
        Regex ExactPattern,
        Regex InlinePattern);

    private sealed record TextItem(
        TextPrimitive Primitive,
        int PrimitiveIndex,
        string SourceId,
        string Text,
        PlanRect Bounds);

    private sealed record FieldCandidate(
        TitleBlockFieldKind Kind,
        string Value,
        string RawText,
        PlanRect Bounds,
        Confidence Confidence,
        IReadOnlyList<string> SourcePrimitiveIds,
        IReadOnlyList<string> Evidence,
        double Score)
    {
        public TitleBlockField ToField(int pageNumber) =>
            new(
                Kind,
                Value,
                RawText,
                pageNumber,
                Bounds,
                Confidence,
                SourcePrimitiveIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Evidence);
    }
}
